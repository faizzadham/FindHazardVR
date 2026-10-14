using System.Collections.Generic;
using System.Linq;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Modules.BodyAnimation.Editor;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="BodyAnimationSetupService" /> — the single code path that
    ///     configures body animation on a character, and the preflight that decides what the setup
    ///     card is allowed to promise before the user presses anything.
    /// </summary>
    public sealed class BodyAnimationSetupServiceTests
    {
        private readonly List<Object> _cleanup = new();

        /// <summary>Watches the folder copy-on-write writes a character's own config into.</summary>
        private ProjectAssetFolderGuard _projectAssets;

        [SetUp]
        public void SetUp() => _projectAssets = ProjectAssetFolderGuard.Watch();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
            {
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _cleanup.Clear();

            _projectAssets.RemoveWhatTheTestWrote();
        }

        /// <summary>
        ///     A controller on a valid embodiment root, which is what the setup service is asked
        ///     about in practice.
        /// </summary>
        /// <remarks>
        ///     The <see cref="EmbodimentContext" /> is added <b>before</b> the controller and is not
        ///     optional. <c>ConvaiCharacterModule.OnEnable</c> resolves its context the moment
        ///     <c>AddComponent</c> returns, and with no context it correctly logs a setup error
        ///     ("… is not on a Convai character") — which the test framework then counts as an
        ///     unexpected error and fails the test on. Silencing it with <c>LogAssert.Expect</c>
        ///     would keep every test in this fixture exercising the not-on-a-character path by
        ///     accident, and would break again the next time that message is reworded.
        /// </remarks>
        private ConvaiBodyAnimationController CreateController(string name = "SetupTestCharacter")
        {
            var root = new GameObject(name);
            _cleanup.Add(root);
            root.AddComponent<EmbodimentContext>();
            return root.AddComponent<ConvaiBodyAnimationController>();
        }

        private static BodyAnimationCheck Check(BodyAnimationPreflight preflight, string id) =>
            preflight.Checks.Single(c => c.Id == id);

        // ------------------------------------------------------------------ preflight honesty

        [Test]
        public void Inspect_AlwaysReportsTheSameFourChecks()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(CreateController());

            Assert.AreEqual(4, preflight.Checks.Count);
            CollectionAssert.AreEquivalent(
                new[]
                {
                    BodyAnimationSetupService.CheckRig,
                    BodyAnimationSetupService.CheckCharacter,
                    BodyAnimationSetupService.CheckContent,
                    BodyAnimationSetupService.CheckMovement
                },
                preflight.Checks.Select(c => c.Id).ToArray());
        }

        /// <summary>
        ///     A rig cannot be authored for the user, so it must read as Blocked — the card disables
        ///     its button on this, rather than running setup and half-failing.
        /// </summary>
        [Test]
        public void Inspect_WithNoAnimator_BlocksOnTheRig()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(CreateController());

            Assert.AreEqual(BodyAnimationCheckState.Blocked,
                Check(preflight, BodyAnimationSetupService.CheckRig).State);
            Assert.IsTrue(preflight.HasBlocker);
            Assert.IsFalse(preflight.IsConfigured);
        }

        [Test]
        public void Inspect_WithNonHumanoidAnimator_BlocksOnTheRig_AndSaysWhy()
        {
            ConvaiBodyAnimationController controller = CreateController();
            controller.gameObject.AddComponent<Animator>();

            BodyAnimationCheck rig = Check(
                BodyAnimationSetupService.Inspect(controller), BodyAnimationSetupService.CheckRig);

            Assert.AreEqual(BodyAnimationCheckState.Blocked, rig.State);
            StringAssert.Contains("Humanoid", rig.Detail);
        }

        /// <summary>
        ///     Movement is a legitimate choice, not a defect: a stationary character still idles,
        ///     talks, gestures and points. It must never block or nag.
        /// </summary>
        [Test]
        public void Inspect_WithoutMovement_ReportsItAsOptional()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(CreateController());

            Assert.AreEqual(BodyAnimationCheckState.Optional,
                Check(preflight, BodyAnimationSetupService.CheckMovement).State);
        }

        [Test]
        public void Inspect_WithMovementPresent_ReportsItAsOk()
        {
            ConvaiBodyAnimationController controller = CreateController();
            controller.gameObject.AddComponent<ConvaiNavMeshLocomotion>();

            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(controller);

            Assert.AreEqual(BodyAnimationCheckState.Ok,
                Check(preflight, BodyAnimationSetupService.CheckMovement).State);
        }

        [Test]
        public void Inspect_WithoutContent_ReportsContentAsNotSatisfied()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(CreateController());

            BodyAnimationCheck content = Check(preflight, BodyAnimationSetupService.CheckContent);
            Assert.AreNotEqual(BodyAnimationCheckState.Ok, content.State,
                "A controller with no set or profile must not report content as satisfied.");
        }

        /// <summary>A set assigned directly (no profile) satisfies the content check.</summary>
        [Test]
        public void Inspect_WithDirectSet_ReportsContentAsOk()
        {
            ConvaiBodyAnimationController controller = CreateController();
            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);

            var serialized = new SerializedObject(controller);
            serialized.FindProperty("_animationSet").objectReferenceValue = set;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(controller);

            Assert.AreEqual(BodyAnimationCheckState.Ok,
                Check(preflight, BodyAnimationSetupService.CheckContent).State);
        }

        // ------------------------------------------------------------------ apply

        /// <summary>
        ///     Setup is idempotent: pressing it on an already-configured character must report that
        ///     nothing changed rather than duplicating components.
        /// </summary>
        [Test]
        public void Apply_Twice_DoesNotDuplicateMovement()
        {
            ConvaiBodyAnimationController controller = CreateController();

            BodyAnimationSetupService.Apply(controller, new BodyAnimationSetupOptions { IncludeMovement = true });
            BodyAnimationSetupResult second = BodyAnimationSetupService.Apply(
                controller, new BodyAnimationSetupOptions { IncludeMovement = true });

            Assert.AreEqual(1, controller.GetComponentsInChildren<ConvaiNavMeshLocomotion>(true).Length);
            Assert.IsFalse(second.Changed, "A second run has nothing left to do.");
        }

        [Test]
        public void Apply_WithoutMovementOption_LeavesTheCharacterStationary()
        {
            ConvaiBodyAnimationController controller = CreateController();

            BodyAnimationSetupService.Apply(controller, new BodyAnimationSetupOptions { IncludeMovement = false });

            Assert.IsNull(controller.GetComponentInChildren<ConvaiNavMeshLocomotion>(true));
        }

        /// <summary>Everything setup does — and everything it could not do — must be reported.</summary>
        [Test]
        public void Apply_ReportsWhatHappened()
        {
            ConvaiBodyAnimationController controller = CreateController();

            BodyAnimationSetupResult result = BodyAnimationSetupService.Apply(
                controller, new BodyAnimationSetupOptions { IncludeMovement = true });

            Assert.IsNotNull(result.Notes);
            Assert.IsNotEmpty(result.Summary);
            Assert.IsTrue(result.Notes.Count > 0,
                "Adding movement must be reported so the change is never silent.");
        }

        [Test]
        public void Apply_WithNullController_IsASafeNoOp()
        {
            BodyAnimationSetupResult result = BodyAnimationSetupService.Apply(
                null, BodyAnimationSetupOptions.Default);

            Assert.IsFalse(result.Changed);
            Assert.IsNotEmpty(result.Summary);
        }

        // ------------------------------------------------------------------ fixes

        /// <summary>
        ///     Only fixes this service can actually perform may advertise a button, or a surface
        ///     would render one that does nothing.
        /// </summary>
        [Test]
        public void CharacterScopedFixes_AreTheOnlyOnesThatDescribeAButton()
        {
            Assert.IsNotNull(BodyAnimationSetupService.DescribeFix(BodyAnimationFixId.AssignDefaultContent));
            Assert.IsNotNull(BodyAnimationSetupService.DescribeFix(BodyAnimationFixId.AddMovement));
            Assert.IsNotNull(BodyAnimationSetupService.DescribeFix(BodyAnimationFixId.ClearAnimatorController));

            Assert.IsNull(BodyAnimationSetupService.DescribeFix(BodyAnimationFixId.None));
            Assert.IsNull(BodyAnimationSetupService.DescribeFix(BodyAnimationFixId.GenerateUpperBodyMask),
                "Set-scoped fixes belong to BodyAnimationFixes, not the setup service.");
        }

        /// <summary>
        ///     Missing clips are the ordinary starting state of a project that has not imported the
        ///     samples, so the content row must never claim the character is broken. Every surface
        ///     colours and counts rows from this state, and <c>Blocked</c> is the one that reads as
        ///     an error and disables the setup button — which would then be unavailable for the rest
        ///     of the setup it is still perfectly able to do.
        /// </summary>
        [Test]
        public void Inspect_ContentRow_IsNeverBlocked()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(CreateController());
            BodyAnimationCheck content = Check(preflight, BodyAnimationSetupService.CheckContent);

            Assert.AreNotEqual(BodyAnimationCheckState.Blocked, content.State,
                "A character with no clips is unfinished, not broken — Blocked belongs to the rig.");
            Assert.That(content.State,
                Is.EqualTo(BodyAnimationCheckState.Fixable)
                    .Or.EqualTo(BodyAnimationCheckState.NeedsContent),
                "Content is either assignable from what this project already has, or waiting on "
                + "content that does not exist yet. There is no third answer.");
        }

        [Test]
        public void ApplyFix_AddMovement_AddsLocomotionOnce()
        {
            ConvaiBodyAnimationController controller = CreateController();

            Assert.IsTrue(BodyAnimationSetupService.ApplyFix(controller, BodyAnimationFixId.AddMovement));
            Assert.IsFalse(BodyAnimationSetupService.ApplyFix(controller, BodyAnimationFixId.AddMovement));
            Assert.AreEqual(1, controller.GetComponentsInChildren<ConvaiNavMeshLocomotion>(true).Length);
        }

        [Test]
        public void ApplyFix_ClearAnimatorController_ClearsIt()
        {
            ConvaiBodyAnimationController controller = CreateController();
            Animator animator = controller.gameObject.AddComponent<Animator>();
            animator.runtimeAnimatorController = new UnityEditor.Animations.AnimatorController();

            Assert.IsTrue(
                BodyAnimationSetupService.ApplyFix(controller, BodyAnimationFixId.ClearAnimatorController));
            Assert.IsNull(animator.runtimeAnimatorController);
        }
    }
}
