using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Core.Async;

namespace Convai.Runtime.Room
{
    public enum PttState { Idle, Preparing, Capturing, Draining, AwaitingCommit, AwaitingBotTurn, Error }
    public enum PttCommitStatus { Committed, Empty, Failed }
    public enum PttReleaseMode { Auto, Legacy }

    /// <summary>Core's finalization result. A committed turn can still require reconnection before another capture.</summary>
    public sealed class PttCommitResult
    {
        public string ReleaseId { get; }
        public PttCommitStatus Status { get; }
        public string ResponseId { get; }
        public string Reason { get; }
        public bool ReconnectRequired { get; }
        public PttCommitResult(string releaseId, PttCommitStatus status, string responseId = null,
            string reason = null, bool reconnectRequired = false)
        {
            ReleaseId = releaseId;
            Status = status;
            ResponseId = responseId ?? string.Empty;
            Reason = reason ?? string.Empty;
            ReconnectRequired = reconnectRequired;
        }
    }

    /// <summary>Additive service for correlated PTT completion; existing room interfaces remain compatible.</summary>
    public interface IConvaiPttService
    {
        IConvaiOperation<PttCommitResult> ForceUserStoppedSpeakingAsync(string releaseId,
            CancellationToken cancellationToken = default);
    }

    /// <summary>Optional native source boundary. It acknowledges local media submission, not server receipt.</summary>
    public interface IMicrophoneCaptureDrainFactory
    {
        bool SupportsCaptureDrain { get; }
    }

    public interface IMicrophoneCaptureDrain
    {
        Task StopAndDrainCaptureAsync(CancellationToken cancellationToken = default);
    }
}
