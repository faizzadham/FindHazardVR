using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyAnimation.Executors;
using Convai.Modules.BodyLanguage.Executors;
using Convai.Modules.Emotion.Executors;
using Convai.Modules.Gaze.Executors;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     EditMode coverage for the module-backed Action Behaviors: Attention (Gaze), Expression
    ///     (Emotion, Body Language), Gesture and Movement (Body Animation).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         What these tests can prove without a rig is the contract every one of them shares: a
    ///         behavior whose module is absent <em>declines</em> rather than failing or throwing, it
    ///         declines without side effects, and it reads the words a language model actually sends.
    ///         That contract is what makes a character degrade gracefully when a module is not
    ///         installed, and it is exactly what regresses silently.
    ///     </para>
    ///     <para>
    ///         What they deliberately do not attempt: whether a gaze settles, whether a nod looks
    ///         like a nod, whether a walk paths around an obstacle. Those need a rigged character, a
    ///         baked NavMesh, and a human watching — they belong to the manual scene pass, and
    ///         asserting a fake version of them here would buy confidence that is not real.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public class ConvaiModuleActionBehaviorTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(NamePrefix, StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── Every module behavior declines when its module is not on the character ────────

        [Test]
        public async Task LookAtTarget_WithoutGaze_Declines()
        {
            var behavior = NewBehavior<ConvaiLookAtActionExecutor>();
            GameObject target = NewGameObject("Target");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Look At", target), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiGazeController");
        }

        [Test]
        public async Task WatchThePlayer_WithoutGaze_Declines()
        {
            var behavior = NewBehavior<ConvaiWatchPlayerActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiGazeController");
        }

        [Test]
        public async Task ScanEnvironment_WithoutGaze_Declines()
        {
            var behavior = NewBehavior<ConvaiScanEnvironmentActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiGazeController");
        }

        [Test]
        public async Task SetMood_WithoutEmotion_Declines()
        {
            var behavior = NewBehavior<ConvaiSetMoodActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiEmotionController");
        }

        [Test]
        public async Task React_WithoutEmotion_Declines()
        {
            var behavior = NewBehavior<ConvaiReactActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiEmotionController");
        }

        [Test]
        public async Task NodOrShakeHead_WithoutBodyLanguage_Declines()
        {
            var behavior = NewBehavior<ConvaiHeadResponseActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiBodyLanguageController");
        }

        [Test]
        public async Task PlayGesture_WithoutBodyAnimation_Declines()
        {
            var behavior = NewBehavior<ConvaiPlayGestureActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationForAction("Wave"), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiBodyAnimationController");
        }

        [Test]
        public async Task PointAtTarget_WithoutBodyAnimation_Declines()
        {
            var behavior = NewBehavior<ConvaiPointAtActionExecutor>();
            GameObject target = NewGameObject("Target");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Point At", target), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiBodyAnimationController");
        }

        [Test]
        public async Task LeadPlayer_WithoutLocomotion_Declines()
        {
            var behavior = NewBehavior<ConvaiLeadPlayerActionExecutor>();
            GameObject target = NewGameObject("Target");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Lead Player", target), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiNavMeshLocomotion");
        }

        [Test]
        public async Task WalkToTarget_WithoutLocomotion_Declines()
        {
            var behavior = NewBehavior<ConvaiWalkToActionExecutor>();
            GameObject target = NewGameObject("Target");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Walk To", target), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiNavMeshLocomotion");
        }

        [Test]
        public async Task TurnToFaceTarget_WithoutBodyAnimation_Declines()
        {
            var behavior = NewBehavior<ConvaiTurnToFaceActionExecutor>();
            GameObject target = NewGameObject("Target");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Turn To Face", target), CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiBodyAnimationController");
        }

        [Test]
        public async Task FollowThePlayer_WithoutLocomotion_Declines()
        {
            var behavior = NewBehavior<ConvaiFollowPlayerActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiNavMeshLocomotion");
            Assert.That(behavior.IsFollowing, Is.False, "A declined follow must not leave the character following.");
        }

        [Test]
        public async Task ReturnToStart_WithoutLocomotion_Declines()
        {
            var behavior = NewBehavior<ConvaiReturnToStartActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            AssertDeclinedNaming(result, "ConvaiNavMeshLocomotion");
        }

        // ── Missing target is a decline too, not a crash ──────────────────────────────────

        [Test]
        public async Task LookAtTarget_WithNothingToLookAt_Declines()
        {
            var behavior = NewBehavior<ConvaiLookAtActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Look At", null), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
        }

        [Test]
        public async Task WalkToTarget_WithNowhereToGo_Declines()
        {
            var behavior = NewBehavior<ConvaiWalkToActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                InvocationWithTarget("Walk To", null), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
        }

        // ── The words a language model actually sends ────────────────────────────────────

        [TestCase("glance", ConvaiGazeLookMode.Glance)]
        [TestCase("quick", ConvaiGazeLookMode.Glance)]
        [TestCase("sustained", ConvaiGazeLookMode.Sustained)]
        [TestCase("stare", ConvaiGazeLookMode.Sustained)]
        [TestCase("sideways", ConvaiGazeLookMode.Sustained)]
        public void LookAtTarget_ReadsTheEverydayWordsForHowLongToLook(string requested, ConvaiGazeLookMode expected)
        {
            Assert.That(ConvaiLookAtActionExecutor.ParseMode(requested, ConvaiGazeLookMode.Sustained), Is.EqualTo(expected));
        }

        [TestCase("watch", ConvaiWatchPlayerMode.Watch)]
        [TestCase("stop", ConvaiWatchPlayerMode.StopWatching)]
        [TestCase("stop_watching", ConvaiWatchPlayerMode.StopWatching)]
        [TestCase("away", ConvaiWatchPlayerMode.StopWatching)]
        public void WatchThePlayer_ReadsBothStartingAndStopping(string requested, ConvaiWatchPlayerMode expected)
        {
            Assert.That(ConvaiWatchPlayerActionExecutor.ParseMode(requested, ConvaiWatchPlayerMode.Watch), Is.EqualTo(expected));
        }

        [TestCase("yes", HeadGestureKind.Nod)]
        [TestCase("agree", HeadGestureKind.Nod)]
        [TestCase("no", HeadGestureKind.Shake)]
        [TestCase("refuse", HeadGestureKind.Shake)]
        [TestCase("maybe", HeadGestureKind.Tilt)]
        [TestCase("think", HeadGestureKind.Tilt)]
        public void NodOrShakeHead_IsAskedForAMeaning_NotForAMotion(string requested, HeadGestureKind expected)
        {
            Assert.That(ConvaiHeadResponseActionExecutor.ParseResponse(requested, HeadGestureKind.Nod), Is.EqualTo(expected));
        }

        [TestCase("follow", ConvaiFollowMode.Follow)]
        [TestCase("come", ConvaiFollowMode.Follow)]
        [TestCase("stop", ConvaiFollowMode.Stop)]
        [TestCase("stay", ConvaiFollowMode.Stop)]
        [TestCase("wait", ConvaiFollowMode.Stop)]
        public void FollowThePlayer_ReadsBothStartingAndStopping(string requested, ConvaiFollowMode expected)
        {
            Assert.That(ConvaiFollowPlayerActionExecutor.ParseMode(requested, ConvaiFollowMode.Follow), Is.EqualTo(expected));
        }

        /// <summary>
        ///     Following outlives the action that started it, so it is still re-aiming while later
        ///     actions arrive. Without standing down it would overwrite their destination four times a
        ///     second, and a character asked to walk somewhere while following would take two steps
        ///     and come straight back.
        /// </summary>
        [TestCase(true, TestName = "FollowThePlayer_StandsAsideForAnotherWalk_AndResumesWhenItArrives")]
        [TestCase(false, TestName = "FollowThePlayer_StandsAsideForAnotherWalk_AndResumesWhenItIsCutShort")]
        public void FollowThePlayer_StandsAsideForAnotherWalk(bool otherWalkArrived)
        {
            var behavior = NewBehavior<ConvaiFollowPlayerActionExecutor>();

            Assert.That(behavior.IsStandingDown, Is.False,
                "A follow that has never seen another walk is driving its own legs.");

            behavior.HandleMoveStarted(Vector3.zero);
            Assert.That(behavior.IsStandingDown, Is.True,
                "A move this follow did not order belongs to another action, which has to be allowed to finish it.");

            behavior.HandleMoveEnded(otherWalkArrived);
            Assert.That(behavior.IsStandingDown, Is.False,
                "Once the other action's move is over nobody else is steering, arrived or not, so the follow resumes.");
        }

        [Test]
        public void PlayGesture_FallsBackToTheActionsOwnName_SoOneActionPerGestureNeedsNoParameters()
        {
            ConvaiActionInvocation invocation = InvocationForAction("Bow");

            Assert.That(ConvaiPlayGestureActionExecutor.ResolveGestureName(invocation, string.Empty), Is.EqualTo("Bow"));
            Assert.That(ConvaiPlayGestureActionExecutor.ResolveGestureName(invocation, "Wave"), Is.EqualTo("Wave"),
                "An authored default outranks the action name.");
        }

        // ── Movement geometry, which is pure maths and worth pinning down ─────────────────

        [Test]
        public void WalkToTarget_StopsShortOfItsDestination_OnTheGroundPlane()
        {
            Vector3 standingSpot = ConvaiWalkToActionExecutor.ResolveStandingSpot(
                Vector3.zero, new Vector3(10f, 0f, 0f), 1f);

            Assert.That(standingSpot.x, Is.EqualTo(9f).Within(0.001f));
        }

        [Test]
        public void WalkToTarget_IgnoresHeight_SoAWallMountedTargetIsStillApproachedAcrossTheFloor()
        {
            Vector3 standingSpot = ConvaiWalkToActionExecutor.ResolveStandingSpot(
                Vector3.zero, new Vector3(10f, 5f, 0f), 1f);

            Assert.That(standingSpot.x, Is.EqualTo(9f).Within(0.001f),
                "The approach is measured flat; height must not shorten the walk.");
            Assert.That(standingSpot.y, Is.EqualTo(5f).Within(0.001f), "The destination's own height is preserved.");
        }

        [Test]
        public void WalkToTarget_AlreadyCloseEnough_DoesNotWalkPastTheTarget()
        {
            Vector3 standingSpot = ConvaiWalkToActionExecutor.ResolveStandingSpot(
                Vector3.zero, new Vector3(0.5f, 0f, 0f), 1f);

            Assert.That(standingSpot, Is.EqualTo(new Vector3(0.5f, 0f, 0f)),
                "Standing inside the arrival distance must not push the destination behind the character.");
        }

        // ── Catalog metadata ─────────────────────────────────────────────────────────────

        [Test]
        public void EveryModuleBehavior_IsPickableFromTheCatalogUnderAPlainEnglishName()
        {
            AssertArchetype<ConvaiLookAtActionExecutor>("Look At Target", "ConvaiGazeController");
            AssertArchetype<ConvaiWatchPlayerActionExecutor>("Watch The Player", "ConvaiGazeController");
            AssertArchetype<ConvaiScanEnvironmentActionExecutor>("Scan Environment", "ConvaiGazeController");
            AssertArchetype<ConvaiSetMoodActionExecutor>("Set Mood", "ConvaiEmotionController");
            AssertArchetype<ConvaiReactActionExecutor>("React", "ConvaiEmotionController");
            AssertArchetype<ConvaiHeadResponseActionExecutor>("Nod Or Shake Head", "ConvaiBodyLanguageController");
            AssertArchetype<ConvaiPlayGestureActionExecutor>("Play Gesture", "ConvaiBodyAnimationController");
            AssertArchetype<ConvaiPointAtActionExecutor>("Point At Target", "ConvaiBodyAnimationController");
            AssertArchetype<ConvaiLeadPlayerActionExecutor>("Lead Player To Target", "ConvaiNavMeshLocomotion");
            AssertArchetype<ConvaiWalkToActionExecutor>("Walk To Target", "ConvaiNavMeshLocomotion");
            AssertArchetype<ConvaiTurnToFaceActionExecutor>("Turn To Face Target", "ConvaiBodyAnimationController");
            AssertArchetype<ConvaiFollowPlayerActionExecutor>("Follow The Player", "ConvaiNavMeshLocomotion");
            AssertArchetype<ConvaiReturnToStartActionExecutor>("Return To Start", "ConvaiNavMeshLocomotion");
        }

        // ── Fixture helpers ──────────────────────────────────────────────────────────────

        private const string NamePrefix = "ModuleActionBehaviorTests_";

        private static GameObject NewGameObject(string suffix) => new($"{NamePrefix}{suffix}");

        private static T NewBehavior<T>() where T : Component => NewGameObject(typeof(T).Name).AddComponent<T>();

        /// <summary>
        ///     A decline names the component the author has to add. A message that only says "cannot
        ///     run" turns a two-second fix into a hunt, which is why the name is asserted rather than
        ///     just the status.
        /// </summary>
        private static void AssertDeclinedNaming(ConvaiActionExecutionResult result, string expectedComponentName)
        {
            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled),
                "A missing module is a soft decline: another behavior may still answer this action.");
            Assert.That(result.Message, Does.Contain(expectedComponentName),
                "The message must name the component that is missing.");
        }

        private static ConvaiActionInvocation InvocationWithTarget(string actionName, GameObject targetObject)
        {
            ConvaiResolvedActionTarget resolvedTarget = targetObject == null
                ? null
                : ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition
                {
                    Name = targetObject.name,
                    GameObjectReference = targetObject
                });

            return new ConvaiActionInvocation(new ConvaiActionCommand(actionName), null, resolvedTarget, null, 0, 0);
        }

        private static ConvaiActionInvocation InvocationForAction(string actionName) =>
            new(new ConvaiActionCommand(actionName), new ConvaiActionDefinition { ActionName = actionName }, null, null, 0, 0);

        private static void AssertArchetype<T>(string expectedDisplayName, string expectedPeerHint)
        {
            var archetype = typeof(T).GetCustomAttribute<ConvaiActionArchetypeAttribute>();
            Assert.That(archetype, Is.Not.Null, $"{typeof(T).Name} must carry [ConvaiActionArchetype].");
            Assert.That(archetype.DisplayName, Is.EqualTo(expectedDisplayName));
            Assert.That(archetype.RequiredPeerHint, Is.EqualTo(expectedPeerHint),
                "The peer hint is what lets the editor offer to add the missing component.");
            Assert.That(archetype.Description, Is.Not.Null.And.Not.Empty);

            var menu = typeof(T).GetCustomAttribute<AddComponentMenu>();
            Assert.That(menu, Is.Not.Null, $"{typeof(T).Name} must be findable under Add Component.");
            StringAssert.StartsWith("Convai/Actions/", menu.componentMenu);
        }
    }
}
