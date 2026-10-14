using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Convai.Domain.Logging;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the pure, scene-free logic backing <see cref="ConvaiActionsEditorWindow" />:
    ///     <see cref="ConvaiActionsEditorModel" />'s status computation, grouping/filtering, and
    ///     diagnostic summarizing. No GUI/EditorWindow dependency.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsEditorModelTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith("ConvaiActionsEditorModelTests_", System.StringComparison.Ordinal))
                    Object.DestroyImmediate(gameObject);
            }
        }

        // ── MatchesFilter ──────────────────────────────────────────────────────

        [Test]
        public void MatchesFilter_EmptyFilter_MatchesEverything()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Move To" };
            Assert.IsTrue(ConvaiActionsEditorModel.MatchesFilter(definition, string.Empty));
            Assert.IsTrue(ConvaiActionsEditorModel.MatchesFilter(definition, null));
        }

        [Test]
        public void MatchesFilter_MatchesActionNameCaseInsensitive()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Move To" };
            Assert.IsTrue(ConvaiActionsEditorModel.MatchesFilter(definition, "move"));
            Assert.IsFalse(ConvaiActionsEditorModel.MatchesFilter(definition, "pick up"));
        }

        [Test]
        public void MatchesFilter_MatchesDescription()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Move To", Description = "Walks to a target." };
            Assert.IsTrue(ConvaiActionsEditorModel.MatchesFilter(definition, "walks"));
        }

        // ── ComputeStatus ──────────────────────────────────────────────────────

        [Test]
        public void ComputeStatus_ExecutableNoDiagnostics_IsReady()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_ready");
            try
            {
                var executor = go.AddComponent<RecordingExecutor>();
                var definition = new ConvaiActionDefinition { ActionName = "Move To", Executor = executor };

                ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(
                    definition, new List<ConvaiActionConfigDiagnostic>(), "Action definition #1");

                Assert.AreEqual(ConvaiActionRowStatus.Ready, status);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ComputeStatus_ExecutableWithMatchingWarning_IsNeedsAttention()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_warning");
            try
            {
                var executor = go.AddComponent<RecordingExecutor>();
                var definition = new ConvaiActionDefinition { ActionName = "Move To", Executor = executor };
                var diagnostics = new List<ConvaiActionConfigDiagnostic>
                {
                    new(ConvaiActionConfigDiagnosticSeverity.Warning, "Action 'Move To' has no description.")
                };

                ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(definition, diagnostics, "Action definition #1");

                Assert.AreEqual(ConvaiActionRowStatus.NeedsAttention, status);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ComputeStatus_UnboundWithResolvableHint_IsNeedsAttention()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Move To", ExecutorTypeHint = nameof(RecordingExecutor) };

            ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(
                definition, new List<ConvaiActionConfigDiagnostic>(), "Action definition #1");

            Assert.AreEqual(ConvaiActionRowStatus.NeedsAttention, status);
        }

        [Test]
        public void ComputeStatus_UnboundWithNoHint_IsBroken()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Move To" };

            ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(
                definition, new List<ConvaiActionConfigDiagnostic>(), "Action definition #1");

            Assert.AreEqual(ConvaiActionRowStatus.Broken, status);
        }

        [Test]
        public void ComputeStatus_MatchingError_IsBrokenEvenWhenExecutable()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_error");
            try
            {
                var executor = go.AddComponent<RecordingExecutor>();
                var definition = new ConvaiActionDefinition { ActionName = "Move To", Executor = executor };
                var diagnostics = new List<ConvaiActionConfigDiagnostic>
                {
                    new(ConvaiActionConfigDiagnosticSeverity.Error, "Duplicate action definition 'move to'.", "Action definition #1")
                };

                ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(definition, diagnostics, "Action definition #1");

                Assert.AreEqual(ConvaiActionRowStatus.Broken, status);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void ComputeStatus_UnrelatedDiagnostic_IsIgnored()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_unrelated");
            try
            {
                var executor = go.AddComponent<RecordingExecutor>();
                var definition = new ConvaiActionDefinition { ActionName = "Move To", Executor = executor };
                var diagnostics = new List<ConvaiActionConfigDiagnostic>
                {
                    new(ConvaiActionConfigDiagnosticSeverity.Error, "Action 'Pick Up' is missing a valid executor.", "Action definition #2")
                };

                ConvaiActionRowStatus status = ConvaiActionsEditorModel.ComputeStatus(definition, diagnostics, "Action definition #1");

                Assert.AreEqual(ConvaiActionRowStatus.Ready, status);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── Summarize ──────────────────────────────────────────────────────────

        [Test]
        public void Summarize_CountsErrorsAndWarningsSeparately()
        {
            var diagnostics = new List<ConvaiActionConfigDiagnostic>
            {
                new(ConvaiActionConfigDiagnosticSeverity.Error, "e1"),
                new(ConvaiActionConfigDiagnosticSeverity.Error, "e2"),
                new(ConvaiActionConfigDiagnosticSeverity.Warning, "w1"),
                new(ConvaiActionConfigDiagnosticSeverity.Info, "i1")
            };

            (int errors, int warnings) = ConvaiActionsEditorModel.Summarize(diagnostics);

            Assert.AreEqual(2, errors);
            Assert.AreEqual(1, warnings);
        }

        [Test]
        public void Summarize_NullOrEmpty_ReturnsZeroes()
        {
            (int errors, int warnings) = ConvaiActionsEditorModel.Summarize(null);
            Assert.AreEqual(0, errors);
            Assert.AreEqual(0, warnings);
        }

        // ── BuildGroups / HasAuthoredContent ───────────────────────────────────

        [Test]
        public void BuildGroups_InlineOnly_ProducesThisCharacterGroupInOrder()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_inline");
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                var executor = go.AddComponent<RecordingExecutor>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Move To", Executor = executor },
                    new() { ActionName = "Pick Up" }
                });

                List<ConvaiActionGroup> groups = ConvaiActionsEditorModel.BuildGroups(source, new List<ConvaiActionConfigDiagnostic>(), null);

                Assert.AreEqual(1, groups.Count);
                Assert.AreEqual("This Character", groups[0].Title);
                Assert.AreEqual(2, groups[0].Rows.Count);
                Assert.AreEqual("Move To", groups[0].Rows[0].DisplayName);
                Assert.IsFalse(groups[0].Rows[0].IsShared);
                Assert.AreEqual(0, groups[0].Rows[0].OwnerIndex);
                Assert.AreEqual(1, groups[0].Rows[1].OwnerIndex);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildGroups_WithActionSet_AddsSharedGroupCarryingTrueSetIndices()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_set");
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                ReplaceSetDefinitions(set, new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Nod", ExecutorTypeHint = nameof(RecordingExecutor) }
                });
                source.ReplaceActionSets(new List<ConvaiActionSet> { set });

                List<ConvaiActionGroup> groups = ConvaiActionsEditorModel.BuildGroups(source, new List<ConvaiActionConfigDiagnostic>(), null);

                Assert.AreEqual(2, groups.Count);
                Assert.AreEqual("This Character", groups[0].Title);
                Assert.AreEqual(0, groups[0].Rows.Count);
                Assert.AreSame(set, groups[1].Set);
                Assert.AreEqual(1, groups[1].Rows.Count);
                Assert.IsTrue(groups[1].Rows[0].IsShared);
                Assert.AreSame(set, groups[1].Rows[0].OwningSet);
                Assert.AreEqual(0, groups[1].Rows[0].OwnerIndex,
                    "A shared row must carry its true index inside the owning set so reorder/remove target the right entry.");
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildGroups_SearchFilter_ExcludesNonMatchingRows()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_filter");
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Move To" },
                    new() { ActionName = "Pick Up" }
                });

                List<ConvaiActionGroup> groups = ConvaiActionsEditorModel.BuildGroups(source, new List<ConvaiActionConfigDiagnostic>(), "pick");

                Assert.AreEqual(1, groups[0].Rows.Count);
                Assert.AreEqual("Pick Up", groups[0].Rows[0].DisplayName);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void BuildGroups_BlankActionName_UsesUnnamedFallbackDisplayName()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_unnamed");
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceDefinitions(new List<ConvaiActionDefinition> { new() { ActionName = string.Empty } });

                List<ConvaiActionGroup> groups = ConvaiActionsEditorModel.BuildGroups(source, new List<ConvaiActionConfigDiagnostic>(), null);

                Assert.AreEqual("(unnamed action)", groups[0].Rows[0].DisplayName);
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HasAuthoredContent_NoInlineNoSets_IsFalse()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_empty");
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                Assert.IsFalse(ConvaiActionsEditorModel.HasAuthoredContent(source));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HasAuthoredContent_OnlyActionSetHasEntries_IsTrue()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_setonly");
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                ReplaceSetDefinitions(set, new List<ConvaiActionDefinition> { new() { ActionName = "Nod" } });
                source.ReplaceActionSets(new List<ConvaiActionSet> { set });

                Assert.IsTrue(ConvaiActionsEditorModel.HasAuthoredContent(source));
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HasAuthoredContent_AssignedButEmptySet_IsTrue()
        {
            // Regression: "create a new Action Set and use it" produces exactly this state. Counting
            // only definitions sent the window back to the "Add your first action" hero, which hides
            // the whole left pane — so the set the user just made became invisible and unreachable.
            GameObject go = new("ConvaiActionsEditorModelTests_emptyset");
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceActionSets(new List<ConvaiActionSet> { set });

                Assert.IsTrue(ConvaiActionsEditorModel.HasAuthoredContent(source),
                    "An assigned set must keep the action list visible even before it holds any actions.");
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void HasAuthoredContent_OnlyNullSetSlot_IsFalse()
        {
            // A null slot renders nothing, so on its own it must not suppress the empty-state hero.
            GameObject go = new("ConvaiActionsEditorModelTests_nullset");
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceActionSets(new List<ConvaiActionSet> { null });

                Assert.IsFalse(ConvaiActionsEditorModel.HasAuthoredContent(source));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }

        // ── Action Set assignment helpers ──────────────────────────────────────

        [Test]
        public void IsSetAssigned_DistinguishesAssignedFromUnassigned()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_assigned");
            ConvaiActionSet assigned = ConvaiActionSet.CreateDefault();
            ConvaiActionSet other = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceActionSets(new List<ConvaiActionSet> { assigned });

                Assert.IsTrue(ConvaiActionsEditorModel.IsSetAssigned(source, assigned));
                Assert.IsFalse(ConvaiActionsEditorModel.IsSetAssigned(source, other));
                Assert.IsFalse(ConvaiActionsEditorModel.IsSetAssigned(source, null));
                Assert.IsFalse(ConvaiActionsEditorModel.IsSetAssigned(null, assigned));
            }
            finally
            {
                Object.DestroyImmediate(other);
                Object.DestroyImmediate(assigned);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CountAssignedSets_IgnoresNullSlots()
        {
            GameObject go = new("ConvaiActionsEditorModelTests_count");
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource source = go.AddComponent<ConvaiActionConfigSource>();
                source.ReplaceActionSets(new List<ConvaiActionSet> { set, null });

                Assert.AreEqual(1, ConvaiActionsEditorModel.CountAssignedSets(source));
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(go);
            }
        }

        [Test]
        public void CountCharactersUsingSet_CountsOnlyAssignedSources()
        {
            GameObject userGo = new("ConvaiActionsEditorModelTests_user");
            GameObject bystanderGo = new("ConvaiActionsEditorModelTests_bystander");
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                ConvaiActionConfigSource user = userGo.AddComponent<ConvaiActionConfigSource>();
                ConvaiActionConfigSource bystander = bystanderGo.AddComponent<ConvaiActionConfigSource>();
                user.ReplaceActionSets(new List<ConvaiActionSet> { set });

                var all = new List<ConvaiActionConfigSource> { user, bystander };
                Assert.AreEqual(1, ConvaiActionsEditorModel.CountCharactersUsingSet(all, set));
                Assert.AreEqual(0, ConvaiActionsEditorModel.CountCharactersUsingSet(all, null));
                Assert.AreEqual(0, ConvaiActionsEditorModel.CountCharactersUsingSet(null, set));
            }
            finally
            {
                Object.DestroyImmediate(set);
                Object.DestroyImmediate(bystanderGo);
                Object.DestroyImmediate(userGo);
            }
        }

        [Test]
        public void GetActionSetDisplayName_BlankName_FallsBackToPositional()
        {
            ConvaiActionSet set = ConvaiActionSet.CreateDefault();
            try
            {
                Assert.AreEqual("Action Set #2", ConvaiActionsEditorModel.GetActionSetDisplayName(set, 1));
            }
            finally
            {
                Object.DestroyImmediate(set);
            }
        }

        // ── Helpers ────────────────────────────────────────────────────────────

        private static void ReplaceSetDefinitions(ConvaiActionSet set, List<ConvaiActionDefinition> definitions)
        {
            FieldInfo field = typeof(ConvaiActionSet).GetField("_definitions", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, "ConvaiActionSet._definitions should exist.");
            field.SetValue(set, definitions);
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
