using System;
using System.Reflection;
using Convai.Domain.DomainEvents.Transcript;
using Convai.Modules.BodyAnimation.Core.Lifecycle;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="SpokenLineRelay" />, extracted from
    ///     <see cref="ConvaiBodyAnimationController" />'s spoken-line feed: both transcript
    ///     subscriptions (direct character event and the event-hub fallback), the pending slot and
    ///     its threading contract, and the identity-mismatch/interim-result filters.
    /// </summary>
    public sealed class SpokenLineRelayTests
    {
        private static ConvaiCharacter CreateCharacter(out GameObject root, string characterId)
        {
            root = new GameObject("SpokenLineRelayTestCharacter");
            var character = root.AddComponent<ConvaiCharacter>();
            var serialized = new SerializedObject(character);
            serialized.FindProperty("_characterId").stringValue = characterId;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            return character;
        }

        /// <summary>
        ///     Raises <see cref="ConvaiCharacter.OnTranscriptReceived" /> directly via its
        ///     compiler-generated backing field — the event can only appear on the left of
        ///     +=/-= from outside the declaring type, and in production it is only ever raised
        ///     internally off a routed <c>CharacterTtsTextChunk</c> domain event, which is more
        ///     than this relay-focused suite needs to stand up.
        /// </summary>
        private static void RaiseTranscriptReceived(ConvaiCharacter character, string text, bool isFinal)
        {
            FieldInfo field = typeof(ConvaiCharacter).GetField(
                "OnTranscriptReceived", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "ConvaiCharacter.OnTranscriptReceived must be a standard field-backed event.");
            var handler = (Action<string, bool>)field.GetValue(character);
            handler?.Invoke(text, isFinal);
        }

        [Test]
        public void TryConsumePending_NothingQueued_ReturnsFalse()
        {
            var relay = new SpokenLineRelay();
            Assert.IsFalse(relay.TryConsumePending(out string text));
            Assert.IsNull(text);
        }

        [Test]
        public void CharacterEvent_FinalTranscript_IsConsumedOnce()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(null);

                RaiseTranscriptReceived(character, "hello there", true);

                Assert.IsTrue(relay.TryConsumePending(out string text));
                Assert.AreEqual("hello there", text);

                // Single slot: a second consume without a new line finds nothing.
                Assert.IsFalse(relay.TryConsumePending(out string second));
                Assert.IsNull(second);
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void CharacterEvent_InterimResult_IsNotQueued()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(null);

                RaiseTranscriptReceived(character, "still talking", false);

                Assert.IsFalse(relay.TryConsumePending(out _));
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EventHub_FinalTranscript_MatchingCharacterId_IsQueued()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var hub = new FakeEventHub();
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(hub);

                hub.Publish(CharacterTranscriptReceived.Create("char-1", "Display", "from the hub", true));

                Assert.IsTrue(relay.TryConsumePending(out string text));
                Assert.AreEqual("from the hub", text);
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EventHub_UnspokenFinalTranscript_IsNotQueuedForCoSpeech()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var hub = new FakeEventHub();
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(hub);
                CharacterTranscriptReceived spokenShape = CharacterTranscriptReceived.Create(
                    "char-1", "Display", "quiet output", true);

                hub.Publish(new CharacterTranscriptReceived(
                    spokenShape.Message,
                    isSpoken: false));

                Assert.IsFalse(relay.TryConsumePending(out _),
                    "Quiet bot output must not drive referential gestures or co-speech timing.");
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void EventHub_MismatchedCharacterId_IsIgnored()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var hub = new FakeEventHub();
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(hub);

                hub.Publish(CharacterTranscriptReceived.Create("some-other-character", "Display", "not mine", true));

                Assert.IsFalse(relay.TryConsumePending(out _));
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void Detach_ClearsPendingAndStopsFurtherQueuing()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);
                relay.Attach(null);
                RaiseTranscriptReceived(character, "about to be dropped", true);

                relay.Detach();

                Assert.IsFalse(relay.TryConsumePending(out _),
                    "Detach must drop any not-yet-consumed pending line.");

                // The character event is unsubscribed — raising it post-detach must not queue.
                RaiseTranscriptReceived(character, "after detach", true);
                Assert.IsFalse(relay.TryConsumePending(out _));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void SetCharacter_RecordsCharacter_WithoutSubscribing()
        {
            ConvaiCharacter character = CreateCharacter(out GameObject root, "char-1");
            var relay = new SpokenLineRelay();
            try
            {
                relay.SetCharacter(character);

                Assert.AreSame(character, relay.Character);

                // Not yet attached — the character event must not be wired up.
                RaiseTranscriptReceived(character, "too early", true);
                Assert.IsFalse(relay.TryConsumePending(out _));
            }
            finally
            {
                relay.Detach();
                Object.DestroyImmediate(root);
            }
        }
    }
}
