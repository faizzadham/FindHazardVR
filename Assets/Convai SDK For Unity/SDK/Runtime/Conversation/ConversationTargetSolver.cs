using System.Collections.Generic;
using UnityEngine;

namespace Convai.Runtime.Conversation
{
    /// <summary>One character the player could be addressing, reduced to what the choice needs.</summary>
    internal readonly struct ConversationTargetCandidate
    {
        /// <param name="id">Caller-assigned identity, compared but never interpreted.</param>
        /// <param name="position">Where the character is, in world space.</param>
        /// <param name="isCurrentTarget">Whether this candidate currently holds the conversation.</param>
        public ConversationTargetCandidate(long id, Vector3 position, bool isCurrentTarget)
        {
            Id = id;
            Position = position;
            IsCurrentTarget = isCurrentTarget;
        }

        /// <summary>Caller-assigned identity. The solver only compares it; it never interprets it.</summary>
        public long Id { get; }

        /// <summary>Where the character is, in world space — ideally head height, not the feet.</summary>
        public Vector3 Position { get; }

        /// <summary>Whether this candidate currently holds the conversation.</summary>
        public bool IsCurrentTarget { get; }
    }

    /// <summary>What the solver is looking from, and under what rules.</summary>
    internal readonly struct ConversationTargetQuery
    {
        /// <param name="viewPosition">Where the player is looking from, in world space.</param>
        /// <param name="viewForward">The direction they are looking, normalised or not.</param>
        /// <param name="options">The distance and angle the choice is bounded by.</param>
        public ConversationTargetQuery(
            Vector3 viewPosition,
            Vector3 viewForward,
            ConversationTargetingOptions options)
        {
            ViewPosition = viewPosition;
            ViewForward = viewForward;
            Options = options;
        }

        /// <summary>Where the player is looking from, in world space.</summary>
        /// <remarks>
        ///     Resolved from a real transform by the caller and never invented: measuring from a
        ///     stand-in origin answers with whichever character happens to sit near it, which reads
        ///     as the conversation moving for no reason rather than as there being nothing to
        ///     measure from.
        /// </remarks>
        public Vector3 ViewPosition { get; }

        /// <summary>The direction the player is looking. Need not be normalised.</summary>
        public Vector3 ViewForward { get; }

        /// <summary>The distance and angle bounds the choice is made under. Never <c>null</c>.</summary>
        public ConversationTargetingOptions Options { get; }
    }

    /// <summary>
    ///     Chooses which character the player is addressing. Plain C# with no Unity object
    ///     dependencies, so the rule can be tested without a scene.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why angle and not a raycast.</b> A raycast needs a collider on the character and a
    ///         layer mask that includes it. Neither is guaranteed on a character somebody just dropped
    ///         into a scene, and when either is missing a raycast finds nothing and reports nothing —
    ///         a silent failure, which is the one kind a beginner cannot diagnose. Scoring by angle
    ///         needs no physics at all, so the rule holds on any character with a transform.
    ///     </para>
    ///     <para>
    ///         <b>The result is deliberately sticky.</b> When nobody qualifies, the current target is
    ///         kept rather than cleared. A cleared target means the player speaks and nothing answers,
    ///         which reads as a broken game rather than as "you are addressing nobody". Somebody is
    ///         always listening until somebody else is clearly addressed instead.
    ///     </para>
    /// </remarks>
    internal static class ConversationTargetSolver
    {
        /// <summary>Returned when no candidate qualifies and none currently holds the conversation.</summary>
        public const long NoTarget = long.MinValue;

        /// <summary>
        ///     Returns the id of the character the player is addressing, or <see cref="NoTarget" />.
        /// </summary>
        /// <remarks>
        ///     The score is the angle between the view direction and the character, in degrees, so a
        ///     lower score is better and distance only settles ties. The candidate holding the
        ///     conversation is scored <see cref="ConversationTargetingOptions.SwitchMargin" /> degrees
        ///     better than it measures, which is what stops two characters standing near each other
        ///     from trading the conversation back and forth on tiny camera movements.
        /// </remarks>
        public static long Solve(
            in ConversationTargetQuery query,
            IReadOnlyList<ConversationTargetCandidate> candidates)
        {
            ConversationTargetingOptions options = query.Options;
            if (candidates == null || candidates.Count == 0 || options == null)
                return NoTarget;

            long currentTargetId = NoTarget;
            for (int i = 0; i < candidates.Count; i++)
                if (candidates[i].IsCurrentTarget)
                {
                    currentTargetId = candidates[i].Id;
                    break;
                }

            bool byLookDirection = options.Mode == ConversationTargetingMode.LookAt;
            Vector3 forward = query.ViewForward.sqrMagnitude > Mathf.Epsilon
                ? query.ViewForward.normalized
                : Vector3.forward;

            long bestId = NoTarget;
            float bestScore = float.MaxValue;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                ConversationTargetCandidate candidate = candidates[i];
                Vector3 toCandidate = candidate.Position - query.ViewPosition;
                float distance = toCandidate.magnitude;
                if (distance > options.MaxDistance)
                    continue;

                float angle = 0f;
                if (byLookDirection)
                {
                    // A candidate at the exact view position has no direction to measure. Treating it
                    // as dead ahead is the only answer that does not depend on floating-point noise.
                    angle = distance <= Mathf.Epsilon
                        ? 0f
                        : Vector3.Angle(forward, toCandidate / distance);
                    if (angle > options.MaxAngle)
                        continue;
                }

                float score = byLookDirection ? angle : distance;
                if (candidate.IsCurrentTarget)
                    score -= byLookDirection ? options.SwitchMargin : 0f;

                if (score > bestScore || (Mathf.Approximately(score, bestScore) && distance >= bestDistance))
                    continue;

                bestId = candidate.Id;
                bestScore = score;
                bestDistance = distance;
            }

            // Nobody qualified. Keep whoever holds the conversation rather than dropping it.
            return bestId == NoTarget ? currentTargetId : bestId;
        }
    }
}
