using Convai.Domain.Embodiment.Semantics;

namespace Convai.Editor.UI
{
    /// <summary>
    ///     The one place the eight conversation states are named and explained for a user.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Gaze, Body Animation, Body Language and Emotion all show the same live state, and for
    ///         a while they did not agree on what to call it: the gaze personality table said
    ///         "Addressed" and "Winding down" where every live panel said "Attending" and
    ///         "Settling". Two names for one state reads as two states, and the user cannot tell
    ///         which row of the table the thing on screen belongs to.
    ///     </para>
    ///     <para>
    ///         The name is the state's own — the same word the API uses, so what the inspector says
    ///         and what <see cref="DialogueState" /> says are never two vocabularies to learn. The
    ///         explanation carries the meaning instead, right next to the name, which is the job the
    ///         friendlier synonyms were doing badly.
    ///     </para>
    /// </remarks>
    internal static class ConvaiDialogueStateLabels
    {
        /// <summary>The state's name, in the user's inspector and in their code alike.</summary>
        internal static string Name(DialogueState state) => state switch
        {
            DialogueState.Idle => "Idle",
            DialogueState.Attending => "Attending",
            DialogueState.Listening => "Listening",
            DialogueState.Thinking => "Thinking",
            DialogueState.Speaking => "Speaking",
            DialogueState.Reacting => "Reacting",
            DialogueState.Interrupted => "Interrupted",
            DialogueState.Settling => "Settling",
            _ => state.ToString()
        };

        /// <summary>One line saying what the character is actually doing in this state.</summary>
        internal static string Explain(DialogueState state) => state switch
        {
            DialogueState.Idle => "Nobody is talking to it. Also the fallback for any state not listed here.",
            DialogueState.Attending => "It is engaged with someone — addressed, or between beats of a live conversation.",
            DialogueState.Listening => "The player is speaking to it.",
            DialogueState.Thinking => "It is working out what to say.",
            DialogueState.Speaking => "It is talking.",
            DialogueState.Reacting => "Something just happened that it is responding to.",
            DialogueState.Interrupted => "It was cut off mid-sentence.",
            DialogueState.Settling => "Its turn is over and the conversation is winding down.",
            _ => string.Empty
        };
    }
}
