using System.Collections;
using Convai.Modules.Gaze.Components;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     Pins the module's single hardest-to-discover requirement: <b>the head bone's local +Z
    ///     must be the character's visual forward, and +Y up.</b> The docs state it, the setup
    ///     troubleshooter repeats it for generic rigs, and until now nothing tested it — so the
    ///     only way a customer found out was by pressing Play and watching the character stare
    ///     sideways.
    /// </summary>
    /// <remarks>
    ///     These tests exist to make the assumption <em>observable</em>, which is what lets the
    ///     Gaze editor window's Rig Report surface it as a pass/fail with a measured angle instead
    ///     of a paragraph of prose. A mis-oriented rig is not "broken code" — it is a content
    ///     problem the tooling has to be able to name.
    /// </remarks>
    public sealed class GazeForwardAxisTests
    {
        private GazeRigTestHarness _rig;

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        /// <summary>
        ///     The quantity the Rig Report shows: the <b>head bone's</b> forward against the
        ///     character's. Deliberately not <c>CurrentEyeRestForward</c> — that is captured from
        ///     the character root at bind time, so comparing the two is tautological and reads ~0°
        ///     on every rig including a badly mis-oriented one. Writing this test is what caught
        ///     the window doing exactly that.
        /// </summary>
        private float FacingErrorDegrees() =>
            Vector3.Angle(_rig.Head.forward, _rig.Root.transform.forward);

        [UnityTest]
        public IEnumerator ConventionalRig_HeadFacesTheSameWayAsTheCharacter()
        {
            _rig = GazeRigTestHarness.Build();
            yield return null;

            Assert.That(_rig.Gaze.Chain.IsBound, "The chain must bind before the axis means anything.");
            Assert.That(FacingErrorDegrees(), Is.LessThan(5f),
                "A rig built to the +Z-forward convention must report a head forward within a few " +
                "degrees of the character's own forward.");
        }

        [UnityTest]
        public IEnumerator MisOrientedHead_IsDetectableAsAnAngleFromCharacterForward()
        {
            // A 90° yawed head bone: the classic mis-imported rig, where +Z points out of the
            // character's ear instead of its face.
            _rig = GazeRigTestHarness.Build(headForwardLocalRotation: Quaternion.Euler(0f, 90f, 0f));
            yield return null;

            Assert.That(FacingErrorDegrees(), Is.GreaterThan(45f),
                "A mis-oriented head must be measurable as a large angle from the character's " +
                "forward. This number is what the Rig Report shows the user — if it cannot be " +
                "measured, the tooling cannot explain the problem.");
        }

        [UnityTest]
        public IEnumerator RestForward_IsNotAUsableFacingCheck()
        {
            // Pins the trap itself, so nobody reinstates the tautological check later: the chain's
            // rest forward agrees with the character even on a rig whose head is 90° out.
            _rig = GazeRigTestHarness.Build(headForwardLocalRotation: Quaternion.Euler(0f, 90f, 0f));
            yield return null;

            float restError = Vector3.Angle(
                _rig.Gaze.Chain.CurrentEyeRestForward.normalized, _rig.Root.transform.forward);

            Assert.That(restError, Is.LessThan(5f),
                "CurrentEyeRestForward is captured from the character root, so it cannot detect a " +
                "mis-oriented head. Any facing check built on it is a guaranteed false pass.");
            Assert.That(FacingErrorDegrees(), Is.GreaterThan(45f),
                "…while the head bone's own forward does detect it.");
        }

        [UnityTest]
        public IEnumerator MisOrientedHead_StillDoesNotThrow()
        {
            _rig = GazeRigTestHarness.Build(headForwardLocalRotation: Quaternion.Euler(0f, 90f, 0f));

            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            _rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false });
            yield return GazeRigTestHarness.RunForRealSeconds(2f);

            // The aim will be wrong — that is the content problem. What must not happen is the
            // solver throwing, NaN-ing the pose, or spinning the head without bound.
            Vector2 head = _rig.Gaze.CaptureSnapshot().HeadAngles;
            Assert.IsFalse(float.IsNaN(head.x) || float.IsNaN(head.y),
                "A mis-oriented rig must not produce NaN angles.");
            Assert.That(Mathf.Abs(head.x), Is.LessThanOrEqualTo(_rig.Profile.MaxHeadYawDegrees + 1f),
                "Even with a wrong forward axis the authored yaw limit must hold.");
        }
    }
}
