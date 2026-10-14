using System;
using System.Collections.Generic;

namespace Convai.Runtime.Room
{
    /// <summary>
    ///     Keeps local audio capture muted while a conversation route is changing without losing
    ///     the mute state the player asked for. It covers hands-free, prewarmed PTT, and direct room
    ///     routing APIs.
    /// </summary>
    /// <remarks>
    ///     Routing commands may overlap, so audio is restored only after every unresolved
    ///     generation has released. User mute requests made while routing is suppressed are
    ///     retained as intent; an unmute request cannot reopen the microphone mid-route.
    /// </remarks>
    internal sealed class ConversationRoutingMicrophoneGate
    {
        private int _generation;
        private readonly HashSet<int> _suppressingGenerations = new();
        private bool _isRecoveryMuted;
        private bool _restoreUserMuteAfterConnectionBoundary;
        private bool _userMuted;

        internal bool IsSuppressing => _suppressingGenerations.Count > 0;

        internal bool IsRecoveryMuted => _isRecoveryMuted;

        internal bool IsUserMuted => _userMuted;

        /// <summary>
        ///     Returns the player's saved preference rather than a temporary effective mute while
        ///     routing suppression or recovery owns the transport.
        /// </summary>
        internal bool ResolveUserMuted(bool effectiveMuted) =>
            IsSuppressing || _isRecoveryMuted || _restoreUserMuteAfterConnectionBoundary
                ? _userMuted
                : effectiveMuted;

        internal int Begin(
            bool shouldSuppress,
            bool isCurrentlyMuted,
            Action<bool> applyEffectiveMute)
        {
            int generation = ++_generation;

            if (!shouldSuppress)
                return generation;

            // The first overlapping command captures the state the player actually had. Later
            // commands see our temporary mute and must not mistake it for a user preference.
            if (!IsSuppressing)
            {
                // Recovery deliberately leaves the transport muted while disconnect starts. Do
                // not mistake that fail-closed mute for a preference if stale code attempts one
                // more route before the connection boundary finishes.
                if (!_isRecoveryMuted && !_restoreUserMuteAfterConnectionBoundary)
                    _userMuted = isCurrentlyMuted;
                _restoreUserMuteAfterConnectionBoundary = false;
            }

            _suppressingGenerations.Add(generation);
            try
            {
                applyEffectiveMute?.Invoke(true);
            }
            catch
            {
                // Beginning a route is transactional. If transport/event application throws before
                // the caller receives its lease, no later finally can release this generation.
                _suppressingGenerations.Remove(generation);
                try
                {
                    applyEffectiveMute?.Invoke(isCurrentlyMuted);
                }
                catch
                {
                    // Preserve the original failure; routing state is already rolled back.
                }
                throw;
            }
            return generation;
        }

        internal void SetUserMuted(bool muted, Action<bool> applyEffectiveMute)
        {
            _userMuted = muted;
            applyEffectiveMute?.Invoke(IsSuppressing || _isRecoveryMuted || muted);
        }

        internal void Complete(int generation, Action<bool> applyEffectiveMute)
        {
            if (!_suppressingGenerations.Remove(generation)) return;
            if (IsSuppressing) return;

            applyEffectiveMute?.Invoke(_isRecoveryMuted || _userMuted);
        }

        /// <summary>
        ///     Releases a routing generation after an ambiguous room has been retired, but leaves
        ///     the effective microphone muted while transport teardown runs.
        /// </summary>
        internal void AbortForConnectionRecovery(Action<bool> applyEffectiveMute)
        {
            // Room recovery retires every target command in this connection, including a newer
            // overlapping generation. Invalidate them all before the lease that discovered the
            // ambiguity releases; a generation-specific completion could otherwise leave the
            // newest route able to restore audio during disconnect.
            _generation++;
            _suppressingGenerations.Clear();
            _isRecoveryMuted = true;
            _restoreUserMuteAfterConnectionBoundary = true;
            applyEffectiveMute?.Invoke(true);
        }

        /// <summary>
        ///     Reapplies the player's saved preference when microphone startup is safe in the next
        ///     connection. Until this point teardown's effective mute remains in place.
        /// </summary>
        internal void RestoreUserMuteAfterConnectionBoundary(Action<bool> applyEffectiveMute)
        {
            if (!_restoreUserMuteAfterConnectionBoundary) return;

            _restoreUserMuteAfterConnectionBoundary = false;
            applyEffectiveMute?.Invoke(_userMuted);
        }

        /// <summary>
        ///     Consumes connection-boundary restoration when startup intentionally owns the
        ///     effective mute (for example prewarmed push-to-talk). The first user toggle should
        ///     then be based on that effective policy mute, not the previous connection's intent.
        /// </summary>
        internal void AcceptPolicyMuteAfterConnectionBoundary()
        {
            _restoreUserMuteAfterConnectionBoundary = false;
        }

        /// <summary>
        ///     Clears connection-only suppression without changing the effective transport state.
        ///     The player's saved mute preference is retained for the next room.
        /// </summary>
        internal void ResetForConnectionBoundary()
        {
            _generation++;
            _suppressingGenerations.Clear();
            _isRecoveryMuted = false;
            _restoreUserMuteAfterConnectionBoundary = true;
        }
    }
}
