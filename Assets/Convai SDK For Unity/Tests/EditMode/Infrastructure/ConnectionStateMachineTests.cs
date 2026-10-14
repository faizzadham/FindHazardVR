using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Infrastructure.Networking.Connection;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class ConnectionStateMachineTests
    {
        private ConnectionStateMachine _stateMachine;
        private List<(ConnectionState oldState, ConnectionState newState, string error)> _events;

        private static IEnumerable<TestCaseData> TransitionCases()
        {
            foreach (ConnectionState from in Enum.GetValues(typeof(ConnectionState)))
            foreach (ConnectionState to in Enum.GetValues(typeof(ConnectionState)))
                yield return new TestCaseData(from, to, IsValidTransition(from, to))
                    .SetName($"Transition_{from}_To_{to}_Is_{{2}}");
        }

        [SetUp]
        public void SetUp()
        {
            _stateMachine = new ConnectionStateMachine();
            _events = new List<(ConnectionState, ConnectionState, string)>();
            _stateMachine.StateChanged += (oldState, newState, error) =>
                _events.Add((oldState, newState, error));
        }

        [Test]
        public void StartsDisconnected() =>
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(ConnectionState.Disconnected));

        [TestCaseSource(nameof(TransitionCases))]
        public void TryTransition_EnforcesCompleteStateGraph(ConnectionState from, ConnectionState to, bool expected)
        {
            _stateMachine.ForceTransition(from);
            _events.Clear();

            bool changed = _stateMachine.TryTransition(to, "reason");

            Assert.That(changed, Is.EqualTo(expected));
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(expected ? to : from));
            Assert.That(_events, Has.Count.EqualTo(expected ? 1 : 0));
            if (!expected) return;
            Assert.That(_events[0], Is.EqualTo((from, to, "reason")));
        }

        [Test]
        public void ForceTransition_BypassesGraphButSameStateIsNoOp()
        {
            _stateMachine.ForceTransition(ConnectionState.Connected, "forced");
            _stateMachine.ForceTransition(ConnectionState.Connected, "ignored");

            Assert.That(_stateMachine.CurrentState, Is.EqualTo(ConnectionState.Connected));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_events[0], Is.EqualTo((ConnectionState.Disconnected, ConnectionState.Connected, "forced")));
        }

        [Test]
        public void Reset_PublishesOnlyWhenStateChanges()
        {
            _stateMachine.ForceTransition(ConnectionState.Reconnecting);
            _events.Clear();

            _stateMachine.Reset();
            _stateMachine.Reset();

            Assert.That(_stateMachine.CurrentState, Is.EqualTo(ConnectionState.Disconnected));
            Assert.That(_events, Has.Count.EqualTo(1));
            Assert.That(_events[0].oldState, Is.EqualTo(ConnectionState.Reconnecting));
            Assert.That(_events[0].newState, Is.EqualTo(ConnectionState.Disconnected));
        }

        [Test]
        public void ConcurrentTransition_AllowsOneWinner()
        {
            _stateMachine.ForceTransition(ConnectionState.Connecting);
            int successes = 0;

            Parallel.For(0, 100, _ =>
            {
                if (_stateMachine.TryTransition(ConnectionState.Connected))
                    Interlocked.Increment(ref successes);
            });

            Assert.That(successes, Is.EqualTo(1));
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(ConnectionState.Connected));
        }

        [Test]
        public void StateChangedHandlerException_DoesNotBlockTransition()
        {
            _stateMachine.StateChanged += (_, _, _) => throw new InvalidOperationException("subscriber failure");

            bool changed = _stateMachine.TryTransition(ConnectionState.Connecting);

            Assert.That(changed, Is.True);
            Assert.That(_stateMachine.CurrentState, Is.EqualTo(ConnectionState.Connecting));
        }

        private static bool IsValidTransition(ConnectionState from, ConnectionState to) => (from, to) switch
        {
            (ConnectionState.Disconnected, ConnectionState.Connecting) => true,
            (ConnectionState.Connecting, ConnectionState.Connected or ConnectionState.Disconnected) => true,
            (ConnectionState.Connected, ConnectionState.Reconnecting or ConnectionState.Disconnecting) => true,
            (ConnectionState.Reconnecting, ConnectionState.Connected or ConnectionState.Disconnected) => true,
            (ConnectionState.Disconnecting, ConnectionState.Disconnected) => true,
            _ => false
        };
    }
}
