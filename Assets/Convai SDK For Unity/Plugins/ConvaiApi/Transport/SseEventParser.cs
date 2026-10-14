#nullable enable
using System;
using System.Collections.Generic;
using System.Text;

namespace Convai.Api.Transport
{
    internal sealed class SseEventParser
    {
        private readonly List<string> _data = new();
        private string? _eventName;
        private string? _id;

        public ConvaiApiSseEvent? Push(string line)
        {
            if (line.Length == 0)
            {
                if (_data.Count == 0 && _eventName == null && _id == null)
                    return null;
                var result = new ConvaiApiSseEvent(string.Join("\n", _data), _eventName, _id);
                _data.Clear();
                _eventName = null;
                _id = null;
                return result;
            }

            if (line[0] == ':')
                return null;
            int separator = line.IndexOf(':');
            string field = separator < 0 ? line : line.Substring(0, separator);
            string value = separator < 0 ? string.Empty : line.Substring(separator + 1);
            if (value.Length > 0 && value[0] == ' ')
                value = value.Substring(1);
            switch (field)
            {
                case "data":
                    _data.Add(value);
                    break;
                case "event":
                    _eventName = value;
                    break;
                case "id":
                    _id = value;
                    break;
            }
            return null;
        }
    }

    internal sealed class SseChunkDecoder
    {
        private readonly Decoder _decoder = Encoding.UTF8.GetDecoder();
        private readonly StringBuilder _pending = new();
        private readonly SseEventParser _parser = new();

        public void Push(byte[] data, int length, Action<ConvaiApiSseEvent> onEvent)
        {
            int charCount = _decoder.GetCharCount(data, 0, length);
            var chars = new char[charCount];
            _decoder.GetChars(data, 0, length, chars, 0);
            _pending.Append(chars);
            while (true)
            {
                string value = _pending.ToString();
                int newline = value.IndexOf('\n');
                if (newline < 0)
                    return;
                string line = value.Substring(0, newline).TrimEnd('\r');
                _pending.Remove(0, newline + 1);
                ConvaiApiSseEvent? parsed = _parser.Push(line);
                if (parsed != null)
                    onEvent(parsed);
            }
        }
    }
}
