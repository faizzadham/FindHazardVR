using System;
using System.Collections;

namespace Convai.Infrastructure.Networking.WebGL
{
    /// <summary>
    ///     Keeps a published browser microphone track muted exactly when its
    ///     <see cref="WebGLMicrophoneSource.IsMuted" /> is true.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         On native platforms the SDK mutes by dropping captured frames. On WebGL the browser captures and
    ///         sends the audio, so the LiveKit track itself has to be muted, or push-to-talk release and
    ///         <c>SetMicMuted(true)</c> leave audio flowing to the server.
    ///     </para>
    ///     <para>
    ///         Browser mute and unmute are asynchronous, so changes are applied one at a time and only the
    ///         latest state is kept. A track starts unmuted when published. After <see cref="Detach" />,
    ///         nothing more is applied, so a late unmute cannot reopen a microphone that is being unpublished.
    ///     </para>
    /// </remarks>
    internal sealed class WebGLMicrophoneMuteSync
    {
        private readonly WebGLMicrophoneSource _source;
        private readonly Func<bool, Func<bool?>> _applyMute;
        private readonly Action<IEnumerator> _startRoutine;
        private bool _applied;
        private bool _applying;
        private bool _detached;

        /// <param name="source">The microphone source whose mute state the track follows.</param>
        /// <param name="applyMute">
        ///     Starts muting (true) or unmuting (false) the track and returns a poll: null while pending, true once
        ///     applied, false if the browser refused. A refused change is retried on the next mute change.
        /// </param>
        /// <param name="startRoutine">Runs a coroutine on the player loop.</param>
        internal WebGLMicrophoneMuteSync(
            WebGLMicrophoneSource source,
            Func<bool, Func<bool?>> applyMute,
            Action<IEnumerator> startRoutine)
        {
            _source = source ?? throw new ArgumentNullException(nameof(source));
            _applyMute = applyMute ?? throw new ArgumentNullException(nameof(applyMute));
            _startRoutine = startRoutine ?? throw new ArgumentNullException(nameof(startRoutine));
            _source.MuteChanged += OnMuteChanged;
            ApplyLatest();
        }

        /// <summary>Stops following the source. Idempotent.</summary>
        internal void Detach()
        {
            if (_detached) return;
            _detached = true;
            _source.MuteChanged -= OnMuteChanged;
        }

        private void OnMuteChanged(bool muted) => ApplyLatest();

        private void ApplyLatest()
        {
            if (_detached || _applying || _applied == _source.IsMuted) return;
            _applying = true;
            _startRoutine(ApplyRoutine());
        }

        private IEnumerator ApplyRoutine()
        {
            try
            {
                while (!_detached && _applied != _source.IsMuted)
                {
                    bool target = _source.IsMuted;
                    Func<bool?> outcome = _applyMute(target);
                    bool? applied;
                    while ((applied = outcome()) == null) yield return null;
                    if (applied == false) yield break;
                    _applied = target;
                }
            }
            finally
            {
                _applying = false;
            }
        }
    }
}
