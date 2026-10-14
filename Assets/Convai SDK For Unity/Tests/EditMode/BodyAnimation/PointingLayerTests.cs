using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyAnimation;
using Convai.Domain.Embodiment.Semantics;
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
    ///     Regression tests for the pointing-layer re-point fix: re-aiming while a live pose is
    ///     still on screen (holding, releasing, or fading out) must always crossfade into the
    ///     new direction, never zero-fade swap (which would teleport the arm to the new clip's
    ///     first frame).
    /// </summary>
    public sealed class PointingLayerTests
    {
        [Test]
        public void RepointDuringRelease_Crossfades()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("PointingLayerRepointReleaseTests");
            var layer = new PointingLayer();
            bool initialized = false;
            var root = new GameObject("root");

            try
            {
                ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithPointingEntries(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("PointingLayerRepointReleaseTests"),
                    RandomSeed = 5,
                    CharacterRoot = root.transform
                };

                layer.Initialize(runtime, LayerPorts.Pointing);
                initialized = true;

                layer.Point(new Vector3(0f, 1.35f, 2f), null, -1f, 1f, -1f, -1f, false);

                for (int i = 0; i < 7; i++)
                    Tick(layer, 0.1f);

                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Holding"));

                layer.Release();
                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Releasing"));

                layer.Point(new Vector3(2f, 1.35f, 0.01f), null, -1f, 1f, -1f, -1f, false);

                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Raising"));
                Assert.That(layer.MixerTransitioningForTests, Is.True,
                    "re-pointing during release must crossfade, not zero-fade swap");
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void RepointDuringFadeOut_Crossfades()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("PointingLayerRepointFadeOutTests");
            var layer = new PointingLayer();
            bool initialized = false;
            var root = new GameObject("root");

            try
            {
                ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithPointingEntries(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("PointingLayerRepointFadeOutTests"),
                    RandomSeed = 11,
                    CharacterRoot = root.transform
                };

                layer.Initialize(runtime, LayerPorts.Pointing);
                initialized = true;

                layer.Point(new Vector3(0f, 1.35f, 2f), null, -1f, 1f, -1f, -1f, false);

                for (int i = 0; i < 7; i++)
                    Tick(layer, 0.1f);

                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Holding"));

                layer.ReleaseImmediate();
                Assert.That(layer.ModeLabelForTests, Is.EqualTo("FadingOut"));

                Tick(layer, 0.01f);
                Assert.That(layer.Weight, Is.GreaterThan(0.1f));

                layer.Point(new Vector3(2f, 1.35f, 0.01f), null, -1f, 1f, -1f, -1f, false);

                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Raising"));
                Assert.That(layer.MixerTransitioningForTests, Is.True,
                    "re-pointing during fade-out must crossfade, not zero-fade swap");
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        /// <summary>
        ///     authored-preference invariant. A set with no pointing entries must never let
        ///     this layer improvise a clip — <see cref="PointingLayer.Point" /> must fail cleanly
        ///     (null handle, no transition into Raising) so the authored-vs-procedural choice
        ///     stays entirely with which action executor a character is wired to, never a silent
        ///     in-layer substitution.
        /// </summary>
        [Test]
        public void Point_WhenSetHasNoPointingEntries_ReturnsNull_AndStaysOff()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("PointingLayerNoContentTests");
            var layer = new PointingLayer();
            bool initialized = false;
            var root = new GameObject("root");

            try
            {
                ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
                cleanup.Add(set);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("PointingLayerNoContentTests"),
                    RandomSeed = 3,
                    CharacterRoot = root.transform
                };

                layer.Initialize(runtime, LayerPorts.Pointing);
                initialized = true;

                BodyAnimationPointingHandle handle =
                    layer.Point(new Vector3(0f, 1.35f, 2f), null, -1f, 1f, -1f, -1f, false);

                Assert.IsNull(handle, "No authored pointing content means no handle — never a silent fallback.");
                Assert.That(layer.ModeLabelForTests, Is.EqualTo("Off"));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                Object.DestroyImmediate(root);
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        private static ConvaiBodyAnimationSet CreateSetWithPointingEntries(List<Object> cleanup)
        {
            var forwardClip = new AnimationClip { name = "point_forward" };
            forwardClip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
            cleanup.Add(forwardClip);

            var rightClip = new AnimationClip { name = "point_right" };
            rightClip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Linear(0f, 0f, 1f, 1f));
            cleanup.Add(rightClip);

            var forwardEntry = new PointingEntry();
            forwardEntry.Initialize(forwardClip, 0f, 0f);

            var rightEntry = new PointingEntry();
            rightEntry.Initialize(rightClip, 90f, 0f);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.Pointing.Add(forwardEntry);
            set.Pointing.Add(rightEntry);
            return set;
        }

        private static void Tick(PointingLayer layer, float deltaTime)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, DialogueState.Idle, in emotion, 0f, false, false);
            layer.Tick(in context);
        }
    }
}
