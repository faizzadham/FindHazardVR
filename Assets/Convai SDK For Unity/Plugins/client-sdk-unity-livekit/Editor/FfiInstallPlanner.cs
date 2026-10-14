using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveKit.Editor
{
    public enum FfiInstallAction
    {
        Keep,
        Download,
        Purge
    }

    public readonly struct FfiInstallStep
    {
        public FfiInstallStep(string folder, FfiInstallAction action, string reason)
            : this(folder, action, reason, carriesSharedAndroidJar: false)
        {
        }

        public FfiInstallStep(string folder, FfiInstallAction action, string reason, bool carriesSharedAndroidJar)
        {
            Folder = folder;
            Action = action;
            Reason = reason;
            CarriesSharedAndroidJar = carriesSharedAndroidJar;
        }

        public string Folder { get; }
        public FfiInstallAction Action { get; }
        public string Reason { get; }

        /// <summary>
        /// True for the one Android download that unpacks the shared <c>libwebrtc.jar</c>.
        /// Every other Android folder omits it; a second copy fails the Android build with duplicate classes.
        /// </summary>
        public bool CarriesSharedAndroidJar { get; }
    }

    public sealed class FfiInstallPlan
    {
        public bool PurgeAll { get; internal set; }
        public List<FfiInstallStep> Steps { get; } = new List<FfiInstallStep>();

        public IEnumerable<string> Downloads => Steps.Where(s => s.Action == FfiInstallAction.Download).Select(s => s.Folder);
        public IEnumerable<string> Purges => Steps.Where(s => s.Action == FfiInstallAction.Purge).Select(s => s.Folder);
        public bool IsNoOp => Steps.All(s => s.Action == FfiInstallAction.Keep);
    }

    /// <summary>
    /// Decides what the installer must do. Pure: no file, network or editor access.
    /// </summary>
    public static class FfiInstallPlanner
    {
        private const string AndroidFolderPrefix = "ffi-android-";
        private const string AndroidArm64Folder = "ffi-android-arm64";

        /// <param name="sharedAndroidJarOwners">
        /// Present folders that already hold <c>libwebrtc.jar</c>. When one of them survives the plan,
        /// no download unpacks the jar again.
        /// </param>
        public static FfiInstallPlan Plan(
            string manifestTag,
            IReadOnlyDictionary<string, string> manifestHashes,
            FfiLockFile lockFile,
            IEnumerable<string> requiredFolders,
            IEnumerable<string> presentFolders,
            IEnumerable<string> sharedAndroidJarOwners = null)
        {
            if (string.IsNullOrEmpty(manifestTag)) throw new ArgumentException("Manifest tag is required.", nameof(manifestTag));
            if (manifestHashes == null) throw new ArgumentNullException(nameof(manifestHashes));

            var plan = new FfiInstallPlan();
            var required = (requiredFolders ?? Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var present = new HashSet<string>(presentFolders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);

            bool tagMismatch = lockFile == null || !string.Equals(lockFile.FfiTag, manifestTag, StringComparison.Ordinal);
            if (tagMismatch)
            {
                plan.PurgeAll = true;
                string why = lockFile == null ? "no lock file" : $"lock is {lockFile.FfiTag}, manifest is {manifestTag}";
                foreach (string folder in present)
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Purge, why));
                foreach (string folder in required)
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Download, why));
                AssignSharedAndroidJar(plan, present, sharedAndroidJarOwners, manifestHashes);
                return plan;
            }

            foreach (string folder in required)
            {
                if (!manifestHashes.TryGetValue(folder, out string expected))
                {
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Keep, "not in manifest"));
                    continue;
                }

                if (!present.Contains(folder))
                {
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Download, "missing"));
                    continue;
                }

                string recorded = lockFile.HashFor(folder);
                if (recorded == null)
                {
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Download, "present but not recorded"));
                    continue;
                }

                if (!string.Equals(recorded, expected, StringComparison.OrdinalIgnoreCase))
                {
                    plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Download, "hash changed"));
                    continue;
                }

                plan.Steps.Add(new FfiInstallStep(folder, FfiInstallAction.Keep, "verified"));
            }

            AssignSharedAndroidJar(plan, present, sharedAndroidJarOwners, manifestHashes);
            return plan;
        }

        /// <summary>
        /// Gives <c>libwebrtc.jar</c> to exactly one Android folder. A present owner that the plan neither
        /// purges nor re-downloads keeps it; otherwise the arm64 download takes it, else the first Android download.
        /// If no download is pending, re-download one required folder from the manifest to restore a missing jar.
        /// </summary>
        public static void AssignSharedAndroidJar(FfiInstallPlan plan, IEnumerable<string> presentFolders,
            IEnumerable<string> sharedAndroidJarOwners, IReadOnlyDictionary<string, string> manifestHashes = null)
        {
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var present = new HashSet<string>(presentFolders ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var replaced = new HashSet<string>(plan.Purges.Concat(plan.Downloads), StringComparer.OrdinalIgnoreCase);
            bool ownerSurvives = (sharedAndroidJarOwners ?? Array.Empty<string>())
                .Any(folder => IsAndroid(folder) && present.Contains(folder) && !replaced.Contains(folder));

            for (int i = 0; i < plan.Steps.Count; i++)
            {
                FfiInstallStep step = plan.Steps[i];
                if (step.CarriesSharedAndroidJar)
                    plan.Steps[i] = new FfiInstallStep(step.Folder, step.Action, step.Reason, carriesSharedAndroidJar: false);
            }

            if (ownerSurvives)
                return;

            int owner = -1;
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                FfiInstallStep step = plan.Steps[i];
                if (step.Action != FfiInstallAction.Download || !IsAndroid(step.Folder))
                    continue;
                if (owner < 0)
                    owner = i;
                if (string.Equals(step.Folder, AndroidArm64Folder, StringComparison.OrdinalIgnoreCase))
                {
                    owner = i;
                    break;
                }
            }

            if (owner < 0 && manifestHashes != null)
            {
                for (int i = 0; i < plan.Steps.Count; i++)
                {
                    FfiInstallStep step = plan.Steps[i];
                    if (step.Action != FfiInstallAction.Keep || !IsAndroid(step.Folder) ||
                        !manifestHashes.ContainsKey(step.Folder))
                        continue;
                    if (owner < 0)
                        owner = i;
                    if (string.Equals(step.Folder, AndroidArm64Folder, StringComparison.OrdinalIgnoreCase))
                    {
                        owner = i;
                        break;
                    }
                }

                if (owner >= 0)
                    plan.Steps[owner] = new FfiInstallStep(plan.Steps[owner].Folder,
                        FfiInstallAction.Download, "shared Android jar missing");
            }

            if (owner < 0)
                return;

            FfiInstallStep chosen = plan.Steps[owner];
            plan.Steps[owner] = new FfiInstallStep(chosen.Folder, chosen.Action, chosen.Reason, carriesSharedAndroidJar: true);
        }

        private static bool IsAndroid(string folder)
            => folder != null && folder.StartsWith(AndroidFolderPrefix, StringComparison.OrdinalIgnoreCase);
    }
}
