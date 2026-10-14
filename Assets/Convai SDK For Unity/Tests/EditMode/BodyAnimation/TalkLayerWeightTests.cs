using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class TalkLayerWeightTests
    {
        /// <summary>
        ///     A config nobody configured caps the talk overlay where Convai's own settings cap it,
        ///     and unmetered talk reaches that cap rather than something below it.
        /// </summary>
        /// <remarks>
        ///     The cap used to be 1 here and 0.45 on every shipped asset, which made a hand-made
        ///     config gesture roughly twice as hard as any Convai sample. Reading the number off the
        ///     field would let the two drift apart again silently, so
        ///     <see cref="BodyAnimationShippedConfigGuardTests" /> owns that comparison; this test
        ///     owns the part it can see on its own — that nothing between the field and
        ///     <c>ResolveTalkLayerWeightScale</c> quietly scales the cap away.
        /// </remarks>
        [Test]
        public void DefaultConfig_UnmeteredTalk_ReachesTheOverlayCap()
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();

            try
            {
                Assert.That(config.TalkOverlayWeight, Is.EqualTo(0.45f).Within(0.0001f));
                Assert.That(config.TalkReleaseDelaySeconds, Is.EqualTo(0.16f).Within(0.0001f));
                Assert.That(config.ResolveTalkLayerWeightScale(false, 0f),
                    Is.EqualTo(config.TalkOverlayWeight).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void SpeechEnergyPresent_InterpolatesBetweenLowEnergyAndOverlayCap()
        {
            ConvaiBodyAnimationConfig config = CreateConfig(0.4f, 0.7f, useSpeechEnergy: true);

            try
            {
                Assert.That(config.ResolveTalkLayerWeightScale(true, 0f), Is.EqualTo(0.4f).Within(0.0001f));
                Assert.That(config.ResolveTalkLayerWeightScale(true, 0.5f), Is.EqualTo(0.55f).Within(0.0001f));
                Assert.That(config.ResolveTalkLayerWeightScale(true, 1f), Is.EqualTo(0.7f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void LowEnergyWeightAboveOverlayCap_NeverExceedsOverlayCap()
        {
            ConvaiBodyAnimationConfig config = CreateConfig(0.9f, 0.6f, useSpeechEnergy: true);

            try
            {
                Assert.That(config.ResolveTalkLayerWeightScale(true, 0f), Is.EqualTo(0.6f).Within(0.0001f));
                Assert.That(config.ResolveTalkLayerWeightScale(true, 1f), Is.EqualTo(0.6f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void SpeechEnergyDisabled_UsesOverlayCap()
        {
            ConvaiBodyAnimationConfig config = CreateConfig(0.2f, 0.8f, useSpeechEnergy: false);

            try
            {
                Assert.That(config.ResolveTalkLayerWeightScale(true, 0f), Is.EqualTo(0.8f).Within(0.0001f));
                Assert.That(config.ResolveTalkLayerWeightScale(true, 1f), Is.EqualTo(0.8f).Within(0.0001f));
            }
            finally
            {
                Object.DestroyImmediate(config);
            }
        }

        [Test]
        public void TalkLayer_SpeechStops_HoldsReleaseBeforeFadingOut()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerReleaseTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    lowEnergyWeight: 1f,
                    overlayWeight: 1f,
                    useSpeechEnergy: false,
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.5f,
                    releaseDelaySeconds: 0.25f);
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithTalkClip(cleanup);
                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerReleaseTests"),
                    RandomSeed = 17
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                Tick(layer, 0.1f, DialogueState.Speaking);
                Assert.That(layer.StateLabel, Is.EqualTo("Talking"));
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                Tick(layer, 0.1f, DialogueState.Idle);
                Assert.That(layer.StateLabel, Is.EqualTo("ReleaseHold"));
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));
                Assert.That(layer.ActivePlaybackSpeedForTests, Is.EqualTo(0.2f).Within(0.0001f),
                    "A talk clip must decelerate as soon as speech ends so a late arm stroke cannot keep rising.");

                Tick(layer, 0.1f, DialogueState.Idle);
                Assert.That(layer.StateLabel, Is.EqualTo("ReleaseHold"));
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                Tick(layer, 0.2f, DialogueState.Idle);
                Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));
                Assert.That(layer.Weight, Is.LessThan(0.95f));
                Assert.That(layer.IsReleaseDeceleratingForTests, Is.True,
                    "Without an authored outro, fade-out must decelerate the live clip toward rest " +
                    "instead of freezing it outright.");
                Assert.That(layer.ActivePlaybackSpeedForTests, Is.LessThan(0.2f).And.GreaterThan(0f),
                    "The release-delay speed must ease further down toward 0, not jump straight to it.");
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void TalkLayer_MovingSuppression_BypassesReleaseHold()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerMoveSuppressionTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    lowEnergyWeight: 1f,
                    overlayWeight: 1f,
                    useSpeechEnergy: false,
                    fadeInSeconds: 0.1f,
                    fadeOutSeconds: 0.5f,
                    releaseDelaySeconds: 0.25f,
                    movingTalkMode: 2);
                cleanup.Add(config);

                ConvaiBodyAnimationSet set = CreateSetWithTalkClip(cleanup);
                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerMoveSuppressionTests"),
                    RandomSeed = 23
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                Tick(layer, 0.1f, DialogueState.Speaking);
                Tick(layer, 0.1f, DialogueState.Speaking, isMoving: true);

                Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));
                Assert.That(layer.Weight, Is.LessThan(0.95f));
            }
            finally
            {
                if (initialized)
                    layer.Teardown();
                if (graph.IsValid())
                    graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        private static ConvaiBodyAnimationConfig CreateConfig(
            float lowEnergyWeight,
            float overlayWeight,
            bool useSpeechEnergy,
            float fadeInSeconds = 0.5f,
            float fadeOutSeconds = 0.65f,
            float releaseDelaySeconds = 0f,
            int movingTalkMode = 0)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = fadeInSeconds;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = fadeOutSeconds;
            serialized.FindProperty("_talkReleaseDelaySeconds").floatValue = releaseDelaySeconds;
            serialized.FindProperty("_movingTalkMode").intValue = movingTalkMode;
            serialized.FindProperty("_useSpeechEnergy").boolValue = useSpeechEnergy;
            serialized.FindProperty("_talkWeightAtLowEnergy").floatValue = lowEnergyWeight;
            serialized.FindProperty("_talkOverlayWeight").floatValue = overlayWeight;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSetWithTalkClip(List<Object> cleanup)
        {
            var clip = new AnimationClip { name = "talk" };
            cleanup.Add(clip);

            var talk = new TalkEntry();
            talk.Initialize(clip);

            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, mask);
            return set;
        }

        private static void Tick(TalkLayer layer, float deltaTime, DialogueState state, bool isMoving = false)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, state, in emotion, 1f, false, isMoving);
            layer.Tick(in context);
        }

        [Test]
        public void ConversationalIntensityScale_VisiblyScalesTalkOverlayWeight()
        {
            // A peer's emotion-derived intensity report visibly scales the talk
            // overlay (reported -> clamped by the reporting side -> smoothed here into Weight).
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("TalkLayerIntensityScaleTests");
            var layer = new TalkLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig(
                    lowEnergyWeight: 1f, overlayWeight: 1f, useSpeechEnergy: false, fadeInSeconds: 0.05f);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSetWithTalkClip(cleanup);
                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("TalkLayerIntensityScaleTests"),
                    RandomSeed = 31
                };

                layer.Initialize(runtime, LayerPorts.Talk);
                initialized = true;

                // Converge fully at neutral intensity (1f, the LayerTickContext default) first.
                for (int i = 0; i < 100; i++)
                    TickWithIntensity(layer, 0.05f, DialogueState.Speaking, 1f);
                float neutralWeight = layer.Weight;
                Assert.That(neutralWeight, Is.GreaterThan(0.9f), "Sanity: fully converged at neutral intensity.");

                // Now report a reduced intensity every tick (as the controller would) and let the
                // 0.5s time-constant smoothing settle.
                for (int i = 0; i < 100; i++)
                    TickWithIntensity(layer, 0.05f, DialogueState.Speaking, 0.7f);

                Assert.That(layer.ReportedIntensityScaleForTests, Is.EqualTo(0.7f).Within(0.02f));
                Assert.That(layer.Weight, Is.LessThan(neutralWeight * 0.75f),
                    "A reduced reported intensity must visibly scale the converged talk-overlay weight down.");
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static void TickWithIntensity(
            TalkLayer layer, float deltaTime, DialogueState state, float conversationalIntensityScale)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(
                deltaTime, state, in emotion, 1f, false, false, conversationalIntensityScale);
            layer.Tick(in context);
        }
    }
}
