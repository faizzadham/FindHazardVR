#nullable enable
using System;

namespace Convai.Api.Transport
{
    internal static class UnityWebRequestErrors
    {
        // Unity documents "Request timeout"; some platforms instead report "timed out".
        internal static bool IsTimeout(string? error) =>
            error?.IndexOf("timeout", StringComparison.OrdinalIgnoreCase) >= 0 ||
            error?.IndexOf("timed out", StringComparison.OrdinalIgnoreCase) >= 0;
    }
}
