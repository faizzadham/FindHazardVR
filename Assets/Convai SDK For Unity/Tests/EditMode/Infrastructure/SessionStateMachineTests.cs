using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.EventSystem;
using Convai.Infrastructure.Networking.Services;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class SessionStateMachineTests
    {
        private EventHub _eventHub;
        private SessionStateMachine _stateMachine;
        private List<SessionStateChanged> _events;

        private static IEnumerable<TestCaseData> TransitionCases()
        {
            foreach (SessionState from in Enum.GetValues(typeof(SessionState)))
            foreach (SessionState to in Enum.GetValues(typeof(SessionState)))
                yield return new TestCaseData(from, to, IsValidTransition(from, to))
                    .SetName($"Transition_{from}_To_{to}_Is_{{2}}");
        }

        [SetUp]
        public void SetUp()
        {
            _eventHub = new EventHub(new ImmediateScheduler());
            _stateMachine = new SessionStateMachine(_eventHub);
            _events = new List<SessionStateChanged>();
            _eventHub.Subscribe<SessionStateChanged>(_events.Add);
        }

        [Test]
        public void StartsDisconnectedWithoutSession()
        {
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(SessionState.Disconnected));
            Assert.That(_stateMachine.SessionId, Is.Null);
        }

        [TestCaseSource(nameof(TransitionCases))]
        public void TryTransition_EnforcesCompleteStateGraph(SessionState from, SessionState to, bool expected)
        {
            _stateMachine.ForceTransition(from);
            _events.Clear();

            bool changed = _stateMachine.TryTransition(to);

            Assert.That(changed, Is.EqualTo(expected));
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(expected ? to : from));
            Assert.That(_events, Has.Count.EqualTo(expected ? 1 : 0));
            if (!expected) return;
            Assert.That(_events[0].OldState, Is.EqualTo(from));
            Assert.That(_events[0].NewState, Is.EqualTo(to));
        }

        [Test]
        public void ConnectedAndErrorEvents_CarryMetadata()
        {
            _stateMachine.TryTransition(SessionState.Connecting);
            _stateMachine.TryTransition(SessionState.Connected, "session-7");
            _stateMachine.TryTransition(SessionState.Reconnecting);
            SessionError error = SessionError.Create("network.timeout", "Timed out");
            _stateMachine.TryTransition(SessionState.Error, error: error);

            Assert.That(_events[1].SessionId, Is.EqualTo("session-7"));
            Assert.That(_events[1].IsConnectionEstablished, Is.True);
            Assert.That(_events[2].IsReconnecting, Is.True);
            Assert.That(_events[3].ErrorCode, Is.EqualTo("network.timeout"));
            Assert.That(_events[3].IsError, Is.True);
        }

        [Test]
        public void Reconnection_PreservesSessionUntilDisconnected()
        {
            _stateMachine.TryTransition(SessionState.Connecting);
            _stateMachine.TryTransition(SessionState.Connected, "session-1");
            _stateMachine.TryTransition(SessionState.Reconnecting);
            _stateMachine.TryTransition(SessionState.Connected);
            Assert.That(_stateMachine.SessionId, Is.EqualTo("session-1"));

            _stateMachine.TryTransition(SessionState.Disconnecting);
            _stateMachine.TryTransition(SessionState.Disconnected);
            Assert.That(_stateMachine.SessionId, Is.Null);
        }

        [Test]
        public void Reset_ClearsSessionAndPublishesOnlyWhenStateChanges()
        {
            _stateMachine.TryTransition(SessionState.Connecting);
            _stateMachine.TryTransition(SessionState.Connected, "session-1");
            _events.Clear();

            _stateMachine.Reset();
            _stateMachine.Reset();

            Assert.That(_stateMachine.CurrentState, Is.EqualTo(SessionState.Disconnected));
            Assert.That(_stateMachine.SessionId, Is.Null);
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_events[0].IsDisconnected, Is.True);
        }

        [Test]
        public void ConcurrentTransition_AllowsOneWinner()
        {
            int successes = 0;
            Parallel.For(0, 100, _ =>
            {
                if (_stateMachine.TryTransition(SessionState.Connecting))
                    Interlocked.Increment(ref successes);
            });

            Assert.That(successes, Is.EqualTo(1));
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(SessionState.Connecting));
        }

        private static bool IsValidTransition(SessionState from, SessionState to) => (from, to) switch
        {
            (SessionState.Disconnected, SessionState.Connecting) => true,
            (SessionState.Connecting, SessionState.Connected or SessionState.Disconnected or SessionState.Error) =>
                true,
            (SessionState.Connected, SessionState.Disconnecting or SessionState.Reconnecting) => true,
            (SessionState.Reconnecting, SessionState.Connected or SessionState.Error) => true,
            (SessionState.Disconnecting, SessionState.Disconnected) => true,
            (SessionState.Error, SessionState.Disconnected) => true,
            _ => false
        };

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
