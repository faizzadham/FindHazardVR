using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class SearchDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const float DefaultMaxSearchSeconds = 3f;

        // Observer (character gaze origin) at the world origin; the player starts 3 m ahead
        // along +Z (the character faces +Z) so the view direction is non-degenerate.
        private static readonly Vector3 Observer = Vector3.zero;
        private static readonly Vector3 StartPointFacingZ = new(0f, 1.6f, 3f);

        private SearchDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _director = new SearchDirector();
            _random = new DeterministicEmbodimentRandom(101u);
        }

        /// <summary>Mirrors <see cref="SearchDirector" />'s internal lateral-axis derivation for assertions.</summary>
        private static Vector3 ComputeLateralAxis(Vector3 lastPoint, Vector3 observerPosition)
        {
            Vector3 viewDir = lastPoint - observerPosition;
            viewDir.y = 0f;
            return viewDir.sqrMagnitude > 0.0001f
                ? Vector3.Cross(Vector3.up, viewDir.normalized)
                : Vector3.right;
        }

        /// <summary>Simulates continuous engagement with a player moving at a constant velocity.</summary>
        private Vector3 RunEngagement(
            float seconds, Vector3 startPoint, Vector3 velocity, Vector3 observerPosition,
            DialogueState state = DialogueState.Listening)
        {
            Vector3 point = startPoint;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
            {
                point += velocity * Dt;
                _director.Tick(true, true, point, observerPosition, state, true, DefaultMaxSearchSeconds, Dt, ref _random);
            }
            return point;
        }

        [Test]
        public void LossAfterSufficientEngagement_TriggersSearch()
        {
            RunEngagement(2.2f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);

            bool active = _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);

            Assert.IsTrue(active, "A loss after >=2s engagement must trigger a search.");
            Assert.IsTrue(_director.SearchActive);
        }

        [Test]
        public void LossAfterInsufficientEngagement_DoesNotSearch()
        {
            RunEngagement(1.0f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);

            bool active = false;
            for (int i = 0; i < 60; i++)
                active |= _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);

            Assert.IsFalse(active, "A loss after <2s engagement must never trigger a search.");
        }

        [Test]
        public void IdleState_NeverSearches()
        {
            RunEngagement(2.5f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer, DialogueState.Listening);

            bool active = false;
            for (int i = 0; i < 60; i++)
                active |= _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Idle, true, DefaultMaxSearchSeconds, Dt, ref _random);

            Assert.IsFalse(active, "Search must never run while the dialogue state is Idle.");
        }

        [Test]
        public void SearchCompletesWithinMaxDuration_ThenReleases()
        {
            const float maxSeconds = 1f;
            RunEngagement(2.5f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);
            _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, maxSeconds, Dt, ref _random);
            Assert.IsTrue(_director.SearchActive, "Search should begin on the loss tick.");

            bool released = false;
            int steps = Mathf.CeilToInt((maxSeconds + 0.5f) / Dt);
            for (int i = 0; i < steps; i++)
            {
                _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, maxSeconds, Dt, ref _random);
                if (_director.JustReleased) released = true;
            }

            Assert.IsTrue(released, "Search must release within the max duration cap.");
            Assert.IsFalse(_director.SearchActive);
        }

        [Test]
        public void ReappearingTarget_AbortsImmediately()
        {
            RunEngagement(2.5f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);
            _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
            for (int i = 0; i < 10; i++)
                _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
            Assert.IsTrue(_director.SearchActive, "Precondition: search must still be active.");

            bool active = _director.Tick(true, true, new Vector3(5f, 1.6f, 3f), Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);

            Assert.IsFalse(active, "Reappearance must abort the search on the same tick.");
            Assert.IsFalse(_director.SearchActive);
            Assert.IsTrue(_director.JustReleased);
        }

        [Test]
        public void FixationOffsets_StayWithinYawEnvelope_AndVelocityBiased()
        {
            Vector3 lastPoint = RunEngagement(2.5f, StartPointFacingZ, new Vector3(2f, 0f, 0f), Observer); // moving +x
            _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);

            Vector3 lateralAxis = ComputeLateralAxis(lastPoint, Observer);
            float expectedSign = Mathf.Sign(Vector3.Dot(new Vector3(2f, 0f, 0f), lateralAxis));

            Assert.IsTrue(_director.SearchActive);
            Assert.AreEqual(expectedSign, Mathf.Sign(_director.CurrentYawOffsetDegrees),
                "First fixation must be biased toward Sign(Dot(velocity, lateralAxis)).");
            Assert.LessOrEqual(Mathf.Abs(_director.CurrentYawOffsetDegrees), 15f);

            for (int i = 0; i < 200 && _director.SearchActive; i++)
            {
                _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
                Assert.LessOrEqual(Mathf.Abs(_director.CurrentYawOffsetDegrees), 15f,
                    "Every fixation offset must stay within the +/-15 degree yaw envelope.");
            }

            // Reversed velocity should bias the first fixation the other way.
            var director2 = new SearchDirector();
            var random2 = new DeterministicEmbodimentRandom(101u);
            Vector3 point = StartPointFacingZ;
            Vector3 negVelocity = new(-2f, 0f, 0f);
            int steps = Mathf.CeilToInt(2.5f / Dt);
            for (int i = 0; i < steps; i++)
            {
                point += negVelocity * Dt;
                director2.Tick(true, true, point, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref random2);
            }
            director2.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref random2);

            Vector3 lateralAxis2 = ComputeLateralAxis(point, Observer);
            float expectedSign2 = Mathf.Sign(Vector3.Dot(negVelocity, lateralAxis2));
            Assert.AreEqual(expectedSign2, Mathf.Sign(director2.CurrentYawOffsetDegrees),
                "Reversed velocity must flip the first fixation's bias sign.");
        }

        [Test]
        public void FixationOffsets_AreLateralToFacingDirection_NotWorldX()
        {
            // The character faces world +X (target sits ~3 m along +X from the observer), and
            // the player drifts slightly along Z while exiting. A world-axis-locked
            // ("Vector3.right") search would put the whole offset on X (depth, invisible); the
            // character-relative lateral axis must instead put the offset predominantly on Z.
            // The Z drift is kept small relative to the 3 m X distance so the view direction
            // stays close to +X, while still giving the velocity a well-defined lateral sign.
            Vector3 startPoint = new(3f, 1.6f, 0f);
            Vector3 velocity = new(0f, 0f, 0.05f);
            Vector3 lastPoint = RunEngagement(2.5f, startPoint, velocity, Observer);

            _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
            Assert.IsTrue(_director.SearchActive);

            Vector3 lateralAxis = ComputeLateralAxis(lastPoint, Observer);
            Assert.Less(Mathf.Abs(lateralAxis.x), 0.15f, "Precondition: facing near +X means the lateral axis is close to X-free.");
            Assert.Greater(Mathf.Abs(lateralAxis.z), 0.9f, "Precondition: facing near +X means the lateral axis is close to pure Z.");

            Vector3 offset = _director.SearchPoint - lastPoint;
            Assert.Less(Mathf.Abs(offset.x), 0.05f, "Search offset must not leak onto the depth (X) axis when facing +X.");
            Assert.Greater(Mathf.Abs(offset.z), 0.1f, "Search offset must be lateral (Z) when the character faces +X.");

            float expectedSign = Mathf.Sign(Vector3.Dot(velocity, lateralAxis));
            Assert.AreEqual(expectedSign, Mathf.Sign(_director.CurrentYawOffsetDegrees),
                "Bias sign must follow Sign(Dot(velocity, lateralAxis)), not Sign(velocity.x).");
        }

        [Test]
        public void Abort_ImmediatelyEndsSearchAndDoesNotReengageForSameLossEvent()
        {
            RunEngagement(2.5f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);
            _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
            Assert.IsTrue(_director.SearchActive, "Precondition: search must be active before aborting.");

            _director.Abort();

            Assert.IsFalse(_director.SearchActive, "Abort must end the search immediately.");
            Assert.IsTrue(_director.JustReleased, "Abort must report a release the tick it fires.");

            // A scripted/glance-tier target taking over (e.g. joint attention) should not let
            // the aborted search silently resume for the same loss event.
            for (int i = 0; i < 60; i++)
            {
                bool active = _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref _random);
                Assert.IsFalse(active, "The same loss event must not re-trigger a search after an abort.");
            }
        }

        [Test]
        public void Abort_WhileIdle_IsNoOp()
        {
            _director.Abort();
            Assert.IsFalse(_director.SearchActive);
            Assert.IsFalse(_director.JustReleased, "Aborting when nothing is active must not report a release.");
        }

        [Test]
        public void DisabledFlag_SuppressesSearch()
        {
            RunEngagement(2.5f, StartPointFacingZ, new Vector3(1f, 0f, 0f), Observer);

            bool active = false;
            for (int i = 0; i < 60; i++)
                active |= _director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, false, DefaultMaxSearchSeconds, Dt, ref _random);

            Assert.IsFalse(active, "Disabled flag must suppress the search entirely.");
        }

        [Test]
        public void Deterministic_UnderFixedSeed()
        {
            (bool active, Vector3 point, float yaw)[] Run(uint seed)
            {
                var director = new SearchDirector();
                var random = new DeterministicEmbodimentRandom(seed);
                Vector3 point = StartPointFacingZ;
                int engageSteps = Mathf.CeilToInt(2.5f / Dt);
                for (int i = 0; i < engageSteps; i++)
                {
                    point += new Vector3(1.5f, 0f, 0f) * Dt;
                    director.Tick(true, true, point, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref random);
                }

                var results = new (bool, Vector3, float)[120];
                for (int i = 0; i < results.Length; i++)
                {
                    bool active = director.Tick(false, false, Vector3.zero, Observer, DialogueState.Listening, true, DefaultMaxSearchSeconds, Dt, ref random);
                    results[i] = (active, director.SearchPoint, director.CurrentYawOffsetDegrees);
                }
                return results;
            }

            var a = Run(4242u);
            var b = Run(4242u);

            Assert.AreEqual(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.AreEqual(a[i].active, b[i].active, $"Mismatch at tick {i}.");
                Assert.AreEqual(a[i].point, b[i].point, $"Point mismatch at tick {i}.");
                Assert.AreEqual(a[i].yaw, b[i].yaw, $"Yaw mismatch at tick {i}.");
            }
        }
    }
}
