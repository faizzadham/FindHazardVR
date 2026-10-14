using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Core;
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
    ///     Controller-level gates for the dialogue-beat reaction hooks in
    ///     <see cref="ConvaiEmotionController" />: driving the Thinking sustained envelope and the
    ///     Reacting/Interrupted one-shot beat accents from an <c>IConversationFlowSource</c> the
    ///     same way <see cref="ConvaiEmotionControllerListeningTests" /> exercises Listening, and
    ///     degrading gracefully when the ConversationFlow module is absent. Follows the same
    ///     rig/log conventions as those tests.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerBeatTests
    {
        private const string CharacterId = "test-char-id";

        private EmbodimentTestRig _rig;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerBeatTests));

            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();
            CreateFaceMesh(_rig.Root.transform, "Brow_Raise_Outer_L");
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
        public void NoConversationFlowSource_MicroExpressionsEnabled_NonzeroBeatStrengths_DoesNotThrow()
        {
            // No IConversationFlowSource registered at all — the controller's dialogue-state read
            // must degrade to Idle without throwing or logging, so Thinking/Reacting/Interrupted
            // never engage.
            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);
            SetPrivateField(profile, "thinkingReactionStrength", 0.8f);
            SetPrivateField(profile, "reactingAccentStrength", 0.8f);
            SetPrivateField(profile, "interruptedFlinchStrength", 0.8f);

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
        public void ConversationFlowSourceRegistered_ThinkingState_FeedsDirectorWithoutThrowing()
        {
            var flowSource = new FakeConversationFlowSource();
            _rig.Context.Provide<IConversationFlowSource>(flowSource);

            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);
            SetPrivateField(profile, "thinkingReactionStrength", 0.6f);

            try
            {
                Assert.DoesNotThrow(() =>
                {
                    harness.ApplyProfile(profile);
                    harness.Controller.LockEmotion("joy", 1f);

                    for (int i = 0; i < 10; i++)
                        harness.Tick(1f / 60f);

                    flowSource.SetState(DialogueState.Thinking);

                    for (int i = 0; i < 60; i++)
                        harness.Tick(1f / 60f);
                });

                MicroExpressionDirector director = GetMicroDirector(harness.Controller);
                Assert.NotNull(director, "Expected the controller to have built a MicroExpressionDirector.");
                Assert.That(GetThinkingEnvelope(director), Is.GreaterThan(0.1f),
                    "Thinking state with a nonzero ThinkingReactionStrength must drive the thinking envelope up.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ConversationFlowSourceRegistered_ReactingTransition_FiresOnceNotEveryTick()
        {
            var flowSource = new FakeConversationFlowSource();
            _rig.Context.Provide<IConversationFlowSource>(flowSource);

            var harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetMicroExpressionsEnabled(profile, true);
            SetPrivateField(profile, "reactingAccentStrength", 0.9f);

            try
            {
                harness.ApplyProfile(profile);
                harness.Controller.LockEmotion("joy", 1f);

                for (int i = 0; i < 5; i++)
                    harness.Tick(1f / 60f);

                MicroExpressionDirector director = GetMicroDirector(harness.Controller);
                Assert.NotNull(director);
                Assert.That(GetBeatEnvelope(director), Is.EqualTo(0f),
                    "Sanity: the beat envelope must be at rest before any Reacting transition.");

                // Transition Idle -> Reacting: the beat accent must fire (envelope rises above 0).
                flowSource.SetState(DialogueState.Reacting);
                harness.Tick(1f / 60f);

                float afterFirstTick = GetBeatEnvelope(director);
                Assert.That(afterFirstTick, Is.GreaterThan(0f),
                    "Transitioning into Reacting must trigger the one-shot beat envelope.");

                // Let the one-shot fully decay while the state PERSISTS as Reacting (no new
                // transition) — since triggers only fire on transition, it must NOT re-trigger,
                // so the envelope must reach exactly 0 and stay there.
                for (int i = 0; i < 400; i++)
                    harness.Tick(1f / 60f);

                Assert.That(GetBeatEnvelope(director), Is.EqualTo(0f),
                    "With the state unchanged (still Reacting, no new transition), the one-shot must have fully decayed and not re-fired.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static void SetMicroExpressionsEnabled(ConvaiEmotionProfile profile, bool value) =>
            SetPrivateField(profile, "microExpressionsEnabled", value);

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"Missing field '{fieldName}' on {target.GetType()}.");
            field.SetValue(target, value);
        }

        private static MicroExpressionDirector GetMicroDirector(ConvaiEmotionController controller)
        {
            FieldInfo field = typeof(ConvaiEmotionController).GetField(
                "_microDirector", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "ConvaiEmotionController must declare a private '_microDirector' field.");
            return (MicroExpressionDirector)field.GetValue(controller);
        }

        private static float GetBeatEnvelope(MicroExpressionDirector director) =>
            GetDirectorPrivateFloat(director, "_beatEnvelope");

        private static float GetThinkingEnvelope(MicroExpressionDirector director) =>
            GetDirectorPrivateFloat(director, "_thinkingEnvelope");

        private static float GetDirectorPrivateFloat(MicroExpressionDirector director, string fieldName)
        {
            FieldInfo field = typeof(MicroExpressionDirector).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"MicroExpressionDirector must declare a private '{fieldName}' field.");
            return (float)field.GetValue(director);
        }

        private static SkinnedMeshRenderer CreateFaceMesh(Transform parent, string blendshapeName)
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
            mesh.AddBlendShapeFrame(blendshapeName, 100f, deltaVertices, deltaNormals, deltaTangents);

            SkinnedMeshRenderer renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }
    }
}
