// Copyright (c) Convai. Licensed under the Convai SDK license. See LICENSE in the package root.

using UnityEngine;

namespace Convai.Sample.Camera
{
    /// <summary>
    ///     Controls adaptive depth-of-field with smooth state transitions.
    ///     <para>
    ///         Manages current and target DOF state (aperture, focus distance) with critically
    ///         damped smoothing. Follows composition pattern of existing Orbit components like
    ///         <see cref="OrbitLensController" /> and <see cref="OrbitSmoother" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Lifecycle:
    ///         <list type="number">
    ///             <item><description>Call <see cref="Calculate" /> to set target state from camera distance.</description></item>
    ///             <item><description>Call <see cref="Advance" /> each frame to smooth current state toward target.</description></item>
    ///             <item><description>Access <see cref="CurrentState" /> to retrieve smoothed values.</description></item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Allocation-free per frame. All state stored in value types.
    ///     </para>
    /// </remarks>
    public sealed class OrbitDepthOfFieldController
    {
        private OrbitDofState _currentState;
        private OrbitDofState _targetState;
        private float _apertureVelocity;
        private float _focusVelocity;

        /// <summary>Current (smoothed) DOF state. Read-only.</summary>
        public OrbitDofState CurrentState => _currentState;

        /// <summary>Target DOF state before smoothing. Read-only.</summary>
        public OrbitDofState TargetState => _targetState;

        /// <summary>
        ///     Calculates and updates target DOF state from camera orbit distance.
        ///     Does not modify current state (call <see cref="Advance" /> for that).
        /// </summary>
        /// <param name="cameraDistance">Current camera orbit distance in meters.</param>
        /// <param name="settings">DOF configuration parameters.</param>
        /// <example>
        ///     <code>
        ///     _dofController.Calculate(cameraDistance: 1.5f, settings);
        ///     // Target state now set, but current state unchanged until Advance() called
        ///     </code>
        /// </example>
        public void Calculate(float cameraDistance, OrbitDofSettings settings)
        {
            OrbitDofMath.AdaptiveDofResult result = OrbitDofMath.Evaluate(
                cameraDistance,
                settings.closeDistance,
                settings.farDistance,
                settings.closeAperture,
                settings.farAperture);

            _targetState.Aperture = Mathf.Clamp(result.Aperture, settings.minAperture, settings.maxAperture);
            _targetState.FocusDistance = Mathf.Max(OrbitDofMath.MinimumFocusDistance, result.FocusDistance + settings.focusBias);
        }

        /// <summary>
        ///     Advances current state toward target with critically damped smoothing.
        ///     Call once per frame after <see cref="Calculate" />.
        /// </summary>
        /// <param name="deltaTime">Frame delta time in seconds.</param>
        /// <param name="settings">DOF configuration (uses smooth times).</param>
        /// <returns>Updated current state (same as <see cref="CurrentState" />).</returns>
        /// <example>
        ///     <code>
        ///     OrbitDofState smoothed = _dofController.Advance(Time.deltaTime, settings);
        ///     // Apply smoothed.Aperture and smoothed.FocusDistance to Volume
        ///     </code>
        /// </example>
        public OrbitDofState Advance(float deltaTime, OrbitDofSettings settings)
        {
            if (deltaTime < 1e-4f)
                return _currentState;

            _currentState.Aperture = SmoothDamp(
                _currentState.Aperture,
                _targetState.Aperture,
                ref _apertureVelocity,
                settings.apertureSmoothTime,
                deltaTime);

            _currentState.FocusDistance = SmoothDamp(
                _currentState.FocusDistance,
                _targetState.FocusDistance,
                ref _focusVelocity,
                settings.focusSmoothTime,
                deltaTime);

            return _currentState;
        }

        /// <summary>
        ///     Resets current and target state to specified values immediately.
        ///     Use for teleports, scene loads, or when <see cref="ConvaiOrbitCamera.SnapToTarget" /> called.
        /// </summary>
        /// <param name="aperture">Aperture to snap to (f-stop).</param>
        /// <param name="focusDistance">Focus distance to snap to (meters).</param>
        /// <example>
        ///     <code>
        ///     _dofController.Reset(aperture: 8.0f, focusDistance: 2.5f);
        ///     // Current and target both set to f/8.0 at 2.5m, no smoothing
        ///     </code>
        /// </example>
        public void Reset(float aperture, float focusDistance)
        {
            _currentState = new OrbitDofState(aperture, focusDistance);
            _targetState = _currentState;
            _apertureVelocity = 0f;
            _focusVelocity = 0f;
        }

        /// <summary>
        ///     Resets to the depth of field the supplied distance actually calls for, with no smoothing.
        ///     <para>
        ///         Use on spawn, teleport, or scene load. Because the snapped values are the same ones
        ///         <see cref="Calculate" /> would target for that distance, the first rendered frame already
        ///         shows the settled look instead of racking focus into place.
        ///     </para>
        /// </summary>
        /// <param name="settings">DOF configuration parameters.</param>
        /// <param name="initialDistance">Camera orbit distance to settle at, in meters.</param>
        public void Reset(OrbitDofSettings settings, float initialDistance)
        {
            Calculate(initialDistance, settings);
            Reset(_targetState.Aperture, _targetState.FocusDistance);
        }

        /// <summary>
        ///     Critically damped smooth interpolation (same as <see cref="Mathf.SmoothDamp" />).
        /// </summary>
        private static float SmoothDamp(
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
