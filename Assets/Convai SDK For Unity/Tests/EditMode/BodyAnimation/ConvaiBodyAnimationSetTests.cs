using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public class ConvaiBodyAnimationSetTests
    {
        private ConvaiBodyAnimationSet _set;
        private readonly List<Object> _cleanup = new();

        [TearDown]
        public void TearDown()
        {
            foreach (Object obj in _cleanup)
                Object.DestroyImmediate(obj);
            _cleanup.Clear();
            _set = null;
        }

        private ConvaiBodyAnimationSet CreateSet()
        {
            _set = ScriptableObject.CreateInstance<ConvaiBodyAnimationSet>();
            _cleanup.Add(_set);
            return _set;
        }

        private AnimationClip CreateClip(string name)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);
            return clip;
        }

        /// <summary>A clip whose <c>isLooping</c> is true, for the intro/outro-must-not-loop validation tests.</summary>
        private AnimationClip CreateLoopingClip(string name)
        {
            AnimationClip clip = CreateClip(name);
            clip.wrapMode = WrapMode.Loop;

            AnimationClipSettings settings = AnimationUtility.GetAnimationClipSettings(clip);
            settings.loopTime = true;
            AnimationUtility.SetAnimationClipSettings(clip, settings);

            return clip;
        }

        private static ActionEntry CreateAction(
            string name, AnimationClip clip, params string[] aliases)
        {
            var entry = new ActionEntry();
            entry.Initialize(name, clip, ActionMaskMode.UpperBody, aliases: aliases);
            return entry;
        }

        [Test]
        public void TryGetAction_ResolvesPrimaryName_CaseInsensitive()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("Dance", CreateClip("dance")) }, null);

            Assert.IsTrue(set.TryGetAction("dance", out ActionEntry entry));
            Assert.AreEqual("Dance", entry.ActionName);
            Assert.IsTrue(set.TryGetAction("DANCE", out _));
        }

        [Test]
        public void TryGetAction_ResolvesAliases_AndSeparatorVariants()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("pick_up", CreateClip("pickup"), "grab item") },
                null);

            Assert.IsTrue(set.TryGetAction("pick up", out _), "space should match underscore");
            Assert.IsTrue(set.TryGetAction("Pick-Up", out _), "dash should match underscore");
            Assert.IsTrue(set.TryGetAction("grab_item", out _), "alias with separator variant");
            Assert.IsFalse(set.TryGetAction("drop", out _));
        }

        [Test]
        public void TryGetAction_UnknownOrEmpty_ReturnsFalse()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null, new List<ActionEntry>(), null);

            Assert.IsFalse(set.TryGetAction("missing", out _));
            Assert.IsFalse(set.TryGetAction("", out _));
            Assert.IsFalse(set.TryGetAction(null, out _));
        }

        [Test]
        public void CollectIssues_FlagsMissingIdle()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var issues = new List<string>();

            int count = set.CollectIssues(issues);

            Assert.Greater(count, 0);
            Assert.IsTrue(issues.Exists(i => i.Contains("idle", System.StringComparison.OrdinalIgnoreCase)));
        }

        [Test]
        public void CollectIssues_FlagsDuplicateActionAliases()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry>
                {
                    CreateAction("wave", CreateClip("a"), "hello"),
                    CreateAction("greet", CreateClip("b"), "Hello")
                },
                null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsTrue(
                issues.Exists(i => i.Contains("collides")),
                $"expected alias collision issue, got: {string.Join(" | ", issues)}");
        }

        [Test]
        public void CollectIssues_FlagsNonLoopingIdle()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var idle = new IdleEntry();
            idle.Initialize(CreateClip("idle_noloop"));
            set.InitializeContent("Test", new List<IdleEntry> { idle }, null, null, null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsTrue(issues.Exists(i => i.Contains("Loop Time")));
        }

        [Test]
        public void HasAnyIdle_IgnoresZeroWeightAndEmptyEntries()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var zeroWeight = new IdleEntry();
            zeroWeight.Initialize(CreateClip("idle"), 0f);
            set.InitializeContent("Test", new List<IdleEntry> { zeroWeight, new() }, null, null, null);

            Assert.IsFalse(set.HasAnyIdle);
        }

        [Test]
        public void CollectIssues_MixedAdditiveTalkEntries_ReportsIssue()
        {
            ConvaiBodyAnimationSet set = CreateSet();

            var additiveTalk = new TalkEntry();
            additiveTalk.Initialize(CreateClip("talk_additive"), 1f, BodyCoverage.UpperBody, additive: true);

            var overrideTalk = new TalkEntry();
            overrideTalk.Initialize(CreateClip("talk_override"), 1f, BodyCoverage.UpperBody, additive: false);

            set.InitializeContent("Test", null,
                new List<TalkEntry> { additiveTalk, overrideTalk }, null, null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsTrue(
                issues.Exists(i => i.Contains("mix Additive")),
                $"expected a mixed-additive-mode issue, got: {string.Join(" | ", issues)}");
        }

        [Test]
        public void CollectIssues_UniformAdditiveTalkEntries_NoMixIssue()
        {
            ConvaiBodyAnimationSet set = CreateSet();

            var first = new TalkEntry();
            first.Initialize(CreateClip("talk_a"), 1f, BodyCoverage.UpperBody, additive: true);

            var second = new TalkEntry();
            second.Initialize(CreateClip("talk_b"), 1f, BodyCoverage.UpperBody, additive: true);

            set.InitializeContent("Test", null,
                new List<TalkEntry> { first, second }, null, null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsFalse(issues.Exists(i => i.Contains("mix Additive")));
        }

        [Test]
        public void CollectIssues_FlagsLoopingIntroAndOutroClips()
        {
            ConvaiBodyAnimationSet set = CreateSet();

            var talk = new TalkEntry();
            talk.Initialize(
                CreateLoopingClip("talk_loop"), 1f, BodyCoverage.UpperBody, additive: false,
                introClip: CreateLoopingClip("intro_loop"), outroClip: CreateLoopingClip("outro_loop"));

            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsTrue(
                issues.Exists(i => i.Contains("intro clip") && i.Contains("Loop Time")),
                $"expected a looping-intro-clip issue, got: {string.Join(" | ", issues)}");
            Assert.IsTrue(
                issues.Exists(i => i.Contains("outro clip") && i.Contains("Loop Time")),
                $"expected a looping-outro-clip issue, got: {string.Join(" | ", issues)}");
        }

        [Test]
        public void CollectIssues_NonLoopingIntroAndOutroClips_NoBracketIssue()
        {
            ConvaiBodyAnimationSet set = CreateSet();

            var talk = new TalkEntry();
            talk.Initialize(
                CreateLoopingClip("talk_loop"), 1f, BodyCoverage.UpperBody, additive: false,
                introClip: CreateClip("intro_oneshot"), outroClip: CreateClip("outro_oneshot"));

            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, null);

            var issues = new List<string>();
            set.CollectIssues(issues);

            Assert.IsFalse(issues.Exists(i => i.Contains("intro clip")));
            Assert.IsFalse(issues.Exists(i => i.Contains("outro clip")));
        }
    }

    public class ActionEntryTests
    {
        [Test]
        public void NormalizeName_CollapsesSeparatorsAndCase()
        {
            Assert.AreEqual("pick_up", ActionEntry.NormalizeName("Pick Up"));
            Assert.AreEqual("pick_up", ActionEntry.NormalizeName("pick--up"));
            Assert.AreEqual("pick_up", ActionEntry.NormalizeName("  PICK_UP  "));
            Assert.AreEqual(string.Empty, ActionEntry.NormalizeName(null));
            Assert.AreEqual(string.Empty, ActionEntry.NormalizeName("   "));
        }
    }

    public class PointingSectionTests
    {
        [Test]
        public void FindClosest_PicksNearestDirection()
        {
            var section = new PointingSection();
            var clips = new List<Object>();

            PointingEntry Make(string name, float yaw, float pitch)
            {
                var clip = new AnimationClip { name = name };
                clips.Add(clip);
                var entry = new PointingEntry();
                entry.Initialize(clip, yaw, pitch);
                section.Add(entry);
                return entry;
            }

            PointingEntry forward = Make("F", 0f, 0f);
            PointingEntry right = Make("R", 90f, 0f);
            PointingEntry upForward = Make("UF", 0f, 45f);
            Make("B", 180f, 0f);

            try
            {
                Assert.AreSame(forward, section.FindClosest(10f, 5f));
                Assert.AreSame(right, section.FindClosest(70f, 0f));
                Assert.AreSame(upForward, section.FindClosest(-5f, 40f));
                // Yaw wraps: −170° is closer to +180° (back) than to −90°.
                Assert.AreEqual("B", section.FindClosest(-170f, 0f).Clip.name);
            }
            finally
            {
                foreach (Object clip in clips)
                    Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void FindClosest_EmptySection_ReturnsNull()
        {
            var section = new PointingSection();
            Assert.IsNull(section.FindClosest(0f, 0f));
            Assert.IsFalse(section.HasAny);
        }
    }
}
