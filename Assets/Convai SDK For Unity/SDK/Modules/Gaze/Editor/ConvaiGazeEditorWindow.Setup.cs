using System.Collections.Generic;
using Convai.Editor.UI;
using Convai.Modules.Gaze.Core;
using Convai.Modules.Gaze.Core.Solvers;
using UnityEditor;
using UnityEngine;

namespace Convai.Modules.Gaze.Editor
{
    /// <summary>One capability row on the Setup tab: the model plus the checkbox's live state.</summary>
    internal struct GazeCapabilityInfoRow
    {
        public GazeCapabilityId Id;
        public string DisplayName;
        public string Description;
        public bool Present;
    }

    internal sealed partial class ConvaiGazeEditorWindow
    {
        private void RefreshCapabilityRows()
        {
            Transform root = GazeSetupService.ResolveRoot(_controller);
            // Reused scratch list: this used to allocate a fresh one inside OnGUI.
            List<GazeCapabilityInfo> infos = _capabilityScratch;
            GazeCapabilities.Evaluate(root, infos);

            for (int i = 0; i < infos.Count; i++)
            {
                _capabilityRows.Add(new GazeCapabilityInfoRow
                {
                    Id = infos[i].Id,
                    DisplayName = infos[i].DisplayName,
                    Description = infos[i].Description,
                    Present = infos[i].IsPresent
                });
            }
        }

