using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     EditMode coverage for the Flow &amp; Utility pack. These behaviors need no Convai module,
    ///     so they are the ones every project gets: <see cref="ConvaiUnityEventActionExecutor" />,
    ///     <see cref="ConvaiWaitActionExecutor" />, <see cref="ConvaiSequenceActionExecutor" />,
    ///     <see cref="ConvaiSetActiveActionExecutor" />, <see cref="ConvaiAnimatorStateActionExecutor" />,
    ///     and <see cref="ConvaiPlaySoundActionExecutor" />.
    /// </summary>
    /// <remarks>
    ///     Not covered here, deliberately rather than silently:
    ///     <see cref="ConvaiAnimatorStateActionExecutor" />'s wait-for-state path needs a real,
    ///     playing state machine (<c>GetCurrentAnimatorStateInfo</c> means nothing without one), and
    ///     whether a sound is audible cannot be asserted in EditMode. Both belong to the manual scene
    ///     pass in §6.1 of the plan; what is asserted here is every decision the behavior makes
    ///     before and around those points.
    /// </remarks>
    [TestFixture]
    public class ConvaiFlowActionBehaviorTests
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

        // ── Raise Unity Event ─────────────────────────────────────────────────────────────

        [Test]
        public async Task RaiseUnityEvent_RunsTheAuthoredEvent()
        {
            ConvaiUnityEventActionExecutor behavior = NewBehavior<ConvaiUnityEventActionExecutor>();
            var raised = 0;
            var authored = new UnityEngine.Events.UnityEvent();
            authored.AddListener(() => raised++);
            SetPrivateField(behavior, ConvaiUnityEventActionExecutor.EventFieldName, authored);

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(raised, Is.EqualTo(1));
        }

        [Test]
        public async Task RaiseUnityEvent_WithNothingWired_StillSucceeds()
        {
            ConvaiUnityEventActionExecutor behavior = NewBehavior<ConvaiUnityEventActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded),
                "An unwired placeholder must not fail at runtime — authoring tooling reports it instead.");
        }

        // ── Wait ──────────────────────────────────────────────────────────────────────────

        [Test]
        public async Task Wait_ZeroSeconds_CompletesImmediately()
        {
            ConvaiWaitActionExecutor behavior = NewBehavior<ConvaiWaitActionExecutor>();
            SetPrivateField(behavior, "_seconds", 0f);

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
        }

        [Test]
        public void Wait_AlreadyCancelled_ThrowsBeforeWaiting()
        {
            ConvaiWaitActionExecutor behavior = NewBehavior<ConvaiWaitActionExecutor>();
            SetPrivateField(behavior, "_seconds", 30f);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.That(async () => await behavior.ExecuteAsync(null, cancellation.Token),
                Throws.InstanceOf<OperationCanceledException>(),
                "Cancellation must propagate so the dispatcher can classify it as canceled or timed out.");
        }

        [Test]
        public void Wait_ClampsBothTheAuthoredValueAndTheRequestedOne()
        {
            ConvaiWaitActionExecutor behavior = NewBehavior<ConvaiWaitActionExecutor>();
            SetPrivateField(behavior, "_seconds", -5f);
            SetPrivateField(behavior, "_maxSeconds", -1f);

            InvokePrivate(behavior, "OnValidate");

            Assert.That(GetPrivateField<float>(behavior, "_seconds"), Is.Zero);
            Assert.That(GetPrivateField<float>(behavior, "_maxSeconds"), Is.Zero);
        }

        // ── Run In Order ──────────────────────────────────────────────────────────────────

        [Test]
        public async Task RunInOrder_WithNoSteps_Succeeds()
        {
            ConvaiSequenceActionExecutor behavior = NewBehavior<ConvaiSequenceActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
        }

        [Test]
        public async Task RunInOrder_RunsEveryStepInOrder()
        {
            var order = new List<string>();
            ConvaiSequenceActionExecutor behavior = NewBehavior<ConvaiSequenceActionExecutor>();
            SetPrivateField(behavior, "_steps", new List<MonoBehaviour>
            {
                NewRecordingStep("first", order, ConvaiActionExecutionResult.Succeeded()),
                NewRecordingStep("second", order, ConvaiActionExecutionResult.Succeeded())
            });

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(order, Is.EqualTo(new[] { "first", "second" }));
        }

        [Test]
        public async Task RunInOrder_StopsAtTheFirstStepThatDoesNotSucceed_AndNamesIt()
        {
            var order = new List<string>();
            ConvaiSequenceActionExecutor behavior = NewBehavior<ConvaiSequenceActionExecutor>();
            SetPrivateField(behavior, "_steps", new List<MonoBehaviour>
            {
                NewRecordingStep("first", order, ConvaiActionExecutionResult.Succeeded()),
                NewRecordingStep("second", order, ConvaiActionExecutionResult.Failed(
                    "the rig is missing", ConvaiActionFailureReason.PeerMissing)),
                NewRecordingStep("third", order, ConvaiActionExecutionResult.Succeeded())
            });

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(result.FailureReason, Is.EqualTo(ConvaiActionFailureReason.PeerMissing),
                "The failing step's own reason must survive, not be flattened into a generic failure.");
            Assert.That(result.Message, Does.Contain("Step 2").And.Contain("the rig is missing"));
            Assert.That(order, Is.EqualTo(new[] { "first", "second" }), "Steps after a failure must not run.");
        }

        [Test]
        public async Task RunInOrder_WithAnEntryThatIsNotAnActionBehavior_FailsAndNamesThePosition()
        {
            ConvaiSequenceActionExecutor behavior = NewBehavior<ConvaiSequenceActionExecutor>();
            SetPrivateField(behavior, "_steps", new List<MonoBehaviour>
            {
                NewGameObject("PlainBehavior").AddComponent<PlainBehavior>()
            });

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(result.FailureReason, Is.EqualTo(ConvaiActionFailureReason.InvalidState));
            Assert.That(result.Message, Does.Contain("Step 1"));
        }

        [Test]
        public async Task RunInOrder_ListingItself_FailsInsteadOfRecursingForever()
        {
            ConvaiSequenceActionExecutor behavior = NewBehavior<ConvaiSequenceActionExecutor>();
            SetPrivateField(behavior, "_steps", new List<MonoBehaviour> { behavior });

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(null, CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(result.FailureReason, Is.EqualTo(ConvaiActionFailureReason.InvalidState));
        }

        // ── Show Or Hide Object ───────────────────────────────────────────────────────────

        [Test]
        public async Task ShowOrHide_ShowMode_TurnsAHiddenObjectOn()
        {
            GameObject target = NewGameObject("Target");
            target.SetActive(false);
            ConvaiSetActiveActionExecutor behavior = NewBehavior<ConvaiSetActiveActionExecutor>();
            SetPrivateField(behavior, "_mode", ConvaiShowHideMode.Show);

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Show Or Hide Object", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(target.activeSelf, Is.True);
        }

        [Test]
        public async Task ShowOrHide_ToggleMode_FlipsWhateverStateTheObjectIsIn()
        {
            GameObject target = NewGameObject("Target");
            target.SetActive(true);
            ConvaiSetActiveActionExecutor behavior = NewBehavior<ConvaiSetActiveActionExecutor>();
            SetPrivateField(behavior, "_mode", ConvaiShowHideMode.Toggle);

            await behavior.ExecuteAsync(CreateInvocation("Show Or Hide Object", target), CancellationToken.None);
            Assert.That(target.activeSelf, Is.False);

            await behavior.ExecuteAsync(CreateInvocation("Show Or Hide Object", target), CancellationToken.None);
            Assert.That(target.activeSelf, Is.True);
        }

        [Test]
        public async Task ShowOrHide_AskedForTheStateItIsAlreadyIn_SucceedsAndSaysSo()
        {
            GameObject target = NewGameObject("Target");
            target.SetActive(true);
            ConvaiSetActiveActionExecutor behavior = NewBehavior<ConvaiSetActiveActionExecutor>();
            SetPrivateField(behavior, "_mode", ConvaiShowHideMode.Show);

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Show Or Hide Object", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded),
                "A request that is already satisfied is fulfilled, not failed.");
            Assert.That(result.Message, Does.Contain("Already"));
        }

        [TestCase("hide", false)]
        [TestCase("deactivate", false)]
        [TestCase("off", false)]
        [TestCase("show", true)]
        [TestCase("enable", true)]
        public async Task ShowOrHide_AcceptsTheEverydaySynonymsAModelActuallySends(string requested, bool expectedVisible)
        {
            GameObject target = NewGameObject("Target");
            target.SetActive(!expectedVisible);
            ConvaiSetActiveActionExecutor behavior = NewBehavior<ConvaiSetActiveActionExecutor>();
            SetPrivateField(behavior, "_mode", expectedVisible ? ConvaiShowHideMode.Hide : ConvaiShowHideMode.Show);

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocationWithMode(target, requested), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(target.activeSelf, Is.EqualTo(expectedVisible));
        }

        [Test]
        public async Task ShowOrHide_WithNoObjectToActOn_Declines()
        {
            ConvaiSetActiveActionExecutor behavior = NewBehavior<ConvaiSetActiveActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Show Or Hide Object", null), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
        }

        // ── Play Animator State ───────────────────────────────────────────────────────────

        [Test]
        public async Task PlayAnimatorState_WithNoAnimator_Declines()
        {
            ConvaiAnimatorStateActionExecutor behavior = NewBehavior<ConvaiAnimatorStateActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocationForAction("Wave"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(result.Message, Does.Contain(nameof(Animator)));
        }

        [Test]
        public async Task PlayAnimatorState_ForAnActionItHasNoRowFor_DeclinesSoAnotherBehaviorCanAnswer()
        {
            ConvaiAnimatorStateActionExecutor behavior = NewBehaviorOnAnimatedCharacter<ConvaiAnimatorStateActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocationForAction("Unmapped"), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(result.Message, Does.Contain("Unmapped"));
        }

        [Test]
        public async Task PlayAnimatorState_MatchesTheActionNameWhateverItsCapitalisation()
        {
            ConvaiAnimatorStateActionExecutor behavior = NewBehaviorOnAnimatedCharacter<ConvaiAnimatorStateActionExecutor>();
            SetBindings(behavior, "Wave", "WaveTrigger");

            // The Animator here has no controller, so Unity may complain about the trigger it is
            // asked to set. That complaint is the point of the manual scene pass, not of this test:
            // what is asserted is that a differently-capitalised action name still finds its row.
            LogAssert.ignoreFailingMessages = true;
            try
            {
                ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                    CreateInvocationForAction("wave"), CancellationToken.None);

                Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded),
                    "An action name is authored by a person and produced by a model; capitalisation cannot decide whether it works.");
            }
            finally
            {
                LogAssert.ignoreFailingMessages = false;
            }
        }

        // ── Play Sound ────────────────────────────────────────────────────────────────────

        [Test]
        public async Task PlaySound_WithNoAudioSourceAnywhere_DeclinesAndSaysWhatToAssign()
        {
            GameObject target = NewGameObject("Target");
            ConvaiPlaySoundActionExecutor behavior = NewBehavior<ConvaiPlaySoundActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Play Sound", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(result.Message, Does.Contain("Audio Source"));
        }

        [Test]
        public async Task PlaySound_WithAnAudioSourceButNoClip_FailsRatherThanPretendingItPlayed()
        {
            GameObject target = NewGameObject("Target");
            target.AddComponent<AudioSource>();
            ConvaiPlaySoundActionExecutor behavior = NewBehavior<ConvaiPlaySoundActionExecutor>();

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Play Sound", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Failed));
            Assert.That(result.FailureReason, Is.EqualTo(ConvaiActionFailureReason.InvalidState));
        }

        [Test]
        public async Task PlaySound_NeverBorrowsTheCharactersOwnAudioSource()
        {
            // The behavior sits on the character, which has an Audio Source — the one the character
            // speaks through. The action points at an object that has none. Reaching up the
            // hierarchy here would cut the character off mid-sentence, so it must decline instead.
            GameObject character = NewGameObject("Character");
            character.AddComponent<AudioSource>();
            ConvaiPlaySoundActionExecutor behavior = character.AddComponent<ConvaiPlaySoundActionExecutor>();
            GameObject target = NewGameObject("SilentTarget");

            ConvaiActionExecutionResult result = await behavior.ExecuteAsync(
                CreateInvocation("Play Sound", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
        }

        // ── Catalog metadata ──────────────────────────────────────────────────────────────

        [Test]
        public void EveryFlowBehavior_IsPickableFromTheCatalogUnderAPlainEnglishName()
        {
            AssertArchetype<ConvaiUnityEventActionExecutor>("Raise Unity Event", ConvaiActionTargetRequirement.None, null);
            AssertArchetype<ConvaiWaitActionExecutor>("Wait", ConvaiActionTargetRequirement.None, null);
            AssertArchetype<ConvaiSequenceActionExecutor>("Run In Order", ConvaiActionTargetRequirement.None, null);
            AssertArchetype<ConvaiSetActiveActionExecutor>("Show Or Hide Object", ConvaiActionTargetRequirement.Object, null);
            AssertArchetype<ConvaiAnimatorStateActionExecutor>("Play Animator State", ConvaiActionTargetRequirement.None, "Animator");
            AssertArchetype<ConvaiPlaySoundActionExecutor>("Play Sound", ConvaiActionTargetRequirement.Either, null);
        }

        [Test]
        public void TheEmptyStateHasStartersToOffer()
        {
            var featured = new List<string>();
            foreach (Type type in new[]
                     {
                         typeof(ConvaiUnityEventActionExecutor), typeof(ConvaiWaitActionExecutor),
                         typeof(ConvaiSequenceActionExecutor), typeof(ConvaiSetActiveActionExecutor),
                         typeof(ConvaiAnimatorStateActionExecutor), typeof(ConvaiPlaySoundActionExecutor)
                     })
            {
                var archetype = type.GetCustomAttribute<ConvaiActionArchetypeAttribute>();
                if (archetype is { FeaturedOrder: > 0 })
                    featured.Add(type.Name);
            }

            Assert.That(featured, Is.Not.Empty,
                "A character with no actions is shown ready-made starters drawn from FeaturedOrder. With none " +
                "declared, that hero row silently empties — which is how the Actions Editor stops teaching.");
        }

        // ── Shared fixture helpers ────────────────────────────────────────────────────────

        private const string NamePrefix = "FlowActionBehaviorTests_";

        private static GameObject NewGameObject(string suffix) => new($"{NamePrefix}{suffix}");

        private static T NewBehavior<T>() where T : Component => NewGameObject(typeof(T).Name).AddComponent<T>();

        private static T NewBehaviorOnAnimatedCharacter<T>() where T : Component
        {
            GameObject character = NewGameObject($"{typeof(T).Name}_Character");
            character.AddComponent<Animator>();
            return character.AddComponent<T>();
        }

        private static MonoBehaviour NewRecordingStep(string name, List<string> order, ConvaiActionExecutionResult result)
        {
            RecordingStep step = NewGameObject($"Step_{name}").AddComponent<RecordingStep>();
            step.Configure(name, order, result);
            return step;
        }

        private static void SetBindings(ConvaiAnimatorStateActionExecutor behavior, string actionName, string triggerName)
        {
            Type bindingType = typeof(ConvaiActionExecutorBase).Assembly
                .GetType("Convai.Runtime.Actions.ConvaiAnimatorActionBinding");
            Assert.That(bindingType, Is.Not.Null, "Expected the Animator binding row type to exist.");

            object binding = Activator.CreateInstance(bindingType);
            bindingType.GetField("ActionName").SetValue(binding, actionName);
            bindingType.GetField("TriggerName").SetValue(binding, triggerName);

            var list = (System.Collections.IList)Activator.CreateInstance(
                typeof(List<>).MakeGenericType(bindingType));
            list.Add(binding);

            SetPrivateField(behavior, "_bindings", list);
        }

        private static ConvaiActionInvocation CreateInvocation(string actionName, GameObject targetObject)
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

        private static ConvaiActionInvocation CreateInvocationWithMode(GameObject targetObject, string mode)
        {
            var command = new ConvaiActionCommand("Show Or Hide Object")
            {
                Parameters =
                {
                    ["mode"] = new ConvaiActionParameterValue
                    {
                        Type = ConvaiActionParameterType.String,
                        RawValue = mode,
                        StringValue = mode
                    }
                }
            };

            ConvaiResolvedActionTarget resolvedTarget = ConvaiResolvedActionTarget.FromObject(
                new ConvaiActionObjectDefinition { Name = targetObject.name, GameObjectReference = targetObject });

            return new ConvaiActionInvocation(command, null, resolvedTarget, null, 0, 0);
        }

        private static ConvaiActionInvocation CreateInvocationForAction(string actionName) =>
            new(new ConvaiActionCommand(actionName), new ConvaiActionDefinition { ActionName = actionName }, null, null, 0, 0);

        private static void AssertArchetype<T>(
            string expectedDisplayName,
            ConvaiActionTargetRequirement expectedRequirement,
            string requiredPeerHint)
        {
            var archetype = typeof(T).GetCustomAttribute<ConvaiActionArchetypeAttribute>();
            Assert.That(archetype, Is.Not.Null, $"{typeof(T).Name} must carry [ConvaiActionArchetype].");
            Assert.That(archetype.DisplayName, Is.EqualTo(expectedDisplayName));
            Assert.That(archetype.TargetRequirement, Is.EqualTo(expectedRequirement));
            Assert.That(archetype.RequiredPeerHint, Is.EqualTo(requiredPeerHint));
            Assert.That(archetype.Description, Is.Not.Null.And.Not.Empty,
                "The description is what the catalog card shows; without it the entry is a bare name.");

            var menu = typeof(T).GetCustomAttribute<AddComponentMenu>();
            Assert.That(menu, Is.Not.Null, $"{typeof(T).Name} must be findable under Add Component.");
            StringAssert.StartsWith("Convai/Actions/", menu.componentMenu);
        }

        /// <summary>
        ///     Writes a private field, walking up the hierarchy so fields declared on a shared base —
        ///     such as the character component on <see cref="ConvaiCharacterActionExecutor{TPeer}" /> —
        ///     are reachable too.
        /// </summary>
        private static void SetPrivateField(object instance, string fieldName, object value) =>
            FindField(instance.GetType(), fieldName).SetValue(instance, value);

        private static T GetPrivateField<T>(object instance, string fieldName) =>
            (T)FindField(instance.GetType(), fieldName).GetValue(instance);

        private static void InvokePrivate(object instance, string methodName)
        {
            MethodInfo method = instance.GetType().GetMethod(
                methodName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            Assert.That(method, Is.Not.Null, $"Expected {instance.GetType().Name}.{methodName} to exist.");
            method.Invoke(instance, null);
        }

        private static FieldInfo FindField(Type type, string fieldName)
        {
            for (Type current = type; current != null; current = current.BaseType)
            {
                FieldInfo field = current.GetField(
                    fieldName, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                if (field != null)
                    return field;
            }

            throw new MissingFieldException(type.FullName, fieldName);
        }

        private sealed class RecordingStep : MonoBehaviour, IConvaiActionExecutor
        {
            private string _name;
            private List<string> _order;
            private ConvaiActionExecutionResult _result;

            public void Configure(string name, List<string> order, ConvaiActionExecutionResult result)
            {
                _name = name;
                _order = order;
                _result = result;
            }

            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, CancellationToken cancellationToken)
            {
                _order.Add(_name);
                return Task.FromResult(_result);
            }
        }

        private sealed class PlainBehavior : MonoBehaviour
        {
        }
    }
}
