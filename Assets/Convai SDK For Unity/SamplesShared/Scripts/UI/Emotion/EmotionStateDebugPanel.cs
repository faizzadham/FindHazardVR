using System;
using System.Collections.Generic;
using Convai.Domain.Emotion;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.Emotion.Components;
using Convai.Runtime.Components;
using Convai.SampleCommon.UI.Debug;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.Emotion
{
    /// <summary>
    ///     Compact runtime overlay for checking emotion config, incoming events, and face output in sample scenes.
    /// </summary>
    public sealed class EmotionStateDebugPanel : MonoBehaviour, ISampleDebugPanel
    {
        private const float HostedPanelHeight = 188f;

        [SerializeField] private ConvaiCharacter _character;
        [SerializeField] private ConvaiEmotionController _emotionController;
        [SerializeField] private bool _autoResolve = true;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private int _sortingOrder = 660;

        private TMP_Text _summaryText;
        private TMP_Text _pipelineText;
        private TMP_Text _rawToFaceText;
        private TMP_Text _diagnosticText;
        private ConvaiCharacter _subscribedCharacter;
        private int _receivedEventCount;
        private string _lastEventEmotion;
        private int _lastEventIntensity;
        private float _lastEventRealtime = -1f;
        private GameObject _panelRoot;
        private RectTransform _hostedContentRoot;
        private bool _hostedInHub;
        private bool _built;

        public string PanelLabel => "Emotion";
        public Vector2 PreferredDrawerSize => new(560f, 260f);
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
            SubscribeToCharacter();
            Render();
        }

        private void OnDisable()
        {
            UnsubscribeFromCharacter();
        }

        private void Update()
        {
            ResolveReferences();
            Render();
        }

        public void SetCharacter(ConvaiCharacter character)
        {
            if (_character == character) return;

            if (isActiveAndEnabled && _character != null)
                UnsubscribeFromCharacter();

            _character = character;
            ResetEventState();
            if (!IsControllerForCharacter(_emotionController, _character))
                _emotionController = null;

            if (isActiveAndEnabled && _character != null)
                SubscribeToCharacter();

            ResolveEmotionController();
            Render();
        }

        public void SetEmotionController(ConvaiEmotionController emotionController)
        {
            if (_character == null && emotionController != null)
            {
                _character = emotionController.GetComponentInParent<ConvaiCharacter>(true);
                ResetEventState();
                if (isActiveAndEnabled)
                    SubscribeToCharacter();
            }

            _emotionController = IsControllerForCharacter(emotionController, _character)
                ? emotionController
                : null;
            Render();
        }

        private void HandleEmotionChanged(string emotion, int intensity)
        {
            _receivedEventCount++;
            _lastEventEmotion = emotion;
            _lastEventIntensity = intensity;
            _lastEventRealtime = Time.realtimeSinceStartup;
            Render();
        }

        private void ResolveReferences()
        {
            if (!_autoResolve) return;

            if (_character == null)
                _character = FindAnyObjectByType<ConvaiCharacter>();

            if (isActiveAndEnabled)
                SubscribeToCharacter();

            ResolveEmotionController();
        }

        private void SubscribeToCharacter()
        {
            if (_character == null || _subscribedCharacter == _character) return;

            UnsubscribeFromCharacter();
            _character.OnEmotionChanged += HandleEmotionChanged;
            _subscribedCharacter = _character;
        }

        private void UnsubscribeFromCharacter()
        {
            if (_subscribedCharacter == null) return;

            _subscribedCharacter.OnEmotionChanged -= HandleEmotionChanged;
            _subscribedCharacter = null;
        }

        private void ResetEventState()
        {
            _receivedEventCount = 0;
            _lastEventEmotion = null;
            _lastEventIntensity = 0;
            _lastEventRealtime = -1f;
        }

        private void ResolveEmotionController()
        {
            if (_character == null)
            {
                _emotionController = null;
                return;
            }

            if (IsControllerForCharacter(_emotionController, _character)) return;

            _emotionController = _character.GetComponentInChildren<ConvaiEmotionController>(true) ??
                                 _character.GetComponentInParent<ConvaiEmotionController>(true);
        }

        private static bool IsControllerForCharacter(
            ConvaiEmotionController emotionController,
            ConvaiCharacter character)
        {
            if (emotionController == null || character == null) return false;

            return emotionController.GetComponentInParent<ConvaiCharacter>(true) == character ||
                   character.GetComponentInParent<ConvaiEmotionController>(true) == emotionController;
        }

        private void Render()
        {
            if (!_built) return;

            EmotionDetectionMode detectionMode = _character != null
                ? _character.EmotionDetectionMode
                : EmotionDetectionMode.Off;
            string characterName = _character != null && !string.IsNullOrWhiteSpace(_character.CharacterName)
                ? _character.CharacterName
                : "No character";
            string roomState = _character != null ? _character.SessionState.ToString() : "missing";
            string rawEmotion = _character != null && !string.IsNullOrWhiteSpace(_character.CurrentEmotion)
                ? _character.CurrentEmotion
                : _lastEventEmotion ?? "none";
            int currentIntensity = _character != null ? _character.CurrentEmotionIntensity : 0;
            int rawIntensity = currentIntensity > 0 ? currentIntensity : _lastEventIntensity;
            string resolvedEmotion = _emotionController != null
                ? _emotionController.CurrentResolvedEmotion
                : "neutral";
            float resolvedIntensity = _emotionController != null
                ? _emotionController.CurrentNormalizedIntensity
                : 0f;
            EmotionReading reading = _emotionController != null
                ? _emotionController.Current
                : EmotionReading.Neutral;

            if (_summaryText != null)
                _summaryText.text =
                    $"{characterName} | Room {roomState} | Mode {FormatMode(detectionMode)}";

            if (_pipelineText != null)
                _pipelineText.text =
                    $"CONFIG {Status(detectionMode != EmotionDetectionMode.Off)}  " +
                    $"EVENT {Status(_receivedEventCount > 0 || rawIntensity > 0)}  " +
                    $"FACE {Status(_emotionController != null)}";

            if (_rawToFaceText != null)
                _rawToFaceText.text =
                    $"event: {FormatEvent(rawEmotion, rawIntensity)}  ->  " +
                    $"face: {resolvedEmotion} {resolvedIntensity:0.00}\n" +
                    $"count: {_receivedEventCount} | age: {FormatEventAge()} | scores: {FormatTopScores(reading.AllScores, 2)}";

            if (_diagnosticText != null)
                _diagnosticText.text = ResolveDiagnostic(detectionMode, rawEmotion, rawIntensity, resolvedEmotion,
                    resolvedIntensity);
        }

        private static string FormatMode(EmotionDetectionMode mode)
        {
            switch (mode)
            {
                case EmotionDetectionMode.Nrclex:
                    return "NRCLex -> emotion_config.provider=nrclex";
                case EmotionDetectionMode.Llm:
                    return "LLM -> emotion_config.provider=llm";
                default:
                    return "Off -> no emotion_config";
            }
        }

        private static string Status(bool ok) => ok ? "OK" : "--";

        private static string FormatEvent(string rawEmotion, int rawIntensity)
        {
            if (rawIntensity <= 0) return "none";

            return $"{rawEmotion} scale {rawIntensity}/3";
        }

        private string ResolveDiagnostic(
            EmotionDetectionMode detectionMode,
            string rawEmotion,
            int rawIntensity,
            string resolvedEmotion,
            float resolvedIntensity)
        {
            if (_character == null)
                return "MISSING CHARACTER: assign ConvaiCharacter.";

            if (_emotionController == null)
                return "NO FACE OUTPUT: add/assign ConvaiEmotionController.";

            if (detectionMode == EmotionDetectionMode.Off)
                return "EMOTIONS OFF: set Detection Mode to NRCLex or LLM.";

            if (rawIntensity <= 0)
                return "WAITING FOR EVENT: talk to character; expected bot-emotion after response.";

            if (!IsNeutral(rawEmotion) && IsNeutral(resolvedEmotion) && resolvedIntensity <= 0f)
                return "MAPPING ISSUE: event arrived but face stayed neutral. Check taxonomy/profile.";

            return "OK: event is reaching Unity and controller has face output.";
        }

        private static bool IsNeutral(string emotion) =>
            string.Equals(emotion, "neutral", StringComparison.OrdinalIgnoreCase);

        private string FormatEventAge()
        {
            if (_lastEventRealtime < 0f) return "none";

            float elapsed = Mathf.Max(0f, Time.realtimeSinceStartup - _lastEventRealtime);
            return $"{elapsed:0.0}s ago";
        }

        private static string FormatTopScores(IReadOnlyDictionary<string, float> scores, int maxCount)
        {
            if (scores == null || scores.Count == 0) return "none";

            List<KeyValuePair<string, float>> ranked = new List<KeyValuePair<string, float>>(scores);
            ranked.Sort((a, b) => b.Value.CompareTo(a.Value));

            int written = 0;
            string result = string.Empty;
            for (int i = 0; i < ranked.Count && written < maxCount; i++)
            {
                KeyValuePair<string, float> score = ranked[i];
                if (score.Value <= 0.001f) continue;

                if (written > 0) result += " | ";
                result += $"{score.Key} {score.Value:0.00}";
                written++;
            }

            return written > 0 ? result : "neutral";
        }

        private void BuildUi()
        {
            if (_built) return;

            Transform uiParent = _hostedInHub ? _hostedContentRoot : transform;
            if (_hostedInHub)
            {
                _panelRoot = SampleDebugUi.CreateHostedRoot("EmotionPanel", uiParent, HostedPanelHeight);
                _panelRoot.SetActive(false);
                uiParent = _panelRoot.transform;
            }
            else
            {
                SampleDebugUi.EnsureStandaloneCanvas(gameObject, _sortingOrder);
            }

            GameObject panel = SampleDebugUi.CreateUiObject("Panel", uiParent);
            Image panelImage = panel.AddComponent<Image>();
            panelImage.color = new Color(0.045f, 0.048f, 0.055f, 0.9f);

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
                panelRect.anchorMin = new Vector2(0f, 0f);
                panelRect.anchorMax = new Vector2(0f, 0f);
                panelRect.pivot = new Vector2(0f, 0f);
                panelRect.anchoredPosition = new Vector2(24f, 24f);
                panelRect.sizeDelta = new Vector2(560f, 188f);
            }

            VerticalLayoutGroup panelLayout = panel.AddComponent<VerticalLayoutGroup>();
            panelLayout.padding = new RectOffset(14, 14, 10, 10);
            panelLayout.spacing = 6f;
            panelLayout.childForceExpandWidth = true;
            panelLayout.childForceExpandHeight = false;

            SampleDebugUi.AddText(panel.transform, "Emotion Debug", 18f, FontStyles.Bold);
            _summaryText = SampleDebugUi.AddText(panel.transform, "Resolving...", 12f, FontStyles.Normal,
                new Color(0.78f, 0.82f, 0.86f, 1f));
            _pipelineText = SampleDebugUi.AddText(panel.transform, string.Empty, 13f, FontStyles.Bold,
                new Color(0.70f, 0.86f, 0.96f, 1f));
            _rawToFaceText = AddReading(panel.transform);
            _diagnosticText = SampleDebugUi.AddText(panel.transform, string.Empty, 12f, FontStyles.Bold,
                new Color(0.94f, 0.86f, 0.62f, 1f));

            _built = true;
            Render();
        }

        private static TMP_Text AddReading(Transform parent)
        {
            GameObject card = SampleDebugUi.CreateUiObject("Reading", parent);
            Image image = card.AddComponent<Image>();
            image.color = new Color(0.02f, 0.022f, 0.026f, 0.62f);
            LayoutElement layout = card.AddComponent<LayoutElement>();
            layout.minHeight = 56f;
            layout.flexibleWidth = 1f;

            TMP_Text text = SampleDebugUi.AddText(card.transform, string.Empty, 12f, FontStyles.Normal, Color.white);
            RectTransform rect = text.GetComponent<RectTransform>();
            rect.anchorMin = Vector2.zero;
            rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(10f, 8f);
            rect.offsetMax = new Vector2(-10f, -8f);
            text.alignment = TextAlignmentOptions.MidlineLeft;
            return text;
        }

    }
}
