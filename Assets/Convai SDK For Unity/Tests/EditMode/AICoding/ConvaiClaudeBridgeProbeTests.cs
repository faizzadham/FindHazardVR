using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    /// <summary>
    ///     The probe must find the Assistant relay in any of Claude's three configuration locations,
    ///     respect local &gt; project &gt; user precedence, and refuse a Pipeline-only entry. The entry
    ///     key is never the matcher: Unity's generated entry is often called <c>unity-mcp</c>, but a
    ///     differently named entry with the same command is just as effective.
    /// </summary>
    public sealed class ConvaiClaudeBridgeProbeTests
    {
        private const string ProjectRoot = "/Users/dev/UnityProject";
        private const string RelayRoot = "/Users/dev/.unity/relay";
        private const string RelayCommand = RelayRoot + "/relay_mac_arm64";

        [Test]
        public void ProjectLocalEntryIsDetected()
        {
            string user = "{\"projects\":{\"" + ProjectRoot + "\":{\"mcpServers\":{" + Relay("unity-mcp") + "}}}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.Detected));
            Assert.That(status.Scope, Is.EqualTo(ConvaiClaudeConfigScope.ProjectLocal));
            Assert.That(status.ServerName, Is.EqualTo("unity-mcp"));
        }

        [Test]
        public void UserEntryIsDetectedWithoutAnyProjectLocalEntry()
        {
            string user = "{\"mcpServers\":{" + Relay("unity-assistant") + "}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.Detected));
            Assert.That(status.Scope, Is.EqualTo(ConvaiClaudeConfigScope.User));
            Assert.That(status.ServerName, Is.EqualTo("unity-assistant"));
        }

        [Test]
        public void ProjectSharedMcpJsonEntryIsDetected()
        {
            string project = "{\"mcpServers\":{" + Relay("shared-unity") + "}}";

            ConvaiClaudeBridgeStatus status = Evaluate(null, project);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.Detected));
            Assert.That(status.Scope, Is.EqualTo(ConvaiClaudeConfigScope.ProjectShared));
        }

        [Test]
        public void APipelineOnlyEntryIsRejectedAndNamed()
        {
            string user = "{\"mcpServers\":{\"unity-mcp\":{\"command\":\"/usr/local/bin/unity\",\"args\":[\"mcp\"]}}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.NotConfigured));
            Assert.That(status.Detail, Does.Contain("Pipeline"));
        }

        [Test]
        public void AProjectLocalOverrideHidesTheUserEntryItReplaces()
        {
            string user =
                "{\"mcpServers\":{" + Relay("unity-mcp") + "}," +
                "\"projects\":{\"" + ProjectRoot + "\":{\"mcpServers\":{" +
                "\"unity-mcp\":{\"command\":\"/usr/local/bin/unity\",\"args\":[\"mcp\"]}}}}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, null);

            Assert.That(
                status.State,
                Is.EqualTo(ConvaiClaudeBridgeState.NotConfigured),
                "The project-local entry overrides the user entry of the same name, so the user relay is not effective.");
        }

        [Test]
        public void AProjectSharedEntryOverridesTheUserEntryOfTheSameName()
        {
            string user = "{\"mcpServers\":{\"unity-mcp\":{\"command\":\"/usr/local/bin/unity\",\"args\":[\"mcp\"]}}}";
            string project = "{\"mcpServers\":{" + Relay("unity-mcp") + "}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, project);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.Detected));
            Assert.That(status.Scope, Is.EqualTo(ConvaiClaudeConfigScope.ProjectShared));
        }

        [Test]
        public void AnEntryForAnotherProjectIsNotUsed()
        {
            string user = "{\"projects\":{\"/Users/dev/OtherProject\":{\"mcpServers\":{" + Relay("unity-mcp") + "}}}}";

            Assert.That(Evaluate(user, null).State, Is.EqualTo(ConvaiClaudeBridgeState.NotConfigured));
        }

        [Test]
        public void TheProjectKeyMatchesDespiteATrailingSeparator()
        {
            string user = "{\"projects\":{\"" + ProjectRoot + "/\":{\"mcpServers\":{" + Relay("unity-mcp") + "}}}}";

            Assert.That(Evaluate(user, null).State, Is.EqualTo(ConvaiClaudeBridgeState.Detected));
        }

        [Test]
        public void NoConfigurationAtAllReportsNotConfigured()
        {
            ConvaiClaudeBridgeStatus status = Evaluate(null, null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.NotConfigured));
            Assert.That(status.Scope, Is.Null);
        }

        [Test]
        public void UnparseableConfigurationReportsUnableToInspectWithoutEchoingIt()
        {
            ConvaiClaudeBridgeStatus status = Evaluate("{ not json", null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.UnableToInspect));
            Assert.That(status.Detail, Does.Not.Contain("not json"));
        }

        [Test]
        public void AParseErrorDetailReportsTheLocationWithoutTheOffendingValue()
        {
            // Newtonsoft's own message is "Input string '123DEADBEEF' is not a valid number.
            // Path 'apiKey', ...", which copies a configuration value into the UI diagnostic.
            ConvaiClaudeBridgeStatus status = Evaluate("{\"apiKey\":123DEADBEEF}", null);

            Assert.That(status.State, Is.EqualTo(ConvaiClaudeBridgeState.UnableToInspect));
            Assert.That(status.Detail, Does.Not.Contain("DEADBEEF"));
            Assert.That(status.Detail, Does.Not.Contain("apiKey"));
            Assert.That(status.Detail, Does.Contain("line 1"), "The location is the useful part and is safe to show.");
        }

        [TestCase("[]", TestName = "CommandIsAnArray")]
        [TestCase("{}", TestName = "CommandIsAnObject")]
        [TestCase("7", TestName = "CommandIsANumber")]
        [TestCase("null", TestName = "CommandIsNull")]
        public void AMalformedCommandOnAnUnrelatedEntryDoesNotHideTheRelay(string command)
        {
            string user =
                "{\"mcpServers\":{" +
                "\"other\":{\"command\":" + command + ",\"args\":[]}," +
                Relay("unity-mcp") + "}}";

            ConvaiClaudeBridgeStatus status = Evaluate(user, null);

            Assert.That(
                status.State,
                Is.EqualTo(ConvaiClaudeBridgeState.Detected),
                "A malformed neighbour must be skipped, not throw out of the inspection.");
            Assert.That(status.ServerName, Is.EqualTo("unity-mcp"));
        }

        [TestCase("[]", TestName = "CommandIsAnArray")]
        [TestCase("{}", TestName = "CommandIsAnObject")]
        public void AMalformedCommandAloneReportsNotConfiguredRatherThanThrowing(string command)
        {
            string user = "{\"mcpServers\":{\"other\":{\"command\":" + command + ",\"args\":[\"--mcp\"]}}}";

            // The Pipeline fallback reads 'command' a second time, so it has to be type-safe too.
            Assert.That(Evaluate(user, null).State, Is.EqualTo(ConvaiClaudeBridgeState.NotConfigured));
        }

        [TestCase(RelayRoot + "/relay_mac_arm64", true, TestName = "MacArm64Relay")]
        [TestCase(RelayRoot + "/relay_win.exe", true, TestName = "WindowsRelay")]
        [TestCase(RelayRoot + "/v1/relay_linux", true, TestName = "RelayInANestedVersionFolder")]
        [TestCase(RelayRoot + "/relay_unknown", false, TestName = "UnknownFilename")]
        [TestCase("/Users/dev/.unity/relay-backup/relay_mac_arm64", false, TestName = "SiblingFolderThatStartsWithRelay")]
        [TestCase("/Users/dev/relay/relay_mac_arm64", false, TestName = "OutsideTheUnityRelayDirectory")]
        [TestCase("/tmp/relay/../.unity/relay/relay_mac_arm64", false, TestName = "RelativeTraversalIsNotResolvedSoItIsRejected")]
        // A lexical prefix test alone accepts these: they start inside the relay root and then
        // leave it. Claude resolves the '..' and runs the escaped command.
        [TestCase(RelayRoot + "/../../other/relay_linux", false, TestName = "EscapesTheRelayRootThroughParentSegments")]
        [TestCase(RelayRoot + "/../relay_mac_arm64", false, TestName = "EscapesToTheRelayRootsParent")]
        [TestCase(RelayRoot + "/v1/../../evil/relay_win.exe", false, TestName = "EscapesFromANestedFolder")]
        public void TheRelayMatcherChecksDirectoryBoundariesAndTheFilename(string command, bool expected) =>
            Assert.That(
                ConvaiClaudeBridgeProbe.IsAssistantRelay(command, new[] { "--mcp" }, RelayRoot),
                Is.EqualTo(expected));

        [Test]
        public void TheRelayMatcherRequiresExactlyTheMcpArgument()
        {
            Assert.That(ConvaiClaudeBridgeProbe.IsAssistantRelay(RelayCommand, new[] { "--mcp" }, RelayRoot), Is.True);
            Assert.That(ConvaiClaudeBridgeProbe.IsAssistantRelay(RelayCommand, new string[0], RelayRoot), Is.False);
            Assert.That(
                ConvaiClaudeBridgeProbe.IsAssistantRelay(RelayCommand, new[] { "--relay", "--mcp" }, RelayRoot),
                Is.False);
            Assert.That(
                ConvaiClaudeBridgeProbe.IsAssistantRelay(RelayCommand, new[] { "--mcp", "--port", "9002" }, RelayRoot),
                Is.False);
            Assert.That(ConvaiClaudeBridgeProbe.IsAssistantRelay(RelayCommand, new[] { "mcp" }, RelayRoot), Is.False);
        }

        [Test]
        public void AWindowsStyleRelayPathNormalizesToAMatch() =>
            Assert.That(
                ConvaiClaudeBridgeProbe.IsAssistantRelay(
                    "C:\\Users\\dev\\.unity\\relay\\relay_win.exe",
                    new[] { "--mcp" },
                    "C:/Users/dev/.unity/relay"),
                Is.True);

        private static ConvaiClaudeBridgeStatus Evaluate(string userConfig, string projectConfig) =>
            ConvaiClaudeBridgeProbe.Evaluate(userConfig, projectConfig, ProjectRoot, RelayRoot);

        private static string Relay(string name) =>
            "\"" + name + "\":{\"command\":\"" + RelayCommand + "\",\"args\":[\"--mcp\"]}";
    }
}
