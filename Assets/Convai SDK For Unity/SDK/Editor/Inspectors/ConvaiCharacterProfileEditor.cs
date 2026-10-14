using Convai.Editor.Inspectors.Framework;
using Convai.Runtime;
using UnityEditor;
using UnityEngine;
using Chips = Convai.Editor.UI.ConvaiEditorChips;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiCharacterProfile" /> — the reusable identity and default
    ///     interaction settings a designer assigns to one or more characters so they share a name,
    ///     nametag color and connection defaults.
    /// </summary>
    [CustomEditor(typeof(ConvaiCharacterProfile))]
    internal sealed class ConvaiCharacterProfileEditor : ConvaiInspectorEditor
    {
        /// <summary>
        ///     The Character inspector's name for the same setting. The field's own name, "Enable
        ///     Remote Audio On Start", ran past the label column.
        /// </summary>
        private static readonly GUIContent PlayVoiceLabel = new(
            "Play Voice On Start",
            "Characters using this profile play their voice from the moment the session starts. Turn " +
            "it off for a text-only character; the voice can still be switched on from code.");

        private SerializedProperty _characterIdProp;
        private SerializedProperty _versionModeProp;
        private SerializedProperty _versionProp;
        private SerializedProperty _versionReferenceProp;
        private ConvaiCharacterVersionDropdownState _versionDropdown;

        protected override string Title => "Character Profile";
        protected override string Subtitle => "Convai Character Profile";

        protected override string Purpose =>
            "A reusable identity and default interaction settings for a character. Assign this asset " +
            "to a Convai Character so its name, nametag color and connection defaults come from here.";

        protected override string EditorStateHostId => "ConvaiCharacterProfileEditor";

        protected override GUIContent StatusChip =>
            HasValidSelection
                ? Chips.Ready.Content
                : Chips.NotSetUp.Content;

        protected override Color StatusChipTint =>
            HasValidSelection
                ? Chips.Ready.Tint
                : Chips.NotSetUp.Tint;

        private bool HasValidSelection => _characterIdProp != null && _versionModeProp != null &&
            _versionProp != null && _versionReferenceProp != null &&
            ConvaiCharacterVersionReference.TryCreate(_characterIdProp.stringValue,
                (ConvaiCharacterVersionMode)_versionModeProp.intValue, _versionProp.stringValue, out _, out _,
                _versionReferenceProp.stringValue);

        protected override void OnEnable()
        {
            base.OnEnable();

            _characterIdProp = serializedObject.FindProperty("_characterId");
            _versionModeProp = serializedObject.FindProperty("_characterVersionMode");
            _versionProp = serializedObject.FindProperty("_characterVersion");
            _versionReferenceProp = serializedObject.FindProperty("_characterVersionReference");
            _versionDropdown = new ConvaiCharacterVersionDropdownState(Repaint);
        }

        protected override void OnDisable()
        {
            _versionDropdown?.Dispose();
            _versionDropdown = null;
            base.OnDisable();
        }

        protected override void DrawBody()
        {
            if (DrawSection("Identity", "Identity", Glyphs.Identity))
                DrawSectionBody(() =>
                {
                    EditorGUILayout.PropertyField(_characterIdProp);
                    ConvaiCharacterVersionEditorFields.Draw(_characterIdProp, _versionModeProp, _versionProp,
                        _versionReferenceProp, _versionDropdown);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_characterName"));
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_nameTagColor"));

                    if (!ConvaiCharacterVersionReference.TryCreate(_characterIdProp.stringValue,
                            (ConvaiCharacterVersionMode)_versionModeProp.intValue, _versionProp.stringValue,
                            out _, out string error, _versionReferenceProp.stringValue))
                        WarningBox("Character version", error);
                    if ((ConvaiCharacterVersionMode)_versionModeProp.intValue == ConvaiCharacterVersionMode.Specific &&
                        ConvaiSettings.Instance?.ApiMode != ConvaiApiMode.Upgraded)
                        InfoBox("Versioned REST reads",
                            "Set API Mode to Upgraded in Project Settings > Convai SDK > Credentials to fetch " +
                            "the selected character name and narrative data. Legacy reads cannot select a version.");
                });

            if (DrawSection("Defaults", "Defaults", Glyphs.Content))
                DrawSectionBody(() =>
                {
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_enableRemoteAudioOnStart"), PlayVoiceLabel);
                    EditorGUILayout.PropertyField(serializedObject.FindProperty("_enableSessionResume"));
                });
        }
    }
}
