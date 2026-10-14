// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using Convai.Sample.Camera;
using NUnit.Framework;

namespace Convai.Tests.SamplesShared.Camera.EditMode
{
    public class OrbitDepthOfFieldControllerTests
    {
        private static OrbitDofSettings Settings()
        {
            OrbitDofSettings settings = OrbitDofSettings.Default;
            settings.Validate();
            return settings;
        }

        [Test]
        public void Reset_FromSettings_LandsOnTheStateThatDistanceCallsFor()
        {
            // Snapping to an unrelated aperture makes the first seconds of the sample rack focus into
            // place. Reset must land where Calculate would have taken it anyway.
            OrbitDofSettings settings = Settings();
            const float initialDistance = 1.6f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, initialDistance);

            OrbitDepthOfFieldController reference = new OrbitDepthOfFieldController();
            reference.Calculate(initialDistance, settings);

            Assert.AreEqual(reference.TargetState.Aperture, controller.CurrentState.Aperture, 0.0001f);
            Assert.AreEqual(reference.TargetState.FocusDistance, controller.CurrentState.FocusDistance, 0.0001f);
        }

        [Test]
        public void Reset_FromSettings_LeavesNothingToSmoothTowards()
        {
            OrbitDofSettings settings = Settings();
            const float initialDistance = 1.6f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, initialDistance);

            controller.Calculate(initialDistance, settings);
            OrbitDofState afterOneFrame = controller.Advance(1f / 60f, settings);

            Assert.AreEqual(controller.TargetState.Aperture, afterOneFrame.Aperture, 0.0001f, "Aperture drifted on the first frame");
            Assert.AreEqual(controller.TargetState.FocusDistance, afterOneFrame.FocusDistance, 0.0001f, "Focus drifted on the first frame");
        }

        [Test]
        public void Reset_WithNonPositiveDistance_StaysFocusable()
        {
            OrbitDofSettings settings = Settings();

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, -5f);

            Assert.GreaterOrEqual(controller.CurrentState.FocusDistance, OrbitDofMath.MinimumFocusDistance);
            Assert.GreaterOrEqual(controller.CurrentState.Aperture, settings.minAperture);
        }

        [Test]
        public void Calculate_ClampsApertureIntoTheConfiguredBounds()
        {
            OrbitDofSettings settings = Settings();
            settings.closeAperture = 22f;
            settings.minAperture = 2f;
            settings.maxAperture = 5.6f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Calculate(settings.closeDistance, settings);

            Assert.AreEqual(5.6f, controller.TargetState.Aperture, 0.0001f);
        }

        [Test]
        public void Calculate_AppliesFocusBias()
        {
            OrbitDofSettings settings = Settings();
            settings.focusBias = 0.25f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Calculate(1.5f, settings);

            Assert.AreEqual(1.75f, controller.TargetState.FocusDistance, 0.0001f);
        }

        [Test]
        public void Calculate_WithNegativeFocusBias_NeverFocusesBehindTheCamera()
        {
            OrbitDofSettings settings = Settings();
            settings.focusBias = -10f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Calculate(1.5f, settings);

            Assert.GreaterOrEqual(controller.TargetState.FocusDistance, OrbitDofMath.MinimumFocusDistance);
        }

        [Test]
        public void Advance_ConvergesOnTheTargetOverTime()
        {
            OrbitDofSettings settings = Settings();

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, settings.closeDistance);
            controller.Calculate(settings.farDistance, settings);

            for (int frame = 0; frame < 600; frame++)
                controller.Advance(1f / 60f, settings);

            Assert.AreEqual(controller.TargetState.Aperture, controller.CurrentState.Aperture, 0.01f);
            Assert.AreEqual(controller.TargetState.FocusDistance, controller.CurrentState.FocusDistance, 0.01f);
        }

        [Test]
        public void Advance_WithZeroSmoothTime_SnapsImmediately()
        {
            OrbitDofSettings settings = Settings();
            settings.apertureSmoothTime = 0f;
            settings.focusSmoothTime = 0f;

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, settings.closeDistance);
            controller.Calculate(settings.farDistance, settings);
            OrbitDofState state = controller.Advance(1f / 60f, settings);

            Assert.AreEqual(controller.TargetState.Aperture, state.Aperture, 0.0001f);
            Assert.AreEqual(controller.TargetState.FocusDistance, state.FocusDistance, 0.0001f);
        }

        [Test]
        public void Advance_WithPausedTime_HoldsTheCurrentState()
        {
            OrbitDofSettings settings = Settings();

            OrbitDepthOfFieldController controller = new OrbitDepthOfFieldController();
            controller.Reset(settings, settings.closeDistance);
            float apertureBefore = controller.CurrentState.Aperture;

            controller.Calculate(settings.farDistance, settings);
            OrbitDofState state = controller.Advance(0f, settings);

            Assert.AreEqual(apertureBefore, state.Aperture, 0.0001f);
        }
    }
}
