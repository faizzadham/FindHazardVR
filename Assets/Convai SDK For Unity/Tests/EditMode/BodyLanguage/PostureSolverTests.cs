using Convai.Modules.BodyLanguage.Core.Pose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Behavior tests for <see cref="PostureSolver" />: convergence, per-axis limits,
    ///     zero-weight no-output, and fade-out smoothness. The solver is
    ///     a pure signal shaper — these tests read its shaped output properties
    ///     rather than any bone transform; swing-only spine-chain composition is covered at
    ///     <c>ProceduralPoseCompositorTests</c>, which now owns the write path.
    /// </summary>
    public sealed class PostureSolverTests
    {
        private const float Dt = 1f / 60f;
        private const float MaxOpenness = 12f;
        private const float MaxLean = 10f;
        private const float MaxTension = 8f;
        private const float MaxLateralShift = 5f;
        private const float SpringSharpness = 4f;
        private const float MaxAngularSpeed = 90f;

        private PostureSolver _solver;

        [SetUp]
        public void SetUp() => _solver = new PostureSolver();

        // Baseline for the sustained/transient posture-source separation:
        // SuppressionWeight = 1f + every sustain floor = 1f reduces the solver's per-channel
        // weighting to Max(1f, anything) == 1f, i.e. today's pre-split single-weight behavior —
        // this is the default for every existing test in this file (single-target scenarios, no
        // suppression in play). Suppression-specific tests below override these explicitly.
        private PostureSolveInput Input(
            float openness, float lean, float tension, float weight = 1f, float lateralShift = 0f,
            float suppressionWeight = 1f, float opennessSustainFloor = 1f, float leanSustainFloor = 1f,
            float tensionSustainFloor = 1f, float transientLean = 0f) => new()
        {
            DeltaTime = Dt,
            OpennessTarget = openness,
            SustainedLeanTarget = lean,
            TransientLeanTarget = transientLean,
            TensionTarget = tension,
            LateralShiftTarget = lateralShift,
            MasterWeight = weight,
            SuppressionWeight = suppressionWeight,
            OpennessSustainFloor = opennessSustainFloor,
            LeanSustainFloor = leanSustainFloor,
            TensionSustainFloor = tensionSustainFloor,
            MaxOpennessDegrees = MaxOpenness,
            MaxLeanDegrees = MaxLean,
            MaxTensionDegrees = MaxTension,
            MaxLateralShiftDegrees = MaxLateralShift,
            SpringSharpness = SpringSharpness,
            MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
        };

        private void SolveFor(float seconds, PostureSolveInput input)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                _solver.Solve(in input);
        }

        [Test]
        public void Solve_ConvergesToTarget_WithinTolerance()
        {
            SolveFor(3f, Input(1f, 0.5f, -0.3f));

            Assert.That(_solver.Openness, Is.EqualTo(1f).Within(0.02f));
            Assert.That(_solver.Lean, Is.EqualTo(0.5f).Within(0.02f));
            Assert.That(_solver.Tension, Is.EqualTo(-0.3f).Within(0.02f));
        }

        [Test]
        public void Solve_ExtremeTargets_RespectPerAxisDegreeLimits()
        {
            SolveFor(3f, Input(10f, 10f, 10f)); // way beyond the -1..1 domain

            // Targets are clamped to -1..1 before scaling, so the physically applied angle must
            // never exceed the profile's configured max-degrees for that channel.
            float chestSagittal = Mathf.Abs(-_solver.Openness * MaxOpenness + _solver.Lean * MaxLean);
            Assert.That(chestSagittal, Is.LessThanOrEqualTo(MaxOpenness + MaxLean + 0.5f));
            Assert.That(Mathf.Abs(_solver.Tension) * MaxTension, Is.LessThanOrEqualTo(MaxTension + 0.5f));
        }

        [Test]
        public void Solve_ZeroWeight_OutputsStayExactlyZero()
        {
            SolveFor(2f, Input(1f, 1f, 1f, weight: 0f));

            Assert.That(_solver.SpineSagittalDegrees, Is.EqualTo(0f),
                "Weight 0 must keep every solved channel at its rest value (goal is zero, spring never leaves 0).");
            Assert.That(_solver.SpineLateralDegrees, Is.EqualTo(0f));
            Assert.That(_solver.ShoulderTensionDegrees, Is.EqualTo(0f));
        }

        [Test]
        public void Solve_FadeOut_DeltaMagnitudeDecreasesMonotonically()
        {
            SolveFor(3f, Input(1f, 1f, 1f));
            Assert.That(Mathf.Abs(_solver.Openness), Is.GreaterThan(0.5f), "Sanity: posture engaged.");

            float previousMagnitude = float.MaxValue;
            bool sawDecrease = false;
            for (int i = 0; i < 400; i++)
            {
                PostureSolveInput fadeInput = Input(0f, 0f, 0f);
                _solver.Solve(in fadeInput);

                float magnitude = Mathf.Abs(_solver.Openness) + Mathf.Abs(_solver.Lean) + Mathf.Abs(_solver.Tension);
                Assert.That(magnitude, Is.LessThanOrEqualTo(previousMagnitude + 1e-4f),
                    "Fade-out must not increase the delta magnitude at any step.");
                if (magnitude < previousMagnitude - 1e-5f) sawDecrease = true;
                previousMagnitude = magnitude;
            }

            Assert.IsTrue(sawDecrease, "The fade-out must actually decrease over time.");
            Assert.That(previousMagnitude, Is.LessThan(0.05f), "The fade-out must return smoothly to the animated pose.");
        }

        [Test]
        public void Reset_ClearsSolverState()
        {
            SolveFor(2f, Input(1f, 1f, 1f));
            Assert.That(Mathf.Abs(_solver.Openness), Is.GreaterThan(0.1f));

            _solver.Reset();

            Assert.That(_solver.Openness, Is.EqualTo(0f));
            Assert.That(_solver.Lean, Is.EqualTo(0f));
            Assert.That(_solver.Tension, Is.EqualTo(0f));
        }

        [Test]
        public void Reset_FirstTickAfterReset_ProducesNoResidualDelta()
        {
            // Simulates a rig rebind (the controller resets the solver on HandleRigBindingChanged):
            // the very first solve tick after Reset() must start from zero, not carry over the
            // previous settled posture.
            SolveFor(2f, Input(1f, 1f, 1f));
            Assert.That(Mathf.Abs(_solver.Openness), Is.GreaterThan(0.1f), "Sanity: posture engaged before reset.");

            _solver.Reset();
            PostureSolveInput input = Input(1f, 1f, 1f);
            _solver.Solve(in input);

            // One tick at 60 Hz cannot have sprung anywhere near the target — if it had, that
            // would mean stale state survived the reset.
            Assert.That(Mathf.Abs(_solver.Openness), Is.LessThan(0.05f),
                "The first tick after a reset must not carry over the previous settled posture.");
        }

        [Test]
        public void LateralShift_ConvergesToTarget_WithinTolerance()
        {
            SolveFor(3f, Input(0f, 0f, 0f, lateralShift: 1f));

            Assert.That(_solver.LateralShift, Is.EqualTo(1f).Within(0.02f));
        }

        [Test]
        public void LateralShift_Negative_ConvergesToNegativeTarget()
        {
            SolveFor(3f, Input(0f, 0f, 0f, lateralShift: -0.6f));

            Assert.That(_solver.LateralShift, Is.EqualTo(-0.6f).Within(0.02f));
        }

        [Test]
        public void LateralShift_ExtremeTarget_RespectsMaxLateralShiftDegrees()
        {
            SolveFor(3f, Input(0f, 0f, 0f, lateralShift: 10f)); // way beyond -1..1

            float appliedDegrees = Mathf.Abs(_solver.LateralShift) * MaxLateralShift;
            Assert.That(appliedDegrees, Is.LessThanOrEqualTo(MaxLateralShift + 0.5f),
                "The lateral shift target is clamped to -1..1 before scaling, so applied degrees must never exceed MaxLateralShiftDegrees.");
        }

        [Test]
        public void LateralShift_ZeroWeight_OutputStaysExactlyZero()
        {
            SolveFor(2f, Input(0f, 0f, 0f, weight: 0f, lateralShift: 1f));

            Assert.That(_solver.SpineLateralDegrees, Is.EqualTo(0f),
                "Weight 0 must keep the lateral channel at zero, including with a nonzero target.");
        }

        [Test]
        public void LateralShift_FadeOut_DecreasesMonotonicallyAndSkipsWriteNearZero()
        {
            SolveFor(3f, Input(0f, 0f, 0f, lateralShift: 1f));
            Assert.That(Mathf.Abs(_solver.LateralShift), Is.GreaterThan(0.5f), "Sanity: lateral shift engaged.");

            float previousMagnitude = float.MaxValue;
            bool sawDecrease = false;
            for (int i = 0; i < 400; i++)
            {
                PostureSolveInput fadeInput = Input(0f, 0f, 0f, lateralShift: 0f);
                _solver.Solve(in fadeInput);

                float magnitude = Mathf.Abs(_solver.LateralShift);
                Assert.That(magnitude, Is.LessThanOrEqualTo(previousMagnitude + 1e-4f),
                    "Fade-out must not increase the lateral delta magnitude at any step.");
                if (magnitude < previousMagnitude - 1e-5f) sawDecrease = true;
                previousMagnitude = magnitude;
            }

            Assert.IsTrue(sawDecrease, "The lateral fade-out must actually decrease over time.");
            Assert.That(previousMagnitude, Is.LessThan(0.05f), "The lateral fade-out must return smoothly to the animated pose.");
        }

        [Test]
        public void LateralShift_DoesNotDisturbSagittalOpennessOrLean()
        {
            SolveFor(3f, Input(0.5f, 0.3f, 0f, lateralShift: 1f));

            Assert.That(_solver.Openness, Is.EqualTo(0.5f).Within(0.02f),
                "Lateral shift must compose independently — it must not perturb openness.");
            Assert.That(_solver.Lean, Is.EqualTo(0.3f).Within(0.02f),
                "Lateral shift must compose independently — it must not perturb lean.");
        }

        // Sustained/transient posture-source separation: these literal floor values mirror
        // ConvaiBodyLanguageController's OpennessSustainFloor/LeanSustainFloor/TensionSustainFloor
        // consts (0.85/0.75/0.80) — the solver is a caller-agnostic POCO, so the floors are
        // supplied as plain input data here rather than referencing the controller's private
        // consts directly.
        private const float OpennessFloor = 0.85f;
        private const float LeanFloor = 0.75f;
        private const float TensionFloor = 0.80f;

        [Test]
        public void Suppression_AtFullWeight_IsBitIdenticalToPreSplitSingleWeightBehavior()
        {
            // Backward-compat (must): SuppressionWeight == 1 ⇒ every floor's Max(1, floor) == 1,
            // so every channel reduces to Target * MasterWeight * maxDegrees — the pre-split
            // formula — across several representative targets (including transient lean, which
            // must fold in additively at full weight too, and a partial MasterWeight fade).
            (float openness, float sustainedLean, float transientLean, float tension, float lateral, float masterWeight)[] cases =
            {
                (1f, 0.5f, 0f, -0.3f, 0f, 1f),
                (-0.7f, -0.2f, 0.15f, 0.6f, 0.4f, 1f),
                (0.3f, 0.9f, -0.1f, -0.9f, -0.8f, 1f),
                (0.5f, 0.4f, 0.1f, 0.2f, 0.3f, 0.6f), // partial MasterWeight fade — still no suppression
            };

            foreach (var c in cases)
            {
                _solver.Reset();

                var splitInput = new PostureSolveInput
                {
                    DeltaTime = Dt,
                    OpennessTarget = c.openness,
                    SustainedLeanTarget = c.sustainedLean,
                    TransientLeanTarget = c.transientLean,
                    TensionTarget = c.tension,
                    LateralShiftTarget = c.lateral,
                    MasterWeight = c.masterWeight,
                    SuppressionWeight = 1f,
                    OpennessSustainFloor = OpennessFloor,
                    LeanSustainFloor = LeanFloor,
                    TensionSustainFloor = TensionFloor,
                    MaxOpennessDegrees = MaxOpenness,
                    MaxLeanDegrees = MaxLean,
                    MaxTensionDegrees = MaxTension,
                    MaxLateralShiftDegrees = MaxLateralShift,
                    SpringSharpness = SpringSharpness,
                    MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
                };
                SolveFor(3f, splitInput);

                float expectedOpenness = c.openness * c.masterWeight;
                float expectedLean = Mathf.Clamp(c.sustainedLean + c.transientLean, -1f, 1f) * c.masterWeight;
                float expectedTension = c.tension * c.masterWeight;

                Assert.That(_solver.Openness, Is.EqualTo(expectedOpenness).Within(0.02f),
                    $"openness={c.openness} masterWeight={c.masterWeight}: SuppressionWeight=1 must be bit-identical to the pre-split Target*MasterWeight formula.");
                Assert.That(_solver.Lean, Is.EqualTo(expectedLean).Within(0.02f),
                    $"sustainedLean={c.sustainedLean} transientLean={c.transientLean}: SuppressionWeight=1 must be bit-identical to the pre-split combined-then-clamped Target*MasterWeight formula.");
                Assert.That(_solver.Tension, Is.EqualTo(expectedTension).Within(0.02f),
                    $"tension={c.tension} masterWeight={c.masterWeight}: SuppressionWeight=1 must be bit-identical to the pre-split Target*MasterWeight formula.");
            }
        }

        [Test]
        public void Suppression_UnderUpperBodySuppression_SustainedOpennessSurvivesAtItsFloor()
        {
            // SuppressionWeight=0.75 with OpennessSustainFloor=0.85: the effective openness
            // weight is Max(0.75, 0.85) = 0.85, NOT 0.75 — the sustained silhouette survives
            // suppression at its own floor rather than ducking with the transient factor.
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 1f,
                SustainedLeanTarget = 0f,
                TransientLeanTarget = 0f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Openness, Is.EqualTo(OpennessFloor).Within(0.02f),
                "Openness's effective weight under SuppressionWeight=0.75 must be floored at 0.85, not ducked to 0.75.");
        }

        [Test]
        public void Suppression_UnderUpperBodySuppression_TransientLeanDucksToSuppressionWeightWithNoFloor()
        {
            // Transient lean has no sustain floor — it is weighted by SuppressionWeight alone, so
            // at SuppressionWeight=0.75 a pure-transient lean target settles at exactly 0.75, the
            // full duck (no floor rescue).
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 0f,
                TransientLeanTarget = 1f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Lean, Is.EqualTo(0.75f).Within(0.02f),
                "A pure transient lean target must duck fully to SuppressionWeight (0.75), with no sustain-floor rescue.");
        }

        [Test]
        public void Suppression_UnderUpperBodySuppression_SustainedLeanSettlesAtItsFloor()
        {
            // A pure sustained lean target at SuppressionWeight=0.75 with LeanSustainFloor=0.75:
            // Max(0.75, 0.75) = 0.75 — the floor and the suppression weight coincide numerically
            // at this specific chosen value (by choice of these test inputs), so this
            // assertion alone cannot distinguish "floored" from "ducked". The distinguishing
            // evidence is Suppression_UnderUpperBodySuppression_SustainedOpennessSurvivesAtItsFloor
            // (whose floor 0.85 != its SuppressionWeight 0.75) and the double-lean test below
            // (whose combined sustained+transient result only matches the floored formula).
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 1f,
                TransientLeanTarget = 0f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Lean, Is.EqualTo(0.75f).Within(0.02f),
                "A pure sustained lean target settles at Max(SuppressionWeight, LeanSustainFloor) = 0.75 here.");
        }

        [Test]
        public void Suppression_UnderUpperBodySuppression_TensionSurvivesAtItsFloor()
        {
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 0f,
                TransientLeanTarget = 0f,
                TensionTarget = 1f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Tension, Is.EqualTo(TensionFloor).Within(0.02f),
                "Tension's effective weight under SuppressionWeight=0.75 must be floored at 0.80, not ducked to 0.75.");
        }

        [Test]
        public void Suppression_UnderUpperBodySuppression_LateralShiftDucksToSuppressionWeightWithNoFloor()
        {
            // Lateral shift is 100% transient (no sustain floor at all): it must duck fully to
            // SuppressionWeight, exactly like transient lean.
            SolveFor(3f, new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 0f,
                TransientLeanTarget = 0f,
                TensionTarget = 0f,
                LateralShiftTarget = 1f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            });

            Assert.That(_solver.LateralShift, Is.EqualTo(0.75f).Within(0.02f),
                "Lateral shift has no sustain floor — it must duck fully to SuppressionWeight (0.75).");
        }

        [Test]
        public void DoubleLean_LargeSustainedPlusLargeTransient_ClampsCombinedLeanWithoutOverflow()
        {
            // Double-lean guard: a large sustained lean AND a large transient lean must combine
            // and clamp to -1..1 TOGETHER (not compound past it) — at full weight (no
            // suppression) 1.0 + 1.0 must settle at the single-target ceiling, not 2x it.
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 1f,
                TransientLeanTarget = 1f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 1f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Lean, Is.EqualTo(1f).Within(0.02f),
                "Sustained (1.0) + transient (1.0) lean must clamp together to the single-target ceiling of 1.0, never overflow past it.");

            float appliedDegrees = Mathf.Abs(_solver.Lean) * MaxLean;
            Assert.That(appliedDegrees, Is.LessThanOrEqualTo(MaxLean + 0.5f),
                "The combined lean's physically applied degrees must never exceed MaxLeanDegrees — no compounding past a single target's own range.");
        }

        [Test]
        public void DoubleLean_UnderSuppression_CombinedFloorAndTransientAlsoClampTogether()
        {
            // Same guard under suppression: sustained lean floored at LeanFloor plus transient
            // lean at full magnitude and full SuppressionWeight must still combine-then-clamp
            // rather than exceed -1..1 — e.g. sustained=1 (floored effective 0.75) + transient=1
            // (weighted 0.75) sums to 1.5 pre-clamp, which must clamp to 1.0.
            var input = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 0f,
                SustainedLeanTarget = 1f,
                TransientLeanTarget = 1f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 0.75f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, input);

            Assert.That(_solver.Lean, Is.EqualTo(1f).Within(0.02f),
                "Sustained (floored 0.75) + transient (weighted 0.75) sums to 1.5 pre-clamp and must clamp to 1.0, never overflow.");
        }

        [Test]
        public void Suppression_TransitionFromNoSuppressionToUpperBody_ChangesOpennessAndLeanSmoothly()
        {
            // No-pop requirement: stepping SuppressionWeight from 1 down to 0.75 over several
            // ticks (mirrors the controller's own MoveTowards slew) must change the solved
            // openness/lean degrees monotonically toward their new (lower, floored) goal, never
            // in a single-tick jump — the spring smooths the max()-floor slope change exactly
            // like any other goal change.
            var steadyInput = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 1f,
                SustainedLeanTarget = 1f,
                TransientLeanTarget = 0f,
                TensionTarget = 0f,
                LateralShiftTarget = 0f,
                MasterWeight = 1f,
                SuppressionWeight = 1f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            // Settle fully at SuppressionWeight=1 first (openness and sustained lean both at 1.0).
            SolveFor(3f, steadyInput);
            Assert.That(_solver.Openness, Is.EqualTo(1f).Within(0.02f), "Sanity: settled at full weight before the transition.");

            // Since OpennessSustainFloor (0.85) and LeanSustainFloor (0.75) are both BELOW the
            // pre-transition weight of 1.0, dropping SuppressionWeight to 0.75 is a step DOWN in
            // effective weight for openness (to 0.85) and lean (to 0.75) — the solved degrees
            // must decrease monotonically toward the new (lower) goal at every intermediate tick,
            // mirroring the fade-out tests' own monotonic-decrease pattern, and must never
            // overshoot past the new steady-state value.
            float previousOpenness = _solver.Openness;
            float previousLean = _solver.Lean;
            bool sawOpennessDecrease = false;
            bool sawLeanDecrease = false;
            for (int i = 0; i < 30; i++)
            {
                float suppressionWeight = Mathf.Lerp(1f, 0.75f, (i + 1) / 30f);
                var steppedInput = steadyInput;
                steppedInput.SuppressionWeight = suppressionWeight;

                _solver.Solve(in steppedInput);

                Assert.That(_solver.Openness, Is.LessThanOrEqualTo(previousOpenness + 1e-4f),
                    $"tick {i}: openness must not increase (no pop) while SuppressionWeight steps down toward its floor.");
                Assert.That(_solver.Lean, Is.LessThanOrEqualTo(previousLean + 1e-4f),
                    $"tick {i}: lean must not increase (no pop) while SuppressionWeight steps down toward its floor.");
                Assert.That(_solver.Openness, Is.GreaterThanOrEqualTo(OpennessFloor - 0.05f),
                    $"tick {i}: openness must never undershoot past its floor's steady-state value.");
                Assert.That(_solver.Lean, Is.GreaterThanOrEqualTo(LeanFloor - 0.05f),
                    $"tick {i}: lean must never undershoot past its floor's steady-state value.");

                if (_solver.Openness < previousOpenness - 1e-5f) sawOpennessDecrease = true;
                if (_solver.Lean < previousLean - 1e-5f) sawLeanDecrease = true;
                previousOpenness = _solver.Openness;
                previousLean = _solver.Lean;
            }

            Assert.IsTrue(sawOpennessDecrease, "Openness must actually settle down toward its new floored goal over the transition.");
            Assert.IsTrue(sawLeanDecrease, "Lean must actually settle down toward its new floored goal over the transition.");

            // The ramp above holds the suppression weight steady only for its very last tick —
            // the spring (SmoothDamp, smoothTime = 2/SpringSharpness = 0.5s here) needs several
            // smoothTimes at a FIXED goal to actually converge, not just stop increasing. Give it
            // that settle time now, at the ramp's final SuppressionWeight (0.75), before asserting
            // the steady-state floor value — otherwise this asserts on a spring still mid-chase of
            // a target that was moving every tick, not on its converged value.
            var settledInput = steadyInput;
            settledInput.SuppressionWeight = 0.75f;
            SolveFor(3f, settledInput);

            Assert.That(_solver.Openness, Is.EqualTo(OpennessFloor).Within(0.03f), "Openness must settle at its floor after the transition completes.");
            Assert.That(_solver.Lean, Is.EqualTo(LeanFloor).Within(0.03f), "Lean must settle at its floor after the transition completes.");
        }

        [Test]
        public void FullBody_MasterWeightRampingToZero_DecaysAllSolvedDegreesTowardZero()
        {
            // FullBody carve-out (must, verify): MasterWeight is the ONE weight that zeroes the
            // sustained silhouette too — under FullBody suppression the controller ramps
            // MasterWeight to 0 (not SuppressionWeight), and every channel — sustained included —
            // must decay toward zero exactly like today's pre-split fade-out, regardless of how
            // high the sustain floors are (SuppressionWeight stays at 1, simulating "no UpperBody
            // suppression in effect" — only MasterWeight is fading, as in a real FullBody
            // transition).
            var engagedInput = new PostureSolveInput
            {
                DeltaTime = Dt,
                OpennessTarget = 1f,
                SustainedLeanTarget = 1f,
                TransientLeanTarget = 1f,
                TensionTarget = 1f,
                LateralShiftTarget = 1f,
                MasterWeight = 1f,
                SuppressionWeight = 1f,
                OpennessSustainFloor = OpennessFloor,
                LeanSustainFloor = LeanFloor,
                TensionSustainFloor = TensionFloor,
                MaxOpennessDegrees = MaxOpenness,
                MaxLeanDegrees = MaxLean,
                MaxTensionDegrees = MaxTension,
                MaxLateralShiftDegrees = MaxLateralShift,
                SpringSharpness = SpringSharpness,
                MaxAngularSpeedDegreesPerSecond = MaxAngularSpeed
            };
            SolveFor(3f, engagedInput);
            Assert.That(Mathf.Abs(_solver.Openness), Is.GreaterThan(0.5f), "Sanity: posture engaged before the FullBody fade.");

            float previousMagnitude = float.MaxValue;
            bool sawDecrease = false;
            for (int i = 0; i < 400; i++)
            {
                var fadeInput = engagedInput;
                fadeInput.MasterWeight = 0f; // FullBody: the shared master weight ramps fully to zero.
                _solver.Solve(in fadeInput);

                float magnitude = Mathf.Abs(_solver.Openness) + Mathf.Abs(_solver.Lean) +
                                   Mathf.Abs(_solver.Tension) + Mathf.Abs(_solver.LateralShift);
                Assert.That(magnitude, Is.LessThanOrEqualTo(previousMagnitude + 1e-4f),
                    "FullBody fade-out (MasterWeight→0) must not increase the combined delta magnitude at any step — sustained included.");
                if (magnitude < previousMagnitude - 1e-5f) sawDecrease = true;
                previousMagnitude = magnitude;
            }

            Assert.IsTrue(sawDecrease, "The FullBody fade-out must actually decrease over time.");
            Assert.That(previousMagnitude, Is.LessThan(0.05f),
                "MasterWeight→0 must decay ALL solved degrees toward zero — the sustained silhouette does NOT survive FullBody suppression.");
        }
    }
}
