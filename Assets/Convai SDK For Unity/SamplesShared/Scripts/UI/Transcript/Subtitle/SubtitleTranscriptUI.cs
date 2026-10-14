using System;
using Convai.Domain.Logging;
using Convai.Domain.Models;
using Convai.Runtime.Components;
using Convai.Runtime.Facades;
using Convai.Runtime.Logging;
using Convai.Runtime.Presentation.Services.Utilities;
using TMPro;
using UnityEngine;

namespace Convai.Sample.UI.Transcript
{
    /// <summary>
    ///     Reference subtitle renderer built on ConvaiManager.Transcripts.
    /// </summary>
    public class SubtitleTranscriptUI : MonoBehaviour
    {
        [Header("UI References")]
        [SerializeField]
        private TMP_Text subtitleText;

        [SerializeField] private TMP_Text speakerLabel;
        [SerializeField] private GameObject subtitleContainer;

        [Header("Filters")]
        [SerializeField]
        private bool finalOnly;

        [SerializeField] private bool filterBySpeakerType;
        [SerializeField] private TranscriptSpeakerType speakerType = TranscriptSpeakerType.Character;
        [SerializeField] private string speakerIdFilter;
        [SerializeField] private string participantIdFilter;

        [Header("Fade Settings")]
        [SerializeField]
        private CanvasFader canvasFader;

        [SerializeField] private CanvasGroup canvasGroup;
        [SerializeField] private float fadeDuration = 0.3f;

        [Header("Auto-Hide Settings")]
        [SerializeField]
        private float autoHideDelay = 3.0f;

        private IDisposable _subscription;
        private ConvaiTranscripts _boundTranscripts;
        private string _currentTurnId;
        private float _hideTimer;

        private void Awake()
        {
            if (canvasFader == null)
                canvasFader = GetComponentInChildren<CanvasFader>();
            if (canvasGroup == null)
                canvasGroup = GetComponentInChildren<CanvasGroup>();

            if (subtitleContainer != null)
                subtitleContainer.SetActive(false);
        }

        private void OnEnable()
        {
            TrySubscribe(true);
        }

        private void LateUpdate()
        {
            TrySubscribe(false);
        }

        private void Update()
        {
            if (_hideTimer <= 0) return;

            _hideTimer -= Time.deltaTime;
            if (_hideTimer <= 0)
                HideSubtitle();
        }

        private void OnDisable()
        {
            Unsubscribe();
        }

        private void TrySubscribe(bool logFailure)
        {
            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (manager == null || !manager.TryGetTranscripts(out ConvaiTranscripts transcripts))
            {
                Unsubscribe();
                if (logFailure)
                    ConvaiLogger.Warning("[SubtitleTranscriptUI] No active ConvaiManager found.", LogCategory.UI);
                return;
            }

            if (ReferenceEquals(_boundTranscripts, transcripts)) return;

            Unsubscribe();

            _boundTranscripts = transcripts;
            _boundTranscripts.PresentationEnabledChanged += HandlePresentationEnabledChanged;
            ApplyPresentationEnabled(_boundTranscripts.IsPresentationEnabled);
        }

        private void HandlePresentationEnabledChanged(bool enabled) => ApplyPresentationEnabled(enabled);

        private void ApplyPresentationEnabled(bool enabled)
        {
            if (!enabled)
            {
                DisposeCaptionSubscription();
                HideSubtitle();
                return;
            }

            if (_boundTranscripts == null || _subscription != null) return;

            var options = new TranscriptCaptionSubscriptionOptions
            {
                ReplayLatest = true,
                IncludeStreaming = !finalOnly,
                SpeakerType = filterBySpeakerType ? speakerType : null,
                SpeakerId = speakerIdFilter,
                ParticipantId = participantIdFilter
            };

            _subscription = _boundTranscripts.SubscribeCaptions(DisplayCaption, options);
        }

        private void Unsubscribe()
        {
            DisposeCaptionSubscription();
            if (_boundTranscripts != null)
                _boundTranscripts.PresentationEnabledChanged -= HandlePresentationEnabledChanged;
            _boundTranscripts = null;
        }

        private void DisposeCaptionSubscription()
        {
            _subscription?.Dispose();
            _subscription = null;
        }

        private void DisplayCaption(TranscriptCaption caption)
        {
            if (caption == null || !caption.HasText) return;

            _currentTurnId = caption.TurnId;
            _hideTimer = 0;

            if (subtitleContainer != null)
                subtitleContainer.SetActive(true);

            if (speakerLabel != null)
            {
                speakerLabel.text = caption.Speaker?.DisplayName ?? string.Empty;
                speakerLabel.color = caption.Speaker?.Type == TranscriptSpeakerType.Character
                    ? Color.cyan
                    : Color.green;
            }

            if (subtitleText != null)
                subtitleText.text = caption.Text;

            if (canvasFader != null && canvasGroup != null)
                canvasFader.StartFadeIn(canvasGroup, fadeDuration);

            if (caption.IsFinal && _currentTurnId == caption.TurnId)
                _hideTimer = autoHideDelay;
        }

        private void HideSubtitle()
        {
            if (canvasFader != null && canvasGroup != null)
                canvasFader.StartFadeOut(canvasGroup, fadeDuration);
            else if (subtitleContainer != null)
                subtitleContainer.SetActive(false);
        }
    }
}
