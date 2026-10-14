using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Holds R-3 — "a repair to model output is applied only after the raw text has been checked
    ///     against the known vocabulary" — as a property of the code's shape rather than as something
    ///     each new repair has to remember.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>Why a guard at all.</b> The rule was missed three times in a single session: in the
    ///         separator strip, in the brace-slot strip, and in the loop driving them, where the first
    ///         fix stated the rule a second time instead of once. Three misses is not carelessness to
    ///         apologise for; it is evidence that nothing made the rule stick. Every one of them was
    ///         possible because a repair could be written with nothing but a string in hand.
    ///     </para>
    ///     <para>
    ///         <b>Why reflection over signatures and not a source scan.</b> The guard this replaces
    ///         for the reading path matched substrings in source text and had to be taught new
    ///         vocabulary twice. A guard that needs teaching is a guard that lags the code. These
    ///         assertions are about types and parameters, so a repair written in a way nobody
    ///         anticipated still cannot satisfy them by accident.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionRepairBoundingGuardTests
    {
        private static Type WireText => typeof(ConvaiActionWireText);

        private static Type Reading =>
            WireText.GetNestedType("Reading", BindingFlags.NonPublic | BindingFlags.Public)
            ?? throw new AssertionException(
                "ConvaiActionWireText.Reading is gone. It is the type that makes the vocabulary "
                + "unavoidable — if the repairs moved somewhere else, this guard must move with them, "
                + "deliberately, not be deleted.");

        private const BindingFlags AnyDeclared =
            BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        /// <summary>
        ///     A repair cursor cannot be built without the vocabulary it must consult.
        /// </summary>
        /// <remarks>
        ///     This is the load-bearing assertion. Everything else here only stops the repairs
        ///     escaping to somewhere this does not apply.
        /// </remarks>
        [Test]
        public void ARepairCursorCannotExistWithoutAVocabulary()
        {
            ConstructorInfo[] constructors = Reading.GetConstructors(AnyDeclared);

            Assert.That(constructors, Is.Not.Empty, "The cursor must be constructible.");
            foreach (ConstructorInfo constructor in constructors)
            {
                Assert.That(
                    constructor.GetParameters().Any(p => p.ParameterType == typeof(ConvaiActionConfig)),
                    Is.True,
                    "Every way of building a repair cursor must take the vocabulary. A constructor "
                    + "without one is a way to repair a value without checking it, which is R-3 "
                    + "switched off at a call site nobody will read again.");
            }
        }

        /// <summary>
        ///     No repair may sit outside the cursor, where the vocabulary is not in scope.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A repair is a method that takes text and gives back different text. If one of
        ///         those exists on <see cref="ConvaiActionWireText" /> itself rather than on the
        ///         cursor, the vocabulary is not automatically to hand and the next repair added
        ///         beside it inherits the gap.
        ///     </para>
        ///     <para>
        ///         The two string-to-string methods that are allowed here are the entry points
        ///         themselves — one that takes the vocabulary, one that says in its own name that it
        ///         has none. Both are checked below.
        ///     </para>
        /// </remarks>
        [Test]
        public void NoStringRewritingMethodLivesOutsideTheRepairCursor()
        {
            string[] entryPoints =
            {
                nameof(ConvaiActionWireText.Clean),
                nameof(ConvaiActionWireText.CleanWithoutVocabulary)
            };

            List<string> offenders = WireText
                .GetMethods(AnyDeclared)
                .Where(m => m.ReturnType == typeof(string))
                .Where(m => m.GetParameters().Any(p => p.ParameterType == typeof(string)))
                .Where(m => !entryPoints.Contains(m.Name))
                .Select(m => m.Name)
                .ToList();

            Assert.That(
                offenders,
                Is.Empty,
                "These rewrite text from outside the repair cursor, so nothing puts the vocabulary "
                + "in front of them: " + string.Join(", ", offenders) + ". Move them onto "
                + "ConvaiActionWireText.Reading, where having the vocabulary is not optional.");
        }

        /// <summary>
        ///     The vocabulary-taking entry point takes it as a required argument.
        /// </summary>
        /// <remarks>
        ///     An optional parameter would put the rule back where it was. Omitting an argument reads
        ///     as brevity rather than as a decision, and R-3 was in fact switched off that way — the
        ///     split stage in the response parser carried <c>ConvaiActionConfig vocabulary = null</c>
        ///     and the MCP action simulator simply never passed one, so it simulated a stricter
        ///     reader than the one that runs in a conversation.
        /// </remarks>
        [Test]
        public void TheVocabularyIsARequiredArgumentEverywhereItIsTaken()
        {
            IEnumerable<MethodBase> methods = WireText.GetMethods(AnyDeclared)
                .Concat<MethodBase>(Reading.GetConstructors(AnyDeclared))
                .Concat(Reading.GetMethods(AnyDeclared));

            foreach (MethodBase method in methods)
            {
                foreach (ParameterInfo parameter in method.GetParameters())
                {
                    if (parameter.ParameterType != typeof(ConvaiActionConfig))
                        continue;

                    Assert.That(
                        parameter.IsOptional,
                        Is.False,
                        $"{method.DeclaringType?.Name}.{method.Name} makes the vocabulary optional. "
                        + "A default of null means the bounded reading is the one you have to ask "
                        + "for, and the unbounded one is what you get by saying nothing.");
                }
            }
        }

        /// <summary>
        ///     Every split stage in the response parser that can drop text from a value receives the
        ///     vocabulary.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         The splitters are the other half of the repair surface and they do not live behind
        ///         the cursor, because splitting needs the action's declared slots rather than just
        ///         the text. <c>SplitNamedAnchors</c> is the one that matters: treating <c>name:</c>
        ///         as an anchor removes that text, which is a strip under another name, and it is
        ///         precisely the stage that was found — by a live test, not by this suite — to be
        ///         doing it unchecked.
        ///     </para>
        ///     <para>
        ///         Named explicitly rather than pattern-matched, because "which splitters can drop
        ///         text" is a judgement about behaviour that reflection cannot make. The judgement is
        ///         recorded here so that changing it is a visible edit to a test, which is the review
        ///         gate the source-regex guard never was.
        ///     </para>
        /// </remarks>
        [Test]
        public void EverySplitStageThatCanDropTextIsGivenTheVocabulary()
        {
            string[] stagesThatCanDropText =
            {
                "SplitParameterValues",
                "SplitNamedAnchors",
                "StripParamNameMimicry"
            };

            Type parser = typeof(ConvaiActionResponseParser);

            foreach (string stage in stagesThatCanDropText)
            {
                MethodInfo method = parser.GetMethod(stage, AnyDeclared);
                Assert.That(
                    method,
                    Is.Not.Null,
                    $"{stage} is gone or renamed. If the stage no longer exists, remove it from this "
                    + "list deliberately; if it was renamed, rename it here too.");

                ParameterInfo vocabulary = method
                    .GetParameters()
                    .FirstOrDefault(p => p.ParameterType == typeof(ConvaiActionConfig));

                Assert.That(
                    vocabulary,
                    Is.Not.Null,
                    $"{stage} can remove text from a value the Convai Character sent, so it must be "
                    + "handed the vocabulary and check it before believing its own guess.");

                // Checked separately from existence, and the separation is the point. The first
                // version of this guard asserted only that the parameter was there — and stayed
                // green when `ConvaiActionConfig vocabulary = null` was put back, which is the exact
                // shape R-3 was switched off in. A parameter nobody has to pass is not a parameter.
                Assert.That(
                    vocabulary.IsOptional,
                    Is.False,
                    $"{stage} makes the vocabulary optional. A default of null means the bounded "
                    + "reading is the one a caller has to ask for and the unbounded one is what they "
                    + "get by saying nothing — which is how this was missed three times.");
            }
        }
    }
}
