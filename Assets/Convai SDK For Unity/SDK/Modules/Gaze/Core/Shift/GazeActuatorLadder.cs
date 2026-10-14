using Convai.Modules.Gaze.Core.Solvers;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Shift
{
    /// <summary>
    ///     What each rung of the ladder is physically able to contribute, and how willing this
    ///     character is to use it. Separated from the ladder itself so the distribution rule can
    ///     be tested against arbitrary rigs and personalities without a profile or a scene.
    /// </summary>
    internal readonly struct GazeLadderCapacity
    {
        public readonly float HeadYaw;
        public readonly float HeadPitch;
        public readonly float TorsoYaw;
        public readonly float TorsoPitch;

        /// <summary>0–1: how much of a shift this character's head is willing to take at all.</summary>
        public readonly float HeadWillingness;

        /// <summary>
        ///     Head yaw (degrees) the neck can hold without wanting relief. Used as the head's
        ///     cap while the feet cannot help — see the ladder's remarks.
        /// </summary>
        public readonly float HeadComfortYaw;

        /// <summary>False when the rig has no torso bones, or the character never uses them.</summary>
        public readonly bool TorsoAvailable;

        /// <summary>False while something else owns the character's facing (walking a path, scripted).</summary>
        public readonly bool FeetAvailable;

        /// <summary>
        ///     True when this character COULD turn its feet but the dialogue state would rather
        ///     it did not — the Thinking/Settling/Idle rows, a glance, a look round the room.
        ///     Such a state suppresses the comfort turn and the ordinary residual turn, but it
        ///     is not a ban: see the ladder's remarks on reluctance. Never set while the feet
        ///     are unavailable for a reason the character cannot argue with — the profile switch
        ///     is off, or something else owns the facing.
        /// </summary>
        public readonly bool FeetReluctant;

        /// <summary>
        ///     How far (degrees) the eyes may be left resting from centre once the head has
        ///     joined. The head takes at least what lies beyond this, whatever its willingness —
        ///     see the ladder's remarks on the eye budget. Zero or negative disables the floor
        ///     and leaves the head on its willing share alone.
        /// </summary>
        public readonly float EyeRestDegrees;

        /// <summary>
        ///     True while another system owns the character's facing — it is walking a path,
        ///     or a scripted turn is in progress — as opposed to the feet merely being
        ///     disallowed by policy. Only this caps the head at the neck's comfort angle: a
        ///     character that will not turn its body still turns its head fully; a character
        ///     that is about to be turned by its legs does not crane in the meantime.
        /// </summary>
        public readonly bool FacingOwnedElsewhere;

        public GazeLadderCapacity(
            float headYaw,
            float headPitch,
            float torsoYaw,
            float torsoPitch,
            float headWillingness,
            float headComfortYaw,
            bool torsoAvailable,
            bool feetAvailable,
            float eyeRestDegrees = 0f,
            bool facingOwnedElsewhere = false,
            bool feetReluctant = false)
        {
            FeetReluctant = feetReluctant;
            FacingOwnedElsewhere = facingOwnedElsewhere;
            HeadYaw = headYaw;
            HeadPitch = headPitch;
            TorsoYaw = torsoYaw;
            TorsoPitch = torsoPitch;
            HeadWillingness = headWillingness;
            HeadComfortYaw = headComfortYaw;
            TorsoAvailable = torsoAvailable;
            FeetAvailable = feetAvailable;
            EyeRestDegrees = eyeRestDegrees;
        }
    }

    /// <summary>
    ///     Entry angles and onset delays for each rung — the ladder's tuning, read straight off
    ///     the profile.
    /// </summary>
    internal readonly struct GazeLadderTuning
    {
        public readonly float HeadEntryDegrees;
        public readonly float TorsoEntryDegrees;
        public readonly float FeetEntryDegrees;
        public readonly float HeadOnsetSeconds;
        public readonly float TorsoOnsetSeconds;
        public readonly float FeetOnsetSeconds;

        public GazeLadderTuning(
            float headEntryDegrees,
            float torsoEntryDegrees,
            float feetEntryDegrees,
            float headOnsetSeconds,
            float torsoOnsetSeconds,
            float feetOnsetSeconds)
        {
            HeadEntryDegrees = headEntryDegrees;
            TorsoEntryDegrees = torsoEntryDegrees;
            FeetEntryDegrees = feetEntryDegrees;
            HeadOnsetSeconds = headOnsetSeconds;
            TorsoOnsetSeconds = torsoOnsetSeconds;
            FeetOnsetSeconds = feetOnsetSeconds;
        }
    }

    /// <summary>
    ///     Divides one gaze shift across eyes → head → torso → feet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Stateless and engine-free by design: given the shift, what the rig can do, and how
    ///         long the shift has been running, there is exactly one right answer, and it can be
    ///         checked without a rig, a scene or a frame loop. All the state a gaze shift has —
    ///         when it started, what the actuators achieved — lives in
    ///         <see cref="GazeShiftDirector" />.
    ///     </para>
    ///     <para>
    ///         <b>The distribution rule.</b> Rungs are recruited in order and each one is handed
    ///         only what the rungs above it could not take:
    ///     </para>
    ///     <list type="number">
    ///         <item><description>the head takes its willing share of the shift, capped by its range;</description></item>
    ///         <item><description>the torso takes what is left, capped by its (much smaller) range;</description></item>
    ///         <item><description>the feet are asked to close whatever still remains in yaw.</description></item>
    ///     </list>
    ///     <para>
    ///         The eyes are not in that list because they are the residual — see
    ///         <see cref="GazeShiftPlan" />. Because each rung is handed a <i>remainder</i>
    ///         rather than an independently-computed fraction, the contributions always sum to
    ///         the shift, which is the invariant that stops one actuator backing off and another
    ///         silently saturating.
    ///     </para>
    ///     <para>
    ///         <b>Why the feet decide on the residual.</b> A fixed "turn the body past N degrees"
    ///         tripwire cannot know whether the neck is comfortable: at 60° it fires for a
    ///         character whose head could have taken it easily, and holds off for one already
    ///         pinned at its limit. Measuring what is left after the head and torso have taken
    ///         their share asks the question that actually matters — is anything still unmet?
    ///     </para>
    ///     <para>
    ///         <b>The eye budget.</b> Because the eyes are the residual, a head that takes only
    ///         a <i>fraction</i> of the shift leaves the eyes holding the rest — and that rest
    ///         grows with the size of the look. A listener half-engaged with somebody 45° round
    ///         (engagement 0.6, head 0.7) was left with 26° in the eyes, which is the corner of
    ///         the socket: the head faces one way and the eyes another, and it reads as shifty
    ///         rather than attentive. People do not do that. The eyes rest within a small band
    ///         of centre and the head turns as far as it must to keep them there; how willing
    ///         the head is only decides how much of a <i>small</i> look it joins. So the head's
    ///         share is the larger of its willing fraction and the part of the shift that lies
    ///         beyond <see cref="GazeLadderCapacity.EyeRestDegrees" />. The floor is gated by
    ///         the head's onset like every other share (the eyes still lead), and what the feet
    ///         are asked about is still the <i>willing</i> residual: a reluctant head that has
    ///         been made to turn is exactly a character that would rather turn its body.
    ///     </para>
    ///     <para>
    ///         <b>Reluctance is not a ban.</b> A dialogue state that says "do not turn the body"
    ///         — Thinking, Settling, a glance — is describing a person who would rather not get
    ///         up, not one who is bolted to the floor. Read as a ban it produced the failure it
    ///         was meant to prevent: with somebody 95° round, the head saturated at its limit,
    ///         nothing was left that could clamp it, and the whole unreachable remainder landed
    ///         in the eyes, which held 31° at the corner of the socket for as long as the state
    ///         lasted. So the flag suppresses the two turns that are a matter of preference —
    ///         the comfort turn a held neck asks for, and the ordinary residual turn — and does
    ///         not survive a target the head and eyes cannot reach between them. That case is
    ///         measured on the ceiling, not on this frame's share: what would still be left over
    ///         if the head and chest both committed fully, against the band the eyes may rest
    ///         in. Reluctance shows as delay, so the feet wait
    ///         <see cref="ReluctantFeetOnsetMultiple" />× their usual onset before joining.
    ///     </para>
    /// </remarks>
    internal static class GazeActuatorLadder
    {
        /// <summary>
        ///     Width (degrees) of the band over which a rung fades in around its entry angle.
        ///     A hard cut-in reads as the head snapping into service the instant a target
        ///     crosses an invisible line.
        /// </summary>
        private const float EntryBlendDegrees = 10f;

        // A rung's onset used to ramp its participation in over 0.08 s rather than switching it
        // on, because the stage downstream was a rate limiter and a step in its goal came out as
        // a step in the pose. That is no longer true — the actuator turns a step into a movement
        // with a duration of its own — and once it is, the ramp is actively harmful: the share
        // and the movement would both be shaping the same 0.2 s of time, which is two opinions
        // about one movement, the exact failure this ladder exists to prevent. The onset is now
        // purely a gate: it says WHEN a rung joins, and the actuator owns everything about how.

        /// <summary>Soft-limit knee, shared with the rest of the solver chain.</summary>
        private const float SoftLimitFraction = 0.85f;

        /// <summary>
        ///     How much longer than usual the feet wait when the state would rather they did not
        ///     turn at all. A reluctant turn still happens — see the remarks on reluctance — but
        ///     it must not look like the willing one, and the only honest difference between
        ///     "I will" and "…fine, I will" is how long the character holds out first. Expressed
        ///     as a multiple of the profile's own feet onset so a personality that is quick or
        ///     slow to turn stays quick or slow when it is reluctant.
        /// </summary>
        internal const float ReluctantFeetOnsetMultiple = 2f;

        /// <summary>
        ///     Fraction of the eye rest band a fully committed head and chest may leave to the
        ///     eyes before the target counts as out of reach. Measured on the rig, the
        ///     prediction is optimistic by a few degrees: a 90° target predicted 13° left for
        ///     the eyes against a 14° band and was ruled reachable, and the eyes then sat 14–20°
        ///     out for the 1.4 s it took the comfort turn to fire. The margin makes "just inside
        ///     the band on paper" count as out of reach, which it is in practice.
        /// </summary>
        private const float ReachMarginFraction = 0.7f;

        /// <summary>
        ///     Divides <paramref name="measurement" /> across the ladder.
        /// </summary>
        /// <param name="measurement">This frame's required shift.</param>
        /// <param name="capacity">What the rig can contribute and is willing to.</param>
        /// <param name="tuning">Entry angles and onsets.</param>
        /// <param name="shiftAge">Seconds since this shift began — drives the onset cascade.</param>
        /// <param name="engagement">0–1 commitment to the target.</param>
        /// <param name="orbitPressure">
        ///     0–1 from <see cref="GazeComfortModel" />: how hard eyes held off-centre are
        ///     asking the head to take over. Raises the head's share toward its full range so a
        ///     shift the head was only half willing to make gets finished, and the eyes come
        ///     back to centre.
        /// </param>
        /// <param name="comfortPressure">
        ///     0–1 from <see cref="GazeComfortModel" />: how hard a held neck turn is asking for
        ///     the feet. At full pressure the body turns whatever the leftover angle is.
        /// </param>
        /// <param name="headEntryEase">
        ///     The head's recruitment ease for this look, decided and LATCHED by
        ///     <see cref="GazeShiftDirector" />. Negative (the default) recomputes it from the
        ///     instantaneous amplitude, which is correct for a step-and-hold target and is what
        ///     the ladder's own unit tests exercise — but under a MOVING target the instantaneous
        ///     ease is an amplitude-scheduled gain: it sweeps the head's goal at up to ~3× the
        ///     target's own rate through the entry band and zeroes it below the entry angle even
        ///     in sustained motion, which is the whip–freeze–whip ratchet. Live callers
        ///     therefore pass the director's latched value.
        /// </param>
        /// <param name="torsoEntryEase">Same contract as <paramref name="headEntryEase" />, for the chest.</param>
        public static GazeShiftPlan Solve(
            in GazeShiftMeasurement measurement,
            in GazeLadderCapacity capacity,
            in GazeLadderTuning tuning,
            float shiftAge,
            float engagement,
            float orbitPressure = 0f,
            float comfortPressure = 0f,
            float headEntryEase = -1f,
            float torsoEntryEase = -1f)
        {
            if (!measurement.IsValid || engagement <= 0.0001f) return GazeShiftPlan.Idle;

            float amplitude = measurement.Amplitude;
            float clampedEngagement = Mathf.Clamp01(engagement);

            // ---- Head rung -------------------------------------------------------------
            // Orbit return: a character whose personality only half-commits its head still
            // finishes the job when the eyes have been stuck at the corner of the socket for a
            // second or two. The pressure interpolates willingness toward 1 rather than adding
            // to it, so it can never drive the head past the range the rig allows.
            float willingness = Mathf.Lerp(
                Mathf.Clamp01(capacity.HeadWillingness), 1f, Mathf.Clamp01(orbitPressure));

            // Whether the head is taking part in this look, kept separate from how much of it
            // it takes. The hand-over from idle life is driven by the first question alone —
            // see GazeShiftPlan.HeadRecruitment.
            float headEntry = headEntryEase >= 0f
                ? Mathf.Clamp01(headEntryEase)
                : EntryEase(amplitude, tuning.HeadEntryDegrees);
            float headOnset = OnsetEase(shiftAge, tuning.HeadOnsetSeconds);
            float headRecruitment = headEntry * headOnset;

            // The head is a rung of this shift whose turn has not come yet — as opposed to one
            // that is not a rung of it at all. Only the first is an onset gap for idle life to
            // bridge; see GazeShiftPlan.HeadOnsetPending.
            bool headOnsetPending = headEntry > 0.0001f && headOnset <= 0.0001f;

            float headParticipation = headRecruitment * clampedEngagement * willingness;

            // A rung must not take a share that only exists because the rung below it is
            // about to act. While something else owns the character's facing — most often
            // because it is still walking — the head is capped at what the neck can hold
            // comfortably instead of its anatomical limit. Without this, a character finishing
            // its walk cranes its neck to its full range at whoever it is about to face and
            // holds it there until the body is free, which reads as trying to look at you
            // while waiting to stop. It keeps facing where it is going and uses its eyes, then
            // turns properly once the feet are available.
            //
            // This is NOT the same as the feet being disallowed by policy. A listener whose
            // state says "no body turn" is a person who will not turn round, and such a person
            // turns their head as far as it goes; capping them at the comfort angle left the
            // eyes holding the rest — at 70° round, twenty degrees of it — which is the
            // sideways stare from the other direction.
            float headYawCap = !capacity.FacingOwnedElsewhere || capacity.HeadComfortYaw <= 0f
                ? capacity.HeadYaw
                : Mathf.Min(capacity.HeadYaw, capacity.HeadComfortYaw);

            // What the head would take of its own accord. Kept for the feet decision below.
            float willingYaw = GazeSolverMath.SoftClamp(
                measurement.RequiredYaw * headParticipation, headYawCap, SoftLimitFraction);

            // The eye budget: the eyes may rest only so far from centre, so once the head has
            // joined it takes at least the part of the shift that lies beyond that band, however
            // reluctant it is. Measured on the whole amplitude, not per axis, so a diagonal look
            // is held to the same eccentricity as a level one. Onset-gated like the willing
            // share: the eyes lead, and for the first beat they carry the whole look alone.
            float headFraction = headParticipation;
            if (capacity.EyeRestDegrees > 0f && amplitude > capacity.EyeRestDegrees)
            {
                float floorFraction = (1f - capacity.EyeRestDegrees / amplitude) * headOnset;
                headFraction = Mathf.Max(headParticipation, floorFraction);
            }

            float headYaw = GazeSolverMath.SoftClamp(
                measurement.RequiredYaw * headFraction, headYawCap, SoftLimitFraction);
            float headPitch = GazeSolverMath.SoftClamp(
                measurement.RequiredPitch * headFraction, capacity.HeadPitch, SoftLimitFraction);

            // ---- Torso rung: whatever the head could not take --------------------------
            float torsoYaw = 0f;
            float torsoPitch = 0f;
            // The chest the WILLING cascade would have produced, for the feet decision below —
            // a chest computed after the eye budget has forced the head is smaller than the one
            // a reluctant head would have left it, and mixing the two asks for the feet on
            // looks a willing cascade would have finished from the neck up.
            float willingTorsoYaw = 0f;
            if (capacity.TorsoAvailable)
            {
                float torsoEntry = torsoEntryEase >= 0f
                    ? Mathf.Clamp01(torsoEntryEase)
                    : EntryEase(amplitude, tuning.TorsoEntryDegrees);
                float torsoParticipation =
                    torsoEntry *
                    OnsetEase(shiftAge, tuning.TorsoOnsetSeconds) *
                    clampedEngagement;

                torsoYaw = GazeSolverMath.SoftClamp(
                    (measurement.RequiredYaw - headYaw) * torsoParticipation,
                    capacity.TorsoYaw, SoftLimitFraction);
                torsoPitch = GazeSolverMath.SoftClamp(
                    (measurement.RequiredPitch - headPitch) * torsoParticipation,
                    capacity.TorsoPitch, SoftLimitFraction);
                willingTorsoYaw = GazeSolverMath.SoftClamp(
                    (measurement.RequiredYaw - willingYaw) * torsoParticipation,
                    capacity.TorsoYaw, SoftLimitFraction);
            }

            // ---- Feet rung: whatever is still unmet in yaw ------------------------------
            float residualYaw = measurement.RequiredYaw - headYaw - torsoYaw;

            // Comfort return: the feet turn either because something is still unmet, or
            // because the neck has been held turned long enough to want relief even though
            // nothing is unmet. The second reason is why people turn to face someone they are
            // already looking at, and no fixed angle threshold can express it.
            //
            // The feet answer to what the head WANTED to leave, not to what the eye budget made
            // it take: a head forced past its willingness by the budget is a character that
            // would rather turn its body, and asking about the achieved residual would silently
            // retire the body turn for every reluctant personality.
            float willingResidualYaw = measurement.RequiredYaw - willingYaw - willingTorsoYaw;
            bool residualUnmet = Mathf.Abs(willingResidualYaw) > tuning.FeetEntryDegrees;
            bool neckWantsRelief = comfortPressure >= 1f;

            // Out of reach is the third reason, and it is not the same as the residual being
            // unmet. The residual is measured against the feet's entry tolerance on what the
            // head and chest WANT to leave; a target the head and chest cannot reach even at
            // their limits can leave a residual under that tolerance and still park the eyes
            // beyond the band they may rest in. Measured on the rig: a 95° target left 18° for
            // the feet — under the 25° entry — and the character stared 28° off it, eyes at
            // their limit, for the 1.6 s it took the neck's comfort pressure to build before
            // the body turned. Nobody waits like that: when the thing you are looking at is
            // out of reach of your head, your feet are already turning.
            bool beyondReach = IsBeyondReach(in measurement, in capacity, headYawCap, in tuning);
            bool wantsFeet =
                capacity.FeetAvailable &&
                (residualUnmet || neckWantsRelief || beyondReach) &&
                shiftAge >= tuning.FeetOnsetSeconds;

            // Reluctance, not a ban — see the remarks. Neither of the two reasons above applies
            // here: a state that would rather not turn does not turn for comfort, and does not
            // turn for a residual it could have left in the eyes. It turns only when the target
            // is out of reach of the head and eyes together, and it takes its time about it.
            if (!wantsFeet &&
                capacity.FeetReluctant &&
                shiftAge >= tuning.FeetOnsetSeconds * ReluctantFeetOnsetMultiple)
                wantsFeet = beyondReach;

            return new GazeShiftPlan(
                headYaw, headPitch, torsoYaw, torsoPitch, residualYaw, wantsFeet,
                ResolveDepth(amplitude, tuning, headParticipation, torsoYaw, wantsFeet),
                headRecruitment,
                headOnsetPending);
        }

        /// <summary>
        ///     Whether this shift is out of reach of the head and eyes together — the one case
        ///     a reluctant state cannot argue its way out of.
        /// </summary>
        /// <remarks>
        ///     Measured on the CEILING rather than on this frame's allocation: the question is
        ///     not "is anything unmet right now", which the willing residual already answers for
        ///     the ordinary turn, but "would anything still be unmet if the head and chest both
        ///     committed as far as they physically go". Anything beyond that has nowhere to live
        ///     except the eyes, and the eyes may only rest within
        ///     <see cref="GazeLadderCapacity.EyeRestDegrees" />. The feet's own entry tolerance
        ///     stands in for the band on a profile that has switched the eye budget off, so
        ///     such a profile asks the same question the willing residual does rather than
        ///     firing on a stray degree.
        /// </remarks>
        internal static bool IsBeyondReach(
            in GazeShiftMeasurement measurement,
            in GazeLadderCapacity capacity,
            float headYawCap,
            in GazeLadderTuning tuning)
        {
            float committedHeadYaw =
                GazeSolverMath.SoftClamp(measurement.RequiredYaw, headYawCap, SoftLimitFraction);
            float committedTorsoYaw = capacity.TorsoAvailable
                ? GazeSolverMath.SoftClamp(
                    measurement.RequiredYaw - committedHeadYaw, capacity.TorsoYaw, SoftLimitFraction)
                : 0f;

            float leftForTheEyes =
                measurement.RequiredYaw - committedHeadYaw - committedTorsoYaw;
            float eyesMayRest = capacity.EyeRestDegrees > 0f
                ? capacity.EyeRestDegrees * ReachMarginFraction
                : Mathf.Max(0f, tuning.FeetEntryDegrees);

            return Mathf.Abs(leftForTheEyes) > eyesMayRest;
        }

        /// <summary>
        ///     0 below a rung's entry angle, 1 above it, smoothstepped across
        ///     <see cref="EntryBlendDegrees" /> centred on the entry so the rung fades in.
        /// </summary>
        internal static float EntryEase(float amplitude, float entryDegrees)
        {
            float half = EntryBlendDegrees * 0.5f;
            return GazeSolverMath.RecruitmentEase(
                amplitude, entryDegrees - half, entryDegrees + half);
        }

        /// <summary>
        ///     0 before a rung's onset elapses, 1 after. This is the cascade: one clock, started
        ///     when the shift started, read at a different offset by each rung — not three
        ///     independent hold timers that can stack into a freeze.
        /// </summary>
        /// <remarks>
        ///     A gate rather than a ramp, deliberately — see the note where the ramp used to be
        ///     declared. The step this produces is not a discontinuity in the pose: it is a
        ///     discontinuity in the GOAL, which is what a decision to move looks like, and
        ///     turning it into a movement is the actuator's job.
        /// </remarks>
        internal static float OnsetEase(float shiftAge, float onsetSeconds) =>
            onsetSeconds <= 0f || shiftAge >= onsetSeconds ? 1f : 0f;

        private static GazeLadderDepth ResolveDepth(
            float amplitude,
            in GazeLadderTuning tuning,
            float headParticipation,
            float torsoYaw,
            bool wantsFeet)
        {
            if (wantsFeet) return GazeLadderDepth.Feet;
            if (Mathf.Abs(torsoYaw) > 0.01f) return GazeLadderDepth.Torso;
            if (headParticipation > 0.01f) return GazeLadderDepth.Head;
            return amplitude > 0.01f ? GazeLadderDepth.Eyes : GazeLadderDepth.Idle;
        }
    }
}
