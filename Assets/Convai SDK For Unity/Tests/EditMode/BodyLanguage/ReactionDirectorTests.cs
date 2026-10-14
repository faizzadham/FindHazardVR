using System.Collections.Generic;
using Convai.Domain.Embodiment.Readings;
using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Behaviors;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Tests for <see cref="ReactionDirector" />: envelope shapes (bounded, C¹/zero-boundary
    ///     endpoints), autonomous spike detection with refractory/EMA behavior, and scripted
    ///     bypass/replace semantics.
    /// </summary>
    public sealed class ReactionDirectorTests
    {
        private const float Dt = 1f / 60f;

        private static EmotionReading NeutralReading() => EmotionReading.Neutral;

        private static EmotionReading Reading(string label, float score)
        {
            var scores = new Dictionary<string, float> { { label, score } };
            return new EmotionReading(label, score, scores, 0f, 0f);
        }

        private static void TickN(ReactionDirector director, in EmotionReading emotion, int n)
        {
            for (int i = 0; i < n; i++)
                director.Tick(in emotion, Dt);
        }

        // ── Flinch envelope ──────────────────────────────────────────────────

        [Test]
        public void Flinch_RisesWithinAttack_ThenDecaysToZero_WithoutFirstFrameJump()
        {
            var director = new ReactionDirector();
            EmotionReading neutral = NeutralReading();

            bool accepted = director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: true);
            Assert.IsTrue(accepted);

            // First tick after trigger: a C1 attack starting from zero must not jump far.
            director.Tick(in neutral, Dt);
            Assert.That(director.FlinchValue, Is.LessThan(0.5f),
                "The first tick after triggering must stay near zero (eased attack start).");

            // Advance through the attack (0.08s) — value should be rising toward the hold plateau.
            TickN(director, in neutral, 10);
            Assert.That(director.FlinchValue, Is.GreaterThan(0.5f),
                "By the end of the attack the envelope should be near its peak.");

            // Advance well past attack+hold+decay (0.08+0.05+0.6 = 0.73s ⇒ ~44 ticks at 60fps).
            TickN(director, in neutral, 60);
            Assert.That(director.FlinchValue, Is.EqualTo(0f),
                "The flinch envelope must decay fully back to zero.");
            Assert.AreEqual(ReactionKind.None, director.ActiveReaction, "The reaction must clear once its envelope finishes.");
        }

        // ── Bounce envelope ──────────────────────────────────────────────────

        [Test]
        public void Bounce_ZeroAtStartAndEnd_BoundedAndAlternatingSign()
        {
            var director = new ReactionDirector();
            EmotionReading neutral = NeutralReading();

            director.TryTrigger(ReactionKind.AmusementBounce, 1f, bypassRefractory: true);

            director.Tick(in neutral, Dt);
            Assert.That(director.BounceValue, Is.EqualTo(0f).Within(0.05f),
                "The Hann window must start near zero.");

            bool sawPositive = false;
            bool sawNegative = false;
            float maxAbs = 0f;
            int steps = 0;
            while (director.ActiveReaction == ReactionKind.AmusementBounce && steps < 200)
            {
                director.Tick(in neutral, Dt);
                if (director.BounceValue > 0.05f) sawPositive = true;
                if (director.BounceValue < -0.05f) sawNegative = true;
                maxAbs = Mathf.Max(maxAbs, Mathf.Abs(director.BounceValue));
                steps++;
            }

            Assert.IsTrue(sawPositive, "A 2Hz oscillation over 1.2s must produce a positive lobe.");
            Assert.IsTrue(sawNegative, "A 2Hz oscillation over 1.2s must produce a negative lobe.");
            Assert.That(maxAbs, Is.LessThanOrEqualTo(1.01f), "The bounce envelope must stay bounded within ±1.");
            Assert.AreEqual(ReactionKind.None, director.ActiveReaction, "The bounce must self-terminate.");
            Assert.That(director.BounceValue, Is.EqualTo(0f), "The bounce must end exactly at zero.");
        }

        // ── Spike detection ──────────────────────────────────────────────────

        [Test]
        public void SurpriseSpike_StepFromZero_TriggersOnce_SustainedDoesNotRetrigger()
        {
            var director = new ReactionDirector();
            EmotionReading rest = Reading("surprise", 0f);

            // Settle the EMA at 0 first.
            TickN(director, in rest, 120);
            Assert.AreEqual(ReactionKind.None, director.ActiveReaction);

            EmotionReading spike = Reading("surprise", 0.8f);
            director.Tick(in spike, Dt);

            Assert.AreEqual(ReactionKind.SurpriseFlinch, director.ActiveReaction,
                "A sudden 0→0.8 surprise step must trigger a flinch.");

            // Let the flinch envelope finish, then keep feeding the SUSTAINED 0.8 score — once
            // the EMA catches up the spike collapses back near zero and must not retrigger.
            TickN(director, in spike, 120); // envelope finishes well before this
            ReactionKind afterSustain = director.ActiveReaction;
            Assert.AreEqual(ReactionKind.None, afterSustain,
                "A sustained high score (EMA caught up) must not keep retriggering the flinch.");
        }

        [Test]
        public void SurpriseSpike_Refractory_BlocksImmediateRetrigger_AllowsAfterWindowElapses()
        {
            var director = new ReactionDirector();
            EmotionReading rest = Reading("surprise", 0f);
            TickN(director, in rest, 120);

            EmotionReading spike = Reading("surprise", 0.8f);
            director.Tick(in spike, Dt);
            Assert.AreEqual(ReactionKind.SurpriseFlinch, director.ActiveReaction);

            // Return to rest so the EMA can fall again, then re-spike ~2s later — still within
            // the 8s refractory window, so this second spike must be refused.
            EmotionReading dip = Reading("surprise", 0f);
            TickN(director, in dip, 60); // ~1s back to rest — envelope already finished
            TickN(director, in spike, 1); // re-spike attempt at ~t≈1s
            Assert.AreNotEqual(ReactionKind.SurpriseFlinch, director.ActiveReaction,
                "A second spike within the 8s refractory window must not retrigger the flinch.");

            // Advance well past the 8s refractory (return to rest first so the EMA falls, then
            // spike again).
            TickN(director, in dip, 60 * 8); // ~8s more at rest
            director.Tick(in spike, Dt);
            Assert.AreEqual(ReactionKind.SurpriseFlinch, director.ActiveReaction,
                "A spike after the refractory window elapses must trigger normally.");
        }

        // ── TryTrigger semantics ─────────────────────────────────────────────

        [Test]
        public void TryTrigger_Bypass_AlwaysFires_AndRearmsRefractory()
        {
            var director = new ReactionDirector();

            Assert.IsTrue(director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: true));
            Assert.IsTrue(director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: true),
                "bypassRefractory: true must always fire, even immediately after a prior trigger.");

            // Non-bypassing retrigger right after must be refused (refractory just re-armed).
            Assert.IsFalse(director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: false));
        }

        [Test]
        public void TryTrigger_NewKind_ReplacesActiveReaction()
        {
            var director = new ReactionDirector();

            director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: true);
            Assert.AreEqual(ReactionKind.SurpriseFlinch, director.ActiveReaction);

            director.TryTrigger(ReactionKind.AmusementBounce, 1f, bypassRefractory: true);
            Assert.AreEqual(ReactionKind.AmusementBounce, director.ActiveReaction,
                "A new trigger must replace whatever reaction is currently playing.");
        }

        [Test]
        public void TryTrigger_CatchBreathAndSigh_AlwaysReturnFalse()
        {
            var director = new ReactionDirector();

            Assert.IsFalse(director.TryTrigger(ReactionKind.CatchBreath, 1f, bypassRefractory: true),
                "CatchBreath is a breath event, not a reaction envelope — must always be refused.");
            Assert.IsFalse(director.TryTrigger(ReactionKind.Sigh, 1f, bypassRefractory: true),
                "Sigh is a breath event, not a reaction envelope — must always be refused.");
            Assert.IsFalse(director.TryTrigger(ReactionKind.None, 1f, bypassRefractory: true));
            Assert.AreEqual(ReactionKind.None, director.ActiveReaction);
        }

        [Test]
        public void Reset_ReturnsToInactiveState()
        {
            var director = new ReactionDirector();
            director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: true);
            EmotionReading neutral = EmotionReading.Neutral;
            director.Tick(in neutral, Dt);

            director.Reset();

            Assert.AreEqual(ReactionKind.None, director.ActiveReaction);
            Assert.AreEqual(0f, director.FlinchValue);
            Assert.AreEqual(0f, director.BounceValue);

            // A fresh trigger right after Reset must work immediately (refractory cleared too).
            Assert.IsTrue(director.TryTrigger(ReactionKind.SurpriseFlinch, 1f, bypassRefractory: false));
        }
    }
}
