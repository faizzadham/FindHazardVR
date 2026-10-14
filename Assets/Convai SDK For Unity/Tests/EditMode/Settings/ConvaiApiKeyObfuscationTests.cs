using Convai.Runtime;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Settings
{
    public class ConvaiApiKeyObfuscationTests
    {
        [TestCase("simple-key")]
        [TestCase("a")]
        [TestCase("key with spaces and $ymbols !@#")]
        [TestCase("ключ-ユニコード-🔑")]
        public void Obfuscate_RoundTrips(string plain)
        {
            string payload = ConvaiApiKeyObfuscation.Obfuscate(plain);

            Assert.IsTrue(payload.StartsWith(ConvaiApiKeyObfuscation.Prefix));
            Assert.AreNotEqual(plain, payload);
            Assert.IsTrue(ConvaiApiKeyObfuscation.TryDeobfuscate(payload, out string decoded));
            Assert.AreEqual(plain, decoded);
        }

        [Test]
        public void Obfuscate_EmptyOrNull_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, ConvaiApiKeyObfuscation.Obfuscate(null));
            Assert.AreEqual(string.Empty, ConvaiApiKeyObfuscation.Obfuscate(string.Empty));
        }

        [Test]
        public void Obfuscate_DoesNotContainPlaintext()
        {
            const string plain = "convai-secret-api-key-1234567890";
            string payload = ConvaiApiKeyObfuscation.Obfuscate(plain);
            StringAssert.DoesNotContain(plain, payload);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("plaintext-key-without-prefix")]
        [TestCase("cnv1:not-valid-base64!!!")]
        public void TryDeobfuscate_RejectsInvalidPayloads(string payload)
        {
            Assert.IsFalse(ConvaiApiKeyObfuscation.TryDeobfuscate(payload, out string decoded));
            Assert.AreEqual(string.Empty, decoded);
        }
    }
}
