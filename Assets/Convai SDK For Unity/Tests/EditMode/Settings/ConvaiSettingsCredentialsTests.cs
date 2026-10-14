using System;
using System.Reflection;
using Convai.Application;
using Convai.Editor.Settings;
using Convai.Editor.Settings.Services;
using Convai.Runtime;
using Convai.Runtime.Core.Configuration;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Settings
{
    public class ConvaiSettingsCredentialsTests
    {
        private ConvaiSettings _settings;

        [SetUp]
        public void SetUp()
        {
            ConvaiAuthTokenProviderRegistry.Clear();
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
        }

        [TearDown]
        public void TearDown()
        {
            ConvaiAuthTokenProviderRegistry.Clear();
            UnityEngine.Object.DestroyImmediate(_settings);
        }

        [Test]
        public void SetApiKey_StoresObfuscated_AndApiKeyReturnsPlain()
        {
            _settings.SetApiKey("my-api-key");

            Assert.AreEqual("my-api-key", _settings.ApiKey);
            Assert.IsTrue(_settings.HasApiKey);

            var obfuscated = (string)GetField("_apiKeyObfuscated");
            StringAssert.StartsWith(ConvaiApiKeyObfuscation.Prefix, obfuscated);
            StringAssert.DoesNotContain("my-api-key", obfuscated);
            Assert.AreEqual(string.Empty, (string)GetField("_apiKey"));
        }

        [Test]
        public void SetApiKey_TrimsInput_AndEmptyClears()
        {
            _settings.SetApiKey("  padded-key  ");
            Assert.AreEqual("padded-key", _settings.ApiKey);

            _settings.SetApiKey(string.Empty);
            Assert.IsFalse(_settings.HasApiKey);
            Assert.AreEqual(string.Empty, _settings.ApiKey);
        }

        [Test]
        public void MigrateLegacyDataIfNeeded_ObfuscatesPlaintextKey_AndClearsLegacyField()
        {
            SetField("_apiKey", "legacy-plaintext-key");

            _settings.MigrateLegacyDataIfNeeded();

            Assert.AreEqual("legacy-plaintext-key", _settings.ApiKey);
            Assert.AreEqual(string.Empty, (string)GetField("_apiKey"));
            StringAssert.StartsWith(ConvaiApiKeyObfuscation.Prefix, (string)GetField("_apiKeyObfuscated"));
        }

        [Test]
        public void MigrateLegacyDataIfNeeded_IsIdempotent()
        {
            SetField("_apiKey", "legacy-key");
            _settings.MigrateLegacyDataIfNeeded();
            string firstPayload = (string)GetField("_apiKeyObfuscated");

            _settings.MigrateLegacyDataIfNeeded();

            Assert.AreEqual(firstPayload, (string)GetField("_apiKeyObfuscated"));
            Assert.AreEqual("legacy-key", _settings.ApiKey);
        }

        [Test]
        public void MigrateLegacyDataIfNeeded_ClearsPlaintext_WhenObfuscatedValueAlreadyExists()
        {
            SetField("_apiKey", "stale-plaintext-key");
            SetField("_apiKeyObfuscated", ConvaiApiKeyObfuscation.Obfuscate("current-key"));

            _settings.MigrateLegacyDataIfNeeded();

            Assert.AreEqual("current-key", _settings.ApiKey);
            Assert.AreEqual(string.Empty, (string)GetField("_apiKey"));
        }

        [Test]
        public void ApiKey_FallsBackToLegacyPlaintext_WhenNotMigrated()
        {
            SetField("_apiKey", "unmigrated-key");
            Assert.AreEqual("unmigrated-key", _settings.ApiKey);
            Assert.IsTrue(_settings.HasApiKey);
        }

        [Test]
        public void AuthenticationDefaults_PreserveExistingApiKeyBehavior()
        {
            Assert.That(_settings.AuthMode, Is.EqualTo(ConvaiAuthMode.ApiKey));
            Assert.That(_settings.ApiMode, Is.EqualTo(ConvaiApiMode.Legacy));
            Assert.That(_settings.AuthTokenEndpointUrl, Is.Empty);
            Assert.That(_settings.AuthTokenHttpMethod, Is.EqualTo(ConvaiAuthTokenHttpMethod.Get));
            Assert.That(_settings.AuthTokenResponseField, Is.EqualTo("apiAuthToken"));
            Assert.That(_settings.AuthTokenHeaders, Is.Empty);
            Assert.That(_settings.HasValidAuthConfig, Is.False);

            _settings.SetApiKey("api-key");

            Assert.That(_settings.HasValidAuthConfig, Is.True);
        }

        [Test]
        public void AuthTokenMode_ValidAbsoluteEndpoint_IsValidWithoutResolvedToken()
        {
            SetField("_authMode", ConvaiAuthMode.AuthToken);
            SetField("_authTokenEndpointUrl", "  https://auth.example.com/convai-token  ");

            Assert.That(_settings.AuthTokenEndpointUrl,
                Is.EqualTo("https://auth.example.com/convai-token"));
            Assert.That(_settings.HasValidAuthConfig, Is.True);
            Assert.That(_settings.TryGetAuthTokenEndpointUri(out Uri endpoint), Is.True);
            Assert.That(endpoint.AbsoluteUri, Is.EqualTo("https://auth.example.com/convai-token"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("relative/token")]
        [TestCase("ftp://auth.example.com/token")]
        [TestCase("http://auth.example.com/token")]
        [TestCase("http://192.168.1.20/token")]
        [TestCase("https://")]
        public void AuthTokenMode_InvalidEndpoint_IsNotValid(string endpointUrl)
        {
            SetField("_authMode", ConvaiAuthMode.AuthToken);
            SetField("_authTokenEndpointUrl", endpointUrl);

            Assert.That(_settings.HasValidAuthConfig, Is.False);
            Assert.That(_settings.TryGetAuthTokenEndpointUri(out _), Is.False);
        }

        [TestCase("http://127.0.0.1:8787/v1/convai/token")]
        [TestCase("http://localhost:8787/v1/convai/token")]
        public void AuthTokenMode_HttpLoopbackEndpoint_IsValidForLocalDevelopment(string endpointUrl)
        {
            SetField("_authMode", ConvaiAuthMode.AuthToken);
            SetField("_authTokenEndpointUrl", endpointUrl);

            Assert.That(_settings.HasValidAuthConfig, Is.True);
            Assert.That(_settings.TryGetAuthTokenEndpointUri(out Uri endpoint), Is.True);
            Assert.That(endpoint.IsLoopback, Is.True);
        }

        [Test]
        public void AuthTokenMode_RegisteredProvider_IsValidWithoutEndpoint()
        {
            SetField("_authMode", ConvaiAuthMode.AuthToken);
            SetField("_authTokenEndpointUrl", string.Empty);
            var provider = new DelegateAuthTokenProvider(_ =>
                System.Threading.Tasks.Task.FromResult("token"));
            ConvaiAuthTokenProviderRegistry.Register(provider);

            Assert.That(_settings.HasValidAuthConfig, Is.True);
        }

        [Test]
        public void SwitchingToAuthTokenMode_DoesNotDeleteStoredApiKey()
        {
            _settings.SetApiKey("editor-api-key");
            SetField("_authMode", ConvaiAuthMode.AuthToken);
            SetField("_authTokenEndpointUrl", "https://auth.example.com/convai-token");

            Assert.That(_settings.AuthMode, Is.EqualTo(ConvaiAuthMode.AuthToken));
            Assert.That(_settings.HasValidAuthConfig, Is.True);
            Assert.That(_settings.HasApiKey, Is.True);
            Assert.That(_settings.ApiKey, Is.EqualTo("editor-api-key"));
        }

        [Test]
        public void AuthTokenResponseField_BlankFallsBackAndDottedPathIsTrimmed()
        {
            SetField("_authTokenResponseField", "   ");
            Assert.That(_settings.AuthTokenResponseField, Is.EqualTo("apiAuthToken"));

            SetField("_authTokenResponseField", "  payload.credential  ");
            Assert.That(_settings.AuthTokenResponseField, Is.EqualTo("payload.credential"));
        }

        [TestCase(ConvaiApiEnvironment.Production)]
        [TestCase(ConvaiApiEnvironment.Beta)]
        public void ServerUrl_IgnoresSerializedUrl_ForManagedEnvironments(ConvaiApiEnvironment environment)
        {
            SetField("_apiEnvironment", environment);
            SetField("_serverUrl", "https://custom.example.com");

            Assert.AreEqual(ConvaiSettings.DefaultCoreServerUrl, _settings.ServerUrl);
            Assert.AreEqual(string.Empty, _settings.RestBaseUrlOverride);
        }

        [Test]
        public void ServerUrl_HonorsSerializedUrl_OnlyWhenCustom()
        {
            SetField("_apiEnvironment", ConvaiApiEnvironment.Custom);
            SetField("_serverUrl", "https://custom.example.com");
            SetField("_customRestBaseUrl", "https://rest.example.com/");

            Assert.AreEqual("https://custom.example.com", _settings.ServerUrl);
            Assert.AreEqual("https://rest.example.com/", _settings.RestBaseUrlOverride);
        }

        [Test]
        public void ServerUrl_Custom_WithEmptyUrl_FallsBackToDefault()
        {
            SetField("_apiEnvironment", ConvaiApiEnvironment.Custom);
            SetField("_serverUrl", "   ");

            Assert.AreEqual(ConvaiSettings.DefaultCoreServerUrl, _settings.ServerUrl);
        }

        [Test]
        public void CredentialsChanged_PropagatesAcrossMountedHosts()
        {
            using var projectSettings = new ConvaiSettingsViewContext(
                new SerializedObject(_settings), ConvaiSettingsHostKind.ProjectSettings);
            using var configurationWindow = new ConvaiSettingsViewContext(
                new SerializedObject(_settings), ConvaiSettingsHostKind.ConfigurationWindow);
            int projectNotifications = 0;
            int windowNotifications = 0;
            projectSettings.CredentialsChanged += () => projectNotifications++;
            configurationWindow.CredentialsChanged += () => windowNotifications++;

            projectSettings.NotifyCredentialsChanged();

            Assert.AreEqual(1, projectNotifications);
            Assert.AreEqual(1, windowNotifications);
        }

        private object GetField(string name) => Field(name).GetValue(_settings);

        private void SetField(string name, object value) => Field(name).SetValue(_settings, value);

        private static FieldInfo Field(string name) =>
            typeof(ConvaiSettings).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
    }

    public class ApiKeyValidationCacheTests
    {
        private const string TestApiKey = "validation-cache-test-key";
        private const string CustomRestBaseUrl = "";
        private string _cacheKey;

        [SetUp]
        public void SetUp()
        {
            _cacheKey = ApiKeyValidationService.CacheKey(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl);
            ClearCachedResult();
        }

        [TearDown]
        public void TearDown() => ClearCachedResult();

        [Test]
        public void CachedValidation_SurvivesEditorSessionReset()
        {
            ApiKeyValidationService.StoreResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                new ApiKeyValidationResult(true, string.Empty));

            // Unity clears SessionState when the Editor process exits.
            SessionState.EraseString(_cacheKey);

            bool found = ApiKeyValidationService.TryGetCachedResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                out ApiKeyValidationResult result);

            Assert.That(found, Is.True, "Validation should survive closing and reopening the Unity project.");
            Assert.That(result.IsValid, Is.True);
        }

        [Test]
        public void CachedValidation_ExpiresAfterCacheLifetime()
        {
            var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            ApiKeyValidationService.StoreResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                new ApiKeyValidationResult(true, string.Empty),
                now - ApiKeyValidationService.CacheLifetime - TimeSpan.FromSeconds(1));

            bool found = ApiKeyValidationService.TryGetCachedResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                now,
                out _);

            Assert.That(found, Is.False);
            Assert.That(EditorPrefs.HasKey(_cacheKey), Is.False, "Expired cache entries should be removed.");
        }

        [Test]
        public void CachedValidation_FromDifferentSdkVersion_IsIgnored()
        {
            var now = new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);
            EditorPrefs.SetString(
                _cacheKey,
                $"{ApiKeyValidationService.CacheFormatVersion}|{ConvaiSDK.Version.Major + 1}.0.0|" +
                $"{now.ToUnixTimeSeconds()}|valid:");

            bool found = ApiKeyValidationService.TryGetCachedResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                now,
                out _);

            Assert.That(found, Is.False);
            Assert.That(EditorPrefs.HasKey(_cacheKey), Is.False, "SDK-version cache mismatches should be removed.");
        }

        [Test]
        public void LegacyTimelessCachedValidation_IsIgnored()
        {
            EditorPrefs.SetString(_cacheKey, "valid:");

            bool found = ApiKeyValidationService.TryGetCachedResult(
                TestApiKey,
                ConvaiApiEnvironment.Production,
                CustomRestBaseUrl,
                out _);

            Assert.That(found, Is.False);
            Assert.That(EditorPrefs.HasKey(_cacheKey), Is.False, "Timeless cache entries should be removed.");
        }

        private void ClearCachedResult()
        {
            if (string.IsNullOrEmpty(_cacheKey)) return;

            SessionState.EraseString(_cacheKey);
            EditorPrefs.DeleteKey(_cacheKey);
        }
    }
}
