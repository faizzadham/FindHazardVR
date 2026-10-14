using Convai.Domain.Embodiment.Semantics;
using UnityEngine;

namespace Convai.Modules.Gaze.Providers
{
    /// <summary>
    ///     One gaze target candidate offered by a provider for the current frame.
    /// </summary>
    public readonly struct GazeTargetCandidate
    {
        /// <summary>Source classification (Player, WorldObject, …).</summary>
        public GazeTargetKind Kind { get; }

        /// <summary>
        ///     Static priority tier. Higher tiers always win; relevance and interest only
        ///     compete within a tier.
        /// </summary>
        public int Priority { get; }

        /// <summary>Normalized relevance in <c>[0, 1]</c>; <c>0</c> removes the candidate.</summary>
        public float Relevance { get; }

        /// <summary>Optional transform behind the candidate (used for identity/follow).</summary>
        public Transform Target { get; }

        /// <summary>World-space point to gaze at this frame.</summary>
        public Vector3 WorldPoint { get; }

        /// <summary>Stable display name used in diagnostics.</summary>
        public string DebugName { get; }

        /// <summary>
        ///     Whether there is a face at <see cref="WorldPoint" /> worth looking around.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Looking at a person is not looking at a point: the gaze moves between their eyes
        ///         and their mouth rather than freezing on one spot. That is right for a face and
        ///         wrong for anything else — aimed at a camera, a doorway or a prop it simply lands
        ///         a degree or two off whatever the character is supposed to be looking at, which
        ///         reads as the character not looking at it.
        ///     </para>
        ///     <para>
        ///         Defaults to <c>false</c>, so a provider that does not answer gets exact aim.
        ///         Say <c>true</c> only when the point really is somebody's face.
        ///     </para>
        /// </remarks>
        public bool HasFace { get; }

        public GazeTargetCandidate(
            GazeTargetKind kind,
            int priority,
            float relevance,
            Transform target,
            Vector3 worldPoint,
            string debugName,
            bool hasFace = false)
        {
            Kind = kind;
            Priority = priority;
            Relevance = relevance < 0f ? 0f : relevance > 1f ? 1f : relevance;
            Target = target;
            WorldPoint = worldPoint;
            DebugName = debugName;
            HasFace = hasFace;
        }
    }

    /// <summary>
    ///     Supplies gaze target candidates to the <c>ConvaiGazeController</c>. Providers on
    ///     the character hierarchy are discovered automatically; additional providers can be
    ///     registered at runtime through the controller.
    /// </summary>
    /// <remarks>
    ///     Implementations must be cheap — <see cref="TryGetCandidate" /> runs every
    ///     cognition tick. Returning <c>false</c> (or relevance <c>0</c>) simply removes the
    ///     candidate for that frame; the arbiter handles acquisition/release smoothing.
    /// </remarks>
    public interface IGazeTargetProvider
    {
        /// <summary>
        ///     Produces this provider's candidate for the current frame.
        ///     Return <c>false</c> when no candidate is available.
        /// </summary>
        /// <param name="characterRoot">The character's root transform (distance math).</param>
        /// <param name="candidate">The produced candidate when the method returns <c>true</c>.</param>
        bool TryGetCandidate(Transform characterRoot, out GazeTargetCandidate candidate);
    }
}
