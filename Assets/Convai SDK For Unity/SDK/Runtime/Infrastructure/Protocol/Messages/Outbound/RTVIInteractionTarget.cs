using Newtonsoft.Json;

namespace Convai.Infrastructure.Protocol.Messages
{
    /// <summary>Reliably changes which character receives subsequent player interaction.</summary>
    public sealed class RTVIInteractionTarget : RTVISendMessageBase
    {
        public RTVIInteractionTarget(
            string commandId,
            string roomSessionId,
            string targetMembershipId,
            int expectedRouteEpoch)
        {
            Type = "interaction-target";
            Id = commandId;
            Data = new InteractionTargetData
            {
                RoomSessionId = roomSessionId,
                TargetMembershipId = targetMembershipId,
                ExpectedRouteEpoch = expectedRouteEpoch
            };
        }

        private sealed class InteractionTargetData
        {
            [JsonProperty("room_session_id")] public string RoomSessionId { get; set; }
            [JsonProperty("target_membership_id")] public string TargetMembershipId { get; set; }
            [JsonProperty("expected_route_epoch")] public int ExpectedRouteEpoch { get; set; }
        }
    }
}
