using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Reorientation;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     "May turn its body: off" is a preference, not a physical impossibility. A state that
    ///     would rather not turn — Thinking, Settling, a glance — declines the two turns that
    ///     are a matter of taste, and still turns for a target its head and eyes cannot reach
    ///     between them.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Read as a ban, the flag produced exactly the pose it was meant to avoid. With the
    ///         player 95° round while the character was Thinking, nothing downstream could clamp
    ///         the head — the feet rung was switched off, so the residual it exists to absorb had
    ///         nowhere to go — and the head saturated at its limit with the whole unreachable
    ///         remainder parked in the eyes: 31° at the corner of the socket, held for the length
    ///         of the state.
    ///     </para>
    ///     <para>
    ///         Reluctance is expressed as delay, not as refusal, so the assertions below are
    ///         about <i>when</i> the feet join as much as whether they do.
    ///     </para>
    /// </remarks>
    public sealed class GazeReluctantBodyTurnTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>
        ///     Past the ceiling of head (55°) + chest (22°) + the eyes' comfort band (14°): the
        ///     angle measured in the sample scene, and the one the head cannot cover.
        /// </summary>
        private const float UnreachableYaw = 95f;

        /// <summary>Comfortably inside the head's own range — no rung below it is needed.</summary>
        private const float ReachableYaw = 40f;

        private ConvaiGazeProfile _profile;
        private GameObject _root;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _root = new GameObject("ReluctantTurnRoot");
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(_root);
            Object.DestroyImmediate(_profile);
        }

        /// <summary>The point a target that many degrees off the character's forward sits at.</summary>
        private Vector3 TargetAt(float yawDegrees) =>
            _root.transform.position +
            Quaternion.Euler(0f, yawDegrees, 0f) * Vector3.forward * 3f +
            Vector3.up * 1.6f;

        private GazeDirective Directive(Vector3 worldPoint, bool allowBodyTurn) => new()
        {
            Kind = GazeTargetKind.Player,
            WorldPoint = worldPoint,
            Engagement = 1f,
            HeadContribution = 1f,
            AllowBodyTurn = allowBodyTurn,
            TargetName = "Player",
            FixationLiveliness = 1f
        };

        /// <summary>Signed yaw (degrees) from where the character faces to where the target is.</summary>
        private float RequiredYawTo(Vector3 worldPoint)
        {
            Vector3 flat = worldPoint - _root.transform.position;
            flat.y = 0f;
            return Vector3.SignedAngle(_root.transform.forward, flat, Vector3.up);
        }

        /// <summary>What the eyes are left holding once the head and chest have taken their share.</summary>
        private static float EyeResidual(in GazeShiftPlan plan, float requiredYaw) =>
            Mathf.Abs(requiredYaw - plan.HeadYaw - plan.TorsoYaw);

        /// <summary>
        ///     One run of the live chain: plan the shift, hand the verdict to the reorientation
        ///     director, let the procedural fallback rotate the root, and re-measure next frame.
        /// </summary>
        /// <param name="allowBodyTurn">The state policy's flag, as the directive carries it.</param>
        /// <param name="targetYaw">Where the target sits, relative to the character's start facing.</param>
        /// <param name="seconds">How long to run.</param>
        /// <param name="turnBody">
        ///     False to plan the shift without ever executing a turn — the negative control that
        ///     shows what the eyes are left holding when the feet never join.
        /// </param>
        /// <param name="firstFeetSecond">
        ///     Elapsed seconds at the first frame the ladder asked for the feet, or -1 if it
        ///     never did.
        /// </param>
        /// <param name="finalEyeResidual">Eye eccentricity on the last planned frame.</param>
        /// <param name="bodyTurnForbidden">
        ///     The look's OWN refusal, as <see cref="GazeDirective.BodyTurnForbidden" /> carries
        ///     it — a scripted request that asked for no turn. Unlike
        ///     <paramref name="allowBodyTurn" />, which is a dialogue state's preference, this is
        ///     an instruction, and the ladder must never overrule it.
        /// </param>
        private void Run(
            bool allowBodyTurn,
            float targetYaw,
            float seconds,
            bool turnBody,
            out float firstFeetSecond,
            out float finalEyeResidual,
            bool bodyTurnForbidden = false)
        {
            var shift = new GazeShiftDirector();
            var reorientation = new ReorientationDirector();
            Vector3 worldPoint = TargetAt(targetYaw);

            firstFeetSecond = -1f;
            finalEyeResidual = 0f;

            float achievedEye = 0f;
            float achievedHead = 0f;
            int steps = Mathf.CeilToInt(seconds / Dt);

            for (int i = 0; i < steps; i++)
            {
                float requiredYaw = RequiredYawTo(worldPoint);
                var measurement = new GazeShiftMeasurement(requiredYaw, 0f, 0f, 0f);

                GazeShiftPlan plan = shift.Plan(
                    in measurement, _profile,
                    engagement: 1f,
                    headContribution: 1f,
                    torsoAvailable: true,
                    feetAvailable: allowBodyTurn,
                    generationId: 1,
                    deltaTime: Dt,
                    achievedEyeEccentricityDegrees: achievedEye,
                    achievedHeadYawDegrees: achievedHead,
                    bodyTurnForbidden: bodyTurnForbidden);

                if (plan.WantsFeet && firstFeetSecond < 0f) firstFeetSecond = i * Dt;

                achievedEye = EyeResidual(in plan, requiredYaw);
                achievedHead = plan.HeadYaw;
                finalEyeResidual = achievedEye;

                if (!turnBody) continue;

                GazeDirective directive = Directive(worldPoint, allowBodyTurn);
                reorientation.Tick(
                    null, _profile, in directive, requiredYaw, plan.WantsFeet,
                    _root.transform, _root.transform, Dt, null);
            }
        }

        [Test]
        public void NoTurnState_UnreachableTarget_RecruitsTheFeetLateAndBringsTheEyesBack()
        {
            Run(allowBodyTurn: false, UnreachableYaw, seconds: 5f, turnBody: true,
                out float firstFeetSecond, out float finalEyeResidual);

            Assert.That(firstFeetSecond, Is.GreaterThanOrEqualTo(0f),
                "A state that would rather not turn still has to turn for a target its head and " +
                "eyes cannot reach — otherwise the unreachable remainder lands in the eyes and " +
                "stays there for the length of the state.");

            float reluctantOnset =
                _profile.FeetOnsetSeconds * GazeActuatorLadder.ReluctantFeetOnsetMultiple;
            Assert.That(firstFeetSecond, Is.GreaterThanOrEqualTo(reluctantOnset - Dt),
                $"The turn was not reluctant: the feet joined at {firstFeetSecond:0.000}s, which is " +
                $"no later than a willing character's own {_profile.FeetOnsetSeconds:0.00}s onset.");

            Assert.That(finalEyeResidual, Is.LessThanOrEqualTo(_profile.EyeComfortDegrees),
                $"The turn happened but the eyes were still holding {finalEyeResidual:0.0}° of it.");
        }

        /// <summary>
        ///     The instrument bites. Same look, same state, with the profile's body turn switched
        ///     off — which is the one refusal that is not a preference — so the feet never join
        ///     and the eyes are left exactly where the defect left them.
        /// </summary>
        [Test]
        public void WithoutTheBodyTurn_TheSameLookParksTheEyesAtTheCorner()
        {
            var serialized = new SerializedObject(_profile);
            serialized.FindProperty("bodyTurn.enableBodyTurn").boolValue = false;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.IsFalse(_profile.EnableBodyTurn, "Fixture: the profile's body turn is off.");

            Run(allowBodyTurn: false, UnreachableYaw, seconds: 5f, turnBody: true,
                out float firstFeetSecond, out float finalEyeResidual);

            Assert.That(firstFeetSecond, Is.LessThan(0f),
                "A profile that never turns its body must never be talked into it.");
            // Measured at 18.0°: what is left of 95° once the head and chest have both committed
            // as far as they physically go. The live defect was worse (31°, because Thinking
            // engages at 0.7 and the chest takes less of the slack) — this is the floor of it.
            Assert.That(finalEyeResidual, Is.GreaterThan(_profile.EyeComfortDegrees),
                $"Without the turn the eyes hold the unreachable remainder ({finalEyeResidual:0.0}° " +
                $"against a {_profile.EyeComfortDegrees:0}° band) — this is the defect.");
        }

        [Test]
        public void NoTurnState_ReachableTarget_NeverRecruitsTheFeet()
        {
            Run(allowBodyTurn: false, ReachableYaw, seconds: 3f, turnBody: true,
                out float firstFeetSecond, out float finalEyeResidual);

            Assert.That(firstFeetSecond, Is.LessThan(0f),
                "The head covers this look on its own. A state that would rather not turn its " +
                "body must not turn it for an angle it can reach.");
            Assert.That(_root.transform.eulerAngles.y, Is.EqualTo(0f).Within(0.01f),
                "Nothing asked for the feet, so the character must not have moved.");
            Assert.That(finalEyeResidual, Is.LessThanOrEqualTo(_profile.EyeComfortDegrees));
        }

        /// <summary>
        ///     The line between a preference and an instruction. A dialogue state that would
        ///     rather not turn is talked into it by a target beyond reach — that is the test
        ///     above. A look that FORBIDS the turn is not: a scripted request that asked for no
        ///     body turn gets no body turn, however far round the target is and however long it
        ///     is held.
        /// </summary>
        /// <remarks>
        ///     Both halves run the same unreachable look for the same eight seconds — nearly
        ///     twice the horizon of the reluctant test above, and far past the reluctant onset
        ///     the feet would otherwise join at — so the only difference between them is the
        ///     argument itself. Without the forbidding half the reluctance rule would look
        ///     unconditional; without the permitting half the assertion could be passed by a
        ///     ladder that had simply stopped recruiting the feet at all.
        /// </remarks>
        [Test]
        public void ScriptedNoTurnRequest_BeyondReach_NeverRecruitsTheFeet()
        {
            Run(allowBodyTurn: false, UnreachableYaw, seconds: 8f, turnBody: true,
                out float forbiddenFeetSecond, out _, bodyTurnForbidden: true);

            Assert.That(forbiddenFeetSecond, Is.LessThan(0f),
                $"A look that forbids the body turn must never be talked into one: the ladder " +
                $"asked for the feet at {forbiddenFeetSecond:0.000}s. Reluctance is overruled by " +
                $"a target beyond reach; a scripted refusal is not.");
            Assert.That(_root.transform.eulerAngles.y, Is.EqualTo(0f).Within(0.01f),
                "Nothing asked for the feet, so the character must not have moved.");

            // The instrument bites: same look, same horizon, the flag off. The root is still
            // unturned — the run above never asked for the feet — and this run plans without
            // executing a turn, so the second measurement starts from the same geometry.
            Run(allowBodyTurn: false, UnreachableYaw, seconds: 8f, turnBody: false,
                out float reluctantFeetSecond, out _, bodyTurnForbidden: false);

            Assert.That(reluctantFeetSecond, Is.GreaterThanOrEqualTo(0f),
                "Without the scripted refusal the reluctant turn must still fire — otherwise " +
                "the assertion above is passed by a ladder that stopped recruiting the feet for " +
                "its own reasons, and the new argument is not what made the difference.");
        }

        [Test]
        public void TurnAllowed_HeldNeck_StillTurnsForComfortAlone()
        {
            // Nothing is unmet at 40° — the head covers it — so the only thing that can ask for
            // the feet here is the neck wanting relief after holding the turn. That reason is a
            // preference, and it belongs to the states that allow the turn.
            Run(allowBodyTurn: true, ReachableYaw, seconds: 3f, turnBody: false,
                out float firstFeetSecond, out _);

            Assert.That(firstFeetSecond, Is.GreaterThanOrEqualTo(0f),
                "A character that will turn its body turns to face somebody it has been " +
                "watching over its shoulder — the comfort turn is unchanged.");
        }

        /// <summary>
        ///     The state edge. A turn in flight for a target that is still unreachable must not
        ///     be cancelled because the dialogue state moved to one that would rather not turn —
        ///     the character would be left facing halfway between where it was and where it was
        ///     going.
        /// </summary>
        [Test]
        public void TurnInFlight_SurvivesTheStateEdgeThatForbidsIt()
        {
            var reorientation = new ReorientationDirector();
            Vector3 worldPoint = TargetAt(UnreachableYaw);

            GazeDirective willing = Directive(worldPoint, allowBodyTurn: true);
            for (int i = 0; i < 30; i++)
                reorientation.Tick(null, _profile, in willing, RequiredYawTo(worldPoint), true,
                    _root.transform, _root.transform, Dt, null);

            Assert.IsTrue(reorientation.IsReorienting, "Fixture: the turn must be in flight.");
            float coveredBeforeTheEdge = Mathf.Abs(
                Mathf.DeltaAngle(0f, _root.transform.eulerAngles.y));

            // The state moves to Thinking. The ladder keeps asking for the feet, because the
            // target is still out of the head's and eyes' reach.
            GazeDirective reluctant = Directive(worldPoint, allowBodyTurn: false);
            for (int i = 0; i < 240; i++)
                reorientation.Tick(null, _profile, in reluctant, RequiredYawTo(worldPoint), true,
                    _root.transform, _root.transform, Dt, null);

            float remaining = Mathf.Abs(RequiredYawTo(worldPoint));
            Assert.That(remaining,
                Is.LessThanOrEqualTo(_profile.BodyTurnCompletionToleranceDegrees + 1f),
                $"The turn was abandoned on the state edge: it had covered " +
                $"{coveredBeforeTheEdge:0}° of {UnreachableYaw:0}° and was left {remaining:0}° short.");
        }
    }
}
