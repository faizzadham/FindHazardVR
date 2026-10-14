using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Facades;
using Convai.Runtime.Logging;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Presentation.Services.Utilities;
using TMPro;
using UnityEngine;
using UnityEngine.Serialization;
using UnityEngine.UI;

namespace Convai.Runtime.Presentation.Views.Transcript
{
    /// <summary>
    ///     Reference chat transcript UI built on ConvaiManager.Transcripts.
    /// </summary>
    public class ChatTranscriptUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField]
        private ScrollRect scrollRect;

        [SerializeField] private RectTransform chatContainer;
        [SerializeField] private GameObject characterMessagePrefab;
        [SerializeField] private GameObject playerMessagePrefab;
        [SerializeField] private TMP_InputField chatInputField;

        [Header("Fade Settings")]
        [SerializeField]
        private CanvasFader canvasFader;

        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private float fadeDuration = 0.5f;

        [Header("Input Availability")]
        [SerializeField]
        [FormerlySerializedAs("gateInputOnAvailability")]
        [Tooltip(
            "Disables the chat field until the character being addressed can actually receive a " +
            "message. This is presentation only: ConvaiPlayer refuses an early message either way, " +
            "so turning it off means the player finds out by being refused rather than by the " +
            "field being closed. Turn it off if your own UI shows the state differently.")]
        private bool _gateInputOnAvailability = true;

        [SerializeField]
        [FormerlySerializedAs("noticeText")]
        [Tooltip(
            "Optional. Shows why a message was refused, then clears itself. Blank the rest of the " +
            "time — the prompt already says what is happening whenever it is visible. This is for " +
            "the one moment it is not: a refusal keeps the player's text, and their text hides the " +
            "prompt. Leave it empty and refusals stay in the Console.")]
        private TMP_Text _noticeText;

        [SerializeField]
        [FormerlySerializedAs("refusalNoticeSeconds")]
        [Min(0f)]
        [Tooltip(
            "How long a refused message's reason stays on the status label. A refusal is the one " +
            "moment the player is definitely looking, and the Console is not where they are " +
            "looking. Set to 0 to leave refusals to the Console alone.")]
        private float _refusalNoticeSeconds = 4f;

        [SerializeField]
        [FormerlySerializedAs("noCharacterPrompt")]
        [Tooltip("Shown when there is no character to talk to.")]
        private string _noCharacterPrompt = "No character to talk to";

        [SerializeField] [FormerlySerializedAs("offlinePrompt")]
        [Tooltip("Shown when no conversation is connected.")]
        private string _offlinePrompt = "Not connected";

        [SerializeField]
        [FormerlySerializedAs("connectingPrompt")]
        [Tooltip(
            "Shown while connecting when nobody in particular is being addressed — the player is " +
            "looking at no character, and none is set to speak first.")]
        private string _connectingPrompt = "Connecting…";

        [SerializeField]
        [FormerlySerializedAs("connectingToCharacterPrompt")]
        [Tooltip(
            "Shown while connecting to a known character. {0} is the character's name. Before the " +
            "room exists this is whoever the player is looking at, which is who targeting commits " +
            "to the moment it comes up.")]
        private string _connectingToCharacterPrompt = "Connecting to {0}…";

        [SerializeField]
        [FormerlySerializedAs("slowConnectHintSeconds")]
        [Tooltip(
            "Seconds of waiting after which the prompt admits the wait is unusual. Set to 0 to " +
            "never say it. Multi-character connects were measured at 26-34 seconds, so this sits " +
            "above them on purpose: a message that fires during a normal wait teaches the player " +
            "to distrust it, and one that fires seconds before success is pure noise.")]
        private float _slowConnectHintSeconds = 35f;

        [SerializeField]
        [FormerlySerializedAs("slowConnectingPrompt")]
        [Tooltip(
            "Replaces the connecting prompt once the wait passes Slow Connect Hint Seconds. {0} is " +
            "the character's name.")]
        private string _slowConnectingPrompt = "Still connecting to {0}…";

        [SerializeField]
        [FormerlySerializedAs("preparingPrompt")]
        [Tooltip("Shown while the addressed character is still joining. {0} is the character's name.")]
        private string _preparingPrompt = "{0} is getting ready…";

        [SerializeField]
        [FormerlySerializedAs("readyPrompt")]
        [Tooltip("Shown when the character can hear the player. {0} is the character's name.")]
        private string _readyPrompt = "Message {0}";

        [SerializeField]
        [FormerlySerializedAs("answeringPrompt")]
        [Tooltip("Shown while the character is answering; typing still interrupts. {0} is the name.")]
        private string _answeringPrompt = "{0} is speaking…";

        [SerializeField]
        [FormerlySerializedAs("unavailablePrompt")]
        [Tooltip("Shown when the character cannot take part. {0} is the character's name.")]
        private string _unavailablePrompt = "{0} is unavailable";

        private readonly Dictionary<string, GameObject> _messageRowsByTurnId = new();
        private readonly HashSet<string> _locallyHiddenTurnIds = new();
        private ConvaiTranscripts _boundTranscripts;
        private IDisposable _transcriptSubscription;
        private IAgentRegistry _agentRegistry;
        private bool _isActive = true;
        private bool _isInjected;
        private IPlayerInputService _playerInput;

        // Last written prompt state, so the field is only touched when something actually moved.
        private ConvaiConversationAvailability _lastAvailability = (ConvaiConversationAvailability)(-1);
        private ConvaiCharacter _lastAddressed;
        private float _availabilityEnteredAt;
        private bool _slowConnectHintShown;
        private float _refusalNoticeUntil;

        private void Awake()
        {
            if (canvasFader == null)
                canvasFader = GetComponentInChildren<CanvasFader>();
            if (canvasGroup == null)
                canvasGroup = GetComponentInChildren<CanvasGroup>();

            if (chatContainer == null)
                ConvaiLogger.Warning("chatContainer is not assigned - messages will not display",
                    LogCategory.UI);

            if (scrollRect == null)
                ConvaiLogger.Warning("scrollRect is not assigned - auto-scroll will not work",
                    LogCategory.UI);
        }

        private void Start()
        {
            TryResolveDependencies();
            TrySubscribeTranscripts(false);
            StartFadeIn();
        }

        private void OnEnable()
        {
            TryResolveDependencies();
            TrySubscribeTranscripts(true);
            if (chatInputField != null) chatInputField.onSubmit.AddListener(OnChatInputSubmit);
        }

        private void OnDisable()
        {
            if (chatInputField != null) chatInputField.onSubmit.RemoveListener(OnChatInputSubmit);
            UnsubscribeTranscripts();
        }

        private void OnDestroy()
        {
            UnsubscribeTranscripts();
        }

        private void Update()
        {
            TryResolveDependencies();
            TrySubscribeTranscripts(false);
            RefreshInputAvailability();

            if (!IsActive || chatInputField == null) return;

            // Enter must not open a field that cannot send anything; the player would be typing
            // into a message that gets refused.
            if (!chatInputField.isFocused && chatInputField.interactable && IsEnterKeyPressed())
                chatInputField.ActivateInputField();
        }

        /// <summary>
        ///     Keeps the field and its prompt honest about whether the addressed character can hear
        ///     the player.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The field used to be permanently usable and permanently labelled "Click here to
        ///         type", so a player typed a greeting the moment a character appeared and it went
        ///         nowhere — the room was connected, which looks like everything is fine, but the
        ///         character had not been announced by the service yet.
        ///     </para>
        ///     <para>
        ///         Recomputed each frame but written only when something changed, so the common case
        ///         is an enum comparison and a reference comparison. The prompt is only rebuilt when
        ///         the state or the character changes, which keeps this off the allocation path.
        ///     </para>
        /// </remarks>
        private void RefreshInputAvailability()
        {
            if (!_gateInputOnAvailability || chatInputField == null) return;

            ConvaiManager manager = ConvaiManager.ActiveManager;
            ConvaiConversationAvailability availability = manager != null
                ? manager.ConversationAvailability
                : ConvaiConversationAvailability.NoCharacter;
            ConvaiCharacter addressed = manager != null ? manager.AddressedCharacter : null;

            bool canSend = availability.CanAcceptPlayerInput();
            if (chatInputField.interactable != canSend)
            {
                chatInputField.interactable = canSend;

                // A field that just closed must not keep the caret, or the player goes on typing
                // into something that will refuse them.
                if (!canSend && chatInputField.isFocused)
                    chatInputField.DeactivateInputField();
            }

            bool stateChanged = availability != _lastAvailability ||
                                !ReferenceEquals(addressed, _lastAddressed);

            // A wait that has gone on too long changes the prompt without anything else changing,
            // so the elapsed time is a second reason to rewrite it — once, not every frame.
            bool hintDue = !_slowConnectHintShown &&
                           availability == ConvaiConversationAvailability.Connecting &&
                           _slowConnectHintSeconds > 0f &&
                           Time.unscaledTime - _availabilityEnteredAt >= _slowConnectHintSeconds;

            // The notice is on a clock of its own: it appears without the verdict moving and has to
            // be taken down the same way.
            if (_refusalNoticeUntil > 0f && Time.unscaledTime >= _refusalNoticeUntil)
            {
                _refusalNoticeUntil = 0f;
                if (_noticeText != null) _noticeText.text = string.Empty;
            }

            if (!stateChanged && !hintDue) return;

            if (stateChanged)
            {
                _availabilityEnteredAt = Time.unscaledTime;
                _slowConnectHintShown = false;
            }

            if (hintDue) _slowConnectHintShown = true;

            _lastAvailability = availability;
            _lastAddressed = addressed;

            if (chatInputField.placeholder is TMP_Text placeholderText)
                placeholderText.text = BuildPrompt(availability, addressed);
        }

        /// <summary>Puts a refusal where the player is already looking, and clears it on a timer.</summary>
        private void ShowRefusal(string reason)
        {
            if (_noticeText == null || _refusalNoticeSeconds <= 0f ||
                string.IsNullOrWhiteSpace(reason))
                return;

            _refusalNoticeUntil = Time.unscaledTime + _refusalNoticeSeconds;
            _noticeText.text = reason;
        }

        private string BuildPrompt(
            ConvaiConversationAvailability availability,
            ConvaiCharacter addressed)
        {
            string who = addressed != null && !string.IsNullOrWhiteSpace(addressed.CharacterName)
                ? addressed.CharacterName
                : "the character";

            return availability switch
            {
                ConvaiConversationAvailability.NoCharacter => _noCharacterPrompt,
                ConvaiConversationAvailability.Offline => _offlinePrompt,
                ConvaiConversationAvailability.Connecting => BuildConnectingPrompt(addressed, who),
                ConvaiConversationAvailability.Preparing => Format(_preparingPrompt, who),
                ConvaiConversationAvailability.Ready => Format(_readyPrompt, who),
                ConvaiConversationAvailability.Answering => Format(_answeringPrompt, who),
                ConvaiConversationAvailability.Unavailable => Format(_unavailablePrompt, who),
                _ => _offlinePrompt
            };
        }

        /// <summary>
        ///     The connecting prompt, naming the character when there is one to name and admitting
        ///     the wait once it stops looking normal.
        /// </summary>
        /// <remarks>
        ///     A room with several characters can take tens of seconds to start, and a prompt that
        ///     says only "Connecting…" for that long reads as a hang. Saying that the wait is
        ///     expected is the difference between a player waiting and a player quitting.
        /// </remarks>
        private string BuildConnectingPrompt(ConvaiCharacter addressed, string who)
        {
            if (_slowConnectHintSeconds > 0f &&
                !string.IsNullOrEmpty(_slowConnectingPrompt) &&
                Time.unscaledTime - _availabilityEnteredAt >= _slowConnectHintSeconds)
                return Format(_slowConnectingPrompt, who);

            return addressed != null
                ? Format(_connectingToCharacterPrompt, who)
                : _connectingPrompt;
        }

        /// <summary>
        ///     Fills in the character's name, tolerating a project that removed the placeholder.
        /// </summary>
        private static string Format(string template, string characterName) =>
            string.IsNullOrEmpty(template)
                ? string.Empty
                : template.Contains("{0}")
                    ? string.Format(template, characterName)
                    : template;

        public bool IsActive => _isActive && gameObject.activeInHierarchy;

        public void Inject(IAgentRegistry agentRegistry, IPlayerInputService playerInput)
        {
            _agentRegistry = agentRegistry ?? _agentRegistry;
            _playerInput = playerInput ?? _playerInput;
            _isInjected = _playerInput != null;

            if (_playerInput == null)
                ConvaiLogger.Warning("IPlayerInputService not available - text input will not work",
                    LogCategory.UI);
        }

        public void SetActive(bool active)
        {
            _isActive = active;
            gameObject.SetActive(active);

            if (active) StartFadeIn();
        }

        private void StartFadeIn()
        {
            if (canvasFader != null && canvasGroup != null)
                canvasFader.StartFadeIn(canvasGroup, fadeDuration);
        }

        public void ClearAll()
        {
            ClearRenderedRows(true);
        }

        private void ClearRenderedRows(bool rememberAsLocallyHidden)
        {
            if (rememberAsLocallyHidden)
            {
                foreach (string turnId in _messageRowsByTurnId.Keys)
                    _locallyHiddenTurnIds.Add(turnId);
            }

            if (chatContainer != null)
            {
                foreach (Transform child in chatContainer.transform)
                {
                    if (child.gameObject != characterMessagePrefab &&
                        child.gameObject != playerMessagePrefab)
                        Destroy(child.gameObject);
                }
            }

            _messageRowsByTurnId.Clear();
        }

        private void TryResolveDependencies()
        {
            if (_isInjected) return;

            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager == null) return;

            manager.TryGetAgentRegistry(out IAgentRegistry agentRegistry);
            manager.TryGetPlayerInputService(out IPlayerInputService playerInput);
            if (playerInput == null) return;
            Inject(agentRegistry, playerInput);
        }

        private void TrySubscribeTranscripts(bool logFailure)
        {
            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager == null || !manager.TryGetTranscripts(out ConvaiTranscripts transcripts))
            {
                UnsubscribeTranscripts();
                if (logFailure)
                    ConvaiLogger.Warning("No active ConvaiManager found.", LogCategory.UI);
                return;
            }

            if (ReferenceEquals(_boundTranscripts, transcripts)) return;

            UnsubscribeTranscripts();
            _boundTranscripts = transcripts;
            _boundTranscripts.PresentationEnabledChanged += HandlePresentationEnabledChanged;
            ApplyPresentationEnabled(_boundTranscripts.IsPresentationEnabled);
        }

        private void UnsubscribeTranscripts()
        {
            DisposeTranscriptSubscription();
            if (_boundTranscripts != null)
                _boundTranscripts.PresentationEnabledChanged -= HandlePresentationEnabledChanged;
            _boundTranscripts = null;
        }

        private void HandlePresentationEnabledChanged(bool enabled) => ApplyPresentationEnabled(enabled);

        private void ApplyPresentationEnabled(bool enabled)
        {
            if (!enabled)
            {
                DisposeTranscriptSubscription();
                ClearRenderedRows(false);
                return;
            }

            if (_boundTranscripts == null || _transcriptSubscription != null) return;

            _locallyHiddenTurnIds.IntersectWith(_boundTranscripts.CurrentTimeline.TurnsById.Keys);
            _transcriptSubscription = _boundTranscripts.Subscribe(
                DisplayChange,
                new TranscriptSubscriptionOptions { ReplayExisting = true });
        }

        private void DisposeTranscriptSubscription()
        {
            _transcriptSubscription?.Dispose();
            _transcriptSubscription = null;
        }

        private void DisplayChange(TranscriptChange change)
        {
            if (change == null) return;

            if (change.Kind == TranscriptChangeKind.Removed)
            {
                _locallyHiddenTurnIds.Remove(change.TurnId);
                if (_messageRowsByTurnId.TryGetValue(change.TurnId, out GameObject row))
                {
                    _messageRowsByTurnId.Remove(change.TurnId);
                    if (row != null) Destroy(row);
                }

                return;
            }

            DisplayTurn(change.Turn);
        }

        private void DisplayTurn(TranscriptTurn turn)
        {
            if (turn == null || !turn.HasText || chatContainer == null || _locallyHiddenTurnIds.Contains(turn.Id))
                return;

            bool hadRow = _messageRowsByTurnId.TryGetValue(turn.Id, out GameObject messageObj);
            if (!hadRow)
            {
                GameObject prefab = turn.Speaker?.Type == TranscriptSpeakerType.Character
                    ? characterMessagePrefab
                    : playerMessagePrefab;
                if (prefab == null) return;

                messageObj = Instantiate(prefab, chatContainer);
                messageObj.SetActive(true);
                _messageRowsByTurnId.Add(turn.Id, messageObj);

                if (messageObj.TryGetComponent(out ChatMessageBubble bubble))
                {
                    bubble.Identifier = turn.Speaker?.Type == TranscriptSpeakerType.Character
                        ? turn.Speaker.Id
                        : turn.Id;
                    bubble.SetAgentRegistry(_agentRegistry);
                }
            }

            UpdateMessageBubble(messageObj, turn);
            ScrollToBottom();
        }

        private void UpdateMessageBubble(GameObject messageObj, TranscriptTurn turn)
        {
            if (messageObj.TryGetComponent(out ChatMessageBubble bubble))
            {
                bubble.SetSender(turn.Speaker?.DisplayName ?? string.Empty);
                bubble.SetMessage(turn.DisplayText);
                bubble.IsCompleted = turn.IsCommitted;

                if (turn.Speaker?.Type == TranscriptSpeakerType.Character &&
                    _agentRegistry != null &&
                    _agentRegistry.TryGetCharacter(turn.Speaker.Id, out IConvaiCharacterAgent character))
                    bubble.SetSenderColor(character.NameTagColor);
                return;
            }

            TMP_Text textComponent = messageObj.GetComponentInChildren<TMP_Text>();
            if (textComponent != null)
                textComponent.text = $"{turn.Speaker?.DisplayName}: {turn.DisplayText}";
        }

        private void OnChatInputSubmit(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            if (!_isInjected)
            {
                ConvaiLogger.Warning("Cannot send message - dependencies not injected",
                    LogCategory.UI);
                return;
            }

            if (_playerInput == null || !_playerInput.HasPlayer)
            {
                ConvaiLogger.Info("No player found", LogCategory.UI);
                return;
            }

            // Asked rather than assumed: the field can be interactable and the answer still be no,
            // if the character stopped being available between the keystroke and the Return.
            // Keeping the text on refusal matters — retyping a sentence the game swallowed is the
            // part players actually resent.
            //
            // The reason-returning send lives on ConvaiPlayer rather than on IConvaiPlayerAgent,
            // because adding a member to that interface would break every project implementing it.
            // A custom player agent is therefore asked the same question a different way — the
            // manager's verdict, which is the same one ConvaiPlayer consults — so that a refusal
            // keeps the typed sentence on both paths rather than only on the shipped one.
            if (!TrySendThroughPlayer(text, out string reason))
            {
                ConvaiLogger.Warning($"Message not sent: {reason}", LogCategory.UI);
                ShowRefusal(reason);
                chatInputField.ActivateInputField();
                return;
            }

            chatInputField.SetTextWithoutNotify(string.Empty);
            chatInputField.ActivateInputField();
        }

        /// <summary>
        ///     Hands the message to whichever player agent this scene has, and says why when it
        ///     cannot.
        /// </summary>
        /// <remarks>
        ///     The two paths differ only in where the refusal comes from. <see cref="ConvaiPlayer" />
        ///     answers for itself; any other implementation of <see cref="IConvaiPlayerAgent" /> is
        ///     gated on the manager's verdict here, before the message is handed over. Without the
        ///     second path a custom agent lost the typed sentence on every refusal, because a void
        ///     send cannot report one.
        /// </remarks>
        private bool TrySendThroughPlayer(string text, out string reason)
        {
            if (_playerInput.Player is ConvaiPlayer convaiPlayer)
                return convaiPlayer.TrySendTextMessage(text, out reason);

            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager != null && !manager.ConversationAvailability.CanAcceptPlayerInput())
            {
                ConvaiCharacter addressed = manager.AddressedCharacter;
                string who = addressed != null ? $"'{addressed.CharacterName}'" : "the character";
                reason = $"{who} cannot receive messages yet ({manager.ConversationAvailability}).";
                return false;
            }

            _playerInput.Player.SendTextMessage(text);
            reason = null;
            return true;
        }

        private void ScrollToBottom()
        {
            if (scrollRect == null) return;

            Canvas.ForceUpdateCanvases();

            if (chatContainer != null) LayoutRebuilder.ForceRebuildLayoutImmediate(chatContainer);

            scrollRect.verticalNormalizedPosition = 0;
        }

        /// <summary>
        ///     Both Enter keys open the field. The shared helper answers for whichever input
        ///     backend the project is configured with and caches its Input System lookups, so this
        ///     per-frame poll allocates nothing.
        /// </summary>
        private static bool IsEnterKeyPressed() =>
            ConvaiInputSystem.WasKeyPressedThisFrame(KeyCode.Return) ||
            ConvaiInputSystem.WasKeyPressedThisFrame(KeyCode.KeypadEnter);
    }
}
