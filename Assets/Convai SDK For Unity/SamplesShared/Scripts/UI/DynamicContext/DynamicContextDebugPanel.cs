using System;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Facades;
using Convai.SampleCommon.UI.Debug;
using Convai.Shared.Compatibility;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.DynamicContext
{
    /// <summary>
    ///     Self-building runtime panel for testing dynamic context in connected scenes.
    /// </summary>
    public sealed class DynamicContextDebugPanel : MonoBehaviour, ISampleDebugPanel
    {
        private const float HostedPanelHeight = 554f;
        private const float ReferenceResolveIntervalSeconds = 0.5f;

        [SerializeField] private ConvaiCharacter _character;
        [SerializeField] private ConvaiManager _manager;
        [SerializeField] private bool _autoResolve = true;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private int _sortingOrder = 650;

        private TMP_InputField _stateNameInput;
        private TMP_InputField _stateValueInput;
        private TMP_InputField _queryStateInput;
        private TMP_InputField _eventInput;
        private TMP_InputField _attentionInput;
        private TMP_InputField _rawTextInput;
        private TMP_InputField _updateIdInput;
        private Toggle _removeStaticToggle;
        private TMP_Text _reactionButtonLabel;
        private TMP_Text _modeButtonLabel;
        private TMP_Text _statusText;
        private TMP_Text _resultText;

        private ConvaiRespondMode _reactionMode = ConvaiRespondMode.Silent;
        private ConvaiContextUpdateMode _rawMode = ConvaiContextUpdateMode.Append;
        private ConvaiEvents _subscribedEvents;
        private GameObject _panelRoot;
        private RectTransform _hostedContentRoot;
        private bool _hostedInHub;
        private bool _built;
        private float _nextReferenceResolveTime;
        private StatusSnapshot _lastStatusSnapshot;
        private bool _hasStatusSnapshot;

        private readonly struct StatusSnapshot : IEquatable<StatusSnapshot>
        {
            public StatusSnapshot(
                long characterInstanceId,
                string characterName,
                bool isInConversation,
                long managerInstanceId,
                ConvaiRespondMode reactionMode,
                ConvaiContextUpdateMode rawMode)
            {
                CharacterInstanceId = characterInstanceId;
                CharacterName = characterName;
                IsInConversation = isInConversation;
                ManagerInstanceId = managerInstanceId;
                ReactionMode = reactionMode;
                RawMode = rawMode;
            }

            private long CharacterInstanceId { get; }
            public string CharacterName { get; }
            public bool IsInConversation { get; }
            public long ManagerInstanceId { get; }
            public ConvaiRespondMode ReactionMode { get; }
            public ConvaiContextUpdateMode RawMode { get; }

            public bool Equals(StatusSnapshot other) =>
                CharacterInstanceId == other.CharacterInstanceId &&
                string.Equals(CharacterName, other.CharacterName, StringComparison.Ordinal) &&
                IsInConversation == other.IsInConversation &&
                ManagerInstanceId == other.ManagerInstanceId &&
                ReactionMode == other.ReactionMode &&
                RawMode == other.RawMode;
        }

        public string PanelLabel => "Context";
        public Vector2 PreferredDrawerSize => new(500f, 620f);
        public bool IsUiBuilt => _built;

        private void Awake()
        {
            if (!_hostedInHub)
                _hostedInHub = GetComponentInParent<SampleDebugHub>(true) != null;

            if (!_hostedInHub)
            {
                BuildUi();
                gameObject.SetActive(_showOnStart);
            }
        }

        public void ConfigureHosted(RectTransform contentRoot)
        {
            _hostedContentRoot = contentRoot;
            _hostedInHub = true;
        }

        public void EnsureUiBuilt()
        {
            if (!_built)
                BuildUi();
        }

        public void OnPanelShown()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(true);
        }

        public void OnPanelHidden()
        {
            if (_panelRoot != null)
                _panelRoot.SetActive(false);
        }

        private void OnEnable()
        {
            ResolveReferences();
            SubscribeResults();
            _nextReferenceResolveTime = Time.unscaledTime + ReferenceResolveIntervalSeconds;
            RenderStatus(force: true);
        }

        private void OnDisable() => UnsubscribeResults();

        private void Update()
        {
            if (Time.unscaledTime >= _nextReferenceResolveTime)
            {
                _nextReferenceResolveTime = Time.unscaledTime + ReferenceResolveIntervalSeconds;
                ResolveReferences();
                SubscribeResults();
            }

            RenderStatus();
        }

        public void SetCharacter(ConvaiCharacter character)
        {
            _character = character;
            RenderStatus(force: true);
        }

        private void SetState(bool flush)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.SetState(_stateNameInput.text, _stateValueInput.text, _reactionMode);
            if (flush) character.DynamicContext.Flush();
            WriteResult($"Queued state: {_stateNameInput.text}={_stateValueInput.text}");
        }

        private void QueryState()
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            string key = string.IsNullOrWhiteSpace(_queryStateInput.text)
                ? _stateNameInput.text
                : _queryStateInput.text;
            if (character.DynamicContext.TryGetStateValue(key, out string value))
                WriteResult($"Local state: {key}={value}");
            else
                WriteResult($"Local state not found: {key}");
        }

        private void RemoveState(bool flush)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.RemoveState(_stateNameInput.text);
            if (flush) character.DynamicContext.Flush();
            WriteResult($"Queued state removal: {_stateNameInput.text}");
        }

        private void AddEvent(bool flush)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.AddEvent(_eventInput.text, _reactionMode);
            if (flush) character.DynamicContext.Flush();
            WriteResult($"Queued event: {_eventInput.text}");
        }

        private void SetAttention(bool flush)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.SetCurrentAttentionObject(_attentionInput.text, _reactionMode);
            if (flush) character.DynamicContext.Flush();
            WriteResult($"Queued attention object: {_attentionInput.text}");
        }

        private void ClearAttention(bool flush)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.ClearCurrentAttentionObject(_reactionMode);
            if (flush) character.DynamicContext.Flush();
            WriteResult("Queued attention clear");
        }

        private void ApplyRaw()
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            var update = new ConvaiDynamicContextUpdate(
                _rawTextInput.text,
                _rawMode,
                _reactionMode,
                _removeStaticToggle != null && _removeStaticToggle.isOn,
                string.IsNullOrWhiteSpace(_attentionInput.text) ? null : _attentionInput.text,
                string.IsNullOrWhiteSpace(_updateIdInput.text) ? null : _updateIdInput.text);

            character.DynamicContext.Apply(update);
            WriteResult($"Applied advanced update: mode={_rawMode}, reaction={_reactionMode}");
        }

        private void Flush()
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.Flush();
            WriteResult("Flushed pending dynamic context");
        }

        private void ResetContext(bool removeStatic)
        {
            if (!TryGetCharacter(out ConvaiCharacter character)) return;

            character.DynamicContext.Reset(removeStatic);
            character.DynamicContext.Flush();
            WriteResult(removeStatic ? "Reset runtime + static context" : "Reset runtime context");
        }

        private void CycleReaction()
        {
            _reactionMode = _reactionMode switch
            {
                ConvaiRespondMode.Silent => ConvaiRespondMode.Auto,
                ConvaiRespondMode.Auto => ConvaiRespondMode.MustRespond,
                _ => ConvaiRespondMode.Silent
            };
            RenderStatus(force: true);
        }

        private void CycleRawMode()
        {
            _rawMode = _rawMode switch
            {
                ConvaiContextUpdateMode.Append => ConvaiContextUpdateMode.Replace,
                ConvaiContextUpdateMode.Replace => ConvaiContextUpdateMode.Reset,
                _ => ConvaiContextUpdateMode.Append
            };
            RenderStatus(force: true);
        }

        private void ResolveReferences()
        {
            if (!_autoResolve) return;

            if (_character == null)
                _character = FindAnyObjectByType<ConvaiCharacter>();
            if (_manager == null)
                _manager = ConvaiManager.ActiveManager != null ? ConvaiManager.ActiveManager : FindAnyObjectByType<ConvaiManager>();
        }

        private bool TryGetCharacter(out ConvaiCharacter character)
        {
            ResolveReferences();
            character = _character;
            if (character != null) return true;

            WriteResult("No ConvaiCharacter resolved");
            return false;
        }

        private void SubscribeResults()
        {
            if (_manager == null) return;

            ConvaiEvents events;
            try
            {
                events = _manager.Events;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            if (ReferenceEquals(_subscribedEvents, events)) return;

            UnsubscribeResults();
            _subscribedEvents = events;
            _subscribedEvents.OnDynamicContextUpdateResultReceived += HandleDynamicContextResult;
        }

        private void UnsubscribeResults()
        {
            if (_subscribedEvents == null) return;

            _subscribedEvents.OnDynamicContextUpdateResultReceived -= HandleDynamicContextResult;
            _subscribedEvents = null;
        }

        private void HandleDynamicContextResult(DynamicContextUpdateResultReceived result)
        {
            WriteResult(
                $"ACK status={result.Status}\n" +
                $"message={result.Message}\n" +
                $"update_id={result.UpdateId}\n" +
                $"revision={result.ContextRevision} tokens={result.TokenCount} remaining={result.RemainingTokens}\n" +
                $"run_llm requested={result.RequestedRunLlm} actual={result.ActualRunLlm}\n" +
                $"downgrade={result.DowngradeReason} interrupted={result.Interrupted} llm={result.LlmTriggered}");
        }

        private void RenderStatus(bool force = false)
        {
            if (!_built) return;

            StatusSnapshot snapshot = CaptureStatusSnapshot();
            if (!force && _hasStatusSnapshot && snapshot.Equals(_lastStatusSnapshot)) return;

            _lastStatusSnapshot = snapshot;
            _hasStatusSnapshot = true;

            if (_reactionButtonLabel != null)
                _reactionButtonLabel.text = $"Reaction: {snapshot.ReactionMode}";
            if (_modeButtonLabel != null)
                _modeButtonLabel.text = $"Advanced mode: {snapshot.RawMode}";

            string characterName = snapshot.CharacterName ?? "None";
            string conversation = snapshot.IsInConversation ? "connected" : "not connected";
            string manager = snapshot.ManagerInstanceId != 0 ? "ready" : "missing";

            if (_statusText != null)
            {
                _statusText.text =
                    $"Character: {characterName}\n" +
                    $"Room: {conversation}\n" +
                    $"Manager: {manager}\n" +
                    $"Batch: {ConvaiCharacter.DynamicContextBatchDelaySeconds:0.###}s, Flush sends now";
            }
        }

        private StatusSnapshot CaptureStatusSnapshot()
        {
            ConvaiCharacter character = _character;
            ConvaiManager manager = _manager;

            return new StatusSnapshot(
                ConvaiObjectId.Of(character),
                character != null ? character.CharacterName : null,
                character != null && character.IsInConversation,
                ConvaiObjectId.Of(manager),
                _reactionMode,
                _rawMode);
        }

        private void WriteResult(string text)
        {
            if (_resultText != null)
                _resultText.text = text;
        }

        private void BuildUi()
        {
            if (_built) return;

            Transform uiParent = _hostedInHub ? _hostedContentRoot : transform;
            if (_hostedInHub)
            {
                _panelRoot = SampleDebugUi.CreateHostedRoot("ContextPanel", uiParent, HostedPanelHeight);
                _panelRoot.SetActive(false);
                uiParent = _panelRoot.transform;
            }
            else
            {
                SampleDebugUi.EnsureStandaloneCanvas(gameObject, _sortingOrder);
            }

            GameObject panel = SampleDebugUi.CreateUiObject("Panel", uiParent);
            var panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.045f, 0.048f, 0.055f, 0.92f);
            RectTransform panelRect = panel.GetComponent<RectTransform>();
            if (_hostedInHub)
            {
                panelRect.anchorMin = Vector2.zero;
                panelRect.anchorMax = Vector2.one;
                panelRect.offsetMin = Vector2.zero;
                panelRect.offsetMax = Vector2.zero;
            }
            else
            {
                panelRect.anchorMin = new Vector2(1f, 0f);
                panelRect.anchorMax = new Vector2(1f, 1f);
                panelRect.pivot = new Vector2(1f, 0.5f);
                panelRect.offsetMin = new Vector2(-470f, 24f);
                panelRect.offsetMax = new Vector2(-24f, -24f);
            }

            var panelLayout = panel.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(14, 14, 14, 14);
            panelLayout.spacing = 8f;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            SampleDebugUi.AddText(panel.transform, "Dynamic Context", 22f, FontStyles.Bold);
            _statusText = SampleDebugUi.AddText(panel.transform, "Resolving...", 13f, FontStyles.Normal, new Color(0.76f, 0.80f, 0.84f, 1f));

            GameObject scrollRoot = SampleDebugUi.CreateUiObject("Scroll", panel.transform);
            LayoutElement scrollLayout = scrollRoot.AddComponent<LayoutElement>();
            scrollLayout.flexibleHeight = 1f;
            var scrollImage = scrollRoot.AddComponent<Image>();
            scrollImage.color = new Color(0.02f, 0.022f, 0.026f, 0.56f);
            scrollRoot.AddComponent<Mask>().showMaskGraphic = false;
            ScrollRect scrollRect = scrollRoot.AddComponent<ScrollRect>();
            scrollRect.horizontal = false;
            scrollRect.movementType = ScrollRect.MovementType.Clamped;
            scrollRect.scrollSensitivity = 24f;

            GameObject content = SampleDebugUi.CreateUiObject("Content", scrollRoot.transform);
            RectTransform contentRect = content.GetComponent<RectTransform>();
            contentRect.anchorMin = new Vector2(0f, 1f);
            contentRect.anchorMax = new Vector2(1f, 1f);
            contentRect.pivot = new Vector2(0.5f, 1f);
            contentRect.offsetMin = Vector2.zero;
            contentRect.offsetMax = Vector2.zero;
            var contentLayout = content.AddComponent<VerticalLayoutGroup>();
            contentLayout.padding = new RectOffset(8, 8, 8, 8);
            contentLayout.spacing = 8f;
            contentLayout.childForceExpandWidth = true;
            contentLayout.childForceExpandHeight = false;
            content.AddComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            scrollRect.content = contentRect;
            scrollRect.viewport = scrollRoot.GetComponent<RectTransform>();

            BuildControls(content.transform);

            _resultText = SampleDebugUi.AddText(panel.transform, "No dynamic context result yet.", 12f, FontStyles.Normal, new Color(0.68f, 0.74f, 0.78f, 1f));
            LayoutElement resultLayout = _resultText.gameObject.AddComponent<LayoutElement>();
            resultLayout.minHeight = 116f;

            _built = true;
            RenderStatus();
        }

        private void BuildControls(Transform parent)
        {
            _reactionButtonLabel = AddButton(parent, "Reaction: Silent", CycleReaction).GetComponentInChildren<TMP_Text>();
            _modeButtonLabel = AddButton(parent, "Advanced mode: Append", CycleRawMode).GetComponentInChildren<TMP_Text>();

            AddSection(parent, "State");
            _stateNameInput = AddInput(parent, "State name", "Player location");
            _stateValueInput = AddInput(parent, "State value", "Lip Sync Lab");
            AddButtonRow(parent,
                ("Set", () => SetState(false)),
                ("Set + Flush", () => SetState(true)),
                ("Remove", () => RemoveState(true)));
            _queryStateInput = AddInput(parent, "Query state name", "Player location");
            AddButton(parent, "Read Local State", QueryState);

            AddSection(parent, "Event");
            _eventInput = AddInput(parent, "Event text", "The player tested dynamic context from the LipSync scene.");
            AddButtonRow(parent,
                ("Add", () => AddEvent(false)),
                ("Add + Flush", () => AddEvent(true)));

            AddSection(parent, "Attention Object");
            _attentionInput = AddInput(parent, "Action object name", "lever");
            AddButtonRow(parent,
                ("Set", () => SetAttention(false)),
                ("Set + Flush", () => SetAttention(true)),
                ("Clear", () => ClearAttention(true)));

            AddSection(parent, "Advanced Update");
            _rawTextInput = AddInput(parent, "Advanced context text", "Scene state: dynamic context debug panel is active.", true);
            _updateIdInput = AddInput(parent, "Update id", string.Empty);
            _removeStaticToggle = AddToggle(parent, "remove_static on reset");
            AddButton(parent, "Apply Advanced Update", ApplyRaw);

            AddSection(parent, "Session Controls");
            AddButtonRow(parent,
                ("Flush", Flush),
                ("Reset Runtime", () => ResetContext(false)),
                ("Reset + Static", () => ResetContext(true)));
        }

        private static void AddSection(Transform parent, string title)
        {
            TMP_Text label = SampleDebugUi.AddText(parent, title, 14f, FontStyles.Bold, new Color(0.55f, 0.83f, 0.62f, 1f));
            LayoutElement layout = label.gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 22f;
        }

        private static TMP_InputField AddInput(Transform parent, string placeholder, string value, bool multiline = false)
            => SampleDebugUi.CreateInput(parent, placeholder, value, multiline);

        private static Toggle AddToggle(Transform parent, string label)
        {
            GameObject row = SampleDebugUi.CreateUiObject("Toggle", parent);
            row.AddComponent<HorizontalLayoutGroup>().spacing = 8f;
            LayoutElement layout = row.AddComponent<LayoutElement>();
            layout.minHeight = 28f;

            GameObject box = SampleDebugUi.CreateUiObject("Box", row.transform);
            Image boxImage = box.AddComponent<Image>();
            boxImage.color = new Color(0.16f, 0.18f, 0.20f, 1f);
            LayoutElement boxLayout = box.AddComponent<LayoutElement>();
            boxLayout.minWidth = 24f;
            boxLayout.preferredWidth = 24f;
            boxLayout.minHeight = 24f;

            GameObject check = SampleDebugUi.CreateUiObject("Checkmark", box.transform);
            Image checkImage = check.AddComponent<Image>();
            checkImage.color = new Color(0.43f, 0.81f, 0.53f, 1f);
            RectTransform checkRect = check.GetComponent<RectTransform>();
            checkRect.anchorMin = new Vector2(0.22f, 0.22f);
            checkRect.anchorMax = new Vector2(0.78f, 0.78f);
            checkRect.offsetMin = Vector2.zero;
            checkRect.offsetMax = Vector2.zero;

            TMP_Text labelText = SampleDebugUi.AddText(row.transform, label, 13f, FontStyles.Normal, Color.white);
            labelText.alignment = TextAlignmentOptions.MidlineLeft;

            Toggle toggle = row.AddComponent<Toggle>();
            toggle.targetGraphic = boxImage;
            toggle.graphic = checkImage;
            toggle.isOn = false;
            return toggle;
        }

        private static Button AddButton(Transform parent, string text, UnityAction onClick)
        {
            GameObject go = SampleDebugUi.CreateUiObject("Button", parent);
            var image = go.AddComponent<Image>();
            image.color = new Color(0.18f, 0.34f, 0.25f, 1f);
            LayoutElement layout = go.AddComponent<LayoutElement>();
            layout.minHeight = 34f;

            Button button = go.AddComponent<Button>();
            button.targetGraphic = image;
            button.onClick.AddListener(onClick);
            SampleDebugUi.StylePrimaryButton(button, image);

            TMP_Text label = SampleDebugUi.AddText(go.transform, text, 13f, FontStyles.Bold, Color.white);
            RectTransform labelRect = label.GetComponent<RectTransform>();
            labelRect.anchorMin = Vector2.zero;
            labelRect.anchorMax = Vector2.one;
            labelRect.offsetMin = Vector2.zero;
            labelRect.offsetMax = Vector2.zero;
            label.alignment = TextAlignmentOptions.Center;

            return button;
        }

        private static void AddButtonRow(Transform parent, params (string Label, UnityAction Action)[] buttons)
        {
            GameObject row = SampleDebugUi.CreateUiObject("ButtonRow", parent);
            var layout = row.AddComponent<HorizontalLayoutGroup>();
            layout.spacing = 6f;
            layout.childForceExpandWidth = true;
            layout.childForceExpandHeight = false;
            LayoutElement rowLayout = row.AddComponent<LayoutElement>();
            rowLayout.minHeight = 34f;

            foreach ((string label, UnityAction action) in buttons)
                AddButton(row.transform, label, action);
        }
    }
}
