using System.Collections.Generic;
using System.Linq;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the Action Troubleshooter's mechanical one-click fixes
    ///     (remove duplicate inline action, clear unknown starting focus, add an unrepresented
    ///     scene target to Scene Knowledge), the Fix All path running them under a single Undo
    ///     group, and the "all actions disabled" validator warning surfacing as a troubleshooter
    ///     finding. Setup mutations deliberately avoid Undo registration so PerformUndo in the
    ///     Fix All test reverts exactly the fixes.
    /// </summary>
    [TestFixture]
    public class ConvaiActionTroubleshooterTests
    {
        private readonly List<Object> _cleanup = new();
        private ConvaiActionTroubleshooterWindow _window;
        private ConvaiCharacter _character;
        private ConvaiActionConfigSource _source;

        [SetUp]
        public void SetUp()
        {
            var characterObject = new GameObject("Troubleshooter Test Character");
            _cleanup.Add(characterObject);
            _character = characterObject.AddComponent<ConvaiCharacter>();
            _source = characterObject.AddComponent<ConvaiActionConfigSource>();
            characterObject.AddComponent<ConvaiActionDispatcher>();
            characterObject.AddComponent<ConvaiActionFeedbackRelay>();

            _window = ScriptableObject.CreateInstance<ConvaiActionTroubleshooterWindow>();
        }

        [TearDown]
        public void TearDown()
        {
            if (_window != null)
                Object.DestroyImmediate(_window);

            foreach (Object created in _cleanup)
            {
                if (created != null)
                    Object.DestroyImmediate(created);
            }

            _cleanup.Clear();
        }

        private ConvaiActionTroubleshooterFinding FindWithFixLabel(string fixLabel) =>
            _window.Findings.FirstOrDefault(finding => finding.Fix != null && finding.FixLabel == fixLabel);

        private static ConvaiActionDefinition Definition(string name) =>
            new() { ActionName = name, Description = "Test action.", Parameters = new List<ConvaiActionParameterDefinition>() };

        private void SetInitialAttentionWithoutUndo(string value)
        {
            using var serialized = new SerializedObject(_source);
            serialized.FindProperty("_initialAttentionObject").stringValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void DuplicateInlineDefinition_GetsRemoveDuplicateFix_ThatRemovesTheLaterEntry()
        {
            _source.ReplaceDefinitions(new List<ConvaiActionDefinition> { Definition("Wave"), Definition("Wave") });

            _window.EvaluateFor(_character);
            ConvaiActionTroubleshooterFinding finding =
                FindWithFixLabel(ConvaiActionsEditorStrings.TroubleshooterFixRemoveDuplicate.text);

            Assert.IsNotNull(finding, "Expected a Remove Duplicate fix for the second 'Wave' definition.");
            finding.Fix();
            _window.EvaluateFor(_character);

            Assert.That(_source.Definitions.Count, Is.EqualTo(1));
            Assert.That(_source.Definitions[0].ActionName, Is.EqualTo("Wave"));
            Assert.IsNull(FindWithFixLabel(ConvaiActionsEditorStrings.TroubleshooterFixRemoveDuplicate.text));
        }

        [Test]
        public void UnknownStartingFocus_GetsClearFix_ThatEmptiesTheField()
        {
            SetInitialAttentionWithoutUndo("Ghost");

            _window.EvaluateFor(_character);
            ConvaiActionTroubleshooterFinding finding =
                FindWithFixLabel(ConvaiActionsEditorStrings.TroubleshooterFixClearAttention.text);

            Assert.IsNotNull(finding, "Expected a Clear Starting Focus fix for the unknown attention name.");
            finding.Fix();
            _window.EvaluateFor(_character);

            Assert.That(_source.InitialAttentionObject, Is.Empty);
            Assert.IsNull(FindWithFixLabel(ConvaiActionsEditorStrings.TroubleshooterFixClearAttention.text));
        }

        [Test]
        public void UnrepresentedSceneTarget_GetsAddToSceneKnowledgeFix_ThatCreatesAKnownEntry()
        {
            var targetObject = new GameObject("Lantern Object");
            _cleanup.Add(targetObject);
            var target = targetObject.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";
            target.Description = "A brass lantern.";
            target.RegisterOnEnable = false; // Neither known by entry nor auto-registering.

            _window.EvaluateFor(_character);
            ConvaiActionTroubleshooterFinding finding = _window.Findings.FirstOrDefault(candidate =>
                candidate.Fix != null && candidate.Title == "Scene Target — 'Lantern'");

            Assert.IsNotNull(finding, "Expected an Add To Scene Knowledge fix for the unrepresented target.");
            Assert.That(finding.Severity, Is.EqualTo(ConvaiActionTroubleshooterSeverity.Info));
            finding.Fix();
            _window.EvaluateFor(_character);

            Assert.IsTrue(_source.Objects.Any(entry =>
                entry != null && entry.Name == "Lantern" && entry.Description == "A brass lantern."));
            Assert.IsFalse(_window.Findings.Any(candidate => candidate.Title == "Scene Target — 'Lantern'"));
        }

        [Test]
        public void AutoRegisteringSceneTarget_ProducesNoFinding()
        {
            var targetObject = new GameObject("Auto Target Object");
            _cleanup.Add(targetObject);
            var target = targetObject.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Auto Target";
            // RegisterOnEnable defaults to on and the default scope covers every character.

            _window.EvaluateFor(_character);

            Assert.IsFalse(_window.Findings.Any(candidate => candidate.Title == "Scene Target — 'Auto Target'"));
        }

        /// <summary>
        ///     The UI bug this round closes: 'Marcus' with no scene object produced two error rows
        ///     about the same entry — the validator's positional one and the Troubleshooter's own
        ///     link check — so the health pill counted one problem twice.
        /// </summary>
        [Test]
        public void UnlinkedKnownCharacter_ProducesExactlyOneFinding()
        {
            _source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "Marcus", Bio = "The dock foreman." }
            });

            _window.EvaluateFor(_character);

            List<ConvaiActionTroubleshooterFinding> aboutMarcus = _window.Findings
                .Where(finding =>
                    finding.Severity != ConvaiActionTroubleshooterSeverity.Ok &&
                    finding.Message.Contains("Marcus"))
                .ToList();

            Assert.That(
                aboutMarcus.Count, Is.EqualTo(1),
                "One unlinked entry is one problem. Rows: " +
                string.Join(" | ", aboutMarcus.Select(finding => finding.DisplayText)));
            Assert.That(aboutMarcus[0].Severity, Is.EqualTo(ConvaiActionTroubleshooterSeverity.Error));
            Assert.That(
                aboutMarcus[0].Title, Is.EqualTo("Target — 'Marcus'"),
                "The surviving row must name the entry, not its index in a list the reader cannot see.");
        }

        /// <summary>
        ///     The scene answers the name unambiguously, so the row that reports the problem is also
        ///     the row that fixes it — rather than one row explaining and a second one offering.
        /// </summary>
        [Test]
        public void UnlinkedKnownCharacter_WithOneSceneMatch_CarriesTheLinkFixOnThatSameFinding()
        {
            var marcusObject = new GameObject("Marcus");
            _cleanup.Add(marcusObject);
            _source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "Marcus", Bio = "The dock foreman." }
            });

            _window.EvaluateFor(_character);
            ConvaiActionTroubleshooterFinding finding =
                _window.Findings.FirstOrDefault(candidate => candidate.Title == "Target — 'Marcus'");

            Assert.IsNotNull(finding, "Expected the unlinked entry to be reported.");
            Assert.IsNotNull(finding.Fix, "The scene holds exactly one 'Marcus'; the row must offer to link it.");

            finding.Fix();
            _window.EvaluateFor(_character);

            Assert.That(_source.Characters[0].GameObjectReference, Is.EqualTo(marcusObject));
            Assert.IsFalse(
                _window.Findings.Any(candidate => candidate.Title == "Target — 'Marcus'"),
                "Once linked, the entry is finished and must stop being reported.");
        }

        /// <summary>
        ///     A Known entry with no object of its own is completed at run time from a same-named
        ///     scene target (<c>ConvaiCharacter.CompleteAuthoredCharacter</c>). Calling that broken
        ///     told the author to fix something that already worked.
        /// </summary>
        [Test]
        public void KnownEntryAnsweredByASceneTarget_IsNotCountedAsAnIssue()
        {
            var marcusObject = new GameObject("Marcus Object");
            _cleanup.Add(marcusObject);
            var target = marcusObject.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Marcus";
            target.Kind = ConvaiActionTargetKind.Character;
            // RegisterOnEnable defaults to on and the default scope covers every character.

            _source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "Marcus", Bio = "The dock foreman." }
            });

            _window.EvaluateFor(_character);

            Assert.IsFalse(
                _window.Findings.Any(finding =>
                    finding.Severity == ConvaiActionTroubleshooterSeverity.Error &&
                    finding.Message.Contains("Marcus")),
                "A scene target answers this entry, so nothing about it is broken.");
            Assert.IsTrue(
                _window.Findings.Any(finding =>
                    finding.Severity == ConvaiActionTroubleshooterSeverity.Info &&
                    finding.Title == "Target — 'Marcus'"),
                "It is still worth saying which object is answering, and offering to link it for good.");
        }

        /// <summary>
        ///     The count every surface shows is errors plus warnings of the one shared report — never
        ///     a per-surface subset. This is the invariant that kept "1 to fix" and "5 To Fix" on
        ///     screen at the same time.
        /// </summary>
        [Test]
        public void ReportIssueCount_MatchesItsOwnErrorAndWarningFindings()
        {
            _source.ReplaceCharacters(new List<ConvaiActionCharacterDefinition>
            {
                new() { Name = "Marcus", Bio = "The dock foreman." }
            });
            _source.ReplaceDefinitions(new List<ConvaiActionDefinition> { Definition("Wave") });

            ConvaiActionSetupReport report = ConvaiActionSetupReport.Run(_character);

            int errors = report.Findings.Count(f => f.Severity == ConvaiActionTroubleshooterSeverity.Error);
            int warnings = report.Findings.Count(f => f.Severity == ConvaiActionTroubleshooterSeverity.Warning);

            Assert.That(report.ErrorCount, Is.EqualTo(errors));
            Assert.That(report.WarningCount, Is.EqualTo(warnings));
            Assert.That(report.IssueCount, Is.EqualTo(errors + warnings));
            Assert.That(
                report.IssueCount, Is.GreaterThan(0),
                "This character really does have work outstanding; a zero here would prove nothing.");
        }

        [Test]
        public void AllActionsDisabled_ValidatorWarning_SurfacesAsTroubleshooterFinding()
        {
            ConvaiActionDefinition disabled = Definition("Wave");
            disabled.Enabled = false;
            _source.ReplaceDefinitions(new List<ConvaiActionDefinition> { disabled });

            _window.EvaluateFor(_character);

            Assert.IsTrue(
                _window.Findings.Any(finding =>
                    finding.Severity == ConvaiActionTroubleshooterSeverity.Warning &&
                    finding.Message.StartsWith("All 1 action(s) are disabled")),
                "The all-actions-disabled validator warning must surface in the troubleshooter.");
        }

        [Test]
        public void OpeningSubject_ExplicitActionsEditorCharacter_WinsOverHierarchySelection()
        {
            var sofiaObject = new GameObject("Sofia");
            _cleanup.Add(sofiaObject);
            sofiaObject.AddComponent<ConvaiCharacter>();

            GameObject resolved = ConvaiActionTroubleshooterWindow.ResolveOpeningSubject(
                _character, sofiaObject);

            Assert.That(resolved, Is.SameAs(_character.gameObject),
                "The Actions Editor's character picker must win over an unrelated Hierarchy selection.");
            Assert.That(
                ConvaiActionTroubleshooterWindow.ResolveOpeningSubject(null, sofiaObject),
                Is.SameAs(sofiaObject),
                "Inspector and menu callers still need the Hierarchy selection fallback.");
        }

        [Test]
        public void AddActionFeedback_IsOptionalIdempotentAndOneUndoStep()
        {
            Object.DestroyImmediate(_character.GetComponent<ConvaiActionFeedbackRelay>());
            Object.DestroyImmediate(_character.GetComponent<ConvaiActionDispatcher>());

            Assert.That(
                ConvaiActionsEditorWindow.EnsureActionFeedbackForCharacter(_character),
                Is.True);
            Assert.That(_character.GetComponents<ConvaiActionFeedbackRelay>(), Has.Length.EqualTo(1));
            Assert.That(_character.GetComponents<ConvaiActionDispatcher>(), Has.Length.EqualTo(1),
                "The relay's RequireComponent must leave the character able to receive action outcomes.");

            Assert.That(
                ConvaiActionsEditorWindow.EnsureActionFeedbackForCharacter(_character),
                Is.False,
                "Pressing the optional setup action twice must not create duplicates.");
            Assert.That(_character.GetComponents<ConvaiActionFeedbackRelay>(), Has.Length.EqualTo(1));

            Undo.PerformUndo();

            Assert.That(_character.GetComponent<ConvaiActionFeedbackRelay>(), Is.Null,
                "One Undo must remove the component added by the quick fix.");
        }

        [Test]
        public void FixAll_RunsEveryFixInOrder_AndOneUndoRevertsThemAll()
        {
            _source.ReplaceDefinitions(new List<ConvaiActionDefinition> { Definition("Wave"), Definition("Wave") });
            SetInitialAttentionWithoutUndo("Ghost");
            var targetObject = new GameObject("Lantern Object");
            _cleanup.Add(targetObject);
            var target = targetObject.AddComponent<ConvaiActionTarget>();
            target.TargetName = "Lantern";
            target.RegisterOnEnable = false;

            _window.EvaluateFor(_character);
            int applied = _window.RunAllFixes();

            // Other open scenes may legitimately contribute extra fixable findings when this runs
            // outside batchmode, so the count is a floor, and state assertions stay targeted.
            Assert.That(applied, Is.GreaterThanOrEqualTo(3));
            Assert.That(_source.Definitions.Count, Is.EqualTo(1));
            Assert.That(_source.InitialAttentionObject, Is.Empty);
            Assert.IsTrue(_source.Objects.Any(entry => entry != null && entry.Name == "Lantern"));

            Undo.PerformUndo();

            Assert.That(_source.Definitions.Count, Is.EqualTo(2), "One Undo must revert the whole Fix All group.");
            Assert.That(_source.InitialAttentionObject, Is.EqualTo("Ghost"));
            Assert.IsFalse(_source.Objects.Any(entry => entry != null && entry.Name == "Lantern"));
        }
    }
}
