using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.DomainEvents.Session;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Component-level tests for the runtime mood API on <see cref="ConvaiEmotionController" />:
    ///     <see cref="ConvaiEmotionController.SetMood" />/<see cref="ConvaiEmotionController.ClearMood" />
    ///     transitions, the per-character Initial Mood override winning over the profile's Persona
    ///     Baseline at build, unknown-label handling, session-reset discard semantics, and the
    ///     safe-no-op-before-build guarantee. Mirrors the rig/setup of
    ///     <see cref="ConvaiEmotionControllerPersonaBaselineTests" />.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerMoodApiTests
    {
        private const string CharacterId = "mood-api-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerMoodApiTests));
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");
            _harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        private static ConvaiEmotionProfile CreateProfileWithBaseline(string label, float intensity)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "baselineEmotionLabel", label);
            SetPrivateField(profile, "baselineIntensity", intensity);
            return profile;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            return field.GetValue(target);
        }

        [Test]
        public void SetMood_TicksTowardTarget_DominantStaysNeutral()
        {
            // Arrange
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);

                // Act
                _harness.Controller.SetMood("joy", 0.7f, transitionSeconds: 0.5f);
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                // Assert
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("joy"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0.7f).Within(0.05f));
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("neutral"),
                    "A runtime mood must never surface as the dominant (transient) emotion.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ClearMood_ReturnsToProfileBaseline()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateProfileWithBaseline("sadness", 0.3f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetMood("joy", 0.8f, transitionSeconds: 0.2f);
                for (int i = 0; i < 40; i++)
                    _harness.Tick(0.05f);
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("joy"));

                // Act
                _harness.Controller.ClearMood(0.2f);
                for (int i = 0; i < 80; i++)
                    _harness.Tick(0.05f);

                // Assert — back to the profile's authored Persona Baseline, not zero.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("sadness"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0.3f).Within(0.05f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SetMood_UnknownLabel_WarnsOnce_AndTreatsAsNoMood()
        {
            // Arrange
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetMood("joy", 0.6f, transitionSeconds: 0.1f);
                for (int i = 0; i < 20; i++)
                    _harness.Tick(0.05f);
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("joy"));

                // Act — an unrecognized label twice; must warn (bookkeeping) only once and clear.
                _harness.Controller.SetMood("bogus-emotion", 0.9f, transitionSeconds: 0.1f);
                _harness.Controller.SetMood("bogus-emotion", 0.9f, transitionSeconds: 0.1f);
                for (int i = 0; i < 40; i++)
                    _harness.Tick(0.05f);

                // Assert
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0f).Within(0.02f),
                    "An unknown label must be treated as 'no mood'.");

                var warnedLabels = (HashSet<string>)GetPrivateField(_harness.Controller, "_warnedUnknownLabels");
                Assert.That(warnedLabels.Contains("bogus-emotion"), Is.True,
                    "The unknown label must be recorded exactly once in the warn-once set.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void InitialMoodOverride_WinsOverProfileBaseline_AtBuild()
        {
            // Arrange — per-character override configured before the profile (with a DIFFERENT
            // authored baseline) is applied/built.
            SetPrivateField(_harness.Controller, "initialMoodLabel", "anger");
            SetPrivateField(_harness.Controller, "initialMoodIntensity", 0.4f);

            ConvaiEmotionProfile profile = CreateProfileWithBaseline("sadness", 0.6f);
            try
            {
                // Act
                _harness.ApplyProfile(profile);
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                // Assert — the override wins.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("anger"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0.4f).Within(0.05f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ForcedNeutralInitialMood_SuppressesProfileBaseline_AtBuild()
        {
            // Arrange — an Initial Mood label that resolves to the taxonomy's neutral entry
            // now FORCES a truly neutral rest instead of falling through to the profile's Persona
            // Baseline, rather than falling through to it as an empty field would.
            SetPrivateField(_harness.Controller, "initialMoodLabel", "neutral");
            SetPrivateField(_harness.Controller, "initialMoodIntensity", 0.4f);

            ConvaiEmotionProfile profile = CreateProfileWithBaseline("joy", 0.5f);
            try
            {
                // Act
                _harness.ApplyProfile(profile);
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                // Assert — forced neutral wins; the profile's joy baseline is suppressed, and the
                // stored intensity (0.4) is ignored because forced neutral is always 0.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("neutral"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0f).Within(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ForcedNeutralInitialMood_ClearMood_ReturnsToForcedNeutral_NotProfileBaseline()
        {
            // Arrange
            SetPrivateField(_harness.Controller, "initialMoodLabel", "neutral");

            ConvaiEmotionProfile profile = CreateProfileWithBaseline("joy", 0.5f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetMood("sadness", 0.6f, transitionSeconds: 0.1f);
                for (int i = 0; i < 20; i++)
                    _harness.Tick(0.05f);
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("sadness"));

                // Act
                _harness.Controller.ClearMood(0.1f);
                for (int i = 0; i < 40; i++)
                    _harness.Tick(0.05f);

                // Assert — back to the forced-neutral rest, not the profile's joy baseline.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("neutral"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0f).Within(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ForcedNeutralInitialMood_SessionDisconnect_SnapsToForcedNeutral()
        {
            // Arrange
            SetPrivateField(_harness.Controller, "initialMoodLabel", "neutral");

            ConvaiEmotionProfile profile = CreateProfileWithBaseline("joy", 0.5f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetMood("sadness", 0.6f, transitionSeconds: 0.1f);
                for (int i = 0; i < 20; i++)
                    _harness.Tick(0.05f);
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("sadness"));

                // Act
                _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));
                _harness.Tick(1f / 60f);

                // Assert — snapped to the forced-neutral rest, not the profile's joy baseline.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("neutral"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0f).Within(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionDisconnect_DiscardsRuntimeMood_SnapsToAuthoredBaseline()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateProfileWithBaseline("sadness", 0.25f);
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetMood("joy", 0.8f, transitionSeconds: 0.1f);
                for (int i = 0; i < 40; i++)
                    _harness.Tick(0.05f);
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("joy"));

                // Act
                _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));
                _harness.Tick(1f / 60f);

                // Assert — snapped back to the authored baseline, not merely decaying.
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("sadness"));
                Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0.25f).Within(0.02f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SetMood_BeforePipelineBuild_IsSafeNoOp()
        {
            // Arrange — tear down the pipeline (accumulator == null), mirroring the
            // "not built yet" state.
            _harness.Disable();

            // Act / Assert
            Assert.DoesNotThrow(() => _harness.Controller.SetMood("joy", 0.5f));
            Assert.DoesNotThrow(() => _harness.Controller.ClearMood());

            Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("neutral"));
            Assert.That(_harness.Controller.CurrentMoodScore, Is.EqualTo(0f));
        }
    }
}
