using System;
using System.Collections.Generic;
using System.Text;
using Convai.Infrastructure.Protocol;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public sealed class ProtocolGatewayTests
    {
        private const string PrivateFixture = "SYNTHETIC_CONTENT_MUST_STAY_PRIVATE";

        [Test]
        public void DebugPredicate_IsRechecked_AndDoesNotChangeDispatch()
        {
            bool enabled = false;
            var debug = new List<string>();
            var errors = new List<string>();
            var gateway = new ProtocolGateway(null, debug.Add, errors.Add, () => enabled);
            ProtocolPacket packet = Packet("{\"type\":\"user-transcription\",\"payload\":{\"text\":\"" + PrivateFixture + "\"}}");
            int handled = 0;
            gateway.RegisterHandler("user-transcription", message =>
            {
                handled++;
                Assert.That(message.Envelope.Payload.Value<string>("text"), Is.EqualTo(PrivateFixture));
            });

            gateway.ProcessIncoming(packet);
            Assert.That(debug, Is.Empty);
            enabled = true;
            gateway.ProcessIncoming(packet);
            Assert.That(debug, Has.Count.EqualTo(1));
            Assert.That(debug[0], Does.Contain("kind=transcription").And.Contain("payloadKind=Object")
                .And.Contain($"bytes={packet.Payload.Length}").And.Contain("reliable=True"));
            Assert.That(debug[0], Does.Not.Contain(PrivateFixture));
            enabled = false;
            gateway.ProcessIncoming(packet);
            Assert.That(debug, Has.Count.EqualTo(1));
            Assert.That(handled, Is.EqualTo(3));
            Assert.That(errors, Is.Empty);
        }

        [Test]
        public void DisabledDebug_EmptyAndUnhandledPackets_DoNotCallLogger()
        {
            var gateway = new ProtocolGateway(null, _ => Assert.Fail("Debug delegate must not run"),
                _ => Assert.Fail("No parse error expected"), () => false);
            gateway.ProcessIncoming(new ProtocolPacket(ReadOnlyMemory<byte>.Empty, "", "", true));
            gateway.ProcessIncoming(Packet("{\"type\":\"unregistered\",\"payload\":null}"));
        }

        [Test]
        public void LegacyDelegateConstructor_StillLogsMetadata_AndDispatchesTypedDataCaseInsensitively()
        {
            var debug = new List<string>();
            var gateway = new ProtocolGateway(null, debug.Add, _ => Assert.Fail("No error expected"));
            int value = 0;
            gateway.RegisterHandler<FixturePayload>("user-transcription", message => value = message.Payload.Value);
            gateway.ProcessIncoming(Packet("{\"type\":\"USER-TRANSCRIPTION\",\"data\":{\"Value\":17}}"));
            Assert.That(value, Is.EqualTo(17));
            Assert.That(debug, Has.Count.EqualTo(1));
            Assert.That(debug[0], Does.Contain("payloadKind=Object").And.Not.Contain("Value"));
        }

        [Test]
        public void MalformedJson_ReportsErrorCategoryWithoutRawJsonOrParserMessage()
        {
            var errors = new List<string>();
            var gateway = new ProtocolGateway(logError: errors.Add);
            gateway.ProcessIncoming(Packet("{\"" + PrivateFixture + "\": [}"));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("Envelope parse error").And.Contain("JsonReaderException")
                .And.Not.Contain(PrivateFixture).And.Not.Contain("JSON:"));
        }

        [Test]
        public void HandlerFailure_IsSanitized_AndLaterDispatchStillWorks()
        {
            var errors = new List<string>();
            var gateway = new ProtocolGateway(logError: errors.Add);
            int handled = 0;
            gateway.RegisterHandler("fixture", _ =>
            {
                if (++handled == 1) throw new InvalidOperationException(PrivateFixture);
            });
            ProtocolPacket packet = Packet("{\"type\":\"fixture\",\"payload\":null}");
            gateway.ProcessIncoming(packet);
            gateway.ProcessIncoming(packet);
            Assert.That(handled, Is.EqualTo(2));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(errors[0], Does.Contain("Handler threw").And.Contain("InvalidOperationException")
                .And.Not.Contain(PrivateFixture));
        }

        [Test]
        public void UnknownDiscriminator_AndTypedConversionFailure_DoNotExposePacketFields()
        {
            var debug = new List<string>();
            var errors = new List<string>();
            var gateway = new ProtocolGateway(null, debug.Add, errors.Add, () => true);
            gateway.ProcessIncoming(Packet("{\"type\":\"" + PrivateFixture + "\",\"payload\":null}"));
            gateway.RegisterHandler<FixturePayload>("fixture", _ => Assert.Fail("Invalid value must not dispatch"));
            gateway.ProcessIncoming(Packet("{\"type\":\"fixture\",\"payload\":{\"Value\":\"" + PrivateFixture + "\"}}"));
            Assert.That(debug, Has.Count.EqualTo(1));
            Assert.That(errors, Has.Count.EqualTo(1));
            Assert.That(string.Join("", debug) + string.Join("", errors), Does.Not.Contain(PrivateFixture));
        }

        private static ProtocolPacket Packet(string json) => new(Encoding.UTF8.GetBytes(json), "fixture-owner", "fixture-topic", true);
        private sealed class FixturePayload { public int Value { get; set; } }
    }
}
