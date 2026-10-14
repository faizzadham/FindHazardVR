using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    public sealed class ConvaiObservationActionExecutorTests
    {
        private readonly List<GameObject> _objects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _objects.Count - 1; i >= 0; i--)
                if (_objects[i] != null)
                    Object.DestroyImmediate(_objects[i]);
            _objects.Clear();
        }

        [Test]
        public async Task CountTargetGroup_ReturnsAvailableCountAndNames()
        {
            GameObject executorObject = NewObject("Counter");
            GameObject groupObject = NewObject("Crates");
            GameObject firstObject = NewObject("Crate A");
            GameObject secondObject = NewObject("Crate B");

            var executor = executorObject.AddComponent<ConvaiCountTargetGroupActionExecutor>();
            var group = groupObject.AddComponent<ConvaiActionTargetGroup>();
            var first = firstObject.AddComponent<ConvaiActionTarget>();
            var second = secondObject.AddComponent<ConvaiActionTarget>();
            secondObject.SetActive(false);
            SetPrivateField(group, "_members", new List<ConvaiActionTarget> { first, second });

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(
                InvocationWithTarget("Count Target Group", groupObject), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(result.HasAnswer, Is.True);
            Assert.That(result.Answer, Does.Contain("1 of 2"));
            Assert.That(result.Answer, Does.Contain("Crate A"));
            Assert.That(result.Answer, Does.Not.Contain("Crate B"));
        }

        [Test]
        public async Task CountTargetGroup_EmptyGroup_DeclinesInsteadOfReportingZero()
        {
            var executor = NewObject("Counter").AddComponent<ConvaiCountTargetGroupActionExecutor>();
            GameObject groupObject = NewObject("Empty Group");
            groupObject.AddComponent<ConvaiActionTargetGroup>();

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(
                InvocationWithTarget("Count Target Group", groupObject), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Unhandled));
            Assert.That(result.Message, Does.Contain("no members"));
        }

        [Test]
        public async Task MeasureDistance_UsesGroundPlaneAndReturnsAnswer()
        {
            GameObject character = NewObject("Character");
            GameObject target = NewObject("Console");
            target.transform.position = new Vector3(3f, 20f, 4f);
            var executor = character.AddComponent<ConvaiMeasureDistanceActionExecutor>();

            ConvaiActionExecutionResult result = await executor.ExecuteAsync(
                InvocationWithTarget("Measure Distance", target), CancellationToken.None);

            Assert.That(result.Status, Is.EqualTo(ConvaiActionExecutionStatus.Succeeded));
            Assert.That(result.Answer, Does.Contain("5.0 metres"));
        }

        [Test]
        public void NewObservationExecutors_ExposeProductionArchetypes()
        {
            AssertArchetype<ConvaiCountTargetGroupActionExecutor>("Count Target Group", "Observation");
            AssertArchetype<ConvaiMeasureDistanceActionExecutor>("Measure Distance", "Observation");
        }

        private GameObject NewObject(string name)
        {
            var created = new GameObject($"ObservationExecutorTests_{name}");
            _objects.Add(created);
            return created;
        }

        private static ConvaiActionInvocation InvocationWithTarget(string actionName, GameObject target)
        {
            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.FromObject(new ConvaiActionObjectDefinition
            {
                Name = target.name,
                GameObjectReference = target
            });
            return new ConvaiActionInvocation(new ConvaiActionCommand(actionName), null, resolved, null, 0, 0);
        }

        private static void SetPrivateField<T>(object target, string fieldName, T value) =>
            target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(target, value);

        private static void AssertArchetype<T>(string displayName, string family)
        {
            var attribute = typeof(T).GetCustomAttribute<ConvaiActionArchetypeAttribute>();
            Assert.That(attribute, Is.Not.Null);
            Assert.That(attribute.DisplayName, Is.EqualTo(displayName));
            Assert.That(attribute.Family, Is.EqualTo(family));
            Assert.That(attribute.DisplayName, Does.Not.Contain("Lab"));
        }
    }
}
