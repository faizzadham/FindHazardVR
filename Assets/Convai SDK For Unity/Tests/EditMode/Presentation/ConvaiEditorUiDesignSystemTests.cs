#if UNITY_EDITOR
using System;
using Convai.Editor.Actions;
using Convai.Editor.Inspectors;
using Convai.Editor.UI;
using Convai.Shared.Actions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.Presentation
{
    public class ConvaiEditorUiDesignSystemTests
    {
        [Test]
        public void SectionStateStore_GetSet_RoundTrips()
        {
            string hostId = $"Host_{Guid.NewGuid():N}";
            string sectionId = "Core Setup";
            string key = ConvaiEditorSectionState.BuildKey(hostId, sectionId);

            EditorPrefs.DeleteKey(key);
            Assert.IsFalse(ConvaiEditorSectionState.Get(hostId, sectionId, false));

            ConvaiEditorSectionState.Set(hostId, sectionId, true);
            Assert.IsTrue(ConvaiEditorSectionState.Get(hostId, sectionId, false));

            ConvaiEditorSectionState.Set(hostId, sectionId, false);
            Assert.IsFalse(ConvaiEditorSectionState.Get(hostId, sectionId, true));

            EditorPrefs.DeleteKey(key);
        }

        [Test]
        public void SectionStateStore_BuildKey_NormalizesWhitespace()
        {
            string key = ConvaiEditorSectionState.BuildKey("Map Debug Window", "Validation Results");
            Assert.AreEqual("Convai.Editor.MapDebugWindow.ValidationResults.Expanded", key);
        }

        [Test]
        public void TableHeaderCell_MatchesTheHeaderStripHeightAndDoesNotStretch()
        {
            ConvaiEditorStyles.EnsureStyles();

            Assert.AreEqual(
                ConvaiEditorTokens.TableHeaderHeight,
                ConvaiEditorStyles.TableHeaderCell.fixedHeight,
                "A cell taller than the strip overflows it and the title sits on the top edge.");
            Assert.IsFalse(
                ConvaiEditorStyles.TableHeaderCell.stretchWidth,
                "A stretching header column steals width from its neighbours and the title drifts off its cells.");
            Assert.AreEqual(TextAnchor.MiddleLeft, ConvaiEditorStyles.TableHeaderCell.alignment);
            Assert.AreEqual(TextAnchor.MiddleCenter, ConvaiEditorStyles.TableHeaderCellCentered.alignment);
            Assert.AreEqual(TextAnchor.MiddleRight, ConvaiEditorStyles.TableHeaderCellRight.alignment);
        }

        [Test]
        public void StyleCache_EnsureInitialized_ReusesStyleInstances()
        {
            ConvaiEditorStyles.EnsureStyles();
            GUIStyle firstHeader = ConvaiEditorStyles.SectionHeaderLabel;
            GUIStyle firstIcon = ConvaiEditorStyles.SectionIcon;
            GUIStyle firstChevron = ConvaiEditorStyles.SectionChevron;

            ConvaiEditorStyles.EnsureStyles();

            Assert.AreSame(firstHeader, ConvaiEditorStyles.SectionHeaderLabel);
            Assert.AreSame(firstIcon, ConvaiEditorStyles.SectionIcon);
            Assert.AreSame(firstChevron, ConvaiEditorStyles.SectionChevron);
        }

        /// <summary>
        ///     The in-field placeholder is what lets a repeated list row carry guidance without
        ///     carrying a repeated sentence, so it has to be muted enough to read as a hint and never
        ///     be mistaken for a real value the user typed.
        /// </summary>
        [Test]
        public void StyleCache_FieldPlaceholder_IsBuiltAndMutedAgainstBodyText()
        {
            ConvaiEditorStyles.EnsureStyles();
            GUIStyle placeholder = ConvaiEditorStyles.FieldPlaceholder;

            Assert.IsNotNull(placeholder);
            Assert.AreNotEqual(
                ConvaiEditorStyles.BodyWrapped.normal.textColor,
                placeholder.normal.textColor,
                "Placeholder text must be muted, not body text — it is not a value.");
            Assert.AreEqual(
                FontStyle.Italic,
                placeholder.fontStyle,
                "A placeholder sits where a value would; italic is what stops it reading as one.");

            ConvaiEditorStyles.EnsureStyles();
            Assert.AreSame(placeholder, ConvaiEditorStyles.FieldPlaceholder);
        }

        /// <summary>
        ///     The prose fields exist so a long description stops scrolling out of its own box, which
        ///     only holds if the style actually wraps.
        /// </summary>
        [Test]
        public void StyleCache_GrowingTextArea_WrapsAndIsCached()
        {
            ConvaiEditorStyles.EnsureStyles();
            GUIStyle area = ConvaiEditorStyles.GrowingTextArea;

            Assert.IsNotNull(area);
            Assert.IsTrue(area.wordWrap, "A prose field that does not wrap cannot grow to fit its text.");
            Assert.AreEqual(0f, area.fixedHeight, "A fixed height would defeat growing with the content.");

            ConvaiEditorStyles.EnsureStyles();
            Assert.AreSame(area, ConvaiEditorStyles.GrowingTextArea);
        }

        [Test]
        public void Icons_Emblem_ReturnsNonNullTexture()
        {
            Texture2D icon = ConvaiEditorIcons.Emblem();
            Assert.IsNotNull(icon);
        }

        [Test]
        public void ActionDebugPatchDraft_PreservesOmittedFieldsAndBuildsExplicitClears()
        {
            var draft = new ConvaiActionPatchDraft
            {
                IncludeObjects = true,
                IncludeNestedAttention = true,
                NestedAttention = string.Empty
            };

            ConvaiActionConfigPatch patch = draft.BuildActionConfigPatch();

            Assert.IsNull(patch.Actions);
            Assert.IsEmpty(patch.Objects);
            Assert.IsNull(patch.Characters);
            Assert.AreEqual(string.Empty, patch.CurrentAttentionObject);
            Assert.IsNull(draft.BuildTopLevelAttention());
        }

        [Test]
        public void ActionDebugPatchDraft_ParsesActionLinesAndKeepsTopLevelOverrideIndependent()
        {
            var draft = new ConvaiActionPatchDraft
            {
                IncludeActions = true,
                ActionsText = "  Move To  \n\nPick Up\r\n",
                IncludeTopLevelAttention = true,
                TopLevelAttention = "Cube"
            };

            ConvaiActionConfigPatch patch = draft.BuildActionConfigPatch();

            CollectionAssert.AreEqual(new[] { "Move To", "Pick Up" }, patch.Actions);
            Assert.IsNull(patch.CurrentAttentionObject);
            Assert.AreEqual("Cube", draft.BuildTopLevelAttention());
        }

        [Test]
        public void ActionDebugPatchDraft_LoadClonesConfirmedSnapshotAndBindings()
        {
            var cube = new GameObject("Cube");
            try
            {
                var config = new ConvaiActionConfig
                {
                    Actions = new() { "Move To" },
                    Objects = new()
                    {
                        new ConvaiActionObjectDefinition
                        {
                            Name = "Cube",
                            Description = "Target",
                            GameObjectReference = cube
                        }
                    },
                    CurrentAttentionObject = "Cube"
                };
                var draft = new ConvaiActionPatchDraft();

                draft.Load(config);
                ConvaiActionConfigPatch patch = draft.BuildActionConfigPatch();

                Assert.IsTrue(draft.IncludeActions);
                Assert.IsTrue(draft.IncludeObjects);
                Assert.IsTrue(draft.IncludeCharacters);
                Assert.IsTrue(draft.IncludeNestedAttention);
                Assert.IsFalse(draft.IncludeTopLevelAttention);
                Assert.AreEqual("Move To", patch.Actions[0]);
                Assert.AreNotSame(config.Objects[0], patch.Objects[0]);
                Assert.AreSame(cube, patch.Objects[0].GameObjectReference);
                Assert.AreEqual("Cube", patch.CurrentAttentionObject);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cube);
            }
        }

        /// <summary>
        ///     A tinted style must never write its colour onto the shared base style. These helpers
        ///     once did, and because nothing restored it, the last tint asked for anywhere in the
        ///     editor became the colour of every later untinted draw — the Actions Editor overview
        ///     reported healthy group counts in the error colour for the rest of the session.
        /// </summary>
        [Test]
        public void TintedStyles_DoNotRecolourTheSharedBaseStyle()
        {
            ConvaiEditorStyles.EnsureStyles();

            Color tileBase = ConvaiEditorStyles.TileNumber.normal.textColor;
            Color metricBase = ConvaiEditorStyles.MetricNumber.normal.textColor;
            Color microBase = ConvaiEditorStyles.MicroLabelRight.normal.textColor;
            Color readingBase = ConvaiEditorStyles.ReadingValue.normal.textColor;
            Color cellBase = ConvaiEditorStyles.TableCell.normal.textColor;
            Color cellCenteredBase = ConvaiEditorStyles.TableCellCentered.normal.textColor;
            Color liveBase = ConvaiEditorStyles.LiveCellValue.normal.textColor;
            FontStyle liveWeightBase = ConvaiEditorStyles.LiveCellValue.fontStyle;
            Color pillBase = ConvaiEditorStyles.PillLabel.normal.textColor;
            Color chipBase = ConvaiEditorStyles.ChipLabel.normal.textColor;
            Color messageIconBase = ConvaiEditorStyles.MessageIcon.normal.textColor;

            var loud = new Color(1f, 0f, 0.25f, 1f);
            ConvaiEditorStyles.TileNumberTinted(loud);
            ConvaiEditorStyles.MetricNumberTinted(loud);
            ConvaiEditorStyles.MicroLabelRightTinted(loud);
            ConvaiEditorStyles.ReadingValueTinted(loud);
            ConvaiEditorStyles.TableCellTinted(loud);
            ConvaiEditorStyles.TableCellTinted(loud, true);
            ConvaiEditorStyles.LiveCellValueTinted(loud, true);
            ConvaiEditorStyles.CenteredMini(loud);
            ConvaiEditorStyles.PillLabelTinted(loud);
            ConvaiEditorStyles.ChipLabelTinted(loud);
            ConvaiEditorStyles.MessageIconTinted(loud);

            Assert.AreEqual(tileBase, ConvaiEditorStyles.TileNumber.normal.textColor);
            Assert.AreEqual(metricBase, ConvaiEditorStyles.MetricNumber.normal.textColor);
            Assert.AreEqual(microBase, ConvaiEditorStyles.MicroLabelRight.normal.textColor);
            Assert.AreEqual(readingBase, ConvaiEditorStyles.ReadingValue.normal.textColor);
            Assert.AreEqual(cellBase, ConvaiEditorStyles.TableCell.normal.textColor);
            Assert.AreEqual(cellCenteredBase, ConvaiEditorStyles.TableCellCentered.normal.textColor);
            Assert.AreEqual(liveBase, ConvaiEditorStyles.LiveCellValue.normal.textColor);
            Assert.AreEqual(liveWeightBase, ConvaiEditorStyles.LiveCellValue.fontStyle);
            Assert.AreEqual(pillBase, ConvaiEditorStyles.PillLabel.normal.textColor);
            Assert.AreEqual(chipBase, ConvaiEditorStyles.ChipLabel.normal.textColor);
            Assert.AreEqual(messageIconBase, ConvaiEditorStyles.MessageIcon.normal.textColor);
        }

        /// <summary>
        ///     Two tints asked for in the same repaint must be two instances, or the second one
        ///     recolours the first before IMGUI has drawn it.
        /// </summary>
        [Test]
        public void TintedStyles_KeepOneInstancePerColour()
        {
            ConvaiEditorStyles.EnsureStyles();

            var red = new Color(1f, 0f, 0.25f, 1f);
            var green = new Color(0f, 0.85f, 0.4f, 1f);

            GUIStyle firstRed = ConvaiEditorStyles.ReadingValueTinted(red);
            GUIStyle greenStyle = ConvaiEditorStyles.ReadingValueTinted(green);

            Assert.AreSame(firstRed, ConvaiEditorStyles.ReadingValueTinted(red));
            Assert.AreNotSame(firstRed, greenStyle);
            Assert.AreEqual(red, firstRed.normal.textColor);
            Assert.AreEqual(green, greenStyle.normal.textColor);

            // Weight is part of a live cell's identity, so it may not share a pooled instance.
            GUIStyle bold = ConvaiEditorStyles.LiveCellValueTinted(red, true);
            GUIStyle regular = ConvaiEditorStyles.LiveCellValueTinted(red, false);
            Assert.AreNotSame(bold, regular);
            Assert.AreEqual(FontStyle.Bold, bold.fontStyle);
            Assert.AreEqual(FontStyle.Normal, regular.fontStyle);

            // Same for a table cell's alignment.
            Assert.AreNotSame(
                ConvaiEditorStyles.TableCellTinted(red),
                ConvaiEditorStyles.TableCellTinted(red, true));
        }
    }
}
#endif
