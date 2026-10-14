using Convai.Runtime.Animation.ProceduralPose;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime.ProceduralPose
{
    /// <summary>
    ///     Tests for <see cref="TwoBoneLegSolver" />: re-pinning a foot after a small pelvis
    ///     displacement while preserving the knee-bend side, the degenerate-leg bail path, and
    ///     the shared guard's restore round-trip on the three written bones.
    /// </summary>
    public sealed class TwoBoneLegSolverTests
    {
        private GameObject _pelvis;
        private Transform _upperLeg;
        private Transform _lowerLeg;
        private Transform _foot;

        [SetUp]
        public void SetUp()
        {
            _pelvis = new GameObject("Pelvis");

            var upperGo = new GameObject("UpperLeg");
            upperGo.transform.SetParent(_pelvis.transform, false);
            upperGo.transform.localPosition = Vector3.zero;
            _upperLeg = upperGo.transform;

            var lowerGo = new GameObject("LowerLeg");
            lowerGo.transform.SetParent(_upperLeg, false);
            // Small forward knee bend so the bend plane is well-defined (not a degenerate straight leg).
            lowerGo.transform.localPosition = new Vector3(0f, -0.45f, 0.05f);
            _lowerLeg = lowerGo.transform;

            var footGo = new GameObject("Foot");
            footGo.transform.SetParent(_lowerLeg, false);
            footGo.transform.localPosition = new Vector3(0f, -0.45f, -0.05f);
            _foot = footGo.transform;
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_pelvis);

        [Test]
        public void PelvisLateralDisplacement_RePinsFoot_PreservingKneeBendSide()
        {
            Vector3 targetFootPosition = _foot.position;
            Quaternion targetFootRotation = _foot.rotation;
            Vector3 kneeSideBefore = _lowerLeg.position - _upperLeg.position;

            // Simulate a 3cm lateral pelvis weight-shift (the write already happened upstream).
            _pelvis.transform.position += new Vector3(0.03f, 0f, 0f);

            var guard = new AnimatedAdditivePoseGuard();
            TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard);

            Assert.That(Vector3.Distance(_foot.position, targetFootPosition), Is.LessThan(5e-4f),
                "The foot must be re-pinned back to (within 0.5mm of) its pre-pelvis-move position.");
            Assert.That(Quaternion.Angle(_foot.rotation, targetFootRotation), Is.LessThan(0.1f),
                "The foot must be re-pinned back to (within 0.1°) of its pre-pelvis-move rotation.");

            Vector3 kneeSideAfter = _lowerLeg.position - _upperLeg.position;
            Assert.That(Vector3.Dot(kneeSideBefore.normalized, kneeSideAfter.normalized), Is.GreaterThan(0.7f),
                "The knee must stay on the same bend side as before the solve (no pole-vector flip).");
        }

        [Test]
        public void DegenerateStraightLeg_WithCollinearTarget_BailsWithoutWriting()
        {
            // Force a perfectly straight, collinear leg (degenerate primary bend plane) AND a
            // target that is collinear with the fallback normal too (parallel to the pelvis's
            // right axis, at hip height) — the ONE combination that leaves no recoverable bend
            // plane at all, per the solver's two-stage fallback.
            _lowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _foot.localPosition = new Vector3(0f, -0.45f, 0f);

            Quaternion upperBefore = _upperLeg.localRotation;
            Quaternion lowerBefore = _lowerLeg.localRotation;
            Quaternion footBefore = _foot.localRotation;

            Vector3 targetFootPosition = _upperLeg.position + new Vector3(0.03f, 0f, 0f);
            Quaternion targetFootRotation = _foot.rotation;

            var guard = new AnimatedAdditivePoseGuard();
            Assert.DoesNotThrow(() =>
                TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard));

            Assert.That(_upperLeg.localRotation, Is.EqualTo(upperBefore), "A fully degenerate bend plane must bail without writing the upper leg.");
            Assert.That(_lowerLeg.localRotation, Is.EqualTo(lowerBefore), "A fully degenerate bend plane must bail without writing the lower leg.");
            Assert.That(_foot.localRotation, Is.EqualTo(footBefore), "A fully degenerate bend plane must bail without writing the foot.");
        }

        [Test]
        public void ZeroLengthBone_BailsWithoutWriting()
        {
            _lowerLeg.localPosition = Vector3.zero; // Zero-length thigh segment.

            Quaternion upperBefore = _upperLeg.localRotation;

            var guard = new AnimatedAdditivePoseGuard();
            Vector3 target = _foot.position + new Vector3(0.03f, 0f, 0f);
            Assert.DoesNotThrow(() => TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, target, _foot.rotation, guard));

            Assert.That(_upperLeg.localRotation, Is.EqualTo(upperBefore), "A zero-length segment must bail without writing any bone.");
        }

        [Test]
        public void BentLeg_NormalDisplacement_Solves_ReturnsTrue_AndLandsWithinTolerance()
        {
            // Sanity-establishing positive case: a normally bent leg
            // (extension ~0.994, comfortably under the solver's own 0.995 gate) with a real
            // pelvis-scale displacement must still solve and report success.
            Vector3 targetFootPosition = _foot.position;
            Quaternion targetFootRotation = _foot.rotation;

            _pelvis.transform.position += new Vector3(0.03f, 0f, 0f);

            var guard = new AnimatedAdditivePoseGuard();
            bool wrote = TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard);

            Assert.IsTrue(wrote, "A normally bent leg with a real displacement must solve and return true.");
            Assert.That(Vector3.Distance(_foot.position, targetFootPosition), Is.LessThan(1e-3f),
                "The foot must land within 1e-3m of the target.");
        }

        [Test]
        public void BentLeg_SubFiveMillimeterDisplacement_BailsViaDisplacementGate_ReturnsFalse()
        {
            // Displacement gate: same well-bent leg as the positive case
            // above (extension comfortably under both gates), but the target is a sub-5mm,
            // sub-half-degree correction — too small to be worth a solver pass.
            Quaternion upperBefore = _upperLeg.localRotation;
            Quaternion lowerBefore = _lowerLeg.localRotation;
            Quaternion footBefore = _foot.localRotation;

            Vector3 targetFootPosition = _foot.position + new Vector3(0.002f, 0f, 0f); // 2mm, sub-5mm.
            Quaternion targetFootRotation = _foot.rotation; // 0° rotational error.

            var guard = new AnimatedAdditivePoseGuard();
            bool wrote = TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard);

            Assert.IsFalse(wrote, "A sub-5mm/sub-half-degree correction must bail via the displacement gate.");
            Assert.That(_upperLeg.localRotation, Is.EqualTo(upperBefore));
            Assert.That(_lowerLeg.localRotation, Is.EqualTo(lowerBefore));
            Assert.That(_foot.localRotation, Is.EqualTo(footBefore));
        }

        [Test]
        public void FullyExtendedLeg_ExtensionGate_BailsWithoutWriting_EvenWithARecoverableBendPlane()
        {
            // Extension gate: straighten the leg (collinear along -Y only,
            // extension ~= 1.0) so the PRIMARY bend-plane cross product is degenerate, but offset
            // the target off-axis (x) so the FALLBACK bend plane (built from the reach direction
            // and the parent's right axis) IS recoverable — a solve without the gate would still have
            // found a usable bend plane here and written the bones. The extension gate must bail
            // BEFORE that fallback is even attempted.
            _lowerLeg.localPosition = new Vector3(0f, -0.45f, 0f);
            _foot.localPosition = new Vector3(0f, -0.45f, 0f);

            Quaternion upperBefore = _upperLeg.localRotation;
            Quaternion lowerBefore = _lowerLeg.localRotation;
            Quaternion footBefore = _foot.localRotation;

            Vector3 targetFootPosition = _foot.position + new Vector3(0.02f, 0f, 0f); // 2cm, well above both gates.
            Quaternion targetFootRotation = _foot.rotation;

            var guard = new AnimatedAdditivePoseGuard();
            bool wrote = TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard);

            Assert.IsFalse(wrote,
                "A fully-extended chain (extension > 0.995) must bail via the extension gate, regardless of bend-plane recoverability.");
            Assert.That(_upperLeg.localRotation, Is.EqualTo(upperBefore));
            Assert.That(_lowerLeg.localRotation, Is.EqualTo(lowerBefore));
            Assert.That(_foot.localRotation, Is.EqualTo(footBefore));
        }

        [Test]
        public void GuardRecordsAllThreeBones_RestoreReturnsToPreSolvePose_OnAnUnRePosedRig()
        {
            Vector3 targetFootPosition = _foot.position;
            Quaternion targetFootRotation = _foot.rotation;

            Quaternion upperPreSolve = _upperLeg.localRotation;
            Quaternion lowerPreSolve = _lowerLeg.localRotation;
            Quaternion footPreSolve = _foot.localRotation;

            _pelvis.transform.position += new Vector3(0.03f, 0f, 0f);

            var guard = new AnimatedAdditivePoseGuard();
            TwoBoneLegSolver.Solve(_upperLeg, _lowerLeg, _foot, targetFootPosition, targetFootRotation, guard);

            // Sanity: the solve actually wrote something.
            bool anyChanged =
                _upperLeg.localRotation != upperPreSolve ||
                _lowerLeg.localRotation != lowerPreSolve ||
                _foot.localRotation != footPreSolve;
            Assert.IsTrue(anyChanged, "Sanity: the solve must have written at least one bone.");

            guard.RestoreStaleWrites();

            Assert.That(Quaternion.Angle(_upperLeg.localRotation, upperPreSolve), Is.LessThan(1e-4f),
                "Restoring an un-re-posed rig must unwind the upper leg back to its pre-solve rotation.");
            Assert.That(Quaternion.Angle(_lowerLeg.localRotation, lowerPreSolve), Is.LessThan(1e-4f),
                "Restoring an un-re-posed rig must unwind the lower leg back to its pre-solve rotation.");
            Assert.That(Quaternion.Angle(_foot.localRotation, footPreSolve), Is.LessThan(1e-4f),
                "Restoring an un-re-posed rig must unwind the foot back to its pre-solve rotation.");
        }
    }
}
