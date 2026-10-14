using Convai.Editor.UI;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using UnityEditor;
using UnityEngine;

namespace Convai.Modules.BodyAnimation.Editor
{
    /// <summary>
    ///     Setup mode: the same preflight checklist and finding model the component inspector
    ///     renders, in full width. This is a mirror, not the primary surface — a user who never
    ///     opens this window must still be able to complete setup entirely from the inspector
    ///    . Fixes route through the same two services every other surface
    ///     uses (<see cref="BodyAnimationSetupService" /> for character-scoped repairs,
    ///     <see cref="BodyAnimationFixes" /> for set-scoped ones), so a fix behaves identically
    ///     wherever it is pressed.
    /// </summary>
    internal sealed partial class ConvaiBodyAnimationEditorWindow
    {
        private static readonly GUIContent PreflightHeaderContent =
            new(BodyAnimationEditorStrings.SetupPreflightHeader);

        private static readonly GUIContent FindingsHeaderContent =
            new(BodyAnimationEditorStrings.SetupFindingsHeader);

        private static readonly GUIContent IncludeMovementContent = new(
            BodyAnimationEditorStrings.SetupIncludeMovementLabel,
            BodyAnimationEditorStrings.SetupIncludeMovementTooltip);

        private static readonly GUIContent SetupRunContent = new(BodyAnimationEditorStrings.SetupRunButton);

        private static readonly GUIContent SetupBlockedContent = new(BodyAnimationEditorStrings.SetupBlockedButton);

        private bool _setupIncludeMovement = true;

        private void DrawSetupMode()
        {
            if (_controller == null)
            {
                DrawCenteredMessage(BodyAnimationEditorStrings.SetupNoControllerMessage);
                return;
            }

            using (new EditorGUILayout.VerticalScope(GUILayout.Width(Mathf.Min(720f, position.width - LeftPaneWidth - 40f))))
            {
                GUILayout.Space(6f);
                GUILayout.Label(BodyAnimationEditorStrings.SetupModeTitle, ConvaiEditorStyles.SelectedTitle);
                GUILayout.Label(BodyAnimationEditorStrings.SetupModeIntro, ConvaiEditorStyles.MutedWrapped);
                GUILayout.Space(10f);

                DrawPreflightSection();
                DrawFindingsSection();
            }
        }

        private void DrawPreflightSection()
        {
            BodyAnimationPreflight preflight = BodyAnimationSetupService.Inspect(_controller);

            using (ConvaiEditorFrame.Card())
            {
                ConvaiEditorFrame.SectionHeader(
                    ConvaiEditorGlyphs.Validation, PreflightHeaderContent);

                for (int i = 0; i < preflight.Checks.Count; i++)
                    DrawPreflightRow(preflight.Checks[i]);

                GUILayout.Space(6f);
                _setupIncludeMovement = EditorGUILayout.ToggleLeft(IncludeMovementContent, _setupIncludeMovement);
                GUILayout.Space(6f);

                bool blocked = preflight.HasBlocker;
                using (new EditorGUI.DisabledScope(blocked || preflight.IsConfigured))
                {
                    if (ConvaiEditorControls.PrimaryButtonLayout(
                            blocked ? SetupBlockedContent : SetupRunContent, 26f))
                        RunSetup();
                }
            }
        }

        /// <summary>
        ///     Runs setup after the current IMGUI pass, never inside it — a modal raised from within a
        ///     layout scope discards the layout state the enclosing scope is about to close, which
        ///     leaves the surface throwing on every later repaint with no way to recover.
        /// </summary>
        private void RunSetup()
        {
            ConvaiBodyAnimationController controller = _controller;
            bool includeMovement = _setupIncludeMovement;

            EditorApplication.delayCall += () =>
            {
                if (controller == null) return;

                BodyAnimationSetupResult result = BodyAnimationSetupService.Apply(
                    controller, new BodyAnimationSetupOptions { IncludeMovement = includeMovement });

                var message = new System.Text.StringBuilder(result.Summary);
                for (int i = 0; i < result.Notes.Count; i++)
                    message.Append("\n\n• ").Append(result.Notes[i]);

                EditorUtility.DisplayDialog(BodyAnimationEditorStrings.WindowTitle, message.ToString(), "OK");
                RefreshFindings();
            };
        }

