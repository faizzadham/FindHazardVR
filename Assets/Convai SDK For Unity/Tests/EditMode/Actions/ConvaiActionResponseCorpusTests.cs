using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     The one table of what a Convai Character actually sends and what the SDK must make of it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why one table.</b> The repairs that turn model output into a usable command
    ///         accumulated one at a time, each added because something did not work, and no single
    ///         place said what the whole set was for. So nobody could answer the only question that
    ///         matters about them — <em>which of these still earn their place?</em> — and the open
    ///         question of whether to change the rendered wire form rests on exactly that answer. If
    ///         the rendered form changes so the backend's own splitter starts matching, several of
    ///         these repairs become dead code, and this table is how that is measured rather than
    ///         guessed.
    ///     </para>
    ///     <para>
    ///         <b>Every row declares which repair it depends on</b>, and
    ///         <see cref="EveryRepairStepIsExercisedByTheCorpus" /> fails when a step has no row. So a
    ///         repair cannot quietly become unreachable, and one that is deleted takes its rows with
    ///         it visibly.
    ///     </para>
    ///     <para>
    ///         <b>The negative rows are the point.</b> Every repair is a guess that some punctuation
    ///         is decoration, and the guesses are good — which is exactly why nobody noticed them
    ///         destroying real names. A room called <c>Bay 2: North</c>, a prop called <c>- Special</c>,
    ///         a bay called <c>{Annex}</c>: these are the rows that prove R-3 rather than assert it.
    ///     </para>
    ///     <para>
    ///         Rows marked <see cref="Provenance.Observed" /> were produced by a real character
    ///         against the live backend in the Terminal scene. They are not invented examples and
    ///         should not be edited to make a change pass.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionResponseCorpusTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = 0; i < _spawned.Count; i++)
            {
                if (_spawned[i] != null)
                    UnityEngine.Object.DestroyImmediate(_spawned[i]);
            }

            _spawned.Clear();
        }

        /// <summary>Where a row came from, because invented examples and measured ones differ in weight.</summary>
        private enum Provenance
        {
            /// <summary>Produced by a real character against the live backend.</summary>
            Observed,

            /// <summary>Not seen, but the same shape as something that was.</summary>
            Plausible,

            /// <summary>Text that must survive untouched. The rows that bound the repairs.</summary>
            MustSurvive
        }

        /// <summary>
        ///     The repair steps, named so a row can say which one it needs.
        /// </summary>
        /// <remarks>
        ///     This list is the inventory a decision to change the rendered wire form would consume.
        ///     Deleting a repair means deleting its entry here, which means deleting its rows, which
        ///     is a visible edit in a review rather than dead code nobody notices.
        /// </remarks>
        private enum Repair
        {
            /// <summary>No repair needed — the text arrived usable.</summary>
            None,

            /// <summary>A leading <c>- </c> / <c>: </c> echoed off the "Name - description" template.</summary>
            Separator,

            /// <summary>Quotes wrapping the whole value.</summary>
            QuotePair,

            /// <summary>Braces wrapping a filled-in template slot.</summary>
            SlotBraces,

            /// <summary>The slot's own name left in front of the value it was filled with.</summary>
            SlotLabel,

            /// <summary>The whole rendered template echoed back as the command name.</summary>
            TemplatePrefix,

            /// <summary>The action's name in front of the target, which is how the backend sends it.</summary>
            ActionPrefix,

            /// <summary>A parameter's name copied into its own value.</summary>
            ParamNameMimicry,

            /// <summary>Several values arriving as a run of <c>{…}</c> groups.</summary>
            SplitBraceWrapped,

            /// <summary>Several values arriving as <c>name: value</c> pairs.</summary>
            SplitNamedAnchors,

            /// <summary>Several values separated by the authored connector words.</summary>
            SplitByConnectors,

            /// <summary>Several values arriving individually quoted.</summary>
            SplitQuoted,

            /// <summary>
            ///     The last resort: one value per whitespace-separated word.
            /// </summary>
            /// <remarks>
            ///     The one repair R-3 cannot bound. Splitting on whitespace has to guess where a
            ///     value ends, so with two or more slots a multi-word name is unrecoverable — there
            ///     is no candidate substring to offer the vocabulary. Listed rather than omitted
            ///     precisely so the limit is visible in the accounting.
            /// </remarks>
            SplitWhitespace
        }

        private readonly struct Row
        {
            internal string Input { get; }
            internal string Expected { get; }
            internal Repair[] DependsOn { get; }
            internal Provenance From { get; }
            internal string Why { get; }

            internal Row(string input, string expected, Provenance from, string why, params Repair[] dependsOn)
            {
                Input = input;
                Expected = expected;
                From = from;
                Why = why;
                DependsOn = dependsOn.Length == 0 ? new[] { Repair.None } : dependsOn;
            }
        }

        // ── The vocabulary every row is read against ─────────────────────────────────────

        private ConvaiActionConfig Vocabulary()
        {
            var config = new ConvaiActionConfig
            {
                Objects = new List<ConvaiActionObjectDefinition>(),
                Characters = new List<ConvaiActionCharacterDefinition>()
            };

            foreach (string name in new[]
                     {
                         "The Gallery", "Observation Console", "Assembly Bay", "The Rooms",
                         "Power Generator",
                         // The names that look like syntax. Each one is a repair's worst case.
                         "Bay 2: North", "- Special", "{Annex}", "Q", "X-Ray", "Bay-2"
                     })
            {
                var go = new GameObject("corpus-" + name);
                _spawned.Add(go);
                config.Objects.Add(new ConvaiActionObjectDefinition
                {
                    Name = name,
                    GameObjectReference = go,
                    Available = true
                });
            }

            return config;
        }

        // ── Value rows: what Clean must make of a value, and what it must leave alone ────

        private static IReadOnlyList<Row> ValueRows() => new[]
        {
            new Row("The Gallery", "The Gallery", Provenance.Observed,
                "The ordinary case. Most values need nothing.", Repair.None),

            new Row("- The Gallery", "The Gallery", Provenance.Observed,
                "Actions are shown as 'Name - description', and a model completes the pattern it is "
                + "shown.", Repair.Separator),

            new Row("'The Gallery'", "The Gallery", Provenance.Observed,
                "Backends quote values and models quote them again.", Repair.QuotePair),

            new Row("{target: The Gallery}", "The Gallery", Provenance.Observed,
                "Shown '{target: reference}', a model fills the slot in place rather than replacing "
                + "it.", Repair.SlotBraces, Repair.SlotLabel),

            new Row("{target: \"The Rooms\"}", "The Rooms", Provenance.Observed,
                "Observed verbatim from the live backend: braces, label and quotes at once.",
                Repair.SlotBraces, Repair.SlotLabel, Repair.QuotePair),

            new Row("- {target: \"The Gallery\"}", "The Gallery", Provenance.Plausible,
                "A model emits every decoration at once as readily as any one of them, which is why "
                + "the repairs alternate rather than run in a fixed order.",
                Repair.Separator, Repair.SlotBraces, Repair.SlotLabel, Repair.QuotePair),

            // ── The rows that bound the repairs ──────────────────────────────────────────

            new Row("Bay 2: North", "Bay 2: North", Provenance.MustSurvive,
                "A real room. Its first word is followed by a space, not the colon, so it is a value "
                + "and not a slot label — and the vocabulary agrees.", Repair.None),

            new Row("- Special", "- Special", Provenance.MustSurvive,
                "A real prop whose name begins with the separator. Only the vocabulary can tell this "
                + "from an echo, which is the whole of R-3.", Repair.None),

            new Row("{Annex}", "{Annex}", Provenance.MustSurvive,
                "A real bay whose name is braced. The braces are 'always syntax' right up until they "
                + "are somebody's name.", Repair.None),

            new Row("Q", "Q", Provenance.MustSurvive,
                "A one-character name survives the quote strip, which needs a matching pair.",
                Repair.None),

            new Row("X-Ray", "X-Ray", Provenance.MustSurvive,
                "No whitespace after the hyphen, so the separator strip never considers it — this one "
                + "is safe without consulting the vocabulary at all.", Repair.None),

            new Row("Bay-2", "Bay-2", Provenance.MustSurvive,
                "As above. Both are kept as rows because they are the cheap half of the guarantee.",
                Repair.None),

            new Row("3:30", "3:30", Provenance.MustSurvive,
                "A time in a note. Nothing in the vocabulary protects it — it survives because the "
                + "slot-label drop only runs after braces came off, and no braces did.", Repair.None),

            new Row("https://example.com/a-b", "https://example.com/a-b", Provenance.MustSurvive,
                "A URL carries a colon straight after an unspaced word, which is exactly the shape "
                + "the slot-label drop looks for. It survives for the same reason 3:30 does.",
                Repair.None)
        };

        /// <summary>
        ///     Every value row, read the way a command's value is read.
        /// </summary>
        [Test]
        public void EveryValueRowReadsAsTheCorpusSays()
        {
            ConvaiActionConfig vocabulary = Vocabulary();
            var failures = new StringBuilder();

            foreach (Row row in ValueRows())
            {
                string actual = ConvaiActionWireText.Clean(row.Input, vocabulary);
                if (string.Equals(actual, row.Expected, StringComparison.Ordinal))
                    continue;

                failures.AppendLine(
                    $"  \"{row.Input}\"  ->  \"{actual}\"   (expected \"{row.Expected}\")");
                failures.AppendLine($"      {row.From}: {row.Why}");
            }

            Assert.That(failures.ToString(), Is.Empty,
                "Corpus rows did not read as recorded:\n" + failures);
        }

        /// <summary>
        ///     The <see cref="Provenance.MustSurvive" /> rows survive byte for byte, with the
        ///     vocabulary and without it where that is claimed.
        /// </summary>
        /// <remarks>
        ///     Split out from the row walk above so a regression here reads as what it is: the SDK
        ///     rewriting a name that belongs to something real. That is the failure mode R-3 exists
        ///     for and it deserves its own red line rather than being one entry in a list.
        /// </remarks>
        [Test]
        public void NothingThatNamesSomethingRealIsEverRewritten()
        {
            ConvaiActionConfig vocabulary = Vocabulary();

            foreach (Row row in ValueRows().Where(r => r.From == Provenance.MustSurvive))
            {
                Assert.That(
                    ConvaiActionWireText.Clean(row.Input, vocabulary),
                    Is.EqualTo(row.Expected),
                    $"'{row.Input}' was rewritten. {row.Why}");
            }
        }

        /// <summary>
        ///     Which repairs the vocabulary is doing the work for, stated rather than assumed.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>Most of the must-survive rows are not saved by the vocabulary at all</b>, and
        ///         saying so is the point of splitting this out. <c>X-Ray</c>, <c>Bay-2</c>,
        ///         <c>3:30</c>, the URL and even <c>Bay 2: North</c> never match a repair's shape in
        ///         the first place — the separator strip needs whitespace after the hyphen, and the
        ///         slot-label drop needs braces to have come off first. They would survive if R-3 did
        ///         not exist.
        ///     </para>
        ///     <para>
        ///         Only the rows below genuinely depend on it, and they are the measured value of
        ///         R-3. An earlier version of this test listed <c>Bay 2: North</c> among them with an
        ///         expected unrepaired value identical to its repaired one — which asserted nothing
        ///         while reading as though it asserted the headline claim. That is the shape of a
        ///         test that accompanies a claim instead of covering it, and it is worth a comment
        ///         because this suite has produced one before.
        ///     </para>
        /// </remarks>
        [Test]
        public void TheVocabularyIsWhatSavesTheNamesThatLookLikeSyntax()
        {
            (string Input, string WithoutVocabulary)[] savedOnlyByTheVocabulary =
            {
                ("- Special", "Special"),
                ("{Annex}", "Annex")
            };

            foreach ((string input, string withoutVocabulary) in savedOnlyByTheVocabulary)
            {
                Assert.That(
                    ConvaiActionWireText.CleanWithoutVocabulary(input),
                    Is.EqualTo(withoutVocabulary),
                    $"'{input}' read without a vocabulary should be '{withoutVocabulary}'. If this "
                    + "changed, the corpus's account of what R-3 is worth is out of date.");

                Assert.That(
                    ConvaiActionWireText.Clean(input, Vocabulary()),
                    Is.EqualTo(input),
                    $"'{input}' names something real, so it must survive.");
            }
        }

        // ── Command rows: the whole read, name and target together ───────────────────────

        /// <summary>
        ///     The shapes the live backend actually produced, read end to end.
        /// </summary>
        /// <remarks>
        ///     These four came out of one conversation in the Terminal scene. They are the reason the
        ///     name-side strippers exist: the backend's own splitter never matches a template this SDK
        ///     renders, so the target arrives glued inside the name on every single command.
        /// </remarks>
        private static List<ConvaiActionDefinition> CommandDefinitions() => new()
        {
            new()
            {
                ActionName = "Look At",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Description = "Look at something."
            },
            new()
            {
                ActionName = "Count In Group",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            },
            new()
            {
                ActionName = "Measure Distance",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            },
            new()
            {
                ActionName = "Walk To",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Description = "Walk over to a place."
            },
            // Two declared slots, so every multi-value split strategy is reachable and the
            // single-slot vocabulary short-circuit is not.
            new()
            {
                ActionName = "Compare Reading",
                TargetRequirement = ConvaiActionTargetRequirement.Object,
                Parameters = new List<ConvaiActionParameterDefinition>
                {
                    new() { Name = "low", Type = ConvaiActionParameterType.Number },
                    new() { Name = "high", Type = ConvaiActionParameterType.Number, Connector = "and" }
                }
            }
        };

        /// <summary>
        ///     Whole commands, as the backend delivers them: action and target glued together.
        /// </summary>
        /// <remarks>
        ///     These reach the name-side strippers and the split strategies, which no value row can —
        ///     a value is only read once the name has been taken off the front of it.
        /// </remarks>
        private static IReadOnlyList<Row> CommandRows() => new[]
        {
            new Row("Look At Observation Console", "Observation Console", Provenance.Observed,
                "The backend's own splitter never matches a template this SDK renders, so the target "
                + "arrives glued inside the name on every single command.", Repair.ActionPrefix),

            new Row("Count In Group {target: \"The Rooms\"}", "The Rooms", Provenance.Observed,
                "Observed verbatim: name prefix, braces, label and quotes at once.",
                Repair.ActionPrefix, Repair.SlotBraces, Repair.SlotLabel, Repair.QuotePair),

            new Row("Measure Distance {target: Assembly Bay}", "Assembly Bay", Provenance.Observed,
                "As above without the quotes.",
                Repair.ActionPrefix, Repair.SlotBraces, Repair.SlotLabel),

            new Row("Walk To - The Gallery", "The Gallery", Provenance.Observed,
                "The separator echoed off the 'Name - description' the model was shown.",
                Repair.ActionPrefix, Repair.Separator),

            new Row("Walk To {destination: The Gallery} - Walk over to a place.", "The Gallery",
                Provenance.Plausible,
                "The whole rendered template echoed back, description and all.",
                Repair.TemplatePrefix, Repair.ActionPrefix, Repair.SlotBraces, Repair.SlotLabel),

            new Row("Compare Reading {low: 20} {high: 80} {target: Power Generator}", "Power Generator",
                Provenance.Observed,
                "Every slot braced and labelled — the shape that exposed the implicit target slot, "
                + "observed verbatim from the live backend.",
                Repair.SplitBraceWrapped, Repair.SlotLabel),

            new Row("Compare Reading {20} {80} Power Generator", "Power Generator",
                Provenance.Plausible,
                "Only the declared slots braced, with the target left bare. The brace splitter finds "
                + "two groups for three slots, so it must hand over rather than pad the target away.",
                Repair.SplitBraceWrapped, Repair.SplitWhitespace),

            new Row("Compare Reading low: 20 high: 80 Power Generator", "Power Generator",
                Provenance.Plausible,
                "The same two slots as named anchors.", Repair.SplitNamedAnchors),

            new Row("Compare Reading 20 and 80 Power Generator", "Power Generator",
                Provenance.Plausible,
                "The same two slots separated by the authored connector.", Repair.SplitByConnectors),

            new Row("Compare Reading '20' '80' Power Generator", "Power Generator",
                Provenance.Plausible,
                "The same two slots individually quoted.", Repair.SplitQuoted),

            new Row("Compare Reading 20 80 Power Generator", "Power Generator",
                Provenance.Plausible,
                "Nothing to split on but spaces — the last resort, and the one repair the vocabulary "
                + "cannot bound.", Repair.SplitWhitespace),

            new Row("Compare Reading [number] 20 [number] 80 Power Generator", "Power Generator",
                Provenance.Plausible,
                "The template's own type words copied into the values.", Repair.ParamNameMimicry)
        };

        /// <summary>
        ///     Every command row recovers the target it names.
        /// </summary>
        /// <remarks>
        ///     The <c>Expected</c> column here is the resolved target's name rather than a cleaned
        ///     string, because a command is only correct if it reaches the right thing — cleaning it
        ///     tidily and resolving to nothing is the failure this whole area is about.
        /// </remarks>
        [Test]
        public void EveryCommandRowRecoversItsTarget()
        {
            ConvaiActionConfig vocabulary = Vocabulary();
            List<ConvaiActionDefinition> definitions = CommandDefinitions();
            var failures = new StringBuilder();

            foreach (Row row in CommandRows())
            {
                ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                    new ConvaiActionCommand(row.Input), vocabulary, definitions);

                ConvaiActionDefinition definition = ConvaiActionResponseParser.FindTemplate(
                    enriched.Name, definitions);

                if (definition == null)
                {
                    failures.AppendLine($"  \"{row.Input}\"  matched no action.   ({row.Why})");
                    continue;
                }

                bool resolved = ConvaiActionTargetResolution.TryResolve(
                    enriched, definition, vocabulary, Vector3.zero,
                    out ConvaiResolvedActionTarget target);

                if (resolved && string.Equals(target?.Name, row.Expected, StringComparison.Ordinal))
                    continue;

                failures.AppendLine(
                    $"  \"{row.Input}\"  ->  \"{target?.Name ?? "(nothing)"}\"   "
                    + $"(expected \"{row.Expected}\")");
                failures.AppendLine($"      {row.From}: {row.Why}");
            }

            Assert.That(failures.ToString(), Is.Empty,
                "Command rows did not recover their target:\n" + failures);
        }

        /// <summary>
        ///     A name that looks like syntax survives being one of several values, not just the only
        ///     one.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The whole-blob vocabulary check only fires for a single-slot action. With two or
        ///         more slots it cannot help, and the brace and quote splitters were stripping their
        ///         delimiters unasked — so an object genuinely called <c>{Annex}</c> lost its braces
        ///         and stopped resolving, on exactly the multi-parameter actions that are hardest to
        ///         debug. Found by an independent review, not by this suite, which had tested the
        ///         must-survive names only through the single-value path.
        ///     </para>
        /// </remarks>
        [Test]
        public void ANameThatLooksLikeSyntaxSurvivesAMultiSlotSplit()
        {
            ConvaiActionConfig vocabulary = Vocabulary();
            var definitions = new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Store In",
                    TargetRequirement = ConvaiActionTargetRequirement.Object,
                    Parameters = new List<ConvaiActionParameterDefinition>
                    {
                        new() { Name = "item", Type = ConvaiActionParameterType.String },
                        new() { Name = "place", Type = ConvaiActionParameterType.Reference }
                    }
                }
            };

            // Braced once, the way a model fills a slot in place. Doubling the braces would be a
            // shape nothing produces, and the brace pattern cannot express nesting anyway.
            ConvaiActionCommand enriched = ConvaiActionResponseParser.Enrich(
                new ConvaiActionCommand("Store In {crate} {Annex}"), vocabulary, definitions);

            Assert.That(
                enriched.Parameters["place"].StringValue,
                Is.EqualTo("{Annex}"),
                "A bay really called '{Annex}' must keep its braces when it is one of several "
                + "values, exactly as it does when it is the only one.");
        }

        // ── The accounting (G7) ──────────────────────────────────────────────────────────

        /// <summary>
        ///     Every repair step is exercised by at least one corpus row.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         This is the accounting the deferred wire-form decision consumes. A repair with no
        ///         row is a repair nobody can say anything about — it might be load-bearing or it
        ///         might be dead, and "we kept it just in case" is how eight of them accumulated.
        ///     </para>
        ///     <para>
        ///         <see cref="Repair.None" /> is excluded: it is what the negative rows declare, and
        ///         it is not a step.
        ///     </para>
        /// </remarks>
        [Test]
        public void EveryRepairStepIsExercisedByTheCorpus()
        {
            // Derived from rows that actually run, in both tables. An earlier version of this test
            // added six repairs to the set by hand, on the strength of a comment claiming the
            // command-level test covered them — which asserted nothing and would have stayed green
            // if those repairs had never run at all. That is the same "accompanies the claim rather
            // than covers it" shape this suite exists to catch, and it appeared here first.
            var exercised = new HashSet<Repair>(
                ValueRows().SelectMany(r => r.DependsOn)
                    .Concat(CommandRows().SelectMany(r => r.DependsOn)));

            Repair[] unexercised = Enum.GetValues(typeof(Repair))
                .Cast<Repair>()
                .Where(r => r != Repair.None)
                .Where(r => !exercised.Contains(r))
                .ToArray();

            Assert.That(
                unexercised,
                Is.Empty,
                "These repair steps have no corpus row, so nothing can say whether they still earn "
                + "their place: " + string.Join(", ", unexercised) + ". Either add a row that needs "
                + "the step, or delete the step and its enum entry together.");
        }

        /// <summary>
        ///     Prints the accounting so a reader can see which rows rest on which repair.
        /// </summary>
        /// <remarks>
        ///     Not an assertion — a report. When the wire form changes, this is the output to diff:
        ///     a repair whose rows all still pass without it is a repair that has stopped earning its
        ///     place, and that is a deletion somebody can argue for with a table in hand.
        /// </remarks>
        [Test]
        public void ReportTheStripperAccounting()
        {
            var byRepair = new SortedDictionary<string, List<string>>(StringComparer.Ordinal);
            foreach (Row row in ValueRows().Concat(CommandRows()))
            {
                foreach (Repair repair in row.DependsOn)
                {
                    if (repair == Repair.None) continue;
                    if (!byRepair.TryGetValue(repair.ToString(), out List<string> rows))
                        byRepair[repair.ToString()] = rows = new List<string>();
                    rows.Add(row.Input);
                }
            }

            var report = new StringBuilder("Repair step -> corpus rows that need it\n");
            foreach (KeyValuePair<string, List<string>> pair in byRepair)
                report.AppendLine($"  {pair.Key}: {string.Join(" | ", pair.Value)}");

            List<Row> all = ValueRows().Concat(CommandRows()).ToList();
            int mustSurvive = all.Count(r => r.From == Provenance.MustSurvive);
            int observed = all.Count(r => r.From == Provenance.Observed);
            report.AppendLine(
                $"  ({observed} observed, {mustSurvive} must-survive, {all.Count} rows total)");

            Convai.Runtime.Logging.ConvaiLogger.Info(
                report.ToString(), Convai.Domain.Logging.LogCategory.Actions);

            Assert.That(byRepair.Count, Is.GreaterThan(0));
        }
    }
}
