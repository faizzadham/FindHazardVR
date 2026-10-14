namespace Convai.Runtime.Room
{
    internal enum AutoStartMicrophoneReadinessAction
    {
        OpenMicrophone = 0,
        ContinueWaiting,
        WarnAndContinueWaiting,
        Abort
    }

    /// <summary>
    ///     Decides whether hands-free auto-start may open while a multi-character room is waiting
    ///     for its initial membership.
    /// </summary>
    internal static class AutoStartMicrophoneReadinessGate
    {
        internal static AutoStartMicrophoneReadinessAction Decide(
            bool isConnected,
            bool hasMultiCharacterSession,
            bool initialCharacterIsReady,
            bool warningDeadlineElapsed,
            bool warningAlreadyEmitted)
        {
            if (!isConnected)
                return AutoStartMicrophoneReadinessAction.Abort;
            if (!hasMultiCharacterSession || initialCharacterIsReady)
                return AutoStartMicrophoneReadinessAction.OpenMicrophone;
            if (warningDeadlineElapsed && !warningAlreadyEmitted)
                return AutoStartMicrophoneReadinessAction.WarnAndContinueWaiting;
            return AutoStartMicrophoneReadinessAction.ContinueWaiting;
        }
    }
}
