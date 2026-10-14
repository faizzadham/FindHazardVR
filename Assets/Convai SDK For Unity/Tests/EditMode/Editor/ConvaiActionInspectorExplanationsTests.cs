using System;
using System.Text.RegularExpressions;
using Convai.Editor.Inspectors;
using Convai.Runtime.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the pure string-mapping explanations shown under the Actions component
    ///     inspectors' mode/policy dropdowns: every enum value must resolve to a non-empty,
    ///     beginner-readable sentence, and none of them may use banned jargon ("AI", "executor" —
    ///     the standing terminology rule for user-facing text).
    /// </summary>
    [TestFixture]
    public class ConvaiActionInspectorExplanationsTests
    {
        private static readonly string[] BannedWords = { "ai", "executor" };

        [Test]
        public void FeedbackMode_EveryValue_HasNonEmptyExplanation()
        {
            foreach (ConvaiActionFeedbackMode mode in Enum.GetValues(typeof(ConvaiActionFeedbackMode)))
            {
                string explanation = ConvaiActionFeedbackModeExplanations.Explain(mode);
                Assert.IsFalse(string.IsNullOrWhiteSpace(explanation), $"{mode} has no explanation.");
                AssertNoBannedWords(explanation, mode.ToString());
            }
        }

        [Test]
        public void BatchPolicy_EveryValue_HasNonEmptyExplanation()
        {
            foreach (ConvaiActionBatchPolicy policy in Enum.GetValues(typeof(ConvaiActionBatchPolicy)))
            {
                string explanation = ConvaiActionDispatcherPolicyExplanations.ExplainBatchPolicy(policy);
                Assert.IsFalse(string.IsNullOrWhiteSpace(explanation), $"{policy} has no explanation.");
                AssertNoBannedWords(explanation, policy.ToString());
            }
        }

        [Test]
        public void FailurePolicy_EveryValue_HasNonEmptyExplanation()
        {
            foreach (ConvaiActionBatchFailurePolicy policy in Enum.GetValues(typeof(ConvaiActionBatchFailurePolicy)))
            {
                string explanation = ConvaiActionDispatcherPolicyExplanations.ExplainFailurePolicy(policy);
                Assert.IsFalse(string.IsNullOrWhiteSpace(explanation), $"{policy} has no explanation.");
                AssertNoBannedWords(explanation, policy.ToString());
            }
        }

        [Test]
        public void DistinctEnumValues_ProduceDistinctExplanations()
        {
            // A guard against copy-paste regressions: every mode/policy must read differently from
            // its siblings, or the "explanation under the selected value" affordance is pointless.
            CollectionAssert.AllItemsAreUnique(new[]
            {
                ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.Off),
                ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.SilentContext),
                ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.NarrateInCharacter),
                ConvaiActionFeedbackModeExplanations.Explain(ConvaiActionFeedbackMode.ScriptedSpeech)
            });

            CollectionAssert.AllItemsAreUnique(new[]
            {
                ConvaiActionDispatcherPolicyExplanations.ExplainBatchPolicy(ConvaiActionBatchPolicy.Queue),
                ConvaiActionDispatcherPolicyExplanations.ExplainBatchPolicy(ConvaiActionBatchPolicy.ReplaceCurrent),
                ConvaiActionDispatcherPolicyExplanations.ExplainBatchPolicy(ConvaiActionBatchPolicy.DropIncoming)
            });

            CollectionAssert.AllItemsAreUnique(new[]
            {
                ConvaiActionDispatcherPolicyExplanations.ExplainFailurePolicy(ConvaiActionBatchFailurePolicy.StopBatch),
                ConvaiActionDispatcherPolicyExplanations.ExplainFailurePolicy(ConvaiActionBatchFailurePolicy.ContinueBatch)
            });
        }

        [Test]
        public void MissingActionFeedback_IsClearlyOptional_AndSaysTheActionStillRuns()
        {
            string effect = ConvaiActionAnswerDeliveryExplanations.DescribeEffect(
                ConvaiActionAnswerDelivery.UseCharacterSetting, null, "Sofia");
            ConvaiActionAnswerAdvisory advisory = ConvaiActionAnswerDeliveryExplanations.FindAdvisory(
                ConvaiActionAnswerDelivery.UseCharacterSetting, null, "Sofia");

            Assert.That(effect, Does.Contain("still runs normally"));
            Assert.That(advisory.Exists, Is.True);
            Assert.That(advisory.IsWarning, Is.False,
                "Optional Action Feedback must not present a normally working action as broken.");
            Assert.That(advisory.Title, Does.StartWith("Optional:"));
            Assert.That(advisory.Message, Does.Contain("will still run normally"));
            Assert.That(advisory.Message, Does.Contain("only if you want"));
        }

        private static void AssertNoBannedWords(string explanation, string context)
        {
            string lower = explanation.ToLowerInvariant();
            foreach (string banned in BannedWords)
            {
                bool hit = Regex.IsMatch(lower, $@"\b{banned}\b");
                Assert.IsFalse(hit, $"{context} explanation contains banned word '{banned}': \"{explanation}\"");
            }
        }
    }
}
