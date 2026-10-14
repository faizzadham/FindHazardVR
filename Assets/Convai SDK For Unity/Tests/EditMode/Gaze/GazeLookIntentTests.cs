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
    ///     A look carries what the character means by it, from the stage that decided to make it to
    ///     the stages that execute it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Everything downstream used to guess. The movement tempo asked "is there a target at
    ///         all", the chest asked "does the rig have a torso", and face scanning asked "is this
    ///         the player" — so a glance across a room was executed as the same movement as turning
    ///         to face somebody, the chest joined looks nobody asked it to join, and the eyes
    ///         scanned an imaginary face on a camera while holding dead still on a real one.
    ///     </para>
    /// </remarks>
    internal sealed class GazeLookIntentTests
    {
        private ConvaiGazeProfile _profile;

        [SetUp]
        public void SetUp() => _profile = ConvaiGazeProfile.CreateDefault();

        [TearDown]
        public void TearDown()
        {
            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        private static GazeTargetCandidate Candidate(
            GazeTargetKind kind, float relevance, bool hasFace = false, int priority = 5) =>
            new(kind, priority, relevance, null, Vector3.forward * 2f, kind.ToString(), hasFace);

        // ---------------------------------------------------------------- the seam

        [Test]
        public void ADefaultCandidate_ClaimsNoFace()
        {
            Assert.IsFalse(
                Candidate(GazeTargetKind.WorldObject, 1f).HasFace,
                "A provider that does not answer must get exact aim, not an imaginary face.");
        }

        [Test]
        public void AProviderTarget_IsAttentionAndCarriesItsFace()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new[] { Candidate(GazeTargetKind.Character, 0.35f, hasFace: true) };

            GazeTargetDecision decision = arbiter.Tick(candidates, null, true, _profile, 0.5f);

            Assert.IsTrue(decision.TargetHasFace);
            Assert.That(decision.Nature, Is.EqualTo(GazeLookNature.Attention),
                "A candidate the character chose on its own relevance is attention.");
        }

        [Test]
        public void AnArbiterWithNothingOffered_ReportsNoLook()
        {
            var arbiter = new GazeTargetArbiter();
            GazeTargetDecision decision = arbiter.Tick(
                System.Array.Empty<GazeTargetCandidate>(), null, true, _profile, 0.5f);

            Assert.IsFalse(decision.HasTarget);
        }

        /// <summary>
        ///     The release ramp is the one window where nothing is offering the target any more but
        ///     the look is still being let go, so the intent has to survive it — the actuators are
        ///     still deciding how to end a look whose kind they would otherwise have forgotten.
        /// </summary>
        [Test]
        public void WhileReleasing_TheLookKeepsItsIdentityAndSaysItIsReleasing()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new[] { Candidate(GazeTargetKind.Character, 0.4f, hasFace: true) };

            // Acquire.
            for (int i = 0; i < 10; i++)
                arbiter.Tick(candidates, null, true, _profile, 0.1f);

            GazeTargetDecision held = arbiter.Tick(candidates, null, true, _profile, 0.1f);
            Assert.IsTrue(held.HasTarget);
            Assert.IsFalse(held.IsReleasing, "It is still being offered.");

            // Nothing offers it any more.
            GazeTargetDecision releasing = arbiter.Tick(
                System.Array.Empty<GazeTargetCandidate>(), null, true, _profile, 0.05f);

            Assert.IsTrue(releasing.IsReleasing, "The ramp is running down.");
            Assert.IsTrue(releasing.TargetHasFace, "It is still the same look.");
        }

        [Test]
        public void ATargetStillOffered_IsNotReleasing()
        {
            var arbiter = new GazeTargetArbiter();
            var candidates = new[] { Candidate(GazeTargetKind.Player, 1f) };

            for (int i = 0; i < 10; i++)
                arbiter.Tick(candidates, null, true, _profile, 0.1f);

            Assert.IsFalse(arbiter.Tick(candidates, null, true, _profile, 0.1f).IsReleasing);
        }

        // ---------------------------------------------------------------- what it decides

        /// <summary>
        ///     The chest is a permission now, not just a rig fact. This exercises the ladder
        ///     itself rather than a copy of the controller's gate: what matters is that a capacity
        ///     saying "no chest" actually produces a plan without one, and that the shift still
        ///     adds up.
        /// </summary>
        [Test]
        public void WithTheChestWithheld_TheLadderAllocatesNoneToIt()
        {
            var measurement = new GazeShiftMeasurement(45f, 0f, 0f, 0f);
            var tuning = new GazeLadderTuning(12f, 35f, 25f, 0f, 0f, 0f);

            GazeLadderCapacity allowed = Capacity(torsoAvailable: true);
            GazeLadderCapacity withheld = Capacity(torsoAvailable: false);

            GazeShiftPlan withChest = GazeActuatorLadder.Solve(
                in measurement, in allowed, in tuning, shiftAge: 10f, engagement: 1f);
            GazeShiftPlan withoutChest = GazeActuatorLadder.Solve(
                in measurement, in withheld, in tuning, shiftAge: 10f, engagement: 1f);

            Assert.That(Mathf.Abs(withChest.TorsoYaw), Is.GreaterThan(0.5f),
                "A 45 degree shift past the chest's 35 degree entry should recruit it when allowed.");
            Assert.That(withoutChest.TorsoYaw, Is.EqualTo(0f).Within(1e-4f),
                "Withheld, the chest takes nothing at all.");
            Assert.That(withoutChest.HeadYaw, Is.EqualTo(withChest.HeadYaw).Within(1e-4f),
                "Withholding the chest must not change what the head was going to take.");
        }

        /// <summary>
        ///     The controller's own decision, called directly rather than mirrored — a test that
        ///     re-implements the rule it is checking passes no matter what the shipping code does.
        /// </summary>
        [Test]
        public void OnlyAGlance_KeepsTheChestOutOfIt()
        {
            Assert.IsFalse(
                ConvaiGazeController.RecruitsTorso(GazeLookNature.Glance, rigHasTorso: true),
                "A look that turns the body is not a glance.");
            Assert.IsTrue(
                ConvaiGazeController.RecruitsTorso(GazeLookNature.Attention, rigHasTorso: true),
                "Turning to give somebody attention may use the chest.");
            Assert.IsTrue(
                ConvaiGazeController.RecruitsTorso(GazeLookNature.Reflex, rigHasTorso: true),
                "A startle may use everything.");
        }

        [Test]
        public void ARigWithoutAChest_NeverGrowsOne()
        {
            Assert.IsFalse(ConvaiGazeController.RecruitsTorso(GazeLookNature.Attention, rigHasTorso: false));
            Assert.IsFalse(ConvaiGazeController.RecruitsTorso(GazeLookNature.Glance, rigHasTorso: false));
        }

        private static GazeLadderCapacity Capacity(bool torsoAvailable) =>
            new(headYaw: 55f, headPitch: 30f, torsoYaw: 25f, torsoPitch: 12f,
                headWillingness: 0.85f, headComfortYaw: 45f,
                torsoAvailable: torsoAvailable, feetAvailable: false);
    }
}
