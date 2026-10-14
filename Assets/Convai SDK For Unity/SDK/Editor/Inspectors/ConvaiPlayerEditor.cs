using System;
using System.Collections.Generic;
using Convai.Editor.Inspectors.Framework;
using Convai.Editor.UI;
using Convai.Runtime.Components;
using Convai.Shared.Compatibility;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Theme = Convai.Editor.UI.ConvaiEditorTheme;

namespace Convai.Editor.Inspectors
{
    /// <summary>
    ///     Convai editor for <see cref="ConvaiPlayer" />: the local player's identity, a rolling
    ///     validation report on the scene's player setup, and the effective values in Play mode.
    /// </summary>
    [CustomEditor(typeof(ConvaiPlayer))]
    internal sealed class ConvaiPlayerEditor : ConvaiInspectorEditor
    {
        private const string TitleText = "Convai Player";

        /// <remarks>
        ///     It used to send the reader to "ConvaiManager" for the conversation mode. That is the
        ///     class name, not the component's name, and it is the wrong component besides: whether
        ///     the player talks hands free or push to talk is a Convai Room Manager setting.
        /// </remarks>
        private const string PurposeText =
            "Names the person playing, so transcripts and the conversation know who is speaking. " +
            "How the player talks — hands free or push to talk — is set on the Convai Room Manager.";

        private const string SectionIdentityId = "Identity";
        private const string SectionValidationId = "Validation";
        private const string SectionRuntimeId = "Runtime";

        private static readonly GUIContent IdentitySection = new("Identity");
        private static readonly GUIContent ValidationSection = new("Validation");
        private static readonly GUIContent RuntimeSection = new("Runtime");

        private static readonly GUIContent ProjectSettingsButton = new(
            "Project Settings", "Open Project Settings \u2192 Convai SDK.");

        private static readonly GUIContent RefreshChecksButton = new(
            "Refresh Checks", "Re-run the checks above against the scene as it stands now.");

        private static readonly GUIContent EffectiveNameLabel = new(
            "Player Name", "The name this player is actually using right now.");

        private static readonly GUIContent EffectiveIdLabel = new(
            "Player ID", "The identifier transcripts are attributed to right now.");

        private static readonly GUIContent EffectiveColorLabel = new(
            "Name Tag Color", "The colour this player is drawn in right now.");

        private PlayerValidationReport _cachedValidationReport;
        private GUIContent _headerChip;
        private SerializedProperty _nameTagColorProp;
        private ConvaiEditorRefreshTimer _validationTimer;
        private ConvaiPlayer _player;
        private SerializedProperty _playerIdProp;
        private SerializedProperty _playerNameProp;

        protected override string Title => TitleText;
        protected override string Purpose => PurposeText;
        protected override GUIContent StatusChip => _headerChip;

        protected override void OnEnable()
        {
            base.OnEnable();

            _player = (ConvaiPlayer)target;
            _playerNameProp = serializedObject.FindProperty("_playerName");
            _playerIdProp = serializedObject.FindProperty("_playerId");
            _nameTagColorProp = serializedObject.FindProperty("_nameTagColor");
            _headerChip = new GUIContent(HeaderStatusText());

            InvalidateValidationCache();
        }

        /// <summary>
        ///     Refreshes the header chip text in place, so the player's name in the header follows
        ///     edits without allocating a <see cref="GUIContent" /> per repaint.
        /// </summary>
        protected override void OnBeforeInspectorGUI()
        {
            if (_headerChip == null)
                return;

            string status = HeaderStatusText();
            if (!string.Equals(_headerChip.text, status, StringComparison.Ordinal))
                _headerChip.text = status;
        }

        protected override void DrawBody()
        {
            // The identity fields are the whole point of this inspector; without them there is nothing
            // bespoke left to draw, so fall through to the attribute-driven renderer rather than
            // showing an empty page.
            if (_playerNameProp == null || _playerIdProp == null || _nameTagColorProp == null)
            {
                DrawGeneratedSections();
                return;
            }

            DrawIdentitySection();
            DrawValidationSection();
        }

