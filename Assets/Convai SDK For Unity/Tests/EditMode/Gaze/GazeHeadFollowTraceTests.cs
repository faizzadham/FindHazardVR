using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The head's FOLLOWING lane, measured: what the head does with the share the ladder
    ///     allocates it while that share keeps moving.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <see cref="GazePursuitTraceTests" /> asks whether the head keeps up with the
    ///         TARGET. These traces ask the narrower and more falsifiable question one layer in:
    ///         given <see cref="GazeShiftSample.PlannedHead" /> — the share the head was actually
    ///         handed — is the applied head pose a faithful, bandwidth-limited follow of it?
    ///         That is the contract of the tracking lane alone, and it is the thing the dead
    ///         band, the pursuit classifier and the transparent tracking filters all failed.
    ///     </para>
    ///     <para>
    ///         The failure they produced was measured on the real head bone before the lane was
    ///         replaced: a 4 deg/s camera orbit parked the head for 71 of 599 frames and moved it
    ///         in 8-10 deg/s bursts; a 55 deg/s sweep bought a 0.12 s dead start and then 70 deg/s
    ///         of catch-up; a 1 Hz shake came through at gain 0.97 with the acceleration pinned at
    ///         its cap. Every gate below is one of those numbers turned into a bound.
    ///     </para>
    ///     <para>
    ///         Each trace is dumped to <c>Logs/GazePursuitTraces/*.csv</c>, the same place the
    ///         pursuit traces go, so a failure can be read as a curve rather than reconstructed
    ///         from one number.
    ///     </para>
    /// </remarks>
    public sealed class GazeHeadFollowTraceTests
    {
        /// <summary>The head's acceleration envelope, <c>HeadTorsoSolver.Limits.HeadMaxAccel</c>.</summary>
        private const float HeadMaxAccelDegPerSecSquared = 1500f;

        /// <summary>
        ///     The share speed above which "did the head outrun its share" stops being a
        ///     following question: past it the share is saturating against the yaw soft clamp and
        ///     what happens is the body's business.
        /// </summary>
        private const float FollowBandMaxShareSpeed = 60f;

        /// <summary>
        ///     How long after the share last left the follow band a frame is still disqualified
        ///     from the speed comparison.
        /// </summary>
        /// <remarks>
        ///     Two head response times, because the head's speed on any frame is a function of
        ///     the share over the preceding response window, not of the share on that frame. The
        ///     ladder RECRUITS the head at the start of a sweep — the share itself moves at
        ///     110 deg/s for a few frames as the allocation fraction climbs — and without this
        ///     window the momentum legitimately earned there is charged to a frame whose
        ///     instantaneous share speed has already fallen back to 55. Excluding the tail is
        ///     what makes the band mean "the lane operating inside its band" rather than "one
        ///     frame that happened to be inside it".
        /// </remarks>
        private const float FollowBandSettleSeconds = 0.4f;

        /// <summary>Share speed below which a frame is "the share is holding", not "the head is trailing it".</summary>
        private const float MovingShareFloor = 10f;

        /// <summary>Frames before this are the harness's own bind and first fixation, not following.</summary>
        private const float WarmupSeconds = 0.3f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        // ------------------------------------------------------------------ never parks

        /// <summary>
        ///     The measured defect, reproduced as a gate: a 3 deg/s orbit is the slowest motion a
        ///     conversational character is asked to follow, and it is precisely where a dead band
        ///     swallows the whole signal and hands it back in bursts.
        /// </summary>
        [Test]
        public void CreepingOrbit_HeadNeverParks()
        {
            using GazeShiftTraceHarness harness = RunOrbit(
                settleSeconds: 1.5f, degreesPerSecond: 3f, sweepSeconds: 8f, holdSeconds: 0.5f);

            FollowMetrics metrics = Measure(harness, WarmupSeconds, float.PositiveInfinity);
            Dump(harness, nameof(CreepingOrbit_HeadNeverParks), metrics);

            Assert.That(metrics.ParkFrames, Is.Zero,
                "At 3 deg/s the head must creep with its share, not sit still and catch up: a " +
                "parked frame here is the detent read the dead band produced. " + metrics);
            Assert.That(metrics.PeakAcceleration, Is.LessThanOrEqualTo(HeadMaxAccelDegPerSecSquared * 1.02f),
                "Head acceleration must stay inside the envelope. " + metrics);
        }

        // ------------------------------------------------------------------ speed and trail law

        /// <summary>
        ///     A brisk 110 deg sweep at 55 deg/s, then a stop. Three claims at once: the head
        ///     never outruns its own share, it trails it by the follow law, and it settles when
        ///     the share does instead of creeping for seconds.
        /// </summary>
        [Test]
        public void BriskSweep_TrailsItsShareByTheFollowLawAndNeverOutrunsIt()
        {
            const float sweepRate = 55f;
            const float settle = 1.5f;
            const float sweepSeconds = 110f / sweepRate;
            float stopTime = settle + sweepSeconds;

            using GazeShiftTraceHarness harness = RunOrbit(
                settleSeconds: settle, degreesPerSecond: sweepRate, sweepSeconds: sweepSeconds,
                holdSeconds: 2f);

            FollowMetrics metrics = Measure(harness, WarmupSeconds, stopTime);
            Dump(harness, nameof(BriskSweep_TrailsItsShareByTheFollowLawAndNeverOutrunsIt), metrics);

            float followSeconds = _profile.HeadFollowSeconds;
            float predictedTrail = metrics.MeanShareSpeedInBand * followSeconds * 0.5f;

            TestContext.Out.WriteLine(
                $"trail: predicted {predictedTrail:0.00} deg from mean share speed " +
                $"{metrics.MeanShareSpeedInBand:0.0} deg/s, measured {metrics.MeanTrailInBand:0.00} deg");

            Assert.That(metrics.BandFrames, Is.GreaterThan(30),
                "The speed comparison below is only worth making if the trace spent real time "
                + "inside the follow band — a vacuous window would confirm any theory. " + metrics);
            Assert.That(metrics.ParkFrames, Is.Zero,
                "A head being handed a steadily moving share must move every frame. " + metrics);
            Assert.That(metrics.PeakHeadSpeedInBand,
                Is.LessThanOrEqualTo(metrics.PeakShareSpeedInBand * 1.15f),
                "The head may not outrun the share it was allocated — excess speed here is " +
                "catch-up, which is the second half of freeze-then-whip. " + metrics);
            Assert.That(metrics.MeanTrailInBand, Is.LessThanOrEqualTo(predictedTrail * 1.6f),
                $"A constant-velocity share is trailed by about v*HeadFollowSeconds/2 = " +
                $"{predictedTrail:0.00} deg; measured {metrics.MeanTrailInBand:0.00} deg. " + metrics);
            Assert.That(metrics.PeakAcceleration, Is.LessThanOrEqualTo(HeadMaxAccelDegPerSecSquared * 1.02f),
                "Head acceleration must stay inside the envelope. " + metrics);
        }

        /// <summary>
        ///     The same sweep, stopped INSIDE the linear band: does the head settle when its
        ///     share does, and then hold?
        /// </summary>
        /// <remarks>
        ///     Split off from the 110 deg sweep deliberately. By the time that one stops, the
        ///     share has been pinned at the yaw soft clamp for most of a second and the head is
        ///     already sitting on its final value — so a settle gate measured there cannot fail,
        ///     whatever the lane does. 40 deg at the same rate stops with the head still trailing
        ///     by the follow law, which is the only condition under which "it settled" is a
        ///     claim. The guard below makes that a checked precondition rather than an assumption.
        /// </remarks>
        [Test]
        public void SweepThatStopsInsideTheLinearBand_SettlesAndThenHolds()
        {
            const float sweepRate = 55f;
            const float settle = 1.5f;
            const float sweepSeconds = 40f / sweepRate;
            float stopTime = settle + sweepSeconds;

            using GazeShiftTraceHarness harness = RunOrbit(
                settleSeconds: settle, degreesPerSecond: sweepRate, sweepSeconds: sweepSeconds,
                holdSeconds: 2.5f);

            FollowMetrics metrics = Measure(harness, WarmupSeconds, stopTime);
            Dump(harness, nameof(SweepThatStopsInsideTheLinearBand_SettlesAndThenHolds), metrics);

            float followSeconds = _profile.HeadFollowSeconds;
            float toClose = DistanceStillToCloseAt(harness, stopTime);
            float settleSeconds = SettleTimeAfter(harness, stopTime, toleranceDegrees: 0.5f);
            float creep = CreepBetween(harness, stopTime + 0.5f, stopTime + 2f);

            TestContext.Out.WriteLine(
                $"settle: {toClose:0.00} deg still to close at the stop, settled in " +
                $"{settleSeconds:0.000}s, crept {creep:0.000} deg");

            Assert.That(toClose, Is.GreaterThan(1f),
                "The head must actually have been trailing when the share stopped, or this " +
                $"measures nothing; it had {toClose:0.00} deg to close.");
            Assert.That(settleSeconds, Is.LessThanOrEqualTo(2f * followSeconds),
                $"After the share stops the head must settle within two response times " +
                $"({2f * followSeconds:0.00}s); took {settleSeconds:0.000}s.");
            Assert.That(creep, Is.LessThan(1f),
                $"and then hold: it drifted {creep:0.000} deg between 0.5 s and 2 s after the stop.");
        }

        // ------------------------------------------------------------------ reversals

        /// <summary>
        ///     A target swinging through the character's forward axis: the share reverses sign
        ///     twice a cycle, which is where a lane that banks error and discharges it produces a
        ///     head moving the wrong way for a frame.
        /// </summary>
        [Test]
        public void ReversingSweep_HeadNeverMovesAgainstItsShare()
        {
            using GazeShiftTraceHarness harness = RunSine(
                settleSeconds: 1.5f, amplitudeDegrees: 20f, hertz: 0.15f, seconds: 10f);

            FollowMetrics metrics = Measure(harness, 1.5f + WarmupSeconds, float.PositiveInfinity);
            Dump(harness, nameof(ReversingSweep_HeadNeverMovesAgainstItsShare), metrics);

            Assert.That(metrics.BandFrames, Is.GreaterThan(30),
                "The speed comparison below is only worth making if the trace spent real time "
                + "inside the follow band — a vacuous window would confirm any theory. " + metrics);
            Assert.That(metrics.CounterMotionFrames, Is.Zero,
                "While the share moves monotonically in one direction the head must not step the " +
                "other way. " + metrics);
            Assert.That(metrics.PeakAcceleration, Is.LessThanOrEqualTo(HeadMaxAccelDegPerSecSquared * 1.02f),
                "Head acceleration must stay inside the envelope. " + metrics);
            Assert.That(metrics.PeakHeadSpeedInBand,
                Is.LessThanOrEqualTo(metrics.PeakShareSpeedInBand * 1.15f),
                "The head may not outrun its share through a reversal either. " + metrics);
        }

        // ------------------------------------------------------------------ bandwidth

        /// <summary>
        ///     A 1 Hz shake — a hand-held camera, or a player jittering. The lane's response time
        ///     is a property of the neck, so this must come through visibly attenuated. Before the
        ///     rewrite it came through at gain 0.97 with the acceleration cap doing the shaping,
        ///     which is what "the head is glued to the camera" looks like in a number.
        /// </summary>
        [Test]
        public void ShakeAtOneHertz_IsAttenuatedByTheLanesBandwidth()
        {
            using GazeShiftTraceHarness harness = RunSine(
                settleSeconds: 1.5f, amplitudeDegrees: 16f, hertz: 1f, seconds: 3f);

            float gain = MeasureGain(harness, fromTime: 1.5f + 1f, toTime: 1.5f + 3f);
            FollowMetrics metrics = Measure(harness, 1.5f + WarmupSeconds, float.PositiveInfinity);
            Dump(harness, nameof(ShakeAtOneHertz_IsAttenuatedByTheLanesBandwidth), metrics);
            TestContext.Out.WriteLine($"1 Hz tracking gain: {gain:0.000}");

            // The composition: PursuitTracker alone measures 0.85 at 1 Hz, and the solver's
            // 0.05 s goal-velocity estimator adds phase lag to the lead term, raising the whole
            // lane to ~0.93 (closed form 0.94); the pre-rewrite lane measured 0.97 with the
            // acceleration cap doing the shaping. The gate catches a return to transparency, not
            // the last three points.
            Assert.That(gain, Is.LessThanOrEqualTo(0.95f),
                $"A 1 Hz shake must be attenuated, not reproduced; head/share amplitude gain was {gain:0.000}.");
        }

        /// <summary>
        ///     The other end of the same claim: a 0.2 Hz drift is inside the neck's bandwidth and
        ///     must be followed essentially intact. A lane that passes the shake gate by being
        ///     merely sluggish fails here.
        /// </summary>
        [Test]
        public void DriftAtFifthOfAHertz_IsFollowedIntact()
        {
            using GazeShiftTraceHarness harness = RunSine(
                settleSeconds: 1.5f, amplitudeDegrees: 20f, hertz: 0.2f, seconds: 10f);

            float gain = MeasureGain(harness, fromTime: 1.5f + 5f, toTime: 1.5f + 10f);
            FollowMetrics metrics = Measure(harness, 1.5f + WarmupSeconds, float.PositiveInfinity);
            Dump(harness, nameof(DriftAtFifthOfAHertz_IsFollowedIntact), metrics);
            TestContext.Out.WriteLine($"0.2 Hz tracking gain: {gain:0.000}");

            Assert.That(gain, Is.GreaterThanOrEqualTo(0.9f),
                $"A 0.2 Hz drift is inside the neck's bandwidth and must come through essentially " +
                $"intact; gain was {gain:0.000}.");
        }

        // ------------------------------------------------------------------ jerky input

        /// <summary>
        ///     A target driven by piecewise-constant random velocity: the adversarial case for
        ///     any lane that classifies motion before following it, because there is no steady
        ///     state to classify. Seeded, so a failure is reproducible.
        /// </summary>
        [Test]
        public void JerkyOrbit_HeadKeepsFollowingInsideItsEnvelope()
        {
            using var harness = new GazeShiftTraceHarness(_profile);
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 up = harness.Root.up;

            float yaw = 0f;
            Vector3 Orbit() => origin + (Quaternion.AngleAxis(yaw, up) * (forward * 2f));

            RunFrames(harness, 1.5f, Orbit);

            var random = new System.Random(20260904);
            float remaining = 6f;
            while (remaining > 0f)
            {
                float segment = Mathf.Min(remaining, Mathf.Lerp(0.08f, 0.45f, (float)random.NextDouble()));
                float rate = Mathf.Lerp(-120f, 120f, (float)random.NextDouble());
                RunFrames(harness, segment, () =>
                {
                    yaw = Mathf.Clamp(yaw + (rate * harness.FrameDeltaSeconds), -70f, 70f);
                    return Orbit();
                });
                remaining -= segment;
            }

            RunFrames(harness, 1f, Orbit);

            FollowMetrics metrics = Measure(harness, 1.5f + WarmupSeconds, float.PositiveInfinity);
            Dump(harness, nameof(JerkyOrbit_HeadKeepsFollowingInsideItsEnvelope), metrics);

            Assert.That(metrics.BandFrames, Is.GreaterThan(30),
                "The speed comparison below is only worth making if the trace spent real time "
                + "inside the follow band — a vacuous window would confirm any theory. " + metrics);
            Assert.That(metrics.PeakAcceleration, Is.LessThanOrEqualTo(HeadMaxAccelDegPerSecSquared * 1.02f),
                "However jagged the share, the applied head pose stays inside the acceleration " +
                "envelope — that is what the envelope on the composed output is for. " + metrics);
            // Bound was 2.5 frames; the goal-velocity estimate now drops immediately on a
            // reversal (smoothed only while speeding up) instead of easing down, so a jerky
            // orbit's direction flips cost the head one more frame before it picks the share
            // back up. 3.5 frames still catches an actual multi-frame park.
            Assert.That(metrics.LongestParkSeconds, Is.LessThan(3.5f * harness.FrameDeltaSeconds),
                "The head may pass through zero speed as the share reverses, but it must not " +
                "PARK: a stretch of frames with a moving share and a still head is the defect. "
                + metrics);
            Assert.That(metrics.PeakHeadSpeedInBand,
                Is.LessThanOrEqualTo(metrics.PeakShareSpeedInBand * 1.15f),
                "and it must not answer a jerky share with a whip. " + metrics);
        }

        // ------------------------------------------------------------------ frame rate

        /// <summary>
        ///     The same 55 deg/s sweep at 30 and 60 fps. The lane substeps its integration
        ///     precisely so this holds; "independent by construction" is a design intent until two
        ///     traces make it a measurement.
        /// </summary>
        [Test]
        public void BriskSweep_PeaksTheSameAtThirtyAndSixtyFps()
        {
            float sixty = SweepPeakHeadSpeed(GazeShiftTraceHarness.FrameSeconds, "SixtyFps");
            float thirty = SweepPeakHeadSpeed(1f / 30f, "ThirtyFps");

            float divergence = Mathf.Abs(sixty - thirty) / Mathf.Max(sixty, thirty);
            TestContext.Out.WriteLine(
                $"peak head speed: 60 fps {sixty:0.0} deg/s, 30 fps {thirty:0.0} deg/s, " +
                $"divergence {divergence * 100f:0.0}%");

            Assert.That(divergence, Is.LessThanOrEqualTo(0.15f),
                $"The same sweep must produce the same peak head speed at 30 and 60 fps; " +
                $"60 fps {sixty:0.0}, 30 fps {thirty:0.0} deg/s, {divergence * 100f:0.0}% apart.");
        }

        private float SweepPeakHeadSpeed(float frameDelta, string traceSuffix)
        {
            using var harness = new GazeShiftTraceHarness(_profile) { FrameDeltaSeconds = frameDelta };
            DriveOrbit(harness, settleSeconds: 1.5f, degreesPerSecond: 55f, sweepSeconds: 2f,
                holdSeconds: 1f);

            FollowMetrics metrics = Measure(harness, WarmupSeconds, 3.5f);
            Dump(harness, nameof(BriskSweep_PeaksTheSameAtThirtyAndSixtyFps) + "_" + traceSuffix, metrics);
            return metrics.PeakHeadSpeedInBand;
        }

        // ------------------------------------------------------------------ sustained motion

        /// <summary>
        ///     A share in sustained fast motion must be LEARNED, not read as a fresh decision
        ///     every frame. At a low shift trigger and a modest frame rate the per-frame share
        ///     step clears even the in-flight re-trigger bar, so a movement detector that only
        ///     updates its goal-velocity estimate on quiet frames never gets a quiet frame — it
        ///     re-begins the ballistic movement every frame and the head is held in that
        ///     movement's opening phase for as long as the sweep lasts.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The arithmetic that picks this scenario: at the profile's minimum trigger
        ///         (0.5°, the <c>[Range]</c> floor) the in-flight bar is
        ///         <c>0.5 × RetriggerHysteresis(3) = 1.5°</c>, while a 55 deg/s share advances
        ///         <c>55/30 = 1.83°</c> per frame at 30 fps. With the estimate pinned at zero the
        ///         whole 1.83° is charged as excess, every frame, forever — the condition the
        ///         consecutive-trigger fold in <c>HeadTorsoSolver.DetectMovement</c> exists to
        ///         break. Once the second consecutive trigger is folded in, the estimate rises at
        ///         <c>1 - exp(-20 × 1/30) ≈ 0.49</c> per frame and converges on the share's rate
        ///         within a handful of frames, after which the excess falls under the bar.
        ///     </para>
        ///     <para>
        ///         The lane gate is the direct one: a movement re-begun every frame leaves
        ///         <see cref="GazeShiftSample.HeadShiftActive" /> set for essentially the whole
        ///         sweep, whereas a lane that hands off to pursuit shows one genuine movement at
        ///         the sweep's onset and then nothing. "A majority of frames" is a deliberately
        ///         loose bound around an expected near-zero, because the ladder's own recruitment
        ///         transient at the onset is a real decision and is allowed to arm the lane.
        ///     </para>
        ///     <para>
        ///         The displacement gate corroborates it: pinned in an opening phase the head
        ///         cannot keep station with its share. 70% is the loose end of a follow that
        ///         should come in close to 1 — over a window this long the head's displacement
        ///         differs from the share's only by the CHANGE in trail, not by the trail itself.
        ///     </para>
        /// </remarks>
        [Test]
        public void SustainedFastShare_IsLearnedInsteadOfRestartedEveryFrame()
        {
            SetShiftTriggerDegrees(_profile, 0.5f);

            const float settle = 0.5f;
            const float sweepSeconds = 2f;
            const float measureFrom = 1f;
            const float sweepEnd = settle + sweepSeconds;

            using var harness = new GazeShiftTraceHarness(_profile) { FrameDeltaSeconds = 1f / 30f };
            DriveOrbit(harness, settle, degreesPerSecond: 55f, sweepSeconds: sweepSeconds,
                holdSeconds: 0.5f);

            FollowMetrics metrics = Measure(harness, WarmupSeconds, sweepEnd);
            Dump(harness, nameof(SustainedFastShare_IsLearnedInsteadOfRestartedEveryFrame), metrics);

            float shareAdvance = AdvanceBetween(harness, measureFrom, sweepEnd, s => s.PlannedHead.x);
            float headAdvance = AdvanceBetween(harness, measureFrom, sweepEnd, s => s.Head.x);

            int framesAfter = 0;
            int activeAfter = 0;
            foreach (GazeShiftSample s in harness.Samples)
            {
                if (s.Time < measureFrom) continue;
                framesAfter++;
                if (s.HeadShiftActive) activeAfter++;
            }

            TestContext.Out.WriteLine(
                $"sustained sweep: share advanced {shareAdvance:0.0}°, head {headAdvance:0.0}° " +
                $"between {measureFrom:0.0}s and {sweepEnd:0.0}s; ballistic lane armed on " +
                $"{activeAfter} of {framesAfter} frames after {measureFrom:0.0}s");

            Assert.That(framesAfter, Is.GreaterThan(30),
                "The gates below are only worth making over a window with real frames in it.");
            Assert.That(shareAdvance, Is.GreaterThan(20f),
                $"The share must actually have been sweeping across the measured window, or the " +
                $"follow ratio measures nothing; it advanced {shareAdvance:0.0}°.");

            // One movement toward a goal that keeps moving is lengthened as the goal recedes
            // (BallisticMotor.Extend): its clock grows at 0.0125 s per degree while time runs at
            // one second per second, so at 55 °/s the single movement lasts about
            // 0.45 / (1 - 0.0125 * 55) = 1.44 s and hands off to pursuit near 1.9 s. That is why
            // the lane is legitimately armed for well over half of this window. Armed on ALL of
            // it is the failure: the estimate never learning the goal's velocity and the movement
            // being re-begun underneath itself every frame, which never hands off at all.
            Assert.That(activeAfter, Is.LessThan(framesAfter * 0.85f),
                $"A share in sustained motion is one movement handed over to pursuit, not a new " +
                $"decision every frame: the ballistic lane was armed on {activeAfter} of " +
                $"{framesAfter} frames.");

            Assert.That(headAdvance, Is.GreaterThanOrEqualTo(shareAdvance * 0.7f),
                $"and a head that follows its share covers the ground the share covers: it " +
                $"advanced {headAdvance:0.0}° against the share's {shareAdvance:0.0}°. A head " +
                $"pinned in an opening phase falls behind and never catches up.");
        }

        /// <summary>How far a traced signal moved between two trace times.</summary>
        private static float AdvanceBetween(
            GazeShiftTraceHarness harness, float fromTime, float toTime,
            Func<GazeShiftSample, float> select)
        {
            float first = float.NaN, last = float.NaN;
            foreach (GazeShiftSample s in harness.Samples)
            {
                if (s.Time < fromTime || s.Time > toTime) continue;
                if (float.IsNaN(first)) first = select(s);
                last = select(s);
            }

            return float.IsNaN(first) || float.IsNaN(last) ? 0f : Mathf.Abs(last - first);
        }

        /// <summary>
        ///     Writes the profile's shift trigger. Serialized and private, so it is reached the
        ///     same way <c>HeadTorsoSolverTests</c> reaches the body-turn relief.
        /// </summary>
        private static void SetShiftTriggerDegrees(ConvaiGazeProfile profile, float value)
        {
            FieldInfo ladderField = typeof(ConvaiGazeProfile).GetField(
                "gazeShiftLadder", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(ladderField,
                "ConvaiGazeProfile.gazeShiftLadder was renamed; update this helper.");
            object ladder = ladderField.GetValue(profile);
            FieldInfo triggerField = ladder.GetType().GetField(
                "shiftTriggerDegrees", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(triggerField, "shiftTriggerDegrees was renamed; update this helper.");
            triggerField.SetValue(ladder, value);
            Assert.That(profile.ShiftTriggerDegrees, Is.EqualTo(value).Within(0.0001f),
                "Fixture: the profile must be holding the trigger this trace is built around.");
        }

        // ------------------------------------------------------------------ drivers

        private GazeShiftTraceHarness RunOrbit(
            float settleSeconds, float degreesPerSecond, float sweepSeconds, float holdSeconds)
        {
            var harness = new GazeShiftTraceHarness(_profile);
            DriveOrbit(harness, settleSeconds, degreesPerSecond, sweepSeconds, holdSeconds);
            return harness;
        }

        private static void DriveOrbit(
            GazeShiftTraceHarness harness,
            float settleSeconds, float degreesPerSecond, float sweepSeconds, float holdSeconds)
        {
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 up = harness.Root.up;

            float yaw = 0f;
            Vector3 Orbit() => origin + (Quaternion.AngleAxis(yaw, up) * (forward * 2f));

            RunFrames(harness, settleSeconds, Orbit);
            RunFrames(harness, sweepSeconds, () =>
            {
                yaw += degreesPerSecond * harness.FrameDeltaSeconds;
                return Orbit();
            });
            RunFrames(harness, holdSeconds, Orbit);
        }

        private GazeShiftTraceHarness RunSine(
            float settleSeconds, float amplitudeDegrees, float hertz, float seconds)
        {
            var harness = new GazeShiftTraceHarness(_profile);
            Vector3 origin = harness.EyeCenter;
            Vector3 forward = harness.Root.forward;
            Vector3 up = harness.Root.up;

            float yaw = 0f;
            Vector3 Orbit() => origin + (Quaternion.AngleAxis(yaw, up) * (forward * 2f));

            RunFrames(harness, settleSeconds, Orbit);

            float t = 0f;
            RunFrames(harness, seconds, () =>
            {
                t += harness.FrameDeltaSeconds;
                yaw = amplitudeDegrees * Mathf.Sin(2f * Mathf.PI * hertz * t);
                return Orbit();
            });

            return harness;
        }

        private static void RunFrames(
            GazeShiftTraceHarness harness, float seconds, Func<Vector3> targetPoint)
        {
            int steps = Mathf.CeilToInt(seconds / harness.FrameDeltaSeconds);
            for (int i = 0; i < steps; i++) harness.Step(targetPoint());
        }

        // ------------------------------------------------------------------ metrics

        /// <summary>What the applied head did relative to the share it was handed.</summary>
        private readonly struct FollowMetrics
        {
            /// <summary>Frames where the share moved and the head did not — the park signature.</summary>
            public readonly int ParkFrames;

            /// <summary>Longest unbroken stretch of those frames, in seconds.</summary>
            public readonly float LongestParkSeconds;

            /// <summary>Frames where the head moved against a share that was moving the other way.</summary>
            public readonly int CounterMotionFrames;

            public readonly float PeakHeadSpeedInBand;
            public readonly float PeakShareSpeedInBand;
            public readonly float MeanShareSpeedInBand;
            public readonly float MeanTrailInBand;
            public readonly float PeakAcceleration;
            public readonly int BandFrames;

            /// <summary>Band frames on which the share was actually moving — the trail law's own sample.</summary>
            public readonly int MovingFrames;

            public FollowMetrics(
                int parkFrames, float longestParkSeconds, int counterMotionFrames,
                float peakHeadSpeedInBand, float peakShareSpeedInBand, float meanShareSpeedInBand,
                float meanTrailInBand, float peakAcceleration, int bandFrames, int movingFrames)
            {
                MovingFrames = movingFrames;
                ParkFrames = parkFrames;
                LongestParkSeconds = longestParkSeconds;
                CounterMotionFrames = counterMotionFrames;
                PeakHeadSpeedInBand = peakHeadSpeedInBand;
                PeakShareSpeedInBand = peakShareSpeedInBand;
                MeanShareSpeedInBand = meanShareSpeedInBand;
                MeanTrailInBand = meanTrailInBand;
                PeakAcceleration = peakAcceleration;
                BandFrames = bandFrames;
            }

            public override string ToString() =>
                $"[{BandFrames} band frames ({MovingFrames} moving): {ParkFrames} parked (longest {LongestParkSeconds:0.000}s), " +
                $"{CounterMotionFrames} counter-motion, head peak {PeakHeadSpeedInBand:0.0} vs share peak " +
                $"{PeakShareSpeedInBand:0.0} deg/s, share mean {MeanShareSpeedInBand:0.0} deg/s, " +
                $"trail mean {MeanTrailInBand:0.00} deg, peak accel {PeakAcceleration:0} deg/s^2]";
        }

        /// <summary>
        ///     Measures the applied head against <see cref="GazeShiftSample.PlannedHead" /> over a
        ///     time window, restricted to the following band (share speeds the lane is responsible
        ///     for; above <see cref="FollowBandMaxShareSpeed" /> the share is saturating and the
        ///     question becomes the body's).
        /// </summary>
        private static FollowMetrics Measure(
            GazeShiftTraceHarness harness, float fromTime, float toTime)
        {
            IReadOnlyList<GazeShiftSample> samples = harness.Samples;
            float dt = harness.FrameDeltaSeconds;

            int parkFrames = 0, counterFrames = 0, bandFrames = 0, movingFrames = 0;
            float longestPark = 0f, currentPark = 0f;
            float peakHead = 0f, peakShare = 0f, peakAccel = 0f;
            float shareSpeedSum = 0f, trailSum = 0f;
            float lastOutOfBandTime = float.NegativeInfinity;

            for (int i = 1; i < samples.Count; i++)
            {
                GazeShiftSample s = samples[i];
                if (s.Time < fromTime || s.Time > toTime) continue;

                float shareVelocity = (s.PlannedHead.x - samples[i - 1].PlannedHead.x) / dt;
                float headVelocity = s.HeadVelocity.x;
                float shareSpeed = Mathf.Abs(shareVelocity);
                float headSpeed = Mathf.Abs(headVelocity);

                peakAccel = Mathf.Max(peakAccel, Mathf.Abs(s.HeadAcceleration.x));

                // The park gate. Deliberately stated on the share's motion, not the target's: a
                // share that is not moving is a head that is correctly holding still.
                if (shareSpeed > 1.5f && headSpeed < 0.3f)
                {
                    parkFrames++;
                    currentPark += dt;
                    longestPark = Mathf.Max(longestPark, currentPark);
                }
                else
                {
                    currentPark = 0f;
                }

                // Counter-motion: only charged where the share has been going one way for three
                // frames, so a genuine reversal is not read as the head fighting its input.
                if (i >= 3 && shareSpeed > 2f)
                {
                    float previousShare = (samples[i - 1].PlannedHead.x - samples[i - 2].PlannedHead.x) / dt;
                    float earlierShare = (samples[i - 2].PlannedHead.x - samples[i - 3].PlannedHead.x) / dt;
                    bool monotonic = Mathf.Sign(previousShare) == Mathf.Sign(shareVelocity)
                                     && Mathf.Sign(earlierShare) == Mathf.Sign(shareVelocity)
                                     && Mathf.Abs(previousShare) > 2f && Mathf.Abs(earlierShare) > 2f;
                    if (monotonic && headSpeed > 0.3f
                        && Mathf.Sign(headVelocity) != Mathf.Sign(shareVelocity))
                        counterFrames++;
                }

                if (shareSpeed > FollowBandMaxShareSpeed)
                {
                    lastOutOfBandTime = s.Time;
                    continue;
                }

                if (s.Time - lastOutOfBandTime < FollowBandSettleSeconds) continue;

                bandFrames++;
                peakHead = Mathf.Max(peakHead, headSpeed);
                peakShare = Mathf.Max(peakShare, shareSpeed);

                // The trail law is a statement about a share that is MOVING. Averaging it over
                // the settled frames too would mix two regimes and report a mean that describes
                // neither -- and would make the law look satisfied by a window that is mostly
                // stillness.
                if (shareSpeed <= MovingShareFloor) continue;
                movingFrames++;
                shareSpeedSum += shareSpeed;
                trailSum += Mathf.Abs(s.PlannedHead.x - s.Head.x);
            }

            float meanShare = movingFrames > 0 ? shareSpeedSum / movingFrames : 0f;
            float meanTrail = movingFrames > 0 ? trailSum / movingFrames : 0f;

            return new FollowMetrics(
                parkFrames, longestPark, counterFrames, peakHead, peakShare, meanShare, meanTrail,
                peakAccel, bandFrames, movingFrames);
        }

        /// <summary>
        ///     Amplitude ratio of applied head yaw to allocated share yaw, peak-to-peak over a
        ///     window — the tracking lane's gain at whatever frequency the window contains.
        /// </summary>
        private static float MeasureGain(GazeShiftTraceHarness harness, float fromTime, float toTime)
        {
            float headMin = float.PositiveInfinity, headMax = float.NegativeInfinity;
            float shareMin = float.PositiveInfinity, shareMax = float.NegativeInfinity;

            foreach (GazeShiftSample s in harness.Samples)
            {
                if (s.Time < fromTime || s.Time > toTime) continue;
                headMin = Mathf.Min(headMin, s.Head.x);
                headMax = Mathf.Max(headMax, s.Head.x);
                shareMin = Mathf.Min(shareMin, s.PlannedHead.x);
                shareMax = Mathf.Max(shareMax, s.PlannedHead.x);
            }

            float shareSpan = shareMax - shareMin;
            return shareSpan > 0.01f ? (headMax - headMin) / shareSpan : 0f;
        }

        /// <summary>
        ///     Seconds after <paramref name="stopTime" /> until the head is permanently within
        ///     <paramref name="toleranceDegrees" /> of where the trace leaves it.
        /// </summary>
        private static float SettleTimeAfter(
            GazeShiftTraceHarness harness, float stopTime, float toleranceDegrees)
        {
            IReadOnlyList<GazeShiftSample> samples = harness.Samples;
            float settled = samples[^1].Head.x;

            float latest = 0f;
            foreach (GazeShiftSample s in samples)
            {
                if (s.Time < stopTime) continue;
                if (Mathf.Abs(s.Head.x - settled) > toleranceDegrees) latest = s.Time - stopTime;
            }

            return latest;
        }

        /// <summary>
        ///     How far the head still had to travel at <paramref name="atTime" /> to reach the
        ///     value the trace leaves it at — the precondition for a settle measurement.
        /// </summary>
        private static float DistanceStillToCloseAt(GazeShiftTraceHarness harness, float atTime)
        {
            IReadOnlyList<GazeShiftSample> samples = harness.Samples;
            float settled = samples[^1].Head.x;
            foreach (GazeShiftSample s in samples)
                if (s.Time >= atTime)
                    return Mathf.Abs(settled - s.Head.x);

            return 0f;
        }

        /// <summary>How far the head yaw moved between two trace times — the post-stop drift.</summary>
        private static float CreepBetween(GazeShiftTraceHarness harness, float fromTime, float toTime)
        {
            float first = float.NaN, last = float.NaN;
            foreach (GazeShiftSample s in harness.Samples)
            {
                if (s.Time < fromTime) continue;
                if (float.IsNaN(first)) first = s.Head.x;
                if (s.Time <= toTime) last = s.Head.x;
            }

            return float.IsNaN(first) || float.IsNaN(last) ? 0f : Mathf.Abs(last - first);
        }

        // ------------------------------------------------------------------ trace dump

        private static void Dump(GazeShiftTraceHarness harness, string name, FollowMetrics metrics)
        {
            TestContext.Out.WriteLine($"{name}: {metrics}");

            string directory = Path.Combine(
                UnityEngine.Application.dataPath, "..", "Logs", "GazePursuitTraces");
            Directory.CreateDirectory(directory);

            var csv = new StringBuilder(harness.Samples.Count * 64);
            csv.AppendLine("t,requiredYaw,plannedHeadYaw,headYaw,headVelYaw,headAccelYaw,trailYaw,torsoYaw,eyeYaw");
            foreach (GazeShiftSample s in harness.Samples)
                csv.AppendFormat(CultureInfo.InvariantCulture,
                    "{0:0.0000},{1:0.000},{2:0.000},{3:0.000},{4:0.000},{5:0.000},{6:0.000},{7:0.000},{8:0.000}\n",
                    s.Time, s.Required.x, s.PlannedHead.x, s.Head.x, s.HeadVelocity.x,
                    s.HeadAcceleration.x, s.PlannedHead.x - s.Head.x, s.Torso.x, s.Eye.x);

            string path = Path.Combine(directory, name + ".csv");
            File.WriteAllText(path, csv.ToString());
            TestContext.Out.WriteLine($"{name}: trace written to {path}");
        }
    }
}
