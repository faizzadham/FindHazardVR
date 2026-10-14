using Convai.Domain.DomainEvents.Session;
using Convai.Runtime.Behaviors;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     Example input handler that plays an audio cue when the session connects.
    /// </summary>
    public class PlayerSessionStateHandler : ConvaiPlayerInputHandlerBase
    {
        [SerializeField] private AudioClip connectedClip;

        [SerializeField] private AudioSource outputSource;

        public override bool CanHandle(IConvaiPlayerAgent agent, SessionStateChanged sessionState) =>
            sessionState.NewState == SessionState.Connected && connectedClip != null && outputSource != null;

        public override bool ProcessInput(IConvaiPlayerAgent agent, SessionStateChanged sessionState)
        {
            outputSource.PlayOneShot(connectedClip);
            return false;
        }
    }
}
