namespace Convai.Infrastructure.Protocol.Messages
{
    /// <summary>Outbound force-user-stopped-speaking message.</summary>
    public sealed class RTVIForceUserStoppedSpeaking : RTVISendMessageBase
    {
        public RTVIForceUserStoppedSpeaking(string releaseId) : this()
        {
            if (string.IsNullOrWhiteSpace(releaseId)) throw new System.ArgumentException("Release ID is required.", nameof(releaseId));
            Id = releaseId;
        }

        public RTVIForceUserStoppedSpeaking()
        {
            Type = "force-user-stopped-speaking";
            Data = new { };
        }
    }
}
