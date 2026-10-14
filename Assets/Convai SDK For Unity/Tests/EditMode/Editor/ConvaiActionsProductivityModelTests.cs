using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the pure logic behind the Actions Editor window's productivity pack
    ///     (<see cref="ConvaiActionsProductivityModel" /> and <see cref="ConvaiActionsClipboard" />):
    ///     detached copy/paste snapshots, collision-safe naming, inline→Action Set conversion, the
    ///     list scope filter predicate, and multi-selection persistence keys. No GUI/EditorWindow
    ///     dependency.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsProductivityModelTests
    {
        private const string ObjectPrefix = "ConvaiActionsProductivityModelTests_";

        [OneTimeSetUp]
        public void OneTimeSetUp() => ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            ConvaiActionsClipboard.Clear();
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(ObjectPrefix, System.StringComparison.Ordinal))
                    Object.DestroyImmediate(gameObject);
            }
        }

        // ── Clipboard snapshot round-trip ──────────────────────────────────────

        [Test]
        public void Clipboard_CopyWithBoundExecutor_PasteCloneCarriesTypeHintNotSceneReference()
        {
            GameObject go = new(ObjectPrefix + "clipboard");
            var executor = go.AddComponent<RecordingExecutor>();
            var original = new ConvaiActionDefinition
            {
                ActionName = "Wave",
                Description = "Waves at the player.",
                Executor = executor,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "speed", Description = "How fast." }
                }
            };

            ConvaiActionsClipboard.Copy(original);
            ConvaiActionDefinition pasted = ConvaiActionsClipboard.CreatePasteClone();

            Assert.IsTrue(ConvaiActionsClipboard.HasContent);
            Assert.IsNotNull(pasted);
            Assert.IsNull(pasted.Executor, "A paste clone must never alias a scene component.");
            Assert.AreEqual(nameof(RecordingExecutor), pasted.ExecutorTypeHint);
            Assert.AreEqual("Wave", pasted.ActionName);
            Assert.AreEqual(1, pasted.Parameters.Count);
            Assert.AreEqual("speed", pasted.Parameters[0].Name);
        }

        [Test]
        public void Clipboard_PastingTwice_YieldsIndependentInstances()
        {
            ConvaiActionsClipboard.Copy(new ConvaiActionDefinition
            {
                ActionName = "Wave",
                Parameters = new List<ConvaiActionParameterDefinition> { new() { Name = "speed" } }
            });

            ConvaiActionDefinition first = ConvaiActionsClipboard.CreatePasteClone();
            ConvaiActionDefinition second = ConvaiActionsClipboard.CreatePasteClone();

            Assert.AreNotSame(first, second);
            first.ActionName = "Renamed";
            first.Parameters[0].Name = "changed";
            Assert.AreEqual("Wave", second.ActionName);
            Assert.AreEqual("speed", second.Parameters[0].Name);
        }

        [Test]
        public void Clipboard_CopyPreservesExistingHint_AndClearEmpties()
        {
            ConvaiActionsClipboard.Copy(new ConvaiActionDefinition
            {
                ActionName = "Nod",
                ExecutorTypeHint = "ConvaiWaitActionExecutor"
            });
            Assert.AreEqual("ConvaiWaitActionExecutor", ConvaiActionsClipboard.CreatePasteClone().ExecutorTypeHint);

            ConvaiActionsClipboard.Clear();
            Assert.IsFalse(ConvaiActionsClipboard.HasContent);
            Assert.IsNull(ConvaiActionsClipboard.CreatePasteClone());
        }

        // ── Collision-safe naming ──────────────────────────────────────────────

        [Test]
        public void MakeUniqueActionName_FreeName_ReturnsUnchanged()
        {
            string result = ConvaiActionsProductivityModel.MakeUniqueActionName(
                "Wave", new List<string> { "Nod" });
            Assert.AreEqual("Wave", result);
        }

        [Test]
        public void MakeUniqueActionName_Collision_AppendsCopySuffixThenCounter()
        {
            var names = new List<string> { "Wave", "Wave Copy" };
            Assert.AreEqual("Wave Copy 2", ConvaiActionsProductivityModel.MakeUniqueActionName("Wave", names));

            names.Add("Wave Copy 2");
            Assert.AreEqual("Wave Copy 3", ConvaiActionsProductivityModel.MakeUniqueActionName("Wave", names));
        }

        [Test]
        public void MakeUniqueActionName_CollisionIsCaseInsensitive()
        {
            string result = ConvaiActionsProductivityModel.MakeUniqueActionName(
                "wave", new List<string> { "WAVE" });
            Assert.AreEqual("wave Copy", result);
        }

        [Test]
        public void MakeUniqueActionName_BlankName_FallsBackToNewAction()
        {
            Assert.AreEqual("New Action",
                ConvaiActionsProductivityModel.MakeUniqueActionName("   ", new List<string>()));
            Assert.AreEqual("New Action Copy",
                ConvaiActionsProductivityModel.MakeUniqueActionName(null, new List<string> { "New Action" }));
        }

        [Test]
        public void MakeDuplicateActionName_AlwaysCarriesCopySuffix()
        {
            Assert.AreEqual("Wave Copy",
                ConvaiActionsProductivityModel.MakeDuplicateActionName("Wave", new List<string> { "Nod" }));
            Assert.AreEqual("Wave Copy 2",
                ConvaiActionsProductivityModel.MakeDuplicateActionName("Wave", new List<string> { "Wave", "Wave Copy" }));
        }

        // ── Inline → Action Set conversion ─────────────────────────────────────

        [Test]
        public void ConvertForActionSet_BoundExecutor_BecomesHint_NoBehaviorLost()
        {
            GameObject go = new(ObjectPrefix + "convert");
            var executor = go.AddComponent<RecordingExecutor>();
            var definition = new ConvaiActionDefinition { ActionName = "Wave", Executor = executor };

            ConvaiActionDefinition converted =
                ConvaiActionsProductivityModel.ConvertForActionSet(definition, out bool behaviorLost);

            Assert.IsFalse(behaviorLost);
            Assert.IsNull(converted.Executor);
            Assert.AreEqual(nameof(RecordingExecutor), converted.ExecutorTypeHint);
        }

        [Test]
        public void ConvertForActionSet_ExistingHintSurvives_NoBehaviorLost()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Nod", ExecutorTypeHint = "ConvaiWaitActionExecutor" };

            ConvaiActionDefinition converted =
                ConvaiActionsProductivityModel.ConvertForActionSet(definition, out bool behaviorLost);

            Assert.IsFalse(behaviorLost);
            Assert.AreEqual("ConvaiWaitActionExecutor", converted.ExecutorTypeHint);
        }

        [Test]
        public void ConvertForActionSet_NoExecutorAndNoHint_ReportsBehaviorLost()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Wave" };

            ConvaiActionsProductivityModel.ConvertForActionSet(definition, out bool behaviorLost);

            Assert.IsTrue(behaviorLost);
        }

        // ── Collision domains ──────────────────────────────────────────────────

        [Test]
        public void CollectEffectiveActionNames_SpansInlineAndAssignedSets()
        {
            ConvaiActionConfigSource source = CreateSource("names");
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "  Wave  " },
                null,
                new() { ActionName = "" }
            });

            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            set.ReplaceDefinitions(new List<ConvaiActionDefinition> { new() { ActionName = "Nod" } });
            source.ReplaceActionSets(new List<ConvaiActionSet> { set, null });

            List<string> names = ConvaiActionsProductivityModel.CollectEffectiveActionNames(source);

            Assert.AreEqual(2, names.Count);
            CollectionAssert.Contains(names, "Wave");
            CollectionAssert.Contains(names, "Nod");
            Object.DestroyImmediate(set);
        }

        [Test]
        public void CollectSetActionNames_ReadsOnlyTheSet()
        {
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            set.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Nod" },
                new() { ActionName = " Point " }
            });

            List<string> names = ConvaiActionsProductivityModel.CollectSetActionNames(set);

            Assert.AreEqual(2, names.Count);
            CollectionAssert.AreEquivalent(new[] { "Nod", "Point" }, names);
            Object.DestroyImmediate(set);
        }

        // ── Scope filter predicate ─────────────────────────────────────────────

        [Test]
        public void MatchesListFilter_All_PassesEverything()
        {
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.All, ConvaiActionRowStatus.Broken, true, false));
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.All, ConvaiActionRowStatus.Ready, false, true));
        }

        [Test]
        public void MatchesListFilter_NeedsAttention_PassesOnlyNonReady()
        {
            Assert.IsFalse(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.NeedsAttention, ConvaiActionRowStatus.Ready, false, true));
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.NeedsAttention, ConvaiActionRowStatus.NeedsAttention, false, true));
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.NeedsAttention, ConvaiActionRowStatus.Broken, false, true));
        }

        [Test]
        public void MatchesListFilter_NotOffered_PassesOnlyDisabledRows()
        {
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.NotOffered, ConvaiActionRowStatus.Ready, false, false));
            Assert.IsFalse(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.NotOffered, ConvaiActionRowStatus.Ready, false, true));
        }

        [Test]
        public void MatchesListFilter_OwnershipFilters_SplitOnShared()
        {
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.ThisCharacter, ConvaiActionRowStatus.Ready, false, true));
            Assert.IsFalse(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.ThisCharacter, ConvaiActionRowStatus.Ready, true, true));
            Assert.IsTrue(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.FromActionSets, ConvaiActionRowStatus.Ready, true, true));
            Assert.IsFalse(ConvaiActionsProductivityModel.MatchesListFilter(
                ConvaiActionsListFilter.FromActionSets, ConvaiActionRowStatus.Ready, false, true));
        }

        [Test]
        public void BuildGroups_ScopeFilter_CombinesWithTextSearch()
        {
            ConvaiActionConfigSource source = CreateSource("filter");
            var offered = new ConvaiActionDefinition { ActionName = "Wave", ExecutorTypeHint = nameof(RecordingExecutor) };
            var withheld = new ConvaiActionDefinition { ActionName = "Wave Slowly", ExecutorTypeHint = nameof(RecordingExecutor) };
            withheld.Enabled = false;
            source.ReplaceDefinitions(new List<ConvaiActionDefinition> { offered, withheld });

            List<ConvaiActionGroup> groups = ConvaiActionsEditorModel.BuildGroups(
                source, null, "wave", ConvaiActionsListFilter.NotOffered);

            Assert.AreEqual(1, groups[0].Rows.Count);
            Assert.AreSame(withheld, groups[0].Rows[0].Definition);
        }

        // ── Multi-selection persistence helpers ────────────────────────────────

        [Test]
        public void CollectDefinitionsByContextKey_MapsInlineAndSetRowsToTheirValidatorKeys()
        {
            ConvaiActionConfigSource source = CreateSource("contextkeys");
            var first = new ConvaiActionDefinition { ActionName = "Wave" };
            var second = new ConvaiActionDefinition { ActionName = "Nod" };
            source.ReplaceDefinitions(new List<ConvaiActionDefinition> { first, second });

            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            set.name = "Props";
            var shared = new ConvaiActionDefinition { ActionName = "Point" };
            set.ReplaceDefinitions(new List<ConvaiActionDefinition> { shared });
            source.ReplaceActionSets(new List<ConvaiActionSet> { set });

            var map = new Dictionary<string, ConvaiActionDefinition>();
            ConvaiActionsProductivityModel.CollectDefinitionsByContextKey(source, map);

            Assert.AreSame(first, map["Action definition #1"]);
            Assert.AreSame(second, map["Action definition #2"]);
            Assert.AreSame(shared, map["Action set 'Props' definition #1"]);
            Object.DestroyImmediate(set);
        }

        [Test]
        public void IsDefinitionAuthored_FindsInlineAndSetRows_RejectsForeign()
        {
            ConvaiActionConfigSource source = CreateSource("authored");
            var inline = new ConvaiActionDefinition { ActionName = "Wave" };
            source.ReplaceDefinitions(new List<ConvaiActionDefinition> { inline });

            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            var shared = new ConvaiActionDefinition { ActionName = "Nod" };
            set.ReplaceDefinitions(new List<ConvaiActionDefinition> { shared });
            source.ReplaceActionSets(new List<ConvaiActionSet> { set });

            Assert.IsTrue(ConvaiActionsProductivityModel.IsDefinitionAuthored(source, inline));
            Assert.IsTrue(ConvaiActionsProductivityModel.IsDefinitionAuthored(source, shared));
            Assert.IsFalse(ConvaiActionsProductivityModel.IsDefinitionAuthored(
                source, new ConvaiActionDefinition { ActionName = "Wave" }));
            Assert.IsFalse(ConvaiActionsProductivityModel.IsDefinitionAuthored(source, null));
            Object.DestroyImmediate(set);
        }

        // ── Fixture helpers ────────────────────────────────────────────────────

        private static ConvaiActionConfigSource CreateSource(string suffix)
        {
            GameObject go = new(ObjectPrefix + suffix);
            return go.AddComponent<ConvaiActionConfigSource>();
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