        /// <summary>Keeps the Runtime section live.</summary>
        /// <remarks>
        ///     A player's name and id can be set from code while the game runs, which is the only
        ///     reason this section exists. Without a repaint it would show whatever was true when the
        ///     inspector was last drawn, and a stale live reading cannot be told apart from a
        ///     current one.
        /// </remarks>
        public override bool RequiresConstantRepaint() => EditorApplication.isPlaying;

        /// <remarks>
        ///     Readings, not disabled fields. Three greyed-out text boxes are the shape of a form
        ///     somebody has switched off, and a reader tries to type in them; these are the same
        ///     two-column readout every other Convai inspector reports a running scene with. The
        ///     colour is drawn as a swatch because a colour is the one value a name cannot carry.
        /// </remarks>
        protected override void DrawLiveSection()
        {
            if (!DrawSection(SectionRuntimeId, RuntimeSection, Glyphs.Live,
                    defaultExpanded: false, accent: Theme.StatusInfo)) return;
            DrawSectionBody(() =>
            {
                string liveName = _player.PlayerName;
                string liveId = _player.PlayerId;

                Theme.KeyValueRow(
                    EffectiveNameLabel,
                    string.IsNullOrWhiteSpace(liveName) ? "Not set" : liveName,
                    string.IsNullOrWhiteSpace(liveName) ? Theme.StatusWarn : Theme.TextPrimary);

                // Empty is the ordinary case, not a fault, and saying which name it fell back to is
                // the answer to the question somebody opens this section with.
                Theme.KeyValueRow(
                    EffectiveIdLabel,
                    string.IsNullOrWhiteSpace(liveId)
                        ? $"Using the name — {(string.IsNullOrWhiteSpace(liveName) ? "which is not set" : liveName)}"
                        : liveId);

                DrawColorRow(EffectiveColorLabel, _player.NameTagColor);
            });
        }

        /// <summary>A reading whose value is a colour swatch rather than text.</summary>
        private static void DrawColorRow(GUIContent label, Color color)
        {
            Rect row = GUILayoutUtility.GetRect(0f, Theme.ReadingRowHeight, GUILayout.ExpandWidth(true));
            float x = row.x + Theme.ContentInset;
            float keyWidth = Mathf.Min(Theme.ReadingLabelWidth, Mathf.Max(0f, row.width - Theme.ContentInset));

            GUI.Label(new Rect(x, row.y, keyWidth, row.height), label, Theme.ReadingLabel);

            var swatch = new Rect(x + keyWidth, row.y + 3f, 44f, row.height - 6f);
            Theme.FillRounded(swatch, color, 3f);
            Theme.StrokeRounded(swatch, Theme.CardBorder, 3f);
        }

        private void DrawIdentitySection()
        {
            if (!DrawSection(SectionIdentityId, IdentitySection, Glyphs.Identity)) return;
            DrawSectionBody(() =>
            {
                EditorGUILayout.PropertyField(_playerNameProp, ConvaiInspectorContent.PlayerName);
                EditorGUILayout.PropertyField(_playerIdProp, ConvaiInspectorContent.PlayerId);
                EditorGUILayout.PropertyField(_nameTagColorProp, ConvaiInspectorContent.PlayerNameTagColor);
            });
        }

        /// <summary>
        ///     What this scene's player setup looks like, coloured by what was actually found.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The header was permanently amber, so a healthy player still wore a warning badge,
        ///         and the box under it read "Player setup / Player setup looks healthy" — the title
        ///         and the body saying the same thing, above a line that said it a third time. The
        ///         header now takes the colour of what it found and carries the verdict as a summary,
        ///         so a collapsed section still reports; the box appears only when something is
        ///         wrong.
        ///     </para>
        ///     <para>
        ///         What survives in the healthy case is the one line that is not a tautology: which
        ///         name transcripts will be attributed to when Player ID is left empty. That is a
        ///         fact about this scene, not a restatement of the badge.
        ///     </para>
        /// </remarks>
        private void DrawValidationSection()
        {
            PlayerValidationReport report = GetValidationReport();
            bool warned = report.MessageType == MessageType.Warning;

            if (!DrawSection(
                    SectionValidationId,
                    ValidationSection,
                    Glyphs.Validation,
                    accent: warned ? Theme.StatusWarn : Theme.StatusReady,
                    summary: warned ? "Review" : "Ready"))
                return;

            DrawSectionBody(() =>
            {
                if (warned)
                    WarningBox("Needs attention", report.Summary);

                // Wrapped prose, not a two-column row: these are sentences, and a value column
                // would clip them. No bullet glyph either — a bullet inside a wrapping label leaves
                // its continuation lines hanging under the dot instead of under the words.
                for (int i = 0; i < report.Messages.Length; i++)
                {
                    GUILayout.Label(report.Messages[i], Theme.MutedWrapped);
                    if (i < report.Messages.Length - 1) GUILayout.Space(3f);
                }

                GUILayout.Space(4f);

                int clicked = Theme.GhostButtonRow(ProjectSettingsButton, RefreshChecksButton);
                if (clicked == 0)
                    SettingsService.OpenProjectSettings("Project/Convai SDK");
                else if (clicked == 1)
                    InvalidateValidationCache(true);
            });
        }

