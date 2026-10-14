#if UNITY_EDITOR
using Convai.Editor.UI;
using Convai.Runtime.Vision.Sources;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor.Vision
{
    /// <summary>
    ///     Custom inspector for WebcamVisionFrameSource.
    ///     Captures frames from a physical webcam and streams them via the vision pipeline.
    ///     Sections: Webcam Device · Capture Settings · Live Status.
    /// </summary>
    [CustomEditor(typeof(WebcamVisionFrameSource))]
    internal sealed class WebcamVisionFrameSourceEditor : ConvaiVisionBaseEditor
    {
        private static readonly GUIContent ClearDeviceButton = new(
            "Clear", "Clears the device name so the system default webcam is used.");

        private static readonly GUIContent SelectDeviceButton = new(
            "Select", "Uses this webcam as the capture device.");

        private static readonly GUIContent DeviceNameLabel = new(
            "Device Name",
            "The name of the webcam to open, exactly as the OS reports it. " +
            "Leave empty to use the system default camera.");

        private static readonly GUIContent DefaultDeviceHint = new("Empty = system default camera");

        private static readonly GUIContent AvailableDevicesCaption = new("Available Devices");

        private static readonly GUIContent NoDevicesHint = new(
            "No devices detected. Connect a webcam or check device permissions.");

        private static readonly GUIContent IdentifierLabel = new(
            "Identifier",
            "Optional tag to distinguish this source when multiple WebcamVisionFrameSource " +
            "components exist in the same scene. Leave empty if only one webcam source is used.");

        /// <summary>Height of one device row; its label is centred in it beside the row's button.</summary>
        private const float DeviceRowHeight = 20f;

        /// <summary>Width of the column the selected device's status dot sits in.</summary>
        private const float DeviceDotColumn = 14f;

        // Section IDs & icons
        private const string SectionDeviceId = "WebcamDevice";
        private const string SectionCaptureId = "CaptureSettings";
        private const string SectionStatusId = "LiveStatus";


        private SerializedProperty _deviceNameProp;
        private SerializedProperty _fpsProp;
        private SerializedProperty _heightProp;

        // State
        private WebcamVisionFrameSource _source;
        private SerializedProperty _sourceIdProp;
        private SerializedProperty _widthProp;

        protected override string EditorStateHostId => "WebcamSourceEditor";

        // Lifecycle

        protected override void OnEnable()
        {
            base.OnEnable();
            _source = (WebcamVisionFrameSource)target;
            _deviceNameProp = serializedObject.FindProperty("_webcamDeviceName");
            _widthProp = serializedObject.FindProperty("_requestedWidth");
            _heightProp = serializedObject.FindProperty("_requestedHeight");
            _fpsProp = serializedObject.FindProperty("_requestedFps");
            _sourceIdProp = serializedObject.FindProperty("_sourceId");
        }

        protected override string Title => "Webcam Vision Frame Source";

        protected override string Subtitle => "Captures frames from a physical webcam";

        protected override GUIContent StatusChip
        {
            get
            {
                if (!EditorApplication.isPlaying) return ConvaiEditorChips.Ready.Content;
                return ConvaiEditorChips.Running(_source.IsCapturing).Content;
            }
        }

        protected override Color StatusChipTint
        {
            get
            {
                if (!EditorApplication.isPlaying) return ConvaiEditorChips.Ready.Tint;
                return ConvaiEditorChips.Running(_source.IsCapturing).Tint;
            }
        }

        /// <summary>Keeps the live capture readout updating while frames are flowing.</summary>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying && _source.IsCapturing;

        protected override void DrawBody()
        {
            DrawValidation();
            DrawDeviceSection();
            DrawCaptureSection();

            if (EditorApplication.isPlaying)
                DrawLiveStatus();
            else
                DrawStatusPlaceholder();
        }


        private void DrawValidation()
        {
#if UNITY_WEBGL
            // AsyncGPUReadback is unavailable on WebGL — the rest of the inspector is not useful.
            ErrorBox(
                "Not Supported On WebGL",
                "This frame source requires AsyncGPUReadback, which WebGL does not provide. " +
                "Use Camera Vision Frame Source to stream the game canvas instead.");
#else
#if UNITY_ANDROID
            InfoBox(
                "Android Camera Permission",
                "The CAMERA permission is requested at runtime. Make sure it is declared in your AndroidManifest.xml.");
#elif UNITY_IOS
            InfoBox(
                "iOS Camera Permission",
                "NSCameraUsageDescription must be set in Player Settings \u2192 Other Settings.");
#endif

            if (EditorApplication.isPlaying && WebCamTexture.devices.Length == 0)
                WarningBox("No Webcam Detected",
                    "No webcam devices found. Check that a camera is connected and that permissions are granted.");
#endif
        }


        private void DrawDeviceSection()
        {
            WebCamDevice[] devices = WebCamTexture.devices;
            string title = devices.Length > 0 ? $"Webcam Device  \u2014  {devices.Length} found" : "Webcam Device";

            if (!DrawSection(SectionDeviceId, title, ConvaiEditorGlyphs.Capture)) return;

            DrawSectionBody(() =>
            {
                EditorGUILayout.PropertyField(_deviceNameProp, DeviceNameLabel);

                if (string.IsNullOrEmpty(_deviceNameProp.stringValue))
                    GUILayout.Label(DefaultDeviceHint, ConvaiEditorTheme.CaptionWrapped);

                if (devices.Length > 0)
                {
                    ConvaiEditorTheme.GroupCaption(AvailableDevicesCaption);

                    for (int i = 0; i < devices.Length; i++)
                    {
                        WebCamDevice d = devices[i];
                        bool isSelected = _deviceNameProp.stringValue == d.name;
                        string facing = d.isFrontFacing ? "Front" : "Back";

                        EditorGUILayout.BeginHorizontal();
                        Rect dot = GUILayoutUtility.GetRect(
                            DeviceDotColumn, DeviceRowHeight, GUILayout.Width(DeviceDotColumn));
                        if (isSelected)
                            ConvaiEditorTheme.StatusDot(dot, ConvaiGreen);
                        GUILayout.Label(
                            $"{d.name}  ({facing})",
                            isSelected
                                ? ConvaiEditorStyles.ReadingValueTinted(ConvaiGreenLight)
                                : ConvaiEditorTheme.ReadingValue,
                            GUILayout.Height(DeviceRowHeight));
                        GUILayout.FlexibleSpace();

                        GUIContent action = isSelected ? ClearDeviceButton : SelectDeviceButton;
                        float buttonWidth = ConvaiEditorTheme.GhostButtonWidth(action);
                        Rect buttonRect = GUILayoutUtility.GetRect(
                            buttonWidth, DeviceRowHeight, GUILayout.Width(buttonWidth));
                        if (ConvaiEditorControls.GhostButton(buttonRect, action))
                        {
                            _deviceNameProp.stringValue = isSelected ? string.Empty : d.name;
                            serializedObject.ApplyModifiedProperties();
                        }

                        EditorGUILayout.EndHorizontal();
                    }
                }
                else
                {
                    EditorGUILayout.Space(4f);
                    GUILayout.Label(NoDevicesHint, ConvaiEditorTheme.CaptionWrapped);
                }
            });
        }


        private void DrawCaptureSection()
        {
            if (!DrawSection(SectionCaptureId, "Capture Settings", ConvaiEditorGlyphs.Profile)) return;

            DrawSectionBody(() =>
            {
                // Resolution on one row — more compact than two separate fields.
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(
                    new GUIContent("Requested Resolution",
                        "Hint to the device driver. " +
                        "The webcam may return a different resolution based on what it supports."),
                    GUILayout.Width(EditorGUIUtility.labelWidth));
                _widthProp.intValue = EditorGUILayout.IntField(
                    GUIContent.none, _widthProp.intValue, GUILayout.Width(58f));
                GUILayout.Label(
                    "\u00D7", ConvaiEditorTheme.CenteredMini(ConvaiEditorTheme.TextMuted), GUILayout.Width(14f));
                _heightProp.intValue = EditorGUILayout.IntField(
                    GUIContent.none, _heightProp.intValue, GUILayout.Width(58f));
                EditorGUILayout.EndHorizontal();

                _fpsProp.intValue = EditorGUILayout.IntField(
                    new GUIContent("Requested FPS",
                        "Hint to the device driver. Actual frame rate may differ based on hardware."),
                    _fpsProp.intValue);

                EditorGUILayout.Space(4f);

                // Labelled "Identifier" rather than "Source ID" — clearer for external developers.
                EditorGUILayout.PropertyField(_sourceIdProp, IdentifierLabel);

                // Show actual device-negotiated dimensions in play mode.
                // Only visible at runtime once the webcam has initialised.
                if (UnityEngine.Application.isPlaying && _source.CurrentRenderTexture != null)
                {
                    EditorGUILayout.Space(4f);
                    using (new EditorGUI.DisabledScope(true))
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent("Actual Resolution",
                                "The resolution the device opened at. May differ from what was requested."),
                            new GUIContent(
                                $"{_source.FrameDimensions.Width}\u00D7{_source.FrameDimensions.Height}" +
                                $"  @  {_source.TargetFrameRate:F0} fps"));
                    }
                }
            });
        }

        // Live Status section

        private void DrawStatusPlaceholder()
        {
            if (!DrawSection(SectionStatusId, "Live", ConvaiEditorGlyphs.Live,
                    accent: ConvaiEditorTheme.StatusInfo)) return;
            DrawSectionBody(DrawOfflinePlaceholder);
        }

        private void DrawLiveStatus()
        {
            if (!DrawSection(SectionStatusId, "Live", ConvaiEditorGlyphs.Live,
                    accent: ConvaiEditorTheme.StatusInfo)) return;

            Color bg = LiveSectionBackground(_source.IsCapturing);
            DrawSectionBody(() =>
            {
                var statusProvider = _source as IVisionFrameSourceStatusProvider;
                string label = _source.IsCapturing ? "\u25B6  CAPTURING" : "\u25CF  IDLE";
                DrawStatusRow(label, _source.IsCapturing ? ConvaiGreen : StatusIdle);

                EditorGUILayout.Space(6f);

                EditorGUILayout.BeginHorizontal();
                DrawLiveCell("Resolution",
                    $"{_source.FrameDimensions.Width}\u00D7{_source.FrameDimensions.Height}",
                    DefaultValueColor);
                DrawLiveCell("Target FPS", $"{_source.TargetFrameRate:F0}", DefaultValueColor);
                DrawLiveCell("Frames", $"{_source.FrameCount}", DefaultValueColor);
                EditorGUILayout.EndHorizontal();

                EditorGUILayout.Space(4f);

                EditorGUILayout.BeginHorizontal();
                DrawLiveCell("Frame Ready", _source.IsFrameReady ? "Yes" : "No",
                    _source.IsFrameReady ? ConvaiGreenLight : DefaultValueColor, _source.IsFrameReady);
                string id = string.IsNullOrEmpty(_source.SourceId) ? "(none)" : _source.SourceId;
                DrawLiveCell("Identifier", id, DefaultValueColor);
                EditorGUILayout.EndHorizontal();

                if (statusProvider != null)
                {
                    EditorGUILayout.Space(4f);
                    EditorGUILayout.BeginHorizontal();
                    DrawLiveCell("State", statusProvider.State.ToString(), DefaultValueColor);
                    DrawLiveCell("Error", statusProvider.ErrorKind.ToString(), DefaultValueColor);
                    EditorGUILayout.EndHorizontal();
                }

                if (!string.IsNullOrWhiteSpace(statusProvider?.StatusMessage))
                {
                    EditorGUILayout.Space(6f);
                    GUILayout.Label(statusProvider.StatusMessage, ConvaiEditorStyles.CaptionWrapped);
                }
                else if (!string.IsNullOrEmpty(_source.LastErrorCode))
                {
                    EditorGUILayout.Space(6f);
                    ErrorBox(
                        "Capture Failed",
                        $"{_source.LastErrorMessage}  [{_source.LastErrorCode}]");
                }
            }, bg);
        }
    }
}
#endif
