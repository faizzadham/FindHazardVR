using System;
using System.Collections.Generic;
using System.IO;
using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    public sealed class ConvaiCodexInstallationProbeTests
    {
        private const string UnitySource = "\"source\":{\"source\":\"remote\",\"id\":\"plugins_6aa1c02597c081918e358d72f65bd772\"}";

        [TestCase(false)]
        [TestCase(true)]
        public void ScansBothWindowsAppsAndSelectsFirstExistingBundledCli(bool firstAppHasCli)
        {
            string root = Path.Combine(Path.GetTempPath(), "convai-codex-probe-" + Guid.NewGuid().ToString("N"));
            try
            {
                string codexApp = Path.Combine(root, "Codex", "Codex.exe");
                string chatGptApp = Path.Combine(root, "ChatGPT", "ChatGPT.exe");
                string codexCli = Path.Combine(root, "Codex", "resources", "codex.exe");
                string chatGptCli = Path.Combine(root, "ChatGPT", "resources", "codex.exe");
                Directory.CreateDirectory(Path.GetDirectoryName(codexCli));
                Directory.CreateDirectory(Path.GetDirectoryName(chatGptCli));
                File.WriteAllText(codexApp, string.Empty);
                File.WriteAllText(chatGptApp, string.Empty);
                File.WriteAllText(chatGptCli, string.Empty);
                if (firstAppHasCli) File.WriteAllText(codexCli, string.Empty);

                var candidates = new List<string>();
                ConvaiInstallationStatus status = ConvaiCodexInstallationProbe.InspectWindowsDesktop(
                    candidates, protocol => $"\"{(protocol == "codex" ? codexApp : chatGptApp)}\" \"%1\"");

                Assert.That(status.State, Is.EqualTo(ConvaiInstallationState.Detected));
                Assert.That(status.Detail, Does.Contain(codexApp).And.Contain(chatGptApp));
                Assert.That(candidates, Is.EqualTo(new[] { codexCli, chatGptCli }));
                Assert.That(ConvaiCodexInstallationProbe.FindExecutable(candidates, root, true),
                    Is.EqualTo(firstAppHasCli ? codexCli : chatGptCli));
            }
            finally
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [Test]
        public void PathSearchSkipsInvalidEntriesAndUnquotesDirectories()
        {
            string root = Path.Combine(Path.GetTempPath(), "convai-codex-path-" + Guid.NewGuid().ToString("N"));
            string originalPath = Environment.GetEnvironmentVariable("PATH");
            try
            {
                Directory.CreateDirectory(root);
                string cli = Path.Combine(root, "codex.exe");
                File.WriteAllText(cli, string.Empty);
                Environment.SetEnvironmentVariable("PATH",
                    Path.Combine(root, "bad<|>dir") + Path.PathSeparator + "\"" + root + "\"");

                Assert.That(ConvaiCodexInstallationProbe.FindExecutable(Array.Empty<string>(), root, true), Is.EqualTo(cli));
            }
            finally
            {
                Environment.SetEnvironmentVariable("PATH", originalPath);
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        }

        [TestCase(true, true, (int)ConvaiInstallationState.Installed)]
        [TestCase(true, false, (int)ConvaiInstallationState.Disabled)]
        [TestCase(false, false, (int)ConvaiInstallationState.NotInstalled)]
        public void RecognizesExplicitRemotePluginStates(bool installed, bool enabled, int expected)
        {
            string plugin = "{" + UnitySource + ",\"installed\":" + installed.ToString().ToLowerInvariant() +
                            ",\"enabled\":" + enabled.ToString().ToLowerInvariant() + "}";
            string json = installed ? "{\"installed\":[" + plugin + "],\"available\":[]}"
                : "{\"installed\":[],\"available\":[" + plugin + "]}";
            Assert.That(ConvaiCodexInstallationProbe.ParsePluginList(json).State, Is.EqualTo((ConvaiInstallationState)expected));
        }

        [TestCase("{}")]
        [TestCase("not json")]
        [TestCase("{\"installed\":[],\"available\":[]}")]
        [TestCase("{\"installed\":[{\"name\":\"unity\",\"installed\":true,\"enabled\":true}],\"available\":[]}")]
        public void MissingOrUnsupportedEvidenceDoesNotReportUninstalled(string json) =>
            Assert.That(ConvaiCodexInstallationProbe.ParsePluginList(json).State, Is.EqualTo(ConvaiInstallationState.Unknown));

        [TestCase("\"installed\":true")]
        [TestCase("\"installed\":\"true\",\"enabled\":true")]
        [TestCase("\"enabled\":true")]
        public void IncompleteStatesAreUnknown(string fields)
        {
            string json = "{\"installed\":[{" + UnitySource + "," + fields + "}],\"available\":[]}";
            Assert.That(ConvaiCodexInstallationProbe.ParsePluginList(json).State, Is.EqualTo(ConvaiInstallationState.Unknown));
        }

        [Test]
        public void ConflictingEntriesAreUnknown()
        {
            string entry = "{" + UnitySource + ",\"installed\":true,\"enabled\":true}";
            Assert.That(ConvaiCodexInstallationProbe.ParsePluginList(
                "{\"installed\":[" + entry + "," + entry + "],\"available\":[]}").State,
                Is.EqualTo(ConvaiInstallationState.Unknown));
        }

        [TestCase("\"/apps/Codex.exe\" \"%1\"", "/apps/Codex.exe")]
        [TestCase("\"/apps/ChatGPT.exe\" --url \"%1\"", "/apps/ChatGPT.exe")]
        [TestCase("\"/apps/other.exe\" \"%1\"", null)]
        [TestCase("cmd.exe /c anything", null)]
        [TestCase("\"unterminated", null)]
        [TestCase(null, null)]
        public void RegisteredCommandsAreInspectedWithoutExecutingThem(string command, string expected) =>
            Assert.That(ConvaiCodexInstallationProbe.ExtractRegisteredExecutable(command), Is.EqualTo(expected));
    }
}
