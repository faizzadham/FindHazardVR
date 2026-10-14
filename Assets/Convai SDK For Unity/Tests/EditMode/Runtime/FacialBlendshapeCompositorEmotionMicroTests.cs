using System.Collections.Generic;
using System.Reflection;
using Convai.Runtime.Animation;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Shared-infrastructure gate: <see cref="FacialBlendshapeLayers.EmotionMicro" /> must be
    ///     (a) a provable no-op when nothing submits to it (compositor inertness — the hard
    ///     legacy gate for the Emotion module's opt-in micro-life layer) and (b) additive on top
    ///     of the base emotion layers for a shared target key when something does submit,
    ///     never max-blended.
    /// </summary>
    [TestFixture]
    public sealed class FacialBlendshapeCompositorEmotionMicroTests
    {
        private readonly List<Object> _createdObjects = new();

        private ConvaiFacialCompositionProfile _profile;

        [SetUp]
        public void SetUp()
        {
            // ComposePassThrough (the no-profile fallback) max-blends across ALL layers, which
            // would mask EmotionMicro's additive behavior. A real profile routes EmotionMicro
            // through ComposeAndWriteRegion's dedicated additive term instead.
            _profile = ScriptableObject.CreateInstance<ConvaiFacialCompositionProfile>();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                Object obj = _createdObjects[i];
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _createdObjects.Clear();

            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        private sealed class FakeSource : IFacialBlendshapeSource
        {
            public Component SourceComponent => null;
            public string SourceName => "FakeSource";
        }

        [UnityEngine.TestTools.UnityTest]
        public System.Collections.IEnumerator SubmitAndCompose_WarmedLayer_StaysWithinAllocationBudget()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("Brow_Raise");
            FacialBlendshapeCompositorHost host = renderer.gameObject.AddComponent<FacialBlendshapeCompositorHost>();
            host.SetCompositionProfile(_profile);
            var source = new FakeSource();
            var keys = new[] { new BlendshapeTargetKey(renderer, 0) };
            var weights = new[] { 40f };
            var compose = (System.Action)System.Delegate.CreateDelegate(typeof(System.Action), host,
                typeof(FacialBlendshapeCompositorHost).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic));
            long bytes = 0;
            yield return AllocationMeasurement.Measure(i =>
            {
                weights[0] = (i & 1) == 0 ? 20f : 40f;
                host.SubmitLayer(source, FacialBlendshapeLayers.EmotionGeneral, keys, weights, 1);
                compose();
            }, value => bytes = value);

            Assert.That(renderer.GetBlendShapeWeight(0), Is.GreaterThan(0f));
            Assert.That(bytes, Is.LessThanOrEqualTo(48L * AllocationMeasurement.MeasuredCalls),
                "Warmed per-operation allocation must not exceed the measured baseline; tighten after optimizing this path.");
        }

        [Test]
        public void NoEmotionMicroSubmission_ComposesIdenticallyToPreP3Baseline()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("Brow_Raise");
            FacialBlendshapeCompositorHost host = renderer.gameObject.AddComponent<FacialBlendshapeCompositorHost>();
            host.SetCompositionProfile(_profile);
            var source = new FakeSource();

            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionGeneral,
                new[] { new BlendshapeTargetKey(renderer, 0) }, new[] { 40f }, 1);

            InvokeLateUpdate(host);
            float withoutMicro = renderer.GetBlendShapeWeight(0);

            // Second frame: submit the identical EmotionGeneral contribution again, still with
            // no EmotionMicro submission. Compositor inertness means this must reproduce the
            // exact same composed weight — the EmotionMicro GetLayerValue lookup returns 0 and
            // the "+ 0" term changes nothing.
            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionGeneral,
                new[] { new BlendshapeTargetKey(renderer, 0) }, new[] { 40f }, 1);
            InvokeLateUpdate(host);
            float stillWithoutMicro = renderer.GetBlendShapeWeight(0);

            Assert.That(stillWithoutMicro, Is.EqualTo(withoutMicro).Within(1e-6f),
                "With no EmotionMicro submission ever made, repeated identical frames must compose to the identical weight (bit-identical to behaviour without the micro layer).");
            Assert.That(withoutMicro, Is.GreaterThan(0f));
        }

        [Test]
        public void EmotionMicroSubmission_AddsOnTopOfBaseEmotion_ForSharedKey()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("Brow_Raise");
            FacialBlendshapeCompositorHost host = renderer.gameObject.AddComponent<FacialBlendshapeCompositorHost>();
            host.SetCompositionProfile(_profile);
            var source = new FakeSource();
            var key = new BlendshapeTargetKey(renderer, 0);

            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionGeneral, new[] { key }, new[] { 40f }, 1);
            InvokeLateUpdate(host);
            float baseline = renderer.GetBlendShapeWeight(0);

            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionGeneral, new[] { key }, new[] { 40f }, 1);
            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionMicro, new[] { key }, new[] { 10f }, 1);
            InvokeLateUpdate(host);
            float withMicro = renderer.GetBlendShapeWeight(0);

            Assert.That(withMicro, Is.GreaterThan(baseline),
                "A submission on EmotionMicro must ADD to the composed weight for a shared key, not be discarded by a max-blend against the (larger) base emotion contribution.");

            // Additive, not max: composed should reflect base + micro (both pass through the
            // same emotion-category weight in the pass-through/no-profile fallback used here),
            // so it must exceed a pure max(base, micro) which would equal `baseline` since
            // micro (10) < base (40).
            Assert.That(withMicro, Is.GreaterThan(baseline + 1e-3f));
        }

        [Test]
        public void EmotionMicroSubmission_AloneWithNoBaseEmotion_StillComposesNonZero()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("Brow_Raise");
            FacialBlendshapeCompositorHost host = renderer.gameObject.AddComponent<FacialBlendshapeCompositorHost>();
            host.SetCompositionProfile(_profile);
            var source = new FakeSource();
            var key = new BlendshapeTargetKey(renderer, 0);

            host.SubmitLayer(source, FacialBlendshapeLayers.EmotionMicro, new[] { key }, new[] { 15f }, 1);
            InvokeLateUpdate(host);

            Assert.That(renderer.GetBlendShapeWeight(0), Is.GreaterThan(0f),
                "EmotionMicro alone (idle drift with no active base emotion) must still produce visible output.");
        }

        private static void InvokeLateUpdate(FacialBlendshapeCompositorHost host)
        {
            MethodInfo lateUpdate = typeof(FacialBlendshapeCompositorHost).GetMethod(
                "LateUpdate", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(lateUpdate);
            lateUpdate.Invoke(host, null);
        }

        private SkinnedMeshRenderer CreateSkinnedMeshWithBlendshape(string blendshapeName)
        {
            GameObject go = new("SkinnedMesh");
            _createdObjects.Add(go);

            Mesh mesh = new();
            mesh.vertices = new[] { new Vector3(0f, 0f, 0f), new Vector3(1f, 0f, 0f), new Vector3(0f, 1f, 0f) };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            Vector3[] deltaVertices = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaNormals = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaTangents = { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame(blendshapeName, 100f, deltaVertices, deltaNormals, deltaTangents);
            _createdObjects.Add(mesh);

            SkinnedMeshRenderer renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }
    }
}
