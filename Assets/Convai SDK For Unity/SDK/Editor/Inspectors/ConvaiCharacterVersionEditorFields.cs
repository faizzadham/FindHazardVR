using System;
using System.Collections.Generic;
using System.Linq;
using Convai.CharacterApi;
using Convai.Runtime;
using Convai.Runtime.Components;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>Shared version controls for inline characters and reusable profiles.</summary>
    internal static class ConvaiCharacterVersionEditorFields
    {
        private static readonly GUIContent ModeLabel = new("Character Version",
            "Live uses the current character. Other choices are released versions fetched from the API. " +
            "Released selections keep the exact reference returned by the API.");
        private static readonly GUIContent RefreshLabel = new("Refresh", "Reload released character versions.");

        internal readonly struct VersionOption
        {
            internal VersionOption(string label, ConvaiCharacterVersionMode mode, string version, string reference,
                bool selected, bool enabled = true)
            {
                Label = label;
                Mode = mode;
                Version = version;
                Reference = reference;
                Selected = selected;
                Enabled = enabled;
            }

            internal string Label { get; }
            internal ConvaiCharacterVersionMode Mode { get; }
            internal string Version { get; }
            internal string Reference { get; }
            internal bool Selected { get; }
            internal bool Enabled { get; }
        }

        internal static VersionOption[] BuildOptions(ConvaiCharacterVersionMode mode, string savedVersion,
            string savedReference, string currentReference, IReadOnlyList<CharacterReleasedVersion> releasedVersions,
            bool hasLoaded)
        {
            var options = new List<VersionOption>
            {
                new("Live", ConvaiCharacterVersionMode.Live, null, currentReference,
                    mode == ConvaiCharacterVersionMode.Live)
            };
            foreach (CharacterReleasedVersion release in releasedVersions)
                options.Add(new VersionOption(release.IsLatest ? release.Version + " (Latest release)" : release.Version,
                    ConvaiCharacterVersionMode.Specific,
                    release.Version, release.Reference,
                    mode == ConvaiCharacterVersionMode.Specific &&
                    !string.IsNullOrEmpty(savedReference) && release.Reference == savedReference));

            // Missing references and unavailable pins never turn into Live on repaint.
            if (!options.Any(option => option.Selected))
            {
                string label = mode == ConvaiCharacterVersionMode.Specific
                    ? (string.IsNullOrEmpty(savedVersion) ? "Missing saved version" : savedVersion) +
                      (string.IsNullOrEmpty(savedReference) ? " (reselect required)" :
                          hasLoaded ? " (unavailable)" : " (saved)")
                    : "Invalid version selection";
                options.Add(new VersionOption(label, mode, savedVersion, savedReference, true, false));
            }

            return options.ToArray();
        }

        internal static bool HasMultipleCharactersInScene(ConvaiCharacter character)
        {
            if (character == null || EditorUtility.IsPersistent(character)) return false;
            var scene = character.gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded || EditorSceneManager.IsPreviewScene(scene)) return false;

            // Include inactive characters: they can be enabled later in the same scene.
            int characterCount = 0;
            foreach (GameObject root in scene.GetRootGameObjects())
            {
                characterCount += root.GetComponentsInChildren<ConvaiCharacter>(true).Length;
                if (characterCount > 1) return true;
            }

            return false;
        }

        internal static void Draw(SerializedProperty characterId, SerializedProperty mode,
            SerializedProperty version, SerializedProperty selectedReference, ConvaiCharacterVersionDropdownState state)
        {
            if (characterId == null || mode == null || version == null || selectedReference == null || state == null) return;
            object context = GetContextKey(characterId.stringValue);
            EnsureContext(state, context, characterId.stringValue);
            VersionOption[] options = BuildOptions((ConvaiCharacterVersionMode)mode.intValue,
                version.stringValue, selectedReference.stringValue, state.CurrentReference ?? characterId.stringValue,
                state.Versions, state.HasLoaded);
            VersionOption selected = options.First(option => option.Selected);

            // Refresh sits beside the list it reloads, drawn as the same inline ghost button as Copy
            // and Fetch; the states below use the design system's caption and message box rather than
            // Unity's HelpBox, which is the one grey, icon-heavy block in an otherwise Convai inspector.
            Rect field = Theme.FieldWithActions(
                RefreshLabel, state.CanRefresh && !state.IsLoading, null, false, out int clicked);
            Rect popup = EditorGUI.PrefixLabel(field, ModeLabel);
            if (EditorGUI.DropdownButton(popup, new GUIContent(selected.Label), FocusType.Keyboard, EditorStyles.popup))
                ShowMenu(popup, options, characterId, mode, version, selectedReference, state, context);
            if (clicked == 0)
                _ = state.RefreshAsync();

            if (state.IsLoading)
                GUILayout.Label("Loading released versions…", Theme.CaptionWrapped);
            else if (!string.IsNullOrEmpty(state.Error))
                Theme.WarningBox("Released versions unavailable", state.Error);
            else if (state.HasLoaded && state.Versions.Count == 0)
                GUILayout.Label("No released versions yet, so Live is the only choice.", Theme.CaptionWrapped);

            if (!selected.Enabled)
                Theme.WarningBox("Saved version unavailable", string.IsNullOrEmpty(selectedReference.stringValue)
                    ? "The saved version has no API reference. Reselect a released version or choose Live."
                    : "The saved version is not in the available release list. " +
                      "It has been preserved; choose Live or an available release to change it.");
        }

        internal static void DrawReadOnly(ConvaiCharacterVersionMode mode, string version)
        {
            using (new EditorGUI.DisabledScope(true))
            {
                string label = mode == ConvaiCharacterVersionMode.Live ? "Live" : version ?? string.Empty;
                EditorGUILayout.Popup(ModeLabel, 0, new[] { label });
            }
        }

        private static object GetContextKey(string characterId)
        {
            ConvaiSettings settings = ConvaiSettings.Instance;
            // Kept only in this inspector's memory, never logged or persisted. Credentials and
            // workspace/environment changes invalidate old results even when the UUID is unchanged.
            return Tuple.Create(characterId, settings?.ApiKey, settings?.ApiMode,
                settings?.ApiEnvironment, settings?.CharacterApiBaseUrlOverride, settings?.CharacterWorkspaceId);
        }

        private static void EnsureContext(ConvaiCharacterVersionDropdownState state, object context, string characterId)
        {
            if (state.IsCurrentContext(context)) return;
            ConvaiSettings settings = ConvaiSettings.Instance;
            string unavailableReason = null;
            ConvaiCharacterApiOptions options = null;
            if (!ConvaiCharacterVersionReference.TryCreate(characterId, ConvaiCharacterVersionMode.Live,
                    null, out _, out _))
                unavailableReason = "Enter a valid Character ID to load released versions.";
            else if (settings == null || !settings.HasApiKey)
                unavailableReason = "Configure an API key in Project Settings > Convai SDK > Credentials to load released versions.";
            else if (settings.ApiMode != ConvaiApiMode.Upgraded)
                unavailableReason = "Loading released versions requires Upgraded API Mode in Project Settings > Convai SDK > Credentials.";
            else
            {
                try { options = ConvaiRestOptionsFactory.Create(settings.ApiKey).CharacterApiV2; }
                catch (Exception error) when (error is ArgumentException || error is UriFormatException)
                {
                    unavailableReason = "Check the Character API URL and credentials in Project Settings.";
                }
            }

            _ = state.SetContextAsync(context, options == null ? null :
                cancellation => ConvaiCharacterVersionListClient.FetchAsync(options, characterId, cancellation),
                unavailableReason);
        }

        private static void ShowMenu(Rect popup, VersionOption[] options, SerializedProperty characterId,
            SerializedProperty mode, SerializedProperty version, SerializedProperty selectedReference,
            ConvaiCharacterVersionDropdownState state, object context)
        {
            var menu = new GenericMenu();
            UnityEngine.Object target = mode.serializedObject.targetObject;
            string idPath = characterId.propertyPath;
            string id = characterId.stringValue;
            string modePath = mode.propertyPath;
            string versionPath = version.propertyPath;
            string referencePath = selectedReference.propertyPath;
            int generation = state.Generation;
            int? source = mode.serializedObject.FindProperty("_configurationSource")?.intValue;
            foreach (VersionOption option in options)
            {
                if (!option.Enabled)
                {
                    menu.AddDisabledItem(new GUIContent(option.Label), option.Selected);
                    continue;
                }

                menu.AddItem(new GUIContent(option.Label), option.Selected, () =>
                {
                    if (target == null || state.Generation != generation || !state.IsCurrentContext(context) ||
                        !Equals(context, GetContextKey(id))) return;
                    using var serialized = new SerializedObject(target);
                    serialized.Update();
                    if (serialized.FindProperty(idPath)?.stringValue != id ||
                        serialized.FindProperty("_configurationSource")?.intValue != source) return;
                    SerializedProperty selectedMode = serialized.FindProperty(modePath);
                    SerializedProperty selectedVersion = serialized.FindProperty(versionPath);
                    SerializedProperty reference = serialized.FindProperty(referencePath);
                    selectedMode.intValue = (int)option.Mode;
                    selectedVersion.stringValue = option.Version ?? string.Empty;
                    reference.stringValue = option.Reference ?? string.Empty;
                    serialized.ApplyModifiedProperties();
                });
            }

            menu.DropDown(popup);
        }
    }
}
