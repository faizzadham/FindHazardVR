using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionsGrouping" /> : regrouping an already-built list along each
    ///     axis, the health rollup a collapsed header reports, category-name hygiene (existing names,
    ///     near-duplicates) and the cold-start suggestion. All of it is pure logic — no window, no
    ///     draw pass.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsGroupingTests
    {
        private const string ObjectPrefix = "ConvaiActionsGroupingTests_";

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(ObjectPrefix, System.StringComparison.Ordinal))
                    Object.DestroyImmediate(gameObject);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static ConvaiActionRow Row(
            string name, string category = "", ConvaiActionRowStatus status = ConvaiActionRowStatus.Ready)
        {
            var definition = new ConvaiActionDefinition { ActionName = name, Category = category };
            return new ConvaiActionRow(definition, status, false, 0, null, name);
        }

        private static List<ConvaiActionGroup> SourceGroups(params ConvaiActionRow[] rows)
        {
            var group = new ConvaiActionGroup { Title = "This Character", Kind = ConvaiActionGroupKind.ThisCharacter };
            group.Rows.AddRange(rows);
            group.RefreshRollup();
            return new List<ConvaiActionGroup> { group };
        }

        // ── Axes ───────────────────────────────────────────────────────────────

        [Test]
        public void Regroup_SourceAxis_ReturnsTheListUntouched()
        {
            List<ConvaiActionGroup> source = SourceGroups(Row("Greet"), Row("Wave"));

            Assert.AreSame(source, ConvaiActionsGrouping.Regroup(source, ConvaiActionsGroupAxis.Source));
        }

        [Test]
        public void Regroup_Category_SortsAlphabeticallyWithUncategorizedLast()
        {
            List<ConvaiActionGroup> source = SourceGroups(
                Row("Tour Start", "Tour"),
                Row("Greet"),
                Row("Take Order", "Counter"));

            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(source, ConvaiActionsGroupAxis.Category);

            Assert.AreEqual(3, grouped.Count);
            Assert.AreEqual("Counter", grouped[0].Title);
            Assert.AreEqual("Tour", grouped[1].Title);
            Assert.AreEqual(ConvaiActionsGrouping.UncategorizedTitle, grouped[2].Title);
            Assert.AreEqual(string.Empty, grouped[2].CategoryName);
            Assert.AreEqual(ConvaiActionGroupKind.Category, grouped[0].Kind);
        }

        [Test]
        public void Regroup_Category_MergesCasingVariantsAndKeepsTheFirstAuthoredCasing()
        {
            List<ConvaiActionGroup> source = SourceGroups(
                Row("Tour Start", "Tour"),
                Row("Tour End", "tour"),
                Row("Tour Detour", "TOUR"));

            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(source, ConvaiActionsGroupAxis.Category);

            Assert.AreEqual(1, grouped.Count);
            Assert.AreEqual("Tour", grouped[0].Title);
            Assert.AreEqual(3, grouped[0].Rows.Count);
        }

        [Test]
        public void Regroup_Category_KeyIsStableAcrossCasing()
        {
            Assert.AreEqual(
                ConvaiActionsGrouping.BuildCategoryGroupKey("Tour"),
                ConvaiActionsGrouping.BuildCategoryGroupKey(" TOUR "));
        }

        [Test]
        public void Regroup_Category_EmptyListProducesNoGroups()
        {
            Assert.AreEqual(0, ConvaiActionsGrouping.Regroup(
                SourceGroups(), ConvaiActionsGroupAxis.Category).Count);
        }

        [Test]
        public void Regroup_Status_OrdersWorstFirst()
        {
            List<ConvaiActionGroup> source = SourceGroups(
                Row("Ready One"),
                Row("Broken One", string.Empty, ConvaiActionRowStatus.Broken),
                Row("Warned One", string.Empty, ConvaiActionRowStatus.NeedsAttention));

            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(source, ConvaiActionsGroupAxis.Status);

            Assert.AreEqual(3, grouped.Count);
            Assert.AreEqual(ConvaiActionRowStatus.Broken, grouped[0].Rows[0].Status);
            Assert.AreEqual(ConvaiActionRowStatus.NeedsAttention, grouped[1].Rows[0].Status);
            Assert.AreEqual(ConvaiActionRowStatus.Ready, grouped[2].Rows[0].Status);
        }

        [Test]
        public void Regroup_Behavior_UsesTheResolverAndFilesUnknownsLast()
        {
            List<ConvaiActionGroup> source = SourceGroups(Row("Look At"), Row("Walk To"), Row("Mystery"));

            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(
                source,
                ConvaiActionsGroupAxis.Behavior,
                definition => definition.ActionName switch
                {
                    "Look At" => "Gaze",
                    "Walk To" => "Body Animation",
                    _ => string.Empty
                });

            Assert.AreEqual(3, grouped.Count);
            Assert.AreEqual("Body Animation", grouped[0].Title);
            Assert.AreEqual("Gaze", grouped[1].Title);
            Assert.AreEqual(ConvaiActionsGrouping.UnknownBehaviorTitle, grouped[2].Title);
        }

        // ── Rollup ─────────────────────────────────────────────────────────────

        [Test]
        public void Rollup_ReportsTheWorstStatusAndHowManyAreNotReady()
        {
            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(
                SourceGroups(
                    Row("A", "Tour"),
                    Row("B", "Tour", ConvaiActionRowStatus.NeedsAttention),
                    Row("C", "Tour", ConvaiActionRowStatus.Broken)),
                ConvaiActionsGroupAxis.Category);

            Assert.AreEqual(ConvaiActionRowStatus.Broken, grouped[0].WorstStatus);
            Assert.AreEqual(2, grouped[0].UnhealthyCount);
        }

        [Test]
        public void Rollup_AllReady_IsReadyWithNothingOutstanding()
        {
            List<ConvaiActionGroup> grouped = ConvaiActionsGrouping.Regroup(
                SourceGroups(Row("A", "Tour"), Row("B", "Tour")), ConvaiActionsGroupAxis.Category);

            Assert.AreEqual(ConvaiActionRowStatus.Ready, grouped[0].WorstStatus);
            Assert.AreEqual(0, grouped[0].UnhealthyCount);
        }

        // ── Category names ─────────────────────────────────────────────────────

        [Test]
        public void HasAnyCategory_AndCollectCategoryNames_ReadInlineDefinitions()
        {
            var gameObject = new GameObject(ObjectPrefix + "source");
            var source = gameObject.AddComponent<ConvaiActionConfigSource>();

            Assert.IsFalse(ConvaiActionsGrouping.HasAnyCategory(source));

            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Take Order", Category = "Counter" },
                new() { ActionName = "Greet" },
                new() { ActionName = "Pour", Category = "counter" }
            });

            Assert.IsTrue(ConvaiActionsGrouping.HasAnyCategory(source));
            CollectionAssert.AreEqual(new[] { "Counter" }, ConvaiActionsGrouping.CollectCategoryNames(source));
        }

        [Test]
        public void HasAnyCategory_NullSource_IsFalse() =>
            Assert.IsFalse(ConvaiActionsGrouping.HasAnyCategory(null));

        [Test]
        public void Search_MatchesTheCategoryName()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Take Order", Category = "Counter" };

            Assert.IsTrue(ConvaiActionsEditorModel.MatchesFilter(definition, "counter"));
            Assert.IsFalse(ConvaiActionsEditorModel.MatchesFilter(definition, "tour"));
        }

        [Test]
        public void FindNearDuplicate_CatchesPluralsPunctuationAndCasing()
        {
            var existing = new[] { "Tour", "Small Talk" };

            Assert.AreEqual("Tour", ConvaiActionsGrouping.FindNearDuplicate(existing, "Tours"));
            Assert.AreEqual("Small Talk", ConvaiActionsGrouping.FindNearDuplicate(existing, "small-talk"));
            Assert.IsNull(ConvaiActionsGrouping.FindNearDuplicate(existing, "Counter"));
        }

        [Test]
        public void FindNearDuplicate_TheSameCategoryIsAPickNotAWarning()
        {
            var existing = new[] { "Tour" };

            Assert.IsNull(ConvaiActionsGrouping.FindNearDuplicate(existing, "tour"));
            Assert.IsNull(ConvaiActionsGrouping.FindNearDuplicate(existing, " Tour "));
            Assert.IsNull(ConvaiActionsGrouping.FindNearDuplicate(existing, string.Empty));
        }

        // ── Reordering (the drag's arithmetic) ─────────────────────────────────

        private static List<ConvaiActionDefinition> Definitions(params string[] names)
        {
            var list = new List<ConvaiActionDefinition>(names.Length);
            for (int i = 0; i < names.Length; i++)
                list.Add(new ConvaiActionDefinition { ActionName = names[i] });

            return list;
        }

        private static string[] Names(IReadOnlyList<ConvaiActionDefinition> definitions)
        {
            var names = new string[definitions.Count];
            for (int i = 0; i < definitions.Count; i++)
                names[i] = definitions[i].ActionName;

            return names;
        }

        [Test]
        public void MoveWithin_DownwardsLandsAfterTheAnchor()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C", "D");

            List<ConvaiActionDefinition> moved = ConvaiActionsGrouping.MoveWithin(
                ordered, new[] { ordered[0] }, ordered[2], true);

            // The classic off-by-one: computing the insertion point before removing "A" would land it
            // between B and C instead of after C.
            CollectionAssert.AreEqual(new[] { "B", "C", "A", "D" }, Names(moved));
        }

        [Test]
        public void MoveWithin_UpwardsLandsBeforeTheAnchor()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C", "D");

            List<ConvaiActionDefinition> moved = ConvaiActionsGrouping.MoveWithin(
                ordered, new[] { ordered[3] }, ordered[1], false);

            CollectionAssert.AreEqual(new[] { "A", "D", "B", "C" }, Names(moved));
        }

        [Test]
        public void MoveWithin_KeepsTheOrderOfAMultipleSelection()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C", "D", "E");

            List<ConvaiActionDefinition> moved = ConvaiActionsGrouping.MoveWithin(
                ordered, new[] { ordered[0], ordered[2] }, ordered[4], true);

            CollectionAssert.AreEqual(new[] { "B", "D", "E", "A", "C" }, Names(moved));
        }

        [Test]
        public void MoveWithin_DroppingOnItselfChangesNothing()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C");

            List<ConvaiActionDefinition> moved = ConvaiActionsGrouping.MoveWithin(
                ordered, new[] { ordered[1] }, ordered[1], false);

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, Names(moved));
        }

        [Test]
        public void MoveWithin_LeavesTheInputListAlone()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C");

            ConvaiActionsGrouping.MoveWithin(ordered, new[] { ordered[0] }, ordered[2], true);

            CollectionAssert.AreEqual(new[] { "A", "B", "C" }, Names(ordered));
        }

        [Test]
        public void FindLastInCategory_IsTheAnchorAHeaderDropLandsAfter()
        {
            List<ConvaiActionDefinition> ordered = Definitions("A", "B", "C");
            ordered[0].Category = "Tour";
            ordered[2].Category = "tour";

            Assert.AreSame(ordered[2], ConvaiActionsGrouping.FindLastInCategory(ordered, "Tour"));
            Assert.IsNull(ConvaiActionsGrouping.FindLastInCategory(ordered, "Counter"));
        }

        // ── Cold start ─────────────────────────────────────────────────────────

        [Test]
        public void SuggestCategories_ProposesOneCategoryPerBehaviorFamily()
        {
            List<ConvaiActionGroup> source = SourceGroups(Row("Look At"), Row("Watch"), Row("Walk To"));

            List<ConvaiActionsGrouping.CategorySuggestion> suggestions = ConvaiActionsGrouping.SuggestCategories(
                source,
                definition => definition.ActionName == "Walk To" ? "Movement" : "Attention");

            Assert.AreEqual(2, suggestions.Count);
            Assert.AreEqual("Attention", suggestions[0].Category);
            Assert.AreEqual(2, suggestions[0].Rows.Count);
            Assert.AreEqual("Movement", suggestions[1].Category);
        }

        [Test]
        public void SuggestCategories_LeavesAlreadyFiledActionsAlone()
        {
            List<ConvaiActionGroup> source = SourceGroups(
                Row("Look At", "Mine"), Row("Watch"), Row("Walk To"));

            List<ConvaiActionsGrouping.CategorySuggestion> suggestions = ConvaiActionsGrouping.SuggestCategories(
                source,
                definition => definition.ActionName == "Walk To" ? "Movement" : "Attention");

            Assert.AreEqual(2, suggestions.Count);
            Assert.AreEqual(1, suggestions[0].Rows.Count, "The action the user already filed must not be proposed.");
        }

        [Test]
        public void SuggestCategories_OneFamilyIsNotAGrouping()
        {
            List<ConvaiActionGroup> source = SourceGroups(Row("Look At"), Row("Watch"));

            Assert.AreEqual(0, ConvaiActionsGrouping.SuggestCategories(source, _ => "Attention").Count);
        }
    }
}
