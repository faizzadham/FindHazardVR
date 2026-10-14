using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Application
{
    [TestFixture]
    public class LipSyncProfileCatalogTests
    {
        [SetUp]
        public void SetUp() => LipSyncProfileCatalog.ClearCachesForTests();

        [TearDown]
        public void TearDown()
        {
            LipSyncProfileCatalog.ClearCachesForTests();
            for (int i = 0; i < _cleanup.Count; i++)
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);

            _cleanup.Clear();
        }

        private readonly List<Object> _cleanup = new();

        [Test]
        public void GetProfiles_WithDuplicateProfileIds_LastRegistryWinsDeterministicallyBySortOrder()
        {
            // Arrange
            ConvaiLipSyncProfile builtIn = Track(CreateProfile("arkit", "arkit", "BuiltIn"));
            ConvaiLipSyncProfile extA = Track(CreateProfile("arkit", "ext_a", "ExtensionA"));
            ConvaiLipSyncProfile extB = Track(CreateProfile("arkit", "ext_b", "ExtensionB"));
            ConvaiLipSyncProfileRegistry builtInRegistry = Track(CreateRegistry("BuiltIn", 0, builtIn));
            ConvaiLipSyncProfileRegistry extensionZ = Track(CreateRegistry("Z_Extension", 10, extB));
            ConvaiLipSyncProfileRegistry extensionA = Track(CreateRegistry("A_Extension", 10, extA));

            LipSyncProfileCatalog.SetRegistryOverridesForTests(builtInRegistry, new[] { extensionZ, extensionA });

            // Act
            bool found =
                LipSyncProfileCatalog.TryGetProfile(LipSyncProfileId.ARKit, out ConvaiLipSyncProfile resolved);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual("ext_b", resolved.TransportFormat);
            Assert.That(LipSyncProfileCatalog.GetValidationIssues(), Has.Some.Contains("Duplicate profile id 'arkit'"));
        }

        [Test]
        public void GetProfiles_WhenExtensionOverridesBuiltIn_UsesExtensionRegardlessOfPriority()
        {
            // Arrange
            ConvaiLipSyncProfile builtIn = Track(CreateProfile("metahuman", "mha", "BuiltInMetaHuman"));
            ConvaiLipSyncProfile extension =
                Track(CreateProfile("metahuman", "metahuman_custom", "ExtensionMetaHuman"));
            ConvaiLipSyncProfileRegistry builtInRegistry = Track(CreateRegistry("BuiltIn", 999, builtIn));
            ConvaiLipSyncProfileRegistry extensionRegistry = Track(CreateRegistry("Extension", -999, extension));

            LipSyncProfileCatalog.SetRegistryOverridesForTests(builtInRegistry, new[] { extensionRegistry });

            // Act
            bool found =
                LipSyncProfileCatalog.TryGetProfile(LipSyncProfileId.MetaHuman, out ConvaiLipSyncProfile resolved);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual("metahuman_custom", resolved.TransportFormat);
        }

        [Test]
        public void GetProfiles_WhenInvalidProfilePresent_SkipsInvalidAndRecordsValidationIssue()
        {
            // Arrange
            ConvaiLipSyncProfile invalid = Track(CreateProfile(string.Empty, "arkit", "Invalid"));
            ConvaiLipSyncProfileRegistry builtInRegistry = Track(CreateRegistry("BuiltIn", 0, invalid));
            LipSyncProfileCatalog.SetRegistryOverridesForTests(builtInRegistry,
                new ConvaiLipSyncProfileRegistry[0]);

            // Act
            bool found = LipSyncProfileCatalog.TryGetProfile(LipSyncProfileId.ARKit, out _);

            // Assert
            Assert.IsFalse(found);
            Assert.That(LipSyncProfileCatalog.GetValidationIssues(), Has.Some.Contains("Skipping profile"));
        }

        [Test]
        public void TryGetProfile_LegacyMhaAlias_PrefersExactRegisteredProfile()
        {
            ConvaiLipSyncProfile metahuman = Track(CreateProfile("metahuman", "mha", "MetaHuman"));
            ConvaiLipSyncProfile exactLegacy = Track(CreateProfile("mha", "custom_mha", "Custom MHA"));
            ConvaiLipSyncProfileRegistry registry = Track(CreateRegistry("BuiltIn", 0, metahuman, exactLegacy));
            LipSyncProfileCatalog.SetRegistryOverridesForTests(registry, new ConvaiLipSyncProfileRegistry[0]);

            Assert.IsTrue(LipSyncProfileCatalog.TryGetProfile("mha", out ConvaiLipSyncProfile resolved));
            Assert.AreSame(exactLegacy, resolved);
        }

        private static ConvaiLipSyncProfile CreateProfile(string profileId, string format, string displayName)
        {
            var profile = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();
            SerializedObject serialized = new(profile);
            serialized.FindProperty("_profileId").stringValue = profileId;
            serialized.FindProperty("_displayName").stringValue = displayName;
            serialized.FindProperty("_transportFormat").stringValue = format;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }

        private static ConvaiLipSyncProfileRegistry CreateRegistry(
            string name,
            int priority,
            params ConvaiLipSyncProfile[] profiles)
        {
            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncProfileRegistry>();
            registry.name = name;

            SerializedObject serialized = new(registry);
            serialized.FindProperty("_priority").intValue = priority;
            SerializedProperty list = serialized.FindProperty("_profiles");
            list.arraySize = profiles.Length;
            for (int i = 0; i < profiles.Length; i++) list.GetArrayElementAtIndex(i).objectReferenceValue = profiles[i];

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return registry;
        }

        private T Track<T>(T obj) where T : Object
        {
            if (obj != null) _cleanup.Add(obj);

            return obj;
        }
    }
}
