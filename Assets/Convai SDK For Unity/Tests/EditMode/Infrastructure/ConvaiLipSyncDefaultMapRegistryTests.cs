using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class ConvaiLipSyncDefaultMapRegistryTests
    {
        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _cleanup.Count; i++)
                if (_cleanup[i] != null)
                    Object.DestroyImmediate(_cleanup[i]);

            _cleanup.Clear();
        }

        private readonly List<Object> _cleanup = new();

        [Test]
        public void GetForProfile_WithDuplicateEntries_LastEntryWins()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            ConvaiLipSyncMapAsset first = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            ConvaiLipSyncMapAsset second = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 2;
            ConfigureEntry(entries.GetArrayElementAtIndex(0), "arkit", first);
            ConfigureEntry(entries.GetArrayElementAtIndex(1), "arkit", second);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            ConvaiLipSyncMapAsset resolved = registry.GetForProfile(LipSyncProfileId.ARKit);

            // Assert
            Assert.AreSame(second, resolved);
        }

        [Test]
        public void GetForProfile_WithInvalidEntry_ReturnsNull()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            entries.GetArrayElementAtIndex(0).FindPropertyRelative("_profileId").stringValue = string.Empty;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            ConvaiLipSyncMapAsset resolved = registry.GetForProfile(LipSyncProfileId.ARKit);

            // Assert
            Assert.IsNull(resolved);
        }

        [Test]
        public void GetForProfile_WhenEntryIdRequiresNormalization_ResolvesUsingNormalizedId()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            ConvaiLipSyncMapAsset map = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            ConfigureEntry(entries.GetArrayElementAtIndex(0), " ARKIT ", map);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            ConvaiLipSyncMapAsset resolved = registry.GetForProfile(LipSyncProfileId.ARKit);

            // Assert
            Assert.AreSame(map, resolved);
        }

        [Test]
        public void GetForProfile_WhenMapTargetProfileDiffersFromEntryId_UsesMapTargetProfile()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            ConvaiLipSyncMapAsset map = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            SetMapTargetProfile(map, LipSyncProfileId.MetaHuman);

            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            ConfigureEntry(entries.GetArrayElementAtIndex(0), LipSyncProfileId.ARKit.Value, map);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            ConvaiLipSyncMapAsset arkitResolved = registry.GetForProfile(LipSyncProfileId.ARKit);
            ConvaiLipSyncMapAsset metaResolved = registry.GetForProfile(LipSyncProfileId.MetaHuman);

            // Assert
            Assert.IsNull(arkitResolved);
            Assert.AreSame(map, metaResolved);
        }

        [Test]
        public void GetForProfile_WhenMapTargetProfileChanges_RefreshesCacheWithoutRegistryValidate()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            ConvaiLipSyncMapAsset map = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            SetMapTargetProfile(map, LipSyncProfileId.ARKit);
            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 1;
            ConfigureEntry(entries.GetArrayElementAtIndex(0), LipSyncProfileId.ARKit.Value, map);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            ConvaiLipSyncMapAsset firstResolve = registry.GetForProfile(LipSyncProfileId.ARKit);
            Assert.AreSame(map, firstResolve);

            // Act
            SetMapTargetProfile(map, LipSyncProfileId.Cc4Extended);
            ConvaiLipSyncMapAsset arkitResolvedAfterChange = registry.GetForProfile(LipSyncProfileId.ARKit);
            ConvaiLipSyncMapAsset cc4ResolvedAfterChange = registry.GetForProfile(LipSyncProfileId.Cc4Extended);

            // Assert
            Assert.IsNull(arkitResolvedAfterChange);
            Assert.AreSame(map, cc4ResolvedAfterChange);
        }

        [Test]
        public void ValidationIssues_WhenDuplicateProfileDefaultsExist_ReportsIssue()
        {
            // Arrange
            ConvaiLipSyncDefaultMapRegistry registry =
                Track(ScriptableObject.CreateInstance<ConvaiLipSyncDefaultMapRegistry>());
            ConvaiLipSyncMapAsset first = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            ConvaiLipSyncMapAsset second = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());
            SetMapTargetProfile(first, LipSyncProfileId.ARKit);
            SetMapTargetProfile(second, LipSyncProfileId.ARKit);

            SerializedObject serialized = new(registry);
            SerializedProperty entries = serialized.FindProperty("_entries");
            entries.arraySize = 2;
            ConfigureEntry(entries.GetArrayElementAtIndex(0), LipSyncProfileId.ARKit.Value, first);
            ConfigureEntry(entries.GetArrayElementAtIndex(1), LipSyncProfileId.ARKit.Value, second);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            IReadOnlyList<string> issues = registry.ValidationIssues;

            // Assert
            Assert.That(issues, Has.Some.Contains("Duplicate default map for profile 'arkit'"));
        }

        private static void ConfigureEntry(SerializedProperty entry, string profileId, ConvaiLipSyncMapAsset map)
        {
            entry.FindPropertyRelative("_profileId").stringValue = profileId;
            entry.FindPropertyRelative("_defaultMap").objectReferenceValue = map;
        }

        private static void SetMapTargetProfile(ConvaiLipSyncMapAsset map, LipSyncProfileId profileId)
        {
            SerializedObject mapSerialized = new(map);
            mapSerialized.FindProperty("_targetProfileId").stringValue = profileId.Value;
            mapSerialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private T Track<T>(T obj) where T : Object
        {
            if (obj != null) _cleanup.Add(obj);

            return obj;
        }
    }
}
