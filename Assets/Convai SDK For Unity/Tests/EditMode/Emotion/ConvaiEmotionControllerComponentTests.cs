using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.Logging;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Outputs;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime;
using Convai.Runtime.Components;
using Convai.Runtime.Animation;
using Convai.Runtime.Logging;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Component-level tests for <see cref="ConvaiEmotionController" /> complementing
    ///     <see cref="ConvaiEmotionControllerInvariantsTests" />.  Focus: dual-slot registration
    ///     (emotion state + mouth provider), initial state, and tick null-safety.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerComponentTests
    {
        private const string CharacterId = "test-char-id";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        private LogLevel _originalGlobalLevel;
        private LogLevelOverride[] _originalCategoryOverrides;
        private ConvaiSettings _settings;

        [SetUp]
        public void SetUp()
        {
            // Verbosity is project state, so it is forced here and restored below rather than
            // assumed. Without it ConvaiLogger emits nothing, and a fixture that fails on
            // unexpected logs while the logger is muted is not actually watching anything.
            _settings = ConvaiSettings.Instance;
            if (_settings != null)
            {
                _originalGlobalLevel = _settings.GlobalLogLevel;
                _originalCategoryOverrides = CloneOverrides(_settings.CategoryOverrides);
                _settings.SetGlobalLogLevel(LogLevel.Trace);
                _settings.SetCategoryOverrides(System.Array.Empty<LogLevelOverride>());
                LoggingConfig.InvalidateCache();
            }

            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerComponentTests));

            // No log is expected here, and that was measured rather than assumed. Adding a
            // character to a scene with no ConvaiManager is meant to say so, but neither route to
            // that diagnostic runs in EditMode: the error comes from ValidateSDKSetup, which only
            // Awake calls, and the warning from ValidateEditorSetup, which only OnValidate calls —
            // and adding a component from code runs neither. The fixture inherited from the pin
            // silenced every message instead, which passes for the wrong reason. What is left is
            // the half that does work: TearDown fails on any log this fixture did not expect.
            //
            // Proving the character reports a missing manager needs Awake, so it belongs in a
            // PlayMode test rather than an [ExecuteAlways] added to a shipped component to make an
            // edit-mode test pass.
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");
            _harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // Any log this fixture did not expect fails the test that produced it. This is the
            // whole log guard now, and it is a real one: the pin replaced it with a blanket
            // ignore, under which these fifteen tests passed without watching anything.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();

            if (_settings == null) return;
            _settings.SetGlobalLogLevel(_originalGlobalLevel);
            _settings.SetCategoryOverrides(CloneOverrides(_originalCategoryOverrides));
            LoggingConfig.InvalidateCache();
        }

        private static LogLevelOverride[] CloneOverrides(System.Collections.Generic.IReadOnlyList<LogLevelOverride> source)
        {
            if (source == null) return System.Array.Empty<LogLevelOverride>();
            var copy = new LogLevelOverride[source.Count];
            for (int i = 0; i < copy.Length; i++) copy[i] = source[i];
            return copy;
        }

        // ── Slot registration ──────────────────────────────────────────────────

        [Test]
        public void OnEnable_EmotionStateSlot_IsSet()
        {
            IEmotionStateSource slot = _rig.Context.EmotionStateSource;

            Assert.That(slot, Is.Not.Null);
            Assert.That(slot, Is.SameAs(_harness.Controller));
        }

        [Test]
        public void OnEnable_EmotionMouthSlot_IsSet()
        {
            // The controller always registers as mouth provider even without a
            // blendshape binding (returns zero weight in that case).
            IEmotionMouthWeightProvider mouthSlot = _rig.Context.EmotionMouthProvider;

            Assert.That(mouthSlot, Is.Not.Null);
            Assert.That(mouthSlot, Is.SameAs(_harness.Controller));
        }

        [Test]
        public void OnDisable_EmotionStateSlot_IsCleared()
        {
            _harness.Disable();

            Assert.That(_rig.Context.EmotionStateSource, Is.Null);
        }

        [Test]
        public void OnDisable_EmotionMouthSlot_IsCleared()
        {
            _harness.Disable();

            Assert.That(_rig.Context.EmotionMouthProvider, Is.Null);
        }

        [Test]
        public void ReenableAfterDisable_BothSlots_AreReregistered()
        {
            _harness.Disable();
            _harness.Enable();

            Assert.That(_rig.Context.EmotionStateSource, Is.SameAs(_harness.Controller));
            Assert.That(_rig.Context.EmotionMouthProvider, Is.SameAs(_harness.Controller));
        }

        // ── Current-state semantics ────────────────────────────────────────────

        [Test]
        public void Current_AfterEnable_IsDominantNeutral()
        {
            // Without server events the accumulator settles on neutral.
            EmotionReading reading = _harness.Controller.Current;

            Assert.That(reading.DominantLabel,
                Is.EqualTo(EmotionReading.Neutral.DominantLabel),
                "Default dominant emotion must be 'neutral' before any server events.");
        }

        [Test]
        public void Current_AfterTicks_IsDominantNeutral()
        {
            _harness.Tick(0f);
            for (int i = 0; i < 100; i++) _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.DominantLabel,
                Is.EqualTo(EmotionReading.Neutral.DominantLabel));
        }

        [Test]
        public void ScoreAccess_UsesSourceReading_NotStaleSnapshot()
        {
            _harness.Controller.LockEmotion("joy", 0.75f);
            _harness.Tick(1f / 60f);

            IEmotionStateSource source = _harness.Controller;
            var destination = new Dictionary<string, float> { ["stale"] = 1f };

            float score = source.Current.GetScore("joy");
            source.Current.CopyScoresTo(destination);

            Assert.That(score, Is.EqualTo(0.75f).Within(1e-6f));
            Assert.That(destination.ContainsKey("stale"), Is.False);
            Assert.That(destination["joy"], Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void CurrentResolvedState_UsesComposedReading()
        {
            _harness.Controller.LockEmotion("joy", 0.75f);
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));
            Assert.That(_harness.Controller.CurrentNormalizedIntensity, Is.EqualTo(0.75f).Within(1e-6f));
        }

        [Test]
        public void UnknownServerEmotion_FallsBackToNeutral()
        {
            // The warning routes through Context.Logger (Convai.Domain.Logging.ILogger), which
            // this fixture leaves unset (EmbodimentTestRig.Populate(eventHub, logger: null)), so
            // no Debug.Log* is expected here; only the fallback-to-neutral behavior is observable.
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "confused-but-not-taxonomy", 2));

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }

        // ── Tick safety ────────────────────────────────────────────────────────

        // ── Profile application ────────────────────────────────────────────────

        // ── a profile with ONLY material-property slots still counts as output ──

        [Test]
        public void ApplyProfile_OnlyMaterialSlotsAuthored_RegistersBinding_DoesNotWarn()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                profile.MaterialBinding.SetSlots(new[]
                {
                    new MaterialPropertyEmotionSlot("joy", "_EmotionBlush", 0f, 1f)
                });

                _harness.ApplyProfile(profile);

                FieldInfo warnedField = typeof(ConvaiEmotionController).GetField(
                    "_warnedAboutNoAuthoredSlots", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(warnedField, "ConvaiEmotionController must declare _warnedAboutNoAuthoredSlots.");
                Assert.That(warnedField.GetValue(_harness.Controller), Is.EqualTo(false),
                    "A profile with only material-property slots must count as output and must not trigger " +
                    "the no-facial-output warning.");

                FieldInfo activeBindingsField = typeof(ConvaiEmotionController).GetField(
                    "_activeBindings", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.NotNull(activeBindingsField, "ConvaiEmotionController must declare _activeBindings.");
                var activeBindings = (List<IEmotionOutputBinding>)activeBindingsField.GetValue(_harness.Controller);
                Assert.That(activeBindings, Has.Some.InstanceOf<MaterialPropertyEmotionBinding>(),
                    "The authored material-property binding must be registered as an active binding.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
            }
        }
    }
}
