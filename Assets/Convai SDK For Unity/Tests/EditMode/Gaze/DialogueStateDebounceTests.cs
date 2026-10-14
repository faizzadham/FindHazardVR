using Convai.Domain.Embodiment.Semantics;
using Convai.Modules.Gaze.Core.Policy;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     The confirmation window gaze reads the conversation's dialogue state through.
    /// </summary>
    /// <remarks>
    ///     The measurement this exists for: at an utterance boundary the state handed to gaze
    ///     alternates Speaking/Settling on consecutive frames for 0.2-0.3 s (and, in one recorded
    ///     run, for 7 s during a long answer). So the square-wave case is not a stress test, it is
    ///     the shipped input.
    /// </remarks>
    public sealed class DialogueStateDebounceTests
    {
        /// <summary>One frame of the flicker measured in play mode, ~15 ms.</summary>
        private const float FlickerFrame = 0.015f;

        /// <summary>
        ///     Frames of <see cref="FlickerFrame" /> the window spans. Counted rather than timed:
        ///     eight additions of 0.015f may land an ULP either side of 0.12f, and a test whose
        ///     verdict turns on that is measuring the FPU, not the seam.
        /// </summary>
        private static readonly int WindowFrames =
            Mathf.CeilToInt(DialogueStateDebounce.ConfirmationWindowSeconds / FlickerFrame);

        private DialogueStateDebounce _debounce;

        [SetUp]
        public void SetUp() => _debounce = new DialogueStateDebounce();

        /// <summary>Adopts <paramref name="state" /> outright, as the first tick of a binding does.</summary>
        private void Prime(DialogueState state) => _debounce.Tick(state, FlickerFrame);

        /// <summary>
        ///     Holds <paramref name="state" /> until it is adopted, and returns how many frames
        ///     after the first one reporting it that took. Zero would mean "adopted immediately".
        /// </summary>
        private int FramesUntilAdopted(DialogueState state)
        {
            int frames = 0;
            while (_debounce.Tick(state, FlickerFrame) != state)
            {
                frames++;
                Assert.That(frames, Is.LessThan(500), $"{state} was never adopted.");
            }

            return frames;
        }

        [Test]
        public void FirstTick_AdoptsWhateverTheConversationIsDoing()
        {
            Assert.That(_debounce.Current, Is.EqualTo(DialogueState.Idle),
                "Nothing has been reported yet, so there is nothing to act on but Idle.");

            Assert.That(_debounce.Tick(DialogueState.Speaking, FlickerFrame),
                Is.EqualTo(DialogueState.Speaking),
                "A fresh binding must not open with a window of stale Idle — there is nothing " +
                "to disagree with on the first tick.");
        }

        [Test]
        public void FifteenMillisecondSquareWave_NeverLeavesSpeaking()
        {
            Prime(DialogueState.Speaking);

            // Two seconds — an order of magnitude longer than the measured boundary flicker, and
            // long enough that any per-edge accumulation would have adopted many times over.
            const float durationSeconds = 2f;
            var frames = (int)(durationSeconds / FlickerFrame);

            for (int i = 0; i < frames; i++)
            {
                DialogueState raw = i % 2 == 0 ? DialogueState.Settling : DialogueState.Speaking;
                DialogueState adopted = _debounce.Tick(raw, FlickerFrame);

                Assert.That(adopted, Is.EqualTo(DialogueState.Speaking),
                    $"Frame {i}: a state that never held for a window must never reach a gaze consumer.");
            }

            Assert.That(_debounce.Current, Is.EqualTo(DialogueState.Speaking));
        }

        [Test]
        public void StateHeldForTheWindow_IsAdoptedAtTheWindow_AndNotBefore()
        {
            Prime(DialogueState.Speaking);

            int frames = FramesUntilAdopted(DialogueState.Settling);

            Assert.That(frames, Is.GreaterThanOrEqualTo(WindowFrames),
                "Adopted early — the window is the whole mechanism.");
            Assert.That(frames, Is.LessThanOrEqualTo(WindowFrames + 1),
                "A genuine transition must arrive at the window, not appreciably after it: " +
                "one frame of quantisation is the whole allowance.");
        }

        [TestCase(DialogueState.Interrupted)]
        [TestCase(DialogueState.Reacting)]
        public void Reflexes_AreAdoptedOnTheFrameTheyAreReported(DialogueState reflex)
        {
            Prime(DialogueState.Speaking);

            Assert.That(_debounce.Tick(reflex, FlickerFrame), Is.EqualTo(reflex),
                "A startle that lands an eighth of a second late is not a startle.");
            Assert.IsTrue(DialogueStateDebounce.IsReflex(reflex));
        }

        [Test]
        public void LeavingAReflex_IsDebouncedLikeAnythingElse()
        {
            Prime(DialogueState.Speaking);
            _debounce.Tick(DialogueState.Interrupted, FlickerFrame);

            for (int i = 0; i < 5; i++)
                Assert.That(_debounce.Tick(DialogueState.Attending, FlickerFrame),
                    Is.EqualTo(DialogueState.Interrupted),
                    "The exemption is for arriving at a reflex, not for leaving one.");
        }

        [TestCase(DialogueState.Idle)]
        [TestCase(DialogueState.Listening)]
        [TestCase(DialogueState.Attending)]
        [TestCase(DialogueState.Thinking)]
        [TestCase(DialogueState.Speaking)]
        [TestCase(DialogueState.Settling)]
        public void NonReflexStates_AreNotExempt(DialogueState state) =>
            Assert.IsFalse(DialogueStateDebounce.IsReflex(state),
                "The exemption list is deliberately two states long.");

        [Test]
        public void ThreeWayFlicker_RestartsTheWindowRatherThanExtendingIt()
        {
            Prime(DialogueState.Speaking);

            // Neither candidate ever holds: "Settling, Thinking, Settling" is not a fifth of a
            // second of anything, and a naive "raw != current" timer would adopt one of them.
            for (int i = 0; i < 120; i++)
            {
                DialogueState raw = i % 2 == 0 ? DialogueState.Settling : DialogueState.Thinking;
                Assert.That(_debounce.Tick(raw, FlickerFrame), Is.EqualTo(DialogueState.Speaking));
            }
        }

        [Test]
        public void ACandidateThatGoesAwayAndComesBack_StartsItsWindowAgain()
        {
            Prime(DialogueState.Speaking);

            // Most of a window's worth of Settling...
            for (int i = 0; i < 6; i++) _debounce.Tick(DialogueState.Settling, FlickerFrame);
            Assert.That(_debounce.Current, Is.EqualTo(DialogueState.Speaking));

            // ...one frame of Speaking, which is exactly the flicker...
            _debounce.Tick(DialogueState.Speaking, FlickerFrame);

            // ...and the next Settling frame must not inherit the earlier accumulation.
            Assert.That(_debounce.Tick(DialogueState.Settling, FlickerFrame),
                Is.EqualTo(DialogueState.Speaking));
        }

        [Test]
        public void HasPendingState_ReportsAnUnconfirmedDisagreement()
        {
            Prime(DialogueState.Speaking);
            Assert.IsFalse(_debounce.HasPendingState);

            _debounce.Tick(DialogueState.Settling, FlickerFrame);
            Assert.IsTrue(_debounce.HasPendingState, "Settling is on the table but not adopted.");

            _debounce.Tick(DialogueState.Speaking, FlickerFrame);
            Assert.IsFalse(_debounce.HasPendingState, "The disagreement went away.");
        }

        [Test]
        public void Reset_ForgetsThePendingState()
        {
            Prime(DialogueState.Speaking);

            // Accumulate most of a window toward Settling.
            for (int i = 0; i < 7; i++) _debounce.Tick(DialogueState.Settling, FlickerFrame);
            Assert.That(_debounce.Current, Is.EqualTo(DialogueState.Speaking));
            Assert.IsTrue(_debounce.HasPendingState);

            _debounce.Reset();

            Assert.That(_debounce.Current, Is.EqualTo(DialogueState.Idle));
            Assert.IsFalse(_debounce.HasPendingState);

            // Re-bind onto Speaking, then ask for Settling again: it must serve a full window,
            // not the remainder of the one the previous binding had almost finished.
            _debounce.Tick(DialogueState.Speaking, FlickerFrame);

            Assert.That(FramesUntilAdopted(DialogueState.Settling), Is.GreaterThanOrEqualTo(WindowFrames),
                "Reset left half a window on the clock — a rebind inherited the old binding's " +
                "half-confirmed candidate.");
        }

        [Test]
        public void NonPositiveDeltaTime_DoesNotAdvanceTheWindow()
        {
            Prime(DialogueState.Speaking);

            for (int i = 0; i < 100; i++)
                Assert.That(_debounce.Tick(DialogueState.Settling, 0f), Is.EqualTo(DialogueState.Speaking));

            for (int i = 0; i < 100; i++)
                Assert.That(_debounce.Tick(DialogueState.Settling, -1f), Is.EqualTo(DialogueState.Speaking),
                    "A paused or rewound clock must not confirm a state by accident.");
        }
    }
}
