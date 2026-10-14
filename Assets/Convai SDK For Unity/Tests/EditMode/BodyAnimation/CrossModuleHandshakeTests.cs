using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;
using Convai.Runtime.Components;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for the two Body Animation → Domain-interface cross-module handshakes: the
    ///     point-glance request (through <see cref="IGazeGlanceHandler" />) and the
    ///     exertion-source registration lifecycle (through <see cref="IExertionSource" />).
    ///     Both interfaces are consumed through <see cref="EmbodimentContext" /> — no direct
    ///     Gaze/BodyLanguage assembly reference from Body Animation.
    /// </summary>
    public sealed class CrossModuleHandshakeTests
    {
        private sealed class FakeCoSpeechSource : ICoSpeechPerformanceSource
        {
            public CoSpeechPerformanceReading Current => CoSpeechPerformanceReading.None;
        }
        private sealed class FakeGlanceHandler : IGazeGlanceHandler
        {
            public int CallCount;
            public Vector3 LastPosition;
            public float LastDurationSeconds;

            public void RequestGlance(Vector3 worldPosition, float durationSeconds)
            {
                CallCount++;
                LastPosition = worldPosition;
                LastDurationSeconds = durationSeconds;
            }
        }

        private static ConvaiBodyAnimationController CreateController(out GameObject root, out EmbodimentContext context)
        {
            root = new GameObject("CrossModuleHandshakeTestCharacter");
            // A composition root is only created on a real Convai character now — an embodiment
            // component on an unrelated object reports the mistake instead of half-working.
            root.AddComponent<ConvaiCharacter>();
            var controller = root.AddComponent<ConvaiBodyAnimationController>();
            context = root.GetComponent<EmbodimentContext>();
            Assert.NotNull(context, "ConvaiBodyAnimationController must resolve/create an EmbodimentContext on OnEnable.");
            return controller;
        }

        private static void SetConfig(ConvaiBodyAnimationController controller, ConvaiBodyAnimationConfig config)
        {
            FieldInfo field = typeof(ConvaiBodyAnimationController).GetField(
                "_config", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, "ConvaiBodyAnimationController must have a private _config field.");
            field.SetValue(controller, config);
        }

        // ── point → glance ───────────────────────────────────────────────────

        [Test]
        public void PointAt_WorldPosition_RequestsGlanceOnce_WithConfiguredDuration()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root, out EmbodimentContext context);
            var fake = new FakeGlanceHandler();
            ConvaiBodyAnimationConfig defaults = ConvaiBodyAnimationConfig.CreateDefault();

            try
            {
                context.Provide<IGazeGlanceHandler>(fake);
                var target = new Vector3(1f, 0f, 2f);

                controller.PointAt(target, 1f);

                Assert.AreEqual(1, fake.CallCount, "A single PointAt raise must request exactly one glance.");
                Assert.AreEqual(target, fake.LastPosition);
                Assert.AreEqual(defaults.PointGlanceSeconds, fake.LastDurationSeconds, 1e-4f,
                    "The glance duration must come from ConvaiBodyAnimationConfig.PointGlanceSeconds.");
            }
            finally
            {
                context.Withdraw<IGazeGlanceHandler>(fake);
                Object.DestroyImmediate(defaults);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_Transform_RequestsGlanceAtTargetPosition()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root, out EmbodimentContext context);
            var fake = new FakeGlanceHandler();
            var targetGo = new GameObject("PointTarget") { transform = { position = new Vector3(3f, 1f, -2f) } };

            try
            {
                context.Provide<IGazeGlanceHandler>(fake);

                controller.PointAt(targetGo.transform, 1f);

                Assert.AreEqual(1, fake.CallCount);
                Assert.AreEqual(targetGo.transform.position, fake.LastPosition);
            }
            finally
            {
                context.Withdraw<IGazeGlanceHandler>(fake);
                Object.DestroyImmediate(targetGo);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_NoHandlerRegistered_DoesNotThrow()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root, out EmbodimentContext _);

            try
            {
                Assert.DoesNotThrow(() => controller.PointAt(new Vector3(1f, 0f, 1f), 1f));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void PointAt_EnablePointGlanceOff_SkipsRequest()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root, out EmbodimentContext context);
            var fake = new FakeGlanceHandler();
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();

            try
            {
                var serialized = new SerializedObject(config);
                serialized.FindProperty("_enablePointGlance").boolValue = false;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                SetConfig(controller, config);

                context.Provide<IGazeGlanceHandler>(fake);
                controller.PointAt(new Vector3(1f, 0f, 1f), 1f);

                Assert.AreEqual(0, fake.CallCount, "EnablePointGlance = false must skip the glance request entirely.");
            }
            finally
            {
                context.Withdraw<IGazeGlanceHandler>(fake);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(config);
            }
        }

        // ── exertion-source registration lifecycle ──────────────────────────

        [Test]
        public void ExertionSource_RegistersOnEnable_UnregistersOnDisable()
        {
            ConvaiBodyAnimationController controller = CreateController(out GameObject root, out EmbodimentContext context);

            try
            {
                Assert.AreSame(controller, context.ExertionSource,
                    "ConvaiBodyAnimationController must self-register as IExertionSource on OnEnable (PublishExertion defaults to true).");
                Assert.AreEqual(0f, ((IExertionSource)controller).Exertion01,
                    "Exertion must default to 0 before any locomotion tick has run.");

                controller.enabled = false;

                Assert.IsNull(context.ExertionSource,
                    "OnDisable must unregister the exertion source.");
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CoSpeechSource_ContextRegistration_IsOptionalAndOwnerSafe()
        {
            GameObject root = new("CoSpeechContextTest");
            var context = root.AddComponent<EmbodimentContext>();
            var source = new FakeCoSpeechSource();
            try
            {
                Assert.IsNull(context.CoSpeechPerformanceSource);
                context.Provide<ICoSpeechPerformanceSource>(source);
                Assert.AreSame(source, context.CoSpeechPerformanceSource);
                context.Withdraw<ICoSpeechPerformanceSource>(source);
                Assert.IsNull(context.CoSpeechPerformanceSource);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
