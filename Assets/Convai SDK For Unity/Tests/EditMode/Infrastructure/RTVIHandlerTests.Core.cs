using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Infrastructure.Protocol.Messages;
using Newtonsoft.Json.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    public partial class RTVIHandlerTests
    {
        [Test]
        public void SendData_SerializesPayloadIntoTransport()
        {
            RtviTestContext context = CreateContext();

            context.Handler.SendData(new RTVIResetIdleTimer());

            Assert.AreEqual(1, context.Transport.SentPayloads.Count);
            JObject payload = JObject.Parse(context.Transport.SentPayloads[0]);
            Assert.AreEqual("reset-idle-timer", payload.Value<string>("type"));
            Assert.IsFalse(string.IsNullOrWhiteSpace(payload.Value<string>("id")));
        }

        [Test]
        public void PipelineError_MapsFatalAndRecoverableSessionErrors()
        {
            RtviTestContext context = CreateContext();
            var errors = new List<SessionError>();
            context.EventHub.Subscribe<SessionError>(errors.Add);

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "error",
                data: new JObject { ["error"] = "recoverable", ["fatal"] = false }));
            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "error",
                data: new JObject { ["error"] = "fatal", ["fatal"] = true }));

            Assert.AreEqual(2, errors.Count);
            Assert.AreEqual(SessionErrorCodes.ServerError, errors[0].ErrorCode);
            Assert.IsTrue(errors[0].IsRecoverable);
            Assert.AreEqual(SessionErrorCodes.ServerFatalError, errors[1].ErrorCode);
            Assert.IsFalse(errors[1].IsRecoverable);
        }

        [Test]
        public void Metrics_WhenDispatcherRejects_InvokesSubscriberInline()
        {
            RtviTestContext context = CreateContext();
            context.Dispatcher.AcceptDispatch = false;
            RTVIMetricsPayload captured = null;
            context.Handler.OnMetricsReceived += payload => captured = payload;

            context.Gateway.ProcessIncoming(CreateInboundPacket(
                "metrics",
                data: new JObject { ["custom"] = new JObject { ["frames"] = 12 } }));

            Assert.AreEqual(1, context.Dispatcher.DispatchAttempts);
            Assert.NotNull(captured);
            Assert.AreEqual(12, captured.Custom?["frames"]?.Value<int>());
        }

        [Test]
        public void Metrics_ThrowingSubscriber_IsContainedAndLogged()
        {
            RtviTestContext context = CreateContext();
            context.Handler.OnMetricsReceived += _ => throw new InvalidOperationException("subscriber failure");

            Assert.DoesNotThrow(() => context.Gateway.ProcessIncoming(CreateInboundPacket(
                "metrics",
                data: new JObject { ["processing"] = new JObject { ["llm"] = 0.2 } })));
            Assert.IsTrue(context.Logger.Contains("subscriber threw"));
        }
    }
}
