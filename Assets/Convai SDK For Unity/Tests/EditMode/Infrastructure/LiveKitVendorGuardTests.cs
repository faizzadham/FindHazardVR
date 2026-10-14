using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;
using LiveKit.Editor;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class LiveKitVendorGuardTests
    {
        private static readonly string VendorRoot = Path.GetFullPath(
            Path.Combine(UnityEngine.Application.dataPath, "..", "Packages", "com.convai.convai-sdk-for-unity", "Plugins", "client-sdk-unity-livekit"));

        [Test]
        public void VendoredFiles_MatchRecordedGitTree_WithoutNetworkOrForkCheckout()
        {
            JObject record = JObject.Parse(File.ReadAllText(Path.Combine(VendorRoot, "CONVAI-VENDOR.json")));
            var executableFiles = new HashSet<string>(
                record["executableFiles"].Values<string>(), StringComparer.Ordinal);
            foreach (string path in executableFiles)
                Assert.That(File.Exists(Path.Combine(VendorRoot, path)), Is.True, path);
            string actual = BitConverter.ToString(HashTree(VendorRoot, "", executableFiles))
                .Replace("-", "").ToLowerInvariant();
            Assert.That(actual, Is.EqualTo((string)record["tree"]),
                "Vendored content drifted from its immutable fork tree. Fix the fork and re-vendor; do not edit the snapshot.");
        }

        [Test]
        public void GitTreeHash_DetectsContentAddedRemovedAndExecutableChanges()
        {
            string root = Path.Combine(Path.GetTempPath(), "convai-vendor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var modes = new HashSet<string>(StringComparer.Ordinal);
                // Git's canonical empty-tree SHA-1 anchors the object format independently.
                Assert.That(BitConverter.ToString(HashTree(root, "", modes)).Replace("-", "").ToLowerInvariant(),
                    Is.EqualTo("4b825dc642cb6eb9a060e54bf8d69288fbee4904"));
                File.WriteAllText(Path.Combine(root, "file"), "first");
                byte[] original = HashTree(root, "", modes);
                File.WriteAllText(Path.Combine(root, "file"), "changed");
                Assert.That(HashTree(root, "", modes), Is.Not.EqualTo(original));
                File.WriteAllText(Path.Combine(root, "file"), "first");
                modes.Add("file");
                Assert.That(HashTree(root, "", modes), Is.Not.EqualTo(original));
                modes.Clear();
                Directory.CreateDirectory(Path.Combine(root, "nested"));
                File.WriteAllText(Path.Combine(root, "nested", "added"), "extra");
                Assert.That(HashTree(root, "", modes), Is.Not.EqualTo(original));
                File.Delete(Path.Combine(root, "nested", "added"));
                Assert.That(HashTree(root, "", modes), Is.EqualTo(original));
            }
            finally { Directory.Delete(root, true); }
        }

        private static byte[] HashTree(string directory, string relative, HashSet<string> executableFiles)
        {
            var entries = new List<(byte[] SortKey, string Name, string Mode, byte[] Hash)>();
            foreach (string path in Directory.GetFileSystemEntries(directory))
            {
                string name = Path.GetFileName(path);
                if (relative.Length == 0 && (name == "CONVAI-VENDOR.json" || name == "CONVAI-VENDOR.json.meta")) continue;
                bool isDirectory = Directory.Exists(path);
                if (isDirectory && !Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any()) continue;
                string child = relative + name;
                string mode = isDirectory ? "40000" : executableFiles.Contains(child) ? "100755" : "100644";
                byte[] hash = isDirectory ? HashTree(path, child + "/", executableFiles) : HashObject("blob", File.ReadAllBytes(path));
                entries.Add((Encoding.UTF8.GetBytes(name + (isDirectory ? "/" : "")), name, mode, hash));
            }
            entries.Sort((a, b) => a.SortKey.AsSpan().SequenceCompareTo(b.SortKey));
            using var content = new MemoryStream();
            foreach (var entry in entries)
            {
                byte[] header = Encoding.UTF8.GetBytes(entry.Mode + " " + entry.Name + "\0");
                content.Write(header, 0, header.Length);
                content.Write(entry.Hash, 0, entry.Hash.Length);
            }
            return HashObject("tree", content.ToArray());
        }

        private static byte[] HashObject(string kind, byte[] content)
        {
            using var bytes = new MemoryStream();
            byte[] header = Encoding.ASCII.GetBytes(kind + " " + content.Length + "\0");
            bytes.Write(header, 0, header.Length);
            bytes.Write(content, 0, content.Length);
            bytes.Position = 0;
            using var sha = SHA1.Create();
            return sha.ComputeHash(bytes);
        }

        [Test]
        public void RuntimeAssembly_ExcludesWebGL()
        {
            string asmdef = File.ReadAllText(Path.Combine(VendorRoot, "Runtime", "livekit.unity.Runtime.asmdef"));
            Assert.That(asmdef, Does.Contain("\"WebGL\""),
                "The native LiveKit assembly must exclude WebGL; the LiveKitBridge assembly owns that platform.");
        }

        [Test]
        public void UniTaskBridge_IsNotVendored()
        {
            Assert.That(Directory.Exists(Path.Combine(VendorRoot, "Runtime", "Scripts", "UniTask")), Is.False);
        }

        [Test]
        public void VendorRecord_VersionIni_AndManifest_AgreeOnFfiTag()
        {
            string record = File.ReadAllText(Path.Combine(VendorRoot, "CONVAI-VENDOR.json"));
            string versionIni = File.ReadAllText(Path.Combine(VendorRoot, "version.ini"));

            Assert.That(record, Does.Contain($"\"ffiTag\": \"{LiveKitFfiManifest.Tag}\""));
            Assert.That(versionIni, Does.Contain(LiveKitFfiManifest.Tag.Replace("livekit-ffi/v", "livekit-ffi@v")));
        }

        [Test]
        public void Manifest_ListsEveryPlatformTheInstallerKnows()
        {
            foreach (string folder in new[]
                     {
                         "ffi-android-arm64", "ffi-android-armv7", "ffi-android-x86_64", "ffi-ios-arm64", "ffi-ios-sim-arm64",
                         "ffi-linux-x86_64", "ffi-linux-arm64", "ffi-macos-arm64", "ffi-macos-x86_64", "ffi-windows-arm64", "ffi-windows-x86_64"
                     })
            {
                Assert.That(LiveKitFfiManifest.Sha256.ContainsKey(folder), Is.True, folder);
                Assert.That(LiveKitFfiManifest.Sha256[folder], Has.Length.EqualTo(64), folder);
            }
        }
    }
}
