using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Data;
using Convai.Modules.BodyLanguage.Editor;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Guards on what Body Language presents to a customer, as opposed to what it computes.
    /// </summary>
    /// <remarks>
    ///     These pin decisions that are cheap to regress and expensive to notice: a control that
    ///     drives nothing, an Inspector tooltip written for the team rather than the user, or a
    ///     default that leaves a whole authored table inert. Every one of them shipped at least once
    ///     before this suite existed.
    /// </remarks>
    public sealed class BodyLanguageReleaseSurfaceGuardTests
    {
        /// <summary>
        ///     Vocabulary that describes how the SDK was built rather than what it does. Inspector
        ///     tooltips are read by customers, so none of it belongs in one.
        /// </summary>
        /// <remarks>
        ///     Two kinds, because the first alone was not enough. Development artifacts — plan
        ///     sections, phase numbers — were caught from the start. Internal *system* vocabulary was
        ///     not, and eight tooltips shipped naming a C# interface, a tick phase, or an enum value
        ///     the user has no way to see: a field explained as "retained under UpperBody
        ///     suppression" told a customer nothing they could act on, because nowhere in the
        ///     Inspector is anything called that. Name the behavior, not the type that implements it.
        /// </remarks>
        private static readonly Regex InternalVocabulary = new(
            @"\b(V\d+ plan|v\d+ plan|plan §|Plan §|Phase \d|phase \d|root cause [A-Z]\d|fixes [A-Z]\d" +
            // Interface and type names: a customer cannot search for these, and naming the peer
            // module that provides the capability is always the more useful sentence.
            @"|I[A-Z][A-Za-z]*(Provider|Source|Budget|Performer|Sink|Handler|Binding)" +
            // Tick phases and the compositor: internal machinery, never user-visible.
            @"|Cognition|Expression latency|compositor|tick phase" +
            // Suppression levels are enum members, not labels anything renders.
            @"|UpperBody|FullBody|GestureSuppression" +
            // Our own internal shorthand for why something is on.
            @"|mission behavior|mission behaviour" +
            // Speculative surface: a tooltip must describe what ships, not what might.
            @"|a future |not yet implemented|planned path)\b",
            RegexOptions.Compiled);

        [Test]
        public void NoSerializedField_HasATooltipWrittenForTheTeamRatherThanTheUser()
        {
            var offenders = new List<string>();

            foreach (FieldInfo field in SerializedFields())
            {
                var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                if (tooltip == null) continue;

                Match match = InternalVocabulary.Match(tooltip.tooltip);
                if (match.Success)
                    offenders.Add($"{field.Name} → \"{match.Value}\"");
            }

            Assert.That(offenders, Is.Empty,
                "A tooltip is customer-facing text. These reference internal development artifacts:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void EverySerializedField_HasATooltip()
        {
            var missing = new List<string>();

            foreach (FieldInfo field in SerializedFields())
                if (field.GetCustomAttribute<TooltipAttribute>() == null)
                    missing.Add(field.Name);

            Assert.That(missing, Is.Empty,
                "Every authoring control must explain itself in the Inspector. Missing tooltips: " +
                string.Join(", ", missing));
        }

        [Test]
        public void TheRemovedRestingBreathKnobsStayRemoved()
        {
            // They were serialized, clamped, publicly readable, documented — and read by no runtime
            // code, because breath rate and depth come from the per-state policy table. Reinstating
            // either one would put a control that does nothing back in front of a customer.
            Assert.IsNull(typeof(ConvaiBodyLanguageProfile).GetProperty("RestingBreathRateCpm"),
                "Breath rate belongs to the per-state policy table, which is its single source.");
            Assert.IsNull(typeof(ConvaiBodyLanguageProfile).GetProperty("RestingBreathDepth"),
                "Breath depth belongs to the per-state policy table, which is its single source.");
        }

        [Test]
        public void ANewProfile_HasEmotionModulationOnSoItsEmotionTableIsNotInert()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.IsTrue(profile.EnableEmotionModulation,
                    "A new profile ships a hand-tuned emotion table and a valence/arousal fallback. " +
                    "With the toggle that reads them off, none of it does anything until a user " +
                    "finds and flips it.");
                Assert.That(profile.EmotionModifiers.Count, Is.GreaterThan(0),
                    "The toggle being on is only meaningful if there is a table behind it.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void TheProfileShipsSilent()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                Assert.That(profile.TraceVerbosity, Is.EqualTo(BodyLanguageTraceVerbosity.Off),
                    "Diagnostics are opt-in. A module that narrates itself into a customer's console " +
                    "by default is a defect, not a feature.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void NoPublicMemberDocumentsItselfAgainstAnInternalPlan()
        {
            // XML documentation reaches customers through IntelliSense. This catches the member
            // names, which is all reflection can see; the wording itself is covered by review.
            var offenders = new List<string>();

            foreach (MemberInfo member in PublicSurface())
                if (InternalVocabulary.IsMatch(member.Name))
                    offenders.Add(member.Name);

            Assert.That(offenders, Is.Empty,
                "Public API named after an internal plan: " + string.Join(", ", offenders));
        }

        /// <summary>
        ///     Anatomy and machinery a customer should never have to decode to set a control. Checked
        ///     against the label the Inspector actually draws, not the field name behind it.
        /// </summary>
        private static readonly string[] LabelJargon =
        {
            "Obliquity", "Sagittal", "Refractory", "Hysteresis", "Derivative", "Slew", "Cpm",
            "Suppression", "Lod"
        };

        [Test]
        public void EveryDrawnField_HasALabelWithoutAnatomyOrMachinery()
        {
            var offenders = new List<string>();

            foreach (string field in DrawnFields())
            {
                string label = BodyLanguageLabels.ForField(field);

                foreach (string term in LabelJargon)
                    if (label.Contains(term, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{field} → \"{label}\" ({term})");
            }

            Assert.That(offenders, Is.Empty,
                "A field label is the first thing a customer reads, and the only one they cannot look " +
                "past. These still render in anatomy or internal machinery:\n" + string.Join("\n", offenders));
        }

        /// <summary>
        ///     The same bar for the two authored tables. Their rows are public serialized struct
        ///     fields, so they cannot be renamed without breaking assets and customer code — the
        ///     Inspector relabels them instead, and this holds that relabelling honest.
        /// </summary>
        [Test]
        public void EveryTableRowField_HasALabelWithoutAnatomyOrMachinery()
        {
            var offenders = new List<string>();

            foreach (Type row in new[] { typeof(BodyLanguageStatePolicy), typeof(BodyLanguageEmotionModifier) })
            foreach (FieldInfo field in row.GetFields(BindingFlags.Instance | BindingFlags.Public))
            {
                string label = BodyLanguageLabels.ForRowField(field.Name);

                foreach (string term in LabelJargon)
                    if (label.Contains(term, StringComparison.OrdinalIgnoreCase))
                        offenders.Add($"{row.Name}.{field.Name} → \"{label}\" ({term})");
            }

            Assert.That(offenders, Is.Empty,
                "Rows of the per-state and per-emotion tables still read in anatomy or machinery:\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void EveryRowLabel_NamesAFieldThatStillExists()
        {
            var fields = new HashSet<string>();
            foreach (Type row in new[] { typeof(BodyLanguageStatePolicy), typeof(BodyLanguageEmotionModifier) })
            foreach (FieldInfo field in row.GetFields(BindingFlags.Instance | BindingFlags.Public))
                fields.Add(field.Name);

            var orphans = new List<string>();
            foreach (string name in BodyLanguageLabels.Rows.Keys)
                if (!fields.Contains(name))
                    orphans.Add(name);

            Assert.That(orphans, Is.Empty,
                "Row label overrides naming fields that no longer exist: " + string.Join(", ", orphans));
        }

        [Test]
        public void EveryPlainLabel_NamesAFieldThatIsStillDrawn()
        {
            // A label map is exactly the kind of table that rots quietly: rename or drop a field and
            // the entry stays, doing nothing, looking maintained.
            var orphans = new List<string>();
            var drawn = new HashSet<string>(DrawnFields());

            foreach (string field in BodyLanguageLabels.Fields.Keys)
                if (!drawn.Contains(field))
                    orphans.Add(field);

            Assert.That(orphans, Is.Empty,
                "Label overrides for fields the inspector no longer draws: " + string.Join(", ", orphans));
        }

        /// <summary>
        ///     The setup report tells a user which switch is holding their character still. If it
        ///     named that switch anything other than what the Inspector draws, the user would be sent
        ///     looking for a control that does not exist under that name — so the switch table carries
        ///     the field, not a label of its own, and this proves every one of them resolves.
        /// </summary>
        [Test]
        public void EverySwitch_NamesAFieldTheInspectorDraws()
        {
            ConvaiBodyLanguageProfile profile = ConvaiBodyLanguageProfile.CreateDefault();
            try
            {
                var drawn = new HashSet<string>(DrawnFields());
                var offenders = new List<string>();

                foreach (BodyLanguageSwitch entry in BodyLanguageSetupService.SwitchesOf(profile))
                {
                    if (!drawn.Contains(entry.FieldName))
                        offenders.Add($"{entry.FieldName} (not drawn by the profile inspector)");
                    else if (string.IsNullOrWhiteSpace(entry.Label))
                        offenders.Add($"{entry.FieldName} (no label)");
                }

                Assert.That(offenders, Is.Empty,
                    "Switches the diagnosis can name but the Inspector does not show:\n" +
                    string.Join("\n", offenders));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        /// <summary>Every serialized field the profile inspector puts on screen, in section order.</summary>
        private static IEnumerable<string> DrawnFields()
        {
            foreach ((GUIContent _, string _, string[] properties) in ConvaiBodyLanguageProfileEditor.Sections)
                foreach (string property in properties)
                    yield return property;
        }

        private static IEnumerable<FieldInfo> SerializedFields()
        {
            FieldInfo[] fields = typeof(ConvaiBodyLanguageProfile).GetFields(
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

            foreach (FieldInfo field in fields)
            {
                if (field.IsNotSerialized) continue;
                if (!field.IsPublic && field.GetCustomAttribute<SerializeField>() == null) continue;
                yield return field;
            }
        }

        private static IEnumerable<MemberInfo> PublicSurface()
        {
            var types = new[] { typeof(ConvaiBodyLanguageProfile), typeof(ConvaiBodyLanguageController) };

            foreach (Type type in types)
                foreach (MemberInfo member in type.GetMembers(BindingFlags.Public | BindingFlags.Instance |
                                                              BindingFlags.Static | BindingFlags.DeclaredOnly))
                    yield return member;
        }
    }
}
