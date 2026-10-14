using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Animation.ProceduralPose;
using Convai.Shared.Compatibility;
using UnityEngine;

namespace Convai.Modules.Gaze.Core.Targeting
{
    /// <summary>Arbiter output for one tick.</summary>
    internal struct GazeTargetDecision
    {
        public GazeTargetKind Kind;
        public Transform Target;
        public Vector3 SmoothedPoint;
        public float Commitment;       // 0..1 acquisition/release ramp
        public int GenerationId;       // bumps on every re-target and teleport
        public string Name;

        /// <summary>
        ///     The aim point moved discontinuously this tick, so the eye stage must re-acquire
        ///     with a fresh ballistic saccade instead of gliding. Set by BOTH a re-target and a
        ///     camera cut — the eyes jump either way.
        /// </summary>
        public bool TeleportedThisTick;

        /// <summary>
        ///     The point jumped because the world did, not because the character chose to look
        ///     somewhere else: a camera cut or a teleported target, i.e. only the displacement
        ///     branch of <c>UpdateSmoothedPoint</c>. Deliberately narrower than
        ///     <see cref="TeleportedThisTick" /> — re-acquiring after a cut is a reflex and is
        ///     executed at reflex speed, while deciding to look at something is voluntary and
        ///     must not be. Conflating the two made every ordinary look — the idle curiosity
        ///     glance most visibly — execute as a startle.
        /// </summary>
        public bool WasCut;

        public float ScriptedEngagementOverride; // < 0 → none

        /// <summary>Head participation the scripted entry asks for, 0–1; < 0 → none.</summary>
        public float ScriptedHeadContributionOverride;

        public bool ScriptedAllowBodyTurn;
        public bool IsScripted;
        public int ScriptedEntryId;

        /// <summary>
        ///     What the character means by this look. Decided here, where the reason is known,
        ///     because nothing downstream can recover it from the geometry.
        /// </summary>
        public GazeLookNature Nature;

        /// <summary>Whether the aim point is somebody's face — see GazeTargetCandidate.HasFace.</summary>
        public bool TargetHasFace;

        /// <summary>
        ///     The look is being let go: nothing is offering this target any more and the
        ///     commitment ramp is running down. The head unwinds over that ramp, but the eyes must
        ///     not go on aiming at something the character has stopped looking at.
        /// </summary>
        public bool IsReleasing;

        public bool HasTarget => Kind != GazeTargetKind.None && Commitment > 0.0001f;

        public static GazeTargetDecision None => new()
        {
            Kind = GazeTargetKind.None,
            SmoothedPoint = Vector3.zero,
            Commitment = 0f,
            Name = "-",
            ScriptedEngagementOverride = -1f,
            ScriptedHeadContributionOverride = -1f,
            Nature = GazeLookNature.Attention
        };
    }

