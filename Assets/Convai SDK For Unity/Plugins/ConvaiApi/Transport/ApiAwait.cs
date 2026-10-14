namespace Convai.Api.Transport
{
    /// <summary>
    ///     Where the REST and character API clients resume their <c>await</c>s. Every <c>ConfigureAwait</c> in them
    ///     passes <see cref="ResumeOnCapturedContext" /> instead of a literal, except the HttpClient transports,
    ///     which only run outside WebGL.
    /// </summary>
    /// <remarks>
    ///     In a WebGL player the UnityWebRequest transports finish on the main thread, and a
    ///     <c>ConfigureAwait(false)</c> continuation is then queued to a thread pool the player never runs, so the
    ///     request would never return to its caller. The same rule and measurement as the SDK's
    ///     <c>ConvaiAwait</c>, which these assemblies cannot reference.
    /// </remarks>
    internal static class ApiAwait
    {
#if UNITY_WEBGL && !UNITY_EDITOR
        internal const bool ResumeOnCapturedContext = true;
#else
        internal const bool ResumeOnCapturedContext = false;
#endif
    }
}
