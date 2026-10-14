using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Shift;
using Convai.Modules.Gaze.Core.Solvers;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The eye budget: however little of a look the head is willing to take, the eyes are
    ///     never left resting past a small band from centre. The defect this pins was seen in
    ///     a room of three — every listener watched the speaker out of the corner of its eye
    ///     with its head facing somewhere else, because the head's share was a fraction of the
    ///     angle and the eyes were handed the rest.
    /// </summary>
    public sealed class GazeEyeRestBudgetTests
    {
        private const float Settled = 2f;
        private const float RestBand = 14f;

        private static GazeShiftPlan Solve(
            float yaw, float pitch = 0f, float age = Settled, float engagement = 1f,
            float headWillingness = 1f, float eyeRest = RestBand, bool feet = true,
            float orbitPressure = 0f, bool torso = true, bool facingOwned = false)
        {
            var measurement = new GazeShiftMeasurement(yaw, pitch, 0f, 0f);
            var capacity = new GazeLadderCapacity(
                55f, 32f, 22f, 6f, headWillingness, 35f, torsoAvailable: torso, feetAvailable: feet,
                eyeRestDegrees: eyeRest, facingOwnedElsewhere: facingOwned);
            var tuning = new GazeLadderTuning(
                headEntryDegrees: 12f, torsoEntryDegrees: 35f, feetEntryDegrees: 25f,
                headOnsetSeconds: 0.06f, torsoOnsetSeconds: 0.15f, feetOnsetSeconds: 0.25f);
            return GazeActuatorLadder.Solve(
                in measurement, in capacity, in tuning, age, engagement, orbitPressure);
        }

        /// <summary>What the eyes are left holding once the head and chest have taken their share.</summary>
        private static float EyeResidual(in GazeShiftPlan plan, float yaw) =>
            Mathf.Abs(yaw - plan.HeadYaw - plan.TorsoYaw);

        [Test]
        public void AHalfEngagedLook_DoesNotLeaveTheEyesPastTheirBudget()
        {
            // The bystander's numbers: attention engagement 0.6, head contribution 0.7.
            GazeShiftPlan plan = Solve(yaw: 45f, engagement: 0.6f, headWillingness: 0.7f);

            Assert.That(EyeResidual(in plan, 45f), Is.LessThanOrEqualTo(RestBand + 0.01f),
                "The eyes were left at the corner of the socket while the head faced elsewhere.");
        }

        [Test]
        public void WithoutTheBudget_TheSameLookLeavesTheEyesAtTheCorner()
        {
            // The instrument bites: the old proportional split is what the budget replaces.
            // No chest, as for a glance or a rig without spine bones — with one, the chest takes
            // part of the slack and the eyes land a few degrees short of the corner instead.
            GazeShiftPlan plan = Solve(
                yaw: 45f, engagement: 0.6f, headWillingness: 0.7f, eyeRest: 0f, torso: false);

            Assert.That(EyeResidual(in plan, 45f), Is.GreaterThan(RestBand + 5f));
        }

        [Test]
        public void WithoutAChest_TheBudgetStillHolds()
        {
            GazeShiftPlan plan = Solve(yaw: 45f, engagement: 0.6f, headWillingness: 0.7f, torso: false);

            Assert.That(EyeResidual(in plan, 45f), Is.LessThanOrEqualTo(RestBand + 0.01f));
        }

        [Test]
        public void ADiagonalLook_IsHeldToTheSameEccentricity()
        {
            GazeShiftPlan plan = Solve(yaw: 30f, pitch: -30f, engagement: 0.6f, headWillingness: 0.5f);

            float eyeYaw = 30f - plan.HeadYaw - plan.TorsoYaw;
            float eyePitch = -30f - plan.HeadPitch - plan.TorsoPitch;
            float eccentricity = Mathf.Sqrt(eyeYaw * eyeYaw + eyePitch * eyePitch);

            Assert.That(eccentricity, Is.LessThanOrEqualTo(RestBand + 0.01f));
        }

        [Test]
        public void BeforeTheHeadJoins_TheEyesStillCarryTheWholeLook()
        {
            // The eyes lead: the budget is a resting rule, not permission to launch the head early.
            GazeShiftPlan plan = Solve(yaw: 45f, age: 0f, engagement: 0.6f, headWillingness: 0.7f);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThan(0.5f));
        }

        [Test]
        public void ALookInsideTheBudget_IsUnchanged()
        {
            GazeShiftPlan budgeted = Solve(yaw: 12f, engagement: 0.6f, headWillingness: 0.5f);
            GazeShiftPlan unbudgeted = Solve(yaw: 12f, engagement: 0.6f, headWillingness: 0.5f, eyeRest: 0f);

            Assert.That(budgeted.HeadYaw, Is.EqualTo(unbudgeted.HeadYaw).Within(0.001f),
                "A small look is the head's willingness alone; the budget only speaks past the band.");
        }

        [Test]
        public void AWillingHead_TakesMoreThanTheBudgetDemands()
        {
            GazeShiftPlan plan = Solve(yaw: 45f);

            Assert.That(plan.HeadYaw, Is.GreaterThan(45f - RestBand + 1f),
                "The budget is a floor on the head's share, not its share.");
        }

        [Test]
        public void AWiderBudget_LetsTheEyesReachFurther()
        {
            GazeShiftPlan attention = Solve(yaw: 40f, engagement: 0.6f, headWillingness: 0.5f, eyeRest: 14f);
            GazeShiftPlan glance = Solve(yaw: 40f, engagement: 0.6f, headWillingness: 0.5f, eyeRest: 28f);

            Assert.That(EyeResidual(in glance, 40f), Is.GreaterThan(EyeResidual(in attention, 40f)));
            Assert.That(EyeResidual(in glance, 40f), Is.LessThanOrEqualTo(28f + 0.01f));
        }

        [Test]
        public void AReluctantHead_KeepsTheEyesInBudget_AndStillAsksForTheFeet()
        {
            GazeShiftPlan plan = Solve(yaw: 60f, headWillingness: 0.1f);

            Assert.That(EyeResidual(in plan, 60f), Is.LessThanOrEqualTo(RestBand + 0.01f));
            Assert.IsTrue(plan.WantsFeet,
                "A head made to turn against its will is a character that would rather turn its body.");
        }

        [Test]
        public void ALookTheWillingCascadeWouldFinishFromTheNeckUp_DoesNotAskForTheFeet()
        {
            // 45°, reluctant head: a chest left the whole look by a willing head takes ~20°,
            // leaving under the feet entry. The budget-forced head must not change that verdict
            // by shrinking the chest's share and inflating the residual the feet are asked about.
            GazeShiftPlan plan = Solve(yaw: 45f, headWillingness: 0.1f);

            Assert.IsFalse(plan.WantsFeet);
            Assert.That(EyeResidual(in plan, 45f), Is.LessThanOrEqualTo(RestBand + 0.01f));
        }

        [Test]
        public void WhileTheFeetAreBusy_TheHeadStopsAtTheNeckAndTheEyesHoldTheRest()
        {
            // The one place the budget yields, on purpose: a walking character keeps its head
            // within the neck's comfort and uses its eyes until the body is free to turn.
            GazeShiftPlan plan = Solve(yaw: 60f, feet: false, torso: false, facingOwned: true);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.LessThanOrEqualTo(35f + 0.5f));
            Assert.That(EyeResidual(in plan, 60f), Is.GreaterThan(RestBand));
        }

        [Test]
        public void AListenerThatMayNotTurnItsBody_StillTurnsItsHeadFarEnough()
        {
            // The bystander's other half: attention ships with the body turn off, and a speaker
            // 70° round used to meet a head capped at the neck's comfort angle (35°) with the
            // eyes holding the rest. Disallowed feet are not busy feet.
            GazeShiftPlan plan = Solve(yaw: 70f, engagement: 0.6f, headWillingness: 0.7f, feet: false);

            Assert.That(Mathf.Abs(plan.HeadYaw), Is.GreaterThan(40f));
            Assert.That(EyeResidual(in plan, 70f), Is.LessThanOrEqualTo(RestBand + 0.01f));
        }

        // ------------------------------------------------------------ the eyes' reach in flight

        [Test]
        public void WhileTheHeadIsInFlight_TheEyesRunAheadOnlyToTheirReach()
        {
            // A 40° target with the head still to arrive: unbounded, the eyes fly to the end of
            // their travel (35°, soft from 28°) and sit there for the second the head takes.
            ConvaiGazeProfile profile = ConvaiGazeProfile.CreateDefault();
            var root = new GameObject("Root");
            try
            {
                Transform head = NewChild(root.transform, "Head", new Vector3(0f, 1.65f, 0f));
                Transform leftEye = NewChild(head, "LeftEye", new Vector3(-0.032f, 1.7f, 0.08f));
                Transform rightEye = NewChild(head, "RightEye", new Vector3(0.032f, 1.7f, 0.08f));
                var chain = new GazeChainCalibration();
                chain.BindManual(root.transform, null, null, null, head, leftEye, rightEye);

                float reach = ConvaiGazeController.EyeReachDegrees(profile);
                Vector3 target = leftEye.position + Quaternion.Euler(0f, 40f, 0f) * Vector3.forward * 2f;

                float Settle(float reachDegrees)
                {
                    var solver = new EyeSolver();
                    var input = new EyeSolveInput
                    {
                        Chain = chain, Profile = profile, DeltaTime = 1f / 120f, TargetPoint = target,
                        HasTarget = true, Engagement = 1f, FixationLiveliness = 1f, GenerationId = 1,
                        ApplyToBones = true, ReachDegrees = reachDegrees
                    };
                    for (int i = 0; i < 120; i++) solver.Solve(in input);
                    return Mathf.Abs(solver.LeftEyeAngles.x + solver.RightEyeAngles.x) * 0.5f;
                }

                float unbounded = Settle(0f);
                float bounded = Settle(reach);

                Assert.That(unbounded, Is.GreaterThan(reach + 3f), "Sanity: without a reach the eyes go to the corner.");
                Assert.That(bounded, Is.LessThanOrEqualTo(reach + 0.5f));
                Assert.That(bounded, Is.GreaterThan(reach - 3f), "The reach is a cap, not a retreat.");
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(profile);
            }
        }

        private static Transform NewChild(Transform parent, string name, Vector3 worldPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.position = worldPosition;
            return go.transform;
        }

        [Test]
        public void AGlanceTheBodyCanFollow_RestsItsEyesFurtherOutThanAttention()
        {
            ConvaiGazeProfile profile = ScriptableObject.CreateInstance<ConvaiGazeProfile>();
            try
            {
                float glance = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, true, profile);
                float attention = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Attention, true, profile);
                float reflex = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Reflex, true, profile);

                Assert.That(attention, Is.EqualTo(profile.EyeComfortDegrees));
                Assert.That(reflex, Is.EqualTo(profile.EyeComfortDegrees));
                Assert.That(glance, Is.GreaterThan(attention));
                Assert.That(glance, Is.LessThanOrEqualTo(profile.EyeMaxYawDegrees));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     The wider band a glance gets is the eyes running ahead of a movement that will
        ///     finish. A glance the policy has already excused the body from has no such ending,
        ///     so the eyes would simply hold whatever they were handed for the whole beat — which
        ///     is the sideways stare, arriving through the exception made for glances.
        /// </summary>
        [Test]
        public void AGlanceTheBodyWillNotFollow_IsHeldToTheComfortBand()
        {
            ConvaiGazeProfile profile = ScriptableObject.CreateInstance<ConvaiGazeProfile>();
            try
            {
                float free = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, true, profile);
                float pinned = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, false, profile);

                Assert.That(pinned, Is.EqualTo(profile.EyeComfortDegrees));
                Assert.That(free, Is.GreaterThan(pinned));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>
        ///     The same rule where it is felt rather than where it is stated: a forty-degree idle
        ///     glance at somebody, with the body ruled out and the head barely willing, must end
        ///     with the eyes inside the comfort band and the head having taken the rest. Traced
        ///     through the shift director frame by frame — the share alone would not say whether
        ///     the eyes ever came back.
        /// </summary>
        [Test]
        public void ASocialGlanceTheBodyWillNotFollow_EndsWithTheEyesNearCentre()
        {
            ConvaiGazeProfile profile = ScriptableObject.CreateInstance<ConvaiGazeProfile>();
            try
            {
                // The idle glance's own numbers: a glance's head share, and the body ruled out.
                const float GlanceHeadShare = 0.7f * 0.35f;
                float budget = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, false, profile);
                float loose = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, true, profile);
                var measurement = new GazeShiftMeasurement(40f, 0f, 0f, 0f);

                float Trace(float eyeRest)
                {
                    var director = new GazeShiftDirector();
                    float residual = 0f;
                    // Two seconds at 60Hz: past the head's onset, well inside a glance's life.
                    for (int i = 0; i < 120; i++)
                    {
                        GazeShiftPlan plan = director.Plan(
                            in measurement, profile, 0.5f, GlanceHeadShare, false, false,
                            generationId: 1, deltaTime: 1f / 60f, eyeRestDegrees: eyeRest);
                        residual = Mathf.Abs(40f - plan.HeadYaw - plan.TorsoYaw);
                    }

                    return residual;
                }

                float held = Trace(budget);
                float parked = Trace(loose);

                Assert.That(held, Is.LessThanOrEqualTo(profile.EyeComfortDegrees + 0.5f),
                    "The head never took the rest, so the glance was made out of the corner of the eye.");
                Assert.That(parked, Is.GreaterThan(held + 3f),
                    "Sanity: the instrument bites — the wider band is what parked the eyes.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void AHeldGlance_BecomesALook()
        {
            // The director draws a glance's wider budget in to the comfort band under orbit
            // pressure, so a glance that is held is finished by the head like any other look.
            ConvaiGazeProfile profile = ScriptableObject.CreateInstance<ConvaiGazeProfile>();
            try
            {
                float glanceBudget = ConvaiGazeController.EyeRestBudgetFor(GazeLookNature.Glance, true, profile);
                var measurement = new GazeShiftMeasurement(40f, 0f, 0f, 0f);

                // Past the head's onset, eyes reported comfortable: the glance's own share.
                var fresh = new GazeShiftDirector();
                GazeShiftPlan atOnce = default;
                for (int i = 0; i < 30; i++)
                {
                    atOnce = fresh.Plan(
                        in measurement, profile, 1f, 0.3f, false, false, generationId: 1, deltaTime: 1f / 60f,
                        eyeRestDegrees: glanceBudget);
                }
                Assert.That(Mathf.Abs(atOnce.HeadYaw), Is.GreaterThan(1f), "The head must have joined the glance.");

                var held = new GazeShiftDirector();
                GazeShiftPlan afterHolding = default;
                for (int i = 0; i < 240; i++)
                {
                    // Report the eyes strained past the comfort band, as a held glance would.
                    afterHolding = held.Plan(
                        in measurement, profile, 1f, 0.3f, false, false, generationId: 1, deltaTime: 1f / 60f,
                        achievedEyeEccentricityDegrees: glanceBudget,
                        eyeRestDegrees: glanceBudget);
                }

                Assert.That(Mathf.Abs(afterHolding.HeadYaw), Is.GreaterThan(Mathf.Abs(atOnce.HeadYaw) + 1f),
                    "Holding a glance should draw the head in behind the eyes.");
                Assert.That(40f - afterHolding.HeadYaw, Is.LessThanOrEqualTo(profile.EyeComfortDegrees + 0.5f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
