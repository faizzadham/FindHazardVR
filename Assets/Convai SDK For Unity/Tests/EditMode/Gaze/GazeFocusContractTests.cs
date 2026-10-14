using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Every fixture here builds its character with a <see cref="ConvaiCharacter" /> before adding
    ///     Gaze. Gaze correctly reports an unusable setup when it cannot resolve one, and that error
    ///     fails the whole test — so a bare GameObject is not a lighter test rig, it is an invalid one.
    /// </summary>
    public sealed class GazeFocusContractTests
    {
        [Test]
        public void PublicEnumValues_AreStable_AndNaturalSocialAreDefaults()
        {
            Assert.That((int)GazeEyeContactMode.Natural, Is.EqualTo(0));
            Assert.That((int)GazeEyeContactMode.ConversationLock, Is.EqualTo(1));
            Assert.That((int)GazeEyeContactMode.AlwaysLock, Is.EqualTo(2));
            Assert.That((int)GazeEyeContactMode.SpeakingFocus, Is.EqualTo(3));
            Assert.That((int)GazeFocusFidelity.Social, Is.EqualTo(0));
            Assert.That((int)GazeFocusFidelity.Exact, Is.EqualTo(1));
            Assert.That((int)GazeAnchorAimMode.Auto, Is.EqualTo(0));
            Assert.That((int)GazeAnchorAimMode.ExactTransform, Is.EqualTo(1));
            Assert.That((int)GazeAnchorAimMode.LocalOffset, Is.EqualTo(2));

            var root = new GameObject("GazeDefaults");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                var controller = root.AddComponent<ConvaiGazeController>();
                Assert.That(controller.EyeContactMode, Is.EqualTo(GazeEyeContactMode.Natural));
                Assert.That(controller.FocusFidelity, Is.EqualTo(GazeFocusFidelity.Social));
                Assert.That(controller.PlayerAnchorAimMode, Is.EqualTo(GazeAnchorAimMode.Auto));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SpeakingFocus_UsesSpeechOrDialogue_AndReleaseGrace()
        {
            var evaluator = new GazeFocusScopeEvaluator();

            Assert.IsTrue(evaluator.Evaluate(
                GazeEyeContactMode.SpeakingFocus, DialogueState.Idle, true, 0.016f));
            Assert.IsTrue(evaluator.Evaluate(
                GazeEyeContactMode.SpeakingFocus, DialogueState.Speaking, false, 0.016f));
            Assert.IsTrue(evaluator.Evaluate(
                GazeEyeContactMode.SpeakingFocus, DialogueState.Idle, false, 0.19f));
            Assert.IsFalse(evaluator.Evaluate(
                GazeEyeContactMode.SpeakingFocus, DialogueState.Idle, false, 0.02f));
        }

        [Test]
        public void FocusScope_ModeMatrix_IsDeterministic()
        {
            var evaluator = new GazeFocusScopeEvaluator();
            Assert.IsFalse(evaluator.Evaluate(GazeEyeContactMode.Natural, DialogueState.Speaking, true, 1f));
            Assert.IsFalse(evaluator.Evaluate(GazeEyeContactMode.ConversationLock, DialogueState.Idle, false, 1f));
            Assert.IsTrue(evaluator.Evaluate(GazeEyeContactMode.ConversationLock, DialogueState.Listening, false, 1f));
            Assert.IsTrue(evaluator.Evaluate(GazeEyeContactMode.AlwaysLock, DialogueState.Idle, false, 1f));
        }

        [Test]
        public void AnchorAimModes_PreserveAuto_AndRespectRotatedLocalOffset()
        {
            var root = new GameObject("AnchorProvider");
            var anchor = new GameObject("Anchor").transform;
            try
            {
                var provider = root.AddComponent<PlayerAnchorTargetProvider>();
                provider.ExplicitAnchor = anchor;
                anchor.position = new Vector3(2f, 3f, 4f);
                anchor.rotation = Quaternion.Euler(35f, 90f, 20f);

                provider.AimMode = GazeAnchorAimMode.Auto;
                Assert.IsTrue(provider.TryGetFocusCandidate(out var automatic));
                Assert.That(automatic.WorldPoint, Is.EqualTo(anchor.position + Vector3.up * 1.6f));

                provider.AimMode = GazeAnchorAimMode.ExactTransform;
                Assert.IsTrue(provider.TryGetFocusCandidate(out var exact));
                Assert.That(exact.WorldPoint, Is.EqualTo(anchor.position));

                provider.AimMode = GazeAnchorAimMode.LocalOffset;
                provider.LocalAimOffset = new Vector3(0f, 0f, 1f);
                Assert.IsTrue(provider.TryGetFocusCandidate(out var local));
                Assert.That(Vector3.Distance(local.WorldPoint, anchor.TransformPoint(provider.LocalAimOffset)),
                    Is.LessThan(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(anchor.gameObject);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Settlement_RequiresConsecutiveTwoAxisContactSamples()
        {
            var evaluator = new GazeSettlementEvaluator();
            Assert.IsFalse(evaluator.Tick(7, true, 1f));
            Assert.IsFalse(evaluator.Tick(7, true, 1f));
            Assert.IsTrue(evaluator.Tick(7, true, 1f));
            Assert.IsFalse(evaluator.Tick(8, true, 1f), "Target change resets the streak.");
            Assert.IsFalse(evaluator.Tick(8, true, 4f), "Out-of-cone contact resets the streak.");
            Assert.IsFalse(evaluator.Tick(8, false, 0f), "Low commitment cannot settle.");
            Assert.That(ConvaiGazeController.ComputeHeadFacingError(Vector3.forward, Vector3.forward),
                Is.EqualTo(0f).Within(0.0001f));
            Assert.That(ConvaiGazeController.ComputeHeadFacingError(Vector3.forward, Vector3.right),
                Is.EqualTo(90f).Within(0.0001f));
        }

        [Test]
        public void SocialFocus_ClampsMicroLife_WhileNaturalIsUnchanged()
        {
            Vector2 authored = new Vector2(2f, -1f);
            Assert.That(ConvaiGazeController.ConstrainMicroOffset(authored, false), Is.EqualTo(authored));
            Assert.That(ConvaiGazeController.ConstrainMicroOffset(authored, true).magnitude,
                Is.EqualTo(0.75f).Within(0.0001f));
            Assert.That(ConvaiGazeController.ResolveFocusAversionOffset(authored, false), Is.EqualTo(authored));
            Assert.That(ConvaiGazeController.ResolveFocusAversionOffset(authored, true), Is.EqualTo(Vector2.zero),
                "Entering Social or Exact focus masks an already-active planning break immediately.");

            var turnTaking = new TurnTakingDirector();
            System.Reflection.BindingFlags flags =
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
            typeof(TurnTakingDirector).GetField("_breakScheduled", flags)?.SetValue(turnTaking, true);
            typeof(TurnTakingDirector).GetField("_breakActive", flags)?.SetValue(turnTaking, true);
            turnTaking.CancelPlanningBreak();
            Assert.IsFalse(turnTaking.PlanningBreakActive);
            Assert.IsFalse((bool)typeof(TurnTakingDirector).GetField("_breakScheduled", flags)!.GetValue(turnTaking));
        }

        [Test]
        public void ExactFocus_RejectsScriptedRequestUnlessExplicitlyAllowed()
        {
            var root = new GameObject("ExactFocus");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                var controller = root.AddComponent<ConvaiGazeController>();
                controller.EyeContactMode = GazeEyeContactMode.AlwaysLock;
                controller.FocusFidelity = GazeFocusFidelity.Exact;
                typeof(ConvaiGazeController).GetField("_focusActive",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.SetValue(controller, true);

                var rejected = controller.GazeAt(Vector3.forward);
                Assert.IsFalse(rejected.IsActive);
                Assert.IsTrue(rejected.Settled.IsCompleted);
                Assert.IsFalse(rejected.Settled.Result);

                controller.AllowScriptedOverridesDuringExactFocus = true;
                var allowed = controller.GazeAt(Vector3.forward);
                Assert.IsTrue(allowed.IsActive);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ProvisioningAndArbitrationSeams_PreserveNaturalAndPinFocusedPlayer()
        {
            Assert.IsFalse(ConvaiGazeController.ShouldProvisionPlayerAnchor(
                true, false, providerCount: 1, runtimeProviderCount: 0, focusActive: false),
                "Natural with an intentional custom provider preserves its legacy target set.");
            Assert.IsTrue(ConvaiGazeController.ShouldProvisionPlayerAnchor(
                true, false, providerCount: 1, runtimeProviderCount: 0, focusActive: true),
                "A focus contract provisions its own player anchor even with custom providers.");
            Assert.IsTrue(ConvaiGazeController.ShouldUseFocusedPlayerCandidates(true, false));
            Assert.IsFalse(ConvaiGazeController.ShouldUseFocusedPlayerCandidates(true, true),
                "Social focus preserves explicit scripted preemption.");
            Assert.IsTrue(ConvaiGazeController.ShouldResetArbiterForMissingFocus(true, 0, false));
            Assert.IsFalse(ConvaiGazeController.ShouldResetArbiterForMissingFocus(true, 1, false));
            Assert.IsFalse(ConvaiGazeController.ShouldResetArbiterForMissingFocus(true, 0, true));
        }

        [Test]
        public void FocusCandidate_BypassesRangeAndOutranksAutomaticProviderTiers()
        {
            var root = new GameObject("FarPlayerProvider");
            var character = new GameObject("Character").transform;
            var anchor = new GameObject("FarAnchor").transform;
            try
            {
                var provider = root.AddComponent<PlayerAnchorTargetProvider>();
                provider.ExplicitAnchor = anchor;
                anchor.position = new Vector3(0f, 0f, 100f);
                provider.Configure(8f, 4f, false, Physics.DefaultRaycastLayers);

                Assert.IsFalse(provider.TryGetCandidate(character, out _),
                    "Natural sensing rejects a player outside max distance.");
                Assert.IsTrue(provider.TryGetFocusCandidate(out var focus));
                Assert.That(focus.Priority, Is.EqualTo(int.MaxValue));
                Assert.That(focus.Relevance, Is.EqualTo(1f));
                Assert.IsTrue(ConvaiGazeController.IsUsableFocusProvider(provider));
                provider.enabled = false;
                Assert.IsFalse(ConvaiGazeController.IsUsableFocusProvider(provider),
                    "A disabled provider cannot own contractual focus.");
                Assert.IsFalse(ConvaiGazeController.ShouldProvisionPlayerAnchor(
                    true, hasPlayerProvider: true, providerCount: 1,
                    runtimeProviderCount: 0, focusActive: true),
                    "A disabled component blocks duplicate AddComponent and degrades cleanly.");
            }
            finally
            {
                Object.DestroyImmediate(anchor.gameObject);
                Object.DestroyImmediate(character.gameObject);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void LiveResampling_IsRestrictedToEngagedFocusedPlayer()
        {
            Assert.IsTrue(ConvaiGazeController.ShouldResampleFocusedPlayer(
                true, true, GazeTargetKind.Player));
            Assert.IsFalse(ConvaiGazeController.ShouldResampleFocusedPlayer(
                false, true, GazeTargetKind.Player), "Natural provider-authored WorldPoint is preserved.");
            Assert.IsFalse(ConvaiGazeController.ShouldResampleFocusedPlayer(
                true, true, GazeTargetKind.WorldObject));
            Assert.IsFalse(ConvaiGazeController.ShouldResampleFocusedPlayer(
                true, false, GazeTargetKind.Player));
        }

        [Test]
        public void RuntimeSwitchToExact_CompletesPreExistingScriptedHandle()
        {
            var root = new GameObject("ExactTransition");
            try
            {
                root.AddComponent<ConvaiCharacter>();
                var controller = root.AddComponent<ConvaiGazeController>();
                GazeHandle held = controller.GazeAt(Vector3.forward);
                Assert.IsTrue(held.IsActive);

                typeof(ConvaiGazeController).GetMethod("RejectScriptedRequestsForExactFocus",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                    ?.Invoke(controller, null);

                Assert.IsFalse(held.IsActive);
                Assert.IsTrue(held.Completion.IsCompleted);
                Assert.IsTrue(held.Settled.IsCompleted);
                Assert.IsFalse(held.Settled.Result);
                Assert.That(controller.ScriptedStack.Count, Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ExplicitControllerAutoAim_CanRestoreManualProviderAutoMode()
        {
            var root = new GameObject("AimOwnership");
            try
            {
                var provider = root.AddComponent<PlayerAnchorTargetProvider>();
                provider.AimMode = GazeAnchorAimMode.ExactTransform;
                root.AddComponent<ConvaiCharacter>();
                var controller = root.AddComponent<ConvaiGazeController>();
                controller.RefreshProviders();

                controller.PlayerAnchorAimMode = GazeAnchorAimMode.Auto;

                Assert.That(provider.AimMode, Is.EqualTo(GazeAnchorAimMode.Auto));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
