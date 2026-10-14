using System.Collections.Generic;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The preflight model behind the gaze setup card. The single most important assertion here
    ///     is <see cref="WorkingCharacter_IsFunctional_EvenWithNothingAssigned" />: gaze runs with
    ///     no configuration at all, and a surface that calls such a character "not set up" is the
    ///     defect this whole round exists to fix.
    /// </summary>
    public sealed class GazeSetupServiceTests
    {
        private GameObject _root;

        /// <summary>Watches the folder copy-on-write writes a character's own profile into.</summary>
        private ProjectAssetFolderGuard _projectAssets;

        [SetUp]
        public void SetUp()
        {
            _projectAssets = ProjectAssetFolderGuard.Watch();
            _root = new GameObject("GazeSetupTestCharacter");
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            _projectAssets.RemoveWhatTheTestWrote();
        }

        private ConvaiGazeController BuildCharacter(bool withHead = true)
        {
            _root.AddComponent<ConvaiCharacter>();

            if (withHead)
            {
                var neck = new GameObject("Neck");
                neck.transform.SetParent(_root.transform, false);
                var head = new GameObject("Head");
                head.transform.SetParent(neck.transform, false);
            }

            return _root.AddComponent<ConvaiGazeController>();
        }

        // ------------------------------------------------------------------ preflight shape

        [Test]
        public void Inspect_ProducesFourRows()
        {
            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter());

            Assert.That(preflight.Checks.Count, Is.EqualTo(4),
                "Four rows is deliberate — a longer checklist reads as a wall of work on a component " +
                "that mostly just works.");
        }

        [Test]
        public void Inspect_NullController_IsSafe()
        {
            GazePreflight preflight = GazeSetupService.Inspect(null);
            Assert.IsEmpty(preflight.Checks);
            Assert.IsFalse(preflight.HasBlocker);
        }

        [Test]
        public void EveryRow_HasALabelAndADetail()
        {
            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter());

            foreach (GazeCheck check in preflight.Checks)
            {
                Assert.IsNotEmpty(check.Label, $"{check.Id} has no label.");
                Assert.IsNotEmpty(check.Detail,
                    $"{check.Id} has no detail — a row that does not say what it found is noise.");
            }
        }

        // ------------------------------------------------------------------ the headline behaviour

        [Test]
        public void WorkingCharacter_IsFunctional_EvenWithNothingAssigned()
        {
            ConvaiGazeController controller = BuildCharacter();
            GazePreflight preflight = GazeSetupService.Inspect(controller);

            Assert.IsTrue(preflight.IsFunctional,
                "A character with a head bone and no profile still gazes at the player. Reporting it " +
                "as non-functional is exactly the lie this round removes.");
            Assert.IsFalse(preflight.HasBlocker);
        }

        [Test]
        public void OnlyTheRig_CanBlock()
        {
            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter(withHead: false));

            Assert.IsTrue(preflight.HasBlocker);
            Assert.IsTrue(preflight.TryGetBlocker(out GazeCheck blocker));
            Assert.AreEqual(GazeSetupService.CheckRig, blocker.Id,
                "The rig is the only thing that can stop gaze; every other row degrades.");

            foreach (GazeCheck check in preflight.Checks)
                if (check.Id != GazeSetupService.CheckRig)
                    Assert.AreNotEqual(GazeCheckState.Blocked, check.State,
                        $"{check.Id} must never block — gaze works without it.");
        }

        [Test]
        public void NoHeadBone_OffersTheRigBindingFix()
        {
            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter(withHead: false));
            Assert.IsTrue(preflight.TryGetBlocker(out GazeCheck blocker));

            Assert.AreEqual(GazeFixId.AddRigBinding, blocker.Fix);
            Assert.IsNotNull(GazeSetupService.DescribeFix(blocker.Fix),
                "A blocked row without a named button leaves the user with prose and no action.");
        }

        [Test]
        public void ExistingRigBinding_DoesNotOfferToAddAnother()
        {
            _root.AddComponent<StandardRigBinding>();
            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter(withHead: false));

            Assert.IsTrue(preflight.TryGetBlocker(out GazeCheck blocker));
            Assert.AreNotEqual(GazeFixId.AddRigBinding, blocker.Fix,
                "Adding a second rig binding is itself an error state; the fix must not create one.");
        }

        [Test]
        public void DuplicateRigBindings_Block()
        {
            _root.AddComponent<StandardRigBinding>();
            var child = new GameObject("Rig");
            child.transform.SetParent(_root.transform, false);
            child.AddComponent<StandardRigBinding>();

            GazePreflight preflight = GazeSetupService.Inspect(BuildCharacter());

            Assert.IsTrue(preflight.HasBlocker,
                "Ambiguous rig ownership must be surfaced, not silently resolved by picking one.");
        }

        // ------------------------------------------------------------------ fixes

        [Test]
        public void EveryFixId_HasAButtonLabel()
        {
            foreach (GazeFixId fix in System.Enum.GetValues(typeof(GazeFixId)))
            {
                if (fix == GazeFixId.None) continue;
                Assert.IsNotNull(GazeSetupService.DescribeFix(fix), $"{fix} has no button text.");
            }
        }

        [Test]
        public void AddRigBindingFix_AddsExactlyOne_AndIsIdempotent()
        {
            ConvaiGazeController controller = BuildCharacter(withHead: false);

            Assert.IsTrue(GazeSetupService.ApplyFix(controller, GazeFixId.AddRigBinding));
            Assert.AreEqual(1, _root.GetComponentsInChildren<StandardRigBinding>(true).Length);

            Assert.IsFalse(GazeSetupService.ApplyFix(controller, GazeFixId.AddRigBinding),
                "Running a fix twice must be a no-op, not a duplicate component.");
            Assert.AreEqual(1, _root.GetComponentsInChildren<StandardRigBinding>(true).Length);
        }

        [Test]
        public void AddPlayerAnchorFix_AddsExactlyOne_AndIsIdempotent()
        {
            ConvaiGazeController controller = BuildCharacter();

            Assert.IsTrue(GazeSetupService.ApplyFix(controller, GazeFixId.AddPlayerAnchor));
            Assert.IsFalse(GazeSetupService.ApplyFix(controller, GazeFixId.AddPlayerAnchor));
            Assert.AreEqual(1, _root.GetComponentsInChildren<PlayerAnchorTargetProvider>(true).Length);
        }

        // ------------------------------------------------------------------ capabilities

        [Test]
        public void ApplyCapabilities_AddsWhatIsTicked()
        {
            ConvaiGazeController controller = BuildCharacter();
            var notes = new List<string>();

            bool changed = GazeSetupService.ApplyCapabilities(
                controller, new[] { GazeCapabilityId.PlayerAttention }, notes);

            Assert.IsTrue(changed);
            Assert.IsNotNull(_root.GetComponentInChildren<PlayerAttentionSensor>(true));
            Assert.IsNotEmpty(notes, "The user must be told what was added.");
        }

        [Test]
        public void ApplyCapabilities_RemovesWhatIsUnticked()
        {
            ConvaiGazeController controller = BuildCharacter();
            GazeSetupService.ApplyCapabilities(controller, new[] { GazeCapabilityId.PlayerAttention }, null);
            Assert.IsNotNull(_root.GetComponentInChildren<PlayerAttentionSensor>(true));

            bool changed = GazeSetupService.ApplyCapabilities(controller, new GazeCapabilityId[0], null);

            Assert.IsTrue(changed);
            Assert.IsNull(_root.GetComponentInChildren<PlayerAttentionSensor>(true),
                "A checkbox that only ever adds is not a checkbox.");
        }

        [Test]
        public void ApplyCapabilities_IsIdempotent()
        {
            ConvaiGazeController controller = BuildCharacter();
            var wanted = new[] { GazeCapabilityId.PlayerAttention, GazeCapabilityId.AttentionGrounding };

            Assert.IsTrue(GazeSetupService.ApplyCapabilities(controller, wanted, null));
            Assert.IsFalse(GazeSetupService.ApplyCapabilities(controller, wanted, null),
                "Re-applying the same selection must change nothing.");

            Assert.AreEqual(1, _root.GetComponentsInChildren<PlayerAttentionSensor>(true).Length);
            Assert.AreEqual(1, _root.GetComponentsInChildren<GazeDynamicContextBridge>(true).Length);
        }

        [Test]
        public void ApplyCapabilities_ReEnablesADisabledProvider_RatherThanAddingASecond()
        {
            ConvaiGazeController controller = BuildCharacter();
            PlayerAttentionSensor sensor = _root.AddComponent<PlayerAttentionSensor>();
            sensor.enabled = false;

            Assert.IsTrue(GazeSetupService.ApplyCapabilities(
                controller, new[] { GazeCapabilityId.PlayerAttention }, null));

            Assert.AreEqual(1, _root.GetComponentsInChildren<PlayerAttentionSensor>(true).Length,
                "Two components fighting over one job is worse than the disabled one.");
            Assert.IsTrue(sensor.enabled);
        }

        [Test]
        public void RecommendedCapabilities_AreAllRealCapabilities()
        {
            var known = new HashSet<GazeCapabilityId>();
            foreach (GazeCapabilityId id in System.Enum.GetValues(typeof(GazeCapabilityId)))
                known.Add(id);

            foreach (GazeCapabilityId id in GazeSetupOptions.RecommendedCapabilities)
                Assert.IsTrue(known.Contains(id), $"{id} is recommended but not in the catalogue.");
        }

        // ------------------------------------------------------------------ apply

        [Test]
        public void Apply_ReportsWhatItDid_AndIsIdempotent()
        {
            ConvaiGazeController controller = BuildCharacter();

            GazeSetupResult first = GazeSetupService.Apply(controller, GazeSetupOptions.Default);
            Assert.IsTrue(first.Changed);
            Assert.IsNotEmpty(first.Summary);
            Assert.IsNotEmpty(first.Notes, "Setup must say what it did, not just that it ran.");

            GazeSetupResult second = GazeSetupService.Apply(controller, GazeSetupOptions.Default);
            Assert.IsFalse(second.Changed,
                "Pressing the setup button twice must not keep changing the character.");
        }

        [Test]
        public void Apply_NullController_IsSafe()
        {
            GazeSetupResult result = GazeSetupService.Apply(null, GazeSetupOptions.Default);
            Assert.IsFalse(result.Changed);
            Assert.IsNotEmpty(result.Summary);
        }
    }
}
