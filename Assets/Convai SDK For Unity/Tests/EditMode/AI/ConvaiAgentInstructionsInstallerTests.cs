using System;
using System.IO;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AI
{
    public sealed class ConvaiAgentInstructionsInstallerTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ConvaiAgentInstructions", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [TestCase((int)Convai.Editor.ConvaiAgentClient.Codex, "AGENTS.md")]
        [TestCase((int)Convai.Editor.ConvaiAgentClient.ClaudeCode, "CLAUDE.md")]
        [TestCase((int)Convai.Editor.ConvaiAgentClient.Cursor, ".cursor/rules/convai-unity-sdk.mdc")]
        [TestCase((int)Convai.Editor.ConvaiAgentClient.Gemini, "GEMINI.md")]
        [TestCase((int)Convai.Editor.ConvaiAgentClient.Copilot, ".github/copilot-instructions.md")]
        public void UpsertCreatesExpectedClientFile(int clientValue, string expectedPath)
        {
            var client = (Convai.Editor.ConvaiAgentClient)clientValue;
            Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, client);

            string path = Path.Combine(_root, expectedPath);
            Assert.That(File.Exists(path), Is.True);
            Assert.That(File.ReadAllText(path), Does.Contain(Convai.Editor.ConvaiAgentInstructionsInstaller.BeginSentinel));
        }

        [Test]
        public void UpsertPreservesContentFrontmatterAndCrLfAndIsIdempotent()
        {
            string path = Path.Combine(_root, ".cursor/rules/convai-unity-sdk.mdc");
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            const string original = "---\r\ndescription: Project rule\r\nalwaysApply: true\r\n---\r\n\r\nKeep this rule.\r\n";
            File.WriteAllText(path, original);

            Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, Convai.Editor.ConvaiAgentClient.Cursor);
            string once = File.ReadAllText(path);
            Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, Convai.Editor.ConvaiAgentClient.Cursor);
            string twice = File.ReadAllText(path);

            Assert.That(twice, Is.EqualTo(once));
            Assert.That(twice, Does.StartWith(original.TrimEnd('\r', '\n')));
            Assert.That(twice.Replace("\r\n", string.Empty), Does.Not.Contain("\n"));
            Assert.That(Count(twice, Convai.Editor.ConvaiAgentInstructionsInstaller.BeginSentinel), Is.EqualTo(1));
        }

        [Test]
        public void RemoveDeletesOnlyManagedBlock()
        {
            string path = Path.Combine(_root, "AGENTS.md");
            File.WriteAllText(path, "Before\n");
            Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, Convai.Editor.ConvaiAgentClient.Codex);

            Convai.Editor.ConvaiAgentInstructionsInstaller.Remove(_root, Convai.Editor.ConvaiAgentClient.Codex);

            Assert.That(File.ReadAllText(path), Is.EqualTo("Before"));
        }

        [Test]
        public void RemovePreservesContentAfterManagedBlock()
        {
            string path = Path.Combine(_root, "AGENTS.md");
            File.WriteAllText(path,
                "Before\n\n" + Convai.Editor.ConvaiAgentInstructionsInstaller.BeginSentinel +
                "\nManaged\n" + Convai.Editor.ConvaiAgentInstructionsInstaller.EndSentinel +
                "\n\nAfter\n");

            Convai.Editor.ConvaiAgentInstructionsInstaller.Remove(_root, Convai.Editor.ConvaiAgentClient.Codex);

            Assert.That(File.ReadAllText(path), Is.EqualTo("Before\n\nAfter\n"));
        }

        [Test]
        public void MalformedManagedBlockFailsWithoutWriting()
        {
            string path = Path.Combine(_root, "CLAUDE.md");
            string original = "Keep\n" + Convai.Editor.ConvaiAgentInstructionsInstaller.BeginSentinel + "\nBroken\n";
            File.WriteAllText(path, original);

            Assert.Throws<InvalidOperationException>(() =>
                Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, Convai.Editor.ConvaiAgentClient.ClaudeCode));
            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
        }

        [Test]
        public void UpsertFailsWithoutChangingUnreadableFile()
        {
            string path = Path.Combine(_root, "AGENTS.md");
            const string original = "Keep this content.\n";
            File.WriteAllText(path, original);

            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<InvalidOperationException>(() =>
                    Convai.Editor.ConvaiAgentInstructionsInstaller.Upsert(_root, Convai.Editor.ConvaiAgentClient.Codex));
            }

            Assert.That(File.ReadAllText(path), Is.EqualTo(original));
        }

        [TestCase("2.13.0-pre.2", true)]
        [TestCase("2.13.0-pre.1", false)]
        [TestCase("2.13.0", true)]
        [TestCase("2.14.0-pre.1", true)]
        [TestCase("2.12.9", false)]
        [TestCase("3.0.0-pre.1", true)]
        [TestCase("3.0.0", false)]
        [TestCase("invalid", false)]
        public void AssistantVersionGateHonorsSupportedRange(string version, bool expected) =>
            Assert.That(Convai.Editor.ConvaiAICodingSetupWindow.IsAssistantCompatible(version), Is.EqualTo(expected));

        private static int Count(string value, string token)
        {
            int count = 0;
            int index = 0;
            while ((index = value.IndexOf(token, index, StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += token.Length;
            }
            return count;
        }
    }
}
