using System;
using System.IO;
using System.Linq;
using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    /// <summary>
    ///     Install, Update, Remove, and the locally-modified guard, against a representative SDK
    ///     source and project on disk. Update always synchronizes to whatever SDK is installed now,
    ///     newer or older; there is no separate downgrade flow.
    /// </summary>
    [TestFixture(false)]
    [TestFixture(true)]
    public sealed class ConvaiSkillMirrorTests
    {
        private readonly ConvaiAgentClient _client;

        public ConvaiSkillMirrorTests(bool codex) =>
            _client = codex ? ConvaiAgentClient.Codex : ConvaiAgentClient.ClaudeCode;

        private string _root;
        private string _project;
        private string _sdk;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "ConvaiSkillMirror", Guid.NewGuid().ToString("N"));
            _project = Path.Combine(_root, "UnityProject");
            _sdk = Path.Combine(_root, "Sdk");
            Directory.CreateDirectory(_project);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [Test]
        public void InstallCopiesThePayloadStampsItAndLeavesMetaFilesBehind()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");

            ConvaiSkillMirror.Install(_project, source, client: _client);

            string mirror = ConvaiSkillMirror.GetMirrorRoot(_project, client: _client);
            Assert.That(File.Exists(Path.Combine(mirror, "SKILL.md")), Is.True);
            Assert.That(File.Exists(Path.Combine(mirror, "references", "quickstart.md")), Is.True);
            Assert.That(File.ReadAllText(Path.Combine(mirror, "agents", "openai.yaml")),
                Is.EqualTo(File.ReadAllText(Path.Combine(source.SkillRoot, "agents", "openai.yaml"))));
            Assert.That(
                Directory.EnumerateFiles(mirror, "*.meta", SearchOption.AllDirectories),
                Is.Empty,
                "Source .meta files are Unity import metadata, not skill payload.");
            Assert.That(File.Exists(Path.Combine(mirror, ConvaiSkillPayload.StampFileName)), Is.True);
            Assert.That(
                File.ReadAllText(Path.Combine(mirror, "SKILL.md")),
                Is.EqualTo(File.ReadAllText(Path.Combine(source.SkillRoot, "SKILL.md"))),
                "The mirror must carry the canonical content and frontmatter unchanged.");
        }

        [Test]
        public void StampRecordsTheResolvedSdkAndSkillVersions()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);

            Assert.That(ConvaiSkillMirror.TryReadStamp(_project, out ConvaiSkillStamp stamp, out string error, client: _client), Is.True, error);
            Assert.That(stamp.SdkVersion, Is.EqualTo("4.6.0"));
            Assert.That(stamp.SkillVersion, Is.EqualTo("2.3.4"));
            Assert.That(stamp.SkillName, Is.EqualTo("convai-unity-sdk"));
            Assert.That(stamp.Files.Keys, Does.Contain("references/quickstart.md"));
            Assert.That(stamp.Files.Keys, Has.No.Member(ConvaiSkillPayload.StampFileName));
            Assert.That(stamp.InstalledContentHash, Does.StartWith(ConvaiSkillPayload.HashPrefix));
        }

        [Test]
        public void AFreshMirrorReportsInstalled()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);

            Assert.That(
                ConvaiSkillMirror.Inspect(_project, source, client: _client).State,
                Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void NoMirrorReportsNotInstalled() =>
            Assert.That(
                ConvaiSkillMirror.Inspect(_project, CreateSource("4.6.0"), client: _client).State,
                Is.EqualTo(ConvaiSkillMirrorState.NotInstalled));

        [TestCase("4.7.0", TestName = "NewerSdk")]
        [TestCase("4.5.0", TestName = "OlderSdk")]
        public void AnySdkVersionChangeMakesTheMirrorStale(string installedNow)
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);

            ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, CreateSource(installedNow), client: _client);

            Assert.That(status.State, Is.EqualTo(ConvaiSkillMirrorState.Stale));
            Assert.That(status.Detail, Does.Contain(installedNow));
        }

        [Test]
        public void ChangedCanonicalContentMakesTheMirrorStaleAtTheSameSdkVersion()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            ConvaiSkillSource source = CreateSource("4.6.0");
            File.AppendAllText(Path.Combine(source.SkillRoot, "references", "quickstart.md"), "\nNew guidance.\n");

            Assert.That(
                ConvaiSkillMirror.Inspect(_project, source, client: _client).State,
                Is.EqualTo(ConvaiSkillMirrorState.Stale));
        }

        [Test]
        public void UpdateSynchronizesToTheInstalledSdkEvenWhenItIsOlder()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.7.0"), client: _client);
            ConvaiSkillSource older = CreateSource("4.5.0");
            File.WriteAllText(Path.Combine(older.SkillRoot, "references", "quickstart.md"), "# Older quickstart\n");

            ConvaiSkillMirror.Install(_project, older, client: _client);

            Assert.That(ConvaiSkillMirror.TryReadStamp(_project, out ConvaiSkillStamp stamp, out string error, client: _client), Is.True, error);
            Assert.That(stamp.SdkVersion, Is.EqualTo("4.5.0"));
            Assert.That(
                File.ReadAllText(Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "references", "quickstart.md")),
                Is.EqualTo("# Older quickstart\n"));
            Assert.That(ConvaiSkillMirror.Inspect(_project, older, client: _client).State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void UpdateDropsFilesTheCanonicalSkillNoLongerShips()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            ConvaiSkillSource trimmed = CreateSource("4.7.0");
            File.Delete(Path.Combine(trimmed.SkillRoot, "references", "quickstart.md"));

            ConvaiSkillMirror.Install(_project, trimmed, client: _client);

            Assert.That(
                File.Exists(Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "references", "quickstart.md")),
                Is.False);
        }

        [Test]
        public void AnEditedMirrorFileReportsLocallyModifiedAndNamesTheFile()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);
            File.AppendAllText(
                Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "SKILL.md"),
                "\nLocal edit.\n");

            ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, source, client: _client);

            Assert.That(status.State, Is.EqualTo(ConvaiSkillMirrorState.LocallyModified));
            Assert.That(status.Detail, Does.Contain("SKILL.md"));
        }

        [Test]
        public void LocallyModifiedAndStaleShowsLocallyModifiedAndKeepsTheUpdateDetail()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            File.AppendAllText(
                Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "SKILL.md"),
                "\nLocal edit.\n");

            ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, CreateSource("4.8.0"), client: _client);

            Assert.That(status.State, Is.EqualTo(ConvaiSkillMirrorState.LocallyModified));
            Assert.That(status.UpdateAvailable, Is.True);
            Assert.That(status.Detail, Does.Contain("4.8.0"));
        }

        [Test]
        public void InstallRefusesLocallyModifiedContentUntilReplaceIsChosen()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);
            string skill = Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "SKILL.md");
            File.AppendAllText(skill, "\nLocal edit.\n");
            string modified = File.ReadAllText(skill);

            Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Install(_project, CreateSource("4.8.0"), client: _client));
            Assert.That(File.ReadAllText(skill), Is.EqualTo(modified));

            ConvaiSkillMirror.Install(_project, source, true, client: _client);

            Assert.That(File.ReadAllText(skill), Is.EqualTo(File.ReadAllText(Path.Combine(source.SkillRoot, "SKILL.md"))));
            Assert.That(ConvaiSkillMirror.Inspect(_project, source, client: _client).State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void AMirrorWithoutAStampIsUnmanagedAndIsNotSilentlyReplaced()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);
            File.Delete(ConvaiSkillMirror.GetStampPath(_project, client: _client));

            Assert.That(
                ConvaiSkillMirror.Inspect(_project, source, client: _client).State,
                Is.EqualTo(ConvaiSkillMirrorState.Unmanaged));
            Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Install(_project, source, client: _client));
            Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Remove(_project, source, client: _client));
        }

        [Test]
        public void RemoveDeletesOnlyTheConvaiMirror()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);
            string otherSkill = Path.Combine(ConvaiSkillMirror.GetSkillsRoot(_project, client: _client), "someone-elses-skill");
            Directory.CreateDirectory(otherSkill);
            File.WriteAllText(Path.Combine(otherSkill, "SKILL.md"), "# Not ours\n");

            ConvaiSkillMirror.Remove(_project, source, client: _client);

            Assert.That(Directory.Exists(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client)), Is.False);
            Assert.That(File.Exists(Path.Combine(otherSkill, "SKILL.md")), Is.True);
            Assert.That(Directory.Exists(ConvaiSkillMirror.GetSkillsRoot(_project, client: _client)), Is.True);
        }

        [Test]
        public void AMirrorStaysInspectableAndRemovableWhenTheSourceIsGone()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            Directory.Delete(_sdk, true);

            // default(ConvaiSkillSource) is what the caller has once resolution fails.
            ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, default, client: _client);

            Assert.That(
                status.State,
                Is.EqualTo(ConvaiSkillMirrorState.Installed),
                "A managed mirror must not read as Not installed just because the SDK went missing.");
            Assert.That(status.Detail, Does.Contain("unknown"), "Staleness cannot be judged without a source.");

            Assert.DoesNotThrow(() => ConvaiSkillMirror.Remove(_project, default, client: _client));
            Assert.That(Directory.Exists(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client)), Is.False);
        }

        [Test]
        public void ASourcelessRemoveStillRefusesLocallyModifiedContent()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            File.AppendAllText(Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "SKILL.md"), "\nedit\n");
            Directory.Delete(_sdk, true);

            Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Remove(_project, default, client: _client));
            Assert.DoesNotThrow(() => ConvaiSkillMirror.Remove(_project, default, true, client: _client));
        }

        [Test]
        public void RemoveOnAMissingMirrorDoesNothing() =>
            Assert.DoesNotThrow(() => ConvaiSkillMirror.Remove(_project, CreateSource("4.6.0"), client: _client));

        [Test]
        public void ALeftoverTemporaryFolderIsReportedRatherThanOverwritten()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            string leftover = Path.Combine(
                ConvaiSkillMirror.GetSkillsRoot(_project, client: _client),
                ConvaiSkillMirror.TemporaryFolderName);
            Directory.CreateDirectory(leftover);
            File.WriteAllText(Path.Combine(leftover, "SKILL.md"), "# Interrupted install\n");

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => ConvaiSkillMirror.Install(_project, source, client: _client));

            Assert.That(exception.Message, Does.Contain(ConvaiSkillMirror.TemporaryFolderName));
            Assert.That(File.Exists(Path.Combine(leftover, "SKILL.md")), Is.True);
            Assert.That(Directory.Exists(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client)), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FailedStagingPreservesTheMirrorAndAllowsRetry(bool update)
        {
            if (update) ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            ConvaiSkillSource source = CreateSource("4.7.0");
            string mirror = ConvaiSkillMirror.GetMirrorRoot(_project, client: _client);
            string originalStamp = update ? File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, client: _client)) : null;
            string locked = Path.Combine(source.SkillRoot, "references", "quickstart.md");

            using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Install(_project, source, client: _client));
            }

            AssertNoStagingFolders();
            Assert.That(Directory.Exists(mirror), Is.EqualTo(update));
            if (update)
                Assert.That(File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, client: _client)), Is.EqualTo(originalStamp));

            ConvaiSkillMirror.Install(_project, source, client: _client);
            Assert.That(ConvaiSkillMirror.Inspect(_project, source, client: _client).State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void MissingSourcePayloadCannotReplaceAHealthyMirror(bool entireDirectory)
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            ConvaiSkillSource source = CreateSource("4.6.0");
            string originalStamp = File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, client: _client));
            if (entireDirectory) Directory.Delete(source.SkillRoot, true);
            else File.Delete(source.SkillFilePath);

            Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Install(_project, source, client: _client));

            AssertNoStagingFolders();
            Assert.That(File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, client: _client)), Is.EqualTo(originalStamp));
            Assert.That(ConvaiSkillMirror.Inspect(_project, default, client: _client).State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void UnreadableMirrorReportsAnActionableStateAndRecoversAfterUnlock()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);
            string skill = Path.Combine(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client), "SKILL.md");

            using (new FileStream(skill, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, source, client: _client);
                Assert.That(status.State, Is.EqualTo(ConvaiSkillMirrorState.Unmanaged));
                Assert.That(status.Detail, Does.Contain("Could not read").And.Contain("refresh"));
                Assert.Throws<InvalidOperationException>(() => ConvaiSkillMirror.Remove(_project, source, client: _client));
            }

            Assert.That(ConvaiSkillMirror.Inspect(_project, source, client: _client).State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void UnreadableSourceDoesNotInvalidateAHealthyMirror()
        {
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: _client);

            using (new FileStream(source.SkillFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                ConvaiSkillMirrorStatus status = ConvaiSkillMirror.Inspect(_project, source, client: _client);
                Assert.That(status.State, Is.EqualTo(ConvaiSkillMirrorState.Installed));
                Assert.That(status.Detail, Does.Contain("could not be read").And.Contain("unknown"));
            }
        }

        [Test]
        public void ClientInstructionsUseTheExistingMirrorWithoutResolvingTheMissingSource()
        {
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: _client);
            Directory.Delete(_sdk, true);
            string instructions = Path.Combine(_project, ConvaiAgentInstructionsInstaller.GetRelativePath(_client));

            ConvaiAgentInstructionsInstaller.UpsertWithResolvedSkill(
                _project, _client,
                () => throw new InvalidOperationException("Source unavailable"));

            string content = File.ReadAllText(instructions);
            Assert.That(content, Does.Contain("the installed `convai-unity-sdk` skill"));
            Assert.That(content, Does.Not.Contain("SKILL.md"));
        }

        [Test]
        public void ClientMirrorsInstallUpdateAndRemoveIndependently()
        {
            ConvaiAgentClient other = _client == ConvaiAgentClient.Codex
                ? ConvaiAgentClient.ClaudeCode : ConvaiAgentClient.Codex;
            ConvaiSkillSource source = CreateSource("4.6.0");
            ConvaiSkillMirror.Install(_project, source, client: other);
            string otherStamp = File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, other));
            Assert.That(ConvaiSkillMirror.Inspect(_project, source, _client).State,
                Is.EqualTo(ConvaiSkillMirrorState.NotInstalled));
            ConvaiSkillMirror.Install(_project, source, client: _client);
            ConvaiSkillMirror.Install(_project, CreateSource("4.7.0"), client: _client);
            ConvaiSkillMirror.Remove(_project, default, client: _client);
            Assert.That(File.ReadAllText(ConvaiSkillMirror.GetStampPath(_project, other)), Is.EqualTo(otherStamp));
            Assert.That(ConvaiSkillMirror.Inspect(_project, default, other).State,
                Is.EqualTo(ConvaiSkillMirrorState.Installed));
        }

        [Test]
        public void InstructionsDoNotUseTheOtherClientsMirror()
        {
            ConvaiAgentClient other = _client == ConvaiAgentClient.Codex
                ? ConvaiAgentClient.ClaudeCode : ConvaiAgentClient.Codex;
            ConvaiSkillMirror.Install(_project, CreateSource("4.6.0"), client: other);
            ConvaiAgentInstructionsInstaller.UpsertWithResolvedSkill(_project, _client, () => "resolved/SKILL.md");
            string content = File.ReadAllText(Path.Combine(_project, ConvaiAgentInstructionsInstaller.GetRelativePath(_client)));
            Assert.That(content, Does.Contain("resolved/SKILL.md"));
            Assert.That(content, Does.Not.Contain("the installed `convai-unity-sdk` skill"));
        }

        private void AssertNoStagingFolders()
        {
            Assert.That(Directory.Exists(Path.Combine(ConvaiSkillMirror.GetSkillsRoot(_project, client: _client),
                ConvaiSkillMirror.TemporaryFolderName)), Is.False);
            Assert.That(Directory.Exists(Path.Combine(ConvaiSkillMirror.GetSkillsRoot(_project, client: _client),
                ConvaiSkillMirror.BackupFolderName)), Is.False);
        }

        [Test]
        public void MirrorLivesUnderTheClientSkillsFolder() =>
            Assert.That(
                ConvaiSkillSourceResolver.Normalize(ConvaiSkillMirror.GetMirrorRoot(_project, client: _client)),
                Does.EndWith((_client == ConvaiAgentClient.Codex ? ".agents" : ".claude") + "/skills/convai-unity-sdk"));

        /// <summary>Writes a representative canonical skill, including the .meta files Unity keeps beside it.</summary>
        private ConvaiSkillSource CreateSource(string sdkVersion)
        {
            string skillRoot = ConvaiSkillSourceResolver.GetSkillRoot(_sdk);
            if (Directory.Exists(skillRoot)) Directory.Delete(skillRoot, true);
            Directory.CreateDirectory(Path.Combine(skillRoot, "references"));
            Directory.CreateDirectory(Path.Combine(skillRoot, "agents"));

            Write(Path.Combine(skillRoot, "SKILL.md"), "---\nname: convai-unity-sdk\n---\n\n# Convai Unity SDK\n");
            Write(Path.Combine(skillRoot, "agents", "openai.yaml"), "interface:\n  display_name: Convai Unity SDK\n");
            Write(Path.Combine(skillRoot, "SKILL.md.meta"), "fileFormatVersion: 2\nguid: 0\n");
            Write(Path.Combine(skillRoot, "skill-manifest.json"), "{\n  \"name\": \"convai-unity-sdk\",\n  \"version\": \"2.3.4\"\n}\n");
            Write(Path.Combine(skillRoot, "references", "quickstart.md"), "# Quickstart\n");
            Write(Path.Combine(skillRoot, "references", "quickstart.md.meta"), "fileFormatVersion: 2\nguid: 1\n");

            return new ConvaiSkillSource(
                _sdk,
                skillRoot,
                Path.Combine(skillRoot, "SKILL.md"),
                sdkVersion,
                ConvaiSkillSourceLayout.EmbeddedPackage,
                null);
        }

        private static void Write(string path, string content) => File.WriteAllText(path, content);
    }
}
