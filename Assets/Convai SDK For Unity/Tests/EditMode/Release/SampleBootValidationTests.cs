using System;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Modules.LipSync;
using Convai.Runtime.Adapters.Networking;
using Convai.Runtime.Components;
using Convai.Runtime.Configuration;
using Convai.Runtime.Presentation.Views.Notifications;
using Convai.Sample.UI.Settings;
using Convai.Samples;
using Convai.Shared.Compatibility;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Release
{
    [Category("Release")]
    public sealed class SampleBootValidationTests
    {
        private const string BasicSampleScenePath =
            "Packages/com.convai.convai-sdk-for-unity/Samples/BasicSample/Scenes/Basic Sample.unity";

        private const string LipSyncSampleScenePath =
            "Packages/com.convai.convai-sdk-for-unity/Samples/LipSyncSample/Scenes/LipSync Sample.unity";

        private const string LipSyncSampleVolumeProfilePath =
            "Packages/com.convai.convai-sdk-for-unity/Samples/LipSyncSample/Scenes/" +
            "LipSync Sample Global Volume Profile.asset";

        private const string MultiCharacterSampleScenePath =
            "Packages/com.convai.convai-sdk-for-unity/Samples/MultiCharacterSample/Scenes/MultiCharacterSample.unity";

        [TearDown]
        public void TearDown()
        {
            // Ensure subsequent tests do not inherit the sample scene state.
            EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        }

        // The Basic sample is the minimal conversation path: manager, room, player, character and
        // the notification UI. It deliberately ships without an Actions setup, so nothing here
        // asserts action config, dispatchers or behaviors.
        [Test]
        public void BasicSample_Loads_AndContainsCoreRuntimeObjects()
        {
            Scene scene = EditorSceneManager.OpenScene(BasicSampleScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), "Expected Basic sample scene to load.");

            ConvaiManager manager = Object.FindAnyObjectByType<ConvaiManager>(FindObjectsInactive.Include);
            Assert.IsNotNull(manager,
                "Basic sample should contain ConvaiManager.");
            Assert.IsNotNull(ResolveOrProvisionRoomManager(manager),
                "Basic sample should expose a ConvaiRoomManager path (serialized or manager-provisioned).");
            Assert.IsNotNull(Object.FindAnyObjectByType<ConvaiPlayer>(FindObjectsInactive.Include),
                "Basic sample should contain ConvaiPlayer.");

            ConvaiCharacter[] characters = ConvaiObjectFind.All<ConvaiCharacter>(FindObjectsInactive.Include);
            Assert.GreaterOrEqual(characters.Length, 1, "Basic sample should contain at least one ConvaiCharacter.");

            Assert.IsNotNull(Object.FindAnyObjectByType<NotificationHandler>(FindObjectsInactive.Include),
                "Basic sample should include the NotificationSystem prefab instance.");

            AssertNoEditorOnlyBehavioursInScene();
        }

        [Test]
        public void LipSyncSample_Loads_AndContainsLipSyncComponent()
        {
            Scene scene = EditorSceneManager.OpenScene(LipSyncSampleScenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), "Expected LipSync sample scene to load.");

            ConvaiManager manager = Object.FindAnyObjectByType<ConvaiManager>(FindObjectsInactive.Include);
            Assert.IsNotNull(manager,
                "LipSync sample should contain ConvaiManager.");
            Assert.IsNotNull(ResolveOrProvisionRoomManager(manager),
                "LipSync sample should expose a ConvaiRoomManager path (serialized or manager-provisioned).");

            ConvaiCharacter[] characters = ConvaiObjectFind.All<ConvaiCharacter>(FindObjectsInactive.Include);
            Assert.GreaterOrEqual(characters.Length, 1, "LipSync sample should contain at least one ConvaiCharacter.");

            Assert.IsNotNull(Object.FindAnyObjectByType<ConvaiLipSyncComponent>(FindObjectsInactive.Include),
                "LipSync sample should include at least one ConvaiLipSyncComponent.");

            AssertLipSyncBackgroundLightLayers();
            AssertNoEditorOnlyBehavioursInScene();
        }

        [TestCase("\n", TestName = "LipSyncSample_ColorAdjustmentsApplyAuthoredPostExposure_LF")]
        [TestCase("\r\n", TestName = "LipSyncSample_ColorAdjustmentsApplyAuthoredPostExposure_CRLF")]
        public void LipSyncSample_ColorAdjustmentsApplyAuthoredPostExposure(string lineEnding)
        {
            string profile = NormalizeLineEndings(File.ReadAllText(LipSyncSampleVolumeProfilePath))
                .Replace("\n", lineEnding);
            Assert.That(ColorAdjustmentsPostExposure.IsMatch(NormalizeLineEndings(profile)), Is.True,
                "The LipSync sample's authored post-exposure correction must be active and overridden.");
        }

        /// <summary>
        ///     The ColorAdjustments override block with post-exposure active and overridden. The
        ///     class identifier line is matched loosely on purpose: naming the render-pipeline
        ///     assembly here would make the test assembly own a render-pipeline dependency, which
        ///     <c>ArchitectureGuardTests</c> forbids outside the samples.
        /// </summary>
        private static readonly Regex ColorAdjustmentsPostExposure = new(
            "  m_Name: ColorAdjustments\n" +
            "  m_EditorClassIdentifier: [^\n]+\n" +
            "  active: 1\n" +
            "  postExposure:\n" +
            "    m_OverrideState: 1\n" +
            "    m_Value: 0\\.25\n",
            RegexOptions.Compiled);

        private static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n").Replace("\r", "\n");

        [TestCase(BasicSampleScenePath)]
        [TestCase(LipSyncSampleScenePath)]
        [TestCase(MultiCharacterSampleScenePath)]
        public void SampleScene_SettingsUiHasPointerAndShortcutInput(string scenePath)
        {
            Scene scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Single);
            Assert.IsTrue(scene.IsValid(), $"Expected sample scene to load: {scenePath}");

            EventSystem eventSystem = Object.FindAnyObjectByType<EventSystem>(FindObjectsInactive.Include);
            Assert.IsNotNull(eventSystem,
                "A visible settings control needs an EventSystem so clicks reach buttons and text fields.");
            Assert.IsNotNull(eventSystem.GetComponent<BaseInputModule>(),
                "The EventSystem needs an input module to route pointer and keyboard events.");

            SettingsHandler settingsHandler = Object.FindAnyObjectByType<SettingsHandler>(FindObjectsInactive.Include);
            Assert.IsNotNull(settingsHandler,
                $"The sample should spawn and bind the shared runtime settings panel: {scenePath}");
            Assert.IsNotNull(new SerializedObject(settingsHandler).FindProperty("settingsPanelPrefab")?.objectReferenceValue,
                "The settings handler should reference the shared settings panel prefab.");

            ConvaiSampleInteractionController shortcutController =
                Object.FindAnyObjectByType<ConvaiSampleInteractionController>(FindObjectsInactive.Include);
            Assert.IsNotNull(shortcutController,
                $"The sample should expose the shared F10 settings shortcut: {scenePath}");

            SerializedObject serializedShortcut = new(shortcutController);
            ConvaiKeyBindings keyBindings =
                serializedShortcut.FindProperty("keyBindings")?.objectReferenceValue as ConvaiKeyBindings;
            Assert.IsNotNull(keyBindings,
                "The settings shortcut should reference the shared key bindings.");
            Assert.That(keyBindings.OpenSettingsKey, Is.EqualTo(KeyCode.F10),
                "The shared settings shortcut should remain bound to F10.");
            Assert.AreSame(settingsHandler, serializedShortcut.FindProperty("settingsHandler")?.objectReferenceValue,
                "The settings shortcut should toggle the scene's shared settings handler.");
        }

        private static void AssertLipSyncBackgroundLightLayers()
        {
            Light[] lights = ConvaiObjectFind.All<Light>(FindObjectsInactive.Include);
            Light backgroundLight = Array.Find(lights, light => light != null && light.name == "Background Light");
            Assert.IsNotNull(backgroundLight, "LipSync sample should contain its isolated Background Light.");
            Assert.That(backgroundLight.renderingLayerMask, Is.EqualTo(2u),
                "Background Light must affect only the background rendering layer.");

            MonoBehaviour additionalLightData = Array.Find(
                backgroundLight.GetComponents<MonoBehaviour>(),
                behaviour => behaviour != null &&
                             behaviour.GetType().FullName ==
                             "UnityEngine.Rendering.Universal.UniversalAdditionalLightData");
            Assert.IsNotNull(additionalLightData,
                "Background Light should include Universal Additional Light Data.");

            SerializedObject serializedLightData = new(additionalLightData);
            AssertRenderingLayerMask(serializedLightData.FindProperty("m_RenderingLayers"),
                "URP 17.0 legacy rendering layers");

            SerializedProperty currentMask = serializedLightData.FindProperty("m_RenderingLayersMask");
            if (currentMask != null)
                AssertRenderingLayerMask(currentMask.FindPropertyRelative("m_Bits"),
                    "URP 17.4+ rendering layers");
        }

        private static void AssertRenderingLayerMask(SerializedProperty property, string label)
        {
            Assert.IsNotNull(property, $"Missing {label} serialization on Background Light.");
            Assert.That(property.longValue, Is.EqualTo(2L),
                $"{label} must stay aligned so the high-intensity background light cannot affect the character.");
        }

        private static void AssertNoEditorOnlyBehavioursInScene()
        {
            MonoBehaviour[] behaviours = ConvaiObjectFind.All<MonoBehaviour>(FindObjectsInactive.Include);

            for (int i = 0; i < behaviours.Length; i++)
            {
                MonoBehaviour behaviour = behaviours[i];
                if (behaviour == null) continue;

                Type type = behaviour.GetType();
                string assemblyName = type.Assembly.GetName().Name ?? string.Empty;
                if (assemblyName.EndsWith(".Editor", StringComparison.Ordinal))
                {
                    Assert.Fail(
                        $"Scene contains editor-only behaviour '{type.FullName}' from assembly '{assemblyName}'.");
                }

                string ns = type.Namespace ?? string.Empty;
                if (ns.Contains(".Editor", StringComparison.Ordinal))
                {
                    Assert.Fail($"Scene contains editor-only behaviour '{type.FullName}' (namespace '{ns}').");
                }
            }
        }

        private static ConvaiRoomManager ResolveOrProvisionRoomManager(ConvaiManager manager)
        {
            if (manager == null) return null;

            ConvaiRoomManager roomManager = Object.FindAnyObjectByType<ConvaiRoomManager>(FindObjectsInactive.Include);
            if (roomManager != null) return roomManager;

            MethodInfo ensureRoomManagerReference = typeof(ConvaiManager).GetMethod(
                "EnsureRoomManagerReference",
                BindingFlags.Instance | BindingFlags.NonPublic);
            ensureRoomManagerReference?.Invoke(manager, null);

            return manager.GetComponent<ConvaiRoomManager>()
                   ?? Object.FindAnyObjectByType<ConvaiRoomManager>(FindObjectsInactive.Include);
        }
    }
}
