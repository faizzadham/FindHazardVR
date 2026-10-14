using System.Text;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Diagnostics;
using Convai.Modules.BodyLanguage.Data;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace Convai.SampleCommon.UI.BodyLanguage
{
    /// <summary>
    ///     Large runtime overlay for the Convai Body Language system: dialogue state, rig
    ///     resolution, posture (openness/lean/tension/lateral shift, current vs. target),
    ///     breath, gesticulation (suppression, cadence, posture pulse, last semantic cue),
    ///     head-gesture program status, listening/fidget signals, and the scrolling trace
    ///     (the same ring buffer the logs write). Toggle with F3.
    /// </summary>
    /// <remarks>
    ///     Everything is read from <see cref="ConvaiBodyLanguageController.CaptureSnapshot(BodyLanguageSnapshot)" />,
    ///     so what the panel shows is exactly what the diagnostics record — a screenshot of
    ///     this HUD is a valid bug report.
    /// </remarks>
    public sealed class BodyLanguageDebugPanel : MonoBehaviour
    {
        private const float RefreshInterval = 0.1f;
        private const int TraceLines = 12;
        private const int BarSegments = 24;

        private const string ColorHeader = "#7FD4FF";
        private const string ColorGood = "#7DFF9C";
        private const string ColorWarn = "#FFD37D";
        private const string ColorDim = "#9AA5B1";

        [SerializeField] private ConvaiBodyLanguageController _controller;
        [SerializeField] private bool _autoResolve = true;
        [SerializeField] private bool _showOnStart = true;
        [SerializeField] private int _sortingOrder = 672;
        [SerializeField, Min(200f)] private float _panelWidth = 560f;

        private readonly BodyLanguageSnapshot _snapshot = new();
        private readonly StringBuilder _text = new(4096);

        private GameObject _root;
        private TMP_Text _content;
        private float _refreshTimer;
        private bool _built;

        public void SetController(ConvaiBodyLanguageController controller) => _controller = controller;

        private void Awake()
        {
            BuildUi();
            _root.SetActive(_showOnStart);
        }

        private void Update()
        {
            if (Keyboard.current != null && Keyboard.current.f3Key.wasPressedThisFrame)
                _root.SetActive(!_root.activeSelf);

            // F4 = A/B toggle: enable/disable the whole Body Language layer at runtime so its
            // contribution is instantly comparable on vs off — the single most useful thing when
            // evaluating a "feel" system. Works whether or not the HUD is visible; the
            // controller's OnDisable fades its deltas out and restores the animator pose (no
            // residual), and re-enabling resumes cleanly.
            if (Keyboard.current != null && Keyboard.current.f4Key.wasPressedThisFrame)
            {
                if (_controller == null && _autoResolve)
                    _controller = FindAnyObjectByType<ConvaiBodyLanguageController>();
                if (_controller != null)
                    _controller.enabled = !_controller.enabled;
            }

            if (!_root.activeSelf) return;

            if (_autoResolve && _controller == null)
                _controller = FindAnyObjectByType<ConvaiBodyLanguageController>();

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
                    $"<color={ColorHeader}><b>CONVAI BODY LANGUAGE</b></color>\n" +
                    $"<color={ColorWarn}>No ConvaiBodyLanguageController found.</color>";
                return;
            }

            _controller.CaptureSnapshot(_snapshot);
            _text.Clear();

            _text.Append($"<color={ColorHeader}><b>CONVAI BODY LANGUAGE</b></color>")
                 .Append($"  <color={ColorDim}>{Escape(_controller.name, 30)} | F3 hide</color>\n");

            // A/B state — the headline the sample is for: is the layer contributing right now?
            string blState = _controller.enabled
                ? $"<color={ColorGood}>ON</color>"
                : $"<color={ColorWarn}>OFF (animator only)</color>";
            _text.Append($"body language <b>{blState}</b>  <color={ColorDim}>F4 A/B toggle</color>\n");

            // Header
            string inert = _snapshot.IsInert ? $"  <color={ColorWarn}>INERT</color>" : string.Empty;
            _text.Append($"dialogue <b>{_snapshot.DialogueState}</b>  ")
                 .Append($"profile <color={ColorDim}>'{Escape(_snapshot.ProfileName, 30)}'</color>{inert}\n")
                 .Append($"rig spine {(_snapshot.HasSpine ? "ok" : "missing")}  ")
                 .Append($"chest {(_snapshot.HasChest ? "ok" : "-")}  ")
                 .Append($"upper {(_snapshot.HasUpperChest ? "ok" : "-")}  ")
                 .Append($"shoulders {(_snapshot.HasShoulders ? "ok" : "-")}\n")
                 .Append($"procedural arms {(_snapshot.HasProceduralArmChain ? "ok" : "-")}  ")
                 .Append($"fingers {(_snapshot.HasProceduralFingerChain ? "ok" : "wrist-only")}\n");

            // Posture
            _text.Append($"\n<color={ColorHeader}><b>POSTURE</b></color>\n")
                 .Append($"open {_snapshot.PostureOpennessCurrent,6:+0.00;-0.00} (->{_snapshot.PostureOpennessTarget,6:+0.00;-0.00})  ")
                 .Append($"lean {_snapshot.PostureLeanCurrent,6:+0.00;-0.00} (->{_snapshot.PostureLeanTarget,6:+0.00;-0.00})  ")
                 .Append($"tension {_snapshot.PostureTensionCurrent,6:+0.00;-0.00} (->{_snapshot.PostureTensionTarget,6:+0.00;-0.00})\n")
                 .Append($"lateral {_snapshot.PostureLateralShiftCurrent,6:+0.00;-0.00} (->{_snapshot.PostureLateralShiftTarget,6:+0.00;-0.00})\n")
                 .Append($"weight {Bar(_snapshot.MasterWeight, BarSegments)} {_snapshot.MasterWeight:F2}  ")
                 .Append($"suppression {Bar(_snapshot.PostureSuppressionWeight, 10)} {_snapshot.PostureSuppressionWeight:F2}\n");

            // Breath
            _text.Append($"\n<color={ColorHeader}><b>BREATH</b></color>\n")
                 .Append($"phase {_snapshot.BreathPhase:0.00} wave {_snapshot.BreathWaveform,6:+0.00;-0.00}  ")
                 .Append($"rate {_snapshot.BreathRateCpm:0.0} cpm  depth {_snapshot.BreathDepth:0.00}\n");

            // Gesticulation
            _text.Append($"\n<color={ColorHeader}><b>GESTICULATION</b></color>\n")
                 .Append($"suppression <b>{_snapshot.GesticulationSuppression}</b>  ")
                 .Append($"cadence {(_snapshot.GesticulationStatisticalCadenceActive ? "<color=" + ColorWarn + ">STATISTICAL</color>" : "energy")}  ")
                 .Append($"posturePulse {_snapshot.GesticulationPosturePulseValue:0.00}\n")
                 .Append($"last cue <b>{_snapshot.LastGestureCueKind}</b> ")
                 .Append(_snapshot.LastGestureCueAccepted
                     ? $"<color={ColorGood}>accepted</color>\n"
                     : $"<color={ColorDim}>refused/none</color>\n")
                 .Append($"procedural fallback {(_snapshot.ProceduralGestureFallbackActive ? "<color=" + ColorGood + ">ACTIVE</color>" : "inactive")}\n");

            // Head gesture
            _text.Append($"\n<color={ColorHeader}><b>HEAD GESTURE</b></color>\n")
                 .Append(_snapshot.HeadGestureIsPlaying
                     ? $"<color={ColorGood}>playing</color> {Bar(_snapshot.HeadGestureProgress, 10)} {_snapshot.HeadGestureProgress:F2}\n"
                     : $"<color={ColorDim}>idle</color>\n")
                 .Append($"consumers {_snapshot.HeadGestureConsumerCount}  ")
                 .Append($"fallback {(_snapshot.HeadGestureFallbackActive ? "<color=" + ColorWarn + ">ACTIVE</color>" : "-")}\n");

            // Listening / fidget
            _text.Append($"\n<color={ColorHeader}><b>LISTENING / FIDGET</b></color>\n")
                 .Append($"lean-in {Bar(_snapshot.ListeningLeanIn, 10)} {_snapshot.ListeningLeanIn:F2}  ")
                 .Append($"still {Bar(_snapshot.ListeningStillnessFactor, 10)} {_snapshot.ListeningStillnessFactor:F2}\n")
                 .Append($"tilt-hold {(_snapshot.ListeningWantsTiltHold ? "<b>WANT</b>" : "-")}  ")
                 .Append($"fidget shift {_snapshot.FidgetWeightShift,6:+0.00;-0.00}\n");

            // Motion meter (v2 plan §4.8): one line per channel, converting a representative
            // rotational delta into an approximate linear travel at the sternum via the
            // compositor's SternumLeverMeters lever-arm estimate — makes small swing deltas
            // legible without a scene-view ruler. All values come straight off the snapshot
            // (no new per-refresh allocation beyond this method's existing StringBuilder use).
            float breathDeg = _snapshot.BreathAppliedSagittalDegrees;
            float breathCm = DegreesToCentimeters(breathDeg, _snapshot.SternumLeverMeters);
            _text.Append($"\n<color={ColorHeader}><b>MOTION METER</b></color>\n")
                 .Append($"Breath      {Bar(Mathf.Clamp01(Mathf.Abs(breathDeg) / 6f), 12)}  ")
                 .Append($"{breathDeg,5:+0.0;-0.0}°/{breathCm,5:+0.0;-0.0}cm  duck {_snapshot.BreathDuckFactor:0.00}\n")
                 .Append($"Posture     {Bar(Mathf.Clamp01((Mathf.Abs(_snapshot.PostureOpennessCurrent) + Mathf.Abs(_snapshot.PostureLeanCurrent)) * 0.5f), 12)}  ")
                 .Append($"open {_snapshot.PostureOpennessCurrent,6:+0.00;-0.00}  lean {_snapshot.PostureLeanCurrent,6:+0.00;-0.00}\n")
                 .Append($"Sway        {Bar(Mathf.Clamp01((Mathf.Abs(_snapshot.SwaySagittal) + Mathf.Abs(_snapshot.SwayLateral)) * 0.5f), 12)}  ")
                 .Append($"sag {_snapshot.SwaySagittal,6:+0.00;-0.00}  lat {_snapshot.SwayLateral,6:+0.00;-0.00}\n")
                 .Append($"Stance      {Bar(Mathf.Clamp01(Mathf.Abs(_snapshot.StanceLateralCentimeters) / 6f), 12)}  ")
                 .Append($"{_snapshot.StanceLateralCentimeters,5:+0.0;-0.0}cm  obliquity {_snapshot.StanceObliquityDegrees,6:+0.00;-0.00}°\n")
                 .Append($"Accent      {Bar(Mathf.Clamp01(_snapshot.GesticulationPosturePulseValue), 12)}  ")
                 .Append($"pulse {_snapshot.GesticulationPosturePulseValue:0.00}\n")
                 .Append($"Occupancy   {Bar(Mathf.Clamp01(_snapshot.UpperBodyOccupancy), 12)}  ")
                 .Append(_snapshot.UsingMotionBudget
                     ? $"{_snapshot.UpperBodyOccupancy:0.00}\n"
                     : $"<color={ColorDim}>no budget</color>\n")
                 .Append($"Expressive  {Bar(Mathf.Clamp01(_snapshot.Expressiveness), 12)}  ")
                 .Append($"{_snapshot.Expressiveness:0.00}  amp {_snapshot.AmplitudeGain:0.00}x freq {_snapshot.FrequencyGain:0.00}x rich {_snapshot.RichnessGain:0.00}x\n")
                 .Append($"Reaction    {Bar(Mathf.Clamp01(Mathf.Max(Mathf.Abs(_snapshot.ReactionFlinch), Mathf.Abs(_snapshot.ReactionBounce))), 12)}  ")
                 .Append($"flinch {_snapshot.ReactionFlinch:0.00}  bounce {_snapshot.ReactionBounce,6:+0.00;-0.00}\n");

            // Trace
            _text.Append($"\n<color={ColorHeader}><b>TRACE</b></color> <color={ColorDim}>(newest last)</color>\n<size=13>");
            int start = Mathf.Max(0, _snapshot.RecentTrace.Count - TraceLines);
            for (int i = start; i < _snapshot.RecentTrace.Count; i++)
            {
                BodyLanguageTraceEntry entry = _snapshot.RecentTrace[i];
                string color = entry.Level == BodyLanguageTraceVerbosity.State ? "#E8EDF2" : ColorDim;
                _text.Append($"<color={ColorDim}>{entry.Time,7:F2}</color> <color={color}>{Escape(entry.Message, 90)}</color>\n");
            }
            _text.Append("</size>");

            _text.Append($"\n<color={ColorDim}>fps {1f / Mathf.Max(Time.smoothDeltaTime, 1e-4f):F0}</color>");
            _content.text = _text.ToString();
        }

        /// <summary>
        ///     Converts a small rotational delta (degrees) into an approximate linear travel
        ///     (centimeters) at the sternum, using the compositor's documented lever-arm estimate
        ///     (v2 plan §4.8 motion meter) — <c>tan(degrees) * leverMeters</c>, scaled to cm.
        /// </summary>
        private static float DegreesToCentimeters(float degrees, float leverMeters) =>
            Mathf.Tan(degrees * Mathf.Deg2Rad) * leverMeters * 100f;

        private static string Bar(float value01, int segments)
        {
            int filled = Mathf.Clamp(Mathf.RoundToInt(value01 * segments), 0, segments);
            return $"<color={ColorGood}>{new string('=', filled)}</color>" +
                   $"<color=#3A4450>{new string('-', segments - filled)}</color>";
        }

        // maxLen bounds free-form strings (controller/profile names, trace messages) so a single
        // unusually long value can never push a line past the panel's right edge — NoWrap is
        // deliberate (keeps the hand-aligned bars/columns intact), so unbounded text has to be
        // clamped here instead of wrapped or relying on overflow clipping.
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

            _root = new GameObject("BodyLanguageDebugCanvas");
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
            // Debug HUD must always show every line. With NoWrap+Truncate, a single
            // over-long line (e.g. a long controller GameObject name in the header, or a
            // long trace message) overflows horizontally and TMP stops laying out the rest
            // of the text — blanking every line below it. Overflow renders all lines
            // regardless of bounds (there is no mask clipping this canvas).
            _content.overflowMode = TextOverflowModes.Overflow;
            _content.alignment = TextAlignmentOptions.TopLeft;
        }
    }
}
