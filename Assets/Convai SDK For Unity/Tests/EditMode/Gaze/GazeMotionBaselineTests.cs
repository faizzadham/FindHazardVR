using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Records the head step-response shape across several amplitudes, so the baseline is a
    ///     number captured by the harness rather than a table copied out of a manual play session.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Measurement only — this harness asserts nothing yet. The bang-bang regression this
    ///         records, and whatever eventually replaces it, are both free to move these numbers;
    ///         this test exists to see the shape, not to police it. The gates that will eventually
    ///         police it (a main-sequence band and a unimodality invariant) belong on a later
    ///         test, once the trajectory primitive they are written against exists.
    ///     </para>
    ///     <para>
    ///         Runs against <see cref="GazeShiftTraceHarness" /> with a default profile, same as
    ///         <see cref="GazeShiftTraceTests" />, so the numbers are directly comparable to that
    ///         suite's invariants.
    ///     </para>
    /// </remarks>
    public sealed class GazeMotionBaselineTests
    {
        private static readonly float[] AmplitudesDegrees = { 5f, 10f, 20f, 40f };

        private const int FrameCount = 240; // 4 s at 60 fps: generous for even the slowest baseline.

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        /// <summary>
        ///     Not a gate: see the class remarks. Logs peak speed, movement duration, where the
        ///     peak fell within that duration, and peak acceleration for each amplitude, so a
        ///     later change can diff its own numbers against what shipped the day this baseline
        ///     was recorded.
        /// </summary>
        [Test]
        public void HeadStepResponse_BaselineIsRecordedAcrossAmplitudes()
        {
            foreach (float amplitude in AmplitudesDegrees)
            {
                using GazeShiftTraceHarness harness =
                    GazeShiftTraceHarness.RunStepResponse(_profile, amplitude, FrameCount);

                float peakSpeed = harness.PeakAngularSpeed();
                float duration = harness.MovementDurationSeconds(0.5f);
                float peakPositionFraction = harness.PeakSpeedPositionFraction();
                float peakAcceleration = harness.MaxAbsAcceleration();
                bool unimodal = harness.IsVelocityUnimodal(2f);

                Record(amplitude, peakSpeed, duration, peakPositionFraction, peakAcceleration, unimodal);
            }
        }

        /// <summary>
        ///     Writes the baseline row to the test log. Test code, not runtime, so
        ///     <c>Debug.Log</c> rather than <c>ConvaiLogger</c> — matching
        ///     <c>EmotionTickBudgetTests.Record</c>, the nearest precedent for a measurement test
        ///     writing a number out for later comparison.
        /// </summary>
        private static void Record(
            float amplitudeDegrees,
            float peakSpeedDegPerSec,
            float durationSeconds,
            float peakPositionFraction,
            float peakAccelerationDegPerSec2,
            bool unimodal)
        {
            string line =
                $"[GazeMotionBaseline] amplitude={amplitudeDegrees:0}deg " +
                $"peakSpeed={peakSpeedDegPerSec:0.0}deg/s " +
                $"duration={durationSeconds:0.000}s " +
                $"peakAt={peakPositionFraction:0.00} " +
                $"peakAccel={peakAccelerationDegPerSec2:0}deg/s^2 " +
                $"unimodal={unimodal}";

            Debug.Log(line);
        }
    }
}
