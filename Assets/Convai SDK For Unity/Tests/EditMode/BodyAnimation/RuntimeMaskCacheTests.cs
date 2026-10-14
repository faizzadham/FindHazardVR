using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Ref-count lifecycle of <see cref="RuntimeMaskCache" />, including the set-handoff
    ///     case (two live layers holding the same shared mask, the retiring one torn down
    ///     after the replacement is built) that the R-3b fix addressed for mixers.
    /// </summary>
    internal sealed class RuntimeMaskCacheTests
    {
        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                if (obj != null)
                    Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        // ------------------------------------------------------------------ full body / arms

        [Test]
        public void AcquireFullBody_MultipleCallers_ShareOneInstance()
        {
            AvatarMask first = RuntimeMaskCache.AcquireFullBody();
            AvatarMask second = RuntimeMaskCache.AcquireFullBody();

            try
            {
                Assert.AreSame(first, second, "full-body mask must be shared across acquirers");
            }
            finally
            {
                RuntimeMaskCache.Release(first);
                RuntimeMaskCache.Release(second);
            }
        }

        [Test]
        public void AcquireArms_MultipleCallers_ShareOneInstance()
        {
            AvatarMask first = RuntimeMaskCache.AcquireArms();
            AvatarMask second = RuntimeMaskCache.AcquireArms();

            try
            {
                Assert.AreSame(first, second, "arms mask must be shared across acquirers");
            }
            finally
            {
                RuntimeMaskCache.Release(first);
                RuntimeMaskCache.Release(second);
            }
        }

        [Test]
        public void Release_DropsToZero_DestroysMask()
        {
            AvatarMask first = RuntimeMaskCache.AcquireFullBody();
            AvatarMask second = RuntimeMaskCache.AcquireFullBody();

            RuntimeMaskCache.Release(first);
            Assert.IsNotNull(second, "one live reference remains — must not be destroyed yet");

            RuntimeMaskCache.Release(second);
            Assert.IsTrue(second == null, "last reference released — the shared mask must be destroyed");
        }

        [Test]
        public void Release_ReacquireAfterFullRelease_BuildsAFreshInstance()
        {
            AvatarMask first = RuntimeMaskCache.AcquireFullBody();
            RuntimeMaskCache.Release(first);

            AvatarMask second = RuntimeMaskCache.AcquireFullBody();
            try
            {
                Assert.IsFalse(ReferenceEqualsUnityAware(first, second),
                    "after the last release destroys the shared instance, the next acquire must build a new one");
            }
            finally
            {
                RuntimeMaskCache.Release(second);
            }
        }

        // ------------------------------------------------------------------ talk upper-body (keyed on source)

        [Test]
        public void AcquireTalkUpperBody_NullSource_ReturnsNull()
        {
            Assert.IsNull(RuntimeMaskCache.AcquireTalkUpperBody(null));
        }

        [Test]
        public void AcquireTalkUpperBody_SameSource_SharesInstance_DifferentSourceDoesNot()
        {
            var sourceA = new AvatarMask { name = "source-a" };
            var sourceB = new AvatarMask { name = "source-b" };
            _cleanup.Add(sourceA);
            _cleanup.Add(sourceB);

            AvatarMask derivedA1 = RuntimeMaskCache.AcquireTalkUpperBody(sourceA);
            AvatarMask derivedA2 = RuntimeMaskCache.AcquireTalkUpperBody(sourceA);
            AvatarMask derivedB = RuntimeMaskCache.AcquireTalkUpperBody(sourceB);

            try
            {
                Assert.AreSame(derivedA1, derivedA2, "same source instance must share the derived mask");
                Assert.AreNotSame(derivedA1, derivedB, "different source instances must not share a derived mask");
                Assert.IsFalse(derivedA1.GetHumanoidBodyPartActive(AvatarMaskBodyPart.Body),
                    "derived talk mask must have the Body part disabled");
            }
            finally
            {
                RuntimeMaskCache.Release(derivedA1);
                RuntimeMaskCache.Release(derivedA2);
                RuntimeMaskCache.Release(derivedB);
            }
        }

        // ------------------------------------------------------------------ handoff safety (R-3b class of bug)

        [Test]
        public void Handoff_RetiringLayerTeardownAfterReplacementBuilt_DoesNotDestroyLiveMask()
        {
            // Mirrors an animation-set handoff: two TalkLayer instances share the same set
            // (same UpperBodyMask source), the new one is built while the old one is still
            // alive, and the old one is torn down after. The live (new) layer's mask must
            // survive the retiring layer's Teardown.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("RuntimeMaskCacheHandoffTests");
            var oldLayer = new TalkLayer();
            var newLayer = new TalkLayer();
            bool oldInitialized = false, newInitialized = false;

            try
            {
                ConvaiBodyAnimationSet set = CreateSetWithUpperBodyMask(cleanup);
                ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
                cleanup.Add(config);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("RuntimeMaskCacheHandoffTests"),
                    RandomSeed = 5
                };

                oldLayer.Initialize(runtime, LayerPorts.Talk);
                oldInitialized = true;

                // New replacement built while the old one is still alive — both hold a
                // reference into the same cache entry (refcount 2). It gets its OWN
                // LayerMixerHost because that is what TryBeginSetHandoff does: the incoming stack
                // is built on a fresh mixer and crossfaded in at the graph root. Re-using the
                // outgoing stack's mixer would connect two sources to one already-connected port
                // and corrupt the graph topology — a fault of the harness, not of the cache.
                LayerRuntime incomingRuntime = CloneRuntimeWithFreshMixer(runtime, graph);
                newLayer.Initialize(incomingRuntime, LayerPorts.Talk);
                newInitialized = true;

                AvatarMask liveMask = newLayer.RuntimeTalkMaskForTests;
                Assert.IsNotNull(liveMask);
                Assert.AreSame(oldLayer.RuntimeTalkMaskForTests, liveMask,
                    "sanity: both layers share the same cache entry before teardown");

                // Retiring layer torn down after the replacement is built and in use.
                oldLayer.Teardown();
                oldInitialized = false;

                Assert.IsTrue(liveMask != null,
                    "the retiring layer's Teardown must not destroy a mask the live layer still holds");
                Assert.AreSame(liveMask, newLayer.RuntimeTalkMaskForTests);
            }
            finally
            {
                if (oldInitialized) oldLayer.Teardown();
                if (newInitialized) newLayer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup)
                    if (obj != null)
                        Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Handoff_BothLayersTornDown_MaskEventuallyDestroyed()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("RuntimeMaskCacheHandoffFinalReleaseTests");
            var oldLayer = new TalkLayer();
            var newLayer = new TalkLayer();

            try
            {
                ConvaiBodyAnimationSet set = CreateSetWithUpperBodyMask(cleanup);
                ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
                cleanup.Add(config);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("RuntimeMaskCacheHandoffFinalReleaseTests"),
                    RandomSeed = 6
                };

                oldLayer.Initialize(runtime, LayerPorts.Talk);
                // Own mixer, as in a real handoff — see the sibling test for why.
                newLayer.Initialize(CloneRuntimeWithFreshMixer(runtime, graph), LayerPorts.Talk);
                AvatarMask liveMask = newLayer.RuntimeTalkMaskForTests;

                oldLayer.Teardown();
                newLayer.Teardown();

                Assert.IsTrue(liveMask == null,
                    "once every holder releases, the shared mask must actually be destroyed (no leak)");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup)
                    if (obj != null)
                        Object.DestroyImmediate(obj);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        ///     The incoming half of a set handoff: same graph, same content, but its own
        ///     <see cref="LayerMixerHost" />. <c>TryBeginSetHandoff</c> builds the incoming stack on
        ///     a fresh mixer and crossfades it in at the graph root, so a test that reuses the
        ///     outgoing mixer would connect two sources to one port and assert on a corrupted
        ///     graph rather than on the cache behaviour it is actually testing.
        /// </summary>
        private static LayerRuntime CloneRuntimeWithFreshMixer(LayerRuntime source, PlayableGraph graph) =>
            new()
            {
                Graph = graph,
                Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                Set = source.Set,
                Config = source.Config,
                Trace = source.Trace,
                RandomSeed = source.RandomSeed
            };

        private static ConvaiBodyAnimationSet CreateSetWithUpperBodyMask(List<Object> cleanup)
        {
            var mask = new AvatarMask { name = "set-upper-body" };
            cleanup.Add(mask);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, null, null, mask);
            return set;
        }

        // Avoids Unity's fake-null `==` override so a destroyed-but-not-yet-collected wrapper
        // is not mistaken for "same instance" as a freshly built one.
        private static bool ReferenceEqualsUnityAware(object a, object b) => ReferenceEquals(a, b);
    }
}