        private string HeaderStatusText()
        {
            string playerName = _player != null ? _player.PlayerName : string.Empty;
            return string.IsNullOrWhiteSpace(playerName) ? "Player" : playerName;
        }

        private PlayerValidationReport GetValidationReport()
        {
            if (_validationTimer.ShouldRefresh(_cachedValidationReport != null))
                _cachedValidationReport = BuildValidationReport();

            return _cachedValidationReport;
        }

        private PlayerValidationReport BuildValidationReport()
        {
            string playerName = _playerNameProp.stringValue?.Trim() ?? string.Empty;
            string playerId = _playerIdProp.stringValue?.Trim() ?? string.Empty;
            var manager = FindAnyObjectByType<ConvaiManager>();
            ConvaiPlayer[] players = ConvaiObjectFind.All<ConvaiPlayer>(FindObjectsInactive.Exclude);

            // Product names, not class names. A reader looking for "ConvaiManager" in the Add
            // Component menu does not find it; they are looking for Convai Manager.
            var messages = new List<string>();
            var messageType = MessageType.Info;
            string summary = string.Empty;

            if (string.IsNullOrWhiteSpace(playerName))
            {
                messageType = MessageType.Warning;
                summary = "This player has no name.";
                messages.Add(
                    "Set Player Name. It is what transcripts and the Console call this person, and " +
                    "an unnamed player is hard to follow in either.");
            }

            if (manager == null)
            {
                messageType = MessageType.Warning;
                summary = "There is no Convai Manager in this scene.";
                messages.Add(
                    "Add a Convai Manager. It starts Convai for the scene, and without one this " +
                    "player never joins a conversation.");
            }

            if (players.Length > 1)
            {
                if (messageType != MessageType.Warning)
                {
                    messageType = MessageType.Warning;
                    summary = $"This scene has {players.Length} Convai Players.";
                }

                messages.Add(
                    "A scene normally has one, for the person at the keyboard. Several is only " +
                    "right if your own code decides which one owns the conversation.");
            }

            // Not a fault, and the reason it is said at all: it answers "whose name will I see in
            // the transcript" before the question is asked.
            if (string.IsNullOrWhiteSpace(playerId))
                messages.Add(
                    string.IsNullOrWhiteSpace(playerName)
                        ? "Player ID is empty, so transcripts fall back to Player Name — which is not set either."
                        : $"Player ID is empty, so transcripts are attributed to the name '{playerName}'.");
            else if (messages.Count == 0)
                // A section with nothing but two buttons in it reads as broken. This is the one
                // sentence a fully configured player still has to say, and it is a fact about this
                // scene rather than a restatement of the badge above it.
                messages.Add($"Transcripts are attributed to '{playerId}', shown as '{playerName}'.");

            return new PlayerValidationReport(summary, messageType, messages.ToArray());
        }

        private void InvalidateValidationCache(bool forceImmediateRefresh = false)
        {
            _cachedValidationReport = null;
            _validationTimer.Invalidate(forceImmediateRefresh);
        }

        private sealed class PlayerValidationReport
        {
            public PlayerValidationReport(string summary, MessageType messageType, string[] messages)
            {
                Summary = summary;
                MessageType = messageType;
                Messages = messages ?? Array.Empty<string>();
            }

            public string Summary { get; }
            public MessageType MessageType { get; }
            public string[] Messages { get; }
        }
    }
}
