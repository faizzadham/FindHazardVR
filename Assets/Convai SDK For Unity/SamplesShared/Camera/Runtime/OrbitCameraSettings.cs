// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using System;
using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Tunable parameters for <see cref="ConvaiOrbitCamera" />.
    ///     Authored as a plain serializable value type so callers can configure the camera directly
    ///     in the Inspector without depending on a ScriptableObject asset. A static
    ///     <see cref="Default" /> preset is provided as a production-safe starting point.
    /// </summary>
    /// <remarks>
    ///     The camera operates on a state machine of four quantities — yaw, pitch, distance,
    ///     and a 2D pan offset. Each has its own smoothing time, which follows standard DCC
    ///     convention: rotation reacts fastest, dolly is heavier (for cinematic weight),
    ///     and pan sits in between. Values are normalized by <see cref="Validate" />.
    /// </remarks>
    [Serializable]
    public struct OrbitCameraSettings
    {
        [Header("Framing")]
        [Tooltip("World-space offset added to the resolved pivot point before the camera is placed " +
                 "(for example, raise the aim to a humanoid character's head height).")]
        public Vector3 targetOffset;

        [Header("Initial Pose")]
        [Tooltip("Initial yaw (rotation around the world Y axis) in degrees.")]
        public float initialYaw;

        [Tooltip("Initial pitch in degrees. Positive values tilt the camera downward toward the pivot.")]
        public float initialPitch;

        [Tooltip("Initial distance from the pivot in meters.")]
        public float initialDistance;

        [Header("Limits")]
        [Tooltip("Minimum pitch in degrees (camera looking upwards most).")]
        public float pitchMin;

        [Tooltip("Maximum pitch in degrees (camera looking downwards most).")]
        public float pitchMax;

        [Tooltip("Minimum orbit distance in meters.")]
        public float distanceMin;

        [Tooltip("Maximum orbit distance in meters.")]
        public float distanceMax;

        [Tooltip("Maximum lateral pan offset in meters (along the camera's right axis).")]
        public float panClampX;

        [Tooltip("Maximum vertical pan offset in meters (along the camera's up axis).")]
        public float panClampY;

        [Header("Target Following")]
        [Tooltip("Approximate time (seconds) for the resolved target pivot to reach the target transform. " +
                 "Use zero for a rigid lock, or a small value to absorb animation/root-motion jitter.")]
        public float targetSmoothTime;

        [Tooltip("If the target moves farther than this distance in one frame, the camera snaps to the " +
                 "new pivot instead of smoothing across the gap. Use this for teleports and scene loads.")]
        public float targetTeleportThreshold;

        [Header("Cinematic Framing")]
        [Tooltip("When enabled, the component drives the attached Camera as a physical camera and smooths " +
                 "focal length changes. This has no render-pipeline dependency.")]
        public bool lensControlEnabled;

        [Tooltip("Physical camera sensor size in millimeters. 36x24 matches a full-frame camera.")]
        public Vector2 sensorSize;

        [Tooltip("Target focal length in millimeters. Higher values compress perspective and feel more cinematic.")]
        public float focalLength;

        [Tooltip("Approximate time (seconds) for focal length to settle.")]
        public float lensSmoothTime;

        [Tooltip("Screen-space composition offset. X shifts framing horizontally, Y vertically. Small values " +
                 "produce portrait-style headroom without moving the pivot.")]
        public Vector2 screenComposition;

        [Header("Cinematic Drift")]
        [Tooltip("Adds subtle handheld-style idle motion after input stops. Uses deterministic sine waves, " +
                 "not random per-frame noise.")]
        public bool idleDriftEnabled;

        [Tooltip("Seconds after the last orbit, pan, or zoom input before idle drift fades in.")]
        public float idleDriftDelay;

        [Tooltip("Maximum idle yaw drift in degrees.")]
        public float idleYawDriftAmplitude;

        [Tooltip("Maximum idle pitch drift in degrees.")]
        public float idlePitchDriftAmplitude;

        [Tooltip("Maximum idle dolly drift in meters.")]
        public float idleDistanceDriftAmplitude;

        [Tooltip("Primary idle drift frequency in cycles per second.")]
        public float idleDriftFrequency;

        [Tooltip("Secondary idle drift frequency in cycles per second.")]
        public float idleDriftSecondaryFrequency;

        [Header("Orbit")]
        [Tooltip("Degrees of yaw/pitch applied per pixel of mouse motion while orbiting.")]
        public float orbitSensitivity;

        [Tooltip("Invert the vertical orbit axis.")]
        public bool invertY;

        [Header("Zoom (Dolly)")]
        [Tooltip("Global multiplier applied to every zoom step. Tune first; leave the near/far " +
                 "step values at their defaults unless you need a different pacing curve.")]
        public float zoomSensitivity;

        [Tooltip("Meters of dolly per scroll notch when the camera is at the minimum distance. " +
                 "Smaller values give fine-grained control for close-ups.")]
        public float zoomStepNear;

        [Tooltip("Meters of dolly per scroll notch when the camera is at the maximum distance. " +
                 "Larger values let the camera traverse big spaces quickly.")]
        public float zoomStepFar;

        [Header("Pan (Middle Mouse)")]
        [Tooltip("Meters of pan per pixel of mouse motion, scaled by the current orbit distance " +
                 "so panning stays consistent at any zoom level.")]
        public float panSensitivity;

        [Header("Collision")]
        [Tooltip("Use a sphere cast from the framed pivot toward the desired camera position so scene " +
                 "geometry cannot occlude the target.")]
        public bool collisionEnabled;

        [Tooltip("Layers considered solid for camera collision.")]
        public LayerMask collisionLayers;

        [Tooltip("Radius in meters used for camera collision sphere casts.")]
        public float collisionRadius;

        [Tooltip("Small offset kept between the camera and the hit surface.")]
        public float collisionSkin;

        [Tooltip("Minimum camera distance allowed after collision pulls the camera toward the pivot.")]
        public float collisionMinimumDistance;

        [Tooltip("Approximate time (seconds) for collision distance recovery. Lower values avoid clipping; " +
                 "higher values feel heavier when leaving occluders.")]
        public float collisionSmoothTime;

        [Header("Smoothing")]
        [Tooltip("Approximate time (seconds) for yaw and pitch to reach their targets. " +
                 "Lower values feel snappier; higher values feel cinematic.")]
        public float rotationSmoothTime;

        [Tooltip("Approximate time (seconds) for the orbit distance to reach its target. " +
                 "Typically higher than rotation to give dolly moves a sense of weight.")]
        public float distanceSmoothTime;

        [Tooltip("Approximate time (seconds) for the pan offset to reach its target.")]
        public float panSmoothTime;

        [Header("Depth of Field")]
        [Tooltip("Enable adaptive depth-of-field based on camera distance. Requires URP or HDRP with Volume component.")]
        public bool dofEnabled;

        [Tooltip("Depth-of-field tuning parameters. Close distance uses shallow DOF (blurred background), far distance uses deep DOF (sharp).")]
        public OrbitDofSettings dofSettings;

        [Header("Behaviour")]
        [Tooltip("Drive the smoothing from Time.unscaledDeltaTime so the camera keeps responding " +
                 "when Time.timeScale is zero (paused menus, bullet-time, etc.).")]
        public bool useUnscaledTime;

        [Tooltip("Lock and hide the mouse cursor while the user is actively orbiting or panning. " +
                 "The previous cursor state is restored when input ends.")]
        public bool lockCursorDuringInput;

        /// <summary>
        ///     Production-safe defaults for a medium-range orbit around a humanoid character.
        ///     Fast rotation, weighted dolly, and a moderate pan response.
        /// </summary>
        public static OrbitCameraSettings Default => new OrbitCameraSettings
        {
            targetOffset = new Vector3(0f, 1.6f, 0f),
            initialYaw = 0f,
            initialPitch = 10f,
            initialDistance = 2.5f,
            pitchMin = -30f,
            pitchMax = 70f,
            distanceMin = 0.5f,
            distanceMax = 8f,
            panClampX = 1.5f,
            panClampY = 1.0f,
            targetSmoothTime = 0.08f,
            targetTeleportThreshold = 4f,
            lensControlEnabled = true,
            sensorSize = new Vector2(36f, 24f),
            focalLength = 62f,
            lensSmoothTime = 0.2f,
            screenComposition = new Vector2(0f, 0.012f),
            idleDriftEnabled = false,
            idleDriftDelay = 0.35f,
            idleYawDriftAmplitude = 0.12f,
            idlePitchDriftAmplitude = 0.035f,
            idleDistanceDriftAmplitude = 0.0025f,
            idleDriftFrequency = 0.09f,
            idleDriftSecondaryFrequency = 0.062f,
            orbitSensitivity = 0.1f,
            invertY = false,
            zoomSensitivity = 1.0f,
            zoomStepNear = 0.025f,
            zoomStepFar = 0.08f,
            panSensitivity = 0.0008f,
            collisionEnabled = true,
            collisionLayers = Physics.DefaultRaycastLayers,
            collisionRadius = 0.18f,
            collisionSkin = 0.04f,
            collisionMinimumDistance = 0.2f,
            collisionSmoothTime = 0.05f,
            rotationSmoothTime = 0.12f,
            distanceSmoothTime = 0.24f,
            panSmoothTime = 0.14f,
            dofEnabled = false,
            dofSettings = OrbitDofSettings.Default,
            useUnscaledTime = true,
            lockCursorDuringInput = true
        };

        /// <summary>
        ///     Normalizes related fields and clamps values into safe ranges.
        ///     Call this after authoring, inside <c>OnValidate</c>, or before supplying
        ///     the struct to <see cref="ConvaiOrbitCamera" />.
        /// </summary>
        public void Validate()
        {
            if (pitchMin > pitchMax)
            {
                (pitchMin, pitchMax) = (pitchMax, pitchMin);
            }

            pitchMin = Mathf.Clamp(pitchMin, -89f, 89f);
            pitchMax = Mathf.Clamp(pitchMax, -89f, 89f);

            distanceMin = Mathf.Max(0.01f, distanceMin);
            distanceMax = Mathf.Max(distanceMin, distanceMax);

            panClampX = Mathf.Max(0f, panClampX);
            panClampY = Mathf.Max(0f, panClampY);

            targetSmoothTime = Mathf.Max(0f, targetSmoothTime);
            targetTeleportThreshold = Mathf.Max(0f, targetTeleportThreshold);

            sensorSize.x = Mathf.Max(1f, sensorSize.x);
            sensorSize.y = Mathf.Max(1f, sensorSize.y);
            focalLength = Mathf.Clamp(focalLength, 1f, 300f);
            lensSmoothTime = Mathf.Max(0f, lensSmoothTime);
            screenComposition.x = Mathf.Clamp(screenComposition.x, -0.45f, 0.45f);
            screenComposition.y = Mathf.Clamp(screenComposition.y, -0.45f, 0.45f);

            idleDriftDelay = Mathf.Max(0f, idleDriftDelay);
            idleYawDriftAmplitude = Mathf.Max(0f, idleYawDriftAmplitude);
            idlePitchDriftAmplitude = Mathf.Max(0f, idlePitchDriftAmplitude);
            idleDistanceDriftAmplitude = Mathf.Max(0f, idleDistanceDriftAmplitude);
            idleDriftFrequency = Mathf.Max(0.01f, idleDriftFrequency);
            idleDriftSecondaryFrequency = Mathf.Max(0.01f, idleDriftSecondaryFrequency);

            initialPitch = Mathf.Clamp(initialPitch, pitchMin, pitchMax);
            initialDistance = Mathf.Clamp(initialDistance, distanceMin, distanceMax);

            orbitSensitivity = Mathf.Max(0f, orbitSensitivity);
            zoomSensitivity = Mathf.Max(0f, zoomSensitivity);
            zoomStepNear = Mathf.Max(0f, zoomStepNear);
            zoomStepFar = Mathf.Max(zoomStepNear, zoomStepFar);
            panSensitivity = Mathf.Max(0f, panSensitivity);

            if (collisionLayers.value == 0)
                collisionLayers = Physics.DefaultRaycastLayers;

            collisionRadius = Mathf.Max(0.01f, collisionRadius);
            collisionSkin = Mathf.Max(0f, collisionSkin);
            collisionMinimumDistance = Mathf.Clamp(
                collisionMinimumDistance,
                0.01f,
                Mathf.Max(0.01f, distanceMax));
            collisionSmoothTime = Mathf.Max(0f, collisionSmoothTime);

            rotationSmoothTime = Mathf.Max(0f, rotationSmoothTime);
            distanceSmoothTime = Mathf.Max(0f, distanceSmoothTime);
            panSmoothTime = Mathf.Max(0f, panSmoothTime);

            // Validate DOF settings
            dofSettings.Validate();
        }
    }
}
