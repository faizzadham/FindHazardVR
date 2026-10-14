using System;
using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Presentation.Services.Utilities;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class IdleResetPolicyTests
    {
        [Test]
        public void Warning_Then_UiActivity_Allows_Reset_Once()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();

            policy.HandleIdleWarning();

            Assert.IsTrue(policy.ShouldMonitorUi);
            Assert.IsTrue(policy.TryBeginResetAttempt(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

            policy.HandleResetResult(true);

            Assert.IsFalse(policy.IsWarningActive);
            Assert.IsFalse(policy.ShouldMonitorUi);
        }

        [Test]
        public void Warning_Then_VoiceActivity_Clears_Without_Reset()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();

            policy.HandleIdleWarning();
            policy.HandlePlayerSpeakingStateChanged(true);

            Assert.IsFalse(policy.IsWarningActive);
            Assert.IsFalse(policy.ShouldMonitorUi);
            Assert.IsFalse(policy.TryBeginResetAttempt(DateTime.UtcNow));
        }

        [Test]
        public void Warning_Then_ServerVisible_Rtvi_Activity_Clears_Without_Reset()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();

            policy.HandleIdleWarning();
            policy.HandleOutboundRtviMessage("trigger-message");

            Assert.IsFalse(policy.IsWarningActive);
            Assert.IsFalse(policy.ShouldMonitorUi);
        }

        [Test]
        public void No_Warning_Does_Not_Allow_Reset()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();

            Assert.IsFalse(policy.TryBeginResetAttempt(DateTime.UtcNow));
        }

        [Test]
        public void Failed_Reset_Uses_Cooldown_Before_Retry()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            policy.HandleIdleWarning();
            Assert.IsTrue(policy.TryBeginResetAttempt(start));

            policy.HandleResetResult(false);

            Assert.IsTrue(policy.IsWarningActive);
            Assert.IsFalse(policy.TryBeginResetAttempt(start.AddSeconds(2)));
            Assert.IsTrue(policy.TryBeginResetAttempt(start.AddSeconds(4)));
        }

        [Test]
        public void Repeated_Warning_Rearms_After_Successful_Reset()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();
            DateTime start = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

            policy.HandleIdleWarning();
            Assert.IsTrue(policy.TryBeginResetAttempt(start));
            policy.HandleResetResult(true);

            policy.HandleIdleWarning();

            Assert.IsTrue(policy.IsWarningActive);
            Assert.IsTrue(policy.TryBeginResetAttempt(start.AddSeconds(4)));
        }

        [Test]
        public void Disconnect_Clears_State()
        {
            IdleResetPolicy policy = CreateConnectedPolicy();

            policy.HandleIdleWarning();
            policy.HandleSessionStateChanged(SessionState.Disconnected);

            Assert.IsFalse(policy.IsWarningActive);
            Assert.IsFalse(policy.IsVoiceActive);
            Assert.IsFalse(policy.ShouldMonitorUi);
        }

        [Test]
        public void IdleDeadline_Elapses_Once_At_ServerProvided_Deadline()
        {
            DateTime warningAt = new(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
            var tracker = new IdleDeadlineTracker();
            tracker.Arm(new UserIdleWarningReceived(120, "Idle", warningAt));

            Assert.IsFalse(tracker.TryConsume(warningAt.AddSeconds(119), out _));
            Assert.IsTrue(tracker.TryConsume(warningAt.AddSeconds(120), out UserIdleTimeoutElapsed elapsed));
            Assert.AreEqual(warningAt, elapsed.WarningReceivedAt);
            Assert.AreEqual(warningAt.AddSeconds(120), elapsed.DeadlineUtc);
            Assert.IsFalse(tracker.IsArmed);
            Assert.IsFalse(tracker.TryConsume(warningAt.AddSeconds(121), out _));
        }

        [Test]
        public void IdleDeadline_Rearm_Replaces_Older_Warning_And_Clear_Cancels_It()
        {
            DateTime warningAt = new(2026, 7, 31, 10, 0, 0, DateTimeKind.Utc);
            var tracker = new IdleDeadlineTracker();
            tracker.Arm(new UserIdleWarningReceived(120, "First", warningAt));
            tracker.Arm(new UserIdleWarningReceived(60, "Second", warningAt.AddSeconds(30)));

            Assert.AreEqual(warningAt.AddSeconds(90), tracker.DeadlineUtc);

            tracker.Clear();

            Assert.IsFalse(tracker.IsArmed);
            Assert.IsFalse(tracker.TryConsume(warningAt.AddMinutes(10), out _));
        }

        private static IdleResetPolicy CreateConnectedPolicy()
        {
            var policy = new IdleResetPolicy(TimeSpan.FromSeconds(3));
            policy.HandleSessionStateChanged(SessionState.Connected);
            return policy;
        }
    }
}
