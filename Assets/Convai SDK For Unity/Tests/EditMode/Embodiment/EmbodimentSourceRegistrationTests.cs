using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    public sealed class EmbodimentSourceRegistrationTests
    {
        // ── ConversationFlowSource ─────────────────────────────────────────────

        [Test]
        public void RegisterConversationFlowSource_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_FlowRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IConversationFlowSource received = null;
                ctx.AddServiceChangedHandler<IConversationFlowSource>(s => received = s);

                StubFlowSource source = new();
                ctx.Provide<IConversationFlowSource>(source);

                Assert.AreSame(source, received);
                Assert.AreSame(source, ctx.ConversationFlowSource);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterConversationFlowSource_SameSourceTwice_EventNotFiredSecondTime()
        {
            GameObject root = new("SrcReg_FlowDuplicateSame");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                int callCount = 0;
                ctx.AddServiceChangedHandler<IConversationFlowSource>(_ => callCount++);

                StubFlowSource source = new();
                ctx.Provide<IConversationFlowSource>(source);
                ctx.Provide<IConversationFlowSource>(source);

                Assert.AreEqual(1, callCount, "Event must not fire on re-registration of same source");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterConversationFlowSource_DifferentSource_WarnsAndKeepsOriginal()
        {
            GameObject root = new("SrcReg_FlowDuplicateDiff");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubFlowSource first = new();
                StubFlowSource second = new();
                ctx.Provide<IConversationFlowSource>(first);

                LogAssert.Expect(LogType.Warning,
                    new Regex("Duplicate conversation flow source", RegexOptions.IgnoreCase));

                ctx.Provide<IConversationFlowSource>(second);

                Assert.AreSame(first, ctx.ConversationFlowSource, "Second source must not replace first");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterConversationFlowSource_MatchingSource_ClearsAndFiresNull()
        {
            GameObject root = new("SrcReg_FlowUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubFlowSource source = new();
                ctx.Provide<IConversationFlowSource>(source);

                IConversationFlowSource received = source;
                ctx.AddServiceChangedHandler<IConversationFlowSource>(s => received = s);

                ctx.Withdraw<IConversationFlowSource>(source);

                Assert.IsNull(ctx.ConversationFlowSource);
                Assert.IsNull(received);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterConversationFlowSource_NonMatchingSource_DoesNotClear()
        {
            GameObject root = new("SrcReg_FlowUnregisterWrong");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubFlowSource registered = new();
                StubFlowSource unrelated = new();
                ctx.Provide<IConversationFlowSource>(registered);

                ctx.Withdraw<IConversationFlowSource>(unrelated);

                Assert.AreSame(registered, ctx.ConversationFlowSource);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── EmotionStateSource ─────────────────────────────────────────────────

        [Test]
        public void RegisterEmotionStateSource_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_EmotionRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IEmotionStateSource received = null;
                ctx.AddServiceChangedHandler<IEmotionStateSource>(s => received = s);

                StubEmotionSource source = new();
                ctx.Provide<IEmotionStateSource>(source);

                Assert.AreSame(source, received);
                Assert.AreSame(source, ctx.EmotionStateSource);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── ProfileReceiver ────────────────────────────────────────────────────

        [Test]
        public void RegisterProfileReceiver_FiresRegisteredEvent()
        {
            GameObject root = new("SrcReg_ReceiverRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                EmbodimentProfileReceiverRegistration? received = null;
                ctx.ProfileReceiverRegistered += r => received = r;

                StubReceiver receiver = root.AddComponent<StubReceiver>();
                ctx.RegisterProfileReceiver(receiver, receiver);

                Assert.IsTrue(received.HasValue);
                Assert.AreSame(receiver, received.Value.Receiver);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterProfileReceiver_SameReceiverTwice_IsIdempotent()
        {
            GameObject root = new("SrcReg_ReceiverDuplicate");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                int callCount = 0;
                ctx.ProfileReceiverRegistered += _ => callCount++;

                StubReceiver receiver = root.AddComponent<StubReceiver>();
                ctx.RegisterProfileReceiver(receiver, receiver);
                ctx.RegisterProfileReceiver(receiver, receiver);

                Assert.AreEqual(1, callCount, "Second registration of same receiver must be ignored");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterProfileReceiver_RemovesFromGetProfileReceivers()
        {
            GameObject root = new("SrcReg_ReceiverUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubReceiver receiver = root.AddComponent<StubReceiver>();
                ctx.RegisterProfileReceiver(receiver, receiver);

                ctx.UnregisterProfileReceiver(receiver);

                var results = new List<EmbodimentProfileReceiverRegistration>();
                ctx.GetProfileReceivers(results);
                Assert.AreEqual(0, results.Count);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void GetProfileReceivers_AfterRegister_ContainsReceiver()
        {
            GameObject root = new("SrcReg_GetReceivers");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubReceiver receiver = root.AddComponent<StubReceiver>();
                ctx.RegisterProfileReceiver(receiver, receiver);

                var results = new List<EmbodimentProfileReceiverRegistration>();
                ctx.GetProfileReceivers(results);

                Assert.AreEqual(1, results.Count);
                Assert.AreSame(receiver, results[0].Receiver);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── HeadGestureChannel ─────────────────────────────────────────────────

        [Test]
        public void RegisterHeadGestureChannel_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_HeadGestureRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IHeadGestureChannel received = null;
                ctx.AddServiceChangedHandler<IHeadGestureChannel>(c => received = c);

                StubHeadGestureChannel channel = new();
                ctx.Provide<IHeadGestureChannel>(channel);

                Assert.AreSame(channel, received);
                Assert.AreSame(channel, ctx.HeadGestureChannel);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterHeadGestureChannel_SameChannelTwice_EventNotFiredSecondTime()
        {
            GameObject root = new("SrcReg_HeadGestureDuplicateSame");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                int callCount = 0;
                ctx.AddServiceChangedHandler<IHeadGestureChannel>(_ => callCount++);

                StubHeadGestureChannel channel = new();
                ctx.Provide<IHeadGestureChannel>(channel);
                ctx.Provide<IHeadGestureChannel>(channel);

                Assert.AreEqual(1, callCount, "Event must not fire on re-registration of same channel");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterHeadGestureChannel_DifferentChannel_WarnsAndKeepsOriginal()
        {
            GameObject root = new("SrcReg_HeadGestureDuplicateDiff");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubHeadGestureChannel first = new();
                StubHeadGestureChannel second = new();
                ctx.Provide<IHeadGestureChannel>(first);

                LogAssert.Expect(LogType.Warning,
                    new Regex("Duplicate head gesture channel", RegexOptions.IgnoreCase));

                ctx.Provide<IHeadGestureChannel>(second);

                Assert.AreSame(first, ctx.HeadGestureChannel, "Second channel must not replace first");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterHeadGestureChannel_MatchingChannel_ClearsAndFiresNull()
        {
            GameObject root = new("SrcReg_HeadGestureUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubHeadGestureChannel channel = new();
                ctx.Provide<IHeadGestureChannel>(channel);

                IHeadGestureChannel received = channel;
                ctx.AddServiceChangedHandler<IHeadGestureChannel>(c => received = c);

                ctx.Withdraw<IHeadGestureChannel>(channel);

                Assert.IsNull(ctx.HeadGestureChannel);
                Assert.IsNull(received);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── ConversationalGesturePerformer ──────────────────────────────────────

        [Test]
        public void RegisterConversationalGesturePerformer_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_GesturePerformerRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IConversationalGesturePerformer received = null;
                ctx.AddServiceChangedHandler<IConversationalGesturePerformer>(p => received = p);

                StubConversationalGesturePerformer performer = new();
                ctx.Provide<IConversationalGesturePerformer>(performer);

                Assert.AreSame(performer, received);
                Assert.AreSame(performer, ctx.ConversationalGesturePerformer);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterConversationalGesturePerformer_SamePerformerTwice_EventNotFiredSecondTime()
        {
            GameObject root = new("SrcReg_GesturePerformerDuplicateSame");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                int callCount = 0;
                ctx.AddServiceChangedHandler<IConversationalGesturePerformer>(_ => callCount++);

                StubConversationalGesturePerformer performer = new();
                ctx.Provide<IConversationalGesturePerformer>(performer);
                ctx.Provide<IConversationalGesturePerformer>(performer);

                Assert.AreEqual(1, callCount, "Event must not fire on re-registration of same performer");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterConversationalGesturePerformer_DifferentPerformer_WarnsAndKeepsOriginal()
        {
            GameObject root = new("SrcReg_GesturePerformerDuplicateDiff");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubConversationalGesturePerformer first = new();
                StubConversationalGesturePerformer second = new();
                ctx.Provide<IConversationalGesturePerformer>(first);

                LogAssert.Expect(LogType.Warning,
                    new Regex("Duplicate conversational gesture performer", RegexOptions.IgnoreCase));

                ctx.Provide<IConversationalGesturePerformer>(second);

                Assert.AreSame(first, ctx.ConversationalGesturePerformer, "Second performer must not replace first");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterConversationalGesturePerformer_MatchingPerformer_ClearsAndFiresNull()
        {
            GameObject root = new("SrcReg_GesturePerformerUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubConversationalGesturePerformer performer = new();
                ctx.Provide<IConversationalGesturePerformer>(performer);

                IConversationalGesturePerformer received = performer;
                ctx.AddServiceChangedHandler<IConversationalGesturePerformer>(p => received = p);

                ctx.Withdraw<IConversationalGesturePerformer>(performer);

                Assert.IsNull(ctx.ConversationalGesturePerformer);
                Assert.IsNull(received);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── GazeCommandHandler (action seam) ────────────────────────

        [Test]
        public void RegisterGazeCommandHandler_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_GazeCommandRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IGazeCommandHandler received = null;
                ctx.AddServiceChangedHandler<IGazeCommandHandler>(h => received = h);

                StubGazeCommandHandler handler = new();
                ctx.Provide<IGazeCommandHandler>(handler);

                Assert.AreSame(handler, received);
                Assert.AreSame(handler, ctx.GazeCommandHandler);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RegisterGazeCommandHandler_DifferentHandler_WarnsAndKeepsOriginal()
        {
            GameObject root = new("SrcReg_GazeCommandDuplicateDiff");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubGazeCommandHandler first = new();
                StubGazeCommandHandler second = new();
                ctx.Provide<IGazeCommandHandler>(first);

                LogAssert.Expect(LogType.Warning,
                    new Regex("Duplicate gaze command handler", RegexOptions.IgnoreCase));

                ctx.Provide<IGazeCommandHandler>(second);

                Assert.AreSame(first, ctx.GazeCommandHandler, "Second handler must not replace first");
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterGazeCommandHandler_MatchingHandler_ClearsAndFiresNull()
        {
            GameObject root = new("SrcReg_GazeCommandUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubGazeCommandHandler handler = new();
                ctx.Provide<IGazeCommandHandler>(handler);

                IGazeCommandHandler received = handler;
                ctx.AddServiceChangedHandler<IGazeCommandHandler>(h => received = h);

                ctx.Withdraw<IGazeCommandHandler>(handler);

                Assert.IsNull(ctx.GazeCommandHandler);
                Assert.IsNull(received);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── MoodCommandHandler (action seam) ────────────────────────

        [Test]
        public void RegisterMoodCommandHandler_FiresChangedEvent()
        {
            GameObject root = new("SrcReg_MoodCommandRegister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                IMoodCommandHandler received = null;
                ctx.AddServiceChangedHandler<IMoodCommandHandler>(h => received = h);

                StubMoodCommandHandler handler = new();
                ctx.Provide<IMoodCommandHandler>(handler);

                Assert.AreSame(handler, received);
                Assert.AreSame(handler, ctx.MoodCommandHandler);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void UnregisterMoodCommandHandler_MatchingHandler_ClearsAndFiresNull()
        {
            GameObject root = new("SrcReg_MoodCommandUnregister");
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                StubMoodCommandHandler handler = new();
                ctx.Provide<IMoodCommandHandler>(handler);

                IMoodCommandHandler received = handler;
                ctx.AddServiceChangedHandler<IMoodCommandHandler>(h => received = h);

                ctx.Withdraw<IMoodCommandHandler>(handler);

                Assert.IsNull(ctx.MoodCommandHandler);
                Assert.IsNull(received);
            }
            finally { Object.DestroyImmediate(root); }
        }

        // ── Stubs ──────────────────────────────────────────────────────────────

        private sealed class StubHeadGestureChannel : IHeadGestureChannel
        {
            public void RegisterConsumer(object consumer) { }
            public void UnregisterConsumer(object consumer) { }
            public bool TryGetOffset(out HeadGestureOffset offset)
            {
                offset = HeadGestureOffset.None;
                return false;
            }
        }

        private sealed class StubConversationalGesturePerformer : IConversationalGesturePerformer
        {
            public GestureSuppression CurrentSuppression => GestureSuppression.None;
            public bool TryPerform(in GestureCue cue) => false;
            public event Action<GestureCue, GesturePerformanceResult> Completed
            {
                add { }
                remove { }
            }
        }

        private sealed class StubFlowSource : IConversationFlowSource
        {
            public DialogueStateReading Current => DialogueStateReading.Idle;
            public event Action<DialogueStateReading> Changed
            {
                add { }
                remove { }
            }
        }

        private sealed class StubEmotionSource : IEmotionStateSource
        {
            public EmotionReading Current => EmotionReading.Neutral;
        }

        private sealed class StubReceiver : MonoBehaviour, IEmbodimentProfileReceiver
        {
            public string ModuleId => "test.src-reg-stub";
            public ScriptableObject Profile => null;
            public bool CanApplyProfile(ScriptableObject candidate) => true;
            public bool ApplyProfile(ScriptableObject candidate) => true;
        }

        private sealed class StubGazeCommandHandler : IGazeCommandHandler
        {
            public bool RequestSustainedGaze(Vector3 worldPosition, float durationSeconds, int priority) => true;
            public bool RequestGlance(Vector3 worldPosition, float durationSeconds, int priority) => true;
            public void ReleaseGaze() { }
        }

        private sealed class StubMoodCommandHandler : IMoodCommandHandler
        {
            public bool RequestMood(string label, float intensity, float transitionSeconds) => true;
            public bool RequestEmotionBeat(string label, float intensity) => true;
        }
    }
}
