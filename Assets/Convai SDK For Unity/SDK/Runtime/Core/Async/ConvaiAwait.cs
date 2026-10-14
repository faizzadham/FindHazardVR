namespace Convai.Runtime.Core.Async
{
    /// <summary>
    ///     Where the SDK's own <c>await</c>s resume. Every <c>ConfigureAwait</c> in code that can run in a
    ///     player passes <see cref="ResumeOnCapturedContext" /> instead of a literal.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A WebGL player has no thread pool: work queued to it never runs. When a task completes on the
    ///         main thread, where <c>SynchronizationContext.Current</c> is Unity's context, a
    ///         <c>ConfigureAwait(false)</c> continuation is not run inline but queued to the thread pool, so on
    ///         WebGL it is silently lost. Measured in a WebGL player: a <c>ThreadPool</c> work item, a
    ///         <c>Task.Run</c> body and a <c>ConfigureAwait(false)</c> continuation all never ran, which left
    ///         every microphone publish waiting forever with its locks held.
    ///     </para>
    ///     <para>
    ///         On WebGL the SDK therefore resumes on the captured context, which the player loop pumps. On
    ///         every other platform it keeps <c>ConfigureAwait(false)</c>, so native threading is unchanged.
    ///     </para>
    /// </remarks>
    internal static class ConvaiAwait
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        internal const bool ResumeOnCapturedContext = true;
#else
        internal const bool ResumeOnCapturedContext = false;
#endif
    }
}
