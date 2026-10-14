using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.BodyLanguage.Core.Policy;
using Convai.Modules.BodyLanguage.Data;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyLanguage
{
    /// <summary>
    ///     Behavior tests for <see cref="BodyLanguagePolicyEngine" />: smooth (never snapping)
    ///     policy transitions, stable steady-state resolution, and determinism.
    /// </summary>
    public sealed class BodyLanguagePolicyEngineTests
    {
        private const float Dt = 1f / 60f;
        private const float TransitionSeconds = 0.4f;

        private static BodyLanguageStatePolicy IdlePolicy() => new()
        {
            State = DialogueState.Idle,
            GesticulationIntensity = 0f,
            PostureOpennessBias = 0f,
            BreathRateCpm = 13f,
            BreathDepth = 0.5f
        };

        private static BodyLanguageStatePolicy SpeakingPolicy() => new()
        {
            State = DialogueState.Speaking,
            GesticulationEnabled = true,
            GesticulationIntensity = 0.8f,
            PostureOpennessBias = 0.2f,
            BreathRateCpm = 14f,
            BreathDepth = 0.6f
        };

        [Test]
        public void FirstTick_SnapsToTarget()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            BodyLanguageStatePolicy result = engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.That(result.GesticulationIntensity, Is.EqualTo(0.8f).Within(1e-5f),
                "The first tick must snap so enable never eases in from a zeroed policy.");
        }

        [Test]
        public void StateSwitch_BlendsOverTransitionTime_NoSnap()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.Tick(in idle, TransitionSeconds, Dt);

            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            BodyLanguageStatePolicy afterOneTick = engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.That(afterOneTick.GesticulationIntensity, Is.GreaterThan(0f),
                "The blend must start moving toward the new policy.");
            Assert.That(afterOneTick.GesticulationIntensity, Is.LessThan(0.8f),
                "One tick must not snap to the new policy.");
            Assert.That(afterOneTick.BreathRateCpm, Is.GreaterThan(13f).And.LessThan(14f),
                "Breath rate must blend, not snap.");

            // After several transition time constants the blend converges.
            for (int i = 0; i < 600; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.That(engine.Current.GesticulationIntensity, Is.EqualTo(0.8f).Within(1e-3f));
            Assert.That(engine.Current.BreathRateCpm, Is.EqualTo(14f).Within(1e-2f));
        }

        [Test]
        public void BooleanGates_FollowTargetImmediately()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.Tick(in idle, TransitionSeconds, Dt);

            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            BodyLanguageStatePolicy result = engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.IsTrue(result.GesticulationEnabled,
                "Boolean gates switch immediately; downstream directors own their ramps.");
            Assert.That(result.State, Is.EqualTo(DialogueState.Speaking));
        }

        [Test]
        public void RepeatedSameStateResolution_IsStable()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            for (int i = 0; i < 300; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);

            float settled = engine.Current.GesticulationIntensity;
            for (int i = 0; i < 300; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.That(engine.Current.GesticulationIntensity, Is.EqualTo(settled).Within(1e-5f),
                "A settled policy must not drift or oscillate under repeated same-state ticks.");
        }

        [Test]
        public void ZeroTransitionSeconds_Snaps()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.Tick(in idle, 0f, Dt);
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            BodyLanguageStatePolicy result = engine.Tick(in speaking, 0f, Dt);

            Assert.That(result.GesticulationIntensity, Is.EqualTo(0.8f).Within(1e-5f),
                "A zero transition time snaps by contract.");
        }

        [Test]
        public void IdenticalInputSequences_ProduceIdenticalOutputs()
        {
            var a = new BodyLanguagePolicyEngine();
            var b = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy idle = IdlePolicy();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();

            for (int i = 0; i < 120; i++)
            {
                BodyLanguageStatePolicy target = i < 60 ? idle : speaking;
                BodyLanguageStatePolicy resultA = a.Tick(in target, TransitionSeconds, Dt);
                BodyLanguageStatePolicy resultB = b.Tick(in target, TransitionSeconds, Dt);

                Assert.That(resultA.GesticulationIntensity, Is.EqualTo(resultB.GesticulationIntensity),
                    $"Tick {i}: the engine must be deterministic.");
                Assert.That(resultA.BreathRateCpm, Is.EqualTo(resultB.BreathRateCpm));
                Assert.That(resultA.PostureOpennessBias, Is.EqualTo(resultB.PostureOpennessBias));
            }
        }

        [Test]
        public void BeginHold_FreezesScalars_ButBooleanGatesStillSnap()
        {
            var engine = new BodyLanguagePolicyEngine();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            for (int i = 0; i < 300; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);
            float frozenIntensity = engine.Current.GesticulationIntensity;

            // Enter Idle (gesticulation off) under a freeze hold: scalars must not blend, but the
            // gesticulation gate must still snap off so a frozen character stops gesturing.
            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.BeginHold(0.3f);
            BodyLanguageStatePolicy result = engine.Tick(in idle, TransitionSeconds, Dt);

            Assert.That(result.GesticulationIntensity, Is.EqualTo(frozenIntensity).Within(1e-4f),
                "During a freeze hold the scalar policy must not blend (the 'hard pause' beat).");
            Assert.IsFalse(result.GesticulationEnabled,
                "Boolean gates must still snap during a hold so gesticulation hard-stops.");
            Assert.That(result.State, Is.EqualTo(DialogueState.Idle));
            Assert.IsTrue(engine.IsHolding);
        }

        [Test]
        public void Hold_Elapses_ThenBlendingResumes()
        {
            var engine = new BodyLanguagePolicyEngine();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            for (int i = 0; i < 300; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);

            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.BeginHold(0.3f);

            // Tick through the hold (~0.3s ≈ 18 ticks): scalars stay frozen the whole time.
            for (int i = 0; i < 18; i++)
                engine.Tick(in idle, TransitionSeconds, Dt);
            float duringHold = engine.Current.GesticulationIntensity;
            Assert.That(duringHold, Is.GreaterThan(0.7f), "Scalars must stay frozen through the hold.");

            // Past the hold, blending resumes toward Idle's zero.
            for (int i = 0; i < 300; i++)
                engine.Tick(in idle, TransitionSeconds, Dt);

            Assert.IsFalse(engine.IsHolding);
            Assert.That(engine.Current.GesticulationIntensity, Is.EqualTo(0f).Within(1e-2f),
                "After the hold elapses the scalar policy must resume blending to the new target.");
        }

        [Test]
        public void LongerHoldRequest_DoesNotShortenAnActiveHold()
        {
            var engine = new BodyLanguagePolicyEngine();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            engine.Tick(in speaking, TransitionSeconds, Dt);

            engine.BeginHold(0.5f);
            engine.BeginHold(0.1f); // shorter — must be ignored
            Assert.IsTrue(engine.IsHolding);

            // Tick 0.2s (12 ticks): a 0.1s hold would have expired; a 0.5s hold still holds.
            for (int i = 0; i < 12; i++)
                engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.IsTrue(engine.IsHolding,
                "A shorter BeginHold must not cut an active longer hold short.");
        }

        [Test]
        public void Reset_ClearsHold()
        {
            var engine = new BodyLanguagePolicyEngine();
            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            engine.Tick(in speaking, TransitionSeconds, Dt);
            engine.BeginHold(1f);
            Assert.IsTrue(engine.IsHolding);

            engine.Reset();

            Assert.IsFalse(engine.IsHolding, "Reset must clear an active freeze hold.");
        }

        [Test]
        public void Reset_MakesNextTickSnapAgain()
        {
            var engine = new BodyLanguagePolicyEngine();

            BodyLanguageStatePolicy idle = IdlePolicy();
            engine.Tick(in idle, TransitionSeconds, Dt);
            engine.Reset();

            BodyLanguageStatePolicy speaking = SpeakingPolicy();
            BodyLanguageStatePolicy result = engine.Tick(in speaking, TransitionSeconds, Dt);

            Assert.That(result.GesticulationIntensity, Is.EqualTo(0.8f).Within(1e-5f),
                "After Reset the next tick must snap like a fresh engine.");
        }
    }
}
