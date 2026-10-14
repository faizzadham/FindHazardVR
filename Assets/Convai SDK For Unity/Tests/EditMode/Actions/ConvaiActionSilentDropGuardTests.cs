using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using Convai.Runtime.Actions;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Guards against the class of defect that made every targeted action silently do nothing.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         That failure was not one mistake. It was three changes landing in the same week, each
    ///         defensible on its own, made by two people who could not see each other's half — and no
    ///         test measured the combination. These tests measure combinations.
    ///     </para>
    ///     <para>
    ///         They are deliberately blunt. Each one fails if the specific shape of the original
    ///         defect returns, whatever route it takes back in.
    ///     </para>
    /// </remarks>
    [TestFixture]
    public sealed class ConvaiActionSilentDropGuardTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        private sealed class AcceptingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public System.Threading.Tasks.Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, System.Threading.CancellationToken cancellationToken) =>
                System.Threading.Tasks.Task.FromResult(ConvaiActionExecutionResult.Succeeded("ok"));
        }

        /// <summary>
        ///     The whole original defect, end to end, in one test.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         A target rebuilt the way a scene load rebuilds it — field by field, no constructor
        ///         — named inside the action text the way the backend actually sends it, with the
        ///         separator a model echoes off the template it was shown. Each of the three faults
        ///         alone was enough to drop this command, and none of them was individually visible.
        ///     </para>
        ///     <para>
        ///         This is the test that did not exist. Revert any one of the three fixes and it goes
        ///         red; that is its entire job.
        ///     </para>
        /// </remarks>
        [Test]
        public void CommandFromTheWire_ReachesAnExecutor_ThroughEveryStageAtOnce()
        {
            var host = new GameObject("silent-drop-guard-host");
            var gallery = new GameObject("silent-drop-guard-gallery");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();

                // Rebuilt without a constructor: exactly how Unity restores a [Serializable] entry
                // from a scene or prefab, and where a property initializer silently does not run.
                var entry = (ConvaiActionObjectDefinition)System.Runtime.Serialization.FormatterServices
                    .GetUninitializedObject(typeof(ConvaiActionObjectDefinition));
                entry.Name = "The Gallery";
                entry.Description = "The east room.";
                entry.GameObjectReference = gallery;

                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition> { entry }
                };
                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Walk To",
                        Description = "Walk over to a place.",
                        TargetRequirement = ConvaiActionTargetRequirement.Either,
                        Executor = executor
                    }
                };

                // No Target field, the name inside the action text, and the separator the model
                // copies from "Name - description".
                var command = new ConvaiActionCommand("Walk To - The Gallery");
                var drops = new ConvaiActionDropCollector(true);

                IReadOnlyList<ConvaiActionCommand> accepted =
                    ConvaiActionResponseParser.FilterExecutableBatch(
                        new[] { command }, config, definitions, drops, Vector3.zero);

                Assert.That(accepted.Count, Is.EqualTo(1),
                    drops.Reports.Count > 0
                        ? "Command was dropped: " + drops.Reports[0].Explanation
                        : "Command was dropped with no explanation, which is its own failure.");

                Assert.That(
                    ConvaiActionTargetResolution.TryResolveActionable(
                        accepted[0], definitions[0], config, Vector3.zero,
                        out ConvaiResolvedActionTarget target),
                    Is.True);
                Assert.That(target.GameObjectReference, Is.SameAs(gallery));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gallery);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     No <c>[Serializable]</c> type Unity rebuilds may carry a property initializer.
        /// </summary>
        /// <remarks>
        ///     This is the shape of the <c>Available = true</c> defect rather than that one instance
        ///     of it. Unity restores such a type field by field without running a constructor, so an
        ///     initializer is a default that looks present in the source and is absent at runtime —
        ///     a discrepancy no amount of reading the class will reveal.
        /// </remarks>
        [Test]
        public void SerializableActionTypes_CarryNoPropertyInitializerUnityWouldNotRun()
        {
            var offenders = new List<string>();

            foreach (Type type in typeof(ConvaiActionConfig).Assembly.GetTypes())
            {
                if (!type.IsClass || type.GetCustomAttribute<SerializableAttribute>() == null)
                    continue;

                // Only types Unity actually restores field-by-field: a [Serializable] class whose
                // state is auto-properties is not one Unity round-trips at all.
                if (!HasUnitySerializedBackingField(type))
                    continue;

                foreach (PropertyInfo property in type.GetProperties(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (property.GetMethod == null || property.SetMethod == null)
                        continue;

                    if (HasInitializerBackingField(type, property) &&
                        !IsInvertedStorage(type, property))
                        offenders.Add($"{type.Name}.{property.Name}");
                }
            }

            Assert.That(offenders, Is.Empty,
                "A property initializer on a type Unity rebuilds without a constructor is a default " +
                "that exists in the source and not at runtime. Store the value so its zero means the " +
                "intended default — see ConvaiActionObjectDefinition.Available.");
        }

        private static bool HasUnitySerializedBackingField(Type type)
        {
            foreach (FieldInfo field in type.GetFields(
                         BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.IsPublic || field.GetCustomAttribute<SerializeField>() != null)
                    return true;
            }

            return false;
        }

        /// <summary>
        ///     Whether the compiler emitted an auto-property backing field that a constructor would
        ///     have to run to populate.
        /// </summary>
        private static bool HasInitializerBackingField(Type type, PropertyInfo property)
        {
            FieldInfo backing = type.GetField(
                $"<{property.Name}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (backing == null)
                return false;

            // An uninitialized instance is exactly what Unity hands back. If the value differs from
            // one built normally, a constructor was doing work Unity will not do.
            object bare = System.Runtime.Serialization.FormatterServices.GetUninitializedObject(type);
            object built;
            try
            {
                built = Activator.CreateInstance(type);
            }
            catch (MissingMethodException)
            {
                return false;
            }

            object bareValue = backing.GetValue(bare);
            object builtValue = backing.GetValue(built);
            if (bareValue == null && builtValue == null)
                return false;

            // Reference-typed defaults (an empty list) are restored by Unity itself, so only value
            // types can silently disagree.
            if (!backing.FieldType.IsValueType)
                return false;

            return !Equals(bareValue, builtValue);
        }

        /// <summary>Inverted storage is the fix, not the defect: its zero already means the default.</summary>
        private static bool IsInvertedStorage(Type type, PropertyInfo property) =>
            type.GetField(
                $"_{char.ToLowerInvariant(property.Name[0])}{property.Name.Substring(1)}",
                BindingFlags.NonPublic | BindingFlags.Instance) == null &&
            type.GetField(
                $"<{property.Name}>k__BackingField",
                BindingFlags.NonPublic | BindingFlags.Instance) == null;

        /// <summary>
        ///     Every early exit on the admission path must record why.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Reads the source of the two methods that decide whether a command lives, and fails
        ///         on a bare <c>return</c> or <c>continue</c> that no drop record accompanies. Crude
        ///         on purpose: the defect it guards is a line of code that says nothing, and there is
        ///         no runtime signal for a path that produces no signal.
        ///     </para>
        ///     <para>
        ///         If this fires on a legitimately silent exit, record the drop rather than relaxing
        ///         the test — the whole point is that the next silent path is added deliberately, in
        ///         front of somebody, instead of by accident.
        ///     </para>
        ///     <para>
        ///         <b>This is a backstop, and it is honestly a weak one.</b> It matches substrings in
        ///         a 600-character window, so <c>Report</c> hits any identifier containing the word
        ///         and <c>== null</c> hits any nearby null check. Its token list has been extended
        ///         twice, which is the tell: a scan that has to be taught new vocabulary lags the code
        ///         it guards. The property it approximates is measured exactly by
        ///         <see cref="AdmissionPath_AccountsForEveryCommandItIsGiven" /> — every command in is
        ///         either accepted or counted — and that is the test to trust and to extend. This one
        ///         stays only because it costs nothing and it reads the paths that test cannot reach,
        ///         such as the dispatcher's threading exits.
        ///     </para>
        /// </remarks>
        /// <summary>
        ///     Every command that goes in comes out either accepted or counted. Nothing evaporates.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         <b>This is the real guard, and the source scan below is now only a backstop.</b>
        ///         "No exit says nothing" is a conservation property — <c>in == accepted + reported</c>
        ///         — and it can be measured directly instead of inferred by looking for report-shaped
        ///         words near a <c>return</c>. Measuring it needs no list of tokens, so it cannot fall
        ///         behind the code the way a token list does: a silent exit added tomorrow, by any
        ///         route and under any name, breaks the arithmetic.
        ///     </para>
        ///     <para>
        ///         The batch below is deliberately every way a command can fail at once — unknown
        ///         action, unresolvable target, an entry with nothing in the scene behind it, an
        ///         unresolvable reference parameter, blank and null commands — mixed with commands
        ///         that must succeed, so the test would catch over-refusal as readily as silence.
        ///     </para>
        /// </remarks>
        [Test]
        public void AdmissionPath_AccountsForEveryCommandItIsGiven()
        {
            var host = new GameObject("conservation-host");
            var gallery = new GameObject("conservation-gallery");
            try
            {
                var executor = host.AddComponent<AcceptingExecutor>();
                var config = new ConvaiActionConfig
                {
                    Objects = new List<ConvaiActionObjectDefinition>
                    {
                        new() { Name = "The Gallery", GameObjectReference = gallery },
                        new() { Name = "The Idea" }
                    },
                    Characters = new List<ConvaiActionCharacterDefinition>()
                };

                var definitions = new List<ConvaiActionDefinition>
                {
                    new()
                    {
                        ActionName = "Walk To",
                        TargetRequirement = ConvaiActionTargetRequirement.Either,
                        Executor = executor
                    },
                    new()
                    {
                        ActionName = "Wave",
                        TargetRequirement = ConvaiActionTargetRequirement.None,
                        Executor = executor
                    }
                };

                ConvaiActionCommand[] batch =
                {
                    new("Walk To The Gallery"),                 // resolves
                    new("Wave"),                                // needs nothing
                    new("Walk To - The Gallery"),               // separator echo, resolves
                    new("Walk To The Idea"),                    // resolves, nothing in the scene
                    new("Walk To Nowhere At All"),              // names nothing
                    new("Teleport To The Gallery"),             // unknown action
                    new(""),                                    // blank
                    new("   "),                                 // whitespace
                    null                                        // nothing at all
                };

                var refusing = new ConvaiActionDropCollector(true);
                IReadOnlyList<ConvaiActionCommand> accepted =
                    ConvaiActionResponseParser.FilterExecutableBatch(
                        batch, config, definitions, refusing, Vector3.zero);

                Assert.That(
                    accepted.Count + refusing.DroppedCount,
                    Is.EqualTo(batch.Length),
                    "Some command neither came out of admission nor was counted as dropped. That is "
                    + "the silent drop this whole area was rewritten to remove, and no source scan "
                    + "has to recognise its shape for this to catch it.");

                // The refusing path must also not count the same command twice, or the arithmetic
                // above could balance by accident — one command reported twice hiding one lost.
                Assert.That(accepted.Count, Is.GreaterThan(0), "Sanity: some of these must succeed.");
                Assert.That(refusing.DroppedCount, Is.GreaterThan(0), "Sanity: some of these must fail.");
                Assert.That(refusing.Reports.Count, Is.EqualTo(refusing.DroppedCount),
                    "With detail on, every counted drop carries its explanation.");

                // The non-refusing path hands everything back by contract, and still explains what
                // will not work. It is allowed to report a command more than once — a command can be
                // wrong in two ways — so only the returned count is fixed here.
                var reading = new ConvaiActionDropCollector(true);
                IReadOnlyList<ConvaiActionCommand> read =
                    ConvaiActionResponseParser.ReadWithoutRefusing(
                        batch, config, definitions, reading, Vector3.zero);

                Assert.That(read.Count, Is.EqualTo(batch.Length),
                    "ReadWithoutRefusing returns every command it is given — that is the whole "
                    + "difference between it and the filter, and ConvaiActionDispatcher.ReadLocalBatch "
                    + "relies on it to queue the read commands rather than the raw ones.");
                Assert.That(reading.DroppedCount, Is.GreaterThanOrEqualTo(refusing.DroppedCount),
                    "It explains at least as much as the filter refuses.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(gallery);
                UnityEngine.Object.DestroyImmediate(host);
            }
        }

        /// <summary>
        ///     The parse stage accounts for every entry in the payload too.
        /// </summary>
        /// <remarks>
        ///     This is what the <c>skippedEntries++</c> token in the source scan was arguing for, and
        ///     arguing is what a scan has to do — it cannot see whether the count is right. Measuring
        ///     it is exact, and it is the reason that token can stop being load-bearing.
        /// </remarks>
        [Test]
        public void ParseStage_AccountsForEveryEntryInThePayload()
        {
            var payload = Newtonsoft.Json.Linq.JObject.Parse(@"{
                ""actions"": [
                    { ""name"": ""Walk To The Gallery"" },
                    { ""name"": ""Wave"", ""target"": ""Sofia"" },
                    { ""name"": """" },
                    { ""target"": ""no name at all"" },
                    ""not an object"",
                    12345,
                    null
                ]
            }");

            bool parsed = ConvaiActionResponseParser.TryParseBatch(
                payload, out IReadOnlyList<ConvaiActionCommand> actions, out int skipped);

            Assert.That(parsed, Is.True);
            Assert.That(
                actions.Count + skipped,
                Is.EqualTo(7),
                "An entry in the payload was neither read into a command nor counted as unreadable, "
                + "so the batch quietly shrank between the wire and the SDK.");
        }

        [Test]
        public void AdmissionPath_HasNoExitThatSaysNothing()
        {
            // ReadBatch is the body FilterExecutableBatch became when the local-injection path was
            // given the same reading. The guard follows the code rather than the old name: pointing
            // it at a method that is now one expression would have quietly scanned nothing.
            AssertNoSilentExit(
                "SDK/Runtime/Actions/ConvaiActionResponseParser.cs",
                "ReadBatch");
            AssertNoSilentExit(
                "SDK/Runtime/Actions/ConvaiActionResponseParser.cs",
                "TryParseBatch");
            AssertNoSilentExit(
                "SDK/Runtime/Actions/ConvaiActionDispatcher.cs",
                "EnqueueBatchOnDispatcherThread");
            AssertNoSilentExit(
                "SDK/Runtime/Actions/ConvaiActionDispatcher.cs",
                "ReceiveBatch");
        }

        private static void AssertNoSilentExit(string relativePath, string methodName)
        {
            string path = Path.Combine(
                "Packages/com.convai.convai-sdk-for-unity", relativePath.Replace('/', Path.DirectorySeparatorChar));
            Assert.That(File.Exists(path), Is.True, $"Guard cannot find {path}; update the guard, not the path.");

            string source = File.ReadAllText(path);
            int start = source.IndexOf(methodName + "(", StringComparison.Ordinal);
            Assert.That(start, Is.GreaterThanOrEqualTo(0), $"{methodName} not found in {relativePath}.");

            string body = ExtractBody(source, start);
            foreach (Match match in Regex.Matches(body, @"^[ \t]*(return|continue)\s*;", RegexOptions.Multiline))
            {
                string preceding = body.Substring(0, match.Index);
                int lineStart = preceding.LastIndexOf('\n', Math.Max(0, preceding.Length - 1));
                string window = preceding.Substring(Math.Max(0, lineStart - 600));

                bool accountedFor =
                    window.Contains("drops.") ||
                    window.Contains("Record(") ||
                    window.Contains("Report") ||
                    window.Contains("Explain(") ||
                    window.Contains("Count(") ||
                    window.Contains("accepted.Add(") ||
                    window.Contains("skippedEntries++") ||
                    window.Contains("Count == 0") ||
                    window.Contains("== null");

                Assert.That(accountedFor, Is.True,
                    $"{methodName} in {relativePath} has an exit that records nothing.\n\n" +
                    "Before adding a token to this list, check whether " +
                    nameof(AdmissionPath_AccountsForEveryCommandItIsGiven) + " passes. If it does, " +
                    "the exit really is accounted for and the honest fix is to cover it there with a " +
                    "case that would fail if it were not — not to teach this scan another word. If it " +
                    "does not, you have found a real silent drop.");
            }
        }

        private static string ExtractBody(string source, int start)
        {
            int open = source.IndexOf('{', start);
            int depth = 0;
            for (int i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0)
                    return source.Substring(open, i - open + 1);
            }

            return source.Substring(open);
        }
    }
}
