using Convai.Runtime.Core;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     What the manager should do about a character whose GameObject was just enabled or
    ///     disabled.
    /// </summary>
    internal enum LateCharacterAction
    {
        /// <summary>Do nothing — startup composition or shutdown owns this moment.</summary>
        None,

        /// <summary>Refresh ownership so the room observes who can be in the conversation.</summary>
        Refresh,

        /// <summary>Adopt the character as runtime-owned, then refresh ownership.</summary>
        AdoptAndRefresh
    }

    /// <summary>
    ///     Decides whether a character appearing, changing activity, or leaving the scene is a live
    ///     roster event the manager must act on, or a moment the startup/shutdown sequence already owns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         At scene load every character's <c>OnEnable</c> runs while the runtime is still
    ///         <see cref="RuntimeState.Created" />, and the manager's own <c>Start</c>-time refresh
    ///         is about to observe them all at once. Acting then would stack one ownership refresh
    ///         per character on top of the startup sequence, so <c>Created</c> answers
    ///         <see cref="LateCharacterAction.None" />. The same applies once the runtime is
    ///         tearing down — there is no room left to tell.
    ///     </para>
    ///     <para>
    ///         Adoption exists for scene installers and explicit ownership lists: both are authored
    ///         against the scene as it was, so a character that appears afterwards can never be on
    ///         them. A never-injected character that enables while the runtime is live is adopted as
    ///         runtime-owned — the same additive rule the late-module path applies via
    ///         <c>TrackRuntimeModuleOwner</c>. An already-injected character only needs the refresh,
    ///         because it is owned already and merely changed whether it can be in the room.
    ///     </para>
    ///     <para>
    ///         Kept as a plain decision so it can be tested without a scene; the caller owns every
    ///         Unity-side fact and passes it in as a plain answer, the same way
    ///         <see cref="LiveRosterPlanner" /> is fed.
    ///     </para>
    /// </remarks>
    internal static class LateCharacterPlanner
    {
        internal static LateCharacterAction Plan(
            bool hasCharacter,
            bool hasRuntime,
            RuntimeState runtimeState,
            bool characterInjected,
            bool characterBecameEnabled,
            bool characterWasDestroyed = false)
        {
            // A Unity object already compares equal to null while its OnDestroy callback is running.
            // The explicit destruction fact remains authoritative through that fake-null window.
            if ((!hasCharacter && !characterWasDestroyed) || !hasRuntime)
                return LateCharacterAction.None;

            if (runtimeState is RuntimeState.Created or RuntimeState.Stopping
                or RuntimeState.Stopped or RuntimeState.Disposed)
                return LateCharacterAction.None;

            if (characterWasDestroyed)
                return LateCharacterAction.Refresh;

            return characterBecameEnabled && !characterInjected
                ? LateCharacterAction.AdoptAndRefresh
                : LateCharacterAction.Refresh;
        }
    }
}
