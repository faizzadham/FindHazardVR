using System;
using System.Collections.Generic;
using Convai.Editor.Actions;
using Convai.Editor.Inspectors.Framework;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Compatibility;
using Convai.Shared.Types;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiActionTarget" /> — the
    ///     beginner's most-touched component, so the richest editor: curated Identity/
    ///     Recognition/Placement/Visibility groups (bypassing the generic field renderer), a
    ///     "Check a phrase" mini-tool that runs the same matching a Convai Character uses to find
    ///     this target by name, and a Play-mode Live section reporting whether the target is
    ///     currently registered. Scene-view gizmo/handle affordances live in the
    ///     <c>ConvaiActionTargetGizmos</c> half of this partial class.
    /// </summary>
    [CustomEditor(typeof(ConvaiActionTarget))]
    [CanEditMultipleObjects]
    internal sealed partial class ConvaiActionTargetEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Action Target";
        private const string SubtitleText = "Scene Knowledge";
        private const string PurposeText =
            "Makes this object something a Convai Character can know about and act on.";

        private static readonly GUIContent NameLabel = new(
            "Name", "What the Convai Character calls this. Leave blank to use the GameObject's name.");

        private static readonly GUIContent KindLabel = new(
            "Kind", "Whether this is an object the Convai Character can act on, or a character it can talk about.");

        private static readonly GUIContent DescriptionLabel = new(
            "Description", "A short description sent to the Convai Character so it understands what this object is.");

        private static readonly GUIContent BioLabel = new(
            "Bio", "A short background sent to the Convai Character so it understands who this character is.");

        private static readonly GUIContent AliasesLabel = new(
            "Aliases", "Other names people might use instead of the main name — the Convai Character will recognize these too.");

        private static readonly GUIContent InteractionPointLabel = new(
            "Interaction Point", "Where the Convai Character moves to or aims at. Leave empty to use this object's own position.");

        private static readonly GUIContent CreatePointButton = new(
            "Create Point", "Adds a new empty child object to use as the interaction point.");

        private static readonly GUIContent ApplyToLabel = new(
            "Visible To", "Which Convai Characters can see and act on this target while it is enabled.");

        private static readonly GUIContent SpecificCharactersLabel = new(
            "Characters", "The specific Convai Characters that can see this target.");

        private static readonly GUIContent RegisterOnEnableLabel = new(
            "Register Automatically",
            "Adds this target when the object is enabled and removes it when disabled. Turn off to control registration from code.");

        private static readonly GUIContent AdvancedTitle = new(
            "Advanced", "Less commonly needed settings.");

        /// <summary>
        ///     The two kinds a target can be. <see cref="ConvaiActionTargetKind.None" /> is the
        ///     "nothing resolved" value, not an authoring choice, so it is not offered.
        /// </summary>
        private static readonly GUIContent[] KindOptions =
        {
            new("Object", "Something the Convai Character can act on."),
            new("Character", "Someone the Convai Character can talk about.")
        };

        /// <summary>One segment per <see cref="ConvaiActionTargetApplyScope" /> value, in declaration order.</summary>
        private static readonly GUIContent[] ApplyToOptions =
        {
            new("All Characters", "Every enabled Convai Character."),
            new("Specific Characters", "Only the Convai Characters listed below.")
        };

        private static readonly GUIContent CanonicalSourceNote = new(
            "Name and description come from the referenced World Object component.");

        private const string RequiredByCharactersSectionId = "RequiredByCharacters";
        private const string IdentitySectionId = "Identity";
        private const string RecognitionSectionId = "Recognition";
        private const string PlacementSectionId = "Placement";
        private const string VisibilitySectionId = "Visibility";
        private const string AdvancedSectionId = "Advanced";
        private const string CheckPhraseSectionId = "CheckPhrase";
        private const string LiveSectionId = "Live";

        private static readonly GUIContent IdentityTitle = new("Identity");
        private static readonly GUIContent RecognitionTitle = new("Recognition");
        private static readonly GUIContent PlacementTitle = new("Placement");
        private static readonly GUIContent VisibilityTitle = new("Visibility");
        private static readonly GUIContent CheckPhraseTitle = new("Check A Phrase");
        private static readonly GUIContent LiveTitle = new("Live");
        private static readonly GUIContent RequiredByCharactersTitle = new("Required By Characters");

        private static readonly GUIContent RecognitionHint = new(
            "Aliases let people say a different name and still mean this — for example \"lamp\" for \"Desk Lamp\".");

        private static readonly GUIContent DescriptionEmptyHint = new(
            "A short description helps the Convai Character pick the right object.");

        private static readonly GUIContent CheckPhraseHint = new(
            "This tests the same matching Convai Characters use to find objects and characters by name.");

        private static readonly GUIContent CheckPhraseFieldLabel = new(
            "Phrase", "Type words a player might say, like \"the lamp\" or \"desk lamp\".");

        private static readonly GUIContent MultiEditCheckNote = new("Select a single target to test a phrase.");

        private static readonly GUIContent RegisteredYes = new(
            "Registered right now — this target can currently be found and used by Convai Characters.");

        private static readonly GUIContent RegisteredNo = new(
            "Not registered right now — this target cannot currently be found by name. " +
            "Check that the object and this component are enabled.");

        private SerializedProperty _metadataSourceProp;
        private SerializedProperty _targetNameProp;
        private SerializedProperty _kindProp;
        private SerializedProperty _descriptionProp;
        private SerializedProperty _bioProp;
        private SerializedProperty _aliasesProp;
        private SerializedProperty _interactionPointProp;
        private SerializedProperty _applyToProp;
        private SerializedProperty _specificCharactersProp;
        private SerializedProperty _registerOnEnableProp;

        private string _checkPhrase = string.Empty;
        private string _checkResultText = string.Empty;

        // Reverse-view scan cache : recomputed only on enable
        // (which Unity also triggers on selection change), on scene hierarchy changes, and right
        // after a fix runs — never per repaint, matching the executor inspector's own scan cache.
        private readonly List<MissingTargetRequirement> _missingRequirements = new();
        private bool _missingRequirementsDirty = true;

        /// <summary>
        ///     One de-duplicated "a character in this scene needs component X on this object"
        ///     finding, pre-built at scan time so drawing it never re-allocates its strings.
        /// </summary>
        private readonly struct MissingTargetRequirement
        {
            internal MissingTargetRequirement(string title, string message, string addButton, Action fix)
            {
                Title = title;
                Message = message;
                AddButton = addButton;
                Fix = fix;
            }

            internal string Title { get; }
            internal string Message { get; }
            internal string AddButton { get; }
            internal Action Fix { get; }
        }

        protected override string Title => TitleText;
        protected override string Subtitle => SubtitleText;
        protected override string Purpose => PurposeText;

        protected override void OnEnable()
        {
            base.OnEnable();

            _metadataSourceProp = serializedObject.FindProperty("_metadataSource");
            _targetNameProp = serializedObject.FindProperty("_targetName");
            _kindProp = serializedObject.FindProperty("_kind");
            _descriptionProp = serializedObject.FindProperty("_description");
            _bioProp = serializedObject.FindProperty("_bio");
            _aliasesProp = serializedObject.FindProperty("_aliases");
            _interactionPointProp = serializedObject.FindProperty("_interactionPoint");
            _applyToProp = serializedObject.FindProperty("_applyTo");
            _specificCharactersProp = serializedObject.FindProperty("_specificCharacters");
            _registerOnEnableProp = serializedObject.FindProperty("_registerOnEnable");

            EditorApplication.hierarchyChanged += MarkMissingRequirementsDirty;
            RefreshMissingRequirements();
        }

        protected override void OnDisable()
        {
            EditorApplication.hierarchyChanged -= MarkMissingRequirementsDirty;
            base.OnDisable();
        }

        private void MarkMissingRequirementsDirty()
        {
            _missingRequirementsDirty = true;
            Repaint();
        }

        protected override void OnBeforeInspectorGUI()
        {
            if (_missingRequirementsDirty)
                RefreshMissingRequirements();
        }

        // Deliberately bypasses the generic field renderer entirely in favor of
        // curated groups; the base's per-field section loop never runs for this component.
        protected override void DrawBody()
        {
            DrawRequiredByCharactersSection();
            DrawIdentitySection();
            DrawRecognitionSection();
            DrawPlacementSection();
            DrawVisibilitySection();
            DrawCheckPhraseSection();
        }

        /// <summary>
        ///     Reverse view of the executor inspector's target-requirement notice: while that
        ///     surface says "this behavior needs a component on its
        ///     target", this one looks the other way — "a character in this scene needs a
        ///     component this object doesn't have" — with the one-click fix that actually belongs
        ///     here, since this is the object the missing component would go on.
        /// </summary>
        private void DrawRequiredByCharactersSection()
        {
            if (targets.Length != 1 || _missingRequirements.Count == 0)
                return;

            DrawSection(RequiredByCharactersSectionId, RequiredByCharactersTitle, Glyphs.Animator, () =>
            {
                for (int i = 0; i < _missingRequirements.Count; i++)
                {
                    MissingTargetRequirement requirement = _missingRequirements[i];
                    WarningBox(requirement.Title, requirement.Message, requirement.AddButton, requirement.Fix);
                }
            }, accent: Theme.StatusWarn);
        }

        /// <summary>
        ///     Scans every <see cref="ConvaiActionConfigSource" /> in the open scenes for actions
        ///     whose archetype declares a <c>RequiredTargetComponent</c> this object is known to
        ///     (via <see cref="ConvaiActionsSceneKnowledgeModel" />, the same "known" definition the
        ///     Action Troubleshooter and Scene Knowledge scan use) but does not have. Findings are
        ///     de-duplicated per missing component across every character/action that needs it.
        ///     Skips prefab assets and Play mode, where a static scene scan is meaningless.
        /// </summary>
        private void RefreshMissingRequirements()
        {
            _missingRequirementsDirty = false;
            _missingRequirements.Clear();

            if (targets.Length != 1 || target == null)
                return;

            var owner = (ConvaiActionTarget)target;
            GameObject go = owner.gameObject;
            if (EditorUtility.IsPersistent(go) || UnityEngine.Application.isPlaying)
                return;

            var actionsByType = new Dictionary<Type, List<string>>();
            ConvaiActionConfigSource[] sources = ConvaiObjectFind.All<ConvaiActionConfigSource>(FindObjectsInactive.Include);
            for (int i = 0; i < sources.Length; i++)
            {
                ConvaiActionConfigSource source = sources[i];
                if (source == null)
                    continue;

                var character = source.GetComponent<ConvaiCharacter>();
                bool autoRegisters = owner.RegisterOnEnable && character != null && owner.AppliesToCharacter(character);
                ConvaiSceneKnowledgeScanStatus status = ConvaiActionsSceneKnowledgeModel.Classify(
                    owner.TargetName, owner.Kind, autoRegisters, source.Objects, source.Characters);
                if (status == ConvaiSceneKnowledgeScanStatus.NotKnown)
                    continue; // This character does not know about this object at all.

                IReadOnlyList<ConvaiActionDefinition> definitions = source.GetEffectiveDefinitions();
                for (int d = 0; d < definitions.Count; d++)
                {
                    ConvaiActionDefinition definition = definitions[d];
                    if (definition == null || string.IsNullOrWhiteSpace(definition.ActionName))
                        continue;

                    ConvaiActionArchetypeCatalogEntry entry = ConvaiActionArchetypeCatalog.FindByDefinition(definition);
                    string hint = entry?.RequiredTargetComponent;
                    if (string.IsNullOrWhiteSpace(hint))
                        continue;

                    Type requiredType = ConvaiComponentTypeResolver.Resolve(hint.Trim());
                    if (requiredType == null || go.GetComponent(requiredType) != null)
                        continue;

                    if (!actionsByType.TryGetValue(requiredType, out List<string> names))
                    {
                        names = new List<string>();
                        actionsByType[requiredType] = names;
                    }

                    if (!names.Contains(definition.ActionName))
                        names.Add(definition.ActionName);
                }
            }

            foreach (KeyValuePair<Type, List<string>> pair in actionsByType)
            {
                Type componentType = pair.Key;
                string niceName = ConvaiComponentTypeResolver.DisplayName(componentType);
                string actionList = string.Join(", ", pair.Value);
                _missingRequirements.Add(new MissingTargetRequirement(
                    $"Missing {niceName}",
                    $"A Convai Character in this scene can {actionList}, but this object has no {niceName} component.",
                    $"Add {niceName}",
                    () => ApplyMissingRequirementFix(componentType)));
            }
        }

        private void ApplyMissingRequirementFix(Type componentType)
        {
            if (targets.Length != 1 || target == null)
                return;

            var owner = (ConvaiActionTarget)target;
            GameObject go = owner.gameObject;

            if (go.GetComponent(componentType) == null)
                Undo.AddComponent(go, componentType);

            EditorUtility.SetDirty(go);
            _missingRequirementsDirty = true;
        }

        private void DrawIdentitySection()
        {
            DrawSection(IdentitySectionId, IdentityTitle, Glyphs.Identity, () =>
            {
                EditorGUILayout.PropertyField(_metadataSourceProp);
                bool canonical = _metadataSourceProp.objectReferenceValue != null;
                using (new EditorGUI.DisabledScope(canonical)) EditorGUILayout.PropertyField(_targetNameProp, NameLabel);
                if (canonical) GUILayout.Label(CanonicalSourceNote, Theme.MutedWrapped);
                if (targets.Length == 1 && string.IsNullOrWhiteSpace(_targetNameProp.stringValue))
                {
                    var owner = (ConvaiActionTarget)target;
                    GUILayout.Label($"Uses the GameObject name: '{owner.gameObject.name}'", Theme.MutedWrapped);
                }

                GUILayout.Space(4f);
                DrawKindField();

                GUILayout.Space(4f);
                using (new EditorGUI.DisabledScope(canonical)) EditorGUILayout.PropertyField(_descriptionProp, DescriptionLabel);
                if (!canonical && string.IsNullOrWhiteSpace(_descriptionProp.stringValue))
                    GUILayout.Label(DescriptionEmptyHint, Theme.MutedWrapped);

                if (_kindProp.enumValueIndex == (int)ConvaiActionTargetKind.Character)
                {
                    GUILayout.Space(4f);
                    EditorGUILayout.PropertyField(_bioProp, BioLabel);
                }
            });
        }

        /// <summary>
        ///     Kind as Object/Character segments. A mixed multi-selection, or a stored
        ///     <see cref="ConvaiActionTargetKind.None" />, shows no segment selected until one is picked.
        /// </summary>
        private void DrawKindField()
        {
            int stored = _kindProp.enumValueIndex;
            int current = _kindProp.hasMultipleDifferentValues ? -1
                : stored == (int)ConvaiActionTargetKind.Object ? 0
                : stored == (int)ConvaiActionTargetKind.Character ? 1
                : -1;

            int next = Theme.LabeledSegments(KindLabel, KindOptions, current, _kindProp);
            if (next != current)
            {
                _kindProp.enumValueIndex = next == 0
                    ? (int)ConvaiActionTargetKind.Object
                    : (int)ConvaiActionTargetKind.Character;
            }
        }

        private void DrawRecognitionSection()
        {
            DrawSection(RecognitionSectionId, RecognitionTitle, Glyphs.Discovery, () =>
            {
                GUILayout.Label(RecognitionHint, Theme.MutedWrapped);
                GUILayout.Space(4f);
                EditorGUILayout.PropertyField(_aliasesProp, AliasesLabel, true);
            });
        }

        private void DrawPlacementSection()
        {
            DrawSection(PlacementSectionId, PlacementTitle, Glyphs.Placement, () =>
            {
                EditorGUILayout.PropertyField(_interactionPointProp, InteractionPointLabel);

                if (_interactionPointProp.objectReferenceValue == null && targets.Length == 1)
                {
                    GUILayout.Space(4f);
                    if (Theme.GhostButtonLayout(CreatePointButton))
                        CreateInteractionPoint();
                }
            });
        }

        private void CreateInteractionPoint()
        {
            var owner = (ConvaiActionTarget)target;
            var point = new GameObject("Interaction Point");
            Undo.RegisterCreatedObjectUndo(point, "Create Interaction Point");
            Undo.SetTransformParent(point.transform, owner.transform, "Create Interaction Point");
            point.transform.localPosition = Vector3.zero;
            point.transform.localRotation = Quaternion.identity;

            _interactionPointProp.objectReferenceValue = point.transform;
            serializedObject.ApplyModifiedProperties();
        }

        private void DrawVisibilitySection()
        {
            DrawSection(VisibilitySectionId, VisibilityTitle, Glyphs.Visibility, () =>
            {
                int current = _applyToProp.hasMultipleDifferentValues ? -1 : _applyToProp.enumValueIndex;
                int next = Theme.LabeledSegments(ApplyToLabel, ApplyToOptions, current, _applyToProp);
                if (next != current)
                    _applyToProp.enumValueIndex = next;

                if (_applyToProp.enumValueIndex == (int)ConvaiActionTargetApplyScope.SpecificCharacters)
                {
                    GUILayout.Space(4f);
                    EditorGUILayout.PropertyField(_specificCharactersProp, SpecificCharactersLabel, true);
                }
            });

            DrawSection(AdvancedSectionId, AdvancedTitle, Glyphs.Contract,
                () => EditorGUILayout.PropertyField(_registerOnEnableProp, RegisterOnEnableLabel),
                defaultExpanded: false);
        }

        private void DrawCheckPhraseSection()
        {
            DrawSection(CheckPhraseSectionId, CheckPhraseTitle, Glyphs.Discovery, DrawCheckPhrase);
        }

        private void DrawCheckPhrase()
        {
            if (targets.Length != 1)
            {
                GUILayout.Label(MultiEditCheckNote, Theme.MutedWrapped);
                return;
            }

            GUILayout.Label(CheckPhraseHint, Theme.MutedWrapped);
            GUILayout.Space(4f);

            string newPhrase = EditorGUILayout.TextField(CheckPhraseFieldLabel, _checkPhrase);
            if (!string.Equals(newPhrase, _checkPhrase, StringComparison.Ordinal))
            {
                _checkPhrase = newPhrase;
                RecomputeCheckResult();
            }

            GUILayout.Space(2f);
            GUILayout.Label(_checkResultText, Theme.BodyWrapped);
        }

        private void RecomputeCheckResult()
        {
            var owner = (ConvaiActionTarget)target;
            string effectiveName = ConvaiActionTargetPhraseMatcher.EffectiveName(
                _targetNameProp.stringValue, owner.gameObject.name);

            var aliases = new List<string>(_aliasesProp.arraySize);
            for (int i = 0; i < _aliasesProp.arraySize; i++)
                aliases.Add(_aliasesProp.GetArrayElementAtIndex(i).stringValue);

            ConvaiActionTargetPhraseMatcher.MatchResult result =
                ConvaiActionTargetPhraseMatcher.Match(_checkPhrase, effectiveName, aliases);
            _checkResultText = ConvaiActionTargetPhraseMatcher.Describe(result);
        }

        protected override void DrawLiveSection()
        {
            if (targets.Length != 1)
                return;

            DrawSection(LiveSectionId, LiveTitle, Glyphs.Live, () =>
            {
                bool registered = IsRegistered((ConvaiActionTarget)target);
                Theme.BeginPanel(registered ? Theme.StatusReady : (Color?)null);
                GUILayout.Label(registered ? RegisteredYes : RegisteredNo, Theme.BodyWrapped);
                Theme.EndPanel();
            }, accent: Theme.StatusInfo);
        }

        // Manual loop (no LINQ) over the small internal active-target list — avoids the
        // enumerator boxing / per-frame allocation a LINQ Contains() would introduce here,
        // since this runs every repaint while the inspector is open in Play mode.
        private static bool IsRegistered(ConvaiActionTarget owner)
        {
            IReadOnlyList<ConvaiActionTarget> active = ConvaiActionTarget.ActiveTargets;
            for (int i = 0; i < active.Count; i++)
            {
                if (ReferenceEquals(active[i], owner))
                    return true;
            }

            return false;
        }
    }
}
