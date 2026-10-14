using System;
using System.Threading;

namespace Convai.Domain.Logging
{
    public static class LogDiagnostics
    {
        public const string FormattingFallback = "[log formatting failed]";
        private static long _formattingFailures, _sinkFailures, _recursiveEntries;
        public static long FormattingFailures => Interlocked.Read(ref _formattingFailures);
        public static long SinkFailures => Interlocked.Read(ref _sinkFailures);
        public static long RecursiveEntries => Interlocked.Read(ref _recursiveEntries);
        public static void FormattingFailed() => Interlocked.Increment(ref _formattingFailures);
        public static void SinkFailed() => Interlocked.Increment(ref _sinkFailures);
        public static void RecursiveEntry() => Interlocked.Increment(ref _recursiveEntries);
        [ThreadStatic] private static int _formatDepth;
        public static string Format<T0>(string template, T0 value0)
        {
            if (++_formatDepth > 8) { --_formatDepth; FormattingFailed(); return FormattingFallback; }
            try { return string.Format(template, value0); }
            catch { FormattingFailed(); return FormattingFallback; }
            finally { --_formatDepth; }
        }
        public static string Format<T0, T1>(string template, T0 value0, T1 value1)
        {
            if (++_formatDepth > 8) { --_formatDepth; FormattingFailed(); return FormattingFallback; }
            try { return string.Format(template, value0, value1); }
            catch { FormattingFailed(); return FormattingFallback; }
            finally { --_formatDepth; }
        }
        public static string Format<T0, T1, T2>(string template, T0 value0, T1 value1, T2 value2)
        {
            if (++_formatDepth > 8) { --_formatDepth; FormattingFailed(); return FormattingFallback; }
            try { return string.Format(template, value0, value1, value2); }
            catch { FormattingFailed(); return FormattingFallback; }
            finally { --_formatDepth; }
        }
        public static string SafeString(object value)
        {
            if (value == null) return string.Empty;
            if (++_formatDepth > 8) { --_formatDepth; FormattingFailed(); return FormattingFallback; }
            try { return value.ToString() ?? string.Empty; }
            catch { FormattingFailed(); return FormattingFallback; }
            finally { --_formatDepth; }
        }
    }
}
