using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.LipSync;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public sealed class SkinnedMeshBlendshapeSinkCompositorBoundaryTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                Object obj = _createdObjects[i];
                if (obj != null)
                    Object.DestroyImmediate(obj);
            }

            _createdObjects.Clear();
        }

        [Test]
        public void Initialize_DirectPath_DoesNotCreateCompositorHost()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("jawOpen");
            ConvaiLipSyncMapAsset map = CreateMap("jawOpen", "jawOpen");

            var sink = new SkinnedMeshBlendshapeSink(renderer);
            sink.Initialize(new List<SkinnedMeshRenderer> { renderer }, map);

            Assert.IsNull(renderer.GetComponent<FacialBlendshapeCompositorHost>());
            Assert.IsNull(renderer.GetComponentInParent<FacialBlendshapeCompositorHost>(true));

            sink.Apply(new[] { 0.5f }, new[] { "jawOpen" });

            Assert.IsNull(renderer.GetComponentInParent<FacialBlendshapeCompositorHost>(true));
            Assert.AreEqual(50f, renderer.GetBlendShapeWeight(0), 1e-3f,
                "Direct path should write mesh weights in edit mode without a compositor.");
        }

        [Test]
        public void Initialize_WithExistingCompositor_UsesCompositorSink_DoesNotAddSecondHost()
        {
            GameObject characterRoot = new("Character");
            _createdObjects.Add(characterRoot);
            characterRoot.AddComponent<ConvaiCharacter>();

            GameObject face = new("Face");
            face.transform.SetParent(characterRoot.transform);
            SkinnedMeshRenderer renderer = CreateSkinnedMeshOn(face, "jawOpen");
            FacialBlendshapeCompositorHost host = characterRoot.AddComponent<FacialBlendshapeCompositorHost>();

            ConvaiLipSyncMapAsset map = CreateMap("jawOpen", "jawOpen");
            var sink = new SkinnedMeshBlendshapeSink(renderer);
            sink.Initialize(new List<SkinnedMeshRenderer> { renderer }, map);

            Assert.AreEqual(1, characterRoot.GetComponentsInChildren<FacialBlendshapeCompositorHost>(true).Length);

            ILipSyncBlendshapeOutputSink output =
                LipSyncBlendshapeOutputSinkFactory.Create(renderer, sink);
            Assert.IsInstanceOf<CompositorLipSyncBlendshapeOutputSink>(output);

            sink.SetSpeechActive(true);
            sink.Apply(new[] { 0.5f }, new[] { "jawOpen" });

            MethodInfo lateUpdate = typeof(FacialBlendshapeCompositorHost).GetMethod(
                "LateUpdate",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            Assert.IsNotNull(lateUpdate);
            lateUpdate.Invoke(host, null);

            Assert.AreEqual(1, characterRoot.GetComponentsInChildren<FacialBlendshapeCompositorHost>(true).Length);
            float composedWeight = renderer.GetBlendShapeWeight(0);
            Assert.Greater(composedWeight, 0f,
                "Compositor path should drive a non-zero blendshape after speech is marked active.");
            Assert.LessOrEqual(composedWeight, 50f + 1e-3f,
                "Compositor path output must stay within mapped lip-sync bounds for a single frame.");
        }

        [Test]
        public void Apply_DirectPath_ShapesValueWithCurveExponent()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("jawOpen");
            ConvaiLipSyncMapAsset map = CreateMap("jawOpen", "jawOpen", curveExponent: 0.5f);

            var sink = new SkinnedMeshBlendshapeSink(renderer);
            sink.Initialize(new List<SkinnedMeshRenderer> { renderer }, map);

            sink.Apply(new[] { 0.25f }, new[] { "jawOpen" });

            Assert.AreEqual(50f, renderer.GetBlendShapeWeight(0), 1e-3f,
                "curveExponent 0.5 should map input 0.25 to sqrt(0.25) = 0.5 before the 0-100 scale.");
        }

        [Test]
        public void ResetToZero_DirectPath_ClearsMeshWeights()
        {
            SkinnedMeshRenderer renderer = CreateSkinnedMeshWithBlendshape("jawOpen");
            ConvaiLipSyncMapAsset map = CreateMap("jawOpen", "jawOpen");
            var sink = new SkinnedMeshBlendshapeSink(renderer);
            sink.Initialize(new List<SkinnedMeshRenderer> { renderer }, map);

            sink.Apply(new[] { 0.5f }, new[] { "jawOpen" });
            Assert.Greater(renderer.GetBlendShapeWeight(0), 1f);

            sink.ResetToZero(new[] { "jawOpen" });
            Assert.AreEqual(0f, renderer.GetBlendShapeWeight(0), 1e-4f);
        }

        [Test]
        public void Initialize_AfterDrivingOldMesh_ResetsOldTargetsBeforeRebinding()
        {
            SkinnedMeshRenderer oldRenderer = CreateSkinnedMeshWithBlendshape("jawOpen");
            SkinnedMeshRenderer nextRenderer = CreateSkinnedMeshWithBlendshape("jawOpen");
            ConvaiLipSyncMapAsset map = CreateMap("jawOpen", "jawOpen");
            var sink = new SkinnedMeshBlendshapeSink(oldRenderer);
            sink.Initialize(new List<SkinnedMeshRenderer> { oldRenderer }, map);
            sink.Apply(new[] { 0.8f }, new[] { "jawOpen" });
            Assert.Greater(oldRenderer.GetBlendShapeWeight(0), 1f);

            sink.Initialize(new List<SkinnedMeshRenderer> { nextRenderer }, map);

            Assert.AreEqual(0f, oldRenderer.GetBlendShapeWeight(0), 1e-4f);
        }

        private SkinnedMeshRenderer CreateSkinnedMeshWithBlendshape(string blendshapeName)
        {
            GameObject go = new("SkinnedMesh");
            _createdObjects.Add(go);
            return CreateSkinnedMeshOn(go, blendshapeName);
        }

        private SkinnedMeshRenderer CreateSkinnedMeshOn(GameObject go, string blendshapeName)
        {
            Mesh mesh = CreateMesh(blendshapeName);
            _createdObjects.Add(mesh);

            SkinnedMeshRenderer renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }

        private static Mesh CreateMesh(string blendshapeName)
        {
            Mesh mesh = new();
            mesh.vertices = new[]
            {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };

            Vector3[] deltaVertices = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaNormals = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaTangents = { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame(blendshapeName, 100f, deltaVertices, deltaNormals, deltaTangents);
            return mesh;
        }

        private ConvaiLipSyncMapAsset CreateMap(string source, string target, float curveExponent = 1f)
        {
            var map = ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>();
            _createdObjects.Add(map);

            SerializedObject serialized = new(map);
            serialized.FindProperty("_allowUnmappedPassthrough").boolValue = false;
            SerializedProperty mappings = serialized.FindProperty("_mappings");
            mappings.arraySize = 1;
            SerializedProperty entry = mappings.GetArrayElementAtIndex(0);
            entry.FindPropertyRelative("sourceBlendshape").stringValue = source;
            entry.FindPropertyRelative("enabled").boolValue = true;
            entry.FindPropertyRelative("multiplier").floatValue = 1f;
            entry.FindPropertyRelative("offset").floatValue = 0f;
            entry.FindPropertyRelative("curveExponent").floatValue = curveExponent;
            entry.FindPropertyRelative("useOverrideValue").boolValue = false;
            entry.FindPropertyRelative("ignoreGlobalModifiers").boolValue = false;
            entry.FindPropertyRelative("clampMinValue").floatValue = 0f;
            entry.FindPropertyRelative("clampMaxValue").floatValue = 1f;
            SerializedProperty targetNames = entry.FindPropertyRelative("targetNames");
            targetNames.arraySize = 1;
            targetNames.GetArrayElementAtIndex(0).stringValue = target;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return map;
        }
    }
}
