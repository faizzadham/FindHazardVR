using Convai.Modules.LipSync.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class ConvaiLipSyncProfileTests
    {
        [Test]
        public void IsValid_WithProfileIdAndFormatSet_ReturnsTrue()
        {
            // Arrange
            var asset = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();

            try
            {
                SerializedObject serialized = new(asset);
                serialized.FindProperty("_profileId").stringValue = "arkit";
                serialized.FindProperty("_transportFormat").stringValue = "arkit";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                // Act
                bool isValid = asset.IsValid;

                // Assert
                Assert.IsTrue(isValid);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DescribeValidationIssue_WhenProfileIdMissing_ReturnsExpectedReason()
        {
            // Arrange
            var asset = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();

            try
            {
                SerializedObject serialized = new(asset);
                serialized.FindProperty("_profileId").stringValue = string.Empty;
                serialized.FindProperty("_transportFormat").stringValue = "arkit";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                // Act
                string issue = asset.DescribeValidationIssue();

                // Assert
                Assert.AreEqual("ProfileId is empty.", issue);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void DisplayName_WhenWhitespaceConfigured_FallsBackToProfileId()
        {
            // Arrange
            var asset = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();

            try
            {
                SerializedObject serialized = new(asset);
                serialized.FindProperty("_profileId").stringValue = "metahuman";
                serialized.FindProperty("_displayName").stringValue = "   ";
                serialized.FindProperty("_transportFormat").stringValue = "mha";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                // Act
                string displayName = asset.DisplayName;

                // Assert
                Assert.AreEqual("metahuman", displayName);
            }
            finally
            {
                Object.DestroyImmediate(asset);
            }
        }
    }
}
