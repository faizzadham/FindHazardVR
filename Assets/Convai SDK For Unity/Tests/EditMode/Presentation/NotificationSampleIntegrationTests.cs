using System.IO;
using Convai.Runtime.Presentation.Views.Notifications;
using Convai.Runtime.Presentation.Views.Settings;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Presentation
{
    [Category("Integration")]
    public class NotificationSampleIntegrationTests
    {
        private const string NotificationPrefabPath =
            "Packages/com.convai.convai-sdk-for-unity/Prefabs/Notifications/NotificationSystem.prefab";

        private const string NotificationGroupAssetPath =
            "Packages/com.convai.convai-sdk-for-unity/Resources/SONotificationGroup.asset";

        private const string BasicScenePath =
            "Packages/com.convai.convai-sdk-for-unity/Samples/BasicSample/Scenes/Basic Sample.unity";

        private const string NotificationPrefabGuid = "fdd25822ce484d44dae163a4072c590b";

        private const string SettingsPanelPrefabPath =
            "Packages/com.convai.convai-sdk-for-unity/Prefabs/SettingsPanel/SettingsPanel_Landscape.prefab";

        [Test]
        public void NotificationSystemPrefab_HasNotificationHandlerAndController()
        {
            var prefabAsset = AssetDatabase.LoadAssetAtPath<GameObject>(NotificationPrefabPath);
            Assert.IsNotNull(prefabAsset, "Notification system prefab should exist.");

            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(NotificationPrefabPath);
            try
            {
                var handler = prefabRoot.GetComponent<NotificationHandler>();
                var controller = prefabRoot.GetComponent<UINotificationController>();

                Assert.IsNotNull(handler, "NotificationSystem prefab should include NotificationHandler.");
                Assert.IsNotNull(controller, "NotificationSystem prefab should include UINotificationController.");

                var expectedGroup = AssetDatabase.LoadAssetAtPath<SONotificationGroup>(NotificationGroupAssetPath);
                Assert.IsNotNull(expectedGroup, "Shared notification group asset should exist.");

                var serializedHandler = new SerializedObject(handler);
                SerializedProperty groupProperty = serializedHandler.FindProperty("notificationGroup");
                AssertHasObjectReference(groupProperty, "notificationGroup");
                Assert.AreEqual(expectedGroup, groupProperty.objectReferenceValue,
                    "NotificationHandler should reference the shared SONotificationGroup asset.");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        [Test]
        public void BasicScene_ReferencesNotificationSystemPrefab()
        {
            string sceneContents = File.ReadAllText(BasicScenePath);
            StringAssert.Contains($"guid: {NotificationPrefabGuid}", sceneContents,
                "Basic scene should include NotificationSystem prefab instance.");
        }

        [Test]
        public void SettingsPanelPrefab_HasNotificationToggleAndControls()
        {
            GameObject prefabRoot = PrefabUtility.LoadPrefabContents(SettingsPanelPrefabPath);
            try
            {
                var panel = prefabRoot.GetComponentInChildren<SettingsPanel>(true);
                Assert.IsNotNull(panel, "Settings panel prefab should contain SettingsPanel component.");

                var serializedPanel = new SerializedObject(panel);
                AssertHasObjectReference(serializedPanel.FindProperty("notificationToggle"), "notificationToggle");
                AssertHasObjectReference(serializedPanel.FindProperty("saveButton"), "saveButton");
                AssertHasObjectReference(serializedPanel.FindProperty("closeButton"), "closeButton");
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(prefabRoot);
            }
        }

        private static void AssertHasObjectReference(SerializedProperty property, string propertyName)
        {
            Assert.IsNotNull(property, $"{propertyName} serialized property should exist.");
            Assert.IsNotNull(property.objectReferenceValue, $"{propertyName} should be assigned.");
        }
    }
}
