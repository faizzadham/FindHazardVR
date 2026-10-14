using Convai.Modules.LipSync;

namespace Convai.Tests.EditMode.Fixtures
{
    public sealed class ManualPlaybackClock : IPlaybackClock
    {
        public double ElapsedSeconds { get; private set; }

        public bool IsValid { get; private set; } = true;

        public void StartClock()
        {
            ElapsedSeconds = 0d;
            IsValid = true;
            LastStartOffset = 0d;
            StartCount++;
        }

        public void StartClock(double initialElapsedSeconds)
        {
            ElapsedSeconds = initialElapsedSeconds;
            IsValid = true;
            LastStartOffset = initialElapsedSeconds;
            StartCount++;
        }

        public void Rebase(double elapsedSeconds)
        {
            ElapsedSeconds = elapsedSeconds;
            LastRebaseValue = elapsedSeconds;
        }

        public double? LastStartOffset { get; private set; }
        public int StartCount { get; private set; }
        public double? LastRebaseValue { get; private set; }
        public int PauseCount { get; private set; }
        public int ResumeCount { get; private set; }
        public bool IsPaused { get; private set; }

        public void Pause()
        {
            PauseCount++;
            IsPaused = true;
        }

        public void Resume()
        {
            ResumeCount++;
            IsPaused = false;
        }

        public void Reset() => ElapsedSeconds = 0d;

        public void SetElapsed(double seconds) => ElapsedSeconds = seconds;

        public void SetValid(bool valid) => IsValid = valid;
    }
}
