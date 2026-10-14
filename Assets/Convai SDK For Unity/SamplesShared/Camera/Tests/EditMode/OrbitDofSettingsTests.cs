// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using Convai.Sample.Camera;
using NUnit.Framework;

namespace Convai.Tests.SamplesShared.Camera.EditMode
{
    public class OrbitDofSettingsTests
    {
        [Test]
        public void Default_KeepsFaceSharpUpClose()
        {
            // The shipped look: narrow f-stop when the camera is near the face so the whole face stays
            // sharp, wider when pulled back so the background falls away. If this ever inverts, the
            // sample ships with a soft face - which is exactly the defect this suite exists to prevent.
            OrbitDofSettings settings = OrbitDofSettings.Default;

            Assert.Greater(
                settings.closeAperture,
                settings.farAperture,
                "Close aperture must be the narrower f-stop for the shipped face-sharp look");
        }

        [Test]
        public void Default_ProducesTheApertureItAdvertisesAtEachEnd()
        {
            // Guards the settings and the math against drifting apart: whatever the defaults say happens
            // at each threshold must be what a camera parked there actually gets.
            OrbitDofSettings settings = OrbitDofSettings.Default;

            float atClose = OrbitDofMath.Evaluate(
                settings.closeDistance,
                settings.closeDistance,
                settings.farDistance,
                settings.closeAperture,
                settings.farAperture).Aperture;

            float atFar = OrbitDofMath.Evaluate(
                settings.farDistance,
                settings.closeDistance,
                settings.farDistance,
                settings.closeAperture,
                settings.farAperture).Aperture;

            Assert.AreEqual(settings.closeAperture, atClose, 0.0001f);
            Assert.AreEqual(settings.farAperture, atFar, 0.0001f);
        }

        [Test]
        public void Default_SurvivesValidateUnchanged()
        {
            // Defaults that Validate() would rewrite are a trap: the Inspector would silently disagree
            // with the documented values the first time anything calls Validate.
            OrbitDofSettings settings = OrbitDofSettings.Default;
            OrbitDofSettings validated = OrbitDofSettings.Default;
            validated.Validate();

            Assert.AreEqual(settings.closeDistance, validated.closeDistance, 0.0001f);
            Assert.AreEqual(settings.farDistance, validated.farDistance, 0.0001f);
            Assert.AreEqual(settings.closeAperture, validated.closeAperture, 0.0001f);
            Assert.AreEqual(settings.farAperture, validated.farAperture, 0.0001f);
            Assert.AreEqual(settings.minAperture, validated.minAperture, 0.0001f);
            Assert.AreEqual(settings.maxAperture, validated.maxAperture, 0.0001f);
        }

        [Test]
        public void Validate_WithUnauthoredStruct_ProducesUsableValues()
        {
            // A field left at default(OrbitDofSettings) - e.g. a struct added to an existing serialized
            // component - must not yield zero distances or zero apertures.
            OrbitDofSettings settings = default;
            settings.Validate();

            Assert.GreaterOrEqual(settings.closeDistance, OrbitDofMath.MinimumFocusDistance);
            Assert.Greater(settings.farDistance, settings.closeDistance);
            Assert.GreaterOrEqual(settings.closeAperture, OrbitDofMath.MinimumAperture);
            Assert.GreaterOrEqual(settings.farAperture, OrbitDofMath.MinimumAperture);
            Assert.GreaterOrEqual(settings.minAperture, OrbitDofMath.MinimumAperture);
            Assert.GreaterOrEqual(settings.maxAperture, settings.minAperture);
        }

        [Test]
        public void Validate_ClampsAperturesIntoTheAuthoredBounds()
        {
            OrbitDofSettings settings = OrbitDofSettings.Default;
            settings.minAperture = 2f;
            settings.maxAperture = 4f;
            settings.closeAperture = 16f;
            settings.farAperture = 0.5f;

            settings.Validate();

            Assert.AreEqual(4f, settings.closeAperture, 0.0001f);
            Assert.AreEqual(2f, settings.farAperture, 0.0001f);
        }

        [Test]
        public void Validate_WithInvertedBounds_RepairsThemInsteadOfInvertingClamps()
        {
            OrbitDofSettings settings = OrbitDofSettings.Default;
            settings.minAperture = 8f;
            settings.maxAperture = 2f;

            settings.Validate();

            Assert.GreaterOrEqual(settings.maxAperture, settings.minAperture);
        }

        [Test]
        public void Validate_WithFarThresholdBelowClose_SeparatesThem()
        {
            OrbitDofSettings settings = OrbitDofSettings.Default;
            settings.closeDistance = 3f;
            settings.farDistance = 1f;

            settings.Validate();

            Assert.Greater(settings.farDistance, settings.closeDistance);
        }
    }
}
