using System.Collections.Generic;
using Convai.RestAPI;
using Convai.RestAPI.Services;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Room;
using Convai.Shared.Actions;

namespace Convai.Infrastructure.Networking.Models
{
    /// <summary>
    ///     Options for joining or creating a room during connection.
    ///     Used to pass join hints to the connection flow.
    /// </summary>
    public sealed class RoomJoinOptions
    {
        /// <summary>
        ///     Creates options for joining an existing room.
        /// </summary>
        public RoomJoinOptions(
            string roomName,
            string characterSessionId = null,
            bool spawnAgent = true,
            int? maxNumParticipants = null,
            string characterId = null)
        {
            RoomName = roomName;
            CharacterSessionId = characterSessionId;
            SpawnAgent = spawnAgent;
            MaxNumParticipants = maxNumParticipants;
            CharacterId = characterId;
        }

        /// <summary>
        ///     The room name to join. If null or empty, a new room will be created.
        /// </summary>
        public string RoomName { get; }

        /// <summary>
        ///     Whether to spawn the agent in the room. Default: true.
        /// </summary>
        public bool SpawnAgent { get; }

        /// <summary>
        ///     Maximum number of participants allowed in the room.
        ///     Null means use server default.
        /// </summary>
        public int? MaxNumParticipants { get; }

        /// <summary>
        ///     The character session ID to resume. If null, a new session will be started.
        /// </summary>
        public string CharacterSessionId { get; }

        /// <summary>
        ///     The character ID for the connection.
        /// </summary>
        public string CharacterId { get; }

        /// <summary>
        ///     Returns true when this request joins an existing legacy or multi-character room.
        /// </summary>
        public bool IsJoinRequest => !string.IsNullOrEmpty(RoomName) || JoinExistingMultiCharacterRoom;

        internal ResolvedTurnTakingOptions ResolvedTurnTakingOptions { get; set; }
        internal UserVadSettings ResolvedUserVadSettings { get; set; }
        internal RoomEmotionConfig ResolvedEmotionConfig { get; set; }
        internal string ResolvedEndUserId { get; set; }
        // Frozen at connect invocation; CharacterId remains the base identity everywhere else.
        internal string ResolvedCharacterReference { get; set; }
        internal IReadOnlyDictionary<string, object> ResolvedEndUserMetadata { get; set; }
        internal string ResolvedSharedSessionKey { get; set; }
        internal string ResolvedRoomSessionId { get; set; }
        internal bool JoinExistingMultiCharacterRoom { get; set; }
        internal IReadOnlyList<RoomConnectionCharacterRequest> ResolvedCharacterRoster { get; set; }
        internal int? ResolvedMaxNumParticipants { get; set; }
        internal ConvaiActionConfig ResolvedActionConfig { get; set; }
        internal RoomVisionInputConfig ResolvedVisionInputConfig { get; set; }
        internal RoomRespondModesConfig ResolvedRespondModes { get; set; }

        /// <summary>
        ///     Creates options for creating a new room.
        /// </summary>
        public static RoomJoinOptions CreateNew(string characterSessionId = null, int? maxNumParticipants = null) =>
            new(null, characterSessionId, true, maxNumParticipants);

        internal static RoomJoinOptions CreateMultiCharacterJoin(string roomSessionId, string sharedSessionKey)
        {
            var options = new RoomJoinOptions(null, null, false)
            {
                JoinExistingMultiCharacterRoom = true,
                ResolvedRoomSessionId = string.IsNullOrWhiteSpace(roomSessionId) ? null : roomSessionId.Trim(),
                ResolvedSharedSessionKey = string.IsNullOrWhiteSpace(sharedSessionKey) ? null : sharedSessionKey.Trim()
            };
            return options;
        }

        internal bool IsMultiCharacterCreate => ResolvedCharacterRoster != null;
        internal bool IsMultiCharacterJoin => JoinExistingMultiCharacterRoom;

        /// <summary>
        ///     Creates options from a ConnectionContext and ReconnectPolicy.
        /// </summary>
        public static RoomJoinOptions FromContext(ConnectionContext context, ReconnectPolicy policy)
        {
            if (context == null || !context.HasValidRoom || !context.IsRoomValidForRejoin(policy.RoomRejoinTtlSeconds))
            {
                string sessionId = policy.ResumePolicy != ResumePolicy.AlwaysFresh && context?.CanResumeSession == true
                    ? context.CharacterSessionId
                    : null;
                return new RoomJoinOptions(null, sessionId)
                {
                    ResolvedCharacterReference = context?.CharacterReference
                };
            }

            string characterSessionId = policy.ResumePolicy != ResumePolicy.AlwaysFresh && context.CanResumeSession
                ? context.CharacterSessionId
                : null;

            return new RoomJoinOptions(
                context.RoomName,
                characterSessionId,
                policy.SpawnAgentOnRejoin)
            {
                ResolvedCharacterReference = context.CharacterReference
            };
        }

        /// <inheritdoc />
        public override string ToString()
        {
            if (IsJoinRequest)
                return $"[RoomJoinOptions Join room={RoomName}, spawnAgent={SpawnAgent}]";
            return "[RoomJoinOptions Create new room]";
        }
    }
}
