#nullable enable
using System.Collections.Generic;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode
{
    /// <summary>
    ///     Tests for behavior extension system using CharacterBehaviorDispatcher.
    /// </summary>
    public class BehaviorExtensionTests
    {
        private readonly List<GameObject> _createdObjects = new();

        [TearDown]
        public void TearDown()
        {
            foreach (GameObject go in _createdObjects)
                if (go != null)
                    Object.DestroyImmediate(go);

        }

        [Test]
        public void CharacterBehaviors_AreDiscoveredAndSortedByPriority()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            go.AddComponent<ConvaiCharacter>();

            var lowPriority = go.AddComponent<RecordingCharacterBehavior>();
            lowPriority.Configure("low", 10, null, false);

            var highPriority = go.AddComponent<RecordingCharacterBehavior>();
            highPriority.Configure("high", 100, null, false);

            var medPriority = go.AddComponent<RecordingCharacterBehavior>();
            medPriority.Configure("med", 50, null, false);

            var dispatcher = go.AddComponent<CharacterBehaviorDispatcher>();
            dispatcher.DiscoverBehaviors();

            Assert.AreEqual(3, dispatcher.BehaviorCount, "Should discover 3 behaviors");

            IReadOnlyList<IConvaiCharacterBehavior> behaviors = dispatcher.Behaviors;
            Assert.AreEqual("high", ((RecordingCharacterBehavior)behaviors[0]).Identifier);
            Assert.AreEqual("med", ((RecordingCharacterBehavior)behaviors[1]).Identifier);
            Assert.AreEqual("low", ((RecordingCharacterBehavior)behaviors[2]).Identifier);
        }

        [Test]
        public void CharacterBehavior_TranscriptInterception_ShortCircuitsFallback()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            go.AddComponent<ConvaiCharacter>();

            List<string> invocationLog = new();

            var interceptor = go.AddComponent<RecordingCharacterBehavior>();
            interceptor.Configure("interceptor", 100, invocationLog, true);

            var fallback = go.AddComponent<RecordingCharacterBehavior>();
            fallback.Configure("fallback", 10, invocationLog, false);

            var dispatcher = go.AddComponent<CharacterBehaviorDispatcher>();
            dispatcher.DiscoverBehaviors();

            Assert.AreEqual(2, dispatcher.BehaviorCount, "Should discover 2 behaviors");

            IReadOnlyList<IConvaiCharacterBehavior> behaviors = dispatcher.Behaviors;
            Assert.AreEqual("interceptor", ((RecordingCharacterBehavior)behaviors[0]).Identifier);
            Assert.AreEqual("fallback", ((RecordingCharacterBehavior)behaviors[1]).Identifier);
        }

        [Test]
        public void CharacterBehaviorDispatcher_RequiresConvaiCharacter()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            go.AddComponent<ConvaiCharacter>();
            var dispatcher = go.AddComponent<CharacterBehaviorDispatcher>();

            Assert.IsNotNull(dispatcher);
            Assert.IsTrue(dispatcher.enabled);
        }

        [Test]
        public void CharacterBehaviorDispatcher_DiscoversBehaviorsOnSameGameObject()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            go.AddComponent<ConvaiCharacter>();

            var behavior1 = go.AddComponent<RecordingCharacterBehavior>();
            behavior1.Configure("behavior1", 50, null, false);

            var behavior2 = go.AddComponent<RecordingCharacterBehavior>();
            behavior2.Configure("behavior2", 75, null, false);

            var dispatcher = go.AddComponent<CharacterBehaviorDispatcher>();
            dispatcher.DiscoverBehaviors();

            Assert.AreEqual(2, dispatcher.BehaviorCount);
        }

        [Test]
        public void ConvaiCharacter_ImplementsIConvaiCharacterAgent()
        {
            var go = new GameObject("TestCharacter");
            _createdObjects.Add(go);

            var character = go.AddComponent<ConvaiCharacter>();

            Assert.IsTrue(character is IConvaiCharacterAgent,
                "ConvaiCharacter should implement IConvaiCharacterAgent interface");
        }

    }

    public sealed class RecordingCharacterBehavior : ConvaiCharacterBehaviorBase
    {
        private int _priority;
        public string Identifier { get; private set; } = string.Empty;
        public List<string>? InvocationLog { get; private set; }
        public bool InterceptTranscripts { get; private set; }
        public bool InterceptSpeechEvents { get; private set; }
        public int TranscriptInvocationCount { get; private set; }
        public int SpeechStartedInvocationCount { get; private set; }
        public int SpeechStoppedInvocationCount { get; private set; }
        public int CharacterInitializedCount { get; private set; }
        public int CharacterShutdownCount { get; private set; }
        public int CharacterReadyCount { get; private set; }

        public override int Priority => _priority;

        public void Configure(string identifier, int priority, List<string>? invocationLog, bool interceptTranscripts,
            bool interceptSpeech = false)
        {
            Identifier = identifier;
            _priority = priority;
            InvocationLog = invocationLog;
            InterceptTranscripts = interceptTranscripts;
            InterceptSpeechEvents = interceptSpeech;
        }

        public override void OnCharacterInitialized(IConvaiCharacterAgent agent)
        {
            CharacterInitializedCount++;
            InvocationLog?.Add($"{Identifier}:initialized");
        }

        public override void OnCharacterShutdown(IConvaiCharacterAgent agent)
        {
            CharacterShutdownCount++;
            InvocationLog?.Add($"{Identifier}:shutdown");
        }

        public override bool OnTranscriptReceived(IConvaiCharacterAgent agent, string transcript, bool isFinal)
        {
            TranscriptInvocationCount++;
            InvocationLog?.Add($"{Identifier}:transcript");
            return InterceptTranscripts;
        }

        public override bool OnSpeechStarted(IConvaiCharacterAgent agent)
        {
            SpeechStartedInvocationCount++;
            InvocationLog?.Add($"{Identifier}:speechStarted");
            return InterceptSpeechEvents;
        }

        public override bool OnSpeechStopped(IConvaiCharacterAgent agent)
        {
            SpeechStoppedInvocationCount++;
            InvocationLog?.Add($"{Identifier}:speechStopped");
            return InterceptSpeechEvents;
        }

        public override void OnCharacterReady(IConvaiCharacterAgent agent)
        {
            CharacterReadyCount++;
            InvocationLog?.Add($"{Identifier}:ready");
        }
    }
}
