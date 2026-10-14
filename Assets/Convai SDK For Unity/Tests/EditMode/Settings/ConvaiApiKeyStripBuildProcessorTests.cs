using Convai.Editor.Build;
using Convai.Runtime;
using Convai.Runtime.Core.Configuration;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Settings
{
    [TestFixture]
    public sealed class ConvaiApiKeyStripBuildProcessorTests
    {
        private string _assetPath;
        private ConvaiSettings _settings;

        [SetUp]
        public void SetUp()
        {
            Assert.That(
                ConvaiApiKeyStripBuildProcessor.RestorePendingBackup("before the build-strip test"),
                Is.True,
                "A stale credential backup must be recoverable before this test can run safely.");

            _assetPath = AssetDatabase.GenerateUniqueAssetPath(TestAssetSandbox.Path(
                nameof(ConvaiApiKeyStripBuildProcessorTests), "Settings.asset"));
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
            AssetDatabase.CreateAsset(_settings, _assetPath);
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiApiKeyStripBuildProcessor.RestorePendingBackup("after the build-strip test");
            if (!string.IsNullOrEmpty(_assetPath))
            {
                AssetDatabase.DeleteAsset(_assetPath);
                TestAssetSandbox.Clear(nameof(ConvaiApiKeyStripBuildProcessorTests));
            }
        }

        [Test]
        public void AuthTokenMode_PostprocessRestore_RoundTripsBothSerializedKeyFields()
        {
            SetCredentials(ConvaiAuthMode.AuthToken, "legacy-key", "obfuscated-key");
            var processor = new ConvaiApiKeyStripBuildProcessor();

            ConvaiApiKeyStripBuildProcessor.StripCredentialsForBuild(_settings);

            AssertCredentials(string.Empty, string.Empty);
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.True);

            processor.OnPostprocessBuild(null);

            AssertCredentials("legacy-key", "obfuscated-key");
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.False);
        }

        [Test]
        public void AuthTokenMode_InterruptedBuildRecovery_RestoresPendingBackup()
        {
            SetCredentials(ConvaiAuthMode.AuthToken, "legacy-key", "obfuscated-key");
            ConvaiApiKeyStripBuildProcessor.StripCredentialsForBuild(_settings);
            AssertCredentials(string.Empty, string.Empty);

            ConvaiApiKeyStripBuildProcessor.RestoreAfterBuildIfNeeded();

            AssertCredentials("legacy-key", "obfuscated-key");
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.False);
        }

        [Test]
        public void AuthTokenMode_EditorCrashRecovery_RestoresPersistentBackup()
        {
            SetCredentials(ConvaiAuthMode.AuthToken, "legacy-key", "obfuscated-key");
            ConvaiApiKeyStripBuildProcessor.StripCredentialsForBuild(_settings);
            AssertCredentials(string.Empty, string.Empty);

            ConvaiApiKeyStripBuildProcessor.ClearSessionStateBackupForTests();
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.True,
                "The Library-backed safety copy must survive loss of Editor SessionState.");

            Assert.That(
                ConvaiApiKeyStripBuildProcessor.RestorePendingBackup("after a simulated Editor crash"),
                Is.True);

            AssertCredentials("legacy-key", "obfuscated-key");
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.False);
        }

        [Test]
        public void ApiKeyMode_DoesNotModifyStoredCredentials()
        {
            SetCredentials(ConvaiAuthMode.ApiKey, "legacy-key", "obfuscated-key");

            ConvaiApiKeyStripBuildProcessor.StripCredentialsForBuild(_settings);

            AssertCredentials("legacy-key", "obfuscated-key");
            Assert.That(ConvaiApiKeyStripBuildProcessor.HasPendingBackup, Is.False);
        }

        private void SetCredentials(
            ConvaiAuthMode authMode,
            string legacyKey,
            string obfuscatedKey)
        {
            var serializedSettings = new SerializedObject(_settings);
            serializedSettings.FindProperty("_authMode").enumValueIndex = (int)authMode;
            serializedSettings.FindProperty("_apiKey").stringValue = legacyKey;
            serializedSettings.FindProperty("_apiKeyObfuscated").stringValue = obfuscatedKey;
            serializedSettings.ApplyModifiedPropertiesWithoutUndo();
            EditorUtility.SetDirty(_settings);
            AssetDatabase.SaveAssetIfDirty(_settings);
        }

        private void AssertCredentials(string expectedLegacyKey, string expectedObfuscatedKey)
        {
            var serializedSettings = new SerializedObject(_settings);
            serializedSettings.Update();
            Assert.That(
                serializedSettings.FindProperty("_apiKey").stringValue,
                Is.EqualTo(expectedLegacyKey));
            Assert.That(
                serializedSettings.FindProperty("_apiKeyObfuscated").stringValue,
                Is.EqualTo(expectedObfuscatedKey));
        }
    }
}
