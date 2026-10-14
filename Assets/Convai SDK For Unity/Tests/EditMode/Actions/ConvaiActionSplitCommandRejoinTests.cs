using System.Collections.Generic;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     One request written as several entries in the actions list is one request.
    /// </summary>
    /// <remarks>
    ///     Every shape here was measured against the live backend on 2026-08-12, where splitting
    ///     accounted for every failure in the run: the action was dropped for naming nothing to act
    ///     on, and the pieces it had been split into were dropped for not being actions.
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionSplitCommandRejoinTests
    {
        private readonly List<GameObject> _created = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _created.Count; i++)
                if (_created[i] != null)
                    Object.DestroyImmediate(_created[i]);

            _created.Clear();
        }

        private ConvaiActionConfig VocabularyWith(params string[] objectNames)
        {
            var objects = new List<ConvaiActionObjectDefinition>();
            foreach (string name in objectNames)
                objects.Add(new ConvaiActionObjectDefinition { Name = name, Available = true });

            return new ConvaiActionConfig { Objects = objects };
        }

        private static ConvaiActionDefinition LightTheRoom() =>
            new()
            {
                ActionName = "Light The Room",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "mode",
                        Type = ConvaiActionParameterType.Choice,
                        Choices = new List<string> { "on", "off", "toggle" }
                    }
                }
            };

        private static ConvaiActionDefinition LeadTheWay() =>
            new()
            {
                ActionName = "Lead The Way",
                TargetRequirement = ConvaiActionTargetRequirement.Either
            };

        private static IReadOnlyList<ConvaiActionCommand> Rejoin(
            ConvaiActionConfig vocabulary,
            IReadOnlyList<ConvaiActionDefinition> definitions,
            out int count,
            params string[] entries)
        {
            var commands = new List<ConvaiActionCommand>();
            foreach (string entry in entries)
                commands.Add(new ConvaiActionCommand(entry, null));

            return ConvaiActionSplitCommandRejoin.Apply(commands, vocabulary, definitions, out count);
        }

        /// <summary>
        ///     The measured shape: an action, its Choice value, and its target, as three entries.
        /// </summary>
        [Test]
        public void ACommandSplitAcrossThreeEntriesBecomesOne()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("Gallery Lights"),
                new List<ConvaiActionDefinition> { LightTheRoom() },
                out int count,
                "Light The Room", "on", "Gallery Lights");

            Assert.That(count, Is.EqualTo(2));
            Assert.That(rejoined, Has.Count.EqualTo(1));
            Assert.That(rejoined[0].Name,
                Is.EqualTo("Light The Room {mode: on} {target: Gallery Lights}"),
                "The join is written in the grammar the reader already speaks, so nothing needs a " +
                "second way of parsing it.");
        }

        /// <summary>
        ///     And it reads back as the command the Convai Character meant.
        /// </summary>
        [Test]
        public void TheRejoinedCommandEnrichesIntoTheRightSlots()
        {
            var definitions = new List<ConvaiActionDefinition> { LightTheRoom() };
            ConvaiActionConfig vocabulary = VocabularyWith("Gallery Lights");

            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                vocabulary, definitions, out int _, "Light The Room", "on", "Gallery Lights");

            ConvaiActionCommand enriched =
                ConvaiActionResponseParser.Enrich(rejoined[0], vocabulary, definitions);

            Assert.That(enriched.Parameters["mode"].StringValue, Is.EqualTo("on"));
            Assert.That(enriched.Parameters["mode"].IsConstraintMatch, Is.True);
            Assert.That(enriched.Parameters["target"].StringValue, Is.EqualTo("Gallery Lights"));
        }

        [Test]
        public void ATargetSplitOntoItsOwnEntryComesBack()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("The Gallery"),
                new List<ConvaiActionDefinition> { LeadTheWay() },
                out int count,
                "Lead The Way", "The Gallery");

            Assert.That(count, Is.EqualTo(1));
            Assert.That(rejoined, Has.Count.EqualTo(1));
            Assert.That(rejoined[0].Name, Is.EqualTo("Lead The Way {target: The Gallery}"));
        }

        /// <summary>
        ///     A stray entry that names nothing this character has is left exactly where it was.
        /// </summary>
        /// <remarks>
        ///     The point of the three conditions is that an entry is only absorbed when there is a
        ///     fact saying it belongs. Without one it must still be dropped and explained, because a
        ///     silent absorption is a worse answer than a reported drop.
        /// </remarks>
        [Test]
        public void AnEntryThatFitsNoSlotIsLeftAlone()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("The Gallery"),
                new List<ConvaiActionDefinition> { LeadTheWay() },
                out int count,
                "Lead The Way", "somewhere nice");

            Assert.That(count, Is.Zero);
            Assert.That(rejoined, Has.Count.EqualTo(2));
        }

        /// <summary>
        ///     A command that already carries a value takes nothing after it.
        /// </summary>
        /// <remarks>
        ///     The split shape is always a bare action name. An entry that arrived with a value is
        ///     one the character filled in, and appending a slot to it could contradict what was
        ///     sent or fill the same slot twice.
        /// </remarks>
        [Test]
        public void ACommandThatAlreadyNamedSomethingAbsorbsNothing()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("The Gallery", "The Control Room"),
                new List<ConvaiActionDefinition> { LeadTheWay() },
                out int count,
                "Lead The Way The Control Room", "The Gallery");

            Assert.That(count, Is.Zero);
            Assert.That(rejoined, Has.Count.EqualTo(2));
        }

        /// <summary>
        ///     Two real actions in a row stay two actions.
        /// </summary>
        [Test]
        public void AnOrderedSequenceOfRealActionsIsUntouched()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("The Gallery", "Gallery Lights"),
                new List<ConvaiActionDefinition> { LeadTheWay(), LightTheRoom() },
                out int count,
                "Lead The Way {target: The Gallery}", "Light The Room {mode: on} {target: Gallery Lights}");

            Assert.That(count, Is.Zero);
            Assert.That(rejoined, Has.Count.EqualTo(2));
        }

        /// <summary>
        ///     The same slot is never filled twice.
        /// </summary>
        [Test]
        public void OnlyTheFirstEntryThatFitsASlotIsTaken()
        {
            IReadOnlyList<ConvaiActionCommand> rejoined = Rejoin(
                VocabularyWith("The Gallery", "The Control Room"),
                new List<ConvaiActionDefinition> { LeadTheWay() },
                out int count,
                "Lead The Way", "The Gallery", "The Control Room");

            Assert.That(count, Is.EqualTo(1), "Lead The Way has one slot, so it can take one value.");
            Assert.That(rejoined, Has.Count.EqualTo(2));
            Assert.That(rejoined[0].Name, Is.EqualTo("Lead The Way {target: The Gallery}"));
        }

        /// <summary>
        ///     The caller's own command objects are never rewritten.
        /// </summary>
        /// <remarks>
        ///     <c>ReadWithoutRefusing</c> is handed commands built in somebody's own code. Repairing
        ///     the copy is the whole contract; repairing theirs would change an object they still
        ///     hold a reference to.
        /// </remarks>
        [Test]
        public void TheCommandsHandedInAreNotModified()
        {
            var original = new ConvaiActionCommand("Lead The Way", null);
            var batch = new List<ConvaiActionCommand> { original, new("The Gallery", null) };

            ConvaiActionSplitCommandRejoin.Apply(
                batch, VocabularyWith("The Gallery"),
                new List<ConvaiActionDefinition> { LeadTheWay() }, out int count);

            Assert.That(count, Is.EqualTo(1));
            Assert.That(original.Name, Is.EqualTo("Lead The Way"),
                "The repair belongs to the copy that goes on to be read, not to the caller's object.");
        }
    }
}
