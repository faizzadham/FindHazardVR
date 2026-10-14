using System.Collections.Generic;
using Convai.Modules.Gaze.Components;
using Convai.Modules.Gaze.Core.Conversation;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Conversation gaze: who a character looks at while somebody else is talking, and what
    ///     it does in the gaps between turns.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Every case is driven through a real <see cref="ConversationRoomModel" /> with a
    ///         fake clock, never through a hand-made snapshot. The timing chain — a speech edge
    ///         becoming an onset, an onset becoming a floor, a floor becoming an expectation about
    ///         who answers — is most of what this director reasons about, and a fixture that
    ///         asserted against a snapshot somebody typed by hand would be asserting against a
    ///         second implementation of it.
    ///     </para>
    ///     <para>
    ///         Each case says which rule it pins, so a mutation to that rule turns exactly one
    ///         case red. The cases marked <i>ported</i> came from the retired
    ///         <c>SpeakerAttentionDirectorTests</c> and <c>SpeakerAttentionLingerTests</c>: the
    ///         behaviour they describe still has to hold, even though nothing about how it is
    ///         produced survived.
    ///     </para>
    /// </remarks>
    public sealed class ConversationGazeDirectorTests
    {
        private const float Dt = 1f / 60f;

        /// <summary>The listener under test.</summary>
        private const int SelfKey = 11;

        /// <summary>The character that does the talking in most cases.</summary>
        private const int SpeakerKey = 77;

        /// <summary>A third character: the one the player addresses, or the one it switches to.</summary>
        private const int OtherKey = 91;

        private ConversationRoomModel _room;
        private ConversationRoomTuning _roomTuning;
        private float _now;
        private int _frame;

        [SetUp]
        public void SetUp()
        {
            // A private instance rather than the shared one: two suites running in the same domain
            // must not be able to see each other's rooms.
            _room = new ConversationRoomModel();
            _roomTuning = ConversationRoomTuning.Default;
            _now = 0f;
            _frame = 0;
        }

        // ── Harness ──────────────────────────────────────────────────────────

        /// <summary>Where each participant stands. Distinct directions, so a yaw means something.</summary>
        private static Vector3 PointOf(int key) => key switch
        {
            SelfKey => new Vector3(0f, 1.6f, 0f),
            SpeakerKey => new Vector3(2f, 1.6f, 2f),
            OtherKey => new Vector3(-2f, 1.6f, 2f),
            ConversationRoomModel.PlayerKey => new Vector3(0f, 1.6f, 2f),
            _ => new Vector3(key * 0.5f, 1.6f, 2f)
        };

        private static ConversationGazeTuning Tuning(
            float maxDistance = 10f,
            float maxYaw = 100f,
            bool allowBodyTurn = false,
            float reactionMedian = 0.28f,
            float reactionSigma = 0.35f,
            float decaySeconds = 6f,
            bool audienceChecks = false,
            float audienceIntervalMin = 9f,
            float audienceIntervalMax = 9f,
            float audienceDuration = 0.6f,
            float interruptionSeconds = ConversationRoomTuning.DefaultInterruptionSeconds,
            bool socialIdle = false,
            float socialIntervalMin = 5f,
            float socialIntervalMax = 12f,
            float socialDuration = 1.4f,
            float socialEngagement = 0.5f) =>
            new(
                engagement: 0.6f,
                headContribution: 0.7f,
                allowBodyTurn: allowBodyTurn,
                maxYawDegrees: maxYaw,
                maxDistance: maxDistance,
                reactionMedianSeconds: reactionMedian,
                reactionSigma: reactionSigma,
                decaySeconds: decaySeconds,
                aversionStrength: 0.2f,
                enableAudienceChecks: audienceChecks,
                audienceCheckIntervalMin: audienceIntervalMin,
                audienceCheckIntervalMax: audienceIntervalMax,
                audienceCheckDuration: audienceDuration,
                interruptionSeconds: interruptionSeconds,
                enableSocialIdle: socialIdle,
                socialIdleIntervalMin: socialIntervalMin,
                socialIdleIntervalMax: socialIntervalMax,
                socialIdleDuration: socialDuration,
                socialIdleEngagement: socialEngagement);

        private static ConversationGazeSelf Self(
            GazeSpeakerAttention mode = GazeSpeakerAttention.Anyone,
            bool inOwnTurn = false,
            int aimTargetKey = 0,
            float aimYaw = 0f,
            int key = SelfKey,
            Vector3 point = default,
            Vector3 forward = default,
            bool turnEnding = false,
            ConversationOcclusionSet occluded = null,
            bool scriptedLookActive = false) =>
            new(
                key,
                point == default ? PointOf(key) : point,
                forward == default ? Vector3.forward : forward,
                inOwnTurn,
                mode,
                aimYaw,
                aimTargetKey,
                turnEnding,
                occluded,
                scriptedLookActive);

        /// <summary>Reports a character to the room. Silent unless named as speaking.</summary>
        private void ReportCharacter(int key, bool speaking = false) =>
            _room.ReportParticipant(key, PointOf(key), Vector3.back, speaking, speaking ? 0.5f : 0f, "C" + key);

        /// <summary>Reports the player. <paramref name="addressee" /> is who they are talking to.</summary>
        private void ReportPlayer(bool speaking = false, int addressee = 0, bool typed = false) =>
            _room.ReportPlayer(
                PointOf(ConversationRoomModel.PlayerKey), Vector3.back,
                serverSpeaking: false, localActive: speaking, localLevel: speaking ? 0.4f : 0f,
                addresseeKey: addressee, typedThisTick: typed);

        /// <summary>Derives the room for this frame. Reports must already be in.</summary>
        private void Derive()
        {
            _now += Dt;
            _frame++;
            _room.Refresh(_now, _frame, in _roomTuning);
        }

        /// <summary>
        ///     Runs one listener for <paramref name="seconds" />, with the room configured the
        ///     same way on every frame. Returns the last decision.
        /// </summary>
        private ConversationGazeState Run(
            ConversationGazeDirector director,
            ref DeterministicEmbodimentRandom random,
            float seconds,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            bool speakerTalking = false,
            bool otherTalking = false,
            bool playerTalking = false,
            int addressee = 0,
            bool includeOther = true,
            bool includePlayer = true)
        {
            ConversationGazeState state = director.Current;
            int steps = Mathf.Max(1, Mathf.RoundToInt(seconds / Dt));

            for (int i = 0; i < steps; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, speakerTalking);
                if (includeOther) ReportCharacter(OtherKey, otherTalking);
                if (includePlayer) ReportPlayer(playerTalking, addressee);
                Derive();
                state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
            }

            return state;
        }

        /// <summary>
        ///     One frame of the standing room: this character, two others and the player, with
        ///     nobody talking unless the caller says so. The idle beats need dozens of seconds of
        ///     it, and a per-case loop that reported the room by hand would be a second harness.
        /// </summary>
        private ConversationGazeState Step(
            ConversationGazeDirector director,
            ref DeterministicEmbodimentRandom random,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            bool speakerTalking = false)
        {
            ReportCharacter(SelfKey);
            ReportCharacter(SpeakerKey, speakerTalking);
            ReportCharacter(OtherKey);
            ReportPlayer();
            Derive();
            return director.Tick(_room.Current, in self, in tuning, Dt, ref random);
        }

        // ── The gates (ported) ───────────────────────────────────────────────

        /// <summary>Pins the own-turn gate: the addressee's own policy row owns its gaze, not this.</summary>
        [Test]
        public void OwnTurn_StandsTheConversationDownSoTheAddresseeKeepsItsOwnPolicy()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(1u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(inOwnTurn: true), Tuning(), playerTalking: true);

            Assert.IsFalse(state.Active);
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.OwnTurn));
        }

        /// <summary>Pins the product switch: Off means off, whoever is talking.</summary>
        [Test]
        public void ModeOff_NeverAttends()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(2u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(GazeSpeakerAttention.Off), Tuning(), playerTalking: true);

            Assert.IsFalse(state.Active);
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.ModeOff));
        }

        /// <summary>Pins the mode filter: a character-only listener ignores the player's turn.</summary>
        [Test]
        public void CharactersOnlyMode_IgnoresThePlayersTurn()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(3u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(GazeSpeakerAttention.Characters), Tuning(), playerTalking: true);

            Assert.IsFalse(state.Active);
        }

        /// <summary>Pins the distance gate, and that it names itself: a speaker across the level is out of earshot.</summary>
        [Test]
        public void ASpeakerAcrossTheLevel_IsOutOfEarshot()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(4u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(GazeSpeakerAttention.Characters),
                Tuning(maxDistance: 0.5f), speakerTalking: true, includePlayer: false);

            Assert.IsFalse(state.Active);
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.TooFar));
        }

        /// <summary>Pins the angle gate: a speaker behind the character is unreachable while the body may not turn.</summary>
        [Test]
        public void ASpeakerBehindTheCharacter_IsNotAttendedWhileTheBodyMayNotTurn()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(5u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(GazeSpeakerAttention.Characters),
                Tuning(maxYaw: 10f), speakerTalking: true, includePlayer: false);

            Assert.IsFalse(state.Active);
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.TooWide));
        }

        /// <summary>Pins the other half of the angle gate: allowing the body to turn makes them reachable again.</summary>
        [Test]
        public void ASpeakerBehindTheCharacter_IsAttendedOnceTheBodyMayTurn()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(6u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(GazeSpeakerAttention.Characters),
                Tuning(maxYaw: 10f, allowBodyTurn: true), speakerTalking: true, includePlayer: false);

            Assert.IsTrue(state.Active);
            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey));
            Assert.IsTrue(state.AllowBodyTurn);
        }

        /// <summary>Pins the precedence of geometry over the floor: an unreachable holder blocks nobody.</summary>
        [Test]
        public void AnUnreachableFloorHolder_DoesNotBlockSomebodyElseWhoIsSpeaking()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(7u);
            // The listener stands at the origin looking down +Z; the speaker who takes the floor
            // is directly behind it, and the player is in front.
            ConversationGazeTuning tuning = Tuning(maxYaw: 60f);
            ConversationGazeSelf self = Self(point: new Vector3(3f, 1.6f, 0f));

            ConversationGazeState state = default;
            for (int i = 0; i < 300; i++)
            {
                _room.ReportParticipant(self.Key, self.HeadPoint, Vector3.forward, false, 0f, "self");
                _room.ReportParticipant(
                    SpeakerKey, self.HeadPoint + new Vector3(0f, 0f, -4f), Vector3.forward, true, 0.5f, "behind");
                ReportPlayer(speaking: i >= 60);
                Derive();
                state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
            }

            Assert.IsTrue(state.Active, "The speaker behind its back left the listener with nobody to look at.");
            Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
        }

        // ── Reacting to a turn ───────────────────────────────────────────────

        /// <summary>Pins the reaction latency: a listener turns to the player, but not instantly.</summary>
        [Test]
        public void APlayerTurn_TurnsAListenerTowardThePlayerAfterAHumanLatency()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(8u);
            ConversationGazeTuning tuning = Tuning();
            // Off to one side: a listener already facing the player has nothing to turn, and its
            // reaction is a different rule (see the eyes-only case).
            ConversationGazeSelf self = Self(point: new Vector3(3f, 1.6f, 0f));

            float startedAt = -1f;
            float landedAt = -1f;
            for (int i = 0; i < 120; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey);
                ReportPlayer(speaking: true);
                Derive();
                if (startedAt < 0f) startedAt = _now;

                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (!state.Active || state.Focus != ConversationGazeFocus.Player) continue;

                landedAt = _now;
                break;
            }

            Assert.That(landedAt, Is.GreaterThan(0f), "The listener never turned to the player at all.");
            Assert.That(landedAt - startedAt, Is.InRange(0.1f, 0.8f),
                "A reaction outside a tenth of a second to eight tenths is not a person reacting.");
        }

        /// <summary>Pins the eyes-only rule: a listener already on the speaker settles, it does not turn.</summary>
        [Test]
        public void AListenerAlreadyOnTheSpeaker_ReactsAtOnceAndWithoutItsHead()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(9u);
            ConversationGazeTuning tuning = Tuning();
            // Standing well off the player's line and aimed nowhere near their bearing, so the
            // only thing that can make this eyes-only is the character knowing it is already
            // looking at that person.
            ConversationGazeSelf self = Self(
                aimTargetKey: ConversationRoomModel.PlayerKey,
                aimYaw: 90f,
                point: new Vector3(3f, 1.6f, 0f));

            _room.ReportParticipant(self.Key, self.HeadPoint, Vector3.forward, false, 0f, "self");
            ReportPlayer(speaking: true);
            Derive();
            ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);

            Assert.IsTrue(state.Active, "Nothing had to move, so there was nothing to wait for.");
            Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
            Assert.That(state.HeadContribution, Is.LessThan(tuning.HeadContribution * 0.5f),
                "Eyes that are already there do not drag the head after them.");
        }

        /// <summary>
        ///     Pins the separation rule: three listeners on one onset never start together, and
        ///     which of them goes first is not their position in the room.
        /// </summary>
        [Test]
        public void ThreeListenersOnOneOnset_StartApartAndNotAlwaysInTheSameOrder()
        {
            const float MinSeparation = ConversationGazeTuning.DefaultMinSeparationSeconds;
            var orders = new System.Collections.Generic.HashSet<string>();

            for (uint seed = 1; seed <= 20; seed++)
            {
                SetUp();
                // Wide enough that a listener at 127° is still reachable: this case is about
                // when they turn, not about whether they may.
                ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
                var directors = new ConversationGazeDirector[3];
                var randoms = new DeterministicEmbodimentRandom[3];
                var selves = new ConversationGazeSelf[3];
                var landed = new float[3];

                for (int i = 0; i < 3; i++)
                {
                    directors[i] = new ConversationGazeDirector();
                    randoms[i] = new DeterministicEmbodimentRandom(seed * 31u + (uint)i);
                    // Three listeners abreast, all facing across the room rather than at the
                    // player, so every one of them has a head to turn.
                    selves[i] = Self(
                        key: 21 + i,
                        point: new Vector3((i - 1) * 1.5f, 1.6f, 0f),
                        forward: Vector3.right);
                    landed[i] = -1f;
                }

                for (int step = 0; step < 240; step++)
                {
                    for (int i = 0; i < 3; i++)
                        _room.ReportParticipant(
                            selves[i].Key, selves[i].HeadPoint, Vector3.forward, false, 0f, "L");
                    ReportPlayer(speaking: true);
                    Derive();

                    for (int i = 0; i < 3; i++)
                    {
                        ConversationGazeState state = directors[i].Tick(
                            _room.Current, in selves[i], in tuning, Dt, ref randoms[i]);
                        if (landed[i] < 0f && state.Active && state.Focus == ConversationGazeFocus.Player)
                            landed[i] = _now;
                    }
                }

                for (int i = 0; i < 3; i++)
                    Assert.That(landed[i], Is.GreaterThan(0f), $"Listener {i} never reacted (seed {seed}).");

                for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.That(Mathf.Abs(landed[i] - landed[j]), Is.GreaterThanOrEqualTo(MinSeparation - Dt * 1.5f),
                        $"Listeners {i} and {j} turned together (seed {seed}).");

                var times = (float[])landed.Clone();
                int[] order = { 0, 1, 2 };
                System.Array.Sort(times, order);
                orders.Add($"{order[0]}{order[1]}{order[2]}");
            }

            Assert.That(orders.Count, Is.GreaterThanOrEqualTo(5),
                "Twenty rooms produced fewer than five turn orders — the room is running to a script.");
        }

        /// <summary>Pins the reaction as a per-character draw: no two listeners share one to the centisecond.</summary>
        [Test]
        public void ThreeListenersAtTheEndOfThePlayersTurn_LookToTheAnswererOneAfterAnother()
        {
            // The responder look is booked like an onset: a latency of its own and a room
            // reservation, so the end of a turn — one shared edge — does not turn every head
            // toward the next speaker on the same frame.
            const float MinSeparation = ConversationGazeTuning.DefaultMinSeparationSeconds;
            const int AnswererKey = 40;

            for (uint seed = 1; seed <= 8; seed++)
            {
                SetUp();
                ConversationGazeTuning tuning = Tuning(maxYaw: 180f, decaySeconds: 8f);
                var directors = new ConversationGazeDirector[3];
                var randoms = new DeterministicEmbodimentRandom[3];
                var selves = new ConversationGazeSelf[3];
                var landed = new float[3];
                float turnEnded = -1f;

                for (int i = 0; i < 3; i++)
                {
                    directors[i] = new ConversationGazeDirector();
                    randoms[i] = new DeterministicEmbodimentRandom(seed * 17u + (uint)i);
                    selves[i] = Self(
                        key: 31 + i,
                        point: new Vector3((i - 1) * 1.5f, 1.6f, 0f),
                        forward: Vector3.right);
                    landed[i] = -1f;
                }

                // The player talks to the answerer for a second, then stops; the floor is held
                // for a while and then released, and only then does the responder look begin.
                for (int step = 0; step < 360; step++)
                {
                    bool playerTalking = step < 60;
                    for (int i = 0; i < 3; i++)
                        _room.ReportParticipant(selves[i].Key, selves[i].HeadPoint, Vector3.forward, false, 0f, "L");
                    _room.ReportParticipant(AnswererKey, new Vector3(3f, 1.6f, 3f), Vector3.back, false, 0f, "A");
                    ReportPlayer(speaking: playerTalking, addressee: AnswererKey);
                    Derive();

                    if (turnEnded < 0f && !playerTalking && _room.Current.FloorKey == ConversationRoomModel.NobodyKey)
                        turnEnded = _now;

                    for (int i = 0; i < 3; i++)
                    {
                        ConversationGazeState state = directors[i].Tick(
                            _room.Current, in selves[i], in tuning, Dt, ref randoms[i]);
                        if (landed[i] < 0f && state.Active && state.CharacterKey == AnswererKey)
                            landed[i] = _now;
                    }
                }

                Assert.That(turnEnded, Is.GreaterThan(0f), "The floor never emptied.");
                for (int i = 0; i < 3; i++)
                {
                    Assert.That(landed[i], Is.GreaterThan(0f), $"Listener {i} never looked to the answerer (seed {seed}).");
                    Assert.That(landed[i] - turnEnded, Is.GreaterThan(Dt * 2f),
                        $"Listener {i} looked to the answerer on the frame the turn ended (seed {seed}).");
                }

                for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.That(Mathf.Abs(landed[i] - landed[j]), Is.GreaterThanOrEqualTo(MinSeparation - Dt * 1.5f),
                        $"Listeners {i} and {j} looked to the answerer together (seed {seed}).");
            }
        }

        [Test]
        public void TwoListeners_NeverShareAReactionTime()
        {
            for (uint seed = 1; seed <= 20; seed++)
            {
                SetUp();
                ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
                var left = new ConversationGazeDirector();
                var right = new ConversationGazeDirector();
                var leftRandom = new DeterministicEmbodimentRandom(seed);
                var rightRandom = new DeterministicEmbodimentRandom(seed + 977u);
                ConversationGazeSelf leftSelf = Self(
                    key: 31, point: new Vector3(-1.5f, 1.6f, 0f), forward: Vector3.right);
                ConversationGazeSelf rightSelf = Self(
                    key: 32, point: new Vector3(1.5f, 1.6f, 0f), forward: Vector3.right);
                float leftAt = -1f;
                float rightAt = -1f;

                for (int step = 0; step < 240; step++)
                {
                    _room.ReportParticipant(leftSelf.Key, leftSelf.HeadPoint, Vector3.forward, false, 0f, "L");
                    _room.ReportParticipant(rightSelf.Key, rightSelf.HeadPoint, Vector3.forward, false, 0f, "R");
                    ReportPlayer(speaking: true);
                    Derive();

                    ConversationGazeState leftState = left.Tick(
                        _room.Current, in leftSelf, in tuning, Dt, ref leftRandom);
                    // Both reacted; when they did is the only thing this case is about.
                    ConversationGazeState rightState = right.Tick(
                        _room.Current, in rightSelf, in tuning, Dt, ref rightRandom);
                    if (leftAt < 0f && leftState.Active) leftAt = _now;
                    if (rightAt < 0f && rightState.Active) rightAt = _now;
                }

                Assert.That(Mathf.Abs(leftAt - rightAt), Is.GreaterThan(0.01f),
                    $"Two listeners reacted on the same centisecond (seed {seed}).");
            }
        }

        // ── Following the conversation (ported) ──────────────────────────────

        /// <summary>Pins the floor's hold: a pause inside a turn does not release the speaker.</summary>
        [Test]
        public void APauseInsideATurn_DoesNotReleaseTheSpeaker()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(10u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self(GazeSpeakerAttention.Characters);

            Run(director, ref random, 2f, self, tuning, speakerTalking: true, includePlayer: false);
            ConversationGazeState state = Run(
                director, ref random, 1f, self, tuning, includePlayer: false);

            Assert.IsTrue(state.Active, "A breath is not the end of a turn.");
            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey));
            Assert.IsTrue(state.IsHolding, "They have stopped talking, so the look is carried rather than driven.");
        }

        /// <summary>Pins the decay: four seconds of quiet is a conversation, not the end of one.</summary>
        [Test]
        public void AfterFourSecondsOfQuiet_TheListenerIsStillWithTheLastSpeaker()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(11u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self(GazeSpeakerAttention.Characters);

            Run(director, ref random, 2f, self, tuning, speakerTalking: true, includePlayer: false);
            ConversationGazeState state = Run(director, ref random, 4f, self, tuning, includePlayer: false);

            Assert.IsTrue(state.Active, "The listener drifted off in the middle of a conversation.");
            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey));
        }

        /// <summary>Pins the stand-down: a conversation nobody has spoken in for fourteen seconds is over.</summary>
        [Test]
        public void AfterFourteenSecondsOfQuiet_TheListenerStandsDown()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(12u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self(GazeSpeakerAttention.Characters);

            Run(director, ref random, 2f, self, tuning, speakerTalking: true, includePlayer: false);
            ConversationGazeState state = Run(director, ref random, 14f, self, tuning, includePlayer: false);

            Assert.IsFalse(state.Active);
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.NoSpeaker));
        }

        /// <summary>Pins the responder refresh: after the player's turn the look goes to whoever will answer.</summary>
        [Test]
        public void AfterThePlayersTurn_TheListenerLooksToWhoeverIsExpectedToAnswer()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(13u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            ConversationGazeState during = Run(
                director, ref random, 2f, self, tuning, playerTalking: true, addressee: OtherKey);
            Assert.That(during.Focus, Is.EqualTo(ConversationGazeFocus.Player), "Sanity: watching the player talk.");

            ConversationGazeState waiting = Run(director, ref random, 4f, self, tuning, addressee: OtherKey);

            Assert.IsTrue(waiting.Active);
            Assert.That(waiting.CharacterKey, Is.EqualTo(OtherKey),
                "While the answer is composed, the listener looks at the one who will give it.");
            Assert.IsTrue(waiting.IsHolding, "Nobody is talking yet; this is expectation, not attention.");
        }

        /// <summary>
        ///     Pins the whole of felt report 3: the addressee changing while nobody speaks moves
        ///     the listener, and it never detours back through the character who spoke before.
        /// </summary>
        [Test]
        public void WhenTheAddresseeChangesWhileNobodySpeaks_TheLookFollowsItAndNeverGoesBack()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(14u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            // The player talks to the speaking character, then turns and addresses the other one.
            Run(director, ref random, 2f, self, tuning, playerTalking: true, addressee: SpeakerKey);
            Run(director, ref random, 4f, self, tuning, addressee: SpeakerKey);
            ConversationGazeState moved = Run(director, ref random, 4f, self, tuning, addressee: OtherKey);

            Assert.That(moved.CharacterKey, Is.EqualTo(OtherKey),
                "The addressee changed and nothing followed it — this is the defect the rewrite exists for.");

            // The player then speaks again. The look must go to them, never back through the
            // character who was addressed first.
            bool visitedTheOldAddressee = false;
            ConversationGazeState state = default;
            for (int i = 0; i < 120; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey);
                ReportCharacter(OtherKey);
                ReportPlayer(speaking: true, addressee: OtherKey);
                Derive();
                state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Active && state.CharacterKey == SpeakerKey) visitedTheOldAddressee = true;
                if (state.Active && state.Focus == ConversationGazeFocus.Player) break;
            }

            Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
            Assert.IsFalse(visitedTheOldAddressee, "The look detoured through the character who spoke first.");
        }

        /// <summary>Pins the hand-off: when the expected responder starts talking the look is already there.</summary>
        [Test]
        public void WhenTheResponderStartsSpeaking_TheLookIsAlreadyOnThem()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(15u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, playerTalking: true, addressee: OtherKey);
            Run(director, ref random, 4f, self, tuning, addressee: OtherKey);
            ConversationGazeState state = Run(director, ref random, 1f, self, tuning, otherTalking: true);

            Assert.That(state.CharacterKey, Is.EqualTo(OtherKey));
            Assert.IsFalse(state.IsHolding, "They are speaking; this is attention, not a hold.");
        }

        // ── The floor, as this director sees it (ported) ─────────────────────

        /// <summary>Pins the interjection rule: a brief noise does not take the floor from a speaker.</summary>
        [Test]
        public void ABriefInterjection_DoesNotMoveTheLook()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(16u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, speakerTalking: true);
            ConversationGazeState state = Run(
                director, ref random, 0.25f, self, tuning, speakerTalking: true, playerTalking: true);

            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey),
                "A quarter of a second of overlap is a mm-hmm, not an interruption.");
        }

        /// <summary>Pins the interruption rule: sustained speech over a speaker does take the floor.</summary>
        [Test]
        public void ASustainedInterruption_MovesTheLookToTheInterrupter()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(17u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, speakerTalking: true);
            ConversationGazeState state = Run(
                director, ref random, 2f, self, tuning, speakerTalking: true, playerTalking: true);

            Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
        }

        /// <summary>Pins the no-ping-pong rule: the look does not trade back while the two overlap.</summary>
        [Test]
        public void TheLookDoesNotPingPong_WhileThePlayerTalksOverACharacter()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(18u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, speakerTalking: true);
            Run(director, ref random, 2f, self, tuning, speakerTalking: true, playerTalking: true);

            int changes = 0;
            ConversationGazeFocus previous = director.Current.Focus;
            for (int i = 0; i < 300; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, true);
                ReportCharacter(OtherKey);
                ReportPlayer(speaking: true);
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Focus != previous) changes++;
                previous = state.Focus;
            }

            Assert.That(changes, Is.Zero, "The look traded back and forth across five seconds of overlap.");
        }

        /// <summary>Pins the typed turn: typing is as much a turn as talking, and it moves the look.</summary>
        [Test]
        public void ATypedMessage_TakesATurnJustLikeSpeech()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(19u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            ConversationGazeState state = default;
            for (int i = 0; i < 120; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey);
                ReportPlayer(typed: i == 0);
                Derive();
                state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Active && state.Focus == ConversationGazeFocus.Player) break;
            }

            Assert.IsTrue(state.Active);
            Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
        }

        // ── Audience checks ──────────────────────────────────────────────────

        /// <summary>Pins the audience check: the look goes to the person being spoken to, and comes back.</summary>
        [Test]
        public void AnAudienceCheck_GoesToTheAddresseeAndReturnsToTheSpeaker()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(20u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 0.5f, audienceIntervalMax: 0.5f, audienceDuration: 0.5f);
            ConversationGazeSelf self = Self();

            // A character speaks, so the person being spoken to is the player.
            Run(director, ref random, 4f, self, tuning, speakerTalking: true);

            bool checkedTheAddressee = false;
            bool cameBack = false;
            for (int i = 0; i < 900; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, true);
                ReportCharacter(OtherKey);
                ReportPlayer();
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);

                if (state.Active && state.IsAudienceCheck)
                {
                    Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
                    Assert.IsFalse(state.AllowBodyTurn, "A check is eyes and a little neck. It never turns the body.");
                    Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Glance));
                    checkedTheAddressee = true;
                    continue;
                }

                if (!checkedTheAddressee) continue;
                if (state.Active && state.CharacterKey == SpeakerKey) cameBack = true;
                if (cameBack) break;
            }

            Assert.IsTrue(checkedTheAddressee, "The listener never checked the person being spoken to.");
            Assert.IsTrue(cameBack, "The check never ended — the look stayed where it glanced.");
        }

        /// <summary>Pins the settle rule: a check never fires in the first beats of taking somebody up.</summary>
        [Test]
        public void AnAudienceCheck_WaitsForTheListenerToSettleFirst()
        {
            // Twenty seeds, because the first check of a turn is also pushed out by the
            // character's own phase — on some seeds that phase alone would hold it back, and a
            // case that only ever runs one of those seeds is not testing the settle rule at all.
            for (uint seed = 1; seed <= 20; seed++)
            {
                SetUp();
                var director = new ConversationGazeDirector();
                var random = new DeterministicEmbodimentRandom(seed);
                ConversationGazeTuning tuning = Tuning(
                    audienceChecks: true, audienceIntervalMin: 0f, audienceIntervalMax: 0f, audienceDuration: 0.5f);
                ConversationGazeSelf self = Self();

                for (int i = 0; i < 144; i++)
                {
                    ReportCharacter(SelfKey);
                    ReportCharacter(SpeakerKey, true);
                    ReportPlayer();
                    Derive();
                    ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                    Assert.IsFalse(state.IsAudienceCheck,
                        $"Turning to somebody and immediately looking away is a twitch, not interest (seed {seed}).");
                }
            }
        }

        /// <summary>Pins the switch that turns checks off entirely.</summary>
        [Test]
        public void WithAudienceChecksOff_TheListenerNeverLooksAway()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(22u);
            ConversationGazeTuning tuning = Tuning(audienceChecks: false);
            ConversationGazeSelf self = Self();

            for (int i = 0; i < 1200; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, true);
                ReportPlayer();
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                Assert.IsFalse(state.IsAudienceCheck, "A check fired with checks switched off.");
            }
        }

        // ── Output and lifecycle ─────────────────────────────────────────────

        /// <summary>Pins the intensity bias: two listeners do not watch with identical commitment.</summary>
        [Test]
        public void TwoListeners_DoNotWatchWithIdenticalIntensity()
        {
            ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
            var left = new ConversationGazeDirector();
            var right = new ConversationGazeDirector();
            var leftRandom = new DeterministicEmbodimentRandom(101u);
            var rightRandom = new DeterministicEmbodimentRandom(2029u);
            ConversationGazeSelf leftSelf = Self(
                key: 31, point: new Vector3(-1.5f, 1.6f, 0f), forward: Vector3.right);
            ConversationGazeSelf rightSelf = Self(
                key: 32, point: new Vector3(1.5f, 1.6f, 0f), forward: Vector3.right);
            ConversationGazeState leftState = default;
            ConversationGazeState rightState = default;

            for (int i = 0; i < 180; i++)
            {
                _room.ReportParticipant(leftSelf.Key, leftSelf.HeadPoint, Vector3.forward, false, 0f, "L");
                _room.ReportParticipant(rightSelf.Key, rightSelf.HeadPoint, Vector3.forward, false, 0f, "R");
                ReportPlayer(speaking: true);
                Derive();
                leftState = left.Tick(_room.Current, in leftSelf, in tuning, Dt, ref leftRandom);
                rightState = right.Tick(_room.Current, in rightSelf, in tuning, Dt, ref rightRandom);
            }

            Assert.IsTrue(leftState.Active && rightState.Active, "Sanity: both listeners are watching.");
            Assert.That(leftState.Engagement, Is.Not.EqualTo(rightState.Engagement).Within(0.001f));
        }

        /// <summary>Pins the nature of an ordinary look: following a speaker is attention, not a glance.</summary>
        [Test]
        public void FollowingASpeaker_IsAttentionRatherThanAGlance()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(23u);

            ConversationGazeState state = Run(
                director, ref random, 2f, Self(), Tuning(), speakerTalking: true);

            Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Attention));
            Assert.That(state.Engagement, Is.GreaterThan(0f));
        }

        /// <summary>Pins reset: a disabled component forgets the room it was standing in.</summary>
        [Test]
        public void Reset_ClearsAttentionAndTheLook()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(24u);

            Run(director, ref random, 2f, Self(), Tuning(), speakerTalking: true);
            Assert.IsTrue(director.Current.Active, "Sanity: it was watching somebody.");

            director.Reset();

            Assert.IsFalse(director.Current.Active);
            Assert.That(director.TrackedCount, Is.Zero);
            Assert.That(director.AttentionOf(SpeakerKey), Is.Zero);
        }

        // ── Walls ────────────────────────────────────────────────────────────

        /// <summary>
        ///     Pins the line-of-sight gate, and that it names itself: a character in the next
        ///     room can be heard taking a turn, and is not somebody to turn toward.
        /// </summary>
        [Test]
        public void ASpeakerBehindAWall_IsNotAttended()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(70u);
            var occluded = new ConversationOcclusionSet();
            occluded.MarkOccludedForTests(SpeakerKey);

            ConversationGazeState state = Run(
                director, ref random, 2f,
                Self(GazeSpeakerAttention.Characters, occluded: occluded), Tuning(),
                speakerTalking: true, includeOther: false, includePlayer: false);

            Assert.IsFalse(state.Active, "The character followed a turn it could not possibly see.");
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.NoLineOfSight));
        }

        /// <summary>
        ///     The positive control for the case above: the same room, the same speaker, the same
        ///     two seconds — the only difference is the wall. Without this the case above would
        ///     pass just as well if nothing were ever attended at all.
        /// </summary>
        [Test]
        public void TheSameSpeakerWithNoWall_IsAttended()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(70u);

            ConversationGazeState state = Run(
                director, ref random, 2f,
                Self(GazeSpeakerAttention.Characters), Tuning(),
                speakerTalking: true, includeOther: false, includePlayer: false);

            Assert.IsTrue(state.Active);
            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey));
        }

        /// <summary>
        ///     Pins what happens when the wall goes: the speaker has been holding this
        ///     character's attention the whole time it was hidden, and its reaction to that onset
        ///     is long since due, so the look lands on the very next tick rather than waiting for
        ///     a fresh onset that may never come.
        /// </summary>
        [Test]
        public void WhenTheWallGoes_TheLookLandsOnTheNextTick()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(71u);
            var occluded = new ConversationOcclusionSet();
            occluded.MarkOccludedForTests(SpeakerKey);
            ConversationGazeSelf self = Self(GazeSpeakerAttention.Characters, occluded: occluded);
            ConversationGazeTuning tuning = Tuning();

            ConversationGazeState hidden = Run(
                director, ref random, 2f, self, tuning,
                speakerTalking: true, includeOther: false, includePlayer: false);
            Assert.IsFalse(hidden.Active, "Premise: the speaker was hidden for the first two seconds.");

            occluded.Clear();
            ConversationGazeState seen = Run(
                director, ref random, Dt, self, tuning,
                speakerTalking: true, includeOther: false, includePlayer: false);

            Assert.IsTrue(seen.Active, "The wall came down and the character went on ignoring the speaker.");
            Assert.That(seen.CharacterKey, Is.EqualTo(SpeakerKey));
        }

        /// <summary>
        ///     Negative control on the quiet-room beat: idle life is the people a character is
        ///     standing with, and somebody through a wall is not one of them.
        /// </summary>
        [Test]
        public void SocialIdle_NeverGlancesAtSomebodyBehindAWall()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(72u);
            var occluded = new ConversationOcclusionSet();
            occluded.MarkOccludedForTests(SpeakerKey);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 2f, socialIntervalMax: 2f, socialDuration: 0.8f);
            ConversationGazeSelf self = Self(occluded: occluded);

            int glances = 0;
            int liveKey = 0;
            for (int i = 0; i < 3600; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                if (!state.Active || state.Look != ConversationGazeLook.SocialIdle)
                {
                    liveKey = 0;
                    continue;
                }

                Assert.That(state.CharacterKey, Is.Not.EqualTo(SpeakerKey),
                    "An idle glance landed on the person on the other side of the wall.");

                if (state.CharacterKey == liveKey) continue;

                liveKey = state.CharacterKey;
                glances++;
            }

            Assert.That(glances, Is.GreaterThanOrEqualTo(3),
                "Sixty seconds produced fewer than three glances — the rule was never exercised.");
        }

        // ── Idle glances between people ──────────────────────────────────────

        /// <summary>Pins social idle: a quiet room is not an empty one, and the character notices the people in it.</summary>
        [Test]
        public void WhileNobodyTalks_TheCharacterGlancesAtThePeopleAroundIt()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(30u);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 3f, socialIntervalMax: 3f,
                socialDuration: 1f, socialEngagement: 0.25f);
            ConversationGazeSelf self = Self();

            ConversationGazeState glance = default;
            for (int i = 0; i < 1800; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                if (!state.Active || state.Look != ConversationGazeLook.SocialIdle) continue;

                glance = state;
                break;
            }

            Assert.IsTrue(glance.Active, "The character stood in a room with two other people and never looked at either.");
            Assert.That(glance.Nature, Is.EqualTo(GazeLookNature.Glance), "Nothing asked for its attention, so this is a glance.");
            Assert.IsFalse(glance.AllowBodyTurn, "A glance never turns the body.");
            Assert.That(glance.CharacterKey, Is.EqualTo(SpeakerKey).Or.EqualTo(OtherKey));
            Assert.That(glance.Engagement, Is.EqualTo(0.25f).Within(0.04f),
                "An idle glance carries the commitment authored beside its interval, not the conversation's.");
        }

        /// <summary>
        ///     Negative control for the variety rule: the idle beat is somebody noticing the
        ///     people it is standing with, not somebody staring at one of them on a timer.
        /// </summary>
        [Test]
        public void SocialIdle_NeverGlancesAtTheSamePersonTwiceRunning()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(31u);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 2f, socialIntervalMax: 2f, socialDuration: 0.8f);
            ConversationGazeSelf self = Self();

            int glances = 0;
            int previousKey = 0;
            int liveKey = 0;
            for (int i = 0; i < 3600; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                bool glancing = state.Active && state.Look == ConversationGazeLook.SocialIdle;
                if (!glancing)
                {
                    liveKey = 0;
                    continue;
                }

                if (state.CharacterKey == liveKey) continue;

                liveKey = state.CharacterKey;
                glances++;
                Assert.That(liveKey, Is.Not.EqualTo(previousKey),
                    $"Glance {glances} landed on the person the one before it did.");
                previousKey = liveKey;
            }

            Assert.That(glances, Is.GreaterThanOrEqualTo(3),
                "Sixty seconds produced fewer than three glances — the rule was never exercised.");
        }

        /// <summary>Negative control: idle life is what happens instead of a conversation, never during one.</summary>
        [Test]
        public void SocialIdle_NeverRunsWhileSomebodyIsSpeaking()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(32u);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 2f, socialIntervalMax: 2f, socialDuration: 1f);
            ConversationGazeSelf self = Self();

            for (int i = 0; i < 1800; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning, speakerTalking: true);
                Assert.That(state.Look, Is.Not.EqualTo(ConversationGazeLook.SocialIdle),
                    "The character glanced away at a bystander while somebody was talking to it.");
            }
        }

        /// <summary>
        ///     Pins the floor under the idle cadence: whatever the component authors, two glances
        ///     closer together than two seconds read as one restless sweep.
        /// </summary>
        [Test]
        public void SocialIdle_KeepsTwoSecondsBetweenGlancesHoweverShortTheAuthoredInterval()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(33u);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 0.1f, socialIntervalMax: 0.1f, socialDuration: 0.5f);
            ConversationGazeSelf self = Self();

            float previousStart = -1f;
            int gaps = 0;
            bool glancing = false;
            for (int i = 0; i < 2400; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                bool live = state.Active && state.Look == ConversationGazeLook.SocialIdle;
                if (!live)
                {
                    glancing = false;
                    continue;
                }

                if (glancing) continue;
                glancing = true;

                if (previousStart >= 0f)
                {
                    gaps++;
                    Assert.That(_now - previousStart, Is.GreaterThanOrEqualTo(2f - Dt),
                        "Two idle glances landed inside two seconds of each other.");
                }

                previousStart = _now;
            }

            Assert.That(gaps, Is.GreaterThanOrEqualTo(2), "Fewer than three glances — the gap rule was never exercised.");
        }

        // ── The speaker's own audience ───────────────────────────────────────

        /// <summary>Pins the speaker's look round the room: eyes only, never the body, and it comes back.</summary>
        [Test]
        public void ASpeakerWithAnAudience_LooksRoundIt_AndOnlyWithItsEyes()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(34u);
            ConversationGazeTuning tuning = Tuning(
                allowBodyTurn: true, audienceChecks: true,
                audienceIntervalMin: 5f, audienceIntervalMax: 5f, audienceDuration: 0.6f);
            ConversationGazeSelf self = Self(inOwnTurn: true);

            ConversationGazeState check = default;
            bool cameBack = false;
            for (int i = 0; i < 3600; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                if (state.Active && state.Look == ConversationGazeLook.SpeakerCheck)
                {
                    check = state;
                    continue;
                }

                if (!check.Active) continue;

                cameBack = state.Reason == ConversationGazeStandDown.OwnTurn;
                break;
            }

            Assert.IsTrue(check.Active, "A character addressing a room of three never once looked round it.");
            Assert.That(check.Focus, Is.EqualTo(ConversationGazeFocus.Character));
            Assert.That(check.CharacterKey, Is.EqualTo(SpeakerKey).Or.EqualTo(OtherKey));
            Assert.That(check.Nature, Is.EqualTo(GazeLookNature.Glance));
            Assert.IsFalse(check.AllowBodyTurn, "Looking round the room is not turning to face somebody.");
            Assert.That(check.HeadContribution, Is.LessThan(tuning.HeadContribution * 0.5f),
                "The check lends the eyes; the head stays with the person being spoken to.");
            Assert.IsTrue(cameBack, "The check never ended, so the speaker's own policy never got its look back.");
        }

        /// <summary>Negative control: two people are a conversation, not an audience.</summary>
        [Test]
        public void ASpeakerWithOneListener_NeverLooksRoundTheRoom()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(35u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 0f, audienceIntervalMax: 0f, audienceDuration: 0.6f);
            ConversationGazeSelf self = Self(inOwnTurn: true);

            for (int i = 0; i < 3600; i++)
            {
                ReportCharacter(SelfKey);
                ReportPlayer();
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                Assert.That(state.Look, Is.Not.EqualTo(ConversationGazeLook.SpeakerCheck),
                    "There was nobody to look round at, so looking away was just avoiding eye contact.");
            }
        }

        /// <summary>Pins the group scaling: the bigger the audience, the sooner it needs holding again.</summary>
        [Test]
        public void ASpeakerAddressingFivePeople_LooksRoundSoonerThanOneAddressingTwo()
        {
            float small = FirstSpeakerCheck(2);
            SetUp();
            float large = FirstSpeakerCheck(5);

            Assert.That(small, Is.GreaterThan(0f), "The smaller room never produced a check at all.");
            Assert.That(large, Is.GreaterThan(0f), "The larger room never produced a check at all.");
            Assert.That(large, Is.LessThan(small * 0.75f),
                "A speaker holding five people looked round no sooner than one holding two.");
        }

        /// <summary>
        ///     Room time of the first look round the room for a speaker with
        ///     <paramref name="others" /> people in front of it. Same seed both times, so the only
        ///     thing that differs between two calls is the size of the audience.
        /// </summary>
        private float FirstSpeakerCheck(int others)
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(36u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 20f, audienceIntervalMax: 20f, audienceDuration: 0.5f);
            ConversationGazeSelf self = Self(inOwnTurn: true);

            for (int i = 0; i < 3600; i++)
            {
                ReportCharacter(SelfKey);
                for (int p = 0; p < others; p++)
                    _room.ReportParticipant(
                        50 + p, new Vector3((p - 2) * 1.2f, 1.6f, 2.5f), Vector3.back, false, 0f, "P");
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Look == ConversationGazeLook.SpeakerCheck) return _now;
            }

            return -1f;
        }

        // ── The interruption reflex ──────────────────────────────────────────

        /// <summary>
        ///     Pins rule 5 from both ends: the eyes go to somebody talking over the floor holder
        ///     while the room is still making its mind up, and the head only follows once the
        ///     floor has actually moved.
        /// </summary>
        [Test]
        public void WhenSomebodyTalksOverTheFloorHolder_TheEyesGoFirstAndTheHeadWaitsForTheFloor()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(37u);
            // A near-instant, spread-free reaction: this case is about the two stages of the
            // interruption, not about when this particular listener noticed it.
            ConversationGazeTuning tuning = Tuning(reactionMedian: 0.05f, reactionSigma: 0f);
            // Off to one side of both of them: a listener already pointed at the interrupter
            // reacts with its eyes for a different reason, and this case is about the reflex.
            ConversationGazeSelf self = Self(point: new Vector3(3f, 1.6f, 0f));

            Run(director, ref random, 2f, self, tuning, speakerTalking: true, includeOther: false);
            Assert.That(director.Current.CharacterKey, Is.EqualTo(SpeakerKey), "Sanity: watching the character talk.");

            float reflexHead = -1f;
            float settledHead = -1f;
            for (int i = 0; i < 300; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, true);
                ReportPlayer(speaking: true);
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);

                if (reflexHead < 0f && state.Look == ConversationGazeLook.Reflex)
                {
                    Assert.That(state.Focus, Is.EqualTo(ConversationGazeFocus.Player));
                    Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Reflex));
                    Assert.IsFalse(state.AllowBodyTurn, "The body does not commit to an interruption the room has not accepted.");
                    Assert.That(_room.Current.FloorKey, Is.EqualTo(SpeakerKey),
                        "The eyes were supposed to move before the floor did.");
                    reflexHead = state.HeadContribution;
                    continue;
                }

                if (reflexHead < 0f) continue;
                if (_room.Current.FloorKey != ConversationRoomModel.PlayerKey) continue;
                if (state.Look != ConversationGazeLook.Attention || state.Focus != ConversationGazeFocus.Player) continue;

                settledHead = state.HeadContribution;
                break;
            }

            Assert.That(reflexHead, Is.GreaterThan(0f), "The eyes never went to the person talking over the speaker.");
            Assert.That(settledHead, Is.GreaterThan(0f), "The floor moved and the listener never turned to the new speaker.");
            Assert.That(settledHead, Is.GreaterThan(reflexHead * 3f),
                $"The head was already committed during the reflex ({reflexHead:0.00} of {settledHead:0.00}) — " +
                "a glance out of the corner of the eye and a turn are the same movement.");
        }

        /// <summary>
        ///     Pins the tempo half of rule 5: a startle and a turn are different movements. Once
        ///     the room has handed the floor to the interrupter, a listener whose head is still
        ///     catching up is doing the ordinary thing — turning to whoever is talking — and it
        ///     must not run at a reflex's speed just because its eyes got there first.
        /// </summary>
        [Test]
        public void OnceTheFloorMovesToTheInterrupter_TheCatchUpIsATurnAndNotAStartle()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(137u);
            ConversationGazeTuning tuning = Tuning(reactionMedian: 0.05f, reactionSigma: 0f);
            ConversationGazeSelf self = Self(point: new Vector3(3f, 1.6f, 0f));

            Run(director, ref random, 2f, self, tuning, speakerTalking: true, includeOther: false);
            Assert.That(director.Current.CharacterKey, Is.EqualTo(SpeakerKey), "Sanity: watching the character talk.");

            bool sawStartle = false;
            bool sawCatchUp = false;
            for (int i = 0; i < 300; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, true);
                ReportPlayer(speaking: true);
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Look != ConversationGazeLook.Reflex) continue;

                if (_room.Current.FloorKey != ConversationRoomModel.PlayerKey)
                {
                    // Still an interruption the room has not accepted: a startle, and it stays one.
                    Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Reflex));
                    sawStartle = true;
                    continue;
                }

                // The floor has moved. This is now the listener turning to the new speaker.
                sawCatchUp = true;
                Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Attention),
                    "A listener catching up to the person who now holds the floor is turning, not startling.");
                Assert.IsFalse(state.AllowBodyTurn, "The head is still waiting for its own booked moment.");
                break;
            }

            Assert.IsTrue(sawStartle, "The eyes never went to the person talking over the speaker.");
            Assert.IsTrue(sawCatchUp, "The floor never moved to the interrupter while the head was still owed.");
        }

        // ── The hand-off, and the looks that are too short to be turns ───────

        /// <summary>
        ///     Pins the hand-off support: an empty floor is the middle of a hand-off, not the end
        ///     of one, so whoever the room expects to answer keeps the attention they carried as
        ///     the addressee until the raise booked for them lands.
        /// </summary>
        [Test]
        public void WhileTheRoomWaitsForAnAnswer_TheExpectedResponderKeepsItsSupport()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(140u);
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, playerTalking: true, addressee: OtherKey);
            Assert.That(director.AttentionOf(OtherKey), Is.EqualTo(0.6f).Within(0.01f),
                "Sanity: the person being spoken to carries the addressee's share.");

            // The player stops. The floor empties a little later; from that moment the support
            // must not lapse for a single frame before the booked raise takes over.
            float lowest = float.PositiveInfinity;
            bool sawEmptyFloor = false;
            for (int i = 0; i < 240; i++)
            {
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey);
                ReportCharacter(OtherKey);
                ReportPlayer(speaking: false, addressee: OtherKey);
                Derive();
                director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (_room.Current.FloorKey != ConversationRoomModel.NobodyKey) continue;

                sawEmptyFloor = true;
                lowest = Mathf.Min(lowest, director.AttentionOf(OtherKey));
                if (director.AttentionOf(OtherKey) >= 0.7f) break;
            }

            Assert.IsTrue(sawEmptyFloor, "Sanity: the floor never emptied.");
            Assert.That(lowest, Is.GreaterThanOrEqualTo(0.59f),
                "The answerer's support lapsed between the floor emptying and the booked raise landing.");
        }

        /// <summary>
        ///     Pins the other end of the same rule: the support is keyed to the booking it
        ///     bridges to, not to a clock, so a room nobody answers in still runs out of people
        ///     worth looking at. Held any other way it would be a conversation that never ends.
        /// </summary>
        [Test]
        public void TheHandOffSupport_DoesNotOutliveTheRaiseItBridgesTo()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(141u);
            ConversationGazeTuning tuning = Tuning();

            Run(director, ref random, 2f, Self(), tuning, playerTalking: true, addressee: OtherKey);
            Run(director, ref random, 20f, Self(), tuning, addressee: OtherKey);

            Assert.That(director.AttentionOf(OtherKey), Is.LessThan(0.15f),
                "Twenty seconds after a question nobody answered, the room is not still waiting.");
        }

        /// <summary>
        ///     Pins the glance share: every look that is short by design — a listener's check, an
        ///     idle glance — asks the head for a fraction of what a committed look does. It is
        ///     what keeps a beat lasting half a second from being a fifty-degree turn and back.
        /// </summary>
        [Test]
        public void EveryLookThatIsShortByDesign_AsksTheHeadForAGlanceShare()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(142u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 3f, audienceIntervalMax: 3f, audienceDuration: 0.6f,
                socialIdle: true, socialIntervalMin: 3f, socialIntervalMax: 3f);
            ConversationGazeSelf self = Self();

            int seen = 0;
            for (int i = 0; i < 7200; i++)
            {
                bool talking = i < 1800;
                ReportCharacter(SelfKey);
                ReportCharacter(SpeakerKey, talking);
                ReportCharacter(OtherKey);
                ReportPlayer();
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                if (state.Look is not (ConversationGazeLook.AudienceCheck or ConversationGazeLook.SocialIdle))
                    continue;

                seen++;
                Assert.That(state.Nature, Is.EqualTo(GazeLookNature.Glance),
                    $"{state.Look} is short by design and must never be issued as attention.");
                Assert.That(state.HeadContribution, Is.LessThanOrEqualTo(tuning.HeadContribution * 0.35f + 0.001f),
                    $"{state.Look} asked the head for more than a glance's share.");
                Assert.IsFalse(state.AllowBodyTurn, $"{state.Look} must never turn the body.");
            }

            Assert.That(seen, Is.GreaterThan(0), "Neither beat ever fired — the case measured nothing.");
        }

        /// <summary>
        ///     The gate for the hand-off flick. In a room the size people actually stand in — every
        ///     pair sixty degrees or so apart, so any look between two of them is a real turn — a
        ///     listener must never commit its head to somebody and take it back before the look
        ///     has been a look. A beat that IS short by design may still go out and back inside a
        ///     second; what it may not do is arrive as attention, because then it is a turn.
        /// </summary>
        [Test]
        public void InAFullRoom_NoCommittedLookGoesOutAndComesStraightBack()
        {
            const float MinimumDwell = 1f;

            for (uint seed = 1; seed <= 20; seed++)
            {
                SetUp();
                var director = new ConversationGazeDirector();
                var random = new DeterministicEmbodimentRandom(seed * 977u + 3u);
                ConversationGazeTuning tuning = Tuning(
                    maxYaw: 180f,
                    audienceChecks: true, audienceIntervalMin: 2f, audienceIntervalMax: 4f, audienceDuration: 0.6f);
                ConversationGazeSelf self = Self(point: RoomPointOf(SelfKey), forward: Vector3.forward);

                int previous = 0;
                int current = 0;
                float currentSince = 0f;
                bool currentWasCommitted = false;

                for (int i = 0; i < 1800; i++)
                {
                    // Ten seconds a lap: the player asks Marcus a question, Marcus answers, the
                    // room falls quiet, and it happens again.
                    int phase = i % 600;
                    bool playerTalking = phase < 240;
                    bool marcusTalking = phase >= 300 && phase < 540;

                    ReportRoom(playerTalking, marcusTalking);
                    Derive();
                    ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);

                    int target = !state.Active
                        ? 0
                        : state.Focus == ConversationGazeFocus.Player
                            ? ConversationRoomModel.PlayerKey
                            : state.CharacterKey;

                    if (target == current)
                    {
                        currentWasCommitted |= state.Active && state.Nature != GazeLookNature.Glance;
                        continue;
                    }

                    if (target != 0 && current != 0 && target == previous && currentWasCommitted)
                        Assert.That(_now - currentSince, Is.GreaterThanOrEqualTo(MinimumDwell),
                            $"Seed {seed}: the look committed to {current} and went back to {previous} " +
                            $"after {_now - currentSince:0.00}s — that is a flick, not two looks.");

                    previous = current;
                    current = target;
                    currentSince = _now;
                    currentWasCommitted = state.Active && state.Nature != GazeLookNature.Glance;
                }
            }
        }

        /// <summary>
        ///     Pins the drift hold. A speaker pausing for breath decays past the person they are
        ///     talking to, who is pinned at the addressee's share — so a lead opens with nothing
        ///     happening at all. Following it costs a turn away and, the moment the speaker
        ///     resumes, a turn back: the same half-second excursion measured in a live room. The
        ///     lead has to stand before the look follows it.
        /// </summary>
        [Test]
        public void ALeadThatOpensWhileASpeakerDrawsBreath_DoesNotMoveTheLook()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(143u);
            // A short memory, so the crossing happens inside the floor's own hold rather than
            // several seconds after the room has stopped calling this a turn.
            ConversationGazeTuning tuning = Tuning(decaySeconds: 1.5f);
            ConversationGazeSelf self = Self();

            Run(director, ref random, 3f, self, tuning, speakerTalking: true);
            Assert.That(director.Current.CharacterKey, Is.EqualTo(SpeakerKey), "Sanity: watching the speaker.");

            // A breath, and then the next sentence. The room still calls this the speaker's turn
            // throughout — the floor hold is 2.5s — so nothing about it is an event.
            ConversationGazeState state = Run(director, ref random, 1.5f, self, tuning);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(SpeakerKey), "Sanity: a breath is not the end of a turn.");
            Assert.IsTrue(state.Active);
            Assert.That(state.CharacterKey, Is.EqualTo(SpeakerKey),
                "The look left a speaker mid-turn because the arithmetic drifted, not because anything happened.");
        }

        /// <summary>Where the three of them stand: each pair a real turn apart, as measured.</summary>
        private static Vector3 RoomPointOf(int key) => key switch
        {
            SelfKey => new Vector3(-1.7f, 1.6f, -1f),
            SpeakerKey => new Vector3(1.7f, 1.6f, -1f),
            OtherKey => new Vector3(0f, 1.6f, 2f),
            _ => new Vector3(0f, 1.6f, 0f)
        };

        /// <summary>One frame of the three-character room, with the player in the middle of it.</summary>
        private void ReportRoom(bool playerTalking, bool otherTalking)
        {
            _room.ReportParticipant(SelfKey, RoomPointOf(SelfKey), Vector3.forward, false, 0f, "Sofia");
            _room.ReportParticipant(SpeakerKey, RoomPointOf(SpeakerKey), Vector3.forward, false, 0f, "Ethan");
            _room.ReportParticipant(
                OtherKey, RoomPointOf(OtherKey), Vector3.back, otherTalking, otherTalking ? 0.5f : 0f, "Marcus");
            _room.ReportPlayer(
                RoomPointOf(0), Vector3.forward,
                serverSpeaking: false, localActive: playerTalking, localLevel: playerTalking ? 0.4f : 0f,
                addresseeKey: OtherKey, typedThisTick: false);
        }

        // ── What a glance can reach ──────────────────────────────────────────

        /// <summary>
        ///     Pins the glance reach. A glance is defined by what it costs: eyes, a little neck,
        ///     and no body. Past the neck's comfortable travel plus the band the eyes may rest in
        ///     there is no such look — what happens instead is that the eyes are parked at the
        ///     corner of the socket for the length of the beat, which is what was measured in a
        ///     live room. The candidate is refused rather than performed badly.
        /// </summary>
        /// <param name="yawDegrees">Where the other character stands, round from forward.</param>
        /// <param name="expected">Whether a glance at them is a glance at all.</param>
        [TestCase(40f, true)]
        [TestCase(62f, false)]
        public void AnIdleGlance_IsOnlyAimedWhereEyesAndAComfortableNeckReach(float yawDegrees, bool expected)
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(150u);
            // Wide enough that the character is ALLOWED to look at them: this case is about what
            // a glance can reach, not about what the attention gates permit.
            ConversationGazeTuning tuning = Tuning(
                maxYaw: 180f, socialIdle: true, socialIntervalMin: 2f, socialIntervalMax: 2f);
            ConversationGazeSelf self = Self();
            Vector3 point = new(
                Mathf.Sin(yawDegrees * Mathf.Deg2Rad) * 3f, 1.6f, Mathf.Cos(yawDegrees * Mathf.Deg2Rad) * 3f);

            bool glanced = false;
            for (int i = 0; i < 3600 && !glanced; i++)
            {
                ReportCharacter(SelfKey);
                _room.ReportParticipant(OtherKey, point, Vector3.back, false, 0f, "Bystander");
                ReportPlayer();
                Derive();
                ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                glanced |= state.Look == ConversationGazeLook.SocialIdle && state.CharacterKey == OtherKey;
            }

            Assert.That(glanced, Is.EqualTo(expected),
                expected
                    ? "A bystander within reach of the eyes and a comfortable neck is glanced at."
                    : "A bystander that far round can only be reached by turning, which is not a glance.");
        }

        // ── One separation per face ──────────────────────────────────────────

        /// <summary>
        ///     The gate for the room turning together. Whatever moved a listener's look — noticing
        ///     somebody start talking, following the floor as it changes hands, the eyes flicking
        ///     to an interrupter, or being handed the answer at the end of a turn — two of them
        ///     arriving on one face within the separation is the defect, and the cause makes no
        ///     difference to that because the viewer cannot see the cause.
        /// </summary>
        [Test]
        public void HoweverTheyGotThere_TwoListenersNeverArriveOnOneFaceTogether()
        {
            const float MinSeparation = ConversationGazeTuning.DefaultMinSeparationSeconds;
            const int ListenerA = 21;
            const int ListenerB = 22;
            const int ListenerC = 23;

            var listeners = new[] { ListenerA, ListenerB, ListenerC };
            var landings = new List<(int Listener, int Target, float At, ConversationGazeLook Look)>();

            for (uint seed = 1; seed <= 30; seed++)
            {
                SetUp();
                landings.Clear();

                var directors = new ConversationGazeDirector[3];
                var randoms = new DeterministicEmbodimentRandom[3];
                var selves = new ConversationGazeSelf[3];
                // What each listener is committed to, and what its eyes have flicked to on top of
                // that. Kept apart because the flick is an excursion: the committed look never
                // moved, so coming back off it is not an arrival on anybody.
                var committed = new int[3];
                var flicked = new int[3];
                for (int i = 0; i < 3; i++)
                {
                    directors[i] = new ConversationGazeDirector();
                    randoms[i] = new DeterministicEmbodimentRandom(seed * 131u + (uint)i);
                    selves[i] = Self(key: listeners[i], point: CrowdPointOf(listeners[i]));
                }

                for (int step = 0; step < 1500; step++)
                {
                    // Twenty-five seconds a lap, and every trigger in it: the player asks Marcus
                    // something (onset, then the answer's hand-off), Marcus answers (onset), Ethan
                    // talks over him and is eventually given the floor (reflex, then promotion).
                    int phase = step % 500;
                    bool playerTalking = phase < 120;
                    bool marcusTalking = phase >= 180 && phase < 420;
                    bool ethanTalking = phase >= 300 && phase < 460;

                    for (int i = 0; i < 3; i++)
                        _room.ReportParticipant(
                            listeners[i], CrowdPointOf(listeners[i]), Vector3.forward, false, 0f, "L");
                    _room.ReportParticipant(
                        OtherKey, CrowdPointOf(OtherKey), Vector3.back, marcusTalking,
                        marcusTalking ? 0.5f : 0f, "Marcus");
                    _room.ReportParticipant(
                        SpeakerKey, CrowdPointOf(SpeakerKey), Vector3.back, ethanTalking,
                        ethanTalking ? 0.5f : 0f, "Ethan");
                    _room.ReportPlayer(
                        CrowdPointOf(0), Vector3.forward,
                        serverSpeaking: false, localActive: playerTalking, localLevel: playerTalking ? 0.4f : 0f,
                        addresseeKey: OtherKey, typedThisTick: false);
                    Derive();

                    for (int i = 0; i < 3; i++)
                    {
                        ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
                        ConversationGazeState state = directors[i].Tick(
                            _room.Current, in selves[i], in tuning, Dt, ref randoms[i]);

                        int target = !state.Active
                            ? 0
                            : state.Focus == ConversationGazeFocus.Player
                                ? ConversationRoomModel.PlayerKey
                                : state.CharacterKey;

                        // A listener whose eyes were already there turns nothing, so it is
                        // deliberately booked against nobody — see OpenReaction. Counting it
                        // would be asserting against a rule this module states outright.
                        bool arrival = target != 0 && state.Look != ConversationGazeLook.EyesOnly;

                        if (state.Active && state.Look == ConversationGazeLook.Reflex)
                        {
                            if (target == flicked[i]) continue;

                            flicked[i] = target;
                            if (arrival) landings.Add((listeners[i], target, _now, state.Look));
                            continue;
                        }

                        flicked[i] = 0;
                        if (target == committed[i]) continue;

                        committed[i] = target;
                        if (arrival) landings.Add((listeners[i], target, _now, state.Look));
                    }
                }

                for (int a = 0; a < landings.Count; a++)
                for (int b = a + 1; b < landings.Count; b++)
                {
                    if (landings[a].Listener == landings[b].Listener) continue;
                    if (landings[a].Target != landings[b].Target) continue;

                    // One frame of slack at each end: a booked moment is observed on the first
                    // frame past it, so two slots exactly MinSeparation apart are seen anywhere
                    // within a frame of that. The epsilon is float accumulation over 25 seconds.
                    float apart = Mathf.Abs(landings[a].At - landings[b].At);
                    if (apart >= MinSeparation - Dt - 0.001f) continue;

                    Assert.Fail(
                        $"Seed {seed}: {landings[a].Listener} ({landings[a].Look} at {landings[a].At:0.00}s) and " +
                        $"{landings[b].Listener} ({landings[b].Look} at {landings[b].At:0.00}s) both arrived on " +
                        $"{landings[a].Target} {apart:0.000}s apart, inside the {MinSeparation:0.00}s the room " +
                        "is supposed to keep between two faces turning to one.");
                }
            }
        }

        /// <summary>A crowd: three listeners round two speakers and the player, all reachable.</summary>
        private static Vector3 CrowdPointOf(int key) => key switch
        {
            21 => new Vector3(-2.2f, 1.6f, -0.4f),
            22 => new Vector3(-0.7f, 1.6f, -1.1f),
            23 => new Vector3(0.9f, 1.6f, -1.2f),
            OtherKey => new Vector3(-0.9f, 1.6f, 2.1f),
            SpeakerKey => new Vector3(1.4f, 1.6f, 1.9f),
            _ => new Vector3(0.2f, 1.6f, 0.6f)
        };

        // ── Arrival ──────────────────────────────────────────────────────────

        /// <summary>Pins rule 6: somebody walking in is noticed by everybody, but not by everybody at once.</summary>
        [Test]
        public void SomebodyWalkingIn_IsNoticedByEveryListenerOneAfterAnother()
        {
            const float MinSeparation = ConversationGazeTuning.DefaultMinSeparationSeconds;
            const int NewcomerKey = 60;

            ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
            var directors = new ConversationGazeDirector[3];
            var randoms = new DeterministicEmbodimentRandom[3];
            var selves = new ConversationGazeSelf[3];
            var noticed = new float[3];

            for (int i = 0; i < 3; i++)
            {
                directors[i] = new ConversationGazeDirector();
                randoms[i] = new DeterministicEmbodimentRandom(41u + (uint)i);
                selves[i] = Self(key: 21 + i, point: new Vector3((i - 1) * 1.5f, 1.6f, 0f), forward: Vector3.right);
                noticed[i] = -1f;
            }

            for (int step = 0; step < 300; step++)
            {
                for (int i = 0; i < 3; i++)
                    _room.ReportParticipant(selves[i].Key, selves[i].HeadPoint, Vector3.forward, false, 0f, "L");
                // Nobody has walked in for the first second: the room every listener woke up in
                // is not an arrival, or a scene load would be eight of them.
                if (step >= 60)
                    _room.ReportParticipant(NewcomerKey, new Vector3(0f, 1.6f, 3f), Vector3.back, false, 0f, "N");
                Derive();

                for (int i = 0; i < 3; i++)
                {
                    ConversationGazeState state = directors[i].Tick(
                        _room.Current, in selves[i], in tuning, Dt, ref randoms[i]);
                    if (noticed[i] < 0f && state.Active && state.CharacterKey == NewcomerKey)
                        noticed[i] = _now;
                }
            }

            for (int i = 0; i < 3; i++)
                Assert.That(noticed[i], Is.GreaterThan(0f), $"Listener {i} never looked at the person who walked in.");

            for (int i = 0; i < 3; i++)
            for (int j = i + 1; j < 3; j++)
                Assert.That(Mathf.Abs(noticed[i] - noticed[j]), Is.GreaterThanOrEqualTo(MinSeparation - Dt * 1.5f),
                    $"Listeners {i} and {j} noticed the newcomer together.");
        }

        // ── The moments a room shares, each listener takes on its own ─────────

        /// <summary>
        ///     Pins the post-turn hold: a character that has just finished speaking keeps looking
        ///     at whoever it spoke to, and never glances at a bystander in the gap before the reply.
        /// </summary>
        [Test]
        public void AfterItsOwnTurn_TheCharacterKeepsLookingAtWhoItSpokeTo()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(41u);
            ConversationGazeTuning tuning = Tuning(socialIdle: true, socialIntervalMin: 0.1f, socialIntervalMax: 0.2f);

            Run(director, ref random, 3f, Self(inOwnTurn: true, aimTargetKey: ConversationRoomModel.PlayerKey), tuning);

            ConversationGazeState first = default;
            bool sawCharacter = false;
            for (int i = 0; i < 120; i++)
            {
                ConversationGazeState state = Run(
                    director, ref random, Dt, Self(aimTargetKey: ConversationRoomModel.PlayerKey), tuning);
                if (i == 0) first = state;
                if (state.Active && state.Focus == ConversationGazeFocus.Character) sawCharacter = true;
            }

            Assert.IsTrue(first.Active, "The look must not lapse the frame the turn ends.");
            Assert.That(first.Focus, Is.EqualTo(ConversationGazeFocus.Player));
            Assert.IsFalse(sawCharacter, "Finishing a turn is not an occasion to glance at somebody else.");
        }

        /// <summary>Pins the speaker-check timing: never in the first three seconds of a turn.</summary>
        [Test]
        public void ASpeakerCheck_DoesNotStartInTheFirstBreathOfATurn()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(42u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 0.1f, audienceIntervalMax: 0.2f, audienceDuration: 0.6f);

            bool checkedEarly = false;
            int steps = Mathf.RoundToInt(2.9f / Dt);
            for (int i = 0; i < steps; i++)
            {
                ConversationGazeState state = Run(director, ref random, Dt, Self(inOwnTurn: true), tuning);
                if (state.Look == ConversationGazeLook.SpeakerCheck) checkedEarly = true;
            }

            Assert.IsFalse(checkedEarly);
        }

        /// <summary>Pins the speaker-check ending rule: a turn that is wrapping up holds its addressee.</summary>
        [Test]
        public void ASpeakerCheck_EndsTheMomentTheTurnIsEnding()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(43u);
            ConversationGazeTuning tuning = Tuning(
                audienceChecks: true, audienceIntervalMin: 0.1f, audienceIntervalMax: 0.2f, audienceDuration: 3f);

            // The earliest check is 3 s in, and the first arming adds up to 6 s of phase spread.
            bool checkedMid = false;
            for (int i = 0; i < Mathf.RoundToInt(14f / Dt); i++)
            {
                ConversationGazeState state = Run(director, ref random, Dt, Self(inOwnTurn: true), tuning);
                if (state.Look == ConversationGazeLook.SpeakerCheck) checkedMid = true;
            }
            Assert.IsTrue(checkedMid, "Sanity: a long turn with two listeners produces a check.");

            ConversationGazeState ending = Run(director, ref random, Dt, Self(inOwnTurn: true, turnEnding: true), tuning);
            Assert.That(ending.Look, Is.Not.EqualTo(ConversationGazeLook.SpeakerCheck));
            Assert.IsFalse(ending.Active);
        }

        /// <summary>Pins the silence gate: idle glances between people wait for the room to be quiet.</summary>
        [Test]
        public void SocialIdle_WaitsForTheRoomToBeQuiet()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(44u);
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 0.1f, socialIntervalMax: 0.2f, decaySeconds: 0.5f);

            Run(director, ref random, 2f, Self(), tuning, speakerTalking: true);
            bool glancedTooSoon = false;
            for (int i = 0; i < Mathf.RoundToInt(3.5f / Dt); i++)
            {
                ConversationGazeState state = Run(director, ref random, Dt, Self(), tuning);
                if (state.Look == ConversationGazeLook.SocialIdle) glancedTooSoon = true;
            }

            Assert.IsFalse(glancedTooSoon, "Three seconds after the last word is a pause, not a quiet room.");
        }

        /// <summary>
        ///     Pins the promotion booking: when a challenger takes the floor from the player, three
        ///     listeners move their heads to the new speaker one after another, not on the frame the
        ///     floor changed hands.
        /// </summary>
        [Test]
        public void WhenAChallengerTakesTheFloor_ListenersTurnOneAfterAnother()
        {
            const float MinSeparation = ConversationGazeTuning.DefaultMinSeparationSeconds;
            const int ChallengerKey = 50;

            for (uint seed = 1; seed <= 8; seed++)
            {
                SetUp();
                ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
                var directors = new ConversationGazeDirector[3];
                var randoms = new DeterministicEmbodimentRandom[3];
                var selves = new ConversationGazeSelf[3];
                var turned = new float[3];
                float floorMoved = -1f;

                for (int i = 0; i < 3; i++)
                {
                    directors[i] = new ConversationGazeDirector();
                    randoms[i] = new DeterministicEmbodimentRandom(seed * 23u + (uint)i);
                    selves[i] = Self(key: 61 + i, point: new Vector3((i - 1) * 1.5f, 1.6f, 0f), forward: Vector3.right);
                    turned[i] = -1f;
                }

                for (int step = 0; step < 420; step++)
                {
                    // The player holds the floor for 3 s; the challenger starts at 2 s and keeps
                    // going, taking the floor after the interruption rule.
                    bool playerTalking = step < 180;
                    bool challengerTalking = step >= 120;
                    for (int i = 0; i < 3; i++)
                        _room.ReportParticipant(selves[i].Key, selves[i].HeadPoint, Vector3.forward, false, 0f, "L");
                    _room.ReportParticipant(
                        ChallengerKey, new Vector3(3f, 1.6f, -3f), Vector3.back, challengerTalking,
                        challengerTalking ? 0.5f : 0f, "C");
                    ReportPlayer(speaking: playerTalking);
                    Derive();
                    if (floorMoved < 0f && _room.Current.FloorKey == ChallengerKey) floorMoved = _now;

                    for (int i = 0; i < 3; i++)
                    {
                        ConversationGazeState state = directors[i].Tick(
                            _room.Current, in selves[i], in tuning, Dt, ref randoms[i]);
                        bool headOnChallenger = state.Active && state.CharacterKey == ChallengerKey &&
                                                state.Look != ConversationGazeLook.Reflex &&
                                                state.Look != ConversationGazeLook.EyesOnly;
                        if (turned[i] < 0f && headOnChallenger) turned[i] = _now;
                    }
                }

                Assert.That(floorMoved, Is.GreaterThan(0f), "Sanity: the challenger took the floor.");
                for (int i = 0; i < 3; i++)
                    Assert.That(turned[i], Is.GreaterThan(0f), $"Listener {i} never turned to the interrupter (seed {seed}).");

                for (int i = 0; i < 3; i++)
                for (int j = i + 1; j < 3; j++)
                    Assert.That(Mathf.Abs(turned[i] - turned[j]), Is.GreaterThanOrEqualTo(MinSeparation - Dt * 1.5f),
                        $"Listeners {i} and {j} turned to the interrupter together (seed {seed}).");
            }
        }

        /// <summary>
        ///     Pins the hand-off: once a listener's eyes have gone to an interrupter, the look never
        ///     returns to the previous speaker for the beat between the floor moving and the head's
        ///     own booked moment. Measured in the garden: eyes to Sofia at 27.18 s, back to the
        ///     player at 27.38 s, head to Sofia at 27.72 s — a flick nobody decided.
        /// </summary>
        [Test]
        public void OnceTheEyesHaveGoneToAnInterrupter_TheLookDoesNotFlickBack()
        {
            const int ChallengerKey = 50;
            for (uint seed = 1; seed <= 8; seed++)
            {
                SetUp();
                var director = new ConversationGazeDirector();
                var random = new DeterministicEmbodimentRandom(seed * 7u);
                ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
                ConversationGazeSelf self = Self(point: new Vector3(0f, 1.6f, 0f), forward: Vector3.right);

                bool onChallenger = false;
                bool flickedBack = false;
                bool headOnChallenger = false;
                for (int step = 0; step < 420; step++)
                {
                    bool playerTalking = step < 180;
                    bool challengerTalking = step >= 120;
                    ReportCharacter(SelfKey);
                    _room.ReportParticipant(
                        ChallengerKey, new Vector3(3f, 1.6f, -3f), Vector3.back, challengerTalking,
                        challengerTalking ? 0.5f : 0f, "C");
                    // The player is addressing somebody else, so the challenger is a true interrupter.
                    ReportPlayer(speaking: playerTalking, addressee: SelfKey);
                    Derive();

                    ConversationGazeState state = director.Tick(_room.Current, in self, in tuning, Dt, ref random);
                    bool lookingAtChallenger = state.Active && state.CharacterKey == ChallengerKey;
                    if (lookingAtChallenger)
                    {
                        onChallenger = true;
                        if (state.Look != ConversationGazeLook.Reflex && state.Look != ConversationGazeLook.EyesOnly)
                            headOnChallenger = true;
                    }
                    else if (onChallenger && !headOnChallenger && state.Active &&
                             state.Focus == ConversationGazeFocus.Player)
                    {
                        flickedBack = true;
                    }
                }

                Assert.IsTrue(headOnChallenger, $"The head never arrived on the interrupter (seed {seed}).");
                Assert.IsFalse(flickedBack, $"The look went back to the player between eyes and head (seed {seed}).");
            }
        }

        /// <summary>
        ///     Pins the expected answer: the addressee answering a player who has finished is the
        ///     next turn, not an interruption — the listener turns to them on an ordinary reaction,
        ///     with no reflex-and-promotion two-step.
        /// </summary>
        [Test]
        public void TheAddresseeAnswering_IsNotTreatedAsAnInterrupter()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(9u);
            ConversationGazeTuning tuning = Tuning(maxYaw: 180f);
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, playerTalking: true, addressee: SpeakerKey);
            bool sawReflex = false;
            float landed = -1f;
            for (int i = 0; i < 120; i++)
            {
                // The player has stopped; the addressee answers within the floor hold.
                ConversationGazeState state = Run(director, ref random, Dt, self, tuning, speakerTalking: true, addressee: SpeakerKey);
                if (state.Look == ConversationGazeLook.Reflex) sawReflex = true;
                if (landed < 0f && state.Active && state.CharacterKey == SpeakerKey &&
                    state.Look == ConversationGazeLook.Attention)
                    landed = i * Dt;
            }

            Assert.IsFalse(sawReflex, "An expected answer is not an interruption.");
            Assert.That(landed, Is.GreaterThan(0f).And.LessThan(1.2f),
                "The listener turns to the answerer on an ordinary reaction, within about a second.");
        }

        // ── Nobody decided that flick ────────────────────────────────────────

        /// <summary>
        ///     Pins the eligibility hysteresis. Two people standing at the edge of the attention
        ///     distance or angle are not standing still: they breathe, shift their weight, and are
        ///     reported through a head bone an idle clip sways. A gate with one threshold turns
        ///     that into a decision several times a second, and the character drops and re-takes
        ///     the look it is in the middle of.
        /// </summary>
        /// <remarks>
        ///     Red before the fix: the boundary crossings alone stood the listener down on roughly
        ///     half the ticks of the oscillation.
        /// </remarks>
        [Test]
        public void ASpeakerHoveringOnTheEdgeOfTheGates_DoesNotDropAndRetakeTheLook()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(40u);
            ConversationGazeTuning tuning = Tuning(maxDistance: 10f, maxYaw: 100f);
            ConversationGazeSelf self = Self();

            // Settled on the speaker from somewhere comfortably inside both gates.
            ConversationGazeState settled = default;
            for (int i = 0; i < 120; i++)
                settled = StepWithSpeakerAt(director, ref random, in self, in tuning, PointAt(5f, 40f), speaking: true);

            Assert.IsTrue(settled.Active && settled.CharacterKey == SpeakerKey, "Premise: the listener is on the speaker.");

            // Now they stand at the limit of both gates, moving the way a person standing does.
            for (int i = 0; i < 120; i++)
            {
                float wobble = i * 0.35f;
                ConversationGazeState state = StepWithSpeakerAt(
                    director, ref random, in self, in tuning,
                    PointAt(10f + 0.3f * Mathf.Sin(wobble), 100f + 3f * Mathf.Sin(wobble * 0.7f)),
                    speaking: true);

                Assert.IsTrue(state.Active && state.CharacterKey == SpeakerKey,
                    $"Tick {i}: the look was dropped by a speaker hovering on the threshold ({state.Reason}).");
            }
        }

        /// <summary>
        ///     The negative control the case above needs: the margin is a margin, not an
        ///     exemption. Somebody who actually walks out of range is let go.
        /// </summary>
        [Test]
        public void ASpeakerWhoWalksClearlyOutOfRange_IsLetGoDespiteTheHysteresis()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(41u);
            ConversationGazeTuning tuning = Tuning(maxDistance: 10f, maxYaw: 100f);
            ConversationGazeSelf self = Self();

            ConversationGazeState settled = default;
            for (int i = 0; i < 120; i++)
                settled = StepWithSpeakerAt(director, ref random, in self, in tuning, PointAt(5f, 40f), speaking: true);
            Assert.IsTrue(settled.Active, "Premise: the listener is on the speaker.");

            ConversationGazeState state = default;
            for (int i = 0; i < 120; i++)
                state = StepWithSpeakerAt(director, ref random, in self, in tuning, PointAt(12f, 40f), speaking: true);

            Assert.IsFalse(state.Active, "Twelve metres is past every margin; the look should have been given up.");
            Assert.That(state.Reason, Is.EqualTo(ConversationGazeStandDown.TooFar));
        }

        /// <summary>
        ///     Pins the sustain on the line-of-sight gate. The rays are cast ten times a second,
        ///     so one measurement is exactly the resolution at which a passer-by, a swinging arm
        ///     or a door frame reads as a wall — and a single measurement must not end a look.
        ///     Half a second of real wall must.
        /// </summary>
        /// <remarks>Red before the fix: the first occluded measurement stood the listener down.</remarks>
        [Test]
        public void AnOcclusionThatFlickers_DoesNotEndTheLook_AndOneThatLastsDoes()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(42u);
            var occluded = new ConversationOcclusionSet();
            ConversationGazeTuning tuning = Tuning();
            ConversationGazeSelf self = Self(occluded: occluded);

            ConversationGazeState settled = default;
            for (int i = 0; i < 120; i++)
                settled = StepWithSpeakerAt(director, ref random, in self, in tuning, PointAt(5f, 40f), speaking: true);
            Assert.IsTrue(settled.Active && settled.CharacterKey == SpeakerKey, "Premise: the listener is on the speaker.");

            // Something crosses the line of sight ten times a second, the way anything moving
            // between two people in a room does.
            float hiddenSince = -1f;
            for (int i = 0; i < 120; i++)
            {
                bool hidden = i / 6 % 2 == 0;
                hiddenSince = SetOccluded(occluded, hidden, hiddenSince);
                ConversationGazeState state = StepWithSpeakerAt(
                    director, ref random, in self, in tuning, PointAt(5f, 40f), speaking: true);

                Assert.IsTrue(state.Active && state.CharacterKey == SpeakerKey,
                    $"Tick {i}: one broken ray ended a look that was going fine ({state.Reason}).");
            }

            // And now a wall.
            ConversationGazeState walled = default;
            for (int i = 0; i < 60; i++)
            {
                hiddenSince = SetOccluded(occluded, hidden: true, hiddenSince);
                walled = StepWithSpeakerAt(director, ref random, in self, in tuning, PointAt(5f, 40f), speaking: true);
            }

            Assert.IsFalse(walled.Active, "The speaker has been out of sight for half a second and is still being watched.");
            Assert.That(walled.Reason, Is.EqualTo(ConversationGazeStandDown.NoLineOfSight));
        }

        /// <summary>
        ///     Pins the withdrawal of a stale responder booking. The look to whoever will answer
        ///     is booked with a latency, and in that window the player can turn from one character
        ///     to another. Paying the old booking anyway is a turn of the head toward somebody the
        ///     conversation stopped expecting half a second ago, followed by a second turn.
        /// </summary>
        /// <remarks>
        ///     Red before the fix: the listener looked at the character the player had stopped
        ///     addressing before arriving at the one it had moved to.
        /// </remarks>
        [Test]
        public void WhenTheAddresseeChangesBeforeTheResponderLookLands_TheOldOneIsNeverVisited()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(43u);
            // A slow, narrow reaction: the point of the case is the window between booking the
            // look to whoever answers and paying it, so that window has to be long enough to act in.
            ConversationGazeTuning tuning = Tuning(reactionMedian: 0.5f, reactionSigma: 0.05f);
            ConversationGazeSelf self = Self();

            ConversationGazeState watching = Run(
                director, ref random, 2f, self, tuning, playerTalking: true, addressee: SpeakerKey);
            Assert.That(watching.Focus, Is.EqualTo(ConversationGazeFocus.Player), "Premise: watching the player talk.");

            // The player stops. The floor stays theirs through its hold and then empties, which is
            // the moment the look to whoever answers is booked — for the character they addressed.
            for (int i = 0; i < 600 && _room.Current.FloorKey != ConversationRoomModel.NobodyKey; i++)
                Run(director, ref random, Dt, self, tuning, addressee: SpeakerKey);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey), "Premise: the turn ended.");
            Run(director, ref random, 0.1f, self, tuning, addressee: SpeakerKey);

            // Before it lands, the player turns to somebody else.
            bool visitedTheFormerAddressee = false;
            ConversationGazeState state = default;
            for (int i = 0; i < 360; i++)
            {
                state = Run(director, ref random, Dt, self, tuning, addressee: OtherKey);
                if (state.Active && state.CharacterKey == SpeakerKey) visitedTheFormerAddressee = true;
            }

            Assert.IsFalse(visitedTheFormerAddressee,
                "The look was paid to the character the player had already turned away from.");
            Assert.That(state.CharacterKey, Is.EqualTo(OtherKey),
                "The look never reached the character the player actually turned to.");
        }

        /// <summary>
        ///     Pins the other half of that withdrawal: the booking goes back to the room, it is
        ///     not merely dropped here. A cancelled beat left in the lane goes on spacing whatever
        ///     is booked at that face afterwards, so the next turn's head movement — the same
        ///     listener, the same person — arrives a whole separation late.
        /// </summary>
        /// <remarks>
        ///     <para>
        ///         Asked of the room rather than of the decision: the cost of the defect is a
        ///         delay measured against a randomly drawn latency, and nothing observable from
        ///         outside says what that draw was. A probe booked far later than any draw, with a
        ///         separation wider than the whole scenario, answers the only question there is —
        ///         whether the withdrawn booking is still standing in the lane.
        ///     </para>
        ///     <para>Red before the fix: the probe came back pushed by the full separation.</para>
        /// </remarks>
        [Test]
        public void WhenAResponderBookingIsWithdrawn_TheRoomStopsSpacingAgainstIt()
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(44u);
            ConversationGazeTuning tuning = Tuning(reactionMedian: 0.5f, reactionSigma: 0.05f);
            ConversationGazeSelf self = Self();

            Run(director, ref random, 2f, self, tuning, playerTalking: true, addressee: SpeakerKey);
            for (int i = 0; i < 600 && _room.Current.FloorKey != ConversationRoomModel.NobodyKey; i++)
                Run(director, ref random, Dt, self, tuning, addressee: SpeakerKey);

            Assert.That(_room.Current.FloorKey, Is.EqualTo(ConversationRoomModel.NobodyKey),
                "Premise: the turn ended.");

            // The look to whoever answers is booked here, in the lane its character owns.
            Run(director, ref random, 0.1f, self, tuning, addressee: SpeakerKey);

            // The player turns to somebody else, so that booking is called off.
            Run(director, ref random, 0.2f, self, tuning, addressee: OtherKey);

            // Later than any latency this tuning can draw, and spaced by more than the scenario
            // lasts: the probe can only move if something is still standing in that lane.
            float probeWantedAt = _now + 50f;
            float probe = _room.ReserveReaction(
                ConversationRoomModel.LaneFor(SpeakerKey), 909, 9090, probeWantedAt, 100f);

            Assert.That(probe, Is.EqualTo(probeWantedAt).Within(0.001f),
                "The cancelled responder booking was still holding a moment in that character's lane.");
        }

        /// <summary>
        ///     Pins the one-glance rule. The curiosity glance at the player and the conversation's
        ///     idle glances between people are two halves of the same idle life, on two clocks;
        ///     while one of them owns the gaze the other stands aside, or the character glances
        ///     away from a glance.
        /// </summary>
        /// <remarks>
        ///     Red before the fix: the director glanced at the people in the room regardless of
        ///     what already had the gaze.
        /// </remarks>
        [Test]
        public void WhileAScriptedLookOwnsTheGaze_TheIdleGlancesBetweenPeopleStandAside()
        {
            ConversationGazeTuning tuning = Tuning(
                socialIdle: true, socialIntervalMin: 3f, socialIntervalMax: 3f, socialDuration: 1f);

            Assert.IsFalse(
                RanASocialGlance(new DeterministicEmbodimentRandom(44u), Self(scriptedLookActive: true), in tuning),
                "Something else already had the gaze and the conversation glanced anyway.");

            Assert.IsTrue(
                RanASocialGlance(new DeterministicEmbodimentRandom(44u), Self(), in tuning),
                "Negative control: with nothing else looking, the same twenty seconds must produce a glance.");
        }

        /// <summary>Runs twenty seconds of quiet room and says whether an idle glance came out of it.</summary>
        /// <param name="random">This character's stream.</param>
        /// <param name="self">The character, which is what the two halves of the case differ in.</param>
        /// <param name="tuning">Idle cadence.</param>
        private bool RanASocialGlance(
            DeterministicEmbodimentRandom random,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning)
        {
            var director = new ConversationGazeDirector();
            for (int i = 0; i < 1200; i++)
            {
                ConversationGazeState state = Step(director, ref random, in self, in tuning);
                if (state.Active && state.Look == ConversationGazeLook.SocialIdle) return true;
            }

            return false;
        }

        /// <summary>A point <paramref name="distance" /> away at <paramref name="yawDegrees" /> off the listener's forward.</summary>
        private static Vector3 PointAt(float distance, float yawDegrees) =>
            PointOf(SelfKey) + Quaternion.Euler(0f, yawDegrees, 0f) * Vector3.forward * distance;

        /// <summary>
        ///     One tick of a two-person room with the speaker placed where the case wants them.
        ///     The geometry cases move somebody around the gates, which the fixed positions the
        ///     rest of the suite reports cannot express.
        /// </summary>
        private ConversationGazeState StepWithSpeakerAt(
            ConversationGazeDirector director,
            ref DeterministicEmbodimentRandom random,
            in ConversationGazeSelf self,
            in ConversationGazeTuning tuning,
            Vector3 speakerPoint,
            bool speaking)
        {
            _room.ReportParticipant(SelfKey, self.HeadPoint, self.Forward, false, 0f, "self");
            _room.ReportParticipant(SpeakerKey, speakerPoint, Vector3.back, speaking, speaking ? 0.5f : 0f, "speaker");
            Derive();
            return director.Tick(_room.Current, in self, in tuning, Dt, ref random);
        }

        /// <summary>
        ///     Drives the occlusion set the way a run of measurements would: the clock on somebody
        ///     out of sight starts when they go and survives every measurement that still cannot
        ///     see them, and coming back into view forgets it.
        /// </summary>
        /// <param name="occluded">The set under test.</param>
        /// <param name="hidden">Whether this measurement found the speaker behind something.</param>
        /// <param name="hiddenSince">The running clock, or -1 while they are visible.</param>
        /// <returns>The clock to pass back in on the next tick.</returns>
        private float SetOccluded(ConversationOcclusionSet occluded, bool hidden, float hiddenSince)
        {
            occluded.Clear();
            if (!hidden) return -1f;

            float since = hiddenSince < 0f ? _now : hiddenSince;
            occluded.MarkOccludedForTests(SpeakerKey, since);
            return since;
        }

        [TestCase(false)]
        [TestCase(true)]
        public void SpeechStoppingBeforeTheReaction_ReleasesItsRoomReservation(bool restart)
        {
            var director = new ConversationGazeDirector();
            var random = new DeterministicEmbodimentRandom(44u);
            ConversationGazeTuning tuning = Tuning(reactionMedian: 0.5f, reactionSigma: 0.05f);
            ConversationGazeSelf self = Self();
            Step(director, ref random, in self, in tuning, speakerTalking: true);
            Step(director, ref random, in self, in tuning, speakerTalking: false);
            if (restart)
            {
                Step(director, ref random, in self, in tuning, speakerTalking: true);
                Step(director, ref random, in self, in tuning, speakerTalking: false);
            }
            float wanted = _now + 50f;
            float reserved = _room.ReserveReaction(ConversationRoomModel.LaneFor(SpeakerKey), 909, 9090, wanted, 100f);
            Assert.That(reserved, Is.EqualTo(wanted).Within(0.001f), "A cancelled onset still spaces later listeners.");
        }

    }
}
