using System.Reflection;
using Convai.Domain.DomainEvents.Runtime;
using Convai.Modules.Emotion.Components;
using Convai.Modules.Emotion.Profiles;
using Convai.Runtime.Animation;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Emotion
{
    /// <summary>
    ///     Pins the release-pass runtime fixes that would otherwise have no guard: the face
    ///     relaxing out of a preview lock, a profile-less character still driving expression, the
    ///     tick staying allocation-free, and a misplaced controller saying so instead of going
    ///     silent.
    /// </summary>
    /// <remarks>
    ///     Each of these shipped broken, and each was the kind of defect that produces no error and
    ///     no log — the face simply does the wrong thing, or nothing. Without a test the next change
    ///     can reintroduce them just as quietly.
    /// </remarks>
    [TestFixture]
    public sealed class EmotionReleaseFixTests
    {
        private const string CharacterId = "release-fix-char";

        private EmbodimentTestRig _rig;
        private EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile> _harness;

        [SetUp]
        public void SetUp()
        {
            _rig = EmbodimentTestRig.Create(nameof(EmotionReleaseFixTests));
            ConvaiCharacter character = _rig.Root.AddComponent<ConvaiCharacter>();
            character.Configure(CharacterId, "Test Character");
            _rig.Root.AddComponent<FacialBlendshapeCompositorHost>();
            _harness = new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(_rig);
        }

        [TearDown]
        public void TearDown()
        {
            // A log this fixture did not expect fails the test that produced it. The pin held
            // LogAssert.ignoreFailingMessages for the whole fixture instead, under which these
            // tests could not fail for a logging reason at all.
            LogAssert.NoUnexpectedReceived();
            _rig.Dispose();
        }

        // ── The face must relax out of a preview lock ──────────────────────────

        [Test]
        public void UnlockEmotion_ClearsTheHeldTarget_SoTheFaceRelaxes()
        {
            // Locking writes the value into the accumulator's TARGET table as well as the current
            // one. Clearing only the flag left the tick smoothing toward the locked emotion, so the
            // face held that expression until the next backend event — indefinitely on a quiet
            // connection, which is what made the inspector's Stop button look broken.
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.LockEmotion("anger", 0.9f);
                _harness.Tick(1f / 60f);
                Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("anger"),
                    "Sanity: the lock must take effect first.");

                _harness.Controller.UnlockEmotion();

                // No backend event arrives — the face must relax on its own.
                for (int i = 0; i < 240; i++) _harness.Tick(1f / 60f);

                Assert.That(_harness.Controller.CurrentNormalizedIntensity, Is.LessThan(0.05f),
                    "With no further events, the previewed expression must decay instead of being held.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void UnlockEmotion_RestoresAnActiveGameplayOverride_RatherThanClearingIt()
        {
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _harness.Controller.SetEmotionOverride("joy", 0.7f);
                _harness.Controller.LockEmotion("anger", 0.9f);
                _harness.Tick(1f / 60f);

                _harness.Controller.UnlockEmotion();
                for (int i = 0; i < 240; i++) _harness.Tick(1f / 60f);

                Assert.That(_harness.Controller.CurrentResolvedEmotion, Is.EqualTo("joy"),
                    "An override owns the target, so unlocking must hand it back rather than zero it.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── A character with no profile assigned must still express ────────────

        [Test]
        public void CreateDefault_ProducesAWorkingProfile_NotABlankOne()
        {
            // The controller's fallback factory used to return a bare CreateInstance: no expression
            // recipes, so a character with no profile asset drove nothing at all.
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                Assert.That(profile.ExpressionRecipes, Is.Not.Null.And.Not.Empty,
                    "A profile-less character must still have expressions to play.");
                Assert.That(profile.MicroExpressionsEnabled, Is.True);
                Assert.That(profile.EnableEmotionBlending, Is.True);

                // Not mood drift: a resting mood is a personality the author chooses, and the
                // temperament this falls back to deliberately has none. "Working" here means the
                // pipeline drives output, which is what the defect broke.
                Assert.That(profile.ListeningReactionStrength, Is.GreaterThan(0f));
                Assert.That(profile.ProsodyCoupling, Is.GreaterThan(0f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ProfilelessCharacter_BuildsItsExpressionPipeline()
        {
            // No ApplyProfile call at all: this is the "user added the component and pressed Play"
            // path, which used to warn and drive nothing.
            _harness.Tick(1f / 60f);

            object planner = typeof(ConvaiEmotionController)
                .GetField("_expressionPlanner", BindingFlags.Instance | BindingFlags.NonPublic)
                ?.GetValue(_harness.Controller);

            Assert.That(planner, Is.Not.Null,
                "A character with no profile assigned must still build an expression pipeline.");
        }

        // ── The tick must not allocate ─────────────────────────────────────────

        [Test]
        public void EmbodimentTick_DoesNotAllocate()
        {
            // The per-label score copy enumerated the accumulator's scores through an
            // IReadOnlyDictionary, which boxes Dictionary's struct enumerator — one heap allocation
            // per tick per character, forever.
            ConvaiEmotionProfile profile = ConvaiEmotionProfile.CreateDefault();
            try
            {
                _harness.ApplyProfile(profile);
                _rig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 2));

                // Warm up: first ticks legitimately allocate while dictionaries size themselves.
                for (int i = 0; i < 30; i++) _harness.Tick(1f / 60f);

                Assert.That(() =>
                    {
                        for (int i = 0; i < 60; i++) _harness.Tick(1f / 60f);
                    },
                    // Called explicitly rather than via a using: the constraints namespace also
                    // defines an Is, which would make every other assertion in this file ambiguous.
                    UnityEngine.TestTools.Constraints.ConstraintExtensions.AllocatingGCMemory(Is.Not),
                    "The emotion tick is a per-frame path and must stay allocation-free in steady state.");
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        // ── A misplaced controller must say so ─────────────────────────────────

        [Test]
        public void ControllerWithNoConvaiCharacterAncestor_ReportsItOnceAndDropsEvents()
        {
            // Without a ConvaiCharacter ancestor the controller can never match an incoming event,
            // so it stays neutral forever. It used to do that in complete silence.
            using EmbodimentTestRig orphanRig = EmbodimentTestRig.Create("orphan-emotion-rig");
            var orphanHarness =
                new EmbodimentReceiverHarness<ConvaiEmotionController, ConvaiEmotionProfile>(orphanRig);

            FieldInfo warned = typeof(ConvaiEmotionController)
                .GetField("_warnedAboutMissingCharacter", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(warned, Is.Not.Null, "The warn-once flag must exist.");
            Assert.That((bool)warned.GetValue(orphanHarness.Controller), Is.False,
                "Sanity: nothing has been reported yet.");

            orphanRig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "joy", 3));
            orphanHarness.Tick(1f / 60f);

            Assert.That((bool)warned.GetValue(orphanHarness.Controller), Is.True,
                "A controller that can never receive an event must say so rather than go silent.");
            Assert.That(orphanHarness.Controller.CurrentNormalizedIntensity, Is.EqualTo(0f).Within(1e-4f),
                "The event must still be dropped — the warning explains the silence, it does not end it.");

            // The flag is the warn-once mechanism: a second event must not re-report.
            orphanRig.EventHub.Publish(CharacterEmotionChanged.Create(CharacterId, "anger", 3));
            orphanHarness.Tick(1f / 60f);
            Assert.That((bool)warned.GetValue(orphanHarness.Controller), Is.True);
        }
    }
}
