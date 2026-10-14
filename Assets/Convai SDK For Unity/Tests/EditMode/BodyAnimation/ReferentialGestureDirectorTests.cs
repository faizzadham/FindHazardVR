using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Core.Policy;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Integration coverage for <see cref="ReferentialGestureDirector" />: fires
    ///     at most one gesture per line, respects the per-class cooldown and the global
    ///     refractory, is fully inert without tagged content or with the feature disabled, and
    ///     is suppressed while a peer layer owns the arms or the character isn't Speaking —
    ///     the same rules <see cref="TalkLayerBeatGestureTests" /> exercises for onset beats.
    /// </summary>
    public sealed class ReferentialGestureDirectorTests
    {
        [Test]
        public void InertWithoutTaggedContent_NeverFires()
        {
            var h = Harness.Build(new Dictionary<GestureCueKind, string>(), enableReferential: true);
            try
            {
                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsFalse(fired);
                Assert.That(h.TalkLayer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void FeatureDisabled_NeverFiresEvenWithContent()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: false);
            try
            {
                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsFalse(fired);
                Assert.That(h.TalkLayer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void MatchedSecondPerson_FiresPalmToPlayer()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true);
            try
            {
                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsTrue(fired);
                Assert.AreEqual("palm_open", h.TalkLayer.BeatClipNameForTests);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void NoMatch_NeverFires()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true);
            try
            {
                bool fired = h.Director.TryFireForUtterance("The weather is nice today.", null, now: 0f);

                Assert.IsFalse(fired);
                Assert.That(h.TalkLayer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void NotSpeaking_NeverFires()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true, tickIntoSpeaking: false);
            try
            {
                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsFalse(fired);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void OneGestureMaxPerLine_HighestPriorityClassWins()
        {
            var content = new Dictionary<GestureCueKind, string>
            {
                [GestureCueKind.IndicateObject] = "indicate_painting",
                [GestureCueKind.PalmToPlayer] = "palm_open"
            };
            var h = Harness.Build(content, enableReferential: true);
            try
            {
                var names = new List<string> { "painting" };
                bool fired = h.Director.TryFireForUtterance("Would you look at the painting?", names, now: 0f);

                Assert.IsTrue(fired);
                Assert.AreEqual(
                    "indicate_painting", h.TalkLayer.BeatClipNameForTests,
                    "IndicateObject outranks PalmToPlayer — only one gesture may fire per line.");
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void GlobalRefractory_BlocksAnyClassUntilElapsed()
        {
            var content = new Dictionary<GestureCueKind, string>
            {
                [GestureCueKind.PalmToPlayer] = "palm_open",
                [GestureCueKind.HandToChest] = "hand_chest"
            };
            var h = Harness.Build(content, enableReferential: true, refractorySeconds: 1f, classCooldownSeconds: 30f);
            try
            {
                Assert.IsTrue(h.Director.TryFireForUtterance("Would you like tea?", null, now: 0f));

                // A different class, well inside the global refractory window: still refused.
                bool firedDuringRefractory = h.Director.TryFireForUtterance("I think so.", null, now: 0.5f);
                Assert.IsFalse(firedDuringRefractory);

                // Global refractory has elapsed; HandToChest has never fired, so its own class
                // cooldown cannot be the reason it now succeeds.
                bool firedAfterRefractory = h.Director.TryFireForUtterance("I think so.", null, now: 1.1f);
                Assert.IsTrue(firedAfterRefractory);
                Assert.AreEqual("hand_chest", h.TalkLayer.BeatClipNameForTests);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void ClassCooldown_BlocksSameClassAfterGlobalRefractoryElapses()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true, refractorySeconds: 0.5f, classCooldownSeconds: 3f);
            try
            {
                Assert.IsTrue(h.Director.TryFireForUtterance("Would you like tea?", null, now: 0f));

                // Global refractory (0.5s) has elapsed, but the class cooldown (3s) has not.
                bool firedDuringClassCooldown = h.Director.TryFireForUtterance("Would you like tea?", null, now: 0.6f);
                Assert.IsFalse(firedDuringClassCooldown);

                bool firedAfterClassCooldown = h.Director.TryFireForUtterance("Would you like tea?", null, now: 3.1f);
                Assert.IsTrue(firedAfterClassCooldown);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void SuppressedByActionLayer_NeverFires_AndDoesNotConsumeCooldown()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true);
            try
            {
                var busyEntry = new ActionEntry();
                busyEntry.Initialize("busy", Clip("busy", 0.5f, h.Cleanup), ActionMaskMode.UpperBody);
                h.ActionLayer.Play(busyEntry, default);
                Assert.IsTrue(h.ActionLayer.IsActive);

                bool firedWhileSuppressed = h.Director.TryFireForUtterance("Would you like tea?", null, now: 0f);
                Assert.IsFalse(firedWhileSuppressed);
                Assert.That(h.TalkLayer.BeatWeight, Is.EqualTo(0f).Within(0.0001f));
            }
            finally
            {
                h.Dispose();
            }
        }

        /// <summary>
        ///     A set that authors no clip for the matched cue must not silently drop the gesture:
        ///     the cue is published with <c>authoredPlayed == false</c> so the controller can hand
        ///     it to a peer performer, and the refractory window is consumed exactly as it would
        ///     be by a local performance — otherwise a content-less set would hand one off on
        ///     every single line while an authored set is held to one per window.
        /// </summary>
        /// <remarks>
        ///     Every probe below repeats a second-person line, so every one resolves to the same
        ///     <see cref="GestureCueKind.PalmToPlayer" /> class and both gates apply: the 6s global
        ///     refractory and the 10s per-class cooldown. The final probe therefore has to sit past
        ///     the <b>longer</b> of the two. It previously sat at 6.1s and expected a second
        ///     publish, which asked the class cooldown to not exist — the director was right to
        ///     refuse, and the test was the thing that was wrong.
        /// </remarks>
        [Test]
        public void NoAuthoredContentForTheCue_PublishesAHandOff_AndConsumesTheWindow()
        {
            var h = Harness.Build(
                new Dictionary<GestureCueKind, string>(), enableReferential: true,
                refractorySeconds: 6f, classCooldownSeconds: 10f);
            try
            {
                var resolutions = new List<(GestureCueKind kind, bool played)>();
                h.Director.GestureResolved += (kind, played) => resolutions.Add((kind, played));

                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsFalse(fired, "nothing was played locally — there is no clip for the cue");
                Assert.AreEqual(1, resolutions.Count, "the cue must be published exactly once");
                Assert.AreEqual(GestureCueKind.PalmToPlayer, resolutions[0].kind);
                Assert.IsFalse(resolutions[0].played, "played=false is what triggers the hand-off");

                h.Director.TryFireForUtterance("Would you like more tea?", null, now: 1f);
                Assert.AreEqual(1, resolutions.Count,
                    "the hand-off consumed the refractory window, so the next line must stay silent");

                h.Director.TryFireForUtterance("Would you like more tea?", null, now: 6.1f);
                Assert.AreEqual(1, resolutions.Count,
                    "the global refractory has passed, but this is the same cue class and its " +
                    "10s cooldown has not — a hand-off consumes that too");

                h.Director.TryFireForUtterance("Would you like more tea?", null, now: 10.1f);
                Assert.AreEqual(2, resolutions.Count,
                    "past both the refractory window and the class cooldown it may publish again");
            }
            finally
            {
                h.Dispose();
            }
        }

        /// <summary>
        ///     A peer layer owning the arms is not the same refusal as missing content. The
        ///     gesture must not happen at all — not locally, and not by hand-off either, because a
        ///     procedural performer would put the arms exactly where the running action needs them.
        /// </summary>
        [Test]
        public void SuppressedByPeers_NeverPublishes_SoNoHandOffHappens()
        {
            var h = Harness.Build(new Dictionary<GestureCueKind, string>(), enableReferential: true);
            try
            {
                var resolutions = new List<(GestureCueKind kind, bool played)>();
                h.Director.GestureResolved += (kind, played) => resolutions.Add((kind, played));

                var busyEntry = new ActionEntry();
                busyEntry.Initialize("busy", Clip("busy", 0.5f, h.Cleanup), ActionMaskMode.UpperBody);
                h.ActionLayer.Play(busyEntry, default);
                Assert.IsTrue(h.ActionLayer.SuppressesConversationOverlays);

                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsFalse(fired);
                CollectionAssert.IsEmpty(resolutions,
                    "a suppressed gesture must not be handed to a peer performer either");

                // …and no budget was consumed, so the next line still gets a fair attempt.
                h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0.1f);
                Assert.AreEqual(0, resolutions.Count);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void SuppressedByActionLayer_WithAllowConversationOverlays_StillFires()
        {
            // A full-body Hold action authored with AllowConversationOverlays is a
            // conversation pose (e.g. seated at a desk), not arm ownership — it must not
            // suppress a referential gesture the way an ordinary busy action does.
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };
            var h = Harness.Build(content, enableReferential: true);
            try
            {
                var seatedEntry = new ActionEntry();
                seatedEntry.Initialize(
                    "seated", Clip("seated", 1f, h.Cleanup), ActionMaskMode.FullBody, ActionLoopMode.HoldUntilStopped);
                typeof(ActionEntry)
                    .GetField("_allowConversationOverlays", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(seatedEntry, true);

                h.ActionLayer.Play(seatedEntry, default);
                Assert.IsTrue(h.ActionLayer.IsActive);
                Assert.IsFalse(h.ActionLayer.SuppressesConversationOverlays,
                    "an AllowConversationOverlays action must not report as suppressing overlays");

                bool fired = h.Director.TryFireForUtterance("Would you like some tea?", null, now: 0f);

                Assert.IsTrue(fired, "a seated-conversation hold must not block referential gestures");
                Assert.AreEqual("palm_open", h.TalkLayer.BeatClipNameForTests);
            }
            finally
            {
                h.Dispose();
            }
        }

        /// <summary>
        ///     Regression for the weight-clamping defect: the config accessor already clamps
        ///     <c>ReferentialGestureWeight</c> to its documented 0..1.5 range, so the call site
        ///     must NOT additionally Clamp01 it before multiplying by proximity — that would
        ///     silently discard Inspector values above 1. Under identical near-distance
        ///     proximity damping (default near scale 0.85, below 1), a config weight of 1.5
        ///     must reach further than a config weight of 1.0 — up to the beat overlay's own
        ///     0..1 safety-net clamp.
        /// </summary>
        [Test]
        public void ReferentialGestureWeight_AboveOne_ScalesPastDefault_UpToOverlayCap()
        {
            var content = new Dictionary<GestureCueKind, string> { [GestureCueKind.PalmToPlayer] = "palm_open" };

            float defaultWeight = MeasureFinalBeatWeight(content, referentialGestureWeight: 1f);
            float elevatedWeight = MeasureFinalBeatWeight(content, referentialGestureWeight: 1.5f);

            Assert.That(defaultWeight, Is.EqualTo(0.85f).Within(0.02f),
                "1.0 * nearScale(0.85) should pass through unclamped.");
            Assert.That(elevatedWeight, Is.GreaterThan(defaultWeight + 0.05f),
                "A config weight above 1 must scale further than the default, not be silently discarded.");
            Assert.That(elevatedWeight, Is.EqualTo(1f).Within(0.02f),
                "1.5 * nearScale(0.85) = 1.275, which the overlay's own Clamp01 safety net caps at 1.");
        }

        private static float MeasureFinalBeatWeight(
            Dictionary<GestureCueKind, string> content, float referentialGestureWeight)
        {
            var h = Harness.Build(content, enableReferential: true, referentialGestureWeight: referentialGestureWeight, enableProximity: true);
            try
            {
                Assert.IsTrue(h.TalkLayer.TryPlayReferentialGesture(GestureCueKind.PalmToPlayer, false));

                // Let the beat overlay's fade-in envelope (0.06s) reach full weight, holding the
                // same near-distance anchor Build() converged proximity against.
                for (int i = 0; i < 8; i++) Harness.TickSpeaking(h.TalkLayer, true, new Vector3(1f, 0f, 0f));

                return h.TalkLayer.BeatWeight;
            }
            finally
            {
                h.Dispose();
            }
        }

        // ------------------------------------------------------------------ harness

        private static AnimationClip Clip(string name, float length, List<Object> cleanup)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0f, length, 0f));
            cleanup.Add(clip);
            return clip;
        }

        /// <summary>Owns the graph/layers/config/set for one test and disposes them symmetrically.</summary>
        private sealed class Harness
        {
            public PlayableGraph Graph;
            public TalkLayer TalkLayer;
            public ActionLayer ActionLayer;
            public PointingLayer PointingLayer;
            public ReferentialGestureDirector Director;
            public List<Object> Cleanup;

            public static Harness Build(
                Dictionary<GestureCueKind, string> taggedContent,
                bool enableReferential,
                float refractorySeconds = 6f,
                float classCooldownSeconds = 10f,
                bool tickIntoSpeaking = true,
                float referentialGestureWeight = 1f,
                bool enableProximity = false)
            {
                var cleanup = new List<Object>();
                PlayableGraph graph = PlayableGraph.Create("ReferentialGestureDirectorHarness");

                ConvaiBodyAnimationConfig config = CreateConfig(
                    enableReferential, refractorySeconds, classCooldownSeconds, referentialGestureWeight, enableProximity);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup, taggedContent);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ReferentialGestureDirectorHarness"),
                    RandomSeed = 7
                };

                // TalkLayer no longer resolves Camera.main itself — it reads the
                // conversation anchor off LayerTickContext, so the harness just picks a fixed
                // anchor position instead of spinning up a real Camera GameObject.
                Vector3 conversationAnchor = default;
                if (enableProximity)
                {
                    var rootGo = new GameObject("CharacterRoot");
                    cleanup.Add(rootGo);
                    rootGo.transform.position = Vector3.zero;
                    runtime.CharacterRoot = rootGo.transform;

                    // Well within the default near distance (1.5m) so the near scale (0.85,
                    // below 1) applies fully — the sub-1 factor a config weight above 1 must
                    // scale past to prove it isn't silently clamped to 1 before multiplying.
                    conversationAnchor = new Vector3(1f, 0f, 0f);
                }

                var talkLayer = new TalkLayer();
                talkLayer.Initialize(runtime, LayerPorts.Talk);

                var actionLayer = new ActionLayer();
                actionLayer.Initialize(runtime, LayerPorts.Action);

                var pointingLayer = new PointingLayer();
                pointingLayer.Initialize(runtime, LayerPorts.Pointing);

                if (tickIntoSpeaking)
                {
                    TickSpeaking(talkLayer, enableProximity, conversationAnchor);
                    if (enableProximity)
                    {
                        // Let the proximity smoothing (tau overridden to 0.02s above) converge
                        // to its near-distance steady state before the test fires a gesture.
                        for (int i = 0; i < 10; i++) TickSpeaking(talkLayer, true, conversationAnchor);
                    }
                }

                var director = new ReferentialGestureDirector(config, talkLayer, actionLayer, pointingLayer);

                return new Harness
                {
                    Graph = graph,
                    TalkLayer = talkLayer,
                    ActionLayer = actionLayer,
                    PointingLayer = pointingLayer,
                    Director = director,
                    Cleanup = cleanup
                };
            }

            public static void TickSpeaking(
                TalkLayer talkLayer, bool hasConversationAnchor = false, Vector3 conversationAnchor = default)
            {
                EmotionReading emotion = EmotionReading.Neutral;
                var context = new LayerTickContext(
                    0.05f, DialogueState.Speaking, in emotion, 0.05f, true, false, 1f, false,
                    hasConversationAnchor, conversationAnchor);
                talkLayer.Tick(in context);
            }

            public void Dispose()
            {
                TalkLayer?.Teardown();
                ActionLayer?.Teardown();
                PointingLayer?.Teardown();
                if (Graph.IsValid()) Graph.Destroy();
                foreach (Object obj in Cleanup) Object.DestroyImmediate(obj);
            }
        }

        private static ConvaiBodyAnimationConfig CreateConfig(
            bool enableReferential, float refractorySeconds, float classCooldownSeconds,
            float referentialGestureWeight = 1f, bool enableProximity = false)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_talkFadeInSeconds").floatValue = 0.05f;
            serialized.FindProperty("_talkFadeOutSeconds").floatValue = 0.1f;
            serialized.FindProperty("_useSpeechEnergy").boolValue = false;
            serialized.FindProperty("_talkOverlayWeight").floatValue = 1f;
            serialized.FindProperty("_enableBeatGestures").boolValue = false;
            serialized.FindProperty("_proximityExpressiveness").boolValue = enableProximity;
            serialized.FindProperty("_proximitySmoothingSeconds").floatValue = 0.02f;
            serialized.FindProperty("_enableReferentialGestures").boolValue = enableReferential;
            serialized.FindProperty("_referentialGestureRefractorySeconds").floatValue = refractorySeconds;
            serialized.FindProperty("_referentialGestureClassCooldownSeconds").floatValue = classCooldownSeconds;
            serialized.FindProperty("_referentialGestureWeight").floatValue = referentialGestureWeight;
            serialized.FindProperty("_actionFadeInSeconds").floatValue = 0.1f;
            serialized.FindProperty("_actionFadeOutSeconds").floatValue = 0.1f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSet(
            List<Object> cleanup, Dictionary<GestureCueKind, string> taggedContent)
        {
            var talkClip = new AnimationClip { name = "talk" };
            cleanup.Add(talkClip);
            var talk = new TalkEntry();
            talk.Initialize(talkClip);

            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            var actions = new List<ActionEntry>();
            foreach (KeyValuePair<GestureCueKind, string> pair in taggedContent)
            {
                AnimationClip clip = Clip(pair.Value, 0.5f, cleanup);
                var entry = new ActionEntry();
                entry.Initialize(pair.Value, clip, ActionMaskMode.UpperBody);
                entry.SetCue(pair.Key);
                actions.Add(entry);
            }

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, actions, mask);
            return set;
        }
    }
}
