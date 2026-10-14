using UnityEngine;

namespace Convai.Runtime.Animation.ProceduralPose
{
    /// <summary>
    ///     The three numbers that shape a <see cref="OneEuroFilter" />: the cut-off it uses when
    ///     the signal is still, how fast that cut-off opens with the signal's own speed, and the
    ///     cut-off applied to the speed estimate itself.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Passed per call rather than stored on the filter, for the same reason
    ///         <see cref="MotorFilter" /> takes its caps per <c>Step</c>: a struct that carries its
    ///         own configuration is invalid when default-constructed (a zero cut-off is not a
    ///         filter), and every caller here has the numbers to hand as internal constants
    ///         anyway.
    ///     </para>
    ///     <para>
    ///         <b><see cref="Beta" /> is in units of the signal.</b> It multiplies a speed, so a
    ///         value tuned for pixels/second is off by three orders of magnitude when the signal is
    ///         metres. Published One-Euro values (β ≈ 0.007) are pixel-scale; a metre-scale signal
    ///         wants β of a few units, and a value carried over from a pixel-scale reference leaves
    ///         the cut-off effectively shut at every speed a person moves at.
    ///     </para>
    /// </remarks>
    internal readonly struct OneEuroTuning
    {
        /// <summary>Cut-off (Hz) applied when the signal is not moving — the jitter knob.</summary>
        public readonly float MinCutoffHz;

        /// <summary>
        ///     How much the cut-off opens per unit of signal speed — the lag knob. At zero the
        ///     filter degenerates into a plain fixed-cut-off low pass, which lags anything that
        ///     genuinely moves by the whole of its time constant.
        /// </summary>
        public readonly float Beta;

        /// <summary>Cut-off (Hz) applied to the speed estimate before it drives the cut-off.</summary>
        /// <remarks>
        ///     Low on purpose. It is what makes an oscillation different from a movement: a bob's
        ///     derivative changes sign twice a cycle and averages to nothing through a 1 Hz low
        ///     pass, so the cut-off stays shut, while a walk's derivative is sustained and opens
        ///     it. Raising this lets the jitter's own derivative open the filter it is supposed to
        ///     be rejected by.
        /// </remarks>
        public readonly float DerivativeCutoffHz;

        public OneEuroTuning(float minCutoffHz, float beta, float derivativeCutoffHz)
        {
            MinCutoffHz = minCutoffHz;
            Beta = beta;
            DerivativeCutoffHz = derivativeCutoffHz;
        }
    }

    /// <summary>
    ///     Scalar One-Euro filter (Casiez, Roussel and Vogel, CHI 2012): a first-order low pass
    ///     whose cut-off frequency rises with the signal's own estimated speed, so a still signal
    ///     is filtered hard and a moving one is barely filtered at all.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why not a fixed low pass.</b> Jitter and lag are the same knob on a fixed filter:
    ///         the cut-off that flattens a talking head's bone bob also drags the character's aim
    ///         behind anyone who walks past. One-Euro splits them — <see cref="OneEuroTuning.MinCutoffHz" />
    ///         sets how still a still signal is held, <see cref="OneEuroTuning.Beta" /> sets how
    ///         quickly the filter gets out of the way once the signal means it.
    ///     </para>
    ///     <para>
    ///         Frame-rate independent: the smoothing factor is derived from the time constant and
    ///         the actual <c>deltaTime</c> each step (<c>alpha = 1 / (1 + tau/dt)</c>), so the same
    ///         signal is filtered the same at 30 Hz and at 144 Hz. A zero or negative
    ///         <c>deltaTime</c> holds the current value.
    ///     </para>
    ///     <para>
    ///         The first <see cref="Step" /> after construction snaps to its input at zero speed,
    ///         so there is no transient from an arbitrary zero start. Struct, zero-alloc, no
    ///         UnityEngine.Object references — the same contract <see cref="MotorFilter" /> and
    ///         <see cref="BallisticMotor" /> keep.
    ///     </para>
    /// </remarks>
    internal struct OneEuroFilter
    {
        private float _value;
        private float _previousInput;
        private float _speed;
        private bool _initialized;

        /// <summary>The last value returned by <see cref="Step" /> (0 before the first Step).</summary>
        public float Current => _value;

        /// <summary>
        ///     The filtered speed estimate (units/second) driving the adaptive cut-off. Exposed for
        ///     tests and diagnostics: whether the filter is "open" is a property of this number and
        ///     nothing in the output can be differentiated to recover it.
        /// </summary>
        public float Speed => _speed;

