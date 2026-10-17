using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Domain.Logging;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Convai.Runtime.Presentation.Views.Transcript
{
    /// <summary>
    ///     Controls the visual appearance of a microphone indicator and allows VR clicking
    ///     to toggle microphone recording ON/OFF (Muted).
    /// </summary>
    public class PlayerSpeakingIndicator : MonoBehaviour, IPointerClickHandler
    {
        [Header("UI References")]
        [SerializeField]
        [Tooltip("The Image component that indicates microphone/speaking state. If not assigned, will try to find on this GameObject.")]
        private Image microphoneIndicator;

        [SerializeField]
        [Tooltip("Optional TextMeshPro label for displaying live status (e.g., StatusText)")]
        private TextMeshProUGUI statusText;

        [Header("VR Mic Toggle & Colors")]
        [SerializeField]
        [Tooltip("Allow VR laser pointer click to toggle mic state")]
        private bool enableClickToToggle = true;

        [SerializeField]
        [Tooltip("Start muted on load to conserve Convai interaction credits")]
        private bool startMuted = true;

        [SerializeField]
        [Tooltip("Icon color when microphone is active and listening")]
        private Color micActiveColor = new Color(0.12f, 0.75f, 0.38f, 1f); // Green (#20C060)

        [SerializeField]
        [Tooltip("Icon color when microphone is muted")]
        private Color micMutedColor = new Color(0.85f, 0.22f, 0.22f, 1f);  // Red (#D93838)

        [Header("Alpha Settings")]
        [SerializeField]
        [Tooltip("Alpha value when player is speaking (0-1)")]
        [Range(0f, 1f)]
        private float speakingAlpha = 1f;

        [SerializeField]
        [Tooltip("Alpha value when player is silent (0-1)")]
        [Range(0f, 1f)]
        private float silentAlpha = 0.5f;

        [Header("Animation")]
        [SerializeField]
        [Tooltip("Enable smooth transition between alpha values")]
        private bool enableSmoothTransition = true;

        [SerializeField]
        [Tooltip("Speed of the alpha transition")]
        private float transitionSpeed = 8f;

        private IEventHub _eventHub;
        private bool _isSubscribed;
        private SubscriptionToken _subscriptionToken;
        private float _targetAlpha;
        private Button _buttonComponent;

        /// <summary>
        ///     Gets whether the player is currently speaking.
        /// </summary>
        public bool IsSpeaking { get; private set; }

        /// <summary>
        ///     Gets whether the microphone is currently muted.
        /// </summary>
        public bool IsMuted { get; private set; }

        private void Awake()
        {
            if (microphoneIndicator == null)
            {
                microphoneIndicator = GetComponent<Image>();
            }

            // Auto-locate StatusText if left unassigned in the inspector
            if (statusText == null)
            {
                var parentContainer = transform.parent != null ? transform.parent.parent : null;
                if (parentContainer != null)
                {
                    var statusContainer = parentContainer.Find("StatusContainer");
                    if (statusContainer != null)
                    {
                        statusText = statusContainer.Find("StatusText")?.GetComponent<TextMeshProUGUI>();
                    }
                }
            }

            // Hook up standard Button component if present
            _buttonComponent = GetComponent<Button>();
            if (_buttonComponent != null)
            {
                _buttonComponent.onClick.RemoveListener(ToggleMicrophone);
                _buttonComponent.onClick.AddListener(ToggleMicrophone);
            }

            IsMuted = startMuted;
        }

        private void Start()
        {
            TryResolveDependencies();
            SubscribeToEvents();

            // Initialize mute state and colors
            ApplyMuteState(IsMuted);
            UpdateVisuals();
        }

        private void Update()
        {
            if (microphoneIndicator == null) return;

            if (enableSmoothTransition)
            {
                float currentAlpha = microphoneIndicator.color.a;
                float newAlpha = Mathf.Lerp(currentAlpha, _targetAlpha, Time.deltaTime * transitionSpeed);
                SetColorAndAlpha(newAlpha);
            }
            else
            {
                SetColorAndAlpha(_targetAlpha);
            }
        }

        /// <summary>
        ///     Triggered when the VR controller laser points at this icon and pulls the Index Trigger.
        /// </summary>
        public void OnPointerClick(PointerEventData eventData)
        {
            if (enableClickToToggle)
            {
                ToggleMicrophone();
            }
        }

        public void ToggleMicrophone()
        {
            IsMuted = !IsMuted;
            ApplyMuteState(IsMuted);
            UpdateVisuals();

            Debug.Log($"<color={(IsMuted ? "red" : "green")}>[Convai VR] Microphone toggled to: {(IsMuted ? "MUTED (Quota Safe)" : "ACTIVE (Listening)")}</color>");
        }

        private void UpdateVisuals()
        {
            if (statusText != null)
            {
                statusText.text = IsMuted ? "MIC: MUTED" : "MIC: ON";
                statusText.color = IsMuted ? micMutedColor : micActiveColor;
            }

            UpdateIndicator(IsSpeaking);
        }

        private void UpdateIndicator(bool isSpeaking)
        {
            if (IsMuted)
            {
                _targetAlpha = 1f; // Solid red when muted
            }
            else
            {
                _targetAlpha = isSpeaking ? speakingAlpha : silentAlpha;
            }

            if (!enableSmoothTransition && microphoneIndicator != null)
            {
                SetColorAndAlpha(_targetAlpha);
            }
        }

        private void SetColorAndAlpha(float alpha)
        {
            if (microphoneIndicator == null) return;

            Color baseColor = IsMuted ? micMutedColor : micActiveColor;
            microphoneIndicator.color = new Color(baseColor.r, baseColor.g, baseColor.b, alpha);
        }

        private void ApplyMuteState(bool mute)
        {
            bool applied = false;

            // 1. Dynamic lookup on ConvaiManager.ActiveManager
            try
            {
                var manager = ConvaiManager.ActiveManager;
                if (manager != null)
                {
                    var props = manager.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance);
                    foreach (var prop in props)
                    {
                        if (prop.Name.Contains("Audio") || prop.Name.Contains("Room"))
                        {
                            var subService = prop.GetValue(manager);
                            if (subService != null)
                            {
                                var method = subService.GetType().GetMethod("SetMicMuted", BindingFlags.Public | BindingFlags.Instance)
                                          ?? subService.GetType().GetMethod("MuteMicrophone", BindingFlags.Public | BindingFlags.Instance);
                                if (method != null)
                                {
                                    method.Invoke(subService, new object[] { mute });
                                    applied = true;
                                    break;
                                }
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. Component lookup for ConvaiRoomManager / AudioTrackManager
            if (!applied)
            {
                MonoBehaviour[] components = FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    string typeName = comp.GetType().Name;

                    if (typeName.Contains("ConvaiRoomManager") || typeName.Contains("AudioTrackManager") || typeName.Contains("ConvaiInput"))
                    {
                        MethodInfo method = comp.GetType().GetMethod("SetMicMuted", BindingFlags.Public | BindingFlags.Instance)
                                         ?? comp.GetType().GetMethod("MuteMicrophone", BindingFlags.Public | BindingFlags.Instance)
                                         ?? comp.GetType().GetMethod("ToggleMute", BindingFlags.Public | BindingFlags.Instance);

                        if (method != null)
                        {
                            var parameters = method.GetParameters();
                            if (parameters.Length == 1 && parameters[0].ParameterType == typeof(bool))
                            {
                                bool argument = method.Name.ToLower().Contains("mute") ? mute : !mute;
                                method.Invoke(comp, new object[] { argument });
                                applied = true;
                                break;
                            }
                        }
                    }
                }
            }

            // 3. Hardware audio fallback
            AudioListener.pause = mute;
        }

        private void OnEnable()
        {
            TryResolveDependencies();
            if (_eventHub != null && !_isSubscribed) SubscribeToEvents();
        }

        private void OnDisable() => UnsubscribeFromEvents();

        private void OnDestroy() => UnsubscribeFromEvents();

        public void Inject(IEventHub eventHub)
        {
            if (_eventHub == eventHub) return;

            UnsubscribeFromEvents();
            _eventHub = eventHub;

            if (isActiveAndEnabled) SubscribeToEvents();
        }

        private void TryResolveDependencies()
        {
            if (_eventHub != null) return;

            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager == null) return;

            if (manager.TryGetEventHub(out IEventHub eventHub)) Inject(eventHub);
        }

        private void SubscribeToEvents()
        {
            if (_isSubscribed) return;

            if (_eventHub == null)
            {
                ConvaiLogger.Warning(
                    "IEventHub not available. Ensure ConvaiManager has initialized before this component.",
                    LogCategory.UI);
                return;
            }

            _subscriptionToken = _eventHub.Subscribe<PlayerSpeakingStateChanged>(OnPlayerSpeakingStateChanged);
            _isSubscribed = true;

            UpdateIndicator(false);
        }

        private void UnsubscribeFromEvents()
        {
            if (!_isSubscribed || _eventHub == null) return;

            _eventHub.Unsubscribe(_subscriptionToken);
            _subscriptionToken = default;
            _isSubscribed = false;
        }

        private void OnPlayerSpeakingStateChanged(PlayerSpeakingStateChanged eventData)
        {
            IsSpeaking = eventData.IsSpeaking;
            UpdateIndicator(IsSpeaking);
        }

        public void SetSpeakingState(bool isSpeaking)
        {
            IsSpeaking = isSpeaking;
            UpdateIndicator(isSpeaking);
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (microphoneIndicator != null && !UnityEngine.Application.isPlaying)
            {
                microphoneIndicator.color = startMuted ? micMutedColor : micActiveColor;
            }
        }

        [ContextMenu("Test - Toggle Mic")]
        private void TestToggleMic() => ToggleMicrophone();
#endif
    }
}
