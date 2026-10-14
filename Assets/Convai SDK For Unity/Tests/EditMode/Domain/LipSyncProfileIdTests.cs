using Convai.Domain.Models.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    public class LipSyncProfileIdTests
    {
        [Test]
        public void Normalize_UsesLowerInvariantAndTrim()
        {
            LipSyncProfileId id = new("  MetaHuman  ");
            Assert.AreEqual("metahuman", id.Value);
        }

        [Test]
        public void Equality_IsOrdinalOnNormalizedValue()
        {
            LipSyncProfileId a = new("CC4_Extended");
            LipSyncProfileId b = new("cc4_extended");
            Assert.AreEqual(a, b);
            Assert.IsTrue(a == b);
        }

        [Test]
        public void BuiltInIds_AreValidAndNormalized()
        {
            Assert.IsTrue(LipSyncProfileId.ARKit.IsValid);
            Assert.IsTrue(LipSyncProfileId.MetaHuman.IsValid);
            Assert.IsTrue(LipSyncProfileId.Cc4Extended.IsValid);

            Assert.AreEqual("arkit", LipSyncProfileId.ARKit.Value);
            Assert.AreEqual("metahuman", LipSyncProfileId.MetaHuman.Value);
            Assert.AreEqual("cc4_extended", LipSyncProfileId.Cc4Extended.Value);
        }
    }
}
