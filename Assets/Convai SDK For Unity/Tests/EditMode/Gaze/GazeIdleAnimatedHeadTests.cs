using System.Text;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Idle life while the idle CLIP turns the head: the animation looks around, and the eyes
    ///     have to go with it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         An idle animation that turns the head is not a disturbance to be cancelled — it is
    ///         the character looking around, authored. There is no target and nothing has been
    ///         decided to look at, so there is no world point to hold: the only correct response
    ///         is for the eyes to ride the turn, staying near the middle of their sockets.
    ///     </para>
    ///     <para>
    ///         What was measured on the shipped sample instead: the idle clip turned the head bone
    ///         about 32° over half a second, and the eyes swung 30° the other way and held there
    ///         for the rest of the fixation — eye yaw against animated head yaw correlating at
    ///         −0.95 across the whole idle segment. The character looks around with its head while
    ///         its eyes stay locked on whatever was behind it, which is the pinned, staring read
    ///         a customer screenshotted.
    ///     </para>
    ///     <para>
    ///         Not the vestibulo-ocular reflex, which is off here by construction
    ///         (<c>EyeSolver.StabilizeAgainstHeadMotion</c> returns immediately without an engaged
    ///         target). The idle aim itself is the counter-rotation: the ambient fixation is
    ///         authored in the character frame and the eyes are handed the residual after
    ///         subtracting where the head bone actually points — a quantity that includes
    ///         everything the ANIMATION is doing to the head. Subtracting the animation makes the
    ///         eyes hold a body-fixed point that idle life never chose.
    ///     </para>
    ///     <para>
    ///         The engaged case is the opposite requirement and is pinned separately by
    ///         <see cref="EyeSolverHeadMotionStabilizationTests" />: while the character is looking
    ///         AT something, a head that turns underneath the eyes must not drag the gaze off it.
    ///         Both are true because only one of them has a world point.
    ///     </para>
    /// </remarks>
    public sealed class GazeIdleAnimatedHeadTests
    {
        /// <summary>Frames of still idle before the clip's head turn starts.</summary>
        private const float SettleSeconds = 0.5f;

        /// <summary>How far the "clip" turns the head, and over how long — the measured beat.</summary>
        private const float AnimatedHeadYawDegrees = 30f;

        private const float TurnSeconds = 0.5f;

        /// <summary>How long the clip holds the head there afterwards.</summary>
        private const float HoldSeconds = 1f;

        /// <summary>
        ///     How far the eyes may sit from the middle of the socket at any point. The profile's
        ///     own eye comfort band: past it the character is looking at something out of the
        ///     corner of its eye, which is exactly the read under test.
        /// </summary>
        private float ComfortDegrees => _profile.EyeComfortDegrees;

        /// <summary>Where the eyes must have come back to once the head has stopped turning.</summary>
        private const float RestedDegrees = 5f;

        /// <summary>
        ///     Largest single-frame change allowed in the applied eye pose. The eyes have nothing
        ///     to saccade to here — no target, and a fixation the head is already carrying them
        ///     to — so any jump is the aim fighting the animation.
        /// </summary>
        private const float MaxStepDegrees = 3f;

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        [Test]
        public void AnimatedHeadTurn_UnderIdleLife_CarriesTheEyesWithIt()
        {
            using var harness = new GazeShiftTraceHarness(_profile)
            {
                AmbientActive = true,

                // The recentre fixation — one of the two idle life deals itself, and the one that
                // isolates the animation: whatever the eyes end up doing here, idle life did not
                // ask them to look anywhere.
                AmbientAngles = Vector2.zero
            };

            int settleFrames = Frames(SettleSeconds);
            int turnFrames = Frames(TurnSeconds);
            int holdFrames = Frames(HoldSeconds);

            for (int i = 0; i < settleFrames; i++) Idle(harness);

            for (int i = 0; i < turnFrames; i++)
            {
                harness.AnimatedHeadYawDegrees =
                    AnimatedHeadYawDegrees * ((i + 1) / (float)turnFrames);
                Idle(harness);
            }

            for (int i = 0; i < holdFrames; i++) Idle(harness);

            float worstEccentricity = 0f;
            int worstFrame = 0;
            float worstStep = 0f;
            int worstStepFrame = 0;
            for (int i = settleFrames; i < harness.Samples.Count; i++)
            {
                float eccentricity = Mathf.Abs(harness.Samples[i].Eye.x);
                if (eccentricity > worstEccentricity)
                {
                    worstEccentricity = eccentricity;
                    worstFrame = i;
                }

                float step = (harness.Samples[i].Eye - harness.Samples[i - 1].Eye).magnitude;
                if (step > worstStep)
                {
                    worstStep = step;
                    worstStepFrame = i;
                }
            }

            float rested = Mathf.Abs(harness.Samples[^1].Eye.x);

            Assert.That(worstEccentricity, Is.LessThanOrEqualTo(ComfortDegrees),
                $"The idle clip's head turn drove the eyes to {worstEccentricity:0.000} deg from " +
                $"centre on frame {worstFrame} — past the {ComfortDegrees:0.0} deg they are ever " +
                "meant to rest at. Nothing was engaged and idle life asked for a fixation dead " +
                "ahead, so every degree of that is the aim resisting the animation." +
                Describe(harness, settleFrames));

            Assert.That(rested, Is.LessThanOrEqualTo(RestedDegrees),
                $"A second after the head stopped turning the eyes are still {rested:0.000} deg " +
                "off centre: they are holding a point the character has turned away from, rather " +
                "than looking where its head is pointing." + Describe(harness, settleFrames));

            Assert.That(worstStep, Is.LessThanOrEqualTo(MaxStepDegrees),
                $"The eyes jumped {worstStep:0.000} deg in one frame on frame {worstStepFrame} " +
                $"({worstStep * 60f:0} deg/s at the harness's 60 Hz). With no target and a fixation " +
                "the head is already carrying them onto, there is nothing here to saccade to." +
                Describe(harness, settleFrames));
        }

        private static int Frames(float seconds) =>
            Mathf.RoundToInt(seconds / GazeShiftTraceHarness.FrameSeconds);

        /// <summary>
        ///     One idle frame: nothing engaged, and the engagement an idle character actually
        ///     carries — the controller hands the solvers <c>_directive.Engagement</c>, which is
        ///     zero while nothing is engaged.
        /// </summary>
        private static void Idle(GazeShiftTraceHarness harness) =>
            harness.Step(Vector3.zero, engagement: 0f, hasTarget: false);

        /// <summary>The trace, so a failure reports the shape of what happened and not only the number.</summary>
        private static string Describe(GazeShiftTraceHarness harness, int fromFrame)
        {
            var text = new StringBuilder("\nAnimated head yaw vs eye yaw (deg):");
            for (int i = fromFrame; i < harness.Samples.Count; i += 6)
                text.Append(
                    $"\n  t={harness.Samples[i].Time:0.000}s  animated={harness.Samples[i].AnimatedDeviation.x:0.000}" +
                    $"  gazeHead={harness.Samples[i].Head.x:0.000}  eye={harness.Samples[i].Eye.x:0.000}");

            return text.ToString();
        }
    }
}
