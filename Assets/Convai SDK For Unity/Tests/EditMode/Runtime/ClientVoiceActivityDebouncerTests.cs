using System.Collections.Generic;
using Convai.Runtime.Networking.Media;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ClientVoiceActivityDebouncerTests
    {
        [Test]
        public void Observe_ConfirmationWindows_DucksThenConfirms()
        {
            var observed = new List<ClientVoiceActivityStateChanged>();
            var debouncer = new ClientVoiceActivityDebouncer(observed.Add);

            for (int i = 0; i < ClientVoiceActivityDebouncer.ConfirmationWindows; i++)
                debouncer.Observe(ClientVoiceActivityDebouncer.ActivationThreshold);

            Assert.That(observed, Has.Count.EqualTo(2));
            Assert.That(observed[0].Stage, Is.EqualTo(ClientVoiceActivityStage.Candidate));
            Assert.That(observed[1].Stage, Is.EqualTo(ClientVoiceActivityStage.Confirmed));
        }

        [Test]
        public void Observe_FewerThanConfirmationWindows_StaysCandidate()
        {
            var observed = new List<ClientVoiceActivityStateChanged>();
            var debouncer = new ClientVoiceActivityDebouncer(observed.Add);

            for (int i = 0; i < ClientVoiceActivityDebouncer.ConfirmationWindows - 1; i++)
                debouncer.Observe(ClientVoiceActivityDebouncer.ActivationThreshold);

            Assert.That(observed, Has.Count.EqualTo(1));
            Assert.That(observed[0].Stage, Is.EqualTo(ClientVoiceActivityStage.Candidate));
        }

        [Test]
        public void Observe_ShortCandidate_ReleasesWithoutConfirmation()
        {
            var observed = new List<ClientVoiceActivityStateChanged>();
            var debouncer = new ClientVoiceActivityDebouncer(observed.Add);

            debouncer.Observe(ClientVoiceActivityDebouncer.ActivationThreshold);
            debouncer.Observe(ClientVoiceActivityDebouncer.ReleaseThreshold);
            debouncer.Observe(ClientVoiceActivityDebouncer.ReleaseThreshold);

            Assert.That(observed, Has.Count.EqualTo(2));
            Assert.That(observed[0].Stage, Is.EqualTo(ClientVoiceActivityStage.Candidate));
            Assert.That(observed[1].Stage, Is.EqualTo(ClientVoiceActivityStage.Cancelled));
        }

        [Test]
        public void Observe_ConfirmedSpeech_EndsAfterSustainedSilence()
        {
            var observed = new List<ClientVoiceActivityStateChanged>();
            var debouncer = new ClientVoiceActivityDebouncer(observed.Add);

            for (int i = 0; i < ClientVoiceActivityDebouncer.ConfirmationWindows; i++)
                debouncer.Observe(ClientVoiceActivityDebouncer.ActivationThreshold);
            for (int i = 0; i < ClientVoiceActivityDebouncer.ConfirmedReleaseWindows; i++)
                debouncer.Observe(ClientVoiceActivityDebouncer.ReleaseThreshold);

            Assert.That(observed, Has.Count.EqualTo(3));
            Assert.That(observed[2].Stage, Is.EqualTo(ClientVoiceActivityStage.Ended));
        }

        [Test]
        public void Stop_ReleasesPendingCandidate()
        {
            var observed = new List<ClientVoiceActivityStateChanged>();
            var debouncer = new ClientVoiceActivityDebouncer(observed.Add);

            debouncer.Observe(ClientVoiceActivityDebouncer.ActivationThreshold);
            debouncer.Stop();

            Assert.That(observed, Has.Count.EqualTo(2));
            Assert.That(observed[1].Stage, Is.EqualTo(ClientVoiceActivityStage.Cancelled));
        }

        [Test]
        public void StateChanged_CarriesEffectiveAcousticEchoCancellationState()
        {
            var state = new ClientVoiceActivityStateChanged(
                ClientVoiceActivityStage.Confirmed,
                0.9f,
                isAcousticEchoCancellationActive: true);

            Assert.That(state.IsAcousticEchoCancellationActive, Is.True);
        }
    }
}
