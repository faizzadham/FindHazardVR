using UnityEngine;

namespace Convai.Editor
{
    /// <summary>
    ///     Labels and tooltips shared by the Room Manager and Room Manager Profile inspectors.
    /// </summary>
    /// <remarks>
    ///     Copy rules: a label names the setting in the player's terms and carries its unit; a tooltip
    ///     says what changes when the value changes and gives the default. Wire names
    ///     (<c>vad_params</c>, <c>run_llm</c>) stay out of labels and tooltips — they are what the
    ///     reader does not know, not what they are looking for.
    /// </remarks>
    internal static class ConvaiInspectorContent
    {
        #region Room source and connection

        internal static readonly GUIContent RoomSetupSource = new(
            "Room Setup Source",
            "Inline keeps the room settings on this component. Profile Asset reads them from a reusable Room " +
            "Manager Profile so several scenes can share one setup.");

        /// <summary>The two setup sources, shared by the Character and Room Manager inspectors.</summary>
        internal static readonly GUIContent[] SetupSourceOptions =
        {
            new("Inline", "Settings live on this component."),
            new("Profile Asset", "Settings come from a reusable profile asset.")
        };

        internal static readonly GUIContent RoomConfigAsset = new(
            "Room Manager Profile Asset",
            "The reusable asset these room settings come from.");

        internal static readonly GUIContent ConnectionType = new(
            "Connection Type",
            "Audio sends the player's voice. Video also sends camera frames, which the character needs to see " +
            "the scene.");

        internal static readonly GUIContent[] ConnectionTypeOptions =
        {
            new("Audio", "The player's voice only."),
            new("Video", "The player's voice and camera frames.")
        };

        internal static readonly GUIContent StartsConnected = new(
            "Starts Connected",
            "Connects automatically when Play starts. Turn it off to connect from your own code.");

        internal static readonly GUIContent ConnectOnStart = new(
            "Connect On Start",
            "Connects automatically when Play starts. Turn it off to connect from your own code.");

        internal static readonly GUIContent Debug = new(
            "Debug",
            "Extra room diagnostics and logging, for troubleshooting.");

        #endregion

        #region Conversation

        internal static readonly GUIContent HowThePlayerTalks = new(
            "How The Player Talks",
            "Hands Free keeps the microphone open and the character replies when the player stops talking. " +
            "Push To Talk opens it only while the player holds a key.");

        internal static readonly GUIContent[] HowThePlayerTalksOptions =
        {
            new("Hands Free", "The microphone stays open; the character replies when the player stops talking."),
            new("Push To Talk", "The player holds a key to talk; letting go ends their turn.")
        };

        internal static readonly GUIContent PushToTalkKey = new(
            "Push To Talk Key",
            "The key the player holds to speak.");

        internal static readonly GUIContent InterruptCharacterWhenPressed = new(
            "Interrupt On Press",
            "Pressing the key while the character is speaking cuts it off, so the player can talk straight " +
            "away. Default: on.");

        internal static readonly GUIContent WaitForCharacterToFinish = new(
            "Wait For Reply To Finish",
            "Ignores the key until the character has finished its current reply. Default: on.");

        internal static readonly GUIContent FallbackWaitTimeMs = new(
            "Wait Timeout (ms)",
            "If the reply never reports that it finished, the key unlocks after this long anyway. " +
            "Default: 5000.");

        internal static readonly GUIContent StartMutedInPushToTalk = new(
            "Start Muted",
            "The microphone starts muted and opens only while the key is held. Default: on.");

        internal static readonly GUIContent PushToTalkStartupMode = new(
            "Prepare Microphone",
            "Before First Press opens the microphone muted when the room connects, so the first press is " +
            "instant. On First Press waits for the key, which delays the first words slightly.");

        internal static readonly GUIContent[] PushToTalkStartupOptions =
        {
            new("Before First Press"), new("On First Press")
        };

        internal static readonly GUIContent DeveloperGroup = new(
            "Developer",
            "Shown because Convai inspector developer settings are on.");

        internal static readonly GUIContent EnableServerSttToggle = new(
            "Pause STT Between Presses",
            "Switches server speech recognition off between presses, which saves server cost. Default: on.");

        internal static readonly GUIContent ReleaseMode = new(
            "Release Mode",
            "Auto waits for the audio already sent to drain before ending the turn. Legacy uses the fixed " +
            "release tail below.");

        internal static readonly GUIContent ReleaseTailMs = new(
            "Release Tail (ms)",
            "How long each finalization window after release lasts. The microphone stays open for one window " +
            "while the final transcript arrives, then server speech recognition gets one more. 0 stops at once. " +
            "Default: 1000.");

        internal static readonly GUIContent AllowSpeechStoppedFallbackAfterSpeechStart = new(
            "Speech Stopped Unlocks Key",
            "Lets the character's speech-stopped event unlock the key once the reply has actually started.");

        #endregion

        #region Listening

