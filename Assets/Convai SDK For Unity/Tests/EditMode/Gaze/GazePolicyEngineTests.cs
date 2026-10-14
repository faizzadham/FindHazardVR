using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Policy;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazePolicyEngineTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private GazePolicyEngine _engine;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _engine = new GazePolicyEngine();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private static GazeTargetDecision FullPlayerDecision(Vector3 point) => new()
        {
            Kind = GazeTargetKind.Player,
            Target = null,
            SmoothedPoint = point,
            Commitment = 1f,
            GenerationId = 1,
            Name = "Player",
            ScriptedEngagementOverride = -1f
        };

        private GazeDirective TickFor(DialogueState state, in GazeTargetDecision decision, float seconds)
        {
            GazeStatePolicy policy = _profile.GetStatePolicy(state);
            GazeDirective directive = default;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                directive = _engine.Tick(in policy, in decision, _profile, Dt);
            return directive;
        }

        [Test]
        public void Speaking_WithFullCommitment_ReachesFullEngagement()
        {
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));

            GazeDirective directive = TickFor(DialogueState.Speaking, in decision, 2f);

            Assert.That(directive.Engagement, Is.EqualTo(1f).Within(0.01f),
                "Speaking must lock fully onto the player (product requirement).");
            Assert.IsTrue(directive.HasEngagedTarget);
            Assert.IsTrue(directive.AllowBodyTurn);
        }

        [Test]
        public void StateSwitch_BlendsEngagementSmoothly()
        {
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));
            TickFor(DialogueState.Speaking, in decision, 2f);

            // One tick after switching to Thinking (0.7 target) engagement must NOT have
            // jumped to the new value — smoothing is what prevents visible pops.
            GazeStatePolicy thinkingPolicy = _profile.GetStatePolicy(DialogueState.Thinking);
            GazeDirective firstThinkingTick = _engine.Tick(in thinkingPolicy, in decision, _profile, Dt);

            Assert.That(firstThinkingTick.Engagement, Is.GreaterThan(0.9f),
                "Engagement should still be near the Speaking value one tick after the switch.");

            GazeDirective settled = TickFor(DialogueState.Thinking, in decision, 3f);
            Assert.That(settled.Engagement, Is.EqualTo(0.7f).Within(0.02f));
            Assert.That(settled.AversionMode, Is.EqualTo(GazeAversionMode.Cognitive));
        }

        [Test]
        public void NoTarget_ProducesZeroEngagement()
        {
            GazeTargetDecision none = GazeTargetDecision.None;

            GazeDirective directive = TickFor(DialogueState.Speaking, in none, 1f);

            Assert.IsFalse(directive.HasEngagedTarget);
            Assert.That(directive.Engagement, Is.EqualTo(0f));
            Assert.That(directive.Kind, Is.EqualTo(GazeTargetKind.None));
        }

        [Test]
        public void ScriptedOverride_ReplacesStateEngagement()
        {
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));
            decision.Kind = GazeTargetKind.Scripted;
            decision.IsScripted = true;
            decision.ScriptedEngagementOverride = 0.4f;
            decision.ScriptedAllowBodyTurn = false;

            GazeDirective directive = TickFor(DialogueState.Idle, in decision, 2f);

            Assert.That(directive.Engagement, Is.EqualTo(0.4f).Within(0.02f),
                "Scripted gaze must work even in Idle, using its own engagement.");
            Assert.IsFalse(directive.AllowBodyTurn);
        }

        [Test]
        public void EngagementModifier_ScalesPolicy()
        {
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));
            _engine.EngagementModifier = 0.5f;

            GazeDirective directive = TickFor(DialogueState.Speaking, in decision, 2f);

            Assert.That(directive.Engagement, Is.EqualTo(0.5f).Within(0.02f));
        }

        [Test]
        public void Reset_ClearsSmoothingAndModifiers()
        {
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));
            _engine.EngagementModifier = 0.25f;
            TickFor(DialogueState.Speaking, in decision, 1f);

            _engine.Reset();

            Assert.That(_engine.SmoothedEngagement, Is.EqualTo(0f));
            Assert.That(_engine.EngagementModifier, Is.EqualTo(1f));
        }

        [Test]
        public void LockedToPlayer_OverridesIdleSuppressionWithFullCommitment()
        {
            // Idle's authored policy suppresses the player target entirely (AllowPlayerTarget
            // false, Engagement 0) — the whole point of the lock override is to bypass that.
            GazeStatePolicy idlePolicy = _profile.GetStatePolicy(DialogueState.Idle);
            Assert.IsFalse(idlePolicy.AllowPlayerTarget, "Sanity: Idle normally suppresses the player.");

            GazeStatePolicy locked = GazeStatePolicy.LockedToPlayer(DialogueState.Idle);

            Assert.IsTrue(locked.AllowPlayerTarget);
            Assert.That(locked.Engagement, Is.EqualTo(1f));
            Assert.That(locked.HeadContribution, Is.EqualTo(1f));
            Assert.IsTrue(locked.AllowBodyTurn);
            Assert.That(locked.AversionMode, Is.EqualTo(GazeAversionMode.None));
            Assert.That(locked.AversionStrength, Is.EqualTo(0f));
            Assert.That(locked.State, Is.EqualTo(DialogueState.Idle));
        }

        [Test]
        public void LockedToPlayer_TickReachesFullEngagementEvenInIdle()
        {
            GazeStatePolicy locked = GazeStatePolicy.LockedToPlayer(DialogueState.Idle);
            GazeTargetDecision decision = FullPlayerDecision(new Vector3(0f, 1.6f, 2f));

            GazeDirective directive = default;
            int steps = Mathf.CeilToInt(2f / Dt);
            for (int i = 0; i < steps; i++)
                directive = _engine.Tick(in locked, in decision, _profile, Dt);

            Assert.That(directive.Engagement, Is.EqualTo(1f).Within(0.01f),
                "The lock override must reach full engagement regardless of the Idle state's own policy.");
            Assert.IsTrue(directive.AllowBodyTurn);
            Assert.That(directive.AversionMode, Is.EqualTo(GazeAversionMode.None));
        }
    }
}
