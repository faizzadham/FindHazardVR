using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="ConvaiBodyAnimationSet.CollectFindings" /> is the single
    ///     validation model — every finding carries a severity assigned where it is raised, never
    ///     inferred from <see cref="BodyAnimationFinding.Message" />. The point of
    ///     <see cref="CollectFindings_KnownBadSet_ProducesExactlyThePinnedIdSet" /> is that ids are
    ///     the contract: pinning them is what makes it impossible for a reworded message to ever
    ///     change a severity again.
    /// </summary>
    internal sealed class BodyAnimationFindingModelTests
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

        private AnimationClip CreateLoopingClip(string name)
        {
            AnimationClip clip = CreateClip(name);
            clip.wrapMode = WrapMode.Loop;
            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);
            return clip;
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"expected private field '{fieldName}' on {target.GetType()}");
            field.SetValue(target, value);
        }

        private static TalkMotionFragment MakeFragment(float start, float end, float weight)
        {
            var fragment = new TalkMotionFragment();
            fragment.Initialize(start, end, weight);
            return fragment;
        }

        /// <summary>
        ///     A set exercising as many distinct finding ids as are reachable without hitting the
        ///     pre-existing "null list element" landmine shared by <c>HasAnyIdle</c>/
        ///     <c>HasAnyTalk</c>/<c>HasAnyAction</c> (a null element there NREs regardless of this
        ///     change — out of scope here, and unreachable through normal Inspector authoring since
        ///     these are plain serialized classes, not object references).
        /// </summary>
        private ConvaiBodyAnimationSet BuildKnownBadSet()
        {
            ConvaiBodyAnimationSet set = CreateSet();

            // Idle: zero-weight (so HasAnyIdle stays false -> idle.missing) with a non-looping clip.
            var idle = new IdleEntry();
            idle.Initialize(CreateClip("idle_noloop"), 0f);

            // Talk[A]: no clip + an invalid (zero-weight) fragment.
            var talkA = new TalkEntry();
            talkA.Initialize(null, 1f);
            talkA.ReplaceFragments(new List<TalkMotionFragment> { MakeFragment(0f, 0.5f, 0f) });

            // Talk[B]: additive, looping main clip, looping intro (must be one-shot).
            var talkB = new TalkEntry();
            talkB.Initialize(CreateLoopingClip("talk_b"), 1f, BodyCoverage.UpperBody, additive: true,
                introClip: CreateLoopingClip("talk_b_intro"));

            // Talk[C]: override, looping main clip, looping outro, overlapping valid fragments —
            // mixes with Talk[B]'s additive mode.
            var talkC = new TalkEntry();
            talkC.Initialize(CreateLoopingClip("talk_c"), 1f, BodyCoverage.UpperBody, additive: false,
                outroClip: CreateLoopingClip("talk_c_outro"));
            talkC.ReplaceFragments(new List<TalkMotionFragment>
            {
                MakeFragment(0f, 0.5f, 1f),
                MakeFragment(0.3f, 0.8f, 1f)
            });

            // Talk[D]: valid but non-looping main clip.
            var talkD = new TalkEntry();
            talkD.Initialize(CreateClip("talk_d_noloop"), 1f);

            // Actions: name missing, name collision, custom-mask-without-mask.
            var actionMissingName = new ActionEntry();
            actionMissingName.Initialize("   ", CreateClip("a1"), ActionMaskMode.UpperBody);

            var actionNoClip = new ActionEntry();
            actionNoClip.Initialize("dup", null, ActionMaskMode.UpperBody);

            var actionCollides = new ActionEntry();
            actionCollides.Initialize("Dup", CreateClip("a2"), ActionMaskMode.UpperBody);

            var actionCustomMask = new ActionEntry();
            actionCustomMask.Initialize("customer", CreateClip("a3"), ActionMaskMode.CustomMask);

            set.InitializeContent(
                "KnownBad",
                new List<IdleEntry> { idle },
                new List<TalkEntry> { talkA, talkB, talkC, talkD },
                new List<ActionEntry> { actionMissingName, actionNoClip, actionCollides, actionCustomMask },
                null /* no upper body mask — talk content needs one */);

            // Locomotion: walk + jog both present but not import-set to loop.
            SetPrivateField(set.Locomotion.Walk, "_clip", CreateClip("walk_noloop"));
            SetPrivateField(set.Locomotion.Jog, "_clip", CreateClip("jog_noloop"));

            // Pointing: one entry with no clip.
            var pointing = new PointingEntry();
            pointing.Initialize(null, 0f, 0f);
            set.Pointing.Add(pointing);

            return set;
        }

        private static readonly string[] ExpectedIds =
        {
            "set.idle.missing",
            "set.idle.notLooping",
            "set.talk.clipMissing",
            "set.talk.fragmentInvalid",
            "set.talk.introLooping",
            "set.talk.outroLooping",
            "set.talk.fragmentOverlap",
            "set.talk.additiveModeMixed",
            "set.talk.notLooping",
            "set.locomotion.walkNotLooping",
            "set.locomotion.jogNotLooping",
            "set.action.nameMissing",
            "set.action.clipMissing",
            "set.action.nameCollision",
            "set.action.customMaskMissing",
            "set.mask.upperBodyMissing",
            "set.pointing.clipMissing"
        };

        [Test]
        public void CollectFindings_KnownBadSet_ProducesExactlyThePinnedIdSet()
        {
            ConvaiBodyAnimationSet set = BuildKnownBadSet();
            var findings = new List<BodyAnimationFinding>();

            set.CollectFindings(findings);

            var actualIds = new HashSet<string>(findings.Select(f => f.Id));
            var expectedIds = new HashSet<string>(ExpectedIds);

            CollectionAssert.AreEquivalent(expectedIds, actualIds,
                $"Finding id set changed. Expected: [{string.Join(", ", expectedIds.OrderBy(x => x))}] " +
                $"Actual: [{string.Join(", ", actualIds.OrderBy(x => x))}]");
        }

        [Test]
        public void CollectIssues_And_CollectValidationFindings_WrapTheSameCollector()
        {
            ConvaiBodyAnimationSet set = BuildKnownBadSet();

            var findings = new List<BodyAnimationFinding>();
            int findingsCount = set.CollectFindings(findings);

            var issues = new List<string>();
            int issuesCount = set.CollectIssues(issues);

            var typedFindings = new List<BodyAnimationValidationFinding>();
            int typedCount = set.CollectValidationFindings(typedFindings);

            Assert.AreEqual(findingsCount, issuesCount);
            Assert.AreEqual(findingsCount, typedCount);

            for (int i = 0; i < findings.Count; i++)
            {
                Assert.AreEqual(findings[i].Message, issues[i]);
                Assert.AreEqual(findings[i].Message, typedFindings[i].Message);
                Assert.AreEqual(findings[i].Severity, typedFindings[i].Severity);
            }
        }

        [Test]
        public void Severity_IsIndependentOfMessageWording()
        {
            var original = new BodyAnimationFinding(
                "set.idle.notLooping", BodyAnimationValidationSeverity.ReleaseBlocker,
                "Idle[0] 'a' is not import-set to loop (Loop Time).");

            // Same id/severity, an entirely reworded message — severity must not move, because it
            // was assigned at construction, never derived from the text.
            var reworded = new BodyAnimationFinding(
                original.Id, original.Severity,
                "This is a completely different sentence containing the word metadata and is null.");

            Assert.AreEqual(original.Severity, reworded.Severity);
            Assert.AreEqual(original.Id, reworded.Id);
            Assert.AreNotEqual(original.Message, reworded.Message);
        }

        [Test]
        public void HealthySet_ProducesNoFindings()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var idle = new IdleEntry();
            idle.Initialize(CreateLoopingClip("idle"), 1f);
            set.InitializeContent("Healthy", new List<IdleEntry> { idle }, null, null, null);

            var findings = new List<BodyAnimationFinding>();
            int count = set.CollectFindings(findings);

            Assert.AreEqual(0, count);
            Assert.IsEmpty(findings);
        }
    }
}
