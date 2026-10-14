using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using Convai.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.AICoding
{
    /// <summary>
    ///     The resolver has to hand back a physical path for every layout the SDK ships in. These use
    ///     representative embedded, PackageCache, and Assets folders on disk rather than Unity's package
    ///     APIs, because the defect being guarded is a path that only looks right.
    /// </summary>
    public sealed class ConvaiSkillSourceResolverTests
    {
        private const string PackageFolder = "com.convai.convai-sdk-for-unity";

        private string _project;

        [SetUp]
        public void SetUp()
        {
            _project = Path.Combine(Path.GetTempPath(), "ConvaiSkillSource", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_project);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_project)) Directory.Delete(_project, true);
        }

        [Test]
        public void EmbeddedPackageResolvesToItsPhysicalSkillFolder()
        {
            string sdkRoot = CreateSdk(Path.Combine(_project, "Packages", PackageFolder), "4.6.0");

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(new ConvaiSkillSourceCandidate(sdkRoot, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0")),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.SkillRoot, Is.EqualTo(Path.Combine(sdkRoot, "AIAssistantSkills", "convai-unity-sdk")));
            Assert.That(File.Exists(source.SkillFilePath), Is.True);
            Assert.That(source.SdkVersion, Is.EqualTo("4.6.0"));
            Assert.That(source.Layout, Is.EqualTo(ConvaiSkillSourceLayout.EmbeddedPackage));
            Assert.That(source.Warning, Is.Null);
        }

        [Test]
        public void PackageCacheResolvesThroughTheSuppliedResolvedPath()
        {
            string sdkRoot = CreateSdk(
                Path.Combine(_project, "Library", "PackageCache", PackageFolder + "@4.6.1"),
                "4.6.1");

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(new ConvaiSkillSourceCandidate(
                        sdkRoot,
                        ConvaiSkillSourceResolver.ClassifyPackagePath(sdkRoot),
                        "4.6.1")),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.Layout, Is.EqualTo(ConvaiSkillSourceLayout.PackageCache));
            Assert.That(source.SkillFilePath, Is.EqualTo(
                Path.Combine(sdkRoot, "AIAssistantSkills", "convai-unity-sdk", "SKILL.md")));
        }

        [Test]
        public void AssetsLayoutResolvesAndReadsItsVersionFromTheManifest()
        {
            string sdkRoot = CreateSdk(
                Path.Combine(_project, "Assets", ConvaiSkillSourceResolver.AssetsSdkFolderName),
                "4.7.0");

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(new ConvaiSkillSourceCandidate(sdkRoot, ConvaiSkillSourceLayout.Assets, null)),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.Layout, Is.EqualTo(ConvaiSkillSourceLayout.Assets));
            Assert.That(source.SdkVersion, Is.EqualTo("4.7.0"));
        }

        [Test]
        public void AssetsLayoutWithoutAManifestStillResolvesWithAnUnknownVersion()
        {
            string sdkRoot = CreateSdk(
                Path.Combine(_project, "Assets", ConvaiSkillSourceResolver.AssetsSdkFolderName),
                null);

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(new ConvaiSkillSourceCandidate(sdkRoot, ConvaiSkillSourceLayout.Assets, null)),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.SdkVersion, Is.EqualTo(ConvaiSkillSourceResolver.UnknownVersion));
        }

        [Test]
        public void FirstValidCandidateWinsAndTheDuplicateBecomesAWarning()
        {
            string packageRoot = CreateSdk(Path.Combine(_project, "Packages", PackageFolder), "4.6.0");
            string assetsRoot = CreateSdk(
                Path.Combine(_project, "Assets", ConvaiSkillSourceResolver.AssetsSdkFolderName),
                "4.5.0");

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(
                        new ConvaiSkillSourceCandidate(packageRoot, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0"),
                        new ConvaiSkillSourceCandidate(assetsRoot, ConvaiSkillSourceLayout.Assets, null)),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.SdkRoot, Is.EqualTo(Path.GetFullPath(packageRoot)));
            Assert.That(source.Warning, Is.Not.Null);
            Assert.That(source.Warning, Does.Contain(ConvaiSkillSourceResolver.Normalize(assetsRoot)));
        }

        [Test]
        public void ACandidateFolderWithoutSkillMarkdownIsSkipped()
        {
            string incomplete = Path.Combine(_project, "Packages", PackageFolder);
            Directory.CreateDirectory(ConvaiSkillSourceResolver.GetSkillRoot(incomplete));
            string assetsRoot = CreateSdk(
                Path.Combine(_project, "Assets", ConvaiSkillSourceResolver.AssetsSdkFolderName),
                "4.6.0");

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(
                        new ConvaiSkillSourceCandidate(incomplete, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0"),
                        new ConvaiSkillSourceCandidate(assetsRoot, ConvaiSkillSourceLayout.Assets, null)),
                    out ConvaiSkillSource source,
                    out string error),
                Is.True,
                error);
            Assert.That(source.SdkRoot, Is.EqualTo(Path.GetFullPath(assetsRoot)));
            Assert.That(source.Warning, Is.Null);
        }

        [Test]
        public void NoValidCandidateReportsWhereItLooked()
        {
            string missing = Path.Combine(_project, "Packages", PackageFolder);

            Assert.That(
                ConvaiSkillSourceResolver.TryResolveFrom(
                    Candidates(new ConvaiSkillSourceCandidate(missing, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0")),
                    out ConvaiSkillSource source,
                    out string error),
                Is.False);
            Assert.That(source.IsValid, Is.False);
            Assert.That(error, Does.Contain("SKILL.md"));
            Assert.That(error, Does.Contain(ConvaiSkillSourceResolver.Normalize(
                ConvaiSkillSourceResolver.GetSkillRoot(missing))));
        }

        [Test]
        public void InstructionPointerIsProjectRelativeWithForwardSlashes()
        {
            string sdkRoot = CreateSdk(Path.Combine(_project, "Packages", PackageFolder), "4.6.0");
            string skillFile = Path.Combine(sdkRoot, "AIAssistantSkills", "convai-unity-sdk", "SKILL.md");

            Assert.That(
                ConvaiSkillSourceResolver.ToInstructionPointer(_project, skillFile),
                Is.EqualTo($"Packages/{PackageFolder}/AIAssistantSkills/convai-unity-sdk/SKILL.md"));
        }

        [Test]
        public void InstructionPointerStaysAbsoluteWhenTheSourceLivesOutsideTheProject()
        {
            string external = Path.Combine(Path.GetTempPath(), "ConvaiSkillSourceExternal", "SKILL.md");

            string pointer = ConvaiSkillSourceResolver.ToInstructionPointer(_project, external);

            Assert.That(pointer, Is.EqualTo(ConvaiSkillSourceResolver.Normalize(Path.GetFullPath(external))));
            Assert.That(pointer, Does.Not.Contain("\\"));
        }

        [Test]
        public void ContainmentPreservesCaseOutsideWindows()
        {
            // On a case-sensitive filesystem /work/Game and /work/game are different directories,
            // so the source is outside the project and the pointer must stay absolute.
            string pointer = ConvaiSkillSourceResolver.ToInstructionPointer(
                "/work/Game", "/work/game/Packages/sdk/SKILL.md");

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                Assert.That(pointer, Is.EqualTo("/work/game/Packages/sdk/SKILL.md"));
            else
                Assert.That(pointer, Is.EqualTo("Packages/sdk/SKILL.md"));
        }

        [Test]
        public void TheSamePhysicalRootIsOnlyCountedOnce()
        {
            var candidates = new List<ConvaiSkillSourceCandidate>();
            string root = Path.Combine(_project, "Packages", PackageFolder);

            ConvaiSkillSourceResolver.AddDistinct(
                candidates, new ConvaiSkillSourceCandidate(root, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0"));
            ConvaiSkillSourceResolver.AddDistinct(
                candidates,
                new ConvaiSkillSourceCandidate(
                    root + Path.DirectorySeparatorChar, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0"));

            Assert.That(candidates, Has.Count.EqualTo(1));
        }

        [Test]
        public void RootsDifferingOnlyInCaseArePreservedOutsideWindows()
        {
            var candidates = new List<ConvaiSkillSourceCandidate>();
            string lower = Path.Combine(_project, "packages", PackageFolder);
            string upper = Path.Combine(_project, "Packages", PackageFolder);

            ConvaiSkillSourceResolver.AddDistinct(
                candidates, new ConvaiSkillSourceCandidate(lower, ConvaiSkillSourceLayout.EmbeddedPackage, "4.6.0"));
            ConvaiSkillSourceResolver.AddDistinct(
                candidates, new ConvaiSkillSourceCandidate(upper, ConvaiSkillSourceLayout.EmbeddedPackage, "4.7.0"));

            // On a case-sensitive filesystem these are two directories and both must survive;
            // folding them would silently drop a real SDK distribution.
            Assert.That(
                candidates,
                Has.Count.EqualTo(
                    RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? 1 : 2));
        }

        [Test]
        public void AGenuinelyContainedSourceStillRelativizesOnEveryPlatform() =>
            Assert.That(
                ConvaiSkillSourceResolver.ToInstructionPointer("/work/Game", "/work/Game/Packages/sdk/SKILL.md"),
                Is.EqualTo("Packages/sdk/SKILL.md"));

        [TestCase("Library/PackageCache/com.convai.convai-sdk-for-unity@4.6.0", true)]
        [TestCase("Packages/com.convai.convai-sdk-for-unity", false)]
        [TestCase("Packages/PackageCacheMirror/com.convai.convai-sdk-for-unity", false)]
        public void PackagePathsAreClassifiedBySegmentNotSubstring(string relative, bool expectPackageCache)
        {
            string path = Path.Combine(_project, relative.Replace('/', Path.DirectorySeparatorChar));

            Assert.That(
                ConvaiSkillSourceResolver.ClassifyPackagePath(path) == ConvaiSkillSourceLayout.PackageCache,
                Is.EqualTo(expectPackageCache));
        }

        private static IReadOnlyList<ConvaiSkillSourceCandidate> Candidates(
            params ConvaiSkillSourceCandidate[] candidates) => candidates;

        /// <summary>Writes the minimum layout the resolver validates: the skill folder plus SKILL.md.</summary>
        private static string CreateSdk(string sdkRoot, string version)
        {
            string skillRoot = ConvaiSkillSourceResolver.GetSkillRoot(sdkRoot);
            Directory.CreateDirectory(Path.Combine(skillRoot, "references"));
            File.WriteAllText(Path.Combine(skillRoot, "SKILL.md"), "---\nname: convai-unity-sdk\n---\n");
            File.WriteAllText(Path.Combine(skillRoot, "references", "quickstart.md"), "# Quickstart\n");
            if (version != null)
                File.WriteAllText(
                    Path.Combine(sdkRoot, "package.json"),
                    "{\n  \"name\": \"com.convai.convai-sdk-for-unity\",\n  \"version\": \"" + version + "\"\n}\n");
            return sdkRoot;
        }
    }
}
