using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Core.Gestures;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Core.Pose;
using Convai.Modules.BodyLanguage.Core.Signals;
using Convai.Modules.BodyLanguage.Data;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Permanent zero-allocation gate: the full Cognition-path POCO set (signal analyzer, policy engine,
    ///     directors, emotion modulator) plus the solver math updates (posture/breath) must not
    ///     allocate managed memory in steady state — a neutral emotion reading and an alternating
    ///     Idle/Speaking dialogue state, which is the realistic everyday tick.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Warms up first, validates the allocation-event reader with a retained positive control,
    ///         then measures the same larger loop twice on the current thread. Only the second
    ///         batch is asserted, after any remaining first-touch initialization in the first batch.
    ///         Zero allocation events establish the existing zero-managed-byte contract.
    ///     </para>
    ///     <para>
    ///         Scope note: <see cref="EmotionBodyModulator" /> short-circuits to identity on a
    ///         neutral reading, and for an ACTIVE emotion re-blends only when the published
    ///         reading changes, so both everyday steady states — no emotion, and a held emotion —
    ///         are covered by this gate. The one remaining allocation
    ///         (<see cref="EmotionReading.CopyScoresTo" /> boxing an enumerator through its
    ///         <c>IReadOnlyDictionary</c>-typed source) is paid only on a reading CHANGE, which
    ///         is event-cadence by construction: publishing new score content requires
    ///         constructing a new <see cref="EmotionReading" />, which itself copies a dictionary.
    ///     </para>
    /// </remarks>
    public sealed class BodyLanguageZeroAllocTests
    {
        private const float Dt = 1f / 60f;
        private const int WarmupIterations = 2000;
        private const int MeasuredIterations = 2000;

        private SpeechPulseAnalyzer _analyzer;
        private BodyLanguagePolicyEngine _policy;
        private EmotionBodyModulator _emotionModulator;
        private ReactionDirector _reactionDirector;
        private PostureDirector _postureDirector;
        private BreathingDirector _breathingDirector;
        private HeadGestureDirector _headGestureDirector;
        private FidgetDirector _fidgetDirector;
        private ListeningPostureDirector _listeningPostureDirector;
        private StanceDirector _stanceDirector;
        private PosturalSwayDirector _swayDirector;
        private PostureSolver _postureSolver;
        private BreathSolver _breathSolver;
        private AnimatedBreathMotionEstimator _breathMotionEstimator;
        private HandMicroSolver _handMicroSolver;
        private ConvaiBodyLanguageProfile _profile;
        private EmotionReading _emotion;

        [SetUp]
        public void SetUp()
        {
            _analyzer = new SpeechPulseAnalyzer();
            _policy = new BodyLanguagePolicyEngine();
            _emotionModulator = new EmotionBodyModulator();
            _reactionDirector = new ReactionDirector();
            _postureDirector = new PostureDirector();
            _breathingDirector = new BreathingDirector();
            _headGestureDirector = new HeadGestureDirector();
            _headGestureDirector.Seed(777);
            _fidgetDirector = new FidgetDirector();
            _fidgetDirector.Seed(4242);
            _listeningPostureDirector = new ListeningPostureDirector();
            _listeningPostureDirector.Seed(24242);
            _stanceDirector = new StanceDirector();
            _stanceDirector.Seed(575757);
            _swayDirector = new PosturalSwayDirector();
            _swayDirector.Seed(919191);
            _postureSolver = new PostureSolver();
            _breathSolver = new BreathSolver();
            _breathSolver.Seed(12345);
            _breathMotionEstimator = new AnimatedBreathMotionEstimator();
            // Unbound by design here (no scene/Animator in this POCO-only gate): exercises the
            // realistic degraded steady state (no Humanoid avatar) every controller LateUpdate
            // hits — HandMicroSolver's own bound-rig zero-alloc case lives in HandMicroSolverTests.
            _handMicroSolver = new HandMicroSolver();
            _profile = ConvaiBodyLanguageProfile.CreateDefault();
            _emotion = EmotionReading.Neutral;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void RunOneTick(int i)
        {
            float energy = 0.5f + 0.5f * Mathf.Sin(i * 0.13f);
            _analyzer.Step(energy, Dt, out SpeechPulse pulse);

            DialogueState state = (i % 8 == 0) ? DialogueState.Speaking : DialogueState.Idle;
            BodyLanguageStatePolicy statePolicy = _profile.GetPolicy(state);
            _policy.Tick(in statePolicy, _profile.PolicyTransitionSeconds, Dt);

            // The reading is held constant per test (neutral, or one active emotion): both are
            // realistic steady states, exercising the neutral short-circuit and the held-emotion
            // identity cache respectively — neither may allocate. The blend itself (which pays
            // one CopyScoresTo enumerator box) runs only on a reading CHANGE and is covered for
            // correctness by EmotionBodyModulatorTests.
            _emotionModulator.Tick(_profile, in _emotion);

            // Reactions — ticked alongside the emotion modulator every Cognition
            // tick. A periodic scripted trigger exercises BOTH steady states this director sits
            // in: idle (no active envelope) and mid-envelope (an active flinch/bounce advancing),
            // mirroring the head-gesture director's own periodic-trigger coverage above.
            if (i % 130 == 0)
                _reactionDirector.TryTrigger(ReactionKind.SurpriseFlinch, 0.5f, bypassRefractory: true);
            _reactionDirector.Tick(in _emotion, Dt);

            BodyLanguageStatePolicy smoothed = _policy.Current;
            _postureDirector.Tick(in smoothed, _emotionModulator, _profile.PostureTargetSlewSeconds, Dt);
            _breathingDirector.Tick(
                in smoothed, _emotionModulator, _profile.PostureTargetSlewSeconds, Dt,
                energy, state == DialogueState.Speaking);

            // Speech-coupled breathing: mirrors the controller's post-pulse
            // call — SpeechPulse is a struct and TryTriggerSpeechGapInhale takes only primitives,
            // so this must stay allocation-free alongside everything else in this measured loop.
            // Only meaningful while Speaking, matching the controller's own state gate.
            if (state == DialogueState.Speaking)
                _breathingDirector.TryTriggerSpeechGapInhale(pulse.Kind, pulse.Strength, conservativeMode: i % 30 == 0);

            // Keep a gesture request queued roughly every 90 ticks so the measured loop covers
            // BOTH steady states this director can sit in: idle (no active/pending program) and
            // mid-program (an envelope actively advancing) — both must stay allocation-free.
            if (i % 90 == 0)
                _headGestureDirector.TryRequest(HeadGestureKind.Nod, 1f);
            _headGestureDirector.Tick(
                Dt,
                _profile.HeadGestureNodMaxPitchDegrees,
                _profile.HeadGestureShakeMaxYawDegrees,
                _profile.HeadGestureTiltMaxRollDegrees,
                _profile.HeadGestureRefractorySeconds,
                _profile.HeadGestureRefractoryVarianceSeconds);

            // Alternates Listening in with Idle/Speaking so the measured loop covers the
            // listening-posture engage/decay/tilt-cadence path too, not just the fidget path.
            // Resolve the LISTENING policy directly for this director (not the smoothed
            // Idle/Speaking policy) — otherwise ListeningPostureEnabled stays false on the
            // Listening ticks and the director never leaves its disengaged early-return, so the
            // engaged lean-in slew + tilt-cadence sampling (where any allocation would hide) goes
            // unmeasured.
            DialogueState listeningState = (i % 5 == 0) ? DialogueState.Listening : state;
            BodyLanguageStatePolicy listeningPolicy = _profile.GetPolicy(listeningState);
            _listeningPostureDirector.Tick(
                listeningState,
                listeningPolicy.ListeningPostureEnabled,
                listeningPolicy.ListeningLeanIn,
                gazeIsAverting: false,
                Dt,
                _profile.ListeningTiltCadenceSeconds,
                _profile.ListeningTiltIntensity);
            if (_listeningPostureDirector.WantsTiltHold)
                _headGestureDirector.TryRequest(HeadGestureKind.Tilt, _listeningPostureDirector.TiltHoldIntensity);

            _fidgetDirector.Tick(
                listeningState,
                smoothed.FidgetsEnabled,
                smoothed.FidgetRate,
                GestureSuppression.None,
                _listeningPostureDirector.StillnessFactor,
                Dt,
                _profile.FidgetGapSeconds,
                _profile.FidgetEaseSeconds,
                _profile.FidgetHoldSeconds);

            // Stance + postural sway — exercised alongside every
            // other Cognition-tick director in this steady-state measured loop.
            _stanceDirector.Tick(
                listeningState,
                _profile.EnableWeightShifts,
                GestureSuppression.None,
                _profile.WeightShiftIntervalSeconds,
                _profile.WeightShiftIntervalVarianceSeconds,
                _profile.WeightShiftTransferSeconds,
                Dt);
            _swayDirector.Tick(_profile.EnableAmbientSway, smoothed.AmbientDrift, Dt);

            // Mirrors the controller's LateUpdate frame protocol: posture then breath (the
            // shared compositor's own BeginFrame/ApplyAccumulated write path is exercised
            // separately by ProceduralPoseCompositorTests' zero-alloc gate).
            var postureInput = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = _postureDirector.OpennessTarget,
                SustainedLeanTarget = _postureDirector.LeanTarget,
                TransientLeanTarget = _listeningPostureDirector.LeanInBias,
                TensionTarget = _postureDirector.TensionTarget,
                LateralShiftTarget = _fidgetDirector.WeightShiftValue,
                MasterWeight = 1f,
                SuppressionWeight = 1f,
                OpennessSustainFloor = 1f,
                LeanSustainFloor = 1f,
                TensionSustainFloor = 1f,
                MaxOpennessDegrees = _profile.MaxOpennessDegrees,
                MaxLeanDegrees = _profile.MaxLeanDegrees,
                MaxTensionDegrees = _profile.MaxTensionDegrees,
                MaxLateralShiftDegrees = _profile.MaxLateralShiftDegrees,
                SpringSharpness = _profile.PostureSpringSharpness,
                MaxAngularSpeedDegreesPerSecond = _profile.PostureMaxAngularSpeed
            };
            _postureSolver.Solve(in postureInput);

            var breathInput = new BreathSolveInput
            {
                DeltaTime = Dt,
                RateCpm = _breathingDirector.RateCpm,
                Depth = _breathingDirector.Depth,
                Irregularity = _breathingDirector.Irregularity,
                MasterWeight = 1f,
                MaxChestExpansionDegrees = _profile.MaxBreathChestExpansionDegrees,
                MaxShoulderLiftDegrees = _profile.MaxBreathShoulderLiftDegrees
            };
            _breathSolver.Solve(in breathInput);

            // Adaptive-layering estimator — exercised every tick alongside the
            // other solver-math updates it accompanies in the controller's real frame.
            _breathMotionEstimator.Tick(Quaternion.identity, sampleValid: true, stateDuckWeight: 1f, Dt);

            // Idle hand/wrist micro-life — ticked every LateUpdate alongside the
            // other pose solvers above; unbound here (see SetUp remarks), still must allocate 0.
            _handMicroSolver.MaxFingerCurlDegrees = _profile.MaxFingerCurlDegrees;
            _handMicroSolver.MaxWristMicroDegrees = _profile.MaxWristMicroDegrees;
            _handMicroSolver.Tick(i * Dt, 1f, Dt);
        }

        [Test]
        public void FullCognitionAndSolverPath_AllocatesNothingInSteadyState()
        {
            _emotion = EmotionReading.Neutral;

            AssertSteadyStateAllocatesNothing("with a neutral emotion reading");
        }

        [Test]
        public void FullCognitionAndSolverPath_AllocatesNothingWhileHoldingAnActiveEmotion()
        {
            // One reading instance held across every tick — exactly how the Emotion module
            // publishes a held emotion (a new reading is only constructed when the composed
            // state changes). The modulator's identity cache must keep this allocation-free.
            var scores = new System.Collections.Generic.Dictionary<string, float>
            {
                { "joy", 0.7f },
                { "anger", 0.3f }
            };
            _emotion = new EmotionReading("joy", 0.7f, scores, 0f, 1f);

            AssertSteadyStateAllocatesNothing("while holding an active blended emotion");
        }

        private void AssertSteadyStateAllocatesNothing(string scenario)
        {
            for (int i = 0; i < WarmupIterations; i++)
                RunOneTick(i);

            // Both measured batches must remain allocation-free after the explicit warm-up.
            long allocationCount = MeasureAllocationCount(WarmupIterations);

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"The full Cognition-path POCO set plus solver updates must produce zero managed " +
                $"allocation events in steady state {scenario}; measured {allocationCount} allocation events over {MeasuredIterations} ticks.");
        }

        private long MeasureAllocationCount(int startIndex)
        {
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < MeasuredIterations; i++)
                    RunOneTick(startIndex + i);
            });

            return allocationCount;
        }
    }
}
