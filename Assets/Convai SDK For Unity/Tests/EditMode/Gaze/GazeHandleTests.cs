using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeHandleTests
    {
        private GameObject _root;
        private ConvaiGazeController _controller;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("GazeHandleTestCharacter");
            _root.AddComponent<ConvaiCharacter>();
            _controller = _root.AddComponent<ConvaiGazeController>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        private static GazeTargetDecision ScriptedDecision(int entryId, float commitment = 1f) => new()
        {
            Kind = GazeTargetKind.Scripted,
            Commitment = commitment,
            IsScripted = true,
            ScriptedEntryId = entryId,
            Name = "prop",
            ScriptedEngagementOverride = 1f
        };

        [Test]
        public void GazeAt_ReturnsActiveHandle_AndPushesStackEntry()
        {
            GazeHandle handle = _controller.GazeAt(new Vector3(1f, 1f, 1f));

            Assert.IsNotNull(handle);
            Assert.IsTrue(handle.IsActive);
            Assert.IsFalse(handle.Settled.IsCompleted);
            Assert.IsFalse(handle.Completion.IsCompleted);
            Assert.That(_controller.ScriptedStack.Count, Is.EqualTo(1));
        }

        [Test]
        public void GazeAt_NullTransform_ReturnsNull()
        {
            Assert.IsNull(_controller.GazeAt((Transform)null));
        }

        [Test]
        public void Release_CompletesTasks_AndRemovesEntry()
        {
            GazeHandle handle = _controller.GazeAt(new Vector3(1f, 1f, 1f));

            handle.Release();

            Assert.IsFalse(handle.IsActive);
            Assert.IsTrue(handle.Completion.IsCompleted);
            Assert.IsTrue(handle.Settled.IsCompleted);
            Assert.IsFalse(handle.Settled.Result, "Released before alignment → Settled is false.");
            Assert.That(_controller.ScriptedStack.Count, Is.EqualTo(0));

            Assert.DoesNotThrow(() => handle.Release(), "Double release must be a safe no-op.");
        }

        [Test]
        public void PostExpressionSettlement_RequiresThreeStableAlignedFrames()
        {
            GazeHandle handle = _controller.GazeAt(new Vector3(0f, 1.6f, 2f));
            GazeTargetStack.Entry entry = _controller.ScriptedStack.ResolveActive(0f);

            _controller.ProcessScriptedHandles(ScriptedDecision(entry.Id));
            _controller.ProcessScriptedSettlement(1.5f);
            _controller.ProcessScriptedSettlement(1.5f);
            Assert.IsFalse(handle.Settled.IsCompleted);
            _controller.ProcessScriptedSettlement(1.5f);

            Assert.IsTrue(handle.Settled.IsCompleted);
            Assert.IsTrue(handle.Settled.Result, "Aligned gaze must settle with true.");
            Assert.IsFalse(handle.Completion.IsCompleted, "Settling does not end the hold.");
        }

        [Test]
        public void ProcessScriptedHandles_DoesNotSettleWhileMisaligned()
        {
            GazeHandle handle = _controller.GazeAt(new Vector3(0f, 1.6f, 2f));
            GazeTargetStack.Entry entry = _controller.ScriptedStack.ResolveActive(0f);

            _controller.ProcessScriptedHandles(ScriptedDecision(entry.Id));
            for (int i = 0; i < 4; i++) _controller.ProcessScriptedSettlement(20f);
            Assert.IsFalse(handle.Settled.IsCompleted, "40° off is not settled.");

            _controller.ProcessScriptedHandles(ScriptedDecision(entry.Id, commitment: 0.3f));
            for (int i = 0; i < 4; i++) _controller.ProcessScriptedSettlement(0.5f);
            Assert.IsFalse(handle.Settled.IsCompleted, "Low commitment is not settled.");
        }

        [Test]
        public void ProcessScriptedHandles_CompletesExpiredEntries()
        {
            GazeHandle handle = _controller.GazeAt(new Vector3(1f, 1f, 1f));
            _controller.ScriptedStack.Remove(handle.EntryId); // simulate hold expiry

            _controller.ProcessScriptedHandles(GazeTargetDecision.None);

            Assert.IsTrue(handle.Completion.IsCompleted);
            Assert.IsFalse(handle.IsActive);
        }

        [Test]
        public void ReleaseAllScriptedGaze_CompletesEveryHandle()
        {
            GazeHandle a = _controller.GazeAt(new Vector3(1f, 1f, 1f));
            GazeHandle b = _controller.GazeAt(new Vector3(-1f, 1f, 1f));

            _controller.ReleaseAllScriptedGaze();

            Assert.IsTrue(a.Completion.IsCompleted);
            Assert.IsTrue(b.Completion.IsCompleted);
            Assert.That(_controller.ScriptedStack.Count, Is.EqualTo(0));
        }

        [Test]
        public void HigherPriorityRequest_WinsTheStack()
        {
            _controller.GazeAt(new Vector3(1f, 1f, 1f), new GazeOptions { Priority = 1 });
            GazeHandle important = _controller.GazeAt(new Vector3(0f, 2f, 0f), new GazeOptions { Priority = 9 });

            GazeTargetStack.Entry active = _controller.ScriptedStack.ResolveActive(0f);

            Assert.That(active.Id, Is.EqualTo(important.EntryId));
        }
    }
}
