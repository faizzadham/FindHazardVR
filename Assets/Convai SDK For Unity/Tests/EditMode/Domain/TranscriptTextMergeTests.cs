using Convai.Domain.Models;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    public class TranscriptTextMergeTests
    {
        [TestCase(null, null, "")]
        [TestCase(null, "incoming", "incoming")]
        [TestCase(" \t", "incoming", "incoming")]
        [TestCase("existing", null, "existing")]
        [TestCase("existing", "\r\n", "existing")]
        public void Append_Returns_Other_Side_When_Either_Side_Has_No_Text(
            string existing,
            string incoming,
            string expected)
        {
            Assert.AreEqual(expected, TranscriptTextMerge.Append(existing, incoming));
        }

        [TestCase("hello", "world", "hello world")]
        [TestCase("hello ", "world", "hello world")]
        [TestCase("hello", " world", "hello world")]
        public void Append_Joins_Text_At_Whitespace_Boundary(string existing, string incoming, string expected)
        {
            Assert.AreEqual(expected, TranscriptTextMerge.Append(existing, incoming));
        }

        [TestCase(null, null, "")]
        [TestCase(null, "incoming", "incoming")]
        [TestCase(" \t", "incoming", "incoming")]
        [TestCase("existing", null, "existing")]
        [TestCase("existing", "\r\n", "existing")]
        public void Merge_Returns_Other_Side_When_Either_Side_Has_No_Text(
            string existing,
            string incoming,
            string expected)
        {
            Assert.AreEqual(expected, TranscriptTextMerge.Merge(existing, incoming));
        }

        [TestCase("hello", "world", "hello world")]
        [TestCase("hello ", "world", "hello world")]
        [TestCase("hello", " world", "hello world")]
        public void Merge_Joins_Text_At_Whitespace_Boundary(string existing, string incoming, string expected)
        {
            Assert.AreEqual(expected, TranscriptTextMerge.Merge(existing, incoming));
        }

        [TestCase("hello", "hello world", "hello world")]
        [TestCase("hello", "hello", "hello hello")]
        [TestCase("hello world", "hello", "hello world hello")]
        public void Merge_Replaces_Only_When_Incoming_Extends_Existing(
            string existing,
            string incoming,
            string expected)
        {
            Assert.AreEqual(expected, TranscriptTextMerge.Merge(existing, incoming));
        }
    }
}
