using System;
using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers the Scene Knowledge entry-to-scene link: how an entry's state is classified, what
    ///     the validator says about each state, and — the defect this all exists for — that an
    ///     authored entry with no object of its own now takes one from the same-named scene
    ///     <see cref="ConvaiActionTarget" /> instead of shadowing it.
    /// </summary>
    [TestFixture]
    public class ConvaiSceneKnowledgeLinkTests
    {
        private const string Prefix = "ConvaiSceneKnowledgeLinkTests_";

        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    UnityEngine.Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }

        private GameObject NewObject(string name)
        {
            var gameObject = new GameObject(Prefix + name);
            _spawned.Add(gameObject);
            return gameObject;
        }

        // ── Classification ─────────────────────────────────────────────────────

        [Test]
        public void Classify_EntryWithAnObject_IsLinked()
        {
            var entry = new ConvaiActionObjectDefinition { Name = "Lantern", GameObjectReference = NewObject("Lantern") };

            Assert.AreEqual(
                ConvaiKnownEntryLinkState.Linked,
                ConvaiSceneKnowledgeLinkModel.ClassifyObject(entry, Array.Empty<ConvaiActionTarget>()));
        }

        [Test]
        public void Classify_EntryWithNothing_IsUnlinked()
        {
            var entry = new ConvaiActionObjectDefinition { Name = "Lantern" };

            Assert.AreEqual(
                ConvaiKnownEntryLinkState.Unlinked,
                ConvaiSceneKnowledgeLinkModel.ClassifyObject(entry, Array.Empty<ConvaiActionTarget>()));
        }

        [Test]
        public void Classify_TextOnlyEntry_IsFinishedNotBroken()
        {
            var entry = new ConvaiActionObjectDefinition { Name = "The Old Harbour", TextOnly = true };

            Assert.AreEqual(
                ConvaiKnownEntryLinkState.TextOnly,
                ConvaiSceneKnowledgeLinkModel.ClassifyObject(entry, Array.Empty<ConvaiActionTarget>()));
        }

        [Test]
        public void Classify_SceneTargetAnswersToTheName_IsAnsweredByTarget()
        {
            GameObject lantern = NewObject("Lantern");
            ConvaiActionTarget target = lantern.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";

            var entry = new ConvaiActionObjectDefinition { Name = "lantern" };

            Assert.AreEqual(
                ConvaiKnownEntryLinkState.AnsweredByTarget,
                ConvaiSceneKnowledgeLinkModel.ClassifyObject(entry, new[] { target }));
        }

        [Test]
        public void FindTargetByName_IgnoresTheOtherKind()
        {
            GameObject guard = NewObject("Guard");
            ConvaiActionTarget target = guard.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Guard";
            target.Kind = ConvaiActionTargetKind.Character;

            Assert.IsNull(ConvaiSceneKnowledgeLinkModel.FindTargetByName(
                "Guard", ConvaiActionTargetKind.Object, new[] { target }));
            Assert.IsNotNull(ConvaiSceneKnowledgeLinkModel.FindTargetByName(
                "Guard", ConvaiActionTargetKind.Character, new[] { target }));
        }

        [Test]
        public void FindObjectsByName_MatchesExactlyAndCaseInsensitively()
        {
            GameObject first = NewObject("Lantern");
            GameObject second = NewObject("Lantern");
            first.name = "Lantern";
            second.name = "lantern";
            GameObject other = NewObject("Torch");
            other.name = "Torch";

            List<GameObject> matches = ConvaiSceneKnowledgeLinkModel.FindObjectsByName(
                "Lantern", new[] { first, second, other });

            Assert.AreEqual(2, matches.Count);
            CollectionAssert.DoesNotContain(matches, other);
        }

        [Test]
        public void NameDiffersFromObject_OnlyWhenBothExistAndDisagree()
        {
            GameObject lantern = NewObject("Lantern");
            lantern.name = "Lantern";

            Assert.IsFalse(ConvaiSceneKnowledgeLinkModel.NameDiffersFromObject("lantern", lantern));
            Assert.IsFalse(ConvaiSceneKnowledgeLinkModel.NameDiffersFromObject("Anything", null));
            Assert.IsTrue(ConvaiSceneKnowledgeLinkModel.NameDiffersFromObject("Old Lamp", lantern));
        }

        // ── Validation ─────────────────────────────────────────────────────────

        [Test]
        public void Validate_UnlinkedEntry_IsAnError()
        {
            ConvaiActionConfigSource source = NewSource();
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "Lantern", Description = "A lantern." }
            });

            Assert.IsTrue(HasErrorMentioning(source, "Lantern"));
        }

        [Test]
        public void Validate_TextOnlyEntry_IsNotAnError()
        {
            ConvaiActionConfigSource source = NewSource();
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "The Old Harbour", Description = "Two streets away.", TextOnly = true }
            });

            Assert.IsFalse(HasErrorMentioning(source, "The Old Harbour"));
        }

        [Test]
        public void Validate_LinkedEntry_IsNotAnError()
        {
            ConvaiActionConfigSource source = NewSource();
            source.ReplaceObjects(new List<ConvaiActionObjectDefinition>
            {
                new() { Name = "Lantern", Description = "A lantern.", GameObjectReference = NewObject("Lantern") }
            });

            Assert.IsFalse(HasErrorMentioning(source, "Lantern"));
        }

        private ConvaiActionConfigSource NewSource() =>
            NewObject("Source").AddComponent<ConvaiActionConfigSource>();

        private static bool HasErrorMentioning(ConvaiActionConfigSource source, string entryName)
        {
            IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics = ConvaiActionConfigValidator.Validate(source);
            for (int i = 0; i < diagnostics.Count; i++)
            {
                if (diagnostics[i].Severity == ConvaiActionConfigDiagnosticSeverity.Error &&
                    diagnostics[i].Message.Contains(entryName))
                    return true;
            }

            return false;
        }

        // ── The merge: an entry no longer shadows the object that would work ───

        [Test]
        public void MergedConfig_UnlinkedEntry_TakesTheObjectFromTheSameNamedSceneTarget()
        {
            (_, ConvaiCharacter character) = CreateCharacter();
            character.GetComponent<ConvaiActionConfigSource>().ReplaceObjects(
                new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Lantern", Description = "The lantern by the door." }
                });

            GameObject lantern = NewObject("Lantern");
            lantern.name = "Lantern";
            ConvaiActionTarget target = lantern.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";
            target.Aliases = new List<string> { "lamp" };
            target.HandleEnable();

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();
            ConvaiActionObjectDefinition entry = FindObject(merged, "Lantern");

            Assert.IsNotNull(entry);
            Assert.AreSame(lantern, entry.GameObjectReference, "The entry must take the scene target's object.");
            Assert.AreEqual("The lantern by the door.", entry.Description, "The authored text still wins.");
            CollectionAssert.Contains(entry.Aliases, "lamp");

            target.HandleDisable();
        }

        [Test]
        public void MergedConfig_TextOnlyEntry_IsLeftAlone()
        {
            (_, ConvaiCharacter character) = CreateCharacter();
            character.GetComponent<ConvaiActionConfigSource>().ReplaceObjects(
                new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Lantern", Description = "Only ever talked about.", TextOnly = true }
                });

            GameObject lantern = NewObject("Lantern");
            lantern.name = "Lantern";
            ConvaiActionTarget target = lantern.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";
            target.HandleEnable();

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();
            ConvaiActionObjectDefinition entry = FindObject(merged, "Lantern");

            Assert.IsNotNull(entry);
            Assert.IsNull(entry.GameObjectReference, "A text-only entry says there is nothing to act on.");

            target.HandleDisable();
        }

        [Test]
        public void MergedConfig_AlreadyLinkedEntry_KeepsItsOwnObject()
        {
            (_, ConvaiCharacter character) = CreateCharacter();
            GameObject authored = NewObject("AuthoredLantern");
            character.GetComponent<ConvaiActionConfigSource>().ReplaceObjects(
                new List<ConvaiActionObjectDefinition>
                {
                    new() { Name = "Lantern", Description = "The one I picked.", GameObjectReference = authored }
                });

            GameObject other = NewObject("OtherLantern");
            ConvaiActionTarget target = other.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";
            target.HandleEnable();

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();

            Assert.AreSame(authored, FindObject(merged, "Lantern")?.GameObjectReference,
                "A real collision keeps the author's decision.");

            target.HandleDisable();
        }

        private (GameObject GameObject, ConvaiCharacter Character) CreateCharacter()
        {
            GameObject gameObject = NewObject("Character_" + Guid.NewGuid().ToString("N"));
            ConvaiCharacter character = gameObject.AddComponent<ConvaiCharacter>();
            gameObject.AddComponent<ConvaiActionConfigSource>();
            return (gameObject, character);
        }

        private static ConvaiActionObjectDefinition FindObject(ConvaiActionConfig config, string name)
        {
            IReadOnlyList<ConvaiActionObjectDefinition> objects = config?.Objects;
            for (int i = 0; objects != null && i < objects.Count; i++)
            {
                if (objects[i] != null &&
                    string.Equals(objects[i].Name, name, StringComparison.OrdinalIgnoreCase))
                    return objects[i];
            }

            return null;
        }
    }
}
