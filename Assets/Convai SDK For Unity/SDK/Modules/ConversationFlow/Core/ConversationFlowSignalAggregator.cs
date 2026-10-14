using System;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.EventSystem;
using Convai.Domain.Models;

namespace Convai.Modules.ConversationFlow.Core
{
    /// <summary>
    ///     Folds <see cref="IEventHub" /> events and LipSync phase signals into the per-frame
    ///     <see cref="ConversationFlowInputs" /> consumed by
    ///     <see cref="ConversationFlowStateMachine" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Pure POCO so it is edit-mode testable. Subscription lifetime is managed by the
    ///         caller via <see cref="Attach" /> / <see cref="Detach" /> ; never in the
    ///         constructor, because the aggregator is created once and reused.
    ///     </para>
    ///     <para>
    ///         The aggregator is scoped to a single character identified by
    ///         <see cref="CharacterId" />. Events for other characters are ignored.
    ///     </para>
    /// </remarks>
    internal sealed class ConversationFlowSignalAggregator
    {
        private string _characterId;
        private IDialoguePhaseProvider _dialoguePhaseProvider;
        private IEventHub _eventHub;

        private SubscriptionToken _playerSpeakingToken;
        private SubscriptionToken _playerTranscriptToken;
        private SubscriptionToken _characterSpeechToken;
        private SubscriptionToken _characterTurnToken;
        private SubscriptionToken _characterReadyToken;
        private SubscriptionToken _localActivityToken;
        private SubscriptionToken _audioPlaybackToken;

        private bool _isAttached;
        private bool _isAddressedByPlayer = true;

        private bool _isPlayerSpeaking;
        private bool _hasPendingPlayerTurn;
        private bool _isCharacterSpeaking;

        // Local evidence that the character's voice is audible, from the remote audio track's own
        // signal detection. One source, deliberately: it is the only thing on this machine that
        // independently knows whether sound is coming out, and it needs neither LipSync nor viseme
        // data to say so. Its weakness is that a silent gap between sentences and a finished answer
        // sound identical to it, which is why the arbiter makes it wait before ending anything.
        private readonly SpeechBoundaryArbiter _speechBoundary = new();
        private bool _audioPlaybackAudible;
        private bool _hasAudioPlaybackEvidence;
        private bool _isCharacterReady;
        private bool _wasInterrupted;
        private bool _turnJustCompletedLatch;

        // One flag per local source rather than one shared bool: the microphone and the talk
        // control run independently and overlap, so a single flag would let whichever went quiet
        // first switch the other one off.
        private bool _microphoneHearsPlayer;
        private bool _pushToTalkHeld;
        private bool _noticePlayerLocally = true;

        public ConversationFlowSignalAggregator(string characterId)
        {
            _characterId = characterId ?? string.Empty;
        }

        /// <summary>Character this aggregator listens for.</summary>
        public string CharacterId => _characterId;

        /// <summary>Updates the scoped character id when ConvaiCharacter finishes initialization.</summary>
        public void SetCharacterId(string characterId)
        {
            _characterId = characterId ?? string.Empty;
        }

        /// <summary>
        ///     Whether the player is talking <i>to this character</i>. In a room holding one
        ///     character that is always true; in a multi-character room it is true for the
        ///     addressed character only.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A character that is not the addressee reports no player turn at all: it cools to
        ///         Idle and lives its ambient life, which is where its own gaze and body language
        ///         decide what a bystander does. Taking the addressee's beats would make every
        ///         character in the room commit to the player at once.
        ///     </para>
        ///     <para>
        ///         The player's raw signals are still recorded either way and gated at
        ///         <see cref="Sample" /> rather than dropped here, so a character that <i>becomes</i>
        ///         the addressee mid-utterance picks the turn up from the signals already in flight
        ///         instead of waiting for the next event.
        ///     </para>
        /// </remarks>
        public void SetAddressedByPlayer(bool isAddressed) => _isAddressedByPlayer = isAddressed;

        /// <summary>
        ///     Whether local evidence that the player is starting to talk may reach the state
        ///     machine. The signals are folded either way, so turning this back on takes effect on
        ///     the next sample rather than on the next microphone event.
        /// </summary>
        public void SetNoticePlayerLocally(bool notice) => _noticePlayerLocally = notice;

