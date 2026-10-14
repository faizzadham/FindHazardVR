using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyAnimation;
using Convai.Modules.BodyAnimation.Core.Diagnostics;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Modules.BodyAnimation.Core.Layers;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Playables;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="ActionLayer" />: the montage lifecycle (play → complete),
    ///     the two replacement policies (immediate crossfade for a same-mask replacement, a
    ///     mask-safe queued fade-out-then-swap for a different-mask replacement), interruption
    ///     rules (non-interruptible rejection, stop cancels a queued replacement), the hold
    ///     timeout auto-stop, and the full-body duck signal consumed by the talk/pointing layers.
    /// </summary>
    public sealed class ActionLayerTests
    {
        [Test]
        public void Play_FromOff_StartsAndCompletes()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerPlayFromOffTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerPlayFromOffTests"),
                    RandomSeed = 1
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entry = Entry(
                    "wave", Clip("wave", 0.5f, cleanup), ActionMaskMode.UpperBody, ActionLoopMode.PlayOnce);

                BodyAnimationActionHandle handle = layer.Play(entry, default);

                Assert.That(handle, Is.Not.Null);
                Assert.That(layer.StateLabel, Is.EqualTo("Playing:wave"));

                bool completed = false;
                for (int i = 0; i < 60 && !completed; i++)
                {
                    Tick(layer, 0.05f);
                    if (events.Any(e => e.ActionName == "wave" && e.Phase == BodyAnimationActionPhase.Completed))
                        completed = true;
                }

                Assert.That(completed, Is.True, "action never reported Completed within the 3s budget");
                Assert.That(handle.IsDone, Is.True);
                Assert.That(handle.Completion.Result, Is.True);
                Assert.That(layer.StateLabel, Is.EqualTo("Off"));
                Assert.That(layer.Weight, Is.EqualTo(0f).Within(0.001f));
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Replace_SameMask_ImmediateCrossfade()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerSameMaskReplaceTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerSameMaskReplaceTests"),
                    RandomSeed = 2
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entryA = Entry(
                    "actionA", LoopingClip("clipA", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);
                ActionEntry entryB = Entry(
                    "actionB", LoopingClip("clipB", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);

                BodyAnimationActionHandle handleA = layer.Play(entryA, default);
                Assert.That(handleA, Is.Not.Null);

                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                events.Clear();
                BodyAnimationActionHandle handleB = layer.Play(entryB, default);

                Assert.That(handleB, Is.Not.Null);
                Assert.That(layer.HasPendingReplaceForTests, Is.False,
                    "same-mask replacement must crossfade immediately, not queue");
                Assert.That(layer.StateLabel, Is.EqualTo("Playing:actionB"));
                Assert.That(handleA.IsDone, Is.True);
                Assert.That(handleA.Completion.Result, Is.False);

                Assert.That(events.Count, Is.GreaterThanOrEqualTo(2));
                Assert.That(events[0].ActionName, Is.EqualTo("actionA"));
                Assert.That(events[0].Phase, Is.EqualTo(BodyAnimationActionPhase.Interrupted));
                Assert.That(events[1].ActionName, Is.EqualTo("actionB"));
                Assert.That(events[1].Phase, Is.EqualTo(BodyAnimationActionPhase.Started));
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void Replace_DifferentMask_QueuesThroughTrough()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerQueueReplaceTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerQueueReplaceTests"),
                    RandomSeed = 3
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entryA = Entry(
                    "actionA", LoopingClip("clipA", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                ActionEntry entryB = Entry(
                    "actionB", LoopingClip("clipB", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);

                BodyAnimationActionHandle handleA = layer.Play(entryA, default);
                Assert.That(handleA, Is.Not.Null);

                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                AvatarMask maskBefore = layer.AppliedMaskForTests;

                BodyAnimationActionHandle handleB = layer.Play(entryB, default);

                Assert.That(handleB, Is.Not.Null);
                Assert.That(layer.HasPendingReplaceForTests, Is.True,
                    "different-mask replacement while visibly playing must queue behind a fade-out");
                Assert.That(layer.StateLabel, Is.EqualTo("FadingOut"));
                Assert.That(handleA.IsDone, Is.True);
                Assert.That(handleA.Completion.Result, Is.False);

                bool maskSwapped = false;
                bool started = false;
                float previousWeight = layer.Weight;

                for (int i = 0; i < 60 && !started; i++)
                {
                    Tick(layer, 0.02f);

                    if (!maskSwapped && layer.AppliedMaskForTests != maskBefore)
                    {
                        maskSwapped = true;
                        Assert.That(previousWeight, Is.LessThan(0.15f),
                            "the mask must not swap while the old action is still visibly on screen");
                    }

                    if (layer.StateLabel == "Playing:actionB")
                        started = true;

                    previousWeight = layer.Weight;
                }

                Assert.That(maskSwapped, Is.True, "mask never swapped within the tick budget");
                Assert.That(started, Is.True, "queued action B never started within the tick budget");
                Assert.That(layer.HasPendingReplaceForTests, Is.False);

                for (int i = 0; i < 10; i++)
                    Tick(layer, 0.02f);

                Assert.That(layer.Weight, Is.GreaterThan(0.5f));
                Assert.That(handleB.IsDone, Is.False, "B is holding, not done");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void QueuedReplace_StopCancelsPending()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerQueuedStopTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerQueuedStopTests"),
                    RandomSeed = 4
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entryA = Entry(
                    "actionA", LoopingClip("clipA", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                ActionEntry entryB = Entry(
                    "actionB", LoopingClip("clipB", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);

                layer.Play(entryA, default);
                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);

                BodyAnimationActionHandle handleB = layer.Play(entryB, default);
                Assert.That(handleB, Is.Not.Null);
                Assert.That(layer.HasPendingReplaceForTests, Is.True);

                events.Clear();
                bool stopped = layer.RequestStop();

                Assert.That(stopped, Is.True);
                Assert.That(layer.HasPendingReplaceForTests, Is.False);
                Assert.That(handleB.IsDone, Is.True);
                Assert.That(handleB.Completion.Result, Is.False);
                Assert.That(events.Any(e =>
                    e.ActionName == "actionB" && e.Phase == BodyAnimationActionPhase.Interrupted), Is.True);

                bool reachedOff = false;
                for (int i = 0; i < 60 && !reachedOff; i++)
                {
                    Tick(layer, 0.02f);
                    Assert.That(layer.StateLabel, Is.Not.EqualTo("Playing:actionB"),
                        "B was cancelled before it ever started");
                    if (layer.StateLabel == "Off")
                        reachedOff = true;
                }

                Assert.That(reachedOff, Is.True, "layer never settled back to Off");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void NonInterruptible_RejectsReplacement()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerNonInterruptibleTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerNonInterruptibleTests"),
                    RandomSeed = 5
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entryA = Entry(
                    "actionA", LoopingClip("clipA", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);
                typeof(ActionEntry)
                    .GetField("_interruptible", BindingFlags.NonPublic | BindingFlags.Instance)
                    .SetValue(entryA, false);

                ActionEntry entryB = Entry(
                    "actionB", LoopingClip("clipB", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);

                BodyAnimationActionHandle handleA = layer.Play(entryA, default);
                Assert.That(handleA, Is.Not.Null);
                Tick(layer, 0.1f);

                events.Clear();
                BodyAnimationActionHandle handleB = layer.Play(entryB, default);

                Assert.That(handleB, Is.Null);
                Assert.That(events.Any(e =>
                    e.ActionName == "actionB" && e.Phase == BodyAnimationActionPhase.Rejected), Is.True);
                Assert.That(layer.StateLabel, Is.EqualTo("Playing:actionA"));
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void HoldTimeout_AutoStops()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerHoldTimeoutTests");
            var layer = new ActionLayer();
            bool initialized = false;
            var events = new List<BodyAnimationActionEvent>();

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerHoldTimeoutTests"),
                    RandomSeed = 6
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;
                layer.LifecycleChanged += events.Add;

                ActionEntry entry = Entry(
                    "actionA", LoopingClip("clipA", 1f, cleanup), ActionMaskMode.UpperBody,
                    ActionLoopMode.HoldUntilStopped);
                var options = new ActionPlayOptions { HoldSeconds = 0.2f };

                BodyAnimationActionHandle handle = layer.Play(entry, in options);
                Assert.That(handle, Is.Not.Null);

                for (int i = 0; i < 60 && !handle.IsDone; i++)
                    Tick(layer, 0.02f);

                Assert.That(events.Any(e =>
                    e.ActionName == "actionA" && e.Phase == BodyAnimationActionPhase.Ending), Is.True);
                Assert.That(handle.IsDone, Is.True);
                Assert.That(handle.Completion.Result, Is.True);
                Assert.That(events.Any(e =>
                    e.ActionName == "actionA" && e.Phase == BodyAnimationActionPhase.Completed), Is.True);
            }
            finally
            {
                if (initialized) layer.Teardown();
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void FullBodyDuck_TracksWeight()
        {
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerFullBodyDuckTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerFullBodyDuckTests"),
                    RandomSeed = 7
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                ActionEntry entry = Entry(
                    "actionA", Clip("clipA", 0.5f, cleanup), ActionMaskMode.FullBody, ActionLoopMode.PlayOnce);

                BodyAnimationActionHandle handle = layer.Play(entry, default);
                Assert.That(handle, Is.Not.Null);

                for (int i = 0; i < 3; i++)
                {
                    Tick(layer, 0.02f);
                    Assert.That(layer.FullBodyDuck01, Is.EqualTo(layer.Weight).Within(1e-4f));
                }

                for (int i = 0; i < 200 && !handle.IsDone; i++)
                    Tick(layer, 0.02f);

                Assert.That(handle.IsDone, Is.True);
                Assert.That(layer.FullBodyDuck01, Is.EqualTo(0f).Within(1e-4f));
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void FullBodyDuck_AllowConversationOverlays_StaysZeroThroughoutFade()
        {
            // A full-body Hold action authored with AllowConversationOverlays must never
            // contribute to the duck — including during its own fade-in — proving the gate is
            // applied at the FullBodyDuck01 source (always 0) rather than as a post-hoc
            // threshold that would snap once the entry reaches full weight.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerAllowOverlaysDuckTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerAllowOverlaysDuckTests"),
                    RandomSeed = 8
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                ActionEntry entry = Entry(
                    "sit_desk", LoopingClip("clipSit", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                SetAllowConversationOverlays(entry, true);

                BodyAnimationActionHandle handle = layer.Play(entry, default);
                Assert.That(handle, Is.Not.Null);

                for (int i = 0; i < 10; i++)
                {
                    Tick(layer, 0.02f);
                    Assert.That(layer.FullBodyDuck01, Is.EqualTo(0f).Within(1e-4f),
                        "an AllowConversationOverlays full-body hold must never duck the overlays, at any fade progress");
                }

                Assert.That(layer.Weight, Is.GreaterThan(0.5f), "sanity: the action itself is actually playing/visible");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void SuppressesConversationOverlays_TracksAllowConversationOverlaysFlag()
        {
            // The shared suppression signal (consumed by the controller's beat-suppression
            // check and ReferentialGestureDirector) must be lifted for a flagged action but stay
            // exactly as before (true while any action plays) for an unflagged one.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerSuppressesOverlaysTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerSuppressesOverlaysTests"),
                    RandomSeed = 9
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                Assert.That(layer.SuppressesConversationOverlays, Is.False, "nothing is playing yet");

                ActionEntry unflagged = Entry(
                    "wave", Clip("wave2", 0.5f, cleanup), ActionMaskMode.UpperBody, ActionLoopMode.PlayOnce);
                layer.Play(unflagged, default);
                Assert.That(layer.SuppressesConversationOverlays, Is.True,
                    "unflagged behavior must stay unchanged — any active action suppresses overlays");

                ActionEntry flagged = Entry(
                    "sit_desk2", LoopingClip("clipSit2", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                SetAllowConversationOverlays(flagged, true);
                layer.Play(flagged, default);
                Tick(layer, 0.02f);

                Assert.That(layer.SuppressesConversationOverlays, Is.False,
                    "a seated-conversation hold must not suppress talk/beat/referential overlays");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void DirectInterrupt_UnflaggedToFlagged_DuckBlendsSmoothly()
        {
            // Defect fix: a direct same-mask interrupt (Weight stays visible, no trough) between
            // two FullBody entries that disagree on AllowConversationOverlays must blend the duck
            // value itself over ActionChainCrossfadeSeconds instead of snapping.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerDuckBlendUnflaggedToFlaggedTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerDuckBlendUnflaggedToFlaggedTests"),
                    RandomSeed = 10
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                ActionEntry entryA = Entry(
                    "seatedA", LoopingClip("clipSeatedA", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                layer.Play(entryA, default);
                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                float outgoingDuck = layer.FullBodyDuck01;
                Assert.That(outgoingDuck, Is.EqualTo(layer.Weight).Within(1e-4f),
                    "sanity: unflagged duck tracks weight before the interrupt");

                ActionEntry entryB = Entry(
                    "seatedB", LoopingClip("clipSeatedB", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                SetAllowConversationOverlays(entryB, true);

                layer.Play(entryB, default);

                // Endpoint exact at start — no tick has advanced the blend yet.
                Assert.That(layer.FullBodyDuck01, Is.EqualTo(outgoingDuck).Within(1e-4f));

                float crossfadeSeconds = config.ActionChainCrossfadeSeconds;
                Tick(layer, crossfadeSeconds * 0.5f);
                float midDuck = layer.FullBodyDuck01;
                Assert.That(midDuck, Is.LessThan(outgoingDuck), "mid-blend must have moved off the outgoing endpoint");
                Assert.That(midDuck, Is.GreaterThan(0f), "mid-blend must not already be at the incoming endpoint");

                Tick(layer, crossfadeSeconds); // well past the blend duration
                Assert.That(layer.FullBodyDuck01, Is.EqualTo(0f).Within(1e-4f),
                    "endpoint exact at end — the flagged incoming entry ducks nothing");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void DirectInterrupt_FlaggedToUnflagged_DuckBlendsSmoothly()
        {
            // Reverse direction of the case above.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerDuckBlendFlaggedToUnflaggedTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerDuckBlendFlaggedToUnflaggedTests"),
                    RandomSeed = 11
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                ActionEntry entryA = Entry(
                    "seatedC", LoopingClip("clipSeatedC", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                SetAllowConversationOverlays(entryA, true);
                layer.Play(entryA, default);
                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                float outgoingDuck = layer.FullBodyDuck01;
                Assert.That(outgoingDuck, Is.EqualTo(0f).Within(1e-4f),
                    "sanity: flagged entry ducks nothing before the interrupt");

                ActionEntry entryB = Entry(
                    "seatedD", LoopingClip("clipSeatedD", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);

                layer.Play(entryB, default);

                // Endpoint exact at start.
                Assert.That(layer.FullBodyDuck01, Is.EqualTo(0f).Within(1e-4f));

                float crossfadeSeconds = config.ActionChainCrossfadeSeconds;
                Tick(layer, crossfadeSeconds * 0.5f);
                float midDuck = layer.FullBodyDuck01;
                Assert.That(midDuck, Is.GreaterThan(0f), "mid-blend must have moved off the outgoing endpoint");
                Assert.That(midDuck, Is.LessThan(layer.Weight), "mid-blend must not already be at the incoming endpoint");

                Tick(layer, crossfadeSeconds); // well past the blend duration
                Assert.That(layer.FullBodyDuck01, Is.EqualTo(layer.Weight).Within(1e-4f),
                    "endpoint exact at end — the unflagged incoming entry tracks weight again");
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        [Test]
        public void DirectInterrupt_UnflaggedToUnflagged_DuckUnchangedAtEveryTick()
        {
            // No duck-level change between the outgoing and incoming entry: must never enter the
            // blend — the duck stays exactly weight-tracked at every tick, byte-identical to
            // behavior before the duck-blend fix.
            var cleanup = new List<Object>();
            PlayableGraph graph = PlayableGraph.Create("ActionLayerDuckBlendUnflaggedToUnflaggedTests");
            var layer = new ActionLayer();
            bool initialized = false;

            try
            {
                ConvaiBodyAnimationConfig config = CreateConfig();
                cleanup.Add(config);
                ConvaiBodyAnimationSet set = CreateSet(cleanup);

                var runtime = new LayerRuntime
                {
                    Graph = graph,
                    Mixer = new LayerMixerHost(graph, LayerPorts.Count),
                    Set = set,
                    Config = config,
                    Trace = new AnimTrace("ActionLayerDuckBlendUnflaggedToUnflaggedTests"),
                    RandomSeed = 12
                };

                layer.Initialize(runtime, LayerPorts.Action);
                initialized = true;

                ActionEntry entryA = Entry(
                    "seatedE", LoopingClip("clipSeatedE", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                layer.Play(entryA, default);
                for (int i = 0; i < 10 && layer.Weight <= 0.9f; i++)
                    Tick(layer, 0.05f);
                Assert.That(layer.Weight, Is.GreaterThan(0.9f));

                ActionEntry entryB = Entry(
                    "seatedF", LoopingClip("clipSeatedF", 1f, cleanup), ActionMaskMode.FullBody,
                    ActionLoopMode.HoldUntilStopped);
                layer.Play(entryB, default);

                float crossfadeSeconds = config.ActionChainCrossfadeSeconds;
                for (int i = 0; i < 10; i++)
                {
                    Assert.That(layer.FullBodyDuck01, Is.EqualTo(layer.Weight).Within(1e-4f),
                        "an unflagged-to-unflagged interrupt must never deviate from weight-tracked duck");
                    Tick(layer, crossfadeSeconds * 0.1f);
                }
            }
            finally
            {
                if (initialized) TeardownFullBodyLayer(layer);
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup) Object.DestroyImmediate(obj);
            }
        }

        // ------------------------------------------------------------------ helpers

        /// <summary>
        ///     Tears down a layer that has played a <see cref="ActionMaskMode.FullBody" /> entry.
        ///     <see cref="ActionLayer.Teardown" /> destroys its lazily-created runtime mask
        ///     with a play-mode-aware helper (DestroyImmediate under the EditMode runner), so
        ///     this is a plain teardown; kept as a seam should teardown grow expectations.
        /// </summary>
        private static void TeardownFullBodyLayer(ActionLayer layer)
        {
            layer.Teardown();
        }

        private static ConvaiBodyAnimationConfig CreateConfig()
        {
            ConvaiBodyAnimationConfig config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            var serialized = new SerializedObject(config);
            serialized.FindProperty("_actionFadeInSeconds").floatValue = 0.1f;
            serialized.FindProperty("_actionFadeOutSeconds").floatValue = 0.1f;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return config;
        }

        private static ConvaiBodyAnimationSet CreateSet(List<Object> cleanup)
        {
            var mask = new AvatarMask { name = "upper-body" };
            cleanup.Add(mask);

            ConvaiBodyAnimationSet set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            cleanup.Add(set);
            set.InitializeContent("Test", null, null, null, mask);
            return set;
        }

        private static ActionEntry Entry(
            string name, AnimationClip clip, ActionMaskMode maskMode, ActionLoopMode loopMode)
        {
            var entry = new ActionEntry();
            entry.Initialize(name, clip, maskMode, loopMode);
            return entry;
        }

        /// <summary>Sets the Inspector-only <c>AllowConversationOverlays</c> flag via reflection
        ///, matching the existing <c>_interruptible</c> reflection pattern above — the
        /// flag has no test-facing constructor/setter since it is authoring-only.</summary>
        private static void SetAllowConversationOverlays(ActionEntry entry, bool value)
        {
            typeof(ActionEntry)
                .GetField("_allowConversationOverlays", BindingFlags.NonPublic | BindingFlags.Instance)
                .SetValue(entry, value);
        }

        private static AnimationClip Clip(string name, float length, List<Object> cleanup)
        {
            var clip = new AnimationClip { name = name };
            clip.SetCurve("", typeof(Transform), "localPosition.x", AnimationCurve.Constant(0f, length, 0f));
            cleanup.Add(clip);
            return clip;
        }

        /// <summary>A clip whose <c>isLooping</c> is true — required for <see cref="OneShotLoop.Hold" />
        /// main clips, whose completion is driven purely by <c>RequestStop</c>, not clip length.</summary>
        private static AnimationClip LoopingClip(string name, float length, List<Object> cleanup)
        {
            AnimationClip clip = Clip(name, length, cleanup);
            clip.wrapMode = WrapMode.Loop;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            return clip;
        }

        private static void Tick(ActionLayer layer, float deltaTime)
        {
            EmotionReading emotion = EmotionReading.Neutral;
            var context = new LayerTickContext(deltaTime, DialogueState.Idle, in emotion, 0f, false, false);
            layer.Tick(in context);
        }
    }
}
