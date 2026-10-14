#nullable enable

using Newtonsoft.Json;

namespace Convai.Infrastructure.Protocol.Messages
{
    /// <summary>Payload for bot transcription events emitted by the server.</summary>
    public class BotTranscriptionPayload
    {
        /// <summary>Gets or sets the transcribed bot text.</summary>
        [JsonProperty("text")]
        public string? Text { get; set; }

        /// <summary>Gets or sets the backend response identifier, when present.</summary>
        [JsonProperty("response_id")]
        public string? ResponseId { get; set; }

        /// <summary>Gets or sets the stable message identifier, when present.</summary>
        [JsonProperty("message_id")]
        public string? MessageId { get; set; }

        /// <summary>Gets or sets the backend turn identifier, when present.</summary>
        [JsonProperty("turn_id")]
        public string? TurnId { get; set; }

        /// <summary>Whether this output is intended to be spoken.</summary>
        [JsonProperty("spoken")]
        public bool? Spoken { get; set; }

        /// <summary>Backend aggregation mode used to produce this output.</summary>
        [JsonProperty("aggregated_by")]
        public string? AggregatedBy { get; set; }

        /// <summary>Backend identifier of the output segment this text belongs to, when present.</summary>
        [JsonProperty("segment_id")]
        public long? SegmentId { get; set; }

        /// <summary>Speaking status of the segment ("new", "in-progress" or "completed"), when present.</summary>
        [JsonProperty("spoken_status")]
        public string? SpokenStatus { get; set; }

        /// <summary>Split of the segment text at the word being spoken, when present.</summary>
        [JsonProperty("spoken_progress")]
        public BotSpokenProgressPayload? SpokenProgress { get; set; }
    }

    /// <summary>Spoken-so-far and remaining text of a bot output segment.</summary>
    public class BotSpokenProgressPayload
    {
        /// <summary>Text already spoken within the segment, including the current word.</summary>
        [JsonProperty("accumulated_text")]
        public string? AccumulatedText { get; set; }

        /// <summary>Text of the segment not yet spoken.</summary>
        [JsonProperty("remaining_text")]
        public string? RemainingText { get; set; }
    }
}
