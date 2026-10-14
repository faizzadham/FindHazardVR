using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.LipSync;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Domain
{
    [TestFixture]
    public class LipSyncBlendshapeSnapshotTests
    {
        [Test]
        public void GetName_WithOutOfRangeIndex_ThrowsArgumentOutOfRangeException()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.5f }, new[] { "jawOpen" });

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetName(1));
        }

        [Test]
        public void GetValue_WithOutOfRangeIndex_ThrowsArgumentOutOfRangeException()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.5f }, new[] { "jawOpen" });

            // Act & Assert
            Assert.Throws<ArgumentOutOfRangeException>(() => snapshot.GetValue(1));
        }

        [Test]
        public void TryGetValue_WithExactChannelName_ReturnsMappedValue()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.25f }, new[] { "jawOpen" });

            // Act
            bool found = snapshot.TryGetValue("jawOpen", out float value);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(0.25f, value, 0.0001f);
        }

        [Test]
        public void TryGetValue_WithDifferentCase_DoesNotMatch()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.25f }, new[] { "jawOpen" });

            // Act
            bool found = snapshot.TryGetValue("JawOpen", out _);

            // Assert
            Assert.IsFalse(found);
        }

        [Test]
        public void CopyValuesTo_WithSmallerDestination_CopiesDestinationLength()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.1f, 0.2f, 0.3f }, new[] { "A", "B", "C" });
            float[] destination = { -1f, -1f };

            // Act
            int copied = snapshot.CopyValuesTo(destination);

            // Assert
            Assert.AreEqual(2, copied);
            Assert.AreEqual(0.1f, destination[0], 0.0001f);
            Assert.AreEqual(0.2f, destination[1], 0.0001f);
        }

        [Test]
        public void Enumerator_WhenIterated_ReturnsPairsInChannelOrder()
        {
            // Arrange
            BlendshapeSnapshot snapshot = CreateSnapshot(new[] { 0.4f, 0.7f }, new[] { "A", "B" });
            BlendshapeSnapshot.Enumerator enumerator = snapshot.GetEnumerator();

            // Act
            bool hasFirst = enumerator.MoveNext();
            KeyValuePair<string, float> first = enumerator.Current;
            bool hasSecond = enumerator.MoveNext();
            KeyValuePair<string, float> second = enumerator.Current;

            // Assert
            Assert.IsTrue(hasFirst && hasSecond);
            Assert.AreEqual("A", first.Key);
            Assert.AreEqual(0.4f, first.Value, 0.0001f);
            Assert.AreEqual("B", second.Key);
            Assert.AreEqual(0.7f, second.Value, 0.0001f);
        }

        private static BlendshapeSnapshot CreateSnapshot(float[] values, string[] names)
        {
            ConstructorInfo constructor = typeof(BlendshapeSnapshot).GetConstructor(
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(float[]), typeof(IReadOnlyList<string>) },
                null);
            if (constructor == null)
                throw new MissingMethodException(nameof(BlendshapeSnapshot), ".ctor(float[], IReadOnlyList<string>)");

            return (BlendshapeSnapshot)constructor.Invoke(new object[] { values, names });
        }
    }
}
