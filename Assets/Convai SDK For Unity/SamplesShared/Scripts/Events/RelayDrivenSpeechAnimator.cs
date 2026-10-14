using UnityEngine;

namespace Convai.Sample.Events
{
    /// <summary>
    ///     Designer-facing sample target for ConvaiCharacterEventRelay speech callbacks.
    /// </summary>
    public sealed class RelayDrivenSpeechAnimator : MonoBehaviour
    {
        [SerializeField] private Animator animator;
        [SerializeField] private string speakingBoolParameter = "IsSpeaking";

        public void HandleSpeechStarted()
        {
            if (animator == null || string.IsNullOrWhiteSpace(speakingBoolParameter)) return;
            animator.SetBool(speakingBoolParameter, true);
        }

        public void HandleSpeechStopped()
        {
            if (animator == null || string.IsNullOrWhiteSpace(speakingBoolParameter)) return;
            animator.SetBool(speakingBoolParameter, false);
        }
    }
}
