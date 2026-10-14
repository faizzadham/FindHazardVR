using System;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.DomainEvents.Session;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Component-level tests for <see cref="ConvaiEmotionController" /> lock/override
    ///     semantics, session-state-driven accumulator reset, and character-id scoping.
    ///     Complements <see cref="ConvaiEmotionControllerComponentTests" />.
    /// </summary>
    [TestFixture]
    public sealed class ConvaiEmotionControllerLockOverrideSessionTests
    {
        private const string CharacterId = "test-char-id";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(ConvaiEmotionControllerLockOverrideSessionTests));
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

        // ── LockEmotion / UnlockEmotion ────────────────────────────────────────

        [Test]
        public void LockEmotion_IgnoresSubsequentServerEvents_ExpressesLockedEmotion()
        {
            _harness.Controller.LockEmotion("anger", 0.9f);
            _harness.Tick(1f / 60f);

            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 10));
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("anger"));
            Assert.That(_harness.Controller.CurrentNormalizedIntensity, Is.EqualTo(0.9f).Within(1e-4f));
        }

        [Test]
        public void UnlockEmotion_RestoresNormalEventHandling()
        {
            _harness.Controller.LockEmotion("anger", 0.9f);
            _harness.Tick(1f / 60f);

            _harness.Controller.UnlockEmotion();
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 10));

            // Multiple ticks so the accumulator lerps toward the new (unlocked) target.
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));
        }

        // ── SetEmotionOverride / ClearEmotionOverride ──────────────────────────

        [Test]
        public void SetEmotionOverride_DrivesCurrentTowardOverrideTarget()
        {
            _harness.Controller.SetEmotionOverride("sadness", 0.6f);

            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("sadness"));
            Assert.That(_harness.Controller.CurrentNormalizedIntensity, Is.EqualTo(0.6f).Within(0.05f));
        }

        [Test]
        public void SetEmotionOverride_IgnoresBackendUntilExplicitlyCleared()
        {
            _harness.Controller.SetEmotionOverride("sadness", 0.6f);
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));

            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("sadness"));

            _harness.Controller.ClearEmotionOverride();
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));
        }

        [Test]
        public void EmotionEvent_OlderTimestamp_CannotRewindAcceptedState()
        {
            DateTime now = DateTime.UtcNow;
            _rig.EventHub.Publish(new CharacterEmotionChanged(CharacterId, "joy", 3, now));
            _rig.EventHub.Publish(new CharacterEmotionChanged(CharacterId, "anger", 3, now.AddSeconds(-1)));

            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));
        }

        [Test]
        public void ClearEmotionOverride_RestoresNeutral()
        {
            _harness.Controller.SetEmotionOverride("sadness", 0.6f);
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            _harness.Controller.ClearEmotionOverride();
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }

        // ── OnSessionStateChanged ───────────────────────────────────────────────

        [Test]
        public void SessionDisconnected_ResetsAccumulator_BackToNeutral()
        {
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 10));
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);
            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));

            _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }

        [Test]
        public void SessionError_ResetsAccumulator_BackToNeutral()
        {
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 10));
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);
            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));

            _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Error, "session-1"));
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }

        [Test]
        public void SessionReconnecting_DoesNotResetAccumulator()
        {
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 10));
            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);
            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"));

            _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Reconnecting, "session-1"));
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"),
                "Reconnecting is not Disconnected/Error, so the accumulator must not reset.");
        }

        [Test]
        public void SessionDisconnected_WhileLocked_DoesNotReset()
        {
            _harness.Controller.LockEmotion("anger", 0.9f);
            _harness.Tick(1f / 60f);

            _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));
            _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("anger"),
                "Per current code, session reset is skipped while lockEmotion is active.");
        }

        [Test]
        public void SessionDisconnected_WhileGameplayOverrideActive_PreservesOverrideContract()
        {
            _harness.Controller.SetEmotionOverride("sadness", 0.6f);
            _rig.EventHub.Publish(SessionStateChanged.Create(SessionState.Connected, SessionState.Disconnected, "session-1"));

            for (int i = 0; i < 240; i++)
                _harness.Tick(1f / 60f);

            _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
            for (int i = 0; i < 60; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("sadness"));
        }

        // ── MatchesCharacter ──────────────────────────────────────────────────

        [Test]
        public void EmotionEvent_ForDifferentCharacterId_IsIgnored()
        {
            _rig.EventHub.Publish(CharacterEmotionChanged.Create("some-other-character", "joy", 10));
            for (int i = 0; i < 60; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }

        [Test]
        public void EmotionEvent_UnscopedCharacterId_IsIgnored()
        {
            // WarnUnscopedEmotionEventOnce routes through Context.Logger (Convai.Domain.Logging.ILogger),
            // which this fixture leaves unset (EmbodimentTestRig.Populate(eventHub, logger: null)), so
            // no Debug.Log* is expected here; only the ignore behavior is observable in EditMode.
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(string.Empty, "joy", 10));
            _rig.EventHub.Publish(CharacterEmotionChanged.Create(string.Empty, "anger", 10));

            for (int i = 0; i < 60; i++)
                _harness.Tick(1f / 60f);

            Assert.That(_harness.Controller.Current.IsNeutral, Is.True);
        }
    }
}
