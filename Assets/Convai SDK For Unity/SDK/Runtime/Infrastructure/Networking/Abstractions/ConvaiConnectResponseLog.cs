using System;
using Convai.RestAPI.Internal;

namespace Convai.Infrastructure.Networking
{
    /// <summary>One accepted room connect response and when it arrived.</summary>
    internal sealed class ConvaiConnectResponseSnapshot
    {
        internal ConvaiConnectResponseSnapshot(RoomDetails details, DateTime receivedAtUtc)
        {
            Details = details;
            ReceivedAtUtc = receivedAtUtc;
        }

        internal RoomDetails Details { get; }
        internal DateTime ReceivedAtUtc { get; }
    }

    /// <summary>
    ///     The last room connect response this process accepted, kept so the editor can hand the
    ///     whole thing to a backend engineer instead of the three IDs the SDK copies out of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Static on purpose: it outlives the room controller, so the response is still there
    ///         after Play mode stops, which is when someone sits down to file the bug. A domain
    ///         reload or the next connect replaces it.
    ///     </para>
    ///     <para>
    ///         Cleared when a connect starts, so a reconnect that fails cannot leave the previous
    ///         room's response looking like the current one. The native controller accepts the
    ///         response on a thread-pool thread and the editor reads it on the main thread; one
    ///         immutable snapshot behind a volatile reference is all that needs.
    ///     </para>
    /// </remarks>
    internal static class ConvaiConnectResponseLog
    {
        private static volatile ConvaiConnectResponseSnapshot _latest;

        /// <summary>The last accepted response, or null when none has arrived since the last connect started.</summary>
        internal static ConvaiConnectResponseSnapshot Latest => _latest;

        internal static void Record(RoomDetails details)
        {
            if (details != null)
                _latest = new ConvaiConnectResponseSnapshot(details, DateTime.UtcNow);
        }

        internal static void Clear() => _latest = null;
    }
}
