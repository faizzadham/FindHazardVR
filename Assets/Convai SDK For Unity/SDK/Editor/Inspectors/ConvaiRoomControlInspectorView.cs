using Convai.Infrastructure.Networking.Models;
using Convai.Runtime.Room;
using Convai.Runtime.Vision.Context;
using Convai.Shared.Types;
using UnityEditor;
using UnityEngine;
using Content = Convai.Editor.ConvaiInspectorContent;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     The serialized properties the room cards draw. They come from the scene component or from a
    ///     Room Manager Profile asset, so every field is optional: a null property means "this host does not own
    ///     that setting" and the row is skipped.
    /// </summary>
    internal sealed class ConvaiRoomControlProperties
    {
        /// <summary>Component only: the key binding is scene input, not a shareable room setting.</summary>
        public SerializedProperty PushToTalkKey;

        public SerializedProperty VideoTrackName;
        public SerializedProperty ServerEndpoint;

        public SerializedProperty TurnTakingOptions;

        public SerializedProperty UserVadSettings;

        public SerializedProperty VisionContextMode;
        public SerializedProperty VisionInputSettings;
        public SerializedProperty VisionRespondModes;

        public SerializedProperty RoomRejoinTtlSeconds;
        public SerializedProperty ResumePolicy;
        public SerializedProperty MaxReconnectAttempts;
        public SerializedProperty SpawnAgentOnRejoin;
        public SerializedProperty StartWaitTimeoutMs;
        public SerializedProperty AutoMicStartDelaySeconds;

        /// <summary>
        ///     Decides whether Auto vision is on. Read-only here: the connection type itself belongs to the
        ///     host's Connection section, so it is never edited twice.
        /// </summary>
        public SerializedProperty ConnectionTypeForDisplay;
    }

    /// <summary>
    ///     Draws the room's settings as feature cards — Conversation, Listening, Interruptions, Vision,
    ///     Reconnect — for the Room Manager component and the Room Manager Profile asset, so the two
    ///     inspectors cannot drift into two layouts of the same settings.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why cards, not one "Advanced Room Control" section.</b> That section held thirty-odd
    ///         controls in five groups under three heading styles, so "Turn Detection Values" and
    ///         "Barge In" read as siblings of "Turn Taking" rather than as parts of it, and a reader
    ///         looking for one setting had to open and scan all of them. A collapsed card per feature
    ///         says what it holds and, through its summary, what state it is in.
    ///     </para>
    ///     <para>
    ///         <b>One default-versus-custom shape.</b> Turn detection used a dropdown and voice
    ///         detection a "Use Server Default" tick box, and both left their value fields on screen,
    ///         greyed out. Both are now the same two segments; the default side says in one sentence
    ///         what the default does, and the custom side shows the fields. Nothing is ever shown
    ///         disabled for being unchosen.
    ///     </para>
    ///     <para>
    ///         <b>Said once.</b> How the player talks and the push-to-talk rules live only in
    ///         Conversation; echo cancellation only in Listening. Settings that depend on another
    ///         appear only when it is on (fade duration, wait timeout, everything under Vision).
    ///     </para>
    /// </remarks>
    internal static class ConvaiRoomControlInspectorView
    {
        #region Cards

        internal static readonly GUIContent ConversationSection = new("Conversation");
        internal static readonly GUIContent ListeningSection = new("Listening");
        internal static readonly GUIContent InterruptionsSection = new("Interruptions");
        internal static readonly GUIContent VisionSection = new("Vision");
        internal static readonly GUIContent ReconnectSection = new("Reconnect");

        internal const string ConversationSectionId = "Conversation";
        internal const string ListeningSectionId = "Listening";
        internal const string InterruptionsSectionId = "Interruptions";
        internal const string VisionSectionId = "Vision";
        internal const string ReconnectSectionId = "Reconnect";

        #endregion

        #region Serialized names

        private const string ModeField = "<Mode>k__BackingField";
        private const string TurnDetectionField = "<TurnDetection>k__BackingField";
        private const string CustomTurnDetectionField = "<CustomTurnDetection>k__BackingField";
        private const string InitialServerSttField = "<InitialServerStt>k__BackingField";
        private const string LocalAudioPolicyField = "<LocalAudioPolicy>k__BackingField";
        private const string PushToTalkPolicyField = "<PushToTalkPolicy>k__BackingField";
        private const string BargeInField = "<BargeIn>k__BackingField";

        private const string StopSecsField = "<StopSecs>k__BackingField";
        private const string PreSpeechMsField = "<PreSpeechMs>k__BackingField";
        private const string MaxDurationSecsField = "<MaxDurationSecs>k__BackingField";

        private const string StartMutedField = "<StartMutedInPushToTalk>k__BackingField";
        private const string EchoCancellationField = "<EnableAcousticEchoCancellation>k__BackingField";
        private const string MicStartupField = "<PushToTalkStartupMode>k__BackingField";

        private const string InterruptOnPressField = "<InterruptBotOnPress>k__BackingField";
        private const string WaitForReplyField = "<RequireTurnCompletionBeforeNextPress>k__BackingField";
        private const string WaitTimeoutField = "<TurnCompletionTimeoutMs>k__BackingField";
        private const string ServerSttToggleField = "<EnableServerSttToggle>k__BackingField";
        private const string ReleaseModeField = "<ReleaseMode>k__BackingField";
        private const string ReleaseTailField = "<ReleaseTailMs>k__BackingField";
        private const string SpeechStoppedFallbackField = "<AllowSpeechStoppedFallbackAfterSpeechStart>k__BackingField";

        private const string SmoothInterruptionField = "<SmoothInterruption>k__BackingField";
        private const string FadeOutSecondsField = "<FadeOutSeconds>k__BackingField";
        private const string ClientDetectionField = "<ClientDetection>k__BackingField";

        private const string UseServerDefaultField = "<UseServerDefault>k__BackingField";
        private const string ConfidenceField = "<Confidence>k__BackingField";
        private const string StartSecsField = "<StartSecs>k__BackingField";
        private const string MinVolumeField = "<MinVolume>k__BackingField";

        private const string SampleIntervalField = "_sampleIntervalSeconds";
        private const string FramesPerTurnField = "_framesPerTurn";
        private const string BufferFramesField = "_bufferFrames";
        private const string SamplingWindowsField = "_samplingWindows";
        private const string StalenessField = "_stalenessSeconds";
        private const string MaxResolutionField = "_maxResolution";
        private const string ReplacePreviousField = "_replacePreviousVisionContext";
        private const string WindowCountField = "_count";
        private const string WindowIntervalField = "_intervalMs";

        private const string RespondVisionField = "_vision";
        private const string RespondContextUpdateField = "_contextUpdate";
        private const string RespondTriggerField = "_trigger";
        private const string RespondSceneMetadataField = "_sceneMetadata";

        #endregion

        #region Copy

        private static readonly string DefaultTurnDetectionText =
            $"The player's turn ends after {SmartTurnSettings.DefaultStopSecs:0.###} s of silence and never " +
            $"runs longer than {SmartTurnSettings.DefaultMaxDurationSecs:0.###} s.";

        private const string TurnDetectionOffText =
            "Nothing ends the turn automatically. Your code has to end it.";

        private const string PushToTalkEndOfTurnText =
            "In Push To Talk, letting go of the key ends the turn.";

        private const string ServerVoiceDetectionText = "The server decides what counts as speech.";

        private const string SttAutomaticHandsFreeText = "On from the start of the session.";
        private const string SttAutomaticPushToTalkText = "Off until the player holds the key.";
        private const string SttOnText = "On from the start of the session, whether or not a key is held.";
        private const string SttOffText = "Off at the start. Your code has to switch it on before the player is heard.";

        private const string ServerDetectionText = "The character stops when the server hears the player.";
        private const string DeviceDetectionText = "The character reacts as soon as this device hears the player.";

        private const string VisionAutoOnText = "On, because Connection Type is Video.";

        private const string VisionAutoOffText =
            "Off, because Connection Type is Audio. Set it to Video, or choose On.";

        private const string VisionOnText = "On. The room connects with video even if Connection Type says Audio.";
        private const string VisionOffText = "Off. The character is not sent camera frames.";

        private const string FrameSpacingEmptyText =
            "Off: frames are spaced evenly at Frame Interval. Add windows to mix spacings — say 3 frames " +
            "200 ms apart for the last moment and 2 frames 2000 ms apart for context.";

        private const string FrameSpacingRulesText =
            "Windows fill in order, so put the most recent first. A window with 0 frames or 0 ms is skipped.";

        private const string RespondModesLegendText =
            "Silent takes it in without speaking. Auto lets the character decide. Must Respond always replies.";

        #endregion

        #region Summaries

        private const string SummaryDefaults = "Defaults";
        private const string SummaryCustom = "Custom";
        private const string SummaryDefaultsWithEcho = "Defaults · Echo Cancellation";
        private const string SummaryCustomWithEcho = "Custom · Echo Cancellation";
        private const string SummaryHandsFree = "Hands Free";
        private const string SummaryPushToTalk = "Push To Talk";
        private const string SummaryOn = "On";
        private const string SummaryOff = "Off";
        private const string SummaryFade = "Fade";
        private const string SummaryFadeOnDevice = "Fade · On-device";
        private const string SummaryInstant = "Instant";
        private const string SummaryInstantOnDevice = "Instant · On-device";

        #endregion

        /// <summary>The reconnect values a new room starts with, for the card's Defaults/Custom summary.</summary>
        private static readonly ReconnectPolicy ReconnectDefaults = ReconnectPolicy.Default;

        private const int NewWindowCount = 2;
        private const int NewWindowIntervalMs = 1000;

        #region Conversation

        internal static string ConversationSummary(ConvaiRoomControlProperties p) =>
            p?.TurnTakingOptions != null && IsPushToTalk(p.TurnTakingOptions) ? SummaryPushToTalk : SummaryHandsFree;

        /// <remarks>
        ///     The push-to-talk key is drawn outside the read-only scope: it lives on the component
        ///     even when a profile owns everything else.
        /// </remarks>
        internal static void DrawConversation(ConvaiRoomControlProperties p, bool readOnly)
        {
            SerializedProperty turnTaking = p?.TurnTakingOptions;
            if (turnTaking == null)
                return;

            using (new EditorGUI.DisabledScope(readOnly))
                Theme.EnumSegments(Content.HowThePlayerTalks, Content.HowThePlayerTalksOptions,
                    turnTaking.FindPropertyRelative(ModeField));

            if (!IsPushToTalk(turnTaking))
                return;

            if (p.PushToTalkKey != null)
                EditorGUILayout.PropertyField(p.PushToTalkKey, Content.PushToTalkKey);

            SerializedProperty policy = turnTaking.FindPropertyRelative(PushToTalkPolicyField);
            SerializedProperty audio = turnTaking.FindPropertyRelative(LocalAudioPolicyField);

            using (new EditorGUI.DisabledScope(readOnly))
            {
                Field(policy, InterruptOnPressField, Content.InterruptCharacterWhenPressed);

                SerializedProperty wait = Field(policy, WaitForReplyField, Content.WaitForCharacterToFinish);
                if (wait is { boolValue: true })
                    using (new EditorGUI.IndentLevelScope())
                        Field(policy, WaitTimeoutField, Content.FallbackWaitTimeMs);

                Field(audio, StartMutedField, Content.StartMutedInPushToTalk);
                Theme.EnumSegments(Content.PushToTalkStartupMode, Content.PushToTalkStartupOptions,
                    audio?.FindPropertyRelative(MicStartupField));

                if (!ConvaiInspectorDeveloperSettings.AreInspectorDeveloperSettingsVisible)
                    return;

                Theme.GroupCaption(Content.DeveloperGroup);
                Field(policy, ServerSttToggleField, Content.EnableServerSttToggle);
                Field(policy, ReleaseModeField, Content.ReleaseMode);
                Field(policy, ReleaseTailField, Content.ReleaseTailMs);
                Field(policy, SpeechStoppedFallbackField, Content.AllowSpeechStoppedFallbackAfterSpeechStart);
            }
        }

        #endregion

        #region Listening

        internal static string ListeningSummary(ConvaiRoomControlProperties p)
        {
            SerializedProperty turnTaking = p?.TurnTakingOptions;
            bool custom =
                (turnTaking?.FindPropertyRelative(TurnDetectionField) is { } detection &&
                 detection.enumValueIndex != (int)TurnDetectionMode.UseDefault) ||
                (turnTaking?.FindPropertyRelative(InitialServerSttField) is { } stt &&
                 stt.enumValueIndex != (int)ServerSttInitialState.UseDefault) ||
                (p?.UserVadSettings?.FindPropertyRelative(UseServerDefaultField) is { boolValue: false });

            bool echo = EchoCancellation(turnTaking) is { boolValue: true };
            return custom
                ? echo ? SummaryCustomWithEcho : SummaryCustom
                : echo ? SummaryDefaultsWithEcho : SummaryDefaults;
        }

        internal static void DrawListening(ConvaiRoomControlProperties p, bool readOnly)
        {
            if (p == null)
                return;

            SerializedProperty turnTaking = p.TurnTakingOptions;
            using (new EditorGUI.DisabledScope(readOnly))
            {
                if (turnTaking != null)
                    DrawEndOfTurn(turnTaking);

                if (p.UserVadSettings != null)
                    DrawVoiceDetection(p.UserVadSettings);

                if (turnTaking != null)
                    DrawSpeechRecognition(turnTaking);

                SerializedProperty echo = EchoCancellation(turnTaking);
                if (echo == null && p.AutoMicStartDelaySeconds == null)
                    return;

                Theme.GroupCaption(Content.MicrophoneGroup);
                if (echo != null)
                    EditorGUILayout.PropertyField(echo, Content.EnableAcousticEchoCancellation);
                if (p.AutoMicStartDelaySeconds != null)
                    EditorGUILayout.PropertyField(p.AutoMicStartDelaySeconds, Content.AutoMicStartDelaySeconds);
            }
        }

        /// <remarks>
        ///     "Off" is a developer option, and used to be enforced by quietly rewriting a saved Off
        ///     back to SDK Default whenever the inspector drew. A value the reader never chose, changed
        ///     by looking at it, is worse than a third segment — so a saved Off is simply shown.
        /// </remarks>
        private static void DrawEndOfTurn(SerializedProperty turnTaking)
        {
            Theme.GroupCaption(Content.EndOfTurnGroup);
            if (IsPushToTalk(turnTaking))
            {
                Caption(PushToTalkEndOfTurnText);
                return;
            }

            SerializedProperty detection = turnTaking.FindPropertyRelative(TurnDetectionField);
            if (detection == null)
                return;

            var current = (TurnDetectionMode)detection.enumValueIndex;
            bool showOff = ConvaiInspectorDeveloperSettings.AreInspectorDeveloperSettingsVisible ||
                         current == TurnDetectionMode.Disabled;

            int selected = showOff ? (int)current : current == TurnDetectionMode.Custom ? 1 : 0;
            int next = Theme.LabeledSegments(
                Content.TurnDetection,
                showOff ? Content.TurnDetectionDeveloperOptions : Content.TurnDetectionOptions,
                selected,
                detection);

            TurnDetectionMode chosen = showOff
                ? (TurnDetectionMode)next
                : next == 1 ? TurnDetectionMode.Custom : TurnDetectionMode.UseDefault;
            if (chosen != current)
                detection.enumValueIndex = (int)chosen;

            switch (chosen)
            {
                case TurnDetectionMode.Custom:
                    SerializedProperty values = turnTaking.FindPropertyRelative(CustomTurnDetectionField);
                    using (new EditorGUI.IndentLevelScope())
                    {
                        Field(values, StopSecsField, Content.StopSecs);
                        Field(values, MaxDurationSecsField, Content.MaxDurationSecs);
                        Field(values, PreSpeechMsField, Content.PreSpeechMs);
                    }

                    break;
                case TurnDetectionMode.Disabled:
                    Caption(TurnDetectionOffText);
                    break;
                default:
                    Caption(DefaultTurnDetectionText);
                    break;
            }
        }

        private static void DrawVoiceDetection(SerializedProperty vad)
        {
            Theme.GroupCaption(Content.VoiceDetectionGroup);

            SerializedProperty useDefault = vad.FindPropertyRelative(UseServerDefaultField);
            if (useDefault == null)
                return;

            int selected = useDefault.boolValue ? 0 : 1;
            int next = Theme.LabeledSegments(
                Content.VoiceDetectionThresholds, Content.VoiceDetectionOptions, selected, useDefault);
            if (next != selected)
                useDefault.boolValue = next == 0;

            if (useDefault.boolValue)
            {
                Caption(ServerVoiceDetectionText);
                return;
            }

            using (new EditorGUI.IndentLevelScope())
            {
                Field(vad, ConfidenceField, Content.VadConfidence);
                Field(vad, StartSecsField, Content.VadStartSecs);
                Field(vad, StopSecsField, Content.VadStopSecs);
                Field(vad, MinVolumeField, Content.VadMinVolume);
            }
        }

        private static void DrawSpeechRecognition(SerializedProperty turnTaking)
        {
            SerializedProperty stt = turnTaking.FindPropertyRelative(InitialServerSttField);
            if (stt == null)
                return;

            Theme.GroupCaption(Content.SpeechRecognitionGroup);
            Theme.EnumSegments(Content.InitialServerStt, Content.InitialServerSttOptions, stt);

            Caption((ServerSttInitialState)stt.enumValueIndex switch
            {
                ServerSttInitialState.Enabled => SttOnText,
                ServerSttInitialState.Disabled => SttOffText,
                _ => IsPushToTalk(turnTaking) ? SttAutomaticPushToTalkText : SttAutomaticHandsFreeText
            });
        }

        #endregion

        #region Interruptions

        internal static string InterruptionsSummary(ConvaiRoomControlProperties p)
        {
            SerializedProperty bargeIn = p?.TurnTakingOptions?.FindPropertyRelative(BargeInField);
            bool fade = bargeIn?.FindPropertyRelative(SmoothInterruptionField) is not { boolValue: false };
            bool onDevice = bargeIn?.FindPropertyRelative(ClientDetectionField) is { } detection &&
                            detection.enumValueIndex != (int)ClientBargeInMode.Disabled;

            return fade
                ? onDevice ? SummaryFadeOnDevice : SummaryFade
                : onDevice ? SummaryInstantOnDevice : SummaryInstant;
        }

        internal static void DrawInterruptions(ConvaiRoomControlProperties p, bool readOnly)
        {
            SerializedProperty bargeIn = p?.TurnTakingOptions?.FindPropertyRelative(BargeInField);
            if (bargeIn == null)
                return;

            SerializedProperty detection = bargeIn.FindPropertyRelative(ClientDetectionField);
            using (new EditorGUI.DisabledScope(readOnly))
            {
                SerializedProperty smooth = Field(bargeIn, SmoothInterruptionField, Content.SmoothInterruption);
                if (smooth is { boolValue: true })
                    using (new EditorGUI.IndentLevelScope())
                        Field(bargeIn, FadeOutSecondsField, Content.BargeInFadeOutSeconds);

                Theme.EnumSegments(Content.ClientBargeInDetection, Content.ClientBargeInOptions, detection);
            }

            bool onDevice = detection != null && detection.enumValueIndex != (int)ClientBargeInMode.Disabled;
            Caption(onDevice ? DeviceDetectionText : ServerDetectionText);

            SerializedProperty echo = EchoCancellation(p.TurnTakingOptions);
            if (!onDevice || echo == null || echo.boolValue)
                return;

            // The two settings live in different cards because they belong to different features,
            // but this one does not work without that one, so the dependency is said here — with the
            // fix — rather than left for the reader to find in a tooltip.
            const string title = "Only lowers the volume without Echo Cancellation";
            const string body =
                "On-device detection can't tell the player from the character's own voice without Echo " +
                "Cancellation, so it lowers the character's volume instead of interrupting. Echo " +
                "Cancellation is under Listening → Microphone.";

            if (readOnly)
                Theme.WarningBox(title, body);
            else
                Theme.WarningBox(title, body, "Turn On Echo Cancellation", () => echo.boolValue = true);
        }

        #endregion

        #region Vision

        internal static string VisionSummary(ConvaiRoomControlProperties p) => IsVisionOn(p) ? SummaryOn : SummaryOff;

        internal static void DrawVision(ConvaiRoomControlProperties p, bool readOnly)
        {
            if (p?.VisionContextMode == null && p?.VisionInputSettings == null && p?.VisionRespondModes == null)
                return;

            using (new EditorGUI.DisabledScope(readOnly))
                Theme.EnumSegments(Content.VisionContextMode, Content.VisionContextModeOptions, p.VisionContextMode);

            Caption(VisionStateText(p));
            if (!IsVisionOn(p))
                return;

            using (new EditorGUI.DisabledScope(readOnly))
            {
                if (p.VideoTrackName != null)
                    EditorGUILayout.PropertyField(p.VideoTrackName, Content.VideoTrackName);

                if (p.VisionInputSettings != null)
                    DrawFrames(p.VisionInputSettings, readOnly);

                if (p.VisionRespondModes != null)
                    DrawRespondModes(p.VisionRespondModes);
            }
        }

        private static void DrawFrames(SerializedProperty input, bool readOnly)
        {
            Theme.GroupCaption(Content.FramesGroup);
            Field(input, SampleIntervalField, Content.SampleIntervalSeconds);
            SerializedProperty framesPerTurn = Field(input, FramesPerTurnField, Content.FramesPerTurn);
            Field(input, BufferFramesField, Content.BufferFrames);
            Field(input, StalenessField, Content.StalenessSeconds);
            Field(input, MaxResolutionField, Content.MaxResolution);
            Field(input, ReplacePreviousField, Content.ReplacePreviousVisionContext);

            SerializedProperty windows = input.FindPropertyRelative(SamplingWindowsField);
            if (windows is { isArray: true })
                DrawFrameSpacing(windows, framesPerTurn, readOnly);
        }

        /// <summary>
        ///     Sampling windows as sentences — "2 frames, 1000 ms apart" — instead of Unity's list of
        ///     foldouts, with the rule the backend enforces checked where the counts are typed.
        /// </summary>
        /// <remarks>
        ///     The raw list showed "List is Empty" under a heading that did not say what a window was,
        ///     and its foldout arrow hung outside the card. Empty, this now says what the feature does
        ///     and gives an example; filled, each window is one line that reads as what it asks for.
        /// </remarks>
        private static void DrawFrameSpacing(SerializedProperty windows, SerializedProperty framesPerTurn, bool readOnly)
        {
            Theme.GroupCaption(Content.FrameSpacingGroup);
            Caption(windows.arraySize == 0 ? FrameSpacingEmptyText : FrameSpacingRulesText);

            // Removal waits until every row has drawn, so this pass lays out exactly the rows its
            // layout pass measured; the next pass measures one fewer.
            int requested = 0;
            int removeAt = -1;
            for (int i = 0; i < windows.arraySize; i++)
            {
                SerializedProperty window = windows.GetArrayElementAtIndex(i);
                SerializedProperty count = window.FindPropertyRelative(WindowCountField);
                SerializedProperty interval = window.FindPropertyRelative(WindowIntervalField);

                if (DrawWindowRow(count, interval))
                    removeAt = i;

                if (count.intValue > 0 && interval.intValue > 0)
                    requested += count.intValue;
            }

            if (removeAt >= 0)
                windows.DeleteArrayElementAtIndex(removeAt);

            if (!readOnly && Theme.GhostButtonRow(Content.AddSamplingWindow) == 0)
            {
                int index = windows.arraySize;
                windows.InsertArrayElementAtIndex(index);
                SerializedProperty added = windows.GetArrayElementAtIndex(index);
                added.FindPropertyRelative(WindowCountField).intValue = NewWindowCount;
                added.FindPropertyRelative(WindowIntervalField).intValue = NewWindowIntervalMs;
            }

            int budget = framesPerTurn != null ? Mathf.Clamp(framesPerTurn.intValue, 1, 20) : 20;
            if (requested > budget)
                Theme.WarningBox(
                    "Windows ask for more frames than a reply takes",
                    $"These windows add up to {requested} frames, but Frames Per Reply is {budget}, so the later " +
                    "windows are trimmed to fit. Raise Frames Per Reply or lower the counts.");
        }

        /// <summary>One window as "[n] frames, [ms] ms apart [✕]". Returns true when its remove button was pressed.</summary>
        private static bool DrawWindowRow(SerializedProperty count, SerializedProperty interval)
        {
            const float numberWidth = 56f;
            const float removeWidth = 18f;
            const float gap = 6f;

            Rect row = EditorGUI.IndentedRect(EditorGUILayout.GetControlRect());
            int indent = EditorGUI.indentLevel;
            EditorGUI.indentLevel = 0;

            float x = row.x;
            EditorGUI.PropertyField(new Rect(x, row.y, numberWidth, row.height), count, GUIContent.none);
            x += numberWidth + gap;

            float labelWidth = Theme.TextWidth(EditorStyles.label, Content.SamplingWindowCount) + gap;
            GUI.Label(new Rect(x, row.y, labelWidth, row.height), Content.SamplingWindowCount);
            x += labelWidth;

            EditorGUI.PropertyField(new Rect(x, row.y, numberWidth + 12f, row.height), interval, GUIContent.none);
            x += numberWidth + 12f + gap;

            GUI.Label(new Rect(x, row.y, Mathf.Max(0f, row.xMax - removeWidth - x), row.height),
                Content.SamplingWindowInterval);

            bool removed = GUI.enabled &&
                           Theme.IconButton(new Rect(row.xMax - removeWidth, row.y, removeWidth, row.height),
                               Content.RemoveSamplingWindow);

            EditorGUI.indentLevel = indent;
            return removed;
        }

        private static void DrawRespondModes(SerializedProperty modes)
        {
            Theme.GroupCaption(Content.RespondModesGroup);
            Caption(RespondModesLegendText);
            Field(modes, RespondVisionField, Content.RespondVision);
            Field(modes, RespondContextUpdateField, Content.RespondContextUpdate);
            Field(modes, RespondTriggerField, Content.RespondTrigger);
            Field(modes, RespondSceneMetadataField, Content.RespondSceneMetadata);
        }

        private static string VisionStateText(ConvaiRoomControlProperties p) =>
            VisionMode(p) switch
            {
                ConvaiVisionContextMode.Enabled => VisionOnText,
                ConvaiVisionContextMode.Disabled => VisionOffText,
                _ => IsVideoConnection(p) ? VisionAutoOnText : VisionAutoOffText
            };

        /// <summary>Whether frames will actually be sent, as the room resolves it on connect.</summary>
        private static bool IsVisionOn(ConvaiRoomControlProperties p) =>
            VisionMode(p) switch
            {
                ConvaiVisionContextMode.Enabled => true,
                ConvaiVisionContextMode.Disabled => false,
                _ => IsVideoConnection(p)
            };

        private static ConvaiVisionContextMode VisionMode(ConvaiRoomControlProperties p) =>
            p?.VisionContextMode != null
                ? (ConvaiVisionContextMode)p.VisionContextMode.enumValueIndex
                : ConvaiVisionContextMode.Auto;

        private static bool IsVideoConnection(ConvaiRoomControlProperties p) =>
            p?.ConnectionTypeForDisplay != null &&
            (ConvaiConnectionType)p.ConnectionTypeForDisplay.enumValueIndex == ConvaiConnectionType.Video;

        #endregion

        #region Reconnect

        internal static string ReconnectSummary(ConvaiRoomControlProperties p)
        {
            if (p == null)
                return SummaryDefaults;

            ReconnectPolicy d = ReconnectDefaults;
            bool custom =
                (p.MaxReconnectAttempts != null && p.MaxReconnectAttempts.intValue != d.MaxReconnectAttempts) ||
                (p.ResumePolicy != null && p.ResumePolicy.enumValueIndex != (int)d.ResumePolicy) ||
                (p.RoomRejoinTtlSeconds != null &&
                 !Mathf.Approximately(p.RoomRejoinTtlSeconds.floatValue, (float)d.RoomRejoinTtlSeconds)) ||
                (p.SpawnAgentOnRejoin != null && p.SpawnAgentOnRejoin.boolValue != d.SpawnAgentOnRejoin) ||
                (p.StartWaitTimeoutMs != null && p.StartWaitTimeoutMs.intValue != d.StartWaitTimeoutMs);

            return custom ? SummaryCustom : SummaryDefaults;
        }

        internal static void DrawReconnect(ConvaiRoomControlProperties p, bool readOnly)
        {
            if (p == null)
                return;

            using (new EditorGUI.DisabledScope(readOnly))
            {
                Optional(p.MaxReconnectAttempts, Content.MaxReconnectAttempts);
                Optional(p.ResumePolicy, Content.ResumePolicy);
                Optional(p.RoomRejoinTtlSeconds, Content.RoomRejoinTtlSeconds);
                Optional(p.SpawnAgentOnRejoin, Content.SpawnAgentOnRejoin);

                if (p.StartWaitTimeoutMs == null && p.ServerEndpoint == null)
                    return;

                Theme.GroupCaption(Content.StartupGroup);
                Optional(p.StartWaitTimeoutMs, Content.StartWaitTimeoutMs);

                // A route nobody should change is noise while it is the default. It appears for
                // developers, and for anyone whose room is not on it, with the reason why.
                if (p.ServerEndpoint != null &&
                    (ConvaiInspectorDeveloperSettings.AreInspectorDeveloperSettingsVisible ||
                     (ConvaiServerEndpoint)p.ServerEndpoint.enumValueIndex != ConvaiServerEndpoint.Connect))
                    ConvaiInspectorFieldUtility.DrawServerEndpointField(p.ServerEndpoint, Content.Server);
            }
        }

        #endregion

        #region Helpers

        private static bool IsPushToTalk(SerializedProperty turnTaking) =>
            turnTaking.FindPropertyRelative(ModeField) is { } mode &&
            (ConversationInputMode)mode.enumValueIndex == ConversationInputMode.PushToTalk;

        private static SerializedProperty EchoCancellation(SerializedProperty turnTaking) =>
            turnTaking?.FindPropertyRelative(LocalAudioPolicyField)?.FindPropertyRelative(EchoCancellationField);

        /// <summary>Draws a child of <paramref name="parent" /> and returns it, or null when it does not exist.</summary>
        private static SerializedProperty Field(SerializedProperty parent, string name, GUIContent label)
        {
            SerializedProperty property = parent?.FindPropertyRelative(name);
            if (property != null)
                EditorGUILayout.PropertyField(property, label);
            return property;
        }

        private static void Optional(SerializedProperty property, GUIContent label)
        {
            if (property != null)
                EditorGUILayout.PropertyField(property, label);
        }

        /// <summary>One muted sentence explaining the choice directly above it.</summary>
        private static void Caption(string text) => GUILayout.Label(text, Theme.CaptionWrapped);

        #endregion
    }
}
