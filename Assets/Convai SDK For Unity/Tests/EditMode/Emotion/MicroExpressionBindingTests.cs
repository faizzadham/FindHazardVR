using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Domain.Logging;
using Convai.Modules.Emotion.Core;
using Convai.Modules.Emotion.Outputs;
using Convai.Runtime;
using Convai.Runtime.Animation;
using Convai.Runtime.Logging;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="MicroExpressionBinding" />: curated-shape resolution
    ///     against a bound rig, submission to the compositor's
    ///     <see cref="FacialBlendshapeLayers.EmotionMicro" /> layer, and graceful degradation
    ///     when no shapes / no compositor is available.
    /// </summary>
    [TestFixture]
    public sealed class MicroExpressionBindingTests
    {
        private readonly List<Object> _createdObjects = new();
        private GameObject _ownerGo;
        private FacialBlendshapeCompositorHost _compositor;
        private static readonly MethodInfo ComposeFrame = typeof(FacialBlendshapeCompositorHost).GetMethod(
            "LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);
        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;

        [SetUp]
        public void SetUp()
        {
            _ownerGo = new GameObject(nameof(MicroExpressionBindingTests));
            _createdObjects.Add(_ownerGo);
            _compositor = _ownerGo.AddComponent<FacialBlendshapeCompositorHost>();

            _settings = ConvaiSettings.Instance;
            if (_settings != null)
            {
                _originalGlobalLevel = _settings.GlobalLogLevel;
                _settings.SetGlobalLogLevel(LogLevel.Trace);
                LoggingConfig.InvalidateCache();
            }
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

            if (_settings != null)
            {
                _settings.SetGlobalLogLevel(_originalGlobalLevel);
                LoggingConfig.InvalidateCache();
            }
        }

        [Test]
        public void Bind_ResolvesCuratedShapes_HasAnyShapeTrue()
        {
            // ARKit-convention brow name, matching MicroExpressionShapeMap's ARKit BrowOuterUp entry.
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "browOuterUpLeft");
            var binding = new MicroExpressionBinding();

            binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, _compositor);

            Assert.That(binding.HasAnyShape, Is.True);
        }

        [Test]
        public void ApplyAndUnbind_WriteAndClearBrowWeight()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "browOuterUpLeft");
            var binding = new MicroExpressionBinding();
            binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, _compositor);

            var director = new MicroExpressionDirector();
            director.Seed(_ownerGo.transform);
            director.SetEmotionBias("joy", 1f);
            director.Tick(1f / 60f, amplitude: 0f, stillness: 0f, speechAccentStrength: 1f, speechEnergy: 1f);

            Assert.That(mesh.GetBlendShapeWeight(0), Is.Zero);
            binding.Apply(director);
            ComposeFrame.Invoke(_compositor, null);
            Assert.That(mesh.GetBlendShapeWeight(0), Is.GreaterThan(0f),
                "A speech accent must reach the facial mesh through the compositor.");

            binding.Unbind();
            ComposeFrame.Invoke(_compositor, null);
            Assert.That(mesh.GetBlendShapeWeight(0), Is.Zero,
                "Unbinding must release the submitted facial output.");
        }

        [Test]
        public void Bind_NoCompositor_IsInert_ApplyIsNoOp()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "browOuterUpLeft");
            var binding = new MicroExpressionBinding();

            Assert.DoesNotThrow(() => binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, null));
            Assert.That(binding.HasAnyShape, Is.False);

            var director = new MicroExpressionDirector();
            director.Seed(_ownerGo.transform);
            Assert.DoesNotThrow(() => binding.Apply(director));
        }

        [Test]
        public void Bind_NoFacialMeshes_IsInert_LogsOnce()
        {
            var binding = new MicroExpressionBinding();
            var sink = new TestLogSink();
            ConvaiLogger.RegisterSink(sink);
            try
            {
                // No meshes on the owner and rig reports none: ResolveFacialMeshes finds nothing.
                Assert.DoesNotThrow(() => binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit), null, _compositor));
                Assert.That(binding.HasAnyShape, Is.False);
            }
            finally
            {
                ConvaiLogger.UnregisterSink(sink);
            }

            var director = new MicroExpressionDirector();
            director.Seed(_ownerGo.transform);
            Assert.DoesNotThrow(() => binding.Apply(director));
        }

        [Test]
        public void Bind_RigWithNoMatchingShapes_LogsOnceAndStaysInert()
        {
            // Mesh exists but carries a blendshape name that matches none of the curated channels.
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "SomeUnrelatedShape");
            var binding = new MicroExpressionBinding();

            var sink = new TestLogSink();
            ConvaiLogger.RegisterSink(sink);
            try
            {
                binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, _compositor);

                List<LogEntry> warnings = sink.Entries
                    .Where(e => e.Level == LogLevel.Warning && e.Message.Contains("MicroExpressionBinding"))
                    .ToList();
                Assert.That(warnings.Count, Is.EqualTo(1),
                    "Exactly one graceful degradation warning is expected when no curated shape resolves.");
            }
            finally
            {
                ConvaiLogger.UnregisterSink(sink);
            }

            Assert.That(binding.HasAnyShape, Is.False);
        }

        [Test]
        public void Unbind_ClearsState_ApplyIsNoOp()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "browOuterUpLeft");
            var binding = new MicroExpressionBinding();
            binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, _compositor);
            Assert.That(binding.HasAnyShape, Is.True);

            binding.Unbind();

            Assert.That(binding.HasAnyShape, Is.False);
            var director = new MicroExpressionDirector();
            director.Seed(_ownerGo.transform);
            Assert.DoesNotThrow(() => binding.Apply(director));
        }

        [Test]
        public void Apply_SteadyState_AllocatesNothing()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "browOuterUpLeft");
            var binding = new MicroExpressionBinding();
            binding.Bind(_ownerGo.transform, StubRig(RigConvention.ARKit, mesh), _ownerGo.transform, _compositor);
            Assert.That(binding.HasAnyShape, Is.True);

            var director = new MicroExpressionDirector();
            director.Seed(_ownerGo.transform);

            string emotionLabel = "joy";
            const float dt = 1f / 60f;
            for (int i = 0; i < 300; i++)
            {
                director.SetEmotionBias(emotionLabel, 0.6f);
                director.Tick(dt, 0.15f, 0.5f, 0.3f, 0.5f + 0.5f * Mathf.Sin(i * 0.1f));
                binding.Apply(director);
            }

            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < 500; i++)
                {
                    director.SetEmotionBias(emotionLabel, 0.6f);
                    director.Tick(dt, 0.15f, 0.5f, 0.3f, 0.5f + 0.5f * Mathf.Sin(i * 0.1f));
                    binding.Apply(director);
                }
            });

            Assert.That(allocationCount, Is.EqualTo(0L),
                $"MicroExpressionDirector.Tick + MicroExpressionBinding.Apply must produce zero managed allocation events in steady state; measured {allocationCount} allocation events.");
        }

        private SkinnedMeshRenderer CreateFaceMesh(string name, string blendshapeName)
        {
            GameObject go = new(name);
            _createdObjects.Add(go);
            go.transform.SetParent(_ownerGo.transform);

            Mesh mesh = new();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
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

        private static IStandardRigBinding StubRig(RigConvention convention, params SkinnedMeshRenderer[] meshes) =>
            new FacialMeshRigStub(convention, meshes);

        private sealed class FacialMeshRigStub : IStandardRigBinding
        {
            public FacialMeshRigStub(RigConvention convention, IReadOnlyList<SkinnedMeshRenderer> facialMeshes)
            {
                DetectedConvention = convention;
                FacialMeshes = facialMeshes;
            }

            public Transform Root => null;
            public IReadOnlyList<SkinnedMeshRenderer> FacialMeshes { get; }
            public RigConvention DetectedConvention { get; }

            public bool TryGetBone(StandardBone semantic, out Transform bone)
            {
                bone = null;
                return false;
            }

            public bool TryGetBlendshape(StandardBlendshape semantic, out SkinnedMeshRenderer mesh, out int blendshapeIndex)
            {
                mesh = null;
                blendshapeIndex = -1;
                return false;
            }
        }
    }
}