        internal static readonly GUIContent EndOfTurnGroup = new(
            "End Of Turn",
            "How the SDK decides the player has finished, so the character can reply.");

        internal static readonly GUIContent TurnDetection = new(
            "Turn Detection",
            "SDK Default uses tuned values. Custom lets you set the silence and length limits yourself.");

        internal static readonly GUIContent[] TurnDetectionOptions =
        {
            new("SDK Default"), new("Custom")
        };

        internal static readonly GUIContent[] TurnDetectionDeveloperOptions =
        {
            new("SDK Default"), new("Off", "Nothing ends the turn automatically; your code must."), new("Custom")
        };

        internal static readonly GUIContent StopSecs = new(
            "Silence Before Reply (s)",
            "How long the player has to stay quiet before their turn ends and the character replies. " +
            "Shorter feels snappier but can cut off a pause mid-sentence. Default: 3.");

        internal static readonly GUIContent MaxDurationSecs = new(
            "Longest Turn (s)",
            "A single turn ends after this long even if the player is still talking. Default: 8.");

        internal static readonly GUIContent PreSpeechMs = new(
            "Pre-Speech Audio (ms)",
            "How much audio from just before the player starts speaking is kept, so the first syllable is " +
            "not clipped. Default: 0.");

        internal static readonly GUIContent VoiceDetectionGroup = new(
            "Voice Detection",
            "How loud and how long a sound has to be before the server treats it as the player speaking. " +
            "Separate from End Of Turn, which decides when the player has finished.");

        internal static readonly GUIContent VoiceDetectionThresholds = new(
            "Thresholds",
            "Server Default lets the server choose. Custom sends your own thresholds when the room connects.");

        internal static readonly GUIContent[] VoiceDetectionOptions =
        {
            new("Server Default"), new("Custom")
        };

        internal static readonly GUIContent VadConfidence = new(
            "Confidence",
            "How sure the detector must be that a sound is speech, from 0 to 1. Raise it if background noise " +
            "triggers the character. Server default: 0.7.");

        internal static readonly GUIContent VadStartSecs = new(
            "Speech Start (s)",
            "How long a sound must last before it counts as the player starting to speak. Server default: 0.2.");

        internal static readonly GUIContent VadStopSecs = new(
            "Speech Stop (s)",
            "How long the player must be quiet before they count as having stopped. In Hands Free, while " +
            "turn detection is on, the server may hold this at 0.2. Server default: 0.2.");

        internal static readonly GUIContent VadMinVolume = new(
            "Minimum Volume",
            "Sounds quieter than this, from 0 to 1, are never speech. Raise it in noisy rooms. " +
            "Server default: 0.6.");

        internal static readonly GUIContent SpeechRecognitionGroup = new(
            "Speech Recognition",
            "Whether the server is transcribing the player when the session starts.");

        internal static readonly GUIContent InitialServerStt = new(
            "At Session Start",
            "Automatic is on in Hands Free, and off until the key is held in Push To Talk. On and Off " +
            "override that; with Off, your code must switch it on before the player is heard.");

        internal static readonly GUIContent[] InitialServerSttOptions =
        {
            new("Automatic"), new("On"), new("Off")
        };

        internal static readonly GUIContent MicrophoneGroup = new(
            "Microphone",
            "How this device's microphone behaves.");

        /// <remarks>
        ///     Short on purpose: "Enable Acoustic Echo Cancellation" was cut off mid-word, and the group
        ///     caption already says this is a microphone setting.
        /// </remarks>
        internal static readonly GUIContent EnableAcousticEchoCancellation = new(
            "Echo Cancellation",
            "Stops the microphone from hearing the character's own voice through the speakers. Turn it on when " +
            "the game plays through device speakers, especially on Android and iOS. Default: off.");

        internal static readonly GUIContent AutoMicStartDelaySeconds = new(
            "Mic Start Delay (s)",
            "How long after connecting the microphone switches on. Default: 0.5.");

        #endregion

        #region Interruptions

        internal static readonly GUIContent SmoothInterruption = new(
            "Fade Out When Interrupted",
            "When the player cuts in, the character's voice fades out instead of stopping dead. Default: on.");

        internal static readonly GUIContent BargeInFadeOutSeconds = new(
            "Fade Duration (s)",
            "How long the fade takes. Default: 0.12.");

        internal static readonly GUIContent ClientBargeInDetection = new(
            "On-Device Detection",
            "Off waits for the server to notice the player is talking. Silero listens on this device too, so " +
            "the character reacts sooner. It needs Echo Cancellation to interrupt; without it, it only lowers " +
            "the character's volume.");

        internal static readonly GUIContent[] ClientBargeInOptions =
        {
            new("Off"), new("Silero")
        };

        #endregion

        #region Vision

        internal static readonly GUIContent VisionContextMode = new(
            "Dynamic Vision",
            "Sends camera frames so the character can see the scene. Auto follows Connection Type: on for " +
            "Video, off for Audio. On connects with video whatever Connection Type says. Off never sends frames.");

