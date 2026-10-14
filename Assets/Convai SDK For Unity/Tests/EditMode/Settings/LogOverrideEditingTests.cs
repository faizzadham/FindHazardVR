using Convai.Domain.Logging;
using Convai.Editor.Settings.Services;
using Convai.Runtime;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Settings
{
    public class LogOverrideEditingTests
    {
        private ConvaiSettings _settings;
        private SerializedObject _serialized;

        [SetUp]
        public void SetUp()
        {
            _settings = ScriptableObject.CreateInstance<ConvaiSettings>();
            _serialized = new SerializedObject(_settings);
        }

        [TearDown]
        public void TearDown()
        {
            _serialized.Dispose();
            Object.DestroyImmediate(_settings);
        }

        [Test]
        public void SetOverride_AddsUpdatesAndRemoves()
        {
            Assert.IsNull(LogOverrideEditing.GetOverride(_serialized, LogCategory.Audio));

            LogOverrideEditing.SetOverride(_serialized, LogCategory.Audio, LogLevel.Debug);
            Assert.AreEqual(LogLevel.Debug, LogOverrideEditing.GetOverride(_serialized, LogCategory.Audio));
            Assert.AreEqual(1, LogOverrideEditing.GetOverrideCount(_serialized));

            LogOverrideEditing.SetOverride(_serialized, LogCategory.Audio, LogLevel.Error);
            Assert.AreEqual(LogLevel.Error, LogOverrideEditing.GetOverride(_serialized, LogCategory.Audio));
            Assert.AreEqual(1, LogOverrideEditing.GetOverrideCount(_serialized));

            LogOverrideEditing.SetOverride(_serialized, LogCategory.Audio, null);
            Assert.IsNull(LogOverrideEditing.GetOverride(_serialized, LogCategory.Audio));
            Assert.AreEqual(0, LogOverrideEditing.GetOverrideCount(_serialized));
        }

        [Test]
        public void SetOverride_AppliedProperties_ReachTheAsset()
        {
            LogOverrideEditing.SetOverride(_serialized, LogCategory.Transport, LogLevel.Trace);
            _serialized.ApplyModifiedProperties();

            Assert.AreEqual(LogLevel.Trace, _settings.GetLogLevel(LogCategory.Transport));
            Assert.AreEqual(_settings.GlobalLogLevel, _settings.GetLogLevel(LogCategory.Audio));
        }

        [Test]
        public void GetOverridesSnapshot_ReturnsAllConfiguredOverrides()
        {
            LogOverrideEditing.SetOverride(_serialized, LogCategory.Audio, LogLevel.Debug);
            LogOverrideEditing.SetOverride(_serialized, LogCategory.REST, LogLevel.Off);

            var snapshot = LogOverrideEditing.GetOverridesSnapshot(_serialized);

            Assert.AreEqual(2, snapshot.Count);
            Assert.AreEqual(LogLevel.Debug, snapshot[LogCategory.Audio]);
            Assert.AreEqual(LogLevel.Off, snapshot[LogCategory.REST]);
        }

        [Test]
        public void ApplyPreset_SetsGlobals_AndClearsOverrides()
        {
            LogOverrideEditing.SetOverride(_serialized, LogCategory.Audio, LogLevel.Debug);

            LogOverrideEditing.ApplyPreset(_serialized, LogLevel.Error, false, false);
            _serialized.ApplyModifiedProperties();

            Assert.AreEqual(LogLevel.Error, _settings.GlobalLogLevel);
            Assert.IsFalse(_settings.IncludeStackTraces);
            Assert.IsFalse(_settings.ColoredOutput);
            Assert.AreEqual(0, _settings.CategoryOverrides.Count);
        }

        [Test]
        public void ResetToDefaults_RestoresSdkDefaults()
        {
            LogOverrideEditing.ApplyPreset(_serialized, LogLevel.Trace, false, false);
            LogOverrideEditing.SetOverride(_serialized, LogCategory.UI, LogLevel.Off);

            LogOverrideEditing.ResetToDefaults(_serialized);
            _serialized.ApplyModifiedProperties();

            Assert.AreEqual(LogLevel.Info, _settings.GlobalLogLevel);
            Assert.IsTrue(_settings.IncludeStackTraces);
            Assert.IsTrue(_settings.ColoredOutput);
            Assert.AreEqual(0, _settings.CategoryOverrides.Count);
        }
    }
}
