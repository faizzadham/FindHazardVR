using System;
using System.Collections;
using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Domain.EventSystem;
using Convai.Modules.BodyLanguage.Components;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using Convai.Modules.BodyLanguage.Data;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.PlayMode.BodyLanguage
{
    /// <summary>
    ///     Live PlayMode coverage for the anticipatory inhale: the
    ///     Domain-event subscription seam the EditMode POCO layer cannot reach. Publishing
    ///     <see cref="CharacterAudioPlaybackStateChanged" />.<c>Started()</c> for THIS character
    ///     must arm <see cref="BreathEventKind.InhaleBeforeSpeaking" /> on the controller's
    ///     breathing director; a mismatched character id must not.
    /// </summary>
    /// <remarks>
    ///     PlayMode (not EditMode) for the same reason as <c>ScriptedApiControllerGlueTests</c>:
    ///     the controller only ticks/subscribes while <c>Application.isPlaying</c>. Mirrors that
    ///     file's rig, plus a real <see cref="EventHub" /> (populated on the
    ///     <see cref="EmbodimentContext" /> — the seam <see cref="ConvaiBodyLanguageController" />
    ///     reads via <c>Context.EventHub</c>) and a <see cref="ConvaiCharacter" /> configured
    ///     with a known id so <c>MatchesCharacter</c> has something to match against.
    /// </remarks>
    public sealed class AnticipatoryInhalePlayModeTests
    {
        private const string CharacterId = "anticipatory-inhale-char";
        private const int FrameBudget = 20;

        private GameObject _root;
        private ConvaiBodyLanguageController _controller;
        private EmbodimentContext _context;
        private EventHub _eventHub;

        [SetUp]
        public void SetUp()
        {
            // ConvaiCharacter.Awake() logs a one-time [Error] when no ConvaiManager exists in the
            // scene (expected here — this is a bare rig, mirrors the same suppression other
            // bare-ConvaiCharacter PlayMode/EditMode rigs use, e.g. ScriptedApiControllerGlueTests'
            // inert-rig test).
            LogAssert.ignoreFailingMessages = true;

            _root = new GameObject("AnticipatoryInhaleRoot");

            Transform spine = NewChild(_root.transform, "Spine", new Vector3(0f, 1f, 0f));
            Transform chest = NewChild(spine, "Chest", new Vector3(0f, 0.15f, 0f));
            Transform upperChest = NewChild(chest, "UpperChest", new Vector3(0f, 0.15f, 0f));
            Transform neck = NewChild(upperChest, "Neck", new Vector3(0f, 0.1f, 0f));
            NewChild(neck, "Head", new Vector3(0f, 0.1f, 0f));

            _root.AddComponent<Animator>();
            _context = _root.AddComponent<EmbodimentContext>();

            _eventHub = new EventHub(new ImmediateScheduler());
            _context.Populate(_eventHub, null);

            ConvaiCharacter character = _root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");

            var profile = ConvaiBodyLanguageProfile.CreateDefault();
            SetPrivateField(profile, "postureTargetSlewSeconds", 0.01f);
            SetPrivateField(profile, "postureFadeSeconds", 0.01f);
            SetPrivateField(profile, "policyTransitionSeconds", 0f);
            SetPrivateField(profile, "enableInhaleBeforeSpeaking", true);

            _controller = _root.AddComponent<ConvaiBodyLanguageController>();
            SetPrivateField(_controller, "profile", profile);
            _controller.enabled = false;
            _controller.enabled = true;
        }

        [TearDown]
        public void TearDown()
        {
            LogAssert.ignoreFailingMessages = false;
            if (_root != null) Object.DestroyImmediate(_root);
        }

        [UnityTest]
        public IEnumerator AudioPlaybackStarted_ForThisCharacter_ArmsInhaleBeforeSpeaking()
        {
            for (int i = 0; i < 3; i++) yield return null;

            Assert.That(_controller.ActiveBreathEvent, Is.Not.EqualTo(BreathEventKind.InhaleBeforeSpeaking),
                "Sanity: the inhale must not already be active before the event is published.");

            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started(CharacterId));

            bool sawInhale = false;
            for (int i = 0; i < FrameBudget && !sawInhale; i++)
            {
                yield return null;
                if (_controller.ActiveBreathEvent == BreathEventKind.InhaleBeforeSpeaking) sawInhale = true;
            }

            Assert.IsTrue(sawInhale,
                "Publishing CharacterAudioPlaybackStateChanged.Started() for this character must arm the anticipatory inhale.");
        }

        [UnityTest]
        public IEnumerator AudioPlaybackStarted_ForADifferentCharacter_IsIgnored()
        {
            for (int i = 0; i < 3; i++) yield return null;

            _eventHub.Publish(CharacterAudioPlaybackStateChanged.Started("some-other-character"));

            for (int i = 0; i < FrameBudget; i++) yield return null;

            Assert.That(_controller.ActiveBreathEvent, Is.Not.EqualTo(BreathEventKind.InhaleBeforeSpeaking),
                "An audio-playback-started event for a DIFFERENT character must not arm this character's inhale.");
        }

        private static Transform NewChild(Transform parent, string name, Vector3 localPosition)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go.transform;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.NotNull(field, $"Missing field {fieldName} on {target.GetType().Name}.");
            field.SetValue(target, value);
        }

        /// <summary>Runs every scheduled action synchronously on Publish — no frame-pump needed to observe delivery.</summary>
        private sealed class ImmediateScheduler : IUnityScheduler
        {
            public void ScheduleOnMainThread(Action action) => action?.Invoke();
            public void ScheduleOnBackground(Action action) => action?.Invoke();
            public bool IsMainThread() => true;
        }
    }
}