    /// <summary>
    ///     Pure-POCO target arbiter. Chooses one target per tick from scripted requests
    ///     (always win) and provider candidates (priority tier, then relevance × interest),
    ///     then shapes the result for believability: commitment ramps on acquire/release,
    ///     interest-budget scanning between equal candidates, point smoothing with
    ///     teleport/camera-cut detection, and a short hold after target loss so gaze decays
    ///     instead of snapping.
    /// </summary>
    /// <remarks>
    ///     The held target is sticky within its priority tier: an equal-priority challenger
    ///     can only take over through the interest-budget forced break (interest drained or
    ///     the continuous-hold cap reached), never by out-scoring the incumbent by the
    ///     epsilon that the drain itself creates — that would flicker the target every tick.
    ///     A higher priority tier still preempts instantly, and forced breaks only fire when
    ///     an alternative exists in the incumbent's own tier, so a committed player lock is
    ///     never broken against background world objects.
    /// </remarks>
    internal sealed class GazeTargetArbiter
    {
        /// <summary>Target-point conditioning, in metres and metres per second.</summary>
        /// <remarks>
        ///     <para>
        ///         Internal feel constants rather than profile fields: this is not a look an author
        ///         chooses, it is the difference between the aim point a character is given and the
        ///         one the world happens to be able to measure. A live target point is a bone on a
        ///         talking character or a camera with head bob, and both carry a centimetre or two
        ///         of oscillation that means nothing. It is enough to keep the head's pursuit
        ///         classifier engaged — its threshold is 4 deg/s and a 2 cm 2 Hz bob at 2 m peaks
        ///         at 6.4 — and an engaged pursuit rides the live goal, so the head follows the
        ///         bob instead of resting in its stability band. That is one of the ways a standing
        ///         conversation acquires small sudden repositions.
        ///     </para>
        ///     <para>
        ///         <b>1 Hz at rest</b> is well below anything an aim point does on purpose and well
        ///         above nothing, so a still target is held still. Measured through the trace
        ///         harness against the fixed 40 ms low pass this replaces (see
        ///         GazeTargetConditioningTests), a plus/minus 2 cm 2 Hz bob at 2 m arrives at the
        ///         head plan as 0.22 deg of yaw standard deviation instead of 0.36 — which is below
        ///         the pursuit threshold, so the head stops following it altogether: applied yaw
        ///         standard deviation 0.00 deg against 0.36, peak head speed 0.0 deg/s against 6.4.
        ///     </para>
        ///     <para>
        ///         <b>Beta = 5 per m/s</b> is the lag knob, and it is why this is a One-Euro filter
        ///         rather than simply a slower low pass. A fixed 1 Hz filter would put 16 cm of lag
        ///         on a walking person; opening the cut-off with the target's own measured speed
        ///         brings that to 2.6 cm at 1 m/s, which is TIGHTER than the filter it replaces — a
        ///         walk-by at 1.4 m/s past 2 m tracks 0.8 deg behind, against 1.3 deg before. The
        ///         conditioning therefore costs no pursuit quality; it only removes motion the
        ///         target never had. Published beta values (about 0.007) are pixel-scale — see
        ///         <see cref="OneEuroTuning" />.
        ///     </para>
        ///     <para>
        ///         <b>Derivative cut-off 1 Hz</b> is what tells a bob from a walk: an oscillation's
        ///         derivative changes sign twice a cycle and averages away, so the cut-off stays
        ///         shut for it, while a walk's is sustained and opens it.
        ///     </para>
        /// </remarks>
        private static readonly OneEuroTuning PointConditioning = new(1f, 5f, 1f);

        private readonly Dictionary<int, float> _interest = new(8);
        private readonly List<int> _staleKeys = new(8);

        private GazeTargetKind _currentKind;
        private Transform _currentTarget;
        private int _currentKey;
        private string _currentName = "-";
        private bool _hasCurrent;
        private float _commitment;
        private float _lossTimer;
        private float _continuousHoldSeconds;
        private Vector3 _smoothedPoint;
        private OneEuroVector3Filter _pointFilter;
        private bool _hasSmoothedPoint;
        private int _generationId;

        // Latched with the target rather than recomputed per frame: they must survive the
        // release ramp, when nothing is offering the candidate any more but the look is still
        // being let go and the actuators still need to know what kind of look they are ending.
        private GazeLookNature _currentNature = GazeLookNature.Attention;
        private bool _currentHasFace;

        /// <summary>Latest decision produced by <see cref="Tick" />.</summary>
        public GazeTargetDecision Current { get; private set; } = GazeTargetDecision.None;

