using System;

namespace Convai.Modules.LipSync
{
    /// <summary>
    ///     Controls smooth fade-out of blendshape values toward a target pose.
    ///     When no target is provided, values fade to zero.
    ///     Uses clamped delta time and a smoothstep curve (at rest at both ends).
    /// </summary>
    internal sealed class FadeController
    {
        private float _duration;
        private float[] _snapshot = Array.Empty<float>();
        private float[] _fadeTargets = Array.Empty<float>();
        private bool _hasFadeTargets;

        // Settle mode: a critically damped spring per channel, seeded with the velocity the frames
        // left off with, so the close is a continuation of the motion rather than a new one.
        private bool _settle;
        private float[] _settleVelocity = Array.Empty<float>();
        private float _settleOmega;
        private float _settleElapsed;

        // The spring's pull ramps in over this long rather than arriving at full strength in the
        // first frame. A spring at full strength from a wide-open mouth accelerates it to three
        // times its speed within one frame — continuous in velocity on paper, a yank on screen.
        // Muscles engage over tens of milliseconds; so does this.
        private const float SettleEngageSeconds = 0.08f;

        public bool IsActive { get; private set; }
        public float Progress { get; private set; }

        /// <summary>Whether the active close is a velocity-continuous settle rather than a timed fade.</summary>
        public bool IsSettling => IsActive && _settle;

        /// <summary>
        ///     Begins a settle to rest: each channel follows a critically damped spring from its
        ///     current value and current velocity toward the rest target (zero unless
        ///     <paramref name="fadeTargets" /> is given). The spring is tuned so the residual at
        ///     <paramref name="duration" /> is under 2%, where the remainder is written outright.
        /// </summary>
        /// <remarks>
        ///     Why a spring and not a curve: a timed curve starts from rest, so a mouth that was
        ///     still moving when its frames ran out stops dead and then starts closing — a kink at
        ///     the very moment the viewer is looking at it. Seeding the spring with the last frame's
        ///     velocity keeps the motion continuous: a jaw still rising rises a little further and
        ///     comes back; a jaw already closing keeps closing. Critical damping never overshoots
        ///     past rest, and a value that would cross below rest is held there.
        /// </remarks>
        public void BeginSettle(float[] currentValues, float[] currentVelocity, float duration)
        {
            Begin(currentValues, duration, null);
            _settle = true;
            _settleElapsed = 0f;
            // Tuned to the jaw, not to the clock. ω = 4/duration puts the peak closing speed at
            // duration/4 with an initial pull a third of what a stiffer spring gives — a mouth
            // that starts closing, not one that is yanked shut — and brings the value to ~90% of
            // the way to rest at `duration`. The last tenth is the lips relaxing: the settle runs
            // on to 1.5×duration, where the residual is 7·e^-6 ≈ 1.7% and is written outright.
            _settleOmega = 4f / _duration;
            _duration *= 1.5f;

            int count = _snapshot.Length;
            if (_settleVelocity.Length != count) _settleVelocity = new float[count];
            if (currentVelocity != null)
            {
                int limit = Math.Min(count, currentVelocity.Length);
                Array.Copy(currentVelocity, _settleVelocity, limit);
                if (limit < count) Array.Clear(_settleVelocity, limit, count - limit);
            }
            else if (count > 0)
            {
                Array.Clear(_settleVelocity, 0, count);
            }
        }

        /// <summary>Begin fading out from the given values toward zero over the specified duration.</summary>
        public void Begin(float[] currentValues, float duration)
        {
            Begin(currentValues, duration, null);
        }

        /// <summary>
        ///     Begin fading out from the given values toward <paramref name="fadeTargets" />.
        ///     When <paramref name="fadeTargets" /> is null or empty, fades to zero.
        ///     Target values are in normalized 0-1 source space (matching engine output).
        /// </summary>
        public void Begin(float[] currentValues, float duration, float[] fadeTargets)
        {
            _duration = Math.Max(0.01f, duration);
            Progress = 0f;
            IsActive = true;

            if (currentValues == null || currentValues.Length == 0)
            {
                _snapshot = Array.Empty<float>();
                _hasFadeTargets = false;
                return;
            }

            if (_snapshot.Length != currentValues.Length) _snapshot = new float[currentValues.Length];
            Array.Copy(currentValues, _snapshot, currentValues.Length);

            _hasFadeTargets = fadeTargets != null && fadeTargets.Length > 0;
            if (_hasFadeTargets)
            {
                if (_fadeTargets.Length != currentValues.Length) _fadeTargets = new float[currentValues.Length];
                int copyLen = Math.Min(fadeTargets.Length, _fadeTargets.Length);
                Array.Copy(fadeTargets, _fadeTargets, copyLen);
                if (copyLen < _fadeTargets.Length)
                    Array.Clear(_fadeTargets, copyLen, _fadeTargets.Length - copyLen);
            }
        }

