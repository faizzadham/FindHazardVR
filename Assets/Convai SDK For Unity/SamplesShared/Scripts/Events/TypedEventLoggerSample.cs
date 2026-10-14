using Convai.Domain.DomainEvents.Session;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Sample.Events
{
    /// <summary>
    ///     Engineer-facing sample that shows the canonical typed event path.
    /// </summary>
    public sealed class TypedEventLoggerSample : MonoBehaviour
    {
        [SerializeField] private ConvaiManager manager;

        private void OnEnable()
        {
            if (manager == null) return;

            manager.Events.OnSessionStateChanged += HandleSessionStateChanged;
            manager.Events.OnSessionError += HandleSessionError;
            manager.Events.OnCharacterTranscriptReceived += HandleCharacterTranscriptReceived;
        }

        private void OnDisable()
        {
            if (manager == null) return;

            manager.Events.OnSessionStateChanged -= HandleSessionStateChanged;
            manager.Events.OnSessionError -= HandleSessionError;
            manager.Events.OnCharacterTranscriptReceived -= HandleCharacterTranscriptReceived;
        }

        private void HandleSessionStateChanged(SessionStateChanged e) =>
            Debug.Log($"[TypedEventLoggerSample] Session {e.OldState} -> {e.NewState}");

        private void HandleSessionError(SessionError e) =>
            Debug.LogError($"[TypedEventLoggerSample] Convai error ({e.ErrorCode}): {e.Message}");

        private void HandleCharacterTranscriptReceived(CharacterTranscriptReceived e) =>
            Debug.Log($"[TypedEventLoggerSample] [{e.CharacterName}] {e.Text}");
    }
}
