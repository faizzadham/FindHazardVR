using Convai.Modules.BodyLanguage.Core.Pose;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Behavior tests for <see cref="BreathSolver" />: phase continuity across rate/depth
    ///     changes, deterministic irregularity given a seed, and cpm→period math. The solver is a
    ///     pure signal shaper — these tests read
    ///     its shaped output properties rather than any bone transform.
    /// </summary>
    public sealed class BreathSolverTests
    {
        private const float Dt = 1f / 60f;

        private BreathSolveInput Input(float rateCpm, float depth, float irregularity, float weight = 1f) => new()
        {
            DeltaTime = Dt,
            RateCpm = rateCpm,
            Depth = depth,
            Irregularity = irregularity,
            MasterWeight = weight,
            MaxChestExpansionDegrees = 1.5f,
            MaxShoulderLiftDegrees = 0.8f
        };

        [Test]
        public void CyclesPerMinute_MapsToExpectedPeriod()
        {
            // At 60 cpm the period is exactly 1 second: 2π radians of phase per second.
            var solver = new BreathSolver();
            solver.Seed(1234);

            int steps = Mathf.RoundToInt(1f / Dt);
            for (int i = 0; i < steps; i++)
                solver.Solve(Input(60f, 0.5f, 0f));

            // One full second at 60 cpm must land back at the start of the cycle.
            Assert.That(solver.Phase, Is.EqualTo(0f).Within(0.15f).Or.EqualTo(2f * Mathf.PI).Within(0.15f));
        }

        [Test]
        public void PhaseContinuity_RateChangeMidCycle_DoesNotDiscontinuity()
        {
            var solver = new BreathSolver();
            solver.Seed(1234);

            for (int i = 0; i < 30; i++)
                solver.Solve(Input(13f, 0.5f, 0f));

            float phaseBefore = solver.Phase;

            // Change rate abruptly — phase must keep advancing continuously from where it was,
            // not reset to zero.
            solver.Solve(Input(20f, 0.5f, 0f));
            float phaseAfter = solver.Phase;

            Assert.That(phaseAfter, Is.Not.EqualTo(0f), "A rate change mid-cycle must not reset phase to zero.");
            Assert.That(Mathf.DeltaAngle(phaseBefore * Mathf.Rad2Deg, phaseAfter * Mathf.Rad2Deg),
                Is.LessThan(30f), "A single tick's phase advance must stay small even across a rate change.");
        }

        [Test]
        public void PhaseContinuity_DepthChange_ScalesOutputWithoutPhaseJump()
        {
            var solver = new BreathSolver();
            solver.Seed(1234);

            for (int i = 0; i < 30; i++)
                solver.Solve(Input(13f, 1f, 0f));
            float phaseAtFullDepth = solver.Phase;

            solver.Solve(Input(13f, 0.1f, 0f));
            float phaseAtLowDepth = solver.Phase;

            Assert.That(Mathf.Abs(phaseAtLowDepth - phaseAtFullDepth), Is.LessThan(0.1f),
                "A depth change must not perturb phase — only the waveform's amplitude scales.");
        }

        [Test]
        public void Deterministic_SameSeed_ProducesIdenticalIrregularity()
        {
            var solverA = new BreathSolver();
            var solverB = new BreathSolver();
            solverA.Seed(999);
            solverB.Seed(999);

            for (int i = 0; i < 600; i++)
            {
                solverA.Solve(Input(13f, 0.5f, 0.8f));
                solverB.Solve(Input(13f, 0.5f, 0.8f));

                Assert.That(solverA.Phase, Is.EqualTo(solverB.Phase).Within(1e-6f),
                    $"Tick {i}: identically seeded solvers must be bit-stable.");
                Assert.That(solverA.Waveform, Is.EqualTo(solverB.Waveform).Within(1e-6f));
            }
        }

        [Test]
        public void DifferentSeeds_ProduceDifferentIrregularityOverTime()
        {
            var solverA = new BreathSolver();
            var solverB = new BreathSolver();
            solverA.Seed(1);
            solverB.Seed(987654321);

            bool sawDivergence = false;
            for (int i = 0; i < 1200; i++)
            {
                solverA.Solve(Input(13f, 0.5f, 1f));
                solverB.Solve(Input(13f, 0.5f, 1f));
                if (Mathf.Abs(solverA.Phase - solverB.Phase) > 0.05f) sawDivergence = true;
            }

            Assert.IsTrue(sawDivergence, "Different seeds must eventually diverge under high irregularity.");
        }

        [Test]
        public void Solve_ZeroWeight_OutputsStayExactlyZero()
        {
            var solver = new BreathSolver();
            solver.Seed(1);

            for (int i = 0; i < 300; i++)
                solver.Solve(Input(13f, 1f, 0f, weight: 0f));

            Assert.That(solver.ChestSagittalDegrees, Is.EqualTo(0f),
                "Weight 0 must shape a zero-amplitude waveform, so the chest sagittal output must be exactly zero.");
            Assert.That(solver.ShoulderLiftDegrees, Is.EqualTo(0f),
                "Weight 0 must shape a zero-amplitude waveform, so the shoulder lift output must be exactly zero.");
        }

        [Test]
        public void Waveform_StaysWithinNormalizedRange()
        {
            var solver = new BreathSolver();
            solver.Seed(42);

            for (int i = 0; i < 2000; i++)
            {
                solver.Solve(Input(13f, 1f, 0.6f));
                Assert.That(solver.Waveform, Is.InRange(-1.05f, 1.05f),
                    "The waveform must stay within its normalized -1..1 range even under jitter.");
            }
        }

        [Test]
        public void Waveform_NoVelocityDiscontinuityAtTrough()
        {
            // Regression for the inhale "kick": the old shape used EaseOutQuad on inhale, which
            // has MAXIMUM velocity at the trough while exhale (EaseInOutQuad) arrives at the
            // trough with ZERO velocity — a per-cycle discontinuity right at the wrap. Both
            // halves now share EaseInOutQuad (zero derivative at both of its own endpoints), so
            // the frame-to-frame delta must not spike at the trough relative to the rest of the
            // cycle.
            var solver = new BreathSolver();
            solver.Seed(7);

            const float rateCpm = 13f;
            const float periodSeconds = 60f / rateCpm;
            int stepsPerCycle = Mathf.RoundToInt(periodSeconds / Dt);
            int totalSteps = stepsPerCycle * 3;

            // Prime with one solved frame first: the measurement below compares consecutive
            // SOLVED frames only — the constructor-to-first-frame transition is start-up, not
            // part of the cycle, and depth scaling would otherwise register as a fake "delta".
            solver.Solve(Input(rateCpm, 1f, 0f));
            float previousWaveform = solver.Waveform;
            float maxDeltaNearTrough = 0f;
            float maxDeltaElsewhere = 0f;

            // A small window of steps either side of each full-cycle wrap counts as "near the
            // trough"; everything else (including the peak handoff) is "elsewhere".
            const int troughWindowSteps = 2;

            for (int i = 1; i < totalSteps; i++)
            {
                solver.Solve(Input(rateCpm, 1f, 0f));

                float delta = Mathf.Abs(solver.Waveform - previousWaveform);
                previousWaveform = solver.Waveform;

                int stepInCycle = i % stepsPerCycle;
                bool nearTrough = stepInCycle < troughWindowSteps || stepInCycle > stepsPerCycle - troughWindowSteps;

                if (nearTrough) maxDeltaNearTrough = Mathf.Max(maxDeltaNearTrough, delta);
                else maxDeltaElsewhere = Mathf.Max(maxDeltaElsewhere, delta);
            }

            Assert.That(maxDeltaNearTrough, Is.LessThan(maxDeltaElsewhere * 2f).Or.LessThan(0.01f),
                "The per-step waveform delta at the trough must not spike far above the rest of " +
                "the cycle — a large ratio here means the inhale still kicks in with a velocity " +
                "discontinuity at the wrap.");
        }

        [Test]
        public void Reset_ClearsPhaseAndWaveformAndOutputs()
        {
            var solver = new BreathSolver();
            solver.Seed(1);
            for (int i = 0; i < 60; i++)
                solver.Solve(Input(13f, 1f, 0f));

            solver.Reset();

            Assert.That(solver.Phase, Is.EqualTo(0f));
            Assert.That(solver.Waveform, Is.EqualTo(-1f));
            Assert.That(solver.ChestSagittalDegrees, Is.EqualTo(0f));
            Assert.That(solver.ShoulderLiftDegrees, Is.EqualTo(0f));
            Assert.That(solver.ChestLateralDegrees, Is.EqualTo(0f));
        }

        // ── Richer breath kinematics ──────────────────────────

        [Test]
        public void ShoulderWaveform_PeaksBeforeChestWaveform_ClavicleLead()
        {
            var solver = new BreathSolver();
            solver.Seed(11);

            const float rateCpm = 12f;
            const float periodSeconds = 60f / rateCpm;
            int stepsPerCycle = Mathf.RoundToInt(periodSeconds / Dt);

            float maxWaveform = float.NegativeInfinity;
            int waveformPeakStep = -1;
            float maxShoulder = float.NegativeInfinity;
            int shoulderPeakStep = -1;

            for (int i = 0; i < stepsPerCycle; i++)
            {
                solver.Solve(Input(rateCpm, 1f, 0f));
                if (solver.Waveform > maxWaveform)
                {
                    maxWaveform = solver.Waveform;
                    waveformPeakStep = i;
                }
                if (solver.ShoulderLiftDegrees > maxShoulder)
                {
                    maxShoulder = solver.ShoulderLiftDegrees;
                    shoulderPeakStep = i;
                }
            }

            Assert.That(shoulderPeakStep, Is.LessThan(waveformPeakStep),
                "The clavicle-led shoulder waveform must reach its own peak strictly before the chest waveform peaks.");
        }

        [Test]
        public void ChestMotion_IsOneDirectional_AndLateralRotationRemainsNeutral()
        {
            var solver = new BreathSolver();
            solver.Seed(22);

            const float rateCpm = 12f;
            const float periodSeconds = 60f / rateCpm;
            int stepsPerCycle = Mathf.RoundToInt(periodSeconds / Dt);

            float maxLateralMagnitude = 0f;
            float mostPositiveChest = float.NegativeInfinity;
            float mostNegativeChest = float.PositiveInfinity;

            for (int i = 0; i < stepsPerCycle; i++)
            {
                solver.Solve(Input(rateCpm, 1f, 0f));
                maxLateralMagnitude = Mathf.Max(maxLateralMagnitude, Mathf.Abs(solver.ChestLateralDegrees));
                mostPositiveChest = Mathf.Max(mostPositiveChest, solver.ChestSagittalDegrees);
                mostNegativeChest = Mathf.Min(mostNegativeChest, solver.ChestSagittalDegrees);
            }

            Assert.That(maxLateralMagnitude, Is.EqualTo(0f));
            Assert.That(mostPositiveChest, Is.LessThanOrEqualTo(0.0001f));
            Assert.That(mostNegativeChest, Is.LessThan(-0.1f));

        }

        [TestCase(30f)]
        [TestCase(60f)]
        [TestCase(120f)]
        public void PhysiologicalEnvelope_IsStableAcrossFrameRates(float framesPerSecond)
        {
            var solver = new BreathSolver();
            solver.Seed(77);
            BreathSolveInput input = Input(12f, 1f, 0f);
            input.DeltaTime = 1f / framesPerSecond;
            int steps = Mathf.RoundToInt(5f * framesPerSecond);

            for (int i = 0; i < steps; i++) solver.Solve(input);

            Assert.That(solver.Phase,
                Is.EqualTo(0f).Within(0.02f).Or.EqualTo(2f * Mathf.PI).Within(0.02f));
            Assert.That(solver.ChestLateralDegrees, Is.EqualTo(0f));
            Assert.That(solver.ChestSagittalDegrees, Is.LessThanOrEqualTo(0.0001f));
        }

        [Test]
        public void InhaleEvent_DuringExhale_ReversesSmoothlyTowardInhalePeak()
        {
            var solver = new BreathSolver();
            solver.Seed(19);
            BreathSolveInput input = Input(60f, 1f, 0f);

            // Enter the exhale half of the free-running cycle.
            for (int i = 0; i < 42; i++) solver.Solve(input);
            float previous = solver.ChestSagittalDegrees;

            input.EventKind = BreathEventKind.SpeechGapInhale;
            for (int i = 0; i < 20; i++)
            {
                solver.Solve(input);
                Assert.That(solver.ChestSagittalDegrees, Is.LessThanOrEqualTo(previous + 0.001f),
                    "An inhale command must move toward expansion even when received during exhale.");
                Assert.That(Mathf.Abs(solver.ChestSagittalDegrees - previous), Is.LessThan(0.2f),
                    "Phase-aware reversal must not introduce a one-frame chest pop.");
                previous = solver.ChestSagittalDegrees;
            }
        }

        [TestCase(30f)]
        [TestCase(60f)]
        [TestCase(120f)]
        public void AnimatorFreeTorsoChain_BreathHasNoVisiblePerFrameKick(float framesPerSecond)
        {
            var root = new GameObject("BreathVisualRoot");
            var spine = new GameObject("Spine").transform;
            var chest = new GameObject("Chest").transform;
            var upperChest = new GameObject("UpperChest").transform;
            spine.SetParent(root.transform, false);
            chest.SetParent(spine, false);
            upperChest.SetParent(chest, false);

            try
            {
                var solver = new BreathSolver();
                solver.Seed(101);
                var compositor = new ProceduralPoseCompositor();
                compositor.BindManual(spine, chest, upperChest, null, null);
                BreathSolveInput input = Input(13f, 1f, 0f);
                input.DeltaTime = 1f / framesPerSecond;
                input.MaxChestExpansionDegrees = 4.5f;

                float previousSagittal = upperChest.localEulerAngles.x;
                float maxAngularStep = 0f;
                int maxStepIndex = -1;
                int steps = Mathf.RoundToInt(10f * framesPerSecond);
                for (int i = 0; i < steps; i++)
                {
                    solver.Solve(input);
                    compositor.BeginFrame();
                    compositor.AddSpineChainSwing(
                        solver.ChestSagittalDegrees, solver.ChestLateralDegrees);
                    compositor.ApplyAccumulated(input.DeltaTime);
                    if (i > 2)
                    {
                        float angularStep = Mathf.Abs(Mathf.DeltaAngle(
                            previousSagittal, upperChest.localEulerAngles.x));
                        if (angularStep > maxAngularStep)
                        {
                            maxAngularStep = angularStep;
                            maxStepIndex = i;
                        }
                    }
                    previousSagittal = upperChest.localEulerAngles.x;
                }

                Assert.That(maxAngularStep, Is.LessThan(21f / framesPerSecond),
                    $"Animator-free breathing exceeded the human tonic angular-speed budget at step {maxStepIndex}.");
                Assert.That(solver.ChestLateralDegrees, Is.EqualTo(0f),
                    "Generic breathing must not introduce lateral torso wobble.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
