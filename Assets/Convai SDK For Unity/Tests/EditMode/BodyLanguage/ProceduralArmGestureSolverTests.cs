using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Modules.BodyLanguage.Core.Pose;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    public sealed class ProceduralArmGestureSolverTests
    {
        [Test]
        public void MissingHumanoidRig_DegradesWithoutThrowing()
        {
            var solver = new ProceduralArmGestureSolver();
            var request = new CoSpeechGestureRequest(
                1, GestureCueKind.Emphatic, 1f, 1f, 0.2f, 0.15f, 0.05f, 0.3f);

            Assert.DoesNotThrow(() => solver.Bind(null));
            Assert.That(solver.TryStart(in request), Is.False);
            Assert.DoesNotThrow(() => solver.Tick(1f / 60f, 1f));
            Assert.DoesNotThrow(solver.Cancel);
            Assert.DoesNotThrow(solver.Reset);
        }

        [Test]
        public void GestureRequest_ClampsUnsafeTimingAndWeights()
        {
            var request = new CoSpeechGestureRequest(
                4, GestureCueKind.Beat, 4f, -1f, 0f, 0f, -1f, 0f);
            Assert.That(request.Intensity, Is.EqualTo(1f));
            Assert.That(request.Confidence, Is.Zero);
            Assert.That(request.PreparationSeconds, Is.GreaterThan(0f));
            Assert.That(request.StrokeSeconds, Is.GreaterThan(0f));
            Assert.That(request.HoldSeconds, Is.Zero);
            Assert.That(request.RetractionSeconds, Is.GreaterThan(0f));
        }
    }
}
