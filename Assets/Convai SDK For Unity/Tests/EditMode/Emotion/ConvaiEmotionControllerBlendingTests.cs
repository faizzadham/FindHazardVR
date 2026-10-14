using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using Convai.Tests.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Controller-level tests for emotion blending and its anti-flicker hysteresis: equivalence
    ///     (the hard gate, blending off by default), hysteresis anti-flicker, margin bypass,
    ///     cross-fade overlap on an accepted switch, taxonomy complement co-occurrence, zero-alloc
    ///     steady state, hysteresis reset on session reset, and same-region clamp safety.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerBlendingTests
    {
        private const string CharacterId = "blending-test-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerBlendingTests));
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

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            field.SetValue(target, value);
        }

        private static ConvaiEmotionProfile CreateBlendingProfile(
            bool enableBlending = true,
            float dwell = 0.35f,
            float margin = 0.15f,
            float complementScale = 0.35f,
            int maxSimultaneous = 2)
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "enableEmotionBlending", enableBlending);
            SetPrivateField(profile, "emotionSwitchDwell", dwell);
            SetPrivateField(profile, "emotionSwitchMargin", margin);
            SetPrivateField(profile, "complementBlendScale", complementScale);
            SetPrivateField(profile, "maxSimultaneousEmotions", maxSimultaneous);
            return profile;
        }

        // ── Legacy equivalence (hard gate) ──────────────────────────────────────────

        [Test]
        public void BlendingOff_LegacyEquivalence_OutputAndDominantMatchWinnerTakesAll()
        {
            // Arrange — two independent controllers, both with blending explicitly off, driven by
            // the identical scripted event+tick sequence. Set explicitly because the shipped
            // default now turns blending ON; this test covers the opted-out path.
            using EmbodimentTestRig legacyRig = EmbodimentTestRig.Create("legacy");
            legacyRig.Root.AddComponent<ConvaiManager>(); // Own the required editor setup for this secondary rig.
            ConvaiCharacter legacyCharacter = legacyRig.Root.AddComponent<ConvaiCharacter>();
            legacyCharacter.Configure(CharacterId, "Legacy Character");
            var legacyHarness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(legacyRig);

            ConvaiEmotionProfile profileA = ConvaiEmotionProfile.CreateDefault();
            ConvaiEmotionProfile profileB = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profileA, "enableEmotionBlending", false);
            SetPrivateField(profileB, "enableEmotionBlending", false);
            try
            {
                _harness.ApplyProfile(profileA);
                legacyHarness.ApplyProfile(profileB);

                void Drive(EmbodimentTestRig rig, EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> harness)
                {
                    rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2));
                    for (int i = 0; i < 20; i++) harness.Tick(0.05f);
                    rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3));
                    for (int i = 0; i < 20; i++) harness.Tick(0.05f);
                    rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "neutral", 1));
                    for (int i = 0; i < 20; i++) harness.Tick(0.05f);
                }

                // Act
                Drive(_rig, _harness);
                Drive(legacyRig, legacyHarness);

                // Assert — numerically identical output for every taxonomy label + dominant.
                foreach (System.Collections.Generic.KeyValuePair<string, float> kvp in legacyHarness.Controller.Current.AllScores)
                {
                    Assert.That(_harness.Controller.Current.AllScores[kvp.Key], Is.EqualTo(kvp.Value).Within(1e-6f),
                        $"Score for '{kvp.Key}' must be bit-identical with blending off (default).");
                }
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo(legacyHarness.Controller.Current.DominantLabel));
                Assert.That(_harness.Controller.Current.DominantScore, Is.EqualTo(legacyHarness.Controller.Current.DominantScore).Within(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(profileA);
                Object.DestroyImmediate(profileB);
            }
        }

        [Test]
        public void BlendingOff_NeutralClearPath_MatchesLegacyExactly()
        {
            // Arrange — blending opted out explicitly; the shipped default now turns it on.
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            SetPrivateField(profile, "enableEmotionBlending", false);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "sadness", 2));
                for (int i = 0; i < 20; i++) _harness.Tick(0.05f);

                // Act — neutral clear
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "neutral", 1));
                for (int i = 0; i < 120; i++) _harness.Tick(0.05f);

                // Assert
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("neutral"));
                Assert.That(_harness.Controller.Current.DominantScore, Is.LessThan(0.01f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Hysteresis anti-flicker ──────────────────────────────────────────────────

        [Test]
        public void BlendingOn_AlternatingSubMarginLabelsFasterThanDwell_PrimaryStaysOnFirst()
        {
            // Arrange — dwell 0.35s, margin 0.15. Alternate joy/anger at sub-dwell cadence with
            // scores close enough that neither exceeds the margin over the other.
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 0.35f, margin: 0.15f);
            try
            {
                _harness.ApplyProfile(profile);

                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2)); // ~0.67
                _harness.Tick(0.05f);
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("joy").Or.EqualTo("neutral"));

                // Act — rapid alternation, each gap well under the 0.35s dwell.
                for (int i = 0; i < 5; i++)
                {
                    _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 2)); // ~0.67, no margin over joy
                    _harness.Tick(0.05f);
                    _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2));
                    _harness.Tick(0.05f);
                }

                // Assert — primary must still be joy (the first accepted label); anger was always rejected.
                object primaryLabel = GetPrivateField(_harness.Controller, "_primaryLabel");
                Assert.That(primaryLabel, Is.EqualTo("joy"),
                    "Hysteresis must reject sub-margin, sub-dwell label flips (anti-flicker).");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void BlendingOn_AfterDwellElapses_NewLabelIsAccepted()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 0.2f, margin: 0.5f);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2));
                _harness.Tick(0.05f);

                // Act — wait past dwell, then a same-magnitude label should now be accepted.
                for (int i = 0; i < 10; i++) _harness.Tick(0.05f); // 0.5s elapsed > 0.2s dwell
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 2));
                _harness.Tick(0.05f);

                // Assert
                object primaryLabel = GetPrivateField(_harness.Controller, "_primaryLabel");
                Assert.That(primaryLabel, Is.EqualTo("anger"),
                    "Once the dwell has elapsed, a new label must be accepted as primary.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Rest-state acceptance (no active primary) ───────────────────────────────

        [Test]
        public void BlendingOn_FromRest_FirstWeakEvent_IsAcceptedImmediately()
        {
            // Arrange — long dwell + high margin so acceptance can ONLY happen via the rest-state
            // clause (no active primary), never via dwell elapsing or margin bypass.
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 2f, margin: 0.9f);
            try
            {
                _harness.ApplyProfile(profile);

                // Act — from a cold start (_primaryScore == 0), publish a weak event (well below
                // the 0.9 margin) and tick once (well under the 2s dwell).
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 1)); // ~0.33, weak
                _harness.Tick(0.05f);

                // Assert — accepted immediately; hysteresis must never block the first emotion.
                object primaryLabel = GetPrivateField(_harness.Controller, "_primaryLabel");
                Assert.That(primaryLabel, Is.EqualTo("joy"),
                    "A weak first emotion from rest must be accepted immediately, not rejected by hysteresis.");
                Assert.That(_harness.Controller.Current.DominantLabel, Is.EqualTo("joy"),
                    "The accepted emotion must drive the dominant/target output.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void BlendingOn_AfterNeutralClear_NextWeakEvent_IsAcceptedImmediately()
        {
            // Arrange — same long-dwell/high-margin setup, but this time drive an emotion first,
            // then clear to neutral (which resets _primaryScore to 0), then send a weak event.
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 2f, margin: 0.9f);
            try
            {
                _harness.ApplyProfile(profile);

                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3)); // strong
                _harness.Tick(0.05f);
                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("anger"));

                // Act — neutral clears the primary back to rest.
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "neutral", 1));
                _harness.Tick(0.05f);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 1)); // weak
                _harness.Tick(0.05f);

                // Assert
                object primaryLabel = GetPrivateField(_harness.Controller, "_primaryLabel");
                Assert.That(primaryLabel, Is.EqualTo("joy"),
                    "Post-neutral, a weak new emotion must be accepted immediately (rest state).");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Margin bypass ────────────────────────────────────────────────────────────

        [Test]
        public void BlendingOn_MuchStrongerLabel_BypassesDwellViaMargin()
        {
            // Arrange — long dwell so only the margin can cause acceptance.
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 2f, margin: 0.15f);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 1)); // ~0.33
                _harness.Tick(0.05f);

                // Act — immediately (dwell not elapsed) publish a much stronger label.
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3)); // ~1.0, margin exceeded
                _harness.Tick(0.05f);

                // Assert
                object primaryLabel = GetPrivateField(_harness.Controller, "_primaryLabel");
                Assert.That(primaryLabel, Is.EqualTo("anger"),
                    "A label whose score exceeds the primary by >= the margin must be accepted before dwell elapses.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Cross-fade preserved ─────────────────────────────────────────────────────

        [Test]
        public void BlendingOn_AcceptedSwitch_PreviousEmotionDecaysWhileNewOneRises_OverlapWindow()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 0f, margin: 0f);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
                for (int i = 0; i < 30; i++) _harness.Tick(0.05f);
                Assert.That(_harness.Controller.Current.AllScores["joy"], Is.GreaterThan(0.5f));

                // Act — switch to anger; sample a few ticks immediately after.
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3));
                _harness.Tick(0.05f);
                _harness.Tick(0.05f);

                // Assert — both non-zero during the overlap window (joy decaying, anger rising).
                Assert.That(_harness.Controller.Current.AllScores["joy"], Is.GreaterThan(0f),
                    "The previous emotion must still be decaying, not snap to zero.");
                Assert.That(_harness.Controller.Current.AllScores["anger"], Is.GreaterThan(0f),
                    "The new primary must be rising.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Complements co-occurrence ────────────────────────────────────────────────

        [Test]
        public void BlendingOn_PrimaryWithTaxonomyComplement_BothNonZero_ComplementScaledDown()
        {
            // Arrange — default taxonomy: joy.Complements = ["trust"].
            ConvaiEmotionProfile profile = CreateBlendingProfile(complementScale: 0.4f, maxSimultaneous: 2);
            try
            {
                _harness.ApplyProfile(profile);

                // Act
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3)); // normalized ~1.0
                for (int i = 0; i < 60; i++) _harness.Tick(0.05f);

                // Assert
                float joyScore = _harness.Controller.Current.AllScores["joy"];
                float trustScore = _harness.Controller.Current.AllScores["trust"];
                Assert.That(joyScore, Is.GreaterThan(0.5f));
                Assert.That(trustScore, Is.GreaterThan(0f), "Complement must be non-zero when the primary is active.");
                Assert.That(trustScore, Is.LessThan(joyScore), "Complement must be weaker than the primary.");
                Assert.That(trustScore, Is.EqualTo(joyScore * 0.4f).Within(0.15f),
                    "Complement should settle near score*complementBlendScale.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void BlendingOn_MaxSimultaneousEmotionsCapsContributors()
        {
            // Arrange — cap at 1: only the primary should ever be non-zero, no complement added.
            ConvaiEmotionProfile profile = CreateBlendingProfile(maxSimultaneous: 1);
            try
            {
                _harness.ApplyProfile(profile);

                // Act
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
                for (int i = 0; i < 60; i++) _harness.Tick(0.05f);

                // Assert
                Assert.That(_harness.Controller.Current.AllScores["joy"], Is.GreaterThan(0.5f));
                Assert.That(_harness.Controller.Current.AllScores["trust"], Is.EqualTo(0f).Within(1e-3f),
                    "With maxSimultaneousEmotions == 1, no complement may be added.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── Zero-alloc ───────────────────────────────────────────────────────────────

        [Test]
        public void BlendingOn_EventPlusTickSteadyState_AllocatesNothing()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateBlendingProfile();
            try
            {
                _harness.ApplyProfile(profile);

                void RunOnce(int i)
                {
                    string label = (i % 2 == 0) ? "joy" : "trust";
                    _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, label, 2));
                    _harness.Tick(0.05f);
                }

                // Warm up (JIT, lazy allocations).
                for (int i = 0; i < 200; i++) RunOnce(i);

                // Act — both measured batches must remain allocation-free.
                long allocationCount = Measure(200, RunOnce);

                // Assert
                Assert.That(allocationCount, Is.EqualTo(0L),
                    $"OnEmotionChanged blending path + EmbodimentTick must produce zero managed allocation events in steady state; measured {allocationCount} allocation events.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static long Measure(int iterations, System.Action<int> body)
        {
            long allocationCount = AllocationCounter.Measure(() =>
            {
                for (int i = 0; i < iterations; i++) body(i);
            });
            return allocationCount;
        }

        // ── Reset clears hysteresis state ───────────────────────────────────────────

        [Test]
        public void SessionReset_ClearsHysteresisState()
        {
            // Arrange
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 2f, margin: 0.9f);
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3));
                _harness.Tick(0.05f);
                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("anger"));

                // Act — simulate a disconnect.
                _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));

                // Assert — hysteresis state reset to neutral/zero so no stale dwell/margin survives.
                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("neutral"));
                Assert.That((float)GetPrivateField(_harness.Controller, "_primaryScore"), Is.EqualTo(0f));
                Assert.That((float)GetPrivateField(_harness.Controller, "_lastSwitchTime"), Is.EqualTo(0f));
                Assert.That((float)GetPrivateField(_harness.Controller, "_emotionClock"), Is.EqualTo(0f));

                // A weaker label must now be accepted immediately post-reset (no stale hysteresis).
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 1));
                _harness.Tick(0.05f);
                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("joy"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void SessionReset_WhileLocked_StillClearsHysteresisState()
        {
            // Arrange — lock the emotion (so the accumulator itself must NOT reset), then drive a
            // strong primary before disconnecting.
            ConvaiEmotionProfile profile = CreateBlendingProfile(dwell: 2f, margin: 0.9f);
            try
            {
                _harness.ApplyProfile(profile);
                SetPrivateField(_harness.Controller, "lockEmotion", true);

                // Note: lockEmotion short-circuits OnEmotionChanged, so drive the hysteresis state
                // directly the way the runtime would have before the lock was engaged.
                SetPrivateField(_harness.Controller, "_primaryLabel", "anger");
                SetPrivateField(_harness.Controller, "_primaryScore", 0.9f);
                SetPrivateField(_harness.Controller, "_lastSwitchTime", 0f);

                // Act — simulate a disconnect while still locked.
                _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));

                // Assert — hysteresis state is reset even though locked (Fix 2).
                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("neutral"));
                Assert.That((float)GetPrivateField(_harness.Controller, "_primaryScore"), Is.EqualTo(0f));

                // Act — unlock, then the next session's first (weak) event must be accepted
                // immediately, proving no stale hysteresis survived the locked reset.
                SetPrivateField(_harness.Controller, "lockEmotion", false);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 1));
                _harness.Tick(0.05f);

                Assert.That(GetPrivateField(_harness.Controller, "_primaryLabel"), Is.EqualTo("joy"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        private static object GetPrivateField(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName}.");
            return field.GetValue(target);
        }

        // ── Same-region clamp ───────────────────────────────────────────────────

        [Test]
        public void BlendingOn_TwoContributorsSameRegion_OutputStaysWithinUnitRange()
        {
            // Arrange — joy + its complement trust both drive blendshape output; verify neither
            // AllScores entry (the pre-compositor per-emotion score) nor a naive sum exceeds the
            // legal [0, 1] range the accumulator guarantees per label. Downstream region
            // composition (the semantic expression output) is out of scope here.
            ConvaiEmotionProfile profile = CreateBlendingProfile(complementScale: 1f, maxSimultaneous: 4);
            try
            {
                _harness.ApplyProfile(profile);

                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
                for (int i = 0; i < 60; i++) _harness.Tick(0.05f);

                // Assert — every per-label score (including the co-occurring complement) is clamped.
                foreach (System.Collections.Generic.KeyValuePair<string, float> kvp in _harness.Controller.Current.AllScores)
                {
                    Assert.That(kvp.Value, Is.GreaterThanOrEqualTo(0f).And.LessThanOrEqualTo(1f),
                        $"Score for '{kvp.Key}' must stay within [0, 1] even with a full-scale complement.");
                }
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }
    }
}
