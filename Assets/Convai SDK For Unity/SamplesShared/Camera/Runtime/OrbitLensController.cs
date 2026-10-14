// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Controls physical camera properties (focal length, sensor size) with smoothing.
    ///     Separates lens logic from camera orchestration.
    /// </summary>
    public sealed class OrbitLensController
    {
        private float _focalLength;
        private float _focalLengthVelocity;

        /// <summary>
        ///     Applies lens settings to the Unity camera component with smoothing.
        /// </summary>
        /// <param name="camera">Camera component to configure.</param>
        /// <param name="deltaTime">Frame time in seconds.</param>
        /// <param name="settings">Camera settings containing lens parameters.</param>
        public void Apply(
            UnityEngine.Camera camera,
            float deltaTime,
            OrbitCameraSettings settings)
        {
            if (!settings.lensControlEnabled || camera == null)
                return;

            _focalLength = Damp(
                _focalLength,
                settings.focalLength,
                ref _focalLengthVelocity,
                settings.lensSmoothTime,
                deltaTime);

            camera.usePhysicalProperties = true;
            camera.sensorSize = settings.sensorSize;
            camera.focalLength = Mathf.Max(1f, _focalLength);
        }

        /// <summary>
        ///     Resets focal length state to the specified value. Call when snapping or changing settings.
        /// </summary>
        public void Reset(float initialFocalLength)
        {
            _focalLength = initialFocalLength;
            _focalLengthVelocity = 0f;
        }

        /// <summary>
        ///     Resolves the vertical field of view for the current camera and settings.
        ///     Uses physical camera properties if lens control is enabled, otherwise
        ///     falls back to the camera's configured FOV.
        /// </summary>
        public float ResolveVerticalFieldOfView(
            UnityEngine.Camera camera,
            OrbitCameraSettings settings)
        {
            if (settings.lensControlEnabled)
            {
                return UnityEngine.Camera.FocalLengthToFieldOfView(
                    Mathf.Max(1f, _focalLength),
                    Mathf.Max(1f, settings.sensorSize.y));
            }

            return camera != null ? camera.fieldOfView : 60f;
        }

        /// <summary>
        ///     Resolves the aspect ratio of the camera. Returns 16:9 as a safe fallback.
        /// </summary>
        public float ResolveAspect(UnityEngine.Camera camera)
        {
            return camera != null && camera.aspect > 0.0001f
                ? camera.aspect
                : 16f / 9f;
        }

        private static float Damp(
            float current,
            float target,
            ref float velocity,
            float smoothTime,
            float deltaTime)
        {
            if (smoothTime <= 0f || deltaTime <= 0f)
            {
                velocity = 0f;
                return target;
            }

            return Mathf.SmoothDamp(current, target, ref velocity, smoothTime, Mathf.Infinity, deltaTime);
        }
    }
}