        /// <summary>
        ///     Hooks the aggregator up to the live event hub and LipSync phase provider. Safe
        ///     to call multiple times ; additional calls are no-ops.
        /// </summary>
        public void Attach(IEventHub eventHub, IDialoguePhaseProvider dialoguePhaseProvider)
        {
            if (_isAttached &&
                ReferenceEquals(_eventHub, eventHub) &&
                ReferenceEquals(_dialoguePhaseProvider, dialoguePhaseProvider))
            {
                return;
            }

            if (_isAttached)
                Detach();

            _eventHub = eventHub;
            _dialoguePhaseProvider = dialoguePhaseProvider;

            if (_eventHub != null)
            {
                _playerSpeakingToken = _eventHub.Subscribe<PlayerSpeakingStateChanged>(OnPlayerSpeakingChanged);
                _playerTranscriptToken = _eventHub.Subscribe<PlayerTranscriptReceived>(OnPlayerTranscript);
                _characterSpeechToken = _eventHub.Subscribe<CharacterSpeechStateChanged>(OnCharacterSpeech);
                _characterTurnToken = _eventHub.Subscribe<CharacterTurnCompleted>(OnCharacterTurnCompleted);
                _characterReadyToken = _eventHub.Subscribe<CharacterReady>(OnCharacterReady);
                _localActivityToken = _eventHub.Subscribe<LocalPlayerActivityChanged>(OnLocalPlayerActivity);
                _audioPlaybackToken =
                    _eventHub.Subscribe<CharacterAudioPlaybackStateChanged>(OnAudioPlaybackStateChanged);
            }

            _isAttached = true;
        }

        /// <summary>Releases event hub subscriptions. Safe to call multiple times.</summary>
        public void Detach()
        {
            if (!_isAttached) return;

            if (_eventHub != null)
            {
                _eventHub.Unsubscribe(_playerSpeakingToken);
                _eventHub.Unsubscribe(_playerTranscriptToken);
                _eventHub.Unsubscribe(_characterSpeechToken);
                _eventHub.Unsubscribe(_characterTurnToken);
                _eventHub.Unsubscribe(_characterReadyToken);
                _eventHub.Unsubscribe(_localActivityToken);
                _eventHub.Unsubscribe(_audioPlaybackToken);
            }

            _playerSpeakingToken = default;
            _playerTranscriptToken = default;
            _characterSpeechToken = default;
            _characterTurnToken = default;
            _characterReadyToken = default;
            _localActivityToken = default;
            _audioPlaybackToken = default;
            _eventHub = null;
            _dialoguePhaseProvider = null;
            _isAttached = false;
        }

        /// <summary>Forces the ready flag (e.g. when the agent is wired up before the event arrives).</summary>
        public void SetCharacterReady(bool isReady) => _isCharacterReady = isReady;

        /// <summary>Resets all signals. Used when the character is shut down.</summary>
        public void Reset()
        {
            _isCharacterReady = false;
            _isPlayerSpeaking = false;
            _hasPendingPlayerTurn = false;
            _isCharacterSpeaking = false;
            _wasInterrupted = false;
            _turnJustCompletedLatch = false;
            _microphoneHearsPlayer = false;
            _pushToTalkHeld = false;
            _audioPlaybackAudible = false;
            _hasAudioPlaybackEvidence = false;
            _speechBoundary.Reset();
        }

        /// <summary>Which local evidence the speech boundary is currently standing on.</summary>
        public SpeechEvidenceRung SpeechEvidence => _speechBoundary.Rung;

        /// <summary>What ended the most recent speaking turn.</summary>
        public SpeechBoundarySource SpeechEndedBy => _speechBoundary.EndedBy;

        /// <summary>
        ///     How far behind local evidence the service's speech-stop was on the most recent turn,
        ///     in seconds. The delay this seam exists to remove, measured rather than assumed.
        /// </summary>
        public float LastServiceLagSeconds => _speechBoundary.LastVoiceLeadSeconds;

