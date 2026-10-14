using Convai.Domain.Models;

namespace Convai.Infrastructure.Networking
{
    /// <summary>
    ///     Constant enum names for transcript diagnostics. Enum <c>ToString()</c> allocates on every
    ///     call, and these diagnostics run once per streamed transcript segment.
    /// </summary>
    internal static class TranscriptDiagnosticNames
    {
        internal static string Source(TranscriptSegmentSourceKind kind) => kind switch
        {
            TranscriptSegmentSourceKind.PlayerAsr => nameof(TranscriptSegmentSourceKind.PlayerAsr),
            TranscriptSegmentSourceKind.PlayerProcessedFinal => nameof(TranscriptSegmentSourceKind.PlayerProcessedFinal),
            TranscriptSegmentSourceKind.PlayerTypedText => nameof(TranscriptSegmentSourceKind.PlayerTypedText),
            TranscriptSegmentSourceKind.BotOutput => nameof(TranscriptSegmentSourceKind.BotOutput),
            TranscriptSegmentSourceKind.BotLlmPreview => nameof(TranscriptSegmentSourceKind.BotLlmPreview),
            TranscriptSegmentSourceKind.LegacyBotTranscript => nameof(TranscriptSegmentSourceKind.LegacyBotTranscript),
            _ => nameof(TranscriptSegmentSourceKind.Unknown)
        };

        internal static string Lifecycle(TranscriptLifecycle lifecycle) => lifecycle switch
        {
            TranscriptLifecycle.Streaming => nameof(TranscriptLifecycle.Streaming),
            TranscriptLifecycle.Stable => nameof(TranscriptLifecycle.Stable),
            TranscriptLifecycle.Completed => nameof(TranscriptLifecycle.Completed),
            _ => "Unknown"
        };
    }
}