        /// <summary>
        ///     Advances the filter by <paramref name="deltaTime" /> toward
        ///     <paramref name="value" /> and returns the filtered result.
        /// </summary>
        public float Step(float value, in OneEuroTuning tuning, float deltaTime)
        {
            if (deltaTime <= 0f) return _value;

            if (!_initialized)
            {
                _value = value;
                _previousInput = value;
                _speed = 0f;
                _initialized = true;
                return _value;
            }

            // Speed estimate first, and low-passed before it is used: the raw finite difference of
            // a noisy signal is mostly noise divided by dt, and feeding that straight into the
            // cut-off would open the filter exactly when the jitter is worst.
            //
            // Differenced against the previous INPUT, not the previous output. Using the output
            // measures the input's speed plus the filter's own tracking error over dt, and that
            // error term is proportional to dt — so the estimate, the cut-off it opens and the
            // steady-state lag all become frame-rate dependent (measured: 3.2 cm of lag on a
            // 1 m/s ramp at 30 Hz against 1.5 cm at 240 Hz, on a filter whose whole claim is that
            // dt does not change its behaviour). Against the input the lag is exactly the time
            // constant at every rate.
            float rawSpeed = (value - _previousInput) / deltaTime;
            _previousInput = value;
            _speed += Alpha(deltaTime, tuning.DerivativeCutoffHz) * (rawSpeed - _speed);

            float cutoff = tuning.MinCutoffHz + tuning.Beta * Mathf.Abs(_speed);
            _value += Alpha(deltaTime, cutoff) * (value - _value);
            return _value;
        }

        /// <summary>
        ///     Re-states the filter at <paramref name="value" />, at zero speed, discarding the
        ///     history. For a discontinuity the filter must not smooth across — a teleport, a
        ///     camera cut, a change of what is being looked at.
        /// </summary>
        /// <remarks>
        ///     A seed, not a bypass: the step immediately after a reset returns
        ///     <paramref name="value" /> exactly if the input has not moved, and filters normally
        ///     from there if it has. Resetting every frame would therefore make the filter a
        ///     pass-through, which is what a caller that resets on the wrong condition gets.
        /// </remarks>
        public void Reset(float value)
        {
            _value = value;
            _previousInput = value;
            _speed = 0f;
            _initialized = true;
        }

        /// <summary>Zeroes all state; the next <see cref="Step" /> snaps to its input.</summary>
        public void Clear()
        {
            _value = 0f;
            _previousInput = 0f;
            _speed = 0f;
            _initialized = false;
        }

        /// <summary>
        ///     Exponential smoothing factor for a first-order low pass of the given cut-off over
        ///     the given step: <c>alpha = 1 / (1 + tau/dt)</c> with <c>tau = 1 / (2·pi·cutoff)</c>.
        /// </summary>
        private static float Alpha(float deltaTime, float cutoffHz)
        {
            float tau = 1f / (2f * Mathf.PI * Mathf.Max(1e-4f, cutoffHz));
            return 1f / (1f + tau / deltaTime);
        }
    }

    /// <summary>
    ///     Three independent <see cref="OneEuroFilter" /> channels over a <see cref="Vector3" />.
    /// </summary>
    /// <remarks>
    ///     Per-axis rather than one filter driven by the vector's speed magnitude: a target that
    ///     bobs laterally while approaching should have its bob rejected and its approach passed,
    ///     and a shared magnitude would let either axis open the other one's cut-off. Independent
    ///     axes cost a fraction of a degree of direction skew during a fast diagonal move, which is
    ///     below anything the aim stages can express.
    /// </remarks>
    internal struct OneEuroVector3Filter
    {
        private OneEuroFilter _x;
        private OneEuroFilter _y;
        private OneEuroFilter _z;

        /// <summary>The last value returned by <see cref="Step" />.</summary>
        public Vector3 Current => new(_x.Current, _y.Current, _z.Current);

        /// <summary>Advances all three channels and returns the filtered point.</summary>
        public Vector3 Step(Vector3 value, in OneEuroTuning tuning, float deltaTime) =>
            new(
                _x.Step(value.x, in tuning, deltaTime),
                _y.Step(value.y, in tuning, deltaTime),
                _z.Step(value.z, in tuning, deltaTime));

        /// <summary>Re-states all three channels at <paramref name="value" />, at zero speed.</summary>
        public void Reset(Vector3 value)
        {
            _x.Reset(value.x);
            _y.Reset(value.y);
            _z.Reset(value.z);
        }

        /// <summary>Zeroes all state; the next <see cref="Step" /> snaps to its input.</summary>
        public void Clear()
        {
            _x.Clear();
            _y.Clear();
            _z.Clear();
        }
    }
}
