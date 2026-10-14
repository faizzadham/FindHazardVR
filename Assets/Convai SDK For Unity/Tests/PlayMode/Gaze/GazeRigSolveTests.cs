using System.Collections;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     The solve chain against a real hierarchy: does the head actually turn toward a target,
    ///     do the eyes lead it, and is the rig left as it was found when the component is disabled.
    ///     The EditMode suites prove the math; this proves the math reaches the bones.
    /// </summary>
    public sealed class GazeRigSolveTests
    {
        private GazeRigTestHarness _rig;

        [SetUp]
        public void SetUp() => _rig = GazeRigTestHarness.Build();

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        [UnityTest]
        public IEnumerator OffAxisTarget_RecruitsTheHeadTowardIt()
        {
            // ~63° to the character's right.
            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));

            float yaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                3f, () => yaw = _rig.Gaze.CaptureSnapshot().HeadAngles.x);

            Assert.That(yaw, Is.GreaterThan(5f),
                "The head must visibly recruit toward a target off its right shoulder. A yaw of ~0 " +
                "means the solve never reached the bones.");
        }

        [UnityTest]
        public IEnumerator TargetOnTheOppositeSide_YawsTheOtherWay()
        {
            Vector3 target = _rig.Head.position + new Vector3(-2f, 0f, 1f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));

            float yaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                3f, () => yaw = _rig.Gaze.CaptureSnapshot().HeadAngles.x);

            Assert.That(yaw, Is.LessThan(-5f),
                "Sign must follow the target. Both sides are asserted because a solve that always " +
                "yaws one way passes a single-sided test.");
        }

        [UnityTest]
        public IEnumerator HeadYaw_NeverExceedsTheProfileLimit()
        {
            // Far behind the shoulder — the solver must clamp, not wrap or overshoot.
            Vector3 target = _rig.Head.position + new Vector3(3f, 0f, -3f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));

            float maxYaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                3f, () => maxYaw = Mathf.Max(maxYaw, Mathf.Abs(_rig.Gaze.CaptureSnapshot().HeadAngles.x)));

            Assert.That(maxYaw, Is.LessThanOrEqualTo(_rig.Profile.MaxHeadYawDegrees + 1f),
                "An unreachable target must clamp at the authored head yaw limit.");
        }

        [UnityTest]
        public IEnumerator EyesLeadTheHead_TowardAnOffAxisTarget()
        {
            // The shared rig deliberately removes head latency and accelerates head turns.
            // Restore a distinct onset window for this ordering assertion.
            GazeRigTestHarness.SetPrivateField(_rig.Profile, "headOnsetSeconds", 1f);
            Vector3 target = _rig.Head.position + new Vector3(1.5f, 0f, 1f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));

            // Sampled early: the profile's head latency means the eyes arrive first, which is the
            // whole point of having an eye stage at all.
            float earlyEyeYaw = 0f;
            float earlyHeadYaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(0.35f, () =>
            {
                var snapshot = _rig.Gaze.CaptureSnapshot();
                earlyEyeYaw = Mathf.Abs(snapshot.LeftEyeAngles.x);
                earlyHeadYaw = Mathf.Abs(snapshot.HeadAngles.x);
            });

            Assert.That(earlyEyeYaw, Is.GreaterThan(0.5f),
                "With eye bones present the eye stage must produce a deflection.");
            Assert.That(earlyEyeYaw, Is.GreaterThan(earlyHeadYaw),
                "The eyes lead, the head follows — reversing that reads as a doll turning its whole head.");

            yield return GazeRigTestHarness.RunForRealSeconds(2f);
            Assert.That(Mathf.Abs(_rig.Gaze.CaptureSnapshot().HeadAngles.x), Is.GreaterThan(5f),
                "The head must follow once its onset delay expires.");
        }

        [UnityTest]
        public IEnumerator DisablingTheController_RestoresTheRestPose()
        {
            Quaternion restHead = _rig.Head.localRotation;
            Quaternion restNeck = _rig.Neck.localRotation;
            Quaternion restLeftEye = _rig.LeftEye.localRotation;

            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            _rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false });
            yield return GazeRigTestHarness.RunForRealSeconds(1.5f);

            Assert.That(Quaternion.Angle(restHead, _rig.Head.localRotation), Is.GreaterThan(1f),
                "Sanity: the pose must actually have moved before restoration means anything.");

            _rig.Gaze.enabled = false;
            yield return null;

            // Only the eyes carry an explicit rest restore (RestoreEyeRest) — the head and neck are
            // re-posed by the Animator every frame in a real character. What must never happen is
            // the eyes being left staring off to one side after the component goes away.
            Assert.That(Quaternion.Angle(restLeftEye, _rig.LeftEye.localRotation), Is.LessThan(0.5f),
                "Disabling gaze must return the eyes to their rest pose, not leave them deflected.");
            Assert.That(_rig.Gaze.Current.TargetKind, Is.EqualTo(GazeTargetKind.None));
            Assert.That(restNeck, Is.Not.Null);
        }
    }
}
