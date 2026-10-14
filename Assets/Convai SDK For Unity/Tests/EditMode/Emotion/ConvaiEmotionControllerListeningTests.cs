using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;
using Convai.Domain.Embodiment.Interfaces;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level gates for the listening-reaction hook in
    ///     <see cref="ConvaiEmotionController" />: reading <c>IConversationFlowSource</c> the same
    ///     way Gaze does, and degrading gracefully (no throw, no log) when the ConversationFlow
    ///     module is entirely absent. Follows the same rig/log conventions as
    ///     <see cref="ConvaiEmotionControllerMicroExpressionTests" />.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerListeningTests
    {
        private const string CharacterId = "test-char-id";

        private EmbodimentTestRig _rig;
        private FacialBlendshapeCompositorHost _compositor;
        private SkinnedMeshRenderer _faceMesh;

        private static readonly MethodInfo ComposeFrame = typeof(FacialBlendshapeCompositorHost).GetMethod(
            "LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic);

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerListeningTests));

            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            _compositor = _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();
            // Supply the CC3 signature as well as the observed brow; unknown rigs are not
            // silently treated as CC3 by the production binding.
            _faceMesh = CreateFaceMesh(_rig.Root.transform,
                "Brow_Raise_Outer_L", "Eye_Blink_L", "Eye_Blink_R", "V_Open");
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
        public void NoConversationFlowSource_MicroExpressionsEnabled_NonzeroListeningStrength_DoesNotThrow()
        {
            // No IConversationFlowSource is registered on the rig's EmbodimentContext at all
            // (the ConversationFlow module is entirely absent from this test rig) — the
            // controller's `Context?.ConversationFlowSource?.Current.Primary ?? DialogueState.Idle`
            // read must degrade to "never listening" without throwing or logging.
            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);
            SetListeningReactionStrength(profile, 0.8f);

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

        [Test]
        public void ConversationFlowSourceRegistered_ListeningState_RaisesAndReleasesBrowOutput()
        {
            var flowSource = new FakeConversationFlowSource();
            _rig.Context.Provide<IConversationFlowSource>(flowSource);

            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);
            SetListeningReactionStrength(profile, 0.6f);
            typeof(ConvaiEmotionProfile).GetField("microExpressionAmplitude",
                BindingFlags.Instance | BindingFlags.NonPublic).SetValue(profile, 0f);

            try
            {
                harness.ApplyProfile(profile);
                harness.Tick(1f / 60f);
                ComposeFrame.Invoke(_compositor, null);
                Assert.That(_faceMesh.GetBlendShapeWeight(0), Is.Zero);

                flowSource.SetState(DialogueState.Listening);
                for (int i = 0; i < 30; i++) harness.Tick(1f / 60f);
                ComposeFrame.Invoke(_compositor, null);
                float listeningWeight = _faceMesh.GetBlendShapeWeight(0);
                Assert.That(listeningWeight, Is.GreaterThan(0f),
                    "The controller must feed the registered listening state into facial output.");

                flowSource.SetState(DialogueState.Idle);
                for (int i = 0; i < 120; i++) harness.Tick(1f / 60f);
                ComposeFrame.Invoke(_compositor, null);
                Assert.That(_faceMesh.GetBlendShapeWeight(0),
                    Is.LessThan(listeningWeight), "Leaving Listening must release the reaction.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static void SetMicroExpressionsEnabled(ConvaiEmotionProfile profile, bool value)
        {
            FieldInfo field = typeof(ConvaiEmotionProfile).GetField(
                "microExpressionsEnabled", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ConvaiEmotionProfile must declare a private 'microExpressionsEnabled' field.");
            field.SetValue(profile, value);
        }

        private static void SetListeningReactionStrength(ConvaiEmotionProfile profile, float value)
        {
            FieldInfo field = typeof(ConvaiEmotionProfile).GetField(
                "listeningReactionStrength", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ConvaiEmotionProfile must declare a private 'listeningReactionStrength' field.");
            field.SetValue(profile, value);
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
