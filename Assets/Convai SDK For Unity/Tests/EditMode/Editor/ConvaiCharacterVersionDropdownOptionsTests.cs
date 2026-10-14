using System;
using System.Linq;
using Convai.Editor.Inspectors;
using Convai.Runtime;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    public sealed class ConvaiCharacterVersionDropdownOptionsTests
    {
        private const string CharacterId = "00000000-0000-4000-8000-000000000001";
        private const string FirstReference = CharacterId + "-v7";
        private const string SecondReference = CharacterId + "-v8";

        [TestCase(false)]
        [TestCase(true)]
        public void Live_RemainsAvailableWithoutDiscovery_AndDoesNotUseOldReleaseMetadata(bool hasLoaded)
        {
            var options = Build(ConvaiCharacterVersionMode.Live, "ignored", FirstReference,
                Array.Empty<CharacterReleasedVersion>(), hasLoaded);

            Assert.That(options.Length, Is.EqualTo(1));
            Assert.That(options[0].Label, Is.EqualTo("Live"));
            Assert.That(options[0].Mode, Is.EqualTo(ConvaiCharacterVersionMode.Live));
            Assert.That(options[0].Version, Is.Null);
            Assert.That(options[0].Reference, Is.EqualTo(CharacterId));
            Assert.That(options[0].Enabled, Is.True);
            Assert.That(options[0].Selected, Is.True);
        }

        [Test]
        public void Live_UsesExactCurrentReferenceReturnedByApi()
        {
            const string current = "ABCDEF01-2345-4678-9ABC-0123456789AB";
            var options = ConvaiCharacterVersionEditorFields.BuildOptions(ConvaiCharacterVersionMode.Live,
                null, null, current, Array.Empty<CharacterReleasedVersion>(), hasLoaded: true);

            Assert.That(options[0].Reference, Is.EqualTo(current));
            Assert.That(options[0].Label, Is.EqualTo("Live"));
        }

        [Test]
        public void ReleasedChoices_UseApiReferencesRatherThanConcatenatedVersionLabels()
        {
            var options = Build(ConvaiCharacterVersionMode.Live, null, null,
                new[] { Release(FirstReference, "1.0"), Release(SecondReference, "1.0.0") }, hasLoaded: true);

            CollectionAssert.AreEqual(new[] { "Live", "1.0", "1.0.0" }, options.Select(option => option.Label));
            CollectionAssert.AreEqual(new[] { CharacterId, FirstReference, SecondReference },
                options.Select(option => option.Reference));
            CollectionAssert.AreEqual(new string[] { null, "1.0", "1.0.0" },
                options.Select(option => option.Version));
            Assert.That(options.All(option => option.Enabled), Is.True);
            Assert.That(options.Count(option => option.Selected), Is.EqualTo(1));
            Assert.That(options[0].Selected, Is.True);
        }

        [TestCase(FirstReference, "1.0")]
        [TestCase(SecondReference, "1.0.0")]
        public void SpecificPin_SelectsByExactReference_NotDisplayVersion(string reference, string expectedVersion)
        {
            var options = Build(ConvaiCharacterVersionMode.Specific, "stale saved label", reference,
                new[] { Release(FirstReference, "1.0"), Release(SecondReference, "1.0.0") }, hasLoaded: true);

            Assert.That(options.Count(option => option.Selected), Is.EqualTo(1));
            var selected = options.Single(option => option.Selected);
            Assert.That(selected.Reference, Is.EqualTo(reference));
            Assert.That(selected.Version, Is.EqualTo(expectedVersion));
            Assert.That(selected.Enabled, Is.True);
            Assert.That(options[0].Selected, Is.False);
        }

        [Test]
        public void LatestReleaseFlag_AnnotatesLabel_WithoutSelectingItOrChangingItsReference()
        {
            var options = Build(ConvaiCharacterVersionMode.Live, null, null,
                new[] { new CharacterReleasedVersion(FirstReference, "1.0", true) }, hasLoaded: true);

            Assert.That(options[0].Selected, Is.True);
            Assert.That(options[1].Label, Is.EqualTo("1.0 (Latest release)"));
            Assert.That(options[1].Version, Is.EqualTo("1.0"));
            Assert.That(options[1].Reference, Is.EqualTo(FirstReference));
            Assert.That(options[1].Mode, Is.EqualTo(ConvaiCharacterVersionMode.Specific));
            Assert.That(options[1].Selected, Is.False);
        }

        [TestCase(false, "1.5 (saved)")]
        [TestCase(true, "1.5 (unavailable)")]
        public void MissingReference_IsPreservedWithoutSelectingSameLabelWithDifferentReference(
            bool hasLoaded, string expectedLabel)
        {
            var options = Build(ConvaiCharacterVersionMode.Specific, "1.5", FirstReference,
                new[] { Release(SecondReference, "1.5") }, hasLoaded);

            Assert.That(options.Count(option => option.Selected), Is.EqualTo(1));
            var selected = options.Single(option => option.Selected);
            Assert.That(selected.Reference, Is.EqualTo(FirstReference));
            Assert.That(selected.Label, Is.EqualTo(expectedLabel));
            Assert.That(selected.Enabled, Is.False);
            Assert.That(options[0].Enabled, Is.True);
            Assert.That(options[0].Selected, Is.False);
            Assert.That(options[1].Selected, Is.False);
            Assert.That(options[1].Enabled, Is.True);
        }

        [TestCase(null)]
        [TestCase("")]
        public void OlderPinWithoutApiReference_RequiresReselectionEvenWhenLabelIsAvailable(string savedReference)
        {
            var options = Build(ConvaiCharacterVersionMode.Specific, "1.0", savedReference,
                new[] { Release(FirstReference, "1.0") }, hasLoaded: true);

            var selected = options.Single(option => option.Selected);
            Assert.That(selected.Label, Is.EqualTo("1.0 (reselect required)"));
            Assert.That(selected.Reference, Is.EqualTo(savedReference));
            Assert.That(selected.Enabled, Is.False);
            Assert.That(options[0].Selected, Is.False);
            Assert.That(options[1].Selected, Is.False);
            Assert.That(options[1].Enabled, Is.True, "The user must be able to reselect the actual API entry.");
        }

        [Test]
        public void FailedOrPendingList_PreservesPinAndKeepsLiveAvailable()
        {
            var options = Build(ConvaiCharacterVersionMode.Specific, "1.0", FirstReference,
                Array.Empty<CharacterReleasedVersion>(), hasLoaded: false);

            Assert.That(options.Length, Is.EqualTo(2));
            Assert.That(options[0].Label, Is.EqualTo("Live"));
            Assert.That(options[0].Enabled, Is.True);
            Assert.That(options[0].Selected, Is.False);
            Assert.That(options[1].Reference, Is.EqualTo(FirstReference));
            Assert.That(options[1].Label, Is.EqualTo("1.0 (saved)"));
            Assert.That(options[1].Enabled, Is.False);
            Assert.That(options[1].Selected, Is.True);
        }

        [Test]
        public void InvalidSerializedMode_IsNotSilentlyTreatedAsLive()
        {
            var options = Build((ConvaiCharacterVersionMode)42, "1.0", FirstReference,
                new[] { Release(FirstReference, "1.0") }, hasLoaded: true);

            Assert.That(options.Count(option => option.Selected), Is.EqualTo(1));
            var selected = options.Single(option => option.Selected);
            Assert.That(selected.Label, Is.EqualTo("Invalid version selection"));
            Assert.That(selected.Enabled, Is.False);
            Assert.That(options[0].Selected, Is.False);
        }

        private static CharacterReleasedVersion Release(string reference, string version) =>
            new(reference, version, false);

        private static ConvaiCharacterVersionEditorFields.VersionOption[] Build(
            ConvaiCharacterVersionMode mode, string savedVersion, string savedReference,
            CharacterReleasedVersion[] releases, bool hasLoaded) =>
            ConvaiCharacterVersionEditorFields.BuildOptions(mode, savedVersion, savedReference, CharacterId,
                releases, hasLoaded);
    }
}
