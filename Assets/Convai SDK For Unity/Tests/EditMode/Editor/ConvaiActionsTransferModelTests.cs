using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Logging;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the Actions Editor's JSON transfer schema and merge logic
    ///     (<see cref="ConvaiActionsTransferModel" />): export/import round-trips, parse failure
    ///     messages, the three collision modes, and additive scene-knowledge merging. Pure data
    ///     logic — no GUI/EditorWindow dependency.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsTransferModelTests
    {
        private const string ObjectPrefix = "ConvaiActionsTransferModelTests_";

        [OneTimeSetUp]
        public void OneTimeSetUp() => ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(ObjectPrefix, System.StringComparison.Ordinal))
                    Object.DestroyImmediate(gameObject);
            }
        }

        // ── Round trip ─────────────────────────────────────────────────────────

        [Test]
        public void RoundTrip_PreservesEveryAuthoredField()
        {
            var original = new ConvaiActionDefinition
            {
                ActionName = "Pour Drink",
                Description = "Pours a drink into a glass.",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                ExecutorTypeHint = "PourActionExecutor",
                TimeoutSeconds = 12.5f,
                FailurePolicyOverride = ConvaiActionFailurePolicyOverride.StopBatch,
                WaitForBotSpeech = true,
                DelayAfterBotSpeechSeconds = 0.75f,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new()
                    {
                        Name = "beverage",
                        Description = "What to pour.",
                        Type = ConvaiActionParameterType.Choice,
                        Connector = "of",
                        Choices = new List<string> { "water", "juice" }
                    }
                }
            };
            original.Enabled = false;
            original.Category = "Counter";

            ConvaiActionsTransferModel.ExportDocument document = ConvaiActionsTransferModel.BuildDocument(
                new List<ConvaiActionDefinition> { original }, false, null, null, null);
            string json = ConvaiActionsTransferModel.ToJson(document);

            Assert.IsTrue(ConvaiActionsTransferModel.TryParse(
                json, out ConvaiActionsTransferModel.ExportDocument parsed, out string error), error);
            Assert.AreEqual(1, parsed.Actions.Count);

            ConvaiActionDefinition restored = ConvaiActionsTransferModel.ToDefinition(parsed.Actions[0]);
            Assert.AreEqual("Pour Drink", restored.ActionName);
            Assert.AreEqual("Pours a drink into a glass.", restored.Description);
            Assert.AreEqual(ConvaiActionTargetRequirement.Object, restored.TargetRequirement);
            Assert.AreEqual("PourActionExecutor", restored.ExecutorTypeHint);
            Assert.AreEqual(12.5f, restored.TimeoutSeconds);
            Assert.AreEqual(ConvaiActionFailurePolicyOverride.StopBatch, restored.FailurePolicyOverride);
            Assert.IsTrue(restored.WaitForBotSpeech);
            Assert.AreEqual(0.75f, restored.DelayAfterBotSpeechSeconds);
            Assert.IsFalse(restored.Enabled);
            Assert.AreEqual("Counter", restored.Category);
            Assert.AreEqual(1, restored.Parameters.Count);
            Assert.AreEqual("beverage", restored.Parameters[0].Name);
            Assert.AreEqual(ConvaiActionParameterType.Choice, restored.Parameters[0].Type);
            Assert.AreEqual("of", restored.Parameters[0].Connector);
            CollectionAssert.AreEqual(new[] { "water", "juice" }, restored.Parameters[0].Choices);
        }

        [Test]
        public void RoundTrip_UncategorizedAction_OmitsTheCategoryFromTheFile()
        {
            var original = new ConvaiActionDefinition { ActionName = "Greet" };

            string json = ConvaiActionsTransferModel.ToJson(ConvaiActionsTransferModel.BuildDocument(
                new List<ConvaiActionDefinition> { original }, false, null, null, null));

            // A project that never uses categories must keep exporting exactly the file it did before
            // the feature existed.
            Assert.IsFalse(json.Contains("category"));

            Assert.IsTrue(ConvaiActionsTransferModel.TryParse(
                json, out ConvaiActionsTransferModel.ExportDocument parsed, out string error), error);
            Assert.AreEqual(string.Empty, ConvaiActionsTransferModel.ToDefinition(parsed.Actions[0]).Category);
        }

        [Test]
        public void BuildDocument_DetachesBoundExecutorIntoTypeHint()
        {
            GameObject go = new(ObjectPrefix + "export");
            var executor = go.AddComponent<RecordingExecutor>();
            var definition = new ConvaiActionDefinition { ActionName = "Wave", Executor = executor };

            ConvaiActionsTransferModel.ActionDto dto = ConvaiActionsTransferModel.ToDto(definition);

            Assert.AreEqual(nameof(RecordingExecutor), dto.BehaviorTypeHint);
        }

        [Test]
        public void BuildDocument_SceneKnowledgeOnlyWhenAsked()
        {
            var objects = new List<ConvaiActionObjectDefinition> { new() { Name = "Lamp", Description = "A lamp." } };
            var characters = new List<ConvaiActionCharacterDefinition> { new() { Name = "Guard", Bio = "Stands watch." } };

            ConvaiActionsTransferModel.ExportDocument without = ConvaiActionsTransferModel.BuildDocument(
                null, false, objects, characters, "Lamp");
            Assert.IsNull(without.Objects);
            Assert.IsNull(without.Characters);
            Assert.IsNull(without.InitialAttentionObject);

            ConvaiActionsTransferModel.ExportDocument with = ConvaiActionsTransferModel.BuildDocument(
                null, true, objects, characters, "Lamp");
            Assert.AreEqual(1, with.Objects.Count);
            Assert.AreEqual("Lamp", with.Objects[0].Name);
            Assert.AreEqual(1, with.Characters.Count);
            Assert.AreEqual("Guard", with.Characters[0].Name);
            Assert.AreEqual("Stands watch.", with.Characters[0].Bio);
            Assert.AreEqual("Lamp", with.InitialAttentionObject);
        }

        // ── Parse failures ─────────────────────────────────────────────────────

        [Test]
        public void TryParse_EmptyText_Fails()
        {
            Assert.IsFalse(ConvaiActionsTransferModel.TryParse("  ", out _, out string error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryParse_MalformedJson_FailsWithoutThrowing()
        {
            Assert.IsFalse(ConvaiActionsTransferModel.TryParse("{ not json", out _, out string error));
            Assert.IsNotNull(error);
        }

        [Test]
        public void TryParse_JsonWithoutActionList_Fails()
        {
            Assert.IsFalse(ConvaiActionsTransferModel.TryParse("{}", out _, out string error));
            StringAssert.Contains("not an Actions export", error);
        }

        [Test]
        public void TryParse_NewerSchemaVersion_FailsWithUpdateGuidance()
        {
            string json = "{ \"schemaVersion\": 999, \"actions\": [] }";
            Assert.IsFalse(ConvaiActionsTransferModel.TryParse(json, out _, out string error));
            StringAssert.Contains("newer", error);
        }

        // ── Import merge collision modes ───────────────────────────────────────

        [Test]
        public void ApplyImport_NoCollision_AddsEverything()
        {
            var existing = new List<ConvaiActionDefinition> { new() { ActionName = "Wave" } };
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("Nod", "Point");

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                existing, null, document, ConvaiActionsImportCollisionMode.Skip);

            Assert.AreEqual(2, result.AddedCount);
            Assert.AreEqual(0, result.SkippedCount);
            Assert.AreEqual(3, result.Definitions.Count);
            Assert.AreEqual(2, result.Imported.Count);
        }

        [Test]
        public void ApplyImport_Skip_LeavesExistingUntouched()
        {
            var kept = new ConvaiActionDefinition { ActionName = "Wave", Description = "Original." };
            var existing = new List<ConvaiActionDefinition> { kept };
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("wave");

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                existing, null, document, ConvaiActionsImportCollisionMode.Skip);

            Assert.AreEqual(1, result.SkippedCount);
            Assert.AreEqual(1, result.Definitions.Count);
            Assert.AreSame(kept, result.Definitions[0]);
        }

        [Test]
        public void ApplyImport_Overwrite_ReplacesInPlace()
        {
            var existing = new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wave", Description = "Old." },
                new() { ActionName = "Nod" }
            };
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("Wave");
            document.Actions[0].Description = "New.";

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                existing, null, document, ConvaiActionsImportCollisionMode.Overwrite);

            Assert.AreEqual(1, result.OverwrittenCount);
            Assert.AreEqual(2, result.Definitions.Count);
            Assert.AreEqual("New.", result.Definitions[0].Description);
            Assert.AreEqual("Wave", result.Definitions[0].ActionName);
        }

        [Test]
        public void ApplyImport_Rename_AvoidsInlineAndReservedNames()
        {
            var existing = new List<ConvaiActionDefinition> { new() { ActionName = "Wave" } };
            var reserved = new List<string> { "Wave Copy" };
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("Wave");

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                existing, reserved, document, ConvaiActionsImportCollisionMode.Rename);

            Assert.AreEqual(1, result.RenamedCount);
            Assert.AreEqual(2, result.Definitions.Count);
            Assert.AreEqual("Wave Copy 2", result.Definitions[1].ActionName);
        }

        [Test]
        public void ApplyImport_RenamedImportsDoNotCollideWithEachOther()
        {
            var existing = new List<ConvaiActionDefinition> { new() { ActionName = "Wave" } };
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("Wave", "Wave");

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                existing, null, document, ConvaiActionsImportCollisionMode.Rename);

            Assert.AreEqual(2, result.RenamedCount);
            Assert.AreEqual("Wave Copy", result.Definitions[1].ActionName);
            Assert.AreEqual("Wave Copy 2", result.Definitions[2].ActionName);
        }

        [Test]
        public void ApplyImport_BlankNamesAreDropped()
        {
            ConvaiActionsTransferModel.ExportDocument document = DocumentWith("  ", "Nod");

            ConvaiActionsTransferModel.ImportResult result = ConvaiActionsTransferModel.ApplyImport(
                new List<ConvaiActionDefinition>(), null, document, ConvaiActionsImportCollisionMode.Skip);

            Assert.AreEqual(1, result.AddedCount);
            Assert.AreEqual(1, result.Definitions.Count);
        }

        // ── Scene knowledge merge ──────────────────────────────────────────────

        [Test]
        public void MergeKnownObjects_AddsOnlyNewNames_CaseInsensitive()
        {
            var objects = new List<ConvaiActionObjectDefinition> { new() { Name = "Lamp" } };
            var incoming = new List<ConvaiActionsTransferModel.KnownObjectDto>
            {
                new() { Name = "LAMP", Description = "Duplicate." },
                new() { Name = "Chair", Description = "A chair." },
                new() { Name = "  " }
            };

            int added = ConvaiActionsTransferModel.MergeKnownObjects(objects, incoming);

            Assert.AreEqual(1, added);
            Assert.AreEqual(2, objects.Count);
            Assert.AreEqual("Chair", objects[1].Name);
            Assert.AreEqual("A chair.", objects[1].Description);
        }

        [Test]
        public void MergeKnownCharacters_AddsOnlyNewNames()
        {
            var characters = new List<ConvaiActionCharacterDefinition> { new() { Name = "Guard" } };
            var incoming = new List<ConvaiActionsTransferModel.KnownCharacterDto>
            {
                new() { Name = "guard", Bio = "Duplicate." },
                new() { Name = "Merchant", Bio = "Sells things." }
            };

            int added = ConvaiActionsTransferModel.MergeKnownCharacters(characters, incoming);

            Assert.AreEqual(1, added);
            Assert.AreEqual(2, characters.Count);
            Assert.AreEqual("Merchant", characters[1].Name);
        }

        // ── Fixture helpers ────────────────────────────────────────────────────

        private static ConvaiActionsTransferModel.ExportDocument DocumentWith(params string[] actionNames)
        {
            var document = new ConvaiActionsTransferModel.ExportDocument
            {
                Actions = new List<ConvaiActionsTransferModel.ActionDto>()
            };
            for (int i = 0; i < actionNames.Length; i++)
                document.Actions.Add(new ConvaiActionsTransferModel.ActionDto { Name = actionNames[i] });
            return document;
        }

        private sealed class RecordingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation,
                CancellationToken cancellationToken) =>
                Task.FromResult(ConvaiActionExecutionResult.Succeeded());
        }
    }
}
