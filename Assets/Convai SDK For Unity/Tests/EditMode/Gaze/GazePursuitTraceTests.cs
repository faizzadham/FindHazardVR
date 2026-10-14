using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Pursuit quality: what the head does while the thing it is looking at KEEPS MOVING.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The shift work is judged on step responses — a target that jumps and then holds
    ///         still. These traces ask the other question: a player orbiting the character,
    ///         walking past it, drifting and pausing. A human head in that situation moves
    ///         continuously with the target; it does not park, wait for the error to build, and
    ///         reposition in a burst. A head that only ever rests at discrete orientations is
    ///         the "turns to the edges of five slices" read, however smooth each burst is.
    ///     </para>
    ///     <para>
    ///         Numbers in the assertions are the intended targets, not the values that happened
    ///         to come out — same doctrine as <see cref="GazeShiftTraceTests" />. Each trace is
    ///         also dumped to <c>Logs/GazePursuitTraces/*.csv</c> so a failure can be read as a
    ///         curve, not reconstructed from one number.
    ///     </para>
    /// </remarks>
    public sealed class GazePursuitTraceTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        // ------------------------------------------------------------------ scenarios

        /// <summary>
        ///     A conversational orbit: the player strafes slowly around the character at talking
        ///     distance. 12°/s for 5 s is a 60° arc — unhurried, and nothing a person's head
        ///     would ratchet through.
        /// </summary>
        [Test]
        public void SlowOrbit_HeadPursuesContinuously()
        {
            using GazeShiftTraceHarness harness = RunSweep(
                settleSeconds: 1.5f, degreesPerSecond: 12f, sweepSeconds: 5f, holdSeconds: 1.5f);

            // Bounded to the pre-saturation band for the same reason as the brisk orbit: past
            // the yaw soft-clamp knee the question is when the BODY arrives, not
            // whether the head pursues.
            PursuitMetrics metrics = MeasureSweepWindow(
                harness, sweepRate: 12f, maxRequiredAbsDegrees: 40f);
            DumpTrace(harness, nameof(SlowOrbit_HeadPursuesContinuously), metrics);

            Assert.That(metrics.LongestStallSeconds, Is.LessThan(0.15f),
                "While the target keeps moving and the head's own share is unmet, the head " +
                "must keep moving too — a parked head here is the detent read. " + metrics);
            Assert.That(metrics.VelocityDipCount, Is.LessThanOrEqualTo(1),
                "Pursuit must not be chopped into bursts: each dip to (near) rest with the " +
                "target still moving is one visible 'slice edge'. " + metrics);
            Assert.That(metrics.RippleCv, Is.LessThan(0.35f),
                "Head speed during a steady sweep should approximate the sweep, not oscillate " +
                "between zero and catch-up speed. " + metrics);
        }

        /// <summary>
        ///     A brisk orbit: 40°/s for 2 s. Fast enough that frame-to-frame share deltas start
        ///     brushing the movement trigger, which is where burst-parking is expected to show
        ///     first if the tracking lane cannot pursue.
        /// </summary>
        [Test]
        public void BriskOrbit_HeadPursuesContinuously()
        {
            using GazeShiftTraceHarness harness = RunSweep(
                settleSeconds: 1.5f, degreesPerSecond: 40f, sweepSeconds: 2f, holdSeconds: 1.5f);

            // Bounded to the pre-saturation band: past ~40° required, the head's share bends
            // into the yaw soft-clamp and what happens THERE is the body's turn arriving in
            // time, which is a body-arrival gate, not a pursuit property.
            PursuitMetrics metrics = MeasureSweepWindow(
                harness, sweepRate: 40f, maxRequiredAbsDegrees: 40f);
            DumpTrace(harness, nameof(BriskOrbit_HeadPursuesContinuously), metrics);

            Assert.That(metrics.LongestStallSeconds, Is.LessThan(0.25f),
                "At a brisk sweep the head has no reason to wait at all. " + metrics);
            Assert.That(metrics.VelocityDipCount, Is.Zero,
                "Steady pursuit is one carry; every dip to rest is stick-slip. " + metrics);
            Assert.That(metrics.RippleCv, Is.LessThan(0.35f),
                "Head speed over the steady band should sit near the sweep rate. " + metrics);
        }

        /// <summary>
        ///     A bystander walking past: straight line, 1.4 m/s, closest approach 2 m. The
        ///     angular rate is bell-shaped (peaks ~40°/s as they cross), which is the everyday
        ///     case every conversational character faces.
        /// </summary>
        [Test]
        public void WalkingBystander_HeadTracksWithoutDetents() =>
            AssertWalkByTracksContinuously(
                GazeShiftTraceHarness.FrameSeconds, nameof(WalkingBystander_HeadTracksWithoutDetents));

        /// <summary>
        ///     The same crossing at 30 fps. The movement detector and pursuit classifier are
        ///     deg/s-based by construction, but "independent by construction" is a design intent —
        ///     this trace is what makes it a measurement. A per-frame-delta detector passes at
        ///     60 Hz and chops the same walk into ballistic bursts the moment the editor slows.
        /// </summary>
        [Test]
        public void WalkingBystander_TracksTheSameAtThirtyFps() =>
            AssertWalkByTracksContinuously(
                1f / 30f, nameof(WalkingBystander_TracksTheSameAtThirtyFps));

        private void AssertWalkByTracksContinuously(float frameDelta, string traceName)
        {
            using var harness = new GazeShiftTraceHarness(_profile)
            {
                FrameDeltaSeconds = frameDelta
            };
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 right = harness.Root.right;

            // Settle on them standing off to the left, then they walk across.
            Vector3 Walker(float x) => origin + forward * 2f + right * x;
            RunFrames(harness, 1.5f, () => Walker(-3f));
            float x = -3f;
            RunFrames(harness, 6f / 1.4f, () =>
            {
                x += 1.4f * harness.FrameDeltaSeconds;
                return Walker(x);
            });
            RunFrames(harness, 1.5f, () => Walker(x));

            // Central 2 s of the crossing: |x| < 1.4m, angular rate 20–40°/s, required yaw
            // sweeping through zero — the head must be in continuous carry the whole time.
            PursuitMetrics metrics = MeasureWindow(
                harness, fromTime: 1.5f + (3f - 1.4f) / 1.4f, toTime: 1.5f + (3f + 1.4f) / 1.4f,
                minRequiredAbsDegrees: 0f, minRequiredRateDegPerSec: 10f);
            DumpTrace(harness, traceName, metrics);

            Assert.That(metrics.LongestStallSeconds, Is.LessThan(0.25f),
                "Someone walking past is tracked with the head, continuously. " + metrics);
            Assert.That(metrics.VelocityDipCount, Is.LessThanOrEqualTo(1),
                "The crossing is one sustained movement, not a chain of repositionings. " + metrics);
            Assert.That(metrics.PeakSpeed, Is.LessThan(metrics.PeakRequiredRate * 1.5f),
                "The head may not move meaningfully faster than the thing it is following — " +
                "excess speed here is a recruitment gain adding unauthored velocity. " + metrics);
        }

        /// <summary>
        ///     Drift, pause, drift again: 20°/s for 1.5 s, a 1 s pause, then 1.5 s more. The
        ///     pause is where a stability band earns its keep; the resume is where it must not
        ///     charge an entry fee (a dead interval, then a lurch) all over again.
        /// </summary>
        [Test]
        public void SweepPausesAndResumes_HeadNeitherFreezesNorLurches()
        {
            using var harness = new GazeShiftTraceHarness(_profile);
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 up = harness.Root.up;

            float yaw = 0f;
            Vector3 Orbit() => origin + Quaternion.AngleAxis(yaw, up) * (forward * 2f);
            RunFrames(harness, 1.5f, Orbit);
            RunFrames(harness, 1.5f, () => { yaw += 20f * harness.FrameDeltaSeconds; return Orbit(); });
            RunFrames(harness, 1f, Orbit);
            float resumeTime = 4f;
            RunFrames(harness, 1.5f, () => { yaw += 20f * harness.FrameDeltaSeconds; return Orbit(); });
            RunFrames(harness, 1.5f, Orbit);

            // After the resume the head must be moving again quickly — the band may absorb a
            // couple of degrees, but 0.5 s of statue is a visible beat at 20°/s.
            float restart = HeadRestartLatency(harness, resumeTime, speedFloorDegPerSec: 4f);
            PursuitMetrics resumed = MeasureWindow(
                harness, fromTime: resumeTime, toTime: resumeTime + 1.5f,
                minRequiredAbsDegrees: 0f, minRequiredRateDegPerSec: 5f);
            DumpTrace(harness, nameof(SweepPausesAndResumes_HeadNeitherFreezesNorLurches), resumed);

            Assert.That(restart, Is.LessThan(0.35f),
                $"Head took {restart:0.00}s to move again after the target resumed. " + resumed);
            Assert.That(resumed.PeakSpeed, Is.LessThan(20f * 3.5f),
                "The resume must not be paid for with a whip: catching up a banded error at " +
                "several times the sweep rate is the lurch half of freeze-then-lurch. " + resumed);
        }

        // ------------------------------------------------------------------ drivers

        private GazeShiftTraceHarness RunSweep(
            float settleSeconds, float degreesPerSecond, float sweepSeconds, float holdSeconds)
        {
            var harness = new GazeShiftTraceHarness(_profile);
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 up = harness.Root.up;

            float yaw = 0f;
            Vector3 Orbit() => origin + Quaternion.AngleAxis(yaw, up) * (forward * 2f);
            RunFrames(harness, settleSeconds, Orbit);
            RunFrames(harness, sweepSeconds, () =>
            {
                yaw += degreesPerSecond * harness.FrameDeltaSeconds;
                return Orbit();
            });
            RunFrames(harness, holdSeconds, Orbit);
            return harness;
        }

        private static void RunFrames(
            GazeShiftTraceHarness harness, float seconds, System.Func<Vector3> targetPoint)
        {
            int steps = Mathf.CeilToInt(seconds / harness.FrameDeltaSeconds);
            for (int i = 0; i < steps; i++) harness.Step(targetPoint());
        }

        // ------------------------------------------------------------------ metrics

        /// <summary>What the head did over a window in which the target was in motion.</summary>
        private readonly struct PursuitMetrics
        {
            public readonly float WindowSeconds;
            public readonly float LongestStallSeconds;
            public readonly int StallCount;
            public readonly int VelocityDipCount;
            public readonly float MeanSpeed;
            public readonly float PeakSpeed;
            public readonly float RippleCv;
            public readonly float PeakRequiredRate;

            public PursuitMetrics(
                float windowSeconds, float longestStallSeconds, int stallCount,
                int velocityDipCount, float meanSpeed, float peakSpeed, float rippleCv,
                float peakRequiredRate)
            {
                WindowSeconds = windowSeconds;
                LongestStallSeconds = longestStallSeconds;
                StallCount = stallCount;
                VelocityDipCount = velocityDipCount;
                MeanSpeed = meanSpeed;
                PeakSpeed = peakSpeed;
                RippleCv = rippleCv;
                PeakRequiredRate = peakRequiredRate;
            }

            public override string ToString() =>
                $"[window {WindowSeconds:0.00}s: longest stall {LongestStallSeconds:0.00}s, " +
                $"{StallCount} stalls, {VelocityDipCount} velocity dips, head speed " +
                $"mean {MeanSpeed:0.0} peak {PeakSpeed:0.0} deg/s, ripple cv {RippleCv:0.00}, " +
                $"peak target rate {PeakRequiredRate:0.0} deg/s]";
        }

        /// <summary>
        ///     The steady part of a constant-rate sweep: past the recruitment band (|required| >
        ///     20°, so the ladder has genuinely handed the head a share) and still sweeping.
        /// </summary>
        private static PursuitMetrics MeasureSweepWindow(
            GazeShiftTraceHarness harness,
            float sweepRate,
            float maxRequiredAbsDegrees = float.PositiveInfinity)
            => MeasureWindow(harness, fromTime: 0f, toTime: float.PositiveInfinity,
                minRequiredAbsDegrees: 20f, minRequiredRateDegPerSec: sweepRate * 0.5f,
                maxRequiredAbsDegrees: maxRequiredAbsDegrees);

        private static PursuitMetrics MeasureWindow(
            GazeShiftTraceHarness harness,
            float fromTime,
            float toTime,
            float minRequiredAbsDegrees,
            float minRequiredRateDegPerSec,
            float maxRequiredAbsDegrees = float.PositiveInfinity)
        {
            IReadOnlyList<GazeShiftSample> samples = harness.Samples;
            float dt = harness.FrameDeltaSeconds;

            float windowSeconds = 0f;
            float longestStall = 0f, currentStall = 0f;
            float peakRequiredRate = 0f;
            int stallCount = 0;
            bool inStall = false;

            // Dip detection: a fall below the low bar followed by a rise back above the high
            // bar counts once — Schmitt-style, so frame noise cannot inflate the count.
            int dipCount = 0;
            bool inDip = false;

            var speeds = new List<float>(samples.Count);

            for (int i = 1; i < samples.Count; i++)
            {
                GazeShiftSample s = samples[i];
                if (s.Time < fromTime || s.Time > toTime) continue;

                float requiredRate = Mathf.Abs(s.Required.x - samples[i - 1].Required.x) / dt;
                if (Mathf.Abs(s.Required.x) < minRequiredAbsDegrees) continue;
                if (Mathf.Abs(s.Required.x) > maxRequiredAbsDegrees) continue;
                if (requiredRate < minRequiredRateDegPerSec) continue;

                peakRequiredRate = Mathf.Max(peakRequiredRate, requiredRate);
                windowSeconds += dt;
                float speed = Mathf.Abs(s.HeadVelocity.x);
                speeds.Add(speed);

                float stallFloor = Mathf.Max(2f, requiredRate * 0.2f);
                if (speed < stallFloor)
                {
                    currentStall += dt;
                    if (!inStall && currentStall >= 3f * dt) { inStall = true; stallCount++; }
                    longestStall = Mathf.Max(longestStall, currentStall);
                }
                else
                {
                    currentStall = 0f;
                    inStall = false;
                }

                float dipLow = requiredRate * 0.25f;
                float dipHigh = requiredRate * 0.6f;
                if (!inDip && speed < dipLow) { inDip = true; dipCount++; }
                else if (inDip && speed > dipHigh) inDip = false;
            }

            float mean = 0f, peak = 0f;
            foreach (float s in speeds) { mean += s; peak = Mathf.Max(peak, s); }
            mean = speeds.Count > 0 ? mean / speeds.Count : 0f;
            float variance = 0f;
            foreach (float s in speeds) variance += (s - mean) * (s - mean);
            float cv = mean > 0.5f && speeds.Count > 1
                ? Mathf.Sqrt(variance / (speeds.Count - 1)) / mean
                : 0f;

            // The first dip is the window opening with the head not yet up to speed — entry,
            // not stick-slip — so it is not charged. Everything after it is.
            return new PursuitMetrics(
                windowSeconds, longestStall, stallCount, Mathf.Max(0, dipCount - 1), mean, peak, cv,
                peakRequiredRate);
        }

        /// <summary>Seconds after <paramref name="fromTime" /> until head speed first exceeds the floor.</summary>
        private static float HeadRestartLatency(
            GazeShiftTraceHarness harness, float fromTime, float speedFloorDegPerSec)
        {
            foreach (GazeShiftSample s in harness.Samples)
            {
                if (s.Time < fromTime) continue;
                if (Mathf.Abs(s.HeadVelocity.x) >= speedFloorDegPerSec) return s.Time - fromTime;
            }

            return float.PositiveInfinity;
        }

        // ------------------------------------------------------------------ trace dump

        private static void DumpTrace(
            GazeShiftTraceHarness harness, string name, PursuitMetrics metrics)
        {
            TestContext.Out.WriteLine($"{name}: {metrics}");

            string directory = Path.Combine(
                UnityEngine.Application.dataPath, "..", "Logs", "GazePursuitTraces");
            Directory.CreateDirectory(directory);

            var csv = new StringBuilder(harness.Samples.Count * 64);
            csv.AppendLine("t,requiredYaw,plannedHeadYaw,headYaw,headVelYaw,torsoYaw,eyeYaw,depth");
            foreach (GazeShiftSample s in harness.Samples)
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:0.0000},{1:0.000},{2:0.000},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7}\n",
                    s.Time, s.Required.x, s.PlannedHead.x, s.Head.x,
                    s.HeadVelocity.x, s.Torso.x, s.Eye.x, (int)s.Depth);

            string path = Path.Combine(directory, name + ".csv");
            File.WriteAllText(path, csv.ToString());
            TestContext.Out.WriteLine($"{name}: trace written to {path}");
        }
    }
}
