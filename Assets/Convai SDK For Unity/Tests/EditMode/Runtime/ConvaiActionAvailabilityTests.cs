using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Shared.Actions;
using Convai.Shared.Types;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Coverage for the authored <see cref="ConvaiActionDefinition.Enabled" />
    ///     flag (inverted serialization + upgrade default), the
    ///     <see cref="ConvaiCharacterActions.SetActionAvailable" /> /
    ///     <see cref="ConvaiCharacterActions.IsActionAvailable" /> runtime overrides and their
    ///     precedence, connect-config exclusion, the dispatcher's unhandled path for disabled
    ///     actions, and the internal editor-tooling command bypass flags.
    /// </summary>
    [TestFixture]
    public class ConvaiActionAvailabilityTests
    {
        [OneTimeSetUp]
        public void OneTimeSetUp() => Convai.Runtime.Logging.ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => Convai.Runtime.Logging.ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith("ActionAvailabilityTests_", StringComparison.Ordinal))
                    UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        // ── Enabled flag: default, inversion, upgrade path ───────────────────────────────

        [Test]
        public void Enabled_DefaultsTrue_OnNewDefinition()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Wave" };

            Assert.IsTrue(definition.Enabled);
        }

        [Test]
        public void Enabled_SerializesInverted_SoDisabledSurvivesRoundTrip()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Wave", Enabled = false };

            string json = JsonUtility.ToJson(definition);
            StringAssert.Contains("\"_disabled\":true", json,
                "Enabled must serialize through the inverted private _disabled field.");

            var roundTripped = JsonUtility.FromJson<ConvaiActionDefinition>(json);
            Assert.IsFalse(roundTripped.Enabled);
        }

        [Test]
        public void Definition_SerializedBeforeFlagExisted_DeserializesEnabled()
        {
            // The upgrade contract: older serialized definitions carry no _disabled field at all,
            // and a missing bool deserializes as false — which must mean ENABLED.
            var upgraded = JsonUtility.FromJson<ConvaiActionDefinition>("{\"ActionName\":\"Wave\"}");

            Assert.AreEqual("Wave", upgraded.ActionName);
            Assert.IsTrue(upgraded.Enabled,
                "A definition serialized before the availability flag existed must stay enabled after upgrade.");
        }

        [Test]
        public void Clone_PreservesDisabledState()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Wave", Enabled = false };

            ConvaiActionDefinition clone = definition.Clone();

            Assert.IsFalse(clone.Enabled);
            Assert.IsTrue(new ConvaiActionDefinition { ActionName = "Nod" }.Clone().Enabled);
        }

        [Test]
        public void FilterAndClone_FirstOccurrenceWins_AndCarriesDisabledFlag()
        {
            // Mirrors the inline-wins-collision merge in GetEffectiveDefinitions: the surviving
            // (first) definition's availability must be the one that carries through.
            var definitions = new List<ConvaiActionDefinition>
            {
                new() { ActionName = "Wave", Enabled = false },
                new() { ActionName = "Wave", Enabled = true }
            };

            List<ConvaiActionDefinition> merged = ConvaiActionDefinition.FilterAndClone(definitions);

            Assert.AreEqual(1, merged.Count);
            Assert.IsFalse(merged[0].Enabled);
        }

        // ── Connect config exclusion ─────────────────────────────────────────────────────

        [Test]
        public void BuildActionConfig_ExcludesAuthoredDisabledActions()
        {
            (_, _, ConvaiActionConfigSource source, _) = CreateCharacterFixture(
                ("Wave", true), ("Bow", false));

            ConvaiActionConfig config = source.BuildActionConfig();

            Assert.NotNull(config);
            Assert.AreEqual(1, config.Actions.Count);
            StringAssert.StartsWith("Wave", config.Actions[0]);
        }

        [Test]
        public void BuildActionConfig_AllActionsDisabled_OmitsConfig()
        {
            (_, _, ConvaiActionConfigSource source, _) = CreateCharacterFixture(("Wave", false));

            Assert.IsNull(source.BuildActionConfig());
        }

        [Test]
        public void BuildActionConfig_RuntimeOverride_WinsInBothDirections()
        {
            (_, ConvaiCharacter character, ConvaiActionConfigSource source, _) = CreateCharacterFixture(
                ("Wave", true), ("Bow", false));

            character.Actions.SetActionAvailable("Wave", false);
            character.Actions.SetActionAvailable("Bow", true);

            ConvaiActionConfig config = source.BuildActionConfig(character.Actions);

            Assert.NotNull(config);
            Assert.AreEqual(1, config.Actions.Count);
            StringAssert.StartsWith("Bow", config.Actions[0]);
        }

        [Test]
        public void RuntimeResolutionConfig_KeepsDisabledActions_ForLocalResolution()
        {
            // Disabled actions must stay locally resolvable so the dispatcher can classify a
            // stale command as "action disabled" instead of losing it as raw-unmatched.
            (_, ConvaiCharacter character, _, _) = CreateCharacterFixture(("Wave", false));

            ConvaiActionConfig merged = character.GetRuntimeActionConfig();

            Assert.NotNull(merged);
            Assert.AreEqual(1, merged.Actions.Count);
            StringAssert.StartsWith("Wave", merged.Actions[0]);
        }

        // ── IsActionAvailable precedence ─────────────────────────────────────────────────

        [Test]
        public void IsActionAvailable_UsesAuthoredEnabled_WhenNoOverride()
        {
            (_, ConvaiCharacter character, _, _) = CreateCharacterFixture(("Wave", true), ("Bow", false));

            Assert.IsTrue(character.Actions.IsActionAvailable("Wave"));
            Assert.IsTrue(character.Actions.IsActionAvailable("wave"), "Lookup must be case-insensitive.");
            Assert.IsFalse(character.Actions.IsActionAvailable("Bow"));
        }

        [Test]
        public void IsActionAvailable_RuntimeOverride_WinsOverAuthored_BothDirections()
        {
            (_, ConvaiCharacter character, _, _) = CreateCharacterFixture(("Wave", true), ("Bow", false));

            character.Actions.SetActionAvailable("wave", false);
            character.Actions.SetActionAvailable("BOW", true);

            Assert.IsFalse(character.Actions.IsActionAvailable("Wave"));
            Assert.IsTrue(character.Actions.IsActionAvailable("Bow"));
        }

        [Test]
        public void IsActionAvailable_UnknownAction_IsFalse_UnlessOverridden()
        {
            (_, ConvaiCharacter character, _, _) = CreateCharacterFixture(("Wave", true));

            Assert.IsFalse(character.Actions.IsActionAvailable("Vanish"));

            character.Actions.SetActionAvailable("Vanish", true);
            Assert.IsTrue(character.Actions.IsActionAvailable("Vanish"));
        }

        // ── Dispatcher: disabled command → unhandled ─────────────────────────────────────

        [Test]
        public async Task Dispatcher_AuthoredDisabledAction_ReportsUnhandled_WithoutExecuting()
        {
            (_, _, _, ConvaiActionDispatcher dispatcher, RecordingExecutor executor) =
                CreateDispatcherFixture(("Wave", false));
            ConvaiActionStepReport report = null;
            dispatcher.OnStepCompleted.AddListener(r => report = r);
            bool unhandledFired = false;
            dispatcher.OnStepUnhandled.AddListener(_ => unhandledFired = true);

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wave") });
            await WaitUntilAsync(() => report != null);

            Assert.IsTrue(unhandledFired);
            Assert.AreEqual(ConvaiActionExecutionStatus.Unhandled, report.Result.Status);
            StringAssert.Contains("disabled", report.Result.Message);
            Assert.IsEmpty(executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_RuntimeDisable_ThenReEnable_TogglesExecution()
        {
            (_, ConvaiCharacter character, _, ConvaiActionDispatcher dispatcher, RecordingExecutor executor) =
                CreateDispatcherFixture(("Wave", true));

            character.Actions.SetActionAvailable("Wave", false);
            int completedCount = 0;
            dispatcher.OnStepCompleted.AddListener(_ => completedCount++);

            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wave") });
            await WaitUntilAsync(() => completedCount == 1);
            Assert.IsEmpty(executor.ExecutedActions, "A runtime-disabled action must not execute.");

            character.Actions.SetActionAvailable("Wave", true);
            dispatcher.EnqueueActions(new[] { new ConvaiActionCommand("Wave") });
            await WaitUntilAsync(() => executor.ExecutedActions.Count == 1);

            CollectionAssert.AreEqual(new[] { "Wave" }, executor.ExecutedActions);
        }

        [Test]
        public async Task Dispatcher_BypassAvailability_RunsDisabledAction()
        {
            (_, _, _, ConvaiActionDispatcher dispatcher, RecordingExecutor executor) =
                CreateDispatcherFixture(("Wave", false));

            dispatcher.EnqueueActions(new[]
            {
                new ConvaiActionCommand("Wave") { BypassAvailability = true }
            });
            await WaitUntilAsync(() => executor.ExecutedActions.Count == 1);

            CollectionAssert.AreEqual(new[] { "Wave" }, executor.ExecutedActions);
        }

        // ── Speech-gate bypass ───────────────────────────────────────────────────────────

        [Test]
        public async Task Dispatcher_BypassSpeechGate_SkipsSpeechWaitEntirely()
        {
            (GameObject gameObject, _, ConvaiActionConfigSource source, ConvaiActionDispatcher dispatcher,
                    RecordingExecutor executor) =
                CreateDispatcherFixture(("Wave", true));

            // A 30 s gate timeout makes a pass here prove the bypass, not the timeout fallback.
            SetPrivateField(dispatcher, "_speechGateTimeoutSeconds", 30f);
            List<ConvaiActionDefinition> definitions = new()
            {
                new ConvaiActionDefinition
                {
                    ActionName = "Wave",
                    Executor = gameObject.GetComponent<RecordingExecutor>(),
                    WaitForBotSpeech = true
                }
            };
            source.ReplaceDefinitions(definitions);

            dispatcher.EnqueueActions(new[]
            {
                new ConvaiActionCommand("Wave")
                {
                    WaitForBotSpeech = true,
                    BypassSpeechGate = true
                }
            });

            await WaitUntilAsync(() => executor.ExecutedActions.Count == 1, timeoutMs: 2000);
            CollectionAssert.AreEqual(new[] { "Wave" }, executor.ExecutedActions);
        }

        [Test]
        public void CommandClone_CarriesInternalBypassFlags()
        {
            var command = new ConvaiActionCommand("Wave")
            {
                BypassSpeechGate = true,
                BypassAvailability = true
            };

            ConvaiActionCommand clone = command.Clone();

            Assert.IsTrue(clone.BypassSpeechGate);
            Assert.IsTrue(clone.BypassAvailability);
            Assert.IsFalse(new ConvaiActionCommand("Wave").Clone().BypassSpeechGate);
        }

        // ── Fixture helpers ──────────────────────────────────────────────────────────────

        private sealed class RecordingExecutor : MonoBehaviour, IConvaiActionExecutor
        {
            public readonly List<string> ExecutedActions = new();

            public Task<ConvaiActionExecutionResult> ExecuteAsync(
                ConvaiActionInvocation invocation, CancellationToken cancellationToken)
            {
                ExecutedActions.Add(invocation.Command?.Name ?? string.Empty);
                return Task.FromResult(ConvaiActionExecutionResult.Succeeded());
            }
        }

        private static (GameObject GameObject, ConvaiCharacter Character, ConvaiActionConfigSource Source,
            RecordingExecutor Executor) CreateCharacterFixture(params (string Name, bool Enabled)[] actions)
        {
            var gameObject = new GameObject($"ActionAvailabilityTests_{Guid.NewGuid():N}");
            var character = gameObject.AddComponent<ConvaiCharacter>();
            SetPrivateField(character, "_characterId", "availability-char");
            SetPrivateField(character, "_characterName", "Availability Test");
            var source = gameObject.AddComponent<ConvaiActionConfigSource>();
            var executor = gameObject.AddComponent<RecordingExecutor>();

            var definitions = new List<ConvaiActionDefinition>();
            foreach ((string name, bool enabled) in actions)
            {
                definitions.Add(new ConvaiActionDefinition
                {
                    ActionName = name,
                    TargetRequirement = ConvaiActionTargetRequirement.None,
                    Executor = executor,
                    Enabled = enabled
                });
            }

            source.ReplaceDefinitions(definitions);
            return (gameObject, character, source, executor);
        }

        private static (GameObject GameObject, ConvaiCharacter Character, ConvaiActionConfigSource Source,
            ConvaiActionDispatcher Dispatcher, RecordingExecutor Executor) CreateDispatcherFixture(
            params (string Name, bool Enabled)[] actions)
        {
            (GameObject gameObject, ConvaiCharacter character, ConvaiActionConfigSource source,
                RecordingExecutor executor) = CreateCharacterFixture(actions);
            var dispatcher = gameObject.AddComponent<ConvaiActionDispatcher>();
            return (gameObject, character, source, dispatcher, executor);
        }

        private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 1000)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!predicate())
            {
                if (DateTime.UtcNow >= deadline)
                    throw new AssertionException("Timed out waiting for condition.");

                await Task.Delay(10);
            }
        }

        private static void SetPrivateField(object instance, string fieldName, object value)
        {
            System.Reflection.FieldInfo field = instance.GetType().GetField(
                fieldName,
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
            if (field == null)
                throw new MissingFieldException(instance.GetType().FullName, fieldName);

            field.SetValue(instance, value);
        }
    }
}
