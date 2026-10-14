using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class BodyAnimationTroubleshooterLocomotionTests
    {
        [Test]
        public void InvalidCustomProvider_IsBlockingError()
        {
            var input = BaseInput();
            input.HasCustomLocomotionProvider = true;
            input.HasValidLocomotionSource = false;
            var findings = new List<BodyAnimationTroubleshooterFinding>();

            BodyAnimationTroubleshooter.Evaluate(in input, findings);

            Assert.That(findings.Exists(f =>
                f.Title == "Locomotion Provider" && f.Severity == BodyAnimationTroubleshooterSeverity.Error), Is.True);
        }

        [Test]
        public void ReadOnlyCustomProvider_ReportsManagedDegradation()
        {
            var input = BaseInput();
            input.HasCustomLocomotionProvider = true;
            input.HasValidLocomotionSource = true;
            var findings = new List<BodyAnimationTroubleshooterFinding>();

            BodyAnimationTroubleshooter.Evaluate(in input, findings);

            Assert.That(findings.Exists(f =>
                f.Title == "Advanced Locomotion" && f.Severity == BodyAnimationTroubleshooterSeverity.Warning), Is.True);
        }

        private static BodyAnimationTroubleshooterInput BaseInput() => new()
        {
            HasAnimator = true,
            IsHumanoid = true,
            HasSetAssigned = true,
            HasConfigAssigned = true,
            HasProfileAsset = true,
            HasAnyIdle = true,
            HasAnyTalk = true,
            HasAnyListen = true,
            HasAnyThink = true,
            HasBeatGesture = true,
            RigMotionScale = 1f,
            SetIssues = new List<string>()
        };
    }
}
