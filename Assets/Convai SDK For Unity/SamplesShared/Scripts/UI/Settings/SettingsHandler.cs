using System;
using System.Collections.Generic;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.Persistence;
using Convai.Runtime.Presentation.Services;
using Convai.Runtime.Presentation.Views.Settings;
using Convai.Runtime.Room;
using Convai.Runtime.Settings;
using Convai.Shared.Abstractions;
using Convai.Shared.Types;
using UnityEngine;

namespace Convai.Sample.UI.Settings
{
    /// <summary>
    ///     Sample settings handler that spawns and injects the settings panel view.
    /// </summary>
    public class SettingsHandler : MonoBehaviour
    {
        [SerializeField] private SettingsPanel settingsPanelPrefab;

        private IMicrophoneDeviceService _microphoneDeviceService;
        private Func<string> _effectivePlayerNameProvider;
        private bool _explicitInjectionSelected;
        private bool _fallbackSelected;
        private bool _hostModeLogged;
        private ConvaiManager _manager;

        private SettingsPanel _panel;

        private IConvaiSettingsPanelController _panelController;
        private bool _panelInjected;
        private IConvaiRoomConnectionService _roomConnectionService;
        private IConvaiRuntimeSettingsService _runtimeSettingsService;
        private IConvaiSettingsPanelController _subscribedPanelController;
        private bool _cursorStateCaptured;
        private CursorLockMode _cursorLockModeBeforeSettings;
        private bool _cursorVisibleBeforeSettings;

        private void Awake()
        {
            if (settingsPanelPrefab == null)
            {
                Debug.LogWarning("[SettingsHandler] Settings panel prefab is not assigned.");
                return;
            }

            _panel = Instantiate(settingsPanelPrefab, transform);
            TryResolveManagerServices();
            TryInitialize();
        }

        private void Start()
        {
            if (TryInitialize()) return;

            if (TryResolveManagerServices())
            {
                TryInitialize();
                return;
            }

            if (_manager != null) return;

            EnsureFallbackServices();
            TryInitialize();
        }

        private void Update()
        {
            if (_panelInjected || TryInitialize()) return;

            if (TryResolveManagerServices())
            {
                TryInitialize();
                return;
            }

            if (_manager == null)
            {
                EnsureFallbackServices();
                TryInitialize();
            }
        }

