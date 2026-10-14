using System.Collections.Generic;
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
    public sealed class ConvaiEmbodimentPresetBindingTests
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
        public void Apply_UsesProfileReceiverModuleIds()
        {
            GameObject root = new("EmbodimentBindingTest");
            StubProfile profile = ScriptableObject.CreateInstance<StubProfile>();
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();

            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                StubReceiver receiver = root.AddComponent<StubReceiver>();
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(StubReceiver.Id, profile)
                });

                binding.Apply(preset);

                Assert.AreSame(profile, receiver.Applied);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Apply_WarnsWhenPresetHasDuplicateModuleIds()
        {
            GameObject root = new("EmbodimentBindingDuplicateTest");
            StubProfile first = ScriptableObject.CreateInstance<StubProfile>();
            StubProfile second = ScriptableObject.CreateInstance<StubProfile>();
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();

            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                StubReceiver receiver = root.AddComponent<StubReceiver>();
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(StubReceiver.Id, first),
                    new(StubReceiver.Id.ToUpperInvariant(), second)
                });

                LogAssert.Expect(LogType.Warning, new Regex("Duplicate module profile slots.*test\\.stub", RegexOptions.IgnoreCase));

                binding.Apply(preset);

                Assert.AreSame(first, receiver.Applied);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(second);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Apply_ReportsUnknownAndMissingSlotsDeterministically()
        {
            GameObject root = new("EmbodimentBindingDiagnosticsTest");
            StubProfile unknownProfile = ScriptableObject.CreateInstance<StubProfile>();
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();

            try
            {
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                root.AddComponent<StubReceiver>();
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new("test.unknown", unknownProfile)
                });

                LogAssert.Expect(LogType.Warning,
                    new Regex("Profile slots without an active receiver.*test\\.unknown.*Active receivers without a preset slot.*test\\.stub",
                        RegexOptions.IgnoreCase));

                binding.Apply(preset);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(unknownProfile);
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void RegisteredReceiverAfterApply_GetsPresetProfile()
        {
            GameObject root = new("EmbodimentBindingLiveReceiverTest");
            StubProfile profile = ScriptableObject.CreateInstance<StubProfile>();
            ConvaiEmbodimentPreset preset = ScriptableObject.CreateInstance<ConvaiEmbodimentPreset>();

            try
            {
                EmbodimentContext context = root.AddComponent<EmbodimentContext>();
                ConvaiEmbodimentPresetBinding binding = root.AddComponent<ConvaiEmbodimentPresetBinding>();
                preset.SetProfileSlots(new List<EmbodimentProfileSlot>
                {
                    new(StubReceiver.Id, profile)
                });

                binding.Apply(preset);

                StubReceiver receiver = root.AddComponent<StubReceiver>();
                context.RegisterProfileReceiver(receiver, receiver);

                Assert.AreSame(profile, receiver.Applied);
            }
            finally
            {
                Object.DestroyImmediate(preset);
                Object.DestroyImmediate(profile);
                Object.DestroyImmediate(root);
            }
        }

        private sealed class StubProfile : ScriptableObject
        {
        }

        private sealed class StubReceiver : MonoBehaviour, IEmbodimentProfileReceiver
        {
            public const string Id = "test.stub";

            public StubProfile Applied { get; private set; }

            public string ModuleId => Id;
            public ScriptableObject Profile => Applied;
            public bool CanApplyProfile(ScriptableObject candidate) => candidate == null || candidate is StubProfile;

            public bool ApplyProfile(ScriptableObject candidate)
            {
                Applied = candidate as StubProfile;
                return candidate == null || Applied != null;
            }
        }

        private static LogLevelOverride[] CloneOverrides(System.Collections.Generic.IReadOnlyList<LogLevelOverride> source)
        {
            if (source == null) return System.Array.Empty<LogLevelOverride>();
            var copy = new LogLevelOverride[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }
    }
}
