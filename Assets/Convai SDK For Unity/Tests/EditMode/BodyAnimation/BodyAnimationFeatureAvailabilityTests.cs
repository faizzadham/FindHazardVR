using System.Collections.Generic;
using System.Reflection;
using Convai.Domain.Embodiment.Interfaces;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="BodyAnimationFeatureAvailability" />, which answers the two
    ///     questions a user cannot see from the animation set alone: is a switch turned on with
    ///     nothing to play, and is content authored while the switch that plays it is off.
    /// </summary>
    /// <remarks>
    ///     The distinction under test is deliberate and easy to get wrong. Only switch-backed
    ///     features with NO procedural substitute (beat gestures, ambient activities) can be
    ///     "inert". Referential gestures never are — a set without referential clips hands the cue
    ///     to a peer performer. Content tiers with no switch at all (gesture brackets, the
    ///     moving-talk additive twin, cue-tagged actions) are never inert either: their absence
    ///     selects a defined fallback, which is intended behaviour, not a defect.
    /// </remarks>
    internal sealed class BodyAnimationFeatureAvailabilityTests
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

        private ConvaiBodyAnimationConfig CreateConfig()
        {
            var config = ScriptableObject.CreateInstance<ConvaiBodyAnimationConfig>();
            _cleanup.Add(config);
            return config;
        }

        private AnimationClip CreateClip(string name)
        {
            var clip = new AnimationClip { name = name };
            _cleanup.Add(clip);
            return clip;
        }

        private static void SetPrivateField(Object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, $"expected private field '{fieldName}' on {target.GetType()}");
            field.SetValue(target, value);
        }

        private ActionEntry CreateAction(string name, GestureCueKind cue = GestureCueKind.None, bool ambient = false)
        {
            var entry = new ActionEntry();
            entry.Initialize(name, CreateClip(name), ActionMaskMode.UpperBody);
            entry.SetCue(cue);
            entry.SetAmbient(ambient);
            return entry;
        }

        private TalkEntry CreateTalk(string name, float weight = 1f)
        {
            var entry = new TalkEntry();
            entry.Initialize(CreateClip(name), weight);
            return entry;
        }

        private static List<string> Inert(BodyAnimationFeatureAvailability availability)
        {
            var names = new List<string>();
            availability.CollectInertFeatureNames(names);
            return names;
        }

        private static List<string> Dormant(BodyAnimationFeatureAvailability availability)
        {
            var names = new List<string>();
            availability.CollectDormantContentNames(names);
            return names;
        }

        // ── Defaults: a switch that plays authored clips ships off ──────────

        [Test]
        public void BeatGesturesAndAmbientActivities_ShipOff_SoNothingIsEverOnWithNothingToPlay()
        {
            ConvaiBodyAnimationConfig config = CreateConfig();

            Assert.IsFalse(config.EnableBeatGestures,
                "Beat gestures play authored clips and have no substitute, so they must not ship on.");
            Assert.IsFalse(config.EnableAmbientActivities,
                "Ambient activities play authored clips and have no substitute, so they must not ship on.");
            Assert.IsTrue(config.EnableReferentialGestures,
                "Referential gestures resolve either way (authored clip, else peer performer), so they ship on.");

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(CreateSet(), config);
            CollectionAssert.IsEmpty(Inert(availability),
                "A default config on an empty set must report nothing as inert.");
        }

        // ── Beat gestures: on without content is the dead-switch case ───────

        [Test]
        public void BeatGestures_TurnedOnWithoutContent_IsReportedInert()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            ConvaiBodyAnimationConfig config = CreateConfig();
            SetPrivateField(config, "_enableBeatGestures", true);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, config);

            Assert.IsTrue(availability.BeatGestures.IsEnabledWithoutContent);
            Assert.IsFalse(availability.BeatGestures.IsEffective);
            CollectionAssert.Contains(Inert(availability), "Beat Gestures");
        }

        [Test]
        public void BeatGestures_TurnedOnWithTaggedContent_IsEffectiveAndSilent()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("nod", GestureCueKind.Beat) }, null);
            ConvaiBodyAnimationConfig config = CreateConfig();
            SetPrivateField(config, "_enableBeatGestures", true);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, config);

            Assert.IsTrue(availability.BeatGestures.IsEffective);
            CollectionAssert.IsEmpty(Inert(availability));
            CollectionAssert.IsEmpty(Dormant(availability));
        }

        // ── The reciprocal: content authored, switch off ────────────────────

        [Test]
        public void BeatContentWithTheSwitchOff_IsReportedDormant_NotInert()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("nod", GestureCueKind.Emphatic) }, null);
            ConvaiBodyAnimationConfig config = CreateConfig(); // ships off

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, config);

            Assert.IsTrue(availability.BeatGestures.IsContentWithoutEnable);
            CollectionAssert.Contains(Dormant(availability), "Beat Gestures");
            CollectionAssert.DoesNotContain(Inert(availability), "Beat Gestures");
        }

        [Test]
        public void AmbientContentWithTheSwitchOff_IsReportedDormant()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("stretch", ambient: true) }, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            CollectionAssert.Contains(Dormant(availability), "Ambient Activities");
        }

        [Test]
        public void ReferentialContentWithTheSwitchOff_IsReportedDormant()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("palm", GestureCueKind.PalmToPlayer) }, null);
            ConvaiBodyAnimationConfig config = CreateConfig();
            SetPrivateField(config, "_enableReferentialGestures", false);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, config);

            CollectionAssert.Contains(Dormant(availability), "Referential Gestures");
        }

        // ── Referential gestures are never inert: they always resolve ───────

        [Test]
        public void ReferentialGestures_OnWithoutContent_IsNotInert_BecauseTheCueIsHandedOver()
        {
            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(CreateSet(), CreateConfig());

            Assert.IsTrue(availability.ReferentialGestures.Enabled);
            Assert.IsFalse(availability.ReferentialGestures.HasContent);
            CollectionAssert.DoesNotContain(Inert(availability), "Referential Gestures");
        }

        // ── Content tiers with no switch are never inert ────────────────────

        [Test]
        public void ContentTiersWithoutASwitch_AreNeverReportedInert()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, new List<TalkEntry> { CreateTalk("talk") }, null, null);
            ConvaiBodyAnimationConfig config = CreateConfig(); // MovingTalkMode defaults to Auto

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, config);

            // All three genuinely lack content here…
            Assert.IsFalse(availability.GestureBrackets.HasContent);
            Assert.IsFalse(availability.MovingTalkAdditive.HasContent);
            Assert.IsFalse(availability.CueTaggedActions.HasContent);

            // …and none of them is a defect, because each has a defined fallback.
            List<string> inert = Inert(availability);
            CollectionAssert.DoesNotContain(inert, "Gesture Brackets");
            CollectionAssert.DoesNotContain(inert, "Moving Talk Additive Tier");
            CollectionAssert.DoesNotContain(inert, "Cue-Tagged Actions");
        }

        [Test]
        public void MovingTalkAdditive_WithAnAuthoredTwin_IsEffective()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            TalkEntry talk = CreateTalk("talk");
            talk.SetAdditiveClip(CreateClip("talk_additive"));
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            Assert.IsTrue(availability.MovingTalkAdditive.IsEffective);
        }

        [Test]
        public void GestureBrackets_WithAnIntroClip_HaveContent()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var talk = new TalkEntry();
            talk.Initialize(CreateClip("talk"), 1f, introClip: CreateClip("intro"));
            set.InitializeContent("Test", null, new List<TalkEntry> { talk }, null, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            Assert.IsTrue(availability.GestureBrackets.IsEffective);
        }

        [Test]
        public void CueTaggedActions_WithAGreetingTag_HaveContent()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null, null,
                new List<ActionEntry> { CreateAction("wave_hello", GestureCueKind.Greeting) }, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            Assert.IsTrue(availability.CueTaggedActions.IsEffective);
        }

        // ── Pool variety: the settings with nothing to act on ───────────────

        [Test]
        public void VariantCounts_ReportPoolVariety_SoSingleVariantPoolsCanBeSurfaced()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            var idle = new IdleEntry();
            idle.Initialize(CreateClip("idle"), 1f);
            set.InitializeContent("Test",
                new List<IdleEntry> { idle },
                new List<TalkEntry> { CreateTalk("talk_a"), CreateTalk("talk_b") },
                null, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            Assert.AreEqual(1, availability.IdleVariantCount);
            Assert.AreEqual(2, availability.TalkVariantCount);
            Assert.IsFalse(availability.HasEmotionAffinities);
        }

        [Test]
        public void ZeroWeightEntries_DoNotCountAsVariants()
        {
            ConvaiBodyAnimationSet set = CreateSet();
            set.InitializeContent("Test", null,
                new List<TalkEntry> { CreateTalk("talk", weight: 0f) }, null, null);

            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(set, CreateConfig());

            Assert.AreEqual(0, availability.TalkVariantCount);
        }

        // ── Null inputs ────────────────────────────────────────────────────

        [Test]
        public void Compute_NullSetOrConfig_ReturnsAllDisabledWithoutThrowing()
        {
            BodyAnimationFeatureAvailability availability =
                BodyAnimationFeatureAvailability.Compute(null, CreateConfig());

            Assert.IsFalse(availability.BeatGestures.Enabled);
            Assert.IsFalse(availability.CueTaggedActions.Enabled);
            Assert.AreEqual(0, availability.CollectInertFeatureNames(new List<string>()));
            Assert.AreEqual(0, availability.CollectDormantContentNames(new List<string>()));
        }

        // ── Agreement between the pure computation and the controller ───────

        [Test]
        public void ControllerExposesTheSameAvailability_AsTheStaticComputation_AfterABuild()
        {
            var root = new GameObject("FeatureAvailabilityAgreementTest");
            try
            {
                // EmbodimentContext first: without one OnEnable logs a setup error the framework counts as a failure.
                root.AddComponent<EmbodimentContext>();
                ConvaiBodyAnimationController controller = root.AddComponent<ConvaiBodyAnimationController>();

                ConvaiBodyAnimationSet set = CreateSet();
                set.InitializeContent("Test", null, null,
                    new List<ActionEntry> { CreateAction("nod", GestureCueKind.Beat) }, null);
                ConvaiBodyAnimationConfig config = CreateConfig();
                SetPrivateField(config, "_enableBeatGestures", true);

                // BuildRuntime is a no-op outside Play Mode (established pattern in this suite —
                // see BodyAnimationLifecycleTests) — fake exactly what it writes: the same call
                // BuildRuntime makes, stored on the same field the public property reads.
                SetPrivateField(controller, "_animationSet", set);
                SetPrivateField(controller, "_config", config);
                SetPrivateField(controller, "_featureAvailability",
                    BodyAnimationFeatureAvailability.Compute(set, config));

                BodyAnimationFeatureAvailability expected =
                    BodyAnimationFeatureAvailability.Compute(set, config);
                BodyAnimationFeatureAvailability actual = controller.FeatureAvailability;

                Assert.AreEqual(expected.BeatGestures.IsEffective, actual.BeatGestures.IsEffective);
                Assert.AreEqual(expected.ReferentialGestures.IsEffective, actual.ReferentialGestures.IsEffective);
                Assert.AreEqual(expected.AmbientActivities.IsEffective, actual.AmbientActivities.IsEffective);
                Assert.AreEqual(expected.GestureBrackets.IsEffective, actual.GestureBrackets.IsEffective);
                Assert.AreEqual(expected.MovingTalkAdditive.IsEffective, actual.MovingTalkAdditive.IsEffective);
                Assert.AreEqual(expected.CueTaggedActions.IsEffective, actual.CueTaggedActions.IsEffective);
                Assert.AreEqual(expected.TalkVariantCount, actual.TalkVariantCount);
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }
    }
}
