using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Shared.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Makes a new field on a target type decide its own merge semantics before it can ship.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         A character's action targets are assembled from four sources — the base config, the
    ///         runtime registry, <c>ConvaiActionTarget</c> components in the scene, and
    ///         <c>ConvaiActionTargetGroup</c>. Every field on a target entry therefore needs an answer
    ///         to "what happens when two sources both have one", and until this guard existed the
    ///         answer was whatever the merge code happened to do. That is how <c>Description</c> came
    ///         to be the only completable field that was never completed: a Scene Knowledge entry with
    ///         a blank description, next to a Convai Action Target carrying one, sent the character
    ///         nothing — the author had written the sentence and it went nowhere.
    ///     </para>
    ///     <para>
    ///         <b>The table below is the declared rule set</b>, transcribed from the architecture
    ///         plan's §4.4. The guard reflects over what Unity actually serializes on both target
    ///         types and fails when a field is not in it. Adding a field to a target type is then a
    ///         choice about merging, made deliberately, rather than a default nobody picked.
    ///     </para>
    ///     <para>
    ///         <b>What this guard does not do</b> is check that the code implements the rule it
    ///         declares — that needs a real <c>ConvaiActionTarget</c> in a scene, whose registration
    ///         runs in <c>OnEnable</c>, which EditMode never calls. Those assertions live in the
    ///         PlayMode composition tests. This one is coverage: it cannot tell you the rule is
    ///         obeyed, only that somebody chose one.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionCompositionGuardTests
    {
        /// <summary>How a field behaves when more than one source supplies it.</summary>
        private enum MergeRule
        {
            /// <summary>Identifies the entry. Trimmed, case-insensitive; the first source to claim it owns it.</summary>
            Key,

            /// <summary>The earlier source's value wins, but a later source may fill a blank.</summary>
            FirstNonEmpty,

            /// <summary>The earlier source's value wins; a later source may fill a null, and two different values are a collision that is reported once.</summary>
            FirstNonNullConflictReported,

            /// <summary>The earlier source's value wins; a later source may fill a null. Two different values are not worth a warning.</summary>
            FirstNonNull,

            /// <summary>Every source's values are kept. Alternate names are more ways to ask, never competing answers.</summary>
            Union,

            /// <summary>Only the owning source decides. A deliberate "nothing in the scene answers to this" is not overridden later.</summary>
            OwningSourceOnly,

            /// <summary>A per-character overlay applied last, over every source. Session-scoped; never touches authored data.</summary>
            SessionOverlay
        }

        /// <summary>
        ///     The declared rules, keyed by the logical member name on a target entry.
        /// </summary>
        /// <remarks>
        ///     One table for both target types: they are structurally identical apart from the prose
        ///     field being called <c>Description</c> on an object and <c>Bio</c> on a character, which
        ///     is the whole reason <c>ConvaiActionTargetCandidate</c> exists.
        /// </remarks>
        private static readonly Dictionary<string, MergeRule> DeclaredRules = new(StringComparer.Ordinal)
        {
            ["Name"] = MergeRule.Key,
            ["Description"] = MergeRule.FirstNonEmpty,
            ["Bio"] = MergeRule.FirstNonEmpty,
            ["GameObjectReference"] = MergeRule.FirstNonNullConflictReported,
            ["InteractionPoint"] = MergeRule.FirstNonNull,
            ["Aliases"] = MergeRule.Union,
            ["TextOnly"] = MergeRule.OwningSourceOnly,
            ["Available"] = MergeRule.SessionOverlay
        };

        private static IEnumerable<Type> TargetTypes => new[]
        {
            typeof(ConvaiActionObjectDefinition),
            typeof(ConvaiActionCharacterDefinition)
        };

        /// <summary>
        ///     Every serialized field on both target types has a declared merge rule.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Reflects over fields rather than properties, because fields are what Unity
        ///         serializes and therefore what actually reaches a customer's scene. Auto-property
        ///         backing fields are mapped back to their property name, and the private backing
        ///         store behind <c>Available</c> is mapped to it, so the table reads in the vocabulary
        ///         an author would use.
        ///     </para>
        ///     <para>
        ///         Revert the <c>Description</c> entry out of the table and this goes red — that is
        ///         the proof, and it is the same shape as the field a future change would add.
        ///     </para>
        /// </remarks>
        [Test]
        public void EverySerializedFieldOnATargetTypeHasADeclaredMergeRule()
        {
            var undeclared = new List<string>();

            foreach (Type type in TargetTypes)
            {
                foreach (FieldInfo field in type.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.DeclaredOnly))
                {
                    string member = LogicalNameOf(field);
                    if (DeclaredRules.ContainsKey(member))
                        continue;

                    undeclared.Add($"{type.Name}.{member}");
                }
            }

            Assert.That(
                undeclared,
                Is.Empty,
                "These fields reach a customer's scene with no decision about what happens when two "
                + "sources both supply them: " + string.Join(", ", undeclared) + ". Add a row to "
                + nameof(DeclaredRules) + " and implement it in ConvaiCharacter.Actions, or the merge "
                + "will pick an answer nobody chose.");
        }

        /// <summary>
        ///     The table does not carry rules for fields that no longer exist.
        /// </summary>
        /// <remarks>
        ///     The other half of coverage, and the half that rots quietly. A rule for a deleted field
        ///     reads as though the merge still handles something it does not, which is worse than no
        ///     rule at all — the next reader trusts it.
        /// </remarks>
        [Test]
        public void TheTableCarriesNoRuleForAFieldThatIsGone()
        {
            var present = new HashSet<string>(StringComparer.Ordinal);
            foreach (Type type in TargetTypes)
            {
                foreach (FieldInfo field in type.GetFields(
                             BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.DeclaredOnly))
                    present.Add(LogicalNameOf(field));
            }

            string[] stale = DeclaredRules.Keys.Where(k => !present.Contains(k)).ToArray();

            Assert.That(
                stale,
                Is.Empty,
                "These have merge rules but no longer exist on either target type: "
                + string.Join(", ", stale) + ". Remove the rows.");
        }

        /// <summary>
        ///     The prose field really is the only difference between the two target types.
        /// </summary>
        /// <remarks>
        ///     The premise the whole composition rests on, and the reason one rule table can serve
        ///     both. If a field is ever added to one type and not the other, every piece of logic
        ///     written once over <c>ConvaiActionTargetCandidate</c> starts quietly meaning two things.
        /// </remarks>
        [Test]
        public void TheTwoTargetTypesStillDifferOnlyInWhatTheProseFieldIsCalled()
        {
            HashSet<string> objectMembers = MembersOf(typeof(ConvaiActionObjectDefinition));
            HashSet<string> characterMembers = MembersOf(typeof(ConvaiActionCharacterDefinition));

            objectMembers.Remove("Description");
            characterMembers.Remove("Bio");

            Assert.That(
                objectMembers.SetEquals(characterMembers),
                Is.True,
                "Object entry has: " + string.Join(", ", objectMembers.OrderBy(x => x))
                + "\nCharacter entry has: " + string.Join(", ", characterMembers.OrderBy(x => x))
                + "\nThey must stay structurally identical apart from Description/Bio — that premise "
                + "is what lets ConvaiActionTargetCandidate read either one with a single body.");
        }

        private static HashSet<string> MembersOf(Type type) =>
            new(type.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                        BindingFlags.DeclaredOnly)
                    .Select(LogicalNameOf),
                StringComparer.Ordinal);

        /// <summary>
        ///     The name an author would use for this field: the property in front of an auto-property
        ///     backing field, and <c>Available</c> in front of its inverted backing store.
        /// </summary>
        private static string LogicalNameOf(FieldInfo field)
        {
            string name = field.Name;

            // "<Name>k__BackingField" -> "Name"
            int open = name.IndexOf('<');
            int close = name.IndexOf('>');
            if (open == 0 && close > 1)
                return name.Substring(1, close - 1);

            // The one hand-written backing store. Available is stored inverted so that Unity's
            // constructor-less deserialization, which zeroes the field, reads as available.
            return name == "_unavailable" ? "Available" : name;
        }
    }
}
