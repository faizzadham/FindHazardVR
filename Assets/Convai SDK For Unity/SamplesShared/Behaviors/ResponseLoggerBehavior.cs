using Convai.Domain.Logging;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Logging;
using UnityEngine;

namespace Convai.Sample.Behaviors
{
    /// <summary>
    ///     Sample response handler that logs bot output without suppressing the default transcript pipeline.
    /// </summary>
    public sealed class ResponseLoggerBehavior : MonoBehaviour, IConvaiResponseHandler
    {
        public int Priority => -10;

        public bool CanHandle(IConvaiCharacterAgent agent, string text, bool isFinal) => true;

        public bool ProcessResponse(IConvaiCharacterAgent agent, string text, bool isFinal)
        {
            ConvaiLogger.Debug($"[ResponseLogger] [{agent.CharacterName}] {text}", LogCategory.SDK);
            return false;
        }
    }
}
