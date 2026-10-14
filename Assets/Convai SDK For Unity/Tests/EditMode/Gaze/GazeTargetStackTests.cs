using Convai.Modules.Gaze.Core.Targeting;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeTargetStackTests
    {
        private GazeTargetStack _stack;

        [SetUp]
        public void SetUp() => _stack = new GazeTargetStack();

        [Test]
        public void ResolveActive_EmptyStack_ReturnsNull()
        {
            Assert.IsNull(_stack.ResolveActive(0f));
        }

        [Test]
        public void HigherPriority_Wins()
        {
            _stack.Push(null, Vector3.zero, false, 0, -1f, false, float.PositiveInfinity, "low");
            _stack.Push(null, Vector3.one, false, 5, -1f, false, float.PositiveInfinity, "high");

            GazeTargetStack.Entry active = _stack.ResolveActive(0f);

            Assert.That(active.Name, Is.EqualTo("high"));
        }

        [Test]
        public void EqualPriority_RecencyWins()
        {
            _stack.Push(null, Vector3.zero, false, 1, -1f, false, float.PositiveInfinity, "first");
            _stack.Push(null, Vector3.one, false, 1, -1f, false, float.PositiveInfinity, "second");

            GazeTargetStack.Entry active = _stack.ResolveActive(0f);

            Assert.That(active.Name, Is.EqualTo("second"));
        }

        [Test]
        public void ExpiredEntries_ArePruned()
        {
            _stack.Push(null, Vector3.zero, false, 1, -1f, false, deadline: 2f, "timed");

            Assert.IsNotNull(_stack.ResolveActive(1f));
            Assert.IsNull(_stack.ResolveActive(2.5f), "Entry past its deadline must expire.");
            Assert.That(_stack.Count, Is.EqualTo(0), "Expired entries are removed from the stack.");
        }

        [Test]
        public void Remove_RestoresPreviousEntry()
        {
            int lowId = _stack.Push(null, Vector3.zero, false, 1, -1f, false, float.PositiveInfinity, "low");
            int highId = _stack.Push(null, Vector3.one, false, 5, -1f, false, float.PositiveInfinity, "high");

            Assert.IsTrue(_stack.Remove(highId));
            GazeTargetStack.Entry active = _stack.ResolveActive(0f);

            Assert.That(active.Name, Is.EqualTo("low"));
            Assert.IsTrue(_stack.Contains(lowId));
            Assert.IsFalse(_stack.Remove(highId), "Double release must be a safe no-op.");
        }

        [Test]
        public void RemoveBelowPriority_DropsOnlyGlanceTierEntries()
        {
            int explicitId = _stack.Push(null, Vector3.zero, false, 0, 1f, false, float.PositiveInfinity, "explicit");
            int glanceId = _stack.Push(null, Vector3.one, false, -5, 1f, false, float.PositiveInfinity, "glance");
            int curiosityId = _stack.Push(null, Vector3.one, false, -100, 0.5f, false, float.PositiveInfinity, "curiosity");

            var removed = new System.Collections.Generic.List<int>();
            int count = _stack.RemoveBelowPriority(0, removed);

            Assert.That(count, Is.EqualTo(2));
            CollectionAssert.AreEquivalent(new[] { glanceId, curiosityId }, removed);
            Assert.IsTrue(_stack.Contains(explicitId), "Explicit requests (priority >= 0) must survive.");
            Assert.IsFalse(_stack.Contains(glanceId));
            Assert.IsFalse(_stack.Contains(curiosityId));

            Assert.That(_stack.RemoveBelowPriority(0, removed), Is.EqualTo(0),
                "A second sweep with nothing below the floor removes nothing.");
        }

        [Test]
        public void DestroyedTransform_ExpiresEntry()
        {
            var go = new GameObject("GazeStackTarget");
            try
            {
                _stack.Push(go.transform, Vector3.zero, true, 1, -1f, false, float.PositiveInfinity, "obj");
                Object.DestroyImmediate(go);

                Assert.IsNull(_stack.ResolveActive(0f),
                    "Entries whose transform died must not keep gaze pinned at origin.");
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void TransformEntry_FollowsTargetPosition()
        {
            var go = new GameObject("GazeStackFollow");
            try
            {
                go.transform.position = new Vector3(1f, 2f, 3f);
                _stack.Push(go.transform, Vector3.zero, true, 1, -1f, false, float.PositiveInfinity, "obj");

                GazeTargetStack.Entry active = _stack.ResolveActive(0f);
                Assert.That(active.ResolvePoint(), Is.EqualTo(new Vector3(1f, 2f, 3f)));

                go.transform.position = new Vector3(-4f, 0f, 1f);
                Assert.That(active.ResolvePoint(), Is.EqualTo(new Vector3(-4f, 0f, 1f)));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