        private void DrawSetupMode()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(14f);
                using (new EditorGUILayout.VerticalScope())
                {
                    DrawChecklist();
                    DrawBlockerCallout();
                    DrawCapabilityRows();
                    DrawRigReport();
                    DrawTroubleshooterFindings();
                    GUILayout.Space(16f);
                }
                GUILayout.Space(14f);
            }
        }

        // ------------------------------------------------------------------ checklist

        private void DrawChecklist()
        {
            DrawSectionTitle(GazeEditorStrings.SetupChecklistTitle);

            if (_preflight.Checks == null) return;

            for (int i = 0; i < _preflight.Checks.Count; i++)
            {
                GazeCheck check = _preflight.Checks[i];
                (Color tint, string word) = CheckStatus(check.State);
                ConvaiEditorTheme.CheckRow(tint, word, check.Label, check.Detail);

                string fixLabel = GazeSetupService.DescribeFix(check.Fix);
                if (fixLabel == null) continue;

                ButtonScratch.text = fixLabel;
                if (ConvaiEditorTheme.GhostButtonRow(ButtonScratch) == 0)
                {
                    GazeSetupService.ApplyFix(_controller, check.Fix);
                    InvalidateModels();
                }
                GUILayout.Space(4f);
            }
        }

        /// <summary>
        ///     A check's dot colour and status word. A fixable row is an offer, not a problem, so it
        ///     keeps the info tint rather than the warning one.
        /// </summary>
        private static (Color tint, string word) CheckStatus(GazeCheckState state) => state switch
        {
            GazeCheckState.Ok => (ConvaiEditorTheme.StatusReady, null),
            GazeCheckState.Fixable => (ConvaiEditorTheme.StatusInfo, "Suggested"),
            GazeCheckState.Blocked => (ConvaiEditorTheme.StatusError, "Blocked"),
            _ => (ConvaiEditorTheme.StatusIdle, "Optional")
        };

        /// <summary>Reused button content for labels that come from data — a fix, a bone, a scene object.</summary>
        private static readonly GUIContent ButtonScratch = new();

        private void DrawBlockerCallout()
        {
            if (!_preflight.TryGetBlocker(out GazeCheck blocker)) return;

            ConvaiEditorTheme.ErrorBox(
                "One thing needs you first",
                $"{blocker.Detail}.\n\nGaze rotates the character's head and eye bones, so it cannot run " +
                "until it can find them. Everything else on this page works once this is resolved.");
        }

        // ------------------------------------------------------------------ capabilities

        /// <summary>
        ///     The direct answer to "what are the other components, and do I need them?" — every
        ///     optional capability as a checkbox with one sentence, and never a class name.
        /// </summary>
        private void DrawCapabilityRows()
        {
            DrawSectionTitle(GazeEditorStrings.SetupExtrasTitle);
            DrawBody(GazeEditorStrings.SetupExtrasBody);
            GUILayout.Space(4f);

            for (int i = 0; i < _capabilityRows.Count; i++)
            {
                GazeCapabilityInfoRow row = _capabilityRows[i];

                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUI.BeginChangeCheck();
                    bool next = EditorGUILayout.ToggleLeft(
                        new GUIContent(row.DisplayName, row.Description), row.Present);
                    if (EditorGUI.EndChangeCheck()) ToggleCapability(row.Id, next);

                    // Laid out rather than drawn into a fixed 14px row: a description that wraps
                    // to a second line was cut off and ran into the next checkbox.
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        GUILayout.Space(18f);
                        GUILayout.Label(row.Description, ConvaiEditorTheme.CaptionWrapped);
                    }
                }
            }
        }

        private void ToggleCapability(GazeCapabilityId id, bool wanted)
        {
            var selection = new List<GazeCapabilityId>(_capabilityRows.Count);
            for (int i = 0; i < _capabilityRows.Count; i++)
            {
                GazeCapabilityInfoRow row = _capabilityRows[i];
                bool keep = row.Id == id ? wanted : row.Present;
                if (keep) selection.Add(row.Id);
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(wanted ? "Add Gaze Capability" : "Remove Gaze Capability");
            GazeSetupService.ApplyCapabilities(_controller, selection, null);
            Undo.CollapseUndoOperations(undoGroup);

            InvalidateModels();
            Repaint();
        }

        // ------------------------------------------------------------------ rig report

        /// <summary>
        ///     What the gaze stack actually resolved on this character, and the facing-direction
        ///     check — the module's hardest-to-discover requirement, shown as a measured angle
        ///     rather than a paragraph of prose.
        /// </summary>
        /// <remarks>
        ///     Both answers come from <see cref="GazeSetupService" /> rather than being measured
        ///     here, so this report, the component inspector and an AI assistant cannot describe the
        ///     same rig differently. It is also why the report now works before Play Mode: the
        ///     service resolves the bones the way the runtime will, instead of waiting for the gaze
        ///     chain to bind.
        /// </remarks>
        private void DrawRigReport()
        {
            DrawSectionTitle(GazeEditorStrings.SetupRigReportTitle);

            // Once bound, the chain is authoritative — it is the rig gaze is actually driving.
            GazeChainCalibration chain = _controller.Chain;
            if (chain != null && chain.IsBound)
            {
                DrawRigRow("Head", chain.Head);
                DrawRigRow("Neck", chain.Neck);
                DrawRigRow("Left eye", chain.LeftEye);
                DrawRigRow("Right eye", chain.RightEye);
            }
            else
            {
                GazeBoneReport bones = GazeSetupService.ResolveBones(_controller);
                DrawRigRow("Head", bones.Head);
                DrawRigRow("Neck", bones.Neck);
                DrawRigRow("Left eye", bones.LeftEye);
                DrawRigRow("Right eye", bones.RightEye);
            }

            ConvaiEditorControls.GroupCaption(GazeEditorStrings.ForwardAxisTitle);

            GazeFacingReport facing = GazeSetupService.InspectFacing(_controller);
            if (facing.Measured)
            {
                bool pass = facing.State == GazeFacingState.Pass;
                Rect row = GUILayoutUtility.GetRect(0f, 18f, GUILayout.ExpandWidth(true));
                ConvaiEditorTheme.StatusDot(
                    new Rect(row.x, row.y, StatusDotColumn, row.height),
                    pass ? ConvaiEditorTheme.StatusReady : ConvaiEditorTheme.StatusError);
                GUI.Label(
                    new Rect(row.x + StatusDotColumn, row.y, Mathf.Max(0f, row.width - StatusDotColumn), row.height),
                    $"{facing.AngleDegrees:0.0}° from the character's forward",
                    ConvaiEditorStyles.MicroLabel);
            }

            DrawBody(facing.Detail);
        }

        /// <summary>Width of the column a status dot sits in, left of its reading.</summary>
        private const float StatusDotColumn = 14f;

        /// <summary>Width of a rig row's label column (Head, Neck, the eyes).</summary>
        private const float RigLabelWidth = 90f;

        private static void DrawRigRow(string label, Transform bone)
        {
            Rect row = GUILayoutUtility.GetRect(0f, 20f, GUILayout.ExpandWidth(true));
            GUI.Label(new Rect(row.x, row.y, RigLabelWidth, row.height), label, ConvaiEditorStyles.RowLabel);
            var value = new Rect(row.x + RigLabelWidth, row.y, Mathf.Max(0f, row.width - RigLabelWidth), row.height);

            if (bone == null)
                GUI.Label(value, "not found", ConvaiEditorTheme.CaptionWrapped);
            else
            {
                ButtonScratch.text = bone.name;
                value.width = Mathf.Min(ConvaiEditorTheme.GhostButtonWidth(ButtonScratch), value.width);
                if (ConvaiEditorTheme.GhostButton(value, ButtonScratch))
                {
                    EditorGUIUtility.PingObject(bone.gameObject);
                    Selection.activeGameObject = bone.gameObject;
                }
            }

            GUILayout.Space(2f);
        }

        // ------------------------------------------------------------------ findings

        private void DrawTroubleshooterFindings()
        {
            bool any = false;
            for (int i = 0; i < _findings.Count; i++)
                if (_findings[i].Severity >= GazeSetupSeverity.Warning) { any = true; break; }

            if (!any) return;

            DrawSectionTitle("Worth knowing");
            for (int i = 0; i < _findings.Count; i++)
            {
                GazeSetupFinding finding = _findings[i];
                if (finding.Severity < GazeSetupSeverity.Warning) continue;

                if (finding.Severity == GazeSetupSeverity.Error)
                    ConvaiEditorTheme.ErrorBox(finding.Title, finding.Message);
                else
                    ConvaiEditorTheme.WarningBox(finding.Title, finding.Message);
            }
        }
    }
}
