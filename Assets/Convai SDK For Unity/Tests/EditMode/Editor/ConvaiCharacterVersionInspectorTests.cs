using System.Collections.Generic;
using Convai.Editor.Inspectors;
using Convai.Runtime;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiCharacterVersionInspectorTests
    {
        private readonly List<Scene> _temporaryScenes = new();
        private readonly List<GameObject> _temporaryCharacters = new();
        private Scene _testScene;

        [SetUp]
        public void SetUp()
        {
            // Unity's EditMode runner supplies an empty scene and restores the user's scenes.
            // A second regular scene cannot be opened while that runner scene is untitled.
            _testScene = SceneManager.GetActiveScene();
            Assert.That(_testScene.IsValid() && _testScene.isLoaded, Is.True);
            foreach (GameObject root in _testScene.GetRootGameObjects())
                Assert.That(root.GetComponentsInChildren<ConvaiCharacter>(true), Is.Empty,
                    "Scene-count tests require the isolated scene supplied by the EditMode runner.");
        }

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject character in _temporaryCharacters)
                if (character != null) Object.DestroyImmediate(character);
            _temporaryCharacters.Clear();

            // Only preview scenes created by this fixture are closed; the runner owns its scene.
            for (int i = _temporaryScenes.Count - 1; i >= 0; i--)
            {
                Scene scene = _temporaryScenes[i];
                if (!scene.IsValid() || !scene.isLoaded) continue;
                EditorSceneManager.ClosePreviewScene(scene);
            }
            _temporaryScenes.Clear();
        }

        [Test]
        public void VersionSelector_DefaultOffersLiveWithoutManualSpecificEntry()
        {
            ConvaiCharacterVersionEditorFields.VersionOption[] options =
                ConvaiCharacterVersionEditorFields.BuildOptions(ConvaiCharacterVersionMode.Live,
                    string.Empty, null, "00000000-0000-4000-8000-000000000001",
                    new CharacterReleasedVersion[0], hasLoaded: false);

            Assert.That(options.Length, Is.EqualTo(1));
            Assert.That(options[0].Label, Is.EqualTo("Live"));
            Assert.That(options[0].Version, Is.Null);
            Assert.That(options[0].Selected, Is.True);
            Assert.That(options[0].Enabled, Is.True);
        }

        [Test]
        public void MultiCharacterWarning_NullCharacter_IsFalse()
        {
            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(null), Is.False);
        }

        [Test]
        public void MultiCharacterWarning_SingleCharacter_IsFalse()
        {
            ConvaiCharacter character = CreateSceneCharacter(_testScene);

            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(character), Is.False);
        }

        [TestCase(true, true)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        public void MultiCharacterWarning_CountsActiveInactiveAndDisabledCharactersInSameScene(
            bool secondObjectActive, bool secondComponentEnabled)
        {
            ConvaiCharacter first = CreateSceneCharacter(_testScene);
            ConvaiCharacter second = CreateSceneCharacter(_testScene, secondObjectActive, secondComponentEnabled);
            second.transform.SetParent(first.transform);

            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(first), Is.True);
            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(second), Is.True);
        }

        [Test]
        public void MultiCharacterWarning_DoesNotCountCharactersInOtherScene()
        {
            ConvaiCharacter first = CreateSceneCharacter(_testScene);
            Scene otherScene = CreatePreviewScene();
            ConvaiCharacter second = CreateSceneCharacter(otherScene);

            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(first), Is.False);
            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(second), Is.False);
        }

        [Test]
        public void MultiCharacterWarning_PreviewScene_IsExcludedEvenWithMultipleCharacters()
        {
            Scene previewScene = CreatePreviewScene();
            ConvaiCharacter first = CreateSceneCharacter(previewScene);
            ConvaiCharacter second = CreateSceneCharacter(previewScene);

            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(first), Is.False);
            Assert.That(ConvaiCharacterVersionEditorFields.HasMultipleCharactersInScene(second), Is.False);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void CharacterAndProfileInspectors_EditSerializedVersionWithoutChangingBaseIdentity(bool useProfile)
        {
            const string characterId = "00000000-0000-4000-8000-000000000001";
            GameObject characterObject = null;
            Object target;
            if (useProfile)
                target = ScriptableObject.CreateInstance<ConvaiCharacterProfile>();
            else
            {
                characterObject = new GameObject("Version Inspector Test");
                characterObject.SetActive(false);
                target = characterObject.AddComponent<ConvaiCharacter>();
            }

            UnityEditor.Editor editor = null;
            try
            {
                editor = UnityEditor.Editor.CreateEditor(target);
                Assert.That(editor.GetType(), Is.EqualTo(useProfile
                    ? typeof(ConvaiCharacterProfileEditor)
                    : typeof(ConvaiCharacterEditor)));
                SerializedObject serialized = editor.serializedObject;
                serialized.Update();
                SerializedProperty id = serialized.FindProperty("_characterId");
                SerializedProperty mode = serialized.FindProperty("_characterVersionMode");
                SerializedProperty version = serialized.FindProperty("_characterVersion");
                SerializedProperty reference = serialized.FindProperty("_characterVersionReference");

                Assert.That(id, Is.Not.Null);
                Assert.That(mode, Is.Not.Null);
                Assert.That(version, Is.Not.Null);
                Assert.That(reference, Is.Not.Null);
                Assert.That(mode.propertyType, Is.EqualTo(SerializedPropertyType.Enum));
                Assert.That(mode.enumNames, Does.Contain("Live"));
                Assert.That(mode.enumNames, Does.Contain("Specific"));
                Assert.That(version.propertyType, Is.EqualTo(SerializedPropertyType.String));
                Assert.That(reference.propertyType, Is.EqualTo(SerializedPropertyType.String));
                id.stringValue = characterId;
                mode.intValue = (int)ConvaiCharacterVersionMode.Specific;
                version.stringValue = "1.2.3";
                reference.stringValue = characterId + "-v7";
                serialized.ApplyModifiedPropertiesWithoutUndo();

                if (target is ConvaiCharacterProfile profile)
                {
                    Assert.That(profile.CharacterId, Is.EqualTo(characterId));
                    Assert.That(profile.SelectedCharacterReference, Is.EqualTo(characterId + "-v7"));
                    Assert.That(profile.CharacterReference, Is.EqualTo(characterId + "-v7"));
                }
                else
                {
                    var character = (ConvaiCharacter)target;
                    Assert.That(character.CharacterId, Is.EqualTo(characterId));
                    Assert.That(character.SelectedCharacterReference, Is.EqualTo(characterId + "-v7"));
                    Assert.That(character.CharacterReference, Is.EqualTo(characterId + "-v7"));
                }
            }
            finally
            {
                if (editor != null) Object.DestroyImmediate(editor);
                if (characterObject != null) Object.DestroyImmediate(characterObject);
                else Object.DestroyImmediate(target);
            }
        }

        private Scene CreatePreviewScene()
        {
            Scene scene = EditorSceneManager.NewPreviewScene();
            _temporaryScenes.Add(scene);
            return scene;
        }

        private ConvaiCharacter CreateSceneCharacter(Scene scene, bool active = true, bool enabled = true)
        {
            // The EditMode runner's isolated scene is active, never the user's scene.
            var characterObject = new GameObject("Version Scene Count Test");
            _temporaryCharacters.Add(characterObject);
            characterObject.SetActive(false);
            SceneManager.MoveGameObjectToScene(characterObject, scene);
            ConvaiCharacter character = characterObject.AddComponent<ConvaiCharacter>();
            character.enabled = enabled;
            characterObject.SetActive(active);
            return character;
        }
    }
}
