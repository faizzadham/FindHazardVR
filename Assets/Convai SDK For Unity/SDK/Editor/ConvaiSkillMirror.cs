using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace Convai.Editor
{
    /// <summary>What the project copy of the skill is, relative to its stamp and the installed SDK.</summary>
    internal enum ConvaiSkillMirrorState
    {
        /// <summary>No mirror folder. Install.</summary>
        NotInstalled,

        /// <summary>Matches its stamp and the currently installed SDK and skill.</summary>
        Installed,

        /// <summary>Matches its stamp, but the installed SDK version or source content moved. Update.</summary>
        Stale,

        /// <summary>Differs from its stamped files. Needs an explicit replace or remove choice.</summary>
        LocallyModified,

        /// <summary>A skill folder Convai did not install, or one whose stamp is missing or unreadable.</summary>
        Unmanaged
    }

    internal readonly struct ConvaiSkillMirrorStatus
    {
        internal ConvaiSkillMirrorStatus(
            ConvaiSkillMirrorState state,
            string detail,
            bool updateAvailable,
            string installedSdkVersion)
        {
            State = state;
            Detail = detail;
            UpdateAvailable = updateAvailable;
            InstalledSdkVersion = installedSdkVersion;
        }

        internal ConvaiSkillMirrorState State { get; }
        internal string Detail { get; }

        /// <summary>Retained alongside <see cref="ConvaiSkillMirrorState.LocallyModified" />, which wins the label.</summary>
        internal bool UpdateAvailable { get; }

        /// <summary>SDK version recorded by the stamp, not the one installed now.</summary>
        internal string InstalledSdkVersion { get; }
    }

    /// <summary>Ownership record written beside the copied payload.</summary>
    internal sealed class ConvaiSkillStamp
    {
        internal const int CurrentSchemaVersion = 1;

        [JsonProperty("schemaVersion")] public int SchemaVersion { get; set; } = CurrentSchemaVersion;
        [JsonProperty("skillName")] public string SkillName { get; set; }
        [JsonProperty("skillVersion")] public string SkillVersion { get; set; }
        [JsonProperty("sdkVersion")] public string SdkVersion { get; set; }
        [JsonProperty("installedContentHash")] public string InstalledContentHash { get; set; }
        [JsonProperty("files")] public Dictionary<string, string> Files { get; set; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    ///     Installs, updates, and removes a client project copy of the canonical skill at
    ///     <c>.claude/skills</c> or <c>.agents/skills</c> inside the project.
    ///     Promotion is a temp-folder/backup rename sequence: the payload lands in a sibling temporary
    ///     folder, the existing mirror is renamed aside, and only then does the temporary folder take
    ///     the real name. There is no journal and no startup recovery pass; a failure rolls back within
    ///     the operation that caused it.
    /// </summary>
    internal static class ConvaiSkillMirror
    {
        internal const string ClaudeSkillsRelativeRoot = ".claude/skills";
        internal const string CodexSkillsRelativeRoot = ".agents/skills";

        internal static string GetSkillsRelativeRoot(ConvaiAgentClient client) => client switch
        {
            ConvaiAgentClient.ClaudeCode => ClaudeSkillsRelativeRoot,
            ConvaiAgentClient.Codex => CodexSkillsRelativeRoot,
            _ => throw new ArgumentOutOfRangeException(nameof(client), client, "This client has no Convai skill mirror.")
        };
        internal const string TemporaryFolderName = ".convai-unity-sdk.tmp";
        internal const string BackupFolderName = ".convai-unity-sdk.bak";

        internal static string GetSkillsRoot(string projectRoot, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode) =>
            Path.Combine(RequireProjectRoot(projectRoot), GetSkillsRelativeRoot(client));

        internal static string GetMirrorRoot(string projectRoot, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode) =>
            Path.Combine(GetSkillsRoot(projectRoot, client), ConvaiSkillSourceResolver.SkillName);

        internal static string GetStampPath(string projectRoot, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode) =>
            Path.Combine(GetMirrorRoot(projectRoot, client), ConvaiSkillPayload.StampFileName);

        /// <summary>
        ///     Classifies the mirror without touching it. Local modification wins over staleness, and
        ///     the update-available detail is retained so the user still learns both facts.
        /// </summary>
        internal static ConvaiSkillMirrorStatus Inspect(string projectRoot, ConvaiSkillSource source, ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            string mirrorRoot = GetMirrorRoot(projectRoot, client);
            if (!Directory.Exists(mirrorRoot))
                return new ConvaiSkillMirrorStatus(
                    ConvaiSkillMirrorState.NotInstalled,
                    "No Convai skill mirror in this project.",
                    false,
                    null);

            if (!TryReadStamp(projectRoot, out ConvaiSkillStamp stamp, out string stampError, client))
                return new ConvaiSkillMirrorStatus(
                    ConvaiSkillMirrorState.Unmanaged,
                    stampError,
                    false,
                    null);

            IReadOnlyDictionary<string, string> actual;
            try
            {
                actual = ConvaiSkillPayload.ComputeFileHashes(mirrorRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return new ConvaiSkillMirrorStatus(
                    ConvaiSkillMirrorState.Unmanaged,
                    $"Could not read the Convai skill mirror at '{ConvaiSkillSourceResolver.Normalize(mirrorRoot)}': " +
                    $"{exception.Message} Check file permissions or locks and refresh.",
                    false,
                    stamp.SdkVersion);
            }

            string difference = DescribeDifference(stamp.Files, actual);
            bool sourceChanged = false;
            string sourceDetail;
            string sourceError = null;
            try
            {
                sourceChanged = TryDescribeSourceChange(stamp, source, out sourceDetail);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                sourceError = $"The packaged skill could not be read: {exception.Message} Whether it is up to date is unknown.";
                sourceDetail = sourceError;
            }

            if (difference != null)
                return new ConvaiSkillMirrorStatus(
                    ConvaiSkillMirrorState.LocallyModified,
                    sourceChanged ? $"{difference} An update is also available: {sourceDetail}" : difference,
                    sourceChanged,
                    stamp.SdkVersion);

            return sourceChanged
                ? new ConvaiSkillMirrorStatus(ConvaiSkillMirrorState.Stale, sourceDetail, true, stamp.SdkVersion)
                : new ConvaiSkillMirrorStatus(
                    ConvaiSkillMirrorState.Installed,
                    sourceError != null
                        ? $"Matches its stamp from Convai SDK {stamp.SdkVersion}. {sourceError}"
                        : source.IsValid
                        ? $"Matches Convai SDK {stamp.SdkVersion}."
                        : $"Matches its stamp from Convai SDK {stamp.SdkVersion}. The packaged skill " +
                          "cannot be resolved, so whether it is up to date is unknown.",
                    false,
                    stamp.SdkVersion);
        }

        /// <summary>
        ///     Installs or updates the mirror to whatever SDK is installed now, newer or older. There
        ///     is no separate downgrade path and no metadata-only refresh.
        /// </summary>
        /// <param name="replaceExisting">
        ///     Required when the destination is locally modified or unmanaged; without it those states
        ///     are refused rather than overwritten.
        /// </param>
        internal static void Install(string projectRoot, ConvaiSkillSource source, bool replaceExisting = false,
            ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            if (!source.IsValid)
                throw new InvalidOperationException("Cannot install the Convai skill mirror without a resolved skill source.");

            string mirrorRoot = GetMirrorRoot(projectRoot, client);
            string skillsRoot = GetSkillsRoot(projectRoot, client);
            string temporaryRoot = Path.Combine(skillsRoot, TemporaryFolderName);
            string backupRoot = Path.Combine(skillsRoot, BackupFolderName);

            ConvaiSkillMirrorStatus status = Inspect(projectRoot, source, client);
            if (!replaceExisting &&
                status.State is ConvaiSkillMirrorState.LocallyModified or ConvaiSkillMirrorState.Unmanaged)
                throw new InvalidOperationException(
                    $"'{ConvaiSkillSourceResolver.Normalize(mirrorRoot)}' is {Describe(status.State)}: {status.Detail} " +
                    "Choose Replace to overwrite it.");

            Directory.CreateDirectory(skillsRoot);
            RequireAbsent(temporaryRoot, "temporary");
            RequireAbsent(backupRoot, "backup");

            bool backedUp = false;
            try
            {
                ConvaiSkillPayload.CopyTo(source.SkillRoot, temporaryRoot);
                WriteStamp(temporaryRoot, BuildStamp(source, temporaryRoot));

                if (Directory.Exists(mirrorRoot))
                {
                    Directory.Move(mirrorRoot, backupRoot);
                    backedUp = true;
                }

                Directory.Move(temporaryRoot, mirrorRoot);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not install the Convai skill mirror: {exception.Message}{Rollback(mirrorRoot, temporaryRoot, backupRoot, backedUp)}",
                    exception);
            }

            // Promotion succeeded. A backup that will not delete is reported rather than retried: a
            // half-deleted backup is not a rollback source, and the new mirror is already complete.
            try
            {
                if (Directory.Exists(backupRoot)) Directory.Delete(backupRoot, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"The Convai skill mirror was installed, but the previous copy at " +
                    $"'{ConvaiSkillSourceResolver.Normalize(backupRoot)}' could not be deleted: {exception.Message} " +
                    "Delete that folder manually.",
                    exception);
            }
        }

        /// <summary>
        ///     Removes only the verified Convai mirror. Other skills, the client skills directory itself, and
        ///     any MCP configuration are left alone.
        /// </summary>
        /// <param name="source">
        ///     May be default. Removal inspects the destination and its stamp; a missing source only
        ///     means staleness cannot be judged.
        /// </param>
        /// <param name="force">Required to delete locally modified or unmanaged content.</param>
        internal static void Remove(string projectRoot, ConvaiSkillSource source, bool force = false,
            ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            string mirrorRoot = GetMirrorRoot(projectRoot, client);
            if (!Directory.Exists(mirrorRoot)) return;

            ConvaiSkillMirrorStatus status = Inspect(projectRoot, source, client);
            if (!force && status.State is ConvaiSkillMirrorState.LocallyModified or ConvaiSkillMirrorState.Unmanaged)
                throw new InvalidOperationException(
                    $"'{ConvaiSkillSourceResolver.Normalize(mirrorRoot)}' is {Describe(status.State)}: {status.Detail} " +
                    "Choose Remove anyway to delete it.");

            try
            {
                Directory.Delete(mirrorRoot, true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    $"Could not remove the Convai skill mirror: {exception.Message}",
                    exception);
            }
        }

        internal static bool TryReadStamp(string projectRoot, out ConvaiSkillStamp stamp, out string error,
            ConvaiAgentClient client = ConvaiAgentClient.ClaudeCode)
        {
            stamp = null;
            string path = GetStampPath(projectRoot, client);
            if (!File.Exists(path))
            {
                error =
                    $"'{ConvaiSkillSourceResolver.Normalize(GetMirrorRoot(projectRoot, client))}' has no " +
                    $"'{ConvaiSkillPayload.StampFileName}', so Convai did not install it.";
                return false;
            }

            try
            {
                stamp = JsonConvert.DeserializeObject<ConvaiSkillStamp>(File.ReadAllText(path));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                error = $"'{ConvaiSkillSourceResolver.Normalize(path)}' is unreadable: {exception.Message}";
                return false;
            }

            if (stamp == null || stamp.Files == null || stamp.SchemaVersion != ConvaiSkillStamp.CurrentSchemaVersion ||
                !stamp.Files.ContainsKey(ConvaiSkillSourceResolver.SkillFileName))
            {
                error =
                    $"'{ConvaiSkillSourceResolver.Normalize(path)}' is not a schema " +
                    $"{ConvaiSkillStamp.CurrentSchemaVersion} Convai skill stamp containing SKILL.md.";
                stamp = null;
                return false;
            }

            error = string.Empty;
            return true;
        }

        internal static ConvaiSkillStamp BuildStamp(ConvaiSkillSource source, string payloadRoot)
        {
            IReadOnlyDictionary<string, string> files = ConvaiSkillPayload.ComputeFileHashes(payloadRoot);
            if (!files.ContainsKey(ConvaiSkillSourceResolver.SkillFileName))
                throw new FileNotFoundException(
                    "The staged Convai skill payload is missing its required SKILL.md.",
                    Path.Combine(payloadRoot, ConvaiSkillSourceResolver.SkillFileName));
            return new ConvaiSkillStamp
            {
                SchemaVersion = ConvaiSkillStamp.CurrentSchemaVersion,
                SkillName = ConvaiSkillSourceResolver.SkillName,
                SkillVersion = ConvaiSkillPayload.ReadSkillVersion(payloadRoot),
                SdkVersion = source.SdkVersion,
                InstalledContentHash = ConvaiSkillPayload.ComputeAggregateHash(files),
                Files = files.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.Ordinal)
            };
        }

        internal static void WriteStamp(string mirrorRoot, ConvaiSkillStamp stamp)
        {
            Directory.CreateDirectory(mirrorRoot);
            File.WriteAllText(
                Path.Combine(mirrorRoot, ConvaiSkillPayload.StampFileName),
                JsonConvert.SerializeObject(stamp, Formatting.Indented) + "\n",
                new UTF8Encoding(false));
        }

        /// <summary>Names the first added, removed, or edited file rather than just reporting a mismatch.</summary>
        private static string DescribeDifference(
            IReadOnlyDictionary<string, string> stamped,
            IReadOnlyDictionary<string, string> actual)
        {
            string edited = stamped
                .Where(entry => actual.TryGetValue(entry.Key, out string hash) &&
                                !string.Equals(hash, entry.Value, StringComparison.Ordinal))
                .Select(entry => entry.Key)
                .OrderBy(path => path, StringComparer.Ordinal)
                .FirstOrDefault();
            if (edited != null) return $"'{edited}' was edited after installation.";

            string removed = stamped.Keys.Except(actual.Keys, StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            if (removed != null) return $"'{removed}' was deleted after installation.";

            string added = actual.Keys.Except(stamped.Keys, StringComparer.Ordinal)
                .OrderBy(path => path, StringComparer.Ordinal).FirstOrDefault();
            return added != null ? $"'{added}' was added after installation." : null;
        }

        /// <summary>Any SDK version change or canonical content change makes the mirror stale, in either direction.</summary>
        private static bool TryDescribeSourceChange(
            ConvaiSkillStamp stamp,
            ConvaiSkillSource source,
            out string detail)
        {
            detail = string.Empty;
            if (!source.IsValid) return false;

            if (!string.Equals(stamp.SdkVersion, source.SdkVersion, StringComparison.Ordinal))
            {
                detail = $"Installed Convai SDK is {source.SdkVersion}; the mirror was copied from {stamp.SdkVersion}.";
                return true;
            }

            string sourceHash = ConvaiSkillPayload.ComputeAggregateHash(
                ConvaiSkillPayload.ComputeFileHashes(source.SkillRoot));
            if (string.Equals(stamp.InstalledContentHash, sourceHash, StringComparison.Ordinal)) return false;

            detail = $"The packaged skill content changed since the mirror was copied from SDK {stamp.SdkVersion}.";
            return true;
        }

        private static string Describe(ConvaiSkillMirrorState state) => state switch
        {
            ConvaiSkillMirrorState.LocallyModified => "locally modified",
            ConvaiSkillMirrorState.Unmanaged => "not a Convai-managed installation",
            _ => state.ToString()
        };

        /// <summary>A leftover temp or backup folder is reported, never overwritten.</summary>
        private static void RequireAbsent(string path, string what)
        {
            if (!Directory.Exists(path) && !File.Exists(path)) return;
            throw new InvalidOperationException(
                $"A {what} folder already exists at '{ConvaiSkillSourceResolver.Normalize(path)}'. " +
                "Inspect and delete it before installing, so an interrupted install is not silently discarded.");
        }

        /// <summary>
        ///     Restores the intact backup under the real name. An initial install leaves no partial
        ///     mirror behind. Rollback failures are appended to the original error, not swallowed.
        /// </summary>
        private static string Rollback(string mirrorRoot, string temporaryRoot, string backupRoot, bool backedUp)
        {
            string detail = string.Empty;
            try
            {
                if (backedUp)
                {
                    if (Directory.Exists(mirrorRoot) && !Directory.Exists(temporaryRoot))
                        Directory.Move(mirrorRoot, temporaryRoot);
                    if (Directory.Exists(backupRoot) && !Directory.Exists(mirrorRoot))
                    {
                        Directory.Move(backupRoot, mirrorRoot);
                        detail = " The previous mirror was restored.";
                    }
                    else
                        detail = $" The previous mirror is at '{ConvaiSkillSourceResolver.Normalize(backupRoot)}' and was not restored.";
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                detail = $" Rollback also failed: {exception.Message}";
            }

            // Only this operation's staging folder is disposable. Before backup succeeds the
            // existing mirror still belongs to the user and must never be removed by rollback.
            try
            {
                if (Directory.Exists(temporaryRoot)) Directory.Delete(temporaryRoot, true);
                return detail + " No temporary payload was left behind.";
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return detail + $" Could not delete temporary payload '{ConvaiSkillSourceResolver.Normalize(temporaryRoot)}': " +
                       $"{exception.Message} Delete that folder before retrying.";
            }
        }

        private static string RequireProjectRoot(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
                throw new ArgumentException("Project root is required.", nameof(projectRoot));
            return projectRoot;
        }
    }
}
