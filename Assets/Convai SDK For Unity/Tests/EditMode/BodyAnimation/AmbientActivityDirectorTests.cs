using System.Collections.Generic;
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
    ///     Coverage for <see cref="AmbientActivityDirector" />: the
    ///     delay → cadence → fire lifecycle, deterministic seeded selection with no immediate
    ///     repeat, the graceful <c>RequestStop</c> wind-down when the player engages, every fire
    ///     gate (peer action busy, moving, player proximity), and full inertness without tagged
    ///     content or with the feature disabled.
    /// </summary>
    public sealed class AmbientActivityDirectorTests
    {
        private const float StartDelaySeconds = 3f;   // clamp floor — the fastest the config allows
        private const float IntervalSeconds = 5f;      // clamp floor — the fastest the config allows

        [Test]
        public void BelowStartDelay_NeverArms()
        {
            Harness h = Harness.Build();
            try
            {
                // 2.5s of Idle — under the 3s start delay.
                for (int i = 0; i < 5; i++)
                    h.Tick(DialogueState.Idle, 0.5f, locomotionMoving: false, playActionAtRunnerActive: false);

                Assert.IsFalse(h.Director.IsRunningAmbientActivityForTests);
                Assert.IsFalse(h.ActionLayer.IsActive);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void PastDelayAndCadence_EventuallyFires()
        {
            Harness h = Harness.Build();
            try
            {
                bool fired = TickUntilFired(h, out _);
                Assert.IsTrue(fired, "an ambient activity never fired within the tick budget");
                Assert.IsTrue(h.ActionLayer.IsActive);
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void CadenceRoll_DeterministicAndWithinJitterBounds()
        {
            Harness a = Harness.Build(randomSeed: 555);
            Harness b = Harness.Build(randomSeed: 555);
            try
            {
                Assert.IsTrue(TickUntilFired(a, out float elapsedA, maxTicks: 400));
                Assert.IsTrue(TickUntilFired(b, out float elapsedB, maxTicks: 400));

                Assert.That(elapsedA, Is.EqualTo(elapsedB).Within(0.001f),
                    "the same seed must produce the same time-to-first-fire");

                float meanWindowEnd = StartDelaySeconds + IntervalSeconds * 1.4f + 0.5f; // +0.5s tick-granularity slack
                Assert.That(elapsedA, Is.LessThanOrEqualTo(meanWindowEnd),
                    "cadence jitter must stay within +/-40% of the mean interval");
            }
            finally
            {
                a.Dispose();
                b.Dispose();
            }
        }

        [Test]
        public void NoImmediateRepeat_WithMultipleEntries()
        {
            Harness h = Harness.Build(ambientEntryCount: 2, playOnce: true);
            try
            {
                var fired = new List<string>();
                string lastName = null;

                for (int round = 0; round < 4; round++)
                {
                    bool sawFire = false;
                    for (int i = 0; i < 400 && !sawFire; i++)
                    {
                        bool wasRunning = h.Director.IsRunningAmbientActivityForTests;
                        h.Tick(DialogueState.Idle, 0.05f, locomotionMoving: false, playActionAtRunnerActive: false);
                        if (!wasRunning && h.Director.IsRunningAmbientActivityForTests)
                        {
                            string name = h.ActionLayer.ActiveActionName;
                            fired.Add(name);
                            sawFire = true;

                            if (lastName != null)
                                Assert.AreNotEqual(lastName, name, "must not repeat the same ambient entry back-to-back");
                            lastName = name;
                        }
                    }

                    Assert.IsTrue(sawFire, $"round {round} never fired within budget");

                    // Let the (PlayOnce, short) clip finish and free the layer before the next round.
                    for (int i = 0; i < 200 && h.Director.IsRunningAmbientActivityForTests; i++)
                        h.Tick(DialogueState.Idle, 0.05f, locomotionMoving: false, playActionAtRunnerActive: false);
                }

                Assert.That(fired.Count, Is.EqualTo(4));
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void DialogueStateLeavesIdle_RequestsGracefulStop()
        {
            Harness h = Harness.Build();
            try
            {
                Assert.IsTrue(TickUntilFired(h, out _));
                string activeName = h.ActionLayer.ActiveActionName;
                Assert.IsNotEmpty(activeName);

                h.Tick(DialogueState.Speaking, 0.05f, locomotionMoving: false, playActionAtRunnerActive: false);

                Assert.IsFalse(h.Director.IsRunningAmbientActivityForTests);
                Assert.AreEqual(activeName, h.Director.LastStoppedActionNameForTests,
                    "the director must have requested a stop on the ambient action it started");

                // The layer itself must be gracefully winding down (RequestStop), not snapped off.
                bool reachedFadingOut = false;
                for (int i = 0; i < 20 && !reachedFadingOut; i++)
                {
                    h.Tick(DialogueState.Speaking, 0.02f, locomotionMoving: false, playActionAtRunnerActive: false);
                    if (h.ActionLayer.StateLabel == "FadingOut") reachedFadingOut = true;
                }
                Assert.IsTrue(reachedFadingOut, "the ambient hold must fade out via RequestStop, not vanish instantly");
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void PlayerNear_SuppressesFiring()
        {
            Harness h = Harness.Build(withCamera: true, cameraDistance: 1f, suppressDistance: 4f);
            try
            {
                Assert.IsFalse(TickUntilFired(h, out _, maxTicks: 400),
                    "a nearby player must suppress ambient activities");
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void PlayerFar_AllowsFiring()
        {
            Harness h = Harness.Build(withCamera: true, cameraDistance: 20f, suppressDistance: 4f);
            try
            {
                Assert.IsTrue(TickUntilFired(h, out _),
                    "a far-away player must not suppress ambient activities");
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void PeerActionBusy_SuppressesFiring()
        {
            Harness h = Harness.Build();
            try
            {
                var busyEntry = new ActionEntry();
                busyEntry.Initialize(
                    "busy", LoopingClip("busyClip", 1f, h.Cleanup), ActionMaskMode.UpperBody, ActionLoopMode.HoldUntilStopped);
                h.ActionLayer.Play(busyEntry, default);

                for (int i = 0; i < 400; i++)
                {
                    h.Tick(DialogueState.Idle, 0.05f, locomotionMoving: false, playActionAtRunnerActive: false);
                    Assert.IsFalse(h.Director.IsRunningAmbientActivityForTests,
                        "ambient must never fire while the action layer is already busy");
                }
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void Locomotion_Moving_SuppressesFiring()
        {
            Harness h = Harness.Build();
            try
            {
                for (int i = 0; i < 400; i++)
                {
                    h.Tick(DialogueState.Idle, 0.05f, locomotionMoving: true, playActionAtRunnerActive: false);
                    Assert.IsFalse(h.Director.IsRunningAmbientActivityForTests,
                        "ambient must never fire while locomotion is moving");
                }
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void NoAmbientTaggedContent_NeverFires()
        {
            Harness h = Harness.Build(ambientEntryCount: 0);
            try
            {
                Assert.IsFalse(TickUntilFired(h, out _, maxTicks: 400));
            }
            finally
            {
                h.Dispose();
            }
        }

        [Test]
        public void FeatureDisabled_NeverFiresEvenWithContent()
        {
            Harness h = Harness.Build(enableAmbient: false);
            try
            {
                Assert.IsFalse(TickUntilFired(h, out _, maxTicks: 400));
            }
            finally
            {
                h.Dispose();
            }
        }

        // ------------------------------------------------------------------ helpers

        private static bool TickUntilFired(Harness h, out float idleElapsedAtFire, int maxTicks = 300)
        {
            idleElapsedAtFire = 0f;
            float elapsed = 0f;
            const float dt = 0.05f;

            for (int i = 0; i < maxTicks; i++)
            {
                elapsed += dt;
                h.Tick(DialogueState.Idle, dt, locomotionMoving: false, playActionAtRunnerActive: false);
                if (h.Director.IsRunningAmbientActivityForTests)
                {
                    idleElapsedAtFire = elapsed;
                    return true;
                }
            }

            return false;
        }

        private static AnimationClip Clip(string name, float length, List<Object> cleanup)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0f, length, 0f));
            cleanup.Add(clip);
            return clip;
        }

        /// <summary>A clip whose <c>isLooping</c> is true — required for HoldUntilStopped main
        /// clips, whose completion is driven purely by RequestStop, not clip length.</summary>
        private static AnimationClip LoopingClip(string name, float length, List<Object> cleanup)
        {
            AnimationClip clip = Clip(name, length, cleanup);
            clip.wrapMode = WrapMode.Loop;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            return clip;
        }

        private static ConvaiBodyAnimationConfig CreateConfig(
            bool enableAmbient, float suppressDistance)
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_actionFadeInSeconds").floatValue = 0.05f;
            serialized.FindProperty("_actionFadeOutSeconds").floatValue = 0.05f;
            serialized.FindProperty("_enableAmbientActivities").boolValue = enableAmbient;
            serialized.FindProperty("_ambientStartDelaySeconds").floatValue = StartDelaySeconds;
            serialized.FindProperty("_ambientIntervalSeconds").floatValue = IntervalSeconds;
            serialized.FindProperty("_ambientSuppressDistance").floatValue = suppressDistance;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSet(List<Object> cleanup, int ambientEntryCount, bool playOnce)
        {
            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            List<ActionEntry> actions = null;
            if (ambientEntryCount > 0)
            {
                actions = new List<ActionEntry>();
                for (int i = 0; i < ambientEntryCount; i++)
                {
                    AnimationClip clip = playOnce ? Clip($"ambient_{i}", 0.15f, cleanup) : LoopingClip($"ambient_{i}", 1f, cleanup);
                    var entry = new ActionEntry();
                    entry.Initialize(
                        $"ambient_{i}", clip, ActionMaskMode.UpperBody,
                        playOnce ? ActionLoopMode.PlayOnce : ActionLoopMode.HoldUntilStopped);
                    entry.SetAmbient(true);
                    actions.Add(entry);
                }
            }

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, null, actions, mask);
            return set;
        }

        /// <summary>Owns the graph/layer/config/set for one test and disposes them symmetrically.</summary>
        private sealed class Harness
        {
            public PlayableGraph Graph;
            public ActionLayer ActionLayer;
            public AmbientActivityDirector Director;
            public List<Object> Cleanup;
            private bool _hasConversationAnchor;
            private Vector3 _conversationAnchor;

            public static Harness Build(
                bool enableAmbient = true,
                int ambientEntryCount = 1,
                bool playOnce = false,
                float suppressDistance = 4f,
                bool withCamera = false,
                float cameraDistance = 0f,
                uint randomSeed = 42)
            {
                var cleanup = new List<Object>();
                PlayableGraph graph = PlayableGraph.Create("AmbientActivityDirectorHarness");

                ConvaiBodyAnimationConfig config = CreateConfig(enableAmbient, suppressDistance);
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup, ambientEntryCount, playOnce);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("AmbientActivityDirectorHarness"),
                    RandomSeed = 7
                };

                var actionLayer = new ActionLayer();
                actionLayer.Initialize(runtime, LayerPorts.Action);

                var rootGo = new GameObject("CharacterRoot");
                cleanup.Add(rootGo);
                rootGo.transform.position = Vector3.zero;

                var director = new AmbientActivityDirector(
                    config, actionLayer, set, rootGo.transform, runtime.Trace, randomSeed);

                return new Harness
                {
                    Graph = graph,
                    ActionLayer = actionLayer,
                    Director = director,
                    Cleanup = cleanup,
                    // the director no longer resolves Camera.main itself — the
                    // controller's ConversationAnchorResolver would hand it an anchor through
                    // Tick's parameters, so the harness reproduces that directly instead of
                    // spinning up a real Camera GameObject.
                    _hasConversationAnchor = withCamera,
                    _conversationAnchor = new Vector3(cameraDistance, 0f, 0f)
                };
            }

            /// <summary>
            ///     Ticks the action layer before the director, mirroring
            ///     <see cref="Convai.Modules.BodyAnimation.Components.ConvaiBodyAnimationController" />'s
            ///     per-frame order — the director's graceful-stop/repeat lifecycle depends on the
            ///     layer's own slot advancing (main-clip completion, stop-requested settle) on the
            ///     same cadence it does in production, not just the director's own bookkeeping.
            /// </summary>
            public void Tick(DialogueState dialogueState, float deltaTime, bool locomotionMoving, bool playActionAtRunnerActive)
            {
                EmotionReading emotion = EmotionReading.Neutral;
                var context = new LayerTickContext(
                    deltaTime, dialogueState, in emotion, 0f, false, locomotionMoving,
                    hasConversationAnchor: _hasConversationAnchor, conversationAnchor: _conversationAnchor);
                ActionLayer.Tick(in context);
                Director.Tick(
                    dialogueState, deltaTime, locomotionMoving, playActionAtRunnerActive,
                    _hasConversationAnchor, _conversationAnchor);
            }

            public void Dispose()
            {
                ActionLayer?.Teardown();
                if (Graph.IsValid()) Graph.Destroy();
                foreach (Object obj in Cleanup) Object.DestroyImmediate(obj);
            }
        }
    }
}
