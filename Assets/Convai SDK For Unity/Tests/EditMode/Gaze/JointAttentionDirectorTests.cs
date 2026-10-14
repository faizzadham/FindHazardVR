using System.Collections.Generic;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class JointAttentionDirectorTests
    {
        private const float Dt = 0.2f; // ~5 Hz evaluation cadence (the spec default).
        private const float ConeAngleDegrees = 8f;
        private const float MaxDistanceMeters = 12f;
        private const float DwellSeconds = 0.7f;
        private const float ReactionDelayMin = 0.2f;
        private const float ReactionDelayMax = 0.5f;
        private const float CooldownSeconds = 10f;
        private const float GlobalMinIntervalSeconds = 4f;

        private static readonly Ray ForwardRay = new(Vector3.zero, Vector3.forward);
        private static readonly List<JointAttentionCandidate> NoCandidates = new(0);

        private JointAttentionDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _director = new JointAttentionDirector();
            _random = new DeterministicEmbodimentRandom(101u);
        }

        private static List<JointAttentionCandidate> Single(long id, Vector3 point, string name = "Object") =>
            new() { new JointAttentionCandidate(id, point, name) };

        private void Tick(IReadOnlyList<JointAttentionCandidate> candidates, bool active = true)
        {
            Ray ray = ForwardRay;
            _director.Tick(
                in ray, candidates, Dt, active,
                ConeAngleDegrees, MaxDistanceMeters, DwellSeconds,
                ReactionDelayMin, ReactionDelayMax, CooldownSeconds, GlobalMinIntervalSeconds,
                ref _random);
        }

        private int RunSteadyHit(long id, Vector3 point, int ticks, string name = "Object")
        {
            List<JointAttentionCandidate> candidates = Single(id, point, name);
            int glances = 0;
            for (int i = 0; i < ticks; i++)
            {
                Tick(candidates);
                if (_director.HasGlanceToFire) glances++;
            }

            return glances;
        }

        // Ticks needed to comfortably clear dwell (0.7s) + max reaction delay (0.5s).
        private const int TicksToClearDwellAndReaction = 8; // 8 * 0.2s = 1.6s

        [Test]
        public void BelowDwellThreshold_NeverGlances()
        {
            // 0.6s of continuous dwell — below the 0.7s threshold.
            int glances = RunSteadyHit(id: 1, point: new Vector3(0f, 0f, 5f), ticks: 3);

            Assert.AreEqual(0, glances);
            Assert.IsNull(_director.AttendedObjectName);
        }

        [Test]
        public void SteadyDwell_FiresExactlyOneGlanceAfterReactionDelay()
        {
            int glances = 0;
            int firstFireTick = -1;
            List<JointAttentionCandidate> candidates = Single(1, new Vector3(0f, 0f, 5f), "Vase");

            for (int i = 0; i < TicksToClearDwellAndReaction; i++)
            {
                Tick(candidates);
                if (_director.HasGlanceToFire)
                {
                    glances++;
                    if (firstFireTick < 0) firstFireTick = i;
                    Assert.AreEqual(1L, _director.GlanceTargetId);
                }
            }

            Assert.AreEqual(1, glances, "Exactly one glance decision must fire for one continuous dwell.");
            // Dwell (0.7s) needs 5 ticks (0.8s elapsed) before scheduling even begins.
            Assert.That(firstFireTick, Is.GreaterThanOrEqualTo(4));
        }

        [Test]
        public void ConeMiss_NeverGlancesOrAttends()
        {
            // atan(1.5 / 5) ≈ 16.7°, comfortably outside the 8° cone.
            int glances = RunSteadyHit(id: 1, point: new Vector3(1.5f, 0f, 5f), ticks: 25);

            Assert.AreEqual(0, glances);
            Assert.IsNull(_director.AttendedObjectName);
        }

        [Test]
        public void SweepAcrossObjects_NeverGlances()
        {
            List<JointAttentionCandidate> a = Single(1, new Vector3(0.1f, 0f, 5f), "A");
            List<JointAttentionCandidate> b = Single(2, new Vector3(-0.1f, 0f, 5f), "B");

            int glances = 0;
            for (int i = 0; i < 25; i++)
            {
                Tick(i % 2 == 0 ? a : b);
                if (_director.HasGlanceToFire) glances++;
            }

            Assert.AreEqual(0, glances, "A sweeping ray must never accumulate enough continuous dwell on one object.");
        }

        [Test]
        public void PerObjectCooldown_SuppressesRepeatGlance()
        {
            Vector3 point = new(0f, 0f, 5f);

            int firstRunGlances = RunSteadyHit(1, point, TicksToClearDwellAndReaction);
            Assert.AreEqual(1, firstRunGlances, "Precondition: the first dwell session must glance once.");

            // Break dwell with two consecutive misses (grace + reset), then re-establish it.
            Tick(NoCandidates);
            Tick(NoCandidates);

            int secondRunGlances = RunSteadyHit(1, point, TicksToClearDwellAndReaction);

            Assert.AreEqual(0, secondRunGlances, "Re-dwelling on the same object within its cooldown must not re-glance.");
        }

        [Test]
        public void GraceEvaluation_SingleMissedHitDoesNotResetDwell()
        {
            Vector3 point = new(0f, 0f, 5f);
            List<JointAttentionCandidate> candidates = Single(1, point, "Vase");

            // Two good hits (0.4s), one miss (grace absorbs it), then hits until it fires.
            Tick(candidates);
            Tick(candidates);
            Tick(NoCandidates); // grace-covered miss — must not reset the dwell start.

            int glances = 0;
            for (int i = 0; i < TicksToClearDwellAndReaction; i++)
            {
                Tick(candidates);
                if (_director.HasGlanceToFire) glances++;
            }

            Assert.AreEqual(1, glances, "A single missed evaluation must not reset a continuous dwell.");
        }

        [Test]
        public void InactiveState_NeverGlancesOrAttends()
        {
            List<JointAttentionCandidate> candidates = Single(1, new Vector3(0f, 0f, 5f), "Vase");

            int glances = 0;
            for (int i = 0; i < 25; i++)
            {
                Tick(candidates, active: false);
                if (_director.HasGlanceToFire) glances++;
            }

            Assert.AreEqual(0, glances, "Gated-out dialogue states (e.g. Speaking) must never glance.");
            Assert.IsNull(_director.AttendedObjectName);
        }

        // ── Object-identity fidelity (finding F-2) ───────────────────────────────
        //
        // The wiring layer used to narrow the 64-bit object id to its 32-bit hash before
        // handing it over, then resolve the glance target back out of a map keyed by that
        // hash — so two candidates whose ids collided pointed at each other's transform.
        // These two ids are exactly such a pair: long.GetHashCode() is (hi ^ lo), and
        // 1 ^ 2 == 3 ^ 0, so anything that narrows them again cannot tell them apart.

        private const long IdHashCollisionA = (1L << 32) | 2L; // hi 1, lo 2
        private const long IdHashCollisionB = 3L << 32;        // hi 3, lo 0

        [Test]
        public void GlanceTargetId_PreservesTheFullWidthObjectId()
        {
            int glances = 0;
            List<JointAttentionCandidate> candidates = Single(IdHashCollisionA, new Vector3(0f, 0f, 5f), "Vase");

            for (int i = 0; i < TicksToClearDwellAndReaction; i++)
            {
                Tick(candidates);
                if (!_director.HasGlanceToFire) continue;

                glances++;
                Assert.AreEqual(IdHashCollisionA, _director.GlanceTargetId,
                    "The glance target id must come back out exactly as it went in — the wiring " +
                    "layer resolves the transform from it.");
            }

            Assert.AreEqual(1, glances, "Precondition: the dwell must produce a glance to inspect.");
        }

        [Test]
        public void HashCollidingIds_AreTreatedAsDistinctObjects()
        {
            Vector3 point = new(0f, 0f, 5f);

            Assert.AreEqual(IdHashCollisionA.GetHashCode(), IdHashCollisionB.GetHashCode(),
                "Precondition: these ids must collide under 32-bit hashing for this test to mean anything.");

            Assert.AreEqual(1, RunSteadyHit(IdHashCollisionA, point, TicksToClearDwellAndReaction, "A"),
                "Precondition: the first object must glance once, arming its per-object cooldown.");

            // Break the dwell and let the 4s global interval elapse (22 * 0.2s = 4.4s), so the
            // only thing that could suppress the second object is its own per-object cooldown.
            for (int i = 0; i < 22; i++) Tick(NoCandidates);

            Assert.AreEqual(1, RunSteadyHit(IdHashCollisionB, point, TicksToClearDwellAndReaction, "B"),
                "A different object must not inherit another object's cooldown just because their " +
                "ids hash alike.");
            Assert.AreEqual(IdHashCollisionB, _director.GlanceTargetId);
        }

        [Test]
        public void AttendedObjectName_EdgeTriggeredSetAndClear()
        {
            Vector3 point = new(0f, 0f, 5f);
            List<JointAttentionCandidate> candidates = Single(1, point, "Vase");

            int setEdges = 0;

            // Dwell to threshold (5 ticks: elapsed reaches 0.8s ≥ 0.7s on the 5th) — expect
            // exactly one "set" edge.
            for (int i = 0; i < 5; i++)
            {
                Tick(candidates);
                if (_director.AttendedChangedThisTick && _director.AttendedObjectName != null) setEdges++;
            }

            Assert.AreEqual(1, setEdges, "Reaching the dwell threshold must fire exactly one set edge.");
            Assert.AreEqual("Vase", _director.AttendedObjectName);

            // Continuing to dwell on the same object must not re-fire any edge.
            for (int i = 0; i < 4; i++)
            {
                Tick(candidates);
                Assert.IsFalse(_director.AttendedChangedThisTick, "Steady dwell must not repeat the set edge.");
            }

            // First of two misses only consumes the grace — no edge yet.
            Tick(NoCandidates);
            Assert.IsFalse(_director.AttendedChangedThisTick, "A grace-covered miss must not clear attention yet.");

            // Second consecutive miss actually drops the dwell — expect exactly one clear edge.
            Tick(NoCandidates);
            Assert.IsTrue(_director.AttendedChangedThisTick, "Losing the dwell must fire a clear edge.");
            Assert.IsNull(_director.AttendedObjectName);
        }
    }
}
