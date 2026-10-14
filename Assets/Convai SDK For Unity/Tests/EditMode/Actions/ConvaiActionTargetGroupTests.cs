using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionTargetGroup" />'s ordered
    ///     membership (with alloc-free null cleanup) and its integration with the existing target
    ///     resolution ladder (mirrors <c>ConvaiActionTargetRegistryTests</c>'s
    ///     <see cref="ConvaiActionTarget" /> component-lifecycle conventions).
    /// </summary>
    [TestFixture]
    public sealed class ConvaiActionTargetGroupTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        // ── Ordered membership / null cleanup ────────────────────────────────────────────

        [Test]
        public void Members_ReturnsInAuthoredOrder()
        {
            GameObject groupGo = new("TargetGroupTests_Group");
            GameObject memberA = new("TargetGroupTests_MemberA");
            GameObject memberB = new("TargetGroupTests_MemberB");
            GameObject memberC = new("TargetGroupTests_MemberC");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();
                ConvaiActionTarget targetA = memberA.AddComponent<ConvaiActionTarget>();
                ConvaiActionTarget targetB = memberB.AddComponent<ConvaiActionTarget>();
                ConvaiActionTarget targetC = memberC.AddComponent<ConvaiActionTarget>();

                SetPrivateField(group, "_members", new List<ConvaiActionTarget> { targetA, targetB, targetC });

                IReadOnlyList<ConvaiActionTarget> members = group.Members;

                Assert.That(members.Count, Is.EqualTo(3));
                Assert.That(members[0], Is.SameAs(targetA));
                Assert.That(members[1], Is.SameAs(targetB));
                Assert.That(members[2], Is.SameAs(targetC));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
                UnityEngine.Object.DestroyImmediate(memberA);
                UnityEngine.Object.DestroyImmediate(memberB);
                UnityEngine.Object.DestroyImmediate(memberC);
            }
        }

        [Test]
        public void Members_DestroyedEntry_IsClearedOnNextAccess()
        {
            GameObject groupGo = new("TargetGroupTests_GroupCleanup");
            GameObject memberA = new("TargetGroupTests_CleanupMemberA");
            GameObject memberB = new("TargetGroupTests_CleanupMemberB");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();
                ConvaiActionTarget targetA = memberA.AddComponent<ConvaiActionTarget>();
                ConvaiActionTarget targetB = memberB.AddComponent<ConvaiActionTarget>();

                SetPrivateField(group, "_members", new List<ConvaiActionTarget> { targetA, targetB });

                Assert.That(group.Members.Count, Is.EqualTo(2));

                UnityEngine.Object.DestroyImmediate(memberA);
                memberA = null;

                IReadOnlyList<ConvaiActionTarget> membersAfterDestroy = group.Members;
                Assert.That(membersAfterDestroy.Count, Is.EqualTo(1));
                Assert.That(membersAfterDestroy[0], Is.SameAs(targetB));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
                if (memberA != null) UnityEngine.Object.DestroyImmediate(memberA);
                UnityEngine.Object.DestroyImmediate(memberB);
            }
        }

        [Test]
        public void Members_EmptyAuthoredList_ReturnsEmpty()
        {
            GameObject groupGo = new("TargetGroupTests_EmptyGroup");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();

                Assert.That(group.Members, Is.Empty);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
            }
        }

        [Test]
        public void IsOrdered_DefaultsToTrue()
        {
            GameObject groupGo = new("TargetGroupTests_DefaultOrder");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();
                Assert.IsTrue(group.IsOrdered);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
            }
        }

        // ── Resolution ladder integration ────────────────────────────────────────────────

        [Test]
        public void EnabledGroup_ResolvesByName_ThroughMergedConfig()
        {
            (GameObject characterGo, ConvaiCharacter character) = CreateCharacterWithOneAction();
            GameObject groupGo = new("TargetGroupTests_Paintings");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();
                SetPrivateField(group, "_groupName", "the paintings");
                SetPrivateField(group, "_description", "A row of four paintings.");
                group.HandleEnable();

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                ConvaiActionObjectDefinition resolved = FindObject(merged, "the paintings");

                Assert.That(resolved, Is.Not.Null);
                Assert.That(resolved.GameObjectReference, Is.SameAs(groupGo));
                Assert.That(resolved.Description, Is.EqualTo("A row of four paintings."));

                ConvaiResolvedActionTarget ladderResolved = ConvaiResolvedActionTarget.Resolve(
                    "the paintings", merged, (ConvaiActionTargetRequirement?)ConvaiActionTargetRequirement.Object);
                Assert.That(ladderResolved, Is.Not.Null);
                Assert.That(ladderResolved.GameObjectReference, Is.SameAs(groupGo));
                Assert.That(ladderResolved.GameObjectReference.GetComponent<ConvaiActionTargetGroup>(), Is.SameAs(group));

                group.HandleDisable();
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
                UnityEngine.Object.DestroyImmediate(characterGo);
            }
        }

        [Test]
        public void DisabledGroup_DoesNotResolve()
        {
            (GameObject characterGo, ConvaiCharacter character) = CreateCharacterWithOneAction();
            GameObject groupGo = new("TargetGroupTests_Disabled");
            try
            {
                ConvaiActionTargetGroup group = groupGo.AddComponent<ConvaiActionTargetGroup>();
                SetPrivateField(group, "_groupName", "the exhibits");
                group.HandleEnable();
                group.HandleDisable();

                ConvaiActionConfig merged = character.GetRuntimeActionConfig();
                Assert.That(FindObject(merged, "the exhibits"), Is.Null);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(groupGo);
                UnityEngine.Object.DestroyImmediate(characterGo);
            }
        }

        // ── Fixture helpers ───────────────────────────────────────────────────────────────

        private sealed class NoOpActionExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }

        private static (GameObject GameObject, ConvaiCharacter Character) CreateCharacterWithOneAction()
        {
            GameObject go = new($"TargetGroupTests_Character_{Guid.NewGuid():N}");
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

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            FieldInfo field = instance.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            field.SetValue(instance, value);
        }
    }
}
