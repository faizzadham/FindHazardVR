using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Solvers;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class EyeApertureWriterTests
    {
        [Test]
        public void ApertureBelowOne_MapsToSquint_NoWide()
        {
            EyeBlendshapeWriter.ResolveApertureWeights(0.5f, 0f, out float squint, out float wide);

            Assert.That(squint, Is.GreaterThan(0f), "Aperture < 1 narrows the lids (squint).");
            Assert.That(wide, Is.EqualTo(0f), "No widening while squinting.");
        }

        [Test]
        public void ApertureAboveOne_MapsToWide_NoSquint()
        {
            EyeBlendshapeWriter.ResolveApertureWeights(1.5f, 0f, out float squint, out float wide);

            Assert.That(wide, Is.GreaterThan(0f), "Aperture > 1 widens the lids.");
            Assert.That(squint, Is.EqualTo(0f), "No squint while widening.");
        }

        [Test]
        public void NeutralAperture_WritesNothing()
        {
            EyeBlendshapeWriter.ResolveApertureWeights(1f, 0f, out float squint, out float wide);

            Assert.That(squint, Is.EqualTo(0f));
            Assert.That(wide, Is.EqualTo(0f), "Aperture 1.0 is a no-op.");
        }

        [Test]
        public void Blink_OverridesAperture()
        {
            EyeBlendshapeWriter.ResolveApertureWeights(0.5f, 1f, out float squintUnderFullBlink, out _);
            Assert.That(squintUnderFullBlink, Is.EqualTo(0f),
                "A full blink zeroes the aperture — a blink always closes.");

            EyeBlendshapeWriter.ResolveApertureWeights(0.5f, 0.5f, out float squintHalfBlink, out _);
            EyeBlendshapeWriter.ResolveApertureWeights(0.5f, 0f, out float squintOpen, out _);
            Assert.That(squintHalfBlink, Is.EqualTo(squintOpen * 0.5f).Within(0.01f),
                "A half blink halves the aperture contribution.");
        }

        [Test]
        public void SymmetricApertureExtremes_ProduceEqualMagnitude()
        {
            EyeBlendshapeWriter.ResolveApertureWeights(0.5f, 0f, out float squint, out _);
            EyeBlendshapeWriter.ResolveApertureWeights(1.5f, 0f, out _, out float wide);

            Assert.That(squint, Is.EqualTo(wide).Within(0.001f),
                "Symmetric aperture deltas map to symmetric shape weights.");
        }

        [TestCase(StandardBlendshape.EyeLookInLeft)]
        [TestCase(StandardBlendshape.EyeLookOutLeft)]
        [TestCase(StandardBlendshape.EyeLookInRight)]
        [TestCase(StandardBlendshape.EyeLookOutRight)]
        public void MissingAnyHorizontalDirection_DisablesBlendshapeEyeBackend(StandardBlendshape missing)
        {
            GameObject host = new("EyeLookRig");
            Mesh mesh = null;
            try
            {
                SkinnedMeshRenderer renderer = host.AddComponent<SkinnedMeshRenderer>();
                mesh = CreateEyeLookMesh();
                renderer.sharedMesh = mesh;
                var rig = new EyeLookRigStub(host.transform, renderer, missing);
                var writer = new EyeBlendshapeWriter();

                writer.Bind(rig);

                Assert.IsFalse(writer.HasLookShapes,
                    $"Missing {missing} must not select a one-sided blendshape eye backend.");
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void AllHorizontalDirections_EnableBlendshapeEyeBackend()
        {
            GameObject host = new("EyeLookRig");
            Mesh mesh = null;
            try
            {
                SkinnedMeshRenderer renderer = host.AddComponent<SkinnedMeshRenderer>();
                mesh = CreateEyeLookMesh();
                renderer.sharedMesh = mesh;
                var writer = new EyeBlendshapeWriter();

                writer.Bind(new EyeLookRigStub(host.transform, renderer, null));

                Assert.IsTrue(writer.HasLookShapes);
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (mesh != null) Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh CreateEyeLookMesh()
        {
            var mesh = new Mesh
            {
                vertices = new[] { Vector3.zero, Vector3.right, Vector3.up },
                normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward },
                triangles = new[] { 0, 1, 2 }
            };
            Vector3[] deltas = { Vector3.zero, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame("EyeLookInLeft", 100f, deltas, deltas, deltas);
            mesh.AddBlendShapeFrame("EyeLookOutLeft", 100f, deltas, deltas, deltas);
            mesh.AddBlendShapeFrame("EyeLookInRight", 100f, deltas, deltas, deltas);
            mesh.AddBlendShapeFrame("EyeLookOutRight", 100f, deltas, deltas, deltas);
            return mesh;
        }

        private sealed class EyeLookRigStub : IStandardRigBinding
        {
            private readonly SkinnedMeshRenderer _renderer;
            private readonly StandardBlendshape? _missing;

            public EyeLookRigStub(Transform root, SkinnedMeshRenderer renderer, StandardBlendshape? missing)
            {
                Root = root;
                _renderer = renderer;
                _missing = missing;
                FacialMeshes = new[] { renderer };
            }

            public Transform Root { get; }
            public IReadOnlyList<SkinnedMeshRenderer> FacialMeshes { get; }
            public RigConvention DetectedConvention => RigConvention.Unknown;

            public bool TryGetBone(StandardBone semantic, out Transform bone)
            {
                bone = null;
                return false;
            }

            public bool TryGetBlendshape(StandardBlendshape semantic, out SkinnedMeshRenderer mesh, out int blendshapeIndex)
            {
                if (_missing == semantic || !IsHorizontalEyeLook(semantic))
                {
                    mesh = null;
                    blendshapeIndex = -1;
                    return false;
                }

                mesh = _renderer;
                blendshapeIndex = semantic switch
                {
                    StandardBlendshape.EyeLookInLeft => 0,
                    StandardBlendshape.EyeLookOutLeft => 1,
                    StandardBlendshape.EyeLookInRight => 2,
                    StandardBlendshape.EyeLookOutRight => 3,
                    _ => -1
                };
                return blendshapeIndex >= 0;
            }

            private static bool IsHorizontalEyeLook(StandardBlendshape semantic) =>
                semantic == StandardBlendshape.EyeLookInLeft ||
                semantic == StandardBlendshape.EyeLookOutLeft ||
                semantic == StandardBlendshape.EyeLookInRight ||
                semantic == StandardBlendshape.EyeLookOutRight;
        }
    }
}
