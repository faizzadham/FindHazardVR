using System.IO;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Keeps <c>ConvaiBodyAnimationController</c> a thin composition root instead of letting
    ///     behaviour creep back into it one "just this one thing" change at a time. Cheap and
    ///     mechanical on purpose — it is the only thing that stops the file re-growing now that
    ///     the behaviour lives in its own classes (SpokenLineRelay, SocialSpacingRunner,
    ///     EmotionalGaitRunner, CoSpeechCoordinator, DeferredRequestSlot,
    ///     AnimationSetHandoffCoordinator, LayerStackBuilder).
    /// </summary>
    /// <remarks>
    ///     The file is ~1740 lines once the extractions are done, and the remainder is
    ///     legitimately the composition root's own job — the public API surface
    ///     (<c>PlayAction</c>/<c>PointAt</c>/<c>PlayActionAt</c> and their required XML docs),
    ///     <c>CaptureSnapshot</c>, the <c>IAnchorMovementDrive</c> implementation, and
    ///     build/teardown orchestration. The threshold below is a regression guard against that
    ///     achieved size: it is set with headroom above it, so it fails the moment new behaviour
    ///     (rather than doc/comment upkeep) creeps back in, without being a nuisance for normal
    ///     maintenance.
    /// </remarks>
    public class BodyAnimationCompositionRootGuardTests
    {
        /// <summary>
        ///     Regression budget (see class remarks): headroom above the ~1830 lines the
        ///     composition root measures once behaviour lives in its own classes.
        /// </summary>
        /// <remarks>
        ///     Raised once from 1800 when the public release documented every public member. That
        ///     is the upkeep this budget's remarks call out as the thing it must not obstruct — no
        ///     behaviour moved back into the controller, only the XML docs a customer-facing API
        ///     has to carry. Raise it again only for the same kind of reason, and say so here.
        /// </remarks>
        private const int MaxLines = 1870;

        private static string ControllerPath => Path.GetFullPath(Path.Combine(
            UnityEngine.Application.dataPath,
            "..",
            "Packages",
            "com.convai.convai-sdk-for-unity",
            "SDK",
            "Modules",
            "BodyAnimation",
            "Components",
            "ConvaiBodyAnimationController.cs"));

        [Test]
        [Category("Architecture")]
        public void ConvaiBodyAnimationController_StaysUnderTheLineBudget()
        {
            string path = ControllerPath;
            Assert.IsTrue(File.Exists(path), $"ConvaiBodyAnimationController.cs not found at: {path}");

            int lineCount = File.ReadAllLines(path).Length;

            Assert.LessOrEqual(lineCount, MaxLines,
                $"ConvaiBodyAnimationController.cs has grown to {lineCount} lines (budget: {MaxLines}). " +
                "This budget exists because the class doc claims the controller is 'a thin composition " +
                "root': new behaviour belongs in an internal policy/lifecycle class under Core/Policy or " +
                "Core/Lifecycle (see SpokenLineRelay, SocialSpacingRunner, EmotionalGaitRunner, " +
                "CoSpeechCoordinator, DeferredRequestSlot, AnimationSetHandoffCoordinator, LayerStackBuilder " +
                "for the pattern), constructed by the controller and unit-testable without a scene — not " +
                "inlined into this file. If the growth is unavoidable (e.g. a new public API member with " +
                "its required XML docs), raise this budget deliberately in the same change, with a reason.");
        }
    }
}
