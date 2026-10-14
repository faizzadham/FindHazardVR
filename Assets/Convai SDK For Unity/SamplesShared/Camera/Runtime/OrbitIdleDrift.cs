// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Evaluates cinematic idle drift using deterministic sine waves.
    ///     Produces subtle handheld-style motion when the camera is idle.
    /// </summary>
    public sealed class OrbitIdleDrift
    {
        /// <summary>
        ///     Evaluates idle drift offsets for yaw, pitch, and distance.
        /// </summary>
        /// <param name="lastInputTime">Time when the last camera input occurred.</param>
        /// <param name="now">Current time.</param>
        /// <param name="settings">Camera settings containing drift parameters.</param>
        /// <returns>Drift offsets (X = yaw, Y = pitch, Z = distance).</returns>
        public Vector3 Evaluate(float lastInputTime, float now, OrbitCameraSettings settings)
        {
            if (!settings.idleDriftEnabled)
                return Vector3.zero;

            float idleTime = now - lastInputTime - settings.idleDriftDelay;
            if (idleTime <= 0f)
                return Vector3.zero;

            float fade = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(idleTime / 0.75f));
            float yawDrift = EvaluateDrift(now, 1.37f, settings.idleYawDriftAmplitude, settings) * fade;
            float pitchDrift = EvaluateDrift(now, 2.91f, settings.idlePitchDriftAmplitude, settings) * fade;
            float distanceDrift = EvaluateDrift(now, 4.13f, settings.idleDistanceDriftAmplitude, settings) * fade;

            return new Vector3(yawDrift, pitchDrift, distanceDrift);
        }

        /// <summary>
        ///     Evaluates a single drift channel using layered sine waves.
        /// </summary>
        /// <param name="time">Current time.</param>
        /// <param name="seed">Phase offset to decorrelate channels.</param>
        /// <param name="amplitude">Maximum drift amplitude.</param>
        /// <param name="settings">Camera settings containing drift frequencies.</param>
        /// <returns>Drift value for this channel.</returns>
        private float EvaluateDrift(float time, float seed, float amplitude, OrbitCameraSettings settings)
        {
            if (amplitude <= 0.0001f)
                return 0f;

            float primary = Mathf.Sin((time * settings.idleDriftFrequency * Mathf.PI * 2f) + seed);
            float secondary = Mathf.Sin((time * settings.idleDriftSecondaryFrequency * Mathf.PI * 2f) + (seed * 1.73f));
            return (primary + (secondary * 0.35f)) * amplitude;
        }
    }
}
