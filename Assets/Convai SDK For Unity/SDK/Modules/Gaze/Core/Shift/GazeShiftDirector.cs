using Convai.Modules.Gaze.Data;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Shift
{
    /// <summary>
    ///     Owns the gaze shift as a single event: when it started, and therefore how far through
    ///     the onset cascade each rung of the actuator ladder is.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the type whose absence produced the coordination defects. Before it, the
    ///         head solver, the eye solver and the body-turn director each decided their own
    ///         participation from their own threshold and their own timer, and nothing checked
    ///         that the three answers added up to the shift being executed — so a rung backing
    ///         off (body-turn relief) left a gap that only the eyes' clamp absorbed.
    ///     </para>
    ///     <para>
    ///         All it holds is the shift clock. The distribution rule itself is stateless and
    ///         lives in <see cref="GazeActuatorLadder" />, which keeps the interesting logic
    ///         testable without a frame loop.
    ///     </para>
    /// </remarks>
    internal sealed class GazeShiftDirector
    {
        /// <summary>
        ///     Settle time (seconds) for a change in how strongly an <i>unchanged</i> look is
        ///     held — see <see cref="SettleAmplitude" />.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately longer than the duration law's own base
        ///         (<c>headTurnBaseSeconds</c>, 0.45 s), and that ordering is the requirement, not
        ///         the number. A re-weighting must read as the character relaxing its hold on a
        ///         look it is still making; if it settles faster than the movement it is not, the
        ///         easing has produced something sharper than the thing it was introduced to
        ///         avoid. Easing it at <c>PolicyBlendSpeed</c> — the rate the policy engine uses
        ///         for gains, 0.2 s — would do exactly that.
        ///     </para>
        ///     <para>
        ///         Not a profile field: it is the one rate in the chain that exists to be slower
        ///         than every authorable rate around it, so exposing it would mainly offer authors
        ///         a way to invert that relationship.
        ///     </para>
        ///     <para>
        ///         Sized for a transparent lane. The head's tracking lane used to shave the peak
        ///         of this ease with its stability band; under classified pursuit the band stands
        ///         aside and the SmoothDamp's own peak rate reaches the bone unmodified — so this
        ///         constant now owns the WHOLE shape of a relax, and 0.9 s (which relied on the
        ///         shave) put a state edge just past the settle-speed bound GazeSteadyLookTests
        ///         pins.
        ///     </para>
        /// </remarks>
        private const float ReweightSettleSeconds = 1.15f;

        // ---- Recruitment latch ---------------------------------------------------------
        //
        // EntryEase answers "does a shift THIS size recruit this rung" — a decision. Evaluated
        // fresh every frame under a MOVING target it stops being a decision and becomes an
        // amplitude-scheduled gain on the goal: through the entry band the goal picks up the
        // ease's own sweep on top of the target's rate (measured at ~3× — a 96°/s whip against a
        // 35°/s walker), and below the entry angle the head's share is zero even in sustained
        // motion, parking the head dead at centre while the eyes carry a moving eccentricity.
        // So the ease is LATCHED per look: it steps on a new look (a decision), rises under a
        // slew bound while the look grows or sustained target motion recruits the head, holds
        // against amplitude falling (a target crossing centre is not a decision to stand the
        // head down), and only relaxes back to the instantaneous ease once the look has been
        // quiescent for a beat — by which point the amplitude it multiplies is small, so the
        // relaxation moves nothing visibly.

        /// <summary>Sustained target motion (deg/s) above which the head is recruited into pursuit.</summary>
        private const float MotionEngageDegreesPerSecond = 4f;

        /// <summary>Target motion (deg/s) below which the look counts as going quiet.</summary>
        private const float MotionReleaseDegreesPerSecond = 2f;

        /// <summary>How long motion must sustain before it recruits — a flicker is not a walk.</summary>
        private const float MotionEngageSeconds = 0.15f;

        /// <summary>How long the goal must stay quiet before the latch may relax.</summary>
        private const float QuiescenceSeconds = 0.4f;

        /// <summary>
        ///     Bound on the goal velocity a RISING latch may add, expressed as degrees/second of
        ///     budget on top of the target's own measured rate. The latch multiplies the
        ///     amplitude, so the per-second rise is this budget (plus the target rate) divided by
        ///     the amplitude — a recruitment therefore fades the head in over a few tenths of a
        ///     second instead of slamming it, which is the slew-bound invariant (plan D-3).
        /// </summary>
        private const float RecruitmentSlewBudgetDegreesPerSecond = 10f;

        /// <summary>Amplitude floor for the slew division, so tiny looks cannot make the rise infinite.</summary>
        private const float SlewAmplitudeFloorDegrees = 5f;

        /// <summary>SmoothDamp time for the quiescent relaxation of a latch.</summary>
        private const float RecruitmentDecaySeconds = 0.8f;

        /// <summary>Response rate (per second) of the smoothed target-motion estimate.</summary>
        private const float RateSmoothingSharpness = 12f;

        private readonly GazeComfortModel _comfort = new();
        private int _generation = int.MinValue;
        private float _shiftAge;

        // The ladder's two amplitude inputs, held across frames so a change WITHIN one look can
        // be eased while a change that IS a new look still arrives as a step. See SettleAmplitude.
        private float _engagement;
        private float _headContribution;
        private float _engagementVelocity;
        private float _headContributionVelocity;
        private bool _amplitudeInitialized;

        // Recruitment latch state. The measured requirement's own rate (deg/s, smoothed) is what
        // tells sustained target motion from noise; the two clocks debounce engage and release.
        private Vector2 _previousRequired;
        private bool _hasPreviousRequired;
        private float _smoothedRequiredRate;
        private float _sustainedMotionSeconds;
        private float _quiescentSeconds;
        private float _headEntryLatch;
        private float _torsoEntryLatch;
        private float _headLatchDecayVelocity;
        private float _torsoLatchDecayVelocity;

        /// <summary>Seconds since the current shift began. Diagnostics, and the ladder's input.</summary>
        public float ShiftAge => _shiftAge;

        /// <summary>Deepest rung the last plan recruited.</summary>
        public GazeLadderDepth Depth { get; private set; } = GazeLadderDepth.Idle;

        /// <summary>The plan produced by the last <see cref="Plan" /> call.</summary>
        public GazeShiftPlan Current { get; private set; } = GazeShiftPlan.Idle;

        /// <summary>How hard held-off-centre eyes are currently asking the head to take over (0–1).</summary>
        public float OrbitPressure => _comfort.OrbitPressure;

        /// <summary>How hard a held neck turn is currently asking for the feet (0–1).</summary>
        public float ComfortPressure => _comfort.ComfortPressure;

        public void Reset()
        {
            _generation = int.MinValue;
            _shiftAge = 0f;
            _engagement = 0f;
            _headContribution = 0f;
            _engagementVelocity = 0f;
            _headContributionVelocity = 0f;
            _amplitudeInitialized = false;
            _previousRequired = Vector2.zero;
            _hasPreviousRequired = false;
            _smoothedRequiredRate = 0f;
            _sustainedMotionSeconds = 0f;
            _quiescentSeconds = 0f;
            _headEntryLatch = 0f;
            _torsoEntryLatch = 0f;
            _headLatchDecayVelocity = 0f;
            _torsoLatchDecayVelocity = 0f;
            _comfort.Reset();
            Depth = GazeLadderDepth.Idle;
            Current = GazeShiftPlan.Idle;
        }

        /// <summary>
        ///     Advances the shift clock and divides this frame's requirement across the ladder.
        /// </summary>
        /// <param name="measurement">The shift still required, measured once from the rig.</param>
        /// <param name="profile">Tuning source.</param>
        /// <param name="engagement">0–1 commitment to the target.</param>
        /// <param name="headContribution">0–1 head willingness from the conversation state policy.</param>
        /// <param name="torsoAvailable">Whether the rig has a torso this character is allowed to use.</param>
        /// <param name="feetAvailable">
        ///     Whether the character's state permits a body turn right now. False while walking a
        ///     path: two systems writing yaw at once is not a coordination problem the ladder can
        ///     solve, so it stands the rung down rather than competing — and false in the states
        ///     that would rather not turn (Thinking, Settling, a glance). The second of those is
        ///     reluctance rather than a ban: paired with
        ///     <paramref name="facingOwnedElsewhere" /> being false it still lets a target the
        ///     head and eyes cannot reach recruit the feet, late. See
        ///     <see cref="GazeLadderCapacity.FeetReluctant" />.
        /// </param>
        /// <param name="generationId">Changes when the gaze re-targets, which starts a new shift.</param>
        /// <param name="achievedEyeEccentricityDegrees">
        ///     How far the eyes actually ended up from centre last frame. Fed back in so a pose
        ///     the ladder produced but the body finds uncomfortable keeps evolving — see
        ///     <see cref="GazeComfortModel" />.
        /// </param>
        /// <param name="achievedHeadYawDegrees">Head yaw actually held last frame.</param>
        /// <param name="eyeRestDegrees">
        ///     How far from centre the eyes may be left resting for this look, in degrees — the
        ///     floor on the head's share. Negative (the default) uses the profile's comfort band.
        ///     A wider budget (a glance) is drawn back in to the comfort band under orbit
        ///     pressure, so a held glance is finished by the head like any other look.
        /// </param>
        /// <param name="facingOwnedElsewhere">
        ///     True while the character's facing belongs to another system (walking a path).
        ///     Caps the head at the neck's comfort angle so it does not crane while waiting
        ///     for the body. A policy that merely disallows the body turn must NOT set this —
        ///     see <see cref="GazeLadderCapacity.FacingOwnedElsewhere" />.
        /// </param>
        /// <param name="bodyTurnForbidden">
        ///     True when the look itself forbids the body turn (a scripted request's option, a
        ///     glance). Unlike a dialogue state that would rather not turn, this is never
        ///     overruled for a target beyond reach — the feet are simply not on the ladder.
        /// </param>
        /// <param name="continuesPreviousShift">
        ///     True when the new target is the same movement continued rather than a fresh
        ///     decision — the hand-off from the path a walking character is watching to whatever
        ///     is at the end of it. Arriving somewhere and settling onto what you came for is
        ///     one movement, not two, so the cascade keeps running instead of restarting and
        ///     freezing the head for another onset.
        /// </param>
        /// <param name="deltaTime">Tick delta.</param>
        public GazeShiftPlan Plan(
            in GazeShiftMeasurement measurement,
            ConvaiGazeProfile profile,
            float engagement,
            float headContribution,
            bool torsoAvailable,
            bool feetAvailable,
            int generationId,
            float deltaTime,
            float achievedEyeEccentricityDegrees = 0f,
            float achievedHeadYawDegrees = 0f,
            bool continuesPreviousShift = false,
            float eyeRestDegrees = -1f,
            bool facingOwnedElsewhere = false,
            bool bodyTurnForbidden = false)
        {
            if (profile == null)
            {
                Current = GazeShiftPlan.Idle;
                Depth = GazeLadderDepth.Idle;
                return Current;
            }

            // A re-target is a new shift, so the cascade restarts. Note this is the ONLY clock
            // in the chain now: the head no longer has a latency timer of its own and the body
            // turn no longer has a hysteresis hold, which is what used to let three independent
            // waits stack into a visible freeze on arrival.
            bool newLook = generationId != _generation;
            if (newLook)
            {
                _generation = generationId;
                if (!continuesPreviousShift) _shiftAge = 0f;
                else _shiftAge += Mathf.Max(0f, deltaTime);
            }
            else
            {
                _shiftAge += Mathf.Max(0f, deltaTime);
            }

            // The amplitude the ladder divides — stepped on a new look, eased within one.
            SettleAmplitude(newLook, engagement, headContribution, deltaTime);
            float ladderEngagement = _engagement;
            float ladderHeadContribution = _headContribution;

            // Recruitment, latched per look rather than recomputed per frame — see the constants
            // block above for why the instantaneous ease cannot be fed to the ladder live.
            AdvanceRecruitment(
                newLook && !continuesPreviousShift, in measurement, profile, deltaTime);

            // The eye budget for this look. A glance may rest its eyes further out than a
            // committed look, but a glance that is HELD becomes a look: orbit pressure draws the
            // budget in to the profile's comfort band, so the head finishes what the eyes
            // started rather than the character staring sideways for as long as it is held.
            float comfortBand = profile.EyeComfortDegrees;
            float lookBudget = eyeRestDegrees < 0f ? comfortBand : eyeRestDegrees;
            float eyeBudget = lookBudget > comfortBand
                ? Mathf.Lerp(lookBudget, comfortBand, _comfort.OrbitPressure)
                : lookBudget;

            // The two ways the feet can be off, kept apart. A profile that never turns its body,
            // or a facing something else already owns, is settled and the ladder does not reopen
            // it. A dialogue state that would rather not turn is a preference, and a preference
            // does not survive a target the head and eyes cannot reach — see the ladder's
            // remarks on reluctance.
            bool feetPermitted = feetAvailable && profile.EnableBodyTurn;
            bool feetReluctant = !feetAvailable && profile.EnableBodyTurn && !facingOwnedElsewhere &&
                                 !bodyTurnForbidden;

            var capacity = new GazeLadderCapacity(
                profile.MaxHeadYawDegrees,
                profile.MaxHeadPitchDegrees,
                profile.MaxTorsoYawDegrees,
                profile.MaxTorsoPitchDegrees,
                ladderHeadContribution,
                profile.HeadComfortYawDegrees,
                torsoAvailable && profile.EnableTorsoRecruitment,
                feetPermitted,
                eyeBudget,
                facingOwnedElsewhere,
                feetReluctant);

            var tuning = new GazeLadderTuning(
                profile.HeadEntryDegrees,
                profile.TorsoEntryDegrees,
                profile.FeetEntryDegrees,
                profile.HeadOnsetSeconds,
                profile.TorsoOnsetSeconds,
                profile.FeetOnsetSeconds);

            // Comfort runs on what was ACHIEVED, not on what was planned: the question is
            // whether the pose the character is actually holding is tiring, which only the
            // previous frame's outcome can answer.
            _comfort.Tick(
                achievedEyeEccentricityDegrees,
                achievedHeadYawDegrees,
                profile.EyeComfortDegrees,
                profile.HeadComfortYawDegrees,
                ladderEngagement > 0.0001f,
                deltaTime);

            Current = GazeActuatorLadder.Solve(
                in measurement, in capacity, in tuning, _shiftAge, ladderEngagement,
                _comfort.OrbitPressure, _comfort.ComfortPressure,
                _headEntryLatch, _torsoEntryLatch);
            Depth = Current.Depth;
            return Current;
        }

        /// <summary>
        ///     Advances the recruitment latches: the target-motion estimate, the engage/release
        ///     clocks, and the head/torso entry eases the ladder will be handed.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The rules, in order: a new look STEPS both latches to the instantaneous ease —
        ///         that step is a decision, and the actuator's ballistic lane exists to shape it.
        ///         Within a look a latch may RISE, under the slew bound, when the look grows past
        ///         a rung's entry or (head only) when sustained target motion recruits it — a
        ///         head glides into pursuit over a few tenths of a second, it is not launched.
        ///         A latch never falls with amplitude: a target crossing centre is not a decision
        ///         to stand the head down, and un-latching there is precisely the centre-freeze.
        ///         Only quiescence relaxes a latch, gently, back to the instantaneous ease.
        ///     </para>
        ///     <para>
        ///         The torso gets the ratchet-and-relax treatment but not the motion floor:
        ///         pursuit of a drifting target is a head-and-eyes behaviour, and a chest that
        ///         swings with every walk-by reads theatrical.
        ///     </para>
        /// </remarks>
        private void AdvanceRecruitment(
            bool freshLook,
            in GazeShiftMeasurement measurement,
            ConvaiGazeProfile profile,
            float deltaTime)
        {
            float dt = Mathf.Max(0f, deltaTime);
            if (!measurement.IsValid)
            {
                _hasPreviousRequired = false;
                _smoothedRequiredRate = 0f;
                _sustainedMotionSeconds = 0f;
                _quiescentSeconds = 0f;
                return;
            }

            float amplitude = measurement.Amplitude;
            var required = new Vector2(measurement.RequiredYaw, measurement.RequiredPitch);

            if (freshLook || !_hasPreviousRequired)
            {
                // The jump to a new target is a decision, not target motion — it must not seed
                // the rate estimate, or every re-target would masquerade as a sprinting target.
                _previousRequired = required;
                _hasPreviousRequired = true;
                _smoothedRequiredRate = 0f;
                _sustainedMotionSeconds = 0f;
                _quiescentSeconds = 0f;

                if (freshLook)
                {
                    _headEntryLatch = GazeActuatorLadder.EntryEase(amplitude, profile.HeadEntryDegrees);
                    _torsoEntryLatch = GazeActuatorLadder.EntryEase(amplitude, profile.TorsoEntryDegrees);
                    _headLatchDecayVelocity = 0f;
                    _torsoLatchDecayVelocity = 0f;
                }

                return;
            }

            if (dt > 0f)
            {
                float rate = (required - _previousRequired).magnitude / dt;
                _smoothedRequiredRate += (rate - _smoothedRequiredRate) *
                                         (1f - Mathf.Exp(-RateSmoothingSharpness * dt));
            }
            _previousRequired = required;

            if (_smoothedRequiredRate >= MotionEngageDegreesPerSecond)
            {
                _sustainedMotionSeconds += dt;
                _quiescentSeconds = 0f;
            }
            else if (_smoothedRequiredRate < MotionReleaseDegreesPerSecond)
            {
                _sustainedMotionSeconds = 0f;
                _quiescentSeconds += dt;
            }

            bool sustainedMotion = _sustainedMotionSeconds >= MotionEngageSeconds;
            bool quiescent = _quiescentSeconds >= QuiescenceSeconds;

            _headEntryLatch = AdvanceLatch(
                _headEntryLatch,
                GazeActuatorLadder.EntryEase(amplitude, profile.HeadEntryDegrees),
                sustainedMotion ? 1f : 0f,
                amplitude, dt, quiescent, ref _headLatchDecayVelocity);
            _torsoEntryLatch = AdvanceLatch(
                _torsoEntryLatch,
                GazeActuatorLadder.EntryEase(amplitude, profile.TorsoEntryDegrees),
                0f,
                amplitude, dt, quiescent, ref _torsoLatchDecayVelocity);
        }

        /// <summary>One latch's frame advance: slew-bounded rise, ratchet hold, quiescent relax.</summary>
        private float AdvanceLatch(
            float latch,
            float instantEase,
            float motionFloor,
            float amplitude,
            float dt,
            bool quiescent,
            ref float decayVelocity)
        {
            float target = Mathf.Max(instantEase, motionFloor);
            if (target > latch)
            {
                // Goal velocity added by a rising latch is amplitude × dLatch/dt; bounding that
                // at (budget + the target's own rate) keeps a recruitment inside the same speed
                // regime as the motion that caused it.
                float rise = dt * (RecruitmentSlewBudgetDegreesPerSecond + _smoothedRequiredRate) /
                             Mathf.Max(amplitude, SlewAmplitudeFloorDegrees);
                decayVelocity = 0f;
                return Mathf.Min(target, latch + rise);
            }

            if (!quiescent) return latch;

            return Mathf.SmoothDamp(
                latch, instantEase, ref decayVelocity, RecruitmentDecaySeconds, Mathf.Infinity, dt);
        }

        /// <summary>
        ///     Advances the two values that set the ladder's amplitude — how strongly the
        ///     character is committed to this look, and how much of it the head is willing to
        ///     take — stepping them on a new look and easing them within one.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Why the two cases differ.</b> The actuator turns a step in its goal into a
        ///         movement: it picks a duration from the amplitude and runs a velocity profile.
        ///         That is exactly right when the goal stepped because the character decided to
        ///         look somewhere else — the step IS the decision, and the ladder must see the
        ///         full destination on the first frame or the movement is planned against the
        ///         wrong amplitude.
        ///     </para>
        ///     <para>
        ///         It is exactly wrong when the goal stepped because the <i>weighting</i> of an
        ///         unchanged look moved. A conversation-state edge, the floor-yield engagement
        ///         pin arming and expiring, the target-loss search's floor, an emotion modifier —
        ///         none of them is a decision to look elsewhere, and the target has not moved. Fed
        ///         straight through, each one made the actuator plan a full, correctly shaped
        ///         movement to re-aim a look that never changed: on the shipped table, Speaking
        ///         (1.0 / 0.85) to Settling (0.6 / 0.6) drops head participation from 0.85 to 0.36
        ///         in one frame, so the head deliberately turns away from a target the eyes are
        ///         still holding, and does it again when the pin expires 0.8 s later.
        ///     </para>
        ///     <para>
        ///         The generation id is what tells the two apart, and it is the same signal the
        ///         shift clock above already trusts to mean "this is a different look".
        ///     </para>
        ///     <para>
        ///         Second order (critically damped) rather than the exponential the policy engine
        ///         uses for gains: the eased value is a movement's goal, and it is consumed by a
        ///         tracking filter that is transparent to in-budget motion, so whatever shape the
        ///         goal has is the shape that reaches the bone. An exponential starts at its peak
        ///         rate, which puts a velocity step on the neck; this starts and ends at rest.
        ///     </para>
        /// </remarks>
        private void SettleAmplitude(
            bool newLook,
            float engagement,
            float headContribution,
            float deltaTime)
        {
            engagement = Mathf.Clamp01(engagement);
            headContribution = Mathf.Clamp01(headContribution);

            if (!_amplitudeInitialized || newLook)
            {
                _amplitudeInitialized = true;
                _engagement = engagement;
                _headContribution = headContribution;
                _engagementVelocity = 0f;
                _headContributionVelocity = 0f;
                return;
            }

            float dt = Mathf.Max(0f, deltaTime);
            _engagement = Mathf.SmoothDamp(
                _engagement, engagement, ref _engagementVelocity,
                ReweightSettleSeconds, Mathf.Infinity, dt);
            _headContribution = Mathf.SmoothDamp(
                _headContribution, headContribution, ref _headContributionVelocity,
                ReweightSettleSeconds, Mathf.Infinity, dt);
        }
    }
}
