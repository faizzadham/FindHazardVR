using System.Reflection;
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
    ///     Component-level tests for the resolved emotion and mood gameplay events on
    ///     <see cref="ConvaiEmotionController" />: <see cref="ConvaiEmotionController.DominantEmotionChanged" />/
    ///     <see cref="ConvaiEmotionController.MoodChanged" /> fire exactly once per label transition,
    ///     never on the pipeline's first composed reading, carry scores consistent with
    ///     <see cref="ConvaiEmotionController.Current" /> at fire time, and a throwing subscriber
    ///     cannot break the tick or block other subscribers. Mirrors the rig/setup of
    ///     <see cref="ConvaiEmotionControllerMoodApiTests" />.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerEventsTests
    {
        private const string CharacterId = "events-test-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerEventsTests));
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

        // ── DominantEmotionChanged transitions ──────────────────────────

        [Test]
        public void DominantEmotionChanged_FiresOncePerTransition_NotOnFirstTickOrPersistence()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);

                int fireCount = 0;
                string lastLabel = null;
                float lastScore = 0f;
                float lastControllerScoreAtFire = 0f;
                _harness.Controller.DominantEmotionChanged += (label, score) =>
                {
                    fireCount++;
                    lastLabel = label;
                    lastScore = score;
                    // Snapshot Current INSIDE the handler — Current is already updated
                    // before the event fires, and further ticks after this point keep
                    // moving the score, so comparing post-loop would be comparing against a
                    // different (later) tick's value.
                    lastControllerScoreAtFire = _harness.Controller.CurrentNormalizedIntensity;
                };

                // The very first composed reading must not fire, even though it transitions
                // bookkeeping from "not yet observed" to "neutral".
                _harness.Tick(1f / 60f);
                Assert.That(fireCount, Is.EqualTo(0), "The first-ever composed reading must not fire.");

                // Drive a real transition and tick until the dominant label settles.
                _harness.Controller.SetEmotionOverride("joy", 0.8f);
                for (int i = 0; i < 240; i++)
                    _harness.Tick(1f / 60f);

                Assert.That(fireCount, Is.EqualTo(1));
                Assert.That(lastLabel, Is.EqualTo("joy"));
                Assert.That(lastScore, Is.EqualTo(lastControllerScoreAtFire).Within(1e-4f),
                    "The score passed with the event must match the reading at fire time.");

                // Further ticks with the same settled label must add nothing.
                for (int i = 0; i < 60; i++)
                    _harness.Tick(1f / 60f);
                Assert.That(fireCount, Is.EqualTo(1));

                // Switching to a second emotion fires again with the new label.
                _harness.Controller.SetEmotionOverride("anger", 0.9f);
                for (int i = 0; i < 240; i++)
                    _harness.Tick(1f / 60f);

                Assert.That(fireCount, Is.EqualTo(2));
                Assert.That(lastLabel, Is.EqualTo("anger"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── MoodChanged transitions ─────────────────────────────────────────

        [Test]
        public void MoodChanged_FiresOnSetMoodAndClearMood_NotOnScoreOnlyEvolution()
        {
            ConvaiEmotionProfile profile = CreateProfileWithBaseline("sadness", 0.3f);
            try
            {
                _harness.ApplyProfile(profile);

                int fireCount = 0;
                string lastLabel = null;
                float lastScore = 0f;
                float lastControllerScoreAtFire = 0f;
                _harness.Controller.MoodChanged += (label, score) =>
                {
                    fireCount++;
                    lastLabel = label;
                    lastScore = score;
                    // Snapshot Current INSIDE the handler — see the equivalent comment on
                    // the DominantEmotionChanged test for why a post-loop comparison is wrong.
                    lastControllerScoreAtFire = _harness.Controller.CurrentMoodScore;
                };

                // First composed reading seeds bookkeeping (to the authored "sadness" baseline)
                // without firing, even though it is a non-neutral starting value.
                _harness.Tick(1f / 60f);
                Assert.That(fireCount, Is.EqualTo(0));
                Assert.That(_harness.Controller.CurrentMoodLabel, Is.EqualTo("sadness"));

                // SetMood: neutral/baseline -> joy.
                _harness.Controller.SetMood("joy", 0.7f, transitionSeconds: 0.2f);
                for (int i = 0; i < 60; i++)
                    _harness.Tick(0.05f);

                Assert.That(fireCount, Is.EqualTo(1));
                Assert.That(lastLabel, Is.EqualTo("joy"));
                Assert.That(lastScore, Is.EqualTo(lastControllerScoreAtFire).Within(1e-4f));

                // Continue ticking while the label stays "joy" but the smoothed score keeps
                // moving toward/holding its target — no extra fires (label-change only).
                for (int i = 0; i < 40; i++)
                    _harness.Tick(0.05f);
                Assert.That(fireCount, Is.EqualTo(1));

                // ClearMood: back to the authored baseline ("sadness").
                _harness.Controller.ClearMood(0.2f);
                for (int i = 0; i < 80; i++)
                    _harness.Tick(0.05f);

                Assert.That(fireCount, Is.EqualTo(2));
                Assert.That(lastLabel, Is.EqualTo("sadness"));
                Assert.That(lastScore, Is.EqualTo(lastControllerScoreAtFire).Within(1e-4f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── guarded dispatch ────────────────────────────────────────────────

        [Test]
        public void DominantEmotionChanged_ThrowingSubscriber_DoesNotBreakTickOrOtherSubscribers()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Tick(1f / 60f); // seed bookkeeping

                int wellBehavedCount = 0;
                _harness.Controller.DominantEmotionChanged += (_, _) => throw new System.InvalidOperationException("boom");
                _harness.Controller.DominantEmotionChanged += (_, _) => wellBehavedCount++;

                _harness.Controller.SetEmotionOverride("joy", 0.8f);

                Assert.DoesNotThrow(() =>
                {
                    for (int i = 0; i < 240; i++)
                        _harness.Tick(1f / 60f);
                });

                Assert.That(wellBehavedCount, Is.EqualTo(1),
                    "A well-behaved subscriber after a throwing one must still receive the event.");
                Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"),
                    "The tick itself must keep running normally despite the throwing subscriber.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
