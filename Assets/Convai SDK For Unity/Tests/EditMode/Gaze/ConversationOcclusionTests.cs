using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Conversation;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Line-of-sight gating between characters: the measurement itself (real colliders, real
    ///     rays) and the gate it feeds in <see cref="CharacterGazeTargetProvider" />, which is
    ///     what keeps a character on the far side of a wall out of the arbiter entirely.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The switch is <c>playerLineOfSight</c> ("Won't Look Through Walls") on the profile;
    ///         the controller reads it and decides whether to hand the set to the provider at all.
    ///         These cases stand in that controller's place — a populated set is the switch on, a
    ///         set nobody handed over is the switch off — because the raycasting and the gate are
    ///         the parts that can be wrong.
    ///     </para>
    ///     <para>
    ///         Everything is built far from the origin so colliders left standing by another
    ///         edit-mode fixture cannot cross these rays, and <c>Physics.SyncTransforms</c> is
    ///         called after every move: nothing simulates in edit mode, so a query reads whatever
    ///         the physics scene was last told.
    ///     </para>
    /// </remarks>
    public sealed class ConversationOcclusionTests
    {
        /// <summary>The observing character. Its eyes are the ray origin.</summary>
        private static readonly Vector3 ObserverPosition = new(600f, 0f, 600f);

        /// <summary>The other character, six metres in front of it.</summary>
        private static readonly Vector3 OtherPosition = new(600f, 0f, 606f);

        /// <summary>Eye line both characters are reported to the room at.</summary>
        private const float EyeLine = 1.6f;

        private readonly List<GameObject> _spawned = new();
        private ConversationRoomModel _room;
        private ConversationOcclusionSet _occlusion;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            ConvaiCharacterGazeRegistry.Clear();
            _room = new ConversationRoomModel();
            _occlusion = new ConversationOcclusionSet();
            _frame = 0;
        }

        [TearDown]
        public void TearDown()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
                if (_spawned[i] != null)
                    Object.DestroyImmediate(_spawned[i]);

            _spawned.Clear();
            Physics.SyncTransforms();
            ConvaiCharacterGazeRegistry.Clear();
        }

        // ── The measurement ──────────────────────────────────────────────────

        /// <summary>
        ///     The whole feature in one case: a wall between two characters is measured, the
        ///     provider refuses the candidate while the switch is on, and offers it while the
        ///     switch is off. The last assertion is what makes the first two mean anything — the
        ///     candidate has to be there to be withheld.
        /// </summary>
        [Test]
        public void AWallBetweenTwoCharacters_WithholdsTheCandidate_OnlyWhileTheCheckIsOn()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            NewCharacter("Other", OtherPosition);
            NewWall(new Vector3(600f, EyeLine, 603f));

            ConvaiCharacterGazeRegistry.Entry other = EntryNamed("Other");
            Measure(observer);

            Assert.IsTrue(_occlusion.IsOccluded(other.Key),
                "A wall stands between the two of them and the measurement did not see it.");
            Assert.That(_occlusion.Count, Is.EqualTo(1), "Only the other character is hidden.");

            Assert.IsFalse(
                observer.TryBuildCandidate(ObserverRoot(observer), other, out _, 0, _occlusion),
                "\"Won't Look Through Walls\" is on: a character behind a wall must not reach the arbiter.");

            Assert.IsTrue(
                observer.TryBuildCandidate(ObserverRoot(observer), other, out GazeTargetCandidate candidate),
                "With the check off (no set handed over) the same character is an ordinary candidate.");
            Assert.That(candidate.Kind, Is.EqualTo(GazeTargetKind.Character));
        }

        /// <summary>
        ///     The negative control: same room, same distance, no wall. Without this the case
        ///     above would pass just as well if the measurement hid everybody unconditionally.
        /// </summary>
        [Test]
        public void WithNothingBetweenThem_NobodyIsHidden()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            NewCharacter("Other", OtherPosition);

            ConvaiCharacterGazeRegistry.Entry other = EntryNamed("Other");
            Measure(observer);

            Assert.That(_occlusion.Count, Is.Zero, "Nothing stands between them, so nothing is occluded.");
            Assert.IsTrue(observer.TryBuildCandidate(ObserverRoot(observer), other, out _, 0, _occlusion));
        }

        /// <summary>
        ///     Pins the exclusion the measurement depends on: the ray ends inside the other
        ///     character, so their own body would report a hit on every participant in the room
        ///     and hide the entire cast from each other.
        /// </summary>
        [Test]
        public void TheOtherCharactersOwnBody_IsNotAWall()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            GameObject other = NewCharacter("Other", OtherPosition).gameObject;

            // A body around the head the ray is aimed at — the collider a character actually has.
            BoxCollider body = other.AddComponent<BoxCollider>();
            body.center = new Vector3(0f, EyeLine * 0.5f, 0f);
            body.size = new Vector3(0.6f, EyeLine * 1.2f, 0.6f);
            Physics.SyncTransforms();

            Measure(observer);

            Assert.That(_occlusion.Count, Is.Zero,
                "A character occluded itself: the target's own hierarchy must be excluded from the ray.");
        }

        /// <summary>
        ///     Pins that the gate lifts of its own accord. Occlusion is a measurement, not a
        ///     latch — the wall coming down has to bring the character back without anything
        ///     being reset.
        /// </summary>
        [Test]
        public void WhenTheWallIsRemoved_TheNextMeasurementClearsIt()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            NewCharacter("Other", OtherPosition);
            GameObject wall = NewWall(new Vector3(600f, EyeLine, 603f));

            ConvaiCharacterGazeRegistry.Entry other = EntryNamed("Other");
            Measure(observer);
            Assert.IsTrue(_occlusion.IsOccluded(other.Key), "Premise: the wall was measured.");

            Object.DestroyImmediate(wall);
            _spawned.Remove(wall);
            Physics.SyncTransforms();

            Measure(observer);

            Assert.IsFalse(_occlusion.IsOccluded(other.Key), "The wall is gone and the character is still hidden.");
            Assert.IsTrue(observer.TryBuildCandidate(ObserverRoot(observer), other, out _, 0, _occlusion));
        }

        /// <summary>
        ///     Pins the throttle: the measurement is a raycast per other character and must not
        ///     run on every cognition tick. A tick shorter than the interval leaves the previous
        ///     answer standing.
        /// </summary>
        [Test]
        public void TheMeasurementIsThrottled_AndHoldsItsPreviousAnswerInBetween()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            NewCharacter("Other", OtherPosition);
            GameObject wall = NewWall(new Vector3(600f, EyeLine, 603f));

            ConvaiCharacterGazeRegistry.Entry other = EntryNamed("Other");
            Assert.IsTrue(Measure(observer), "The first measurement runs.");
            Assert.IsTrue(_occlusion.IsOccluded(other.Key));

            Object.DestroyImmediate(wall);
            _spawned.Remove(wall);
            Physics.SyncTransforms();

            Assert.IsFalse(Measure(observer, deltaTime: 1f / 60f),
                "A sixtieth of a second is inside the tenth-of-a-second interval — no rays this tick.");
            Assert.IsTrue(_occlusion.IsOccluded(other.Key),
                "Between measurements the last answer stands rather than being cleared.");
        }

        /// <summary>
        ///     Pins the conservative rule: a participant the registry has never heard of has no
        ///     transform to exclude, so the ray would end inside a body nothing told us about.
        ///     Such a person is left visible rather than guessed at.
        /// </summary>
        [Test]
        public void AParticipantThatPublishesNoTarget_IsNeverGuessedToBeHidden()
        {
            CharacterGazeTargetProvider observer = NewCharacter("Observer", ObserverPosition);
            NewWall(new Vector3(600f, EyeLine, 603f));

            const int strangerKey = 4242;
            _room.ReportParticipant(
                EntryNamed("Observer").Key, ObserverPosition + Vector3.up * EyeLine, Vector3.forward,
                false, 0f, "Observer");
            _room.ReportParticipant(
                strangerKey, OtherPosition + Vector3.up * EyeLine, Vector3.back, true, 0.5f, "Stranger");
            _room.Refresh(1f, 1, ConversationRoomTuning.Default);

            _occlusion.Refresh(
                _room.Current, EntryNamed("Observer").Key, ObserverPosition + Vector3.up * EyeLine,
                ObserverRoot(observer), Physics.DefaultRaycastLayers, 0.1f, 1f, 0f);

            Assert.That(_occlusion.Count, Is.Zero,
                "Nothing could exclude the stranger's own body from the ray, so no verdict is invented.");
        }

        // ── Fixture ──────────────────────────────────────────────────────────

        /// <summary>
        ///     A character that both publishes itself as a gaze target and looks at others.
        ///     Unity does not run <c>OnEnable</c> in edit mode, so the provider's own lifecycle
        ///     seam is driven directly — the same body play mode runs.
        /// </summary>
        private CharacterGazeTargetProvider NewCharacter(string name, Vector3 position)
        {
            var go = new GameObject(name);
            go.transform.position = position;
            go.AddComponent<ConvaiCharacter>();
            _spawned.Add(go);

            var provider = go.AddComponent<CharacterGazeTargetProvider>();
            provider.HandleEnable();
            return provider;
        }

        /// <summary>A wall wide and tall enough that no ray between two eye lines misses it.</summary>
        private GameObject NewWall(Vector3 position)
        {
            var wall = GameObject.CreatePrimitive(PrimitiveType.Cube);
            wall.name = "Wall";
            wall.transform.position = position;
            wall.transform.localScale = new Vector3(6f, 6f, 0.2f);
            _spawned.Add(wall);
            Physics.SyncTransforms();
            return wall;
        }

        private static ConvaiCharacterGazeRegistry.Entry EntryNamed(string name)
        {
            IReadOnlyList<ConvaiCharacterGazeRegistry.Entry> all = ConvaiCharacterGazeRegistry.All;
            for (int i = 0; i < all.Count; i++)
                if (all[i] != null && all[i].DisplayName == name)
                    return all[i];

            Assert.Fail($"No registered gaze target named '{name}'.");
            return null;
        }

        private static Transform ObserverRoot(CharacterGazeTargetProvider observer) => observer.transform;

        /// <summary>
        ///     Reports every registered character to the room at its own eye line, derives the
        ///     room, and runs one occlusion measurement from the observer's eyes — exactly what
        ///     <c>ConvaiGazeController</c> does once per cognition tick.
        /// </summary>
        /// <param name="observer">The character doing the looking.</param>
        /// <param name="deltaTime">Seconds since its last tick; the default outruns the throttle.</param>
        /// <returns>Whether the rays were cast.</returns>
        private bool Measure(CharacterGazeTargetProvider observer, float deltaTime = 1f)
        {
            IReadOnlyList<ConvaiCharacterGazeRegistry.Entry> all = ConvaiCharacterGazeRegistry.All;
            for (int i = 0; i < all.Count; i++)
            {
                ConvaiCharacterGazeRegistry.Entry entry = all[i];
                if (entry?.Root == null) continue;

                _room.ReportParticipant(
                    entry.Key, entry.Root.position + Vector3.up * EyeLine, entry.Root.forward,
                    false, 0f, entry.DisplayName);
            }

            _room.Refresh(_frame + 1f, ++_frame, ConversationRoomTuning.Default);

            Transform root = ObserverRoot(observer);
            return _occlusion.Refresh(
                _room.Current,
                EntryNamed(observer.name).Key,
                root.position + Vector3.up * EyeLine,
                root,
                Physics.DefaultRaycastLayers,
                0.1f,
                deltaTime,
                phase: 0f);
        }
    }
}
