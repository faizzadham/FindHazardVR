using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     What a camera cut costs the head. A cut — the player teleporting, or the view
    ///     switching to a camera somewhere else — moves the world, not the character's mind, and
    ///     the module used to answer it with the reflex tempo reserved for a startle. In play that
    ///     read as a 90° shift finished in about 1.2 s with the head peaking between 110 and
    ///     140 °/s. Nothing in the scene demanded that speed. The character simply found its
    ///     target somewhere new and whipped round to it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A cut reached the reflex tempo down two independent paths, which is why the fix is
    ///         in two places: <see cref="GazeTargetArbiter" /> stamped the decision
    ///         <see cref="GazeLookNature.Reflex" />, and the controller separately OR'd
    ///         <c>WasCut</c> into its urgency test. Removing either alone changes nothing
    ///         measurable, so the first test here reads the classification through the arbiter
    ///         rather than asserting the controller's rule in isolation.
    ///     </para>
    ///     <para>
    ///         Reflex tempo itself is not being taken away. The startle re-acquisition — the
    ///         character reacting to being interrupted — still gets it, and the last test holds
    ///         that line so "a cut is not urgent" cannot quietly become "nothing is".
    ///     </para>
    /// </remarks>
    public sealed class GazeCutTempoTests
    {
        // The two gates below are stated in the HEAD's frame, not the eye line's, and that
        // distinction is the whole reason they are not the 1.575 s / 90 °/s the movement law
        // gives a 90° shift. A 90° look is not a head movement: the ladder spends about 14° of it
        // on the eyes, hands roughly 24° to the chest and asks for the feet, so the head itself
        // settles near 52° and its own movement law — 0.45 s + 0.0125 s per degree — is the one
        // being measured here. At ordinary tempo that head covers its share in 0.87 s peaking at
        // 96 °/s; at the reflex tempo it took 0.68 s and 129 °/s. The gates sit between those two
        // readings, so a cut that quietly goes back to reflex speed fails both of them.

        /// <summary>The fastest the head may be moving on a cut, at ordinary tempo.</summary>
        private const float MaxOrdinaryPeakDegreesPerSecond = 100f;

        /// <summary>How long the head must take to be most of the way round, at ordinary tempo.</summary>
        private const float MinSecondsToNinetyPercent = 0.8f;

        private const float CutAmplitudeDegrees = 90f;
        private const int TraceFrames = 300; // 5 s at 60 fps — past the settle at every tempo.

        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        // ------------------------------------------------------------------ classification

        /// <summary>
        ///     A cut is reported as a cut — the eyes still re-acquire ballistically — but it is
        ///     not a reflex, and it does not classify the movement as one.
        /// </summary>
        [Test]
        public void CameraCut_IsNotClassifiedAsAReflex()
        {
            GazeTargetDecision cut = CutDecision();

            Assert.IsTrue(cut.WasCut, "Sanity: the displaced target must still be reported as a cut.");
            Assert.IsTrue(cut.TeleportedThisTick, "Sanity: the eyes still re-acquire ballistically.");

            Assert.That(cut.Nature, Is.Not.EqualTo(GazeLookNature.Reflex),
                "A cut moves the world, not the character's mind. Stamping the look a reflex is " +
                "the second, quieter half of executing it at reflex speed.");

            Assert.That(UrgencyOf(cut), Is.EqualTo(GazeMovementUrgency.Neutral),
                "A camera cut is answered at ordinary tempo, like every other look the character " +
                "decides to make.");
        }

        /// <summary>
        ///     The startle re-acquisition is the one thing that still moves at reflex speed:
        ///     it happens TO the character, and the whole point of the class is that something
        ///     stays in it.
        /// </summary>
        [Test]
        public void StartleReacquisition_IsStillUrgent()
        {
            Assert.That(
                ConvaiGazeController.ResolveMovementUrgency(
                    wantsReacquisition: true,
                    ambientActive: false,
                    hasEngagedTarget: true,
                    nature: GazeLookNature.Attention),
                Is.EqualTo(GazeMovementUrgency.Urgent));
        }

        // ------------------------------------------------------------------ what it looks like

        /// <summary>
        ///     The measurement the classification exists for: a 90° cut, traced through the
        ///     actuator at whatever urgency the cut actually resolves to.
        /// </summary>
        [Test]
        public void CameraCut_HeadCoversNinetyDegreesAtOrdinaryTempo()
        {
            using GazeShiftTraceHarness harness = GazeShiftTraceHarness.RunStepResponse(
                _profile, CutAmplitudeDegrees, TraceFrames, UrgencyOf(CutDecision()));

            float peak = harness.PeakAngularSpeed();
            float t90 = SecondsToNinetyPercentOfSettledHead(harness);

            Assert.That(peak, Is.LessThanOrEqualTo(MaxOrdinaryPeakDegreesPerSecond),
                $"The head peaked at {peak:0.0} °/s on a cut, against the {MaxOrdinaryPeakDegreesPerSecond:0} °/s " +
                "an ordinary-tempo turn of this size reaches. Past that it stops reading as a " +
                "person noticing something and starts reading as a flinch — which is what the " +
                "reflex tempo, at 129 °/s, produced here.");

            Assert.That(t90, Is.GreaterThanOrEqualTo(MinSecondsToNinetyPercent),
                $"The head was 90% of the way round {t90:0.00} s after the cut, against the " +
                $"{MinSecondsToNinetyPercent:0.00} s its own movement law allows at the earliest. " +
                "Arriving sooner means the movement is being run at a tempo scale it was not " +
                "given — the reflex tempo lands this at 0.68 s.");
        }

        /// <summary>
        ///     The eyes still go first. Slowing the head down must not turn a cut into a
        ///     single slow swing of the whole head/eye assembly: the saccade is what makes the
        ///     character read as having noticed, and it is what the head is then following.
        /// </summary>
        [Test]
        public void CameraCut_EyesStillLeadTheHead()
        {
            using GazeShiftTraceHarness harness = GazeShiftTraceHarness.RunStepResponse(
                _profile, CutAmplitudeDegrees, TraceFrames, UrgencyOf(CutDecision()));

            int eyeOnset = FirstFrameMoving(harness.Samples, s => s.Eye.magnitude, 1f);
            int headOnset = FirstFrameMoving(harness.Samples, s => s.Head.magnitude, 0.5f);

            Assert.That(eyeOnset, Is.GreaterThanOrEqualTo(0), "The eyes must move at all.");
            Assert.That(headOnset, Is.GreaterThanOrEqualTo(0), "The head must move at all.");
            Assert.That(eyeOnset, Is.LessThan(headOnset),
                $"The eyes moved on frame {eyeOnset} and the head on frame {headOnset}. The saccade " +
                "leads and the head follows it — the profile's Head Starts After The Eyes By is " +
                "the gap between them.");
        }

        /// <summary>
        ///     Reflex tempo still shortens the movement it is given, so the class the cut was
        ///     taken out of is still doing something for the case that belongs in it.
        /// </summary>
        [Test]
        public void StartleReacquisition_MovesFasterThanAnOrdinaryLook()
        {
            using GazeShiftTraceHarness ordinary = GazeShiftTraceHarness.RunStepResponse(
                _profile, CutAmplitudeDegrees, TraceFrames, GazeMovementUrgency.Neutral);
            using GazeShiftTraceHarness urgent = GazeShiftTraceHarness.RunStepResponse(
                _profile, CutAmplitudeDegrees, TraceFrames, GazeMovementUrgency.Urgent);

            float ordinarySeconds = ordinary.MovementDurationSeconds(0.5f);
            float urgentSeconds = urgent.MovementDurationSeconds(0.5f);
            float ratio = urgentSeconds / ordinarySeconds;

            // 0.75 is the reflex tempo scale; the band is the slack a 60 Hz trace and the
            // settle tolerance put around it, not a licence for a different number.
            Assert.That(ratio, Is.InRange(0.65f, 0.85f),
                $"A reflex took {urgentSeconds:0.00} s against the ordinary {ordinarySeconds:0.00} s " +
                $"(x{ratio:0.00}). Reflex tempo is a 0.75 scale on the same movement law.");
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        ///     A real camera cut, produced by the arbiter rather than hand-built: the same target
        ///     (same key, so not a re-target) displaced beyond the cut threshold in one frame.
        /// </summary>
        private GazeTargetDecision CutDecision()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new List<GazeTargetCandidate>
            {
                new(GazeTargetKind.Player, 10, 1f, null, new Vector3(0f, 1.6f, 2f), "Player")
            };

            const float dt = 1f / 60f;
            for (int i = 0; i < 30; i++) arbiter.Tick(candidates, null, true, _profile, dt);

            candidates.Clear();
            candidates.Add(new GazeTargetCandidate(
                GazeTargetKind.Player, 10, 1f, null, new Vector3(12f, 1.6f, 2f), "Player"));

            return arbiter.Tick(candidates, null, true, _profile, dt);
        }

        /// <summary>The urgency the controller would give this decision, with no startle in flight.</summary>
        private static GazeMovementUrgency UrgencyOf(GazeTargetDecision decision) =>
            ConvaiGazeController.ResolveMovementUrgency(
                wantsReacquisition: false,
                ambientActive: false,
                hasEngagedTarget: true,
                nature: decision.Nature);

        /// <summary>
        ///     When the head first reached 90% of the deflection it settled on. Measured against
        ///     the head's OWN settled amplitude rather than the 90° the target moved: at this size
        ///     the ladder splits the shift across eyes, head and chest and asks for the feet, so
        ///     the head never covers 90° and a gate written against the target's amplitude would
        ///     never fire at any tempo.
        /// </summary>
        private static float SecondsToNinetyPercentOfSettledHead(GazeShiftTraceHarness harness)
        {
            IReadOnlyList<GazeShiftSample> samples = harness.Samples;
            float settled = samples[^1].Head.magnitude;
            Assert.That(settled, Is.GreaterThan(5f), "The head must actually take a share of the shift.");

            foreach (GazeShiftSample sample in samples)
                if (sample.Head.magnitude >= settled * 0.9f)
                    return sample.Time;

            return float.PositiveInfinity;
        }

        /// <summary>Index of the first sample whose measured value has left rest, or -1.</summary>
        private static int FirstFrameMoving(
            IReadOnlyList<GazeShiftSample> samples, System.Func<GazeShiftSample, float> value, float threshold)
        {
            for (int i = 0; i < samples.Count; i++)
                if (value(samples[i]) >= threshold)
                    return i;

            return -1;
        }
    }
}
