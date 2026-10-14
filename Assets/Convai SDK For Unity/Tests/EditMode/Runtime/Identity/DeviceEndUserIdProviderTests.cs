using Convai.Runtime.Identity;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime.Identity
{
    /// <summary>
    ///     Safe unit tests for <see cref="DeviceEndUserIdProvider" /> validation behavior.
    /// </summary>
    [TestFixture]
    public class DeviceEndUserIdProviderTests
    {
        [Test]
        public void IsValid_WhenValueIsNull_ReturnsFalse() => Assert.IsFalse(DeviceEndUserIdProvider.IsValid(null));

        [Test]
        public void IsValid_WhenValueIsEmpty_ReturnsFalse() =>
            Assert.IsFalse(DeviceEndUserIdProvider.IsValid(string.Empty));

        [Test]
        public void IsValid_WhenValueIsWhitespace_ReturnsFalse() =>
            Assert.IsFalse(DeviceEndUserIdProvider.IsValid("   "));

        [Test]
        public void IsValid_WhenValueIsAllZeros_ReturnsFalse() =>
            Assert.IsFalse(DeviceEndUserIdProvider.IsValid("0000000000"));

        [Test]
        public void IsValid_WhenValueHasAtLeastOneNonZero_ReturnsTrue()
        {
            Assert.IsTrue(DeviceEndUserIdProvider.IsValid("0000000001"));
            Assert.IsTrue(DeviceEndUserIdProvider.IsValid("1000000000"));
            Assert.IsTrue(DeviceEndUserIdProvider.IsValid("abc123"));
        }
    }
}
