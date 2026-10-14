using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Runtime.Animation;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    /// <summary>
    ///     The travel publisher: which source wins, when movement counts as a journey, and the two
    ///     false positives that would have a standing character behave as though it were walking.
    /// </summary>
    public sealed class ConvaiTravelIntentTests
    {
        private const float Dt = 1f / 60f;

        private GameObject _character;
        private ConvaiTravelIntent _travel;

        [SetUp]
        public void SetUp()
        {
            _character = new GameObject("character");
            _travel = _character.AddComponent<ConvaiTravelIntent>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_character != null) Object.DestroyImmediate(_character);
        }

        /// <summary>
        ///     Drives the cognition tick directly. The component is normally driven by the character's
        ///     tick scheduler, which needs a full embodiment context — irrelevant to what these
        ///     tests are about.
        /// </summary>
        private void Tick(int times = 1)
        {
            var tickable = (IEmbodimentTickable)_travel;
            for (int i = 0; i < times; i++)
                tickable.EmbodimentTick(Dt);
        }

        private TravelIntent Current => ((ITravelIntentSource)_travel).Current;

        private void MoveBy(Vector3 delta) => _character.transform.localPosition += delta;

        /// <summary>Moves at <paramref name="speed" /> m/s for <paramref name="seconds" />, ticking each frame.</summary>
        private void Walk(Vector3 direction, float speed, float seconds)
        {
            int ticks = Mathf.RoundToInt(seconds / Dt);
            for (int i = 0; i < ticks; i++)
            {
                MoveBy(direction.normalized * (speed * Dt));
                Tick();
            }
        }

        // ── Observed motion (the no-Convai-locomotion case) ──────────────────

        [Test]
        public void StandingStill_IsNotTraveling()
        {
            Tick(120);
            Assert.IsFalse(Current.IsTraveling);
            Assert.AreEqual(ConvaiTravelIntent.TravelSource.NotTraveling, _travel.Source);
        }

        [Test]
        public void MovingUnderItsOwnPower_IsNoticedWithoutAnyoneReportingIt()
        {
            // The promise that makes this a movement feature rather than a NavMesh feature: a
            // character driven by a plain script still watches where it is going.
            Walk(Vector3.forward, 1.5f, 1f);

            Assert.IsTrue(Current.IsTraveling);
            Assert.AreEqual(ConvaiTravelIntent.TravelSource.Observed, _travel.Source);
            Assert.Greater(Vector3.Dot(Current.Direction, Vector3.forward), 0.99f);
        }

        [Test]
        public void ObservedTravel_HasNoSubject_SoItOnlyWatchesTheRoad()
        {
            Walk(Vector3.forward, 1.5f, 1f);

            Assert.IsFalse(Current.HasSubject, "Nothing declared a subject, so none may be invented.");
            Assert.AreEqual(TravelSubjectKind.None, Current.SubjectKind);
        }

        [Test]
        public void ADriftBelowTheThreshold_IsNotTravel()
        {
            // Physics jitter, arrival settle, turning on the spot.
            Walk(Vector3.forward, 0.1f, 2f);

            Assert.IsFalse(Current.IsTraveling);
        }

        [Test]
        public void ASingleShove_IsNotTravel()
        {
            // One frame of displacement is a teleport or a collision, not a journey.
            MoveBy(Vector3.forward * 0.5f);
            Tick();

            Assert.IsFalse(Current.IsTraveling, "Movement has to persist before it counts.");
        }

        [Test]
        public void RidingAMovingPlatform_IsNotTravel()
        {
            // The false positive that matters. Measured in world space this is indistinguishable
            // from walking, and the character would spend the whole ride staring down a road that
            // is not there.
            var platform = new GameObject("platform");
            _character.transform.SetParent(platform.transform, worldPositionStays: true);

            try
            {
                for (int i = 0; i < 120; i++)
                {
                    platform.transform.position += Vector3.forward * (2f * Dt);
                    Tick();
                }

                Assert.IsFalse(Current.IsTraveling);
            }
            finally
            {
                Object.DestroyImmediate(platform);
            }
        }

        [Test]
        public void StoppingAfterAWalk_EndsTravel()
        {
            Walk(Vector3.forward, 1.5f, 1f);
            Assert.IsTrue(Current.IsTraveling);

            Tick(60); // standing still
            Assert.IsFalse(Current.IsTraveling);
        }

        // ── Tier resolution ──────────────────────────────────────────────────

        [Test]
        public void AReport_OutranksObservedMotion()
        {
            Walk(Vector3.forward, 1.5f, 1f);
            Assert.AreEqual(ConvaiTravelIntent.TravelSource.Observed, _travel.Source);

            _travel.ReportTravel(Vector3.right, 0.8f, 12f);
            Tick();

            Assert.AreEqual(ConvaiTravelIntent.TravelSource.Reported, _travel.Source);
            Assert.AreEqual(12f, Current.RemainingDistance, 0.001f);
        }

        [Test]
        public void AReportThatStopsBeingRepeated_Expires()
        {
            // A caller that dies mid-move must not leave the character travelling forever.
            _travel.ReportTravel(Vector3.forward, 0.5f, 20f);
            Tick();
            Assert.IsTrue(Current.IsTraveling);

            int ticks = Mathf.CeilToInt((_travel.TravelReportTimeoutSeconds + 0.2f) / Dt);
            Tick(ticks);

            Assert.IsFalse(Current.IsTraveling, "An unrefreshed report has to decay on its own.");
        }

        [Test]
        public void ClearTravel_EndsAReportImmediately()
        {
            _travel.ReportTravel(Vector3.forward, 0.5f, 20f);
            Tick();
            Assert.IsTrue(Current.IsTraveling);

            _travel.ClearTravel();
            Tick();

            Assert.IsFalse(Current.IsTraveling);
        }

        [Test]
        public void ALocomotionPush_OutranksObservedMotionAndCarriesTheDistance()
        {
            Walk(Vector3.forward, 1.5f, 1f);

            _travel.PushLocomotionState(true, Vector3.right, 0.6f, 7.5f);
            Tick();

            Assert.AreEqual(ConvaiTravelIntent.TravelSource.Locomotion, _travel.Source);
            Assert.AreEqual(7.5f, Current.RemainingDistance, 0.001f);
        }

        // ── Subject ──────────────────────────────────────────────────────────

        [Test]
        public void SettingAPlaceAsTheSubject_ReadsAsADestination()
        {
            _travel.ReportTravel(Vector3.forward, 0.5f, 10f);
            _travel.SetSubject(new Vector3(0f, 0f, 10f));
            Tick();

            Assert.AreEqual(TravelSubjectKind.Destination, Current.SubjectKind);
            Assert.AreEqual(10f, Current.SubjectPosition.z, 0.001f);
        }

        [Test]
        public void SettingAPersonAsTheSubject_ReadsAsACompanionAndTracksThem()
        {
            var companion = new GameObject("companion");
            companion.transform.position = new Vector3(3f, 0f, 0f);

            try
            {
                _travel.ReportTravel(Vector3.forward, 0.5f);
                _travel.SetSubject(companion.transform);
                Tick();

                Assert.AreEqual(TravelSubjectKind.Companion, Current.SubjectKind);
                Assert.AreEqual(3f, Current.SubjectPosition.x, 0.001f);

                companion.transform.position = new Vector3(6f, 0f, 0f);
                Tick();

                Assert.AreEqual(6f, Current.SubjectPosition.x, 0.001f);
            }
            finally
            {
                Object.DestroyImmediate(companion);
            }
        }

        [Test]
        public void ACompanionThatIsDestroyed_StopsBeingASubject()
        {
            // Otherwise the character keeps glancing at the empty spot where someone used to be.
            var companion = new GameObject("companion");
            _travel.ReportTravel(Vector3.forward, 0.5f);
            _travel.SetSubject(companion.transform);
            Tick();
            Assert.IsTrue(Current.HasSubject);

            Object.DestroyImmediate(companion);
            _travel.ReportTravel(Vector3.forward, 0.5f);
            Tick();

            Assert.IsFalse(Current.HasSubject);
        }

        // ── Direction smoothing ──────────────────────────────────────────────

        [Test]
        public void TurningACorner_EasesTheDirectionRatherThanSnappingIt()
        {
            // Fed raw, a passed path corner steps the look point far enough to trip the arbiter's
            // teleport test and fire a saccade and blink on a target that never changed.
            for (int i = 0; i < 60; i++)
            {
                _travel.ReportTravel(Vector3.forward, 0.5f);
                Tick();
            }

            Vector3 previous = Current.Direction;
            float maxStepDegrees = 0f;

            for (int i = 0; i < 60; i++)
            {
                _travel.ReportTravel(Vector3.right, 0.5f);
                Tick();

                maxStepDegrees = Mathf.Max(maxStepDegrees, Vector3.Angle(previous, Current.Direction));
                previous = Current.Direction;
            }

            Assert.Less(maxStepDegrees, 15f, "A 90-degree corner must be led into, not jumped.");
            Assert.Greater(
                Vector3.Dot(Current.Direction, Vector3.right), 0.99f,
                "...and it must still finish the turn.");
        }

        [Test]
        public void AFreshJourney_StartsOnItsOwnHeading()
        {
            // Never ramp in from the heading of a journey that already ended.
            for (int i = 0; i < 60; i++)
            {
                _travel.ReportTravel(Vector3.forward, 0.5f);
                Tick();
            }

            _travel.ClearTravel();
            Tick(60);

            _travel.ReportTravel(Vector3.back, 0.5f);
            Tick();

            Assert.Greater(Vector3.Dot(Current.Direction, Vector3.back), 0.99f);
        }
    }
}
