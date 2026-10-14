using System;
using System.Collections;
using System.Collections.Generic;
using Convai.Infrastructure.Networking.WebGL;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    /// <summary>
    ///     On WebGL the browser sends the microphone audio, so muting the source must mute the published track.
    /// </summary>
    [TestFixture]
    public sealed class WebGLMicrophoneMuteSyncTests
    {
        private WebGLMicrophoneSource _source;
        private List<bool> _applied;
        private List<bool?> _outcomes;
        private List<IEnumerator> _routines;

        [SetUp]
        public void SetUp()
        {
            _source = new WebGLMicrophoneSource();
            _applied = new List<bool>();
            _outcomes = new List<bool?>();
            _routines = new List<IEnumerator>();
        }

        [Test]
        public void MutingTheSource_MutesThePublishedTrack_AndUnmutingReopensIt()
        {
            CreateSync(completeAtOnce: true);

            _source.IsMuted = true;
            Pump();
            _source.IsMuted = false;
            Pump();

            CollectionAssert.AreEqual(new[] { true, false }, _applied);
        }

        [Test]
        public void SourceMutedBeforePublish_IsMutedAtOnce()
        {
            _source.IsMuted = true;

            CreateSync(completeAtOnce: true);
            Pump();

            CollectionAssert.AreEqual(new[] { true }, _applied);
        }

        [Test]
        public void UnmutedSource_LeavesTheNewTrackAlone()
        {
            CreateSync(completeAtOnce: true);
            Pump();

            Assert.IsEmpty(_applied);
        }

        [Test]
        public void ChangesDuringAPendingMute_EndOnTheLatestState()
        {
            CreateSync(completeAtOnce: false);

            _source.IsMuted = true;
            _source.IsMuted = false;
            _source.IsMuted = true;
            _source.IsMuted = false;
            Assert.That(_applied, Has.Count.EqualTo(1), "Only one browser call may be in flight.");

            CompletePending();
            Pump();
            CompletePending();
            Pump();

            CollectionAssert.AreEqual(new[] { true, false }, _applied);
        }

        [Test]
        public void AfterDetach_NoMoreChangesReachTheTrack()
        {
            WebGLMicrophoneMuteSync sync = CreateSync(completeAtOnce: false);
            _source.IsMuted = true;

            sync.Detach();
            _source.IsMuted = false;
            CompletePending();
            Pump();

            CollectionAssert.AreEqual(new[] { true }, _applied,
                "An unmute applied after detach could reopen a microphone that is being unpublished.");
        }

        [Test]
        public void RefusedMute_IsNotTakenAsApplied_AndTheNextChangeTriesAgain()
        {
            CreateSync(completeAtOnce: false);
            _source.IsMuted = true;
            CompletePending(succeeded: false);
            Pump();

            _source.IsMuted = false;
            _source.IsMuted = true;
            CompletePending();
            Pump();

            CollectionAssert.AreEqual(new[] { true, true }, _applied,
                "A refused mute left the audio flowing, so the next change must mute again.");
        }

        private WebGLMicrophoneMuteSync CreateSync(bool completeAtOnce) =>
            new(_source,
                muted =>
                {
                    _applied.Add(muted);
                    int index = _outcomes.Count;
                    _outcomes.Add(completeAtOnce ? true : (bool?)null);
                    return () => _outcomes[index];
                },
                routine =>
                {
                    _routines.Add(routine);
                    Step(routine);
                });

        private void CompletePending(bool succeeded = true)
        {
            for (int i = 0; i < _outcomes.Count; i++) _outcomes[i] ??= succeeded;
        }

        private void Pump()
        {
            for (int frame = 0; frame < 10; frame++)
            {
                foreach (IEnumerator routine in _routines.ToArray()) Step(routine);
            }
        }

        private void Step(IEnumerator routine)
        {
            if (!routine.MoveNext()) _routines.Remove(routine);
        }
    }
}
