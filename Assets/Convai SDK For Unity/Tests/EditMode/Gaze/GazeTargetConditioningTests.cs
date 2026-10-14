using System;
using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Target conditioning: what the arbiter's One-Euro filter does to the two signals it has
    ///     to tell apart — a target that is standing still and only appears to move (a talking
    ///     character's head bone, a first-person camera with bob), and a target that is genuinely
    ///     moving (someone walking past).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every scenario is run twice: once through the live arbiter, and once through the
    ///         fixed 40 ms low pass the arbiter used to apply, reproduced here in four lines. That
    ///         control is the point. "The bob no longer reaches the head" is only a claim if the
    ///         same measurement says it DID before, and "pursuit is unharmed" is only a claim if
    ///         the number it is compared against is the behaviour that shipped.
    ///     </para>
    ///     <para>
    ///         The trace harness takes a world point, so the conditioner is composed in front of it
    ///         exactly as the controller composes the arbiter in front of the solvers. The pursuit
    ///         traces in <see cref="GazePursuitTraceTests" /> deliberately do not do this — they
    ///         judge the actuators on a clean signal — which is why the walk-by is re-run here
    ///         rather than asserted about there.
    ///     </para>
    /// </remarks>
    public sealed class GazeTargetConditioningTests
    {
        private const float Dt = GazeShiftTraceHarness.FrameSeconds;

        /// <summary>The bob a talking character's head bone carries: ±2 cm at 2 Hz.</summary>
        private const float BobAmplitudeMetres = 0.02f;

        private const float BobFrequencyHz = 2f;

        /// <summary>
        ///     Conversational distance, and far enough off the character's forward axis that the
        ///     ladder has genuinely handed the head a share to hold — at a target dead ahead the
        ///     head is allocated nothing (<c>headEntryDegrees</c> is 12) and the measurement would
        ///     be of an empty channel.
        /// </summary>
        private const float TargetDistanceMetres = 2f;

        private const float TargetBearingDegrees = 30f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        // ------------------------------------------------------------------ (a) the bob

        [Test]
        public void TalkingHeadBob_NeverReachesTheHeadPlanOrTheMovementDetector()
        {
            BobMetrics conditioned = MeasureBob(ThroughTheArbiter(), "arbiter");
            BobMetrics legacy = MeasureBob(FixedFortyMillisecondLowPass(), "legacy 40 ms low pass");

            Assert.That(legacy.PlannedYawVariationDegrees, Is.GreaterThan(0.02f),
                "Signal check before verdict: the head plan has to move under the OLD filter, or " +
                "this measurement is of a channel the bob never reached and every assertion " +
                $"below it is vacuous. {legacy}");

            Assert.That(conditioned.PlannedYawVariationDegrees, Is.LessThan(0.3f),
                "A character standing still, talking, is not moving. The head plan may not " +
                "wander by more than a third of a degree in response to a bone that never left " +
                $"the room. {conditioned}");
            // Note what the control says about this one: the shift lane was never armed under
            // the old filter either. A 0.57 deg bob is far under the 2 deg movement trigger, so
            // the route the bob took to the head was the PURSUIT classifier, not a re-plan. This
            // assertion is therefore a guard against a future conditioning change waking the
            // detector, not a demonstration of a fix — the fix is the two assertions around it.
            Assert.IsFalse(conditioned.AnyHeadShift,
                "and no ballistic head movement may be planned for it at all: a re-plan is the " +
                $"character deciding to look somewhere, and nothing decided anything. {conditioned}");

            // The instrument has to be able to disagree, or the two assertions above are
            // satisfied by any filter at all, including none.
            Assert.That(conditioned.PlannedYawVariationDegrees,
                Is.LessThan(legacy.PlannedYawVariationDegrees * 0.8f),
                "The conditioning must be measurably doing the work — if this fails while the " +
                "gate above passes, the gate is passing on the actuators' own steadiness and " +
                $"proves nothing about the filter. conditioned {conditioned} / legacy {legacy}");
        }

        [Test]
        public void TalkingHeadBob_PassedStraightThroughTheFixedLowPass()
        {
            // The defect this phase exists for, stated as a measurement rather than a memory: a
            // 4 Hz cut-off passes a 2 Hz bob at ~89% of its amplitude, so whatever the head does
            // with it, it is being asked to do it.
            BobMetrics conditioned = MeasureBob(ThroughTheArbiter(), "arbiter");
            BobMetrics legacy = MeasureBob(FixedFortyMillisecondLowPass(), "legacy 40 ms low pass");

            Assert.That(legacy.RequiredYawVariationDegrees,
                Is.GreaterThan(conditioned.RequiredYawVariationDegrees * 1.3f),
                "The shift requirement is the conditioner's output expressed as an angle, and it " +
                "is the input every downstream stage divides up. At least a third of the bob has " +
                $"to be gone from it. conditioned {conditioned} / legacy {legacy}");
        }

        // ------------------------------------------------------------------ (b) the walk-by

        [Test]
        public void WalkBy_KeepsPeakHeadSpeedAndAddsNoLag()
        {
            using GazeShiftTraceHarness conditioned = RunWalkBy(ThroughTheArbiter());
            using GazeShiftTraceHarness legacy = RunWalkBy(FixedFortyMillisecondLowPass());

            WalkByMetrics metrics = CompareWalkBy(conditioned, legacy);
            TestContext.Out.WriteLine($"WalkBy: {metrics}");

            Assert.That(metrics.PeakSpeedRatio, Is.EqualTo(1f).Within(0.05f),
                "Conditioning is allowed to remove motion the target never had. It is not " +
                "allowed to change how fast the head follows someone who is really walking: " +
                $"peak head speed must land within 5% of the shipped behaviour. {metrics}");
            Assert.That(metrics.MaxHeadYawDifferenceDegrees, Is.LessThanOrEqualTo(1f),
                "and the head must not sit anywhere else along the crossing either — a degree " +
                $"is the whole budget for the difference, at any moment of it. {metrics}");
        }

        // ------------------------------------------------------------------ (c) the teleport

        [Test]
        public void Teleport_StillSnapsOnTheSameFrame()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new List<GazeTargetCandidate>(1);
            Vector3 near = new(0f, 1.6f, 2f);

            for (int i = 0; i < 120; i++) TickArbiter(arbiter, candidates, near, Dt);

            // Same target — same key, so this is a displacement and not a re-target — moved well
            // past the cut threshold.
            Vector3 far = near + new Vector3(0f, 0f, 1f) * (_profile.TargetTeleportThreshold + 2f);
            GazeTargetDecision cut = TickArbiter(arbiter, candidates, far, Dt);

            Assert.That(cut.SmoothedPoint, Is.EqualTo(far),
                "A teleport arrives whole, on the frame it happens. Routing the jump through the " +
                "filter would smear it over the next few frames and open the cut-off while it did.");
            Assert.IsTrue(cut.WasCut);
            Assert.IsTrue(cut.TeleportedThisTick);

            // And the filter is re-seeded rather than left carrying the jump's speed: the frame
            // after a cut must be conditioned normally, not passed straight through.
            Vector3 nudged = far + new Vector3(0.01f, 0f, 0f);
            GazeTargetDecision after = TickArbiter(arbiter, candidates, nudged, Dt);

            Assert.That(after.SmoothedPoint.x, Is.GreaterThan(far.x).And.LessThan(nudged.x - 0.002f),
                "A centimetre of movement immediately after a cut must be filtered, not followed " +
                "exactly — a filter still wide open from the jump is a filter that is not there.");
            Assert.IsFalse(after.WasCut, "One cut, one frame.");
        }

        [Test]
        public void ChangingTarget_SeedsTheFilterAtTheNewPointWithNoDrag()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new List<GazeTargetCandidate>(1);

            for (int i = 0; i < 120; i++)
                TickArbiter(arbiter, candidates, new Vector3(0f, 1.6f, 2f), Dt, "A");

            Vector3 other = new(3f, 1.6f, -1f);
            GazeTargetDecision retarget = TickArbiter(arbiter, candidates, other, Dt, "B");

            Assert.That(retarget.SmoothedPoint, Is.EqualTo(other),
                "Deciding to look at something else is not that thing moving: the point is " +
                "adopted, not smoothed toward.");
            Assert.IsFalse(retarget.WasCut, "A re-target is a decision, not a cut.");

            // The seed must also clear the speed estimate, or the first second on the new target
            // is conditioned by how far away the old one was.
            GazeTargetDecision next = TickArbiter(arbiter, candidates, other + new Vector3(0.01f, 0f, 0f), Dt, "B");
            Assert.That(next.SmoothedPoint.x, Is.LessThan(other.x + 0.005f),
                "The filter must be shut on the new target's first frames, not still open from " +
                "the distance between the two targets.");
        }

        // ------------------------------------------------------------------ conditioners

        /// <summary>The live path: candidate in, arbiter's smoothed point out.</summary>
        private Func<Vector3, float, Vector3> ThroughTheArbiter()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new List<GazeTargetCandidate>(1);
            return (raw, dt) => TickArbiter(arbiter, candidates, raw, dt).SmoothedPoint;
        }

        /// <summary>
        ///     What <c>UpdateSmoothedPoint</c> did before this phase: a fixed exponential low pass
        ///     with a 40 ms time constant, i.e. a 4 Hz cut-off. Reproduced rather than referenced,
        ///     because the point of a control is that it does not move when the code does.
        /// </summary>
        private static Func<Vector3, float, Vector3> FixedFortyMillisecondLowPass()
        {
            var seeded = false;
            Vector3 point = Vector3.zero;
            return (raw, dt) =>
            {
                if (!seeded)
                {
                    point = raw;
                    seeded = true;
                    return point;
                }

                point = Vector3.Lerp(point, raw, 1f - Mathf.Exp(-25f * dt));
                return point;
            };
        }

        private GazeTargetDecision TickArbiter(
            GazeTargetArbiter arbiter,
            List<GazeTargetCandidate> candidates,
            Vector3 point,
            float dt,
            string name = "Speaker")
        {
            candidates.Clear();
            candidates.Add(new GazeTargetCandidate(GazeTargetKind.Player, 10, 1f, null, point, name));
            return arbiter.Tick(candidates, null, true, _profile, dt);
        }

        // ------------------------------------------------------------------ scenarios

        /// <summary>
        ///     Settles on someone standing at conversational distance and off to one side, then
        ///     records three seconds of them talking — which, to the gaze chain, is that person's
        ///     aim point oscillating by two centimetres and nothing else.
        /// </summary>
        private BobMetrics MeasureBob(Func<Vector3, float, Vector3> conditioner, string label)
        {
            using var harness = new GazeShiftTraceHarness(_profile);

            Vector3 origin = harness.EyeCenter;
            Vector3 bearing = Quaternion.AngleAxis(TargetBearingDegrees, harness.Root.up) * harness.Root.forward;
            Vector3 centre = origin + bearing * TargetDistanceMetres;
            Vector3 bobAxis = Vector3.Cross(harness.Root.up, bearing).normalized;

            const float SettleSeconds = 2.5f;
            const float MeasureSeconds = 3f;

            float time = 0f;
            int steps = Mathf.CeilToInt((SettleSeconds + MeasureSeconds) / Dt);
            for (int i = 0; i < steps; i++)
            {
                Vector3 raw = centre + bobAxis *
                    (BobAmplitudeMetres * Mathf.Sin(2f * Mathf.PI * BobFrequencyHz * time));
                harness.Step(conditioner(raw, Dt));
                time += Dt;
            }

            return BobMetrics.Measure(harness, SettleSeconds, label);
        }

        /// <summary>
        ///     The pursuit traces' walk-by, geometry for geometry: a bystander crossing at 1.4 m/s
        ///     with a 2 m closest approach, so the comparison is against the case the pursuit
        ///     assertions were written for.
        /// </summary>
        private GazeShiftTraceHarness RunWalkBy(Func<Vector3, float, Vector3> conditioner)
        {
            var harness = new GazeShiftTraceHarness(_profile);

            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 right = harness.Root.right;

            Vector3 Walker(float x) => origin + forward * 2f + right * x;

            float x = -3f;
            RunFrames(harness, conditioner, 1.5f, () => Walker(x));
            RunFrames(harness, conditioner, 6f / 1.4f, () =>
            {
                x += 1.4f * Dt;
                return Walker(x);
            });
            RunFrames(harness, conditioner, 1.5f, () => Walker(x));
            return harness;
        }

        private static void RunFrames(
            GazeShiftTraceHarness harness,
            Func<Vector3, float, Vector3> conditioner,
            float seconds,
            Func<Vector3> rawPoint)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++) harness.Step(conditioner(rawPoint(), Dt));
        }

        // ------------------------------------------------------------------ metrics

        /// <summary>What the head was asked to do while the target only appeared to move.</summary>
        private readonly struct BobMetrics
        {
            /// <summary>
            ///     Standard deviation of the ladder's allocated head yaw over the measured window.
            /// </summary>
            /// <remarks>
            ///     The planned goal rather than the applied pose, deliberately.
            ///     The head actuator holds a stability band wider than this bob, so
            ///     the applied pose can be perfectly still while the plan underneath it wanders —
            ///     and a plan that wanders is one re-target, one emotion change or one profile with
            ///     a narrower band away from being visible. Measuring the plan measures the input
            ///     to the defect rather than one setting's luck with it.
            /// </remarks>
            public readonly float PlannedYawVariationDegrees;

            /// <summary>Standard deviation of the shift requirement's yaw — the conditioner's own output, in angle.</summary>
            public readonly float RequiredYawVariationDegrees;

            /// <summary>Standard deviation of the applied head yaw.</summary>
            public readonly float AppliedYawVariationDegrees;

            /// <summary>Fastest the head actually moved during the window (deg/s).</summary>
            public readonly float PeakHeadSpeedDegPerSec;

            /// <summary>Whether a ballistic head movement was armed at any point in the window.</summary>
            public readonly bool AnyHeadShift;

            private readonly string _label;

            private BobMetrics(
                string label, float plannedYaw, float requiredYaw, float appliedYaw,
                float peakHeadSpeed, bool anyHeadShift)
            {
                _label = label;
                PlannedYawVariationDegrees = plannedYaw;
                RequiredYawVariationDegrees = requiredYaw;
                AppliedYawVariationDegrees = appliedYaw;
                PeakHeadSpeedDegPerSec = peakHeadSpeed;
                AnyHeadShift = anyHeadShift;
            }

            public static BobMetrics Measure(
                GazeShiftTraceHarness harness, float fromTime, string label)
            {
                var planned = new List<float>(harness.Samples.Count);
                var required = new List<float>(harness.Samples.Count);
                var applied = new List<float>(harness.Samples.Count);
                float peakSpeed = 0f;
                bool anyShift = false;

                foreach (GazeShiftSample sample in harness.Samples)
                {
                    if (sample.Time < fromTime) continue;

                    planned.Add(sample.PlannedHead.x);
                    required.Add(sample.Required.x);
                    applied.Add(sample.Head.x);
                    peakSpeed = Mathf.Max(peakSpeed, Mathf.Abs(sample.HeadVelocity.x));
                    anyShift |= sample.HeadShiftActive;
                }

                var metrics = new BobMetrics(
                    label, StandardDeviation(planned), StandardDeviation(required),
                    StandardDeviation(applied), peakSpeed, anyShift);
                TestContext.Out.WriteLine($"Bob: {metrics}");
                return metrics;
            }

            public override string ToString() =>
                $"[{_label}: head plan yaw sd {PlannedYawVariationDegrees:0.000} deg, " +
                $"requirement yaw sd {RequiredYawVariationDegrees:0.000} deg, " +
                $"applied yaw sd {AppliedYawVariationDegrees:0.000} deg, " +
                $"peak head speed {PeakHeadSpeedDegPerSec:0.00} deg/s, " +
                $"head shift armed {AnyHeadShift}]";
        }

        /// <summary>The conditioned crossing against the same crossing under the shipped filter.</summary>
        private readonly struct WalkByMetrics
        {
            public readonly float ConditionedPeakSpeed;
            public readonly float LegacyPeakSpeed;
            public readonly float MaxHeadYawDifferenceDegrees;
            public readonly float MaxHeadYawDifferenceTime;

            public WalkByMetrics(
                float conditionedPeakSpeed, float legacyPeakSpeed,
                float maxDifference, float maxDifferenceTime)
            {
                ConditionedPeakSpeed = conditionedPeakSpeed;
                LegacyPeakSpeed = legacyPeakSpeed;
                MaxHeadYawDifferenceDegrees = maxDifference;
                MaxHeadYawDifferenceTime = maxDifferenceTime;
            }

            public float PeakSpeedRatio =>
                LegacyPeakSpeed > 0.001f ? ConditionedPeakSpeed / LegacyPeakSpeed : 1f;

            public override string ToString() =>
                $"[peak head speed {ConditionedPeakSpeed:0.00} vs {LegacyPeakSpeed:0.00} deg/s " +
                $"(ratio {PeakSpeedRatio:0.000}), worst head yaw difference " +
                $"{MaxHeadYawDifferenceDegrees:0.000} deg at t={MaxHeadYawDifferenceTime:0.00}s]";
        }

        private static WalkByMetrics CompareWalkBy(
            GazeShiftTraceHarness conditioned, GazeShiftTraceHarness legacy)
        {
            Assert.That(conditioned.Samples.Count, Is.EqualTo(legacy.Samples.Count),
                "The two runs must be frame-aligned for a per-frame comparison to mean anything.");

            // The crossing proper: the walker inside ±1.4 m of closest approach, where the
            // angular rate peaks and any difference in tracking would show.
            const float From = 1.5f + (3f - 1.4f) / 1.4f;
            const float To = 1.5f + (3f + 1.4f) / 1.4f;

            float conditionedPeak = 0f, legacyPeak = 0f;
            float worst = 0f, worstTime = 0f;

            for (int i = 0; i < conditioned.Samples.Count; i++)
            {
                GazeShiftSample a = conditioned.Samples[i];
                GazeShiftSample b = legacy.Samples[i];
                if (a.Time < From || a.Time > To) continue;

                conditionedPeak = Mathf.Max(conditionedPeak, Mathf.Abs(a.HeadVelocity.x));
                legacyPeak = Mathf.Max(legacyPeak, Mathf.Abs(b.HeadVelocity.x));

                float difference = Mathf.Abs(a.Head.x - b.Head.x);
                if (difference <= worst) continue;

                worst = difference;
                worstTime = a.Time;
            }

            return new WalkByMetrics(conditionedPeak, legacyPeak, worst, worstTime);
        }

        private static float StandardDeviation(List<float> values)
        {
            if (values.Count < 2) return 0f;

            float mean = 0f;
            foreach (float value in values) mean += value;
            mean /= values.Count;

            float sumSquares = 0f;
            foreach (float value in values) sumSquares += (value - mean) * (value - mean);
            return Mathf.Sqrt(sumSquares / (values.Count - 1));
        }
    }
}
