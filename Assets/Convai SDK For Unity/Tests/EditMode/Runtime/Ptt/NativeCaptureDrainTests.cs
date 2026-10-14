using System;
using System.Collections;
using LiveKit;
using LiveKit.Internal.FFI;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime
{
    public class NativeCaptureDrainTests
    {
        private GameObject _listener;

        [SetUp]
        public void SetUp()
        {
            FfiClient.Instance.Initialize();
            Assert.That(FfiClient.IsOperational, Is.True, "A real native FFI is required for the drain test.");
            _listener = new GameObject("PTT drain audio listener");
            _listener.AddComponent<AudioListener>();
        }

        [TearDown]
        public void TearDown() => UnityEngine.Object.DestroyImmediate(_listener);

        private sealed class Source : RtcAudioSource
        {
            public Source(bool processAudio) : base(RtcAudioSourceType.AudioSourceMicrophone, 48000, 1, processAudio) { }
            public override event Action<float[], int, int> AudioRead;
            public void Emit(int samples) => AudioRead?.Invoke(new float[samples], 1, 48000);
        }

        [UnityTest]
        public IEnumerator DrainWaitsForRealNativeCallbacksAndClosesCaptureBoundary()
        {
            using var source = new Source(false);
            int observed = 0;
            source.ProcessedAudioRead += (samples, channels, rate) => observed += samples.Length;
            source.Start();
            source.Emit(480);
            source.Emit(480);
            var drain = source.StopAndDrainCaptureAsync();
            Assert.That(source.StopAndDrainCaptureAsync(), Is.SameAs(drain));
            source.Emit(480);
            float deadline = Time.realtimeSinceStartup + 5;
            while (!drain.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(drain.IsCompleted, Is.True, "Native capture callbacks did not finish.");
            Assert.That(drain.IsFaulted, Is.False, drain.Exception?.ToString());
            Assert.That(observed, Is.EqualTo(960), "Samples after the cutoff must not be accepted.");
        }

        [UnityTest]
        public IEnumerator DrainFlushesFinalPartialProcessingFrame()
        {
            using var source = new Source(true);
            int observed = 0;
            source.ProcessedAudioRead += (samples, channels, rate) => observed += samples.Length;
            source.Start();
            source.Emit(240);
            Assert.That(observed, Is.Zero, "The APM should still hold this half frame.");
            var drain = source.StopAndDrainCaptureAsync();
            float deadline = Time.realtimeSinceStartup + 5;
            while (!drain.IsCompleted && Time.realtimeSinceStartup < deadline) yield return null;
            Assert.That(drain.IsCompleted, Is.True);
            Assert.That(drain.IsFaulted, Is.False, drain.Exception?.ToString());
            Assert.That(observed, Is.EqualTo(480), "The half frame must be padded and submitted, not discarded.");
        }
    }
}
