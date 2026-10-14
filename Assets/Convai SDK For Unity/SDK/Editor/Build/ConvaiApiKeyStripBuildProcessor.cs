using System;
using System.IO;
using Convai.Editor.Ownership;
using Convai.Runtime;
using Convai.Runtime.Core.Configuration;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace Convai.Editor.Build
{
    /// <summary>
    ///     Removes locally stored account credentials while an Auth Token player build is
    ///     produced, then restores the Editor settings asset afterwards.
    /// </summary>
    /// <remarks>
    ///     The backup is written under the project's Library folder before the settings asset is stripped, so it
    ///     survives an Editor or machine crash without entering source control. SessionState mirrors the backup for
    ///     ordinary domain reloads. Update and quitting hooks cover build failures and cancellations.
    /// </remarks>
    [InitializeOnLoad]
    public sealed class ConvaiApiKeyStripBuildProcessor :
        IPreprocessBuildWithReport,
        IPostprocessBuildWithReport
    {
        private const int CallbackOrder = -10000;
        private const string BackupPendingKey = "Convai.AuthTokenBuild.ApiKeyBackup.Pending";
        private const string BackupAssetPathKey = "Convai.AuthTokenBuild.ApiKeyBackup.AssetPath";
        private const string BackupLegacyKey = "Convai.AuthTokenBuild.ApiKeyBackup.Legacy";
        private const string BackupObfuscatedKey = "Convai.AuthTokenBuild.ApiKeyBackup.Obfuscated";
        private const string PersistentBackupDirectoryName = "Convai";
        private const string PersistentBackupFileName = "AuthTokenBuildCredentialBackup.json";
        private const string LegacyPropertyName = "_apiKey";
        private const string ObfuscatedPropertyName = "_apiKeyObfuscated";

        private static bool _restoreFailureLogged;
        private static bool _persistentBackupPending = File.Exists(PersistentBackupPath);

        static ConvaiApiKeyStripBuildProcessor()
        {
            EditorApplication.update -= RestoreAfterBuildIfNeeded;
            EditorApplication.update += RestoreAfterBuildIfNeeded;
            EditorApplication.quitting -= RestoreBeforeEditorQuits;
            EditorApplication.quitting += RestoreBeforeEditorQuits;
        }

        /// <inheritdoc />
        public int callbackOrder => CallbackOrder;

        /// <inheritdoc />
        public void OnPreprocessBuild(BuildReport report)
        {
            if (!RestorePendingBackup("before starting a new build"))
            {
                throw new BuildFailedException(
                    "Convai could not restore credentials left by an earlier interrupted build. " +
                    "The new build was stopped to avoid losing the saved API key.");
            }

            ConvaiSettings settings = ConvaiSettings.Instance;
            StripCredentialsForBuild(settings);
        }

        internal static void StripCredentialsForBuild(ConvaiSettings settings)
        {
            if (settings == null || settings.AuthMode != ConvaiAuthMode.AuthToken)
                return;

            string assetPath = AssetDatabase.GetAssetPath(settings);
            if (string.IsNullOrWhiteSpace(assetPath))
            {
                throw new BuildFailedException(
                    "Convai Auth Token mode requires a saved ConvaiSettings asset before building.");
            }

            if (!ConvaiAssetOwnership.IsProjectAsset(settings))
            {
                throw new BuildFailedException(
                    "Convai Auth Token mode requires the ConvaiSettings asset to live in this project, " +
                    "not inside a package — stripping the key from a package asset would not be saved " +
                    "the way the build depends on.");
            }

            var serializedSettings = new SerializedObject(settings);
            serializedSettings.Update();
            SerializedProperty legacyKey = serializedSettings.FindProperty(LegacyPropertyName);
            SerializedProperty obfuscatedKey = serializedSettings.FindProperty(ObfuscatedPropertyName);
            if (legacyKey == null || obfuscatedKey == null)
            {
                throw new BuildFailedException(
                    "Convai could not locate the serialized API-key fields. The build was stopped because " +
                    "Auth Token mode cannot guarantee that the account key is absent.");
            }

            string legacyValue = legacyKey.stringValue ?? string.Empty;
            string obfuscatedValue = obfuscatedKey.stringValue ?? string.Empty;
            if (legacyValue.Length == 0 && obfuscatedValue.Length == 0)
                return;

            StoreBackup(assetPath, legacyValue, obfuscatedValue);

            try
            {
                legacyKey.stringValue = string.Empty;
                obfuscatedKey.stringValue = string.Empty;
                serializedSettings.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                Debug.Log(
                    "[Convai] Temporarily removed the stored API key for this Auth Token player build. " +
                    "It will be restored in Project Settings when the build finishes.");
            }
            catch (Exception exception)
            {
                RestorePendingBackup("after API-key stripping failed");
                throw new BuildFailedException(
                    $"Convai failed to remove the stored API key before building: {exception.Message}");
            }
        }

        /// <inheritdoc />
        public void OnPostprocessBuild(BuildReport report) =>
            RestorePendingBackup("after the player build");

        internal static void RestoreAfterBuildIfNeeded()
        {
            if (!HasPendingBackup || BuildPipeline.isBuildingPlayer)
                return;

            RestorePendingBackup("after an interrupted or cancelled player build");
        }

        private static void RestoreBeforeEditorQuits()
        {
            if (HasPendingBackup)
                RestorePendingBackup("before the Editor quits");
        }

        internal static bool HasPendingBackup =>
            SessionState.GetBool(BackupPendingKey, false) || _persistentBackupPending;

        private static void StoreBackup(string assetPath, string legacyValue, string obfuscatedValue)
        {
            var backup = new CredentialBackup
            {
                AssetPath = assetPath,
                LegacyValue = legacyValue,
                ObfuscatedValue = obfuscatedValue
            };
            PersistBackup(backup);

            SessionState.SetString(BackupAssetPathKey, assetPath);
            SessionState.SetString(BackupLegacyKey, legacyValue);
            SessionState.SetString(BackupObfuscatedKey, obfuscatedValue);
            SessionState.SetBool(BackupPendingKey, true);
            _restoreFailureLogged = false;
        }

        internal static bool RestorePendingBackup(string reason)
        {
            if (!HasPendingBackup)
                return true;

            if (!TryLoadBackup(out CredentialBackup backup, out string loadError))
            {
                LogRestoreFailureOnce($"Convai could not restore the API key {reason}: {loadError}");
                return false;
            }

            string assetPath = backup.AssetPath;
            ConvaiSettings settings = string.IsNullOrWhiteSpace(assetPath)
                ? null
                : AssetDatabase.LoadAssetAtPath<ConvaiSettings>(assetPath);

            if (settings == null)
            {
                LogRestoreFailureOnce(
                    $"Convai could not restore the API key {reason}: the settings asset at '{assetPath}' was not found.");
                return false;
            }

            try
            {
                var serializedSettings = new SerializedObject(settings);
                serializedSettings.Update();
                SerializedProperty legacyKey = serializedSettings.FindProperty(LegacyPropertyName);
                SerializedProperty obfuscatedKey = serializedSettings.FindProperty(ObfuscatedPropertyName);
                if (legacyKey == null || obfuscatedKey == null)
                {
                    LogRestoreFailureOnce(
                        $"Convai could not restore the API key {reason}: the serialized key fields were not found.");
                    return false;
                }

                legacyKey.stringValue = backup.LegacyValue ?? string.Empty;
                obfuscatedKey.stringValue = backup.ObfuscatedValue ?? string.Empty;
                serializedSettings.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssetIfDirty(settings);
                ClearBackup();
                Debug.Log($"[Convai] Restored the Project Settings API key {reason}.");
                return true;
            }
            catch (Exception exception)
            {
                LogRestoreFailureOnce($"Convai could not restore the API key {reason}: {exception.Message}");
                return false;
            }
        }

        private static void ClearBackup()
        {
            if (File.Exists(PersistentBackupPath))
                File.Delete(PersistentBackupPath);
            _persistentBackupPending = false;

            SessionState.EraseBool(BackupPendingKey);
            SessionState.EraseString(BackupAssetPathKey);
            SessionState.EraseString(BackupLegacyKey);
            SessionState.EraseString(BackupObfuscatedKey);
            _restoreFailureLogged = false;
        }

        internal static void ClearSessionStateBackupForTests()
        {
            SessionState.EraseBool(BackupPendingKey);
            SessionState.EraseString(BackupAssetPathKey);
            SessionState.EraseString(BackupLegacyKey);
            SessionState.EraseString(BackupObfuscatedKey);
        }

        private static string PersistentBackupPath
        {
            get
            {
                string projectRoot = Path.GetFullPath(Path.Combine(UnityEngine.Application.dataPath, ".."));
                return Path.Combine(
                    projectRoot,
                    "Library",
                    PersistentBackupDirectoryName,
                    PersistentBackupFileName);
            }
        }

        private static void PersistBackup(CredentialBackup backup)
        {
            string backupPath = PersistentBackupPath;
            string temporaryPath = backupPath + ".tmp";
            try
            {
                string directory = Path.GetDirectoryName(backupPath);
                if (!string.IsNullOrEmpty(directory))
                    Directory.CreateDirectory(directory);

                File.WriteAllText(temporaryPath, JsonUtility.ToJson(backup));
                if (File.Exists(backupPath))
                    File.Delete(backupPath);
                File.Move(temporaryPath, backupPath);
                _persistentBackupPending = true;
            }
            catch (Exception exception)
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);

                throw new BuildFailedException(
                    $"Convai could not persist the API-key safety backup before building: {exception.Message}");
            }
        }

        private static bool TryLoadBackup(out CredentialBackup backup, out string error)
        {
            backup = null;
            error = string.Empty;

            if (File.Exists(PersistentBackupPath))
            {
                try
                {
                    backup = JsonUtility.FromJson<CredentialBackup>(
                        File.ReadAllText(PersistentBackupPath));
                }
                catch (Exception exception)
                {
                    error = $"the persistent backup could not be read ({exception.GetType().Name}).";
                    return false;
                }

                if (backup == null || string.IsNullOrWhiteSpace(backup.AssetPath))
                {
                    error = "the persistent backup was invalid.";
                    return false;
                }

                return true;
            }

            if (!SessionState.GetBool(BackupPendingKey, false))
            {
                error = "no pending backup was found.";
                return false;
            }

            backup = new CredentialBackup
            {
                AssetPath = SessionState.GetString(BackupAssetPathKey, string.Empty),
                LegacyValue = SessionState.GetString(BackupLegacyKey, string.Empty),
                ObfuscatedValue = SessionState.GetString(BackupObfuscatedKey, string.Empty)
            };
            if (string.IsNullOrWhiteSpace(backup.AssetPath))
            {
                error = "the session backup was invalid.";
                return false;
            }

            return true;
        }

        private static void LogRestoreFailureOnce(string message)
        {
            if (_restoreFailureLogged)
                return;

            _restoreFailureLogged = true;
            Debug.LogError(message);
        }

        [Serializable]
        private sealed class CredentialBackup
        {
            public string AssetPath;
            public string LegacyValue;
            public string ObfuscatedValue;
        }
    }
}
