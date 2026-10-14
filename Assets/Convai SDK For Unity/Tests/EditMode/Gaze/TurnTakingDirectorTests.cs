using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Behaviors;
using Convai.Modules.Gaze.Data;
using Convai.Modules.Gaze.Editor;
using Convai.Runtime.Embodiment;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    public sealed class TurnTakingDirectorTests
    {
        private const float Dt = 1f / 60f;
        private const uint Seed = 1234u;

        private ConvaiGazeProfile _profile;
        private TurnTakingDirector _director;
        private DeterministicEmbodimentRandom _random;

        [SetUp]
        public void SetUp()
        {
            _profile = ConvaiGazeProfile.CreateDefault();
            _director = new TurnTakingDirector();
            _random = new DeterministicEmbodimentRandom(Seed);
        }

        [TearDown]
        public void TearDown() => Object.DestroyImmediate(_profile);

        private void SetEnabled(bool value) => SetBool("enableTurnTakingGaze", value);
        private void SetYieldHeadDipEnabled(bool value) => SetBool("enableYieldHeadDip", value);

        private void SetProbability(float value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, "planningBreakProbability").floatValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private void SetBool(string field, bool value)
        {
            var serialized = new SerializedObject(_profile);
            GazeProfileSerializedPaths.Find(serialized, field).boolValue = value;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        /// <summary>Primes the director's edge detector so the next Speaking tick registers as a fresh entry (mirrors InterruptionReactionDirectorTests' "no edge yet" priming).</summary>
        private void PrimeIdle() => _director.Tick(DialogueState.Idle, _profile, false, false, 0f, Dt, ref _random);

        private void PrimeThinking()
        {
            _director.Tick(DialogueState.Idle, _profile, false, false, 0f, Dt, ref _random);
            for (int i = 0; i < 30; i++)
                _director.Tick(DialogueState.Thinking, _profile, false, false, 0f, Dt, ref _random);
        }

        private void TickSpeaking(bool finalTranscript = false, float energy = 0f, bool locked = false) =>
            _director.Tick(DialogueState.Speaking, _profile, locked, finalTranscript, energy, Dt, ref _random);

        private void TickSpeakingContext(bool finalTranscript, int wordCount, bool locked = false) =>
            _director.Tick(DialogueState.Speaking, _profile, locked, finalTranscript, wordCount,
                false, false, 0f, Dt, ref _random);

        [Test]
        public void PlanningBreak_FiresWithinScheduledWindow_AndDurationBounds()
        {
            SetProbability(1f);
            PrimeThinking();

            int ticksToStart = -1;
            for (int i = 0; i < 20; i++)
            {
                TickSpeakingContext(finalTranscript: i == 0, wordCount: i == 0 ? 20 : 0);
                if (_director.PlanningBreakStarted)
                {
                    ticksToStart = i;
                    break;
                }
            }

            Assert.That(ticksToStart, Is.GreaterThanOrEqualTo(0), "With probability 1 the break must fire.");
            float startDelay = (ticksToStart + 1) * Dt;
            Assert.That(startDelay, Is.InRange(0f, 0.2f + Dt), "The break must start 0-0.2s after Speaking begins.");
            Assert.IsTrue(_director.PlanningBreakActive, "The break is active the tick it starts.");
            Assert.That(_director.StartedBreakKind, Is.EqualTo(TurnTakingBreakKind.Opening));
            Assert.That(_director.StartedAversionMode, Is.EqualTo(GazeAversionMode.Cognitive));
            Assert.That(_director.HeadParticipationScale, Is.EqualTo(0.65f).Within(0.0001f));

            float duration = Dt;
            while (_director.PlanningBreakActive)
            {
                TickSpeaking();
                duration += Dt;
            }

            Assert.That(duration, Is.InRange(0.35f - Dt, 0.7f + Dt), "Opening breaks stay brief and bounded.");
        }

        [Test]
        public void PlanningBreak_ExactlyOncePerUtterance()
        {
            SetProbability(1f);
            PrimeThinking();

            int fires = 0;
            int steps = Mathf.CeilToInt(3f / Dt);
            for (int i = 0; i < steps; i++)
            {
                TickSpeakingContext(finalTranscript: i == 0, wordCount: i == 0 ? 20 : 0);
                if (_director.PlanningBreakStarted) fires++;
            }

            Assert.That(fires, Is.EqualTo(1), "Only one planning break may fire per utterance.");
        }

        [Test]
        public void AversionSuppressionFactor_ZeroForWholeSpeakingTurn()
        {
            SetProbability(0f);
            PrimeIdle();

            int stepsUnderTwoSeconds = Mathf.FloorToInt(1.9f / Dt);
            for (int i = 0; i < stepsUnderTwoSeconds; i++)
            {
                TickSpeaking();
                Assert.That(_director.AversionSuppressionFactor, Is.EqualTo(0f),
                    "Aversion must stay suppressed for the first ~2s of Speaking.");
            }

            int stepsPastWindow = Mathf.CeilToInt(1.3f / Dt);
            for (int i = 0; i < stepsPastWindow; i++)
                TickSpeaking();

            Assert.That(_director.AversionSuppressionFactor, Is.EqualTo(0f),
                "TurnTakingDirector exclusively owns intentional Speaking breaks.");
        }

        [Test]
        public void Yield_FinalTranscriptAloneDoesNotFire()
        {
            PrimeIdle();
            for (int i = 0; i < 10; i++)
                TickSpeaking();

            Assert.IsFalse(_director.WantsYieldBlink, "Sanity: no yield yet.");

            TickSpeaking(finalTranscript: true);

            Assert.IsFalse(_director.WantsYieldBlink, "Final transcript may precede audio completion.");
            Assert.IsFalse(_director.YieldEngagementPinActive);
        }

        [Test]
        public void Yield_FiresOnEnergyDecayWithoutFinal()
        {
            PrimeIdle();
            bool fired = false;

            // Build up a clear energy peak.
            for (int i = 0; i < 30; i++)
            {
                TickSpeaking(energy: 1f);
                fired |= _director.WantsYieldBlink;
            }

            TickSpeaking(finalTranscript: true, energy: 1f);

            // Then let it decay well below 30% of the peak.
            int steps = Mathf.CeilToInt(1f / Dt);
            for (int i = 0; i < steps; i++)
            {
                TickSpeaking(energy: 0f);
                fired |= _director.WantsYieldBlink;
            }

            Assert.IsTrue(fired, "A decaying speech-energy trend must raise the floor-yield without isFinal.");
        }

        [Test]
        public void Yield_FallbackOnSpeakingExit()
        {
            PrimeIdle();

            // No speech-energy provider (energy stays 0): peak never crosses the tracking floor,
            // so decay can never trigger, and isFinal is never sent — only the exit fallback can fire.
            for (int i = 0; i < 10; i++)
                TickSpeaking(energy: 0f);

            Assert.IsFalse(_director.WantsYieldBlink, "Sanity: no yield yet.");

            _director.Tick(DialogueState.Listening, _profile, false, false, 0f, Dt, ref _random);

            Assert.IsTrue(_director.WantsYieldBlink, "Speaking exiting without a cue must fall back to a yield.");
        }

        [Test]
        public void Yield_NoSecondFireSameUtterance()
        {
            PrimeIdle();
            for (int i = 0; i < 10; i++)
                TickSpeaking();

            TickSpeaking(finalTranscript: true);
            _director.Tick(DialogueState.Listening, _profile, false, false, 0f, Dt, ref _random);
            Assert.IsTrue(_director.WantsYieldBlink, "Sanity: exit fallback fired the first yield.");

            int fires = 1;
            for (int i = 0; i < 60; i++)
            {
                TickSpeaking(finalTranscript: true);
                if (_director.WantsYieldBlink) fires++;
            }

            Assert.That(fires, Is.EqualTo(1), "Only one floor-yield may fire per utterance.");
        }

        [Test]
        public void Disabled_IsInert()
        {
            SetEnabled(false);
            SetProbability(1f);
            PrimeThinking();

            for (int i = 0; i < 10; i++)
                TickSpeaking();

            TickSpeaking(finalTranscript: true, energy: 1f);
            _director.Tick(DialogueState.Listening, _profile, false, false, 0f, Dt, ref _random);

            Assert.IsFalse(_director.PlanningBreakActive);
            Assert.IsFalse(_director.PlanningBreakStarted);
            Assert.IsFalse(_director.WantsYieldBlink);
            Assert.IsFalse(_director.YieldEngagementPinActive);
            Assert.That(_director.AversionSuppressionFactor, Is.EqualTo(1f));
            Assert.That(_director.YieldHeadDipOffset, Is.EqualTo(Vector2.zero));
        }

        [Test]
        public void Locked_BlocksPlanningBreak_ButYieldBlinkStillAllowed()
        {
            SetProbability(1f);
            PrimeIdle();

            int steps = Mathf.CeilToInt(1.5f / Dt);
            for (int i = 0; i < steps; i++)
            {
                TickSpeakingContext(finalTranscript: i == 0, wordCount: i == 0 ? 20 : 0, locked: true);
                Assert.IsFalse(_director.PlanningBreakStarted, "A locked eye-contact mode must block the planning break.");
                Assert.IsFalse(_director.PlanningBreakActive);
            }

            // A final transcript alone never fires the yield (it may precede audio completion);
            // trigger it through the energy-decay path — entirely under the lock — to prove the
            // lock gates only the planning break, never the floor-yield.
            for (int i = 0; i < 30; i++)
                TickSpeaking(energy: 1f, locked: true);

            bool fired = false;
            int decaySteps = Mathf.CeilToInt(1f / Dt);
            for (int i = 0; i < decaySteps; i++)
            {
                TickSpeaking(energy: 0f, locked: true);
                fired |= _director.WantsYieldBlink;
            }

            Assert.IsTrue(fired, "The floor-yield blink must still be allowed under a lock.");
        }

        [Test]
        public void YieldHeadDip_IsDisabledByDefaultWhileYieldBlinkStillFires()
        {
            PrimeIdle();

            for (int i = 0; i < 10; i++)
                TickSpeaking();

            TickSpeaking(finalTranscript: true);
            _director.Tick(DialogueState.Listening, _profile, false, false, 0f, Dt, ref _random);

            Assert.IsTrue(_director.WantsYieldBlink, "The floor-yield blink remains enabled by default.");
            Assert.That(_director.YieldHeadDipOffset, Is.EqualTo(Vector2.zero),
                "The floor-yield head dip must be disabled by default.");
        }

        [Test]
        public void YieldHeadDip_UsesDedicatedProfileFlag()
        {
            SetYieldHeadDipEnabled(true);
            PrimeIdle();

            for (int i = 0; i < 10; i++)
                TickSpeaking();

            TickSpeaking(finalTranscript: true);
            _director.Tick(DialogueState.Listening, _profile, false, false, 0f, Dt, ref _random);

            Assert.IsTrue(_director.WantsYieldBlink, "Enabling the head-dip flag must not affect the forced blink.");
            Assert.That(_director.YieldHeadDipOffset.y, Is.LessThan(0f),
                "Enabling the dedicated profile flag must add the downward head dip.");
            Assert.IsTrue(_director.YieldEngagementPinActive, "The engagement pin is independent of the head-dip flag.");
        }

        [Test]
        public void Determinism_SameSeedProducesSameOutputs()
        {
            SetProbability(0.7f);

            var directorA = new TurnTakingDirector();
            var randomA = new DeterministicEmbodimentRandom(Seed);
            var directorB = new TurnTakingDirector();
            var randomB = new DeterministicEmbodimentRandom(Seed);

            directorA.Tick(DialogueState.Idle, _profile, false, false, 0f, Dt, ref randomA);
            directorB.Tick(DialogueState.Idle, _profile, false, false, 0f, Dt, ref randomB);

            int steps = Mathf.CeilToInt(3f / Dt);
            for (int i = 0; i < steps; i++)
            {
                bool finalTranscript = i == 40;
                float energy = i < 20 ? 1f : 0f;

                directorA.Tick(DialogueState.Speaking, _profile, false, finalTranscript, energy, Dt, ref randomA);
                directorB.Tick(DialogueState.Speaking, _profile, false, finalTranscript, energy, Dt, ref randomB);

                Assert.That(directorA.PlanningBreakActive, Is.EqualTo(directorB.PlanningBreakActive), $"Step {i}: PlanningBreakActive diverged.");
                Assert.That(directorA.PlanningBreakStarted, Is.EqualTo(directorB.PlanningBreakStarted), $"Step {i}: PlanningBreakStarted diverged.");
                Assert.That(directorA.WantsYieldBlink, Is.EqualTo(directorB.WantsYieldBlink), $"Step {i}: WantsYieldBlink diverged.");
                Assert.That(directorA.YieldHeadDipOffset, Is.EqualTo(directorB.YieldHeadDipOffset), $"Step {i}: YieldHeadDipOffset diverged.");
            }
        }

        /// <summary>
        ///     The head's share of an aversion beat is a GAIN on a signal composed onto the pose
        ///     after the two-lane actuator, so it may fall as sharply as it likes — the beat it
        ///     belongs to starts from rest — but it may not rise sharply, because a cancelled
        ///     beat's offset is still most of the way out when it does.
        /// </summary>
        /// <remarks>
        ///     It used to rise in one frame, twice: the floor-yield's cancel drove the scale to 0
        ///     mid-beat, and the caller — which switched the whole term off outside Speaking —
        ///     restored it to 1 on the Speaking-exit edge. Both put <c>offset x delta</c> straight
        ///     onto the head, downstream of everything that shapes motion.
        /// </remarks>
        [Test]
        public void CancellingABreak_RestoresHeadParticipationOverARampRatherThanInOneFrame()
        {
            SetEnabled(true);
            SetProbability(1f);
            PrimeThinking();

            TickSpeaking(finalTranscript: true);
            for (int i = 0; i < 30 && !_director.PlanningBreakActive; i++)
                TickSpeaking();

            Assert.IsTrue(_director.PlanningBreakActive, "Precondition: the break must be running.");
            Assert.That(_director.HeadParticipationScale, Is.LessThan(0.7f),
                "A running break holds the head's share down; falling to it is allowed to be " +
                "instant because the beat's own offset starts at rest.");

            float duringBreak = _director.HeadParticipationScale;
            _director.CancelPlanningBreak();
            _director.Tick(DialogueState.Speaking, _profile, false, false, 0f, Dt, ref _random);

            Assert.That(_director.HeadParticipationScale, Is.LessThan(duringBreak + 0.25f),
                $"The scale jumped from {duringBreak:0.00} to " +
                $"{_director.HeadParticipationScale:0.00} in one frame. A cancel happens " +
                "mid-beat, with the aversion offset still large, so this rise is a pose step.");

            for (int i = 0; i < 60; i++)
                _director.Tick(DialogueState.Speaking, _profile, false, false, 0f, Dt, ref _random);

            Assert.That(_director.HeadParticipationScale, Is.EqualTo(1f).Within(0.02f),
                "A cancelled break leaves no break for the head to under-participate in, so the " +
                "scale must return to full — not park on 0, which is what the caller then had to " +
                "undo with a state-edge switch.");
        }

        /// <summary>
        ///     Leaving Speaking must not move the scale either: the caller now applies it
        ///     unconditionally, which is only safe because the director holds it at 1 outside a
        ///     break rather than relying on a state test at the consumer.
        /// </summary>
        [Test]
        public void OutsideSpeaking_HeadParticipationIsFull()
        {
            SetEnabled(true);
            PrimeIdle();

            for (int i = 0; i < 30; i++)
                TickSpeaking();

            for (int i = 0; i < 60; i++)
                _director.Tick(DialogueState.Settling, _profile, false, false, 0f, Dt, ref _random);

            Assert.That(_director.HeadParticipationScale, Is.EqualTo(1f).Within(0.02f));
        }
    }
}