        /// <summary>
        ///     Advances the arbiter by one tick.
        /// </summary>
        /// <param name="candidates">Provider candidates for this frame (not mutated).</param>
        /// <param name="scripted">Winning scripted entry, or null.</param>
        /// <param name="allowPlayerTarget">State-policy gate for Player-kind candidates.</param>
        /// <param name="profile">Timing/tuning source.</param>
        /// <param name="deltaTime">Tick delta time.</param>
        public GazeTargetDecision Tick(
            IReadOnlyList<GazeTargetCandidate> candidates,
            GazeTargetStack.Entry scripted,
            bool allowPlayerTarget,
            ConvaiGazeProfile profile,
            float deltaTime)
        {
            bool hasBest;
            GazeTargetKind bestKind;
            Transform bestTarget;
            Vector3 bestPoint;
            int bestKey;
            string bestName;
            int bestPriority = int.MaxValue; // scripted entries outrank every provider tier
            float scriptedOverride = -1f;
            float scriptedHeadOverride = -1f;
            bool scriptedBodyTurn = false;
            GazeLookNature bestNature = GazeLookNature.Attention;
            bool bestHasFace = false;
            bool isScripted = scripted != null;
            int scriptedEntryId = isScripted ? scripted.Id : 0;

            if (isScripted)
            {
                hasBest = true;
                bestKind = GazeTargetKind.Scripted;
                bestTarget = scripted.HasTransform ? scripted.Target : null;
                bestPoint = scripted.ResolvePoint();
                bestKey = ScriptedKey(scripted.Id);
                bestName = scripted.Name;
                scriptedOverride = scripted.EngagementOverride;
                scriptedHeadOverride = scripted.HeadContributionOverride;
                scriptedBodyTurn = scripted.AllowBodyTurn;
                bestNature = scripted.Nature;
                bestHasFace = scripted.TargetHasFace;
            }
            else
            {
                int bestIndex = SelectBestCandidate(candidates, allowPlayerTarget);
                RecoverNeglectedInterest(candidates, bestIndex, profile, deltaTime);
                PruneStaleInterest(candidates);

                hasBest = bestIndex >= 0;
                if (hasBest)
                {
                    GazeTargetCandidate chosen = candidates[bestIndex];
                    bestKind = chosen.Kind;
                    bestTarget = chosen.Target;
                    bestPoint = chosen.WorldPoint;
                    bestKey = CandidateKey(chosen);
                    bestName = chosen.DebugName;
                    bestPriority = chosen.Priority;
                    bestHasFace = chosen.HasFace;
                    // A provider candidate is something the character means to look at - it won
                    // the arbitration on its own relevance - so it is attention.
                    bestNature = GazeLookNature.Attention;
                }
                else
                {
                    bestKind = GazeTargetKind.None;
                    bestTarget = null;
                    bestPoint = default;
                    bestKey = 0;
                    bestName = "-";
                }
            }

            bool teleported = false;
            bool wasCut = false;

            if (hasBest)
            {
                bool changedTarget = !_hasCurrent || bestKey != _currentKey;
                if (changedTarget)
                {
                    _currentKind = bestKind;
                    _currentTarget = bestTarget;
                    _currentKey = bestKey;
                    _currentNature = bestNature;
                    _currentHasFace = bestHasFace;
                    _currentName = bestName;
                    _hasCurrent = true;
                    _continuousHoldSeconds = 0f;
                    _generationId++;
                    // A new target is acquired ballistically: no positional drag from the old
                    // point, and no inherited speed estimate either — the jump from the previous
                    // target is not this target moving.
                    _smoothedPoint = bestPoint;
                    _pointFilter.Reset(bestPoint);
                    _hasSmoothedPoint = true;
                    teleported = true;
                }
                else
                {
                    _continuousHoldSeconds += deltaTime;
                    _currentTarget = bestTarget;

                    if (!isScripted)
                        DrainInterestAndMaybeBreak(candidates, bestKey, bestPriority, profile, deltaTime);
                }

                _lossTimer = 0f;
                UpdateSmoothedPoint(bestPoint, profile, deltaTime, ref teleported, ref wasCut);
            }
            else if (_hasCurrent)
            {
                // Target lost: hold the last point briefly so release reads as a decay.
                _lossTimer += deltaTime;
                if (_lossTimer > Mathf.Max(0f, profile.TargetLossHoldSeconds))
                    ReleaseCurrent();
            }

            UpdateCommitment(hasBest, profile, deltaTime);

            if (_commitment <= 0.0001f && !hasBest)
            {
                _hasSmoothedPoint = false;
                _hasCurrent = false;
            }

            Current = new GazeTargetDecision
            {
                Kind = _hasCurrent || _commitment > 0.0001f ? _currentKind : GazeTargetKind.None,
                Target = _currentTarget,
                SmoothedPoint = _hasSmoothedPoint ? _smoothedPoint : Vector3.zero,
                Commitment = _commitment,
                GenerationId = _generationId,
                Name = _currentName,
                TeleportedThisTick = teleported,
                WasCut = wasCut,
                ScriptedEngagementOverride = isScripted ? scriptedOverride : -1f,
                ScriptedHeadContributionOverride = isScripted ? scriptedHeadOverride : -1f,
                ScriptedAllowBodyTurn = isScripted && scriptedBodyTurn,
                IsScripted = isScripted && _hasCurrent,
                ScriptedEntryId = isScripted && _hasCurrent ? scriptedEntryId : 0,
                // A cut does NOT make the look a reflex. It is the one thing the character did
                // not choose, which is why the eyes re-acquire ballistically — but a reflex is
                // something that happens TO the character and demands an answer now, and finding
                // the same target somewhere else is not that. Stamped Reflex, the whole shift ran
                // at the startle tempo: a 90° cut answered in two thirds of a second, with the
                // head peaking near 130 °/s. The character whipped round at its own camera.
                Nature = _currentNature,
                TargetHasFace = _currentHasFace,
                // Nothing is offering this target any more, but the ramp is still running down.
                IsReleasing = !hasBest && _commitment > 0.0001f
            };
            return Current;
        }

