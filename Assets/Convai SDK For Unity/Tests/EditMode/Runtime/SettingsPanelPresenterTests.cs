using System;
using System.Collections.Generic;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Presentation.Services.Settings;
using Convai.Runtime.Room;
using Convai.Shared.Abstractions;
using Convai.Shared.Types;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class SettingsPanelPresenterTests
    {
        [Test]
        public void SettingsPanelPresenter_BuildsCorrectPatch_FromView()
        {
            var settingsService = new StubSettingsService(
                new ConvaiRuntimeSettingsSnapshot("Default", true, false, "Mic-A"));
            var panelController = new ConvaiSettingsPanelController();
            var microphoneService = new StubMicrophoneDeviceService(
                new ConvaiMicrophoneDevice("Mic-A", "Mic A", 0),
                new ConvaiMicrophoneDevice("Mic-B", "Mic B", 1));
            var view = new StubSettingsPanelView();

            using var presenter = new SettingsPanelPresenter(settingsService, panelController, microphoneService);
            presenter.Bind(view);

            view.PlayerDisplayNameInput = "Rishav";
            view.TranscriptEnabledInput = false;
            view.NotificationsEnabledInput = true;
            view.SelectedMicrophoneDeviceId = "Mic-B";

            panelController.Open();
            view.RaiseSaveRequested();

            Assert.IsNotNull(settingsService.LastPatch);
            Assert.AreEqual("Rishav", settingsService.LastPatch.PlayerDisplayName);
            Assert.AreEqual(false, settingsService.LastPatch.TranscriptEnabled);
            Assert.AreEqual(true, settingsService.LastPatch.NotificationsEnabled);
            Assert.AreEqual("Mic-B", settingsService.LastPatch.PreferredMicrophoneDeviceId);
            Assert.IsFalse(panelController.IsOpen);
        }

        [Test]
        public void SettingsPanelPresenter_RendersSnapshot()
        {
            var settingsService = new StubSettingsService(
                new ConvaiRuntimeSettingsSnapshot("Initial", true, false, "Mic-A"));
            var panelController = new ConvaiSettingsPanelController();
            var microphoneService = new StubMicrophoneDeviceService(
                new ConvaiMicrophoneDevice("Mic-A", "Mic A", 0),
                new ConvaiMicrophoneDevice("Mic-B", "Mic B", 1));
            var view = new StubSettingsPanelView();

            using var presenter = new SettingsPanelPresenter(settingsService, panelController, microphoneService);
            presenter.Bind(view);

            Assert.AreEqual("Initial", view.RenderedPlayerDisplayName);
            Assert.IsTrue(view.RenderedTranscriptEnabled);
            Assert.IsFalse(view.RenderedNotificationsEnabled);
            Assert.AreEqual("Mic-A", view.RenderedSelectedMicrophoneDeviceId);

            settingsService.EmitChange(new ConvaiRuntimeSettingsSnapshot(
                "Updated",
                false,
                true,
                "Mic-B"));

            Assert.AreEqual("Updated", view.RenderedPlayerDisplayName);
            Assert.IsFalse(view.RenderedTranscriptEnabled);
            Assert.IsTrue(view.RenderedNotificationsEnabled);
            Assert.AreEqual("Mic-B", view.RenderedSelectedMicrophoneDeviceId);
        }

        [Test]
        public void SettingsPanelPresenter_RendersEffectivePlayerName_OnBindAndOpen()
        {
            var settingsService = new StubSettingsService(
                new ConvaiRuntimeSettingsSnapshot("Player", true, false, string.Empty));
            var panelController = new ConvaiSettingsPanelController();
            var microphoneService = new StubMicrophoneDeviceService();
            var view = new StubSettingsPanelView();
            string effectiveName = "InspectorName";

            using var presenter = new SettingsPanelPresenter(
                settingsService,
                panelController,
                microphoneService,
                effectivePlayerNameProvider: () => effectiveName);
            presenter.Bind(view);

            Assert.AreEqual("InspectorName", view.RenderedPlayerDisplayName);

            effectiveName = "RuntimeName";
            panelController.Open();

            Assert.AreEqual("RuntimeName", view.RenderedPlayerDisplayName);
        }

        [Test]
        public void SettingsPanelPresenter_ShowsConversationMode_WhenRoomServiceAvailable()
        {
            var settingsService = new StubSettingsService(
                new ConvaiRuntimeSettingsSnapshot("Player", true, false, string.Empty));
            var panelController = new ConvaiSettingsPanelController();
            var microphoneService = new StubMicrophoneDeviceService();
            var roomConnectionService = new MockRoomConnectionService();
            var view = new StubSettingsPanelView();

            using var presenter = new SettingsPanelPresenter(
                settingsService,
                panelController,
                microphoneService,
                roomConnectionService);
            presenter.Bind(view);

            Assert.IsTrue(view.ConversationInputModeVisible);
            Assert.AreEqual(ConversationInputMode.HandsFree, view.RenderedConversationInputMode);
        }

        private sealed class StubSettingsPanelView : ISettingsPanelView
        {
            public string RenderedPlayerDisplayName { get; private set; }
            public bool RenderedTranscriptEnabled { get; private set; }
            public bool RenderedNotificationsEnabled { get; private set; }
            public string RenderedSelectedMicrophoneDeviceId { get; private set; } = string.Empty;
            public ConversationInputMode RenderedConversationInputMode { get; private set; }
            public bool ConversationInputModeVisible { get; private set; }

            public event Action SaveRequested;
            public event Action CloseRequested
            {
                add { }
                remove { }
            }

            public string PlayerDisplayNameInput { get; set; }
            public bool TranscriptEnabledInput { get; set; }
            public bool NotificationsEnabledInput { get; set; }
            public string SelectedMicrophoneDeviceId { get; set; } = string.Empty;
            public ConversationInputMode SelectedConversationInputModeInput { get; set; } = ConversationInputMode.HandsFree;

            public void SetPlayerDisplayName(string value) => RenderedPlayerDisplayName = value;

            public void SetTranscriptEnabled(bool value) => RenderedTranscriptEnabled = value;

            public void SetNotificationsEnabled(bool value) => RenderedNotificationsEnabled = value;

            public void SetMicrophoneOptions(IReadOnlyList<ConvaiMicrophoneDevice> devices, string selectedDeviceId) =>
                RenderedSelectedMicrophoneDeviceId = selectedDeviceId ?? string.Empty;

            public void SetConversationInputMode(ConversationInputMode mode) => RenderedConversationInputMode = mode;
            public void SetConversationInputModeVisible(bool visible) => ConversationInputModeVisible = visible;

            public void RaiseSaveRequested() => SaveRequested?.Invoke();
        }

        private sealed class StubSettingsService : IConvaiRuntimeSettingsService
        {
            public StubSettingsService(ConvaiRuntimeSettingsSnapshot current)
            {
                Current = current;
            }

            public ConvaiRuntimeSettingsPatch LastPatch { get; private set; }

            public event Action<ConvaiRuntimeSettingsChanged> Changed;

            public ConvaiRuntimeSettingsSnapshot Current { get; private set; }

            public ConvaiRuntimeSettingsApplyResult Apply(ConvaiRuntimeSettingsPatch patch)
            {
                LastPatch = patch;
                Current = Current.With(
                    patch?.PlayerDisplayName,
                    patch?.TranscriptEnabled,
                    patch?.NotificationsEnabled,
                    patch?.PreferredMicrophoneDeviceId);

                return ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.All);
            }

            public ConvaiRuntimeSettingsApplyResult ResetToDefaults() =>
                ConvaiRuntimeSettingsApplyResult.Ok(Current, ConvaiRuntimeSettingsChangeMask.None);

            public void EmitChange(ConvaiRuntimeSettingsSnapshot next)
            {
                ConvaiRuntimeSettingsSnapshot previous = Current;
                Current = next;
                Changed?.Invoke(new ConvaiRuntimeSettingsChanged(previous, next, ConvaiRuntimeSettingsChangeMask.All));
            }
        }

        private sealed class StubMicrophoneDeviceService : IMicrophoneDeviceService
        {
            private readonly IReadOnlyList<ConvaiMicrophoneDevice> _devices;

            public StubMicrophoneDeviceService(params ConvaiMicrophoneDevice[] devices)
            {
                _devices = devices ?? Array.Empty<ConvaiMicrophoneDevice>();
            }

            public IReadOnlyList<ConvaiMicrophoneDevice> GetAvailableDevices() => _devices;

            public ConvaiMicrophoneDevice ResolvePreferredDevice(string preferredDeviceId) =>
                _devices.Count > 0 ? _devices[0] : ConvaiMicrophoneDevice.None;

            public string ResolvePreferredDeviceId(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Id;

            public int ResolvePreferredDeviceIndex(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Index;

            public bool TryResolveDeviceId(string deviceId, out ConvaiMicrophoneDevice device)
            {
                for (int i = 0; i < _devices.Count; i++)
                {
                    if (string.Equals(_devices[i].Id, deviceId, StringComparison.Ordinal))
                    {
                        device = _devices[i];
                        return true;
                    }
                }

                device = ConvaiMicrophoneDevice.None;
                return false;
            }
        }
    }
}
