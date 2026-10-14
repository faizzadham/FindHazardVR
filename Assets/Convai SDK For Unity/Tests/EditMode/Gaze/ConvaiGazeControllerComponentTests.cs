using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Components;
using Convai.Runtime.Components;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class ConvaiGazeControllerComponentTests
    {
        private GameObject _root;

        [SetUp]
        public void SetUp() => _root = new GameObject("GazeControllerTestCharacter");

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_root);

        [Test]
        public void OnEnable_RegistersAsGazeSource()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();

            Assert.IsTrue(controller.enabled, "Controller should stay enabled with a valid context.");
            Assert.IsTrue(EmbodimentContext.TryResolve(controller, out EmbodimentContext context));
            Assert.AreSame(controller, context.GazeSource,
                "Controller must register itself as the character's gaze source.");
        }

        [Test]
        public void OnDisable_ClearsGazeSourceSlot_AndResetsReading()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();
            Assert.IsTrue(EmbodimentContext.TryResolve(controller, out EmbodimentContext context));

            controller.enabled = false;

            Assert.IsNull(context.GazeSource, "Disabling must clear the gaze source slot.");
            Assert.That(controller.Current.TargetKind, Is.EqualTo(GazeTargetKind.None));
            Assert.That(controller.Current.Engagement, Is.EqualTo(0f));
        }

        [Test]
        public void OnEnable_DoesNotDemandConversationFlowDriver()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();

            Assert.IsTrue(EmbodimentContext.TryResolve(controller, out EmbodimentContext context));
            Assert.IsFalse(
                context.IsConversationFlowDriverDemanded,
                "A gaze-only stack must not auto-provision ConvaiConversationFlowController.");
        }

        [Test]
        public void CaptureSnapshot_BeforeRuntime_ReturnsDisengagedState()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();

            var snapshot = controller.CaptureSnapshot();

            Assert.That(snapshot.Reading.TargetKind, Is.EqualTo(GazeTargetKind.None));
            Assert.That(snapshot.DialogueState, Is.EqualTo(DialogueState.Idle));
            Assert.That(snapshot.RecentTrace, Is.Empty);
        }

        [Test]
        public void GazeReadingNone_IsFullyDisengaged()
        {
            GazeReading none = GazeReading.None;

            Assert.That(none.TargetKind, Is.EqualTo(GazeTargetKind.None));
            Assert.IsNull(none.Target);
            Assert.That(none.Engagement, Is.EqualTo(0f));
            Assert.IsFalse(none.IsAverting);
        }

        [Test]
        public void GazeReading_ClampsEngagement()
        {
            var reading = new GazeReading(GazeTargetKind.Player, null, Vector3.one, 3.5f, false, 1);
            Assert.That(reading.Engagement, Is.EqualTo(1f));

            reading = new GazeReading(GazeTargetKind.Player, null, Vector3.one, -2f, false, 1);
            Assert.That(reading.Engagement, Is.EqualTo(0f));
        }

        [Test]
        public void PlayerAnchorOverride_PushesIntoProviderAndClearsBack()
        {
            _root.AddComponent<ConvaiCharacter>();
            var provider = _root.AddComponent<Convai.Modules.Gaze.Providers.PlayerAnchorTargetProvider>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();
            controller.RefreshProviders();

            Transform anchor = new GameObject("CustomPlayer").transform;
            try
            {
                controller.PlayerAnchorOverride = anchor;
                Assert.AreSame(anchor, provider.ExplicitAnchor,
                    "Setting the override must re-target the character's player-anchor provider.");
                Assert.AreSame(anchor, controller.PlayerAnchorOverride);

                controller.PlayerAnchorOverride = null;
                Assert.IsNull(provider.ExplicitAnchor,
                    "Clearing the override must hand the provider back to the camera fallback.");
            }
            finally
            {
                Object.DestroyImmediate(anchor.gameObject);
            }
        }

        [Test]
        public void EyeContactMode_DefaultsNatural_AndRoundTripsEveryMode()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();

            Assert.That(controller.EyeContactMode, Is.EqualTo(GazeEyeContactMode.Natural),
                "Natural by default — the profile's state table drives gaze.");

            foreach (GazeEyeContactMode mode in (GazeEyeContactMode[])System.Enum.GetValues(typeof(GazeEyeContactMode)))
            {
                controller.EyeContactMode = mode;
                Assert.That(controller.EyeContactMode, Is.EqualTo(mode),
                    $"Eye contact mode {mode} did not round-trip.");
            }
        }

        [Test]
        public void IsLockActive_CoversModeStateMatrix()
        {
            foreach (DialogueState state in (DialogueState[])System.Enum.GetValues(typeof(DialogueState)))
            {
                Assert.IsFalse(ConvaiGazeController.IsLockActive(GazeEyeContactMode.Natural, state),
                    $"Natural never locks ({state}).");
                Assert.IsTrue(ConvaiGazeController.IsLockActive(GazeEyeContactMode.AlwaysLock, state),
                    $"AlwaysLock locks every state ({state}).");
                Assert.That(
                    ConvaiGazeController.IsLockActive(GazeEyeContactMode.ConversationLock, state),
                    Is.EqualTo(state != DialogueState.Idle),
                    $"ConversationLock locks every non-Idle state ({state}).");
                Assert.That(
                    ConvaiGazeController.IsLockActive(GazeEyeContactMode.SpeakingFocus, state),
                    Is.EqualTo(state == DialogueState.Speaking),
                    $"SpeakingFocus locks only while the character speaks ({state}).");
            }
        }

        [Test]
        public void GlanceAt_UnderLock_IsAbsorbedWithoutTouchingTheStack()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();
            controller.EyeContactMode = GazeEyeContactMode.AlwaysLock;

            Transform prop = new GameObject("GlanceProp").transform;
            try
            {
                GazeHandle absorbed = controller.GlanceAt(prop, 1f);

                Assert.NotNull(absorbed);
                Assert.IsFalse(absorbed.IsActive, "An absorbed glance is complete on arrival.");
                Assert.IsTrue(absorbed.Completion.IsCompleted);
                Assert.IsTrue(absorbed.Settled.IsCompleted);
                Assert.IsFalse(absorbed.Settled.Result, "An absorbed glance never settles.");
                Assert.IsNull(controller.ScriptedStack.ResolveActive(Time.time),
                    "The scripted stack must stay untouched.");

                controller.LockBlocksGlances = false;
                GazeHandle live = controller.GlanceAt(prop, 1f);
                Assert.IsTrue(live.IsActive, "With strictness off the glance goes through.");
                Assert.IsNotNull(controller.ScriptedStack.ResolveActive(Time.time));
            }
            finally
            {
                Object.DestroyImmediate(prop.gameObject);
            }
        }

        [Test]
        public void GazeAt_UnderLock_StillGoesThrough()
        {
            _root.AddComponent<ConvaiCharacter>();
            ConvaiGazeController controller = _root.AddComponent<ConvaiGazeController>();
            controller.EyeContactMode = GazeEyeContactMode.AlwaysLock;

            Transform prop = new GameObject("ExplicitProp").transform;
            try
            {
                GazeHandle handle = controller.GazeAt(prop);

                Assert.IsTrue(handle.IsActive,
                    "An explicit GazeAt is deliberate developer intent — sovereign over the lock.");
                Assert.IsNotNull(controller.ScriptedStack.ResolveActive(Time.time));
            }
            finally
            {
                Object.DestroyImmediate(prop.gameObject);
            }
        }

        // ── Travel check-in aim ──────────────────────────────────────────────

        /// <summary>
        ///     A travel subject is reported as a transform position — the player's root (their
        ///     feet) for a companion, a floor point for a destination. Glancing at it raw means
        ///     glancing at the ground, and the closer the character gets the steeper it is:
        ///     roughly 57° down at a metre. The character ducked its head at its own feet just
        ///     as it stopped.
        /// </summary>
        [Test]
        public void TravelCheckIn_LooksAtASubjectBelowItsEyeLine_Level()
        {
            // Companion two metres ahead, reported at their feet; observer's eyes at 1.55 m.
            var subjectAtFeet = new Vector3(0f, 0f, 2f);

            Vector3 aim = ConvaiGazeController.LiftToEyeLine(subjectAtFeet, 1.55f);

            Assert.That(aim.y, Is.EqualTo(1.55f).Within(1e-4f));
            Assert.That(aim.x, Is.EqualTo(subjectAtFeet.x).Within(1e-4f), "Only the height is corrected.");
            Assert.That(aim.z, Is.EqualTo(subjectAtFeet.z).Within(1e-4f));
        }

        /// <summary>
        ///     One-sided on purpose: a subject genuinely above the character is still looked up
        ///     at. The rule is "do not crane your neck downward at what you are walking toward",
        ///     not "always look straight ahead".
        /// </summary>
        [Test]
        public void TravelCheckIn_LeavesASubjectAboveItsEyeLineAlone()
        {
            var highShelf = new Vector3(0f, 2.4f, 3f);

            Vector3 aim = ConvaiGazeController.LiftToEyeLine(highShelf, 1.55f);

            Assert.That(aim, Is.EqualTo(highShelf));
        }
    }
}
