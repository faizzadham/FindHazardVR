using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Graph;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Animations;
using UnityEngine.Playables;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class CrossfadeMixerTests
    {
        private PlayableGraph _graph;
        private readonly List<Object> _cleanup = new();

        [SetUp]
        public void SetUp()
        {
            _graph = PlayableGraph.Create("CrossfadeMixerTests");
        }

        [TearDown]
        public void TearDown()
        {
            if (_graph.IsValid()) _graph.Destroy();
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private AnimationClip Clip(string name)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);
            return clip;
        }

        private CrossfadeMixer CreateMixer() =>
            new(_graph, AnimationCurve.Linear(0f, 0f, 1f, 1f));

        [Test]
        public void Play_InstantFade_ReachesFullWeightImmediately()
        {
            CrossfadeMixer mixer = CreateMixer();

            bool started = mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);

            Assert.IsTrue(started);
            Assert.IsFalse(mixer.IsTransitioning);
            Assert.AreEqual(1f, mixer.CurrentSourceWeight, 1e-4f);
            Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-4f);
        }

        [Test]
        public void Crossfade_PreservesTotalWeight_AndCompletes()
        {
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);

            AnimationClip b = Clip("b");
            mixer.Play(b, 1f, ClipPlaySettings.Default);

            mixer.Tick(0.5f);
            Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-3f, "mid-fade pose weight must stay 1");
            Assert.AreEqual(0.5f, mixer.CurrentSourceWeight, 0.05f, "linear curve at t=0.5");
            Assert.IsTrue(mixer.IsTransitioning);

            mixer.Tick(0.6f);
            Assert.IsFalse(mixer.IsTransitioning);
            Assert.AreEqual(b, mixer.CurrentClip);
            Assert.AreEqual(1f, mixer.CurrentSourceWeight, 1e-4f);
            Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-3f, "outgoing sources fully released");
        }

        [Test]
        public void InterruptedCrossfade_KeepsTotalWeightAtOne()
        {
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            mixer.Play(Clip("b"), 1f, ClipPlaySettings.Default);
            mixer.Tick(0.4f);

            // Interrupt mid-fade with a third clip.
            mixer.Play(Clip("c"), 1f, ClipPlaySettings.Default);

            for (float t = 0f; t < 1.2f; t += 0.1f)
            {
                mixer.Tick(0.1f);
                Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-3f, $"weight sum broke at t={t:F1}");
            }

            Assert.IsFalse(mixer.IsTransitioning);
            Assert.AreEqual("c", mixer.CurrentClip.name);
        }

        [Test]
        public void Play_SameClipWithoutRestart_IsNoOp()
        {
            CrossfadeMixer mixer = CreateMixer();
            AnimationClip a = Clip("a");
            mixer.Play(a, 0f, ClipPlaySettings.Default);

            bool startedAgain = mixer.Play(a, 0.3f, ClipPlaySettings.Default);

            Assert.IsFalse(startedAgain);
            Assert.IsFalse(mixer.IsTransitioning);
        }

        [Test]
        public void PlayExternal_ReclaimMidFade_ContinuesWithoutPop()
        {
            CrossfadeMixer mixer = CreateMixer();
            AnimationMixerPlayable external = AnimationMixerPlayable.Create(_graph, 1);

            mixer.PlayExternal(external, 0f);
            Assert.AreEqual(1f, mixer.CurrentSourceWeight, 1e-4f);

            // Leave the external source (it starts fading out)…
            mixer.Play(Clip("stop"), 1f, ClipPlaySettings.Default);
            mixer.Tick(0.3f);
            Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-3f);

            // …then reclaim it mid-fade. Its weight must resume rising from its live value.
            mixer.PlayExternal(external, 1f);
            float weightAtReclaim = mixer.CurrentSourceWeight;
            Assert.Greater(weightAtReclaim, 0.5f, "reclaimed source keeps its live weight");

            mixer.Tick(0.1f);
            Assert.GreaterOrEqual(mixer.CurrentSourceWeight, weightAtReclaim - 1e-3f, "no downward pop");
            Assert.AreEqual(1f, mixer.TotalPoseWeight, 1e-3f);

            mixer.Tick(1f);
            Assert.AreEqual(1f, mixer.CurrentSourceWeight, 1e-4f);
        }

        [Test]
        public void FreezeAll_StopsClipClock_NewPlayRestoresNormalSpeed()
        {
            // An interruption freezes the current (and any still-fading) clip
            // clocks in place — the pose holds — without leaving the mixer's next clip
            // stuck at zero speed.
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            mixer.Tick(0.2f);
            float timeBeforeFreeze = mixer.CurrentTime;
            Assert.Greater(timeBeforeFreeze, 0f, "sanity: the clock advances while playing");

            mixer.FreezeAll();
            mixer.Tick(0.2f);
            mixer.Tick(0.2f);
            Assert.AreEqual(timeBeforeFreeze, mixer.CurrentTime, 1e-5f, "frozen clip clock must not advance");

            // A fresh Play() after the freeze must start a brand-new, full-speed source —
            // never reuse (or unfreeze) the frozen one.
            mixer.Play(Clip("b"), 0f, ClipPlaySettings.Default, restartIfSame: true);
            mixer.Tick(0.2f);
            Assert.Greater(mixer.CurrentTime, 0f, "the new source must advance at normal speed");
            Assert.AreEqual("b", mixer.CurrentClip.name);
        }

        // ------------------------------------------------------------------ source pruning

        [Test]
        public void Play_200InterruptedTransitions_KeepsConnectedInputCountBounded()
        {
            // re-playing before a transition completes must never let the fading list (and
            // therefore the mixer's connected input count) grow without bound.
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("seed"), 0f, ClipPlaySettings.Default);

            for (int i = 0; i < 200; i++)
                mixer.Play(Clip($"c{i}"), 5f, ClipPlaySettings.Default); // long fade, never completes

            int inputCount = mixer.Playable.GetInputCount();
            Assert.Less(inputCount, 40,
                $"connected input count ({inputCount}) must stay bounded despite 200 interrupted transitions");
        }

        [Test]
        public void BeginTransition_PrunesFullyDecayedFadingSource_ReusesItsPort()
        {
            // A source whose live mixer weight has decayed to ~0 contributes nothing and will
            // never contribute again; it must be released at the next BeginTransition instead of
            // waiting for that transition to fully complete (which may never happen).
            CrossfadeMixer mixer = new(_graph, AnimationCurve.Linear(0f, 0f, 1f, 1f), initialCapacity: 2);
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            mixer.Play(Clip("b"), 1f, ClipPlaySettings.Default);
            mixer.Tick(0.9999f);
            Assert.IsTrue(mixer.IsTransitioning, "sanity: still mid-fade, not auto-released by Tick's own completion path");

            int inputCountBeforePrune = mixer.Playable.GetInputCount();
            mixer.Play(Clip("c"), 1f, ClipPlaySettings.Default);
            int inputCountAfterPrune = mixer.Playable.GetInputCount();

            Assert.AreEqual(inputCountBeforePrune, inputCountAfterPrune,
                "'a's fully-decayed port must be reclaimed for 'c' instead of growing the mixer");
        }

        [Test]
        public void PruneFadingSources_NeverPrunesExternalSource_EvenPastCap()
        {
            // PlayExternal reclaims a still-fading external source by identity; the locomotion
            // layer relies on that to hop Move -> Stop -> Move without rebuilding its blend. Both
            // the decay prune and the cap eviction must skip external (Owned == false) sources.
            CrossfadeMixer mixer = CreateMixer();
            AnimationMixerPlayable external = AnimationMixerPlayable.Create(_graph, 1);
            mixer.PlayExternal(external, 0f);

            mixer.Play(Clip("seed"), 1f, ClipPlaySettings.Default);
            for (int i = 0; i < 12; i++)
                mixer.Play(Clip($"c{i}"), 1f, ClipPlaySettings.Default); // interrupts each previous; none complete

            mixer.PlayExternal(external, 1f);
            Assert.Greater(mixer.CurrentSourceWeight, 0f,
                "the external source must still be reclaimable (with its live weight, not a pop to 0) after prune/cap eviction");
        }

        // ------------------------------------------------------------------ source pooling

        [Test]
        public void Play_OutgoingSourceFullyReleased_IsReturnedToThePool()
        {
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            Assert.AreEqual(0, mixer.PooledSourceCountForTests, "sanity: nothing released yet");

            mixer.Play(Clip("b"), 1f, ClipPlaySettings.Default);
            mixer.Tick(1f); // completes the fade: 'a' is released

            Assert.AreEqual(1, mixer.PooledSourceCountForTests,
                "the fully-faded-out source must be pooled instead of dropped for the GC");
        }

        [Test]
        public void Play_AfterRelease_RentsPooledInstanceInsteadOfAllocating()
        {
            // Not directly observable without reflection, but the pooled-count bookkeeping
            // proves the same invariant: a rent must draw down the pool by exactly one.
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            mixer.Play(Clip("b"), 1f, ClipPlaySettings.Default);
            mixer.Tick(1f);
            Assert.AreEqual(1, mixer.PooledSourceCountForTests);

            // A NON-ZERO fade matters here. With fadeSeconds 0 the transition completes
            // synchronously inside Play, which releases the outgoing source back into the pool in
            // the same call — the pool would read 1 again and the test would say "no rent
            // happened" when a rent very much did. A live fade leaves the outgoing source in
            // _fading, so the pool level alone shows the draw-down.
            mixer.Play(Clip("c"), 1f, ClipPlaySettings.Default, restartIfSame: true);

            Assert.AreEqual(0, mixer.PooledSourceCountForTests,
                "starting a new source while the pool is non-empty must rent, not allocate a fresh instance");
        }

        [Test]
        public void SourcePool_ManySequentialReleases_NeverExceedsCap()
        {
            // The pool itself must never become the leak. Fully complete twenty
            // transitions back-to-back (each releases exactly one outgoing source, and each
            // subsequent Play rents it straight back out) and confirm the pool never grows
            // past its cap.
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("seed"), 0f, ClipPlaySettings.Default);

            for (int i = 0; i < 20; i++)
            {
                mixer.Play(Clip($"c{i}"), 0.1f, ClipPlaySettings.Default);
                mixer.Tick(0.2f); // completes this transition before starting the next
                Assert.LessOrEqual(mixer.PooledSourceCountForTests, 8,
                    $"pool exceeded its cap after cycle {i}");
            }
        }

        [Test]
        public void SourcePool_BurstOfSimultaneousReleases_CapsAtEight()
        {
            // Unlike the sequential case above, nothing here rents from the pool between
            // releases: 20 long-fade transitions are started back-to-back (each interrupting the
            // last), so PruneFadingSources' cap-eviction path is the one releasing sources, and
            // it can release several in a single call. The pool must still cap itself.
            CrossfadeMixer mixer = CreateMixer();
            mixer.Play(Clip("seed"), 0f, ClipPlaySettings.Default);

            for (int i = 0; i < 20; i++)
                mixer.Play(Clip($"c{i}"), 5f, ClipPlaySettings.Default); // long fade, never completes

            Assert.LessOrEqual(mixer.PooledSourceCountForTests, 8,
                "the pool must cap itself even when many sources are released in a burst");
        }

        [Test]
        public void PruneFadingSources_ReleasedSource_IsPooledExactlyOnce()
        {
            // PruneFadingSources releases a fully decayed source outside the normal
            // Tick-completion path. That source must be pooled exactly once — never twice,
            // which would otherwise hand the same instance to two live sources.
            CrossfadeMixer mixer = new(_graph, AnimationCurve.Linear(0f, 0f, 1f, 1f), initialCapacity: 2);
            mixer.Play(Clip("a"), 0f, ClipPlaySettings.Default);
            mixer.Play(Clip("b"), 1f, ClipPlaySettings.Default);
            mixer.Tick(0.9999f); // 'a' decays to ~0 weight but the transition has not completed

            mixer.Play(Clip("c"), 1f, ClipPlaySettings.Default); // BeginTransition prunes 'a'
            int pooledAfterPrune = mixer.PooledSourceCountForTests;

            mixer.Tick(1f); // completes 'c's transition, releasing 'b' — must not re-release 'a'

            Assert.AreEqual(pooledAfterPrune + 1, mixer.PooledSourceCountForTests,
                "only 'b' should be newly pooled here; 'a' must not be pooled a second time");
        }
    }

    public class Blend1DTests
    {
        [Test]
        public void ComputeWeights_ExactThresholds_AndMidpoints()
        {
            var samples = new[]
            {
                new Blend1D.Sample(null, 1f),
                new Blend1D.Sample(null, 3f)
            };
            var weights = new float[2];

            Blend1D.ComputeWeights(samples, 1f, weights);
            Assert.AreEqual(1f, weights[0], 1e-4f);
            Assert.AreEqual(0f, weights[1], 1e-4f);

            Blend1D.ComputeWeights(samples, 2f, weights);
            Assert.AreEqual(0.5f, weights[0], 1e-4f);
            Assert.AreEqual(0.5f, weights[1], 1e-4f);

            Blend1D.ComputeWeights(samples, 3f, weights);
            Assert.AreEqual(0f, weights[0], 1e-4f);
            Assert.AreEqual(1f, weights[1], 1e-4f);
        }

        [Test]
        public void ComputeWeights_ClampsOutsideRange()
        {
            var samples = new[]
            {
                new Blend1D.Sample(null, 1f),
                new Blend1D.Sample(null, 3f)
            };
            var weights = new float[2];

            Blend1D.ComputeWeights(samples, 0.2f, weights);
            Assert.AreEqual(1f, weights[0], 1e-4f);

            Blend1D.ComputeWeights(samples, 99f, weights);
            Assert.AreEqual(1f, weights[1], 1e-4f);
        }

        [Test]
        public void PhaseSync_AdvancesSharedPhase_WithRateScale()
        {
            PlayableGraph graph = PlayableGraph.Create("Blend1DTests");
            var cleanup = new List<Object>();

            AnimationClip MakeClip(string name, float length)
            {
                var clip = new AnimationClip { name = name };
                clip.SetCurve("", typeof(Transform), "localPosition.x",
                    AnimationCurve.Constant(0f, length, 0f));
                cleanup.Add(clip);
                return clip;
            }

            try
            {
                var blend = new Blend1D(graph, new[]
                {
                    new Blend1D.Sample(MakeClip("walk", 1f), 1f),
                    new Blend1D.Sample(MakeClip("jog", 0.5f), 3f)
                }, applyFootIK: false);

                // Full weight on walk (1s cycle): phase advances at 1 cycle/s.
                blend.SetParameter(1f);
                Assert.AreEqual(1f, blend.BlendedThreshold, 1e-3f, "authored speed at walk sample");
                blend.Tick(0.25f);
                Assert.AreEqual(0.25f, blend.Phase, 1e-3f);

                // Midway between samples the blended authored speed interpolates.
                blend.SetParameter(2f);
                Assert.AreEqual(2f, blend.BlendedThreshold, 1e-3f);
                blend.SetParameter(1f);

                // Rate warp scales the cycle rate.
                blend.RateScale = 2f;
                blend.Tick(0.25f);
                Assert.AreEqual(0.75f, blend.Phase, 1e-3f);

                // Phase wraps.
                blend.Tick(0.2f);
                Assert.Less(blend.Phase, 0.2f);
            }
            finally
            {
                graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }
    }
}
