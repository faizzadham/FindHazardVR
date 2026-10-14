using System.Collections;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;
using Convai.Runtime.Presentation.Views.Notifications;
using Convai.Shared.Interfaces;
using UnityEngine;

namespace Convai.Sample.UI.Utilities
{
    /// <summary>
    ///     Sample utility for checking microphone functionality.
    /// </summary>
    /// <remarks>
    ///     This is a reference implementation in the Sample layer.
    ///     Projects can customize or replace this utility as needed.
    /// </remarks>
    public static class MicrophoneCheck
    {
        private const float INPUT_CHECK_DURATION = 3f;

        private const float SENSITIVITY = 10f;

        private const float THRESHOLD = 1.0f;

        public static IEnumerator CheckMicrophoneDevice(AudioClip audioClip,
            SONotification microphoneIssueNotification = null,
            IConvaiNotificationService notificationService = null)
        {
            if (audioClip == null)
            {
                ConvaiLogger.Error("AudioClip is null!", LogCategory.Character);
                yield break;
            }

            yield return new WaitForSeconds(INPUT_CHECK_DURATION);

            int sampleEnd = (int)(INPUT_CHECK_DURATION * audioClip.frequency * audioClip.channels);

            float[] samples = new float[sampleEnd];
            int samplesLength = samples.Length;

            if (!audioClip.GetData(samples, 0))
            {
                ConvaiLogger.Error("Failed to get audio data!", LogCategory.Character);
                yield break;
            }

            float level = 0;

            for (int i = 0; i < samplesLength; i++) level += Mathf.Abs(samples[i]);

            level = level / samplesLength * SENSITIVITY;

            if (level < THRESHOLD)
            {
                ConvaiLogger.Warning("Microphone Issue Detected!", LogCategory.Character);
                if (microphoneIssueNotification != null)
                    notificationService?.RequestNotification(microphoneIssueNotification);
                yield break;
            }

            ConvaiLogger.Info("Microphone is working fine.", LogCategory.Character);
        }
    }
}
