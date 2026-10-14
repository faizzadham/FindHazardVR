using System;
using System.Collections.Generic;
using Convai.Domain.Logging;
using Convai.RestAPI.Internal;
using Newtonsoft.Json;

namespace Convai.Infrastructure.Networking
{
    internal interface IRoomDetailsStateTarget
    {
        public string Token { get; set; }
        public string RoomName { get; set; }
        public string RoomURL { get; set; }
        public string SessionID { get; set; }
        public string CharacterSessionID { get; set; }
        public string ResolvedSpeakerId { get; set; }
        public string RequestTraceId { get; set; }
        public string ResolvedEndUserId { get; set; }
        public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata { get; set; }
        public bool HasRoomDetails { get; set; }
    }

    internal readonly struct AppliedRoomDetailsState
    {
        // Held only for FormatReceivedLogMessage, which serializes it when called.
        private readonly RoomDetails _roomDetails;

        internal AppliedRoomDetailsState(RoomDetails roomDetails)
        {
            if (roomDetails == null) throw new ArgumentNullException(nameof(roomDetails));

            _roomDetails = roomDetails;
            Token = roomDetails.Token;
            RoomName = roomDetails.RoomName;
            RoomURL = roomDetails.RoomURL;
            SessionID = roomDetails.SessionId;
            CharacterSessionID = roomDetails.CharacterSessionId;
            ResolvedSpeakerId = roomDetails.SpeakerId;
            RequestTraceId = roomDetails.RequestTraceId;
            ResolvedEndUserId = roomDetails.EndUserId;
            ResolvedEndUserMetadata = roomDetails.EndUserMetadata;
        }

        public string Token { get; }
        public string RoomName { get; }
        public string RoomURL { get; }
        public string SessionID { get; }
        public string CharacterSessionID { get; }
        public string ResolvedSpeakerId { get; }
        public string RequestTraceId { get; }
        public string ResolvedEndUserId { get; }
        public IReadOnlyDictionary<string, object> ResolvedEndUserMetadata { get; }

        // Debug diagnostics, built on each call. Callers check the Debug filter first: the received
        // dump serializes the whole room details, which is wasted work when the line is filtered out.
        public string FormatReceivedLogMessage(string prefix = null) =>
            Prefix(prefix, $"Room details received: {JsonConvert.SerializeObject(_roomDetails)}");

        public string FormatSummaryLogMessage(string prefix = null) => Prefix(prefix, CreateSummaryLogMessage(
            Token,
            RoomName,
            RoomURL,
            SessionID,
            CharacterSessionID,
            ResolvedSpeakerId,
            RequestTraceId,
            ResolvedEndUserId));

        private static string CreateSummaryLogMessage(
            string token,
            string roomName,
            string roomUrl,
            string sessionId,
            string characterSessionId,
            string resolvedSpeakerId,
            string requestTraceId,
            string resolvedEndUserId)
        {
            string message =
                $"Token: {token}; Room Name: {roomName}; Room URL: {roomUrl}; Session ID: {sessionId}; Character Session ID: {characterSessionId}";
            if (!string.IsNullOrWhiteSpace(resolvedSpeakerId))
                message += $"; Resolved Speaker ID (diagnostics): {resolvedSpeakerId}";
            if (!string.IsNullOrWhiteSpace(requestTraceId))
                message += $"; Request Trace ID: {requestTraceId}";
            if (!string.IsNullOrWhiteSpace(resolvedEndUserId))
                message += $"; Resolved End User ID: {resolvedEndUserId}";

            return message;
        }

        private static string Prefix(string prefix, string message) => string.IsNullOrWhiteSpace(prefix)
            ? message
            : $"{prefix} {message}";
    }

    internal static class RoomDetailsStateApplier
    {
        internal static AppliedRoomDetailsState CreateState(RoomDetails roomDetails) => new(roomDetails);

        internal static AppliedRoomDetailsState Apply(
            IRoomDetailsStateTarget target,
            RoomDetails roomDetails,
            ILogger logger,
            bool markHasRoomDetails = false,
            string logPrefix = null)
        {
            AppliedRoomDetailsState state = CreateState(roomDetails);
            return Apply(target, state, logger, markHasRoomDetails, logPrefix);
        }

        internal static AppliedRoomDetailsState Apply(
            IRoomDetailsStateTarget target,
            in AppliedRoomDetailsState state,
            ILogger logger,
            bool markHasRoomDetails = false,
            string logPrefix = null)
        {
            if (target == null) throw new ArgumentNullException(nameof(target));
            if (logger == null) throw new ArgumentNullException(nameof(logger));

            bool logDiagnostics = logger.IsEnabled(LogLevel.Debug, LogCategory.Transport);
            if (logDiagnostics)
                logger.Debug(state.FormatReceivedLogMessage(logPrefix), LogCategory.Transport);

            target.Token = state.Token;
            target.RoomName = state.RoomName;
            target.RoomURL = state.RoomURL;
            target.SessionID = state.SessionID;
            target.CharacterSessionID = state.CharacterSessionID;
            target.ResolvedSpeakerId = state.ResolvedSpeakerId;
            target.RequestTraceId = state.RequestTraceId;
            target.ResolvedEndUserId = state.ResolvedEndUserId;
            target.ResolvedEndUserMetadata = state.ResolvedEndUserMetadata;
            target.HasRoomDetails = markHasRoomDetails;

            if (logDiagnostics)
                logger.Debug(state.FormatSummaryLogMessage(logPrefix), LogCategory.Transport);

            return state;
        }
    }
}
