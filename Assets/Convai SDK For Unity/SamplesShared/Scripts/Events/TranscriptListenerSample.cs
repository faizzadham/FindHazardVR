using System;
using Convai.Domain.Models;
using Convai.Runtime.Components;
using Convai.Runtime.Facades;
using TMPro;
using UnityEngine;

namespace Convai.Sample.Events
{
    /// <summary>
    ///     Newcomer-facing sample that shows the quick transcript subscription path.
    /// </summary>
    public sealed class TranscriptListenerSample : MonoBehaviour
    {
        [SerializeField] private TMP_Text transcriptText;
        [SerializeField] private string filterCharacterId;
        [SerializeField] private bool committedOnly = true;

        private IDisposable _subscription;

        private void OnEnable()
        {
            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager == null || !manager.TryGetTranscripts(out ConvaiTranscripts transcripts)) return;

            var options = new TranscriptSubscriptionOptions
            {
                ReplayExisting = true,
                SpeakerType = TranscriptSpeakerType.Character,
                SpeakerId = string.IsNullOrWhiteSpace(filterCharacterId) ? null : filterCharacterId
            };

            _subscription = committedOnly
                ? transcripts.SubscribeCommitted(OnTranscriptTurn, options)
                : transcripts.Subscribe(OnTranscriptTurn, options);
        }

        private void OnDisable()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        private void OnTranscriptTurn(TranscriptChange change)
        {
            TranscriptTurn turn = change?.Turn;
            if (turn == null) return;
            if (transcriptText == null) return;
            string speaker = string.IsNullOrWhiteSpace(turn.Speaker?.DisplayName)
                ? turn.Speaker?.Type.ToString() ?? "Transcript"
                : turn.Speaker.DisplayName;
            transcriptText.text = $"{speaker}: {turn.DisplayText}";
        }
    }
}
