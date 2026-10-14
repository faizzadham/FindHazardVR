using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Draw = Convai.Editor.UI.ConvaiEditorDraw;
using Styles = Convai.Editor.UI.ConvaiEditorStyles;
using Tokens = Convai.Editor.UI.ConvaiEditorTokens;

namespace Convai.Editor.UI
{
    /// <summary>
    ///     The interactive widgets of the Convai editor design system: buttons, pills, chips, stat
    ///     tiles and the search field. Every clickable affordance in Convai's editor UI is one of
    ///     these, so emphasis reads consistently — an accent-filled button always means "the one
    ///     action this view is for", an outlined button always means "secondary".
    /// </summary>
    /// <remarks>
    ///     All widgets are rect-based rather than layout-based. That keeps them usable from both
    ///     <c>GUILayout</c> code (via <see cref="GUILayoutUtility.GetRect(float, float, GUILayoutOption[])" />)
    ///     and the manual rect layout the module windows use for their panes, without a second
    ///     layout-flavoured copy of each widget.
    /// </remarks>
    internal static class ConvaiEditorControls
    {
        #region Captions

        /// <summary>Scratch content so string-titled captions allocate nothing per repaint.</summary>
        private static readonly GUIContent ScratchCaption = new();

        /// <summary>Scratch content for the explanation under a preset picker.</summary>
        private static readonly GUIContent ScratchExplanation = new();

        /// <summary>
        ///     The Custom status pill. Cached: the picker draws every repaint, and a per-repaint
        ///     <see cref="GUIContent" /> is an allocation the design system otherwise refuses.
        /// </summary>
        private static readonly GUIContent CustomStatusPill = new(
            ConvaiEditorProfileField.CustomLabel,
            ConvaiEditorProfileField.CustomCaption);

        /// <summary>
        ///     The caption over a cluster of controls — "Character Type" above a character-type
        ///     picker, "Blinking" above the blink fields. <b>The</b> way to draw one.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Deliberately laid out with <c>GUILayout</c> and never with
        ///         <see cref="EditorGUILayout.LabelField(string,GUIStyle,GUILayoutOption[])" />, so it
        ///         ignores <see cref="EditorGUI.indentLevel" /> exactly as the pickers, buttons and
        ///         cards it names do. A section body indents its property fields by one level; a
        ///         caption that followed that indent sat fifteen pixels to the right of its own picker,
        ///         and — because the SDK's call sites were split between the two idioms — did so in some
        ///         inspectors and not others.
        ///     </para>
        ///     <para>
        ///         Vertical rhythm belongs to the style (<see cref="ConvaiEditorStyles.GroupLabel" />),
        ///         which carries equal margins above and below. Call sites must not add their own
        ///         <see cref="GUILayout.Space(float)" /> around a caption; that is what made the same
        ///         caption sit tight against one picker and float above the next.
        ///     </para>
        /// </remarks>
        internal static void GroupCaption(GUIContent title) => GroupCaption(title, false);

        /// <summary>
        ///     Caption over a cluster of controls, with an optional Custom status on the right when
        ///     the cluster no longer matches a named preset. Identity never goes blank: the pills
        ///     below may all be quiet, but this chip still names the state.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Both forms reserve a row and draw into it at
        ///         <see cref="Styles.ContentInset" />.</b> The plain form used to be a
        ///         <c>GUILayout.Label</c> that leaned on the style's own left margin to place itself,
        ///         while <see cref="KeyValueRow" /> placed its label through a horizontal layout
        ///         group. Two paths, two sets of IMGUI rules, and the caption came out a couple of
        ///         pixels inside the rows it names — measured with every style reporting the same
        ///         edge, which is what ruled the styles out and the layout in.
        ///     </para>
        ///     <para>
        ///         Placing by rect is the fix, and it is the fix because it cannot drift: the caption
        ///         and the rows below it now derive their left edge from the same number rather than
        ///         from two style tables that happen to agree today.
        ///     </para>
        /// </remarks>
        internal static void GroupCaption(GUIContent title, bool customStatus)
        {
            Styles.EnsureStyles();

            GUIStyle caption = Styles.GroupLabel;
            float contentHeight = caption.CalcHeight(title, float.MaxValue);
            float rowHeight = contentHeight + caption.margin.vertical;
            Rect row = GUILayoutUtility.GetRect(
                0f, rowHeight, GUILayout.ExpandWidth(true), GUILayout.Height(rowHeight));

            var labelRect = new Rect(
                row.x + Styles.ContentInset,
                row.y + caption.margin.top,
                Mathf.Max(0f, row.width - Styles.ContentInset),
                contentHeight);

            if (!customStatus)
            {
                GUI.Label(labelRect, title, caption);
                return;
            }

            // The chip sits on the right, vertically centred on the title glyphs.
            float width = PillWidth(CustomStatusPill, true);
            labelRect.width = Mathf.Max(0f, labelRect.width - width - Styles.ContentInset - 6f);
            GUI.Label(labelRect, title, caption);

            var pillRect = new Rect(
                row.xMax - Styles.ContentInset - width,
                labelRect.y + ((labelRect.height - Tokens.PillHeight) * 0.5f),
                width,
                Tokens.PillHeight);
            Pill(pillRect, CustomStatusPill, Tokens.StatusInfo, true);
        }

        /// <summary>
        ///     Caption drawn into a row the caller reserved, for a caption that shares its row with
        ///     a control on the right. The left edge is the same <see cref="Styles.ContentInset" />
        ///     as the layout form; the caption is centred vertically in the row.
        /// </summary>
        internal static void GroupCaption(Rect row, GUIContent title)
        {
            Styles.EnsureStyles();

            GUIStyle caption = Styles.GroupLabel;
            float contentHeight = caption.CalcHeight(title, float.MaxValue);
            GUI.Label(new Rect(
                row.x + Styles.ContentInset,
                row.y + ((row.height - contentHeight) * 0.5f),
                Mathf.Max(0f, row.width - Styles.ContentInset),
                contentHeight), title, caption);
        }

