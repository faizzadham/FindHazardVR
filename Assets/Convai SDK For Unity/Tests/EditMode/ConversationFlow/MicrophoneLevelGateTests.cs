using System.Collections.Generic;
using Convai.Runtime.Networking.Media;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.ConversationFlow
{
    /// <summary>
    ///     The microphone level gate decides, from loudness alone, whether somebody has started
    ///     talking. It is allowed to be wrong — nothing it says can commit a turn — but it has to be
    ///     wrong in the right direction, and it has to survive the two things that break naive
    ///     level gates: a hot microphone and a long sentence.
    /// </summary>
    internal sealed class MicrophoneLevelGateTests
    {
        private List<(bool active, float level)> _events;
        private MicrophoneLevelGate _gate;

        [SetUp]
        public void SetUp()
        {
            _events = new List<(bool, float)>();
            _gate = new MicrophoneLevelGate((active, level) => _events.Add((active, level)));
        }

        private void Windows(float rms, int count)
        {
            for (int i = 0; i < count; i++) _gate.ObserveWindow(rms);
        }

        /// <summary>Enough quiet windows for the floor to settle on the room.</summary>
        private void Settle(float roomNoise) => Windows(roomNoise, 60);

        [Test]
        public void SteadyRoomNoise_NeverOpensTheGate()
        {
            Settle(0.01f);
            Windows(0.01f, 500);

            Assert.IsFalse(_gate.IsActive);
            Assert.IsEmpty(_events, "A room that is merely noisy is not somebody talking.");
        }

        [Test]
        public void SpeechOverRoomNoise_OpensTheGate()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);

            Assert.IsTrue(_gate.IsActive);
            Assert.AreEqual(1, _events.Count);
            Assert.IsTrue(_events[0].active);
        }

        [Test]
        public void OneLoudWindow_IsNotEnough()
        {
            Settle(0.01f);
            Windows(0.08f, 1);

            Assert.IsFalse(_gate.IsActive, "A single click or thump must not read as speech.");
        }

        /// <summary>
        ///     The test that makes the design worth having: the same absolute loudness means
        ///     "talking" on a quiet headset and "silence" on a hot desk microphone, so a fixed
        ///     threshold is a guess about the player's hardware.
        /// </summary>
        [Test]
        public void TheSameLoudness_MeansDifferentThingsOnDifferentMicrophones()
        {
            var quietEvents = new List<(bool, float)>();
            var hotEvents = new List<(bool, float)>();
            var quietMic = new MicrophoneLevelGate((a, l) => quietEvents.Add((a, l)));
            var hotMic = new MicrophoneLevelGate((a, l) => hotEvents.Add((a, l)));

            for (int i = 0; i < 60; i++)
            {
                quietMic.ObserveWindow(0.002f);
                hotMic.ObserveWindow(0.05f);
            }

            for (int i = 0; i < MicrophoneLevelGate.AttackWindows; i++)
            {
                quietMic.ObserveWindow(0.02f);
                hotMic.ObserveWindow(0.02f);
            }

            Assert.IsTrue(quietMic.IsActive, "On a quiet microphone this stands well clear of the room.");
            Assert.IsFalse(hotMic.IsActive, "On a hot microphone the very same level is below the room.");
        }

        [Test]
        public void ASustainedUtterance_DoesNotDragTheFloorUpUnderItself()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);
            Assert.IsTrue(_gate.IsActive);

            // Ten seconds of continuous speech. A floor that learned from it would climb until the
            // speaker fell under it and the character stopped attending mid-sentence.
            Windows(0.08f, 500);

            Assert.IsTrue(_gate.IsActive, "It must still hear the person who has not stopped talking.");
            Assert.AreEqual(1, _events.Count, "and must not have flickered.");
        }

        [Test]
        public void PausesInsideASentence_DoNotCloseTheGate()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);

            Windows(0.01f, MicrophoneLevelGate.ReleaseWindows - 1);
            Assert.IsTrue(_gate.IsActive, "A breath between words is not the end of the turn.");

            Windows(0.08f, 3);
            Windows(0.01f, MicrophoneLevelGate.ReleaseWindows - 1);
            Assert.IsTrue(_gate.IsActive);
        }

        [Test]
        public void SilenceAfterSpeech_ClosesTheGate()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);
            Windows(0.01f, MicrophoneLevelGate.ReleaseWindows);

            Assert.IsFalse(_gate.IsActive);
            Assert.AreEqual(2, _events.Count);
            Assert.IsFalse(_events[1].active);
            Assert.AreEqual(0f, _events[1].level, "The falling edge carries no level.");
        }

        [Test]
        public void ADeadDevice_ReadingZeroes_NeverOpensTheGate()
        {
            Windows(0f, 500);

            Assert.IsFalse(_gate.IsActive,
                "Against a floor of nothing, any faint sound looks infinitely loud — hence the silence floor.");
        }

        /// <summary>
        ///     A room that was noisy and then goes to digital silence drags the learned floor toward
        ///     zero. Without a floor under the floor, the next faint sound is enormous relative to
        ///     nothing and the character lurches at a whisper it should not have heard.
        /// </summary>
        [Test]
        public void AFloorThatDecaysToNothing_StillDoesNotAmplifyAWhisper()
        {
            Settle(0.02f);

            // The input goes properly silent — a device muted downstream, or a stream that stalled.
            Windows(0f, 400);
            Assert.IsFalse(_gate.IsActive);

            // Something very faint arrives. Against a floor of zero this would read as infinite.
            Windows(0.0005f, 10);

            Assert.IsFalse(_gate.IsActive,
                "A whisper below the silence floor is not somebody addressing the character.");
        }

        /// <summary>
        ///     While a character is audible the microphone is hearing it, not the player. The gate
        ///     must go deaf rather than merely be ignored downstream, or the character's own answer
        ///     would train the noise floor and leave the gate open when it finished.
        /// </summary>
        [Test]
        public void WhileSuppressed_TheGateHearsNothing()
        {
            Settle(0.01f);
            _gate.SetSuppressed(true);

            // The character's own voice, loud, for a whole answer.
            for (int i = 0; i < 200; i++) _gate.Observe(Buffer(0.08f), 1);

            Assert.IsFalse(_gate.IsActive);
            Assert.IsEmpty(_events);
        }

        [Test]
        public void AfterSuppressionLifts_TheGateHearsThePlayerAgain()
        {
            Settle(0.01f);
            _gate.SetSuppressed(true);
            for (int i = 0; i < 200; i++) _gate.Observe(Buffer(0.08f), 1);

            _gate.SetSuppressed(false);

            // It starts from nothing, so it re-learns the room before it can call anything speech.
            for (int i = 0; i < 60; i++) _gate.Observe(Buffer(0.01f), 1);
            for (int i = 0; i < MicrophoneLevelGate.AttackWindows; i++) _gate.Observe(Buffer(0.08f), 1);

            Assert.IsTrue(_gate.IsActive);
        }

        [Test]
        public void SuppressionWhileOpen_ClosesTheGateAndSaysSo()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);
            Assert.IsTrue(_gate.IsActive);
            int eventsBefore = _events.Count;

            _gate.SetSuppressed(true);
            _gate.Observe(Buffer(0.08f), 1);

            Assert.IsFalse(_gate.IsActive);
            Assert.AreEqual(eventsBefore + 1, _events.Count,
                "Suppression closes an open gate, and nobody else says so: SetSuppressed only "
                + "raises a flag and its caller publishes nothing. Silent here left every listener "
                + "holding 'the player is talking' until the player's NEXT utterance ended.");
            Assert.IsFalse(_events[^1].active);
        }

        /// <summary>
        ///     The gate's attack and release are authored as window counts, so a window has to be a
        ///     duration. It was a fixed 960 samples, which is 20 ms on a desktop capturing at 48 kHz
        ///     and 60 ms on a phone capturing at 16 kHz — so the same authored half-second release
        ///     hold lasted a second and a half on a platform this SDK ships to, and the character
        ///     went on treating the player as talking long after they had stopped.
        /// </summary>
        /// <remarks>
        ///     Release rather than attack, because it is the longer lever: the hold is
        ///     <see cref="MicrophoneLevelGate.ReleaseWindows" /> windows against the attack's two,
        ///     so a wrong window length shows up as a full second rather than one chunk of audio.
        /// </remarks>
        [Test]
        public void TheGateHoldsOpenForTheSameTime_WhateverTheDeviceRate()
        {
            float at48k = SecondsOfSilenceBeforeClosing(48000);
            float at16k = SecondsOfSilenceBeforeClosing(16000);

            float authored = MicrophoneLevelGate.ReleaseWindows * MicrophoneLevelGate.WindowSeconds;
            Assert.That(at48k, Is.EqualTo(authored).Within(0.05f),
                $"48 kHz held for {at48k:0.000}s against an authored {authored:0.000}s.");
            Assert.That(at16k, Is.EqualTo(at48k).Within(0.05f),
                $"48 kHz held the gate open for {at48k:0.000}s after the player stopped and 16 kHz "
                + $"for {at16k:0.000}s. A window is a duration, not a sample count.");
        }

        /// <summary>
        ///     Seconds of silence, fed at <paramref name="sampleRate" /> in 5 ms chunks, between the
        ///     player going quiet and the gate closing.
        /// </summary>
        private static float SecondsOfSilenceBeforeClosing(int sampleRate)
        {
            var gate = new MicrophoneLevelGate((active, level) => { });

            const float chunkSeconds = 0.005f;
            int chunk = Mathf.RoundToInt(sampleRate * chunkSeconds);
            float[] quiet = Filled(chunk, 0.01f);
            float[] loud = Filled(chunk, 0.08f);

            // Two seconds of room, then speech until it opens — as a real device would arrive.
            for (int i = 0; i < 400; i++) gate.Observe(quiet, 1, sampleRate);
            for (int i = 0; i < 400 && !gate.IsActive; i++) gate.Observe(loud, 1, sampleRate);
            Assert.IsTrue(gate.IsActive, $"The gate never opened at {sampleRate} Hz.");

            for (int i = 1; i <= 1600; i++)
            {
                gate.Observe(quiet, 1, sampleRate);
                if (!gate.IsActive) return i * chunkSeconds;
            }

            Assert.Fail($"The gate never closed at {sampleRate} Hz.");
            return float.NaN;
        }

        private static float[] Filled(int length, float level)
        {
            var samples = new float[length];
            for (int i = 0; i < length; i++) samples[i] = level;
            return samples;
        }

        /// <summary>One window's worth of mono samples at a given loudness.</summary>
        private static float[] Buffer(float level)
        {
            var samples = new float[MicrophoneLevelGate.WindowSamples];
            for (int i = 0; i < samples.Length; i++) samples[i] = level;
            return samples;
        }

        [Test]
        public void Reset_ClosesAnOpenGateAndSaysSo()
        {
            Settle(0.01f);
            Windows(0.08f, MicrophoneLevelGate.AttackWindows);
            Assert.IsTrue(_gate.IsActive);

            _gate.Reset();

            Assert.IsFalse(_gate.IsActive);
            Assert.AreEqual(2, _events.Count);
            Assert.IsFalse(_events[1].active, "A consumer left holding a stale 'active' would attend forever.");
        }

        [Test]
        public void Reset_OnAClosedGate_SaysNothing()
        {
            Settle(0.01f);
            _gate.Reset();

            Assert.IsEmpty(_events);
        }

        [Test]
        public void Observe_AcceptsInterleavedStereoWithoutMisreadingIt()
        {
            // Left channel loud, right silent. Reading every sample instead of every frame would
            // halve the measured loudness and make the gate deaf on stereo devices.
            var stereo = new float[MicrophoneLevelGate.WindowSamples * 2];
            for (int i = 0; i < stereo.Length; i += 2)
            {
                stereo[i] = 0.08f;
                stereo[i + 1] = 0f;
            }

            var quiet = new float[MicrophoneLevelGate.WindowSamples * 2];
            for (int i = 0; i < quiet.Length; i += 2)
            {
                quiet[i] = 0.01f;
                quiet[i + 1] = 0f;
            }

            for (int i = 0; i < 60; i++) _gate.Observe(quiet, 2);
            for (int i = 0; i < MicrophoneLevelGate.AttackWindows; i++) _gate.Observe(stereo, 2);

            Assert.IsTrue(_gate.IsActive);
        }


    }
}