        /// <summary>
        ///     Advances the fade and writes interpolated values to the output buffer.
        ///     Returns true while fading is still in progress, false when complete.
        /// </summary>
        public bool Tick(float deltaTime, float[] output)
        {
            if (!IsActive) return false;

            float dt = Math.Clamp(deltaTime, LipSyncConstants.MinDeltaTime, LipSyncConstants.MaxDeltaTimeForFade);
            Progress += dt / _duration;

            if (Progress >= 1f)
            {
                IsActive = false;
                Progress = 1f;
                _settle = false;
                WriteFinalValues(output);
                return false;
            }

            if (_settle) return TickSettle(dt, output);

            // Smoothstep, not ease-out. Ease-out leaves at maximum velocity, and the pose this
            // fade starts from is a stationary one — Starving holds its last values, and the
            // interruption path fades from whatever frame was last written. Going from zero
            // velocity to maximum in one frame is a visible kink at the very moment the viewer is
            // looking at the mouth. Smoothstep leaves at rest and arrives at rest, so the close
            // reads as a jaw settling rather than a value being driven to zero. Same duration,
            // same endpoints; only the shape between them changed.
            float alpha = Progress * Progress * (3f - (2f * Progress));

            int outputLength = output?.Length ?? 0;
            int limit = Math.Min(_snapshot.Length, outputLength);

            for (int i = 0; i < limit; i++)
            {
                float target = _hasFadeTargets && i < _fadeTargets.Length ? _fadeTargets[i] : 0f;
                output[i] = _snapshot[i] + (target - _snapshot[i]) * alpha;
            }

            for (int i = limit; i < outputLength; i++) output[i] = 0f;

            return true;
        }

        private bool TickSettle(float dt, float[] output)
        {
            _settleElapsed += dt;
            int outputLength = output?.Length ?? 0;
            int limit = Math.Min(_snapshot.Length, outputLength);
            float engage = Math.Min(1f, _settleElapsed / SettleEngageSeconds);
            float omega = _settleOmega * engage;
            float decay = (float)Math.Exp(-omega * dt);

            for (int i = 0; i < limit; i++)
            {
                float target = _hasFadeTargets && i < _fadeTargets.Length ? _fadeTargets[i] : 0f;
                float x = _snapshot[i] - target;
                float v = _settleVelocity[i];

                // Closed-form step of the critically damped spring x'' = -2ωx' - ω²x.
                float temp = (v + omega * x) * dt;
                float nx = (x + temp) * decay;
                float nv = (v - omega * temp) * decay;

                // Blendshape weights do not go below rest. A channel that would cross is parked.
                if ((x > 0f && nx < 0f) || (x < 0f && nx > 0f))
                {
                    nx = 0f;
                    nv = 0f;
                }

                _snapshot[i] = nx + target;
                _settleVelocity[i] = nv;
                output[i] = _snapshot[i];
            }

            for (int i = limit; i < outputLength; i++) output[i] = 0f;

            return true;
        }

        public void Reset()
        {
            IsActive = false;
            Progress = 0f;
            _hasFadeTargets = false;
            _settle = false;
            _settleElapsed = 0f;
            if (_settleVelocity.Length > 0) Array.Clear(_settleVelocity, 0, _settleVelocity.Length);

            if (_snapshot.Length > 0) Array.Clear(_snapshot, 0, _snapshot.Length);
            if (_fadeTargets.Length > 0) Array.Clear(_fadeTargets, 0, _fadeTargets.Length);
        }

        private void WriteFinalValues(float[] output)
        {
            if (output == null) return;

            if (_hasFadeTargets)
            {
                int limit = Math.Min(_fadeTargets.Length, output.Length);
                Array.Copy(_fadeTargets, output, limit);
                if (limit < output.Length)
                    Array.Clear(output, limit, output.Length - limit);
            }
            else
            {
                Array.Clear(output, 0, output.Length);
            }
        }
    }
}
