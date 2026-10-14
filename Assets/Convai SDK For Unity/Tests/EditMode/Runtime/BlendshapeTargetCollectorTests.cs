using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Runtime.Animation;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class BlendshapeTargetCollectorTests
    {
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                Object obj = _createdObjects[i];
                if (obj != null) Object.DestroyImmediate(obj);
            }

            _createdObjects.Clear();
        }

        [Test]
        public void CollectAllMatches_GathersEveryMeshWithTheResolvedBlendshapeName()
        {
            SkinnedMeshRenderer first = CreateRenderer("FaceA", "Eye_Blink_L");
            SkinnedMeshRenderer second = CreateRenderer("FaceB", "Eye_Blink_L");

            StubRigBinding rigBinding = new(
                new[] { first, second },
                new Dictionary<StandardBlendshape, BlendshapeTargetKey>
                {
                    { StandardBlendshape.EyeBlinkLeft, new BlendshapeTargetKey(first, 0) }
                });

            List<BlendshapeTargetKey> targets = new();

            int count = BlendshapeTargetCollector.CollectAllMatches(
                rigBinding, StandardBlendshape.EyeBlinkLeft, targets);

            Assert.AreEqual(2, count);
            Assert.AreEqual(2, targets.Count);
        }

        [Test]
        public void CollectAllMatches_ReturnsZero_WhenSemanticIsMissing()
        {
            SkinnedMeshRenderer first = CreateRenderer("FaceA", "Eye_Blink_L");
            StubRigBinding rigBinding = new(
                new[] { first },
                new Dictionary<StandardBlendshape, BlendshapeTargetKey>());

            List<BlendshapeTargetKey> targets = new();

            int count = BlendshapeTargetCollector.CollectAllMatches(
                rigBinding, StandardBlendshape.EyeBlinkRight, targets);

            Assert.AreEqual(0, count);
            Assert.AreEqual(0, targets.Count);
        }

        private SkinnedMeshRenderer CreateRenderer(string name, string blendshapeName)
        {
            GameObject go = new(name);
            _createdObjects.Add(go);

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

        private sealed class StubRigBinding : IStandardRigBinding
        {
            private readonly IReadOnlyDictionary<StandardBlendshape, BlendshapeTargetKey> _blendshapes;

            public StubRigBinding(
                IReadOnlyList<SkinnedMeshRenderer> facialMeshes,
                IReadOnlyDictionary<StandardBlendshape, BlendshapeTargetKey> blendshapes)
            {
                FacialMeshes = facialMeshes;
                _blendshapes = blendshapes;
            }

            public Transform Root => null;
            public IReadOnlyList<SkinnedMeshRenderer> FacialMeshes { get; }
            public RigConvention DetectedConvention => RigConvention.Unknown;

            public bool TryGetBone(StandardBone semantic, out Transform bone)
            {
                bone = null;
                return false;
            }

            public bool TryGetBlendshape(StandardBlendshape semantic, out SkinnedMeshRenderer mesh, out int blendshapeIndex)
            {
                if (_blendshapes.TryGetValue(semantic, out BlendshapeTargetKey key))
                {
                    mesh = key.Mesh;
                    blendshapeIndex = key.BlendshapeIndex;
                    return true;
                }

                mesh = null;
                blendshapeIndex = -1;
                return false;
            }
        }
    }
}
