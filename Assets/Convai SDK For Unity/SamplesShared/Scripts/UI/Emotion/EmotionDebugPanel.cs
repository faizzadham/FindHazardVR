using System.Collections.Generic;
using System.Text;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.Emotion.Components;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.Emotion
{
    /// <summary>
    ///     Large runtime overlay for the Convai Emotion system: the dominant (transient) reading,
    ///     the persona/mood reading, the top active score contributors (non-zero entries reveal
    ///     blending), mouth influence, and the resolved-emotion/intensity pair. Toggle with F5.
    /// </summary>
    /// <remarks>
    ///     Everything is read from the public <see cref="ConvaiEmotionController.Current" />
    ///     (<see cref="EmotionReading" />) plus <see cref="ConvaiEmotionController.CurrentMoodLabel" />/
    ///     <see cref="ConvaiEmotionController.CurrentMoodScore" /> — no internal blend/micro-life
    ///     state is read, so a screenshot of this HUD is exactly what a customer sees from the
    ///     public API surface.
    /// </remarks>
    public sealed class EmotionDebugPanel : MonoBehaviour
    {
        private const float RefreshInterval = 0.1f;
        private const int BarSegments = 24;
        private const int TopScoreRows = 5;

        private const string ColorHeader = "#7FD4FF";
        private const string ColorGood = "#7DFF9C";
        private const string ColorWarn = "#FFD37D";
        private const string ColorDim = "#9AA5B1";

        [SerializeField] private ConvaiEmotionController _controller;
        [SerializeField] private bool _autoResolve = true;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private int _sortingOrder = 672;
        [SerializeField, Min(200f)] private float _panelWidth = 560f;

        private readonly StringBuilder _text = new(2048);
        private readonly List<KeyValuePair<string, float>> _topScores = new(8);

        private GameObject _root;
        private TMP_Text _content;
        private float _refreshTimer;
        private bool _built;

        public void SetController(ConvaiEmotionController controller) => _controller = controller;

        private void Awake()
        {
            BuildUi();
            _root.SetActive(_showOnStart);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f5Key.wasPressedThisFrame)
                _root.SetActive(!_root.activeSelf);

            if (!_root.activeSelf) return;

            if (_autoResolve && _controller == null)
                _controller = FindAnyObjectByType<ConvaiEmotionController>();

            _refreshTimer += Time.unscaledDeltaTime;
            if (_refreshTimer < RefreshInterval) return;
            _refreshTimer = 0f;

            Render();
        }

        // ------------------------------------------------------------------ rendering

        private void Render()
        {
            if (_controller == null)
            {
                _content.text =
                    $"<color={ColorHeader}><b>CONVAI EMOTION</b></color>\n" +
                    $"<color={ColorWarn}>No ConvaiEmotionController found.</color>";
                return;
            }

            EmotionReading reading = _controller.Current;
            _text.Clear();

            _text.Append($"<color={ColorHeader}><b>CONVAI EMOTION</b></color>")
                 .Append($"  <color={ColorDim}>{Escape(_controller.name, 30)} | F5 hide</color>\n");

            // Dominant (transient) reading
            _text.Append($"\n<color={ColorHeader}><b>DOMINANT</b></color>\n")
                 .Append($"<b>{Escape(reading.DominantLabel, 24)}</b> {Bar(reading.DominantScore, BarSegments)} {reading.DominantScore:F2}\n")
                 .Append($"hold {reading.DominantHoldSeconds:F1}s  ")
                 .Append($"mouth {Bar(reading.MouthInfluence, 10)} {reading.MouthInfluence:F2}\n");

            // Persona / mood reading
            _text.Append($"\n<color={ColorHeader}><b>MOOD (PERSONA BASELINE)</b></color>\n")
                 .Append($"<b>{Escape(reading.MoodLabel, 24)}</b> {Bar(reading.MoodScore, BarSegments)} {reading.MoodScore:F2}\n");

            // Resolved emotion / normalized intensity (public convenience accessors)
            _text.Append($"\n<color={ColorHeader}><b>RESOLVED</b></color>\n")
                 .Append($"emotion <b>{Escape(_controller.CurrentResolvedEmotion, 24)}</b>  ")
                 .Append($"intensity {_controller.CurrentNormalizedIntensity:F2}\n");

            // Top active scores (non-zero entries reveal blending)
            _text.Append($"\n<color={ColorHeader}><b>TOP SCORES</b></color> <color={ColorDim}>(non-zero = blending)</color>\n");
            EmotionScoreRanking.CollectTopScores(reading.AllScores, TopScoreRows, _topScores);
            if (_topScores.Count == 0)
            {
                _text.Append($"<color={ColorDim}>(none active)</color>\n");
            }
            else
            {
                for (int i = 0; i < _topScores.Count; i++)
                {
                    KeyValuePair<string, float> entry = _topScores[i];
                    _text.Append($"{Escape(entry.Key, 16),-16} {Bar(entry.Value, 16)} {entry.Value:F2}\n");
                }
            }

            _text.Append($"\n<color={ColorDim}>fps {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0}</color>");
            _content.text = _text.ToString();
        }

        private static string Bar(float value01, int segments)
        {
            int filled = Mathf.Clamp(Mathf.RoundToInt(value01 * segments), 0, segments);
            return $"<color={ColorGood}>{new string('=', filled)}</color>" +
                   $"<color=#3A4450>{new string('-', segments - filled)}</color>";
        }

        // maxLen bounds free-form strings (controller name, labels) so a single unusually long
        // value can never push a line past the panel's right edge — NoWrap is deliberate (keeps
        // the hand-aligned bars/columns intact), so unbounded text has to be clamped here instead
        // of wrapped or relying on overflow clipping.
        private static string Escape(string message, int maxLen = 48)
        {
            if (message == null) return string.Empty;
            string clean = message.Replace('<', '[').Replace('>', ']')
                .Replace('—', '-')
                .Replace("→", "->");
            return clean.Length > maxLen ? clean.Substring(0, maxLen - 1) + "…" : clean;
        }

        // ------------------------------------------------------------------ UI construction

        private void BuildUi()
        {
            if (_built) return;
            _built = true;

            _root = new GameObject("EmotionDebugCanvas");
            _root.transform.SetParent(transform, false);

            var canvas = _root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = _sortingOrder;
            _root.AddComponent<CanvasScaler>().uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            _root.GetComponent<CanvasScaler>().referenceResolution = new Vector2(1920f, 1080f);

            var panel = new GameObject("Panel", typeof(RectTransform), typeof(Image));
            panel.transform.SetParent(_root.transform, false);
            var panelRect = (RectTransform)panel.transform;
            panelRect.anchorMin = new Vector2(1f, 0f);
            panelRect.anchorMax = new Vector2(1f, 1f);
            panelRect.pivot = new Vector2(1f, 1f);
            panelRect.anchoredPosition = new Vector2(-12f, -12f);
            panelRect.sizeDelta = new Vector2(_panelWidth, -24f);
            panel.GetComponent<Image>().color = new Color(0.05f, 0.07f, 0.1f, 0.86f);

            var textGo = new GameObject("Content", typeof(RectTransform));
            textGo.transform.SetParent(panel.transform, false);
            var textRect = (RectTransform)textGo.transform;
            textRect.anchorMin = Vector2.zero;
            textRect.anchorMax = Vector2.one;
            textRect.offsetMin = new Vector2(14f, 12f);
            textRect.offsetMax = new Vector2(-14f, -12f);

            _content = textGo.AddComponent<TextMeshProUGUI>();
            _content.fontSize = 17f;
            _content.richText = true;
            _content.textWrappingMode = TextWrappingModes.NoWrap;
            // Must be Overflow, not Truncate: with NoWrap+Truncate, a single over-long line
            // (e.g. a long controller/label name) overflows horizontally and TMP stops laying
            // out every line below it, blanking the whole panel body. Overflow renders all lines
            // regardless of horizontal bounds (there is no mask clipping this canvas).
            _content.overflowMode = TextOverflowModes.Overflow;
            _content.alignment = TextAlignmentOptions.TopLeft;
        }
    }
}
