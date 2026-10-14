using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using Convai.Modules.LipSync.Editor;
using Convai.Modules.LipSync.Profiles;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Application
{
    [TestFixture]
    public class LipSyncDefaultMappingResolverTests
    {
        [SetUp]
        public void SetUp()
        {
            LipSyncDefaultMappingResolver.ClearCachesForTests();
            LipSyncProfileCatalog.ClearCachesForTests();
        }

        [TearDown]
        public void TearDown()
        {
            LipSyncDefaultMappingResolver.ClearCachesForTests();
            LipSyncProfileCatalog.ClearCachesForTests();
            DestroyTrackedObjects();
        }

        private readonly List<Object> _cleanup = new();

        [Test]
        public void ResolveEffective_WithProfileMatchedAssignedMap_ReturnsAssignedMap()
        {
            // Arrange
            ConvaiLipSyncMapAsset assigned = Track(CreateMapAsset(LipSyncProfileId.ARKit));

            // Act
            ConvaiLipSyncMapAsset resolved = LipSyncDefaultMappingResolver.ResolveEffective(
                assigned,
                LipSyncProfileId.ARKit,
                out bool usedFallback);

            // Assert
            Assert.AreSame(assigned, resolved);
            Assert.IsFalse(usedFallback);
        }

        [Test]
        public void ResolveEffective_WithProfileMismatchedAssignedMap_UsesProfileDefault()
        {
            // Arrange
            ConvaiLipSyncMapAsset assignedMismatch = Track(CreateMapAsset(LipSyncProfileId.Cc4Extended));
            ConvaiLipSyncMapAsset profileDefault = Track(CreateMapAsset(LipSyncProfileId.ARKit));
            ConvaiLipSyncDefaultMapRegistry registry = Track(CreateRegistry((LipSyncProfileId.ARKit, profileDefault)));
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            // Act
            ConvaiLipSyncMapAsset resolved = LipSyncDefaultMappingResolver.ResolveEffective(
                assignedMismatch,
                LipSyncProfileId.ARKit,
                out bool usedFallback);

            // Assert
            Assert.AreSame(profileDefault, resolved);
            Assert.IsFalse(usedFallback);
        }

        [Test]
        public void ResolveEffective_WhenAssignedMapIsNull_UsesProfileDefault()
        {
            // Arrange
            ConvaiLipSyncMapAsset profileDefault = Track(CreateMapAsset(LipSyncProfileId.Cc4Extended));
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(CreateRegistry((LipSyncProfileId.Cc4Extended, profileDefault)));
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            // Act
            ConvaiLipSyncMapAsset resolved = LipSyncDefaultMappingResolver.ResolveEffective(
                null,
                LipSyncProfileId.Cc4Extended,
                out bool usedFallback);

            // Assert
            Assert.AreSame(profileDefault, resolved);
            Assert.IsFalse(usedFallback);
        }

        [Test]
        [Description(
            "Regression coverage: resolver must generate a safe-disabled map when profile defaults are missing.")]
        public void ResolveEffective_WhenProfileDefaultMissing_ReturnsSafeDisabledMap()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry = Track(CreateRegistry());
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            // Act
            ConvaiLipSyncMapAsset resolved = LipSyncDefaultMappingResolver.ResolveEffective(
                null,
                LipSyncProfileId.ARKit,
                out bool usedFallback);

            // Assert
            string firstBlendshape = LipSyncProfileCatalog.GetSourceBlendshapeNamesOrEmpty(LipSyncProfileId.ARKit)[0];
            Assert.IsTrue(usedFallback);
            Assert.IsNotNull(resolved);
            Assert.IsFalse(resolved.AllowUnmappedPassthrough);
            Assert.IsFalse(resolved.IsEnabled(firstBlendshape));
        }

        [Test]
        public void ResolveEffective_WithMissingDefaults_CachesSafeDisabledMapPerProfile()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry = Track(CreateRegistry());
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            // Act
            ConvaiLipSyncMapAsset first =
                LipSyncDefaultMappingResolver.ResolveEffective(null, LipSyncProfileId.MetaHuman, out _);
            ConvaiLipSyncMapAsset second =
                LipSyncDefaultMappingResolver.ResolveEffective(null, LipSyncProfileId.MetaHuman, out _);

            // Assert
            Assert.AreSame(first, second);
        }

        [Test]
        public void ResolveEffective_WithDifferentProfiles_ResolvesDifferentProfileDefaults()
        {
            // Arrange
            ConvaiLipSyncMapAsset arkitDefault = Track(CreateMapAsset(LipSyncProfileId.ARKit));
            ConvaiLipSyncMapAsset cc4Default = Track(CreateMapAsset(LipSyncProfileId.Cc4Extended));
            ConvaiLipSyncDefaultMapRegistry registry = Track(CreateRegistry(
                (LipSyncProfileId.ARKit, arkitDefault),
                (LipSyncProfileId.Cc4Extended, cc4Default)));
            LipSyncDefaultMappingResolver.SetRegistryOverrideForTests(registry);

            // Act
            ConvaiLipSyncMapAsset resolvedArkit =
                LipSyncDefaultMappingResolver.ResolveEffective(null, LipSyncProfileId.ARKit, out _);
            ConvaiLipSyncMapAsset resolvedCc4 =
                LipSyncDefaultMappingResolver.ResolveEffective(null, LipSyncProfileId.Cc4Extended, out _);

            // Assert
            Assert.AreSame(arkitDefault, resolvedArkit);
            Assert.AreSame(cc4Default, resolvedCc4);
        }

        [Test]
        public void ResolveEffective_LegacyMhaMap_IsAcceptedForCanonicalMetaHumanProfile()
        {
            ConvaiLipSyncProfile metahuman = Track(CreateProfile(LipSyncProfileId.MetaHumanValue, "mha"));
            ConvaiLipSyncProfileRegistry profiles = Track(CreateProfileRegistry(metahuman));
            LipSyncProfileCatalog.SetRegistryOverridesForTests(profiles, new ConvaiLipSyncProfileRegistry[0]);
            ConvaiLipSyncMapAsset legacyMap = Track(CreateMapAsset(new LipSyncProfileId("mha")));

            ConvaiLipSyncMapAsset resolved = LipSyncDefaultMappingResolver.ResolveEffective(
                legacyMap,
                LipSyncProfileId.MetaHuman,
                out bool usedFallback);

            Assert.AreSame(legacyMap, resolved);
            Assert.IsFalse(usedFallback);
        }

        private static ConvaiLipSyncMapAsset CreateMapAsset(LipSyncProfileId profileId)
        {
            var map = ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>();
            SerializedObject serialized = new(map);
            serialized.FindProperty("_targetProfileId").stringValue = profileId.Value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            map.InitializeWithDefaults();
            return map;
        }

        private static ConvaiLipSyncProfile CreateProfile(string profileId, string transportFormat)
        {
            var profile = ScriptableObject.CreateInstance<ConvaiLipSyncProfile>();
            SerializedObject serialized = new(profile);
            serialized.FindProperty("_profileId").stringValue = profileId;
            serialized.FindProperty("_displayName").stringValue = profileId;
            serialized.FindProperty("_transportFormat").stringValue = transportFormat;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return profile;
        }

        private static ConvaiLipSyncProfileRegistry CreateProfileRegistry(params ConvaiLipSyncProfile[] profiles)
        {
            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncProfileRegistry>();
            SerializedObject serialized = new(registry);
            SerializedProperty list = serialized.FindProperty("_profiles");
            list.arraySize = profiles.Length;
            for (int i = 0; i < profiles.Length; i++)
                list.GetArrayElementAtIndex(i).objectReferenceValue = profiles[i];
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return registry;
        }

        private static ConvaiLipSyncDefaultMapRegistry CreateRegistry(
            params (LipSyncProfileId profileId, ConvaiLipSyncMapAsset map)[] entries)
        {
            var registry = ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>();
            SerializedObject serialized = new(registry);
            SerializedProperty list = serialized.FindProperty("_entries");
            list.arraySize = entries.Length;

            for (int i = 0; i < entries.Length; i++)
            {
                SerializedProperty entry = list.GetArrayElementAtIndex(i);
                entry.FindPropertyRelative("_profileId").stringValue = entries[i].profileId.Value;
                entry.FindPropertyRelative("_defaultMap").objectReferenceValue = entries[i].map;
            }

            serialized.ApplyModifiedPropertiesWithoutUndo();
            return registry;
        }

        private T Track<T>(T obj) where T : Object
        {
            if (obj != null) _cleanup.Add(obj);

            return obj;
        }

        private void DestroyTrackedObjects()
        {
            for (int i = 0; i < _cleanup.Count; i++)
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);

            _cleanup.Clear();
        }
    }
}
