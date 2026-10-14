using System.Collections.Generic;
using Convai.Domain.Models.LipSync;
using Convai.Modules.LipSync;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class ConvaiLipSyncMapAssetTests
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
        public void GetTargetNames_WithExplicitEntry_ReturnsConfiguredTargets()
        {
            // Arrange
            ConvaiLipSyncMapAsset map = Track(CreateMapWithSingleEntry("jawOpen", "MouthOpen", false, true));

            // Act
            IReadOnlyList<string> targets = map.GetTargetNames("jawOpen");

            // Assert
            Assert.AreEqual(1, targets.Count);
            Assert.AreEqual("MouthOpen", targets[0]);
        }

        [Test]
        public void IsEnabled_WithUnmappedSourceAndDisabledPassthrough_ReturnsFalse()
        {
            // Arrange
            ConvaiLipSyncMapAsset map = Track(CreateMapWithSingleEntry("jawOpen", "MouthOpen", false, true));

            // Act
            bool isEnabled = map.IsEnabled("unknown");

            // Assert
            Assert.IsFalse(isEnabled);
        }

        [Test]
        public void TryGetEntry_WithDuplicateSourceEntries_UsesLastEntry()
        {
            // Arrange
            var map = ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>();
            Track(map);
            SerializedObject serialized = new(map);
            SerializedProperty mappings = serialized.FindProperty("_mappings");
            mappings.arraySize = 2;
            ConfigureEntry(mappings.GetArrayElementAtIndex(0), "jawOpen", "MouthA", false);
            ConfigureEntry(mappings.GetArrayElementAtIndex(1), "jawOpen", "MouthB", true);
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            bool found = map.TryGetEntry("jawOpen", out ConvaiLipSyncMapAsset.BlendshapeMappingSnapshot snapshot);

            // Assert
            Assert.IsTrue(found);
            Assert.IsTrue(snapshot.Enabled);
            Assert.AreEqual("MouthB", snapshot.TargetNames[0]);
        }

        [Test]
        public void TryGetEntry_WithValidCurveExponent_PreservesConfiguredValue()
        {
            // Arrange
            ConvaiLipSyncMapAsset map = Track(CreateMapWithSingleEntry("jawOpen", "MouthOpen", false, true));
            SerializedObject serialized = new(map);
            serialized.FindProperty("_mappings").GetArrayElementAtIndex(0)
                .FindPropertyRelative("curveExponent").floatValue = 0.7f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            bool found = map.TryGetEntry("jawOpen", out ConvaiLipSyncMapAsset.BlendshapeMappingSnapshot snapshot);

            // Assert
            Assert.IsTrue(found);
            Assert.AreEqual(0.7f, snapshot.CurveExponent, 1e-5f);
        }

        [Test]
        public void TryGetEntry_WithOutOfRangeCurveExponent_FallsBackToLinear()
        {
            // Arrange: assets serialized before curveExponent existed deserialize it as 0.
            ConvaiLipSyncMapAsset map = Track(CreateMapWithSingleEntry("jawOpen", "MouthOpen", false, true));
            SerializedObject serialized = new(map);
            serialized.FindProperty("_mappings").GetArrayElementAtIndex(0)
                .FindPropertyRelative("curveExponent").floatValue = 0f;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            // Act
            map.TryGetEntry("jawOpen", out ConvaiLipSyncMapAsset.BlendshapeMappingSnapshot snapshot);

            // Assert
            Assert.AreEqual(1f, snapshot.CurveExponent, 1e-5f);
        }

        [Test]
        public void InitializeAsSafeDisabledProfile_DisablesAllKnownSourceChannels()
        {
            // Arrange
            ConvaiLipSyncMapAsset map = Track(ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>());

            // Act
            map.InitializeAsSafeDisabledProfile(LipSyncProfileId.ARKit);

            // Assert
            Assert.IsFalse(map.AllowUnmappedPassthrough);
            Assert.IsFalse(map.IsEnabled("EyeBlinkLeft"));
        }

        [Test]
        public void CreateSafeDisabledMap_ForProfile_CreatesRuntimeOnlyDisabledMap()
        {
            // Arrange & Act
            ConvaiLipSyncMapAsset map = Track(ConvaiLipSyncMapAsset.CreateSafeDisabledMap(LipSyncProfileId.MetaHuman));

            // Assert
            Assert.AreEqual(HideFlags.HideAndDontSave, map.hideFlags);
            Assert.IsFalse(map.AllowUnmappedPassthrough);
        }

        private static ConvaiLipSyncMapAsset CreateMapWithSingleEntry(string source, string target,
            bool allowPassthrough, bool enabled)
        {
            var map = ScriptableObject.CreateInstance<ConvaiLipSyncMapAsset>();
            SerializedObject serialized = new(map);
            serialized.FindProperty("_allowUnmappedPassthrough").boolValue = allowPassthrough;
            SerializedProperty mappings = serialized.FindProperty("_mappings");
            mappings.arraySize = 1;
            ConfigureEntry(mappings.GetArrayElementAtIndex(0), source, target, enabled);
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return map;
        }

        private static void ConfigureEntry(SerializedProperty entry, string source, string target, bool enabled)
        {
            entry.FindPropertyRelative("sourceBlendshape").stringValue = source;
            entry.FindPropertyRelative("enabled").boolValue = enabled;
            entry.FindPropertyRelative("multiplier").floatValue = 1f;
            entry.FindPropertyRelative("offset").floatValue = 0f;
            entry.FindPropertyRelative("useOverrideValue").boolValue = false;
            entry.FindPropertyRelative("ignoreGlobalModifiers").boolValue = false;
            entry.FindPropertyRelative("clampMinValue").floatValue = 0f;
            entry.FindPropertyRelative("clampMaxValue").floatValue = 1f;
            SerializedProperty targetNames = entry.FindPropertyRelative("targetNames");
            targetNames.arraySize = 1;
            targetNames.GetArrayElementAtIndex(0).stringValue = target;
        }

        private T Track<T>(T obj) where T : Object
        {
            if (obj != null) _cleanup.Add(obj);

            return obj;
        }
    }
}
