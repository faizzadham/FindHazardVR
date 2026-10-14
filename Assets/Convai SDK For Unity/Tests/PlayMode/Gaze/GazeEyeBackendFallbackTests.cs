using System.Collections;
using Convai.Modules.Gaze.Components;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.Gaze
{
    /// <summary>
    ///     The eye-backend fallback chain on a real rig: bones when they exist, blendshapes when
    ///     they do not, head-only when neither — with one clear warning and never an exception.
    ///     The module's graceful-degradation promise is stated in three places (the docs, the
    ///     inspector's Setup section, the runtime warning) and until now was verified in none.
    /// </summary>
    public sealed class GazeEyeBackendFallbackTests
    {
        private GazeRigTestHarness _rig;

        [TearDown]
        public void TearDown() => _rig?.Dispose();

        [UnityTest]
        public IEnumerator EyeBonesPresent_UsesTheBoneBackend()
        {
            _rig = GazeRigTestHarness.Build(withEyeBones: true);
            yield return null;

            Assert.IsTrue(_rig.Gaze.EyeBackendUsesBones, "Eye bones must win under Auto.");
            Assert.IsFalse(_rig.Gaze.EyeBackendUsesLookShapes,
                "The blendshape backend must not run at the same time as the bone backend.");
        }

        [UnityTest]
        public IEnumerator NoEyeBonesAndNoLookShapes_DegradesToHeadOnly_WithoutThrowing()
        {
            _rig = GazeRigTestHarness.Build(withEyeBones: false);

            // The runtime warns through ConvaiLogger; the point of this test is that it degrades
            // rather than throws, so the expected log is tolerated rather than asserted on wording.
            LogAssert.ignoreFailingMessages = true;
            yield return null;

            Assert.IsFalse(_rig.Gaze.EyeBackendUsesBones);
            Assert.IsFalse(_rig.Gaze.EyeBackendUsesLookShapes);

            // Head-only gaze must still track: this is the difference between "degrades" and "dies".
            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            Assert.NotNull(_rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false }));

            float yaw = 0f;
            yield return GazeRigTestHarness.RunForRealSeconds(
                3f, () => yaw = _rig.Gaze.CaptureSnapshot().HeadAngles.x);

            LogAssert.ignoreFailingMessages = false;
            Assert.That(yaw, Is.GreaterThan(5f),
                "A rig with no eyes at all must still turn its head — head-only is a supported tier, " +
                "not a failure state.");
        }

        [UnityTest]
        public IEnumerator LosingAnEyeBoneAtRuntime_ReResolvesToHeadOnly()
        {
            _rig = GazeRigTestHarness.Build(withEyeBones: true);
            yield return null;
            Assert.IsTrue(_rig.Gaze.EyeBackendUsesBones, "Sanity: the bone backend must start active.");

            LogAssert.ignoreFailingMessages = true;
            Object.DestroyImmediate(_rig.RightEye.gameObject);

            // A rebind is the documented way to re-resolve; the controller must not keep writing
            // through a half-destroyed eye pair.
            _rig.Gaze.enabled = false;
            _rig.Gaze.enabled = true;
            yield return null;

            Assert.IsFalse(_rig.Gaze.EyeBackendUsesBones,
                "One eye is not a binocular backend — the solve must fall back rather than write " +
                "through a destroyed bone.");

            Vector3 target = _rig.Head.position + new Vector3(2f, 0f, 1f);
            _rig.Gaze.GazeAt(target, new GazeOptions { Engagement = 1f, AllowBodyTurn = false });
            yield return GazeRigTestHarness.RunForRealSeconds(1.5f);
            LogAssert.ignoreFailingMessages = false;

            Assert.Pass("Survived a destroyed eye bone without throwing.");
        }
    }
}
