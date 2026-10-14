using System.Linq;
using System.Reflection;
using Convai.Editor.Actions;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Executors;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Convai.Tests.EditMode.Editor
{
    [TestFixture]
    public class ConvaiActionsEditorArchetypeProvisioningTests
    {
        [TearDown]
        public void TearDown()
        {
            Undo.ClearAll();
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name == "ConvaiActionsEditorArchetypeProvisioningTests_Character")
                    Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void AddArchetypeAction_AddsExecutorPeerAndTransitiveUnityRequirement()
        {
            var character = new GameObject("ConvaiActionsEditorArchetypeProvisioningTests_Character");
            ConvaiActionConfigSource source = character.AddComponent<ConvaiActionConfigSource>();
            ConvaiActionArchetypeCatalogEntry entry = ConvaiActionArchetypeCatalog.Entries.Single(
                candidate => candidate.ExecutorType == typeof(ConvaiLeadPlayerActionExecutor));

            MethodInfo addMethod = typeof(ConvaiActionsEditorWindow).GetMethod(
                "AddArchetypeAction",
                BindingFlags.Static | BindingFlags.NonPublic);

            Assert.That(addMethod, Is.Not.Null);
            var definition = addMethod.Invoke(null, new object[] { source, entry }) as ConvaiActionDefinition;

            Assert.That(definition, Is.Not.Null);
            Assert.That(definition.Executor, Is.TypeOf<ConvaiLeadPlayerActionExecutor>());
            Assert.That(definition.ExecutorTypeHint, Is.EqualTo(nameof(ConvaiLeadPlayerActionExecutor)));
            Assert.That(character.GetComponent<ConvaiNavMeshLocomotion>(), Is.Not.Null);
            Assert.That(character.GetComponent<NavMeshAgent>(), Is.Not.Null);
            Assert.That(source.Definitions, Has.Count.EqualTo(1));
            Assert.That(source.Definitions[0], Is.SameAs(definition));
        }
    }
}
