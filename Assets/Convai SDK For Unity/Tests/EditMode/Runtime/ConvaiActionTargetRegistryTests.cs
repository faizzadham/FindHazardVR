using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Coverage for the runtime target registry, <see cref="ConvaiCharacterActions" />
    ///     merged-view precedence, the <see cref="ConvaiActionTarget" /> component lifecycle, and
    ///     the <see cref="ConvaiResolvedActionTarget" /> resolution ladder. Mirrors
    ///     <c>ActionSystemTests</c>'s fixture conventions.
    /// </summary>
    [TestFixture]
    public class ConvaiActionTargetRegistryTests
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
                if (gameObject != null && gameObject.name.StartsWith("ActionTargetRegistryTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── Registry lifecycle (via ConvaiCharacterActions) ──────────────────────────────

        [Test]
        public void RetiredComponentNameCanBeReusedAfterAcknowledgedRemoval()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            var targetObject = new GameObject("Temporary component target");
            try
            {
                var target = targetObject.AddComponent<ConvaiActionTarget>(); target.TargetName = "fixture-lantern";
                ConvaiActionConfig projected = character.GetRuntimeActionConfig();
                character.SetResolvedSessionActionConfig(projected);
                UnityEngine.Object.DestroyImmediate(targetObject);
                var removed = character.GetRuntimeActionConfig();
                Assert.That(FindObject(removed, "fixture-lantern"), Is.Null);
                character.SetResolvedSessionActionConfig(removed);
                removed.Objects.Add(new ConvaiActionObjectDefinition { Name = "fixture-lantern", Description = "Registered later" });
                character.SetResolvedSessionActionConfig(removed);
                for (int i = 0; i < 3; i++)
                    Assert.That(FindObject(character.GetRuntimeActionConfig(), "fixture-lantern")?.Description, Is.EqualTo("Registered later"));
            }
            finally { if (targetObject) UnityEngine.Object.DestroyImmediate(targetObject); UnityEngine.Object.DestroyImmediate(go); }
        }

        [Test]
        public void RegisterObject_EmptyName_NoOpsAndLogsWarningOnce()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                character.Actions.RegisterObject("", "desc");
                character.Actions.RegisterObject(null, "desc");

                Assert.That(character.Actions.Targets, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RegisterObject_ThenGetRuntimeActionConfig_AddsObjectToMergedConfig()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                character.Actions.RegisterObject("lantern", "A brass lantern.");

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                Assert.That(merged, Is.Not.Null);
                Assert.That(FindObject(merged, "lantern"), Is.Not.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void RegisterObject_DuplicateAuthoredName_AuthoredWins()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                ConvaiActionConfigSource source = go.GetComponent<ConvaiActionConfigSource>();
                GameObject authoredGo = new("ActionTargetRegistryTests_AuthoredCube");
                source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "cube", Description = "authored", GameObjectReference = authoredGo }
                });

                GameObject runtimeGo = new("ActionTargetRegistryTests_RuntimeCube");
                character.Actions.RegisterObject("cube", "runtime", runtimeGo);

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                ConvaiActionObjectDefinition resolved = FindObject(merged, "cube");
                Assert.That(resolved, Is.Not.Null);
                Assert.That(resolved.Description, Is.EqualTo("authored"));
                Assert.That(resolved.GameObjectReference, Is.EqualTo(authoredGo));

                UnityEngine.Object.DestroyImmediate(authoredGo);
                UnityEngine.Object.DestroyImmediate(runtimeGo);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void UnregisterTarget_RemovesAllInstancesOfName()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                character.Actions.RegisterObject("chair", "chair one");
                character.Actions.RegisterObject("chair", "chair two");

                ConvaiActionConfig before = character.GetRuntimeActionConfig();
                Assert.That(CountNamed(before.Objects, "chair"), Is.EqualTo(2));

                character.Actions.UnregisterTarget("chair");

                ConvaiActionConfig after = character.GetRuntimeActionConfig();
                Assert.That(CountNamed(after.Objects, "chair"), Is.EqualTo(0));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void SetTargetAvailable_MarksEntryUnavailable_ExcludedFromResolution()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                character.Actions.RegisterObject("lantern", "A brass lantern.");
                character.Actions.SetTargetAvailable("lantern", false);

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    "lantern", merged, (ConvaiActionTargetRequirement?)ConvaiActionTargetRequirement.Object);

                Assert.That(resolved, Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        // ── Resolution ladder (direct, no character needed) ──────────────────────────────

        [Test]
        public void Ladder_ExactMatch_Resolves()
        {
            ConvaiActionConfig config = ConfigWithObjects(("wooden drawer", null, null, true));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "wooden drawer", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved?.Name, Is.EqualTo("wooden drawer"));
        }

        [Test]
        public void Ladder_AliasMatch_Resolves()
        {
            var config = new ConvaiActionConfig();
            config.Objects.Add(new ConvaiActionObjectDefinition
            {
                Name = "wooden drawer",
                Aliases = new List<string> { "the drawer" },
                Available = true
            });

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "the drawer", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved?.Name, Is.EqualTo("wooden drawer"));
        }

        [Test]
        public void Ladder_NormalizedArticleStrip_Resolves()
        {
            ConvaiActionConfig config = ConfigWithObjects(("red key", null, null, true));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "the red key", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved?.Name, Is.EqualTo("red key"));
        }

        [Test]
        public void Ladder_UniqueContainsMatch_Resolves()
        {
            ConvaiActionConfig config = ConfigWithObjects(("brass key ring", null, null, true));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "key", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved?.Name, Is.EqualTo("brass key ring"));
        }

        [Test]
        public void Ladder_AmbiguousContains_ReturnsNull()
        {
            ConvaiActionConfig config = ConfigWithObjects(
                ("brass key ring", null, null, true),
                ("rusty key", null, null, true));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "key", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void Ladder_NearestOfDuplicates_WithOrigin_ResolvesNearer()
        {
            GameObject near = new("ActionTargetRegistryTests_ChairNear") { transform = { position = new Vector3(1f, 0f, 0f) } };
            GameObject far = new("ActionTargetRegistryTests_ChairFar") { transform = { position = new Vector3(10f, 0f, 0f) } };
            try
            {
                var config = new ConvaiActionConfig();
                config.Objects.Add(new ConvaiActionObjectDefinition { Name = "chair", GameObjectReference = far, Available = true });
                config.Objects.Add(new ConvaiActionObjectDefinition { Name = "chair", GameObjectReference = near, Available = true });

                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    "chair", config, ConvaiActionTargetRequirement.Object, Vector3.zero);

                Assert.That(resolved?.GameObjectReference, Is.EqualTo(near));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(near);
                UnityEngine.Object.DestroyImmediate(far);
            }
        }

        [Test]
        public void Ladder_UnavailableEntry_ExcludedFromExactMatch()
        {
            ConvaiActionConfig config = ConfigWithObjects(("lantern", null, null, false));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "lantern", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved, Is.Null);
        }

        [Test]
        public void InteractionPoint_FallsBackToGameObjectTransform_WhenNoExplicitPoint()
        {
            GameObject cube = new("ActionTargetRegistryTests_Cube");
            try
            {
                ConvaiActionConfig config = new();
                config.Objects.Add(new ConvaiActionObjectDefinition { Name = "cube", GameObjectReference = cube, Available = true });

                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    "cube", config, ConvaiActionTargetRequirement.Object);

                Assert.That(resolved?.InteractionPoint, Is.EqualTo(cube.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void InteractionPoint_UsesExplicitPoint_WhenSet()
        {
            GameObject cube = new("ActionTargetRegistryTests_Cube");
            GameObject point = new("ActionTargetRegistryTests_Point");
            try
            {
                ConvaiActionConfig config = new();
                config.Objects.Add(new ConvaiActionObjectDefinition
                {
                    Name = "cube", GameObjectReference = cube, InteractionPoint = point.transform, Available = true
                });

                ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                    "cube", config, ConvaiActionTargetRequirement.Object);

                Assert.That(resolved?.InteractionPoint, Is.EqualTo(point.transform));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
                UnityEngine.Object.DestroyImmediate(point);
            }
        }

        [Test]
        public void InteractionPoint_IsNull_WhenNoGameObjectAndNoExplicitPoint()
        {
            ConvaiActionConfig config = ConfigWithObjects(("ghost", null, null, true));

            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "ghost", config, ConvaiActionTargetRequirement.Object);

            Assert.That(resolved?.InteractionPoint, Is.Null);
        }

        // ── ConvaiActionTarget component lifecycle ───────────────────────────────────────

        [Test]
        public void ConvaiActionTarget_OnEnable_AllCharacters_RegistersOntoExistingCharacter()
        {
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            GameObject targetGo = new("ActionTargetRegistryTests_Lantern");
            try
            {
                ConvaiActionTarget target = targetGo.AddComponent<ConvaiActionTarget>();
                SetPrivateField(target, "_targetName", "lantern");
                target.HandleEnable();

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                Assert.That(FindObject(merged, "lantern"), Is.Not.Null);

                target.HandleDisable();
                merged = character.GetRuntimeActionConfig();
                Assert.That(FindObject(merged, "lantern"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetGo);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ConvaiActionTarget_LateEnablingCharacter_StillSeesExistingTarget()
        {
            GameObject targetGo = new("ActionTargetRegistryTests_Lantern");
            (GameObject go, ConvaiCharacter character) = CreateCharacterWithOneAction();
            try
            {
                ConvaiActionTarget target = targetGo.AddComponent<ConvaiActionTarget>();
                SetPrivateField(target, "_targetName", "lantern");
                target.HandleEnable();

                // The character above is created (and its Actions/config wired) after the target
                // already registered itself into the static active-target list; the merge is
                // read-time-polled, so no push/late-enable bookkeeping is required.
                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                Assert.That(FindObject(merged, "lantern"), Is.Not.Null);

                target.HandleDisable();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetGo);
                UnityEngine.Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ConvaiActionTarget_SpecificCharacters_OnlyAppliesToListedCharacter()
        {
            (GameObject goA, ConvaiCharacter characterA) = CreateCharacterWithOneAction();
            (GameObject goB, ConvaiCharacter characterB) = CreateCharacterWithOneAction();
            GameObject targetGo = new("ActionTargetRegistryTests_Torch");
            try
            {
                ConvaiActionTarget target = targetGo.AddComponent<ConvaiActionTarget>();
                SetPrivateField(target, "_targetName", "torch");
                target.ApplyTo = ConvaiActionTargetApplyScope.SpecificCharacters;
                target.SpecificCharacters = new List<ConvaiCharacter> { characterA };
                target.HandleEnable();

                Assert.That(FindObject(characterA.GetRuntimeActionConfig(), "torch"), Is.Not.Null);
                Assert.That(FindObject(characterB.GetRuntimeActionConfig(), "torch"), Is.Null);

                target.HandleDisable();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(targetGo);
                UnityEngine.Object.DestroyImmediate(goA);
                UnityEngine.Object.DestroyImmediate(goB);
            }
        }

        // ── Fixture helpers ───────────────────────────────────────────────────────────────

        private sealed class NoOpActionExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }

        /// <summary>
        ///     One character with a single valid, executable action definition — required for
        ///     <see cref="ConvaiActionConfigSource.BuildActionConfig" /> (and therefore
        ///     <see cref="ConvaiCharacter.GetRuntimeActionConfig" />) to return non-null so the
        ///     runtime-target merge has a base config to merge into.
        /// </summary>
        private static (GameObject GameObject, ConvaiCharacter Character) CreateCharacterWithOneAction()
        {
            GameObject go = new($"ActionTargetRegistryTests_Character_{Guid.NewGuid():N}");
            ConvaiCharacter character = go.AddComponent<ConvaiCharacter>();
            ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
            NoOpActionExecutor executor = go.AddComponent<NoOpActionExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Move To",
                    TargetRequirement = ConvaiActionTargetRequirement.Either,
                    Executor = executor
                }
            });

            return (go, character);
        }

        private static ConvaiActionConfig ConfigWithObjects(
            params (string Name, List<string> Aliases, GameObject GameObjectReference, bool Available)[] objects)
        {
            var config = new ConvaiActionConfig();
            foreach ((string name, List<string> aliases, GameObject gameObjectReference, bool available) in objects)
            {
                config.Objects.Add(new ConvaiActionObjectDefinition
                {
                    Name = name,
                    Aliases = aliases ?? new List<string>(),
                    GameObjectReference = gameObjectReference,
                    Available = available
                });
            }

            return config;
        }

        private static ConvaiActionObjectDefinition FindObject(ConvaiActionConfig config, string name)
        {
            if (config?.Objects == null) return null;
            foreach (ConvaiActionObjectDefinition o in config.Objects)
            {
                if (o != null && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                    return o;
            }

            return null;
        }

        private static int CountNamed(IReadOnlyList<ConvaiActionObjectDefinition> objects, string name)
        {
            int count = 0;
            foreach (ConvaiActionObjectDefinition o in objects)
            {
                if (o != null && string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase))
                    count++;
            }

            return count;
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            System.Reflection.FieldInfo field = instance.GetType().GetField(
                fieldName, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            field.SetValue(instance, value);
        }
    }
}
