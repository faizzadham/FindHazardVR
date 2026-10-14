using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace LiveKit.Editor
{
    /// <summary>
    /// Automatically downloads LiveKit FFI native libraries on first import.
    /// This keeps the package size small by downloading platform-specific binaries on-demand.
    /// Downloaded native plugins are written under Assets/Convai/... so installs from a
    /// read-only PackageCache location still have a writable import destination.
    /// </summary>
    [InitializeOnLoad]
    public static class LiveKitFFIDownloader
    {
        private static readonly string PluginsPath;
        private static string LockPath => Path.Combine(PluginsPath, FfiLockFile.FileName);
        private static bool _isDownloadInProgress;

        private static readonly Dictionary<string, string[]> PlatformArchitectures = new()
        {
            { "android", new[] { "arm64", "armv7", "x86_64" } },
            { "ios", new[] { "arm64", "sim-arm64" } },
            { "macos", new[] { "arm64", "x86_64" } },
            { "linux", new[] { "x86_64", "arm64" } },
            { "windows", new[] { "arm64", "x86_64" } }
        };

        private sealed class DownloadResult
        {
            public bool Success { get; }
            public string Platform { get; }
            public string Arch { get; }
            public List<string> AssetPaths { get; }
            public string Sha256 { get; }

            private DownloadResult(bool success, string platform, string arch, List<string> assetPaths, string sha256)
            {
                Success = success;
                Platform = platform;
                Arch = arch;
                AssetPaths = assetPaths ?? new List<string>();
                Sha256 = sha256;
            }

            public static DownloadResult SuccessResult(string platform, string arch, List<string> assetPaths, string sha256)
                => new DownloadResult(true, platform, arch, assetPaths, sha256);

            public static DownloadResult Failure(string platform, string arch)
                => new DownloadResult(false, platform, arch, new List<string>(), null);
        }


        static LiveKitFFIDownloader()
        {
            // Keep downloaded native libraries in a project-local Assets path instead of the
            // package folder because UPM installs may live in a read-only PackageCache.
            PluginsPath = Path.GetFullPath(Path.Combine(Application.dataPath, "Convai", "Plugins", "client-sdk-unity-livekit", "Runtime", "Plugins"));

            if (!Directory.Exists(PluginsPath))
            {
                Directory.CreateDirectory(PluginsPath);
            }

            EditorApplication.delayCall += CheckAndDownloadFFI;
        }

        private static string FullPathToAssetPath(string fullPath)
        {
            if (string.IsNullOrEmpty(fullPath))
                return "";

            string normalized = fullPath.Replace("\\", "/");
            string dataPath = Application.dataPath.Replace("\\", "/");
            if (!normalized.StartsWith(dataPath, StringComparison.OrdinalIgnoreCase))
                return "";

            string relative = normalized.Substring(dataPath.Length).TrimStart('/');
            return $"Assets/{relative}";
        }

        private static void CheckAndDownloadFFI()
        {
            if (_isDownloadInProgress)
                return;

            string[] requiredPlatforms = GetRequiredPlatformsForDownload();
            if (requiredPlatforms == null || requiredPlatforms.Length == 0)
                return;

            List<string> requiredFolders = requiredPlatforms
                .SelectMany(p => GetRequiredArchitecturesForDownload(p).Select(a => FolderName(p, a)))
                .ToList();

            FfiInstallPlan plan = BuildPlan(requiredFolders);
            if (plan.IsNoOp)
                return;

            if (plan.PurgeAll)
                Debug.Log($"[LiveKit] Native FFI cache does not match {LiveKitFfiManifest.Tag}. Reinstalling.");
            else
                Debug.Log($"[LiveKit] Missing or changed FFI libraries: {string.Join(", ", plan.Downloads)}. Starting download.");

            _ = ExecutePlanAsync(plan);
        }

        private static FfiInstallPlan BuildPlan(IEnumerable<string> requiredFolders)
        {
            FfiLockFile lockFile = FfiLockFile.Read(LockPath);
            List<string> present = PresentFolders();

            return FfiInstallPlanner.Plan(LiveKitFfiManifest.Tag, LiveKitFfiManifest.Sha256, lockFile, requiredFolders, present, SharedAndroidJarOwners());
        }

        private static List<string> PresentFolders()
        {
            return Directory.Exists(PluginsPath)
                ? Directory.GetDirectories(PluginsPath, "ffi-*").Where(HasNativeLibraryInFolder).Select(Path.GetFileName).ToList()
                : new List<string>();
        }

        /// <summary>Installed Android folders that currently hold the shared libwebrtc.jar.</summary>
        private static List<string> SharedAndroidJarOwners()
        {
            return Directory.Exists(PluginsPath)
                ? Directory.GetDirectories(PluginsPath, "ffi-android-*")
                    .Where(folder => File.Exists(Path.Combine(folder, "libwebrtc.jar")))
                    .Select(Path.GetFileName)
                    .ToList()
                : new List<string>();
        }

        private static string FolderName(string platform, string arch) => $"ffi-{platform}-{arch}";

        private static async Task ExecutePlanAsync(FfiInstallPlan plan)
        {
            if (_isDownloadInProgress) return;
            _isDownloadInProgress = true;
            try
            {
                FfiLockFile lockFile = plan.PurgeAll ? null : FfiLockFile.Read(LockPath);
                if (plan.PurgeAll)
                {
                    foreach (string folder in plan.Purges)
                        PurgeFolder(folder);
                    lockFile = new FfiLockFile { ffiTag = LiveKitFfiManifest.Tag };
                    lockFile.Write(LockPath);
                }

                lockFile ??= new FfiLockFile { ffiTag = LiveKitFfiManifest.Tag };

                FfiInstallStep[] downloads = plan.Steps.Where(s => s.Action == FfiInstallAction.Download).ToArray();
                for (int i = 0; i < downloads.Length; i++)
                {
                    FfiInstallStep step = downloads[i];
                    SplitFolder(step.Folder, out string platform, out string arch);

                    DownloadResult result = await DownloadAndExtractFFIAsync(
                        platform, arch, $"Downloading LiveKit FFI ({i + 1}/{downloads.Length})", step.CarriesSharedAndroidJar);
                    if (!result.Success)
                        continue;

                    ApplyImportSettingsForFiles(result.AssetPaths, result.Platform, result.Arch);
                    lockFile.Record(step.Folder, result.Sha256);
                    lockFile.Write(LockPath);
                }

                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
            finally
            {
                _isDownloadInProgress = false;
            }
        }

        private static void SplitFolder(string folder, out string platform, out string arch)
        {
            // ffi-<platform>-<arch>, where arch may itself contain a dash (sim-arm64).
            string[] parts = folder.Split('-', 3);
            platform = parts.Length > 1 ? parts[1] : "";
            arch = parts.Length > 2 ? parts[2] : "";
        }

        private static void PurgeFolder(string folder)
        {
            string folderPath = Path.Combine(PluginsPath, folder);
            try
            {
                if (Directory.Exists(folderPath))
                    Directory.Delete(folderPath, true);
                string meta = folderPath + ".meta";
                if (File.Exists(meta))
                    File.Delete(meta);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[LiveKit] Failed to purge {folder}: {ex.Message}");
            }
        }

        private static bool HasNativeLibraryInFolder(string folderPath)
        {
            try
            {
                if (string.IsNullOrEmpty(folderPath) || !Directory.Exists(folderPath))
                    return false;

                foreach (string file in Directory.GetFiles(folderPath))
                {
                    if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string ext = Path.GetExtension(file);
                    if (string.IsNullOrEmpty(ext))
                        continue;

                    if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".so", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase) ||
                        ext.Equals(".a", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // Best-effort check only.
            }

            return false;
        }

        private static string GetCurrentEditorPlatform()
        {
#if UNITY_EDITOR_WIN
            return "windows";
#elif UNITY_EDITOR_OSX
            return "macos";
#elif UNITY_EDITOR_LINUX
            return "linux";
#else
            return "windows";
#endif
        }

        internal static string GetCurrentEditorArchitecture()
        {
            var arch = RuntimeInformation.ProcessArchitecture;
            return arch switch
            {
                Architecture.Arm64 => "arm64",
                Architecture.X64 => "x86_64",
                Architecture.X86 => "x86",
                _ => "x86_64"
            };
        }

        private static string[] GetRequiredArchitecturesForDownload(string platform)
        {
            if (string.IsNullOrEmpty(platform))
                return Array.Empty<string>();

            List<string> required = new List<string>();

            // The Editor needs only the architecture of the running process.
            string editorPlatform = GetCurrentEditorPlatform();
            if (string.Equals(editorPlatform, platform, StringComparison.OrdinalIgnoreCase))
            {
                required.Add(GetCurrentEditorArchitecture());
            }

            // The active build target may require different or additional architectures. Keep both
            // when it shares the Editor platform (for example, an Apple Silicon Editor and a
            // universal macOS player build).
            string activeBuildPlatform = GetActiveBuildPlatform();
            if (!string.IsNullOrEmpty(activeBuildPlatform) &&
                string.Equals(activeBuildPlatform, platform, StringComparison.OrdinalIgnoreCase))
            {
                string[] buildArchs = GetArchitecturesForActiveBuildTargetPlatform(platform);
                if (buildArchs != null && buildArchs.Length > 0)
                    required.AddRange(buildArchs);
            }

            if (required.Count > 0)
            {
                return required
                    .Where(arch => !string.IsNullOrEmpty(arch))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            // Explicit all-platform downloads still use the complete known architecture list.
            return GetArchitecturesForPlatform(platform);
        }

        private static string[] GetArchitecturesForActiveBuildTargetPlatform(string platform)
        {
            if (string.IsNullOrEmpty(platform))
                return Array.Empty<string>();

            switch (platform)
            {
                case "android":
                    {
                        if (TryGetAndroidEnabledArchitectures(out string[] archs) && archs.Length > 0)
                            return archs;
                        return new[] { "arm64" };
                    }
                case "ios":
                    {
                        if (TryGetIosSdkArchitectures(out string[] archs) && archs.Length > 0)
                            return archs;
                        return new[] { "arm64" };
                    }
                case "macos":
                    {
                        if (TryGetMacOSBuildArchitectures(out string[] archs) && archs.Length > 0)
                            return archs;
                        return new[] { "x86_64" };
                    }
                case "windows":
                    // Unity's StandaloneWindows targets are typically x86_64; users can explicitly download arm64 via menu if needed.
                    return new[] { "x86_64" };
                case "linux":
                    return new[] { "x86_64" };
                default:
                    return GetArchitecturesForPlatform(platform);
            }
        }

        private static bool TryGetMacOSBuildArchitectures(out string[] archs)
        {
            archs = Array.Empty<string>();
            try
            {
                // PlayerSettings.macOSArchitecture exists in newer Unity versions.
                PropertyInfo prop = typeof(PlayerSettings).GetProperty("macOSArchitecture", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    return false;

                object value = prop.GetValue(null, null);
                if (value == null)
                    return false;

                string s = value.ToString() ?? string.Empty;
                // Common values: "x86_64", "ARM64", "x86_64ARM64" (universal).
                bool mentionsArm = s.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0;
                bool mentionsIntel = s.IndexOf("x86", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("x64", StringComparison.OrdinalIgnoreCase) >= 0;
                if (mentionsArm && mentionsIntel)
                {
                    archs = new[] { "arm64", "x86_64" };
                    return true;
                }

                if (s.IndexOf("ARM", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    archs = new[] { "arm64" };
                    return true;
                }

                archs = new[] { "x86_64" };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetAndroidEnabledArchitectures(out string[] archs)
        {
            archs = Array.Empty<string>();
            try
            {
                // PlayerSettings.Android.targetArchitectures exists in many Unity versions.
                Type androidType = typeof(PlayerSettings).GetNestedType("Android", BindingFlags.Public | BindingFlags.NonPublic);
                if (androidType == null)
                    return false;

                PropertyInfo prop = androidType.GetProperty("targetArchitectures", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    return false;

                object value = prop.GetValue(null, null);
                if (value == null)
                    return false;

                // It's a flags enum; stringify and look for known tokens.
                string s = value.ToString() ?? string.Empty;
                List<string> enabled = new List<string>();
                if (s.IndexOf("ARM64", StringComparison.OrdinalIgnoreCase) >= 0)
                    enabled.Add("arm64");
                if (s.IndexOf("ARMv7", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("ARMV7", StringComparison.OrdinalIgnoreCase) >= 0)
                    enabled.Add("armv7");
                if (s.IndexOf("X86_64", StringComparison.OrdinalIgnoreCase) >= 0 || s.IndexOf("X86-64", StringComparison.OrdinalIgnoreCase) >= 0)
                    enabled.Add("x86_64");

                if (enabled.Count == 0)
                    return false;

                archs = enabled.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryGetIosSdkArchitectures(out string[] archs)
        {
            archs = Array.Empty<string>();
            try
            {
                // PlayerSettings.iOS.sdkVersion can indicate device vs simulator.
                Type iosType = typeof(PlayerSettings).GetNestedType("iOS", BindingFlags.Public | BindingFlags.NonPublic);
                if (iosType == null)
                    return false;

                PropertyInfo prop = iosType.GetProperty("sdkVersion", BindingFlags.Public | BindingFlags.Static);
                if (prop == null)
                    return false;

                object value = prop.GetValue(null, null);
                if (value == null)
                    return false;

                string s = value.ToString() ?? string.Empty;
                // Common values: "DeviceSDK", "SimulatorSDK".
                if (s.IndexOf("Simulator", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    archs = new[] { "sim-arm64" };
                    return true;
                }

                archs = new[] { "arm64" };
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string GetRequiredPlatformForDownload()
        {
            string buildPlatform = GetActiveBuildPlatform();
            if (!string.IsNullOrEmpty(buildPlatform))
                return buildPlatform;

            return GetCurrentEditorPlatform();
        }

        private static string[] GetRequiredPlatformsForDownload()
        {
            List<string> required = new List<string>();

            string editorPlatform = GetCurrentEditorPlatform();
            if (!string.IsNullOrEmpty(editorPlatform))
                required.Add(editorPlatform);

            string buildPlatform = GetActiveBuildPlatform();
            if (!string.IsNullOrEmpty(buildPlatform))
                required.Add(buildPlatform);

            return required
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string GetActiveBuildPlatform()
        {
            BuildTarget target = EditorUserBuildSettings.activeBuildTarget;
            switch (target)
            {
                case BuildTarget.Android:
                    return "android";
                case BuildTarget.iOS:
                    return "ios";
                case BuildTarget.StandaloneWindows:
                case BuildTarget.StandaloneWindows64:
                    return "windows";
                case BuildTarget.StandaloneOSX:
                    return "macos";
                case BuildTarget.StandaloneLinux64:
                    return "linux";
                default:
                    return "";
            }
        }

        private static string[] GetArchitecturesForPlatform(string platform)
        {
            if (string.IsNullOrEmpty(platform))
                return new[] { "x86_64" };

            return PlatformArchitectures.GetValueOrDefault(platform, new[] { "x86_64" });
        }

        [MenuItem("Convai/Platform Support/Download FFI Libraries (Current Platform)")]
        public static void DownloadCurrentPlatformFFI() => DownloadForPlatforms(new[] { GetCurrentEditorPlatform() });

        [MenuItem("Convai/Platform Support/Download FFI Libraries (Active Build Target)")]
        public static void DownloadActiveBuildTargetFFI() => DownloadForPlatforms(new[] { GetRequiredPlatformForDownload() });

        [MenuItem("Convai/Platform Support/Download FFI Libraries (All Platforms)")]
        public static void DownloadAllPlatformsFFI()
        {
            List<string> all = PlatformArchitectures.SelectMany(p => p.Value.Select(a => FolderName(p.Key, a))).ToList();
            _ = ExecutePlanAsync(ForceDownload(all));
        }

        private static void DownloadForPlatforms(IEnumerable<string> platforms)
        {
            List<string> folders = platforms
                .Where(p => !string.IsNullOrEmpty(p))
                .SelectMany(p => GetRequiredArchitecturesForDownload(p).Select(a => FolderName(p, a)))
                .ToList();
            _ = ExecutePlanAsync(ForceDownload(folders));
        }

        private static FfiInstallPlan ForceDownload(IEnumerable<string> folders)
        {
            FfiInstallPlan plan = BuildPlan(folders);
            if (plan.PurgeAll)
                return plan;

            var forced = new FfiInstallPlan();
            foreach (string folder in folders)
                forced.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Download, "requested from menu"));
            FfiInstallPlanner.AssignSharedAndroidJar(forced, PresentFolders(), SharedAndroidJarOwners());
            return forced;
        }

        private static void ApplyImportSettingsForFiles(IEnumerable<string> assetPaths, string platform, string arch)
        {
            if (assetPaths == null)
                return;

            AssetDatabase.Refresh();

            HashSet<string> uniquePaths = new HashSet<string>(assetPaths.Where(p => !string.IsNullOrEmpty(p)));
            int total = uniquePaths.Count;
            int current = 0;
            foreach (string assetPath in uniquePaths)
            {
                current++;
                float progress = total > 0 ? (float)current / total : 1f;
                EditorUtility.DisplayProgressBar("Applying LiveKit Import Settings",
                    $"{platform}-{arch}: {Path.GetFileName(assetPath)} ({current}/{total})",
                    progress);

                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = AssetImporter.GetAtPath(assetPath) as PluginImporter;
                if (importer == null)
                {
                    continue;
                }

                if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(Path.GetExtension(assetPath), ".jar", StringComparison.OrdinalIgnoreCase))
                {
                    PluginImportSettingsUtil.ApplyAndroidJarImportSettings(importer);
                }
                else
                {
                    PluginImportSettingsUtil.ApplyNativePluginImportSettings(importer, platform, arch);
                }

                importer.SaveAndReimport();
                AssetDatabase.WriteImportSettingsIfDirty(assetPath);
                AssetDatabase.ForceReserializeAssets(new[] { assetPath }, ForceReserializeAssetsOptions.ReserializeAssetsAndMetadata);
            }

            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        [MenuItem("Convai/Platform Support/Clear FFI Libraries")]
        public static void ClearFFILibraries()
        {
            bool confirm = EditorUtility.DisplayDialog(
                "Clear FFI Libraries",
                "This will delete all downloaded FFI native libraries.\nYou will need to re-download them to use platform support.\n\nContinue?",
                "Clear",
                "Cancel"
            );

            if (!confirm) return;

            foreach (string folder in Directory.Exists(PluginsPath) ? Directory.GetDirectories(PluginsPath, "ffi-*").Select(Path.GetFileName) : Enumerable.Empty<string>())
                PurgeFolder(folder);
            if (File.Exists(LockPath))
                File.Delete(LockPath);
            AssetDatabase.Refresh();
            Debug.Log("[LiveKit] FFI libraries cleared.");
        }

        private static async Task<DownloadResult> DownloadAndExtractFFIAsync(
            string platform,
            string arch,
            string progressTitle,
            bool includeSharedAndroidJar)
        {
            string folderName = $"ffi-{platform}-{arch}";
            string zipFileName = $"{folderName}.zip";
            string downloadUrl = $"{LiveKitFfiManifest.BaseUrl}/{LiveKitFfiManifest.Tag}/{zipFileName}";
            string destFolder = Path.Combine(PluginsPath, folderName);
            string tempZipPath = Path.Combine(Application.temporaryCachePath, zipFileName);

            try
            {
                EditorUtility.DisplayProgressBar(progressTitle, $"Downloading {folderName}...", 0f);

                using (UnityWebRequest request = UnityWebRequest.Get(downloadUrl))
                {
                    var operation = request.SendWebRequest();

                    while (!operation.isDone)
                    {
                        EditorUtility.DisplayProgressBar(progressTitle,
                            $"Downloading {folderName}... ({request.downloadProgress * 100:F0}%)",
                            request.downloadProgress * 0.8f);
                        await Task.Delay(100);
                    }

                    if (request.result != UnityWebRequest.Result.Success)
                    {
                        Debug.LogError($"[LiveKit] Failed to download {folderName}: {request.error}");
                        return DownloadResult.Failure(platform, arch);
                    }

                    File.WriteAllBytes(tempZipPath, request.downloadHandler.data);
                }

                if (!LiveKitFfiManifest.Sha256.TryGetValue(folderName, out string expectedHash))
                {
                    Debug.LogError($"[LiveKit] {folderName} is not listed in LiveKitFfiManifest for {LiveKitFfiManifest.Tag}. Refusing to install.");
                    TryDeleteTemp(tempZipPath);
                    return DownloadResult.Failure(platform, arch);
                }

                string actualHash = ComputeSha256(tempZipPath);
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Debug.LogError($"[LiveKit] Checksum mismatch for {zipFileName}: expected {expectedHash}, got {actualHash}. The download was discarded.");
                    TryDeleteTemp(tempZipPath);
                    return DownloadResult.Failure(platform, arch);
                }

                if (Directory.Exists(destFolder))
                {
                    // A previous install of this folder is replaced wholesale so stale files cannot survive a version bump.
                    PurgeFolder(folderName);
                }

                EditorUtility.DisplayProgressBar(progressTitle, $"Extracting {folderName}...", 0.9f);

                FfiArchiveInstallResult install = FfiArchiveInstaller.Install(tempZipPath, destFolder, platform, includeSharedAndroidJar);
                TryDeleteTemp(tempZipPath);

                if (!install.Success)
                {
                    // Recording this folder would let the planner treat a stale native as verified, so it is left
                    // unrecorded and the next editor start plans it again.
                    Debug.LogError(
                        $"[LiveKit] {folderName} was not installed: {string.Join("; ", install.FailedEntries)}. " +
                        "Close whatever holds the previous native library (on Windows, restart the editor); the installer retries on the next start.");
                    return DownloadResult.Failure(platform, arch);
                }

                List<string> extractedAssetPaths = install.InstalledFiles
                    .Select(FullPathToAssetPath)
                    .Where(assetPath => !string.IsNullOrEmpty(assetPath))
                    .ToList();

                Debug.Log($"[LiveKit] Successfully installed {folderName}");
                return DownloadResult.SuccessResult(platform, arch, extractedAssetPaths, actualHash);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[LiveKit] Error downloading {folderName}: {ex.Message}");
                return DownloadResult.Failure(platform, arch);
            }
        }

        private static string ComputeSha256(string path)
        {
            using FileStream stream = File.OpenRead(path);
            using SHA256 sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(stream);
            var builder = new StringBuilder(hash.Length * 2);
            foreach (byte b in hash) builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static void TryDeleteTemp(string path) => FfiArchiveInstaller.TryDeleteTemp(path);
    }

    public static class PluginImportSettingsUtil
    {
        public static bool ApplyAndroidJarImportSettings(PluginImporter importer)
        {
            bool changed = false;
            changed |= SetCompatibleWithAnyPlatform(importer, false);
            changed |= SetCompatibleWithEditor(importer, false);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.Android, true);
            changed |= DisableOtherPlatforms(importer, BuildTarget.Android);
            return changed;
        }

        public static bool ApplyNativePluginImportSettings(PluginImporter importer, string platform, string arch)
        {
            bool changed = false;
            changed |= SetCompatibleWithAnyPlatform(importer, false);

            switch (platform)
            {
                case "macos":
                    changed |= ApplyMacSettings(importer, arch);
                    break;
                case "windows":
                    changed |= ApplyWindowsSettings(importer, arch);
                    break;
                case "linux":
                    changed |= ApplyLinuxSettings(importer, arch);
                    break;
                case "android":
                    changed |= ApplyAndroidSettings(importer, arch);
                    break;
                case "ios":
                    changed |= ApplyIosSettings(importer, arch);
                    break;
            }

            return changed;
        }

        private static bool ApplyMacSettings(PluginImporter importer, string arch)
        {
            bool changed = false;
            string currentEditorArch = LiveKitFFIDownloader.GetCurrentEditorArchitecture();
            bool enableForEditor = arch == currentEditorArch;

            changed |= SetCompatibleWithEditor(importer, enableForEditor);
            if (enableForEditor)
            {
                changed |= SetEditorData(importer, "OS", "OSX");
                changed |= SetEditorData(importer, "CPU", arch == "arm64" ? "ARM64" : "x86_64");
            }

            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneOSX, true);
            changed |= SetPlatformData(importer, BuildTarget.StandaloneOSX, "CPU", arch == "arm64" ? "ARM64" : "x86_64");

            changed |= DisableOtherPlatforms(importer, BuildTarget.StandaloneOSX);
            return changed;
        }

        private static bool ApplyWindowsSettings(PluginImporter importer, string arch)
        {
            bool changed = false;
            string currentEditorArch = LiveKitFFIDownloader.GetCurrentEditorArchitecture();
            bool enableForEditor = arch == currentEditorArch;

            changed |= SetCompatibleWithEditor(importer, enableForEditor);
            if (enableForEditor)
            {
                changed |= SetEditorData(importer, "OS", "Windows");
                changed |= SetEditorData(importer, "CPU", arch == "arm64" ? "ARM64" : "x86_64");
            }

            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneWindows64, true);
            if (arch == "arm64")
            {
                changed |= SetPlatformData(importer, BuildTarget.StandaloneWindows64, "CPU", "ARM64");
            }
            else
            {
                changed |= SetPlatformData(importer, BuildTarget.StandaloneWindows64, "CPU", "x86_64");
            }

            changed |= DisableOtherPlatforms(importer, BuildTarget.StandaloneWindows64);
            return changed;
        }

        private static bool ApplyLinuxSettings(PluginImporter importer, string arch)
        {
            bool changed = false;
            bool isArm = string.Equals(arch, "arm64", StringComparison.OrdinalIgnoreCase);
            string cpu = isArm ? "ARM64" : "x86_64";
            bool enableForEditor = !isArm;

            changed |= SetCompatibleWithEditor(importer, enableForEditor);
            if (enableForEditor)
            {
                changed |= SetEditorData(importer, "OS", "Linux");
                changed |= SetEditorData(importer, "CPU", "x86_64");
            }

            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneLinux64, true);
            changed |= SetPlatformData(importer, BuildTarget.StandaloneLinux64, "CPU", cpu);
            changed |= DisableOtherPlatforms(importer, BuildTarget.StandaloneLinux64);
            return changed;
        }

        private static bool ApplyAndroidSettings(PluginImporter importer, string arch)
        {
            bool changed = false;
            changed |= SetCompatibleWithEditor(importer, false);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.Android, true);

            string androidCpu = arch switch
            {
                "arm64" => "ARM64",
                "armv7" => "ARMv7",
                "x86_64" => "x86_64",
                _ => "ARM64"
            };
            changed |= SetPlatformData(importer, BuildTarget.Android, "CPU", androidCpu);
            changed |= SetPlatformData(importer, BuildTarget.Android, "AndroidCPU", androidCpu);
            changed |= SetPlatformData(importer, BuildTarget.Android, "OS", "Android");

            if (!string.Equals(importer.GetPlatformData(BuildTarget.Android, "CPU"), androidCpu, StringComparison.OrdinalIgnoreCase))
            {
                changed |= ForceAndroidPlatformData(importer, androidCpu);
            }

            changed |= DisableOtherPlatforms(importer, BuildTarget.Android);
            return changed;
        }

        private static bool ApplyIosSettings(PluginImporter importer, string arch)
        {
            bool changed = false;
            changed |= SetCompatibleWithEditor(importer, false);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.iOS, true);

            if (arch == "arm64" || arch == "sim-arm64")
            {
                changed |= SetPlatformData(importer, BuildTarget.iOS, "CPU", "ARM64");
            }

            changed |= DisableOtherPlatforms(importer, BuildTarget.iOS);
            return changed;
        }

        private static bool DisableOtherPlatforms(PluginImporter importer, BuildTarget activeTarget)
        {
            bool changed = false;
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.Android, activeTarget == BuildTarget.Android);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.iOS, activeTarget == BuildTarget.iOS);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneWindows64, activeTarget == BuildTarget.StandaloneWindows64);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneOSX, activeTarget == BuildTarget.StandaloneOSX);
            changed |= SetCompatibleWithPlatform(importer, BuildTarget.StandaloneLinux64, activeTarget == BuildTarget.StandaloneLinux64);
            return changed;
        }

        private static bool SetCompatibleWithAnyPlatform(PluginImporter importer, bool value)
        {
            bool current = importer.GetCompatibleWithAnyPlatform();
            if (current == value) return false;
            importer.SetCompatibleWithAnyPlatform(value);
            return true;
        }

        private static bool SetCompatibleWithEditor(PluginImporter importer, bool value)
        {
            bool current = importer.GetCompatibleWithEditor();
            if (current == value) return false;
            importer.SetCompatibleWithEditor(value);
            return true;
        }

        private static bool SetCompatibleWithPlatform(PluginImporter importer, BuildTarget target, bool value)
        {
            bool current = importer.GetCompatibleWithPlatform(target);
            if (current == value) return false;
            importer.SetCompatibleWithPlatform(target, value);
            return true;
        }

        private static bool SetEditorData(PluginImporter importer, string key, string value)
        {
            string current = importer.GetEditorData(key);
            if (current == value) return false;
            importer.SetEditorData(key, value);
            return true;
        }

        private static bool SetPlatformData(PluginImporter importer, BuildTarget target, string key, string value)
        {
            string current = importer.GetPlatformData(target, key);
            if (current == value) return false;
            importer.SetPlatformData(target, key, value);
            return true;
        }

        private static bool ForceAndroidPlatformData(PluginImporter importer, string androidCpu)
        {
            SerializedObject serialized = new SerializedObject(importer);
            SerializedProperty platformData = serialized.FindProperty("m_PlatformData");

            if (platformData == null || !platformData.isArray)
                return false;

            bool changed = false;
            for (int i = 0; i < platformData.arraySize; i++)
            {
                SerializedProperty element = platformData.GetArrayElementAtIndex(i);
                SerializedProperty keyProp = element.FindPropertyRelative("first");
                if (keyProp == null || keyProp.stringValue != "Android")
                    continue;

                SerializedProperty valueProp = element.FindPropertyRelative("second");
                if (valueProp == null)
                    continue;

                SerializedProperty enabledProp = valueProp.FindPropertyRelative("m_Enabled");
                if (enabledProp != null && !enabledProp.boolValue)
                {
                    enabledProp.boolValue = true;
                    changed = true;
                }

                SerializedProperty settingsProp = valueProp.FindPropertyRelative("m_Settings");
                if (settingsProp != null && settingsProp.isArray)
                {
                    changed |= UpsertPlatformSetting(settingsProp, "CPU", androidCpu);
                    changed |= UpsertPlatformSetting(settingsProp, "AndroidCPU", androidCpu);
                    changed |= UpsertPlatformSetting(settingsProp, "OS", "Android");
                }
            }

            if (changed)
            {
                serialized.ApplyModifiedPropertiesWithoutUndo();
            }

            return changed;
        }

        private static bool UpsertPlatformSetting(SerializedProperty settingsArray, string key, string value)
        {
            for (int i = 0; i < settingsArray.arraySize; i++)
            {
                SerializedProperty element = settingsArray.GetArrayElementAtIndex(i);
                SerializedProperty keyProp = element.FindPropertyRelative("first");
                SerializedProperty valueProp = element.FindPropertyRelative("second");

                if (keyProp != null && keyProp.stringValue == key)
                {
                    if (valueProp != null && valueProp.stringValue == value)
                        return false;

                    if (valueProp != null)
                    {
                        valueProp.stringValue = value;
                        return true;
                    }
                }
            }

            int insertIndex = settingsArray.arraySize;
            settingsArray.InsertArrayElementAtIndex(insertIndex);
            SerializedProperty newElement = settingsArray.GetArrayElementAtIndex(insertIndex);
            SerializedProperty newKey = newElement.FindPropertyRelative("first");
            SerializedProperty newValue = newElement.FindPropertyRelative("second");

            if (newKey != null)
                newKey.stringValue = key;
            if (newValue != null)
                newValue.stringValue = value;

            return true;
        }
    }
}
