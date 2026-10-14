using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Pins the log-once contract across rig rebinds. Before T1, <c>ValidateRig</c> ran only on
    ///     first initialization, so a runtime rebind to a binding without a Head mapping stopped
    ///     gaze with no warning at all. It now runs on every rebind — which makes the opposite
    ///     failure (a rebind loop flooding the console) the thing that has to be pinned.
    /// </summary>
    public sealed class GazeRigRebindTests
    {
        /// <summary>Minimal binding stub — identity is all the latch decision reads.</summary>
        private sealed class FakeBinding : IStandardRigBinding
        {
            public Transform Root => null;

            public IReadOnlyList<SkinnedMeshRenderer> FacialMeshes => System.Array.Empty<SkinnedMeshRenderer>();

            public RigConvention DetectedConvention => RigConvention.Unknown;

            public bool TryGetBone(StandardBone semantic, out Transform bone)
            {
                bone = null;
                return false;
            }

            public bool TryGetBlendshape(
                StandardBlendshape semantic, out SkinnedMeshRenderer mesh, out int blendshapeIndex)
            {
                mesh = null;
                blendshapeIndex = -1;
                return false;
            }
        }

        [Test]
        public void UsableRig_NeverWarns()
        {
            Assert.IsFalse(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: true, current: new FakeBinding(), lastReported: null, alreadyReported: false),
                "A rig with a Head mapping must never produce a warning.");
        }

        [Test]
        public void UnusableRig_WarnsOnce()
        {
            var binding = new FakeBinding();

            Assert.IsTrue(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: false, current: binding, lastReported: null, alreadyReported: false),
                "The first unusable rig must warn.");
        }

        [Test]
        public void UnusableRig_RebindingTheSameBinding_DoesNotWarnAgain()
        {
            var binding = new FakeBinding();

            Assert.IsFalse(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: false, current: binding, lastReported: binding, alreadyReported: true),
                "A rebind loop on one broken binding must not flood the console.");
        }

        [Test]
        public void UnusableRig_RebindingToADifferentBinding_WarnsAgain()
        {
            Assert.IsTrue(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: false, current: new FakeBinding(), lastReported: new FakeBinding(),
                    alreadyReported: true),
                "A different broken binding is new information and must be reported.");
        }

        [Test]
        public void MissingBindingEntirely_WarnsOnce_ThenStaysQuiet()
        {
            Assert.IsTrue(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: false, current: null, lastReported: null, alreadyReported: false),
                "No binding at all must warn.");

            Assert.IsFalse(
                ConvaiGazeController.ShouldReportRigWarning(
                    usable: false, current: null, lastReported: null, alreadyReported: true),
                "Re-evaluating the same missing binding must stay quiet.");
        }
    }
}
