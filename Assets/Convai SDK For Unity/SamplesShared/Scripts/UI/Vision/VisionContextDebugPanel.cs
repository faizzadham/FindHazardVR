using System;
using Convai.Domain.DomainEvents.Vision;
using Convai.Domain.EventSystem;
using Convai.Runtime;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Vision.Context;
using Convai.SampleCommon.UI.Debug;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.Vision
{
    /// <summary>
    ///     Sample-only vision debug controls for dynamic vision context testing.
    /// </summary>
    public sealed class VisionContextDebugPanel : MonoBehaviour, ISampleDebugPanel
    {
        private const float HostedPanelHeight = 320f;

        [SerializeField] private ConvaiPlayer _player;
        [SerializeField] private ConvaiRoomManager _roomManager;
        [SerializeField] private bool _autoResolve = true;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private int _sortingOrder = 655;

        private TMP_InputField _textMessageInput;
        private TMP_InputField _visionPromptInput;
        private TMP_InputField _visionRespondModeInput;
        private TMP_Text _resultText;
        private GameObject _panelRoot;
        private RectTransform _hostedContentRoot;
        private string _lastAction = "Idle";
        private IEventHub _eventHub;
        private SubscriptionToken _visionStatusToken;
        private SubscriptionToken _visionTriggerToken;
        private bool _hostedInHub;
        private bool _built;

        public string PanelLabel => "Vision";
        public Vector2 PreferredDrawerSize => new(460f, 360f);
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

        private void OnDestroy()
        {
            if (_eventHub == null)
                return;

            if (_visionStatusToken != default)
                _eventHub.Unsubscribe(_visionStatusToken);
            if (_visionTriggerToken != default)
                _eventHub.Unsubscribe(_visionTriggerToken);
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

        private void SendTextMessage()
        {
            ResolveReferences();
            TrySubscribeVisionEvents();
            if (_player == null)
            {
                WriteResult("No ConvaiPlayer found");
                return;
            }

            _player.SendTextMessage(_textMessageInput.text);
            WriteResult("Sent text message");
        }

        private void RequestVisionStatus()
        {
            if (!TryResolveRoomManager())
                return;

            bool sent = _roomManager.RequestVisionStatus();
            WriteResult(sent ? "Requested vision status" : "Vision status not sent");
        }

        private void TriggerVision()
        {
            if (!TryResolveRoomManager())
                return;

            var request = new ConvaiVisionTriggerRequest { Text = _visionPromptInput.text };
            string modeText = _visionRespondModeInput.text;
            if (!string.IsNullOrWhiteSpace(modeText))
            {
                if (!ConvaiRespondModeExtensions.TryParseWireString(modeText, out ConvaiRespondMode mode))
                {
                    WriteResult($"Invalid respond mode '{modeText}' — use silent, auto or must_respond");
                    return;
                }

                request.RespondMode = mode;
            }

            bool sent = _roomManager.TriggerVision(request);
            WriteResult(sent ? "Triggered vision" : "Vision trigger not sent");
        }

        private bool TryResolveRoomManager()
        {
            ResolveReferences();
            TrySubscribeVisionEvents();
            if (_roomManager != null)
                return true;

            WriteResult("No ConvaiRoomManager found");
            return false;
        }

        private void ResolveReferences()
        {
            if (!_autoResolve)
                return;

            if (_player == null)
                _player = FindAnyObjectByType<ConvaiPlayer>();
            if (_roomManager == null)
                _roomManager = FindAnyObjectByType<ConvaiRoomManager>();
        }

        private void TrySubscribeVisionEvents()
        {
            if (_eventHub != null)
                return;

            ConvaiManager manager = FindAnyObjectByType<ConvaiManager>();
            if (manager == null || !manager.TryGetEventHub(out IEventHub eventHub))
                return;

            _eventHub = eventHub;
            _visionStatusToken = _eventHub.Subscribe<VisionContextStatusReceived>(OnVisionStatusReceived);
            _visionTriggerToken = _eventHub.Subscribe<VisionContextTriggerReceived>(OnVisionTriggerReceived);
        }

        private void OnVisionStatusReceived(VisionContextStatusReceived evt)
        {
            _lastAction =
                $"Vision status: status={evt.Status}, outcome={evt.Outcome}, source={evt.ActiveSourceLabel}, ageMs={evt.LastFrameAgeMs}";
            WriteResult(_lastAction);
        }

        private void OnVisionTriggerReceived(VisionContextTriggerReceived evt)
        {
            _lastAction =
                $"Vision trigger: status={evt.Status}, outcome={evt.Outcome}, attach={evt.AttachOutcome}, frames={evt.FramesAttached}, downgraded={evt.Downgraded}";
            WriteResult(_lastAction);
        }

        private void WriteResult(string text)
        {
            _lastAction = text;
            if (_resultText != null)
                _resultText.text = text;
        }

        private void BuildUi()
        {
            if (_built)
                return;

            Transform uiParent = _hostedInHub ? _hostedContentRoot : transform;
            if (_hostedInHub)
            {
                _panelRoot = SampleDebugUi.CreateHostedRoot("VisionPanel", uiParent, HostedPanelHeight);
                _panelRoot.SetActive(false);
                uiParent = _panelRoot.transform;
            }
            else
            {
                SampleDebugUi.EnsureStandaloneCanvas(gameObject, _sortingOrder);
            }

            GameObject panel = SampleDebugUi.CreateUiObject("Panel", uiParent);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.045f, 0.048f, 0.055f, 0.92f);

            if (!_hostedInHub)
            {
                RectTransform panelRect = panel.GetComponent<RectTransform>();
                panelRect.anchorMin = new Vector2(0f, 1f);
                panelRect.anchorMax = new Vector2(0f, 1f);
                panelRect.pivot = new Vector2(0f, 1f);
                panelRect.anchoredPosition = new Vector2(24f, -24f);
                panelRect.sizeDelta = new Vector2(440f, 320f);
            }
            else
            {
                Stretch(panel.GetComponent<RectTransform>());
            }

            VerticalLayoutGroup panelLayout = panel.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(14, 14, 12, 12);
            panelLayout.spacing = 8f;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            SampleDebugUi.AddText(panel.transform, "Dynamic Vision Context", 20f, FontStyles.Bold);
            _textMessageInput = AddInput(panel.transform, "Text message", "Describe the objects in front of you.");
            AddButton(panel.transform, "Send Text", SendTextMessage);
            _visionPromptInput = AddInput(panel.transform, "Vision prompt", "What objects can you see in the scene?");
            _visionRespondModeInput = AddInput(panel.transform, "Respond mode", "must_respond");
            AddButtonRow(panel.transform,
                ("Vision Status", RequestVisionStatus),
                ("Vision Trigger", TriggerVision));
            _resultText = SampleDebugUi.AddText(panel.transform, "Idle", 12f, FontStyles.Normal, new Color(0.68f, 0.74f, 0.78f, 1f));

            _built = true;
        }

        private static void Stretch(RectTransform rect)
        {
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = Vector2.zero;
            rect.offsetMax = Vector2.zero;
        }

        private static TMP_InputField AddInput(Transform parent, string placeholder, string value)
            => SampleDebugUi.CreateInput(parent, placeholder, value);

        private static Button AddButton(Transform parent, string text, UnityAction onClick)
        {
            GameObject go = SampleDebugUi.CreateUiObject("Button", parent);
            Image image = go.AddComponent<Image>();
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
            HorizontalLayoutGroup layout = row.AddComponent<HorizontalLayoutGroup>();
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
