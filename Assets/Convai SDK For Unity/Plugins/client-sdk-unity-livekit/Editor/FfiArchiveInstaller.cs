using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Threading;

namespace LiveKit.Editor
{
    public sealed class FfiArchiveInstallResult
    {
        public FfiArchiveInstallResult(IReadOnlyList<string> installedFiles, IReadOnlyList<string> failedEntries)
        {
            InstalledFiles = installedFiles ?? Array.Empty<string>();
            FailedEntries = failedEntries ?? Array.Empty<string>();
        }

        /// <summary>True only when every entry that belongs in the folder is in place.</summary>
        public bool Success => FailedEntries.Count == 0;

        /// <summary>Full paths of the plugin files (native libraries and jars) that were written.</summary>
        public IReadOnlyList<string> InstalledFiles { get; }

        /// <summary>One line per entry that could not be placed, as "name: reason".</summary>
        public IReadOnlyList<string> FailedEntries { get; }
    }

    /// <summary>
    /// Unpacks one FFI release zip into its <c>ffi-*</c> folder. A folder is either fully replaced or
    /// reported as failed; a partially replaced folder still holds a stale native and must not be
    /// recorded in the lock file as the manifest version.
    /// </summary>
    public static class FfiArchiveInstaller
    {
        private const string SharedAndroidJar = "libwebrtc.jar";
        private static readonly string[] NotShipped = { "livekit_ffi.h", "LICENSE.md" };

        public static FfiArchiveInstallResult Install(string zipPath, string destFolder, string platform, bool includeSharedAndroidJar)
        {
            if (string.IsNullOrEmpty(zipPath)) throw new ArgumentException("Zip path is required.", nameof(zipPath));
            if (string.IsNullOrEmpty(destFolder)) throw new ArgumentException("Destination folder is required.", nameof(destFolder));

            bool android = string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase);
            bool dropJar = android && !includeSharedAndroidJar;
            var installed = new List<string>();
            var failed = new List<string>();

            Directory.CreateDirectory(destFolder);

            using (ZipArchive archive = ZipFile.OpenRead(zipPath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name) || entry.Name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (IsNotShipped(entry.Name))
                        continue;
                    if (dropJar && IsSharedAndroidJar(entry.Name))
                        continue;

                    string destPath = Path.Combine(destFolder, entry.Name);
                    string tempPath = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}_{entry.Name}");
                    try
                    {
                        entry.ExtractToFile(tempPath, overwrite: true);
                        if (!TryMoveIntoPlace(tempPath, destPath))
                        {
                            failed.Add($"{entry.Name}: the existing file is locked or could not be replaced");
                        }
                        else if (IsPluginFileName(entry.Name))
                        {
                            installed.Add(destPath);
                        }
                    }
                    catch (Exception ex)
                    {
                        failed.Add($"{entry.Name}: {ex.Message}");
                    }
                    finally
                    {
                        TryDeleteTemp(tempPath);
                    }
                }
            }

            // Leftovers from an earlier install that the purge could not remove.
            foreach (string name in NotShipped)
                RemoveStale(destFolder, name, failed);
            if (dropJar)
                RemoveStale(destFolder, SharedAndroidJar, failed);

            return new FfiArchiveInstallResult(installed, failed);
        }

        internal static bool IsPluginFileName(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext == ".dll" || ext == ".so" || ext == ".dylib" || ext == ".a" || ext == ".jar";
        }

        internal static bool TryMoveIntoPlace(string tempPath, string destPath, int retryCount = 3)
        {
            if (!File.Exists(tempPath))
                return true;

            for (int attempt = 0; attempt < retryCount; attempt++)
            {
                try
                {
                    if (File.Exists(destPath))
                    {
                        File.SetAttributes(destPath, FileAttributes.Normal);
                        File.Delete(destPath);
                    }

                    File.Move(tempPath, destPath);
                    return true;
                }
                catch (UnauthorizedAccessException)
                {
                    Thread.Sleep(200);
                }
                catch (IOException)
                {
                    Thread.Sleep(200);
                }
            }

            return false;
        }

        internal static void TryDeleteTemp(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return;
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch
            {
                // Temp cleanup is best effort.
            }
        }

        private static void RemoveStale(string destFolder, string name, List<string> failed)
        {
            string path = Path.Combine(destFolder, name);
            if (!File.Exists(path))
                return;
            try
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
            catch (Exception ex)
            {
                failed.Add($"{name}: stale copy could not be removed ({ex.Message})");
            }
        }

        private static bool IsNotShipped(string name)
        {
            foreach (string skip in NotShipped)
            {
                if (string.Equals(name, skip, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        private static bool IsSharedAndroidJar(string name)
            => string.Equals(name, SharedAndroidJar, StringComparison.OrdinalIgnoreCase);
    }
}
