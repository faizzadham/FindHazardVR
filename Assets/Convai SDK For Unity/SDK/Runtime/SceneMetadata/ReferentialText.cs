using System;
using System.Collections.Generic;

namespace Convai.Runtime.SceneMetadata
{
    internal static class ReferentialText
    {
        internal static List<string> Tokenize(string text)
        {
            var result = new List<string>(16);
            TokenizeInto(text, result);
            return result;
        }

        internal static void TokenizeInto(string text, List<string> into)
        {
            if (string.IsNullOrEmpty(text)) return;

            int start = -1;
            for (int i = 0; i < text.Length; i++)
            {
                bool alphanumeric = char.IsLetterOrDigit(text[i]);
                if (alphanumeric && start < 0)
                {
                    start = i;
                }
                else if (!alphanumeric && start >= 0)
                {
                    into.Add(text.Substring(start, i - start).ToLowerInvariant());
                    start = -1;
                }
            }

            if (start >= 0) into.Add(text.Substring(start).ToLowerInvariant());
        }

        internal static bool ContainsSequence(List<string> haystack, List<string> needle)
        {
            if (needle.Count == 0 || needle.Count > haystack.Count) return false;

            for (int i = 0; i <= haystack.Count - needle.Count; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Count; j++)
                {
                    if (!string.Equals(haystack[i + j], needle[j], StringComparison.Ordinal))
                    {
                        match = false;
                        break;
                    }
                }

                if (match) return true;
            }

            return false;
        }
    }
}
