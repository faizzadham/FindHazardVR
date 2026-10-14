using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
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
    ///     Zero-allocation gate for <see cref="GesticulationDirector" />'s fast channel (energy-
    ///     driven and no-provider statistical cadence), semantic channel, and the posture-pulse
    ///     path through <see cref="PostureSolver" />/<see cref="PostureSolveInput" />. Kept as a
    ///     sibling file (rather than folded into <see cref="BodyLanguageZeroAllocTests" />) so
    ///     that file's existing measured loop stays focused on the Cognition/solver set —
    ///     this file exercises the semantic-cue path on its own, same warm-up-twice-measure-twice
    ///     methodology.
    /// </summary>
    public sealed class GesticulationDirectorZeroAllocTests
    {
        private const float Dt = 1f / 60f;
        private const int WarmupIterations = 2000;
        private const int MeasuredIterations = 2000;

        private sealed class NullPerformer : IConversationalGesturePerformer
        {
            public GestureSuppression CurrentSuppression => GestureSuppression.None;
            public event System.Action<GestureCue, GesturePerformanceResult> Completed { add { } remove { } }
            public bool TryPerform(in GestureCue cue) => true;
        }

        private GesticulationDirector _director;
        private PostureSolver _postureSolver;
        private ConvaiBodyLanguageProfile _profile;
        private readonly NullPerformer _performer = new();

        [SetUp]
        public void SetUp()
        {
            _director = new GesticulationDirector();
            _director.Seed(2024);
            _postureSolver = new PostureSolver();
            _profile = ConvaiBodyLanguageProfile.CreateDefault();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void RunOneTick(int i, bool hasProvider)
        {
            float energy = 0.5f + 0.5f * Mathf.Sin(i * 0.11f);
            SpeechPulse pulse = (i % 17 == 0) ? new SpeechPulse(SpeechPulseKind.Emphasis, 0.8f, i * Dt) : default;

            DialogueState state = (i % 5 == 0) ? DialogueState.Speaking : DialogueState.Idle;

            _director.Tick(
                state,
                gesticulationEnabled: true,
                gesticulationIntensity: 0.8f,
                in pulse,
                hasSpeechEnergyProvider: hasProvider,
                suppression: GestureSuppression.None,
                gestureIntensityScale: 1f,
                gestureRateScale: 1f,
                deltaTime: Dt,
                beatMinIntervalSeconds: _profile.BeatMinIntervalSeconds,
                beatIntervalVarianceSeconds: _profile.BeatIntervalVarianceSeconds,
                beatHeadIntensity: _profile.BeatHeadIntensity,
                posturePulseAmplitude: _profile.PosturePulseAmplitude,
                posturePulseAttackSeconds: _profile.PosturePulseAttackSeconds,
                posturePulseDecaySeconds: _profile.PosturePulseDecaySeconds,
                energyToIntensityGain: _profile.EnergyToIntensityGain,
                statisticalCadenceIntervalSeconds: _profile.StatisticalCadenceIntervalSeconds,
                statisticalCadenceVarianceSeconds: _profile.StatisticalCadenceVarianceSeconds,
                upperBodySuppressionWeight: _profile.UpperBodySuppressionPostureWeight,
                trace: null);

            // Every 41 ticks, exercise the semantic channel too (steady state: mostly refused by
            // its own refractory, occasionally accepted) — both are realistic steady states.
            if (i % 41 == 0)
            {
                var cue = new GestureCue(GestureCueKind.Affirmative, 1f);
                _director.TryEmitCue(
                    in cue, _performer, GestureSuppression.None,
                    gestureIntensityScale: 1f,
                    semanticCueRefractorySeconds: _profile.SemanticCueRefractorySeconds,
                    beatHeadIntensity: _profile.BeatHeadIntensity,
                    posturePulseAmplitude: _profile.PosturePulseAmplitude,
                    trace: null);
            }

            // Every 53 ticks, an Uncertain cue — exercises the shoulder-shrug trigger/envelope
            // path alongside the semantic channel above; TickShrugEnvelope itself
            // runs every tick regardless (see GesticulationDirector.Tick), so this also covers
            // the envelope mid-flight (rising/holding/decaying), not just the idle branch.
            if (i % 53 == 0)
            {
                var uncertainCue = new GestureCue(GestureCueKind.Uncertain, 1f);
                _director.TryEmitCue(
                    in uncertainCue, _performer, GestureSuppression.None,
                    gestureIntensityScale: 1f,
                    semanticCueRefractorySeconds: _profile.SemanticCueRefractorySeconds,
                    beatHeadIntensity: _profile.BeatHeadIntensity,
                    posturePulseAmplitude: _profile.PosturePulseAmplitude,
                    trace: null);
            }

            var postureInput = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0.1f,
                SustainedLeanTarget = 0.1f,
                TransientLeanTarget = _director.PosturePulseValue,
                TensionTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 1f,
                OpennessSustainFloor = 1f,
                LeanSustainFloor = 1f,
                TensionSustainFloor = 1f,
                MaxOpennessDegrees = _profile.MaxOpennessDegrees,
                MaxLeanDegrees = _profile.MaxLeanDegrees,
                MaxTensionDegrees = _profile.MaxTensionDegrees,
                SpringSharpness = _profile.PostureSpringSharpness,
                MaxAngularSpeedDegreesPerSecond = _profile.PostureMaxAngularSpeed
            };
            _postureSolver.Solve(in postureInput);
        }

        [Test]
        public void FastChannelAndPosturePulsePath_AllocatesNothingInSteadyState_WithProvider()
        {
            AssertSteadyStateAllocatesNothing(hasProvider: true, "with a speech-energy provider present");
        }

        [Test]
        public void StatisticalCadencePath_AllocatesNothingInSteadyState_WithoutProvider()
        {
            AssertSteadyStateAllocatesNothing(hasProvider: false, "on the no-provider statistical cadence fallback");
        }

        private void AssertSteadyStateAllocatesNothing(bool hasProvider, string scenario)
        {
            for (int i = 0; i < WarmupIterations; i++)
                RunOneTick(i, hasProvider);

            long allocationCount = MeasureAllocationCount(WarmupIterations, hasProvider);

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"GesticulationDirector.Tick/TryEmitCue plus the posture-pulse solver path must allocate zero " +
                $"managed allocation events in steady state {scenario}; measured {allocationCount} allocation events over {MeasuredIterations} ticks.");
        }

        private long MeasureAllocationCount(int startIndex, bool hasProvider)
        {
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < MeasuredIterations; i++)
                    RunOneTick(startIndex + i, hasProvider);
            });

            return allocationCount;
        }
    }
}
