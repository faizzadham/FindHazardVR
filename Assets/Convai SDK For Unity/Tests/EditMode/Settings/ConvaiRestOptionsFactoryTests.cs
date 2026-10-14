using System.Reflection;
using Convai.RestAPI;
using Convai.Runtime;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Settings
{
    public class ConvaiRestOptionsFactoryTests
    {
        private const string DefaultProductionBaseUrl = "https://api.convai.com/";

        [Test]
        public void Create_Production_UsesProductionEnvironment()
        {
            ConvaiRestClientOptions options =
                ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Production, null, ConvaiApiMode.Upgraded);

            Assert.AreEqual("key", options.ApiKey);
            Assert.AreEqual(ConvaiEnvironment.Production, options.Environment);
            Assert.AreEqual(DefaultProductionBaseUrl, options.ProductionBaseUrl);
            Assert.That(options.CharacterApiV2, Is.Not.Null);
            Assert.That(options.CharacterApiV2.BaseUri.ToString(), Is.EqualTo("https://api2.convai.com/"));
        }

        [Test]
        public void Create_LegacyApiMode_PreservesLegacyRouting()
        {
            ConvaiRestClientOptions options = ConvaiRestOptionsFactory.Create(
                "key",
                ConvaiApiEnvironment.Production,
                null,
                ConvaiApiMode.Legacy);

            Assert.That(options.CharacterApiV2, Is.Null);
            Assert.That(options.ProductionBaseUrl, Is.EqualTo(DefaultProductionBaseUrl));
        }

        [Test]
        public void Create_Beta_UsesBetaEnvironment()
        {
            ConvaiRestClientOptions options =
                ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Beta, null, ConvaiApiMode.Upgraded);

            Assert.AreEqual(ConvaiEnvironment.Beta, options.Environment);
            Assert.That(options.CharacterApiV2.BaseUri.ToString(), Is.EqualTo("https://api2-stg.convai.com/"));
        }

        [Test]
        public void Create_Custom_OverridesProductionBaseUrl()
        {
            ConvaiRestClientOptions options =
                ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Custom, " https://rest.example.com/ ");

            Assert.AreEqual(ConvaiEnvironment.Production, options.Environment);
            Assert.AreEqual("https://rest.example.com/", options.ProductionBaseUrl);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("   ")]
        public void Create_Custom_WithEmptyOverride_KeepsProductionBaseUrl(string overrideUrl)
        {
            ConvaiRestClientOptions options =
                ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Custom, overrideUrl);

            Assert.AreEqual(DefaultProductionBaseUrl, options.ProductionBaseUrl);
        }

        [TestCase(false, ConvaiAuthenticationMode.ApiKey)]
        [TestCase(true, ConvaiAuthenticationMode.AuthToken)]
        public void CreateForRuntimeCredential_SelectsAuthenticationMode(
            bool usesAuthToken,
            ConvaiAuthenticationMode expectedMode)
        {
            ConvaiRestClientOptions options =
                ConvaiRestOptionsFactory.CreateForRuntimeCredential("credential", usesAuthToken);

            Assert.AreEqual("credential", options.ApiKey);
            Assert.AreEqual(expectedMode, options.AuthenticationMode);
        }

        [Test]
        public void CreateForRuntimeCredential_AuthTokenKeepsMigratedReadsOnCompatibleRoutes()
        {
            var settings = UnityEngine.ScriptableObject.CreateInstance<ConvaiSettings>();
            try
            {
                typeof(ConvaiSettings)
                    .GetField("_apiMode", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(settings, ConvaiApiMode.Upgraded);

                ConvaiRestClientOptions options =
                    ConvaiRestOptionsFactory.CreateForRuntimeCredential("credential", true, settings);

                Assert.AreEqual(ConvaiAuthenticationMode.AuthToken, options.AuthenticationMode);
                Assert.IsNull(options.CharacterApiV2);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }

        [Test]
        public void Create_DefaultStaysLegacyUntilQualifiedAndCustomCharacterHostIsIndependent()
        {
            Assert.That(ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Production, null).CharacterApiV2, Is.Null);
            var options = ConvaiRestOptionsFactory.Create("key", ConvaiApiEnvironment.Custom,
                "https://legacy.example/", ConvaiApiMode.Upgraded, "https://character.example/api", "workspace");
            Assert.That(options.ProductionBaseUrl, Is.EqualTo("https://legacy.example/"));
            Assert.That(options.CharacterApiV2.BaseUri.AbsoluteUri, Is.EqualTo("https://character.example/api/"));
            Assert.That(options.CharacterApiV2.WorkspaceId, Is.EqualTo("workspace"));
        }

        [Test]
        public void CreateForRuntimeCredential_UsesProjectConnectionTimeout()
        {
            var settings = UnityEngine.ScriptableObject.CreateInstance<ConvaiSettings>();
            try
            {
                typeof(ConvaiSettings)
                    .GetField("_connectionTimeout", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(settings, 47f);
                typeof(ConvaiSettings)
                    .GetField("_apiMode", BindingFlags.Instance | BindingFlags.NonPublic)
                    ?.SetValue(settings, ConvaiApiMode.Upgraded);

                ConvaiRestClientOptions options =
                    ConvaiRestOptionsFactory.CreateForRuntimeCredential("credential", false, settings);

                Assert.AreEqual(47d, options.DefaultTimeout.TotalSeconds);
                Assert.AreEqual(47d, options.CharacterApiV2.Timeout.TotalSeconds);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(settings);
            }
        }
    }
}
