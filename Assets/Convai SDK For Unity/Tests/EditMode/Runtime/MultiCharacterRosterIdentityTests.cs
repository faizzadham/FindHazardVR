using System.Collections.Generic;
using Convai.Domain.Emotion;
using Convai.Runtime.Behaviors;
using Convai.Runtime.DynamicContext;
using Convai.Runtime.Room;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime.MultiCharacter
{
    /// <summary>
    ///     Covers the rule that no two characters in one room may be the same Convai character.
    /// </summary>
    /// <remarks>
    ///     The mistake it catches is the ordinary one — a character duplicated in the Hierarchy keeps
    ///     the original's Character ID — and the failure it prevents does not look like a failure:
    ///     the room connects, and one character answers in the other's place because ownership,
    ///     participants and audio are all keyed by that ID.
    /// </remarks>
    [TestFixture]
    public sealed class MultiCharacterRosterIdentityTests
    {
        [Test]
        public void ARosterOfDistinctCharactersHasNoDuplicate()
        {
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(
                    new IConvaiCharacterAgent[] { Agent("Sofia", "id-1"), Agent("James", "id-2") },
                    out _,
                    out _),
                Is.False);
        }

        [Test]
        public void TheDuplicatedCharacterIsTheSecondOneNotTheFirst()
        {
            var original = Agent("Sofia", "id-1");
            var copy = Agent("Sofia (1)", "id-1");

            bool found = MultiCharacterRosterIdentity.TryFindDuplicate(
                new IConvaiCharacterAgent[] { original, Agent("James", "id-2"), copy },
                out IConvaiCharacterAgent existing,
                out IConvaiCharacterAgent duplicate);

            Assert.That(found, Is.True);
            Assert.That(existing, Is.SameAs(original),
                "The character already holding the ID keeps it; naming the copy as the incumbent "
                + "would point the reader at the wrong GameObject.");
            Assert.That(duplicate, Is.SameAs(copy));
        }

        [Test]
        public void AStraySpaceDoesNotDisguiseADuplicate()
        {
            // A Character ID pasted from the dashboard with a trailing space is the same character.
            // Comparing raw would let exactly the copy this rule exists for through.
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(
                    new IConvaiCharacterAgent[] { Agent("Sofia", "id-1"), Agent("Sofia (1)", " id-1 ") },
                    out _,
                    out _),
                Is.True);
        }

        [Test]
        public void CaseAloneDoesNotMakeADifferentCharacter()
        {
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(
                    new IConvaiCharacterAgent[] { Agent("Sofia", "ID-1"), Agent("Sofia (1)", "id-1") },
                    out _,
                    out _),
                Is.True);
        }

        [Test]
        public void CharactersWithNoIdAreLeftToTheCheckThatOwnsThem()
        {
            // Two blank IDs are two characters missing a Character ID, which is refused separately
            // and in words that name the empty field. Reporting them as a clash here would send the
            // reader looking for a duplicate that does not exist.
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(
                    new IConvaiCharacterAgent[] { Agent("Sofia", null), Agent("James", "  ") },
                    out _,
                    out _),
                Is.False);
        }

        [Test]
        public void ARosterTooSmallToClashIsNotWalked()
        {
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(null, out _, out _), Is.False);
            Assert.That(
                MultiCharacterRosterIdentity.TryFindDuplicate(
                    new IConvaiCharacterAgent[] { Agent("Sofia", "id-1") }, out _, out _),
                Is.False);
        }

        [Test]
        public void AnArrivingCharacterIsCheckedAgainstTheRosterItJoins()
        {
            var sofia = Agent("Sofia", "id-1");
            var james = Agent("James", "id-2");

            Assert.That(
                MultiCharacterRosterIdentity.ConflictsWithRoster(
                    new IConvaiCharacterAgent[] { sofia, james },
                    Agent("Marina", "id-3"),
                    out _),
                Is.False);

            Assert.That(
                MultiCharacterRosterIdentity.ConflictsWithRoster(
                    new IConvaiCharacterAgent[] { sofia, james },
                    Agent("James (1)", "id-2"),
                    out IConvaiCharacterAgent existing),
                Is.True);
            Assert.That(existing, Is.SameAs(james));
        }

        [Test]
        public void ACharacterAlreadyInTheRosterDoesNotClashWithItself()
        {
            // The live path can be handed a character the roster already holds. That is a different
            // refusal — "this instance is already a member" — and it must not be reported as one
            // character clashing with itself.
            var sofia = Agent("Sofia", "id-1");

            Assert.That(
                MultiCharacterRosterIdentity.ConflictsWithRoster(
                    new IConvaiCharacterAgent[] { sofia }, sofia, out _),
                Is.False);
        }

        [Test]
        public void TheRefusalNamesBothCharactersTheIdAndTheFix()
        {
            string message = MultiCharacterRosterIdentity.DescribeDuplicate(
                Agent("Sofia", "id-1"),
                Agent("Sofia (1)", "id-1"));

            Assert.That(message, Does.Contain("Sofia"));
            Assert.That(message, Does.Contain("Sofia (1)"));
            Assert.That(message, Does.Contain("id-1"),
                "Without the ID the reader cannot tell which of several fields to look at.");
            Assert.That(message, Does.Contain("Convai dashboard"),
                "A refusal that does not say where the fix comes from leaves the reader stuck.");
        }

        [Test]
        public void ACharacterWithNoDisplayNameIsStillNamed()
        {
            // Falling through to an empty string would produce "'' and 'Sofia (1)' both use…",
            // which names nothing the reader can go and find.
            string message = MultiCharacterRosterIdentity.DescribeDuplicate(
                Agent(string.Empty, "id-1"),
                Agent("Sofia (1)", "id-1"));

            Assert.That(message, Does.Not.Contain("'' "));
            Assert.That(message, Does.Contain("unnamed character"));
        }

        private static IConvaiCharacterAgent Agent(string name, string characterId) =>
            new StubAgent(name, characterId);

        /// <summary>A character agent whose name and Character ID are separate facts.</summary>
        private sealed class StubAgent : IConvaiCharacterAgent
        {
            internal StubAgent(string name, string characterId)
            {
                CharacterName = name;
                CharacterId = characterId;
            }

            public string CharacterId { get; }
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext => null;
            public EmotionDetectionMode EmotionDetectionMode => default;

            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }

            public override string ToString() => CharacterName;
        }
    }
}
