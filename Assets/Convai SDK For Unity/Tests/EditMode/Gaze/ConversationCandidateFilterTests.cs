using Convai.Modules.Gaze.Core.Conversation;
using Convai.Domain.Embodiment.Semantics;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     Which people the arbiter is allowed to see while the conversation has already decided
    ///     who this character is looking at.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The rule lives in a static helper rather than inline in
    ///         <c>ConvaiGazeController.GatherCandidates</c> for one reason: the defect it fixes is
    ///         a substitution the arbiter makes on a single frame, and a case that had to stand up
    ///         a character, a rig, a registry and a room to reach that frame would be a scene test
    ///         about scene wiring. This is the decision, on its own, in the shape the caller asks
    ///         it in.
    ///     </para>
    ///     <para>
    ///         Every case here was red before the filter existed: the controller offered every
    ///         registered character to the arbiter on every tick, whatever the conversation had
    ///         decided.
    ///     </para>
    /// </remarks>
    public sealed class ConversationCandidateFilterTests
    {
        /// <summary>The character the conversation decided on.</summary>
        private const int AttendedKey = 77;

        /// <summary>Somebody else standing in the room, who decided nothing.</summary>
        private const int BystanderKey = 91;

        private static ConversationGazeState Attending(
            ConversationGazeFocus focus,
            int characterKey,
            ConversationGazeLook look = ConversationGazeLook.Attention) => new()
        {
            Active = true,
            Focus = focus,
            CharacterKey = characterKey,
            Look = look
        };

        /// <summary>
        ///     Pins the whole of it: while a character is being attended it is the only person on
        ///     the table, and everything that is not a person is untouched.
        /// </summary>
        [Test]
        public void WhileACharacterIsAttended_NobodyElseIsOffered()
        {
            ConversationGazeState attention = Attending(ConversationGazeFocus.Character, AttendedKey);

            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, AttendedKey, in attention),
                "The person the conversation decided on has to reach the arbiter, or nothing happens at all.");
            Assert.IsFalse(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, BystanderKey, in attention),
                "A bystander was offered alongside the attended speaker and can take the look on relevance alone.");
            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.WorldObject, 0, in attention),
                "Props are not people and lose to the conversation on priority; taking them away empties the room.");
            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Player, 0, in attention),
                "The player anchor is governed by the policy switch, not by this.");
        }

        /// <summary>
        ///     Pins the player case. When the look is on the player the player is the whole of the
        ///     answer, and a character candidate can only ever be a substitution for it.
        /// </summary>
        [Test]
        public void WhileThePlayerIsAttended_NoCharacterIsOffered()
        {
            ConversationGazeState attention = Attending(ConversationGazeFocus.Player, 0);

            Assert.IsFalse(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, AttendedKey, in attention));
            Assert.IsFalse(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, BystanderKey, in attention));
            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.WorldObject, 0, in attention));
        }

        /// <summary>
        ///     Pins the missing-candidate rule. When the attended character cannot be built this
        ///     tick — the registry has not caught up, the head anchor is mid-rebind — the answer is
        ///     an empty room, not a different room: the arbiter's target-loss hold then carries the
        ///     look for a moment instead of somebody else arriving in their place.
        /// </summary>
        [Test]
        public void WhenTheAttendedCharacterCannotBeBuilt_NobodyTakesTheirPlace()
        {
            ConversationGazeState attention = Attending(ConversationGazeFocus.Character, AttendedKey);

            // The attended entry is simply not among the ones being offered this tick.
            Assert.IsFalse(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, BystanderKey, in attention));
            Assert.IsFalse(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, 0, in attention),
                "A candidate with no room key is nobody the conversation named.");
        }

        /// <summary>
        ///     The idle glance, the speaker's audience check and the interruption reflex all name
        ///     one person too, and each of them is the decision for the tick it runs in. A filter
        ///     that only recognised plain attention would starve every one of them.
        /// </summary>
        [Test]
        public void TheOtherLooksThatNameSomebody_StillReachTheArbiter()
        {
            foreach (ConversationGazeLook look in new[]
                     {
                         ConversationGazeLook.SocialIdle,
                         ConversationGazeLook.SpeakerCheck,
                         ConversationGazeLook.AudienceCheck,
                         ConversationGazeLook.Reflex,
                         ConversationGazeLook.EyesOnly
                     })
            {
                ConversationGazeState attention = Attending(ConversationGazeFocus.Character, AttendedKey, look);

                Assert.IsTrue(
                    ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, AttendedKey, in attention),
                    $"A {look} look named somebody and the candidate for them was withheld.");
                Assert.IsFalse(
                    ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, BystanderKey, in attention),
                    $"A {look} look is about one person; the rest of the room is not on offer.");
            }
        }

        /// <summary>
        ///     The negative control that makes the rest mean something. With no decision live, the
        ///     room is open and the arbiter arbitrates — which is how a character behaves for most
        ///     of its life.
        /// </summary>
        [Test]
        public void WithNoConversationDecision_EverybodyIsOffered()
        {
            ConversationGazeState none = ConversationGazeState.StoodDown(ConversationGazeStandDown.NoSpeaker);

            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, AttendedKey, in none));
            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.Character, BystanderKey, in none));
            Assert.IsTrue(ConversationCandidateFilter.ShouldOffer(GazeTargetKind.WorldObject, 0, in none));
        }
    }
}
