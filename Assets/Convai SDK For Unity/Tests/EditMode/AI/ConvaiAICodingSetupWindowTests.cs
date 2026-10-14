using System.Linq;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiAICodingSetupWindowTests
    {
        [Test]
        public void AssistantRepairTargetsConfiguredSupportedVersion()
        {
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.AssistantPackageIdentifier,
                Is.EqualTo("com.unity.ai.assistant@2.14.0-pre.1"));
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.IsAssistantCompatible("2.14.0-pre.1"),
                Is.True);
        }

        [TestCase(null, true)]
        [TestCase("invalid", true)]
        [TestCase("2.12.9", true)]
        [TestCase("2.13.0-pre.2", false)]
        [TestCase("2.14.0", false)]
        [TestCase("3.0.0", true)]
        public void AssistantRepairIsOfferedOnlyForMissingOrUnsupportedVersions(string version, bool expected) =>
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.CanRepairAssistant(version),
                Is.EqualTo(expected));

        [TestCase(null, false, false)]
        [TestCase("2.12.9", false, false)]
        [TestCase("2.13.0-pre.2", false, true)]
        [TestCase("2.13.0", false, true)]
        [TestCase("2.14.0", true, false)]
        public void ToolRegistrationRepairRequiresCompatibleAssistant(
            string assistantVersion,
            bool toolsReady,
            bool expected) =>
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.CanRepairToolRegistration(
                    assistantVersion,
                    toolsReady),
                Is.EqualTo(expected));

        [Test]
        public void ToolRegistryRefreshFindsUnityMcpRegistry()
        {
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.TryRefreshToolRegistry(out string error),
                Is.True,
                error);
            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.HasExpectedRegisteredConvaiTools(
                    out int toolCount,
                    out string issue),
                Is.True,
                issue);
            Assert.That(toolCount, Is.EqualTo(Convai.Editor.ConvaiAICodingSetupWindow.ExpectedToolCount));
        }

        [Test]
        public void ToolCatalogRejectsWrongNamesEvenWhenCountMatches()
        {
            string[] wrongNames = Convai.Editor.ConvaiAICodingSetupWindow.ExpectedToolNames.ToArray();
            // The stand-in has to be a name no tool will ever carry. It used to be
            // Convai_ConfigureBodyAnimation, which stopped being wrong the moment that tool
            // shipped — and a test whose wrong answer quietly becomes right tests nothing.
            wrongNames[0] = "Convai_NoSuchToolWillEverBeNamedThis";

            Assert.That(
                Convai.Editor.ConvaiAICodingSetupWindow.HasExpectedToolNames(wrongNames, out string issue),
                Is.False);
            Assert.That(issue, Does.Contain("missing Convai_BootstrapScene"));
            Assert.That(issue, Does.Contain("unexpected Convai_NoSuchToolWillEverBeNamedThis"));
        }
    }
}
