using System.Collections.Generic;
using Convai.Runtime;
using Convai.Runtime.DynamicContext;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiDynamicContextTrackerTests
    {
        [Test]
        public void BuildCanonicalContext_EmptyTracker_ReturnsEmptyString()
        {
            var tracker = new ConvaiDynamicContextTracker();

            Assert.AreEqual(string.Empty, tracker.BuildCanonicalContext());
            Assert.IsFalse(tracker.HasTrackedContent);
        }

        [Test]
        public void SetState_PreservesInsertionOrder()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.SetState("Ammo", "6");

            Assert.AreEqual("Health is 100\nAmmo is 6", tracker.BuildCanonicalContext());
        }

        [Test]
        public void SetState_UpdatingExistingState_DoesNotDuplicateKey()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.SetState("Ammo", "6");
            tracker.SetState("Health", "50");

            Assert.AreEqual("Health is 50\nAmmo is 6", tracker.BuildCanonicalContext());
        }

        [Test]
        public void SetState_SameValue_ReturnsNoChange()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            ConvaiDynamicContextStateChangeResult result = tracker.SetState("Health", "100");

            Assert.IsFalse(result.HasChanged);
            Assert.AreEqual("Health is 100", tracker.BuildCanonicalContext());
        }

        [Test]
        public void BuildCanonicalContext_StatesAppearBeforeEvents()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.AddEvent("Door opened");
            tracker.SetState("Ammo", "6");
            tracker.AddEvent("Enemy spotted");

            Assert.AreEqual("Health is 100\nAmmo is 6\nDoor opened\nEnemy spotted", tracker.BuildCanonicalContext());
        }

        [Test]
        public void BuildCanonicalContext_ExcludedStates_AreOmittedButEventsRemain()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.SetState("Ammo", "6");
            tracker.AddEvent("Door opened");

            Assert.AreEqual(
                "Health is 100\nDoor opened",
                tracker.BuildCanonicalContext(new HashSet<string> { "Ammo" }));
        }

        [Test]
        public void RemoveState_RemovesTrackedLine()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.SetState("Ammo", "6");
            bool removed = tracker.RemoveState("Health");

            Assert.IsTrue(removed);
            Assert.AreEqual("Ammo is 6", tracker.BuildCanonicalContext());
        }

        [Test]
        public void Reset_ClearsAllTrackedContent()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.SetState("Health", "100");
            tracker.AddEvent("Door opened");
            tracker.Reset();

            Assert.AreEqual(string.Empty, tracker.BuildCanonicalContext());
            Assert.IsFalse(tracker.HasTrackedContent);
        }

        [Test]
        public void BuildPendingBatch_OrdersEventsBeforeChangedStateDeltaLines()
        {
            var tracker = new ConvaiDynamicContextTracker();
            tracker.SetState("Health", "100");

            tracker.StageState("Health", "80", ConvaiRespondMode.MustRespond);
            tracker.StageEvent("Door opened", ConvaiRespondMode.Auto);
            tracker.StageState("Ammo", "6", ConvaiRespondMode.Auto);

            ConvaiDynamicContextBatch batch = tracker.BuildPendingBatch();

            Assert.AreEqual(
                "Health is 80\nDoor opened\nHealth changed from 100 to 80\nAmmo is 6",
                batch.Text);
            Assert.AreEqual(ConvaiContextUpdateMode.Replace, batch.Mode);
            Assert.AreEqual(ConvaiRespondMode.MustRespond, batch.Reaction);
        }

        [Test]
        public void BuildPendingBatch_ChangedStateToClause_OnlyOmittedAboveThreeWords()
        {
            var threeWordTracker = new ConvaiDynamicContextTracker();
            threeWordTracker.SetState("Notes", "old");
            threeWordTracker.StageState("Notes", "one two three", ConvaiRespondMode.MustRespond);

            Assert.AreEqual(
                "Notes is one two three\nNotes changed from old to one two three",
                threeWordTracker.BuildPendingBatch().Text);

            var fourWordTracker = new ConvaiDynamicContextTracker();
            fourWordTracker.SetState("Notes", "old");
            fourWordTracker.StageState("Notes", "one two three four", ConvaiRespondMode.MustRespond);

            Assert.AreEqual(
                "Notes is one two three four\nNotes changed from old",
                fourWordTracker.BuildPendingBatch().Text);
        }

        [Test]
        public void StageEvent_DedupesWithinPendingBatch()
        {
            var tracker = new ConvaiDynamicContextTracker();

            Assert.IsTrue(tracker.StageEvent("Door opened", ConvaiRespondMode.Auto));
            Assert.IsFalse(tracker.StageEvent("Door opened", ConvaiRespondMode.MustRespond));

            ConvaiDynamicContextBatch batch = tracker.BuildPendingBatch();

            Assert.AreEqual("Door opened", batch.Text);
            Assert.AreEqual(ConvaiRespondMode.Auto, batch.Reaction);
        }

        [Test]
        public void BuildPendingBatch_ReactionEscalatesAndAttentionLastWins()
        {
            var tracker = new ConvaiDynamicContextTracker();

            tracker.StageState("Health", "100", ConvaiRespondMode.Silent);
            tracker.StageEvent("Door opened", ConvaiRespondMode.Auto);
            tracker.StageAttention("door", ConvaiRespondMode.Silent);
            tracker.StageAttention("lever", ConvaiRespondMode.MustRespond);

            ConvaiDynamicContextBatch batch = tracker.BuildPendingBatch();

            Assert.AreEqual(ConvaiRespondMode.MustRespond, batch.Reaction);
            Assert.IsTrue(batch.HasAttention);
            Assert.AreEqual("lever", batch.AttentionObject);
        }

        [Test]
        public void StageCanonicalResync_StagesCanonicalReplaceWithoutDeltas()
        {
            var tracker = new ConvaiDynamicContextTracker();
            tracker.StageState("Health", "80", ConvaiRespondMode.MustRespond);
            tracker.ClearPendingBatch();

            tracker.StageCanonicalResync();

            ConvaiDynamicContextBatch batch = tracker.BuildPendingBatch();

            Assert.AreEqual("Health is 80", batch.Text);
            Assert.AreEqual(ConvaiContextUpdateMode.Replace, batch.Mode);
            Assert.AreEqual(ConvaiRespondMode.Silent, batch.Reaction);
        }

        [Test]
        public void ClearPendingBatch_IsIdempotent()
        {
            var tracker = new ConvaiDynamicContextTracker();
            tracker.StageState("Health", "100", ConvaiRespondMode.Auto);

            tracker.ClearPendingBatch();
            tracker.ClearPendingBatch();

            Assert.IsFalse(tracker.HasPendingBatch);
            Assert.AreEqual("Health is 100", tracker.BuildCanonicalContext());
        }
    }
}
