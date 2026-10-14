using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;
using Convai.Domain.Embodiment.Interfaces;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     E6 character-to-character mutual gaze: registry lifecycle, self-exclusion,
    ///     speaker-vs-idle relevance, distance culling, the lookAtOthers gate, the eye-line
    ///     fallback gaze point, and head-anchor resolution across a rig rebind.
    /// </summary>
    /// <remarks>
    ///     Unity does not invoke <c>OnEnable</c> for plain MonoBehaviours in edit mode, so
    ///     these tests drive the provider's internal <c>HandleEnable</c>/<c>HandleDisable</c>
    ///     seams — the exact bodies the play-mode lifecycle runs.
    /// </remarks>
    public sealed class CharacterGazeTargetProviderTests
    {
        private readonly List<GameObject> _spawned = new();

        [SetUp]
        public void SetUp() => ConvaiCharacterGazeRegistry.Clear();

        [TearDown]
        public void TearDown()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
            ConvaiCharacterGazeRegistry.Clear();
        }

        // ── Registry lifecycle ───────────────────────────────────────────────

        [Test]
        public void Registry_DoubleRegister_AddsOnce_ThenUnregisterRemoves()
        {
            var entry = new ConvaiCharacterGazeRegistry.Entry();

            ConvaiCharacterGazeRegistry.Register(entry);
            ConvaiCharacterGazeRegistry.Register(entry);
            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(1),
                "Registering the same entry twice must add it only once.");

            ConvaiCharacterGazeRegistry.Unregister(entry);
            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(0));
        }

        [Test]
        public void PublishSelf_Registers_AndProviderNeverEmitsItself()
        {
            GameObject character = NewCharacter("Self", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(character);

            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(1),
                "publishSelf must register the character as a lookable target.");

            ConvaiCharacterGazeRegistry.Entry ownEntry = ConvaiCharacterGazeRegistry.All[0];
            Assert.IsFalse(provider.TryBuildCandidate(character.transform, ownEntry, out _),
                "A provider must never emit a gaze candidate for its own entry.");
        }

        [Test]
        public void Disable_UnregistersFromRegistry()
        {
            GameObject character = NewCharacter("Transient", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(character);
            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(1));

            provider.HandleDisable();

            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(0),
                "Disabling the provider must remove the character from the registry.");
        }

        // ── Candidate relevance ──────────────────────────────────────────────

        [Test]
        public void SpeakingOther_OutranksIdleOther_AtEqualDistance()
        {
            GameObject observer = NewCharacter("Observer", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(observer);

            ConvaiCharacterGazeRegistry.Entry speaker =
                BuildEntry("Speaker", new Vector3(0f, 0f, 6f), DialogueState.Speaking);
            ConvaiCharacterGazeRegistry.Entry idle =
                BuildEntry("Idle", new Vector3(0f, 0f, 6f), DialogueState.Idle);

            Assert.IsTrue(provider.TryBuildCandidate(observer.transform, speaker, out GazeTargetCandidate speaking));
            Assert.IsTrue(provider.TryBuildCandidate(observer.transform, idle, out GazeTargetCandidate notSpeaking));

            Assert.That(speaking.Kind, Is.EqualTo(GazeTargetKind.Character));
            Assert.That(speaking.Relevance, Is.GreaterThan(notSpeaking.Relevance),
                "A speaking character is fully relevant (listeners turn to it); an idle one is not.");
        }

        [Test]
        public void BeyondMaxDistance_EmitsNoCandidate()
        {
            GameObject observer = NewCharacter("Observer", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(observer);

            ConvaiCharacterGazeRegistry.Entry farOther =
                BuildEntry("Far", new Vector3(0f, 0f, 40f), DialogueState.Speaking);

            Assert.IsFalse(provider.TryBuildCandidate(observer.transform, farOther, out _),
                "A character beyond maxDistance (12 m) is not a candidate.");
        }

        [Test]
        public void LookAtOthersDisabled_EmitsNothing_ButStillPublishes()
        {
            GameObject character = NewCharacter("Publisher", Vector3.zero);
            CharacterGazeTargetProvider provider = character.AddComponent<CharacterGazeTargetProvider>();
            SetPrivateBool(provider, "lookAtOthers", false);
            provider.HandleEnable();

            Assert.That(ConvaiCharacterGazeRegistry.All.Count, Is.EqualTo(1),
                "publishSelf still registers even when lookAtOthers is off.");

            ConvaiCharacterGazeRegistry.Entry other =
                BuildEntry("Other", new Vector3(0f, 0f, 3f), DialogueState.Speaking);
            Assert.IsFalse(provider.TryBuildCandidate(character.transform, other, out _),
                "A provider with lookAtOthers off must not emit candidates.");
        }

        // ── Gaze point ───────────────────────────────────────────────────────

        [Test]
        public void RootFallbackAnchor_LiftsGazePointToEyeLine()
        {
            GameObject observer = NewCharacter("Observer", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(observer);

            // No head bone resolvable (no rig binding on the test character), so the entry
            // stays on the root fallback — the candidate must aim at the eye line, not the feet.
            ConvaiCharacterGazeRegistry.Entry other =
                BuildEntry("Headless", new Vector3(0f, 0f, 3f), DialogueState.Idle);

            Assert.IsTrue(provider.TryBuildCandidate(observer.transform, other, out GazeTargetCandidate candidate));
            Assert.That(candidate.WorldPoint.y, Is.EqualTo(other.EyeLineOffset).Within(0.001f),
                "The root-fallback gaze point is lifted to the eye line so characters do not stare at feet.");
        }

        [Test]
        public void HeadAnchor_FallsBackToRoot_AndSurvivesRebind()
        {
            GameObject character = NewCharacter("Camila", Vector3.zero);
            CharacterGazeTargetProvider provider = AddEnabledProvider(character);
            ConvaiCharacterGazeRegistry.Entry entry = ConvaiCharacterGazeRegistry.All[0];

            Assert.IsNotNull(entry.Root);
            Assert.AreEqual(entry.Root, entry.HeadAnchor,
                "With no head bone the anchor falls back to the character root.");

            Assert.IsTrue(EmbodimentContext.TryResolve(provider, out EmbodimentContext context));
            context.NotifyRigBindingChanged();

            Assert.AreEqual(entry.Root, entry.HeadAnchor,
                "A rebind re-resolves the anchor (still root here) without breaking the entry.");
            Assert.That(entry.DisplayName, Is.EqualTo(entry.Root.name));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private GameObject NewCharacter(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            go.AddComponent<ConvaiCharacter>();
            _spawned.Add(go);
            return go;
        }

        private static CharacterGazeTargetProvider AddEnabledProvider(GameObject character)
        {
            var provider = character.AddComponent<CharacterGazeTargetProvider>();
            provider.HandleEnable();
            return provider;
        }

        private ConvaiCharacterGazeRegistry.Entry BuildEntry(string name, Vector3 position, DialogueState state)
        {
            GameObject go = NewCharacter(name, position);
            Assert.IsTrue(EmbodimentContext.TryResolve(go.GetComponent<ConvaiCharacter>(), out EmbodimentContext context));

            var flow = new FakeConversationFlowSource();
            context.Provide<IConversationFlowSource>(flow);
            flow.SetState(state);

            return new ConvaiCharacterGazeRegistry.Entry
            {
                Context = context,
                Root = go.transform,
                HeadAnchor = go.transform,
                DisplayName = name
            };
        }

        private static void SetPrivateBool(object target, string field, bool value) =>
            target.GetType()
                .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(target, value);
    }
}
