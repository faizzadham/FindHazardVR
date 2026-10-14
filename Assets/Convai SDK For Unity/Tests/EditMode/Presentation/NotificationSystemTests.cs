using System;
using System.Collections.Generic;
using Convai.Domain.DomainEvents.Session;
using Convai.Domain.Errors;
using Convai.Domain.EventSystem;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Presentation.Views.Notifications;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Presentation
{
    /// <summary>
    ///     Tests for the notification system components:
    ///     - ConvaiNotificationService
    ///     - ConvaiNotificationEventBridge
    /// </summary>
    public class NotificationSystemTests
    {
        #region Helper Methods

        private static SONotification CreateNotification(string id)
        {
            var n = ScriptableObject.CreateInstance<SONotification>();
            n.name = id;
            return n;
        }

        private static SONotificationErrorMap CreateTestErrorMap()
        {
            var map = ScriptableObject.CreateInstance<SONotificationErrorMap>();
            SONotification microphoneIssue = CreateNotification("MICROPHONE_ISSUE");
            SONotification networkIssue = CreateNotification("NETWORK_REACHABILITY_ISSUE");
            SONotification noMicrophone = CreateNotification("NO_MICROPHONE_DETECTED");
            SONotification apiKeyMissing = CreateNotification("API_KEY_NOT_FOUND");
            SONotification usageLimitExceeded = CreateNotification("USAGE_LIMIT_EXCEEDED");

            map.Rules = new List<SessionErrorNotificationRule>
            {
                new() { ErrorPattern = SessionErrorCodes.ConnectionAuthFailed, MatchType = SessionErrorMatchType.Exact, Notification = apiKeyMissing },
                new() { ErrorPattern = SessionErrorCodes.ConnectionInvalidToken, MatchType = SessionErrorMatchType.Exact, Notification = apiKeyMissing },
                new() { ErrorPattern = SessionErrorCodes.ConfigApiKeyMissing, MatchType = SessionErrorMatchType.Exact, Notification = apiKeyMissing },
                new() { ErrorPattern = SessionErrorCodes.ConnectionRateLimited, MatchType = SessionErrorMatchType.Exact, Notification = usageLimitExceeded },
                new() { ErrorPattern = SessionErrorCodes.ServerUsageLimitReached, MatchType = SessionErrorMatchType.Exact, Notification = usageLimitExceeded },
                new() { ErrorPattern = SessionErrorCodes.ConnectionTimeout, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.ConnectionNetworkError, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.ConnectionServiceUnavailable, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.ConnectionServerError, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.TransportLivekitError, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.ConnectionFailed, MatchType = SessionErrorMatchType.Exact, Notification = networkIssue },
                new() { ErrorPattern = SessionErrorCodes.AudioMicUnavailable, MatchType = SessionErrorMatchType.Exact, Notification = noMicrophone },
                new() { ErrorPattern = SessionErrorCodes.AudioMicPermissionDenied, MatchType = SessionErrorMatchType.Exact, Notification = microphoneIssue },
                new() { ErrorPattern = SessionErrorCodes.AudioMicPublishFailed, MatchType = SessionErrorMatchType.Exact, Notification = microphoneIssue },
                new() { ErrorPattern = "connection.", MatchType = SessionErrorMatchType.Prefix, Notification = networkIssue },
                new() { ErrorPattern = "transport.", MatchType = SessionErrorMatchType.Prefix, Notification = networkIssue },
                new() { ErrorPattern = "server.", MatchType = SessionErrorMatchType.Prefix, Notification = networkIssue },
                new() { ErrorPattern = "audio.", MatchType = SessionErrorMatchType.Prefix, Notification = microphoneIssue },
                new() { ErrorPattern = "config.", MatchType = SessionErrorMatchType.Prefix, Notification = apiKeyMissing }
            };
            return map;
        }

        private (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub)
            CreateEventBridge(bool notificationsEnabled)
        {
            var mockService = new MockNotificationService();
            var eventHub = new EventHub(new ImmediateScheduler());
            var bridge = new ConvaiNotificationEventBridge(mockService, eventHub, () => notificationsEnabled,
                CreateTestErrorMap());

            return (bridge, mockService, eventHub);
        }

        #endregion

        #region ImmediateScheduler

        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }

        #endregion

        #region Mock NotificationService

        private sealed class MockNotificationService : IConvaiNotificationService
        {
            public List<SONotification> RequestedNotifications { get; } = new();
            public int DismissCount { get; private set; }

            public event Action<SONotification> OnNotificationRequested;
            public event Action OnNotificationDismissed;

            public void RequestNotification(SONotification notification)
            {
                RequestedNotifications.Add(notification);
                OnNotificationRequested?.Invoke(notification);
            }

            public void DismissNotification()
            {
                DismissCount++;
                OnNotificationDismissed?.Invoke();
            }
        }

        #endregion

        #region NotificationGroup Tests

        [Test]
        public void NotificationGroup_TryResolve_UsesStableIdRegistry()
        {
            SONotification canonicalNotification = CreateNotification("MICROPHONE_ISSUE");
            SONotification requestedNotification = CreateNotification("MICROPHONE_ISSUE");
            var group = ScriptableObject.CreateInstance<SONotificationGroup>();
            group.soNotifications = new[] { canonicalNotification };

            bool resolved = group.TryResolve(requestedNotification, out SONotification resolvedNotification);

            Assert.IsTrue(resolved);
            Assert.AreSame(canonicalNotification, resolvedNotification);
        }

        #endregion

        #region ConvaiNotificationService Tests

        [Test]
        public void ConvaiNotificationService_RequestNotification_InvokesHandler()
        {
            var scheduler = new ImmediateScheduler();
            var service = new ConvaiNotificationService(scheduler);
            SONotification received = null;
            SONotification mic = CreateNotification("MICROPHONE_ISSUE");

            service.OnNotificationRequested += n => received = n;
            service.RequestNotification(mic);

            Assert.AreEqual(mic, received);
            Assert.AreEqual("MICROPHONE_ISSUE", received.Id);
        }

        [Test]
        public void ConvaiNotificationService_RequestNotification_NoHandler_DoesNotThrow()
        {
            var scheduler = new ImmediateScheduler();
            var service = new ConvaiNotificationService(scheduler);

            Assert.DoesNotThrow(() => service.RequestNotification(CreateNotification("MICROPHONE_ISSUE")));
        }

        [Test]
        public void ConvaiNotificationService_DismissNotification_InvokesHandler()
        {
            var scheduler = new ImmediateScheduler();
            var service = new ConvaiNotificationService(scheduler);
            bool dismissed = false;

            service.OnNotificationDismissed += () => dismissed = true;
            service.DismissNotification();

            Assert.IsTrue(dismissed);
        }

        [Test]
        public void ConvaiNotificationService_MultipleHandlers_AllInvoked()
        {
            var scheduler = new ImmediateScheduler();
            var service = new ConvaiNotificationService(scheduler);
            int invokeCount = 0;

            service.OnNotificationRequested += _ => invokeCount++;
            service.OnNotificationRequested += _ => invokeCount++;

            service.RequestNotification(CreateNotification("NETWORK_REACHABILITY_ISSUE"));

            Assert.AreEqual(2, invokeCount);
        }

        [Test]
        public void ConvaiNotificationService_UnsubscribeHandler_NoLongerInvoked()
        {
            var scheduler = new ImmediateScheduler();
            var service = new ConvaiNotificationService(scheduler);
            int invokeCount = 0;

            void Handler(SONotification n) => invokeCount++;

            service.OnNotificationRequested += Handler;
            service.RequestNotification(CreateNotification("MICROPHONE_ISSUE"));
            Assert.AreEqual(1, invokeCount);

            service.OnNotificationRequested -= Handler;
            service.RequestNotification(CreateNotification("MICROPHONE_ISSUE"));
            Assert.AreEqual(1, invokeCount, "Handler should not be invoked after unsubscribe");
        }

        #endregion

        #region ConvaiNotificationEventBridge Error Code Mapping Tests

        [Test]
        public void EventBridge_AuthFailed_MapsToApiKeyNotFound()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionAuthFailed, "Auth failed",
                "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("API_KEY_NOT_FOUND", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_RateLimited_MapsToUsageLimitExceeded()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionRateLimited, "Rate limited",
                "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("USAGE_LIMIT_EXCEEDED", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_NetworkError_MapsToNetworkReachabilityIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Network error",
                "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_Timeout_MapsToNetworkReachabilityIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionTimeout, "Timeout", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_MicUnavailable_MapsToNoMicrophoneDetected()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.AudioMicUnavailable, "No mic", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("NO_MICROPHONE_DETECTED", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_MicPermissionDenied_MapsToMicrophoneIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.AudioMicPermissionDenied, "Permission denied",
                "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("MICROPHONE_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_UnknownErrorCode_WithAudioPrefix_MapsToMicrophoneIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create("audio.unknown_error", "Unknown audio error", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("MICROPHONE_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_UnknownErrorCode_WithConnectionPrefix_MapsToNetworkIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create("connection.unknown", "Unknown connection error", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_ServerError_MapsToNetworkIssue()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ServerError, "Pipeline error", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_ServerUsageLimitReached_MapsToUsageLimitExceeded()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ServerUsageLimitReached, "Quota exceeded",
                "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("USAGE_LIMIT_EXCEEDED", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        [Test]
        public void DefaultErrorMap_ServerErrors_MapToExpectedNotifications()
        {
            SONotificationErrorMap map = SONotificationErrorMap.LoadDefault();

            Assert.IsNotNull(map);
            Assert.IsTrue(map.TryResolve(SessionErrorCodes.ServerError, out SONotification serverError));
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", serverError.Id);
            Assert.IsTrue(map.TryResolve(SessionErrorCodes.ServerFatalError, out SONotification fatalError));
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", fatalError.Id);
            Assert.IsTrue(map.TryResolve(SessionErrorCodes.ServerUsageLimitReached, out SONotification usageLimit));
            Assert.AreEqual("USAGE_LIMIT_EXCEEDED", usageLimit.Id);
        }

        [Test]
        public void EventBridge_CompletelyUnknownErrorCode_DoesNotNotify()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            eventHub.Publish(SessionError.Create("completely.unknown.code", "Unknown error", "test-session"));

            Assert.AreEqual(0, mockService.RequestedNotifications.Count);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_CustomRule_AllowsNewNotificationCategoryWithoutCodeChanges()
        {
            var customNotification = CreateNotification("VISION_CAMERA_LOST");
            var map = ScriptableObject.CreateInstance<SONotificationErrorMap>();
            map.Rules = new List<SessionErrorNotificationRule>
            {
                new()
                {
                    ErrorPattern = "vision.camera_lost",
                    MatchType = SessionErrorMatchType.Exact,
                    Notification = customNotification
                }
            };

            var mockService = new MockNotificationService();
            var eventHub = new EventHub(new ImmediateScheduler());
            var bridge = new ConvaiNotificationEventBridge(mockService, eventHub, () => true, map);

            eventHub.Publish(SessionError.Create("vision.camera_lost", "Camera lost", "test-session"));

            Assert.AreEqual(1, mockService.RequestedNotifications.Count);
            Assert.AreEqual("VISION_CAMERA_LOST", mockService.RequestedNotifications[0].Id);

            bridge.Dispose();
        }

        #endregion

        #region Cooldown Tests

        [Test]
        public void EventBridge_Cooldown_PreventsDuplicateNotifications()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);
            bridge.CooldownSeconds = 60f; // Long cooldown

            // First notification should go through
            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Error 1", "test-session"));
            Assert.AreEqual(1, mockService.RequestedNotifications.Count);

            // Same type within cooldown should be suppressed
            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionTimeout, "Error 2", "test-session"));
            Assert.AreEqual(1, mockService.RequestedNotifications.Count,
                "Same notification type should be suppressed during cooldown");

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_DifferentNotificationTypes_NotAffectedByCooldown()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);
            bridge.CooldownSeconds = 60f;

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Network error",
                "test-session"));
            eventHub.Publish(SessionError.Create(SessionErrorCodes.AudioMicUnavailable, "Mic error", "test-session"));

            Assert.AreEqual(2, mockService.RequestedNotifications.Count,
                "Different notification types should not affect each other's cooldown");
            Assert.AreEqual("NETWORK_REACHABILITY_ISSUE", mockService.RequestedNotifications[0].Id);
            Assert.AreEqual("NO_MICROPHONE_DETECTED", mockService.RequestedNotifications[1].Id);

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_ClearCooldowns_AllowsImmediateNotification()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);
            bridge.CooldownSeconds = 60f;

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Error 1", "test-session"));
            Assert.AreEqual(1, mockService.RequestedNotifications.Count);

            bridge.ClearCooldowns();

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Error 2", "test-session"));
            Assert.AreEqual(2, mockService.RequestedNotifications.Count,
                "After clearing cooldowns, same notification type should work");

            bridge.Dispose();
        }

        #endregion

        #region NotificationsEnabled Tests

        [Test]
        public void EventBridge_NotificationsDisabled_DoesNotNotify()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(false);

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Error", "test-session"));

            Assert.AreEqual(0, mockService.RequestedNotifications.Count,
                "Should not notify when notifications are disabled");

            bridge.Dispose();
        }

        [Test]
        public void EventBridge_RequestNotificationIfEnabled_RespectsSettings()
        {
            var mockService = new MockNotificationService();
            var eventHub = new EventHub(new ImmediateScheduler());
            SONotificationErrorMap map = CreateTestErrorMap();
            bool notificationsEnabled = false;
            var bridge = new ConvaiNotificationEventBridge(mockService, eventHub, () => notificationsEnabled, map);

            // Disabled - should not notify
            SONotification microphoneIssue = map.Rules.Find(rule => rule.Notification?.Id == "MICROPHONE_ISSUE")?.Notification;
            bridge.RequestNotificationIfEnabled(microphoneIssue);
            Assert.AreEqual(0, mockService.RequestedNotifications.Count);

            // Enable and try again
            notificationsEnabled = true;
            bridge.RequestNotificationIfEnabled(microphoneIssue);
            Assert.AreEqual(1, mockService.RequestedNotifications.Count);

            bridge.Dispose();
        }

        #endregion

        #region Dispose Tests

        [Test]
        public void EventBridge_AfterDispose_DoesNotReceiveEvents()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService mockService, EventHub eventHub) =
                CreateEventBridge(true);

            bridge.Dispose();

            eventHub.Publish(SessionError.Create(SessionErrorCodes.ConnectionNetworkError, "Error", "test-session"));

            Assert.AreEqual(0, mockService.RequestedNotifications.Count, "Should not receive events after dispose");
        }

        [Test]
        public void EventBridge_DoubleDispose_DoesNotThrow()
        {
            (ConvaiNotificationEventBridge bridge, MockNotificationService _, EventHub _) = CreateEventBridge(true);

            Assert.DoesNotThrow(() =>
            {
                bridge.Dispose();
                bridge.Dispose();
            });
        }

        #endregion
    }
}
