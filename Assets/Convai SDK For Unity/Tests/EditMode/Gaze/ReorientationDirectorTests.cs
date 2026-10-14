using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Reorientation;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class ReorientationDirectorTests
    {
        private const float Dt = 1f / 60f;

        private sealed class FakeHandler : ICharacterReorientationHandler
        {
            public int RequestCount;   // turn starts (called while not reorienting)
            public int ReaimCount;     // steering calls while a turn is in flight
            public bool Accept = true;
            public bool IsReorienting { get; set; }
            public Vector3 LastDirection;

            public bool TryReorient(Vector3 worldDirection, string reason)
            {
                LastDirection = worldDirection;
                if (IsReorienting)
                {
                    ReaimCount++;
                    return true;
                }

                RequestCount++;
                if (Accept) IsReorienting = true;
                return Accept;
            }

            public void CancelReorientation(string reason) => IsReorienting = false;
        }

        private ConvaiGazeProfile _profile;
        private ReorientationDirector _director;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new ReorientationDirector();
            _root = new GameObject("ReorientRoot");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        private GazeDirective DirectiveBehind(bool allowBodyTurn = true) => new()
        {
            Kind = GazeTargetKind.Player,
            WorldPoint = _root.transform.position - _root.transform.forward * 3f + Vector3.up * 1.6f,
            Engagement = 1f,
            HeadContribution = 1f,
            AllowBodyTurn = allowBodyTurn,
            TargetName = "Player",
            FixationLiveliness = 1f
        };

        /// <summary>
        ///     Ticks the director with an explicit ladder verdict. Whether the feet SHOULD turn
        ///     is no longer this type's decision — it belongs to the actuator ladder, and is
        ///     tested there (<c>GazeActuatorLadderTests</c>). What these tests own is what the
        ///     director does once it has been asked.
        /// </summary>
        private void TickFor(
            float seconds, FakeHandler handler, in GazeDirective directive, float yawError, bool wantsFeet = true)
        {
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                _director.Tick(handler, _profile, in directive, yawError, wantsFeet,
                    _root.transform, _root.transform, Dt, null);
        }

        [Test]
        public void LadderAsksForTheFeet_FiresTheHandlerOnce()
        {
            var handler = new FakeHandler();
            GazeDirective directive = DirectiveBehind();

            TickFor(0.5f, handler, in directive, 170f);

            Assert.That(handler.RequestCount, Is.EqualTo(1),
                "One request while the ladder keeps asking — the handler is already turning after it.");
            Assert.IsTrue(_director.IsReorienting);
        }

        [Test]
        public void LadderDoesNotAsk_NeverFires()
        {
            var handler = new FakeHandler();
            GazeDirective directive = DirectiveBehind();

            // A large raw yaw error the head and torso are covering between them: the director
            // must not form its own opinion from the error it is handed. That opinion belonged
            // to a fixed threshold here, which could not tell a comfortable head from a pinned
            // one; it now belongs to the ladder.
            TickFor(3f, handler, in directive, 170f, wantsFeet: false);

            Assert.That(handler.RequestCount, Is.EqualTo(0));
        }

        [Test]
        public void PolicyDisallowsBodyTurn_NeverFires()
        {
            var handler = new FakeHandler();
            GazeDirective directive = DirectiveBehind(allowBodyTurn: false);

            // The policy is applied where the verdict is formed — the ladder reads the same flag
            // and treats it as reluctance, so a state that would rather not turn declines every
            // turn it can decline and asks for the feet only for a target the head and eyes
            // cannot reach. What this director owes it is obedience: no turn without a verdict.
            TickFor(3f, handler, in directive, 170f, wantsFeet: false);

            Assert.That(handler.RequestCount, Is.EqualTo(0),
                "States like Thinking gate body turns off via policy.");
        }

        [Test]
        public void WhileHandlerTurning_ReaimsInsteadOfRestarting()
        {
            var handler = new FakeHandler();
            GazeDirective directive = DirectiveBehind();

            TickFor(0.5f, handler, in directive, 170f);
            Assert.That(handler.RequestCount, Is.EqualTo(1));

            // While the handler reports an in-flight turn the director never fires a new
            // turn through the hold-hysteresis path — it steers the existing one toward
            // the live target at a throttled cadence instead.
            TickFor(2f, handler, in directive, 170f);
            Assert.That(handler.RequestCount, Is.EqualTo(1),
                "No duplicate turn starts while one is in flight.");
            Assert.That(handler.ReaimCount, Is.GreaterThan(3),
                "The in-flight turn is periodically re-aimed at the live target.");
        }

        [Test]
        public void NoHandler_ProceduralFallbackRotatesRoot()
        {
            GazeDirective directive = DirectiveBehind();
            float initialYaw = _root.transform.eulerAngles.y;

            // 5.1 s, not 3.3: the same slack over the min-jerk clock (1.875 x 180 / peak) at the
            // shipped 90 deg-per-second turn speed that 3.3 s gave it at the old 140.
            TickFor(5.1f, null, in directive, 170f);

            Vector3 toTarget = directive.WorldPoint - _root.transform.position;
            toTarget.y = 0f;
            float remaining = Vector3.Angle(_root.transform.forward, toTarget.normalized);
            Assert.That(remaining, Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees + 1f),
                "The procedural fallback must rotate the character to face the target.");
            Assert.That(Mathf.Abs(Mathf.DeltaAngle(initialYaw, _root.transform.eulerAngles.y)), Is.GreaterThan(90f));
        }

        /// <summary>
        ///     A turn toward a target that is not moving must land on its own clock.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The two tests around this one assert the turn eventually completes, and they
        ///         are generous about when. This one is about the clock, because the way it broke
        ///         was invisible to a generous deadline: the driver is re-aimed every frame of a
        ///         turn whether or not the target moved, and the re-aim's guard —
        ///         <c>TurnSeconds(remaining) &gt; Remaining</c> — is satisfied by a healthy
        ///         minimum-jerk movement for most of its life, because the profile leaves at rest
        ///         so the angle shrinks more slowly than the clock. Every frame therefore reset
        ///         the clock to a full fresh duration, the turn never left its opening phase, and
        ///         a 180° pivot planned for 2.4 s covered 65° in 3.3 s and was still going.
        ///     </para>
        ///     <para>
        ///         Nothing about that reads as broken frame to frame: velocity carries across a
        ///         re-plan, so the motion stays smooth and correctly shaped the whole time. Only
        ///         the clock catches it, which is why the bound here is a duration and not a pose.
        ///     </para>
        /// </remarks>
        [Test]
        public void TurnTowardAStationaryTarget_LandsOnItsPlannedClock()
        {
            GazeDirective directive = DirectiveBehind();

            // 1.875 x 180 deg / 90 deg-per-second peak = ~3.75 s planned. Allowing 4.5 s is
            // slack for the completion tolerance and the settle test, and is still far inside
            // the "re-plans every frame" behaviour, which never finishes at all.
            // (Was 2.9 s against a 140 °/s turn; the same proportional slack over the shipped
            // 90 °/s default, which is the only number that moved.)
            const float plannedSeconds = 4.5f;

            int steps = Mathf.CeilToInt(plannedSeconds / Dt);
            for (int i = 0; i < steps; i++)
                _director.Tick(null, _profile, in directive, 170f, true,
                    _root.transform, _root.transform, Dt, null);

            Vector3 toTarget = directive.WorldPoint - _root.transform.position;
            toTarget.y = 0f;
            float remaining = Vector3.Angle(_root.transform.forward, toTarget.normalized);

            Assert.That(remaining, Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees + 1f),
                $"{remaining:0.0}° still to go after {plannedSeconds:0.0} s on a turn planned for " +
                "about 3.8 s. The turn is being re-planned rather than executed — see " +
                "ProceduralReorientationDriver.Retarget: only a goal that actually moved may " +
                "reset the clock.");
            Assert.IsFalse(_director.IsReorienting, "A landed turn must report itself finished.");
        }

        [Test]
        public void HandlerRefusal_FallsBackToProcedural()
        {
            var handler = new FakeHandler { Accept = false };
            GazeDirective directive = DirectiveBehind();

            // 5.1 s for the same reason as NoHandler_ProceduralFallbackRotatesRoot.
            TickFor(5.1f, handler, in directive, 170f);

            Assert.That(handler.RequestCount, Is.GreaterThanOrEqualTo(1));
            Vector3 toTarget = directive.WorldPoint - _root.transform.position;
            toTarget.y = 0f;
            float remaining = Vector3.Angle(_root.transform.forward, toTarget.normalized);
            Assert.That(remaining, Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees + 1f),
                "When the handler refuses, the procedural driver must still complete the turn.");
        }

        [Test]
        public void AnimatedTurnResidual_IsSettledProcedurally()
        {
            var handler = new FakeHandler();
            GazeDirective directive = DirectiveBehind();

            TickFor(0.5f, handler, in directive, 170f);
            Assert.That(handler.RequestCount, Is.EqualTo(1));

            // The animated turn ends 30° short of the live target (it kept moving). The head
            // and torso can absorb that much between them, so the ladder stops asking for the
            // feet — which is precisely the band the settle swivel exists to close.
            Vector3 toTarget = directive.WorldPoint - _root.transform.position;
            toTarget.y = 0f;
            _root.transform.rotation = Quaternion.LookRotation(
                Quaternion.AngleAxis(30f, Vector3.up) * toTarget.normalized, Vector3.up);
            handler.IsReorienting = false;

            TickFor(2f, handler, in directive, 30f, wantsFeet: false);

            Assert.That(handler.RequestCount, Is.EqualTo(1),
                "A residual the ladder no longer wants the feet for must not fire a second " +
                "animated turn — it is settled procedurally instead.");
            toTarget = directive.WorldPoint - _root.transform.position;
            toTarget.y = 0f;
            float remaining = Vector3.Angle(_root.transform.forward, toTarget.normalized);
            Assert.That(remaining, Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees + 1f),
                "The residual left by the animated turn is closed by the procedural settle.");
        }

        [Test]
        public void TargetReleasedMidTurn_CancelsProceduralTurn()
        {
            GazeDirective directive = DirectiveBehind();
            TickFor(0.6f, null, in directive, 170f);
            Assert.IsTrue(_director.IsReorienting, "Procedural turn should be in flight.");

            GazeDirective released = GazeDirective.Disengaged;
            TickFor(0.5f, null, in released, 0f);

            Assert.IsFalse(_director.IsReorienting,
                "Releasing the target must stop the fallback turn instead of spinning forever.");
        }
    }
}
