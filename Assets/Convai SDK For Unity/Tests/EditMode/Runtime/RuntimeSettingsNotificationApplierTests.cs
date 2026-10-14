using System;
using Convai.Runtime.Presentation.Views.Notifications;
using Convai.Runtime.Settings;
using Convai.Shared.Abstractions;
using Convai.Shared.Interfaces;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class RuntimeSettingsNotificationApplierTests
    {
        [Test]
        public void OnSettingsChanged_NotificationsDisabled_DismissesNotification()
        {
            var settingsService = new StubRuntimeSettingsService(CreateSnapshot(true));
            var notificationService = new StubNotificationService();

            using var applier = new RuntimeSettingsNotificationApplier(settingsService, notificationService);

            ConvaiRuntimeSettingsSnapshot previous = settingsService.Current;
            ConvaiRuntimeSettingsSnapshot current = previous.With(notificationsEnabled: false);
            settingsService.RaiseChanged(new ConvaiRuntimeSettingsChanged(
                previous,
                current,
                ConvaiRuntimeSettingsChangeMask.NotificationsEnabled));

            Assert.AreEqual(1, notificationService.DismissCount);
        }

        [Test]
        public void OnSettingsChanged_MaskExcludesNotificationsEnabled_DoesNotDismissNotification()
        {
            var settingsService = new StubRuntimeSettingsService(CreateSnapshot(true));
            var notificationService = new StubNotificationService();

            using var applier = new RuntimeSettingsNotificationApplier(settingsService, notificationService);

            ConvaiRuntimeSettingsSnapshot previous = settingsService.Current;
            ConvaiRuntimeSettingsSnapshot current = previous.With(notificationsEnabled: false);
            settingsService.RaiseChanged(new ConvaiRuntimeSettingsChanged(
                previous,
                current,
                ConvaiRuntimeSettingsChangeMask.TranscriptEnabled));

            Assert.AreEqual(0, notificationService.DismissCount);
        }

        [Test]
        public void OnSettingsChanged_NotificationsRemainEnabled_DoesNotDismissNotification()
        {
            var settingsService = new StubRuntimeSettingsService(CreateSnapshot(true));
            var notificationService = new StubNotificationService();

            using var applier = new RuntimeSettingsNotificationApplier(settingsService, notificationService);

            ConvaiRuntimeSettingsSnapshot previous = settingsService.Current;
            ConvaiRuntimeSettingsSnapshot current =
                previous.With(notificationsEnabled: true, playerDisplayName: "Updated");
            settingsService.RaiseChanged(new ConvaiRuntimeSettingsChanged(
                previous,
                current,
                ConvaiRuntimeSettingsChangeMask.NotificationsEnabled |
                ConvaiRuntimeSettingsChangeMask.PlayerDisplayName));

            Assert.AreEqual(0, notificationService.DismissCount);
        }

        [Test]
        public void Dispose_UnsubscribesFromSettingsChanged()
        {
            var settingsService = new StubRuntimeSettingsService(CreateSnapshot(true));
            var notificationService = new StubNotificationService();
            var applier = new RuntimeSettingsNotificationApplier(settingsService, notificationService);

            applier.Dispose();

            ConvaiRuntimeSettingsSnapshot previous = settingsService.Current;
            ConvaiRuntimeSettingsSnapshot current = previous.With(notificationsEnabled: false);
            settingsService.RaiseChanged(new ConvaiRuntimeSettingsChanged(
                previous,
                current,
                ConvaiRuntimeSettingsChangeMask.NotificationsEnabled));

            Assert.AreEqual(0, notificationService.DismissCount);
        }

        private static ConvaiRuntimeSettingsSnapshot CreateSnapshot(bool notificationsEnabled)
        {
            return new ConvaiRuntimeSettingsSnapshot(
                "Player",
                true,
                notificationsEnabled,
                string.Empty);
        }

        private sealed class StubRuntimeSettingsService : IConvaiRuntimeSettingsService
        {
            public StubRuntimeSettingsService(ConvaiRuntimeSettingsSnapshot initial)
            {
                Current = initial;
            }

            public event Action<ConvaiRuntimeSettingsChanged> Changed;

            public ConvaiRuntimeSettingsSnapshot Current { get; private set; }

            public ConvaiRuntimeSettingsApplyResult Apply(ConvaiRuntimeSettingsPatch patch) =>
                ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.None);

            public ConvaiRuntimeSettingsApplyResult ResetToDefaults() =>
                ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.None);

            public void RaiseChanged(ConvaiRuntimeSettingsChanged changed)
            {
                Current = changed.Current;
                Changed?.Invoke(changed);
            }
        }

        private sealed class StubNotificationService : IConvaiNotificationService
        {
            public int DismissCount { get; private set; }

            public event Action<SONotification> OnNotificationRequested;
            public event Action OnNotificationDismissed;

            public void RequestNotification(SONotification notificationType) =>
                OnNotificationRequested?.Invoke(notificationType);

            public void DismissNotification()
            {
                DismissCount++;
                OnNotificationDismissed?.Invoke();
            }
        }
    }
}