        /// <summary>
        ///     One preflight check as a Convai check row. Shared with the component inspector's
        ///     checklist, so the two surfaces read as one checklist rendered at two widths.
        /// </summary>
        internal static void DrawPreflightRow(BodyAnimationCheck check)
        {
            Color color = check.State switch
            {
                BodyAnimationCheckState.Ok => ConvaiEditorTheme.StatusReady,
                BodyAnimationCheckState.Fixable => ConvaiEditorTheme.StatusInfo,
                BodyAnimationCheckState.Blocked => ConvaiEditorTheme.StatusError,
                BodyAnimationCheckState.NeedsContent => ConvaiEditorTheme.StatusWarn,
                _ => ConvaiEditorTheme.StatusIdle
            };

            string statusWord = check.State switch
            {
                BodyAnimationCheckState.Blocked => "Blocked",
                BodyAnimationCheckState.Fixable or BodyAnimationCheckState.NeedsContent => "Check this",
                _ => null
            };

            ConvaiEditorControls.CheckRow(color, statusWord, check.Label, check.Detail);
        }

        private void DrawFindingsSection()
        {
            bool anyActionable = false;
            for (int i = 0; i < _findings.Count; i++)
            {
                if (_findings[i].Severity > BodyAnimationTroubleshooterSeverity.Ok) anyActionable = true;
            }

            if (!anyActionable)
            {
                ConvaiEditorFrame.InfoBox(
                    BodyAnimationEditorStrings.SetupFindingsHeader,
                    BodyAnimationEditorStrings.SetupAllGoodMessage);
                return;
            }

            ConvaiBodyAnimationSet set = BodyAnimationSetupService.ResolveAssignedSet(_controller);

            using (ConvaiEditorFrame.Card())
            {
                ConvaiEditorFrame.SectionHeader(ConvaiEditorGlyphs.Discovery, FindingsHeaderContent);

                for (int i = 0; i < _findings.Count; i++)
                {
                    BodyAnimationTroubleshooterFinding finding = _findings[i];
                    if (finding.Severity == BodyAnimationTroubleshooterSeverity.Ok)
                    {
                        GUILayout.Label($"{finding.Title}: {finding.Message}", ConvaiEditorStyles.CaptionWrapped);
                        continue;
                    }

                    DrawFindingPanel(finding, set);
                }
            }
        }

        /// <summary>One finding as a severity message box with its own one-click fix, when it has one.</summary>
        private void DrawFindingPanel(BodyAnimationTroubleshooterFinding finding, ConvaiBodyAnimationSet set)
        {
            string fixLabel = null;
            System.Action fix = null;

            string characterFix = BodyAnimationSetupService.DescribeFix(finding.Fix);
            string setFix = BodyAnimationFixes.DescribeSetFix(finding.Fix);
            if (characterFix != null)
            {
                fixLabel = characterFix;
                fix = () =>
                {
                    BodyAnimationSetupService.ApplyFix(_controller, finding.Fix);
                    RefreshFindings();
                };
            }
            else if (setFix != null && set != null)
            {
                fixLabel = setFix;
                fix = () =>
                {
                    BodyAnimationFixes.ApplyToSet(set, finding.Fix);
                    RefreshFindings();
                };
            }
            else
            {
                string configFix = BodyAnimationFixes.DescribeConfigFix(finding.Fix);
                ConvaiBodyAnimationConfig configAsset = configFix != null
                    ? BodyAnimationSetupService.ResolveAssignedConfig(_controller)
                    : null;
                if (configAsset != null)
                {
                    fixLabel = configFix;
                    fix = () =>
                    {
                        BodyAnimationFixes.ApplyToConfig(configAsset, finding.Fix, _controller);
                        RefreshFindings();
                    };
                }
            }

            switch (finding.Severity)
            {
                case BodyAnimationTroubleshooterSeverity.Error:
                    ConvaiEditorFrame.ErrorBox(finding.Title, finding.Message, fixLabel, fix);
                    break;
                case BodyAnimationTroubleshooterSeverity.Warning:
                    ConvaiEditorFrame.WarningBox(finding.Title, finding.Message, fixLabel, fix);
                    break;
                default:
                    ConvaiEditorFrame.InfoBox(finding.Title, finding.Message, fixLabel, fix);
                    break;
            }
        }

        private static void DrawCenteredMessage(string message)
        {
            GUILayout.FlexibleSpace();
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label(message, ConvaiEditorStyles.CenteredBody, GUILayout.MaxWidth(360f));
                GUILayout.FlexibleSpace();
            }
            GUILayout.FlexibleSpace();
        }
    }
}
