using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Domain.Logging;
using Convai.Modules.Embodiment.Components;
using Convai.Modules.Embodiment.Presets;
using Convai.Runtime;
using Convai.Runtime.Embodiment;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Embodiment
{
    public sealed class ConvaiEmbodimentPresetBindingEdgeCasesTests
    {
        private ConvaiSettings _settings;
        private LogLevel _originalGlobalLevel;
        private LogLevelOverride[] _originalCategoryOverrides;

        [SetUp]
        public void SetUp()
        {
            _settings = ConvaiSettings.Instance;
            if (_settings == null)
                return;

            _originalGlobalLevel = _settings.GlobalLogLevel;
            _originalCategoryOverrides = CloneOverrides(_settings.CategoryOverrides);
            _settings.SetGlobalLogLevel(LogLevel.Trace);
            _settings.SetCategoryOverrides(System.Array.Empty<LogLevelOverride>());
            LoggingConfig.InvalidateCache();
        }

        [TearDown]
        public void TearDown()
        {
            if (_settings == null)
                return;

            _settings.SetGlobalLogLevel(_originalGlobalLevel);
            _settings.SetCategoryOverrides(CloneOverrides(_originalCategoryOverrides));
            LoggingConfig.InvalidateCache();
        }

        [Test]
        public void Apply_NullPreset_IsNoOp()
        {
            GameObject root = new("PresetEdge_NullPreset");
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                TrackingReceiver receiver = root.AddComponent<TrackingReceiver>();

                binding.Apply(null);

                Assert.IsFalse(receiver.ApplyCalled);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void Apply_PreserveMissingSlotsDefaultTrue_ReceiverWithoutSlotNotCalled()
        {
            GameObject root = new("PresetEdge_PreserveTrue");
            ConvaiEmbodimentPreset emptyPreset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                TrackingReceiver receiver = root.AddComponent<TrackingReceiver>();

                // Diagnostic warning expected because receiver has no slot in preset.
                LogAssert.Expect(LogType.Warning,
                    new Regex("Active receivers without a preset slot.*inspector profiles will be preserved",
                        RegexOptions.IgnoreCase));

                binding.Apply(emptyPreset);

                Assert.IsFalse(receiver.ApplyCalled, "ApplyProfile must not be called when preserveMissingSlots=true");
            }
            finally
            {
                Object.DestroyImmediate(emptyPreset);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Apply_PreserveMissingSlotsFalse_ReceiverWithoutSlotGetsNull()
        {
            GameObject root = new("PresetEdge_PreserveFalse");
            ConvaiEmbodimentPreset emptyPreset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                TrackingReceiver receiver = root.AddComponent<TrackingReceiver>();

                SetPrivateField(binding, "preserveMissingSlots", false);

                LogAssert.Expect(LogType.Warning,
                    new Regex("Active receivers without a preset slot.*null profiles will be applied",
                        RegexOptions.IgnoreCase));

                binding.Apply(emptyPreset);

                Assert.IsTrue(receiver.ApplyCalled, "ApplyProfile must be called when preserveMissingSlots=false");
                Assert.IsNull(receiver.AppliedProfile);
            }
            finally
            {
                Object.DestroyImmediate(emptyPreset);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Apply_NullProfileInSlot_CallsApplyWithNull()
        {
            GameObject root = new("PresetEdge_NullProfile");
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                TrackingReceiver receiver = root.AddComponent<TrackingReceiver>();

                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(TrackingReceiver.Id, null)
                });

                // Diagnostic for null profile slot expected.
                LogAssert.Expect(LogType.Warning,
                    new Regex("Profile slots with null profiles", RegexOptions.IgnoreCase));

                binding.Apply(preset);

                Assert.IsTrue(receiver.ApplyCalled);
                Assert.IsNull(receiver.AppliedProfile);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ApplyPresetById_NoLibrary_ReturnsFalse()
        {
            GameObject root = new("PresetEdge_NoLibrary");
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();

                bool result = binding.ApplyPresetById("some-id");

                Assert.IsFalse(result);
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void ApplyPresetById_UnknownId_ReturnsFalse()
        {
            GameObject root = new("PresetEdge_UnknownId");
            ConvaiEmbodimentPresetLibrary library = ScriptableObject.CreateInstance<ConvaiEmbodimentPresetLibrary>();
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                SetPrivateField(binding, "library", library);

                bool result = binding.ApplyPresetById("nonexistent");

                Assert.IsFalse(result);
            }
            finally
            {
                Object.DestroyImmediate(library);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void ApplyPresetById_KnownId_AppliesPresetAndReturnsTrue()
        {
            GameObject root = new("PresetEdge_KnownId");
            StubProfile profile = ScriptableObject.CreateInstance<StubProfile>();
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            ConvaiEmbodimentPresetLibrary library = ScriptableObject.CreateInstance<ConvaiEmbodimentPresetLibrary>();
            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                TrackingReceiver receiver = root.AddComponent<TrackingReceiver>();

                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(TrackingReceiver.Id, profile)
                });
                SetPrivateField(preset, "presetId", "test-preset");
                SetLibraryPresets(library, new List<ConvaiEmbodimentPreset> { preset });
                SetPrivateField(binding, "library", library);

                bool result = binding.ApplyPresetById("test-preset");

                Assert.IsTrue(result);
                Assert.AreSame(profile, receiver.AppliedProfile);
            }
            finally
            {
                Object.DestroyImmediate(library);
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Apply_FiresEmbodimentConfigurationChanged()
        {
            GameObject root = new("PresetEdge_ConfigChanged");
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();
            try
            {
                EmbodimentContext ctx = root.AddComponent<EmbodimentContext>();
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                int count = 0;
                ctx.EmbodimentConfigurationChanged += () => count++;

                // Empty preset → diagnostic warning for receiver-less apply.
                binding.Apply(preset);

                Assert.AreEqual(1, count);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(root);
            }
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, $"Field '{fieldName}' not found on {target.GetType().Name}");
            field.SetValue(target, value);
        }

        private static void SetLibraryPresets(ConvaiEmbodimentPresetLibrary library, List<ConvaiEmbodimentPreset> presets)
        {
            FieldInfo field = typeof(ConvaiEmbodimentPresetLibrary).GetField(
                "presets", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "Field 'presets' not found on ConvaiEmbodimentPresetLibrary");
            field.SetValue(library, presets);
        }

        private static LogLevelOverride[] CloneOverrides(System.Collections.Generic.IReadOnlyList<LogLevelOverride> source)
        {
            if (source == null) return System.Array.Empty<LogLevelOverride>();
            var copy = new LogLevelOverride[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        private sealed class StubProfile : ScriptableObject { }

        private sealed class TrackingReceiver : MonoBehaviour, IEmbodimentProfileReceiver
        {
            public const string Id = "test.tracking";

            public bool ApplyCalled { get; private set; }
            public ScriptableObject AppliedProfile { get; private set; }

            public string ModuleId => Id;
            public ScriptableObject Profile => AppliedProfile;
            public bool CanApplyProfile(ScriptableObject candidate) => true;

            public bool ApplyProfile(ScriptableObject candidate)
            {
                ApplyCalled = true;
                AppliedProfile = candidate;
                return true;
            }
        }
    }
}
