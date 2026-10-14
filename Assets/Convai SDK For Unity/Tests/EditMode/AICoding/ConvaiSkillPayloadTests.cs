using System;
using System.Collections.Generic;
using System.IO;
using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    /// <summary>
    ///     Hashing decides Installed against Locally modified, so a CRLF checkout must not read as a
    ///     local edit. Paths are part of the aggregate, so renames, additions, and deletions move it.
    /// </summary>
    public sealed class ConvaiSkillPayloadTests
    {
        private string _root;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ConvaiSkillPayload", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void CrLfAndLfContentHashTheSame()
        {
            string lf = Write("lf.md", "# Title\n\nBody line.\n");
            string crlf = Write("crlf.md", "# Title\r\n\r\nBody line.\r\n");

            Assert.That(
                ConvaiSkillPayload.ComputeFileHash(crlf),
                Is.EqualTo(ConvaiSkillPayload.ComputeFileHash(lf)));
        }

        [Test]
        public void AContentChangeStillChangesTheHash()
        {
            string original = Write("a.md", "# Title\n");
            string hash = ConvaiSkillPayload.ComputeFileHash(original);
            File.WriteAllText(original, "# Different title\n");

            Assert.That(ConvaiSkillPayload.ComputeFileHash(original), Is.Not.EqualTo(hash));
        }

        [Test]
        public void MetaFilesAndOsClutterAreNotPayload()
        {
            Write("SKILL.md", "# Skill\n");
            Write("SKILL.md.meta", "fileFormatVersion: 2\n");
            Write(".DS_Store", "junk");
            Write(ConvaiSkillPayload.StampFileName, "{}");

            Assert.That(ConvaiSkillPayload.EnumerateRelativePaths(_root), Is.EqualTo(new[] { "SKILL.md" }));
        }

        [Test]
        public void RelativePathsAreForwardSlashedAndOrdinalSorted()
        {
            Write("SKILL.md", "# Skill\n");
            Write(Path.Combine("references", "quickstart.md"), "# Quickstart\n");
            Write(Path.Combine("resources", "foundation-tools.md"), "# Tools\n");

            Assert.That(
                ConvaiSkillPayload.EnumerateRelativePaths(_root),
                Is.EqualTo(new[] { "SKILL.md", "references/quickstart.md", "resources/foundation-tools.md" }));
        }

        [Test]
        public void RenamingAFileChangesTheAggregateEvenThoughContentIsIdentical()
        {
            Write("SKILL.md", "# Skill\n");
            Write(Path.Combine("references", "quickstart.md"), "# Quickstart\n");
            string before = ConvaiSkillPayload.ComputeAggregateHash(ConvaiSkillPayload.ComputeFileHashes(_root));

            File.Move(
                Path.Combine(_root, "references", "quickstart.md"),
                Path.Combine(_root, "references", "getting-started.md"));

            Assert.That(
                ConvaiSkillPayload.ComputeAggregateHash(ConvaiSkillPayload.ComputeFileHashes(_root)),
                Is.Not.EqualTo(before));
        }

        [Test]
        public void AddingAFileChangesTheAggregate()
        {
            Write("SKILL.md", "# Skill\n");
            string before = ConvaiSkillPayload.ComputeAggregateHash(ConvaiSkillPayload.ComputeFileHashes(_root));

            Write(Path.Combine("references", "narrative.md"), "# Narrative\n");

            Assert.That(
                ConvaiSkillPayload.ComputeAggregateHash(ConvaiSkillPayload.ComputeFileHashes(_root)),
                Is.Not.EqualTo(before));
        }

        [Test]
        public void TheAggregateIsStableAcrossRepeatedReads()
        {
            Write("SKILL.md", "# Skill\n");
            Write(Path.Combine("references", "quickstart.md"), "# Quickstart\r\n");

            IReadOnlyDictionary<string, string> first = ConvaiSkillPayload.ComputeFileHashes(_root);
            IReadOnlyDictionary<string, string> second = ConvaiSkillPayload.ComputeFileHashes(_root);

            Assert.That(
                ConvaiSkillPayload.ComputeAggregateHash(second),
                Is.EqualTo(ConvaiSkillPayload.ComputeAggregateHash(first)));
        }

        [Test]
        public void CopyingLeavesTheSourceUntouchedAndSkipsExcludedFiles()
        {
            Write("SKILL.md", "# Skill\r\n");
            Write("SKILL.md.meta", "fileFormatVersion: 2\n");
            Write(Path.Combine("references", "quickstart.md"), "# Quickstart\n");
            string destination = Path.Combine(Path.GetTempPath(), "ConvaiSkillPayloadCopy", Guid.NewGuid().ToString("N"));

            try
            {
                ConvaiSkillPayload.CopyTo(_root, destination);

                Assert.That(File.ReadAllText(Path.Combine(destination, "SKILL.md")), Is.EqualTo("# Skill\r\n"));
                Assert.That(File.ReadAllText(Path.Combine(_root, "SKILL.md")), Is.EqualTo("# Skill\r\n"));
                Assert.That(File.Exists(Path.Combine(destination, "SKILL.md.meta")), Is.False);
                Assert.That(File.Exists(Path.Combine(destination, "references", "quickstart.md")), Is.True);
            }
            finally
            {
                if (Directory.Exists(destination)) Directory.Delete(destination, true);
            }
        }

        private string Write(string relativePath, string content)
        {
            string path = Path.Combine(_root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path) ?? _root);
            File.WriteAllText(path, content);
            return path;
        }
    }
}
