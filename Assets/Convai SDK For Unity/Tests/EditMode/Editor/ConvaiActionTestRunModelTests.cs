using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionTestRunModel" /> — the Test Run panel's typed command
    ///     building: brace-blob rendering in parameter order (blank optionals preserved), coercion
    ///     through the real enrichment parser (number/bool/choice incl. constraint mismatch), the
    ///     target-with-parameters override, and number-text validation.
    /// </summary>
    [TestFixture]
    public class ConvaiActionTestRunModelTests
    {
        private static ConvaiActionConfig ConfigWithObject(string name) =>
            new()
            {
                Objects = new List<ConvaiActionObjectDefinition> { new() { Name = name } }
            };

        private static ConvaiActionDefinition TypedDefinition() =>
            new()
            {
                ActionName = "Set Counter",
                TargetRequirement = ConvaiActionTargetRequirement.None,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "count", Type = ConvaiActionParameterType.Number },
                    new() { Name = "active", Type = ConvaiActionParameterType.Bool },
                    new()
                    {
                        Name = "color",
                        Type = ConvaiActionParameterType.Choice,
                        Choices = new List<string> { "red", "green" }
                    },
                    new() { Name = "note", Type = ConvaiActionParameterType.String }
                }
            };

        [Test]
        public void BuildParameterBlob_WrapsValuesInBraces_InParameterOrder()
        {
            ConvaiActionDefinition definition = TypedDefinition();
            string blob = ConvaiActionTestRunModel.BuildParameterBlob(
                definition.Parameters, new[] { "2.5", "true", "green", "hello there" });

            Assert.AreEqual("{2.5} {true} {green} {hello there}", blob);
        }

        [Test]
        public void BuildParameterBlob_BlankOptionals_RenderAsEmptyBraces()
        {
            ConvaiActionDefinition definition = TypedDefinition();
            string blob = ConvaiActionTestRunModel.BuildParameterBlob(
                definition.Parameters, new[] { "1", null, "  ", string.Empty });

            Assert.AreEqual("{1} {} {} {}", blob);
        }

        [Test]
        public void BuildParameterBlob_NoParameters_ReturnsEmpty()
        {
            Assert.AreEqual(string.Empty, ConvaiActionTestRunModel.BuildParameterBlob(null, null));
            Assert.AreEqual(string.Empty,
                ConvaiActionTestRunModel.BuildParameterBlob(new List<ConvaiActionParameterDefinition>(), null));
        }

        [Test]
        public void BuildCommand_NullOrUnnamedDefinition_ReturnsNull()
        {
            Assert.IsNull(ConvaiActionTestRunModel.BuildCommand(null, null, null, null, null));
            Assert.IsNull(ConvaiActionTestRunModel.BuildCommand(
                new ConvaiActionDefinition { ActionName = "   " }, null, null, null, null));
        }

        [Test]
        public void BuildCommand_ParameterlessAction_CarriesPickedTargetAndEnriches()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Move To",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            };
            ConvaiActionConfig config = ConfigWithObject("Red Cube");

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, "Red Cube", null, config, new[] { definition });

            Assert.IsNotNull(command);
            Assert.IsTrue(command.Enriched);
            Assert.AreEqual("Move To", command.Name);
            Assert.AreEqual("Red Cube", command.Target);
            Assert.IsTrue(command.Parameters.ContainsKey("target"),
                "The parameterless path routes the target through the parser's implicit target parameter.");
            Assert.AreEqual("Red Cube", command.Parameters["target"].ResolvedReference?.Name);
        }

        [Test]
        public void BuildCommand_TypedParameters_CoerceThroughTheRealParser()
        {
            ConvaiActionDefinition definition = TypedDefinition();

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, null, new[] { "2.5", "true", "green", string.Empty }, null, new[] { definition });

            Assert.IsNotNull(command);
            Assert.IsTrue(command.Enriched);

            Assert.AreEqual(ConvaiActionParameterType.Number, command.Parameters["count"].Type);
            Assert.AreEqual(2.5f, command.Parameters["count"].NumberValue, 0.0001f);

            Assert.AreEqual(ConvaiActionParameterType.Bool, command.Parameters["active"].Type);
            Assert.IsTrue(command.Parameters["active"].BoolValue);

            Assert.AreEqual(ConvaiActionParameterType.Choice, command.Parameters["color"].Type);
            Assert.IsTrue(command.Parameters["color"].IsConstraintMatch);
            Assert.AreEqual("green", command.Parameters["color"].StringValue);

            Assert.AreEqual(string.Empty, command.Parameters["note"].RawValue,
                "A blank optional parameter arrives as an empty value, not a missing key.");
        }

        [Test]
        public void BuildCommand_InvalidChoice_IsFlaggedNotDropped()
        {
            ConvaiActionDefinition definition = TypedDefinition();

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, null, new[] { "1", "no", "blue", "x" }, null, new[] { definition });

            Assert.IsFalse(command.Parameters["color"].IsConstraintMatch);
            Assert.AreEqual("blue", command.Parameters["color"].StringValue);
        }

        [Test]
        public void BuildCommand_ParametersPlusPickedTarget_AimsTheEnrichedCommand()
        {
            ConvaiActionDefinition definition = TypedDefinition();
            definition.TargetRequirement = ConvaiActionTargetRequirement.Object;
            ConvaiActionConfig config = ConfigWithObject("Red Cube");

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, "Red Cube", new[] { "3", "false", "red", string.Empty }, config, new[] { definition });

            Assert.AreEqual("Red Cube", command.Target);
            Assert.AreEqual("Set Counter Red Cube", command.ActionString);
            Assert.AreEqual(3f, command.Parameters["count"].NumberValue, 0.0001f);
        }

        [Test]
        public void BuildCommand_WithoutDefinitionsList_StillEnrichesAgainstItsOwnDefinition()
        {
            ConvaiActionDefinition definition = TypedDefinition();

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, null, new[] { "7", string.Empty, string.Empty, string.Empty }, null, null);

            Assert.IsTrue(command.Enriched);
            Assert.AreEqual(7f, command.Parameters["count"].NumberValue, 0.0001f);
        }

        [Test]
        public void BuildCommand_MarksTestRunCommands_ToSkipTheSpeechGate()
        {
            var definition = new ConvaiActionDefinition
            {
                ActionName = "Move To",
                TargetRequirement = ConvaiActionTargetRequirement.None,
                WaitForBotSpeech = true
            };

            ConvaiActionCommand command = ConvaiActionTestRunModel.BuildCommand(
                definition, null, null, null, new[] { definition });

            Assert.IsTrue(command.BypassSpeechGate,
                "Test runs happen without a conversation, so the dispatcher's speech gate must be skipped.");
            Assert.IsFalse(command.BypassAvailability,
                "Availability is only ever bypassed by the explicit Run Anyway affordance.");
        }

        [Test]
        public void IsNumberTextValid_AcceptsBlankAndInvariantFloats_RejectsEverythingElse()
        {
            Assert.IsTrue(ConvaiActionTestRunModel.IsNumberTextValid(null));
            Assert.IsTrue(ConvaiActionTestRunModel.IsNumberTextValid(string.Empty));
            Assert.IsTrue(ConvaiActionTestRunModel.IsNumberTextValid("  "));
            Assert.IsTrue(ConvaiActionTestRunModel.IsNumberTextValid("1.5"));
            Assert.IsTrue(ConvaiActionTestRunModel.IsNumberTextValid(" -3 "));
            Assert.IsFalse(ConvaiActionTestRunModel.IsNumberTextValid("abc"));
            Assert.IsFalse(ConvaiActionTestRunModel.IsNumberTextValid("1,5"));
        }
    }
}
