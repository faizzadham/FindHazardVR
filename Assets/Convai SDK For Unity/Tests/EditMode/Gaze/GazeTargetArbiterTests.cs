using System.Collections.Generic;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Targeting;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Providers;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class GazeTargetArbiterTests
    {
        private const float Dt = 1f / 60f;

        private ConvaiGazeProfile _profile;
        private GazeTargetArbiter _arbiter;
        private List<GazeTargetCandidate> _candidates;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _arbiter = new GazeTargetArbiter();
            _candidates = new List<GazeTargetCandidate>();
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private static GazeTargetCandidate PlayerCandidate(Vector3 point, float relevance = 1f, string name = "Player") =>
            new(GazeTargetKind.Player, 10, relevance, null, point, name);

        private static GazeTargetCandidate WorldCandidate(Vector3 point, string name, int priority = 5) =>
            new(GazeTargetKind.WorldObject, priority, 1f, null, point, name);

        private GazeTargetDecision TickFor(float seconds, bool allowPlayer = true, GazeTargetStack.Entry scripted = null)
        {
            GazeTargetDecision decision = default;
            int steps = Mathf.CeilToInt(seconds / Dt);
            for (int i = 0; i < steps; i++)
                decision = _arbiter.Tick(_candidates, scripted, allowPlayer, _profile, Dt);
            return decision;
        }

        [Test]
        public void Acquisition_RampsCommitmentToFull()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));

            GazeTargetDecision decision = TickFor(_profile.CommitmentAcquireSeconds + 0.1f);

            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Player));
            Assert.That(decision.Commitment, Is.EqualTo(1f).Within(0.001f));
        }

        [Test]
        public void FirstAcquisition_SnapsPointWithoutDrag()
        {
            Vector3 point = new(3f, 1.6f, -4f);
            _candidates.Add(PlayerCandidate(point));

            GazeTargetDecision decision = _arbiter.Tick(_candidates, null, true, _profile, Dt);

            Assert.That(decision.SmoothedPoint, Is.EqualTo(point));
            Assert.IsTrue(decision.TeleportedThisTick, "A new acquisition is ballistic.");
            Assert.IsFalse(decision.WasCut,
                "Choosing to look at something is not a camera cut. The two are reported " +
                "separately because the eyes jump for both while the head takes reflex speed only " +
                "for the second — conflating them executed every ordinary look as a startle.");
        }

        [Test]
        public void CameraCut_IsReportedAsACutAndARetarget()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f), name: "Player"));
            TickFor(0.5f);

            // Same target (same key, so not a re-target), displaced beyond the cut threshold.
            _candidates.Clear();
            _candidates.Add(PlayerCandidate(new Vector3(12f, 1.6f, 2f), name: "Player"));
            GazeTargetDecision decision = _arbiter.Tick(_candidates, null, true, _profile, Dt);

            Assert.IsTrue(decision.TeleportedThisTick, "The eyes must re-acquire ballistically.");
            Assert.IsTrue(decision.WasCut, "The world moved, not the character's mind — that is a reflex.");
        }

        [Test]
        public void TargetLoss_HoldsThenReleases()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));
            TickFor(1f);

            _candidates.Clear();
            GazeTargetDecision heldDecision = TickFor(_profile.TargetLossHoldSeconds * 0.5f);
            Assert.That(heldDecision.Commitment, Is.GreaterThan(0f),
                "Commitment must decay smoothly, not drop instantly.");

            GazeTargetDecision released = TickFor(_profile.TargetLossHoldSeconds + _profile.CommitmentReleaseSeconds + 0.2f);
            Assert.That(released.Commitment, Is.EqualTo(0f).Within(0.001f));
            Assert.That(released.Kind, Is.EqualTo(GazeTargetKind.None));
        }

        [Test]
        public void Teleport_BumpsGenerationAndSnapsPoint()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));
            GazeTargetDecision before = TickFor(0.5f);
            int generationBefore = before.GenerationId;

            _candidates.Clear();
            Vector3 farPoint = new(10f, 1.6f, 2f);
            _candidates.Add(PlayerCandidate(farPoint));
            GazeTargetDecision after = _arbiter.Tick(_candidates, null, true, _profile, Dt);

            Assert.IsTrue(after.TeleportedThisTick, "A jump beyond the threshold is a re-acquisition.");
            Assert.That(after.GenerationId, Is.GreaterThan(generationBefore));
            Assert.That(after.SmoothedPoint, Is.EqualTo(farPoint), "No positional drag across the room.");
        }

        [Test]
        public void PlayerCandidates_AreFilteredWhenPolicyDisallows()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));

            GazeTargetDecision decision = TickFor(1f, allowPlayer: false);

            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.None),
                "Idle-style states must suppress the player target.");
        }

        [Test]
        public void ScriptedEntry_BeatsProviders()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));
            var stack = new GazeTargetStack();
            stack.Push(null, new Vector3(-5f, 1f, 0f), false, 0, 0.8f, true, float.PositiveInfinity, "prop");
            GazeTargetStack.Entry scripted = stack.ResolveActive(0f);

            GazeTargetDecision decision = TickFor(0.5f, scripted: scripted);

            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Scripted));
            Assert.IsTrue(decision.IsScripted);
            Assert.That(decision.ScriptedEngagementOverride, Is.EqualTo(0.8f).Within(0.0001f));
            Assert.IsTrue(decision.ScriptedAllowBodyTurn);
        }

        [Test]
        public void ScriptedEntry_IgnoresAllowPlayerTargetGate()
        {
            var stack = new GazeTargetStack();
            stack.Push(null, new Vector3(0f, 1f, 3f), false, 0, -1f, false, float.PositiveInfinity, "prop");
            GazeTargetStack.Entry scripted = stack.ResolveActive(0f);

            GazeTargetDecision decision = TickFor(0.5f, allowPlayer: false, scripted: scripted);

            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Scripted),
                "Scripted gaze bypasses the state-policy player gate.");
        }

        [Test]
        public void InterestBudget_ForcesBreakToAlternativeAfterMaxHold()
        {
            var a = WorldCandidate(new Vector3(1f, 1.5f, 2f), "A");
            var b = WorldCandidate(new Vector3(-1f, 1.5f, 2f), "B");
            _candidates.Add(a);
            _candidates.Add(b);

            GazeTargetDecision first = TickFor(0.2f);
            string firstName = first.Name;

            GazeTargetDecision later = TickFor(_profile.MaxContinuousHoldSeconds + 1f);

            Assert.That(later.Name, Is.Not.EqualTo(firstName),
                "With an equal-priority alternative the arbiter must eventually glance away.");
        }

        [Test]
        public void EqualPriorityCandidates_DoNotThrashBetweenTicks()
        {
            _candidates.Add(WorldCandidate(new Vector3(1f, 1.5f, 2f), "A"));
            _candidates.Add(WorldCandidate(new Vector3(-1f, 1.5f, 2f), "B"));

            _arbiter.Tick(_candidates, null, true, _profile, Dt);
            string first = _arbiter.Current.Name;
            int generation = _arbiter.Current.GenerationId;

            // 2 s — far below any interest-budget break horizon. Interest drain lowers the
            // incumbent's score a hair every tick; without stickiness that flips the
            // target every other tick (visible as target flicker and head jitter).
            for (int i = 0; i < 120; i++)
            {
                _arbiter.Tick(_candidates, null, true, _profile, Dt);
                Assert.That(_arbiter.Current.Name, Is.EqualTo(first),
                    "Interest drain must never let an equal-priority alternative flicker the target.");
            }

            Assert.That(_arbiter.Current.GenerationId, Is.EqualTo(generation),
                "A stable hold must not bump the generation (no phantom saccade triggers).");
        }

        [Test]
        public void LowerTierAlternatives_NeverForceBreakTheIncumbent()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));
            _candidates.Add(WorldCandidate(new Vector3(1f, 1.5f, 2f), "prop"));

            TickFor(0.5f);
            int generation = _arbiter.Current.GenerationId;

            TickFor(_profile.MaxContinuousHoldSeconds + 2f);

            Assert.That(_arbiter.Current.Kind, Is.EqualTo(GazeTargetKind.Player),
                "A committed player lock must never break against lower-tier world objects.");
            Assert.That(_arbiter.Current.GenerationId, Is.EqualTo(generation),
                "No re-acquisition twitch: the generation stays stable through the hold cap.");
        }

        [Test]
        public void HigherPriorityTier_AlwaysWins()
        {
            _candidates.Add(WorldCandidate(new Vector3(1f, 1.5f, 2f), "low", priority: 1));
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));

            GazeTargetDecision decision = TickFor(0.5f);

            Assert.That(decision.Kind, Is.EqualTo(GazeTargetKind.Player));
        }

        [Test]
        public void Reset_ClearsAllState()
        {
            _candidates.Add(PlayerCandidate(new Vector3(0f, 1.6f, 2f)));
            TickFor(1f);

            _arbiter.Reset();

            Assert.That(_arbiter.Current.Kind, Is.EqualTo(GazeTargetKind.None));
            Assert.That(_arbiter.Current.Commitment, Is.EqualTo(0f));
        }
    }
}
