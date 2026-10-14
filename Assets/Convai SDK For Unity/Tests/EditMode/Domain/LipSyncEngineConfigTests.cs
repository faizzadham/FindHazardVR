using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncEngineConfigTests
    {
        [Test]
        public void Constructor_WithOutOfRangeValues_ClampsAllFieldsToSafeBounds()
        {
            // Arrange & Act
            LipSyncEngineConfig config = new(
                -1f,
                10f,
                5f,
                99f,
                2f);

            // Assert
            Assert.AreEqual(0.01f, config.FadeOutDuration, 0.0001f);
            Assert.AreEqual(0.95f, config.SmoothingFactor, 0.0001f);
            Assert.AreEqual(1f, config.TimeOffsetSeconds, 0.0001f);
            Assert.AreEqual(10f, config.MaxBufferedSeconds, 0.0001f);
            Assert.AreEqual(1f, config.MinResumeHeadroomSeconds, 0.0001f);
        }

        [Test]
        public void Equality_WithSameValues_ReturnsTrueAndHashCodesMatch()
        {
            // Arrange
            LipSyncEngineConfig left = new(0.2f, 0.2f, 0.1f, 2f, 0.2f);
            LipSyncEngineConfig right = new(0.2f, 0.2f, 0.1f, 2f, 0.2f);

            // Act
            bool isEqual = left == right;

            // Assert
            Assert.IsTrue(isEqual);
            Assert.AreEqual(left.GetHashCode(), right.GetHashCode());
        }

        [Test]
        public void Equality_WithDifferentValues_ReturnsFalse()
        {
            // Arrange
            LipSyncEngineConfig left = new(0.2f, 0.2f, 0.1f, 2f, 0.2f);
            LipSyncEngineConfig right = new(0.3f, 0.2f, 0.1f, 2f, 0.2f);

            // Act
            bool isEqual = left.Equals(right);

            // Assert
            Assert.IsFalse(isEqual);
        }
    }
}
