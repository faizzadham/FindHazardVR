using Convai.Modules.Gaze.Data;
using UnityEngine;

namespace Convai.Modules.Gaze.Core
{
    /// <summary>
    ///     Distance + visibility level-of-detail decisions for the gaze controller, so a crowd
    ///     of gaze-enabled characters stays in budget: far characters think less often and
    ///     off-screen characters skip actuation entirely. A pure, stateful decision table (the
    ///     only state is the far-band hysteresis latch and the skipped-tick time accumulator),
    ///     fully unit-testable without a scene.
    /// </summary>
    internal sealed class GazeLodGovernor
    {
        /// <summary>Hysteresis half-width (meters) around the far distance → a 1 m band, no thrash at the boundary.</summary>
        private const float DistanceHysteresis = 0.5f;

        /// <summary>
        ///     Tolerance (seconds) on the interval comparison. Accumulating frame deltas in
        ///     float undershoots the exact interval (five 0.02 steps sum to 0.099999994, not
        ///     0.1), which would silently defer every executed tick by one extra frame.
        /// </summary>
        private const float IntervalToleranceSeconds = 1e-4f;

        /// <summary>
        ///     Ceiling (seconds) on the lumped delta time an executed tick is handed.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Accumulating the skipped ticks is right for everything that INTEGRATES: springs,
        ///         ramps, decay and every exponential smoother in the chain are exact under a large
        ///         step, and advancing them by the true elapsed time is what keeps a character that
        ///         drops in and out of the far band from running in slow motion.
        ///     </para>
        ///     <para>
        ///         It is wrong for everything that PLANS against the step size. The ballistic
        ///         movement lane floors its re-plan horizon at two frames
        ///         (<c>Mathf.Max(EffectiveHorizon(skew), 2f * deltaTime)</c>) because a quintic
        ///         solved into a horizon of a frame or two is ill-conditioned. That floor is
        ///         measured in the dt it is given, so a 0.2 s lump does not merely advance the
        ///         movement by 0.2 s — it silently re-plans it against a 0.4 s horizon, stretching
        ///         a movement that had 0.05 s left into one shaped for eight times that. The first
        ///         near-band frame after a far spell is exactly when a character is most likely to
        ///         be looked at, and it is where that shows.
        ///     </para>
        ///     <para>
        ///         1/20 s caps the implied horizon floor at 0.1 s, which is shorter than the
        ///         shortest movement the shift director plans, so the floor is inert again. The
        ///         price is that a character crossing back from the far band advances its
        ///         integrators by at most 50 ms of the elapsed time on that one tick — a bounded,
        ///         one-off slowdown on a character that was too far away to read a moment ago,
        ///         against an unbounded re-plan on the frame it becomes readable.
        ///     </para>
        /// </remarks>
        private const float MaxCognitionDeltaSeconds = 1f / 20f;

        private bool _far;
        private float _accumulatedDeltaTime;

        /// <summary>Whether the character is currently in the reduced-rate far band.</summary>
        public bool IsFar => _far;

        public void Reset()
        {
            _far = false;
            _accumulatedDeltaTime = 0f;
        }

        /// <summary>
        ///     Advances the governor for one Cognition tick.
        /// </summary>
        /// <param name="profile">Active gaze profile (LOD knobs).</param>
        /// <param name="distance">Distance (meters) from the character head pivot to the player anchor.</param>
        /// <param name="anyRendererVisible">Whether any of the character's renderers is visible to a camera.</param>
        /// <param name="deltaTime">Time since the last Cognition tick.</param>
        /// <param name="cognitionDeltaTime">
        ///     The delta time the executed tick should use — the sum of this and every skipped
        ///     tick since the last execution, so springs and ramps stay variable-dt correct,
        ///     capped at <see cref="MaxCognitionDeltaSeconds" /> so the stages that plan against
        ///     the step size are not handed one no frame ever produces.
        /// </param>
        /// <param name="skipExpression">Whether the LateUpdate solver stage should be skipped this frame.</param>
        /// <returns><c>true</c> when Cognition should run this tick; <c>false</c> to skip it.</returns>
        public bool TickCognition(
            ConvaiGazeProfile profile,
            float distance,
            bool anyRendererVisible,
            float deltaTime,
            out float cognitionDeltaTime,
            out bool skipExpression)
        {
            if (profile == null || !profile.EnableGazeLod)
            {
                _far = false;
                _accumulatedDeltaTime = 0f;
                cognitionDeltaTime = deltaTime;
                skipExpression = false;
                return true;
            }

            skipExpression = profile.SkipWhenInvisible && !anyRendererVisible;

            _accumulatedDeltaTime += Mathf.Max(0f, deltaTime);
            float interval = ResolveCognitionInterval(profile, distance);
            if (_accumulatedDeltaTime + IntervalToleranceSeconds < interval)
            {
                cognitionDeltaTime = 0f;
                return false;
            }

            // The accumulator is cleared in full: the cap bounds what the actuators are asked
            // to advance by, it does not bank the remainder for the next tick. Carrying it would
            // hand the following tick another over-large lump and turn a one-off into a queue.
            cognitionDeltaTime = Mathf.Min(_accumulatedDeltaTime, MaxCognitionDeltaSeconds);
            _accumulatedDeltaTime = 0f;
            return true;
        }

        private float ResolveCognitionInterval(ConvaiGazeProfile profile, float distance)
        {
            if (_far)
            {
                if (distance < profile.LodFarDistance - DistanceHysteresis) _far = false;
            }
            else
            {
                if (distance > profile.LodFarDistance + DistanceHysteresis) _far = true;
            }

            return _far ? 1f / Mathf.Max(0.01f, profile.LodFarCognitionHz) : 0f;
        }
    }
}
