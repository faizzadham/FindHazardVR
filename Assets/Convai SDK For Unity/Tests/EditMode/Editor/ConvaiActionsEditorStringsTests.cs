using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the Convai Actions AAA plan §8 naming rule: every
    ///     <see cref="ConvaiActionsEditorStrings" /> label/tooltip pair is non-empty, no label
    ///     contains a forbidden word ("AI", "executor", "game" as whole words — API names and
    ///     "GameObject" are fine in tooltips, never in a label), and every
    ///     <see cref="ConvaiActionArchetypeCatalog" /> entry resolves a display name and description
    ///     for the window's "Add Action" catalog and empty-state starter cards.
    /// </summary>
    [TestFixture]
    public class ConvaiActionsEditorStringsTests
    {
        private static readonly Regex[] ForbiddenLabelWords =
        {
            new(@"\bAI\b", RegexOptions.IgnoreCase),
            new(@"\bexecutor\b", RegexOptions.IgnoreCase),
            new(@"\bgame\b", RegexOptions.IgnoreCase)
        };

        /// <summary>
        ///     Marks the contents that are drawn <em>inside</em> an empty field rather than beside
        ///     it. The example belongs where the reader's cursor already is, so these carry a label
        ///     and nothing else — see the placeholder pattern in the Scene Knowledge fields.
        /// </summary>
        private const string PlaceholderSuffix = "Placeholder";

        private static IEnumerable<FieldInfo> AllStaticContentFields()
        {
            FieldInfo[] fields = typeof(ConvaiActionsEditorStrings).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType != typeof(GUIContent))
                    continue;

                yield return field;
            }
        }

        private static IEnumerable<GUIContent> AllStaticContents()
        {
            foreach (FieldInfo field in AllStaticContentFields())
                yield return (GUIContent)field.GetValue(null);
        }

        /// <summary>
        ///     Every label is non-empty, and every content carries hover help — <em>except</em>
        ///     placeholders, which must carry none.
        /// </summary>
        /// <remarks>
        ///     A placeholder is painted into an empty field and disappears the moment the field has
        ///     a value, so Unity never has a rect to hover: a tooltip on one is text that can never
        ///     reach a reader, and demanding one is asking for text nobody will see. The rule is
        ///     therefore inverted rather than waived — a placeholder that grows a tooltip fails here
        ///     too, which is what stops the suffix from becoming a way to opt out of hover help.
        ///     The field the placeholder sits in still owes its own tooltip, and is checked for one
        ///     like everything else.
        /// </remarks>
        [Test]
        public void EveryStaticContent_HasNonEmptyLabelAndTooltip()
        {
            int count = 0;
            int placeholderCount = 0;
            foreach (FieldInfo field in AllStaticContentFields())
            {
                count++;
                var content = (GUIContent)field.GetValue(null);
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(content.text),
                    $"'{field.Name}' is a static GUIContent with an empty label.");

                if (field.Name.EndsWith(PlaceholderSuffix, System.StringComparison.Ordinal))
                {
                    placeholderCount++;
                    Assert.IsTrue(
                        string.IsNullOrEmpty(content.tooltip),
                        $"'{field.Name}' is a placeholder and carries a tooltip. A placeholder is drawn " +
                        "inside an empty field, so there is no rect to hover and the tooltip can never " +
                        "be read — put the guidance on the field's own label instead.");
                    continue;
                }

                Assert.IsFalse(string.IsNullOrWhiteSpace(content.tooltip), $"'{content.text}' has an empty tooltip.");
            }

            Assert.Greater(count, 0, "Expected at least one static GUIContent field on ConvaiActionsEditorStrings.");
            Assert.Greater(
                placeholderCount, 0,
                "Expected at least one placeholder content; if they were all renamed, this exemption no " +
                "longer describes anything and should go rather than sit here as a standing loophole.");
        }

        /// <summary>
        ///     The search box is the one field whose placeholder had hover help to lose: it sits in a
        ///     toolbar with no label beside it, so exempting the placeholder from
        ///     <see cref="EveryStaticContent_HasNonEmptyLabelAndTooltip" /> would have quietly deleted
        ///     the only sentence saying that descriptions are searched too. The exemption is only
        ///     honest while that sentence still exists on the field itself, which is what this checks.
        /// </summary>
        [Test]
        public void SearchField_KeepsItsHoverHelp_OnTheFieldRatherThanThePlaceholder()
        {
            Assert.IsTrue(
                string.IsNullOrEmpty(ConvaiActionsEditorStrings.SearchPlaceholder.tooltip),
                "The search placeholder is drawn inside the empty box and must carry a label only.");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(ConvaiActionsEditorStrings.SearchFieldHelp),
                "The search field itself must still say what it filters on hover.");
        }

        [Test]
        public void CoreComponentTitles_UseJobBasedDisplayNames()
        {
            Assert.That(ConvaiActionsEditorStrings.InspectorStatusCardTitle.text, Is.EqualTo("Actions"));
            Assert.That(ConvaiActionsEditorStrings.DispatcherTitle.text, Is.EqualTo("Action Runner"));
            Assert.That(ConvaiActionsEditorStrings.SettingsAddDispatcherButton.text, Is.EqualTo("Add Action Runner"));
        }

        [Test]
        public void NoStaticLabel_ContainsForbiddenWords()
        {
            foreach (GUIContent content in AllStaticContents())
                AssertLabelHasNoForbiddenWords(content);
        }

        // The vocabulary guardrail: the words "AI" and "executor" may not appear standalone
        // ANYWHERE in the string table — labels, tooltips, raw string members, or dynamic builder
        // output. Compound API identifiers ("IConvaiActionExecutor", "MoveToActionExecutor") are
        // inherently permitted because \b-delimited matching requires the word to stand alone;
        // inspection of the table shows tooltips only ever mention the API name inside such
        // compounds, so no member-name-based exemption exists (or is allowed to creep in).
        private static readonly Regex[] EverywhereBannedWords =
        {
            new(@"\bAI\b", RegexOptions.IgnoreCase),
            new(@"\bexecutor\b", RegexOptions.IgnoreCase)
        };

        [Test]
        public void NoStringTableText_AnySurface_ContainsStandaloneAiOrExecutor()
        {
            var checkedTexts = new List<string>();

            FieldInfo[] fields = typeof(ConvaiActionsEditorStrings).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                if (field.FieldType == typeof(GUIContent))
                {
                    var content = (GUIContent)field.GetValue(null);
                    checkedTexts.Add(content.text);
                    checkedTexts.Add(content.tooltip);
                }
                else if (field.FieldType == typeof(string))
                {
                    checkedTexts.Add((string)field.GetValue(null));
                }
                else if (field.FieldType == typeof(string[]))
                {
                    checkedTexts.AddRange((string[])field.GetValue(null));
                }
            }

            foreach (GUIContent content in BuildAllDynamicContents())
            {
                checkedTexts.Add(content.text);
                checkedTexts.Add(content.tooltip);
            }

            Assert.Greater(checkedTexts.Count, 0);
            foreach (string text in checkedTexts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;

                foreach (Regex banned in EverywhereBannedWords)
                {
                    Assert.IsFalse(
                        banned.IsMatch(text),
                        $"String table text contains a standalone banned word matching '{banned}': \"{text}\". " +
                        "Say 'Convai Character' instead of 'AI' and 'Action Behavior' instead of 'executor'; " +
                        "API names are only allowed as full compound identifiers (e.g. IConvaiActionExecutor).");
                }
            }
        }

        [Test]
        public void DynamicContentBuilders_ProduceNonEmptyLabelAndTooltip()
        {
            foreach (GUIContent content in BuildAllDynamicContents())
            {
                Assert.IsFalse(string.IsNullOrWhiteSpace(content.text));
                Assert.IsFalse(string.IsNullOrWhiteSpace(content.tooltip));
            }
        }

        [Test]
        public void DynamicContentBuilders_LabelsContainNoForbiddenWords()
        {
            foreach (GUIContent content in BuildAllDynamicContents())
                AssertLabelHasNoForbiddenWords(content);
        }

        /// <summary>
        ///     D15 guard: a raw C# component/type name (e.g. "ConvaiActionConfigSource") must never
        ///     reach a user, which is exactly how the Action Troubleshooter's old copy read
        ///     ("No ConvaiActionConfigSource component.") before this rule existed. Scoped to
        ///     members whose field name starts with "Troubleshooter" rather than the whole class:
        ///     several pre-existing tooltips elsewhere in this table (for example
        ///     <see cref="ConvaiActionsEditorStrings.SetActionBehaviorLabel" />) legitimately cite a
        ///     compound API identifier such as "IConvaiActionExecutor" as an engineer hint — a
        ///     deliberate, documented house convention (see the class doc comment) that this guard
        ///     does not touch or weaken. The Troubleshooter's own members never need that exemption:
        ///     every resolved component type flows through <c>ConvaiComponentTypeResolver.DisplayName</c>
        ///     or the archetype catalog's <c>DisplayName</c> first, so nothing under this prefix has a
        ///     reason to name a class directly.
        /// </summary>
        [Test]
        public void TroubleshooterStaticText_ContainsNoConvaiPrefixedClassName()
        {
            var classNamePattern = new Regex(@"Convai[A-Z][A-Za-z]*");
            var checkedTexts = new List<string>();

            FieldInfo[] fields = typeof(ConvaiActionsEditorStrings).GetFields(
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            foreach (FieldInfo field in fields)
            {
                if (!field.Name.StartsWith("Troubleshooter"))
                    continue;

                if (field.FieldType == typeof(GUIContent))
                {
                    var content = (GUIContent)field.GetValue(null);
                    checkedTexts.Add(content.text);
                    checkedTexts.Add(content.tooltip);
                }
                else if (field.FieldType == typeof(string))
                {
                    checkedTexts.Add((string)field.GetValue(null));
                }
            }

            Assert.Greater(checkedTexts.Count, 0, "Expected at least one Troubleshooter-prefixed static text member.");
            foreach (string text in checkedTexts)
            {
                if (string.IsNullOrEmpty(text))
                    continue;

                Assert.IsFalse(
                    classNamePattern.IsMatch(text),
                    $"Troubleshooter text names a C# class directly: \"{text}\". Resolve a display name " +
                    "through ConvaiComponentTypeResolver.DisplayName (or the archetype catalog's " +
                    "DisplayName) instead of surfacing the raw type.");
            }
        }

        [Test]
        public void EveryCatalogEntry_ResolvesDisplayNameAndDescription()
        {
            IReadOnlyList<ConvaiActionArchetypeCatalogEntry> entries = ConvaiActionArchetypeCatalog.Entries;
            Assert.Greater(entries.Count, 0, "Expected at least one archetype-attributed IConvaiActionExecutor to be discovered.");

            foreach (ConvaiActionArchetypeCatalogEntry entry in entries)
            {
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(entry.DisplayName),
                    $"Executor type '{entry.ExecutorType}' resolved no display name.");

                ConvaiActionDefinition definition = entry.BuildDefinition();
                Assert.IsNotNull(definition, $"Archetype '{entry.DisplayName}' failed to build a definition.");
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(definition.Description),
                    $"Archetype '{entry.DisplayName}' resolved no description.");
                Assert.IsFalse(
                    string.IsNullOrWhiteSpace(definition.ActionName),
                    $"Archetype '{entry.DisplayName}' resolved no action name.");
            }
        }

        [Test]
        public void EmptyStateStarters_TeachTheCoreBeginnerJourneyInProductOrder()
        {
            List<ConvaiActionArchetypeCatalogEntry> starters =
                ConvaiActionArchetypeCatalog.FeaturedEntries(4);
            string[] names = starters.Select(entry => entry.DisplayName).ToArray();

            Assert.That(names, Is.EqualTo(new[]
            {
                "Walk To Target",
                "Follow The Player",
                "Look At Target",
                "Play Gesture"
            }));

            foreach (ConvaiActionArchetypeCatalogEntry starter in starters)
            {
                Assert.That(starter.FeaturedDescription, Is.Not.Empty,
                    $"Starter '{starter.DisplayName}' must explain itself on its card.");
                Assert.That(starter.FeaturedDescription.Length, Is.LessThanOrEqualTo(70),
                    $"Starter '{starter.DisplayName}' exceeds the compact card-copy budget.");
            }
        }

        [Test]
        public void MissingTargetCopy_ExplainsTheImpactWithoutInternalVocabulary()
        {
            string message = ConvaiActionsEditorStrings.BuildMissingTargetMessage(
                "Look At", "GameObject", ConvaiActionTargetRequirement.Either);

            Assert.That(ConvaiActionsEditorStrings.BuildMissingTargetTitle("Look At"),
                Is.EqualTo("Add a target for \"Look At\""));
            Assert.That(message, Does.Contain("object or another character"));
            Assert.That(message, Does.Contain("Scene Knowledge"));
            Assert.That(message, Does.Contain("only this action is unavailable"));
            Assert.That(message, Does.Not.Contain("actionable"));
            Assert.That(message, Does.Not.Contain("targets are named"));
        }

        [Test]
        public void ReadyMadeParameters_AreReferenceQualityAndExplainTheirValues()
        {
            foreach (ConvaiActionArchetypeCatalogEntry entry in ConvaiActionArchetypeCatalog.Entries
                         .Where(entry => entry.Origin == ConvaiActionArchetypeOrigin.BuiltIn))
            {
                ConvaiActionDefinition definition = entry.BuildDefinition();
                foreach (ConvaiActionParameterDefinition parameter in definition.Parameters)
                {
                    Assert.That(parameter.Description, Is.Not.Null.And.Not.Empty,
                        $"Ready-made action '{definition.ActionName}' must explain parameter " +
                        $"'{parameter.Name}' because new SDK users will copy this definition.");
                }
            }
        }

        [Test]
        public void CoreStarterDefinitions_ExposeIntentNotTechnicalTuning()
        {
            ConvaiActionDefinition walk = DefinitionFor("Walk To Target");
            Assert.That(walk.Parameters, Is.Empty);
            Assert.That(walk.TargetRequirement, Is.EqualTo(ConvaiActionTargetRequirement.Either));
            Assert.That(walk.TimeoutSeconds, Is.EqualTo(45f));
            Assert.That(walk.Description, Does.Contain("named place, object, or person"));
            Assert.That(walk.Description, Does.Not.Contain("Lead"));

            ConvaiActionDefinition follow = DefinitionFor("Follow The Player");
            Assert.That(follow.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "mode" }));
            Assert.That(follow.Parameters[0].Choices, Is.EqualTo(new[] { "follow", "stop" }));
            Assert.That(follow.Description, Does.Contain("player chooses the route").IgnoreCase);
            Assert.That(follow.Description, Does.Not.Contain("Lead"));

            ConvaiActionDefinition look = DefinitionFor("Look At Target");
            Assert.That(look.Parameters, Is.Empty);
            Assert.That(look.TimeoutSeconds, Is.EqualTo(10f));
            Assert.That(look.FailurePolicyOverride,
                Is.EqualTo(ConvaiActionFailurePolicyOverride.ContinueBatch));

            ConvaiActionDefinition gesture = DefinitionFor("Play Gesture");
            Assert.That(gesture.Parameters.Select(parameter => parameter.Name), Is.EqualTo(new[] { "gesture" }));
            Assert.That(gesture.Parameters[0].Type, Is.EqualTo(ConvaiActionParameterType.String));
            Assert.That(gesture.Parameters[0].Description, Does.Contain("Animation Set"));
            Assert.That(gesture.Description, Does.Contain("assigned Animation Set"));
            Assert.That(gesture.Description, Does.Not.Match("(?i)wave|nod|shrug|bow"),
                "Play Gesture must not promise gesture names because each assigned Animation Set differs.");
            Assert.That(gesture.TimeoutSeconds, Is.EqualTo(15f));
        }

        [Test]
        public void GuidedMovementAndObservations_ShipWithPurposeBuiltDefaults()
        {
            ConvaiActionDefinition lead = DefinitionFor("Lead Player To Target");
            Assert.That(lead.Parameters, Is.Empty);
            Assert.That(lead.TimeoutSeconds, Is.EqualTo(120f));
            Assert.That(lead.Description, Does.Contain("pausing when the player falls behind"));

            Assert.That(DefinitionFor("Count Target Group").AnswerDelivery,
                Is.EqualTo(ConvaiActionAnswerDelivery.TellThePlayer));
            Assert.That(DefinitionFor("Measure Distance").AnswerDelivery,
                Is.EqualTo(ConvaiActionAnswerDelivery.TellThePlayer));
        }

        [Test]
        public void AddActionMenu_IsCuratedThenAlphabeticalAndKeepsLocalContentOutOfTheMainList()
        {
            List<ConvaiActionArchetypeMenuItem> items = ConvaiActionArchetypeCatalog.BuildMenuItems();
            Assert.That(items.Select(item => item.Entry).Distinct().Count(), Is.EqualTo(items.Count));
            Assert.That(items.Count, Is.EqualTo(ConvaiActionArchetypeCatalog.Entries.Count));

            List<ConvaiActionArchetypeMenuItem> recommended = items
                .TakeWhile(item => !item.StartsSection ||
                                   item.SectionHeader == ConvaiActionsEditorStrings.RecommendedActionsMenuSection)
                .ToList();
            Assert.That(recommended.Select(item => item.MenuPath), Is.EqualTo(new[]
            {
                "Walk To Target",
                "Follow The Player",
                "Look At Target",
                "Play Gesture",
                "Point At Target",
                "Turn To Face Target",
                "Show Or Hide Object"
            }));

            List<ConvaiActionArchetypeMenuItem> readyMade = items
                .SkipWhile(item => item.SectionHeader != ConvaiActionsEditorStrings.ReadyMadeActionsMenuSection)
                .TakeWhile((item, index) => index == 0 || !item.StartsSection)
                .ToList();
            string[] readyMadeNames = readyMade.Select(item => item.MenuPath).ToArray();
            Assert.That(readyMadeNames, Is.Ordered.Using<string>(System.StringComparer.OrdinalIgnoreCase));
            Assert.That(readyMade.All(
                item => item.Entry.Origin == ConvaiActionArchetypeOrigin.BuiltIn && !item.MenuPath.Contains('/')),
                Is.True);

            Assert.That(items.Where(item => item.Entry.Origin == ConvaiActionArchetypeOrigin.Sample).All(
                item => item.MenuPath.StartsWith(ConvaiActionsEditorStrings.SampleActionsMenu + "/")), Is.True);
            Assert.That(items.Where(item => item.Entry.Origin == ConvaiActionArchetypeOrigin.ProjectOrPackage).All(
                item => item.MenuPath.StartsWith(ConvaiActionsEditorStrings.ProjectActionsMenu + "/")), Is.True);
        }

        private static IEnumerable<GUIContent> BuildAllDynamicContents()
        {
            yield return ConvaiActionsEditorStrings.BuildTroubleshooterChipIssues(3);
            yield return ConvaiActionsEditorStrings.BuildStatusStripIssues(2, 1);
            yield return ConvaiActionsEditorStrings.BuildStatusStripIssues(0, 1);
            yield return ConvaiActionsEditorStrings.BuildCommandPreviewValue("Move To {target}");
            yield return ConvaiActionsEditorStrings.BuildCommandPreviewValue(string.Empty);
            yield return ConvaiActionsEditorStrings.BuildBehaviorBoundStatus("Move To", "MoveToActionExecutor", "NPC");
            yield return ConvaiActionsEditorStrings.BuildAddAndBindButton("Move To");
            yield return ConvaiActionsEditorStrings.BuildInspectorMoreRow(4);
            yield return ConvaiActionsEditorStrings.BuildInspectorSummary(3, 1, 0, 0);
            yield return ConvaiActionsEditorStrings.BuildInspectorSummary(3, 1, 2, 1);
            yield return ConvaiActionsEditorStrings.BuildActionRowLabel("Move To");
            yield return ConvaiActionsEditorStrings.BuildMultiSelectionTitle(3);
            yield return ConvaiActionsEditorStrings.BuildTroubleshooterFixAllSummary(1);
            yield return ConvaiActionsEditorStrings.BuildTroubleshooterFixAllSummary(2);
            yield return ConvaiActionsEditorStrings.BuildInitialAttentionUnknown("Lantern");
            yield return ConvaiActionsEditorStrings.BuildInitialAttentionChoice("Lantern");
            yield return ConvaiActionsEditorStrings.BuildKnownEntryAnsweredStatus("Lantern");
            yield return ConvaiActionsEditorStrings.BuildKnownEntryLinkSuggestion("Lantern");
            yield return ConvaiActionsEditorStrings.BuildKnownEntryNoMatchFound("Lantern");
            yield return ConvaiActionsEditorStrings.BuildKnownEntryManyMatchesFound(3);
            yield return ConvaiActionsEditorStrings.BuildGroupAttentionPill(2);
            yield return ConvaiActionsEditorStrings.BuildCategoryMenuItem("Counter");
            yield return ConvaiActionsEditorStrings.BuildCategoryGroupLabel("Counter");
            yield return ConvaiActionsEditorStrings.BuildCategoryNearDuplicateWarning("Tour");
            yield return ConvaiActionsEditorStrings.BuildCategoryRenameSummary("Tour", 1);
            yield return ConvaiActionsEditorStrings.BuildCategoryRenameSummary("Tour", 6);
            yield return ConvaiActionsEditorStrings.BuildCategoryRemoveSummary("Tour", 1);
            yield return ConvaiActionsEditorStrings.BuildCategoryRemoveSummary("Tour", 6);
            yield return ConvaiActionsEditorStrings.BuildCategorySharedNote(1);
            yield return ConvaiActionsEditorStrings.BuildCategorySharedNote(3);
            yield return ConvaiActionsEditorStrings.BuildBehaviorHostRowName("Action Behaviors");
            yield return ConvaiActionsEditorStrings.BuildBehaviorHostRemainingPill(2);
            yield return ConvaiActionsEditorStrings.BuildBehaviorHostOffer(7);
        }

        private static ConvaiActionDefinition DefinitionFor(string displayName) =>
            ConvaiActionArchetypeCatalog.Entries.Single(entry => entry.DisplayName == displayName).BuildDefinition();

        private static void AssertLabelHasNoForbiddenWords(GUIContent content)
        {
            foreach (Regex forbidden in ForbiddenLabelWords)
            {
                Assert.IsFalse(
                    forbidden.IsMatch(content.text),
                    $"Label '{content.text}' contains a forbidden word matching '{forbidden}'. " +
                    "Naming rule (Convai Actions AAA plan §8): no 'AI'/'executor'/'game' in labels " +
                    "(those words are only allowed inside tooltips, as API-name hints for engineers).");
            }
        }
    }
}
