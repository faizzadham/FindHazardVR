using System.Collections;
using Convai.Modules.Gaze.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     Rig rebinding mid-play — the path the T1 audit found broken in two ways: the cached
    ///     renderer array was never refreshed (despite a comment claiming it was), and the
    ///     unusable-rig warning never re-ran, so a character whose rig broke at runtime stopped
    ///     gazing in silence.
    /// </summary>
    public sealed class GazeRebindPlayModeTests
    {
        private GazeRigTestHarness _rig;

        [SetUp]
        public void SetUp() => _rig = GazeRigTestHarness.Build();

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        [UnityTest]
        public IEnumerator RebindMidPlay_KeepsTracking()
        {
            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));
            yield return GazeRigTestHarness.RunForRealSeconds(1.5f);

            Assert.IsTrue(_rig.Root.TryGetComponent(out EmbodimentContext context));
            context.NotifyRigBindingChanged();
            yield return null;

            float yaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                2.5f, () => yaw = _rig.Gaze.CaptureSnapshot().HeadAngles.x);

            Assert.That(yaw, Is.GreaterThan(5f),
                "A rebind recalibrates the chain; it must not leave the character permanently " +
                "un-aimed at a target it was already tracking.");
        }

        [UnityTest]
        public IEnumerator RebindAfterAMeshSwap_DoesNotStrandTheRendererCache()
        {
            // The crowd-LOD off-screen check reads a cached renderer array. Before T1 a mesh swap
            // left it full of destroyed references, which read as "off-screen forever" and
            // throttled the character to the far cognition tier with the expression stage skipped.
            var meshHolder = new GameObject("SkinMesh");
            meshHolder.transform.SetParent(_rig.Root.transform, false);
            meshHolder.AddComponent<SkinnedMeshRenderer>();

            Assert.IsTrue(_rig.Root.TryGetComponent(out EmbodimentContext context));
            context.NotifyRigBindingChanged();
            yield return null;

            // The swap: the old mesh goes, a new one arrives, and the rebind hook is what has to
            // notice.
            Object.DestroyImmediate(meshHolder);
            var replacement = new GameObject("SwappedSkinMesh");
            replacement.transform.SetParent(_rig.Root.transform, false);
            replacement.AddComponent<SkinnedMeshRenderer>();

            context.NotifyRigBindingChanged();
            yield return null;

            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            _rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false });

            float yaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                2.5f, () => yaw = _rig.Gaze.CaptureSnapshot().HeadAngles.x);

            Assert.That(yaw, Is.GreaterThan(5f),
                "After a mesh swap the character must still gaze. A stale renderer cache is what " +
                "used to make this fail, and only with crowd LOD enabled — which is exactly why it " +
                "went unnoticed.");
        }

        [UnityTest]
        public IEnumerator RepeatedRebinds_DoNotAccumulateOrThrow()
        {
            Assert.IsTrue(_rig.Root.TryGetComponent(out EmbodimentContext context));

            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            _rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false });

            for (int i = 0; i < 8; i++)
            {
                context.NotifyRigBindingChanged();
                yield return null;
            }

            yield return GazeRigTestHarness.RunForRealSeconds(2f);

            Vector2 head = _rig.Gaze.CaptureSnapshot().HeadAngles;
            Assert.IsFalse(float.IsNaN(head.x) || float.IsNaN(head.y),
                "A rebind loop must not corrupt the solve state.");
        }
    }
}