        /// <summary>Produces a per-frame input struct and clears one-shot latches.</summary>
        /// <param name="isPerformingAction">
        ///     Whether the character is carrying out work it was given. Passed in rather than
        ///     subscribed to because it is a polled state on a peer seam, not an event this
        ///     aggregator can latch — everything else here arrives on the event hub.
        /// </param>
        public ConversationFlowInputs Sample(
            bool isPerformingAction = false,
            SpeechBoundaryArbiterConfig speechBoundary = default,
            float deltaTime = 0f,
            Convai.Runtime.Animation.SpeechPlaybackReading? speechPlayback = null)
        {
            bool turnCompleted = _turnJustCompletedLatch;
            bool wasInterrupted = _wasInterrupted;
            _turnJustCompletedLatch = false;
            _wasInterrupted = false;

            bool lipSyncSpeaking = _dialoguePhaseProvider?.IsSpeechActive ?? false;

            // null, not false, when nothing on this machine can speak to it: the absence of a
            // witness must not read as a verdict of silence.
            bool? localSpeaking = _hasAudioPlaybackEvidence ? _audioPlaybackAudible : null;
            bool speaking = _speechBoundary.Step(
                _isCharacterSpeaking, localSpeaking, speechPlayback, in speechBoundary, deltaTime);

            bool addressed = _isAddressedByPlayer;

            return new ConversationFlowInputs(
                isCharacterReady: _isCharacterReady,
                isPlayerSpeaking: addressed && _isPlayerSpeaking,
                hasPendingPlayerTurn: addressed && _hasPendingPlayerTurn,
                isCharacterSpeaking: speaking,
                isLipSyncSpeaking: lipSyncSpeaking,
                wasRecentlyInterrupted: wasInterrupted,
                turnJustCompleted: turnCompleted,
                isPerformingAction: isPerformingAction,
                isPlayerLocallyActive: addressed && _noticePlayerLocally &&
                                       (_microphoneHearsPlayer || _pushToTalkHeld),
                isAddressedByPlayer: addressed);
        }

        private bool IsOurCharacter(string characterId) =>
            !string.IsNullOrWhiteSpace(_characterId) &&
            string.Equals(_characterId, characterId, StringComparison.OrdinalIgnoreCase);

        private void OnPlayerSpeakingChanged(PlayerSpeakingStateChanged e)
        {
            _isPlayerSpeaking = e.IsSpeaking;
        }

        private void OnPlayerTranscript(PlayerTranscriptReceived e)
        {
            if (e.Phase == TranscriptionPhase.ProcessedFinal ||
                e.Phase == TranscriptionPhase.Completed ||
                e.Phase == TranscriptionPhase.AsrFinal)
            {
                _hasPendingPlayerTurn = true;
            }
        }

        private void OnCharacterSpeech(CharacterSpeechStateChanged e)
        {
            if (!IsOurCharacter(e.CharacterId)) return;
            _isCharacterSpeaking = e.IsSpeaking;
            if (e.IsSpeaking) _hasPendingPlayerTurn = false;
        }

        private void OnCharacterTurnCompleted(CharacterTurnCompleted e)
        {
            if (!IsOurCharacter(e.CharacterId)) return;
            _turnJustCompletedLatch = true;
            _wasInterrupted = e.WasInterrupted;
            _isCharacterSpeaking = false;
            _hasPendingPlayerTurn = false;
        }

        private void OnAudioPlaybackStateChanged(CharacterAudioPlaybackStateChanged e)
        {
            if (!IsOurCharacter(e.CharacterId)) return;
            _audioPlaybackAudible = e.IsPlaying;
            _hasAudioPlaybackEvidence = true;
        }

        private void OnLocalPlayerActivity(LocalPlayerActivityChanged e)
        {
            switch (e.Source)
            {
                case LocalPlayerActivitySource.Microphone:
                    _microphoneHearsPlayer = e.IsActive;
                    break;
                case LocalPlayerActivitySource.PushToTalk:
                    _pushToTalkHeld = e.IsActive;
                    break;
            }
        }

        private void OnCharacterReady(CharacterReady e)
        {
            if (!IsOurCharacter(e.CharacterId)) return;
            _isCharacterReady = true;
        }
    }
}
