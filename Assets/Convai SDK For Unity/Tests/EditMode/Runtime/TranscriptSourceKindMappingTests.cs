using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Domain.Models;
using Convai.Runtime.Presentation.Events;
using NUnit.Framework;

namespace Convai.Tests.EditMode
{
    public class TranscriptSourceKindMappingTests
    {
        private static readonly IReadOnlyDictionary<TranscriptTextSource, TranscriptSegmentSourceKind>
            ExpectedMappings = new Dictionary<TranscriptTextSource, TranscriptSegmentSourceKind>
            {
                [TranscriptTextSource.Unknown] = TranscriptSegmentSourceKind.Unknown,
                [TranscriptTextSource.InterimAsr] = TranscriptSegmentSourceKind.PlayerAsr,
                [TranscriptTextSource.AsrFinal] = TranscriptSegmentSourceKind.PlayerAsr,
                [TranscriptTextSource.ProcessedFinal] = TranscriptSegmentSourceKind.PlayerProcessedFinal,
                [TranscriptTextSource.TypedText] = TranscriptSegmentSourceKind.PlayerTypedText,
                [TranscriptTextSource.BotOutput] = TranscriptSegmentSourceKind.BotOutput,
                [TranscriptTextSource.BotPreview] = TranscriptSegmentSourceKind.BotLlmPreview,
                [TranscriptTextSource.LegacyBotTranscript] = TranscriptSegmentSourceKind.LegacyBotTranscript
            };

        private static readonly TestCaseData[] ModelSourceCases =
        {
            new(TranscriptSegmentSourceKind.Unknown, TranscriptLifecycle.Stable, TranscriptTextSource.Unknown),
            new(TranscriptSegmentSourceKind.PlayerAsr, TranscriptLifecycle.Streaming,
                TranscriptTextSource.InterimAsr),
            new(TranscriptSegmentSourceKind.PlayerAsr, TranscriptLifecycle.Stable, TranscriptTextSource.AsrFinal),
            new(TranscriptSegmentSourceKind.PlayerProcessedFinal, TranscriptLifecycle.Stable,
                TranscriptTextSource.ProcessedFinal),
            new(TranscriptSegmentSourceKind.PlayerTypedText, TranscriptLifecycle.Stable,
                TranscriptTextSource.TypedText),
            new(TranscriptSegmentSourceKind.BotOutput, TranscriptLifecycle.Stable, TranscriptTextSource.BotOutput),
            new(TranscriptSegmentSourceKind.BotLlmPreview, TranscriptLifecycle.Stable,
                TranscriptTextSource.BotPreview),
            new(TranscriptSegmentSourceKind.LegacyBotTranscript, TranscriptLifecycle.Stable,
                TranscriptTextSource.LegacyBotTranscript)
        };

        private static IEnumerable<TestCaseData> SourceKindCases()
        {
            foreach (KeyValuePair<TranscriptTextSource, TranscriptSegmentSourceKind> mapping in ExpectedMappings)
                yield return new TestCaseData(mapping.Key, mapping.Value)
                    .SetName($"ToSourceKind_{mapping.Key}_MapsTo_{mapping.Value}");
        }

        [TestCaseSource(nameof(SourceKindCases))]
        public void ToSourceKind_MapsEveryKnownSource(
            TranscriptTextSource source,
            TranscriptSegmentSourceKind expected)
        {
            Assert.AreEqual(expected, ConvaiTranscriptEventRelay.ToSourceKind(source));
        }

        [Test]
        public void ExpectedMappings_CoverEveryTranscriptTextSource()
        {
            var sources = (TranscriptTextSource[])Enum.GetValues(typeof(TranscriptTextSource));

            CollectionAssert.AreEquivalent(sources, ExpectedMappings.Keys);
        }

        [TestCaseSource(nameof(ModelSourceCases))]
        public void MapSource_MapsEveryKnownSource(
            TranscriptSegmentSourceKind sourceKind,
            TranscriptLifecycle lifecycle,
            TranscriptTextSource expected)
        {
            Assert.AreEqual(expected, TranscriptModelMapper.MapSource(sourceKind, lifecycle, string.Empty));
        }

        [Test]
        public void ModelSourceCases_CoverEveryTranscriptSegmentSourceKind()
        {
            TranscriptSegmentSourceKind[] expected =
                (TranscriptSegmentSourceKind[])Enum.GetValues(typeof(TranscriptSegmentSourceKind));
            TranscriptSegmentSourceKind[] covered = ModelSourceCases
                .Select(testCase => (TranscriptSegmentSourceKind)testCase.Arguments[0])
                .Distinct()
                .ToArray();

            CollectionAssert.AreEquivalent(expected, covered);
        }
    }
}
