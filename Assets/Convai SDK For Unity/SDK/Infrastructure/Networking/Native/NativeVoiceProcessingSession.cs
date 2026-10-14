using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using UnityEngine;

namespace Convai.Infrastructure.Networking.Native
{
    internal sealed class NativeVoiceProcessingSession : IDisposable
    {
        private bool _isActive;

        public bool SupportsRouteMonitoring
        {
            get
            {
#if (UNITY_ANDROID || UNITY_IOS) && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        ///     Completes once the session entered by <see cref="Start" /> has finished switching, or the wait timed
        ///     out, failed, or the session was stopped. Already complete where <see cref="Start" /> applies
        ///     synchronously (iOS, Editor, desktop). Continuations run asynchronously.
        /// </summary>
        public Task WhenSettled
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return _androidHold?.Settled ?? Task.CompletedTask;
#else
                return Task.CompletedTask;
#endif
            }
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        // One worker for the process: the communication mode is device-wide state shared by every session, and the
        // jobs of consecutive sessions must run in order. Created on the main thread on first use.
        private static AndroidCommunicationModeWorker _androidWorker;
        private AndroidCommunicationModeWorker.Hold _androidHold;
#endif

        public void Start()
        {
            if (_isActive)
                return;

            bool started = true;
            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                started = StartAndroid();
#elif UNITY_IOS && !UNITY_EDITOR
                started = StartIos();
#endif
            }
            catch (Exception ex)
            {
                started = false;
                Debug.LogWarning($"[Convai] Failed to enable mobile voice processing for AEC: {ex.Message}");
            }

            _isActive = started;
        }

        public void Stop()
        {
            if (!_isActive)
                return;

            try
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                StopAndroid();
#elif UNITY_IOS && !UNITY_EDITOR
                StopIos();
#endif
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Convai] Failed to restore mobile audio session after AEC: {ex.Message}");
            }
            finally
            {
                _isActive = false;
            }
        }

        public void Dispose()
        {
            Stop();
        }

        /// <summary>
        ///     Returns the current audio route signature without blocking. On Android the route is read on the audio
        ///     worker, so this returns the latest value read since <see cref="Start" /> settled (false until then) and
        ///     requests a fresh one for a later call.
        /// </summary>
        public bool TryGetRouteSignature(out int signature)
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_androidHold == null)
            {
                signature = 0;
                return false;
            }

            return _androidHold.TryGetRouteSignature(out signature);
#elif UNITY_IOS && !UNITY_EDITOR
            signature = GetIosRouteSignature();
            return true;
#else
            signature = 0;
            return true;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        private bool StartAndroid()
        {
            _androidWorker ??= CreateAndroidWorker();
            if (_androidWorker == null)
                return false;

            _androidHold = _androidWorker.Acquire();
            return true;
        }

        private void StopAndroid()
        {
            AndroidCommunicationModeWorker.Hold hold = _androidHold;
            _androidHold = null;
            hold?.Release();
        }

        // getSystemService does not wait on the audio service, so the lookup can stay on the main thread.
        // AndroidJavaObject holds a JNI global reference, so the worker can call it after attaching.
        private static AndroidCommunicationModeWorker CreateAndroidWorker()
        {
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            using AndroidJavaObject activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            if (activity == null)
                return null;

            AndroidJavaObject audioManager = activity.Call<AndroidJavaObject>("getSystemService", "audio");
            if (audioManager == null)
                return null;

            return new AndroidCommunicationModeWorker(
                new JniAudioManager(audioManager),
                static () => AndroidJNI.AttachCurrentThread() == 0,
                static () => AndroidJNI.DetachCurrentThread());
        }

        private sealed class JniAudioManager : IAndroidAudioManager
        {
            private readonly AndroidJavaObject _audioManager;

            public JniAudioManager(AndroidJavaObject audioManager) => _audioManager = audioManager;

            public int GetMode() => _audioManager.Call<int>("getMode");
            public void SetMode(int mode) => _audioManager.Call("setMode", mode);
            public bool IsSpeakerphoneOn() => _audioManager.Call<bool>("isSpeakerphoneOn");
            public void SetSpeakerphoneOn(bool on) => _audioManager.Call("setSpeakerphoneOn", on);
            public bool IsBluetoothScoOn() => _audioManager.Call<bool>("isBluetoothScoOn");
            public bool IsBluetoothA2dpOn() => _audioManager.Call<bool>("isBluetoothA2dpOn");
            public bool IsWiredHeadsetOn() => _audioManager.Call<bool>("isWiredHeadsetOn");
        }
#endif

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern bool Convai_AecVoiceProcessingSession_Begin();

        [DllImport("__Internal")]
        private static extern void Convai_AecVoiceProcessingSession_End();

        [DllImport("__Internal")]
        private static extern int Convai_AecVoiceProcessingSession_GetRouteSignature();

        private static bool StartIos()
        {
            if (!Convai_AecVoiceProcessingSession_Begin())
            {
                Debug.LogWarning("[Convai] iOS voice-processing session could not be enabled for AEC.");
                return false;
            }

            return true;
        }

        private static void StopIos()
        {
            Convai_AecVoiceProcessingSession_End();
        }

        private static int GetIosRouteSignature() => Convai_AecVoiceProcessingSession_GetRouteSignature();
#endif
    }
}
