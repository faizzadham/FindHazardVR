using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Travel gaze: what a walking character looks at. These pin the behavioral contract
    ///     (path candidate, check-in cadence, arrival hand-off) and the two traps that would make
    ///     the feature look catastrophically broken rather than merely wrong — an unstable arbiter
    ///     key, and a path that loses to the player during a follow.
    /// </summary>
    public sealed class TravelGazeDirectorTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private TravelGazeDirector _director;
        private GameObject _root;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new TravelGazeDirector();
            _root = new GameObject("character");
            _random = new DeterministicEmbodimentRandom(1234u);
        }

        [TearDown]
        public void TearDown()
        {
            if (_root != null) Object.DestroyImmediate(_root);
            if (_profile != null) Object.DestroyImmediate(_profile);
        }

        private static TravelIntent Traveling(
            Vector3 direction,
            float speed01 = 0.5f,
            float remaining = float.PositiveInfinity,
            TravelSubjectKind subject = TravelSubjectKind.None,
            Vector3 subjectPosition = default) =>
            new(true, direction.normalized, speed01, remaining, subjectPosition, subject);

        /// <summary>Runs the ramp to completion so tests start from a fully engaged travel.</summary>
        private GazeTargetCandidate Settle(in TravelIntent intent, int ticks = 60)
        {
            GazeTargetCandidate candidate = default;
            for (int i = 0; i < ticks; i++)
                _director.TryBuildPathCandidate(in intent, _root.transform, 1.6f, _profile, Dt, out candidate);

            return candidate;
        }

        // ── Degradation ──────────────────────────────────────────────────────

        [Test]
        public void NotTraveling_ProducesNoCandidate()
        {
            bool produced = _director.TryBuildPathCandidate(
                TravelIntent.None, _root.transform, 1.6f, _profile, Dt, out _);

            Assert.IsFalse(produced, "A character standing still must offer no path candidate at all.");
            Assert.IsFalse(_director.IsActive);
        }

        [Test]
        public void FeatureDisabled_ProducesNoCandidate()
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableTravelGaze").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            TravelIntent intent = Traveling(Vector3.forward);

            Settle(in intent);
            bool produced = _director.TryBuildPathCandidate(
                in intent, _root.transform, 1.6f, _profile, Dt, out _);

            Assert.IsFalse(produced, "The opt-out must restore the pre-feature behavior exactly.");
        }

        [Test]
        public void ZeroDirection_ProducesNoCandidate()
        {
            // A publisher that reports travel with no usable heading must not produce a look point
            // on top of the character's own head.
            TravelIntent intent = Traveling(Vector3.forward);
            Settle(in intent);

            var degenerate = new TravelIntent(
                true, Vector3.zero, 0.5f, float.PositiveInfinity, Vector3.zero, TravelSubjectKind.None);

            for (int i = 0; i < 60; i++)
                _director.TryBuildPathCandidate(in degenerate, _root.transform, 1.6f, _profile, Dt, out _);

            bool produced = _director.TryBuildPathCandidate(
                in degenerate, _root.transform, 1.6f, _profile, Dt, out _);
            Assert.IsFalse(produced);
        }

        // ── The path candidate ───────────────────────────────────────────────

        [Test]
        public void PathCandidate_KeyIsStableWhileTheCharacterMoves()
        {
            // The trap this whole feature can die on. The path point has no transform and moves with
            // the character every frame; if the arbiter keys it by position, every tick looks like a
            // brand-new target — a re-acquisition saccade and blink, every frame, forever.
            TravelIntent intent = Traveling(Vector3.forward);
            Settle(in intent);

            var arbiter = new GazeTargetArbiter();
            var candidates = new System.Collections.Generic.List<GazeTargetCandidate>(1);
            int generationBumps = 0;
            int lastGeneration = int.MinValue;

            for (int i = 0; i < 300; i++)
            {
                _root.transform.position += Vector3.forward * (2f * Dt);

                candidates.Clear();
                if (_director.TryBuildPathCandidate(
                        in intent, _root.transform, 1.6f, _profile, Dt, out GazeTargetCandidate candidate))
                {
                    candidates.Add(candidate);
                }

                GazeTargetDecision decision = arbiter.Tick(candidates, null, true, _profile, Dt);
                if (decision.GenerationId != lastGeneration)
                {
                    generationBumps++;
                    lastGeneration = decision.GenerationId;
                }
            }

            Assert.LessOrEqual(
                generationBumps, 1,
                "The path target must be acquired once, not re-acquired every frame.");
        }

        [Test]
        public void PathCandidate_OutranksThePlayerAnchor()
        {
            // Without this, a character following the player walks the whole way staring at them —
            // the exact behavior travel gaze exists to remove.
            TravelIntent intent = Traveling(Vector3.forward);
            GazeTargetCandidate path = Settle(in intent);

            Assert.Greater(
                path.Priority, 10,
                "The path must outrank the player anchor's default priority of 10 while travelling.");
        }

        [Test]
        public void PathCandidate_LooksFurtherAheadWhenFaster()
        {
            TravelIntent slow = Traveling(Vector3.forward, 0f);
            GazeTargetCandidate slowCandidate = Settle(in slow);
            float slowDistance = Vector3.Distance(_root.transform.position, slowCandidate.WorldPoint);

            _director.Reset();
            TravelIntent fast = Traveling(Vector3.forward, 1f);
            GazeTargetCandidate fastCandidate = Settle(in fast);
            float fastDistance = Vector3.Distance(_root.transform.position, fastCandidate.WorldPoint);

            Assert.Greater(fastDistance, slowDistance);
        }

        [Test]
        public void PathCandidate_SitsAtEyeHeight()
        {
            TravelIntent intent = Traveling(Vector3.forward);
            GazeTargetCandidate candidate = Settle(in intent);

            Assert.AreEqual(
                _root.transform.position.y + 1.6f, candidate.WorldPoint.y, 0.001f,
                "A traveller watches the road ahead, not the ground at its feet.");
        }

        [Test]
        public void PathCandidate_RampsInRatherThanSnapping()
        {
            TravelIntent intent = Traveling(Vector3.forward);

            _director.TryBuildPathCandidate(in intent, _root.transform, 1.6f, _profile, Dt, out GazeTargetCandidate first);
            float firstRelevance = first.Relevance;

            GazeTargetCandidate settled = Settle(in intent);

            Assert.Less(firstRelevance, settled.Relevance);
            Assert.AreEqual(1f, settled.Relevance, 0.01f);
        }

        // ── Arrival ──────────────────────────────────────────────────────────

        [Test]
        public void Arrival_FadesOutMonotonicallyAndThenReleases()
        {
            TravelIntent far = Traveling(
                Vector3.forward, 0.5f, remaining: 12f, TravelSubjectKind.Destination, Vector3.forward * 12f);
            Settle(in far);

            float previous = float.MaxValue;
            bool released = false;

            // Walk the remaining distance down through the arrival window.
            for (float remaining = 12f; remaining >= 0f; remaining -= 0.1f)
            {
                TravelIntent intent = Traveling(
                    Vector3.forward, 0.5f, remaining, TravelSubjectKind.Destination, Vector3.forward * remaining);

                if (!_director.TryBuildPathCandidate(
                        in intent, _root.transform, 1.6f, _profile, Dt, out GazeTargetCandidate candidate))
                {
                    released = true;
                    break;
                }

                Assert.LessOrEqual(
                    candidate.Relevance, previous + 0.0001f,
                    "The arrival settle must be a fade, never a rise.");
                previous = candidate.Relevance;
            }

            Assert.IsTrue(released, "At the release distance the path stops being a target at all.");
        }

        [Test]
        public void FollowingSomeone_NeverArrives()
        {
            // A follow has no destination, so it must never enter the arrival fade — the character
            // would stop watching the road for the rest of the walk.
            TravelIntent intent = Traveling(
                Vector3.forward, 0.5f, float.PositiveInfinity, TravelSubjectKind.Companion, Vector3.right * 2f);

            GazeTargetCandidate candidate = Settle(in intent, 600);

            Assert.AreEqual(1f, candidate.Relevance, 0.01f);
        }

        // ── Check-ins ────────────────────────────────────────────────────────
        //
        // Every test below turns the destination glance ON first, because it is opt-in and ships
        // off. Its cadence is a real contract and worth pinning — but only for the character whose
        // author asked for it, and a test that reads zero because the whole feature is disabled
        // pins nothing at all. The default itself is pinned once, immediately below.

        /// <summary>
        ///     Turns the destination glance on. Watching the road is on by default; glancing back
        ///     at where you are going is not, and these tests are about the second one.
        /// </summary>
        private void EnableDestinationGlances()
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "enableDestinationGlances").boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>
        ///     The shipped default: no destination glances at all.
        /// </summary>
        /// <remarks>
        ///     Deliberate, and the reason the rest of this region has to opt in. The glance's
        ///     timing comes from a countdown rather than from anything the character noticed, so
        ///     it reads as an unexplained look away from the path — worst near arrival, where the
        ///     subject is close and the glance is therefore a large movement. Watching the road
        ///     is the part worth having on for everyone.
        /// </remarks>
        [Test]
        public void DestinationGlances_AreOptIn()
        {
            TravelIntent intent = Traveling(
                Vector3.forward, 0.5f, 50f, TravelSubjectKind.Destination, Vector3.forward * 50f);

            int fired = CountCheckIns(in intent, DialogueState.Idle, seconds: 60f);

            Assert.AreEqual(0, fired,
                "Destination glances ship off. A character whose author never asked for them " +
                "must never look away from the road on a countdown.");
        }

        [Test]
        public void NoSubject_NeverChecksIn()
        {
            EnableDestinationGlances();
            TravelIntent intent = Traveling(Vector3.forward);

            int fired = 0;
            for (int i = 0; i < 60 * 60; i++)
                if (_director.TickCheckIn(in intent, DialogueState.Idle, _profile, ref _random, Dt))
                    fired++;

            Assert.AreEqual(
                0, fired,
                "With nothing declared as the subject there is nothing to check on — the character just watches the road.");
        }

        [Test]
        public void Destination_ChecksInPeriodically()
        {
            EnableDestinationGlances();
            TravelIntent intent = Traveling(
                Vector3.forward, 0.5f, 50f, TravelSubjectKind.Destination, Vector3.forward * 50f);

            int fired = CountCheckIns(in intent, DialogueState.Idle, seconds: 30f);

            // 30 s at a 2.5–5 s cadence: roughly 6–12, and emphatically neither 0 nor "every tick".
            Assert.GreaterOrEqual(fired, 4);
            Assert.LessOrEqual(fired, 14);
        }

        [Test]
        public void Companion_ChecksInMoreOftenThanADestination()
        {
            EnableDestinationGlances();
            // "Someone walking with me who is also keeping an eye on me" — the whole point of the
            // separate companion cadence.
            TravelIntent destination = Traveling(
                Vector3.forward, 0.5f, 100f, TravelSubjectKind.Destination, Vector3.forward * 100f);
            int destinationChecks = CountCheckIns(in destination, DialogueState.Idle, seconds: 60f);

            _director.Reset();
            _random = new DeterministicEmbodimentRandom(1234u);

            TravelIntent companion = Traveling(
                Vector3.forward, 0.5f, float.PositiveInfinity, TravelSubjectKind.Companion, Vector3.right * 2f);
            int companionChecks = CountCheckIns(in companion, DialogueState.Idle, seconds: 60f);

            Assert.Greater(companionChecks, destinationChecks);
        }

        [Test]
        public void InConversation_ChecksInMoreOften()
        {
            EnableDestinationGlances();
            TravelIntent intent = Traveling(
                Vector3.forward, 0.5f, float.PositiveInfinity, TravelSubjectKind.Companion, Vector3.right * 2f);

            int idleChecks = CountCheckIns(in intent, DialogueState.Idle, seconds: 60f);

            _director.Reset();
            _random = new DeterministicEmbodimentRandom(1234u);
            int speakingChecks = CountCheckIns(in intent, DialogueState.Speaking, seconds: 60f);

            Assert.Greater(
                speakingChecks, idleChecks,
                "A character talking to you while walking should look at you more, not the same.");
        }

        [Test]
        public void StoppingTravel_StopsCheckIns()
        {
            EnableDestinationGlances();
            TravelIntent intent = Traveling(
                Vector3.forward, 0.5f, float.PositiveInfinity, TravelSubjectKind.Companion, Vector3.right * 2f);
            CountCheckIns(in intent, DialogueState.Idle, seconds: 10f);

            int fired = 0;
            for (int i = 0; i < 60 * 30; i++)
                if (_director.TickCheckIn(TravelIntent.None, DialogueState.Idle, _profile, ref _random, Dt))
                    fired++;

            Assert.AreEqual(0, fired);
        }

        private int CountCheckIns(in TravelIntent intent, DialogueState state, float seconds)
        {
            int fired = 0;
            int ticks = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < ticks; i++)
                if (_director.TickCheckIn(in intent, state, _profile, ref _random, Dt))
                    fired++;

            return fired;
        }

        // ── Arrival ──────────────────────────────────────────────────────────

        /// <summary>
        ///     Once the character is on top of its destination there is nothing left to check
        ///     on, and the subject is now underfoot — so a glance at it is a glance at the
        ///     floor. The check-in cadence used to TIGHTEN on approach, so the last thing a
        ///     character did before stopping was repeatedly duck its head at its own feet.
        /// </summary>
        [Test]
        public void ArrivedAtADestination_StopsCheckingIn()
        {
            EnableDestinationGlances();
            TravelIntent arrived = Traveling(
                Vector3.forward, 0.2f,
                remaining: _profile.ArrivalReleaseMeters * 0.5f,
                TravelSubjectKind.Destination, Vector3.forward * 0.5f);

            int fired = CountCheckIns(in arrived, DialogueState.Idle, seconds: 30f);

            Assert.AreEqual(0, fired,
                "A destination you are standing on is not something to check on.");
        }

        /// <summary>
        ///     The same journey, still under way, must keep checking in — the arrival rule must
        ///     not have switched the behaviour off altogether.
        /// </summary>
        [Test]
        public void StillApproaching_KeepsCheckingIn()
        {
            EnableDestinationGlances();
            TravelIntent approaching = Traveling(
                Vector3.forward, 0.5f,
                remaining: _profile.ArrivalReleaseMeters + 4f,
                TravelSubjectKind.Destination, Vector3.forward * 10f);

            int fired = CountCheckIns(in approaching, DialogueState.Idle, seconds: 30f);

            Assert.That(fired, Is.GreaterThan(0),
                "There is still road left, so there is still something to look at.");
        }

        /// <summary>
        ///     Following someone is a journey with no known end, so it never arrives and the
        ///     companion is checked on for as long as the walk lasts.
        /// </summary>
        [Test]
        public void FollowingACompanion_NeverCountsAsArrived()
        {
            EnableDestinationGlances();
            TravelIntent following = Traveling(
                Vector3.forward, 0.5f,
                remaining: float.PositiveInfinity,
                TravelSubjectKind.Companion, Vector3.forward * 2f);

            int fired = CountCheckIns(in following, DialogueState.Idle, seconds: 30f);

            Assert.That(fired, Is.GreaterThan(0),
                "A companion is worth checking on however close they are.");
        }
    }
}
