/// <summary>
///     ConvaiManager partial: WebGL-specific voice start arming, gesture consumption,
///     pointer/touch input detection, and UI overlay checks.
/// </summary>

using Convai.Domain.Logging;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Logging;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Convai.Runtime.Components
{
    public partial class ConvaiManager
    {
        private bool ShouldArmWebGLVoiceStart()
        {
            if (!CanArmWebGLVoiceStart(
                    _enableVoiceOnFirstSceneClickAfterConnectInWebGL,
                    UnityEngine.Application.platform,
                    TryGetRoomManager(out ConvaiRoomManager roomManager),
                    roomManager?.IsConnected ?? false,
                    roomManager?.RequiresUserGestureForAudio ?? false,
                    roomManager?.IsAudioPlaybackActive ?? false))
                return false;

            return true;
        }

        /// <summary>
        ///     Checked on each scene click instead of being decided once at connect: the browser can block
        ///     audio a moment after the room connects, when the character's first audio fails to autoplay.
        ///     A decision taken at connect then left every later click unable to start audio.
        /// </summary>
        private void TryConsumeWebGLVoiceStartGesture()
        {
            if (!_enableVoiceOnFirstSceneClickAfterConnectInWebGL ||
                UnityEngine.Application.platform != RuntimePlatform.WebGLPlayer) return;
            if (!WasScenePointerPressedThisFrame()) return;
            if (!ShouldConsumeWebGLVoiceStartGesture(true, IsPointerOverUI())) return;
            if (!ShouldArmWebGLVoiceStart()) return;

            if (_debugLogging)
            {
                ConvaiLogger.Debug(
                    "Consuming first WebGL scene interaction to enable audio playback and microphone capture.",
                    LogCategory.SDK);
            }

            EnableAudioAndStartListening();
        }

        private static bool CanArmWebGLVoiceStart(
            bool featureEnabled,
            RuntimePlatform platform,
            bool hasRoomManager,
            bool isConnected,
            bool requiresUserGestureForAudio,
            bool isAudioPlaybackActive)
        {
            return featureEnabled
                   && platform == RuntimePlatform.WebGLPlayer
                   && hasRoomManager
                   && isConnected
                   && requiresUserGestureForAudio
                   && !isAudioPlaybackActive;
        }

        private static bool ShouldConsumeWebGLVoiceStartGesture(bool pointerPressedThisFrame, bool pointerOverUi) =>
            pointerPressedThisFrame && !pointerOverUi;

        /// <summary>
        ///     A click or a tap anywhere in the scene is the gesture WebGL needs before it may start
        ///     audio. The shared input helper covers both backends and caches its lookups.
        /// </summary>
        private static bool WasScenePointerPressedThisFrame() =>
            ConvaiInputSystem.WasMouseButtonPressedThisFrame(0) ||
            ConvaiInputSystem.WasTouchStartedThisFrame();

        private static bool IsPointerOverUI()
        {
            EventSystem current = EventSystem.current;
            if (current == null) return false;

#if ENABLE_LEGACY_INPUT_MANAGER
            for (int i = 0; i < Input.touchCount; i++)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.phase == TouchPhase.Began && current.IsPointerOverGameObject(touch.fingerId)) return true;
            }
#endif

            return current.IsPointerOverGameObject();
        }
    }
}
