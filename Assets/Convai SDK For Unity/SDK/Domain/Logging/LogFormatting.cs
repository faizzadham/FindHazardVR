using System;
using System.Buffers;
using System.Text;

namespace Convai.Domain.Logging
{
    public static class LogFormatting
    {
        private static readonly string[] LevelNames = Enum.GetNames(typeof(LogLevel));
        private static readonly string[] CategoryNames = Enum.GetNames(typeof(LogCategory));
        private static readonly string[] LevelColors = { "", "red", "yellow", "grey", "cyan", "gray" };
        [ThreadStatic] private static StringBuilder _builder;
        private static readonly SpanAction<char, LargeMessage> WriteLargeMessage = WriteLarge;
        public const int MaximumRetainedCapacity = 4096;
        public static int RetainedBuilderCapacity => _builder?.Capacity ?? 0;
        private static StringBuilder Acquire()
        {
            var builder = _builder;
            _builder = null; // A nested formatter must own a different buffer.
            return builder ?? new StringBuilder(256);
        }
        private static void Release(StringBuilder builder)
        {
            if (builder.Capacity > MaximumRetainedCapacity || _builder != null) return;
            builder.Clear(); _builder = builder;
        }
        public static string GetLevelName(LogLevel level) => (uint)level < LevelNames.Length ? LevelNames[(int)level] : "Invalid";
        public static string GetCategoryName(LogCategory category) => (uint)category < CategoryNames.Length ? CategoryNames[(int)category] : "Invalid";
        public static string GetLevelColor(LogLevel level) => (uint)level < LevelColors.Length ? LevelColors[(int)level] : "";
        private static void Digits(StringBuilder builder, int value) => builder.Append((char)('0' + value / 10)).Append((char)('0' + value % 10));
        public static string Format(in LogEntry entry, bool colored, LogStackTraceLevels stacks)
        {
            StringBuilder builder = null;
            try
            {
                if (entry.Message != null && entry.Message.Length > MaximumRetainedCapacity && !entry.HasContext && !entry.HasException)
                {
                    var state = new LargeMessage(in entry, colored);
                    return string.Create(state.Length, state, WriteLargeMessage);
                }
                builder = Acquire();
                string color = colored ? GetLevelColor(entry.Level) : "";
                if (color.Length > 0) builder.Append("<color=").Append(color).Append('>');
                DateTime local = entry.Timestamp.ToLocalTime();
                builder.Append('['); Digits(builder, local.Hour); builder.Append(':'); Digits(builder, local.Minute);
                builder.Append(':'); Digits(builder, local.Second); builder.Append('.');
                builder.Append((char)('0' + local.Millisecond / 100)); Digits(builder, local.Millisecond % 100);
                builder.Append("][").Append(GetLevelName(entry.Level)).Append("][").Append(GetCategoryName(entry.Category)).Append(']');
                if (entry.HasCorrelationId) builder.Append('[').Append(entry.CorrelationId).Append(']');
                builder.Append(": ");
                if (!string.IsNullOrEmpty(entry.Source)) builder.Append('[').Append(entry.Source).Append("] ");
                builder.Append(entry.Message);
                if (entry.HasContext)
                {
                    builder.Append(" {"); bool first = true;
                    foreach (var item in entry.Context)
                    { if (!first) builder.Append(", "); first = false; builder.Append(item.Key).Append('=').Append(item.Value); }
                    builder.Append('}');
                }
                if (entry.Exception != null)
                {
                    builder.Append('\n');
                    if ((stacks & (LogStackTraceLevels)(1 << (int)entry.Level)) != 0)
                        builder.Append(LogDiagnostics.SafeString(entry.Exception));
                    else
                    {
                        var exception = entry.Exception;
                        for (int depth = 0; exception != null && depth < 32; depth++, exception = exception.InnerException)
                        { if (depth > 0) builder.Append(" ---> "); builder.Append(exception.GetType().Name).Append(": ").Append(exception.Message); }
                    }
                }
                if (color.Length > 0) builder.Append("</color>");
                return builder.ToString();
            }
            catch { LogDiagnostics.FormattingFailed(); return LogDiagnostics.FormattingFallback; }
            finally { if (builder != null) Release(builder); }
        }

        // Large plain messages need only their final string. Writing directly avoids retaining
        // or repeatedly allocating a second large buffer while preserving the same text contract.
        private readonly struct LargeMessage
        {
            internal readonly string Message, Level, Category, Correlation, Source, Color;
            internal readonly DateTime Local;
            internal readonly int Length;
            internal LargeMessage(in LogEntry entry, bool colored)
            {
                Message = entry.Message; Level = GetLevelName(entry.Level); Category = GetCategoryName(entry.Category);
                Correlation = entry.CorrelationId; Source = entry.Source;
                Color = colored ? GetLevelColor(entry.Level) : "";
                Local = entry.Timestamp.ToLocalTime();
                Length = checked(20 + Level.Length + Category.Length + Message.Length
                    + (string.IsNullOrEmpty(Correlation) ? 0 : Correlation.Length + 2)
                    + (string.IsNullOrEmpty(Source) ? 0 : Source.Length + 3)
                    + (Color.Length == 0 ? 0 : Color.Length + 16));
            }
        }
        private static void Copy(Span<char> output, ref int position, string value)
        { value.AsSpan().CopyTo(output.Slice(position)); position += value.Length; }
        private static void Digits(Span<char> output, ref int position, int value)
        { output[position++] = (char)('0' + value / 10); output[position++] = (char)('0' + value % 10); }
        private static void WriteLarge(Span<char> output, LargeMessage state)
        {
            int position = 0;
            if (state.Color.Length > 0) { Copy(output, ref position, "<color="); Copy(output, ref position, state.Color); output[position++] = '>'; }
            output[position++] = '['; Digits(output, ref position, state.Local.Hour); output[position++] = ':';
            Digits(output, ref position, state.Local.Minute); output[position++] = ':';
            Digits(output, ref position, state.Local.Second); output[position++] = '.';
            output[position++] = (char)('0' + state.Local.Millisecond / 100); Digits(output, ref position, state.Local.Millisecond % 100);
            Copy(output, ref position, "]["); Copy(output, ref position, state.Level);
            Copy(output, ref position, "]["); Copy(output, ref position, state.Category); output[position++] = ']';
            if (!string.IsNullOrEmpty(state.Correlation)) { output[position++] = '['; Copy(output, ref position, state.Correlation); output[position++] = ']'; }
            Copy(output, ref position, ": ");
            if (!string.IsNullOrEmpty(state.Source)) { output[position++] = '['; Copy(output, ref position, state.Source); Copy(output, ref position, "] "); }
            Copy(output, ref position, state.Message);
            if (state.Color.Length > 0) Copy(output, ref position, "</color>");
        }
    }
}
