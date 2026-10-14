using System;
using System.Reflection;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using Convai.Domain.Embodiment.Interfaces;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level micro-expression gates for <see cref="ConvaiEmotionController" />: the hard
    ///     opted-out invariant (<c>MicroExpressionsEnabled == false</c> never submits
    ///     to the compositor's <see cref="FacialBlendshapeLayers.EmotionMicro" /> layer) and
    ///     opt-in behavior once enabled with a resolvable facial mesh.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerMicroExpressionTests
    {
        private const string CharacterId = "test-char-id";

        private EmbodimentTestRig _rig;
        private FacialBlendshapeCompositorHost _compositor;
        private SkinnedMeshRenderer _faceMesh;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerMicroExpressionTests));

            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            // Pre-add the compositor + a face mesh so EnsureCompositor()/EnsureRigBinding()
            // resolve in EditMode (they only auto-create at runtime).
            _compositor = _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();

            // Include enough ReallusionCC3 signature shapes (Eye_Blink_L/R, V_Open) for
            // RigConventionResolver to classify this mesh as CC3 rather than Unknown — the
            // shape map no longer guesses CC3 for an unrecognized rig, so the curated
            // "Brow_Raise_Outer_L" channel only resolves once the convention is actually detected.
            _faceMesh = CreateFaceMesh(_rig.Root.transform,
                "Eye_Blink_L", "Eye_Blink_R", "V_Open", "Brow_Raise_Outer_L");
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        [Test]
        public void MicroExpressionsDisabled_NeverSubmitsToEmotionMicroLayer()
        {
            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();

            // Opted out explicitly: the shipped default now turns the life layer ON, so a character
            // reads as alive out of the box. This test covers the opted-out path staying inert.
            typeof(ConvaiEmotionProfile)
                .GetField("microExpressionsEnabled", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(profile, false);
            Assert.That(profile.MicroExpressionsEnabled, Is.False, "Sanity: the feature must be off for this test.");

            try
            {
                harness.ApplyProfile(profile);
                harness.Controller.LockEmotion("joy", 1f);

                for (int i = 0; i < 30; i++)
                    harness.Tick(1f / 60f);

                Assert.That(HasEmotionMicroSubmission(_compositor), Is.False,
                    "With MicroExpressionsEnabled turned off, the controller must never submit to the EmotionMicro compositor layer.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void MicroExpressionsEnabled_WithResolvableMesh_SubmitsToEmotionMicroLayer()
        {
            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);

            try
            {
                harness.ApplyProfile(profile);
                harness.Controller.LockEmotion("joy", 1f);

                for (int i = 0; i < 30; i++)
                    harness.Tick(1f / 60f);

                Assert.That(HasEmotionMicroSubmission(_compositor), Is.True,
                    "With MicroExpressionsEnabled == true and a resolvable curated shape, the controller must submit to the EmotionMicro compositor layer.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void MicroExpressionsEnabled_NoCompositor_DoesNotThrow()
        {
            // Rebuild a rig with no compositor pre-added to exercise the degradation path.
            using EmbodimentTestRig bareRig = EmbodimentTestRig.Create("BareRig");
            bareRig.Root.AddComponent<ConvaiManager>(); // Own the required editor setup for this secondary rig.
            ConvaiCharacter character = bareRig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(bareRig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);

            try
            {
                Assert.DoesNotThrow(() =>
                {
                    harness.ApplyProfile(profile);
                    for (int i = 0; i < 10; i++)
                        harness.Tick(1f / 60f);
                });
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TickMicroExpressions_SpeechEnergyProviderSampleThrows_NeverThrows()
        {
            // Regression net for the "reads .Current only, never calls .Sample()" contract at
            // the controller integration boundary: a provider whose Sample() throws must never
            // surface through TickMicroExpressions, proving the production path never invokes it.
            var throwingProvider = new ThrowingSampleSpeechEnergyProvider { CurrentValue = 0.5f };
            _rig.Context.Provide<ISpeechEnergyProvider>(throwingProvider);

            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);

            try
            {
                Assert.DoesNotThrow(() =>
                {
                    harness.ApplyProfile(profile);
                    harness.Controller.LockEmotion("joy", 1f);

                    for (int i = 0; i < 30; i++)
                        harness.Tick(1f / 60f);
                });
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     <see cref="ISpeechEnergyProvider" /> whose <see cref="Sample" /> throws, so any
        ///     production code path that calls it (instead of reading <see cref="Current" />
        ///     only) fails the test loudly.
        /// </summary>
        private sealed class ThrowingSampleSpeechEnergyProvider : ISpeechEnergyProvider
        {
            public float CurrentValue { get; set; }
            public float Current => CurrentValue;

            public void Sample(float deltaTime) =>
                throw new InvalidOperationException(
                    "Sample() must never be called from TickMicroExpressions; it must read Current only.");
        }

        private static void SetMicroExpressionsEnabled(ConvaiEmotionProfile profile, bool value)
        {
            FieldInfo field = typeof(ConvaiEmotionProfile).GetField(
                "microExpressionsEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ConvaiEmotionProfile must declare a private 'microExpressionsEnabled' field.");
            field.SetValue(profile, value);
        }

        private static bool HasEmotionMicroSubmission(FacialBlendshapeCompositorHost compositor)
        {
            FieldInfo field = typeof(FacialBlendshapeCompositorHost).GetField(
                "_layerFrames", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field);
            var layerFrames = (System.Collections.IDictionary)field.GetValue(compositor);
            if (!layerFrames.Contains(FacialBlendshapeLayers.EmotionMicro)) return false;

            var frame = (System.Collections.ICollection)layerFrames[FacialBlendshapeLayers.EmotionMicro];
            return frame != null && frame.Count > 0;
        }

        private static SkinnedMeshRenderer CreateFaceMesh(Transform parent, params string[] blendshapeNames)
        {
            GameObject go = new("Face");
            go.transform.SetParent(parent);

            Mesh mesh = new();
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            Vector3[] deltaVertices = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaNormals = { Vector3.zero, Vector3.zero, Vector3.zero };
            Vector3[] deltaTangents = { Vector3.zero, Vector3.zero, Vector3.zero };
            foreach (string blendshapeName in blendshapeNames)
                mesh.AddBlendShapeFrame(blendshapeName, 100f, deltaVertices, deltaNormals, deltaTangents);

            SkinnedMeshRenderer renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }
    }
}
