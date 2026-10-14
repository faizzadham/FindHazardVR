using System.Collections.Generic;
using Convai.Editor.Inspectors;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionTargetPhraseMatcher" /> — the pure step-matching and
    ///     effective-name logic behind <c>ConvaiActionTargetEditor</c>'s "Check a phrase"
    ///     mini-tool: exact/alias/normalized/contains/no-match step selection, the exact wording
    ///     each step reports, and the authored-name-vs-GameObject-name fallback.
    /// </summary>
    [TestFixture]
    public class ConvaiActionTargetPhraseMatcherTests
    {
        [Test]
        public void Match_ExactName_ReturnsExactStep()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("Desk Lamp", "Desk Lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Exact, result.Step);
            Assert.AreEqual("Desk Lamp", result.MatchedText);
        }

        [Test]
        public void Match_ExactName_IsCaseInsensitive()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("desk lamp", "Desk Lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Exact, result.Step);
        }

        [Test]
        public void Match_Alias_ReturnsAliasStep_BeforeNormalizedOrContains()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result = ConvaiActionTargetPhraseMatcher.Match(
                "lamp", "Desk Lamp", new List<string> { "lamp", "light" });

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Alias, result.Step);
            Assert.AreEqual("lamp", result.MatchedText);
        }

        [Test]
        public void Match_Normalized_StripsLeadingArticleAndCollapsesSpaces()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("the   desk lamp", "Desk Lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Normalized, result.Step);
        }

        [Test]
        public void Match_Contains_MatchesPartialPhrase()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("lamp", "Antique Desk Lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Contains, result.Step);
            Assert.AreEqual("Antique Desk Lamp", result.MatchedText);
        }

        [Test]
        public void Match_Contains_MatchesWhenPhraseContainsName()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("go to the lamp please", "lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Contains, result.Step);
        }

        [Test]
        public void Match_NoMatch_ReturnsNoneStep()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("chair", "Desk Lamp", new List<string> { "light" });

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.None, result.Step);
            Assert.IsNull(result.MatchedText);
        }

        [Test]
        public void Match_EmptyPhrase_ReturnsNoneStep()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match(string.Empty, "Desk Lamp", new List<string>());

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.None, result.Step);
        }

        [Test]
        public void Match_NullAliases_DoesNotThrow_AndStillMatchesExact()
        {
            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match("Desk Lamp", "Desk Lamp", null);

            Assert.AreEqual(ConvaiActionTargetPhraseMatcher.MatchStep.Exact, result.Step);
        }

        [Test]
        public void Describe_Exact_ReportsStepOneWording()
        {
            var result = new ConvaiActionTargetPhraseMatcher.MatchResult(
                ConvaiActionTargetPhraseMatcher.MatchStep.Exact, "Desk Lamp");

            Assert.AreEqual("Matches: exact name (step 1 — Exact Name).", ConvaiActionTargetPhraseMatcher.Describe(result));
        }

        [Test]
        public void Describe_Alias_ReportsStepTwoWordingWithMatchedAlias()
        {
            var result = new ConvaiActionTargetPhraseMatcher.MatchResult(
                ConvaiActionTargetPhraseMatcher.MatchStep.Alias, "lamp");

            Assert.AreEqual("Matches: alias 'lamp' (step 2 — Alias).", ConvaiActionTargetPhraseMatcher.Describe(result));
        }

        [Test]
        public void Describe_Normalized_ReportsStepThreeWording()
        {
            var result = new ConvaiActionTargetPhraseMatcher.MatchResult(
                ConvaiActionTargetPhraseMatcher.MatchStep.Normalized, "Desk Lamp");

            StringAssert.Contains("step 3 — Normalized", ConvaiActionTargetPhraseMatcher.Describe(result));
        }

        [Test]
        public void Describe_Contains_ReportsStepFourWordingWithMatchedText()
        {
            var result = new ConvaiActionTargetPhraseMatcher.MatchResult(
                ConvaiActionTargetPhraseMatcher.MatchStep.Contains, "Antique Desk Lamp");

            Assert.AreEqual(
                "Matches: partial match with 'Antique Desk Lamp' (step 4 — Contains).",
                ConvaiActionTargetPhraseMatcher.Describe(result));
        }

        [Test]
        public void Describe_None_ReportsNoMatchWording()
        {
            var result = new ConvaiActionTargetPhraseMatcher.MatchResult(
                ConvaiActionTargetPhraseMatcher.MatchStep.None, null);

            Assert.AreEqual(
                "No match — try the exact name or one of the aliases.",
                ConvaiActionTargetPhraseMatcher.Describe(result));
        }

        // ── EffectiveName ──────────────────────────────────────────────────────

        [Test]
        public void EffectiveName_UsesAuthoredName_WhenSet()
        {
            Assert.AreEqual("Desk Lamp", ConvaiActionTargetPhraseMatcher.EffectiveName("Desk Lamp", "Lamp01"));
        }

        [Test]
        public void EffectiveName_FallsBackToGameObjectName_WhenAuthoredNameBlank()
        {
            Assert.AreEqual("Lamp01", ConvaiActionTargetPhraseMatcher.EffectiveName(string.Empty, "Lamp01"));
            Assert.AreEqual("Lamp01", ConvaiActionTargetPhraseMatcher.EffectiveName(null, "Lamp01"));
            Assert.AreEqual("Lamp01", ConvaiActionTargetPhraseMatcher.EffectiveName("   ", "Lamp01"));
        }

        [Test]
        public void EffectiveName_NullGameObjectName_ReturnsEmptyString()
        {
            Assert.AreEqual(string.Empty, ConvaiActionTargetPhraseMatcher.EffectiveName(null, null));
        }
    }
}
