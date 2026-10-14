using System.Collections.Generic;
using Newtonsoft.Json;

namespace Convai.Infrastructure.Protocol.Messages
{
    /// <summary>Reliably adds or removes character memberships in the current LiveKit room.</summary>
    public sealed class RTVICharacterRosterUpdate : RTVISendMessageBase
    {
        public RTVICharacterRosterUpdate(
            string commandId,
            string roomSessionId,
            int expectedRosterEpoch,
            IReadOnlyList<CharacterRosterAddition> add,
            IReadOnlyList<string> removeMembershipIds,
            string replacementTargetMembershipId)
        {
            Type = "character-roster-update";
            Id = commandId;
            Data = new CharacterRosterUpdateData
            {
                CommandId = commandId,
                RoomSessionId = roomSessionId,
                ExpectedRosterEpoch = expectedRosterEpoch,
                Add = add ?? System.Array.Empty<CharacterRosterAddition>(),
                RemoveMembershipIds = removeMembershipIds ?? System.Array.Empty<string>(),
                ReplacementTargetMembershipId = replacementTargetMembershipId
            };
        }

        public sealed class CharacterRosterAddition
        {
            public CharacterRosterAddition(string characterId, string characterSessionId = null)
            {
                CharacterId = characterId;
                CharacterSessionId = characterSessionId;
            }

            [JsonProperty("character_id")]
            public string CharacterId { get; }

            [JsonProperty("character_session_id")]
            public string CharacterSessionId { get; }
        }

        private sealed class CharacterRosterUpdateData
        {
            [JsonProperty("command_id")] public string CommandId { get; set; }
            [JsonProperty("room_session_id")] public string RoomSessionId { get; set; }
            [JsonProperty("expected_roster_epoch")] public int ExpectedRosterEpoch { get; set; }
            [JsonProperty("add")] public IReadOnlyList<CharacterRosterAddition> Add { get; set; }
            [JsonProperty("remove_membership_ids")] public IReadOnlyList<string> RemoveMembershipIds { get; set; }
            [JsonProperty("replacement_target_membership_id")]
            public string ReplacementTargetMembershipId { get; set; }
        }
    }
}
