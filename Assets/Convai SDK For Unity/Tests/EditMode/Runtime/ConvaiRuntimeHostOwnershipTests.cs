using System.Collections.Generic;
using Convai.Editor.Inspectors;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.Core.Composition;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class ConvaiRuntimeHostOwnershipTests
    {
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _createdObjects.Count; i++)
                if (_createdObjects[i] != null)
                    Object.DestroyImmediate(_createdObjects[i]);
            _createdObjects.Clear();
        }

        [Test]
        public void CaptureConnectableCharacters_ExcludesInactiveAndDisabledCharactersFromStartupRoster()
        {
            ConvaiCharacter active = CreateCharacter("active");
            ConvaiCharacter disabled = CreateCharacter("disabled");
            disabled.enabled = false;
            ConvaiCharacter inactive = CreateCharacter("inactive");
            inactive.gameObject.SetActive(false);

            List<IConvaiCharacterAgent> result = ConvaiRuntimeHost.CaptureConnectableCharacters(
                new[] { active, disabled, inactive });

            Assert.That(result, Is.EqualTo(new IConvaiCharacterAgent[] { active }));
        }

        [Test]
        public void CaptureConnectableCharacters_DefaultSelectionIncludesEveryActiveCharacter()
        {
            ConvaiCharacter first = CreateCharacter("first");
            ConvaiCharacter second = CreateCharacter("second");

            List<IConvaiCharacterAgent> result = ConvaiRuntimeHost.CaptureConnectableCharacters(
                new[] { first, second });

            Assert.That(result, Is.EqualTo(new IConvaiCharacterAgent[] { first, second }));
        }

        [Test]
        public void CaptureConnectableCharacters_ExplicitSelectionIncludesOnlyChosenFiveOfTen()
        {
            var characters = new List<ConvaiCharacter>();
            for (int i = 0; i < 10; i++)
                characters.Add(CreateCharacter($"character {i + 1}"));

            List<ConvaiCharacter> included = characters.GetRange(0, 5);
            List<IConvaiCharacterAgent> result = ConvaiRuntimeHost.CaptureConnectableCharacters(
                characters,
                true,
                included);

            Assert.That(result, Is.EqualTo(included));
        }

        [Test]
        public void CaptureConnectableCharacters_SelectedInactiveCharacterStillDoesNotJoinStartupRoster()
        {
            ConvaiCharacter active = CreateCharacter("active");
            ConvaiCharacter inactive = CreateCharacter("inactive");
            inactive.gameObject.SetActive(false);

            List<IConvaiCharacterAgent> result = ConvaiRuntimeHost.CaptureConnectableCharacters(
                new[] { active, inactive },
                true,
                new[] { active, inactive });

            Assert.That(result, Is.EqualTo(new IConvaiCharacterAgent[] { active }));
        }

        [Test]
        public void ResolveOwnedCharacters_DefaultSceneDiscoversCharacterAddedAfterOriginalBinding()
        {
            ConvaiCharacter original = CreateCharacter("original");
            ConvaiCharacter added = CreateCharacter("added");

            IReadOnlyList<ConvaiCharacter> result = ConvaiRuntimeHost.ResolveOwnedCharacters(
                null,
                new[] { original },
                original,
                new[] { original, added });

            Assert.That(result, Is.EqualTo(new[] { original, added }));
        }

        [Test]
        public void ResolveOwnedCharacters_ConfiguredSceneInstallerRemainsOwnershipBoundary()
        {
            ConvaiCharacter owned = CreateCharacter("owned");
            ConvaiCharacter outsideInstaller = CreateCharacter("outside installer");
            var installerObject = new GameObject("installer");
            _createdObjects.Add(installerObject);
            ConvaiSceneInstaller installer = installerObject.AddComponent<ConvaiSceneInstaller>();
            var serialized = new SerializedObject(installer);
            SerializedProperty characters = serialized.FindProperty("_characters");
            characters.arraySize = 1;
            characters.GetArrayElementAtIndex(0).objectReferenceValue = owned;
            serialized.ApplyModifiedPropertiesWithoutUndo();

            IReadOnlyList<ConvaiCharacter> result = ConvaiRuntimeHost.ResolveOwnedCharacters(
                installer,
                System.Array.Empty<ConvaiCharacter>(),
                owned,
                new[] { owned, outsideInstaller });

            Assert.That(result, Is.EqualTo(new[] { owned }));
        }

        [Test]
        public void ManagerInspector_PlayerResolutionHonorsExplicitBindingWhenSeveralPlayersExist()
        {
            ConvaiPlayer explicitPlayer = CreatePlayer("explicit");
            CreatePlayer("other");

            Assert.That(ConvaiManagerEditor.CanResolvePlayer(2, explicitPlayer), Is.True);
            Assert.That(ConvaiManagerEditor.CanResolvePlayer(2, null), Is.False);
        }

        [Test]
        public void ResolveDefaultInitialCharacter_TakesTheFirstCharacterInSceneOrder()
        {
            ConvaiCharacter first = CreateCharacter("first");
            ConvaiCharacter second = CreateCharacter("second");

            // Reversed input: the rule must read scene order, not the caller's list order, because
            // Edit Mode discovery and runtime discovery hand it the characters in different orders.
            ConvaiCharacter resolved = ConvaiRuntimeHost.ResolveDefaultInitialCharacter(
                new[] { second, first }, false, null);

            Assert.That(resolved, Is.SameAs(first));
        }

        [Test]
        public void ResolveDefaultInitialCharacter_SkipsCharactersThatCannotJoinTheRoom()
        {
            ConvaiCharacter inactive = CreateCharacter("first");
            inactive.gameObject.SetActive(false);
            ConvaiCharacter excluded = CreateCharacter("second");
            ConvaiCharacter joinable = CreateCharacter("third");

            ConvaiCharacter resolved = ConvaiRuntimeHost.ResolveDefaultInitialCharacter(
                new[] { inactive, excluded, joinable },
                true,
                new[] { joinable });

            Assert.That(resolved, Is.SameAs(joinable));
        }

        [Test]
        public void ResolveDefaultInitialCharacter_ReturnsNullWhenNoCharacterCanJoin()
        {
            ConvaiCharacter inactive = CreateCharacter("only");
            inactive.gameObject.SetActive(false);

            Assert.That(
                ConvaiRuntimeHost.ResolveDefaultInitialCharacter(new[] { inactive }, false, null),
                Is.Null);
        }

        private ConvaiCharacter CreateCharacter(string name)
        {
            var gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            return gameObject.AddComponent<ConvaiCharacter>();
        }

        private ConvaiPlayer CreatePlayer(string name)
        {
            var gameObject = new GameObject(name);
            _createdObjects.Add(gameObject);
            return gameObject.AddComponent<ConvaiPlayer>();
        }
    }
}
