using System;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Modules;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.ConversationFlow.Core;
using Convai.Modules.ConversationFlow.Profiles;
using Convai.Domain.Logging;
using Convai.Runtime.Animation;
using Convai.Runtime.Logging;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Modules.ConversationFlow.Components
{
    /// <summary>
    ///     MonoBehaviour front-end for the ConversationFlow module. Bridges the
    ///     <see cref="IEventHub" /> signal stream and the per-frame tick into the pure POCO
    ///     state machine.
    /// </summary>
    [EmbodimentModule(ModuleIds.ConversationFlow, "Conversation Flow",
        Description = "Tracks whether the character is idle, listening, thinking or talking.",
        Absence = "the other features cannot tell listening apart from speaking, and fall back to " +
                  "their neutral behaviour.",
        Order = 5)]
    [AddComponentMenu("Convai/Embodiment/Conversation Flow")]
    [DisallowMultipleComponent]
    public sealed class ConvaiConversationFlowController : ConvaiCharacterModule<ConvaiConversationFlowProfile>,
        IConversationFlowSource,
        IEmbodimentTickable
    {
        /// <summary>Length of a reaction beat when the caller does not name one.</summary>
        private const float DefaultReactionSeconds = 0.6f;

        /// <summary>
        ///     A reaction is a beat, not a mode. Past about a second and a half it stops reading as
        ///     "something just happened" and starts reading as the character being stuck.
        /// </summary>
        private const float MaxReactionSeconds = 1.5f;

        private ConversationFlowStateMachine _stateMachine;
        private ConversationFlowSignalAggregator _aggregator;
        private bool _dependenciesChangedHandlerRegistered;
        private bool _scopeChangeHandlerRegistered;
        private bool _lastPlayerScopeWasAmbiguous;
        private int _lastScopeDriverCount = -1;
        private ConvaiCharacter _lastScopeCharacter;
        private ConvaiCharacter _lastScopeTarget;

        private ConvaiCharacter _character;

#if UNITY_INCLUDE_TESTS
        private Func<ConvaiCharacter> _conversationTargetResolverOverride;
#endif

        /// <inheritdoc />
        public DialogueStateReading Current => _stateMachine?.Current ?? DialogueStateReading.Idle;

        /// <inheritdoc />
        public event Action<DialogueStateReading> Changed;

        /// <summary>
        ///     Which local evidence decided the character was speaking, on the most recent tick.
        /// </summary>
        internal SpeechEvidenceRung SpeechEvidence =>
            _aggregator?.SpeechEvidence ?? SpeechEvidenceRung.ServiceOnly;

        /// <summary>What ended the most recent speaking turn: local evidence, or the service.</summary>
        internal SpeechBoundarySource SpeechEndedBy =>
            _aggregator?.SpeechEndedBy ?? SpeechBoundarySource.None;

        /// <summary>
        ///     How far behind local evidence the service's speech-stop was on the most recent turn,
        ///     in seconds. Surfaced rather than kept internal because it is the only way to see the
        ///     defect this seam addresses actually happening: without it, a fixed ending and an
        ///     ending that was never late look exactly the same.
        /// </summary>
        internal float LastServiceLagSeconds => _aggregator?.LastServiceLagSeconds ?? 0f;

        /// <summary>
        ///     Plays a short reaction beat: the character breaks whatever it was doing to respond to
        ///     something that just happened, then returns to the beat the conversation is actually
        ///     on. Gaze snaps and commits, body language sharpens, emotion is free to spike.
        /// </summary>
        /// <param name="durationSeconds">
        ///     How long the beat lasts. Clamped to a sane one-shot length; values at or below zero
        ///     do nothing.
        /// </param>
        /// <remarks>
        ///     <para>
        ///         This is the verb for a moment the conversation itself cannot see — a door slams,
        ///         the player throws something, a quest beat fires. The other seven states are
        ///         derived from the conversation and are never driven from game code; this one is
        ///         the opposite by design, which is why it is the only state with a public verb.
        ///     </para>
        ///     <para>
        ///         Safe to call at any time and from inside a <see cref="Changed" /> handler. Calling
        ///         it again while a beat is running restarts it rather than queueing a second one. A
        ///         character that is not in the conversation yet stays <see cref="DialogueState.Idle" />
        ///         and the beat is dropped rather than held — by the time it joins, whatever it was
        ///         reacting to has passed.
        ///     </para>
        /// </remarks>
        public void PulseReaction(float durationSeconds = DefaultReactionSeconds)
        {
            if (durationSeconds <= 0f) return;
            _stateMachine?.Pulse(
                DialogueState.Reacting,
                Mathf.Min(durationSeconds, MaxReactionSeconds));
        }

        /// <inheritdoc />
        EmbodimentTickPhase IEmbodimentTickable.Phase => EmbodimentTickPhase.Cognition;

        /// <inheritdoc />
        protected override string ProfileModuleId => ModuleIds.ConversationFlow;

        /// <inheritdoc />
        protected override System.Func<ConvaiConversationFlowProfile> DefaultProfileFactory => ConvaiConversationFlowProfile.CreateDefault;

        protected override void Awake()
        {
            base.Awake();

            _stateMachine = new ConversationFlowStateMachine();
            _stateMachine.Changed += OnStateMachineChanged;

            _character = GetComponentInParent<ConvaiCharacter>(true);
            _aggregator = new ConversationFlowSignalAggregator(ResolveCharacterId());
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (!enabled) return;

            ProvideService<IConversationFlowSource>(this);
            Context.DependenciesPopulated += HandleDependenciesPopulated;
            _dependenciesChangedHandlerRegistered = true;
            Context.EnsureTickScheduler()?.Register(this);

            ResetRuntimeState();
            RefreshRuntimeBindings();

            ConvaiConversationFlowDriverRegistry.Register(this);
            ConvaiConversationFlowDriverRegistry.ScopeChanged += HandleDriverScopeChanged;
            _scopeChangeHandlerRegistered = true;
            RebindPlayerScope();
        }

        protected override void OnDisable()
        {
            if (_scopeChangeHandlerRegistered)
            {
                ConvaiConversationFlowDriverRegistry.ScopeChanged -= HandleDriverScopeChanged;
                _scopeChangeHandlerRegistered = false;
            }
            ConvaiConversationFlowDriverRegistry.Unregister(this);

            if (_dependenciesChangedHandlerRegistered && Context != null)
            {
                Context.DependenciesPopulated -= HandleDependenciesPopulated;
                _dependenciesChangedHandlerRegistered = false;
            }

            _aggregator?.Detach();
            // Released by the base class in base.OnDisable().
            Context?.TickScheduler?.Unregister(this);
            ResetRuntimeState();

            base.OnDisable();
        }

        protected override void OnDestroy()
        {
            if (_stateMachine != null)
                _stateMachine.Changed -= OnStateMachineChanged;

            base.OnDestroy();
        }

        /// <inheritdoc />
        protected override void OnProfileApplied(ConvaiConversationFlowProfile newProfile)
        {
            ResetRuntimeState();
            if (Context != null && isActiveAndEnabled)
                RefreshRuntimeBindings();
        }

        void IEmbodimentTickable.EmbodimentTick(float deltaTime)
        {
            if (_aggregator == null || _stateMachine == null) return;

            // The active conversation target can change at runtime without the set of flow
            // controllers changing. Re-evaluate the cached scope before sampling player signals.
            RebindPlayerScope();

            // An errand the character is running counts as engagement, so a Walk To or Follow
            // Me does not decay to Idle in silence. Absent seam → false → previous behaviour.
            bool performingAction = Context?.ActionActivitySource?.IsPerformingAction ?? false;

            // Reacting to the player before the service has confirmed them is a per-character
            // choice, so it is read from the profile rather than decided by whoever publishes the
            // hint. Off means the aggregator keeps folding the signal and simply never reports it.
            _aggregator.SetNoticePlayerLocally(EffectiveProfile.NoticeYouLocally);

            ConversationFlowInputs inputs = _aggregator.Sample(
                performingAction,
                EffectiveProfile.ToSpeechBoundaryConfig(),
                deltaTime,
                Context?.SpeechPlaybackWitness?.Current);
            ConversationFlowTimings timings = EffectiveProfile.ToTimings();
            _stateMachine.Tick(inputs, timings, deltaTime);
        }


        private void OnStateMachineChanged(DialogueStateReading reading)
        {
            Changed?.Invoke(reading);
        }

        private void HandleDependenciesPopulated()
        {
            RefreshRuntimeBindings();
        }

        private void HandleDriverScopeChanged() => RebindPlayerScope();

        private void RefreshRuntimeBindings()
        {
            if (Context == null || _aggregator == null) return;

            Context.EnsureTickScheduler()?.Register(this);
            _aggregator.SetCharacterId(ResolveCharacterId());
            RebindPlayerScope();
            _aggregator.Attach(Context.EventHub, Context.DialoguePhase);

            if (_character != null && _character.IsCharacterReady)
                _aggregator.SetCharacterReady(true);
        }

        private string ResolveCharacterId()
        {
            if (Context != null && Context.Character != null)
                _character = Context.Character;

            if (_character == null)
                _character = GetComponentInParent<ConvaiCharacter>(true);
            if (_character == null)
                _character = GetComponentInChildren<ConvaiCharacter>(true);

            return _character != null ? _character.CharacterId : null;
        }

        private void RebindPlayerScope()
        {
            if (_aggregator == null) return;

            ResolveCharacterId();
            int activeDriverCount = ConvaiConversationFlowDriverRegistry.ActiveCount;
            ConvaiCharacter conversationTarget = ResolveConversationTarget();

            if (activeDriverCount == _lastScopeDriverCount &&
                ReferenceEquals(_character, _lastScopeCharacter) &&
                ReferenceEquals(conversationTarget, _lastScopeTarget))
            {
                return;
            }

            bool hasMultipleDrivers = activeDriverCount > 1;
            bool hasScopedTarget = conversationTarget != null;

            // One character in the room: the player has nobody else to be talking to. More than one:
            // only the addressed character takes the turn. The rest cool to Idle, which is where
            // their own gaze and body language decide how a bystander behaves — handing them the
            // addressee's beats would make every character in the room commit to the player at once.
            bool isAddressed = !hasMultipleDrivers ||
                               ReferenceEquals(conversationTarget, _character);
            _aggregator.SetAddressedByPlayer(isAddressed);

            // Nobody has been named yet in a room that holds several. The SDK answers this itself
            // once the room is up, so it is worth saying only if it persists — and it is a note
            // about who takes the turn, not a report that speech is being thrown away.
            bool playerScopeIsAmbiguous = hasMultipleDrivers && !hasScopedTarget;
            if (playerScopeIsAmbiguous && !_lastPlayerScopeWasAmbiguous && UnityEngine.Application.isPlaying)
            {
                ConvaiLogger.Info(
                    $"[ConvaiConversationFlowController] '{name}' is one of {activeDriverCount} characters and " +
                    "nothing has said which of them the player is addressing yet, so none of them takes the " +
                    "player's turn. Set ConvaiManager.TalkTo(...) or leave automatic conversation targeting on.",
                    LogCategory.Character);
            }

            _lastPlayerScopeWasAmbiguous = playerScopeIsAmbiguous;
            _lastScopeDriverCount = activeDriverCount;
            _lastScopeCharacter = _character;
            _lastScopeTarget = conversationTarget;
        }

        private ConvaiCharacter ResolveConversationTarget()
        {
#if UNITY_INCLUDE_TESTS
            if (_conversationTargetResolverOverride != null)
                return _conversationTargetResolverOverride();
#endif
            // AddressedCharacter is the SDK's own answer to "who is the player talking to": the
            // room's active membership, the character the player is looking at while the room
            // settles, and only then whoever would take the first turn. ActiveConversationCharacter
            // is the last of those three on its own — an ownership-time pick in scene order — so
            // scoping on it hands the player's turn to a character they may never have addressed.
            return ConvaiManager.ActiveManager != null
                ? ConvaiManager.ActiveManager.AddressedCharacter
                : null;
        }

#if UNITY_INCLUDE_TESTS
        internal void SetConversationTargetResolverForTests(Func<ConvaiCharacter> resolver)
        {
            _conversationTargetResolverOverride = resolver;
            RebindPlayerScope();
        }
#endif

        private void ResetRuntimeState()
        {
            _aggregator?.Reset();
            _stateMachine?.Reset();
        }
    }
}