        /// <inheritdoc cref="GroupCaption(GUIContent)" />
        internal static void GroupCaption(string title)
        {
            ScratchCaption.text = title;
            ScratchCaption.tooltip = string.Empty;
            GroupCaption(ScratchCaption);
        }

        #endregion

        #region Buttons

        /// <summary>
        ///     Accent-filled call-to-action. Reserve for the single most important action in a view —
        ///     its visual weight is what makes it readable as "press this one".
        /// </summary>
        internal static bool PrimaryButton(Rect rect, GUIContent content)
        {
            Styles.EnsureStyles();
            bool hover = rect.Contains(Event.current.mousePosition) && GUI.enabled;

            Draw.FillRounded(rect, hover ? Tokens.AccentBright : Tokens.Accent, Tokens.ControlRadius);
            if (hover)
                Draw.StrokeRounded(rect, Tokens.OnAccentHighlight, Tokens.ControlRadius);

            if (GUI.enabled)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return GUI.Button(rect, content, Styles.PrimaryButtonLabel);
        }

        /// <summary>
        ///     Secondary action button — Copy, Fetch, Refresh, Project Settings: an accent outline over
        ///     a faint accent wash, one step quieter than <see cref="PrimaryButton" />.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This used to be a grey outline that only turned green on hover. Beside the Room
        ///         Manager's accent-filled segments it read as a plain Unity box, so inspectors looked
        ///         like two products depending on which control a row happened to use. The accent now
        ///         marks every Convai action at rest, and the filled button stays reserved for the one
        ///         main action.
        ///     </para>
        ///     <para>
        ///         A disabled button drops the wash and the accent, so "cannot press this now" never
        ///         looks like an action waiting to be taken.
        ///     </para>
        /// </remarks>
        internal static bool GhostButton(Rect rect, GUIContent content)
        {
            Styles.EnsureStyles();
            bool enabled = GUI.enabled;
            bool hover = enabled && rect.Contains(Event.current.mousePosition);

            if (enabled)
                Draw.FillRounded(rect, Tokens.Fade(Tokens.Accent, hover ? 0.22f : 0.10f), Tokens.ControlRadius);
            Draw.StrokeRounded(
                rect,
                !enabled ? Tokens.CardBorder : hover ? Tokens.AccentBright : Tokens.Fade(Tokens.Accent, 0.6f),
                Tokens.ControlRadius);

            if (enabled)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return GUI.Button(rect, content, Styles.GhostButtonLabel);
        }

        /// <summary>
        ///     A neutral outline for an unselected tab, where an accent at rest would compete with the
        ///     selected tab's accent fill.
        /// </summary>
        private static bool NeutralButton(Rect rect, GUIContent content)
        {
            Styles.EnsureStyles();
            bool hover = rect.Contains(Event.current.mousePosition) && GUI.enabled;

            if (hover)
                Draw.FillRounded(rect, Tokens.Fade(Tokens.Accent, 0.10f), Tokens.ControlRadius);
            Draw.StrokeRounded(
                rect,
                hover ? Tokens.Fade(Tokens.Accent, 0.75f) : Tokens.CardBorder,
                Tokens.ControlRadius);

            if (GUI.enabled)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return GUI.Button(rect, content, Styles.GhostButtonLabel);
        }

        /// <summary>Small square glyph button (reorder, remove, clear). Shows a soft hover plate.</summary>
        internal static bool IconButton(Rect rect, GUIContent content)
        {
            Styles.EnsureStyles();
            bool hover = rect.Contains(Event.current.mousePosition) && GUI.enabled;

            if (hover)
                Draw.FillRounded(rect, Tokens.HoverPlate, Tokens.IconButtonRadius);

            if (GUI.enabled)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return GUI.Button(rect, content, Styles.IconButtonLabel);
        }

