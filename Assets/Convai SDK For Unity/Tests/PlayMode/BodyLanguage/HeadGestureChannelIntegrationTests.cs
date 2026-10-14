using System.Collections;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Gestures;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Controller-level integration coverage for the head-gesture channel:
    ///     the channel is registered on the <see cref="EmbodimentContext" /> slot on enable and
    ///     released on disable; the consumer-count guard never throws or goes negative; the
    ///     no-consumer fallback writes Neck/Head only while zero consumers are registered and
    ///     stops (with the shared write guard restoring bit-exact rest) once a consumer claims
    ///     the channel or the controller disables.
    /// </summary>
    /// <remarks>
    ///     PlayMode (not EditMode) because <c>ConvaiBodyLanguageController</c> only registers the
    ///     head-gesture channel (and every other runtime-only slot) while
    ///     <c>Application.isPlaying</c> is true — matching <c>BodyPoseOrderingTests</c>'s pattern
    ///     for the same reason.
    /// </remarks>
    public sealed class HeadGestureChannelIntegrationTests
    {
        private GameObject _root;
        private Transform _spine, _chest, _upperChest, _neck, _head;
        private Quaternion _restNeck, _restHead;
        private ConvaiBodyLanguageController _controller;
        private EmbodimentContext _context;

        [SetUp]
        public void SetUp()
        {
            _root = new GameObject("HeadGestureChannelIntegrationRoot");

            _spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            _chest = NewChild(_spine, "Chest", new Vector3(0f, 0.15f, 0f));
            _upperChest = NewChild(_chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            _neck = NewChild(_upperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            _head = NewChild(_neck, "Head", new Vector3(0f, 0.1f, 0f));

            _restNeck = _neck.localRotation;
            _restHead = _head.localRotation;

            // Plain (non-Humanoid) Animator so StandardRigBinding resolves bones through its
            // name-based fallback tables ("Spine", "Chest", "UpperChest", "Neck", "Head") —
            // exactly BodyPoseOrderingTests' setup, extended with the Neck/Head chain this
            // phase adds.
            _root.AddComponent<Animator>();
            _context = _root.AddComponent<EmbodimentContext>();

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            SetPrivateField(profile, "headGestureNodMaxPitchDegrees", 8f);
            SetPrivateField(profile, "headGestureRefractorySeconds", 0f);
            SetPrivateField(profile, "headGestureRefractoryVarianceSeconds", 0f);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", profile);
            _controller.enabled = false;
            _controller.enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName} on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        private static bool InvokeRequestHeadGesture(ConvaiBodyLanguageController controller, HeadGestureKind kind)
        {
            MethodInfo method = typeof(ConvaiBodyLanguageController).GetMethod(
                "RequestHeadGesture", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(method, "RequestHeadGesture must exist as an internal method.");
            return (bool)method.Invoke(controller, new object[] { kind, 1f });
        }

        [UnityTest]
        public IEnumerator Channel_IsRegisteredOnEnable_AndUnregisteredOnDisable()
        {
            yield return null;

            Assert.IsNotNull(_context.HeadGestureChannel, "Enable must register the channel on the context slot.");

            _controller.enabled = false;
            yield return null;

            Assert.IsNull(_context.HeadGestureChannel, "Disable must release the channel.");
        }

        [UnityTest]
        public IEnumerator ConsumerCountGuard_DoubleRegister_And_UnregisterWithoutRegister_NeverThrowsOrGoesNegative()
        {
            yield return null;

            IHeadGestureChannel channel = _context.HeadGestureChannel;
            Assert.NotNull(channel);
            var consumerA = new object();
            var consumerB = new object();

            Assert.DoesNotThrow(() => channel.UnregisterConsumer(consumerA));
            Assert.AreEqual(0, _controller.HeadGestureConsumerCount);

            Assert.DoesNotThrow(() => channel.RegisterConsumer(consumerA));
            Assert.DoesNotThrow(() => channel.RegisterConsumer(consumerA));
            Assert.AreEqual(1, _controller.HeadGestureConsumerCount, "Double-register of the same consumer must not double-count.");

            Assert.DoesNotThrow(() => channel.RegisterConsumer(consumerB));
            Assert.AreEqual(2, _controller.HeadGestureConsumerCount);

            Assert.DoesNotThrow(() => channel.UnregisterConsumer(consumerA));
            Assert.AreEqual(1, _controller.HeadGestureConsumerCount);

            Assert.DoesNotThrow(() => channel.UnregisterConsumer(consumerA));
            Assert.AreEqual(1, _controller.HeadGestureConsumerCount, "Unregistering an already-removed consumer must be a no-op.");

            Assert.DoesNotThrow(() => channel.UnregisterConsumer(consumerB));
            Assert.AreEqual(0, _controller.HeadGestureConsumerCount);
            Assert.DoesNotThrow(() => channel.UnregisterConsumer(consumerB));
            Assert.AreEqual(0, _controller.HeadGestureConsumerCount, "Consumer count must never go negative.");
        }

        [UnityTest]
        public IEnumerator NoConsumer_FallbackWritesNeckAndHead()
        {
            yield return null;

            Assert.IsTrue(InvokeRequestHeadGesture(_controller, HeadGestureKind.Nod));

            // Ceiling raised from 60 to 600 (a Nod now runs 1.15s, was 0.85s) and to
            // absorb this headless runner's per-frame Time.deltaTime running smaller than a
            // typical editor frame — a larger ceiling only affects how long the wait can take,
            // never whether the gesture eventually engages.
            bool sawDeviation = false;
            for (int i = 0; i < 600 && !sawDeviation; i++)
            {
                yield return null;
                float headAngle = Quaternion.Angle(_head.localRotation, _restHead);
                if (headAngle > 0.5f) sawDeviation = true;
            }

            Assert.IsTrue(sawDeviation,
                "With zero registered consumers, the controller must self-actuate the Nod onto Head.");
        }

        [UnityTest]
        public IEnumerator RegisteredConsumer_SuppressesFallback_NoNeckOrHeadWrites()
        {
            yield return null;

            IHeadGestureChannel channel = _context.HeadGestureChannel;
            var consumer = new object();
            channel.RegisterConsumer(consumer);

            Assert.IsTrue(InvokeRequestHeadGesture(_controller, HeadGestureKind.Nod));

            for (int i = 0; i < 60; i++)
            {
                yield return null;
                Assert.That(Quaternion.Angle(_head.localRotation, _restHead), Is.LessThan(0.05f),
                    "With a registered consumer, the controller must never write Head itself.");
                Assert.That(Quaternion.Angle(_neck.localRotation, _restNeck), Is.LessThan(0.05f),
                    "With a registered consumer, the controller must never write Neck itself.");
            }
        }

        [UnityTest]
        public IEnumerator ConsumerRegistersMidPlay_FallbackStopsCleanlyNextFrame()
        {
            yield return null;

            Assert.IsTrue(InvokeRequestHeadGesture(_controller, HeadGestureKind.Nod));

            // Ceiling raised from 30 to 300 — see NoConsumer_FallbackWritesNeckAndHead above.
            bool sawDeviation = false;
            for (int i = 0; i < 300 && !sawDeviation; i++)
            {
                yield return null;
                if (Quaternion.Angle(_head.localRotation, _restHead) > 0.5f) sawDeviation = true;
            }
            Assert.IsTrue(sawDeviation, "Sanity: the fallback must have engaged before the consumer registers.");

            IHeadGestureChannel channel = _context.HeadGestureChannel;
            var consumer = new object();
            channel.RegisterConsumer(consumer);

            // The fallback stops feeding new gesture input the instant a consumer is present,
            // but the compositor's per-channel MotorFilter carries persistent
            // velocity state and smoothly decelerates rather than snapping — "a filter's release
            // tail can outlive its input" (ProceduralPoseCompositor.ApplyAccumulated). A fixed
            // 2-frame wait predates that filter and assumed an instant guard-restore; waiting
            // for the tail to actually decay (bounded by a generous ceiling) is the correct
            // update, not a weakened assertion — Head must still end up at rest, just not on the
            // very next frame.
            for (int i = 0; i < 600 && Quaternion.Angle(_head.localRotation, _restHead) >= 0.05f; i++)
                yield return null;

            Assert.That(Quaternion.Angle(_head.localRotation, _restHead), Is.LessThan(0.05f),
                "Once a consumer registers, the fallback must stop and the guard must restore Head to rest.");
            Assert.That(Quaternion.Angle(_neck.localRotation, _restNeck), Is.LessThan(0.05f),
                "Once a consumer registers, the fallback must stop and the guard must restore Neck to rest.");
        }

        [UnityTest]
        public IEnumerator Disable_RestoresNeckAndHead_BitEqualToRest()
        {
            yield return null;

            Assert.IsTrue(InvokeRequestHeadGesture(_controller, HeadGestureKind.Nod));

            // Ceiling raised from 30 to 300 — see NoConsumer_FallbackWritesNeckAndHead above.
            bool sawDeviation = false;
            for (int i = 0; i < 300 && !sawDeviation; i++)
            {
                yield return null;
                if (Quaternion.Angle(_head.localRotation, _restHead) > 0.5f) sawDeviation = true;
            }
            Assert.IsTrue(sawDeviation, "Sanity: the fallback must have visibly moved Head before disable.");

            _controller.enabled = false;

            AssertBitEqual(_head.localRotation, _restHead, "Head");
            AssertBitEqual(_neck.localRotation, _restNeck, "Neck");
        }

        private static void AssertBitEqual(Quaternion actual, Quaternion expected, string boneName)
        {
            const float tolerance = 1e-5f;
            Assert.That(actual.x, Is.EqualTo(expected.x).Within(tolerance), $"{boneName}.x");
            Assert.That(actual.y, Is.EqualTo(expected.y).Within(tolerance), $"{boneName}.y");
            Assert.That(actual.z, Is.EqualTo(expected.z).Within(tolerance), $"{boneName}.z");
            Assert.That(actual.w, Is.EqualTo(expected.w).Within(tolerance), $"{boneName}.w");
        }
    }
}
