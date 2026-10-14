using Convai.Domain.DomainEvents.Narrative;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public void ServerMessage_BehaviorTree_PublishesNarrativeSection()
        {
            RtviTestContext context = CreateContext();
            context.Registry.RegisterCharacter(new TestCharacterAgent("char-1", "Camila"));
            context.Registry.SetParticipantId("char-1", "participant-1");
            NarrativeSectionChanged captured = default;
            context.EventHub.Subscribe<NarrativeSectionChanged>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "behavior-tree-response",
                new JObject
                {
                    ["narrative_section_id"] = "section-1",
                    ["bt_code"] = "code",
                    ["bt_constants"] = "constants"
                },
                "participant-1"));

            Assert.AreEqual("section-1", captured.SectionId);
            Assert.AreEqual("char-1", captured.CharacterId);
            Assert.AreEqual("participant-1", captured.ParticipantId);
        }

        [Test]
        public void ServerMessage_UsageLimit_PublishesLimitAndFatalSessionError()
        {
            RtviTestContext context = CreateContext();
            UsageLimitReached limit = default;
            SessionError error = default;
            context.EventHub.Subscribe<UsageLimitReached>(evt => limit = evt);
            context.EventHub.Subscribe<SessionError>(evt => error = evt);

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "usage-limit-reached",
                new JObject { ["quota_type"] = "daily", ["message"] = "limit" }));

            Assert.AreEqual("daily", limit.QuotaType);
            Assert.AreEqual("limit", limit.Message);
            Assert.AreEqual(SessionErrorCodes.ServerUsageLimitReached, error.ErrorCode);
            Assert.IsFalse(error.IsRecoverable);
        }

        [Test]
        public void ServerMessage_Moderation_PublishesResult()
        {
            RtviTestContext context = CreateContext();
            ModerationResponseReceived captured = default;
            context.EventHub.Subscribe<ModerationResponseReceived>(evt => captured = evt);

            context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "moderation-response",
                new JObject
                {
                    ["result"] = true,
                    ["user_input"] = "input",
                    ["reason"] = "reason"
                }));

            Assert.IsTrue(captured.WasFlagged);
            Assert.AreEqual("input", captured.UserInput);
            Assert.AreEqual("reason", captured.Reason);
        }

        [Test]
        public void ServerMessage_UnknownType_IsContainedAndLogged()
        {
            RtviTestContext context = CreateContext();

            Assert.DoesNotThrow(() => context.Gateway.ProcessIncoming(CreateServerMessagePacket(
                "future-message",
                new JObject())));
            Assert.IsTrue(context.Logger.Contains("Unhandled server-message type: future-message"));
        }
    }
}