        /// <summary>Full-width <see cref="PrimaryButton" /> that reserves its own layout row.</summary>
        internal static bool PrimaryButtonLayout(GUIContent content, float height = 28f) =>
            PrimaryButton(GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true)), content);

        /// <summary>Full-width <see cref="GhostButton" /> that reserves its own layout row.</summary>
        internal static bool GhostButtonLayout(GUIContent content, float height = 22f) =>
            GhostButton(GUILayoutUtility.GetRect(0f, height, GUILayout.ExpandWidth(true)), content);

        /// <summary>Breathing room each side of a ghost button's label.</summary>
        private const float GhostButtonSidePadding = 14f;

        /// <summary>Space between neighbouring buttons in a row, and between a field and its inline actions.</summary>
        private const float ButtonGap = 6f;

        /// <summary>Side padding of a ghost button that shares a single-line row with a field.</summary>
        private const float InlineButtonSidePadding = 9f;

        /// <summary>
        ///     Narrowest an inline action gets. Sized for the common verbs (Copy, Fetch, Clear,
        ///     Refresh), so their fields all end on one edge instead of wherever each label's width put
        ///     them; a longer label still grows to fit.
        /// </summary>
        private const float InlineButtonMinWidth = 64f;

        /// <summary>
        ///     A section's secondary actions: up to three ghost buttons, each sized to its label, on
        ///     one row at <see cref="Styles.ContentInset" />. Returns the index clicked this pass, or -1.
        ///     A null slot is skipped without leaving a gap and keeps its index, so a button that only
        ///     sometimes applies can be passed conditionally.
        /// </summary>
        /// <remarks>
        ///     The same eight lines were written out by hand in several inspectors, each with its own
        ///     gap and its own idea of where the row starts. Fixed overloads rather than
        ///     <c>params</c> so a repaint does not allocate an array.
        /// </remarks>
        internal static int GhostButtonRow(GUIContent first, GUIContent second = null, GUIContent third = null)
        {
            Rect row = GUILayoutUtility.GetRect(0f, 22f, GUILayout.ExpandWidth(true));
            float x = row.x + Styles.ContentInset;
            int clicked = -1;

            if (RowButton(ref x, row, first)) clicked = 0;
            if (RowButton(ref x, row, second)) clicked = 1;
            if (RowButton(ref x, row, third)) clicked = 2;
            return clicked;
        }

        private static bool RowButton(ref float x, Rect row, GUIContent content)
        {
            if (content == null) return false;

            float width = GhostButtonWidth(content);
            bool pressed = GhostButton(new Rect(x, row.y, width, row.height), content);
            x += width + ButtonGap;
            return pressed;
        }

        /// <summary>
        ///     One single-line control row with up to two compact ghost buttons at its right end —
        ///     Copy beside an ID, Fetch beside the name it fills. Returns the rect left for the field;
        ///     <paramref name="clicked" /> is the index pressed this pass, or -1.
        /// </summary>
        /// <remarks>
        ///     An action belongs beside the value it acts on. A row of buttons under a block of fields
        ///     makes the reader match each button to its field by name; beside the field there is
        ///     nothing to match. Disabled buttons still carry their tooltip, which is where the reason
        ///     they are disabled goes.
        /// </remarks>
        internal static Rect FieldWithActions(
            GUIContent first, bool firstEnabled, GUIContent second, bool secondEnabled, out int clicked)
        {
            clicked = -1;
            float actionsWidth = InlineButtonSlot(first) + InlineButtonSlot(second);
            float estimatedField = EditorGUIUtility.currentViewWidth - EstimatedViewChrome -
                                   EditorGUIUtility.labelWidth - actionsWidth;

            Rect row = EditorGUILayout.GetControlRect();
            if (estimatedField >= MinInlineFieldWidth)
            {
                float x = row.xMax;
                if (InlineButton(ref x, row, second, secondEnabled)) clicked = 1;
                if (InlineButton(ref x, row, first, firstEnabled)) clicked = 0;
                return new Rect(row.x, row.y, Mathf.Max(0f, x - row.x), row.height);
            }

            // Too narrow to share: the field keeps its whole row and the actions sit right-aligned on
            // the row beneath it, rather than squeezing the value down to a few pixels.
            Rect actions = EditorGUILayout.GetControlRect();
            float right = actions.xMax;
            if (InlineButton(ref right, actions, second, secondEnabled)) clicked = 1;
            if (InlineButton(ref right, actions, first, firstEnabled)) clicked = 0;
            return row;
        }

        /// <summary>Narrowest a field may get beside its inline actions before they move below it.</summary>
        private const float MinInlineFieldWidth = 72f;

        /// <summary>Inspector margins, section padding and scrollbar the layout pass cannot see.</summary>
        private const float EstimatedViewChrome = 80f;

        /// <summary>Width one inline action takes from its row, gap included; none for a null slot.</summary>
        private static float InlineButtonSlot(GUIContent content)
        {
            if (content == null) return 0f;

            Styles.EnsureStyles();
            return Mathf.Max(
                       InlineButtonMinWidth,
                       ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, content) + (InlineButtonSidePadding * 2f)) +
                   ButtonGap;
        }

        /// <summary>Draws one inline button ending at <paramref name="right" /> and moves the edge left past it.</summary>
        private static bool InlineButton(ref float right, Rect row, GUIContent content, bool enabled)
        {
            if (content == null) return false;

            Styles.EnsureStyles();
            float width = Mathf.Max(
                InlineButtonMinWidth,
                ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, content) + (InlineButtonSidePadding * 2f));
            right -= width;
            bool pressed;
            using (new EditorGUI.DisabledScope(!enabled))
                pressed = GhostButton(new Rect(right, row.y, width, row.height), content);
            right -= ButtonGap;
            return pressed;
        }

        /// <summary>
        ///     A labelled single-line choice between a few named options — Default or Custom, Off or
        ///     on-device — drawn as segments rather than a dropdown, so every option and the current
        ///     one are visible without a click. Returns the selected index after this pass.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Wrapped in <see cref="EditorGUI.BeginProperty" /> when a property is passed, so prefab
        ///         overrides still show in bold and the right-click Revert menu still works — the two things
        ///         a hand-drawn control usually loses.
        ///     </para>
        ///     <para>
        ///         When the options need more room than the field column has, the segments start early,
        ///         in the part of the label column the label itself does not use. A Convai card's label
        ///         column is a share of the view, so a short label ("Visible To", "Mode") leaves most of
        ///         it empty while four options squeezed into the field column were cut off mid-word.
        ///         That one row stops lining up with the fields above it; nothing is clipped.
        ///     </para>
        /// </remarks>
        internal static int LabeledSegments(
            GUIContent label, GUIContent[] options, int selectedIndex, SerializedProperty property = null)
        {
            Rect row = EditorGUILayout.GetControlRect();
            if (property != null)
                label = EditorGUI.BeginProperty(row, label, property);

            Rect field = EditorGUI.PrefixLabel(row, label);
            float shortfall = SegmentsNaturalWidth(options) - field.width;
            if (shortfall > 0f)
            {
                // A prefab override draws the label bold, and bold runs wider.
                GUIStyle labelStyle = property != null && property.prefabOverride
                    ? EditorStyles.boldLabel
                    : EditorStyles.label;
                float labelEnd = EditorGUI.IndentedRect(row).x +
                                 ConvaiEditorTextMetrics.Width(labelStyle, label) + SegmentsLabelGap;
                field.xMin = Mathf.Max(Mathf.Min(labelEnd, field.xMin), field.xMin - shortfall);
            }

            int result = SegmentedPicker(field, options, selectedIndex);

            if (property != null)
                EditorGUI.EndProperty();
            return result;
        }

        /// <summary>
        ///     A sequential enum property as <see cref="LabeledSegments" />, one segment per value in
        ///     declaration order. Does nothing when <paramref name="property" /> is null.
        /// </summary>
        internal static void EnumSegments(GUIContent label, GUIContent[] options, SerializedProperty property)
        {
            if (property == null || options == null || options.Length == 0)
                return;

            int current = Mathf.Clamp(property.enumValueIndex, 0, options.Length - 1);
            int next = LabeledSegments(label, options, current, property);
            if (next != current)
                property.enumValueIndex = next;
        }

        /// <summary>Space between neighbouring segments of a segmented picker.</summary>
        private const float SegmentGap = 2f;

        /// <summary>Room a segment adds around its label.</summary>
        private const float SegmentLabelPadding = 12f;

        /// <summary>Space kept between a segmented row's label and segments that start early.</summary>
        private const float SegmentsLabelGap = 6f;

        /// <summary>Width a segmented picker needs to show every option in full, gaps included.</summary>
        private static float SegmentsNaturalWidth(GUIContent[] options)
        {
            if (options == null || options.Length == 0)
                return 0f;

            Styles.EnsureStyles();
            float width = SegmentGap * (options.Length - 1);
            for (int i = 0; i < options.Length; i++)
                width += ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, options[i]) + SegmentLabelPadding;
            return width;
        }

        /// <summary>Rect form of <see cref="SegmentedPicker(GUIContent[],int,float)" />: equal segments across <paramref name="rect" />.</summary>
        internal static int SegmentedPicker(Rect rect, GUIContent[] options, int selectedIndex)
        {
            Styles.EnsureStyles();
            if (options == null || options.Length == 0)
                return selectedIndex;

            // Each segment gets its label's width plus an equal share of the spare room, so a long
            // label ("Characters") is not squeezed to the same width as a short one ("Off"). Short of
            // room, every segment shrinks in proportion instead.
            int count = options.Length;
            float available = Mathf.Max(0f, rect.width - (SegmentGap * (count - 1)));
            float natural = 0f;
            for (int i = 0; i < count; i++)
                natural += ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, options[i]) + SegmentLabelPadding;

            float spare = (available - natural) / count;
            float scale = natural > 0f ? available / natural : 1f;
            int result = selectedIndex;
            float x = rect.x;

            for (int i = 0; i < count; i++)
            {
                float labelWidth = ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, options[i]) + SegmentLabelPadding;
                float width = spare >= 0f ? labelWidth + spare : labelWidth * scale;
                var segment = new Rect(x, rect.y, width, rect.height);
                x += width + SegmentGap;
                bool selected = i == selectedIndex;
                bool hover = GUI.enabled && segment.Contains(Event.current.mousePosition);

                if (selected)
                    Draw.FillRounded(segment, hover ? Tokens.AccentBright : Tokens.Accent, Tokens.ControlRadius);
                else
                {
                    Draw.FillRounded(segment, hover ? Tokens.CardBgHover : Tokens.CardBg, Tokens.ControlRadius);
                    Draw.StrokeRounded(
                        segment, hover ? Tokens.Fade(Tokens.Accent, 0.6f) : Tokens.CardBorder, Tokens.ControlRadius);
                }

                if (GUI.enabled)
                    EditorGUIUtility.AddCursorRect(segment, MouseCursor.Link);

                if (GUI.Button(segment, options[i], selected ? Styles.PrimaryButtonLabel : Styles.GhostButtonLabel))
                {
                    result = i;
                    GUI.changed = true;
                }
            }

            return result;
        }

        /// <summary>
        ///     Width a <see cref="GhostButton" /> needs to show <paramref name="content" /> without
        ///     clipping it — the counterpart of <see cref="PillWidth" />, for the case where a ghost
        ///     button sits beside other controls and so cannot use
        ///     <see cref="GhostButtonLayout" />'s full-width row.
        /// </summary>
        internal static float GhostButtonWidth(GUIContent content)
        {
            Styles.EnsureStyles();
            return ConvaiEditorTextMetrics.Width(Styles.GhostButtonLabel, content) +
                   (GhostButtonSidePadding * 2f);
        }

        #endregion

        #region Pills and chips

        /// <summary>Horizontal breathing room on each side of a pill's content run.</summary>
        private const float PillSidePadding = 9f;

        /// <summary>Space between the status dot and the label it introduces.</summary>
        private const float PillDotGap = 5f;

        /// <summary>Total width the dot occupies, including the gap after it.</summary>
        private const float PillDotExtra = (Tokens.PillDotRadius * 2f) + PillDotGap;

        /// <summary>Measured width for a pill drawn with <see cref="Pill" /> or <see cref="PillButton" />.</summary>
        /// <remarks>
        ///     Must stay the single source of truth for pill width: <see cref="Pill" /> centres its
        ///     content run inside whatever rect it is given, so a caller that measures differently gets
        ///     a pill with uneven padding rather than a clipped one — which is harder to notice and was
        ///     exactly the bug that made the status dot look misplaced.
        /// </remarks>
        internal static float PillWidth(GUIContent content, bool dot = false)
        {
            Styles.EnsureStyles();
            return ConvaiEditorTextMetrics.Width(Styles.PillLabel, content) +
                   (PillSidePadding * 2f) + (dot ? PillDotExtra : 0f);
        }

        /// <summary>
        ///     Non-interactive tinted pill — status chips, badges, counts. Pass <paramref name="dot" />
        ///     for a leading status dot, which makes a state chip readable at a glance before the text
        ///     is. Measure with <see cref="PillWidth" /> using the same <paramref name="dot" /> value.
        /// </summary>
        internal static void Pill(Rect rect, GUIContent content, Color tint, bool dot = false)
        {
            Styles.EnsureStyles();
            float radius = rect.height * 0.5f;
            Draw.FillAndStrokeRounded(rect, Tokens.Tint(tint), Tokens.TintBorder(tint), radius);

            DrawPillContent(rect, content, tint, dot, Styles.PillLabelTinted(tint), Styles.PillLabel);
        }

        /// <summary>
        ///     Lays the dot and the label out as one horizontal run centred in <paramref name="rect" />,
        ///     and returns the label rect so an interactive pill can reuse the same geometry.
        /// </summary>
        /// <remarks>
        ///     Centring the whole run is what keeps the padding symmetric: position the dot at a fixed
        ///     inset and centre the text separately, and the two gaps end up different widths — the
        ///     shorter the label, the more obviously the content sits off to one side. The label is
        ///     drawn <see cref="TextAnchor.MiddleLeft" /> in an exactly-measured rect rather than
        ///     centred in a loose one, so its position follows from the measurement instead of from
        ///     IMGUI re-centring it inside padding of its own.
        /// </remarks>
        /// <param name="label">The tinted style the text is drawn with.</param>
        /// <param name="metrics">
        ///     The untinted style the text is measured with. Same typography, one instance for every
        ///     tint — so a status pill drawn in four colours keeps one memoised width per string
        ///     rather than four, and <see cref="PillWidth" /> measures against the same key its
        ///     callers do.
        /// </param>
        private static Rect DrawPillContent(
            Rect rect, GUIContent content, Color tint, bool dot, GUIStyle label, GUIStyle metrics)
        {
            float textWidth = ConvaiEditorTextMetrics.Width(metrics, content);
            float runWidth = textWidth + (dot ? PillDotExtra : 0f);
            float runX = rect.x + ((rect.width - runWidth) * 0.5f);
            float centreY = rect.y + (rect.height * 0.5f);

            if (dot)
            {
                Draw.FillCircle(new Vector2(runX + Tokens.PillDotRadius, centreY), Tokens.PillDotRadius, tint);
                runX += PillDotExtra;
            }

            var labelRect = new Rect(runX, rect.y, textWidth, rect.height);
            GUI.Label(labelRect, content, label);
            return labelRect;
        }

        /// <summary>Clickable tinted pill — a status chip that navigates to what it reports.</summary>
        /// <param name="dot">
        ///     Draws the same leading status dot <see cref="Pill" /> does. A header chip that becomes
        ///     clickable must keep its dot: losing it would make an actionable chip read as a
        ///     different kind of thing from the inert one beside it, when the only difference is that
        ///     this one leads somewhere.
        /// </param>
        internal static bool PillButton(Rect rect, GUIContent content, Color tint, bool dot = false)
        {
            Styles.EnsureStyles();
            bool hover = rect.Contains(Event.current.mousePosition) && GUI.enabled;
            float radius = rect.height * 0.5f;

            Draw.FillRounded(
                rect,
                hover ? Tokens.Fade(tint, EditorGUIUtility.isProSkin ? 0.18f : 0.24f) : Tokens.Tint(tint),
                radius);
            Draw.StrokeRounded(rect, Tokens.TintBorder(tint), radius);

            GUIStyle style = Styles.ChipLabelTinted(tint);

            if (GUI.enabled)
                EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

            if (!dot)
                return GUI.Button(rect, content, style);

            // The dot and label are laid out by the same routine the inert pill uses, so a chip does
            // not shift by a pixel on the pass where it becomes clickable.
            bool pressed = GUI.Button(rect, GUIContent.none, style);
            DrawPillContent(rect, content, tint, true, style, Styles.ChipLabel);
            return pressed;
        }

        /// <summary>
        ///     A row of mutually exclusive choices — personality archetypes, character types, modes.
        ///     The selected segment is filled in the brand accent; the rest read as quiet surfaces.
        ///     Returns the index the user clicked this pass, or -1.
        /// </summary>
        /// <remarks>
        ///     Lives here because modules never reference each other, so a picker any two of them need
        ///     has to be owned by the design system or written twice. The obvious local substitute —
        ///     <c>EditorStyles.miniButton</c> tinted through <see cref="GUI.backgroundColor" /> — reads
        ///     as a pressed toolbar button rather than a selection, and cannot follow the brand accent.
        /// </remarks>
        internal static int SegmentedPicker(GUIContent[] options, int selectedIndex, float height = 22f)
        {
            Styles.EnsureStyles();
            if (options == null || options.Length == 0)
                return -1;

            int clicked = -1;
            using (new EditorGUILayout.HorizontalScope())
            {
                for (int i = 0; i < options.Length; i++)
                {
                    // Measured with the style it is actually drawn with, so a long option reserves the
                    // width it will really need.
                    Rect rect = GUILayoutUtility.GetRect(
                        options[i], Styles.GhostButtonLabel, GUILayout.Height(height), GUILayout.ExpandWidth(true));

                    bool selected = i == selectedIndex;
                    bool hover = GUI.enabled && rect.Contains(Event.current.mousePosition);

                    if (selected)
                    {
                        Draw.FillRounded(rect, hover ? Tokens.AccentBright : Tokens.Accent, Tokens.ControlRadius);
                    }
                    else
                    {
                        Draw.FillRounded(rect, hover ? Tokens.CardBgHover : Tokens.CardBg, Tokens.ControlRadius);
                        Draw.StrokeRounded(
                            rect,
                            hover ? Tokens.Fade(Tokens.Accent, 0.6f) : Tokens.CardBorder,
                            Tokens.ControlRadius);
                    }

                    if (GUI.enabled)
                        EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);

                    GUIStyle label = selected ? Styles.PrimaryButtonLabel : Styles.GhostButtonLabel;
                    if (GUI.Button(rect, options[i], label))
                        clicked = i;
                }
            }

            return clicked;
        }

        /// <summary>
        ///     A named-preset row: caption, optional Custom status pill, segmented picker, and a
        ///     one-line explanation. When <paramref name="selectedIndex"/> is negative the pills stay
        ///     unselected and Custom is shown — identity never goes blank.
        /// </summary>
        /// <param name="explanation">
        ///     The matched preset's description, or <see cref="ConvaiEditorProfileField.CustomCaption" />
        ///     when nothing is selected. Empty hides the line.
        /// </param>
        internal static int PresetPicker(
            GUIContent caption,
            GUIContent[] options,
            int selectedIndex,
            string explanation,
            float height = 22f)
        {
            GroupCaption(caption, selectedIndex < 0);
            int clicked = SegmentedPicker(options, selectedIndex, height);
            if (string.IsNullOrEmpty(explanation)) return clicked;

            ScratchExplanation.text = explanation;
            ScratchExplanation.tooltip = string.Empty;
            GUILayout.Label(ScratchExplanation, Styles.CaptionWrapped);
            return clicked;
        }

        #endregion

        #region Stat tiles

        /// <summary>
        ///     One centred stat tile — a number over a caption, as a rounded recessed panel. Draw
        ///     several inside a <see cref="EditorGUILayout.HorizontalScope" /> to form a stat row.
        /// </summary>
        internal static void StatTile(GUIContent label, string value, Color? numberTint = null)
        {
            Styles.EnsureStyles();
            Rect rect = EditorGUILayout.BeginVertical(Styles.PanelBody, GUILayout.ExpandWidth(true));
            Draw.FillAndStrokeRounded(
                rect,
                Tokens.InnerBg,
                numberTint.HasValue ? Tokens.TintBorder(numberTint.Value) : Tokens.CardBorder,
                Tokens.PanelRadius);

            GUILayout.Label(value, Styles.TileNumberTinted(numberTint ?? Tokens.TextPrimary));
            GUILayout.Label(label, Styles.TileLabel);
            EditorGUILayout.EndVertical();
        }

        /// <summary>
        ///     A stat tile that leads somewhere. Returns true on the pass it was clicked.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Same tile, one affordance added: a hover surface and a link cursor. A tile that
        ///         reports a problem and cannot open it is the defect this exists to close — the
        ///         Actions inspector showed <c>ISSUES 1</c> as a dead label while the findings behind
        ///         that 1 were already computed and thrown away.
        ///     </para>
        ///     <para>
        ///         <paramref name="clickable" /> is a parameter rather than a separate call site
        ///         because the same tile is inert at zero: a click that opens a report saying nothing
        ///         is wrong is a worse answer than a number the user can already read.
        ///     </para>
        /// </remarks>
        internal static bool StatTile(GUIContent label, string value, Color? numberTint, bool clickable)
        {
            if (!clickable)
            {
                StatTile(label, value, numberTint);
                return false;
            }

            Styles.EnsureStyles();
            Rect rect = EditorGUILayout.BeginVertical(Styles.PanelBody, GUILayout.ExpandWidth(true));
            bool hover = GUI.enabled && rect.Contains(Event.current.mousePosition);

            Draw.FillAndStrokeRounded(
                rect,
                hover ? Tokens.CardBgHover : Tokens.InnerBg,
                numberTint.HasValue
                    ? Tokens.TintBorder(numberTint.Value)
                    : hover
                        ? Tokens.Fade(Tokens.Accent, 0.6f)
                        : Tokens.CardBorder,
                Tokens.PanelRadius);

            GUILayout.Label(value, Styles.TileNumberTinted(numberTint ?? Tokens.TextPrimary));
            GUILayout.Label(label, Styles.TileLabel);
            EditorGUILayout.EndVertical();

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.Link);
            return GUI.Button(rect, GUIContent.none, Styles.InvisibleButton);
        }

        /// <summary>
        ///     A labelled live telemetry readout — small uppercase caption over a tinted value.
        ///     Fixed width so a row of them stays aligned as values change.
        /// </summary>
        internal static void LiveCell(string label, string value, Color valueColor, float width = 104f, bool bold = false)
        {
            Styles.EnsureStyles();
            using (new EditorGUILayout.VerticalScope(
                       GUILayout.Width(width), GUILayout.Height(32f), GUILayout.ExpandWidth(false)))
            {
                EditorGUILayout.LabelField(
                    label.ToUpperInvariant(), Styles.LiveCellLabel,
                    GUILayout.Width(width), GUILayout.Height(14f));
                EditorGUILayout.LabelField(
                    value, Styles.LiveCellValueTinted(valueColor, bold),
                    GUILayout.Width(width), GUILayout.Height(16f));
            }
        }

        /// <summary>
        ///     One key/value reading: a fixed-width label column and the value beside it. Use for any
        ///     "what is this set to" or "what is it doing right now" line — a stack of these is the
        ///     Convai way to report state that the user reads rather than edits.
        /// </summary>
        /// <remarks>
        ///     The label column is a fixed <see cref="Tokens.ReadingLabelWidth" /> rather than measured
        ///     per row on purpose. A column that sizes to its own label puts every value at a different
        ///     x, so a stack of readings reads as ragged text instead of as a table.
        /// </remarks>
        /// <remarks>
        ///     Drawn into one reserved rect rather than through a horizontal layout group, so the
        ///     label's left edge is <see cref="Styles.ContentInset" /> and nothing else — the same
        ///     number <see cref="GroupCaption" /> uses. A horizontal group applies its first child's
        ///     margin by its own rules, which is how a row and the caption above it ended up on
        ///     different pixels while both styles reported the same edge.
        /// </remarks>
        internal static void KeyValueRow(
            GUIContent label, string value, Color? valueTint = null, float labelWidth = Tokens.ReadingLabelWidth)
        {
            Styles.EnsureStyles();

            Rect row = GUILayoutUtility.GetRect(
                0f, Tokens.ReadingRowHeight, GUILayout.ExpandWidth(true));

            float x = row.x + Styles.ContentInset;
            float available = Mathf.Max(0f, row.xMax - Styles.ContentInset - x);
            float keyWidth = Mathf.Min(labelWidth, available);

            GUI.Label(new Rect(x, row.y, keyWidth, row.height), label, Styles.ReadingLabel);
            GUI.Label(
                new Rect(x + keyWidth, row.y, Mathf.Max(0f, available - keyWidth), row.height),
                value ?? string.Empty,
                valueTint.HasValue ? Styles.ReadingValueTinted(valueTint.Value) : Styles.ReadingValue);
        }

        /// <summary>Height of one check's title row.</summary>
        private const float CheckRowHeight = 18f;

        /// <summary>Width of the column the status dot sits in, left of a check's title.</summary>
        private const float CheckDotColumn = 14f;

        /// <summary>
        ///     One validation check: a status dot, its title, the word for anything that is not
        ///     healthy, and the sentence underneath. Pass a null or empty
        ///     <paramref name="statusWord" /> for a healthy check.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A healthy check says nothing on the right. The dot has already said it, and a column
        ///         of the word "Ready" teaches the eye to skip the place where "Blocked" will appear.
        ///         The word is there at all so a row never relies on colour alone.
        ///     </para>
        ///     <para>
        ///         The title is the reading and the sentence under it the detail, so the title takes
        ///         primary text and the sentence muted. They were the other way round — a 10pt muted
        ///         label-column style over body text — which made "Configured and validated" louder
        ///         than the "API Key" it describes.
        ///     </para>
        /// </remarks>
        internal static void CheckRow(Color tint, string statusWord, string title, string message)
        {
            Styles.EnsureStyles();
            bool healthy = string.IsNullOrEmpty(statusWord);

            Rect row = GUILayoutUtility.GetRect(0f, CheckRowHeight, GUILayout.ExpandWidth(true));
            Draw.StatusDot(new Rect(row.x, row.y, CheckDotColumn, row.height), tint, !healthy);

            float statusWidth = healthy
                ? 0f
                : Mathf.Min(ConvaiEditorTextMetrics.Width(Styles.MicroLabelRight, statusWord) + 8f, row.width * 0.4f);

            GUI.Label(
                new Rect(
                    row.x + CheckDotColumn, row.y,
                    Mathf.Max(0f, row.width - CheckDotColumn - statusWidth), row.height),
                title,
                Styles.ReadingValue);

            if (statusWidth > 0f)
                GUI.Label(
                    new Rect(row.xMax - statusWidth, row.y, statusWidth, row.height),
                    statusWord,
                    Styles.MicroLabelRightTinted(tint));

            if (!string.IsNullOrWhiteSpace(message))
                GUILayout.Label(message, Styles.MutedWrapped);

            GUILayout.Space(6f);
        }

        #endregion

        #region Mode bar

        /// <summary>
        ///     The row of tabs that switches an editor window between its top-level views, with the
        ///     hairline that separates it from the content below. Returns the index the user clicked
        ///     this pass, or -1.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Shared, because a mode bar written privately per window drifts in every dimension it
        ///         has — tab height, label padding, gap — and none of those differences is ever a
        ///         decision. The visible result is windows of one product disagreeing about how wide a
        ///         tab is.
        ///     </para>
        ///     <para>
        ///         Index-based rather than generic so a caller with a conditional tab (a Live view that
        ///         only exists in Play mode) builds the visible set it wants and maps the result back
        ///         itself, without this control needing to know the caller's enum.
        ///     </para>
        /// </remarks>
        internal static int ModeBar(GUIContent[] options, int selectedIndex)
        {
            Styles.EnsureStyles();
            if (options == null || options.Length == 0)
                return -1;

            Rect row = GUILayoutUtility.GetRect(0f, Tokens.ModeBarHeight, GUILayout.ExpandWidth(true));
            Rect slice = Draw.CenteredSlice(row, Tokens.ModeTabHeight);

            int clicked = -1;
            float x = row.x + Tokens.ModeBarInset;
            for (int i = 0; i < options.Length; i++)
            {
                float width = PillWidth(options[i]) + Tokens.ModeTabPadding;
                var rect = new Rect(x, slice.y, width, Tokens.ModeTabHeight);
                bool selected = i == selectedIndex;

                if (selected ? PrimaryButton(rect, options[i]) : NeutralButton(rect, options[i]))
                {
                    if (!selected)
                        clicked = i;
                }

                x += width + Tokens.ModeTabGap;
            }

            // Spans the bar's own row, not the window: a mode bar nested in a window's right pane
            // would otherwise draw its separator back across the character list beside it, reading
            // as a black seam cutting through that card rather than as the underline of these tabs.
            // Where the bar does own the full width the row is the full width, so this is the same line.
            Draw.DividerLine(new Rect(row.x, row.yMax - 1f, row.width, 1f), Tokens.Divider);
            return clicked;
        }

        #endregion

        #region Search field

        /// <summary>
        ///     Rounded search field with a leading glyph, placeholder and clear button. Returns the new
        ///     value. <paramref name="controlName" /> must be unique per field so focus tracking (and
        ///     therefore the placeholder) works when a window hosts more than one.
        /// </summary>
        /// <remarks>
        ///     <paramref name="help" /> is what the field says on hover, and it belongs on the field
        ///     rather than on <paramref name="placeholder" />: the placeholder is painted inside the
        ///     box and is gone the moment the box is focused or has a value, so a tooltip carried
        ///     there would only be readable in the one state where the reader is not yet looking. The
        ///     whole rounded rect answers instead, in every state, which is also what the
        ///     placeholder-carries-a-label-and-nothing-else rule expects of the field it sits in.
        /// </remarks>
        internal static string SearchField(
            Rect rect, string value, GUIContent placeholder, string help, GUIContent clearButton, string controlName)
        {
            Styles.EnsureStyles();
            bool focused = GUI.GetNameOfFocusedControl() == controlName;
            float radius = rect.height * 0.5f;

            Draw.FillRounded(rect, Tokens.CardBg, radius);
            Draw.StrokeRounded(rect, focused ? Tokens.Fade(Tokens.Accent, 0.8f) : Tokens.CardBorder, radius);

            // Drawn before the field's own contents so anything with a tooltip of its own — the clear
            // button — still wins the hover, since the last content drawn under the cursor is the one
            // Unity reports.
            ScratchSearchHelp.tooltip = help ?? string.Empty;
            GUI.Label(rect, ScratchSearchHelp, GUIStyle.none);

            GUI.Label(
                new Rect(rect.x + 6f, rect.y, 20f, rect.height),
                ConvaiEditorGlyphs.Discovery, Styles.SearchGlyph);

            var textRect = new Rect(rect.x + 28f, rect.y + 2f, rect.width - 52f, rect.height - 4f);
            GUI.SetNextControlName(controlName);
            string result = GUI.TextField(textRect, value ?? string.Empty, Styles.SearchText);
            if (string.IsNullOrEmpty(result) && !focused)
                GUI.Label(textRect, placeholder, Styles.SearchPlaceholder);

            if (!string.IsNullOrEmpty(result))
            {
                var clearRect = new Rect(rect.xMax - 22f, rect.y + ((rect.height - 16f) * 0.5f), 16f, 16f);
                if (IconButton(clearRect, clearButton))
                {
                    result = string.Empty;
                    GUIUtility.keyboardControl = 0;
                }
            }

            return result;
        }

        /// <summary>Invisible hover target for the search field's help, reused so it allocates nothing per repaint.</summary>
        private static readonly GUIContent ScratchSearchHelp = new(string.Empty);

        /// <summary>
        ///     An ordinary text field that shows <paramref name="placeholder" /> in muted text while it
        ///     is empty and unfocused. Returns the new value.
        /// </summary>
        /// <remarks>
        ///     Use this instead of a sentence under the field when the guidance is an example of what
        ///     to type. A field repeated once per list entry cannot carry prose beside it — the same
        ///     sentence four rows down reads as noise and trains the eye to skip that whole region —
        ///     but the same guidance inside the control is read exactly once, by whoever is about to
        ///     type, and is gone the moment there is a real value to show.
        ///     <paramref name="controlName" /> must be unique per field so focus tracking works.
        /// </remarks>
        internal static string PlaceholderTextField(
            Rect rect, string value, GUIContent placeholder, string controlName)
        {
            Styles.EnsureStyles();

            GUI.SetNextControlName(controlName);
            string result = EditorGUI.TextField(rect, value ?? string.Empty);

            if (string.IsNullOrEmpty(result) && GUI.GetNameOfFocusedControl() != controlName)
                GUI.Label(rect, placeholder, Styles.FieldPlaceholder);

            return result;
        }

        #endregion

        #region Prose fields

        /// <summary>
        ///     A labelled text field for authored prose: one line tall while the text fits on one
        ///     line, growing to fit whatever is written into it. Returns the new value.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Why the width is remembered rather than asked for.</b> IMGUI decides an element's
        ///         height during the layout pass, before widths are resolved. A wrapping field placed
        ///         in a horizontal group is therefore measured at the width its text would need
        ///         <em>unwrapped</em> — which is always one line, so it can never grow. That is not a
        ///         tuning problem, it is the reason Unity's own <c>[TextArea]</c> puts its label on a
        ///         line of its own and takes the full width below: a wrapping field in a vertical flow
        ///         knows its width, one beside a label does not.
        ///     </para>
        ///     <para>
        ///         Keeping the label in its column is worth one frame of latency, so the field's width
        ///         is recorded on repaint and used to measure the next pass. The first draw is one line
        ///         tall and every draw after it is right; a window resize settles in the same frame the
        ///         resize itself repaints. <paramref name="key" /> must be unique per field, since that
        ///         is what the remembered width is filed under.
        ///     </para>
        ///     <para>
        ///         A fixed-height box was the alternative and is worse: prose fields are usually short,
        ///         so a box tall enough for the rare long description wastes three rows on every entry
        ///         that does not need it. Growing on demand costs nothing in the common case and stops
        ///         the field from hiding its own contents in the uncommon one.
        ///     </para>
        ///     <para>
        ///         <b>There is no line cap.</b> There was one, and it recreated in miniature the defect
        ///         this control exists to fix: <see cref="EditorGUI.TextArea(Rect, string, GUIStyle)" />
        ///         has no scrollbar, so a capped field silently clips whatever runs past the cap and
        ///         shows nothing to say it did — the author cannot see their own text or tell that
        ///         there is more of it. Wrapping the field in a scroll view is not the fix either; in
        ///         IMGUI that fights the caret. Growing without a limit is the only shape in which
        ///         nothing is ever hidden, and the pathological case — someone writing twenty lines
        ///         into a field the product calls "a short description" — is visible, self-inflicted,
        ///         and absorbed by the pane's own scrolling.
        ///     </para>
        /// </remarks>
        internal static string ProseField(
            GUIContent label, string value, string key, GUIContent placeholder = null, float minHeight = 0f)
        {
            Styles.EnsureStyles();

            GUIStyle style = Styles.GrowingTextArea;
            minHeight = Mathf.Max(minHeight, EditorGUIUtility.singleLineHeight);

            ProseFieldWidths.TryGetValue(key, out float lastWidth);

            float height = minHeight;
            if (lastWidth > 1f && !string.IsNullOrEmpty(value))
                height = Mathf.Max(minHeight, ConvaiEditorTextMetrics.WrappedHeight(style, value, lastWidth));

            Rect row = EditorGUILayout.GetControlRect(false, height);
            Rect field = EditorGUI.PrefixLabel(row, label);

            if (Event.current.type == EventType.Repaint && !Mathf.Approximately(field.width, lastWidth))
                ProseFieldWidths[key] = field.width;

            GUI.SetNextControlName(key);
            string result = EditorGUI.TextArea(field, value ?? string.Empty, style);

            if (placeholder != null && string.IsNullOrEmpty(result) && GUI.GetNameOfFocusedControl() != key)
                GUI.Label(field, placeholder, Styles.FieldPlaceholder);

            return result;
        }

        /// <summary>
        ///     Each prose field's width as of its last repaint, so the next layout pass can measure the
        ///     wrapped height at the width the field will actually be drawn at.
        /// </summary>
        private static readonly Dictionary<string, float> ProseFieldWidths = new();

        #endregion
    }
}
