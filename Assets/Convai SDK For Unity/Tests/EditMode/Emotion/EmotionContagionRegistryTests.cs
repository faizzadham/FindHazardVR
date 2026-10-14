using System.Collections.Generic;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Core;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Registry lifecycle tests for <see cref="EmotionContagionRegistry" />: register/
    ///     unregister round-trip, double-register idempotency, <see cref="EmotionContagionRegistry.Clear" />,
    ///     and destroyed-entry iteration safety. Mirrors
    ///     <c>Convai.Modules.Gaze.Providers.ConvaiCharacterGazeRegistry</c>'s own test conventions.
    ///     Exercises the registry API directly with manually-built entries rather than the full
    ///     <see cref="ConvaiEmotionController" /> pipeline — the controller's OWN
    ///     register-on-enable/unregister-on-disable wiring is covered by the two-rig
    ///     component-level contagion tests.
    /// </summary>
    [TestFixture]
    public sealed class EmotionContagionRegistryTests
    {
        private readonly List<EmbodimentTestRig> _rigs = new();

        [SetUp]
        public void SetUp()
        {
            EmotionContagionRegistry.Clear();
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _rigs.Count - 1; i >= 0; i--)
                _rigs[i].Dispose();
            _rigs.Clear();
            EmotionContagionRegistry.Clear();
        }

        /// <summary>
        ///     Builds a real controller on a real character host, then disables it, leaving an inert
        ///     instance to use as a typed identity for the hand-built entries these tests register.
        /// </summary>
        /// <remarks>
        ///     The host is an <see cref="EmbodimentTestRig" /> — the same composition root every other
        ///     embodiment component test uses — so the controller resolves its context and enables
        ///     cleanly. Dropping it on a bare <see cref="UnityEngine.GameObject" /> instead would make
        ///     it report a genuine setup mistake ("not on a Convai character") that the fixture would
        ///     then have to suppress. Disabling afterwards unregisters the entry the controller added
        ///     for itself on enable, so the registry is empty again when the test starts.
        /// </remarks>
        private ConvaiEmotionController NewDisabledController(string name)
        {
            EmbodimentTestRig rig = EmbodimentTestRig.Create(name);
            _rigs.Add(rig);
            ConvaiEmotionController controller = rig.AddComponent<ConvaiEmotionController>();
            controller.enabled = false;
            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(0),
                "A disabled controller must have unregistered itself, so these tests start from an " +
                "empty registry.");
            return controller;
        }

        [Test]
        public void Register_Unregister_RoundTrip()
        {
            ConvaiEmotionController controller = NewDisabledController("A");
            var entry = new EmotionContagionRegistry.Entry { Root = controller.transform, Controller = controller };

            EmotionContagionRegistry.Register(entry);
            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(1));

            EmotionContagionRegistry.Unregister(entry);
            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(0));
        }

        [Test]
        public void DoubleRegister_AddsOnce()
        {
            ConvaiEmotionController controller = NewDisabledController("A");
            var entry = new EmotionContagionRegistry.Entry { Root = controller.transform, Controller = controller };

            EmotionContagionRegistry.Register(entry);
            EmotionContagionRegistry.Register(entry);

            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(1),
                "Registering the same entry twice must add it only once.");
        }

        [Test]
        public void Clear_EmptiesRegistry()
        {
            ConvaiEmotionController a = NewDisabledController("A");
            ConvaiEmotionController b = NewDisabledController("B");
            EmotionContagionRegistry.Register(new EmotionContagionRegistry.Entry { Root = a.transform, Controller = a });
            EmotionContagionRegistry.Register(new EmotionContagionRegistry.Entry { Root = b.transform, Controller = b });
            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(2));

            EmotionContagionRegistry.Clear();

            Assert.That(EmotionContagionRegistry.All.Count, Is.EqualTo(0));
        }

        [Test]
        public void DestroyedControllerEntry_IteratesSafely_WithoutThrowing()
        {
            ConvaiEmotionController controller = NewDisabledController("Destroyed");
            var entry = new EmotionContagionRegistry.Entry { Root = controller.transform, Controller = controller };
            EmotionContagionRegistry.Register(entry);

            // TearDown's Dispose tolerates an already-destroyed root, so the rig stays in the list.
            Object.DestroyImmediate(controller.gameObject);

            Assert.DoesNotThrow(() =>
            {
                IReadOnlyList<EmotionContagionRegistry.Entry> all = EmotionContagionRegistry.All;
                for (int i = 0; i < all.Count; i++)
                {
                    EmotionContagionRegistry.Entry e = all[i];
                    Assert.That(e.Controller == null, Is.True,
                        "A destroyed controller must fake-null via Unity's overridden == operator, never throw.");
                }
            });
        }
    }
}
