using System;
using System.IO;
using System.Linq;
using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    /// <summary>
    ///     CC-13. The installer's managed block and rule 7 of the canonical <c>SKILL.md</c> state the
    ///     same maintained restriction: Play Mode, scene saves, and NavMesh bakes need an explicit
    ///     request. This fails if either side relaxes while the other stays strict. An untracked local
    ///     <c>AGENTS.md</c> is a personal customization and is deliberately not an input here.
    /// </summary>
    public sealed class ConvaiAgentPolicyConsistencyTests
    {
        /// <summary>Every restricted action, plus the words that make the restriction absolute.</summary>
        private static readonly string[] RequiredTokens =
        {
            "never",
            "play mode",
            "scene",
            "navmesh",
            "explicit"
        };

        /// <summary>Phrasings that would turn the restriction into a suggestion.</summary>
        private static readonly string[] PermissiveTokens =
        {
            "may enter",
            "may change",
            "can enter",
            "can change",
            "if needed",
            "if necessary",
            "when necessary",
            "without asking",
            "at your discretion",
            "automatically enter"
        };

        [Test]
        public void InstallerAndSkillStateTheSameMaintainedPlayModeRestriction()
        {
            string installerPolicy = ConvaiAgentInstructionsInstaller.PlayModePolicy;
            string skillRule = ReadSkillRuleSeven();

            AssertStrict("installer managed block", installerPolicy);
            AssertStrict("SKILL.md rule 7", skillRule);
        }

        [Test]
        public void ManagedBlockCarriesThePolicyForEveryClient()
        {
            string block = ConvaiAgentInstructionsInstaller.BuildManagedInstructions(
                "Packages/com.convai.convai-sdk-for-unity/AIAssistantSkills/convai-unity-sdk/SKILL.md");

            Assert.That(
                block,
                Does.Contain(ConvaiAgentInstructionsInstaller.PlayModePolicy),
                "The managed block must carry the maintained Play Mode restriction verbatim.");
        }

        private static void AssertStrict(string what, string text)
        {
            Assert.That(text, Is.Not.Null.And.Not.Empty, $"{what} has no Play Mode restriction to check.");
            string lowered = text.ToLowerInvariant();

            foreach (string token in RequiredTokens)
                Assert.That(
                    lowered,
                    Does.Contain(token),
                    $"{what} no longer restricts '{token}'. Keep it in step with the other side.");

            foreach (string token in PermissiveTokens)
                Assert.That(
                    lowered,
                    Does.Not.Contain(token),
                    $"{what} became permissive ('{token}'). The maintained policy requires an explicit request.");
        }

        /// <summary>Reads rule 7 out of the canonical skill the SDK actually ships.</summary>
        private static string ReadSkillRuleSeven()
        {
            Assert.That(
                ConvaiSkillSourceResolver.TryResolve(out ConvaiSkillSource source, out string error),
                Is.True,
                error);

            string[] lines = File.ReadAllLines(source.SkillFilePath);
            string rule = lines.FirstOrDefault(line =>
                line.TrimStart().StartsWith("7. ", StringComparison.Ordinal));
            Assert.That(rule, Is.Not.Null, $"'{source.SkillFilePath}' has no numbered rule 7.");
            return rule;
        }
    }
}
