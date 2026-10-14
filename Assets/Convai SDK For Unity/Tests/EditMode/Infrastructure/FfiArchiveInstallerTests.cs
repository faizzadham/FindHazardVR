using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using LiveKit.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    /// Exercises the zip-to-folder step of the FFI installer on real files under a temp directory.
    /// </summary>
    [TestFixture]
    public sealed class FfiArchiveInstallerTests
    {
        private string _root;
        private string _zipPath;
        private string _destFolder;

        [SetUp]
        public void SetUp()
        {
            _root = Path.Combine(Path.GetTempPath(), "convai-ffi-installer-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
            _zipPath = Path.Combine(_root, "ffi-android-armv7.zip");
            _destFolder = Path.Combine(_root, "ffi-android-armv7");
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        private void WriteZip(params string[] entryNames)
        {
            using ZipArchive archive = ZipFile.Open(_zipPath, ZipArchiveMode.Create);
            foreach (string name in entryNames)
            {
                using Stream stream = archive.CreateEntry(name).Open();
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes("payload:" + name);
                stream.Write(bytes, 0, bytes.Length);
            }
        }

        [Test]
        public void Install_WhenEveryEntryLands_ReportsSuccessAndListsThePluginFiles()
        {
            WriteZip("liblivekit_ffi.so", "libwebrtc.jar", "livekit_ffi.h", "LICENSE.md");

            FfiArchiveInstallResult result = FfiArchiveInstaller.Install(_zipPath, _destFolder, "android", includeSharedAndroidJar: true);

            Assert.That(result.Success, Is.True);
            Assert.That(result.InstalledFiles.Select(Path.GetFileName), Is.EquivalentTo(new[] { "liblivekit_ffi.so", "libwebrtc.jar" }));
            Assert.That(File.Exists(Path.Combine(_destFolder, "liblivekit_ffi.so")), Is.True);
            Assert.That(File.Exists(Path.Combine(_destFolder, "livekit_ffi.h")), Is.False, "headers are not shipped");
            Assert.That(File.Exists(Path.Combine(_destFolder, "LICENSE.md")), Is.False, "the licence is not shipped");
        }

        [Test]
        public void Install_ForAndroidWithoutTheSharedJar_LeavesLibwebrtcJarOut()
        {
            WriteZip("liblivekit_ffi.so", "libwebrtc.jar");

            FfiArchiveInstallResult result = FfiArchiveInstaller.Install(_zipPath, _destFolder, "android", includeSharedAndroidJar: false);

            Assert.That(result.Success, Is.True);
            Assert.That(File.Exists(Path.Combine(_destFolder, "libwebrtc.jar")), Is.False);
            Assert.That(result.InstalledFiles.Select(Path.GetFileName), Is.EquivalentTo(new[] { "liblivekit_ffi.so" }));
        }

        [Test]
        public void Install_WhenAnEntryCannotBeReplaced_ReportsFailureAndNamesTheEntry()
        {
            // A directory squatting on the destination path makes the move fail on every platform,
            // the same way a DLL loaded by the Windows editor does.
            WriteZip("liblivekit_ffi.so", "libwebrtc.jar");
            Directory.CreateDirectory(Path.Combine(_destFolder, "liblivekit_ffi.so"));

            FfiArchiveInstallResult result = FfiArchiveInstaller.Install(_zipPath, _destFolder, "android", includeSharedAndroidJar: true);

            Assert.That(result.Success, Is.False, "a folder holding a stale native must not be reported as installed");
            Assert.That(result.FailedEntries, Has.Some.Contains("liblivekit_ffi.so"));
        }
    }
}
