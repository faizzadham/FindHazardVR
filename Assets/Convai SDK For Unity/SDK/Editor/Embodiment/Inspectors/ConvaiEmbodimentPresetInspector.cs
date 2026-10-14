using System;
using System.Collections.Generic;
using Convai.Editor.Embodiment.Setup;
using Convai.Editor.Inspectors;
using Convai.Modules.Embodiment.Presets;
using UnityEditor;
using UnityEngine;
using Convai.Editor.UI;

namespace Convai.Editor.Embodiment.Inspectors
{
    /// <summary>
    ///     Inspector for <see cref="ConvaiEmbodimentPreset" />: one row per feature, a dropdown of the
    ///     features that actually exist, and a picker that only offers settings the feature accepts.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The feature used to be a free-text field validated after the fact, so a typo was
    ///         something you discovered from a warning rather than something you could not make. It is
    ///         now a dropdown built from <see cref="EmbodimentModuleCatalog" />, and the settings slot
    ///         is filtered to the profile type the feature's own generic base declares — so both
    ///         classes of mistake are unrepresentable rather than diagnosed.
    ///     </para>
    ///     <para>
    ///         The report is computed <b>once per repaint</b> and reused by the header, the rows and
    ///         the findings list. The previous inspector ran its analysis three times per repaint —
    ///         twice from two header property getters answering two halves of one question — each time
    ///         allocating a fresh list and set.
    ///     </para>
    /// </remarks>
    [CustomEditor(typeof(ConvaiEmbodimentPreset))]
    internal sealed class ConvaiEmbodimentPresetInspector : ConvaiEmbodimentProfileEditorBase<ConvaiEmbodimentPreset>
    {
        internal const string SectionIdentity = "Identity";
        internal const string SectionSlots = "ProfileSlots";
        internal const string SectionDiagnostics = "SlotDiagnostics";

        private static readonly GUIContent AddFeatureButton = new(
            "Add Feature", "Adds a row for the first feature this preset does not list yet.");
        private static readonly GUIContent RemoveLastButton = new("Remove Last", "Removes the last feature row.");
        private static readonly GUIContent RemoveFeatureButton = new(
            ConvaiEditorGlyphs.Affordance.Remove, "Remove this feature from the preset.");
        private static readonly GUIContent FixEverythingButton = new("Fix Everything Above");

        private EmbodimentSetupReport _report;
        private bool _reportValid;

        protected override string HeaderTitle => "Embodiment Preset";
        protected override string HeaderSubtitle => "One set of settings per feature";
        protected override string HeaderStatus => Report.HeaderStatus;

        protected override Color HeaderStatusColor => Report.WorstSeverity switch
        {
            EmbodimentFindingSeverity.Error => ConvaiEditorTheme.StatusError,
            EmbodimentFindingSeverity.Warning => ConvaiEditorTheme.StatusWarn,
            _ => ConvaiEditorTheme.AccentBright
        };

        /// <summary>The report for this repaint, computed at most once.</summary>
        private EmbodimentSetupReport Report
        {
            get
            {
                if (!_reportValid)
                {
                    _report = EmbodimentPresetTroubleshooter.Evaluate(Profile);
                    _reportValid = true;
                }

                return _report;
            }
        }

        /// <summary>
        ///     Invalidates the cached report once per repaint, not per access — the whole point of
        ///     caching it. This is the hook the base calls before any drawing, so the header status and
        ///     every section below it read one consistent evaluation.
        /// </summary>
        protected override void OnBeforeInspectorGUI() => _reportValid = false;

        protected override string Purpose =>
            "Hands one set of settings to each of the character's features in a single step. Every " +
            "feature also works on its own, so a preset is a convenience, not a requirement.";

        protected override void DrawProfileInspector()
        {
            if (DrawSection(SectionIdentity, "Identity", ConvaiEditorGlyphs.Content))
            {
                DrawSectionBody(() =>
                {
                    DrawProperty("presetId", "Preset ID");
                    DrawProperty("description", "Description");
                });
            }

            if (DrawSection(SectionSlots, "Features", ConvaiEditorGlyphs.Routing))
                DrawSectionBody(DrawFeatureRows);

            EmbodimentSetupReport report = Report;
            if (DrawSection(SectionDiagnostics, "Setup Check", ConvaiEditorGlyphs.Validation,
                    accent: report.WorstSeverity >= EmbodimentFindingSeverity.Warning
                        ? ConvaiEditorTheme.StatusWarn
                        : (Color?)null))
                DrawSectionBody(() => DrawFindings(report));
        }

        // ── feature rows ────────────────────────────────────────────────────────────

        private void DrawFeatureRows()
        {
            SerializedProperty slots = Find("profileSlots");
            if (slots == null || !slots.isArray)
            {
                WarningBox("Missing Data", "This preset asset has no feature list to edit.");
                return;
            }

            string[] ids = EmbodimentModuleCatalog.ModuleIdsInDisplayOrder();
            string[] names = EmbodimentModuleCatalog.DisplayNamesInDisplayOrder();

            if (ids.Length == 0)
            {
                WarningBox(
                    "No Features Found",
                    "No Convai character features are present in this project, so there is nothing to " +
                    "configure. Check that the Convai package compiled.");
                return;
            }

            for (int i = 0; i < slots.arraySize; i++)
            {
                SerializedProperty slot = slots.GetArrayElementAtIndex(i);
                SerializedProperty moduleId = slot.FindPropertyRelative("moduleId");
                SerializedProperty profile = slot.FindPropertyRelative("profile");
                if (moduleId == null || profile == null) continue;

                if (DrawFeatureRow(slots, i, moduleId, profile, ids, names)) return;
            }

            GUILayout.Space(4f);
            switch (ConvaiEditorTheme.GhostButtonRow(
                        AddFeatureButton, slots.arraySize > 0 ? RemoveLastButton : null))
            {
                case 0:
                    AddFirstUnusedFeature(slots, ids);
                    break;
                case 1:
                    slots.arraySize--;
                    break;
            }
        }

