using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Editor.Inspectors;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Ladder-parity guard: proves the editor-side single-target mirror
    ///     (<see cref="ConvaiActionTargetPhraseMatcher" /> — behind the Convai Action Target
    ///     inspector's "Check a phrase" tool and the Actions Editor's dry-run step labels) agrees
    ///     with the <em>real</em> runtime resolution ladder
    ///     (<see cref="ConvaiResolvedActionTarget.Resolve" />) for single-candidate scenarios:
    ///     whenever the runtime resolves, the mirror reports a step, and vice versa — across the
    ///     exact / alias / normalized / contains / no-match rungs, including the untrimmed-alias
    ///     semantics the mirror was aligned to. If either side's matching rules drift, this fixture
    ///     fails.
    /// </summary>
    [TestFixture]
    public class ConvaiActionResolutionLadderParityTests
    {
        private static ConvaiActionConfig SingleObjectConfig(string name, params string[] aliases) =>
            new()
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = name, Aliases = new List<string>(aliases) }
                }
            };

        private static ConvaiActionConfig SingleCharacterConfig(string name, params string[] aliases) =>
            new()
            {
                Characters = new List<ConvaiActionCharacterDefinition>
                {
                    new() { Name = name, Aliases = new List<string>(aliases) }
                }
            };

        /// <summary>
        ///     Asserts the runtime ladder and the editor mirror agree for one single-candidate
        ///     scenario: same matched/not-matched verdict, and (when matched) the mirror reports
        ///     <paramref name="expectedStep" />.
        /// </summary>
        private static void AssertObjectParity(
            string phrase,
            string name,
            string[] aliases,
            ConvaiActionTargetPhraseMatcher.MatchStep expectedStep)
        {
            ConvaiActionConfig config = SingleObjectConfig(name, aliases);
            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                phrase, config, (ConvaiActionTargetRequirement?)null);
            ConvaiActionTargetPhraseMatcher.MatchResult mirror =
                ConvaiActionTargetPhraseMatcher.Match(phrase, name, aliases);

            bool runtimeMatched = resolved != null;
            bool mirrorMatched = mirror.Step != ConvaiActionTargetPhraseMatcher.MatchStep.None;
            Assert.AreEqual(runtimeMatched, mirrorMatched,
                $"Runtime ladder and editor mirror disagree on matchability for phrase '{phrase}' vs name '{name}'.");

            Assert.AreEqual(expectedStep, mirror.Step,
                $"Editor mirror reported an unexpected step for phrase '{phrase}' vs name '{name}'.");

            if (runtimeMatched)
                Assert.AreEqual(name, resolved.Name);
        }

        [Test]
        public void Exact_BothMatch()
        {
            AssertObjectParity("red cube", "Red Cube", new string[0],
                ConvaiActionTargetPhraseMatcher.MatchStep.Exact);
        }

        [Test]
        public void Alias_BothMatch_BeforeNormalizedOrContains()
        {
            AssertObjectParity("the box", "Crate01", new[] { "the box", "container" },
                ConvaiActionTargetPhraseMatcher.MatchStep.Alias);
        }

        [Test]
        public void Normalized_BothMatch_ArticleAndSpacingIgnored()
        {
            AssertObjectParity("the  red cube", "Red Cube", new string[0],
                ConvaiActionTargetPhraseMatcher.MatchStep.Normalized);
        }

        [Test]
        public void Contains_BothMatch_PhraseWithinName()
        {
            AssertObjectParity("red cube", "Big Red Cube", new string[0],
                ConvaiActionTargetPhraseMatcher.MatchStep.Contains);
        }

        [Test]
        public void Contains_BothMatch_NameWithinPhrase()
        {
            AssertObjectParity("go to the lamp", "Lamp", new string[0],
                ConvaiActionTargetPhraseMatcher.MatchStep.Contains);
        }

        [Test]
        public void NoMatch_BothMiss()
        {
            AssertObjectParity("banana", "Red Cube", new[] { "box" },
                ConvaiActionTargetPhraseMatcher.MatchStep.None);
        }

        [Test]
        public void PaddedAlias_MatchesOnBothSides()
        {
            // Stray padding in an alias is invisible in the inspector, so an alias that silently
            // matched nothing was a whole entry doing no work with no way to see why. Both the
            // runtime ladder and this mirror now trim, and this test is what keeps them together.
            AssertObjectParity("box", "Crate01", new[] { " box " },
                ConvaiActionTargetPhraseMatcher.MatchStep.Alias);
        }

        [Test]
        public void ExactWinsOverAlias_WhenBothWouldMatch()
        {
            AssertObjectParity("Lamp", "Lamp", new[] { "Lamp" },
                ConvaiActionTargetPhraseMatcher.MatchStep.Exact);
        }

        [Test]
        public void CharacterLadder_AgreesToo()
        {
            ConvaiActionConfig config = SingleCharacterConfig("Guard Captain", "the captain");
            ConvaiResolvedActionTarget resolved = ConvaiResolvedActionTarget.Resolve(
                "the captain", config, (ConvaiActionTargetRequirement?)null);
            ConvaiActionTargetPhraseMatcher.MatchResult mirror =
                ConvaiActionTargetPhraseMatcher.Match("the captain", "Guard Captain", new[] { "the captain" });

            Assert.IsNotNull(resolved);
            Assert.AreEqual("Guard Captain", resolved.Name);
            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Alias, mirror.Step);
        }

        [Test]
        public void UnavailableTarget_RuntimeOnlyFilter_SkipsResolution()
        {
            // Availability is a runtime overlay, filtered before the ladder's string steps run;
            // the single-target mirror deliberately has no availability concept (the dry-run tool
            // resolves through the real ladder, which applies it).
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Red Cube", Available = false }
                }
            };

            Assert.IsNull(ConvaiResolvedActionTarget.Resolve(
                "red cube", config, (ConvaiActionTargetRequirement?)null));
        }

        [Test]
        public void HasUsableTarget_RespectsTheActionsAcceptedTargetKind()
        {
            ConvaiActionConfig objectsOnly = SingleObjectConfig("Red Cube");

            Assert.That(ConvaiActionEditTimeResolver.HasUsableTarget(
                objectsOnly, ConvaiActionTargetRequirement.Object), Is.True);
            Assert.That(ConvaiActionEditTimeResolver.HasUsableTarget(
                objectsOnly, ConvaiActionTargetRequirement.Either), Is.True);
            Assert.That(ConvaiActionEditTimeResolver.HasUsableTarget(
                objectsOnly, ConvaiActionTargetRequirement.Character), Is.False);
        }

        [Test]
        public void HasUsableTarget_NoneNeedsNoSceneTarget_AndUnavailableEntriesDoNotCount()
        {
            var unavailable = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Red Cube", Available = false }
                }
            };

            Assert.That(ConvaiActionEditTimeResolver.HasUsableTarget(
                null, ConvaiActionTargetRequirement.None), Is.True);
            Assert.That(ConvaiActionEditTimeResolver.HasUsableTarget(
                unavailable, ConvaiActionTargetRequirement.Object), Is.False);
        }
    }
}
