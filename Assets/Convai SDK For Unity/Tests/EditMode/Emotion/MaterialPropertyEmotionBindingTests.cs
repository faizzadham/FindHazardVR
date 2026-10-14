using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Domain.Logging;
using Convai.Modules.Emotion.Outputs;
using Convai.Modules.Emotion.Taxonomy;
using Convai.Runtime;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Unit tests for <see cref="MaterialPropertyEmotionBinding" />: MaterialPropertyBlock
    ///     get-modify-set writes, alternation/gain scaling, same-property max-combine, rest-on-
    ///     unbind, unresolvable-label/empty-property skipping, the all-miss warn-once diagnostic,
    ///     and graceful degradation on a missing/empty rig.
    /// </summary>
    [TestFixture]
    public sealed class MaterialPropertyEmotionBindingTests
    {
        private const string SupportedProperty = "_Smoothness";
        private const string UnsupportedProperty = "_ConvaiTestUnsupportedProperty";

        /// <summary>
        ///     A minimal unlit shader declaring the two float properties these tests read back, and
        ///     nothing else. Pipeline-independent on purpose — see <see cref="GetFixtureShader" />.
        /// </summary>
        private const string FixtureShaderSource =
            "Shader \"Hidden/Convai/Tests/MaterialPropertyEmotionBindingFixture\"\n" +
            "{\n" +
            "    Properties\n" +
            "    {\n" +
            "        _Smoothness (\"Smoothness\", Float) = 0\n" +
            "        _Metallic (\"Metallic\", Float) = 0\n" +
            "    }\n" +
            "    SubShader\n" +
            "    {\n" +
            "        Pass\n" +
            "        {\n" +
            "            CGPROGRAM\n" +
            "            #pragma vertex vert\n" +
            "            #pragma fragment frag\n" +
            "            #include \"UnityCG.cginc\"\n" +
            "            float _Smoothness;\n" +
            "            float _Metallic;\n" +
            "            float4 vert(float4 vertex : POSITION) : SV_POSITION { return UnityObjectToClipPos(vertex); }\n" +
            "            fixed4 frag() : SV_Target { return fixed4(_Smoothness, _Metallic, 0, 1); }\n" +
            "            ENDCG\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        private readonly List<Object> _createdObjects = new();
        private Shader _fixtureShader;
        private EmotionTaxonomyAsset _taxonomy;
        private GameObject _ownerGo;
        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;

        [SetUp]
        public void SetUp()
        {
            _taxonomy = EmotionTaxonomyAsset.CreateDefault();
            _ownerGo = new GameObject(nameof(MaterialPropertyEmotionBindingTests));
            _createdObjects.Add(_ownerGo);

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
            LogAssert.NoUnexpectedReceived();

            for (int i = _createdObjects.Count - 1; i >= 0; i--)
            {
                Object obj = _createdObjects[i];
                if (obj != null) Object.DestroyImmediate(obj);
            }
            _createdObjects.Clear();
            _fixtureShader = null;

            if (_taxonomy != null) Object.DestroyImmediate(_taxonomy);

            if (_settings != null)
            {
                _settings.SetGlobalLogLevel(_originalGlobalLevel);
                LoggingConfig.InvalidateCache();
            }
        }

        // ── basic bind + apply writes the lerped value into the renderer's MPB ─────

        [Test]
        public void Apply_WritesLerpedValue_ReadableViaGetPropertyBlock()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f) });

            ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh));
            binding.Apply(new Dictionary<string, float> { ["joy"] = 0.5f }, intensityGain: 1f);

            var mpb = new MaterialPropertyBlock();
            mesh.GetPropertyBlock(mpb);
            Assert.That(mpb.GetFloat(SupportedProperty), Is.EqualTo(0.5f).Within(0.001f));
        }

        // ── alternation and intensityGain both scale the composed intensity ────────

        [Test]
        public void Apply_IntensityGain_ScalesComposedValue()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f) });
            ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh));

            binding.Apply(new Dictionary<string, float> { ["joy"] = 0.5f }, 1f);
            float valueAtGainOne = ReadFloat(mesh, SupportedProperty);

            binding.Apply(new Dictionary<string, float> { ["joy"] = 0.5f }, 0.5f);
            float valueAtGainHalf = ReadFloat(mesh, SupportedProperty);

            Assert.That(valueAtGainOne, Is.EqualTo(0.5f).Within(0.001f));
            Assert.That(valueAtGainHalf, Is.EqualTo(0.25f).Within(0.001f));
        }

        // ── two slots on the same property MAX-COMBINE, order-independent ──────────

        [Test]
        public void Apply_TwoSlotsSameProperty_MaxCombines_OrderIndependent()
        {
            SkinnedMeshRenderer meshA = CreateFaceMesh("FaceA", "Brow_Raise", SupportedProperty);
            var bindingJoyFirst = new MaterialPropertyEmotionBinding();
            bindingJoyFirst.SetSlots(new[]
            {
                new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f),
                new MaterialPropertyEmotionSlot("trust", SupportedProperty, 0f, 0.5f)
            });
            ((IEmotionOutputBinding)bindingJoyFirst).Bind(_ownerGo, _taxonomy, StubRig(meshA));
            var scores = new Dictionary<string, float> { ["joy"] = 0.3f, ["trust"] = 0.8f };
            bindingJoyFirst.Apply(scores, 1f);
            float valueJoyFirst = ReadFloat(meshA, SupportedProperty);

            SkinnedMeshRenderer meshB = CreateFaceMesh("FaceB", "Brow_Raise", SupportedProperty);
            var bindingTrustFirst = new MaterialPropertyEmotionBinding();
            bindingTrustFirst.SetSlots(new[]
            {
                new MaterialPropertyEmotionSlot("trust", SupportedProperty, 0f, 0.5f),
                new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f)
            });
            ((IEmotionOutputBinding)bindingTrustFirst).Bind(_ownerGo, _taxonomy, StubRig(meshB));
            bindingTrustFirst.Apply(scores, 1f);
            float valueTrustFirst = ReadFloat(meshB, SupportedProperty);

            // trust (0.8 intensity) beats joy (0.3), so trust's [0, 0.5] range wins: Lerp(0, 0.5, 0.8) = 0.4.
            Assert.That(valueJoyFirst, Is.EqualTo(0.4f).Within(0.001f));
            Assert.That(valueTrustFirst, Is.EqualTo(valueJoyFirst).Within(0.0001f),
                "Max-combine must be independent of authoring order.");
        }

        // ── Unbind resets the property to the slot's minValue ──────────────────────

        [Test]
        public void Unbind_ResetsPropertyToMinValue()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0.2f, 1f) });
            ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh));
            binding.Apply(new Dictionary<string, float> { ["joy"] = 1f }, 1f);

            Assert.That(ReadFloat(mesh, SupportedProperty), Is.EqualTo(1f).Within(0.001f));

            binding.Unbind(_ownerGo);

            Assert.That(ReadFloat(mesh, SupportedProperty), Is.EqualTo(0.2f).Within(0.001f));
        }

        // ── skip rules + the all-miss warn-once diagnostic ──────────────────────────

        [Test]
        public void Bind_UnresolvableEmotionLabel_SkipsSilently_NoWrite()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            // Nonzero MinValue: if the slot had actually resolved, even zero intensity would
            // have written 0.3f. A read-back of exactly 0f (a fresh MPB's untouched default)
            // proves the property was never written at all, not merely written at rest.
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("not-a-real-emotion", SupportedProperty, 0.3f, 1f) });

            Assert.DoesNotThrow(() => ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh)));
            Assert.DoesNotThrow(() => binding.Apply(new Dictionary<string, float> { ["not-a-real-emotion"] = 1f }, 1f));

            Assert.That(ReadFloat(mesh, SupportedProperty), Is.EqualTo(0f),
                "An unresolvable emotion label must never resolve to a write target, so the property must stay untouched.");

            FieldInfo boundField = typeof(MaterialPropertyEmotionBinding).GetField(
                "_bound", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(boundField, "MaterialPropertyEmotionBinding must declare a private _bound field.");
            Assert.That(boundField.GetValue(binding), Is.EqualTo(false),
                "An all-unresolvable slot list must leave the binding unbound (no resolved slots).");
        }

        [Test]
        public void Bind_EmptyPropertyName_SkipsSilently_DoesNotWarn()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", string.Empty, 0f, 1f) });

            var sink = new TestLogSink();
            ConvaiLogger.RegisterSink(sink);
            try
            {
                Assert.DoesNotThrow(() => ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh)));

                Assert.That(sink.Entries.Any(e => e.Level == LogLevel.Warning), Is.False,
                    "An empty PropertyName is a deliberate skip, not a typo, and must never warn.");
            }
            finally
            {
                ConvaiLogger.UnregisterSink(sink);
            }
        }

        [Test]
        public void Bind_NoTargetMaterialHasAuthoredProperty_WarnsExactlyOnce()
        {
            // The mesh's material is the same supported shader used elsewhere in this fixture,
            // but the authored property name is one no shader in this fixture ever declares —
            // guaranteeing the all-miss warn condition without needing an error-shader material.
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", UnsupportedProperty, 0f, 1f) });

            var sink = new TestLogSink();
            ConvaiLogger.RegisterSink(sink);
            try
            {
                ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh));

                List<LogEntry> warnings = sink.Entries
                    .Where(e => e.Level == LogLevel.Warning && e.Message.Contains(UnsupportedProperty))
                    .ToList();
                Assert.That(warnings.Count, Is.EqualTo(1),
                    "Exactly one warning naming the unmatched property is expected in the all-miss case.");
            }
            finally
            {
                ConvaiLogger.UnregisterSink(sink);
            }
        }

        [Test]
        public void Bind_NoRigAndNoFacialMeshes_IsInert_ApplyIsNoOp()
        {
            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f) });

            Assert.DoesNotThrow(() => ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, null));
            Assert.DoesNotThrow(() => binding.Apply(new Dictionary<string, float> { ["joy"] = 1f }, 1f));
        }

        // ── get-modify-set preserves a pre-existing MPB entry ───────────────────────

        [Test]
        public void Apply_PreservesPreExistingMaterialPropertyBlockEntry()
        {
            SkinnedMeshRenderer mesh = CreateFaceMesh("Face", "Brow_Raise", SupportedProperty);

            const string coexistingProperty = "_Metallic";
            var preexisting = new MaterialPropertyBlock();
            preexisting.SetFloat(coexistingProperty, 0.77f);
            mesh.SetPropertyBlock(preexisting);

            var binding = new MaterialPropertyEmotionBinding();
            binding.SetSlots(new[] { new MaterialPropertyEmotionSlot("joy", SupportedProperty, 0f, 1f) });
            ((IEmotionOutputBinding)binding).Bind(_ownerGo, _taxonomy, StubRig(mesh));
            binding.Apply(new Dictionary<string, float> { ["joy"] = 0.5f }, 1f);

            Assert.That(ReadFloat(mesh, coexistingProperty), Is.EqualTo(0.77f).Within(0.001f),
                "A pre-existing MPB entry from another system must survive this binding's get-modify-set writes.");
            Assert.That(ReadFloat(mesh, SupportedProperty), Is.EqualTo(0.5f).Within(0.001f));
        }

        private static float ReadFloat(Renderer renderer, string propertyName)
        {
            var mpb = new MaterialPropertyBlock();
            renderer.GetPropertyBlock(mpb);
            return mpb.GetFloat(propertyName);
        }

        private SkinnedMeshRenderer CreateFaceMesh(string name, string blendshapeName, string materialPropertyName)
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

            var material = new Material(GetFixtureShader());
            _createdObjects.Add(material);
            Assert.IsTrue(material.HasProperty(materialPropertyName),
                $"Test fixture shader must declare '{materialPropertyName}' for this test to be meaningful.");
            renderer.sharedMaterial = material;

            return renderer;
        }

        /// <summary>
        ///     The fixture's own shader, compiled from source so the suite owns exactly which
        ///     properties exist.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This used to be <c>Shader.Find("Universal Render Pipeline/Lit")</c>, which made a
        ///         package test suite fail on any project without URP installed — the package itself
        ///         declares no render-pipeline dependency, and <c>ArchitectureGuardTests</c> holds it
        ///         to that. Borrowing a shipped shader also left the test's premise in someone else's
        ///         hands: a URP release that renamed <c>_Smoothness</c> would have failed these tests
        ///         for a reason that has nothing to do with the binding under test.
        ///     </para>
        ///     <para>
        ///         What the tests actually need is one material that declares
        ///         <see cref="SupportedProperty" /> and does not declare
        ///         <see cref="UnsupportedProperty" /> — which is what decides the binding's found /
        ///         all-miss diagnostic. Declaring those here states that premise instead of assuming it.
        ///     </para>
        /// </remarks>
        private Shader GetFixtureShader()
        {
            if (_fixtureShader != null) return _fixtureShader;

            _fixtureShader = ShaderUtil.CreateShaderAsset(FixtureShaderSource);
            Assert.NotNull(_fixtureShader, "Failed to compile the fixture shader.");
            _fixtureShader.hideFlags = HideFlags.HideAndDontSave;
            _createdObjects.Add(_fixtureShader);
            return _fixtureShader;
        }

        private static IStandardRigBinding StubRig(params SkinnedMeshRenderer[] meshes) => new FacialMeshRigStub(meshes);

        private sealed class FacialMeshRigStub : IStandardRigBinding
        {
            public FacialMeshRigStub(IReadOnlyList<SkinnedMeshRenderer> facialMeshes) => FacialMeshes = facialMeshes;

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
                mesh = null;
                blendshapeIndex = -1;
                return false;
            }
        }
    }
}