        /// <summary>Draws one feature row. Returns <c>true</c> when the row was deleted.</summary>
        private bool DrawFeatureRow(
            SerializedProperty slots,
            int index,
            SerializedProperty moduleId,
            SerializedProperty profile,
            string[] ids,
            string[] names)
        {
            using (ConvaiEditorFrame.Panel())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    int current = Array.IndexOf(ids, moduleId.stringValue);

                    // An id that is not in the catalog is shown as-is rather than silently snapped to
                    // the first entry — a user's own feature is legitimate, and quietly rewriting
                    // their data would be worse than showing it.
                    if (current < 0)
                    {
                        EditorGUILayout.LabelField(
                            new GUIContent($"Unknown: {moduleId.stringValue}",
                                "This id does not match any Convai feature in the project."),
                            ConvaiEditorStyles.SectionTitle);
                    }
                    else
                    {
                        int picked = EditorGUILayout.Popup("Feature", current, names);
                        if (picked != current && picked >= 0 && picked < ids.Length)
                            moduleId.stringValue = ids[picked];
                    }

                    Rect remove = GUILayoutUtility.GetRect(
                        22f, EditorGUIUtility.singleLineHeight, GUILayout.Width(22f));
                    if (ConvaiEditorTheme.IconButton(remove, RemoveFeatureButton))
                    {
                        slots.DeleteArrayElementAtIndex(index);
                        return true;
                    }
                }

                DrawSettingsPicker(moduleId.stringValue, profile);

                if (EmbodimentModuleCatalog.TryGet(moduleId.stringValue, out EmbodimentModuleDescriptor module)
                    && !string.IsNullOrEmpty(module.Description))
                {
                    EditorGUILayout.LabelField(module.Description, ConvaiEditorStyles.MicroLabel);
                }
            }

            return false;
        }

        /// <summary>Draws the settings slot, filtered to the profile type the feature declares.</summary>
        private void DrawSettingsPicker(string moduleId, SerializedProperty profile)
        {
            if (!EmbodimentModuleCatalog.TryGet(moduleId, out EmbodimentModuleDescriptor module)
                || module.ProfileType == null)
            {
                EditorGUILayout.PropertyField(profile, new GUIContent("Settings"));
                return;
            }

            var label = new GUIContent(
                "Settings",
                $"Only {ObjectNames.NicifyVariableName(module.ProfileType.Name)} assets can go here. " +
                "Leave empty to use the feature's built-in defaults.");

            UnityEngine.Object picked = EditorGUILayout.ObjectField(
                label, profile.objectReferenceValue, module.ProfileType, false);

            if (picked != profile.objectReferenceValue)
                profile.objectReferenceValue = picked;
        }

        private void AddFirstUnusedFeature(SerializedProperty slots, string[] ids)
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < slots.arraySize; i++)
            {
                SerializedProperty existing = slots.GetArrayElementAtIndex(i).FindPropertyRelative("moduleId");
                if (existing != null) used.Add(existing.stringValue);
            }

            string next = null;
            for (int i = 0; i < ids.Length; i++)
            {
                if (used.Contains(ids[i])) continue;
                next = ids[i];
                break;
            }

            slots.arraySize++;
            SerializedProperty added = slots.GetArrayElementAtIndex(slots.arraySize - 1);
            SerializedProperty addedId = added.FindPropertyRelative("moduleId");
            SerializedProperty addedProfile = added.FindPropertyRelative("profile");

            // With every feature already listed there is nothing sensible to default to, so the new
            // row starts on the first feature and the duplicate check says so.
            if (addedId != null) addedId.stringValue = next ?? ids[0];
            if (addedProfile != null) addedProfile.objectReferenceValue = null;
        }

        // ── findings ────────────────────────────────────────────────────────────────

        private void DrawFindings(EmbodimentSetupReport report)
        {
            IReadOnlyList<EmbodimentFinding> findings = report.Findings;

            for (int i = 0; i < findings.Count; i++)
            {
                EmbodimentFinding finding = findings[i];

                Action fix = null;
                if (finding.CanFix)
                    fix = () =>
                    {
                        finding.Fix();
                        _reportValid = false;
                    };

                switch (finding.Severity)
                {
                    case EmbodimentFindingSeverity.Error:
                        ConvaiEditorTheme.ErrorBox(finding.Title, finding.Message, finding.FixLabel, fix);
                        break;
                    case EmbodimentFindingSeverity.Warning:
                        ConvaiEditorTheme.WarningBox(finding.Title, finding.Message, finding.FixLabel, fix);
                        break;
                    default:
                        ConvaiEditorTheme.InfoBox(finding.Title, finding.Message, finding.FixLabel, fix);
                        break;
                }
            }

            if (!report.HasFixes) return;

            GUILayout.Space(4f);
            if (ConvaiEditorTheme.PrimaryButtonLayout(FixEverythingButton))
            {
                EmbodimentPresetTroubleshooter.ApplyAllFixes(report);
                _reportValid = false;
            }
        }
    }
}
