using System;
using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Graph;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Playables;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Resource-discipline guard: proves the two pooling mechanisms —
    ///     <see cref="CrossfadeMixer" />'s pooled <c>Source</c> and <see cref="RuntimeMaskCache" />
    ///     — allocate nothing once warm, covering the recurring transitions that dominate a
    ///     session (idle-variant swap, talk-variant switch, beat gesture and locomotion state
    ///     change all funnel through <see cref="CrossfadeMixer.Play" />).
    /// </summary>
    /// <remarks>
    ///     <para>
    ///     <b>Scope, stated honestly:</b> these tests measure only the pooling/caching code this
    ///     phase touched — <see cref="CrossfadeMixer.Play" />/<see cref="CrossfadeMixer.Tick" />
    ///     and <see cref="RuntimeMaskCache" />'s Acquire/Release — in isolation from a full layer
    ///     stack. They deliberately do NOT drive <c>LocomotionLayer</c> or <c>TalkLayer</c>
    ///     end-to-end and assert zero there, because both layers' Detail-level trace calls build
    ///     an interpolated string as a normal method argument (e.g. <c>TalkLayer.cs</c>'s
    ///     "Talk variant switch on loop" and "Talk motion phrase switch" messages), which C#
    ///     evaluates eagerly at the call site before the verbosity gate inside <c>AnimTrace</c>
    ///     ever runs. That allocation is real, pre-existing, outside the file ownership
    ///     (<c>CrossfadeMixer.cs</c>/<c>TalkLayer.cs</c> mask code/<c>ActionLayer.cs</c> mask
    ///     code/<c>RuntimeMaskCache.cs</c> only — not the trace call sites), and unrelated to
    ///     pooling or mask sharing. Asserting zero across a full tick would either be a false
    ///     failure caused by someone else's code, or would require raising the verbosity-gate
    ///     threshold in a way that quietly stops catching a real regression in the code this
    ///     phase does own — either way, a worse guard than the narrow one below.
    ///     </para>
    /// </remarks>
    internal sealed class BodyAnimationAllocationGuardTests
    {
        private const int WarmupIterations = 64;
        private const int MeasuredIterations = 512;

        [Test]
        public void CrossfadeMixer_SteadyStateCrossfadeCycle_AllocatesZeroAfterWarmup()
        {
            // Alternating Play/Tick-to-completion between two clips is the shape of every
            // steady-state transition: idle-variant swap, talk-variant switch on
            // loop, a beat gesture firing again, a locomotion state hop. Each cycle both starts
            // a new source (rents from the pool) and fully releases the outgoing one (returns it),
            // so it exercises both halves of the pool.
            PlayableGraph graph = PlayableGraph.Create("AllocGuard_CrossfadeMixer");
            var cleanup = new List<Object>();

            try
            {
                var mixer = new CrossfadeMixer(graph, AnimationCurve.Linear(0f, 0f, 1f, 1f));
                AnimationClip clipA = MakeClip(cleanup, "guard-a");
                AnimationClip clipB = MakeClip(cleanup, "guard-b");

                for (int i = 0; i < WarmupIterations; i++)
                    RunCrossfadeCycle(mixer, clipA, clipB, i);

                long allocated = AllocationCounter.Measure(() =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                        RunCrossfadeCycle(mixer, clipA, clipB, i);
                });

                Assert.That(allocated, Is.Zero,
                    "steady-state Play/Tick crossfade cycles must not allocate once the source pool is warm");
                Assert.That(mixer.PooledSourceCountForTests, Is.LessThanOrEqualTo(8),
                    "the pool must stay capped, not just avoid allocating");
            }
            finally
            {
                if (graph.IsValid()) graph.Destroy();
                foreach (Object obj in cleanup)
                    Object.DestroyImmediate(obj);
            }
        }

        private static void RunCrossfadeCycle(CrossfadeMixer mixer, AnimationClip clipA, AnimationClip clipB, int i)
        {
            AnimationClip target = i % 2 == 0 ? clipB : clipA;
            mixer.Play(target, 0.05f, ClipPlaySettings.Default);
            mixer.Tick(0.06f); // exceeds the fade duration: completes the transition in one step
        }

        [Test]
        public void RuntimeMaskCache_SteadyStateAcquireRelease_AllocatesZeroAfterWarmup()
        {
            // At least one holder of each mask stays alive throughout (an "always-resident"
            // character), so the measured Acquire/Release pairs below always hit the warm
            // dictionary-lookup path and never trigger the one-time build or the zero-refcount
            // destroy — exactly the steady-state a scene with several conversing characters
            // produces as they spawn/despawn independently of each other.
            var source = new AvatarMask { name = "AllocGuard_TalkSource" };

            AvatarMask residentFullBody = RuntimeMaskCache.AcquireFullBody();
            AvatarMask residentArms = RuntimeMaskCache.AcquireArms();
            AvatarMask residentTalk = RuntimeMaskCache.AcquireTalkUpperBody(source);

            try
            {
                for (int i = 0; i < WarmupIterations; i++)
                    RunAcquireReleaseCycle(source);

                long allocated = AllocationCounter.Measure(() =>
                {
                    for (int i = 0; i < MeasuredIterations; i++)
                        RunAcquireReleaseCycle(source);
                });

                Assert.That(allocated, Is.Zero,
                    "acquiring/releasing already-cached masks must not allocate");
            }
            finally
            {
                RuntimeMaskCache.Release(residentFullBody);
                RuntimeMaskCache.Release(residentArms);
                RuntimeMaskCache.Release(residentTalk);
                Object.DestroyImmediate(source);
            }
        }

        private static void RunAcquireReleaseCycle(AvatarMask source)
        {
            AvatarMask fullBody = RuntimeMaskCache.AcquireFullBody();
            AvatarMask arms = RuntimeMaskCache.AcquireArms();
            AvatarMask talk = RuntimeMaskCache.AcquireTalkUpperBody(source);

            RuntimeMaskCache.Release(fullBody);
            RuntimeMaskCache.Release(arms);
            RuntimeMaskCache.Release(talk);
        }

        private static AnimationClip MakeClip(List<Object> cleanup, string name)
        {
            var clip = new AnimationClip { name = name };
            cleanup.Add(clip);
            return clip;
        }
    }
}
