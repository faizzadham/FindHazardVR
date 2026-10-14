using System.Collections.Generic;
using Convai.Domain.Emotion;
using Convai.Infrastructure.Networking.Audio;
using Convai.Runtime.Behaviors;
using Convai.Runtime.DynamicContext;
using Convai.Tests.EditMode.Mocks;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     Unit tests for RemoteAudioPreferenceManager.
    ///     Tests the three-strategy resolution in ShouldSubscribe and preference management.
    /// </summary>
    [TestFixture]
    public class RemoteAudioPreferenceManagerTests
    {
        [SetUp]
        public void SetUp()
        {
            _mockRegistry = new MockAgentRegistry();
            _manager = new RemoteAudioPreferenceManager(_mockRegistry);
        }

        private RemoteAudioPreferenceManager _manager;
        private MockAgentRegistry _mockRegistry;

        /// <summary>
        ///     Minimal mock implementation of IConvaiCharacterAgent for testing.
        /// </summary>
        private sealed class TestCharacterAgent : IConvaiCharacterAgent
        {
            public TestCharacterAgent(string characterId, string characterName = "Test Character")
            {
                CharacterId = characterId;
                CharacterName = characterName;
            }

            public string CharacterId { get; }
            public string CharacterName { get; }
            public Color NameTagColor => Color.white;
            public bool EnableSessionResume => false;
            public string InitialDynamicInfoText => string.Empty;
            public bool InitialDynamicInfoKeepInContext => false;
            public IConvaiDynamicContext DynamicContext { get; } = new MockDynamicContext();
            public EmotionDetectionMode EmotionDetectionMode => Convai.Domain.Emotion.EmotionDetectionMode.Off;
            public void SendTrigger(string triggerName) { }
            public void SendNarrativeEvent(string eventMessage) { }
            public void SendNarrativeSpeech(string speechText) { }
            public void UpdateTemplateKeys(Dictionary<string, string> templateKeys) { }
        }

        [Test]
        public void IsRemoteAudioEnabled_NullCharacterId_ReturnsFalse() =>
            Assert.IsFalse(_manager.IsRemoteAudioEnabled(null));

        [Test]
        public void IsRemoteAudioEnabled_EmptyCharacterId_ReturnsFalse() =>
            Assert.IsFalse(_manager.IsRemoteAudioEnabled(string.Empty));

        [Test]
        public void IsRemoteAudioEnabled_UnknownCharacter_ReturnsFalse() =>
            Assert.IsFalse(_manager.IsRemoteAudioEnabled("unknown-char"));

        [Test]
        public void IsRemoteAudioEnabled_AfterSetEnabled_ReturnsTrue()
        {
            _manager.SetRemoteAudioEnabled("char-1", true);
            Assert.IsTrue(_manager.IsRemoteAudioEnabled("char-1"));
        }

        [Test]
        public void IsRemoteAudioEnabled_AfterSetDisabled_ReturnsFalse()
        {
            _manager.SetRemoteAudioEnabled("char-1", true);
            _manager.SetRemoteAudioEnabled("char-1", false);
            Assert.IsFalse(_manager.IsRemoteAudioEnabled("char-1"));
        }

        [Test]
        public void ShouldSubscribe_NullIdentity_ReturnsFalse() => Assert.IsFalse(_manager.ShouldSubscribe(null));

        [Test]
        public void ShouldSubscribe_EmptyIdentity_ReturnsFalse() =>
            Assert.IsFalse(_manager.ShouldSubscribe(string.Empty));

        [Test]
        public void ShouldSubscribe_IdentityMatchesRegisteredCharacter_UsesCharacterPreference()
        {
            // Register a character with char-123 as the ID
            var agent = new TestCharacterAgent("char-123");
            _mockRegistry.RegisterCharacter(agent);

            _manager.SetRemoteAudioEnabled("char-123", false);
            // When looking up by character ID, it should use the preference
            Assert.IsFalse(_manager.ShouldSubscribe("char-123"));

            _manager.SetRemoteAudioEnabled("char-123", true);
            Assert.IsTrue(_manager.ShouldSubscribe("char-123"));
        }

        [Test]
        public void ShouldSubscribe_IdentityIsCharacterId_UsesDirectPreference()
        {
            _manager.SetRemoteAudioEnabled("char-direct", true);
            Assert.IsTrue(_manager.ShouldSubscribe("char-direct"));
        }

        [Test]
        public void ShouldSubscribe_UnknownIdentity_NoPreferences_ReturnsTrue() =>
            Assert.IsTrue(_manager.ShouldSubscribe("ConvAI-Bot"));

        [Test]
        public void ShouldSubscribe_UnknownIdentity_AnyCharacterEnabled_ReturnsTrue()
        {
            _manager.SetRemoteAudioEnabled("char-1", false);
            _manager.SetRemoteAudioEnabled("char-2", true);

            Assert.IsTrue(_manager.ShouldSubscribe("ConvAI-Bot"));
        }

        [Test]
        public void ShouldSubscribe_UnknownIdentity_AllCharactersDisabled_ReturnsFalse()
        {
            _manager.SetRemoteAudioEnabled("char-1", false);
            _manager.SetRemoteAudioEnabled("char-2", false);

            Assert.IsFalse(_manager.ShouldSubscribe("ConvAI-Bot"));
        }

        [Test]
        public void SetRemoteAudioEnabled_RaisesEvent_OnChange()
        {
            string eventCharId = null;
            bool eventEnabled = false;
            _manager.RemoteAudioEnabledChanged += (charId, enabled) =>
            {
                eventCharId = charId;
                eventEnabled = enabled;
            };

            _manager.SetRemoteAudioEnabled("char-1", true);

            Assert.AreEqual("char-1", eventCharId);
            Assert.IsTrue(eventEnabled);
        }

        [Test]
        public void SetRemoteAudioEnabled_DoesNotRaiseEvent_WhenNoChange()
        {
            _manager.SetRemoteAudioEnabled("char-1", true);

            int eventCount = 0;
            _manager.RemoteAudioEnabledChanged += (_, _) => eventCount++;

            _manager.SetRemoteAudioEnabled("char-1", true);

            Assert.AreEqual(0, eventCount);
        }
    }
}
