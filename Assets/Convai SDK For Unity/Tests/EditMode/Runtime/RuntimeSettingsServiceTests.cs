using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Runtime;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Settings;
using Convai.Shared.Abstractions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public class RuntimeSettingsServiceTests
    {
        [Test]
        public void RuntimeSettingsService_Loads_Defaults_Then_Overrides()
        {
            var defaults = ScriptableObject.CreateInstance<ConvaiSettings>();
            try
            {
                var store = new InMemoryRuntimeSettingsStore
                {
                    LoadedOverrides = new ConvaiRuntimeSettingsOverrides
                    {
                        PlayerDisplayName = "Risha",
                        TranscriptEnabled = false,
                        NotificationsEnabled = true,
                        PreferredMicrophoneDeviceId = "Mic-B"
                    }
                };

                var microphoneService = new StubMicrophoneDeviceService(
                    new ConvaiMicrophoneDevice("Mic-A", "Mic A", 0),
                    new ConvaiMicrophoneDevice("Mic-B", "Mic B", 1));

                var service = new ConvaiRuntimeSettingsService(defaults, store, microphoneService);

                Assert.AreEqual("Risha", service.Current.PlayerDisplayName);
                Assert.IsFalse(service.Current.TranscriptEnabled);
                Assert.IsTrue(service.Current.NotificationsEnabled);
                Assert.AreEqual("Mic-B", service.Current.PreferredMicrophoneDeviceId);
            }
            finally
            {
                Object.DestroyImmediate(defaults);
            }
        }

        [Test]
        public void RuntimeSettingsService_Apply_IsAtomic_And_EmitsSingleChange()
        {
            var defaults = ScriptableObject.CreateInstance<ConvaiSettings>();
            try
            {
                var store = new InMemoryRuntimeSettingsStore();
                var microphoneService = new StubMicrophoneDeviceService(
                    new ConvaiMicrophoneDevice("Mic-A", "Mic A", 0),
                    new ConvaiMicrophoneDevice("Mic-B", "Mic B", 1));

                var service = new ConvaiRuntimeSettingsService(defaults, store, microphoneService);
                int eventCount = 0;
                var mask = ConvaiRuntimeSettingsChangeMask.None;
                service.Changed += changed =>
                {
                    eventCount++;
                    mask = changed.Mask;
                };

                int saveCountBeforeApply = store.SaveCount;

                ConvaiRuntimeSettingsApplyResult result = service.Apply(new ConvaiRuntimeSettingsPatch
                {
                    PlayerDisplayName = "Updated Name",
                    TranscriptEnabled = false,
                    NotificationsEnabled = true,
                    PreferredMicrophoneDeviceId = "Mic-B"
                });

                Assert.IsTrue(result.Success);
                Assert.AreEqual(1, eventCount);
                Assert.AreEqual(saveCountBeforeApply + 1, store.SaveCount);
                Assert.IsTrue(mask.HasFlag(ConvaiRuntimeSettingsChangeMask.PlayerDisplayName));
                Assert.IsTrue(mask.HasFlag(ConvaiRuntimeSettingsChangeMask.TranscriptEnabled));
                Assert.IsTrue(mask.HasFlag(ConvaiRuntimeSettingsChangeMask.NotificationsEnabled));
                Assert.IsTrue(mask.HasFlag(ConvaiRuntimeSettingsChangeMask.PreferredMicrophoneDeviceId));
            }
            finally
            {
                Object.DestroyImmediate(defaults);
            }
        }

        [Test]
        public void RuntimeSettingsService_Normalizes_InvalidMicDevice_ToFallback_AndPersists()
        {
            var defaults = ScriptableObject.CreateInstance<ConvaiSettings>();
            try
            {
                SetPrivateField(defaults, "_defaultMicrophoneDeviceId", "Mic-B");

                var store = new InMemoryRuntimeSettingsStore
                {
                    LoadedOverrides = new ConvaiRuntimeSettingsOverrides
                    {
                        PreferredMicrophoneDeviceId = "UnknownMic"
                    }
                };

                var microphoneService = new StubMicrophoneDeviceService(
                    new ConvaiMicrophoneDevice("Mic-A", "Mic A", 0),
                    new ConvaiMicrophoneDevice("Mic-B", "Mic B", 1));

                var service = new ConvaiRuntimeSettingsService(defaults, store, microphoneService);

                Assert.AreEqual("Mic-A", service.Current.PreferredMicrophoneDeviceId);
                Assert.IsNotNull(store.LastSavedOverrides);
                Assert.AreEqual("Mic-A", store.LastSavedOverrides.PreferredMicrophoneDeviceId);
            }
            finally
            {
                Object.DestroyImmediate(defaults);
            }
        }

        [Test]
        public void SettingsPanelController_OpenCloseToggle_EmitsVisibilityState()
        {
            var controller = new ConvaiSettingsPanelController();
            var states = new List<bool>();
            controller.VisibilityChanged += isOpen => states.Add(isOpen);

            controller.Open();
            controller.Open();
            controller.Close();
            controller.Toggle();

            CollectionAssert.AreEqual(new[] { true, false, true }, states);
            Assert.IsTrue(controller.IsOpen);
        }

        private static void SetPrivateField(ConvaiSettings settings, string fieldName, object value)
        {
            FieldInfo field =
                typeof(ConvaiSettings).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            field?.SetValue(settings, value);
        }

        private sealed class InMemoryRuntimeSettingsStore : IConvaiRuntimeSettingsStore
        {
            public ConvaiRuntimeSettingsOverrides LoadedOverrides { get; set; }
            public ConvaiRuntimeSettingsOverrides LastSavedOverrides { get; private set; }
            public int SaveCount { get; private set; }

            public ConvaiRuntimeSettingsOverrides LoadOverrides() => Clone(LoadedOverrides);

            public void SaveOverrides(ConvaiRuntimeSettingsOverrides overrides)
            {
                SaveCount++;
                LastSavedOverrides = Clone(overrides);
            }

            public void ClearOverrides()
            {
                SaveCount++;
                LastSavedOverrides = null;
            }

            private static ConvaiRuntimeSettingsOverrides Clone(ConvaiRuntimeSettingsOverrides source)
            {
                if (source == null) return null;

                return new ConvaiRuntimeSettingsOverrides
                {
                    PlayerDisplayName = source.PlayerDisplayName,
                    TranscriptEnabled = source.TranscriptEnabled,
                    NotificationsEnabled = source.NotificationsEnabled,
                    PreferredMicrophoneDeviceId = source.PreferredMicrophoneDeviceId
                };
            }
        }

        private sealed class StubMicrophoneDeviceService : IMicrophoneDeviceService
        {
            private readonly List<ConvaiMicrophoneDevice> _devices;

            public StubMicrophoneDeviceService(params ConvaiMicrophoneDevice[] devices)
            {
                _devices = new List<ConvaiMicrophoneDevice>(devices ?? Array.Empty<ConvaiMicrophoneDevice>());
            }

            public IReadOnlyList<ConvaiMicrophoneDevice> GetAvailableDevices() => _devices;

            public ConvaiMicrophoneDevice ResolvePreferredDevice(string preferredDeviceId)
            {
                if (_devices.Count == 0) return ConvaiMicrophoneDevice.None;

                if (!string.IsNullOrWhiteSpace(preferredDeviceId))
                {
                    for (int i = 0; i < _devices.Count; i++)
                        if (string.Equals(_devices[i].Id, preferredDeviceId, StringComparison.Ordinal))
                            return _devices[i];
                }

                return _devices[0];
            }

            public string ResolvePreferredDeviceId(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Id;

            public int ResolvePreferredDeviceIndex(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Index;

            public bool TryResolveDeviceId(string deviceId, out ConvaiMicrophoneDevice device)
            {
                device = ConvaiMicrophoneDevice.None;
                if (string.IsNullOrWhiteSpace(deviceId)) return false;

                for (int i = 0; i < _devices.Count; i++)
                {
                    if (string.Equals(_devices[i].Id, deviceId, StringComparison.Ordinal))
                    {
                        device = _devices[i];
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
