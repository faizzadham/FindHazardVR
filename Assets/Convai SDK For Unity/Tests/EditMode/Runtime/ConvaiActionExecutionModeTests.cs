// No `using System;`: it would make `Object` ambiguous against UnityEngine.Object, which this
// fixture uses constantly for cleanup. Matches the sibling Actions fixtures.
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Domain.Logging;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Runtime.Logging;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    /// <summary>
    ///     Covers <see cref="ConvaiActionExecutionMode" />: the declaration on
    ///     <see cref="ConvaiActionConfigSource" /> that says who is responsible for running the
    ///     action commands a Convai Character receives, and the
    ///     <see cref="ConvaiActionConfigValidator" /> diagnostic it governs.
    /// </summary>
    /// <remarks>
    ///     The behavior under test exists because declaring actions and running them are separate
    ///     steps, and a character that does the first without the second fails completely silently.
    ///     The validator can only speak for the dispatcher mode — a custom handler is a runtime
    ///     subscription that does not exist at authoring time — so these tests pin both halves: the
    ///     error is raised when it is true, and is *not* raised when the user has declared another
    ///     arrangement.
    /// </remarks>
    [TestFixture]
    public class ConvaiActionExecutionModeTests
    {
        private const string NamePrefix = "ConvaiActionExecutionModeTests_";

        [OneTimeSetUp]
        public void OneTimeSetUp() => ConvaiLogger.Initialize();

        [OneTimeTearDown]
        public void OneTimeTearDown() => ConvaiLogger.ClearSinks();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject gameObject in Resources.FindObjectsOfTypeAll<GameObject>())
            {
                if (gameObject != null && gameObject.name.StartsWith(NamePrefix, System.StringComparison.Ordinal))
                    Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DefaultMode_IsConvaiActionDispatcher()
        {
            GameObject gameObject = CreateCharacterGameObject("default-mode");
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();

                Assert.That(
                    source.ActionExecutionMode,
                    Is.EqualTo(ConvaiActionExecutionMode.ConvaiActionDispatcher),
                    "The shipped dispatcher is the recommended path, so it must be what a freshly " +
                    "added component declares.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Validator_ReportsError_WhenDispatcherModeHasNoDispatcher()
        {
            GameObject gameObject = CreateCharacterGameObject("dispatcher-missing");
            try
            {
                ConvaiActionConfigSource source = AuthorOneRunnableAction(gameObject);

                IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics =
                    ConvaiActionConfigValidator.Validate(source);

                Assert.That(
                    diagnostics.Any(d =>
                        d.Severity == ConvaiActionConfigDiagnosticSeverity.Error &&
                        d.Message.Contains("no Convai Action Runner")),
                    Is.True,
                    "Without this the health readout says Ready while nothing runs — the exact " +
                    "silent failure this diagnostic exists to prevent.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Validator_ReportsNoDispatcherError_WhenDispatcherIsPresent()
        {
            GameObject gameObject = CreateCharacterGameObject("dispatcher-present");
            try
            {
                ConvaiActionConfigSource source = AuthorOneRunnableAction(gameObject);
                gameObject.AddComponent<ConvaiActionDispatcher>();

                Assert.That(HasMissingDispatcherError(source), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void DispatcherMode_UsesFriendlyInspectorName()
        {
            FieldInfo field = typeof(ConvaiActionExecutionMode).GetField(
                nameof(ConvaiActionExecutionMode.ConvaiActionDispatcher));
            var inspectorName = field?.GetCustomAttribute<InspectorNameAttribute>();

            Assert.That(inspectorName?.displayName, Is.EqualTo("Convai Action Runner"));
        }

        [Test]
        public void Validator_ReportsNoDispatcherError_WhenCustomCodeRunsTheActions()
        {
            GameObject gameObject = CreateCharacterGameObject("custom-code");
            try
            {
                ConvaiActionConfigSource source = AuthorOneRunnableAction(gameObject);
                source.SetActionExecutionMode(ConvaiActionExecutionMode.CustomCode);

                Assert.That(
                    HasMissingDispatcherError(source),
                    Is.False,
                    "A project handling ConvaiCharacter.OnActionsReceived itself is correctly set " +
                    "up without a dispatcher; flagging it would be a false alarm it cannot fix.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Validator_FindsDispatcherOnAChildObject()
        {
            GameObject gameObject = CreateCharacterGameObject("dispatcher-on-child");
            var child = new GameObject($"{NamePrefix}child");
            try
            {
                ConvaiActionConfigSource source = AuthorOneRunnableAction(gameObject);
                child.transform.SetParent(gameObject.transform);
                child.AddComponent<ConvaiCharacter>();
                child.AddComponent<ConvaiActionDispatcher>();

                Assert.That(
                    HasMissingDispatcherError(source),
                    Is.False,
                    "Every other Actions surface resolves the dispatcher with GetComponentInChildren; " +
                    "this check must agree with them or it contradicts the Troubleshooter.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void Validator_StillReportsOtherFindings_WhenCustomCodeRunsTheActions()
        {
            GameObject gameObject = CreateCharacterGameObject("custom-code-other-findings");
            try
            {
                ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
                source.SetActionExecutionMode(ConvaiActionExecutionMode.CustomCode);
                source.ReplaceDefinitions(new List<ConvaiActionDefinition>
                {
                    new() { ActionName = "Wave", ExecutorTypeHint = "ThisTypeDoesNotExistAnywhere12345" }
                });

                IReadOnlyList<ConvaiActionConfigDiagnostic> diagnostics =
                    ConvaiActionConfigValidator.Validate(source);

                Assert.That(
                    diagnostics.Any(d => d.Severity == ConvaiActionConfigDiagnosticSeverity.Error),
                    Is.True,
                    "Custom Code silences one specific check, not validation as a whole.");
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }

        private static bool HasMissingDispatcherError(ConvaiActionConfigSource source) =>
            ConvaiActionConfigValidator.Validate(source)
                .Any(d => d.Message.Contains("no Convai Action Runner"));

        /// <summary>
        ///     A source carrying one fully bound, runnable action, so the only thing a diagnostic can
        ///     legitimately complain about is who runs it.
        /// </summary>
        private static ConvaiActionConfigSource AuthorOneRunnableAction(GameObject gameObject)
        {
            ConvaiActionConfigSource source = gameObject.AddComponent<ConvaiActionConfigSource>();
            ExecutionModeTestExecutor executor = gameObject.AddComponent<ExecutionModeTestExecutor>();
            source.ReplaceDefinitions(new List<ConvaiActionDefinition>
            {
                new()
                {
                    ActionName = "Wave",
                    Description = "Waves at whoever is nearby.",
                    Executor = executor
                }
            });
            return source;
        }

        private static GameObject CreateCharacterGameObject(string suffix)
        {
            GameObject gameObject = new($"{NamePrefix}{suffix}");
            ConvaiCharacter character = gameObject.AddComponent<ConvaiCharacter>();
            SetPrivateField(character, "_characterId", $"char-{suffix}");
            SetPrivateField(character, "_characterName", suffix);
            return gameObject;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"Expected a private field '{fieldName}'.");
            field.SetValue(target, value);
        }
    }

    /// <summary>Minimal bound behavior so the fixture's actions are runnable, never run.</summary>
    internal sealed class ExecutionModeTestExecutor : ConvaiActionExecutorBase
    {
        public override System.Threading.Tasks.Task<ConvaiActionExecutionResult> ExecuteAsync(
            ConvaiActionInvocation invocation,
            System.Threading.CancellationToken cancellationToken) =>
            System.Threading.Tasks.Task.FromResult(ConvaiActionExecutionResult.Succeeded());
    }
}
