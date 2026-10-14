using Convai.Modules.LipSync.Editor;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public sealed class ConvaiLipSyncMapImportParserTests
    {
        [Test]
        public void TryParse_CanonicalVersionedJson_RoundTripsFields()
        {
            const string json = "{\"version\":1,\"targetProfileId\":\"metahuman\",\"mappings\":[{" +
                                "\"sourceBlendshape\":\"jawOpen\",\"targetNames\":[\"Jaw\"]," +
                                "\"multiplier\":0.75,\"enabled\":true}]}";

            bool parsed = ConvaiLipSyncMapImportParser.TryParse(json, out var data, out string error);

            Assert.IsTrue(parsed, error);
            Assert.AreEqual("metahuman", data.TargetProfileId);
            Assert.AreEqual(1, data.Entries.Count);
            Assert.AreEqual("jawOpen", data.Entries[0].SourceBlendshape);
            Assert.AreEqual("Jaw", data.Entries[0].TargetNames[0]);
            Assert.AreEqual(0.75f, data.Entries[0].Multiplier, 1e-6f);
        }

        [TestCase("jawOpen -> Jaw")]
        [TestCase("(jawOpen, TargetNames=(Jaw))")]
        [TestCase("{\"version\":1,\"jawOpen\":\"Jaw\"}")]
        [TestCase("[{\"sourceBlendshape\":\"jawOpen\",\"targetNames\":[\"Jaw\"]}]")]
        public void TryParse_UndocumentedFormats_AreRejected(string text)
        {
            Assert.IsFalse(ConvaiLipSyncMapImportParser.TryParse(text, out _, out string error));
            Assert.IsNotEmpty(error);
        }

        [Test]
        public void TryParse_MissingOrUnknownVersion_IsRejected()
        {
            const string missing = "{\"mappings\":[{\"sourceBlendshape\":\"jawOpen\"}]}";
            const string future = "{\"version\":2,\"mappings\":[{\"sourceBlendshape\":\"jawOpen\"}]}";

            Assert.IsFalse(ConvaiLipSyncMapImportParser.TryParse(missing, out _, out _));
            Assert.IsFalse(ConvaiLipSyncMapImportParser.TryParse(future, out _, out _));
        }
    }
}
