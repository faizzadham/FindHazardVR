using System.Collections.Generic;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyAnimation.Core;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage: <see cref="ActionEntry.Cue" /> tagging, back-compat deserialization
    ///     of untagged (pre-cue) assets, <see cref="ConvaiBodyAnimationSet.TryGetActionForCue" />
    ///     resolution, and the <see cref="ConversationalGesturePerformer" /> suppression truth
    ///     table — all pure-logic, no scene required.
    /// </summary>
    public class ConversationalGestureCueResolutionTests
    {
        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
        }

        private ConvaiBodyAnimationSet CreateSet()
        {
            var set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(set);
            return set;
        }

        private AnimationClip CreateClip(string name)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);
            return clip;
        }

        private static ActionEntry CreateTaggedAction(
            string name, AnimationClip clip, GestureCueKind cue)
        {
            var entry = new ActionEntry();
            entry.Initialize(name, clip, ActionMaskMode.UpperBody);
            entry.SetCue(cue);
            return entry;
        }

        [Test]
        public void TryGetActionForCue_TaggedSet_ResolvesCorrectEntry()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            ActionEntry wave = CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting);
            ActionEntry yes = CreateTaggedAction("yes", CreateClip("yes"), GestureCueKind.Affirmative);
            set.InitializeContent("Test", null, null, new List<ActionEntry> { wave, yes }, null);

            Assert.IsTrue(set.TryGetActionForCue(GestureCueKind.Greeting, out ActionEntry resolvedGreeting));
            Assert.AreSame(wave, resolvedGreeting);

            Assert.IsTrue(set.TryGetActionForCue(GestureCueKind.Affirmative, out ActionEntry resolvedAffirmative));
            Assert.AreSame(yes, resolvedAffirmative);
        }

        [Test]
        public void TryGetActionForCue_AllNoneSet_RefusesEveryCue()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry>
                {
                    CreateTaggedAction("like", CreateClip("like"), GestureCueKind.None),
                    CreateTaggedAction("wink", CreateClip("wink"), GestureCueKind.None)
                },
                null);

            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Affirmative, out _));
            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Negative, out _));
            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Greeting, out _));
            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Uncertain, out _));
            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Emphatic, out _));
            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Beat, out _));
        }

        [Test]
        public void TryGetActionForCue_NoneKind_AlwaysRefused()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.None) },
                null);

            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.None, out _));
        }

        [Test]
        public void TryGetActionForCue_TwoEntriesSameTag_ResolvesFirstInAuthoredOrder()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            ActionEntry first = CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting);
            ActionEntry second = CreateTaggedAction("bye", CreateClip("bye"), GestureCueKind.Greeting);
            set.InitializeContent("Test", null, null, new List<ActionEntry> { first, second }, null);

            Assert.IsTrue(set.TryGetActionForCue(GestureCueKind.Greeting, out ActionEntry resolved));
            Assert.AreSame(first, resolved, "First authored match must win, deterministically.");
        }

        [Test]
        public void TryGetActionForCue_InvalidTaggedEntry_IsSkipped()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            // No clip => IsValid is false even though it carries the cue tag.
            var invalid = new ActionEntry();
            invalid.Initialize("broken", null, ActionMaskMode.UpperBody);
            invalid.SetCue(GestureCueKind.Greeting);
            ActionEntry valid = CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting);

            set.InitializeContent("Test", null, null, new List<ActionEntry> { invalid, valid }, null);

            Assert.IsTrue(set.TryGetActionForCue(GestureCueKind.Greeting, out ActionEntry resolved));
            Assert.AreSame(valid, resolved);
        }

        // ── Back-compat: pre-cue serialized payloads ───────────────────────────

        [Test]
        public void ActionEntry_DeserializedFromLegacyJson_DefaultsToNoneCue()
        {
            // Payload shaped like an asset authored before the _cue field existed.
            const string legacyJson = @"{
                ""_actionName"": ""wave"",
                ""_aliases"": [""hi"", ""hello""],
                ""_maskMode"": 1,
                ""_loopMode"": 0,
                ""_loopCount"": 1,
                ""_speed"": 1.0,
                ""_suspendsLocomotion"": true,
                ""_interruptible"": true,
                ""_fadeInSecondsOverride"": -1.0,
                ""_fadeOutSecondsOverride"": -1.0
            }";

            var entry = new ActionEntry();
            JsonUtility.FromJsonOverwrite(legacyJson, entry);

            Assert.AreEqual(GestureCueKind.None, entry.Cue,
                "Untagged legacy asset must round-trip to Cue == None.");
            Assert.AreEqual("wave", entry.ActionName);
        }

        [Test]
        public void ConvaiBodyAnimationSet_LegacyDeserializedEntry_RefusesCuePlayback()
        {
            const string legacyJson = @"{
                ""_actionName"": ""wave"",
                ""_maskMode"": 1,
                ""_loopMode"": 0,
                ""_loopCount"": 1,
                ""_speed"": 1.0,
                ""_suspendsLocomotion"": true,
                ""_interruptible"": true,
                ""_fadeInSecondsOverride"": -1.0,
                ""_fadeOutSecondsOverride"": -1.0
            }";

            var entry = new ActionEntry();
            JsonUtility.FromJsonOverwrite(legacyJson, entry);
            // A legacy entry has no clip in this payload (clips are object refs, not JSON-portable
            // in this test), so IsValid is false regardless — but the cue gate must refuse first.
            Assert.AreEqual(GestureCueKind.None, entry.Cue);

            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null, new List<ActionEntry> { entry }, null);

            Assert.IsFalse(set.TryGetActionForCue(GestureCueKind.Greeting, out _),
                "An untagged legacy entry must never resolve for a cue.");
        }

        // ── Suppression truth table ─────────────────────────────────────────────

        private sealed class SuppressionInputs
        {
            public bool FullBodyAction;
            public bool TurningInPlace;
            public bool TalkFullBodyCoverage;
            public bool Moving;
            public bool TalkActive;
        }

        // Exercises the SHIPPED policy, not a test-local copy of it.
        private static GestureSuppression ComputeSuppression(SuppressionInputs inputs) =>
            ConversationalGesturePerformer.ComputeSuppression(
                inputs.FullBodyAction,
                inputs.TurningInPlace,
                inputs.TalkFullBodyCoverage,
                inputs.Moving,
                inputs.TalkActive);

        [Test]
        public void Suppression_Idle_IsNone()
        {
            Assert.AreEqual(GestureSuppression.None, ComputeSuppression(new SuppressionInputs()));
        }

        [Test]
        public void Suppression_FullBodyActionRunning_IsFullBody()
        {
            var inputs = new SuppressionInputs { FullBodyAction = true };
            Assert.AreEqual(GestureSuppression.FullBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_TurningInPlace_IsFullBody()
        {
            var inputs = new SuppressionInputs { TurningInPlace = true };
            Assert.AreEqual(GestureSuppression.FullBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_TalkFullBodyCoverage_IsFullBody()
        {
            var inputs = new SuppressionInputs { TalkFullBodyCoverage = true };
            Assert.AreEqual(GestureSuppression.FullBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_Locomotion_IsUpperBody()
        {
            var inputs = new SuppressionInputs { Moving = true };
            Assert.AreEqual(GestureSuppression.UpperBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_UpperBodyTalkActive_IsUpperBody()
        {
            var inputs = new SuppressionInputs { TalkActive = true };
            Assert.AreEqual(GestureSuppression.UpperBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_FullBodyBeatsUpperBody_WhenBothTrue()
        {
            var inputs = new SuppressionInputs { FullBodyAction = true, Moving = true };
            Assert.AreEqual(GestureSuppression.FullBody, ComputeSuppression(inputs));
        }

        [Test]
        public void Suppression_TalkFullBodyCoverageBeatsLocomotionUpperBody()
        {
            var inputs = new SuppressionInputs { TalkFullBodyCoverage = true, Moving = true, TalkActive = true };
            Assert.AreEqual(GestureSuppression.FullBody, ComputeSuppression(inputs));
        }

        // ── ConversationalGesturePerformer: TryPerform refusal matrix ───────────

        [Test]
        public void TryPerform_NoneKind_Refused()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting) },
                null);

            var performer = new ConversationalGesturePerformer(set, null, null, null);

            Assert.IsFalse(performer.TryPerform(new GestureCue(GestureCueKind.None)));
        }

        [Test]
        public void TryPerform_UnmappedKind_Refused()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting) },
                null);

            var performer = new ConversationalGesturePerformer(set, null, null, null);

            // No ActionLayer at all means TryPerform must refuse before touching layer state.
            Assert.IsFalse(performer.TryPerform(new GestureCue(GestureCueKind.Affirmative)));
        }

        [Test]
        public void TryPerform_NoActionLayer_AlwaysRefusedRegardlessOfMapping()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateTaggedAction("wave", CreateClip("wave"), GestureCueKind.Greeting) },
                null);

            var performer = new ConversationalGesturePerformer(set, null, null, null);

            Assert.IsFalse(performer.TryPerform(new GestureCue(GestureCueKind.Greeting)));
        }

        [Test]
        public void CurrentSuppression_NoLayersRegistered_IsNone()
        {
            var performer = new ConversationalGesturePerformer(null, null, null, null);
            Assert.AreEqual(GestureSuppression.None, performer.CurrentSuppression);
        }

        [Test]
        public void TryPerform_NullSet_Refused()
        {
            var performer = new ConversationalGesturePerformer(null, null, null, null);
            Assert.IsFalse(performer.TryPerform(new GestureCue(GestureCueKind.Greeting)));
        }

        [Test]
        public void Detach_WithoutPendingCue_DoesNotFireCompletedAndDoesNotThrow()
        {
            var performer = new ConversationalGesturePerformer(null, null, null, null);
            bool fired = false;
            performer.Completed += (_, _) => fired = true;

            performer.Detach();
            performer.Detach(); // idempotent — controller may unregister defensively

            Assert.IsFalse(fired, "Detach with no in-flight cue must not raise Completed.");
        }
    }
}