        private void LateUpdate()
        {
            // A gameplay controller can run after the panel opens and lock the cursor again.
            // Keep runtime settings interactive for as long as the shared controller is open.
            if (_panelController?.IsOpen != true) return;

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void OnEnable()
        {
            if (_panelInjected)
                SubscribeToPanelVisibility(_panelController);
        }

        private void OnDisable()
        {
            SubscribeToPanelVisibility(null);
        }

        private void OnDestroy()
        {
            SubscribeToPanelVisibility(null);
        }

        public void Inject(
            IConvaiSettingsPanelController panelController = null,
            IConvaiRuntimeSettingsService runtimeSettingsService = null,
            IMicrophoneDeviceService microphoneDeviceService = null,
            IConvaiRoomConnectionService roomConnectionService = null)
        {
            _explicitInjectionSelected = true;
            _panelController = panelController;
            _runtimeSettingsService = runtimeSettingsService;
            _microphoneDeviceService = microphoneDeviceService;
            _roomConnectionService = roomConnectionService ?? _roomConnectionService;
            _effectivePlayerNameProvider = _manager != null
                ? () => _manager?.Player?.PlayerName
                : () => FindAnyObjectByType<ConvaiPlayer>()?.PlayerName;
            _panelInjected = false;
            TryInitialize();
        }

        private bool TryResolveManagerServices()
        {
            _manager = FindAnyObjectByType<ConvaiManager>();
            if (_manager == null) return false;

            _manager.TryGetSettingsPanelController(out IConvaiSettingsPanelController panelController);
            _manager.TryGetRuntimeSettingsService(out IConvaiRuntimeSettingsService runtimeSettingsService);
            _manager.TryGetMicrophoneDeviceService(out IMicrophoneDeviceService microphoneDeviceService);
            _manager.TryGetRoomConnectionService(out IConvaiRoomConnectionService roomConnectionService);

            if (panelController == null ||
                runtimeSettingsService == null ||
                microphoneDeviceService == null ||
                roomConnectionService == null)
                return false;

            IConvaiSettingsPanelController resolvedPanelController =
                _explicitInjectionSelected && _panelController != null ? _panelController : panelController;
            IConvaiRuntimeSettingsService resolvedRuntimeSettingsService =
                _explicitInjectionSelected && _runtimeSettingsService != null
                    ? _runtimeSettingsService
                    : runtimeSettingsService;
            IMicrophoneDeviceService resolvedMicrophoneDeviceService =
                _explicitInjectionSelected && _microphoneDeviceService != null
                    ? _microphoneDeviceService
                    : microphoneDeviceService;
            IConvaiRoomConnectionService resolvedRoomConnectionService =
                _explicitInjectionSelected && _roomConnectionService != null
                    ? _roomConnectionService
                    : roomConnectionService;

            if (!ReferenceEquals(_panelController, resolvedPanelController) ||
                !ReferenceEquals(_runtimeSettingsService, resolvedRuntimeSettingsService) ||
                !ReferenceEquals(_microphoneDeviceService, resolvedMicrophoneDeviceService) ||
                !ReferenceEquals(_roomConnectionService, resolvedRoomConnectionService))
                _panelInjected = false;

            _panelController = resolvedPanelController;
            _runtimeSettingsService = resolvedRuntimeSettingsService;
            _microphoneDeviceService = resolvedMicrophoneDeviceService;
            _roomConnectionService = resolvedRoomConnectionService;

            _effectivePlayerNameProvider = () => _manager?.Player?.PlayerName;

            if (!_hostModeLogged)
            {
                Debug.Log("[SettingsHandler] Using ConvaiManager runtime services.");
                _hostModeLogged = true;
            }

            return true;
        }

        private bool TryInitialize()
        {
            if (_panel == null ||
                _panelController == null ||
                _runtimeSettingsService == null ||
                _microphoneDeviceService == null ||
                (_manager != null && _roomConnectionService == null))
                return false;

            if (!_panelInjected)
            {
                _panel.Inject(
                    _panelController,
                    _runtimeSettingsService,
                    _microphoneDeviceService,
                    _roomConnectionService,
                    effectivePlayerNameProvider: _effectivePlayerNameProvider);
                _panelInjected = true;
            }

            if (isActiveAndEnabled)
                SubscribeToPanelVisibility(_panelController);

            return true;
        }

        private void SubscribeToPanelVisibility(IConvaiSettingsPanelController panelController)
        {
            if (ReferenceEquals(_subscribedPanelController, panelController)) return;

            if (_subscribedPanelController != null)
                _subscribedPanelController.VisibilityChanged -= HandlePanelVisibilityChanged;

            RestoreCursorState();
            _subscribedPanelController = panelController;

            if (_subscribedPanelController == null) return;

            _subscribedPanelController.VisibilityChanged += HandlePanelVisibilityChanged;
            HandlePanelVisibilityChanged(_subscribedPanelController.IsOpen);
        }

        private void HandlePanelVisibilityChanged(bool isOpen)
        {
            if (!isOpen)
            {
                RestoreCursorState();
                return;
            }

            if (!_cursorStateCaptured)
            {
                _cursorLockModeBeforeSettings = Cursor.lockState;
                _cursorVisibleBeforeSettings = Cursor.visible;
                _cursorStateCaptured = true;
            }

            Cursor.lockState = CursorLockMode.None;
            Cursor.visible = true;
        }

        private void RestoreCursorState()
        {
            if (!_cursorStateCaptured) return;

            Cursor.lockState = _cursorLockModeBeforeSettings;
            Cursor.visible = _cursorVisibleBeforeSettings;
            _cursorStateCaptured = false;
        }

        private void EnsureFallbackServices()
        {
            _panelController ??= new ConvaiSettingsPanelController();
            _microphoneDeviceService ??= new SampleMicrophoneDeviceService();
            _runtimeSettingsService ??= new ConvaiRuntimeSettingsService(
                ConvaiSettings.Instance,
                new ConvaiRuntimeSettingsStore(new PlayerPrefsKeyValueStore()),
                _microphoneDeviceService);
            _effectivePlayerNameProvider = () => FindAnyObjectByType<ConvaiPlayer>()?.PlayerName;

            if (!_fallbackSelected)
            {
                Debug.LogWarning(
                    "[SettingsHandler] No ConvaiManager found; using standalone sample settings services.");
                _fallbackSelected = true;
            }
        }

        public void TogglePanel()
        {
            bool managerResolved = TryResolveManagerServices();
            if (!managerResolved && _manager == null && !TryInitialize())
                EnsureFallbackServices();

            TryInitialize();

            _panelController?.Toggle();
        }

        private sealed class SampleMicrophoneDeviceService : IMicrophoneDeviceService
        {
            public IReadOnlyList<ConvaiMicrophoneDevice> GetAvailableDevices()
            {
#if UNITY_WEBGL && !UNITY_EDITOR
                return Array.Empty<ConvaiMicrophoneDevice>();
#else
                string[] names = Microphone.devices ?? Array.Empty<string>();
                if (names.Length == 0) return Array.Empty<ConvaiMicrophoneDevice>();

                var idsByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var devices = new List<ConvaiMicrophoneDevice>(names.Length);

                for (int i = 0; i < names.Length; i++)
                {
                    string displayName = string.IsNullOrWhiteSpace(names[i]) ? $"Microphone {i + 1}" : names[i].Trim();
                    if (!idsByName.TryGetValue(displayName, out int count)) count = 0;

                    count++;
                    idsByName[displayName] = count;

                    string id = count == 1 ? displayName : $"{displayName}#{count}";
                    devices.Add(new ConvaiMicrophoneDevice(id, displayName, i));
                }

                return devices;
#endif
            }

            public ConvaiMicrophoneDevice ResolvePreferredDevice(string preferredDeviceId)
            {
                IReadOnlyList<ConvaiMicrophoneDevice> devices = GetAvailableDevices();
                if (devices.Count == 0) return ConvaiMicrophoneDevice.None;

                if (TryResolveDeviceId(preferredDeviceId, out ConvaiMicrophoneDevice resolved))
                    return resolved;

                return devices[0];
            }

            public string ResolvePreferredDeviceId(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Id;

            public int ResolvePreferredDeviceIndex(string preferredDeviceId) =>
                ResolvePreferredDevice(preferredDeviceId).Index;

            public bool TryResolveDeviceId(string deviceId, out ConvaiMicrophoneDevice device)
            {
                IReadOnlyList<ConvaiMicrophoneDevice> devices = GetAvailableDevices();
                device = ConvaiMicrophoneDevice.None;

                if (devices.Count == 0 || string.IsNullOrWhiteSpace(deviceId)) return false;

                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].Id, deviceId, StringComparison.Ordinal))
                    {
                        device = devices[i];
                        return true;
                    }
                }

                for (int i = 0; i < devices.Count; i++)
                {
                    if (string.Equals(devices[i].Id, deviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        device = devices[i];
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
