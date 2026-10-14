using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Domain.Models;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Modules;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Domain.EventSystem;
using Convai.Modules.Gaze.Core;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Core.Conversation;
using Convai.Modules.Gaze.Core.Diagnostics;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Reorientation;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Integrations;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using Convai.Shared.Compatibility;
using UnityEngine;

namespace Convai.Modules.Gaze.Components
{
    /// <summary>
    ///     The Convai Gaze system: a single, fully code-driven controller that decides what
    ///     the character looks at (targeting), how strongly each dialogue state commits to it
    ///     (policy), and articulates the look anatomically across torso, neck/head, eyes, and
    ///     eyelids (solvers) — including full-body turns toward off-axis targets. Behavior is
    ///     authored through one <see cref="ConvaiGazeProfile" /> asset.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The component is a thin composition root: targeting and policy run in the
    ///         embodiment <see cref="EmbodimentTickPhase.Cognition" /> tick, the solver chain
    ///         runs in <c>LateUpdate</c> (execution order <see cref="EmbodimentExecutionOrders.Gaze" />)
    ///         after the Animator has posed the skeleton and before the facial compositor
    ///         flushes. All behavior lives in the internal <c>Core</c> classes.
    ///     </para>
    ///     <para>
    ///         The current decision is published as a <see cref="GazeReading" /> through
    ///         <see cref="IGazeSource" /> on the character's embodiment context; every target
    ///         transition is traced (see <see cref="ConvaiGazeProfile.TraceVerbosity" />) and
    ///         mirrored to <see cref="TargetChanged" />. Call <see cref="CaptureSnapshot()" />
    ///         for a full live view.
    ///     </para>
    /// </remarks>
    [EmbodimentModule(ModuleIds.Gaze, "Gaze",
        Description = "Where the character looks — eye contact, glances, and attention.",
        Absence = "the eyes and head stay wherever the animation puts them, so the character never " +
                  "makes eye contact.",
        Order = 10)]
    [AddComponentMenu("Convai/Embodiment/Gaze")]
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(EmbodimentExecutionOrders.Gaze)]
    [RequireComponent(typeof(GazeAttentionRequests))]
    public sealed class ConvaiGazeController :
        ConvaiCharacterModule<ConvaiGazeProfile>,
        IGazeSource,
        IGazeGlanceHandler,
        IEmbodimentTickable,
        IFacialBlendshapeSource
    {
        [SerializeField]
        [Tooltip("When enabled, a Gaze Player Anchor provider is created at runtime if the character has no target provider.")]
        private bool autoCreatePlayerAnchor = true;

        [SerializeField]
        [Tooltip("Optional: the transform this character treats as 'the player'. Empty = the main " +
                 "camera (XR rigs included). Set for split-screen, multiplayer, or cutscene rigs.")]
        private Transform playerAnchorOverride;

        [SerializeField]
        [Tooltip("How eye contact is governed. Natural: the profile's per-state policy table. " +
                 "Speaking Focus: player focus only while the character is producing speech. " +
                 "Conversation Lock: full commitment to the player anchor in every conversational " +
                 "(non-Idle) state, ambient idle life preserved. Always Lock: full commitment in " +
                 "every state including Idle. Scripted GazeAt() preempts Social focus; Exact " +
                 "rejects it unless Allow Scripted Overrides is enabled.")]
        private GazeEyeContactMode eyeContactMode = GazeEyeContactMode.Natural;

        [SerializeField]
        [Tooltip("Whether this character turns to whoever else is speaking, the way people in a " +
                 "group look at the person talking. Anyone: the player and other characters. " +
                 "Player / Characters: only that side. Off: the character follows only its own " +
                 "conversation. Applies while the character is not in its own turn; distance, " +
                 "angle and line-of-sight limits live in the profile's Conversation Attention group.")]
        private GazeSpeakerAttention attendToSpeaker = GazeSpeakerAttention.Anyone;

        [SerializeField]
        [Tooltip("Social keeps subtle fixation life while focused. Exact suppresses intentional look-aways and fixation offsets.")]
        private GazeFocusFidelity focusFidelity = GazeFocusFidelity.Social;

        [SerializeField]
        [Tooltip("Where on the player the character aims. Auto picks the camera when the anchor is a camera and the object's own origin otherwise, which is what almost every scene wants.")]
        private GazeAnchorAimMode playerAnchorAimMode = GazeAnchorAimMode.Auto;

        [SerializeField]
        [Tooltip("Anchor-local aim point used when Player Anchor Aim Mode is Local Offset.")]
        private Vector3 playerAnchorAimOffset;

        [SerializeField]
        [Tooltip("How the character turns its body to look at something behind it. Stepping Turn " +
                 "plays the body's own turn animation, which reads as a person turning but needs " +
                 "those clips and takes as long as they take. Smooth Rotation turns the character " +
                 "directly, which is instant to set up, never fights an animation, and is what many " +
                 "first-person and stylised games use. Only affects turns the gaze system asks for.")]
        private GazeBodyTurnStyle bodyTurnStyle = GazeBodyTurnStyle.SteppingTurn;

        [SerializeField]
        [Tooltip("Allow explicit GazeAt requests to preempt an active Exact focus. Off is recommended for kiosk and presenter use cases.")]
        private bool allowScriptedOverridesDuringExactFocus;

        [SerializeField]
        [Tooltip("While an eye-contact lock is in force, absorb glance-tier scripted requests " +
                 "(GlanceAt, referential glances) so nothing briefly pulls gaze off the player " +
                 "anchor. Explicit GazeAt() preempts Social focus; Exact follows its Allow " +
                 "Scripted Overrides setting.")]
        private bool lockBlocksGlances = true;

        private readonly List<IGazeTargetProvider> _providers = new(4);
        private readonly List<IGazeTargetProvider> _providerScratch = new(8);
        private readonly List<IGazeTargetProvider> _runtimeProviders = new(2);
        private readonly List<GazeTargetCandidate> _candidates = new(4);
        private readonly List<GazeTargetCandidate> _focusCandidates = new(1);

        /// <summary>
        ///     Normalized speech energy above which the character counts as "producing speech"
        ///     — the gate that hard-suppresses listening backchannel nods so the character
        ///     never nods over its own words.
        /// </summary>
        private const float CharacterSpeakingEnergyThreshold = 0.1f;

        /// <summary>
        ///     Priority of a <see cref="GlanceAt(Transform, float)" /> request. Strictly below
        ///     the default <see cref="GazeOptions.Priority" /> (0) so any explicit
        ///     <see cref="GazeAt(Transform, GazeOptions)" /> outranks a glance, yet above the
        ///     internal curiosity-glance tier (-100) so a scripted glance still wins over
        ///     ambient curiosity.
        /// </summary>
        private const int GlancePriority = -5;

        /// <summary>Minimum engagement floor while a target-loss search substitutes for the lost player target, so the head/eye stages still commit to the search fixations.</summary>
        private const float SearchEngagementFloor = 0.6f;

        private readonly GazeTargetArbiter _arbiter = new();
        private readonly GazePolicyEngine _policy = new();
        private readonly DialogueStateDebounce _dialogueDebounce = new();
        private readonly GazeFocusScopeEvaluator _focusScope = new();
        private readonly GazeScriptedRequests _scripted = new();
        private readonly List<string> _absorbedScratch = new(2);
        private readonly GazeChainCalibration _chain = new();
        private readonly HeadTorsoSolver _headTorso = new();
        private readonly AmbientExplorationDirector _ambient = new();
        private readonly EyeSolver _eyes = new();
        private readonly BlinkDirector _blink = new();
        private readonly FixationMicroMotion _micro = new();
        private readonly FaceScanDirector _faceScan = new();
        private readonly EyeBlendshapeWriter _eyeWriter = new();
        private readonly ReorientationDirector _reorientation = new();

        /// <summary>Owns the gaze shift as one event: its clock, and its division across the ladder.</summary>
        private readonly GazeShiftDirector _shiftDirector = new();

        /// <summary>The eyes' short drop-and-lift as the character comes to rest after a walk.</summary>
        private readonly ArrivalSettleDirector _arrivalSettle = new();

        /// <summary>This frame's shift requirement, measured once and shared by every stage.</summary>
        private GazeShiftMeasurement _shiftMeasurement;

        /// <summary>
        ///     What was engaged last frame. Only used to recognise the travel-path hand-off, so
        ///     arriving somewhere reads as one continuous movement rather than two.
        /// </summary>
        private GazeTargetKind _previousTargetKind = GazeTargetKind.None;

        // Contributor-trace cache. Set once per expression tick, read by CaptureSnapshot.
        private Vector2 _lastHeadGoal;
        private Vector2 _lastHeadGestureOffset;
        private Vector2 _lastHeadAversionOffset;
        private Vector2 _lastEyeAversionOffset;
        private Vector2 _lastEyeMicroOffset;
        private Vector3 _lastDirectiveWorldPoint;
        private bool _hasLastDirectiveWorldPoint;
        private float _targetPointDeltaMeters;
        private Quaternion _lastAppliedHeadWorldRotation;
        private bool _hasLastAppliedHeadWorldRotation;
        private float _appliedHeadDeltaDegrees;

        private readonly AversionDirector _aversion = new();
        private readonly SearchDirector _search = new();
        private readonly BackchannelDirector _backchannel = new();
        private readonly InterruptionReactionDirector _interruptionReaction = new();
        private readonly TurnTakingDirector _turnTaking = new();
        private readonly HeadGestureArbiter _headGestureArbiter = new();
        private readonly EmotionGazeModulator _emotionModulator = new();
        private readonly PupilArousalModel _pupilArousal = new();
        private readonly BrowCueCoordinator _browCueCoordinator = new();
        private readonly ProxemicRegulator _proxemics = new();
        private readonly CuriosityGlanceDirector _curiosity = new();
        private readonly ConversationGazeDirector _conversation = new();

        /// <summary>
        ///     Who in the room this character cannot see. Refreshed on a throttle from the room's
        ///     own account of where everybody's head is, and read by both consumers of "who is
        ///     worth looking at": the conversation director and the character target provider.
        /// </summary>
        private readonly ConversationOcclusionSet _conversationOcclusion = new();

        /// <summary>
        ///     Display name of the character conversation attention last resolved as speaking, so
        ///     diagnostics can name it. Not the attention target itself — the arbiter owns that.
        /// </summary>
        private string _attendedSpeakerName = "-";

        /// <summary>
        ///     The character conversation gaze is looking at this tick (0 = none), handed to the
        ///     character provider so that character is published as the one to look at.
        /// </summary>
        private int _attentionCharacterKey;

        /// <summary>
        ///     The room has had speech in it recently enough to still be a conversation. Idle life
        ///     — curiosity and character glances — stands aside for as long as it is true, not
        ///     only while this character is attending somebody: a glance fired between two turns
        ///     pulls the head off the conversation exactly as the answer arrives.
        /// </summary>
        private bool _conversationLive;

        /// <summary>
        ///     Somebody the room can hear is speaking, but there is no point to look at for them.
        ///     Diagnostics only — the overlay is suppressed rather than aimed at nothing.
        /// </summary>
        private bool _conversationAnchorMissing;
        private readonly TravelGazeDirector _travel = new();
        private readonly GazeLodGovernor _lodGovernor = new();

        /// <summary>This tick's travel reading. <c>None</c> whenever nothing publishes travel.</summary>
        private TravelIntent _travelIntent = TravelIntent.None;

        /// <summary>Parent-local root position from the previous tick, for the provisioning probe.</summary>
        private Vector3 _lastLocalRootPosition;
        private bool _hasLastLocalRootPosition;
        private bool _travelIntentProvisioned;
        private bool _wasTraveling;

        private SkinnedMeshRenderer[] _renderers;
        private bool _lodSkipExpression;
        private DeterministicEmbodimentRandom _random;
        private DeterministicEmbodimentRandom _turnTakingRandom;
        private bool _useEyeBones;
        private bool _useLookShapes;
        private GazeTrace _trace;
        private GazeDirective _directive = GazeDirective.Disengaged;
        private PlayerAnchorTargetProvider _ownedPlayerAnchor;
        private CharacterGazeTargetProvider _characterGaze;
        private PlayerAttentionSensor _attentionSensor;
        private ConvaiCharacter _character;
        private PendingTurnTranscript _turnTranscript;
        private bool _finalTranscriptBlinkPending;

        /// <summary>
        ///     Delay (seconds) after the player's binary VAD falls (stops speaking) before the
        ///     blink-cluster cue opens — the boundary is felt a beat after the silence
        ///     starts, not on the raw edge itself.
        /// </summary>
        private const float PlayerPauseClusterDelaySeconds = 0.3f;

        // Blink clustering (Domain-event seam): cached delegate assigned once in the
        // constructor (never per OnEnable) so repeated enable/disable cycles never allocate a
        // fresh delegate — mirrors ConvaiBodyLanguageController's own Domain-event wiring.
        private readonly Action<PlayerSpeakingStateChanged> _handlePlayerSpeakingStateChanged;
        private readonly Action<PlayerTranscriptReceived> _handlePlayerTranscriptReceived;
        private readonly Action<LocalPlayerActivityChanged> _handleLocalPlayerActivityChanged;
        private IEventHub _subscribedEventHub;
        private SubscriptionToken _playerSpeakingToken;
        private SubscriptionToken _playerTranscriptToken;
        private SubscriptionToken _localPlayerActivityToken;

        /// <summary>
        ///     The open microphone's level gate currently hears the player. Local evidence: no
        ///     round trip, so it is what the room reacts to.
        /// </summary>
        private bool _playerMicrophoneActive;

        /// <summary>The player is holding the push-to-talk control. Local evidence, and a certainty rather than a guess.</summary>
        private bool _playerPushToTalkActive;

        /// <summary>Microphone level above the measured noise floor, for the room's salience. Zero from every other source.</summary>
        private float _playerLocalLevel;

        /// <summary>
        ///     This character's identity in <see cref="ConversationRoomModel" />, cached because it
        ///     is a stable function of the character root and is read every cognition tick.
        /// </summary>
        private int _conversationRoomKey;

        /// <summary>
        ///     The <see cref="_playerTypedAt" /> stamp already handed to the room. A typed message
        ///     is an edge, not a state: reporting it for as long as the local window is open would
        ///     restart the room's own window every frame and leave the player holding the floor
        ///     long after they stopped.
        /// </summary>
        private float _reportedTypedAt = float.NegativeInfinity;

        /// <summary>
        ///     Eye line used when reporting this character to the room before the rig binds, so a
        ///     participant is never reported standing on its own feet. Matches the character gaze
        ///     target's own fallback.
        /// </summary>
        private const float RoomEyeLineFallbackMeters = 1.6f;

        /// <summary>
        ///     Seconds between this character's line-of-sight measurements of the other people in
        ///     the room. The same throttle the player anchor has always used: ten times a second
        ///     is far quicker than anybody walks out from behind a wall, and it keeps a room of
        ///     eight to fifty-six rays a second in total.
        /// </summary>
        private const float ConversationLineOfSightIntervalSeconds = 0.1f;

        /// <summary>
        ///     <c>Time.time</c> the player last sent a typed message, or negative infinity. A
        ///     player who types takes a turn as surely as one who talks, and nothing about voice
        ///     detection knows it happened.
        /// </summary>
        private float _playerTypedAt = float.NegativeInfinity;

        /// <summary>Last dialogue state observed by the Speaking-exit edge check (own small cache — see class remarks).</summary>
        private DialogueState _lastBlinkClusterState = DialogueState.Idle;
        private bool _hasBlinkClusterState;

        /// <summary>
        ///     The dialogue state gaze is acting on this frame: the conversation-flow source read
        ///     exactly once per frame, through <see cref="DialogueStateDebounce" />.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Every consumer in this component reads this field rather than the source, for
        ///         two separate reasons. The debounce is the first: an utterance boundary reports
        ///         Speaking and Settling on alternating frames for a fifth of a second, and each
        ///         of those edges is a real instruction to gaze. The single read is the second,
        ///         and it would matter even without the flicker — cognition resolves the state
        ///         policy and expression drives turn-taking, aversion and backchannel, and if the
        ///         two stages sample the source separately they can act on different states
        ///         within one frame.
        ///     </para>
        ///     <para>
        ///         Written in the Cognition tick BEFORE the crowd-LOD gate, so a throttled
        ///         character still advances the confirmation window on wall-clock time and the
        ///         Expression tick never reads a value from an older frame than it thinks.
        ///     </para>
        /// </remarks>
        private DialogueState _dialogueState = DialogueState.Idle;

        /// <summary>
        ///     Raw (unsmoothed) player-speaking flag from <see cref="PlayerSpeakingStateChanged" />,
        ///     reused by the listener mouth-bias face scan (FaceScanDirector does its own
        ///     ~0.5s smoothing of this flag).
        /// </summary>
        private bool _playerSpeaking;

        private bool _rigHandlerRegistered;

        // Latch for ValidateRig's log-once contract across rebinds (see ValidateRig).
        private IStandardRigBinding _rigWarningBinding;
        private bool _rigWarningReported;
        private IHeadGestureChannel _registeredHeadGestureChannel;
        private readonly GazeDiagnosticsReporter _diagnostics = new();
        private bool _tickRegistered;
        private bool _runtimeInitialized;
        private bool _focusActive;
        private Vector3 _lastFocusPoint;
        private bool _hasLastFocusPoint;
        private bool _focusDegraded;
        private bool _ownedPlayerAnchorFocusOnly;

        // Look-where-you-act: a glance at the target while a targeted action step
        // executes. See ActionPerformanceGazeReactor remarks.
        private ActionPerformanceGazeReactor _actionPerformanceReactor;

        /// <summary>Raised for every gaze target transition, mirroring the trace log.</summary>
        public event Action<GazeTargetChange> TargetChanged;

        /// <inheritdoc />
        public GazeReading Current { get; private set; } = GazeReading.None;

        EmbodimentTickPhase IEmbodimentTickable.Phase => EmbodimentTickPhase.Cognition;

        Component IFacialBlendshapeSource.SourceComponent => this;

        string IFacialBlendshapeSource.SourceName => nameof(ConvaiGazeController);

        /// <inheritdoc />
        protected override string ProfileModuleId => ModuleIds.Gaze;

        /// <inheritdoc />
        protected override Func<ConvaiGazeProfile> DefaultProfileFactory => ConvaiGazeProfile.CreateDefault;

        /// <summary>Calibrated bone chain (internal seam for editor gizmos and tests).</summary>
        internal GazeChainCalibration Chain => _chain;

        /// <summary>
        ///     The profile this character is actually running on — the assigned one, or the
        ///     built-in defaults when none is assigned. Internal seam for the integrations that
        ///     live outside the component but answer to its tuning.
        /// </summary>
        internal ConvaiGazeProfile ActiveProfile => EffectiveProfile;

        internal GazeTrace Trace => _trace;

        internal GazeTargetStack ScriptedStack => _scripted.Stack;

        /// <summary>Whether the resolved eye backend drives eye bones (internal seam for the editor troubleshooter).</summary>
        internal bool EyeBackendUsesBones => _useEyeBones;

        /// <summary>Whether the resolved eye backend drives EyeLook* blendshapes (internal seam for the editor troubleshooter).</summary>
        internal bool EyeBackendUsesLookShapes => _useLookShapes;

        /// <summary>
        ///     Whether the head-gesture arbiter currently reports an external program active
        ///     (or still draining its post-completion refractory) — i.e. whether the backchannel
        ///     is being suppressed as this frame's no-double-nod mechanism (internal seam for
        ///     tests/diagnostics).
        /// </summary>
        internal bool HeadGestureExternalActive => _headGestureArbiter.ExternalActive;

        /// <remarks>
        ///     Domain-event handlers are cached here, once, rather than in <c>OnEnable</c>, so
        ///     repeated enable/disable cycles never allocate a fresh delegate.
        /// </remarks>
        public ConvaiGazeController()
        {
            _handlePlayerSpeakingStateChanged = OnPlayerSpeakingStateChanged;
            _handlePlayerTranscriptReceived = OnPlayerTranscriptReceived;
            _handleLocalPlayerActivityChanged = OnLocalPlayerActivityChanged;
        }

        /// <inheritdoc />
        protected override void OnProfileApplied(ConvaiGazeProfile newProfile)
        {
            ConvaiGazeProfile effective = EffectiveProfile;
            if (_trace != null && effective != null)
                _trace.Verbosity = effective.TraceVerbosity;
            ConfigureOwnedPlayerAnchor();
            // An aversion beat is composed downstream of the actuator, so the ladder's duration law
            // never sees it. Hand the law over instead, or the head glances away faster than it
            // would ever turn on purpose.
            if (effective != null)
                _aversion.SetHeadMovementLaw(effective.HeadTurnBaseSeconds, effective.HeadTurnSecondsPerDegree);

            _trace?.State($"Profile applied: '{(newProfile != null ? newProfile.name : "(runtime default)")}'.");
        }

        protected override void OnEnable()
        {
            base.OnEnable();
            if (!enabled) return;

            ProvideService<IGazeSource>(this);
            ProvideService<IGazeGlanceHandler>(this);
            Context.EnsureTickScheduler()?.Register(this);
            _tickRegistered = true;

            // Registers unconditionally, like the seams above; the
            // dispatcher's own Performance toggle decides whether it is ever notified.
            _actionPerformanceReactor ??= new ActionPerformanceGazeReactor(this);
            ContributeService<IActionPerformanceReactor>(_actionPerformanceReactor);

            if (!UnityEngine.Application.isPlaying) return;

            _random = DeterministicEmbodimentRandom.Create(this);
            _turnTakingRandom = DeterministicEmbodimentRandom.Create(this, 0x475A5455u);
            Context.RigBindingChanged += HandleRigBindingChanged;
            _rigHandlerRegistered = true;

            Context.AddServiceChangedHandler<IHeadGestureChannel>(HandleHeadGestureChannelChanged);
            RegisterHeadGestureConsumer(Context.HeadGestureChannel);

            EnsureConversationFlowSource();
            RefreshProviders();
            EnsurePlayerAnchorIfNeeded();
            ApplyPlayerAnchorOverride(clearWhenNull: false);
            ApplyPlayerAnchorAim();
            EnsureRuntimeInitialized();
            SubscribeToEventHub();
        }

        /// <summary>
        ///     Makes sure this character has something answering "what beat of the conversation is
        ///     this", because every row of the gaze state table is keyed off that answer.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Without a driver the module reads <see cref="DialogueState.Idle" /> forever: the
        ///         character never commits to the player while they speak, never breaks contact to
        ///         think, and never holds the gaze while it talks. Nothing errors — the table is
        ///         simply never consulted past its first row, which is the silent no-op this call
        ///         exists to prevent.
        ///     </para>
        ///     <para>
        ///         Provisioning is the shared on-demand path, so a controller the user added by hand
        ///         always wins and the one this creates announces itself in the console.
        ///     </para>
        /// </remarks>
        private void EnsureConversationFlowSource()
        {
            if (Context == null || Context.ConversationFlowSource != null) return;

            Context.MarkConversationFlowDriverDemanded();
            Context.TryEnsureConversationFlowSource();
        }

        /// <summary>
        ///     Three player-side signals: the VAD falling edge that clusters blinks, the typed
        ///     message that counts as a turn, and the local activity gate that tells the
        ///     conversation room the player has started talking without waiting for the service to
        ///     agree. A missing EventHub
        ///     (Context never populated with one — e.g. a bare test rig) leaves the subscription
        ///     unset, which degrades byte-identically to no clustering cue from this trigger —
        ///     the Speaking-exit and isFinal triggers are unaffected. Mirrors
        ///     <c>ConvaiBodyLanguageController.SubscribeToEventHub</c>.
        /// </summary>
        private void SubscribeToEventHub()
        {
            IEventHub hub = Context?.EventHub;
            if (hub == null) return;

            _playerSpeakingToken = hub.Subscribe<PlayerSpeakingStateChanged>(_handlePlayerSpeakingStateChanged);
            _playerTranscriptToken = hub.Subscribe<PlayerTranscriptReceived>(_handlePlayerTranscriptReceived);
            _localPlayerActivityToken =
                hub.Subscribe<LocalPlayerActivityChanged>(_handleLocalPlayerActivityChanged);
            _subscribedEventHub = hub;
        }

        private void UnsubscribeFromEventHub()
        {
            IEventHub hub = _subscribedEventHub;
            if (hub == null) return;

            hub.Unsubscribe(_playerSpeakingToken);
            _playerSpeakingToken = default;
            hub.Unsubscribe(_playerTranscriptToken);
            _playerTranscriptToken = default;
            hub.Unsubscribe(_localPlayerActivityToken);
            _localPlayerActivityToken = default;
            _subscribedEventHub = null;
        }

        /// <summary>
        ///     Blink clustering, trigger (c): a falling edge (player just stopped speaking)
        ///     schedules the cluster cue ~300 ms later — the boundary is felt a beat after the
        ///     silence starts, not on the raw edge.
        /// </summary>
        private void OnPlayerSpeakingStateChanged(PlayerSpeakingStateChanged evt)
        {
            // Listener mouth-bias face scan: cache the raw flag for FaceScanDirector's own
            // smoothing (read at the FaceScan tick call site in SolveEyes).
            _playerSpeaking = evt.IsSpeaking;

            if (evt.IsSpeaking) return;
            _blink.NotifyDelayedClusterCue(PlayerPauseClusterDelaySeconds);
            // A pause in the player's speech is where a listening nod belongs; consumed by the
            // backchannel tick and cleared there.
            _playerPauseCue = true;
        }

        /// <summary>
        ///     Set on the tick the player's voice fell (server verdict or local gate), read once
        ///     by the backchannel director as a nod opportunity.
        /// </summary>
        private bool _playerPauseCue;

        /// <summary>
        ///     A typed message is a turn the microphone never hears. Conversation attention reads
        ///     the resulting window so a room looks up when the player types, exactly as it does
        ///     when they talk.
        /// </summary>
        private void OnPlayerTranscriptReceived(PlayerTranscriptReceived evt)
        {
            if (evt.SourceKind != TranscriptSegmentSourceKind.PlayerTypedText) return;
            _playerTypedAt = Time.time;
        }

        /// <summary>
        ///     The player has started, or stopped, being heard on this machine.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is the earliest thing the room can know about the person at the keyboard.
        ///         <see cref="PlayerSpeakingStateChanged" /> is the service's verdict and arrives a
        ///         round trip later; a listener that waits for it turns its head after the player
        ///         has already said a word or two, which is exactly the "they noticed me late"
        ///         report this signal exists to answer. It is reported to
        ///         <see cref="ConversationRoomModel" /> as a hint alongside the server's verdict,
        ///         never in place of it — nothing here commits a turn.
        ///     </para>
        ///     <para>
        ///         The two sources are independent and may overlap (an open microphone under a held
        ///         push-to-talk key), so they are held as a set rather than as one flag.
        ///     </para>
        /// </remarks>
        private void OnLocalPlayerActivityChanged(LocalPlayerActivityChanged evt)
        {
            if (evt.Source == LocalPlayerActivitySource.PushToTalk)
                _playerPushToTalkActive = evt.IsActive;
            else
                _playerMicrophoneActive = evt.IsActive;

            // Only the level gate measures anything; a press reports zero, and so does every
            // falling edge. Keeping the last measured level while any source is up means the room
            // still has a salience number for a player who is holding the key.
            if (evt.IsActive && evt.Level > 0f) _playerLocalLevel = evt.Level;
            else if (!PlayerLocallyActive) _playerLocalLevel = 0f;

            // The level gate closing is the earliest sign of a pause in the player's speech —
            // a phrase boundary, where a listening nod belongs.
            if (!evt.IsActive && evt.Source == LocalPlayerActivitySource.Microphone)
                _playerPauseCue = true;
        }

        /// <summary>Whether local evidence — microphone gate or push-to-talk — currently sees the player.</summary>
        private bool PlayerLocallyActive => _playerMicrophoneActive || _playerPushToTalkActive;

        protected override void OnDisable()
        {
            UnsubscribeFromEventHub();
            if (_tickRegistered)
            {
                Context?.TickScheduler?.Unregister(this);
                _tickRegistered = false;
            }

            if (_rigHandlerRegistered && Context != null)
            {
                Context.RigBindingChanged -= HandleRigBindingChanged;
                _rigHandlerRegistered = false;
            }

            if (Context != null)
                Context.RemoveServiceChangedHandler<IHeadGestureChannel>(HandleHeadGestureChannelChanged);
            RegisterHeadGestureConsumer(null);
            RegisterTranscriptSource(null);
            _turnTranscript.Clear();
            _finalTranscriptBlinkPending = false;
            _hasBlinkClusterState = false;
            _lastBlinkClusterState = DialogueState.Idle;

            // Gaze source, glance handler and the action reactor were published through the base
            // class, which releases every token in base.OnDisable() below.
            _actionPerformanceReactor?.ReleaseHeldGaze();
            DestroyOwnedPlayerAnchor();
            _arbiter.Reset();
            _policy.Reset();
            // A rebind must not inherit a half-confirmed state from the binding that went down,
            // and the first tick of the new one adopts whatever the conversation is doing then.
            _dialogueDebounce.Reset();
            _dialogueState = DialogueState.Idle;
            _scripted.Reset();
            _chain.RestoreEyeRest();
            Context?.EnsureCompositor()?.ClearLayer(this, FacialBlendshapeLayers.Eyes);
            _chain.Clear();
            _headTorso.Reset();
            _ambient.Reset();
            _eyes.Reset();
            _faceScan.Reset();
            _eyeWriter.Clear();
            _reorientation.Reset();
            _shiftDirector.Reset();
            _arrivalSettle.Reset();
            _previousTargetKind = GazeTargetKind.None;
            _aversion.Reset();
            _search.Reset();
            _backchannel.Reset();
            _interruptionReaction.Reset();
            _turnTaking.Reset();
            _headGestureArbiter.Reset();
            _emotionModulator.Reset();
            _pupilArousal.Reset();
            _browCueCoordinator.Reset();
            _proxemics.Reset();
            _curiosity.Reset();
            _travel.Reset();
            _travelIntent = TravelIntent.None;
            _hasLastLocalRootPosition = false;
            _travelIntentProvisioned = false;
            _wasTraveling = false;
            _lodGovernor.Reset();
            _lodSkipExpression = false;
            _directive = GazeDirective.Disengaged;
            Current = GazeReading.None;
            _diagnostics.Reset();
            _runtimeInitialized = false;
            _rigWarningBinding = null;
            _rigWarningReported = false;
            _focusActive = false;
            _focusScope.Reset();
            _conversation.Reset();
            _conversationOcclusion.Clear();

            // Leave the room rather than reset it: the model is shared, and one character being
            // disabled is that character walking out, not the conversation ending.
            ConversationRoomModel.Shared.RemoveParticipant(_conversationRoomKey);
            _conversationRoomKey = 0;
            _playerMicrophoneActive = false;
            _playerPushToTalkActive = false;
            _playerLocalLevel = 0f;
            _reportedTypedAt = float.NegativeInfinity;

            base.OnDisable();
        }

        /// <summary>
        ///     Claims/releases the head-gesture channel slot as its registered instance changes
        ///     (including going null on disable, or when Body Language enables/disables at
        ///     runtime). Idempotent by construction: registering the same instance twice, or
        ///     unregistering when nothing is registered, are both safe no-ops on the channel
        ///     side (see <see cref="IHeadGestureChannel" />), so this never double-registers or
        ///     throws regardless of call order.
        /// </summary>
        private void RegisterHeadGestureConsumer(IHeadGestureChannel channel)
        {
            if (_registeredHeadGestureChannel == channel) return;

            _registeredHeadGestureChannel?.UnregisterConsumer(this);
            _registeredHeadGestureChannel = channel;
            _registeredHeadGestureChannel?.RegisterConsumer(this);
        }

        private void HandleHeadGestureChannelChanged(IHeadGestureChannel channel) =>
            RegisterHeadGestureConsumer(channel);

        /// <summary>
        ///     Subscribes to the character's transcript stream for the turn-taking floor-yield
        ///     cue, re-resolving idempotently exactly like <see cref="RegisterHeadGestureConsumer" />
        ///     — a rescan finding the same instance (or no character at all) is a safe no-op.
        /// </summary>
        private void RegisterTranscriptSource(ConvaiCharacter character)
        {
            if (_character == character) return;

            if (_character != null) _character.OnTranscriptReceived -= HandleTranscriptReceived;
            _turnTranscript.Clear();
            _finalTranscriptBlinkPending = false;
            _character = character;
            if (_character != null) _character.OnTranscriptReceived += HandleTranscriptReceived;
        }

        /// <summary>
        ///     Latches a final-transcript pulse for <see cref="TurnTakingDirector" /> to consume
        ///     on its next tick; interim (non-final) transcripts are ignored, matching
        ///     <see cref="Providers.GazeReferentialGlances" />'s own final-only handling.
        /// </summary>
        private void HandleTranscriptReceived(string text, bool isFinal)
        {
            if (!isFinal) return;
            _finalTranscriptBlinkPending = true;
            // Delivery may precede cognition or arrive while a state is still being confirmed.
            _turnTranscript.Latch(CountTranscriptWords(text));
        }

        internal static int CountTranscriptWords(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0;
            int count = 0;
            bool insideWord = false;
            for (int i = 0; i < text.Length; i++)
            {
                char current = text[i];
                bool apostropheInsideWord = (current == '\'' || current == '’') && insideWord &&
                                            i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]);
                bool wordCharacter = char.IsLetterOrDigit(current) || apostropheInsideWord;
                if (wordCharacter && !insideWord) count++;
                insideWord = wordCharacter;
            }
            return count;
        }

        /// <summary>
        ///     Fills <paramref name="results" /> with every optional gaze capability and whether
        ///     this character currently has it — the supported way to answer "what is this
        ///     character missing?" without reflecting over component types.
        /// </summary>
        /// <remarks>
        ///     Adding <see cref="ConvaiGazeController" /> already gives a character eyes, a head,
        ///     idle life, blinking, body turns and conversational rhythm. The capabilities reported
        ///     here are the further ones that each live behind their own small component; none is
        ///     created automatically. See <see cref="GazeCapabilities" /> for why.
        /// </remarks>
        public void CaptureCapabilities(List<GazeCapabilityInfo> results) =>
            GazeCapabilities.Evaluate(Context != null ? Context.CharacterRoot : transform.root, results);

        /// <summary>Rescans the character hierarchy for target providers.</summary>
        public void RefreshProviders()
        {
            _providers.Clear();
            Transform root = Context != null ? Context.CharacterRoot : transform.root;
            if (root == null) return;

            _providerScratch.Clear();
            root.GetComponentsInChildren(true, _providerScratch);
            for (int i = 0; i < _providerScratch.Count; i++)
                _providers.Add(_providerScratch[i]);
            _providerScratch.Clear();

            // The character-gaze provider is polled specially (it yields one candidate per
            // OTHER registered character, not a single self-candidate), so it is cached here
            // rather than added to the IGazeTargetProvider list.
            _characterGaze = root.GetComponentInChildren<CharacterGazeTargetProvider>(true);

            // The attention sensor is not a target provider — it feeds curiosity-glance
            // reciprocation (E8) and the live HUD, so it is cached the same way.
            _attentionSensor = root.GetComponentInChildren<PlayerAttentionSensor>(true);

            RefreshRendererCache(root);

            // The character's transcript stream feeds the turn-taking floor-yield cue —
            // only subscribed at runtime, mirroring the other runtime-only event handlers below.
            if (UnityEngine.Application.isPlaying)
                RegisterTranscriptSource(root.GetComponentInChildren<ConvaiCharacter>(true));
        }

        /// <summary>
        ///     Re-caches the character's skinned renderers for the crowd-LOD off-screen check.
        ///     Called from <see cref="RefreshProviders" /> and on every rig rebind, because a mesh
        ///     swap (outfit change, LOD mesh, addressable skin) destroys every cached renderer and
        ///     a stale cache would otherwise read as "off-screen" forever.
        /// </summary>
        private void RefreshRendererCache(Transform root)
        {
            if (root == null) return;
            _renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        }

        /// <summary>
        ///     Re-resolves the eye output backend (eye bones vs. <c>EyeLook*</c> blendshapes)
        ///     from the profile's <see cref="ConvaiGazeProfile.EyeActuationMode" />. Call this
        ///     after changing that mode at runtime.
        /// </summary>
        public void RefreshEyeBackend() => ResolveEyeBackend(EffectiveProfile);

        // ------------------------------------------------------------------ scripted gaze

        /// <summary>
        ///     Directs gaze at a (moving) transform. Scripted requests outrank all automatic
        ///     targets and work in any dialogue state when
        ///     <see cref="GazeOptions.Engagement" /> is set explicitly. Await
        ///     <see cref="GazeHandle.Settled" /> to gate follow-up work (e.g. a pick-up
        ///     action) on the character visibly looking first.
        /// </summary>
        public GazeHandle GazeAt(Transform target, GazeOptions options = default)
        {
            if (target == null) return null;
            return GazeAtInternal(target, target.position, hasTransform: true, target.name, options);
        }

        /// <summary>Directs gaze at a world-space point. See <see cref="GazeAt(Transform, GazeOptions)" />.</summary>
        public GazeHandle GazeAt(Vector3 worldPoint, GazeOptions options = default) =>
            GazeAtInternal(null, worldPoint, hasTransform: false, "point", options);

        /// <summary>
        ///     Glances at a (moving) transform briefly, then returns to whatever the policy
        ///     dictates — the one-line "look there for a moment". A glance is a committed but
        ///     low-priority scripted request: any explicit
        ///     <see cref="GazeAt(Transform, GazeOptions)" /> outranks it, it never turns the
        ///     body, and the policy target resumes automatically when the hold ends. While an
        ///     eye-contact lock is in force (<see cref="EyeContactMode" />) with
        ///     <see cref="LockBlocksGlances" /> on, the glance is absorbed: the returned handle
        ///     is already completed (unsettled) and gaze never leaves the player anchor.
        /// </summary>
        /// <param name="target">Transform to glance at (a <c>null</c> target is a no-op).</param>
        /// <param name="durationSeconds">Hold duration, clamped to at least 0.2 s so the eyes can visibly land it.</param>
        public GazeHandle GlanceAt(Transform target, float durationSeconds = 1.2f)
        {
            if (target == null) return null;
            if (TryAbsorbGlance(target.name, out GazeHandle absorbed)) return absorbed;
            return GazeAt(target, GlanceOptions(durationSeconds));
        }

        /// <summary>Glances at a world-space point briefly. See <see cref="GlanceAt(Transform, float)" />.</summary>
        public GazeHandle GlanceAt(Vector3 worldPoint, float durationSeconds = 1.2f)
        {
            if (TryAbsorbGlance("point", out GazeHandle absorbed)) return absorbed;
            return GazeAt(worldPoint, GlanceOptions(durationSeconds));
        }

        /// <summary>
        ///     <see cref="IGazeGlanceHandler" /> entry point: a cross-module glance
        ///     request (e.g. Body Animation, when the character starts pointing at something)
        ///     routes through the same <see cref="GlanceAt(Vector3, float)" /> path scripted
        ///     callers use.
        /// </summary>
        void IGazeGlanceHandler.RequestGlance(Vector3 worldPosition, float durationSeconds) =>
            GlanceAt(worldPosition, durationSeconds);

        /// <summary>
        ///     Absorbs a glance at the door while the eye-contact lock is in force and
        ///     <see cref="LockBlocksGlances" /> is on: the stack is never touched and the
        ///     caller receives an already-completed (unsettled) handle, so composed code that
        ///     awaits <see cref="GazeHandle.Completion" /> proceeds immediately.
        /// </summary>
        private bool TryAbsorbGlance(string name, out GazeHandle absorbed)
        {
            absorbed = null;
            if (!lockBlocksGlances) return false;

            // The confirmed state, so the door a glance is turned away at is the same lock the
            // Cognition tick evaluated this frame.
            if (!_focusActive && !IsLockActive(eyeContactMode, _dialogueState)) return false;

            absorbed = new GazeHandle(this, entryId: 0, name) { Outcome = GazeOutcome.HeldEyeContactInstead };
            absorbed.MarkCompleted();
            _trace?.State($"Glance '{name}' absorbed by the eye-contact lock.");
            return true;
        }

        private static GazeOptions GlanceOptions(float durationSeconds) => new()
        {
            Priority = GlancePriority,
            HoldSeconds = Mathf.Max(0.2f, durationSeconds),
            Engagement = 1f,          // glances are committed — the brevity is the modifier
            AllowBodyTurn = false     // a glance never turns the body
        };

        /// <summary>Releases every scripted gaze request.</summary>
        public void ReleaseAllScriptedGaze()
        {
            _scripted.ReleaseAll();
            _trace?.State("All scripted gaze requests released.");
        }

        internal void ReleaseGaze(GazeHandle handle)
        {
            if (handle == null) return;

            bool removed = _scripted.Release(handle);
            if (removed)
                _trace?.State($"Scripted gaze '{handle.TargetName}' released.");
        }

        private GazeHandle GazeAtInternal(
            Transform target,
            Vector3 point,
            bool hasTransform,
            string name,
            GazeOptions options)
        {
            if (_focusActive && focusFidelity == GazeFocusFidelity.Exact &&
                !allowScriptedOverridesDuringExactFocus)
            {
                var rejected = new GazeHandle(this, entryId: 0, name);
                rejected.MarkCompleted();
                _trace?.State($"GazeAt '{name}' rejected by Exact focus.");
                return rejected;
            }

            float deadline = options.HoldSeconds > 0f
                ? Time.time + options.HoldSeconds
                : float.PositiveInfinity;
            float engagementOverride = options.Engagement > 0f ? Mathf.Clamp01(options.Engagement) : -1f;

            GazeHandle handle = _scripted.Push(
                this, target, point, hasTransform, options.Priority,
                engagementOverride, options.AllowBodyTurn, deadline, name);

            _trace?.State(
                $"GazeAt '{name}' (priority {options.Priority}, hold " +
                $"{(options.HoldSeconds > 0f ? options.HoldSeconds.ToString("0.0") + "s" : "until released")}, " +
                $"engagement {(engagementOverride > 0f ? engagementOverride.ToString("0.00") : "policy")}, " +
                $"bodyTurn {options.AllowBodyTurn}).");
            return handle;
        }

        /// <summary>
        ///     Latches this tick's scripted winner and expires handles whose stack entry is
        ///     gone (hold elapsed, or the target transform died). Settlement itself is decided
        ///     later, against the solved contact error — see
        ///     <see cref="ProcessScriptedSettlement(float)" />. Internal seam so tests can
        ///     drive it without play mode.
        /// </summary>
        internal void ProcessScriptedHandles(in GazeTargetDecision decision)
        {
            _scripted.ProcessDecision(in decision);
        }

        private void ProcessScriptedSettlement()
        {
            float error = _eyes.ContactErrorDegrees;
            if (float.IsNaN(error) && _directive.HasEngagedTarget)
                error = ComputeHeadFacingError(
                    _chain.CurrentEyeRestForward,
                    _directive.WorldPoint - _chain.HeadPivotPosition);
            ProcessScriptedSettlement(error);
        }

        internal static float ComputeHeadFacingError(Vector3 forward, Vector3 toTarget)
        {
            if (forward.sqrMagnitude <= 1e-8f || toTarget.sqrMagnitude <= 1e-8f)
                return float.NaN;
            return Vector3.Angle(forward, toTarget);
        }

        internal void ProcessScriptedSettlement(float contactErrorDegrees)
        {
            _scripted.ProcessSettlement(contactErrorDegrees);
        }

        // ------------------------------------------------------------------ providers

        /// <summary>Registers a non-component provider (systems, netcode, tests).</summary>
        public void RegisterTargetProvider(IGazeTargetProvider provider)
        {
            if (provider == null || _runtimeProviders.Contains(provider)) return;
            _runtimeProviders.Add(provider);
        }

        /// <summary>Unregisters a provider added via <see cref="RegisterTargetProvider" />.</summary>
        public void UnregisterTargetProvider(IGazeTargetProvider provider) =>
            _runtimeProviders.Remove(provider);

        /// <summary>
        ///     Cognition tick: targeting and policy. Solvers run later in
        ///     <see cref="LateUpdate" /> against the freshly animated pose.
        /// </summary>
        void IEmbodimentTickable.EmbodimentTick(float deltaTime)
        {
            if (!UnityEngine.Application.isPlaying || Context == null) return;

            EnsureRuntimeInitialized();
            ConvaiGazeProfile profile = EffectiveProfile;
            if (profile == null) return;

            // The one read of the conversation's state per frame, and the one place it is
            // confirmed. Deliberately above the crowd-LOD gate below: the window is wall-clock,
            // not tick-count, and a throttled character must not adopt a state late merely
            // because its cognition runs at a third of the frame rate. See _dialogueState.
            DialogueState state = _dialogueState = _dialogueDebounce.Tick(
                Context.ConversationFlowSource?.Current.Primary ?? DialogueState.Idle, deltaTime);
            _turnTranscript.ObserveState(state, _dialogueDebounce.HasPendingState);
            bool characterSpeaking = _character != null && _character.IsSpeaking;
            ISpeechEnergyProvider speech = Context.SpeechEnergyProvider;
            characterSpeaking |= speech != null && speech.Current > CharacterSpeakingEnergyThreshold;

            // The room first, and before the LOD gate below: a character far enough away to have
            // its own cognition throttled is still somebody the rest of the room can hear, and a
            // participant that stops reporting drops out of the conversation entirely.
            ReportConversationRoom(characterSpeaking, speech);

            _focusActive = _focusScope.Evaluate(eyeContactMode, state, characterSpeaking, deltaTime);

            // E10 crowd LOD: focused characters remain full-rate so camera/HMD motion never
            // turns a product-level focus promise into visibly stepped tracking.
            // solver stage. Skipped cognition ticks accumulate their dt so the executed tick
            // advances springs/ramps by the full elapsed time.
            if (profile.EnableGazeLod && !_focusActive)
            {
                bool anyVisible = AnyRendererVisible();
                bool runCognition = _lodGovernor.TickCognition(
                    profile, ResolvePlayerDistance(), anyVisible, deltaTime,
                    out float lodDeltaTime, out bool skipExpression);
                _lodSkipExpression = skipExpression;
                if (!runCognition) return;
                deltaTime = lodDeltaTime;
            }
            else
            {
                _lodSkipExpression = false;
            }

            bool locked = _focusActive;
            GazeStatePolicy statePolicy = locked
                ? GazeStatePolicy.LockedToPlayer(state)
                : profile.GetStatePolicy(state);

            IEmotionStateFrameSource frameSource = Context.EmotionStateFrameSource;
            if (frameSource != null)
            {
                EmotionStateFrame frame = frameSource.CurrentFrame;
                _emotionModulator.Tick(profile, in frame);
            }
            else
            {
                EmotionReading emotion = Context.EmotionStateSource?.Current ?? EmotionReading.Neutral;
                _emotionModulator.Tick(profile, in emotion);
            }
            // The lock promises full commitment no matter what — an authored emotion
            // modifier must not silently scale it back down. Blink-rate modulation stays
            // (blinks are life, not contact), and aversion is already zero in the locked
            // policy so its modifier is inert.
            _policy.EngagementModifier = locked ? 1f : _emotionModulator.EngagementScale;
            _policy.AversionModifier = _emotionModulator.AversionScale;

            // Proxemic intimacy regulation: ticked unconditionally (even while locked) so the
            // smoothed closeness factor stays continuous and never jumps the instant a lock
            // releases — the lock bypass is enforced at each consumption point instead (aversion
            // floor below in LateUpdate, face-scan scale in SolveEyes, blink scale right here).
            bool hasPlayerDistance = TryResolvePlayerAnchor(out _);
            _proxemics.Tick(
                profile.EnableProxemicRegulation, hasPlayerDistance,
                hasPlayerDistance ? ResolvePlayerDistance() : 0f,
                profile.ProxemicCloseDistanceMeters, profile.ProxemicIntensity, deltaTime);

            _blink.RateScale = _emotionModulator.BlinkRateScale * (locked ? 1f : _proxemics.BlinkRateScale);

            // Conversation attention: following somebody else's turn. Applied to the resolved
            // policy before travel gets its say, so a walking character still has its body turn
            // taken away and its head participation scaled by the travel rules below.
            ApplySpeakerAttention(profile, state, locked, ref statePolicy, deltaTime);

            // Travel is read before anything competes for attention, so this tick's candidates and
            // policy both see the same answer to "are we going somewhere?".
            EnsureTravelIntentIfMoving(profile);
            _travelIntent = Context.TravelIntentSource?.Current ?? TravelIntent.None;
            bool traveling = profile.EnableTravelGaze && _travelIntent.IsTraveling;

            // The eyes' settle beat is driven off the raw travel reading, not off `traveling`:
            // it must still play for a character whose travel gaze is switched off, because
            // coming to rest is a thing bodies do, not a gaze feature.
            _arrivalSettle.Tick(
                _travelIntent.IsTraveling,
                profile.ArrivalSettleEyeDropDegrees,
                profile.ArrivalSettleSeconds,
                deltaTime);

            // Rising edge only: a step that started as "look at this" becomes "go there" the moment
            // the character sets off, and the held stare has to be handed over exactly once.
            if (traveling && !_wasTraveling)
                _actionPerformanceReactor?.OnTravelStarted();
            _wasTraveling = traveling;

            if (traveling && !locked)
            {
                // The movement system owns the character's facing while it walks. A gaze-driven body
                // turn on top of that is two systems writing yaw at once, so the reorientation
                // director is stood down for the duration rather than left to fight the path.
                statePolicy.AllowBodyTurn = false;
                statePolicy.HeadContribution *= profile.TravelHeadContributionScale;
            }

            GatherCandidates(profile, deltaTime);
            TickTravelCheckIn(profile, state, deltaTime);
            TickCuriosityGlance(profile, statePolicy, deltaTime);
            if (locked && lockBlocksGlances)
                SuppressGlanceTierRequests();
            GazeTargetStack.Entry scripted = _scripted.ResolveActive(Time.time);
            if (locked && focusFidelity == GazeFocusFidelity.Exact &&
                !allowScriptedOverridesDuringExactFocus)
            {
                RejectScriptedRequestsForExactFocus();
                scripted = null;
            }

            IReadOnlyList<GazeTargetCandidate> arbitrationCandidates = _candidates;
            if (ShouldUseFocusedPlayerCandidates(locked, scripted != null))
            {
                _focusCandidates.Clear();
                PlayerAnchorTargetProvider anchor = FindActivePlayerAnchorProvider();
                if (anchor == null && ShouldProvisionPlayerAnchor(
                        autoCreatePlayerAnchor, FindPlayerAnchorProvider() != null, _providers.Count,
                        _runtimeProviders.Count, focusActive: true))
                {
                    _ownedPlayerAnchorFocusOnly = _providers.Count > 0 || _runtimeProviders.Count > 0;
                    anchor = CreateOwnedPlayerAnchor("Focus mode provisioned a dedicated player anchor.");
                }
                if (anchor != null && anchor.TryGetFocusCandidate(out GazeTargetCandidate focusCandidate, NeedsCurrentTargetNames))
                {
                    _focusCandidates.Add(focusCandidate);
                    _lastFocusPoint = focusCandidate.WorldPoint;
                    _hasLastFocusPoint = true;
                    _focusDegraded = false;
                }
                else if (_hasLastFocusPoint)
                {
                    _focusCandidates.Add(new GazeTargetCandidate(
                        GazeTargetKind.Player, int.MaxValue, 1f, null, _lastFocusPoint,
                        "Last known player focus"));
                    _focusDegraded = true;
                }
                else
                {
                    _focusDegraded = true;
                }

                // The arbiter intentionally holds a lost target for Natural gaze. A focus
                // contract must never inherit that unrelated ownership when no player point
                // has ever been resolved, so clear its state before ticking the empty list.
                if (ShouldResetArbiterForMissingFocus(locked, _focusCandidates.Count, _hasLastFocusPoint))
                    _arbiter.Reset();

                _diagnostics.ReportFocusDegraded(_trace, _focusDegraded);
                arbitrationCandidates = _focusCandidates;
            }
            else if (!locked)
            {
                _focusDegraded = false;
            }

            GazeTargetDecision decision = _arbiter.Tick(
                arbitrationCandidates, scripted, statePolicy.AllowPlayerTarget, profile, deltaTime);

            _directive = _policy.Tick(in statePolicy, in decision, profile, deltaTime);

            if (locked)
                _search.Abort();
            else
                TickTargetLossSearch(profile, in decision, state, deltaTime);

            ProcessScriptedHandles(in decision);
            PublishReading(profile, in decision);
            TraceTargetTransitions(in decision);
            TracePlayerLineOfSight();
        }

        /// <summary>
        ///     Whether the eye-contact lock is in force for <paramref name="state" /> under
        ///     <paramref name="mode" />. Pure — tests pin the full mode × state matrix.
        /// </summary>
        /// <summary>
        ///     Whether a look of this kind may bring the chest into it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A glance never does: a look that turns the body is not a glance, it is turning to
        ///         face someone. Until this existed the only question asked was whether the rig HAS
        ///         a torso, so the chest joined every look on every rig, in every state, at any
        ///         commitment — and <c>AllowBodyTurn</c>, which reads like the control for exactly
        ///         this, only ever governed the feet.
        ///     </para>
        /// </remarks>
        internal static bool RecruitsTorso(GazeLookNature nature, bool rigHasTorso) =>
            rigHasTorso && nature != GazeLookNature.Glance;

        /// <summary>
        ///     How far from centre the eyes may be left resting for a look of this nature.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Attention and reflexes are held to the profile's comfort band, so the head
        ///         turns far enough that the character is not watching somebody out of the corner
        ///         of its eye.
        ///     </para>
        ///     <para>
        ///         A glance is eye-led and brief, so it may rest the eyes further out — halfway
        ///         from the comfort band to the eyes' soft limit, not at the limit: a glance held
        ///         for a second with the eyes at the end of their travel is the same sideways
        ///         stare with a different name. The director draws a held glance back to the
        ///         comfort band.
        ///     </para>
        ///     <para>
        ///         <b>Unless the body is not coming.</b> That wider band is the eyes running ahead
        ///         of a movement that will finish: the head follows, the chest follows it, and by
        ///         the time the glance settles the eyes are near centre again. A glance the policy
        ///         has already excused the body from has no such ending — whatever the eyes are
        ///         handed at the start they hold for the whole beat. Measured in a room of three,
        ///         that was thirty-one degrees for two seconds at somebody sixty degrees round,
        ///         with the head barely moving: the sideways stare the budget exists to prevent,
        ///         arriving through the exception made for glances. So a glance that cannot
        ///         recruit the body is held to the same comfort band as a look.
        ///     </para>
        ///     <para>See <see cref="GazeLadderCapacity.EyeRestDegrees" />.</para>
        /// </remarks>
        /// <param name="nature">What kind of look this is.</param>
        /// <param name="allowBodyTurn">Whether this look may bring the body round behind the eyes.</param>
        /// <param name="profile">The character's comfort bands.</param>
        internal static float EyeRestBudgetFor(
            GazeLookNature nature, bool allowBodyTurn, ConvaiGazeProfile profile) =>
            nature == GazeLookNature.Glance && allowBodyTurn
                ? EyeReachDegrees(profile)
                : profile.EyeComfortDegrees;

        /// <summary>
        ///     How far the eyes go on their own: the endpoint of a glance, and how far ahead of
        ///     a head in flight the eyes may run during a committed look. Halfway from the
        ///     comfort band to the eyes' soft limit — past the band because eyes do lead, short
        ///     of the limit because eyes at the end of their travel read as a stare whatever the
        ///     head is doing.
        /// </summary>
        internal static float EyeReachDegrees(ConvaiGazeProfile profile)
        {
            float comfort = profile.EyeComfortDegrees;
            float softLimit = profile.EyeMaxYawDegrees * profile.EyeSoftLimitFraction;
            return Mathf.Max(comfort, Mathf.Lerp(comfort, softLimit, 0.5f));
        }

        internal static bool IsLockActive(GazeEyeContactMode mode, DialogueState state) => mode switch
        {
            GazeEyeContactMode.AlwaysLock => true,
            GazeEyeContactMode.ConversationLock => state != DialogueState.Idle,
            GazeEyeContactMode.SpeakingFocus => state == DialogueState.Speaking,
            _ => false
        };

        /// <summary>
        ///     Drops every glance-tier scripted entry (priority below the explicit
        ///     <see cref="GazeAt(Transform, GazeOptions)" /> default of 0 — glances, curiosity,
        ///     character glances) while the eye-contact lock is in force, completing their
        ///     handles unsettled. Explicit requests are untouched: a direct <c>GazeAt()</c> is
        ///     deliberate developer intent and stays sovereign over the lock. This is the
        ///     runtime-flip complement of the door check in <see cref="GlanceAt(Transform, float)" /> —
        ///     it purges requests that were already held when the lock engaged.
        /// </summary>
        private void SuppressGlanceTierRequests()
        {
            if (!_scripted.SuppressGlanceTier(_absorbedScratch)) return;

            for (int i = 0; i < _absorbedScratch.Count; i++)
                _trace?.State($"Glance '{_absorbedScratch[i]}' absorbed by the eye-contact lock.");
        }

        /// <summary>
        ///     Edge-triggered occlusion trace: explains WHY the player target dropped when the
        ///     line-of-sight check is on (the arbiter's own transition trace only reports the
        ///     loss). Never logs per-tick — only on the occluded/visible transitions.
        /// </summary>
        private void TracePlayerLineOfSight()
        {
            PlayerAnchorTargetProvider anchor = FindActivePlayerAnchorProvider();
            _diagnostics.ReportPlayerLineOfSight(_trace, anchor != null && anchor.LineOfSightOccluded);
        }

        /// <remarks>
        ///     Solver chain entry point. Runs after the Animator/PlayableGraph has posed the
        ///     skeleton this frame (execution order <see cref="EmbodimentExecutionOrders.Gaze" />)
        ///     so bone writes survive into rendering, and before the facial compositor flush
        ///     (order 20000) so eyelid/blink weights land in the same frame.
        /// </remarks>
        private void LateUpdate()
        {
            if (!UnityEngine.Application.isPlaying || Context == null) return;
            if (!_runtimeInitialized) return;

            ConvaiGazeProfile profile = EffectiveProfile;
            if (profile == null || !_chain.IsBound || !_chain.HasHeadChain) return;

            // E10: while off-screen, skip the whole solver stage (no solves, no bone/blendshape
            // writes). With an Animator present the pose is overwritten anyway; without one the
            // last write persists off-screen, which is invisible by definition.
            if (_lodSkipExpression) return;

            float deltaTime = Time.deltaTime;
            ResampleLiveTargetPoint();
            bool ambientActive = !_directive.HasEngagedTarget && profile.EnableAmbientExploration;
            _ambient.Tick(profile, deltaTime, ambientActive, ref _random);

            // Is there still an idle fixation to hand over? The boolean above flips a whole frame
            // before the ladder has any share to give — the head's onset has not elapsed — so on
            // its own it drops the fixation and the head starts the wrong way before turning out.
            // While the look is not fully taken up (acquiring, or being released) the head goes
            // on holding the fixation until it joins the look. Only the head stage reads this:
            // the eyes are ballistic by design and must keep jumping.
            //
            // HasResumableFixation is the second half of the question, and it is not optional:
            // the director clears its angles to zero once the resume window has expired, and a
            // cleared zero is not a fixation to return to — it is an instruction to face front.
            // Handing the head back to one mid-conversation reads as the character briefly
            // looking away from you and snapping back.
            bool ambientHandover = profile.EnableAmbientExploration && !ambientActive &&
                                   _ambient.HasResumableFixation &&
                                   _directive.TargetCommitment < 0.9999f;

            // This frame's confirmed state, as the Cognition tick resolved it — not a second read
            // of the source. Turn-taking, aversion and the backchannel must be acting on the same
            // beat the state policy above them was built from.
            DialogueState dialogueState = _dialogueState;
            ISpeechEnergyProvider speech = Context.SpeechEnergyProvider;
            bool characterSpeaking = speech != null && speech.Current > CharacterSpeakingEnergyThreshold;
            bool hasSpeechActivitySignal = _character != null;
            bool speechActive = hasSpeechActivitySignal && _character.IsSpeaking;
            characterSpeaking |= speechActive;

            // Blink clustering, trigger (a): the Speaking-exit edge, from this director's own
            // small last-state cache (not TurnTakingDirector's — that one is a separate,
            // guaranteed forced blink for the floor-yield beat, not a probability spike).
            if (_hasBlinkClusterState && _lastBlinkClusterState == DialogueState.Speaking && dialogueState != DialogueState.Speaking)
                _blink.NotifyClusterCue();
            _lastBlinkClusterState = dialogueState;
            _hasBlinkClusterState = true;

            // Turn-taking gaze choreography: planning break / floor-yield bookkeeping,
            // ticked before the aversion director so this tick's break decision can drive it
            // (see TurnTakingDirector remarks). The lock check reuses the same predicate the
            // Cognition tick used to build this frame's statePolicy.
            bool eyeContactLocked = _focusActive;
            bool exactFocus = eyeContactLocked && focusFidelity == GazeFocusFidelity.Exact;
            bool finalTranscriptForTurn = _turnTranscript.TryConsume(dialogueState, out int transcriptWordCount);
            _turnTaking.Tick(
                dialogueState, profile, eyeContactLocked, finalTranscriptForTurn,
                transcriptWordCount, hasSpeechActivitySignal, speechActive,
                speech != null ? speech.Current : 0f, deltaTime, ref _turnTakingRandom);
            if (eyeContactLocked)
                _turnTaking.CancelPlanningBreak();

            // Blink clustering, trigger (b): reuses the same final-transcript pulse
            // TurnTakingDirector just consumed above — no second transcript subscription.
            if (_finalTranscriptBlinkPending)
                _blink.NotifyClusterCue();
            _finalTranscriptBlinkPending = false;
            if (_turnTaking.PlanningBreakStarted && !exactFocus)
                _aversion.ForceBeat(
                    _turnTaking.StartedAversionMode,
                    _turnTaking.PlanningBreakDurationSeconds,
                    _turnTaking.StartedAversionStrength,
                    ref _turnTakingRandom);

            // While a turn-taking break is active it drives the aversion director with its
            // authored kind (opening cognitive vs. mid-turn natural); otherwise TurnTakingDirector
            // suppresses ordinary Speaking aversion so there is exactly one cadence owner.
            GazeAversionMode aversionMode = _directive.AversionMode;
            float aversionStrength = _directive.AversionStrength;
            // Emotional gaze signature: the active emotion's beat-direction bias, unless a
            // turn-taking is forcing a beat: its opening/mid-turn shape is intentional and
            // remains independent of the dominant emotion.
            GazeAversionBias aversionBias = _emotionModulator.AversionBias;
            if (_turnTaking.PlanningBreakActive)
            {
                aversionMode = _turnTaking.StartedAversionMode;
                aversionStrength = _turnTaking.StartedAversionStrength;
                aversionBias = GazeAversionBias.CognitiveDefault;
            }
            else
            {
                // Proxemic intimacy regulation: a close player raises the aversion floor
                // (max, never lowers an already-higher authored/state strength) so contact
                // softens instead of staring harder — bypassed entirely while the eye-contact
                // lock is in force (a kiosk keeps staring; LockedToPlayer's own strength is
                // already 0 regardless).
                bool turnTakingOwnsSpeaking = dialogueState == DialogueState.Speaking &&
                                              profile.EnableTurnTakingGaze;
                aversionStrength = ComposeNaturalSpeakingAversion(
                    aversionStrength,
                    _turnTaking.AversionSuppressionFactor,
                    _proxemics.AversionFloor,
                    applyProxemicFloor: !eyeContactLocked,
                    turnTakingOwnsSpeaking);
            }

            _aversion.Tick(aversionMode, aversionStrength, aversionBias, _directive.HasEngagedTarget, deltaTime, ref _random);

            // Floor-yield engagement pin: hold engagement at 1 for the pin's duration, exactly
            // like the target-loss search's engagement floor (Cognition tick) — mutating the
            // frame-local directive only affects this frame's expression output.
            if (_turnTaking.YieldEngagementPinActive)
            {
                _directive.Engagement = Mathf.Max(_directive.Engagement, 1f);
                // Pinned on both, or the pin would raise the eye/gate value while the ladder —
                // which divides the shift by the settled value — kept dividing the old one.
                _directive.SettledEngagement = Mathf.Max(_directive.SettledEngagement, 1f);
            }

            // Interruption startle beat: one-shot on the Speaking → Interrupted edge.
            // Ticked here (not Cognition) so its pulses are consumed the same frame by the
            // head-tilt and eye/blink stages below.
            _interruptionReaction.Tick(dialogueState, profile, deltaTime, ref _random);

            // Sense the external head-gesture channel BEFORE ticking the backchannel: the
            // arbiter's no-double-nod mechanism needs this tick's freshest external-active
            // state folded into the backchannel's own suppression input below, not last
            // frame's (see HeadGestureArbiter.SenseExternal remarks).
            _headGestureArbiter.SenseExternal(Context.HeadGestureChannel, _aversion.IsAverting, deltaTime);

            // Suppress (pause without re-arm) while the character produces speech — it must
            // never nod over its own words — while there is no engaged target: nodding at
            // nobody (player out of range or line-of-sight lost) reads as a glitch — or while
            // an external head-gesture program is active (or in its post-completion
            // refractory): this is the arbiter's no-double-nod mechanism, reusing the
            // director's own shipped cancel-fade path rather than any new cancellation logic.
            bool nodSuppressed = characterSpeaking || !_directive.HasEngagedTarget || _headGestureArbiter.ExternalActive;
            _backchannel.Tick(
                profile, dialogueState == DialogueState.Listening, nodSuppressed, deltaTime, ref _random,
                pauseCue: _playerPauseCue);
            _playerPauseCue = false;

            _headGestureArbiter.Compose(_backchannel.GestureOffset);

            // Contributor-trace: how far the directive's own world point moved this tick,
            // tracked every expression tick regardless of whether a probe is attached — see
            // GazeSnapshot.TargetPointDeltaMeters.
            Vector3 directiveWorldPoint = _directive.WorldPoint;
            _targetPointDeltaMeters = _hasLastDirectiveWorldPoint
                ? Vector3.Distance(directiveWorldPoint, _lastDirectiveWorldPoint)
                : 0f;
            _lastDirectiveWorldPoint = directiveWorldPoint;
            _hasLastDirectiveWorldPoint = true;

            // ---- The gaze shift, measured once and divided once. ----
            //
            // Order is load-bearing: measure what the shift requires from the rig, hand that
            // one number to the actuator ladder, then let each actuator execute the share it
            // was given. Every stage of this chain used to measure and decide for itself, and
            // the three answers were free to disagree — see GazeActuatorLadder.
            bool hasShift = _directive.HasEngagedTarget &&
                            _chain.TryMeasureShift(_directive.WorldPoint, out _shiftMeasurement);
            if (!hasShift) _shiftMeasurement = default;

            GazeShiftPlan shiftPlan = hasShift
                ? _shiftDirector.Plan(
                    in _shiftMeasurement,
                    profile,
                    // The settled strength, NOT the acquire/release ramp. The ladder's share is
                    // proportional to what it is handed, so handing it the ramp made the head's
                    // goal ramp too — and a ramped goal is tracked, not shaped: the head followed
                    // it at the ramp's speed, skipping the duration law entirely. The actuator
                    // needs to see where the look is going in order to decide how long getting
                    // there should take. See GazeDirective.SettledEngagement.
                    _directive.SettledEngagement,
                    _directive.HeadContribution,
                    RecruitsTorso(_directive.Nature, _chain.HasTorso),
                    _directive.AllowBodyTurn,
                    _directive.GenerationId,
                    deltaTime,
                    // Last frame's achieved pose. Comfort is about what the character is
                    // holding, which only the previous frame can report.
                    (_eyes.LeftEyeAngles + _eyes.RightEyeAngles).magnitude * 0.5f,
                    _headTorso.HeadAngles.x,
                    // Arriving is one movement. Handing the path a walking character was
                    // watching over to whatever is at the end of it must not restart the
                    // cascade — that put a second onset freeze right at the moment the
                    // character reaches you and turns.
                    _previousTargetKind == GazeTargetKind.TravelPath,
                    EyeRestBudgetFor(_directive.Nature, _directive.AllowBodyTurn, profile),
                    // Walking owns the facing; a policy that disallows the body turn does not.
                    _wasTraveling,
                    // A request that forbids the turn is not a preference the ladder may overrule.
                    _directive.BodyTurnForbidden)
                : GazeShiftPlan.Idle;
            _previousTargetKind = _directive.HasEngagedTarget ? _directive.Kind : GazeTargetKind.None;
            _lastHeadGoal = shiftPlan.Head;

            // Ticked BEFORE the actuators so this frame's relief reflects this frame's turn.
            // Read after them, the relief was a frame stale at both ends of every turn: the
            // neck stayed extended into the first frame of a turn and snapped back a frame
            // after it ended.
            _reorientation.Tick(
                bodyTurnStyle == GazeBodyTurnStyle.SteppingTurn ? Context.ReorientationHandler : null,
                profile,
                in _directive,
                _shiftMeasurement.RequiredYaw,
                shiftPlan.WantsFeet,
                _chain.Root != null ? _chain.Root : transform,
                Context.CharacterRoot != null ? Context.CharacterRoot : transform,
                deltaTime,
                _trace);

            // The scale is applied unconditionally: the director holds it at 1 whenever no
            // planning break is running, including outside Speaking, so there is nothing left
            // for a state test to decide. Gating it on the state instead made the term step
            // from the cancelled break's scale straight back to 1 on the Speaking-exit edge,
            // with the beat's residue still on the offset — a gain step on a channel composed
            // downstream of the actuator, which is a pose step by another name.
            Vector2 headAversionOffset = ResolveFocusAversionOffset(
                _aversion.Offset * _turnTaking.HeadParticipationScale, eyeContactLocked);
            // The floor-yield head dip is composed on top of the arbiter's own output — like
            // the interruption tilt below — so it plays even while an external Body Language
            // head-gesture program owns Compose()'s backchannel-vs-external decision.
            Vector2 headGestureOffset = exactFocus
                ? Vector2.zero
                : _headGestureArbiter.Offset + _turnTaking.YieldHeadDipOffset;
            _lastHeadAversionOffset = headAversionOffset;
            _lastHeadGestureOffset = headGestureOffset;

            var input = new HeadTorsoSolveInput
            {
                Chain = _chain,
                Profile = profile,
                DeltaTime = deltaTime,
                TargetPoint = _directive.WorldPoint,
                HasTarget = hasShift,
                Measurement = _shiftMeasurement,
                Plan = shiftPlan,
                Engagement = _directive.Engagement,
                AmbientAngles = _ambient.CurrentAngles,
                AmbientActive = ambientActive,
                AmbientHandover = ambientHandover,
                AversionOffset = headAversionOffset,
                GestureOffset = headGestureOffset,
                GestureRollDegrees = exactFocus
                    ? 0f
                    : _headGestureArbiter.RollDegrees + _interruptionReaction.TiltDegrees,
                BodyTurnActive = _reorientation.IsReorienting,
                // What kind of movement this is, which sets how long it takes. Only genuine
                // reflexes are urgent: a startle beat, and re-acquiring after a cut or teleport.
                // Looking at the player is Neutral like any other act of attention — a person
                // walking up is not an emergency, and a character that whips round to face its
                // own user reads as alarmed by them.
                Urgency = ResolveMovementUrgency(ambientActive),
                PoseSink = Context?.ProceduralPoseCompositor
            };
            _headTorso.Solve(in input);

            // Contributor-trace: degrees the head bone's world rotation moved this frame, after
            // the solve has written it — see GazeSnapshot.AppliedHeadDeltaDegrees.
            Transform headBone = _chain.Head;
            if (headBone != null)
            {
                Quaternion headWorldRotation = headBone.rotation;
                _appliedHeadDeltaDegrees = _hasLastAppliedHeadWorldRotation
                    ? Quaternion.Angle(_lastAppliedHeadWorldRotation, headWorldRotation)
                    : 0f;
                _lastAppliedHeadWorldRotation = headWorldRotation;
                _hasLastAppliedHeadWorldRotation = true;
            }
            else
            {
                _appliedHeadDeltaDegrees = 0f;
                _hasLastAppliedHeadWorldRotation = false;
            }

            SolveEyes(profile, deltaTime, ambientActive, eyeContactLocked, exactFocus,
                shiftPlan.HeadOnsetPending);
            ProcessScriptedSettlement();
            TraceReachLimit(deltaTime);

            TraceFirehose(profile, deltaTime);

            // Pupil response: emotion intensity + gaze engagement arousal, smoothed ~1s,
            // published to an optional eye-appearance driver (e.g. a shader-property pupil
            // dilation binding). A no-op single null check when no driver is registered.
            IEmotionStateFrameSource pupilFrameSource = Context.EmotionStateFrameSource;
            float pupilEmotionScore = pupilFrameSource != null
                ? pupilFrameSource.CurrentFrame.DominantScore
                : Context.EmotionStateSource?.Current.DominantScore ?? 0f;
            _pupilArousal.Tick(pupilEmotionScore, _directive.Engagement, deltaTime);
            Context.EyeAppearanceDriver?.SetPupilDilation(_pupilArousal.Dilation);

            // Eyebrow-gaze coordination: current eye pitch (post-solve, positive upward),
            // this tick's backchannel-nod-start pulse, and the interruption startle
            // re-acquisition pulse decide whether a one-shot brow cue fires this frame. A no-op
            // single null check when no brow-cue sink is registered.
            float eyePitchDegrees = (_eyes.LeftEyeAngles.y + _eyes.RightEyeAngles.y) * 0.5f;
            _browCueCoordinator.Tick(
                eyePitchDegrees, _backchannel.NodStartedThisTick, _interruptionReaction.WantsReacquisition, deltaTime);
            if (_browCueCoordinator.HasPendingCue)
                Context.BrowCueSink?.RaiseBrowCue(_browCueCoordinator.PendingKind, _browCueCoordinator.PendingIntensity);
        }

        /// <summary>
        ///     Classifies this frame's head/torso movement so the actuator can pick a duration.
        /// </summary>
        /// <remarks>
        ///     Only reflexes are urgent, and the startle beat is now the only reflex: it is the
        ///     one thing that happens TO the character and demands an answer this instant.
        ///     Everything else — including acquiring the player, and including re-finding the
        ///     target after a camera cut — is something the character chose, and choices are made
        ///     at ordinary speed. Idle exploration is slower still, which is most of what
        ///     separates an idle character from an alert one.
        /// </remarks>
        private GazeMovementUrgency ResolveMovementUrgency(bool ambientActive) =>
            ResolveMovementUrgency(
                _interruptionReaction.WantsReacquisition,
                ambientActive,
                _directive.HasEngagedTarget,
                _directive.Nature);

        /// <summary>
        ///     The classification itself, as a pure function of the four things that decide it,
        ///     so the rule can be asserted without a rig. <paramref name="wantsReacquisition" />
        ///     is the startle reflex — the one thing that happens TO the character.
        /// </summary>
        internal static GazeMovementUrgency ResolveMovementUrgency(
            bool wantsReacquisition,
            bool ambientActive,
            bool hasEngagedTarget,
            GazeLookNature nature)
        {
            if (wantsReacquisition) return GazeMovementUrgency.Urgent;

            if (ambientActive || !hasEngagedTarget)
                return GazeMovementUrgency.Relaxed;

            // The look's own nature, not whether there happens to be a target. Asking the second
            // question made Relaxed unreachable for anything real — ambientActive is defined as
            // "no engaged target", so the two branches above were the same test written twice —
            // and an idle glance at somebody across the room therefore ran at full conversational
            // tempo, seconds after the idle drift it interrupted ran a third slower. Same
            // character, same beat, two movement laws.
            return nature switch
            {
                GazeLookNature.Glance => GazeMovementUrgency.Relaxed,
                GazeLookNature.Reflex => GazeMovementUrgency.Urgent,
                _ => GazeMovementUrgency.Neutral
            };
        }

        private void SolveEyes(
            ConvaiGazeProfile profile,
            float deltaTime,
            bool ambientActive,
            bool eyeContactLocked,
            bool exactFocus,
            bool headOnsetPending)
        {
            // Whether there is a face here, not whether this happens to be the player. The old
            // test was wrong in both directions at once: aimed at a bare camera it scanned an
            // imaginary face and sat one to two degrees off the thing the viewer is looking
            // through — "why isn't it looking at me" — while aimed at another character, who has
            // an actual face, it held one point dead still and read as a mannequin.
            bool faceScanActive = _directive.HasEngagedTarget && _directive.TargetHasFace;
            // Emotional gaze signature: SaccadeTempoScale paces the micro-saccade dwell too
            // (quicker tempo = livelier fixation, slower tempo = more settled).
            _micro.Tick(profile, deltaTime, _emotionModulator.SaccadeTempoScale, ref _random);
            // Listener mouth-bias: FaceScanDirector smooths this raw flag itself (~0.5s).
            _faceScan.Tick(profile, deltaTime, faceScanActive && !exactFocus, _playerSpeaking, ref _random);

            // Emotional gaze signature: FixationLivelinessScale multiplies the state's own
            // liveliness before the speech-energy modulation, so an emotion's stillness/energy
            // and speech's own boost compose rather than one overriding the other.
            float liveliness = _directive.FixationLiveliness * _emotionModulator.FixationLivelinessScale;
            ISpeechEnergyProvider speechEnergy = Context.SpeechEnergyProvider;
            if (speechEnergy != null && _directive.HasEngagedTarget)
                liveliness *= Mathf.Lerp(0.85f, 1.2f, Mathf.Clamp01(speechEnergy.Current));

            // Proxemic intimacy regulation: scales the face-scan landmark offset by the same
            // factor FaceScanDirector.Offset's own radius would be scaled by (it's a pure
            // multiplier on Landmarks * radius, so scaling the offset here is equivalent to
            // scaling FaceScanRadiusDegrees at the source) — bypassed entirely while the
            // eye-contact lock is in force. Cached for CaptureSnapshot — see
            // GazeSnapshot.MicroOffset / AversionEyeOffset.
            Vector2 eyeMicroOffset = ResolveMicroOffset(eyeContactLocked, exactFocus);
            Vector2 eyeAversionOffset = ResolveFocusAversionOffset(_aversion.EyeOffset, eyeContactLocked);
            _lastEyeMicroOffset = eyeMicroOffset;
            _lastEyeAversionOffset = eyeAversionOffset;

            var eyeInput = new EyeSolveInput
            {
                Chain = _chain,
                Profile = profile,
                DeltaTime = deltaTime,
                TargetPoint = _directive.WorldPoint,
                // The eyes stop aiming at a look the character has let go of, while the head
                // goes on unwinding under its own movement law.
                //
                // The eyes deliberately do NOT scale their aim by engagement — real eyes jump to
                // a target and hold it, and dragging the aim point as commitment ramps would
                // shatter one acquisition into a staircase of catch-up saccades. That is right
                // while a look is being taken up and held. Applied to the release it produced the
                // exact opposite of what eyes do: the head's share shrinks with engagement and
                // unwinds over the release ramp, so for most of a second the head walked back to
                // neutral while the eyes stayed pinned to the abandoned point — and because the
                // eyes are the residual of the shift, they had to deviate FURTHER to hold it as
                // the head left. Eyes are the fast stage; they let go first, not last.
                HasTarget = _directive.HasEngagedTarget && !_directive.IsReleasing,
                Engagement = _directive.Engagement,
                AmbientAngles = _ambient.CurrentAngles,
                AmbientActive = ambientActive,
                // Sampled by the head stage, which ran earlier this tick and is the only place in
                // the frame where the animation's own head pose can be told apart from gaze's.
                // Idle life needs the distinction: an authored head turn must carry the eyes, and
                // gaze's own head share must not.
                AnimatedHeadAngles = _headTorso.AnimatedHeadAngles,
                MicroOffset = eyeMicroOffset,
                AversionOffset = eyeAversionOffset,
                GenerationId = _directive.GenerationId,
                // The interruption startle re-acquisition pulse forces a fresh saccade toward
                // the current target on this tick only, exactly like a real teleport/camera cut
                // — reuses the eye solver's existing fresh-target mechanism rather than a new
                // channel.
                Teleported = _directive.TeleportedThisTick || _interruptionReaction.WantsReacquisition,
                FixationLiveliness = liveliness,
                // Emotional gaze signature: scales saccade reaction latency in the eye stage.
                SaccadeTempoScale = _emotionModulator.SaccadeTempoScale,
                ApplyToBones = _useEyeBones,
                LookShapesActive = _useLookShapes,
                // While the head is about to join or is in flight, the eyes lead only as far as
                // their reach; the head brings the rest. Free again the moment the head lands.
                ReachDegrees = _headTorso.IsShifting || headOnsetPending
                    ? EyeReachDegrees(profile)
                    : 0f
            };
            _eyes.Solve(in eyeInput);

            if (_interruptionReaction.WantsBlink || _turnTaking.WantsYieldBlink)
                _blink.TryTriggerForcedBlink(profile);

            float saccadeAmplitude = _eyes.SaccadeStartedAmplitude;
            if (saccadeAmplitude > 0f)
            {
                bool shiftBlink = _blink.TryTriggerShiftBlink(profile, saccadeAmplitude, ref _random);

                // Saccades fire a few times a second, so the gate is tested before the message
                // is built — at Off verbosity this path allocates nothing at all.
                if (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.Detail))
                {
                    if (shiftBlink)
                        _trace.Detail($"Gaze-shift blink on {saccadeAmplitude:0.0}° saccade.");
                    else if (saccadeAmplitude > 8f)
                        _trace.Detail($"Saccade {saccadeAmplitude:0.0}° toward '{_directive.TargetName}'.");
                }
            }

            _blink.Tick(profile, deltaTime, ref _random);

            FacialBlendshapeCompositorHost compositor = Context?.EnsureCompositor();
            // Lid aperture is expression, not contact, so it rides through even under an
            // eye-contact lock — an angry locked stare should still look angry.
            _eyeWriter.Submit(
                compositor, this, profile, _blink.Weight, _emotionModulator.LidApertureScale,
                _eyes.LeftEyeAngles, _eyes.RightEyeAngles,
                driveLookShapes: _useLookShapes, deltaTime);
        }

        private Vector2 ResolveMicroOffset(bool eyeContactLocked, bool exactFocus)
        {
            if (exactFocus) return Vector2.zero;
            Vector2 offset = _micro.Offset +
                             _faceScan.Offset * (eyeContactLocked ? 1f : _proxemics.FaceScanRadiusScale);

            // The arrival settle rides this channel specifically because the head never reads
            // it: a settle that moved the neck would be a head bow, not a settle.
            offset.y += _arrivalSettle.PitchOffsetDegrees;

            return ConstrainMicroOffset(offset, eyeContactLocked);
        }

        internal static Vector2 ConstrainMicroOffset(Vector2 offset, bool socialFocusActive) =>
            socialFocusActive ? Vector2.ClampMagnitude(offset, 0.75f) : offset;

        internal static Vector2 ResolveFocusAversionOffset(Vector2 offset, bool focusActive) =>
            focusActive ? Vector2.zero : offset;

        internal static float ComposeNaturalSpeakingAversion(
            float authoredStrength,
            float suppressionFactor,
            float proxemicFloor,
            bool applyProxemicFloor,
            bool turnTakingOwnsSpeaking)
        {
            float strength = Mathf.Clamp01(authoredStrength * suppressionFactor);
            if (applyProxemicFloor && !turnTakingOwnsSpeaking)
                strength = Mathf.Max(strength, Mathf.Clamp01(proxemicFloor));
            return strength;
        }

        private void TraceReachLimit(float deltaTime) =>
            _diagnostics.ReportReachLimit(
                _trace, deltaTime, _directive.HasEngagedTarget, _directive.Engagement,
                _eyes.ContactErrorDegrees, _directive.TargetName);

        private void TraceFirehose(ConvaiGazeProfile profile, float deltaTime)
        {
            // Gated here as well as inside the reporter so the sample is not even gathered at the
            // verbosities every shipping character actually runs at.
            if (profile.TraceVerbosity < GazeTraceVerbosity.Firehose) return;

            var sample = new GazeFirehoseSample(
                _directive.Engagement, _headTorso.HeadAngles, _headTorso.TorsoAngles,
                _eyes.LeftEyeAngles, _eyes.PhaseName, _blink.Weight,
                _headTorso.TargetYawError, _directive.Kind, _directive.TargetName);

            _diagnostics.ReportFirehose(
                _trace, deltaTime, profile.TraceVerbosity, profile.FirehoseHz, in sample);
        }

        /// <summary>Fills <paramref name="snapshot" /> with the live gaze state.</summary>
        public void CaptureSnapshot(GazeSnapshot snapshot)
        {
            if (snapshot == null) return;

            snapshot.Clear();
            snapshot.Reading = Current;
            snapshot.TargetKind = _directive.HasEngagedTarget ? _directive.Kind : GazeTargetKind.None;
            snapshot.TargetName = _directive.HasEngagedTarget ? _directive.TargetName : "-";
            if (_directive.HasEngagedTarget && _directive.Kind == GazeTargetKind.Player)
            {
                PlayerAnchorTargetProvider anchor = FindActivePlayerAnchorProvider();
                if (anchor != null)
                    snapshot.TargetName = anchor.RefreshDebugName(_directive.Target, snapshot.TargetName);
            }
            // What gaze is acting on, not what the source last said: this panel answers "why is
            // the character looking like that", and a state gaze has not adopted explains nothing.
            // The conversation-flow inspector is where the raw stream is read.
            snapshot.DialogueState = _dialogueState;
            snapshot.PolicyEngagement = _policy.SmoothedEngagement;
            snapshot.HeadAngles = _headTorso.HeadAngles;
            snapshot.HeadRollDegrees = _headTorso.HeadRollDegrees;
            snapshot.TorsoAngles = _headTorso.TorsoAngles;
            snapshot.TargetErrorAngles = _directive.HasEngagedTarget
                ? new Vector2(_shiftMeasurement.RequiredYaw, _shiftMeasurement.RequiredPitch)
                : Vector2.zero;
            snapshot.LeftEyeAngles = _eyes.LeftEyeAngles;
            snapshot.RightEyeAngles = _eyes.RightEyeAngles;
            snapshot.EyePhase = _eyes.PhaseName;
            snapshot.ContactErrorDegrees = _directive.HasEngagedTarget ? _eyes.ContactErrorDegrees : float.NaN;
            snapshot.AimErrorDegrees = _directive.HasEngagedTarget ? _eyes.AimErrorDegrees : float.NaN;
            snapshot.TargetHasFace = _directive.HasEngagedTarget && _directive.TargetHasFace;
            snapshot.LookNature = _directive.HasEngagedTarget ? _directive.Nature.ToString() : "-";
            snapshot.FocusActive = _focusActive;
            snapshot.FocusFidelity = focusFidelity;
            snapshot.FocusDegraded = _focusDegraded;
            snapshot.ContactUsesBoneBackend = _useEyeBones;
            snapshot.BlinkWeight = _blink.Weight;
            snapshot.IsReorienting = _reorientation.IsReorienting;
            snapshot.IsNodding = _backchannel.IsNodding;
            bool sensorLive = _attentionSensor != null && _attentionSensor.isActiveAndEnabled;
            snapshot.PlayerAttention = sensorLive ? _attentionSensor.PlayerAttention : -1f;
            snapshot.PlayerLooking = sensorLive && _attentionSensor.IsPlayerLooking;
            snapshot.AttendingSpeaker = _conversation.Current.IsAttending;
            snapshot.SpeakerAttention = DescribeSpeakerAttention();
            ConvaiGazeProfile lodProfile = EffectiveProfile;
            snapshot.LodEnabled = lodProfile != null && lodProfile.EnableGazeLod;
            snapshot.LodFar = snapshot.LodEnabled && _lodGovernor.IsFar;
            snapshot.LodExpressionSkipped = _lodSkipExpression;

            // Contributor-trace fields capture the individual sources of head motion.
            // Every value here is read from a field the LateUpdate tick already updated this
            // frame (or the last frame it ran), so capturing costs no extra solving.
            snapshot.HeadGoal = _lastHeadGoal;
            snapshot.HeadShiftActive = _headTorso.IsShifting;
            snapshot.StabilizationOffset = _headTorso.StabilizationOffset;
            snapshot.AnimatedDeviation = _shiftMeasurement.IsValid
                ? new Vector2(_shiftMeasurement.AnimatedYaw, _shiftMeasurement.AnimatedPitch)
                : Vector2.zero;
            snapshot.GestureOffset = _lastHeadGestureOffset;
            snapshot.AversionHeadOffset = _lastHeadAversionOffset;
            snapshot.AversionEyeOffset = _lastEyeAversionOffset;
            snapshot.MicroOffset = _lastEyeMicroOffset;
            snapshot.ChainLagOffset = _headTorso.ChainLagOffset;
            snapshot.AppliedHeadDeltaDegrees = _appliedHeadDeltaDegrees;
            snapshot.TargetPointDeltaMeters = _targetPointDeltaMeters;
            snapshot.GenerationId = _directive.GenerationId;

            _trace?.CopyRecentEntries(snapshot.RecentTrace);
        }

        /// <summary>Allocating convenience overload of <see cref="CaptureSnapshot(GazeSnapshot)" />.</summary>
        public GazeSnapshot CaptureSnapshot()
        {
            var snapshot = new GazeSnapshot();
            CaptureSnapshot(snapshot);
            return snapshot;
        }

        /// <summary>
        ///     While idle (player suppressed by policy), occasionally schedules a soft,
        ///     short glance at the player through the scripted stack so the character still
        ///     feels aware of them. Low priority — any real scripted request outranks it.
        /// </summary>
        /// <summary>
        ///     Priority of a travel check-in glance. Glance tier (below the explicit
        ///     <see cref="GazeAt(Transform, GazeOptions)" /> default of 0) so an authored gaze action
        ///     always wins and an eye-contact lock absorbs it through the existing suppression path —
        ///     above the idle curiosity glance, because checking on where you are going while walking
        ///     is a more purposeful beat than idle curiosity.
        /// </summary>
        private const int TravelCheckInPriority = -50;

        /// <summary>
        ///     Fires the periodic look at what the journey is about. The subject is whatever declared
        ///     it — the destination of a walk, the person being followed, or the target of an action
        ///     step — so a customer's own executor gets this without writing any gaze code.
        /// </summary>
        private void TickTravelCheckIn(ConvaiGazeProfile profile, DialogueState state, float deltaTime)
        {
            if (!_travel.TickCheckIn(in _travelIntent, state, profile, ref _random, deltaTime)) return;

            _scripted.PushUnowned(
                null, ResolveTravelCheckInPoint(), hasTransform: false,
                priority: TravelCheckInPriority, engagementOverride: 0.9f, allowBodyTurn: false,
                deadline: Time.time + profile.TravelGlanceHoldSeconds, name: "travel check-in");

            if (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.Detail))
                _trace.Detail($"Travel check-in glance for {profile.TravelGlanceHoldSeconds:0.00}s.");
        }

        /// <summary>
        ///     Height above the character root that a look point sits at. Uses the head bone when the
        ///     rig offers one, and the same 1.6 m standing eye line as <see cref="ResolveRestPoint" />
        ///     otherwise — one height convention for the module, not two.
        /// </summary>
        /// <summary>
        ///     Where a travel check-in glance actually aims: the journey's subject, raised to the
        ///     character's own eye line when it sits below it.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The travel reading reports a subject's <em>transform position</em>, which for a
        ///         companion is the player's root — their feet, not their eyes — and for a
        ///         destination is a point on the floor. Aimed at raw, a check-in glance therefore
        ///         looks at the ground, and the closer the character gets the worse it is: the
        ///         angle steepens (about 57° down at a metre) while the check-in cadence
        ///         simultaneously tightens on approach. The character arrives somewhere and
        ///         repeatedly ducks its head at its own feet.
        ///     </para>
        ///     <para>
        ///         Raising it is the same correction <c>PlayerAnchorTargetProvider</c> already
        ///         applies to a non-camera anchor, for the same reason — gaze should land on the
        ///         eye line rather than the feet. The rule here is one-sided on purpose: a
        ///         subject ABOVE the character's eye line is left alone, so it still looks up at
        ///         a high shelf. What it will not do is crane its neck downward at something it
        ///         is walking toward.
        ///     </para>
        /// </remarks>
        private Vector3 ResolveTravelCheckInPoint()
        {
            Transform root = Context != null ? Context.CharacterRoot : transform;
            if (root == null) return _travelIntent.SubjectPosition;

            return LiftToEyeLine(
                _travelIntent.SubjectPosition, root.position.y + ResolveEyeHeight(root));
        }

        /// <summary>
        ///     Raises a look point to <paramref name="observerEyeLineY" /> when it sits below it,
        ///     and leaves it alone when it does not.
        /// </summary>
        internal static Vector3 LiftToEyeLine(Vector3 point, float observerEyeLineY)
        {
            point.y = Mathf.Max(point.y, observerEyeLineY);
            return point;
        }

        /// <summary>
        ///     Height of this character's eye line above its root, for the look points that are
        ///     defined relative to it — the path a traveller watches, and the lift applied to a
        ///     travel subject.
        /// </summary>
        /// <remarks>
        ///     Measured from the eye bones when the rig has them, and only from the head bone
        ///     otherwise. The head bone sits below the eyes — about 7 cm on a CC4 rig — and both
        ///     callers mean the eye line specifically, so reading the head bone put a standing
        ///     downward bias of a degree or so on everything a walking character looked at.
        ///     Harmless in isolation and in exactly the wrong direction next to the other
        ///     head-down defects, so it is measured properly rather than left to cancel.
        /// </remarks>
        private float ResolveEyeHeight(Transform root)
        {
            if (root == null || Context?.RigBinding == null) return DefaultEyeHeight;

            IStandardRigBinding binding = Context.RigBinding;
            binding.TryGetBone(StandardBone.LeftEye, out Transform leftEye);
            binding.TryGetBone(StandardBone.RightEye, out Transform rightEye);
            if (leftEye != null && rightEye != null)
                return (leftEye.position.y + rightEye.position.y) * 0.5f - root.position.y;

            if (binding.TryGetBone(StandardBone.Head, out Transform head) && head != null)
                return head.position.y - root.position.y;

            return DefaultEyeHeight;
        }

        /// <summary>Adult eye height (metres) used when the rig offers nothing to measure.</summary>
        private const float DefaultEyeHeight = 1.6f;

        /// <summary>
        ///     Brings the character's travel intent into being the first time it is seen to move.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A deliberately looser probe than the real detection: this only answers "should this
        ///         character have a travel intent at all", and the component itself then applies the
        ///         proper speed and sustain gates. Measured parent-locally for the same reason it is
        ///         there — a character riding a moving platform is not walking.
        ///     </para>
        ///     <para>
        ///         Convai locomotion provisions it at the start of a move instead, so this path only
        ///         matters for characters moved by something else entirely.
        ///     </para>
        /// </remarks>
        private void EnsureTravelIntentIfMoving(ConvaiGazeProfile profile)
        {
            if (_travelIntentProvisioned || !profile.EnableTravelGaze) return;

            if (Context?.TravelIntentSource != null)
            {
                _travelIntentProvisioned = true;
                return;
            }

            // The character root, not this component's own transform: gaze is not required to sit on
            // the root, and a controller on a child object holds a constant local position while the
            // character walks — the probe would never fire, and the component would be provisioned
            // onto the child. Same idiom the owned player anchor already uses.
            Transform root = Context.CharacterRoot != null ? Context.CharacterRoot : transform;

            Vector3 local = root.localPosition;
            if (!_hasLastLocalRootPosition)
            {
                _lastLocalRootPosition = local;
                _hasLastLocalRootPosition = true;
                return;
            }

            Vector3 delta = local - _lastLocalRootPosition;
            _lastLocalRootPosition = local;
            delta.y = 0f;

            // One frame of unmistakable movement is enough to justify the component; being wrong
            // costs an inert component, while being late costs the first stride of the walk.
            if (delta.sqrMagnitude < ProvisioningMoveEpsilonSquared) return;

            _travelIntentProvisioned = ConvaiTravelIntent.EnsureOn(root.gameObject) != null;
        }

        /// <summary>
        ///     Squared per-tick parent-local displacement that justifies provisioning a travel intent
        ///     (2 cm — about 1.2 m/s at 60 Hz, unambiguously locomotion rather than settle or drift).
        /// </summary>
        private const float ProvisioningMoveEpsilonSquared = 0.02f * 0.02f;

        private void TickCuriosityGlance(ConvaiGazeProfile profile, in GazeStatePolicy statePolicy, float deltaTime)
        {
            // A character following somebody else's turn is not idle, whatever its own dialogue
            // state says. Without this the glance directors keep firing — and a scripted glance
            // always outranks a provider candidate, so every one of them would yank the gaze off
            // the speaker the character is deliberately watching.
            bool idleActive = !statePolicy.AllowPlayerTarget &&
                              !_conversation.Current.Active &&
                              !_conversationLive &&
                              _scripted.Count == 0 &&
                              profile.EnableAmbientExploration;

            // E8 reciprocation: when a player attention sensor reports the player is watching,
            // shrink the wait so the idle character glances back sooner (down to ~40% at full
            // attention). No sensor / feature off → unchanged authored cadence.
            float glanceIntervalScale = 1f;
            if (profile.CuriosityRespondsToAttention && _attentionSensor != null && _attentionSensor.isActiveAndEnabled)
                glanceIntervalScale = Mathf.Lerp(1f, 0.4f, Mathf.Clamp01(_attentionSensor.PlayerAttention));

            if (!_curiosity.Tick(profile, deltaTime, idleActive, ref _random, glanceIntervalScale)) return;

            Transform curiosityRoot = Context != null && Context.CharacterRoot != null
                ? Context.CharacterRoot
                : transform;

            for (int i = 0; i < _candidates.Count; i++)
            {
                GazeTargetCandidate candidate = _candidates[i];
                if (candidate.Kind != GazeTargetKind.Player) continue;
                // Same reachability rule the character glance uses, and for the same reason: a
                // glance never turns the body, so a player standing behind the shoulder is not
                // glanced at — it would pin the head and eyes at their limits for the whole hold
                // and then unwind, which is a lurch, not a glance. Skipping re-arms the timer, so
                // the character tries again once the player is somewhere it can actually look.
                if (!IsWithinGlanceReach(curiosityRoot, candidate.WorldPoint)) continue;

                _scripted.PushUnowned(
                    candidate.Target, candidate.WorldPoint, candidate.Target != null,
                    // Engagement 1, like every other glance in this module: a glance is
                    // committed and its brevity is what makes it a glance (see GlanceOptions).
                    // It shipped at 0.5, which was a second damper on top of the state policy's
                    // own head contribution — see GlanceHeadContribution.
                    priority: -100, engagementOverride: 1f, allowBodyTurn: false,
                    deadline: Time.time + profile.CuriosityGlanceDuration, name: "curiosity glance",
                    nature: GazeLookNature.Glance,
                    headContributionOverride: GlanceHeadContribution,
                    localAimOffset: LocalAimOffsetOf(in candidate));
                if (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.Detail))
                    _trace.Detail($"Curiosity glance at '{candidate.DebugName}' for {profile.CuriosityGlanceDuration:0.0}s.");
                return;
            }
        }

        /// <summary>
        ///     Horizontal angle from the character's facing beyond which an idle glance is not
        ///     attempted: a glance never turns the body, so a target behind the shoulder would
        ///     just pin the eyes and head at their limits for the whole hold. The reason is the
        ///     rig's reach, which does not care what the character is glancing at — the glances
        ///     between people moved into the conversation director, where the same limit is the
        ///     attention angle, and this one now belongs to the curiosity glance at the player.
        /// </summary>
        private const float GlanceMaxYawDegrees = 100f;

        /// <summary>
        ///     How much of an idle glance the head takes. A glance at a person is an act of
        ///     attention: the head does most of it and the eyes finish, which is how people look
        ///     at each other.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Stated by the glance rather than inherited from the dialogue state, because the
        ///         state in question is Idle, whose head contribution describes a character
        ///         drifting around a room (0.4 in the shipped sample profile) — a different
        ///         movement that happens to share a row in the table. Inherited, the glance's own
        ///         strength multiplied it: 0.5 × 0.4 = 0.2, a number nobody authored, and the
        ///         result was a character who barely turned her head and held the look at the
        ///         corner of her eyes. Past a 44° shift the eyes even ran out of travel
        ///         (<c>Eye Max Yaw</c> 35°) and the gaze landed short of the player outright —
        ///         "she turned, but she is not looking at me".
        ///     </para>
        ///     <para>
        ///         0.75 keeps the eyes inside their comfort range for anything the glance is
        ///         allowed to attempt: at the widest reachable shift the head takes about three
        ///         quarters and the eyes are left with the rest, instead of the reverse.
        ///     </para>
        /// </remarks>
        private const float GlanceHeadContribution = 0.75f;

        /// <summary>
        ///     A candidate's aim point expressed in its own transform's space, so a glance built
        ///     from it follows the target and still aims where the provider said.
        /// </summary>
        /// <remarks>
        ///     A scripted request that carries a transform resolves its aim from that transform's
        ///     position, which is right for a developer's <c>GazeAt(prop)</c> and wrong for a
        ///     provider candidate: the player anchor aims at an eye line above the rig's root, and
        ///     re-pushing it as a glance without this dropped that offset — the character followed
        ///     the player perfectly and looked at their feet. Zero whenever the point already is
        ///     the transform's origin, which is the case for a camera anchor, so the common desktop
        ///     setup is bit-identical.
        /// </remarks>
        private static Vector3 LocalAimOffsetOf(in GazeTargetCandidate candidate) =>
            candidate.Target != null
                ? candidate.Target.InverseTransformPoint(candidate.WorldPoint)
                : Vector3.zero;

        /// <summary>
        ///     Whether <paramref name="worldPoint" /> is inside <see cref="GlanceMaxYawDegrees" />
        ///     of the character's facing, measured on the horizontal plane. A degenerate facing or
        ///     a point directly overhead counts as reachable — there is no yaw to be beyond.
        /// </summary>
        private static bool IsWithinGlanceReach(Transform root, Vector3 worldPoint)
        {
            if (root == null) return true;

            Vector3 forward = root.forward;
            forward.y = 0f;
            Vector3 toTarget = worldPoint - root.position;
            toTarget.y = 0f;
            if (forward.sqrMagnitude < 1e-6f || toTarget.sqrMagnitude < 1e-6f) return true;

            return Vector3.Angle(forward, toTarget) <= GlanceMaxYawDegrees;
        }

        /// <summary>
        ///     Tells the shared <see cref="ConversationRoomModel" /> what this character knows —
        ///     itself, and the player as it sees them — and then asks the room to derive itself.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Every controller reports the same player, and the room takes the last write of
        ///         the frame; they are all reading one event hub and one anchor, so they agree.
        ///         The report is skipped entirely when no player point resolves, rather than
        ///         reported from the character's own feet — a controller with no anchor has
        ///         nothing to say about where the player is, and saying it anyway would overwrite
        ///         a neighbour that does.
        ///     </para>
        ///     <para>
        ///         <see cref="ConversationRoomModel.Refresh" /> is guarded on the frame counter, so
        ///         however many characters call this, the derivation runs once.
        ///     </para>
        /// </remarks>
        private void ReportConversationRoom(bool characterSpeaking, ISpeechEnergyProvider speech)
        {
            ConversationRoomModel room = ConversationRoomModel.Shared;
            Transform root = Context != null && Context.CharacterRoot != null ? Context.CharacterRoot : transform;

            Vector3 headPoint = ResolveRoomHeadPoint(root);

            room.ReportParticipant(
                ResolveConversationRoomKey(root),
                headPoint,
                root.forward,
                characterSpeaking,
                speech != null ? speech.Current : 0f,
                null, root);

            if (TryResolvePlayerPoint(out Vector3 playerPoint))
            {
                Vector3 playerForward = TryResolvePlayerAnchor(out Transform playerAnchor) && playerAnchor != null
                    ? playerAnchor.forward
                    : Vector3.forward;

                bool typedThisTick = _playerTypedAt > float.NegativeInfinity &&
                                     !Mathf.Approximately(_playerTypedAt, _reportedTypedAt);
                if (typedThisTick) _reportedTypedAt = _playerTypedAt;

                room.ReportPlayer(
                    playerPoint,
                    playerForward,
                    _playerSpeaking,
                    PlayerLocallyActive,
                    _playerLocalLevel,
                    ResolveAddresseeRoomKey(),
                    typedThisTick);
            }

            room.Refresh(Time.time, Time.frameCount, ConversationRoomTuning.Default);
        }

        /// <summary>
        ///     Where this character's eyes are for room purposes: the head pivot once the rig has
        ///     bound, and the eye line above the root until then. The point it is reported to the
        ///     room at and the point its own vision rays start from are deliberately the same one
        ///     — a character that raycast from its feet would find the floor between itself and
        ///     everybody it is standing with.
        /// </summary>
        /// <param name="root">This character's root transform.</param>
        private Vector3 ResolveRoomHeadPoint(Transform root) => _chain.IsBound
            ? _chain.HeadPivotPosition
            : root.position + Vector3.up * RoomEyeLineFallbackMeters;

        /// <summary>
        ///     This character's share of the line-of-sight interval, in 0..1, derived from its own
        ///     room key. Eight characters that woke on the same frame would otherwise raycast on
        ///     the same frame for the rest of the scene; spread by their keys they never do.
        /// </summary>
        /// <param name="roomKey">This character's room key.</param>
        private static float ConversationLineOfSightPhase(int roomKey) =>
            (roomKey & 0x7FFFFFFF) % 1000 * 0.001f;

        /// <summary>
        ///     This character's identity in the room. Derived the same way
        ///     <c>ConvaiCharacterGazeRegistry.Entry.Key</c> derives its own, so a character that
        ///     also publishes a Character Target is one participant and not two — and a character
        ///     that publishes nothing is still in the room, because the rest of it can still hear
        ///     them talk.
        /// </summary>
        /// <remarks>
        ///     0 means nobody and -1 means the player, so a hash landing on either is nudged off
        ///     it. A hash is arbitrary, so nudging one costs nothing.
        /// </remarks>
        private int ResolveConversationRoomKey(Transform root)
        {
            if (_conversationRoomKey != 0) return _conversationRoomKey;
            if (root == null) return 0;

            int key = ConvaiObjectId.Of(root).GetHashCode();
            if (key == 0 || key == ConversationRoomModel.PlayerKey) key = 1;
            _conversationRoomKey = key;
            return key;
        }

        /// <summary>
        ///     The character the player is addressing, as a room key. 0 when nothing says who that
        ///     is, or when the addressed character publishes no gaze target and so is not somebody
        ///     the room can point at.
        /// </summary>
        private static int ResolveAddresseeRoomKey()
        {
            ConvaiCharacter addressed = ConvaiManager.ActiveManager != null
                ? ConvaiManager.ActiveManager.AddressedCharacter
                : null;
            if (addressed == null) return 0;

            return ConvaiCharacterGazeRegistry.TryGetByCharacter(
                addressed, out ConvaiCharacterGazeRegistry.Entry entry, out _)
                ? entry.Key
                : 0;
        }

        /// <summary>How many decay constants of quiet end a conversation, for the idle-life gate.</summary>
        private const float ConversationLiveDecayMultiple = 2f;

        /// <summary>
        ///     Conversation gaze: while the character is not in its own turn, follow the
        ///     conversation happening around it — whoever is talking, whoever is being talked to,
        ///     whoever the room is waiting to hear from.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The director decides; this method only says who this character is, where it
        ///         stands and where it is already looking, and folds the answer into this tick's
        ///         <paramref name="statePolicy" />. Everything after that is the ordinary
        ///         pipeline: the arbiter picks the person (the player anchor when the look is on
        ///         the player, the character's own candidate when it is not), commitment ramps in,
        ///         aversion and face scanning run, and release is the normal engagement unwind.
        ///     </para>
        ///     <para>
        ///         Attention never lowers anything the state already asked for — it raises
        ///         engagement and head participation to at least what a listener needs and leaves
        ///         a stronger authored policy alone.
        ///     </para>
        ///     <para>
        ///         The two narrowings here are the ones the room cannot make for itself. A
        ///         character that does not look at other characters is told so through the mode;
        ///         and a speaker who publishes no Character Target is somebody the room can hear
        ///         but the arbiter has nothing to aim at, so committing to a look at them would be
        ///         committing to a look that never happens.
        ///     </para>
        /// </remarks>
        private void ApplySpeakerAttention(
            ConvaiGazeProfile profile,
            DialogueState state,
            bool locked,
            ref GazeStatePolicy statePolicy,
            float deltaTime)
        {
            ConversationRoomSnapshot room = ConversationRoomModel.Shared.Current;

            // A lock is a promise that the character keeps looking at the player, so the
            // conversation is stood down rather than skipped: the director keeps its account of
            // the room and is ready the moment the lock lifts.
            GazeSpeakerAttention mode = locked ? GazeSpeakerAttention.Off : attendToSpeaker;
            bool charactersAvailable = _characterGaze != null && _characterGaze.isActiveAndEnabled &&
                                       _characterGaze.LookAtOthers;
            if (!charactersAvailable)
                mode = mode switch
                {
                    GazeSpeakerAttention.Anyone => GazeSpeakerAttention.Player,
                    GazeSpeakerAttention.Characters => GazeSpeakerAttention.Off,
                    _ => mode
                };

            Transform root = Context != null && Context.CharacterRoot != null ? Context.CharacterRoot : transform;
            Vector3 observerPosition = _chain.IsBound ? _chain.HeadPivotPosition : root.position;
            int roomKey = ResolveConversationRoomKey(root);

            // Who it cannot see. A wall between two characters is as real a reason not to look at
            // somebody as distance is, and until this ran a listener in the next room turned its
            // head — and then its body — toward masonry every time the other one spoke. Measured
            // here, once, from the room's own account of where everybody's head is, and read both
            // by the director below and by the character candidates gathered later this tick.
            if (profile.PlayerLineOfSight)
                _conversationOcclusion.Refresh(
                    in room, roomKey, ResolveRoomHeadPoint(root), root,
                    profile.PlayerObstructionMask, ConversationLineOfSightIntervalSeconds,
                    deltaTime, ConversationLineOfSightPhase(roomKey));
            else
                _conversationOcclusion.Clear();

            var self = new ConversationGazeSelf(
                roomKey,
                observerPosition,
                root.forward,
                // Attention layers on top of Idle only: a character in its own turn already has an
                // authored policy row saying how it should look at the person it is talking to.
                state != DialogueState.Idle,
                mode,
                CurrentAimYawDegrees(root, observerPosition),
                CurrentAimRoomKey(),
                // Wrapping up: settling after speech, or the floor-yield beat that marks the end
                // of the answer. A speaker holds its addressee through both.
                turnEnding: state == DialogueState.Settling || _turnTaking.YieldEngagementPinActive,
                occluded: _conversationOcclusion,
                // A scripted look already owns the gaze — a curiosity glance at the player, an
                // authored LookAt, an action's look point. The conversation still decides who is
                // worth attending; its own idle glances stand aside rather than booking a beat
                // the scripted stack would outrank anyway. Resolving here prunes nothing that the
                // resolve later in the tick would not prune at the same Time.time.
                scriptedLookActive: _scripted.ResolveActive(Time.time) != null);

            var tuning = new ConversationGazeTuning(
                profile.SpeakerAttentionEngagement,
                profile.SpeakerAttentionHeadContribution,
                profile.SpeakerAttentionAllowBodyTurn,
                profile.SpeakerAttentionMaxAngleDegrees,
                profile.SpeakerAttentionMaxDistance,
                profile.ConversationReactionMedianSeconds,
                profile.ConversationReactionSpread,
                profile.ConversationAttentionDecaySeconds,
                profile.ConversationReactionMinSeparationSeconds,
                profile.SpeakerAttentionAversionStrength,
                profile.EnableAudienceChecks,
                profile.AudienceCheckIntervalMin,
                profile.AudienceCheckIntervalMax,
                profile.AudienceCheckDuration,
                profile.SpeakerAttentionInterruptionSeconds,
                // Idle life between people is authored on the Character Target component, next to
                // everything else about how this character treats the others: the conversation
                // director performs it, but the tuning belongs where a person would look for it.
                enableSocialIdle: charactersAvailable && _characterGaze.EnableIdleGlances,
                socialIdleIntervalMin: _characterGaze != null ? _characterGaze.IdleGlanceIntervalMin : 0f,
                socialIdleIntervalMax: _characterGaze != null ? _characterGaze.IdleGlanceIntervalMax : 0f,
                socialIdleDuration: _characterGaze != null ? _characterGaze.IdleGlanceDuration : 0f,
                socialIdleEngagement: _characterGaze != null ? _characterGaze.IdleGlanceEngagement : 0f,
                // How far this character can look without turning its body. Every beat the
                // director issues as a glance has already decided the body stays out of it, so
                // it needs the same two numbers the ladder uses to know what that costs.
                headComfortYawDegrees: profile.HeadComfortYawDegrees,
                eyeComfortDegrees: profile.EyeComfortDegrees);

            ConversationGazeFocus previousFocus = _conversation.Current.Focus;
            ConversationGazeState attention = _conversation.Tick(in room, in self, in tuning, deltaTime, ref _random);

            _conversationLive = room.IsValid &&
                                room.SilenceSeconds < tuning.DecaySeconds * ConversationLiveDecayMultiple;

            _attentionCharacterKey = ResolveAttentionAnchor(in attention);
            if (!attention.Active || _conversationAnchorMissing) return;

            // A character glancing round its own audience keeps every promise its dialogue state
            // made. That row still says how it looks at the person it is talking to, and it is
            // right; all this beat borrows is the eyes. Standing the player anchor aside for the
            // length of the glance is what lets the arbiter reach the listener being checked —
            // the character was published as fully relevant a moment ago, and the player anchor
            // is the only thing that outranks it.
            if (attention.Look == ConversationGazeLook.SpeakerCheck)
            {
                // The speaker lends its eyes to a listener for a beat: the player stops being a
                // candidate so the pulsed character wins, and the head joins only as far as the
                // check's own (eye-led) share allows — the Speaking row's full head contribution
                // would turn the whole head and chest to somebody the character is not talking to.
                statePolicy.AllowPlayerTarget = false;
                statePolicy.HeadContribution = Mathf.Min(statePolicy.HeadContribution, attention.HeadContribution);
                statePolicy.AllowBodyTurn = false;
                if (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.State) &&
                    previousFocus != attention.Focus)
                    _trace.State("Conversation attention → looking round the room while speaking.");
                return;
            }

            statePolicy.Engagement = Mathf.Max(statePolicy.Engagement, attention.Engagement);
            statePolicy.HeadContribution = Mathf.Max(statePolicy.HeadContribution, attention.HeadContribution);
            statePolicy.AllowBodyTurn |= attention.AllowBodyTurn;
            // This is the switch that lets the arbiter see the player candidate at all: the Idle
            // row suppresses it, and following the player's turn is exactly the case where that
            // row is wrong. With the look on another character it stays suppressed so the
            // character candidate wins on its own relevance.
            statePolicy.AllowPlayerTarget = attention.Focus == ConversationGazeFocus.Player;

            if (statePolicy.AversionMode == GazeAversionMode.None && attention.AversionStrength > 0f)
            {
                statePolicy.AversionMode = GazeAversionMode.Natural;
                statePolicy.AversionStrength = attention.AversionStrength;
            }

            if (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.State) && previousFocus != attention.Focus)
                _trace.State($"Conversation attention → {attention.Focus}" +
                             (attention.IsAudienceCheck ? " (audience check)." : "."));
        }

        /// <summary>
        ///     The character this tick's decision points at, and the name diagnostics will use for
        ///     them. Returns 0 for a look at the player, and 0 with
        ///     <see cref="_conversationAnchorMissing" /> raised for somebody the room can hear but
        ///     who publishes no gaze target.
        /// </summary>
        private int ResolveAttentionAnchor(in ConversationGazeState attention)
        {
            _conversationAnchorMissing = false;
            if (!attention.Active || attention.Focus != ConversationGazeFocus.Character) return 0;

            if (ConvaiCharacterGazeRegistry.TryGetByKey(
                    attention.CharacterKey, out ConvaiCharacterGazeRegistry.Entry attended, out _))
            {
                _attendedSpeakerName = attended.DisplayName;
                return attention.CharacterKey;
            }

            _conversationAnchorMissing = true;
            return 0;
        }

        /// <summary>
        ///     Signed yaw from the character's forward to whatever it is already looking at — or
        ///     zero, which is straight ahead, when it is looking at nothing in particular.
        /// </summary>
        private float CurrentAimYawDegrees(Transform root, Vector3 observerPosition) =>
            _directive.HasEngagedTarget
                ? SignedHorizontalAngleTo(root, observerPosition, _directive.WorldPoint)
                : 0f;

        /// <summary>
        ///     Room key of whoever the character is already looking at, or 0 when that is not a
        ///     person. This is what tells the director the difference between a listener that has
        ///     to turn to the new speaker and one whose eyes are already there.
        /// </summary>
        private int CurrentAimRoomKey()
        {
            if (!_directive.HasEngagedTarget) return 0;

            return _directive.Kind switch
            {
                GazeTargetKind.Player => ConversationRoomModel.PlayerKey,
                GazeTargetKind.Character => _attentionCharacterKey,
                _ => 0
            };
        }

        /// <summary>
        ///     Signed angle (degrees) the character would have to turn on the horizontal plane to
        ///     face <paramref name="worldPoint" />. Horizontal only — the attention limit is about
        ///     turning, and a speaker standing on a balcony is not out of reach — and signed,
        ///     because a person 30° to the left and one 30° to the right are not the same
        ///     direction however much the unsigned angle says they are.
        /// </summary>
        /// <param name="root">Character root, whose forward the angle is measured from.</param>
        /// <param name="observerPosition">Where the character is looking from.</param>
        /// <param name="worldPoint">The point being measured.</param>
        private static float SignedHorizontalAngleTo(Transform root, Vector3 observerPosition, Vector3 worldPoint)
        {
            Vector3 forward = root != null ? root.forward : Vector3.forward;
            forward.y = 0f;
            Vector3 toTarget = worldPoint - observerPosition;
            toTarget.y = 0f;
            if (forward.sqrMagnitude <= 1e-6f || toTarget.sqrMagnitude <= 1e-6f) return 0f;

            return Vector3.SignedAngle(forward, toTarget, Vector3.up);
        }

        /// <summary>
        ///     The point this character treats as the player's eyes, resolved the same way the
        ///     eye-contact path resolves it so attention and conversation never disagree about
        ///     where a person is.
        /// </summary>
        private bool TryResolvePlayerPoint(out Vector3 worldPoint)
        {
            PlayerAnchorTargetProvider provider = FindActivePlayerAnchorProvider();
            if (provider != null && provider.TryResolveFocusPoint(out worldPoint)) return true;

            if (TryResolvePlayerAnchor(out Transform anchor) && anchor != null)
            {
                worldPoint = anchor.position;
                return true;
            }

            worldPoint = default;
            return false;
        }

        /// <summary>
        ///     One line saying what conversation attention is actually doing, or which gate
        ///     turned the speaker away. Written for somebody asking "why isn't it looking at
        ///     whoever is talking", so every branch names the thing they would go and change.
        /// </summary>
        internal string DescribeSpeakerAttention()
        {
            if (attendToSpeaker == GazeSpeakerAttention.Off)
                return "Off — this character does not follow other people's turns.";

            ConversationGazeState state = _conversation.Current;
            string speaker = string.IsNullOrEmpty(_attendedSpeakerName) ? "another character" : _attendedSpeakerName;

            if (_conversationAnchorMissing)
                return "Somebody is speaking but there is nothing to look at — they publish no gaze target " +
                       "(add a Character Target component).";

            if (state.Active)
            {
                if (state.IsAudienceCheck)
                    return $"Checking the player while {speaker} speaks.";

                if (state.Look == ConversationGazeLook.SpeakerCheck)
                    return "Looking round the room while it speaks — its own dialogue state still owns the look.";

                string who = state.Focus == ConversationGazeFocus.Player ? "the player" : speaker;

                if (state.Look == ConversationGazeLook.SocialIdle)
                    return $"Nobody is talking — glancing at {who}.";

                if (state.Look == ConversationGazeLook.Reflex)
                    return $"{who} is talking over whoever has the floor — the eyes have gone, the head has not.";

                return state.IsHolding
                    ? $"Still on {who} — their turn just ended."
                    : $"Watching {who}.";
            }

            if (_focusActive)
                return "Bypassed — an eye-contact lock is holding this character on the player.";

            return state.Reason switch
            {
                ConversationGazeStandDown.OwnTurn => "Standing by — this character is in its own turn.",
                ConversationGazeStandDown.NoSpeaker => "Nobody it attends is speaking.",
                ConversationGazeStandDown.TooFar =>
                    "The speaker is beyond the attention distance in the profile.",
                ConversationGazeStandDown.TooWide =>
                    "The speaker is too far behind this character, and body turns are off for attention.",
                ConversationGazeStandDown.NoAnchor =>
                    "Somebody is speaking but there is nothing to look at — they publish no gaze target " +
                    "(add a Character Target component), or this scene has no player anchor.",
                ConversationGazeStandDown.NoLineOfSight =>
                    "The speaker is behind a wall — this character cannot see them, and " +
                    "\"Won't Look Through Walls\" is on in the profile.",
                ConversationGazeStandDown.Reacting => "Turning to a new speaker.",
                _ => "Off — this character does not follow other people's turns."
            };
        }

        /// <summary>
        ///     Target-loss search: when the player candidate drops out (LOS occlusion or
        ///     range exit) after at least 2 s of continuous engagement, the last known point is
        ///     held and a short burst of searching saccades substitutes for the lost target
        ///     until the search director releases (completion, reacquisition, or a state exit
        ///     to Idle). Substitutes <see cref="_directive" />'s target point/engagement/head
        ///     contribution in place — the same channel every other target already flows
        ///     through — so no new solver seam is needed.
        /// </summary>
        private void TickTargetLossSearch(ConvaiGazeProfile profile, in GazeTargetDecision decision, DialogueState state, float deltaTime)
        {
            // A scripted/glance-tier target (GlanceAt, curiosity, character glance, referential
            // glances, ...) always outranks every provider tier (see GazeTargetArbiter), which
            // means the character's attention has deliberately moved on. Abort outright rather
            // than silently overriding it for the rest of the search — resuming a stale search
            // afterwards would look robotic. decision.IsScripted is the arbiter's own
            // discriminator for "this tick's winner came off the scripted stack".
            if (decision.IsScripted)
            {
                _search.Abort();
                return;
            }

            bool playerValid = TryGetPlayerCandidate(out Vector3 playerPoint);
            bool engagedWithPlayer = _directive.Kind == GazeTargetKind.Player && _directive.HasEngagedTarget;
            bool wasSearching = _search.SearchActive;

            // Same gaze-origin pivot the solver stage uses (ResolvePlayerDistance uses the same
            // fallback) — gives the director a character-relative lateral basis instead of a
            // world axis, so the search reads as sideways regardless of facing direction.
            Vector3 observerPosition = _chain.IsBound ? _chain.HeadPivotPosition : transform.position;

            bool searching = _search.Tick(
                playerValid, engagedWithPlayer, playerPoint, observerPosition, state,
                profile.EnableTargetLossSearch, profile.TargetLossSearchMaxSeconds, deltaTime, ref _random);

            if (!searching) return;

            if (!wasSearching)
                _trace?.State("Player lost — searching last known direction.");

            // Kind is forced (not just left as decision.Kind) because the search can still be
            // active after the arbiter's own loss-hold/commitment decay has fully released the
            // target to None. Target/Name are left as whatever the decision already carries
            // (usually still the player's, mid-decay) so this substitution never reads as a
            // target change to TraceTargetTransitions/TargetChanged — it is the same commitment,
            // just aimed at a searched point instead of the live target position.
            _directive.Kind = GazeTargetKind.Player;
            _directive.WorldPoint = _search.SearchPoint;
            _directive.Engagement = Mathf.Max(_directive.Engagement, SearchEngagementFloor);
            _directive.SettledEngagement = Mathf.Max(_directive.SettledEngagement, SearchEngagementFloor);
            _directive.HeadContribution = _search.HeadContribution;
            _directive.TeleportedThisTick = _directive.TeleportedThisTick || _search.FixationChangedThisTick;
        }

        /// <summary>Finds the player candidate in this tick's gathered list, if any (LOS/range-valid).</summary>
        private bool TryGetPlayerCandidate(out Vector3 point)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                GazeTargetCandidate candidate = _candidates[i];
                if (candidate.Kind == GazeTargetKind.Player && candidate.Relevance > 0f)
                {
                    point = candidate.WorldPoint;
                    return true;
                }
            }

            point = Vector3.zero;
            return false;
        }

        private bool NeedsCurrentTargetNames =>
            TargetChanged != null || (_trace != null && _trace.IsEnabled(GazeTraceVerbosity.State));

        private bool TryGetProviderCandidate(IGazeTargetProvider provider, Transform root, out GazeTargetCandidate candidate) =>
            provider is PlayerAnchorTargetProvider anchor
                ? anchor.TryGetCandidate(root, out candidate, NeedsCurrentTargetNames)
                : provider.TryGetCandidate(root, out candidate);

        private void GatherCandidates(ConvaiGazeProfile profile, float deltaTime)
        {
            _candidates.Clear();
            Transform root = Context != null ? Context.CharacterRoot : transform;

            // The path ahead, offered like any other candidate so the arbiter's acquisition ramp,
            // interest budget and point smoothing all apply to it unchanged.
            if (_travel.TryBuildPathCandidate(
                    in _travelIntent, root, ResolveEyeHeight(root), profile, deltaTime,
                    out GazeTargetCandidate pathCandidate))
            {
                _candidates.Add(pathCandidate);
            }

            for (int i = 0; i < _providers.Count; i++)
            {
                IGazeTargetProvider provider = _providers[i];
                if (provider == null) continue;
                if (!_focusActive && _ownedPlayerAnchorFocusOnly &&
                    ReferenceEquals(provider, _ownedPlayerAnchor)) continue;
                // provider is interface-typed, so "== null" above is plain reference equality
                // and does not catch a destroyed-but-not-yet-collected Behaviour (Unity's
                // "fake null"); behaviour's static type is UnityEngine.Object-derived, so its
                // own "== null" correctly detects that case before touching isActiveAndEnabled.
                if (provider is Behaviour behaviour && (behaviour == null || !behaviour.isActiveAndEnabled)) continue;
                if (TryGetProviderCandidate(provider, root, out GazeTargetCandidate candidate))
                    _candidates.Add(candidate);
            }

            for (int i = 0; i < _runtimeProviders.Count; i++)
            {
                IGazeTargetProvider provider = _runtimeProviders[i];
                if (provider == null) continue;
                if (TryGetProviderCandidate(provider, root, out GazeTargetCandidate candidate))
                    _candidates.Add(candidate);
            }

            IReadOnlyList<WorldObjectGazeTargetProvider> worldObjects =
                WorldObjectGazeTargetProvider.ActiveProviders;
            for (int i = 0; i < worldObjects.Count; i++)
            {
                WorldObjectGazeTargetProvider provider = worldObjects[i];
                if (provider == null) continue;
                if (provider.TryGetCandidate(root, out GazeTargetCandidate candidate))
                    _candidates.Add(candidate);
            }

            // Declarative drag-drop gaze targets (no scene metadata required).
            IReadOnlyList<ConvaiGazeTarget> gazeTargets = ConvaiGazeTarget.ActiveTargets;
            for (int i = 0; i < gazeTargets.Count; i++)
            {
                ConvaiGazeTarget target = gazeTargets[i];
                if (target == null) continue;
                if (target.TryGetCandidate(root, out GazeTargetCandidate candidate))
                    _candidates.Add(candidate);
            }

            // Character-to-character mutual gaze: one candidate per other registered
            // character. Speakers are fully relevant (listeners turn to them); idle
            // characters are low-relevance and cycle through the arbiter's interest budget.
            if (_characterGaze != null && _characterGaze.isActiveAndEnabled && _characterGaze.LookAtOthers)
            {
                IReadOnlyList<ConvaiCharacterGazeRegistry.Entry> others = ConvaiCharacterGazeRegistry.All;
                // While the conversation has named somebody, nobody else is on the table: the
                // arbiter scores candidates without knowing a decision was made, so a nearer
                // bystander could otherwise take the look the character chose. The attended
                // person still has to earn it here — if their candidate cannot be built this
                // tick, no character candidate is offered at all and the arbiter's target-loss
                // hold carries the look, rather than a substitute arriving in their place.
                ConversationGazeState attention = _conversation.Current;
                for (int i = 0; i < others.Count; i++)
                {
                    ConvaiCharacterGazeRegistry.Entry other = others[i];
                    if (other == null) continue;
                    if (!ConversationCandidateFilter.ShouldOffer(
                            GazeTargetKind.Character, other.Key, in attention)) continue;

                    if (_characterGaze.TryBuildCandidate(
                            root, other, out GazeTargetCandidate candidate, _attentionCharacterKey,
                            _conversationOcclusion))
                        _candidates.Add(candidate);
                }
            }
        }

        private void PublishReading(ConvaiGazeProfile profile, in GazeTargetDecision decision)
        {
            if (_directive.HasEngagedTarget)
            {
                Current = new GazeReading(
                    _directive.Kind,
                    _directive.Target,
                    _directive.WorldPoint,
                    _directive.Engagement,
                    _aversion.IsAverting,
                    _directive.GenerationId);
                return;
            }

            if (profile.EnableAmbientExploration)
            {
                Current = new GazeReading(
                    GazeTargetKind.Ambient,
                    null,
                    ResolveRestPoint(),
                    0f,
                    isAverting: false,
                    decision.GenerationId);
                return;
            }

            Current = GazeReading.None;
        }

        private void TraceTargetTransitions(in GazeTargetDecision decision)
        {
            GazeTargetKind kind = _directive.HasEngagedTarget ? _directive.Kind : GazeTargetKind.None;
            string name = _directive.HasEngagedTarget ? _directive.TargetName : "-";

            if (_diagnostics.TryReportTargetTransition(
                    _trace, kind, name, decision.GenerationId, decision.TeleportedThisTick,
                    Time.time, out GazeTargetChange change))
                TargetChanged?.Invoke(change);
        }

        private Vector3 ResolveRestPoint()
        {
            Transform reference = Context?.RigBinding?.Root != null ? Context.RigBinding.Root : transform;
            Vector3 forward = reference.forward.sqrMagnitude > 1e-6f ? reference.forward.normalized : Vector3.forward;

            if (Context?.RigBinding != null &&
                Context.RigBinding.TryGetBone(StandardBone.Head, out Transform head) &&
                head != null)
            {
                return head.position + forward * 2f;
            }

            return reference.position + Vector3.up * 1.6f + forward * 2f;
        }

        /// <summary>Whether any cached renderer is currently visible to a camera (E10 LOD gate).</summary>
        private bool AnyRendererVisible() => EvaluateRendererVisibility(_renderers);

        /// <summary>
        ///     Whether the crowd-LOD governor should treat this character as on-screen, given its
        ///     cached renderer array. Pure and static so the stale-cache cases are unit-testable
        ///     without a camera or a rig.
        /// </summary>
        /// <remarks>
        ///     Both "nothing cached yet" (the rig is still being set up) and "everything cached was
        ///     destroyed" (a mesh swap the rebind hook did not see) report <c>true</c>. They are the
        ///     same situation from the governor's point of view — the cache cannot answer the
        ///     question — and answering <c>false</c> instead would silently pin the character to the
        ///     off-screen tier forever, which reads as gaze half-dying for no visible reason.
        /// </remarks>
        internal static bool EvaluateRendererVisibility(SkinnedMeshRenderer[] renderers)
        {
            if (renderers == null || renderers.Length == 0) return true;

            bool anyAlive = false;
            for (int i = 0; i < renderers.Length; i++)
            {
                if (renderers[i] == null) continue;
                anyAlive = true;
                if (renderers[i].isVisible) return true;
            }

            return !anyAlive;
        }

        /// <summary>Distance from the character's head pivot to the player proxy (main camera) for LOD.</summary>
        private float ResolvePlayerDistance()
        {
            Vector3 point;
            PlayerAnchorTargetProvider provider = FindActivePlayerAnchorProvider();
            if (provider != null && provider.TryResolveFocusPoint(out point))
            {
                Vector3 providerHead = _chain.IsBound ? _chain.HeadPivotPosition : transform.position;
                return Vector3.Distance(providerHead, point);
            }

            Transform anchor = playerAnchorOverride != null
                ? playerAnchorOverride
                : Camera.main != null ? Camera.main.transform : null;
            if (anchor == null) return 0f;

            Vector3 head = _chain.IsBound ? _chain.HeadPivotPosition : transform.position;
            return Vector3.Distance(head, anchor.position);
        }

        /// <summary>
        ///     The transform this character treats as the player's eyes, for anything that needs to
        ///     aim at, measure to, or turn toward the player the same way the gaze system does.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Exists because aiming at "the player" and aiming at the player's <em>GameObject</em>
        ///         are not the same thing: a first-person rig's root sits on the floor, so a request
        ///         built from it makes the character stare at the player's feet. This resolves the same
        ///         anchor the eye-contact path uses — <see cref="PlayerAnchorOverride" />, then the
        ///         active <see cref="PlayerAnchorTargetProvider" /> (which itself prefers
        ///         <c>Camera.main</c> and skips render-texture and utility cameras), then
        ///         <c>Camera.main</c> — so scripted requests and conversational eye contact agree on
        ///         where a person is.
        ///     </para>
        ///     <para>
        ///         Public so game code can agree with it too. A behavior that measures how near the
        ///         player is, or turns to face them, and resolves the player its own way will disagree
        ///         with this character's gaze the moment a project uses split-screen, a multiplayer
        ///         rig, or a cutscene camera — the eyes follow the assigned anchor while the rest of
        ///         the logic follows something else.
        ///     </para>
        /// </remarks>
        /// <param name="anchor">The player's eye-line transform, when this scene has one.</param>
        /// <returns><c>false</c> when there is no anchor, no provider and no camera to fall back on.</returns>
        public bool TryGetPlayerAnchor(out Transform anchor) => TryResolvePlayerAnchor(out anchor);

        private bool TryResolvePlayerAnchor(out Transform anchor)
        {
            anchor = playerAnchorOverride;
            if (anchor != null) return true;

            PlayerAnchorTargetProvider provider = FindActivePlayerAnchorProvider();
            if (provider != null && provider.TryGetFocusCandidate(out GazeTargetCandidate candidate))
            {
                anchor = candidate.Target;
                return anchor != null;
            }

            if (Camera.main != null)
            {
                anchor = Camera.main.transform;
                return true;
            }
            return false;
        }

        private void RejectScriptedRequestsForExactFocus()
        {
            if (!_scripted.RejectAllForExactFocus()) return;
            _trace?.State("Scripted gaze rejected by Exact focus.");
        }

        private void ResampleLiveTargetPoint()
        {
            if (!ShouldResampleFocusedPlayer(
                    _focusActive, _directive.HasEngagedTarget, _directive.Kind)) return;

            PlayerAnchorTargetProvider anchor = FindActivePlayerAnchorProvider();
            if (anchor != null && anchor.TryResolveFocusPoint(out Vector3 focusPoint))
            {
                _directive.WorldPoint = focusPoint;
                _lastFocusPoint = focusPoint;
                _hasLastFocusPoint = true;
            }
        }

        /// <summary>
        ///     The transform this character treats as "the player". <c>null</c> (default)
        ///     resolves to <c>Camera.main</c> (XR rigs included), then any enabled camera.
        ///     Assign for split-screen, multiplayer, or cutscene rigs — engagement policies,
        ///     body turns, and the dynamic-context bridge all follow the new anchor. Applies
        ///     immediately at runtime; setting it back to <c>null</c> returns to the camera.
        /// </summary>
        public Transform PlayerAnchorOverride
        {
            get => playerAnchorOverride;
            set
            {
                playerAnchorOverride = value;
                ApplyPlayerAnchorOverride(clearWhenNull: true);
            }
        }

        /// <summary>
        ///     How this character's eye contact is governed. <see cref="GazeEyeContactMode.Natural" />
        ///     follows the profile's per-state policy table; <see cref="GazeEyeContactMode.ConversationLock" />
        ///     fully commits to the player anchor (<see cref="PlayerAnchorOverride" /> if set,
        ///     otherwise the main camera) in every conversational (non-Idle) state while Idle
        ///     keeps its authored ambient life; <see cref="GazeEyeContactMode.AlwaysLock" />
        ///     commits in every state including Idle. While a lock is in force the table
        ///     (including any authored aversion) and emotion engagement scaling are bypassed.
        ///     Settable at runtime; takes effect on the next tick, ramping in smoothly like any
        ///     other policy change — no snap. A scripted
        ///     <see cref="GazeAt(Transform,GazeOptions)" /> request outranks Social focus. Exact
        ///     focus rejects it unless <see cref="AllowScriptedOverridesDuringExactFocus" /> is
        ///     enabled; glance-tier requests are absorbed while
        ///     <see cref="LockBlocksGlances" /> is on.
        /// </summary>
        public GazeEyeContactMode EyeContactMode
        {
            get => eyeContactMode;
            set => eyeContactMode = value;
        }

        /// <summary>
        ///     Whether this character turns to whoever else currently holds the floor — the
        ///     "everyone looks at the person talking" behaviour of a group conversation.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         It applies only while the character is <b>not</b> in its own turn, so it never
        ///         competes with the conversational gaze of the character actually being spoken to,
        ///         and it is bypassed entirely while an eye-contact lock
        ///         (<see cref="GazeEyeContactMode.ConversationLock" />,
        ///         <see cref="GazeEyeContactMode.AlwaysLock" />) is in force — a lock is a promise
        ///         that the character keeps looking at the player.
        ///     </para>
        ///     <para>
        ///         Attending another character requires that character to publish itself through a
        ///         <see cref="Providers.CharacterGazeTargetProvider" />. Distance, angle,
        ///         reaction delay and how committed the look is are tuned in the profile's
        ///         Conversation Attention group, so one shared profile governs a whole cast.
        ///         Settable at runtime; takes effect on the next tick.
        ///     </para>
        /// </remarks>
        public GazeSpeakerAttention AttendToSpeaker
        {
            get => attendToSpeaker;
            set => attendToSpeaker = value;
        }

        /// <summary>
        ///     Whether the character is following somebody else's turn right now, and who it is
        ///     watching. Diagnostics/tooling seam — the Live panel and
        ///     <c>Convai_DiagnoseGaze</c> report the effective behaviour from here.
        /// </summary>
        internal ConversationGazeState ConversationGaze => _conversation.Current;

        /// <summary>
        ///     Precision used while <see cref="EyeContactMode" /> is active. Social preserves
        ///     subtle fixation life; Exact suppresses intentional offsets without freezing blinks,
        ///     eyelids, pupils, vergence, or anatomical body turns.
        /// </summary>
        public GazeFocusFidelity FocusFidelity
        {
            get => focusFidelity;
            set => focusFidelity = value;
        }

        /// <summary>How the player anchor's conversational aim point is derived.</summary>
        public GazeAnchorAimMode PlayerAnchorAimMode
        {
            get => playerAnchorAimMode;
            set
            {
                playerAnchorAimMode = value;
                ApplyPlayerAnchorAim(authoredNow: true);
            }
        }

        /// <summary>Anchor-local aim offset used by <see cref="GazeAnchorAimMode.LocalOffset" />.</summary>
        public Vector3 PlayerAnchorAimOffset
        {
            get => playerAnchorAimOffset;
            set
            {
                playerAnchorAimOffset = value;
                ApplyPlayerAnchorAim(authoredNow: true);
            }
        }

        /// <summary>
        ///     Whether explicit <see cref="GazeAt(Transform,GazeOptions)" /> requests may preempt
        ///     an active Exact focus. Disabled by default; Social focus always permits them.
        /// </summary>
        public bool AllowScriptedOverridesDuringExactFocus
        {
            get => allowScriptedOverridesDuringExactFocus;
            set => allowScriptedOverridesDuringExactFocus = value;
        }

        /// <summary>
        ///     While an eye-contact lock is in force (see <see cref="EyeContactMode" />),
        ///     absorbs glance-tier scripted requests — <see cref="GlanceAt(Transform, float)" />
        ///     and everything built on it, such as referential glances — so nothing briefly
        ///     pulls gaze off the player anchor. Absorbed handles complete immediately without
        ///     settling. An explicit <see cref="GazeAt(Transform,GazeOptions)" /> preempts Social
        ///     focus; Exact follows <see cref="AllowScriptedOverridesDuringExactFocus" />. On by
        ///     default; turn off to let glances play through the
        ///     lock. Has no effect in <see cref="GazeEyeContactMode.Natural" /> mode.
        /// </summary>
        public bool LockBlocksGlances
        {
            get => lockBlocksGlances;
            set => lockBlocksGlances = value;
        }

        private void EnsurePlayerAnchorIfNeeded()
        {
            if (!ShouldProvisionPlayerAnchor(
                    autoCreatePlayerAnchor,
                    FindActivePlayerAnchorProvider() != null,
                    _providers.Count,
                    _runtimeProviders.Count,
                    focusActive: false)) return;
            if (Context == null || !UnityEngine.Application.isPlaying) return;

            CreateOwnedPlayerAnchor("No gaze target provider found — auto-provisioned a Gaze Player Anchor.");
        }

        /// <summary>
        ///     Pushes <see cref="playerAnchorOverride" /> into the character's player-anchor
        ///     provider, provisioning one when the override needs a carrier. With
        ///     <paramref name="clearWhenNull" /> false (enable-time sync) a null override
        ///     leaves a user-added provider's own Explicit Anchor untouched.
        /// </summary>
        private void ApplyPlayerAnchorOverride(bool clearWhenNull)
        {
            if (playerAnchorOverride == null && !clearWhenNull) return;

            PlayerAnchorTargetProvider provider = FindPlayerAnchorProvider();
            if (provider == null && playerAnchorOverride != null &&
                Context != null && UnityEngine.Application.isPlaying && isActiveAndEnabled)
            {
                provider = CreateOwnedPlayerAnchor(
                    "Player anchor override set — provisioned a Gaze Player Anchor to carry it.");
            }

            if (provider != null)
            {
                provider.ExplicitAnchor = playerAnchorOverride;
                ApplyPlayerAnchorAim(provider);
            }
        }

        internal static bool ShouldProvisionPlayerAnchor(
            bool autoCreate,
            bool hasPlayerProvider,
            int providerCount,
            int runtimeProviderCount,
            bool focusActive)
        {
            if (!autoCreate || hasPlayerProvider) return false;
            return focusActive || (providerCount <= 0 && runtimeProviderCount <= 0);
        }

        internal static bool ShouldUseFocusedPlayerCandidates(bool focusActive, bool hasScriptedWinner) =>
            focusActive && !hasScriptedWinner;

        internal static bool ShouldResampleFocusedPlayer(
            bool focusActive,
            bool hasEngagedTarget,
            GazeTargetKind kind) =>
            focusActive && hasEngagedTarget && kind == GazeTargetKind.Player;

        internal static bool ShouldResetArbiterForMissingFocus(
            bool focusActive,
            int focusCandidateCount,
            bool hasLastFocusPoint) =>
            focusActive && focusCandidateCount == 0 && !hasLastFocusPoint;

        private void ApplyPlayerAnchorAim(bool authoredNow = false) =>
            ApplyPlayerAnchorAim(FindPlayerAnchorProvider(), authoredNow);

        /// <summary>
        ///     Pushes this controller's aim settings onto <paramref name="provider" />.
        /// </summary>
        /// <param name="provider">The anchor to configure. Null is a no-op.</param>
        /// <param name="authoredNow">
        ///     Whether the caller is an act of authoring — someone assigning
        ///     <see cref="PlayerAnchorAimMode" /> or <see cref="PlayerAnchorAimOffset" /> — as
        ///     opposed to the enable-time pass that only replays serialized values.
        /// </param>
        /// <remarks>
        ///     An anchor you placed yourself owns its own aim settings, so the enable-time pass
        ///     leaves it alone unless this controller carries a non-default aim of its own. Without
        ///     that rule, adding a Gaze component next to a hand-configured anchor would silently
        ///     revert it to Auto. Assigning the property is different: choosing Auto there is a
        ///     decision, not an absent one, so it pushes through and the anchor follows.
        /// </remarks>
        private void ApplyPlayerAnchorAim(PlayerAnchorTargetProvider provider, bool authoredNow = false)
        {
            if (provider == null) return;

            bool carriesOwnAim = playerAnchorAimMode != GazeAnchorAimMode.Auto ||
                                 playerAnchorAimOffset != Vector3.zero;
            if (provider != _ownedPlayerAnchor && !authoredNow && !carriesOwnAim)
                return;

            provider.AimMode = playerAnchorAimMode;
            provider.LocalAimOffset = playerAnchorAimOffset;
        }

        private PlayerAnchorTargetProvider FindPlayerAnchorProvider()
        {
            for (int i = 0; i < _providers.Count; i++)
            {
                if (_providers[i] is PlayerAnchorTargetProvider provider)
                    return provider;
            }

            return _ownedPlayerAnchor;
        }

        private PlayerAnchorTargetProvider FindActivePlayerAnchorProvider()
        {
            for (int i = 0; i < _providers.Count; i++)
            {
                if (_providers[i] is PlayerAnchorTargetProvider provider &&
                    IsUsableFocusProvider(provider))
                    return provider;
            }

            return IsUsableFocusProvider(_ownedPlayerAnchor)
                ? _ownedPlayerAnchor
                : null;
        }

        internal static bool IsUsableFocusProvider(PlayerAnchorTargetProvider provider) =>
            provider != null && provider.isActiveAndEnabled;

        private PlayerAnchorTargetProvider CreateOwnedPlayerAnchor(string traceMessage)
        {
            GameObject owner = Context.CharacterRoot != null ? Context.CharacterRoot.gameObject : gameObject;
            _ownedPlayerAnchor = owner.AddComponent<PlayerAnchorTargetProvider>();
            ConfigureOwnedPlayerAnchor();
            ApplyPlayerAnchorAim(_ownedPlayerAnchor);
            RefreshProviders();
            _trace?.Detail(traceMessage);
            return _ownedPlayerAnchor;
        }

        private void ConfigureOwnedPlayerAnchor()
        {
            if (_ownedPlayerAnchor == null) return;

            ConvaiGazeProfile p = EffectiveProfile;
            if (p == null) return;
            _ownedPlayerAnchor.Configure(
                p.PlayerMaxDistance, p.PlayerFullRelevanceDistance,
                p.PlayerLineOfSight, p.PlayerObstructionMask);
        }

        private void DestroyOwnedPlayerAnchor()
        {
            if (_ownedPlayerAnchor == null) return;

            PlayerAnchorTargetProvider provider = _ownedPlayerAnchor;
            _ownedPlayerAnchor = null;
            _ownedPlayerAnchorFocusOnly = false;
            _providers.Remove(provider);

            if (UnityEngine.Application.isPlaying)
                Destroy(provider);
            else
                DestroyImmediate(provider);
        }

        private void HandleRigBindingChanged(IStandardRigBinding rigBinding)
        {
            _chain.RestoreEyeRest();
            Context?.EnsureCompositor()?.ClearLayer(this, FacialBlendshapeLayers.Eyes);
            _headTorso.Reset();
            _eyes.Reset();
            _chain.Bind(Context, transform);
            _eyeWriter.Bind(Context?.EnsureRigBinding());
            ResolveEyeBackend(EffectiveProfile);

            // A mesh swap that comes with a rig rebind destroys every cached renderer, so the
            // crowd-LOD visibility check must re-resolve here or it reads a dead array.
            RefreshRendererCache(Context != null ? Context.CharacterRoot : transform.root);

            // The one-clear-error contract has to survive a rebind: a new binding without a Head
            // mapping stops gaze just as dead as a missing one at startup did.
            ValidateRig();

            _trace?.State("Rig binding changed — gaze chain recalibrated.");
        }

        private void EnsureRuntimeInitialized()
        {
            if (_runtimeInitialized) return;

            ConvaiGazeProfile p = EffectiveProfile;
            if (p == null) return;

            _trace ??= new GazeTrace(name);
            _trace.Verbosity = p.TraceVerbosity;

            _chain.Bind(Context, transform);
            _eyeWriter.Bind(Context?.EnsureRigBinding());
            ResolveEyeBackend(p);
            _blink.Reset(p, ref _random);
            _micro.Reset(ref _random);
            _faceScan.Reset();
            ValidateRig();

            _trace.State(
                $"Gaze runtime initialized. ambient={p.EnableAmbientExploration} faceScan={p.EnableFaceScan} " +
                $"vergence={p.EnableVergence} blink={p.EnableBlink} bodyTurn={p.EnableBodyTurn} " +
                $"emotionModulation={p.EnableEmotionModulation} eyes={p.EyeActuationMode} " +
                $"eyeBackend={(_useEyeBones ? "bones" : _useLookShapes ? "blendshapes" : "disabled")} " +
                // The optional capabilities join the existing init trace rather than adding a
                // second line, so one support log answers "what did this character actually have?"
                $"extras=[{GazeCapabilities.DescribeActive(Context != null ? Context.CharacterRoot : transform.root)}]");

            _runtimeInitialized = true;
        }

        private void ResolveEyeBackend(ConvaiGazeProfile p)
        {
            _useEyeBones = false;
            _useLookShapes = false;
            if (p == null) return;

            switch (p.EyeActuationMode)
            {
                case GazeEyeActuationMode.Auto:
                    _useEyeBones = _chain.HasEyeBones;
                    _useLookShapes = !_useEyeBones && _eyeWriter.HasLookShapes;
                    if (!_useEyeBones && !_useLookShapes)
                        _trace?.Warning(
                            "No eye bones and no EyeLook* blendshapes were resolved — the eye stage is " +
                            "disabled. Head/torso gaze still runs. Check the rig convention mapping.");
                    break;

                case GazeEyeActuationMode.Bones:
                    _useEyeBones = _chain.HasEyeBones;
                    if (!_useEyeBones)
                        _trace?.Warning("Eye backend forced to Bones but no LeftEye/RightEye bone pair was resolved.");
                    break;

                case GazeEyeActuationMode.Blendshapes:
                    _useLookShapes = _eyeWriter.HasLookShapes;
                    if (!_useLookShapes)
                        _trace?.Warning("Eye backend forced to Blendshapes but no EyeLook* shapes were resolved.");
                    break;

                case GazeEyeActuationMode.Disabled:
                    break;
            }
        }

        /// <summary>
        ///     Reports an unusable rig exactly once per distinct binding. Called on first
        ///     initialization and on every rig rebind, because a runtime rebind can break a rig
        ///     that was fine at startup — but a rebind loop must not turn one clear error into a
        ///     per-frame console flood, so the last-reported binding is latched.
        /// </summary>
        private void ValidateRig()
        {
            if (_trace == null) return;

            IStandardRigBinding rigBinding = Context?.EnsureRigBinding();
            bool usable = rigBinding != null &&
                          rigBinding.TryGetBone(StandardBone.Head, out Transform head) && head != null;

            if (!ShouldReportRigWarning(usable, rigBinding, _rigWarningBinding, _rigWarningReported))
            {
                // A usable rig also clears the latch, so a rig that breaks again later still reports.
                if (usable)
                {
                    _rigWarningBinding = null;
                    _rigWarningReported = false;
                }
                return;
            }

            _rigWarningBinding = rigBinding;
            _rigWarningReported = true;

            _trace.Warning(rigBinding == null
                ? "No semantic rig binding could be resolved. Add StandardRigBinding to the character " +
                  "root and map Head (plus optional Neck/Eyes); gaze stays inert until a binding exists."
                : "Rig binding has no semantic Head mapping. Assign Head in StandardRigBinding or use " +
                  "a recognized bone name; head/eye gaze stays inert until Head resolves.");
        }

        /// <summary>
        ///     The log-once decision for <see cref="ValidateRig" />: warn when the rig is unusable
        ///     and this exact binding has not already been reported. Pure and static so the
        ///     rebind-loop cases are unit-testable without a rig.
        /// </summary>
        /// <remarks>
        ///     A rebind to a <em>different</em> broken binding warns again — it is genuinely new
        ///     information. A rebind to the same one does not, which is what keeps a rebind loop
        ///     from turning the one clear error into a per-frame console flood.
        /// </remarks>
        internal static bool ShouldReportRigWarning(
            bool usable, IStandardRigBinding current, IStandardRigBinding lastReported, bool alreadyReported)
        {
            if (usable) return false;
            return !alreadyReported || !ReferenceEquals(lastReported, current);
        }
    }
}
