using UnityEngine;

namespace Convai.Modules.Gaze.Core.Conversation
{
    /// <summary>
    ///     The five durations that decide who the room thinks is holding the conversational
    ///     floor. Passed in per <see cref="ConversationRoomModel.Refresh" /> rather than stored,
    ///     because the model is shared by every character and none of them owns the tuning.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The first three are the floor rules themselves, and they are the same numbers
    ///         <c>SpeakerAttentionDirector</c> has been running on since the floor model was
    ///         introduced — this is a move, not a retune. The last two describe the room's
    ///         bookkeeping rather than its conversation: how long a participant that has stopped
    ///         reporting stays in it, and how long a typed message counts as the player talking.
    ///     </para>
    ///     <para>
    ///         There is no parameterless constructor (C# 9 does not allow one on a struct), so a
    ///         <c>default</c> value is five zeros and would claim the floor on the first frame.
    ///         Use <see cref="Default" />, or name the one or two fields a test wants to move.
    ///     </para>
    /// </remarks>
    internal readonly struct ConversationRoomTuning
    {
        /// <summary>Default for <see cref="ClaimSeconds" />.</summary>
        internal const float DefaultClaimSeconds = 0.12f;

        /// <summary>Default for <see cref="InterruptionSeconds" />.</summary>
        internal const float DefaultInterruptionSeconds = 0.6f;

        /// <summary>Default for <see cref="HoldSeconds" />.</summary>
        internal const float DefaultHoldSeconds = 2.5f;

        /// <summary>Default for <see cref="ExpireSeconds" />.</summary>
        internal const float DefaultExpireSeconds = 1f;

        /// <summary>Default for <see cref="TypedFloorSeconds" />.</summary>
        internal const float DefaultTypedFloorSeconds = 1.4f;

        /// <summary>
        ///     How long a claimant must speak to take an <i>empty</i> floor. Long enough to reject
        ///     a single noisy frame, short enough that a real speaker is never kept waiting.
        /// </summary>
        public readonly float ClaimSeconds;

        /// <summary>
        ///     How long somebody else has to keep speaking to take the floor from whoever holds
        ///     it. Below this it is an interjection, and interjections do not turn a room's heads.
        /// </summary>
        public readonly float InterruptionSeconds;

        /// <summary>
        ///     How long the floor stays with a speaker who has stopped. This is what carries the
        ///     room across the pauses in and around a turn.
        /// </summary>
        public readonly float HoldSeconds;

        /// <summary>
        ///     How long a participant that has stopped reporting itself stays in the room. A
        ///     character reports once per cognition tick, so this only ever fires for one that
        ///     was disabled, destroyed, or unloaded with its scene.
        /// </summary>
        public readonly float ExpireSeconds;

        /// <summary>
        ///     How long a typed message counts as the player holding the floor. Typing has no
        ///     duration the way talking does, but it is just as much a turn.
        /// </summary>
        public readonly float TypedFloorSeconds;

        /// <summary>Creates a tuning, taking the shipped default for anything not named.</summary>
        public ConversationRoomTuning(
            float claimSeconds = DefaultClaimSeconds,
            float interruptionSeconds = DefaultInterruptionSeconds,
            float holdSeconds = DefaultHoldSeconds,
            float expireSeconds = DefaultExpireSeconds,
            float typedFloorSeconds = DefaultTypedFloorSeconds)
        {
            ClaimSeconds = Mathf.Max(0f, claimSeconds);
            InterruptionSeconds = Mathf.Max(0f, interruptionSeconds);
            HoldSeconds = Mathf.Max(0f, holdSeconds);
            ExpireSeconds = Mathf.Max(0f, expireSeconds);
            TypedFloorSeconds = Mathf.Max(0f, typedFloorSeconds);
        }

        /// <summary>The shipped values: 0.12 / 0.6 / 2.5 / 1.0 / 1.4 seconds.</summary>
        public static ConversationRoomTuning Default { get; } = new(DefaultClaimSeconds);
    }
}