        /// <summary>Clears all internal state (disable/rebind).</summary>
        public void Reset()
        {
            _interest.Clear();
            _currentKind = GazeTargetKind.None;
            _currentTarget = null;
            _currentKey = 0;
            _currentName = "-";
            _hasCurrent = false;
            _commitment = 0f;
            _lossTimer = 0f;
            _continuousHoldSeconds = 0f;
            _smoothedPoint = default;
            _pointFilter.Clear();
            _hasSmoothedPoint = false;
            _currentNature = GazeLookNature.Attention;
            _currentHasFace = false;
            Current = GazeTargetDecision.None;
        }

        private int SelectBestCandidate(IReadOnlyList<GazeTargetCandidate> candidates, bool allowPlayerTarget)
        {
            int bestIndex = -1;
            float bestScore = float.NegativeInfinity;
            int bestPriority = int.MinValue;
            int incumbentIndex = -1;
            int incumbentPriority = int.MinValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                GazeTargetCandidate c = candidates[i];
                if (c.Relevance <= 0f) continue;
                if (c.Kind == GazeTargetKind.Player && !allowPlayerTarget) continue;

                int key = CandidateKey(c);
                if (_hasCurrent && key == _currentKey)
                {
                    incumbentIndex = i;
                    incumbentPriority = c.Priority;
                }

                float interest = GetOrInitInterest(key);
                float score = c.Relevance * interest;
                bool wins = c.Priority > bestPriority ||
                            (c.Priority == bestPriority && score > bestScore);
                if (wins)
                {
                    bestScore = score;
                    bestPriority = c.Priority;
                    bestIndex = i;
                }
            }

            // The incumbent is sticky within its tier: interest drain lowers its score a
            // hair below every untouched challenger, so a raw score comparison would flip
            // the target every other tick. Hand-offs inside a tier go through the forced
            // break (which zeroes interest and clears incumbency); a higher tier preempts.
            if (incumbentIndex >= 0 && incumbentPriority >= bestPriority)
                return incumbentIndex;

            return bestIndex;
        }

        private void DrainInterestAndMaybeBreak(
            IReadOnlyList<GazeTargetCandidate> candidates,
            int currentKey,
            int currentPriority,
            ConvaiGazeProfile profile,
            float deltaTime)
        {
            float interest = GetOrInitInterest(currentKey);
            interest = Mathf.Max(0f, interest - profile.InterestDecayPerSecond * deltaTime);

            bool forceBreak =
                _continuousHoldSeconds > profile.MaxContinuousHoldSeconds ||
                interest <= profile.InterestBreakThreshold;

            if (forceBreak)
            {
                if (HasSameTierAlternative(candidates, currentKey, currentPriority))
                {
                    interest = 0f;
                    _hasCurrent = false;
                    _currentKey = 0;
                }
                else
                {
                    interest = Mathf.Max(interest, profile.InterestBreakThreshold);
                    _continuousHoldSeconds = Mathf.Min(_continuousHoldSeconds, profile.MaxContinuousHoldSeconds);
                }
            }

            _interest[currentKey] = interest;
        }

