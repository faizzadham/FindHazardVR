using Convai.Modules.Gaze.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Pins the crowd-LOD off-screen check against the stale-cache failure T1 fixed: a mesh
    ///     swap destroys every cached renderer, and the old code read that as "off-screen forever",
    ///     which throttled cognition to the far rate and skipped the expression stage entirely —
    ///     gaze quietly half-dying with nothing logged.
    /// </summary>
    public sealed class GazeLodVisibilityTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("GazeLodVisibilityTestCharacter");

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [Test]
        public void NullCache_ReadsAsVisible()
        {
            Assert.IsTrue(
                ConvaiGazeController.EvaluateRendererVisibility(null),
                "A rig that has not resolved yet must not be treated as off-screen.");
        }

        [Test]
        public void EmptyCache_ReadsAsVisible()
        {
            Assert.IsTrue(
                ConvaiGazeController.EvaluateRendererVisibility(new SkinnedMeshRenderer[0]),
                "An empty cache cannot answer the question and must not throttle the character.");
        }

        [Test]
        public void EveryCachedRendererDestroyed_ReadsAsVisible()
        {
            var holder = new GameObject("SwappedMesh");
            holder.transform.SetParent(_root.transform);
            var renderer = holder.AddComponent<SkinnedMeshRenderer>();
            var cache = new[] { renderer };

            // The mesh swap the rebind hook did not see.
            Object.DestroyImmediate(holder);

            Assert.IsTrue(
                ConvaiGazeController.EvaluateRendererVisibility(cache),
                "A fully destroyed cache must read as visible, not pin the character off-screen forever.");
        }

        [Test]
        public void LiveButInvisibleRenderer_ReadsAsNotVisible()
        {
            var holder = new GameObject("HiddenMesh");
            holder.transform.SetParent(_root.transform);
            var renderer = holder.AddComponent<SkinnedMeshRenderer>();

            // No camera renders in EditMode, so a live renderer reports isVisible == false — which
            // is exactly the case the governor is entitled to act on.
            Assert.IsFalse(
                ConvaiGazeController.EvaluateRendererVisibility(new[] { renderer }),
                "A live renderer that nothing is rendering must still report as off-screen.");
        }

        [Test]
        public void OneLiveRendererAmongDestroyedOnes_StillEvaluatesTheLiveOne()
        {
            var destroyedHolder = new GameObject("DestroyedMesh");
            destroyedHolder.transform.SetParent(_root.transform);
            var destroyed = destroyedHolder.AddComponent<SkinnedMeshRenderer>();

            var liveHolder = new GameObject("LiveMesh");
            liveHolder.transform.SetParent(_root.transform);
            var live = liveHolder.AddComponent<SkinnedMeshRenderer>();

            Object.DestroyImmediate(destroyedHolder);

            // One survivor means the cache CAN answer, so the answer is the survivor's own state
            // (off-screen in EditMode) rather than the all-destroyed fallback.
            Assert.IsFalse(
                ConvaiGazeController.EvaluateRendererVisibility(new[] { destroyed, live }),
                "A partially stale cache must be judged by its surviving renderers.");
        }
    }
}
