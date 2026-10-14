using System;
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Serialization;
using System.Threading;
using Convai.Infrastructure.Networking.Native;
using LiveKit;
using LiveKit.Internal;
using LiveKit.Internal.FFI;
using LiveKit.Internal.FFI.Pools.Memory;
using LiveKit.Internal.Threading;
using LiveKit.Proto;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime
{
    public class LiveKitAudioProcessingTests
    {
        [Test]
        public void AudioBuffer_ReadDuration_ReturnsNullUntilEnoughTenMillisecondSamplesExist()
        {
            using var buffer = new AudioBuffer();

            buffer.Write(new float[200], 2, 48000);
            using AudioFrame insufficient = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);
            Assert.That(insufficient, Is.Null);

            buffer.Write(new float[960], 2, 48000);
            using AudioFrame frame = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);

            Assert.That(frame, Is.Not.Null);
            Assert.That(frame.NumChannels, Is.EqualTo(2));
            Assert.That(frame.SampleRate, Is.EqualTo(48000));
            Assert.That(frame.SamplesPerChannel, Is.EqualTo(480));
        }

        [Test]
        public void AudioBuffer_Clear_DropsBufferedSamples()
        {
            using var buffer = new AudioBuffer();

            buffer.Write(new float[960], 2, 48000);
            buffer.Clear();

            using AudioFrame frame = buffer.ReadDuration(AudioProcessingModule.FrameDurationMs);
            Assert.That(frame, Is.Null);
        }

        [Test]
        public void MicrophoneSource_OnAudioRead_StereoInput_ForwardsMonoFrame()
        {
            var source =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(source);

            float[] capturedSamples = null;
            int capturedChannels = 0;
            int capturedSampleRate = 0;
            source.AudioRead += (samples, channels, sampleRate) =>
            {
                capturedSamples = samples;
                capturedChannels = channels;
                capturedSampleRate = sampleRate;
            };

            float[] stereoSamples = { 1f, -1f, 0.5f, 0.5f };
            MethodInfo onAudioRead = typeof(MicrophoneSource).GetMethod(
                "OnAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(onAudioRead, Is.Not.Null);
            onAudioRead.Invoke(source, new object[] { stereoSamples, 2, 16000 });

            Assert.That(capturedChannels, Is.EqualTo(1));
            Assert.That(capturedSampleRate, Is.EqualTo(16000));
            Assert.That(capturedSamples, Is.EqualTo(new[] { 0f, 0.5f }));
        }

        [Test]
        public void RtcAudioSource_ProcessedAudioRead_ConvertsProcessedPcm16ToFloat()
        {
            var source =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(source);

            float[] capturedSamples = null;
            int capturedChannels = 0;
            int capturedSampleRate = 0;
            source.ProcessedAudioRead += (samples, channels, sampleRate) =>
            {
                capturedSamples = (float[])samples.Clone();
                capturedChannels = channels;
                capturedSampleRate = sampleRate;
            };

            using var frame = new AudioFrame(16000, 1, 3);
            Marshal.Copy(new short[] { short.MinValue, 0, short.MaxValue }, 0, frame.Data, 3);

            MethodInfo raiseProcessedAudioRead = typeof(RtcAudioSource).GetMethod(
                "RaiseProcessedAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic,
                null,
                new[] { typeof(AudioFrame) },
                null);

            Assert.That(raiseProcessedAudioRead, Is.Not.Null);
            raiseProcessedAudioRead.Invoke(source, new object[] { frame });

            Assert.That(capturedChannels, Is.EqualTo(1));
            Assert.That(capturedSampleRate, Is.EqualTo(16000));
            Assert.That(capturedSamples, Has.Length.EqualTo(3));
            Assert.That(capturedSamples[0], Is.EqualTo(-1f).Within(0.000001f));
            Assert.That(capturedSamples[1], Is.EqualTo(0f).Within(0.000001f));
            Assert.That(capturedSamples[2], Is.EqualTo(short.MaxValue / 32768f).Within(0.000001f));
        }

        [Test]
        public void NativeMicrophoneSource_PcmObserver_SubscribesToProcessedAudioOnlyWhileNeeded()
        {
            var rtcSource =
                (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(rtcSource);
            var nativeSource =
                (NativeMicrophoneSource)FormatterServices.GetUninitializedObject(typeof(NativeMicrophoneSource));
            SetPrivateField(nativeSource, "_underlyingSource", rtcSource);

            float[] capturedSamples = null;
            Action<float[], int, int> observer = (samples, _, _) => capturedSamples = samples;
            nativeSource.PcmFrame += observer;

            FieldInfo processedAudioRead = typeof(RtcAudioSource).GetField(
                "ProcessedAudioRead",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(processedAudioRead, Is.Not.Null);

            var callback = (Action<float[], int, int>)processedAudioRead.GetValue(rtcSource);
            Assert.That(callback, Is.Not.Null);

            float[] expected = { 0.25f, -0.25f };
            callback.Invoke(expected, 1, 16000);
            Assert.That(capturedSamples, Is.SameAs(expected));

            nativeSource.PcmFrame -= observer;
            Assert.That(processedAudioRead.GetValue(rtcSource), Is.Null);
        }

        [Test]
        public void RtcAudioSource_EstimateStreamDelayMs_ClampsToWebRtcLimit()
        {
            int delayMs = RtcAudioSource.EstimateStreamDelayMs(
                bufferLength: 1024,
                numBuffers: 4,
                sampleRate: 16000);

            Assert.That(delayMs, Is.EqualTo(500));
        }

        [Test]
        public void ApmReverseStream_IsActive_RequiresSuccessfulFrameAndResetsAcrossSessions()
        {
            var listenerObject = new GameObject("AEC reverse stream readiness test");
            var probe = listenerObject.AddComponent<AudioProbe>();
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_probe", probe);
            SetPrivateField(reverseStream, "_isActive", true);

            Assert.That(reverseStream.IsActive, Is.False,
                "Attaching an AudioProbe must not report AEC active before processing a reverse frame.");

            SetPrivateField(reverseStream, "_hasProcessedReverseFrame", true);
            Assert.That(reverseStream.IsActive, Is.False,
                "A processed frame must not skip the audible reverse-stream warm-up.");
            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs);
            Assert.That(reverseStream.IsActive, Is.True);

            reverseStream.Start();
            Assert.That(reverseStream.IsActive, Is.False,
                "Restarting an output session must require a fresh processed reverse frame.");
            Assert.That(GetPrivateField<int>(reverseStream, "_warmMs"), Is.Zero,
                "Restarting an output session must restart the warm-up.");

            SetPrivateField(reverseStream, "_hasProcessedReverseFrame", true);
            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs);
            reverseStream.Stop();
            Assert.That(reverseStream.IsActive, Is.False);

            UnityEngine.Object.DestroyImmediate(listenerObject);
        }

        [Test]
        public void ApmReverseStream_IsActive_RequiresWarmupAfterProcessedFrame()
        {
            var listenerObject = new GameObject("AEC reverse stream warm-up test");
            var probe = listenerObject.AddComponent<AudioProbe>();
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_probe", probe);
            SetPrivateField(reverseStream, "_isActive", true);
            SetPrivateField(reverseStream, "_hasProcessedReverseFrame", true);

            Assert.That(reverseStream.IsActive, Is.False,
                "A processed reverse frame alone proves plumbing, not a converged canceller.");

            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs - 1);
            Assert.That(reverseStream.IsActive, Is.False);

            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs);
            Assert.That(reverseStream.IsActive, Is.True);

            reverseStream.ResetWarmup();
            Assert.That(reverseStream.IsActive, Is.False,
                "A capture restart changes the echo delay, so the canceller must warm up again.");

            UnityEngine.Object.DestroyImmediate(listenerObject);
        }

        [Test]
        public void ApmReverseStream_WasAudibleWithin_TracksLastAudibleRenderAndResetsWithWarmup()
        {
            using var reverseStream = new ApmReverseStream(null);
            Assert.That(reverseStream.WasAudibleWithin(RtcAudioSource.EchoTailHoldMs), Is.False,
                "Nothing audible has been rendered yet.");

            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(0));
            Assert.That(reverseStream.WasAudibleWithin(RtcAudioSource.EchoTailHoldMs), Is.True);

            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(RtcAudioSource.EchoTailHoldMs + 100));
            Assert.That(reverseStream.WasAudibleWithin(RtcAudioSource.EchoTailHoldMs), Is.False);

            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(0));
            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs);
            Assert.That(reverseStream.IsWarm, Is.True);

            reverseStream.ResetWarmup();
            Assert.That(reverseStream.IsWarm, Is.False);
            Assert.That(reverseStream.WasAudibleWithin(RtcAudioSource.EchoTailHoldMs), Is.False,
                "A capture restart forgets the last audible render with the warm-up.");
        }

        [Test]
        public void RtcAudioSource_UplinkHold_NotWarmAndFarEndRecentlyAudible_SendsSilence()
        {
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(0));
            MicrophoneSource source = CreateUplinkSource(reverseStream);
            using AudioFrame frame = CreatePcmFrame(1000, -2000, 3000);

            Assert.That(SendUplinkFrame(source, frame), Is.EqualTo(new short[3]));
            Assert.That(source.UplinkHeldFrameCount, Is.EqualTo(1));
            Assert.That(ReadPcm(frame), Is.EqualTo(new short[] { 1000, -2000, 3000 }),
                "The processed frame the local detector reads must keep the real PCM.");
        }

        [Test]
        public void RtcAudioSource_UplinkHold_WhenWarm_PassesThroughAndEndsEpisode()
        {
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(0));
            MicrophoneSource source = CreateUplinkSource(reverseStream);
            using AudioFrame frame = CreatePcmFrame(1000, -2000, 3000);
            SendUplinkFrame(source, frame);

            SetPrivateField(reverseStream, "_warmMs", ApmReverseStream.WarmupMs);

            Assert.That(SendUplinkFrame(source, frame), Is.EqualTo(new short[] { 1000, -2000, 3000 }));
            Assert.That(source.UplinkHeldFrameCount, Is.EqualTo(1));
            Assert.That(GetRtcAudioSourceField<int>(source, "_uplinkHoldEpisodeFrames"), Is.Zero);
        }

        [Test]
        public void RtcAudioSource_UplinkHold_WhenFarEndSilentPastEchoTail_PassesThrough()
        {
            using var reverseStream = new ApmReverseStream(null);
            SetPrivateField(reverseStream, "_lastAudibleTimestamp", TimestampMsAgo(RtcAudioSource.EchoTailHoldMs + 100));
            MicrophoneSource source = CreateUplinkSource(reverseStream);
            using AudioFrame frame = CreatePcmFrame(1000, -2000, 3000);

            Assert.That(SendUplinkFrame(source, frame), Is.EqualTo(new short[] { 1000, -2000, 3000 }));
            Assert.That(source.UplinkHeldFrameCount, Is.Zero);
        }

        [Test]
        public void RtcAudioSource_UplinkHold_WhenAecDisabled_PassesThrough()
        {
            MicrophoneSource source = CreateUplinkSource(null);
            using AudioFrame frame = CreatePcmFrame(1000, -2000, 3000);

            Assert.That(SendUplinkFrame(source, frame), Is.EqualTo(new short[] { 1000, -2000, 3000 }));
            Assert.That(source.UplinkHeldFrameCount, Is.Zero);
            Assert.That(RtcAudioSource.ShouldHoldUplink(aecEnabled: false, warm: false, farEndRecentlyAudible: true),
                Is.False);
        }

        private static MicrophoneSource CreateUplinkSource(ApmReverseStream reverseStream)
        {
            var source = (MicrophoneSource)FormatterServices.GetUninitializedObject(typeof(MicrophoneSource));
            GC.SuppressFinalize(source);
            FieldInfo field = typeof(RtcAudioSource).GetField("_apmReverseStream", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(source, reverseStream);
            return source;
        }

        private static AudioFrame CreatePcmFrame(params short[] samples)
        {
            var frame = new AudioFrame(16000, 1, (uint)samples.Length);
            Marshal.Copy(samples, 0, frame.Data, samples.Length);
            return frame;
        }

        private static short[] ReadPcm(AudioFrame frame)
        {
            var samples = new short[frame.SamplesPerChannel * frame.NumChannels];
            Marshal.Copy(frame.Data, samples, 0, samples.Length);
            return samples;
        }

        private static short[] SendUplinkFrame(RtcAudioSource source, AudioFrame frame)
        {
            MethodInfo method = typeof(RtcAudioSource).GetMethod(
                "CreateUplinkFrameData",
                BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(method, Is.Not.Null);
            var data = (NativeArray<short>)method.Invoke(source, new object[] { frame });
            try { return data.ToArray(); }
            finally { data.Dispose(); }
        }

        private static long TimestampMsAgo(int ms) =>
            System.Diagnostics.Stopwatch.GetTimestamp() - ms * System.Diagnostics.Stopwatch.Frequency / 1000;

        private static T GetRtcAudioSourceField<T>(RtcAudioSource source, string fieldName)
        {
            FieldInfo field = typeof(RtcAudioSource).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(source);
        }

        [Test]
        public void FfiRequestExtensions_Inject_ApmRequests_SetsExpectedOneOfField()
        {
            var newApmRequest = new FfiRequest();
            newApmRequest.Inject(new NewApmRequest());
            Assert.That(newApmRequest.NewApm, Is.Not.Null);

            var processRequest = new FfiRequest();
            processRequest.Inject(new ApmProcessStreamRequest());
            Assert.That(processRequest.ApmProcessStream, Is.Not.Null);

            var reverseRequest = new FfiRequest();
            reverseRequest.Inject(new ApmProcessReverseStreamRequest());
            Assert.That(reverseRequest.ApmProcessReverseStream, Is.Not.Null);

            var delayRequest = new FfiRequest();
            delayRequest.Inject(new ApmSetStreamDelayRequest());
            Assert.That(delayRequest.ApmSetStreamDelay, Is.Not.Null);
        }

        [Test]
        public void FfiRequestExtensions_EnsureClean_ThrowsWhenApmResponseFieldsArePresent()
        {
            var response = new FfiResponse { ApmSetStreamDelay = new ApmSetStreamDelayResponse() };
            Assert.Throws<InvalidOperationException>(() => response.EnsureClean());
        }

        [Test]
        public void AudioStream_Dispose_WhenAudioSourceAlreadyDestroyed_DoesNotThrow()
        {
            var go = new GameObject("Destroyed AudioStream source");
            AudioSource source = go.AddComponent<AudioSource>();
            UnityEngine.Object.DestroyImmediate(go);

            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            SetPrivateField(stream, "_audioSource", source);
            SetPrivateField(stream, "_lock", new object());

            Assert.That(source == null, Is.True);
            Assert.DoesNotThrow(stream.Dispose);
        }

        // Exercise the real disposal and callback entry points without making a native
        // release wait forever. The handle models a backend waiting for its callback.
        [Test]
        public void AudioStream_Dispose_AllowsNativeReleaseToDrainLateCallback()
        {
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            SetPrivateField(stream, "_lock", new object());
            SetPrivateField(stream, "_pendingFfiRequest", true);
            var read = (Action<float[], int, int>)Delegate.CreateDelegate(
                typeof(Action<float[], int, int>), stream,
                typeof(AudioStream).GetMethod("OnAudioRead", BindingFlags.Instance | BindingFlags.NonPublic));
            float[] output = { 1f, 1f };
            Exception callbackError = null;
            var callback = new Thread(() =>
            {
                try { read(output, 1, 48000); }
                catch (Exception error) { callbackError = error; }
            }) { IsBackground = true };
            bool completedDuringRelease = false;
            using var handle = new CallbackReleaseHandle(() =>
            {
                callback.Start();
                completedDuringRelease = callback.Join(TimeSpan.FromSeconds(2));
            });
            SetPrivateField(stream, "_handle", handle);
            int stopped = 0;
            SetPrivateField(stream, "_isStreamingActive", true);
            stream.PlaybackStopped += () => stopped++;

            stream.Dispose();
            bool drained = callback.Join(TimeSpan.FromSeconds(2));
            stream.Dispose();

            Assert.That(drained, Is.True, "The bounded callback must not outlive the test.");
            Assert.That(callbackError, Is.Null);
            Assert.That(completedDuringRelease, Is.True,
                "Native release must not hold the lock needed by an audio callback.");
            Assert.That(output, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(stream.PlaybackSnapshot.State, Is.EqualTo(AudioStreamPlaybackState.Disposed));
            Assert.That(stopped, Is.EqualTo(1));
            Assert.That(handle.ReleaseCount, Is.EqualTo(1));
        }

        [Test]
        public void AudioStream_Dispose_WhenStopSubscriberThrows_StillReleasesResources()
        {
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            SetPrivateField(stream, "_lock", new object());
            SetPrivateField(stream, "_isStreamingActive", true);
            using var handle = new CallbackReleaseHandle(() => { });
            SetPrivateField(stream, "_handle", handle);
            stream.PlaybackStopped += () => throw new InvalidOperationException("Stop observer failed");

            Assert.Throws<InvalidOperationException>(stream.Dispose);
            Assert.That(handle.ReleaseCount, Is.EqualTo(1));
            Assert.That(stream.PlaybackSnapshot.State, Is.EqualTo(AudioStreamPlaybackState.Disposed));
            Assert.DoesNotThrow(stream.Dispose);
        }

        [Test]
        public void AudioStream_Dispose_WinsAgainstCallbackAlreadyWaitingForLock()
        {
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            var gate = new object();
            SetPrivateField(stream, "_lock", gate);
            var read = (Action<float[], int, int>)Delegate.CreateDelegate(typeof(Action<float[], int, int>),
                stream, typeof(AudioStream).GetMethod("OnAudioRead", BindingFlags.Instance | BindingFlags.NonPublic));
            float[] output = { 1f, 1f };
            RunWaitingCallback(gate, () => read(output, 1, 48000), stream.Dispose);
            Assert.That(output, Is.EqualTo(new[] { 0f, 0f }));
            Assert.That(typeof(AudioStream).GetField("_pendingFfiRequest", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(stream), Is.False, "A waiting callback must not enqueue replacement work after disposal.");
        }

        [Test]
        public void AudioStream_FrameWaitingForLock_CannotWriteIntoReplacementStream()
        {
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            var gate = new object();
            using var first = new CallbackReleaseHandle(() => { });
            using var replacement = new CallbackReleaseHandle(() => { });
            using var buffer = new RingBuffer(32);
            SetPrivateField(stream, "_lock", gate);
            SetPrivateField(stream, "_handle", first);
            SetPrivateField(stream, "_buffer", buffer);
            SetPrivateField(stream, "_numChannels", 1u);
            var receive = (Action<AudioStreamEvent>)Delegate.CreateDelegate(typeof(Action<AudioStreamEvent>),
                stream, typeof(AudioStream).GetMethod("OnAudioStreamEvent", BindingFlags.Instance | BindingFlags.NonPublic));
            GCHandle samples = GCHandle.Alloc(new short[] { 123, 456 }, GCHandleType.Pinned);
            try
            {
                var frame = new AudioStreamEvent
                {
                    StreamHandle = 1,
                    FrameReceived = new AudioFrameReceived
                    {
                        Frame = new OwnedAudioFrameBuffer
                        {
                            Handle = new FfiOwnedHandle { Id = 0 },
                            Info = new AudioFrameBufferInfo
                            {
                                DataPtr = (ulong)samples.AddrOfPinnedObject().ToInt64(),
                                SampleRate = 48000, NumChannels = 1, SamplesPerChannel = 2
                            }
                        }
                    }
                };
                RunWaitingCallback(gate, () => receive(frame), () => SetPrivateField(stream, "_handle", replacement));
                Assert.That(buffer.AvailableRead(), Is.Zero, "The old frame must not enter replacement buffering.");
            }
            finally { samples.Free(); }
        }

        [Test]
        public void AudioStream_ReplacementCompletedAfterDisposal_ReleasesAbandonedHandleOutsideLock()
        {
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            var gate = new object();
            SetPrivateField(stream, "_lock", gate);
            stream.Dispose();
            bool releaseHeldLock = true;
            using var replacement = new CallbackReleaseHandle(() => releaseHeldLock = Monitor.IsEntered(gate));
            typeof(AudioStream).GetMethod("AdoptFfiStream", BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(stream, new object[] { replacement, 1u, 48000u });
            Assert.That(replacement.ReleaseCount, Is.EqualTo(1));
            Assert.That(releaseHeldLock, Is.False);
            Assert.That(typeof(AudioStream).GetField("_handle", BindingFlags.Instance | BindingFlags.NonPublic)
                .GetValue(stream), Is.Null);
        }

        private static void RunWaitingCallback(object gate, Action callback, Action whileWaiting)
        {
            Exception error = null;
            var worker = new Thread(() =>
            {
                try { callback(); }
                catch (Exception exception) { error = exception; }
            }) { IsBackground = true };
            bool waiting;
            lock (gate)
            {
                worker.Start();
                // This worker has no other waits: WaitSleepJoin establishes it passed the
                // fast check and is blocked on this monitor, rather than relying on a delay.
                waiting = SpinWait.SpinUntil(
                    () => (worker.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(2));
                if (waiting) whileWaiting();
            }
            Assert.That(worker.Join(TimeSpan.FromSeconds(2)), Is.True, "The callback must drain.");
            Assert.That(waiting, Is.True, "The callback must reach the held monitor before mutation.");
            Assert.That(error, Is.Null);
        }

        private sealed class CallbackReleaseHandle : FfiHandle
        {
            private readonly Action _release;
            public int ReleaseCount { get; private set; }

            public CallbackReleaseHandle(Action release) : base(new IntPtr(1)) => _release = release;

            protected override bool ReleaseHandle()
            {
                ReleaseCount++;
                _release();
                return true;
            }
        }

        [Test]
        public void RingBuffer_WriteExact_WhenCapacityIsInsufficient_DoesNotWritePartialChunk()
        {
            using var buffer = new RingBuffer(8);

            Assert.That(buffer.WriteExact(new byte[] { 1, 2, 3, 4, 5, 6 }), Is.True);
            Assert.That(buffer.WriteExact(new byte[] { 7, 8, 9, 10 }), Is.False);
            Assert.That(buffer.AvailableRead(), Is.EqualTo(6));

            var actual = new byte[8];
            int read = buffer.Read(actual);
            Assert.That(read, Is.EqualTo(6));
            Assert.That(actual[..read], Is.EqualTo(new byte[] { 1, 2, 3, 4, 5, 6 }));
        }

        [Test]
        public void AudioStream_PlaybackSnapshot_ExposesAbsoluteSourceClockAndHealthCounters()
        {
            PropertyInfo property = typeof(AudioStream).GetProperty(nameof(AudioStream.PlaybackSnapshot));

            Assert.That(property, Is.Not.Null);
            Assert.That(property.PropertyType, Is.EqualTo(typeof(AudioStreamPlaybackSnapshot)));
            Assert.That(Enum.GetNames(typeof(AudioStreamPlaybackState)),
                Is.EquivalentTo(new[] { "Idle", "Playing", "Underrun", "Disposed" }));
        }

        [Test]
        public void AudioStream_GetPcm16Magnitude_WhenSampleIsMinimumValue_DoesNotThrow()
        {
            Assert.That(AudioStream.GetPcm16Magnitude(short.MinValue), Is.EqualTo(32768));
            Assert.That(AudioStream.GetPcm16Magnitude(short.MaxValue), Is.EqualTo(32767));
            Assert.That(AudioStream.GetPcm16Magnitude(0), Is.Zero);
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_FindsExactInterleavedStereoFrame()
        {
            short[] samples =
            {
                0, 0,
                100, -600,
                900, 0
            };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 2, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(1));
            Assert.That(peak, Is.EqualTo(900));
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_MonoSilenceDoesNotStartPlayback()
        {
            short[] samples = { 0, 100, -499, 499 };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 1, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(-1));
            Assert.That(peak, Is.EqualTo(499));
        }

        [Test]
        public void AudioStream_GetPeakAndFirstSignalFrame_ThresholdCrossingIsInclusive()
        {
            short[] samples = { 0, -500, 1000 };

            int peak = AudioStream.GetPeakAndFirstSignalFrame(
                samples, samples.Length, 1, 500, out int firstSignalFrame);

            Assert.That(firstSignalFrame, Is.EqualTo(1));
            Assert.That(peak, Is.EqualTo(1000));
        }

        [Test]
        public void AudioStreamPlaybackSnapshot_EstimateRenderedSourceFrame_InterpolatesWithinCallback()
        {
            var snapshot = new AudioStreamPlaybackSnapshot(
                1480, 48000, 1, 1, 0, AudioStreamPlaybackState.Playing,
                1480, 480, 0, 0, 0,
                callbackSourceFrameStart: 1000,
                callbackSourceFrameCount: 480,
                callbackDspTime: 10d);

            Assert.That(snapshot.EstimateRenderedSourceFrame(10d), Is.EqualTo(1000));
            Assert.That(snapshot.EstimateRenderedSourceFrame(10.005d), Is.EqualTo(1240));
            Assert.That(snapshot.EstimateRenderedSourceFrame(11d), Is.EqualTo(1480));
        }

        [Test]
        public void AudioStreamPlaybackSnapshot_EstimateRenderedSourceFrame_FreezesDuringUnderrun()
        {
            var snapshot = new AudioStreamPlaybackSnapshot(
                1480, 48000, 1, 1, 0, AudioStreamPlaybackState.Underrun,
                1480, 480, 0, 0, 1,
                callbackSourceFrameStart: 1000,
                callbackSourceFrameCount: 480,
                callbackDspTime: 10d);

            Assert.That(snapshot.EstimateRenderedSourceFrame(11d), Is.EqualTo(1480));
        }

        [Test]
        public void AudioStream_RampFromFrame_PreservesPerChannelContinuity()
        {
            var samples = new short[] { 0, 0, 1000, -1000, 2000, -2000, 3000, -3000 };
            var sourceFrame = new short[] { 4000, -4000 };

            AudioStream.RampFromFrame(samples, samples.Length, 2, sourceFrame, 4);

            Assert.That(samples[0], Is.EqualTo(3000));
            Assert.That(samples[1], Is.EqualTo(-3000));
            Assert.That(samples[^2], Is.EqualTo(3000));
            Assert.That(samples[^1], Is.EqualTo(-3000));
        }

        [Test]
        public void AudioStream_EvaluatePlaybackGain_FadesQuicklyAndReachesExactTarget()
        {
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 0, 100), Is.EqualTo(1f));
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 50, 100), Is.EqualTo(0.25f));
            Assert.That(AudioStream.EvaluatePlaybackGain(1f, 0f, 100, 100), Is.Zero);
            Assert.That(AudioStream.EvaluatePlaybackGain(0f, 1f, 100, 100), Is.EqualTo(1f));
        }

        private static void SetPrivateField(object target, string fieldName, object value)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            field.SetValue(target, value);
        }

        private static T GetPrivateField<T>(object target, string fieldName)
        {
            FieldInfo field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null);
            return (T)field.GetValue(target);
        }
    }

    public class LiveKitAudioDisposeFailureTests
    {
        [UnityTest]
        public IEnumerator Dispose_WhenFfiLookupThrows_StopsSourceAndReleasesEveryOwner()
        {
            yield return new EnterPlayMode();
            var host = new GameObject("Audio disposal regression");
            var clip = AudioClip.Create("Silent disposal fixture", 48000, 1, 48000, false);
            var source = host.AddComponent<AudioSource>();
            source.clip = clip;
            source.loop = true;
            source.Play();
            var probe = host.AddComponent<AudioProbe>();
            probe.ClearAfterInvocation();
            var stream = (AudioStream)FormatterServices.GetUninitializedObject(typeof(AudioStream));
            GC.SuppressFinalize(stream);
            var pool = new CountingMemoryPool();
            var buffer = (RingBuffer)FormatterServices.GetUninitializedObject(typeof(RingBuffer));
            SetField(buffer, "_buffer", pool.Memory(32));
            using var handle = new CountingHandle();
            SetField(stream, "_lock", new object());
            SetField(stream, "_audioSource", source);
            SetField(stream, "_probe", probe);
            SetField(stream, "_buffer", buffer);
            SetField(stream, "_handle", handle);
            // Retired before the next audio callback: the probe itself is still a live muting owner.
            SetField(stream, "_pendingFfiRequest", true);
            var read = (AudioProbe.OnAudioDelegate)Delegate.CreateDelegate(
                typeof(AudioProbe.OnAudioDelegate), stream,
                typeof(AudioStream).GetMethod("OnAudioRead", BindingFlags.Instance | BindingFlags.NonPublic));
            probe.AudioRead += read;
            MonoBehaviourContext.OnApplicationPauseEvent += stream.OnApplicationPause;
            var configurationChanged = (AudioSettings.AudioConfigurationChangeHandler)Delegate.CreateDelegate(
                typeof(AudioSettings.AudioConfigurationChangeHandler), stream,
                typeof(AudioStream).GetMethod("OnAudioConfigurationChanged", BindingFlags.Instance | BindingFlags.NonPublic));
            AudioSettings.OnAudioConfigurationChanged += configurationChanged;
            FieldInfo singleton = typeof(FfiClient).GetField("instance", BindingFlags.Static | BindingFlags.NonPublic);
            object original = singleton.GetValue(null);
            try
            {
                Assert.That(source.isPlaying, Is.True, "The source must be playing before disposal.");
                var failure = new InvalidOperationException("Synthetic FFI lookup failure");
                try
                {
                    singleton.SetValue(null, new Lazy<FfiClient>(() => throw failure));
                    Assert.That(Assert.Throws<InvalidOperationException>(stream.Dispose), Is.SameAs(failure));
                }
                finally
                {
                    singleton.SetValue(null, original);
                }

                Assert.That(source.isPlaying, Is.False);
                Assert.That(GetField(stream, "_probe"), Is.Null);
                Assert.That(GetField(probe, "AudioRead"), Is.Null, "Detach before deferred Unity destruction.");
                Assert.That(MonoBehaviourContext.OnApplicationPauseEvent?.GetInvocationList() ?? Array.Empty<Delegate>(),
                    Has.None.Matches<Delegate>(callback => ReferenceEquals(callback.Target, stream)));
                Assert.That(GetField(stream, "_buffer"), Is.Null);
                Assert.That(stream.PlaybackSnapshot.State, Is.EqualTo(AudioStreamPlaybackState.Disposed));
                Assert.DoesNotThrow(stream.Dispose);
                Assert.That(pool.ReleaseCount, Is.EqualTo(1));
                Assert.That(handle.ReleaseCount, Is.EqualTo(1));
                yield return null;
                Assert.That(probe == null, Is.True, "The muting probe must be destroyed on the next frame.");
            }
            finally
            {
                singleton.SetValue(null, original);
                MonoBehaviourContext.OnApplicationPauseEvent -= stream.OnApplicationPause;
                AudioSettings.OnAudioConfigurationChanged -= configurationChanged;
                UnityEngine.Object.DestroyImmediate(host);
                UnityEngine.Object.DestroyImmediate(clip);
            }
        }

        [UnityTearDown]
        public IEnumerator LeavePlayMode()
        {
            if (UnityEngine.Application.isPlaying) yield return new ExitPlayMode();
        }

        private static object GetField(object target, string name) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target);

        private static void SetField(object target, string name, object value) =>
            target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).SetValue(target, value);

        private sealed class CountingHandle : FfiHandle
        {
            internal int ReleaseCount;
            internal CountingHandle() : base(new IntPtr(1)) { }
            protected override bool ReleaseHandle() { ReleaseCount++; return true; }
        }

        private sealed class CountingMemoryPool : IMemoryPool
        {
            internal int ReleaseCount;
            public MemoryWrap Memory(int size) => new(new byte[size], size, this);
            public void Release(byte[] buffer) => ReleaseCount++;
        }
    }

}