        private void RecoverNeglectedInterest(
            IReadOnlyList<GazeTargetCandidate> candidates,
            int bestIndex,
            ConvaiGazeProfile profile,
            float deltaTime)
        {
            if (profile.InterestRecoveryPerSecond <= 0f) return;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (i == bestIndex) continue;

                int key = CandidateKey(candidates[i]);
                float interest = GetOrInitInterest(key);
                _interest[key] = Mathf.Min(1f, interest + profile.InterestRecoveryPerSecond * deltaTime);
            }
        }

        private void UpdateCommitment(bool hasBest, ConvaiGazeProfile profile, float deltaTime)
        {
            bool inActiveWindow = hasBest || _hasCurrent;
            float target = inActiveWindow ? 1f : 0f;
            float rampSeconds = target > _commitment
                ? profile.CommitmentAcquireSeconds
                : profile.CommitmentReleaseSeconds;
            float rampRate = rampSeconds > 0.0001f ? 1f / rampSeconds : 1e6f;
            _commitment = Mathf.MoveTowards(_commitment, target, rampRate * deltaTime);
        }

        private void UpdateSmoothedPoint(
            Vector3 targetPoint,
            ConvaiGazeProfile profile,
            float deltaTime,
            ref bool teleported,
            ref bool wasCut)
        {
            if (!_hasSmoothedPoint)
            {
                _smoothedPoint = targetPoint;
                _pointFilter.Reset(targetPoint);
                _hasSmoothedPoint = true;
                return;
            }

            // Camera cut / teleport: never drag gaze across the room — snap the point and
            // bump the generation so the eye stage re-acquires with a ballistic saccade.
            //
            // Detected on the RAW displacement, and before the conditioning filter sees it. A
            // discontinuity is exactly the input an adaptive filter reads as enormous speed, so
            // routing the test through the filter would both smear the jump and leave the cut-off
            // wide open for as long as the speed estimate takes to decay — the frame after a
            // camera cut is precisely when the conditioning must NOT be disarmed. Testing the raw
            // point keeps a teleport a single-frame snap and re-seeds the filter on the far side.
            if ((targetPoint - _smoothedPoint).sqrMagnitude >
                profile.TargetTeleportThreshold * profile.TargetTeleportThreshold)
            {
                _smoothedPoint = targetPoint;
                _pointFilter.Reset(targetPoint);
                _generationId++;
                teleported = true;
                wasCut = true;
                return;
            }

            // Conditioning. The angular dynamics belong to the eye and head stages; what belongs
            // here is that they are handed a point the target actually occupies rather than one
            // that twitches. See PointConditioning for why this is adaptive and not a fixed low
            // pass — the fixed one was the previous line here, and it passed a talking head's
            // bone bob through at 89% of its amplitude.
            _smoothedPoint = _pointFilter.Step(targetPoint, in PointConditioning, deltaTime);
        }

        private void ReleaseCurrent()
        {
            _hasCurrent = false;
            _currentKey = 0;
        }

        private float GetOrInitInterest(int key)
        {
            if (_interest.TryGetValue(key, out float interest)) return interest;
            _interest[key] = 1f;
            return 1f;
        }

        private void PruneStaleInterest(IReadOnlyList<GazeTargetCandidate> candidates)
        {
            if (_interest.Count == 0) return;

            _staleKeys.Clear();
            foreach (KeyValuePair<int, float> kvp in _interest)
                _staleKeys.Add(kvp.Key);

            for (int i = 0; i < candidates.Count; i++)
                _staleKeys.Remove(CandidateKey(candidates[i]));

            if (_hasCurrent)
                _staleKeys.Remove(_currentKey);

            for (int i = 0; i < _staleKeys.Count; i++)
                _interest.Remove(_staleKeys[i]);
        }

        /// <summary>
        ///     Whether another candidate exists in the incumbent's priority tier (or above).
        ///     Lower-tier candidates never justify a forced break: selection would re-pick
        ///     the incumbent's tier on the very next tick, so breaking against them only
        ///     produces a pointless re-acquisition twitch (generation bump → saccade and
        ///     blink) on a target that never actually changes.
        /// </summary>
        private static bool HasSameTierAlternative(
            IReadOnlyList<GazeTargetCandidate> candidates,
            int chosenKey,
            int chosenPriority)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                GazeTargetCandidate candidate = candidates[i];
                if (candidate.Relevance <= 0f) continue;
                if (candidate.Priority < chosenPriority) continue;
                if (CandidateKey(candidate) != chosenKey) return true;
            }
            return false;
        }

        private static int CandidateKey(in GazeTargetCandidate candidate)
        {
            if (candidate.Target != null) return ConvaiObjectId.Of(candidate.Target).GetHashCode();
            if (!string.IsNullOrEmpty(candidate.DebugName)) return candidate.DebugName.GetHashCode();

            Vector3 p = candidate.WorldPoint;
            int h = 17;
            h = h * 31 + Mathf.RoundToInt(p.x * 1000f);
            h = h * 31 + Mathf.RoundToInt(p.y * 1000f);
            h = h * 31 + Mathf.RoundToInt(p.z * 1000f);
            return h;
        }

        private static int ScriptedKey(int stackEntryId) => unchecked(0x5C000000 | stackEntryId);
    }
}