        internal static readonly GUIContent[] VisionContextModeOptions =
        {
            new("Auto"), new("On"), new("Off")
        };

        internal static readonly GUIContent VideoTrackName = new(
            "Video Track Name",
            "Name of the published video track. Leave it empty to use the publisher's default.");

        internal static readonly GUIContent FramesGroup = new(
            "Frames",
            "How the server samples camera frames and how many go with each reply.");

        internal static readonly GUIContent SampleIntervalSeconds = new(
            "Frame Interval (s)",
            "How often the server keeps a frame, from 0.1 to 60. Default: 1.");

        internal static readonly GUIContent FramesPerTurn = new(
            "Frames Per Reply",
            "How many recent frames go to the model with each reply, from 1 to 20. Every frame costs image " +
            "tokens. Default: 5.");

        internal static readonly GUIContent BufferFrames = new(
            "Frames Kept",
            "How many frames the server holds, up to 120. 0 keeps as many as Frames Per Reply, and a smaller " +
            "number is raised to it.");

        internal static readonly GUIContent StalenessSeconds = new(
            "Max Frame Age (s)",
            "Frames older than this are never sent, from 0.1 to 120. Default: 10.");

        internal static readonly GUIContent MaxResolution = new(
            "Max Image Size (px)",
            "Frames are scaled down so their longest side is at most this, from 64 to 2048. 0 lets the " +
            "server choose.");

        internal static readonly GUIContent ReplacePreviousVisionContext = new(
            "Replace Old Frames",
            "Each new set of frames replaces the last in the character's context instead of adding to it. " +
            "Keeps token use flat. Default: on.");

        internal static readonly GUIContent FrameSpacingGroup = new(
            "Custom Frame Spacing",
            "Optional. Mixes frame spacings instead of spacing every frame evenly.");

        internal static readonly GUIContent AddSamplingWindow = new(
            "Add Window", "Add a window: a number of frames and the spacing between them.");

        internal static readonly GUIContent RemoveSamplingWindow = new(
            UI.ConvaiEditorGlyphs.Affordance.Remove, "Remove this window.");

        internal static readonly GUIContent SamplingWindowCount = new(
            "frames,", "Frames to take from this window, from 1 to 20.");

        internal static readonly GUIContent SamplingWindowInterval = new(
            "ms apart", "Spacing between this window's frames in milliseconds, from 1 to 60000.");

        internal static readonly GUIContent RespondModesGroup = new(
            "When The Character Speaks Up",
            "Whether each kind of update can make the character say something unprompted.");

        internal static readonly GUIContent RespondVision = new(
            "New Frames", "When new camera frames arrive. Default: Silent.");

        internal static readonly GUIContent RespondContextUpdate = new(
            "Context Updates", "When your code sends a dynamic-context update. Default: Auto.");

        internal static readonly GUIContent RespondTrigger = new(
            "Vision Triggers", "When your code asks the character to look, without saying how to react. " +
                               "Default: Must Respond.");

        internal static readonly GUIContent RespondSceneMetadata = new(
            "Scene Metadata", "When scene metadata changes. Default: Silent.");

        #endregion

        #region Reconnect

        internal static readonly GUIContent MaxReconnectAttempts = new(
            "Retry Attempts",
            "How many times to try reconnecting after the connection drops before giving up. Default: 3.");

        internal static readonly GUIContent ResumePolicy = new(
            "Resume Conversation",
            "Resume If Possible continues the last conversation after a reconnect and starts fresh if it " +
            "can't. Always Resume fails instead of starting fresh. Always Fresh never resumes. " +
            "Default: Resume If Possible.");

        internal static readonly GUIContent RoomRejoinTtlSeconds = new(
            "Rejoin Window (s)",
            "For this long after a drop, reconnecting rejoins the same room. After that a new room is created. " +
            "Default: 60.");

        internal static readonly GUIContent SpawnAgentOnRejoin = new(
            "Restart Character On Rejoin",
            "Starts the character again when rejoining an existing room. Default: on.");

        internal static readonly GUIContent StartupGroup = new("Startup");

        internal static readonly GUIContent StartWaitTimeoutMs = new(
            "Startup Wait (ms)",
            "How long a connect call waits for the Room Manager to finish starting before it gives up. " +
            "Default: 5000.");

        internal static readonly GUIContent Server = new(
            "Server Route",
            "Which server route the room connects through. Normal projects use Connect.");

        #endregion

        #region Player

        internal static readonly GUIContent PlayerName = new(
            "Player Name",
            "Display name for the local player, used in transcripts and UI.");

        internal static readonly GUIContent PlayerId = new(
            "Player ID",
            "Optional local transcript identifier for the player. Leave it empty to reuse the player name. This is not the backend speaker ID.");

        internal static readonly GUIContent PlayerNameTagColor = new(
            "Name Tag Color",
            "Color used when the player appears in transcript or name-tag style UI.");

        #endregion
    }
}
