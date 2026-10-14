using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Shared.Interfaces;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    public sealed class ConvaiCharacterVersionReferenceTests
    {
        private const string InlineId = "00000000-0000-4000-8000-000000000001";
        private const string ProfileId = "00000000-0000-4000-8000-000000000002";
        private readonly List<Object> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object value in _createdObjects)
                if (value != null)
                    Object.DestroyImmediate(value);
            _createdObjects.Clear();
        }

        [Test]
        public void Mode_LiveIsSerializedDefault_AndSpecificValueIsPreserved()
        {
            Assert.That((int)ConvaiCharacterVersionMode.Live, Is.Zero);
            Assert.That((int)ConvaiCharacterVersionMode.Specific, Is.EqualTo(1));
#pragma warning disable CS0618
            Assert.That(ConvaiCharacterVersionMode.Latest, Is.EqualTo(ConvaiCharacterVersionMode.Live));
#pragma warning restore CS0618
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("draft")]
        [TestCase("1.0")]
        public void Live_UsesBareIdWithoutDiscovery_AndIgnoresVersionLabel(string version)
        {
            Assert.That(ConvaiCharacterVersionReference.Create(InlineId, ConvaiCharacterVersionMode.Live, version),
                Is.EqualTo(InlineId));
        }

        [Test]
        public void Live_PreservesExactApiCurrentReferenceIncludingCasing()
        {
            const string id = "ABCDEF01-2345-4678-9ABC-0123456789AB";
            string current = id.ToLowerInvariant();
            Assert.That(ConvaiCharacterVersionReference.Create(id, ConvaiCharacterVersionMode.Live, null, current),
                Is.EqualTo(current));
        }

        [TestCase(InlineId + "-1.2")]
        [TestCase(InlineId + "-latest")]
        [TestCase(InlineId + "-draft")]
        [TestCase(ProfileId)]
        public void Live_IgnoresStaleSelectionMetadata(string staleReference)
        {
            Assert.That(ConvaiCharacterVersionReference.Create(InlineId, ConvaiCharacterVersionMode.Live, "1.2",
                staleReference), Is.EqualTo(InlineId));
        }

        [TestCase("1.0")]
        [TestCase("1.0.0")]
        [TestCase("12.34.56")]
        [TestCase("future-release")]
        public void Specific_PreservesExactApiReference_WithoutInferringItFromDisplayLabel(string suffix)
        {
            string selectedReference = InlineId + "-" + suffix;
            bool success = ConvaiCharacterVersionReference.TryCreate(InlineId, ConvaiCharacterVersionMode.Specific,
                "display label", out string reference, out string error, selectedReference);
            Assert.That(success, Is.True);
            Assert.That(reference, Is.EqualTo(selectedReference));
            Assert.That(error, Is.Empty);
        }

        [Test]
        public void Specific_PreservesApiUuidCasing()
        {
            const string id = "ABCDEF01-2345-4678-9ABC-0123456789AB";
            string selectedReference = id.ToLowerInvariant() + "-1.0";
            Assert.That(ConvaiCharacterVersionReference.Create(id, ConvaiCharacterVersionMode.Specific, "1.0",
                selectedReference), Is.EqualTo(selectedReference));
        }

        [TestCase(null)]
        [TestCase("")]
        public void Specific_OldSavedLabelRequiresReselection_NeverConstructsSuffixOrFallsBack(string savedReference)
        {
            Assert.That(ConvaiCharacterVersionReference.TryCreate(InlineId, ConvaiCharacterVersionMode.Specific,
                "1.2", out string reference, out string error, savedReference), Is.False);
            Assert.That(reference, Is.Empty);
            Assert.That(error, Does.Contain("reselect"));
            Assert.Throws<ArgumentException>(() => ConvaiCharacterVersionReference.Create(InlineId,
                ConvaiCharacterVersionMode.Specific, "1.2", savedReference));
        }

        [TestCase(InlineId)]
        [TestCase(InlineId + "-")]
        [TestCase(InlineId + "-draft")]
        [TestCase(InlineId + "-LATEST")]
        [TestCase(InlineId + "-live")]
        [TestCase(InlineId + "-default")]
        [TestCase(ProfileId + "-1.0")]
        [TestCase(" " + InlineId + "-1.0")]
        [TestCase(InlineId + "-1.0 ")]
        [TestCase(InlineId + "-1.0\\n")]
        [TestCase(InlineId + "-1.0\n")]
        [TestCase(InlineId + "-1.0\0")]
        [TestCase(InlineId + "-1/0")]
        [TestCase(InlineId + "-1?0")]
        [TestCase(InlineId + "-1#0")]
        public void Specific_RejectsUnsafeAliasesMalformedOrOtherCharacterReferences(string selectedReference)
        {
            Assert.That(ConvaiCharacterVersionReference.TryCreate(InlineId, ConvaiCharacterVersionMode.Specific,
                "1.0", out string reference, out string error, selectedReference), Is.False);
            Assert.That(reference, Is.Empty);
            Assert.That(error, Is.Not.Empty);
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("character-id")]
        [TestCase("00000000000040008000000000000001")]
        [TestCase("{00000000-0000-4000-8000-000000000001}")]
        [TestCase(" " + InlineId)]
        [TestCase(InlineId + " ")]
        [TestCase(InlineId + "-latest")]
        [TestCase(InlineId + "-1.0")]
        public void Reference_RequiresBareUuidWithoutTrimmingOrSuffixes(string characterId)
        {
            Assert.That(ConvaiCharacterVersionReference.TryCreate(characterId, ConvaiCharacterVersionMode.Live,
                null, out string reference, out string error), Is.False);
            Assert.That(reference, Is.Empty);
            Assert.That(error, Does.Contain("Character ID"));
        }

        [Test]
        public void Reference_RejectsUnknownMode()
        {
            Assert.That(ConvaiCharacterVersionReference.TryCreate(InlineId, (ConvaiCharacterVersionMode)42,
                "1.0", out string reference, out string error, InlineId + "-1.0"), Is.False);
            Assert.That(reference, Is.Empty);
            Assert.That(error, Does.Contain("Live or Specific"));
        }

        [Test]
        public void ExistingSerializedComponentAndProfile_DefaultToLiveWithoutChangingIdentity()
        {
            ConvaiCharacter character = CreateCharacter();
            ConvaiCharacterProfile profile = CreateProfile();

            // Existing scene/prefab and asset data has no version fields.
            JsonUtility.FromJsonOverwrite("{\"_characterId\":\"" + InlineId + "\"}", character);
            JsonUtility.FromJsonOverwrite("{\"_characterId\":\"" + ProfileId + "\"}", profile);

            Assert.That(character.CharacterVersionMode, Is.EqualTo(ConvaiCharacterVersionMode.Live));
            Assert.That(character.CharacterVersion, Is.Empty);
            Assert.That(character.CharacterReference, Is.EqualTo(InlineId));
            Assert.That(((ICharacterIdentitySource)character).CharacterId, Is.EqualTo(InlineId));
            Assert.That(((ICharacterVersionSource)character).CharacterReference, Is.EqualTo(InlineId));
            Assert.That(profile.CharacterVersionMode, Is.EqualTo(ConvaiCharacterVersionMode.Live));
            Assert.That(profile.CharacterVersion, Is.Empty);
            Assert.That(profile.CharacterId, Is.EqualTo(ProfileId));
            Assert.That(profile.CharacterReference, Is.EqualTo(ProfileId));
        }

        [TestCase(ConvaiCharacterVersionMode.Live, "invalid-stored-value", null)]
        [TestCase(ConvaiCharacterVersionMode.Specific, " 1.2 ", "1.2")]
        public void InlineIdentity_OwnsVersionSelection(ConvaiCharacterVersionMode mode, string version, string selector)
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", mode);
            SetField(character, "_characterVersion", version);
            SetField(character, "_characterVersionReference", selector == null ? InlineId : InlineId + "-" + selector);

            Assert.That(character.CharacterId, Is.EqualTo(InlineId));
            Assert.That(character.CharacterVersionMode, Is.EqualTo(mode));
            Assert.That(character.CharacterVersion, Is.EqualTo(version));
            Assert.That(character.CharacterReference, Is.EqualTo(selector == null ? InlineId : InlineId + "-" + selector));
        }

        [TestCase(ConvaiCharacterVersionMode.Live, "ignored", null)]
        [TestCase(ConvaiCharacterVersionMode.Specific, "3.4.5", "3.4.5")]
        public void ProfileIdentity_OwnsVersionSelection_WithLegacyAssetSourceInitialization(
            ConvaiCharacterVersionMode mode, string version, string selector)
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "1.0");
            ConvaiCharacterProfile profile = CreateProfile();
            SetField(profile, "_characterId", ProfileId);
            SetField(profile, "_characterVersionMode", mode);
            SetField(profile, "_characterVersion", version);
            SetField(profile, "_characterVersionReference", selector == null ? ProfileId : ProfileId + "-" + selector);
            SetField(character, "_characterConfigAsset", profile);
            SetField(character, "_configurationSourceInitialized", false);

            Assert.That(character.ConfigurationSource, Is.EqualTo(ConvaiConfigSourceMode.Asset));
            Assert.That(character.CharacterId, Is.EqualTo(ProfileId));
            Assert.That(character.CharacterVersionMode, Is.EqualTo(mode));
            Assert.That(character.CharacterVersion, Is.EqualTo(version));
            Assert.That(character.CharacterReference, Is.EqualTo(selector == null ? ProfileId : ProfileId + "-" + selector));
        }

        [Test]
        public void ExplicitInlineMode_IgnoresAssignedProfileVersion()
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "1.2");
            SetField(character, "_characterVersionReference", InlineId + "-1.2");
            ConvaiCharacterProfile profile = CreateProfile();
            SetField(profile, "_characterId", ProfileId);
            SetField(profile, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(profile, "_characterVersion", "9.9");
            SetField(profile, "_characterVersionReference", ProfileId + "-9.9");
            SetField(character, "_characterConfigAsset", profile);
            SetField(character, "_configurationSource", ConvaiConfigSourceMode.Inline);
            SetField(character, "_configurationSourceInitialized", true);

            Assert.That(character.CharacterId, Is.EqualTo(InlineId));
            Assert.That(character.CharacterVersion, Is.EqualTo("1.2"));
            Assert.That(character.CharacterReference, Is.EqualTo(InlineId + "-1.2"));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase(" ")]
        public void BlankProfileIdentity_FallsBackToInlineIdentityAndVersionTogether(string profileId)
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "1.2");
            SetField(character, "_characterVersionReference", InlineId + "-1.2");
            ConvaiCharacterProfile profile = CreateProfile();
            SetField(profile, "_characterId", profileId);
            SetField(profile, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(profile, "_characterVersion", "9.9");
            SetField(profile, "_characterVersionReference", ProfileId + "-9.9");
            SetField(character, "_characterConfigAsset", profile);
            SetField(character, "_configurationSource", ConvaiConfigSourceMode.Asset);
            SetField(character, "_configurationSourceInitialized", true);

            Assert.That(character.CharacterId, Is.EqualTo(InlineId));
            Assert.That(character.CharacterVersionMode, Is.EqualTo(ConvaiCharacterVersionMode.Specific));
            Assert.That(character.CharacterVersion, Is.EqualTo("1.2"));
            Assert.That(character.CharacterReference, Is.EqualTo(InlineId + "-1.2"));
        }

        [Test]
        public void ChangingVersionMode_DoesNotRewriteIdentityOrStoredSpecificVersion()
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersion", "1.2");
            SetField(character, "_characterVersionReference", InlineId + "-1.2");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            Assert.That(character.CharacterReference, Is.EqualTo(InlineId + "-1.2"));

            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Live);

            Assert.That(character.CharacterReference, Is.EqualTo(InlineId));
            Assert.That(character.CharacterId, Is.EqualTo(InlineId));
            Assert.That(character.CharacterVersion, Is.EqualTo("1.2"));
        }

        [Test]
        public void OlderSerializedSpecificSelection_IsPreservedButRequiresApiReference()
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "1.2");
            Assert.That(character.SelectedCharacterReference, Is.Empty);
            Assert.That(character.CharacterVersion, Is.EqualTo("1.2"));
            Assert.Throws<ArgumentException>(() => _ = character.CharacterReference);
        }

        [Test]
        public void ChangingCharacterId_RejectsSavedPinRatherThanConstructingNewReference()
        {
            ConvaiCharacter character = CreateCharacter();
            character.Configure(InlineId, "Inline");
            SetField(character, "_characterVersionMode", ConvaiCharacterVersionMode.Specific);
            SetField(character, "_characterVersion", "1.2");
            SetField(character, "_characterVersionReference", InlineId + "-1.2");
            character.Configure(ProfileId, "Changed");
            Assert.Throws<ArgumentException>(() => _ = character.CharacterReference);
        }

        private ConvaiCharacter CreateCharacter()
        {
            var go = new GameObject("Versioned Character");
            go.SetActive(false);
            _createdObjects.Add(go);
            return go.AddComponent<ConvaiCharacter>();
        }

        private ConvaiCharacterProfile CreateProfile()
        {
            var profile = ScriptableObject.CreateInstance<ConvaiCharacterProfile>();
            _createdObjects.Add(profile);
            return profile;
        }

        private static void SetField(object target, string name, object value)
        {
            FieldInfo field = target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, name);
            field.SetValue(target, value);
        }
    }
}
