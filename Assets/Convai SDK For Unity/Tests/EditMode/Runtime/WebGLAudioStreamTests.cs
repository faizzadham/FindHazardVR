using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Convai.Infrastructure.Networking;
using Convai.Infrastructure.Networking.WebGL;
using LiveKit;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Runtime
{
    public class WebGLAudioStreamTests
    {
        private static readonly BindingFlags s_instanceFlags =
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;

        private Func<JSHandle, string, JSNative.JSDelegate, JSHandle, JSRef> _originalRegistrar;
        private Action<JSHandle, string, JSRef> _originalRemover;
        private Action<UnityEngine.Texture2D> _originalTextureDestroyer;
        private Action<int> _originalNativeTextureDestroyer;
        private Func<HTMLAudioElement, bool> _originalElementPlayingEvaluator;
        private Func<RemoteTrack, UnityEngine.AudioSource, IDisposable> _originalStreamCreator;

        [SetUp]
        public void SetUp()
        {
            _originalRegistrar = HTMLElement.EventListenerRegistrar;
            _originalRemover = HTMLElement.EventListenerRemover;
            _originalTextureDestroyer = HTMLVideoElement.TextureDestroyer;
            _originalNativeTextureDestroyer = HTMLVideoElement.NativeTextureDestroyer;
            _originalElementPlayingEvaluator = WebGLAudioStream.ElementPlayingEvaluator;
            _originalStreamCreator = WebGLAudioStreamFactory.StreamCreator;
        }

        [TearDown]
        public void TearDown()
        {
            HTMLElement.EventListenerRegistrar = _originalRegistrar;
            HTMLElement.EventListenerRemover = _originalRemover;
            HTMLVideoElement.TextureDestroyer = _originalTextureDestroyer;
            HTMLVideoElement.NativeTextureDestroyer = _originalNativeTextureDestroyer;
            WebGLAudioStream.ElementPlayingEvaluator = _originalElementPlayingEvaluator;
            WebGLAudioStreamFactory.StreamCreator = _originalStreamCreator;
            HTMLElement.ResetEventListenerHooks();
            HTMLVideoElement.ResetTestHooks();
            WebGLAudioStream.ResetTestHooks();
            WebGLAudioStreamFactory.ResetTestHooks();
        }

        [Test]
        public void ShouldInvokePlaybackStartedImmediately_WhenTrackingWasJustInitialized_ReturnsFalse()
        {
            Assert.IsFalse(WebGLAudioStream.ShouldInvokePlaybackStartedImmediately(false, 1));
        }

        [Test]
        public void ShouldInvokePlaybackStartedImmediately_WhenTrackingWasAlreadyInitializedAndPlaying_ReturnsTrue()
        {
            Assert.IsTrue(WebGLAudioStream.ShouldInvokePlaybackStartedImmediately(true, 1));
        }

        [Test]
        public void PlaybackStarted_WhenLateSubscriberAddedWhileAlreadyPlaying_InvokesOnce()
        {
            WebGLAudioStream stream = CreateUninitializedStream();
            SetField(stream, "_playbackTrackingInitialized", true);
            GetPlayingElementHandles(stream).Add((IntPtr)101);

            int invokeCount = 0;

            stream.PlaybackStarted += () => invokeCount++;

            Assert.AreEqual(1, invokeCount);
        }

        [Test]
        public void RegisterAndUnregisterElement_RemovesAllEventListeners()
        {
            List<string> addedEvents = new();
            List<(string eventName, JSRef listenerRef)> removedEvents = new();
            Dictionary<string, JSRef> createdRefs = new();

            HTMLElement.EventListenerRegistrar = (_, eventName, _, _) =>
            {
                JSRef listenerRef = CreateListenerRef();
                addedEvents.Add(eventName);
                createdRefs[eventName] = listenerRef;
                return listenerRef;
            };

            HTMLElement.EventListenerRemover = (_, eventName, listenerRef) =>
            {
                removedEvents.Add((eventName, listenerRef));
            };

            WebGLAudioStream.ElementPlayingEvaluator = _ => false;

            WebGLAudioStream stream = CreateUninitializedStream();
            HTMLAudioElement audioElement = CreateAudioElement((IntPtr)201);

            InvokePrivate(stream, "RegisterAttachedElement", audioElement);
            InvokePrivate(stream, "UnregisterElement", (IntPtr)201);

            CollectionAssert.AreEquivalent(new[] { "playing", "pause", "ended" }, addedEvents);
            Assert.AreEqual(3, removedEvents.Count);
            CollectionAssert.AreEquivalent(new[] { "playing", "pause", "ended" }, removedEvents.ConvertAll(x => x.eventName));
            Assert.AreSame(createdRefs["playing"], removedEvents.Find(x => x.eventName == "playing").listenerRef);
            Assert.AreSame(createdRefs["pause"], removedEvents.Find(x => x.eventName == "pause").listenerRef);
            Assert.AreSame(createdRefs["ended"], removedEvents.Find(x => x.eventName == "ended").listenerRef);
        }

        [Test]
        public void RepeatedRegisterAndUnregister_DoesNotAccumulateListenerRemovals()
        {
            int addCount = 0;
            int removeCount = 0;

            HTMLElement.EventListenerRegistrar = (_, _, _, _) =>
            {
                addCount++;
                return CreateListenerRef();
            };

            HTMLElement.EventListenerRemover = (_, _, _) => removeCount++;
            WebGLAudioStream.ElementPlayingEvaluator = _ => false;

            WebGLAudioStream stream = CreateUninitializedStream();
            HTMLAudioElement audioElement = CreateAudioElement((IntPtr)301);

            InvokePrivate(stream, "RegisterAttachedElement", audioElement);
            InvokePrivate(stream, "UnregisterElement", (IntPtr)301);
            InvokePrivate(stream, "RegisterAttachedElement", audioElement);
            InvokePrivate(stream, "UnregisterElement", (IntPtr)301);

            Assert.AreEqual(6, addCount);
            Assert.AreEqual(6, removeCount);
        }

        [Test]
        public void MediaTimingProbe_ExposesSnapshot_AndDisposesAfterLastElementDetaches()
        {
            ConfigureElementListenerStubs();
            int disposeCount = 0;
            WebGLAudioStream.TimingProbeCreator = _ => 42;
            WebGLAudioStream.TimingProbeReader = id => id == 42
                ? new AudioMediaTimelineSnapshot(
                    12.5d,
                    AudioTimelinePlaybackState.Playing,
                    12.25d,
                    signalGeneration: 3,
                    discontinuityGeneration: 1,
                    analyserAvailable: true)
                : null;
            WebGLAudioStream.TimingProbeDisposer = id =>
            {
                Assert.AreEqual(42, id);
                disposeCount++;
            };

            WebGLAudioStream stream = CreateUninitializedStream();
            HTMLAudioElement element = CreateAudioElement((IntPtr)501);
            InvokePrivate(stream, "RegisterAttachedElement", element);

            Assert.IsTrue(((IAudioMediaTimelineSnapshotSource)stream)
                .TryGetAudioMediaTimelineSnapshot(out AudioMediaTimelineSnapshot snapshot));
            Assert.AreEqual(12.5d, snapshot.LogicalPositionSeconds, 0.0001d);
            Assert.AreEqual(12.25d, snapshot.SignalStartPositionSeconds, 0.0001d);
            Assert.IsTrue(snapshot.AnalyserAvailable);

            InvokePrivate(stream, "UnregisterElement", (IntPtr)501);
            Assert.AreEqual(1, disposeCount);
            Assert.IsFalse(((IAudioMediaTimelineSnapshotSource)stream)
                .TryGetAudioMediaTimelineSnapshot(out _));
        }

        [Test]
        public void MediaTimingProbe_FirstTurnAnalyserWarmsWithinFrame_RefreshesColdSnapshot()
        {
            ConfigureElementListenerStubs();
            WebGLAudioStream.ElementPlayingEvaluator = _ => true;
            WebGLAudioStream.TimingProbeCreator = _ => 42;
            int readCount = 0;
            AudioMediaTimelineSnapshot browserSnapshot = new(
                10d,
                AudioTimelinePlaybackState.Playing,
                analyserAvailable: false);
            WebGLAudioStream.TimingProbeReader = _ =>
            {
                readCount++;
                return browserSnapshot;
            };

            WebGLAudioStream stream = CreateUninitializedStream();
            InvokePrivate(stream, "RegisterAttachedElement", CreateAudioElement((IntPtr)551));
            SetField(stream, "_playbackTrackingInitialized", true);

            AudioMediaTimelineSnapshot callbackSnapshot = default;
            stream.PlaybackStarted += () =>
                Assert.IsTrue(((IAudioMediaTimelineSnapshotSource)stream)
                    .TryGetAudioMediaTimelineSnapshot(out callbackSnapshot));
            Assert.IsFalse(callbackSnapshot.AnalyserAvailable);
            Assert.AreEqual(1, readCount);

            // AudioContext.resume() and the analyser graph can become ready later in the same
            // Unity frame as WebGL's immediate playback callback.
            browserSnapshot = new AudioMediaTimelineSnapshot(
                10.016d,
                AudioTimelinePlaybackState.Playing,
                signalStartPositionSeconds: 10d,
                signalGeneration: 1,
                analyserAvailable: true);

            Assert.IsTrue(((IAudioMediaTimelineSnapshotSource)stream)
                .TryGetAudioMediaTimelineSnapshot(out AudioMediaTimelineSnapshot refreshed));
            Assert.IsTrue(refreshed.AnalyserAvailable);
            Assert.AreEqual(1, refreshed.SignalGeneration);
            Assert.AreEqual(10d, refreshed.SignalStartPositionSeconds, 0.0001d);
            Assert.AreEqual(2, readCount,
                "a cold first-turn snapshot must not hide analyser readiness for the rest of the frame");
        }

        [Test]
        public void MediaTimingProbe_ElementReplacement_PreservesSingleProbeAndUpdatesCanonicalElement()
        {
            ConfigureElementListenerStubs();
            int createCount = 0;
            int disposeCount = 0;
            var updatedHandles = new List<IntPtr>();
            WebGLAudioStream.TimingProbeCreator = _ =>
            {
                createCount++;
                return 7;
            };
            WebGLAudioStream.TimingProbeElementUpdater = (id, element) =>
            {
                Assert.AreEqual(7, id);
                updatedHandles.Add(element.NativeHandle.DangerousGetHandle());
            };
            WebGLAudioStream.TimingProbeDisposer = _ => disposeCount++;

            WebGLAudioStream stream = CreateUninitializedStream();
            InvokePrivate(stream, "RegisterAttachedElement", CreateAudioElement((IntPtr)601));
            InvokePrivate(stream, "RegisterAttachedElement", CreateAudioElement((IntPtr)602));

            Assert.AreEqual(1, createCount);
            Assert.IsEmpty(updatedHandles, "a second attached element must not replace the audible clock source");

            InvokePrivate(stream, "UnregisterElement", (IntPtr)601);
            CollectionAssert.AreEqual(new[] { (IntPtr)602 }, updatedHandles);
            Assert.AreEqual(0, disposeCount);

            InvokePrivate(stream, "UnregisterElement", (IntPtr)602);
            Assert.AreEqual(1, disposeCount);
        }

        [Test]
        public void BargeInPlayback_ForwardsDuckCommitAndRestoreToBrowserGainEnvelope()
        {
            var commands = new List<(int probeId, float gain, float duration)>();
            WebGLAudioStream.TimingProbeGainSetter =
                (probeId, gain, duration) => commands.Add((probeId, gain, duration));

            WebGLAudioStream stream = CreateUninitializedStream();
            SetField(stream, "_timingProbeId", 17);
            GetPlayingElementHandles(stream).Add((IntPtr)17);
            var playback = (IBargeInPlaybackControl)stream;

            playback.Duck(0.25f, 0.05f);
            playback.CommitInterruption(0.12f);
            bool playbackAlreadyActive = playback.Restore(0.1f);

            Assert.That(commands.Count, Is.EqualTo(3));
            Assert.That(commands[0], Is.EqualTo((17, 0.25f, 0.05f)));
            Assert.That(commands[1], Is.EqualTo((17, 0f, 0.12f)));
            Assert.That(commands[2], Is.EqualTo((17, 1f, 0.1f)));
            Assert.IsTrue(
                playbackAlreadyActive,
                "WebGL keeps the HTML media element active while its gain is muted");
        }

        [Test]
        public void WebGLAudioStreamFactory_Create_AttachesStreamBeforeReturning()
        {
            int createCount = 0;
            UnityEngine.AudioSource capturedSource = null;
            IDisposable returnedStream = new TestDisposable();
            WebGLAudioStreamFactory.StreamCreator = (_, source) =>
            {
                createCount++;
                capturedSource = source;
                return returnedStream;
            };
            var track = (WebGLRemoteAudioTrack)FormatterServices.GetUninitializedObject(typeof(WebGLRemoteAudioTrack));
            SetField(track, "<UnderlyingTrack>k__BackingField", null);
            var sourceObject = new UnityEngine.GameObject("WebGLAudioStreamFactoryTestSource");
            UnityEngine.AudioSource source = sourceObject.AddComponent<UnityEngine.AudioSource>();

            try
            {
                IDisposable result = new WebGLAudioStreamFactory().Create(track, source);

                Assert.AreSame(returnedStream, result);
                Assert.AreEqual(1, createCount);
                Assert.AreSame(source, capturedSource);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(sourceObject);
            }
        }

        [Test]
        public void HTMLVideoElement_Dispose_RemovesResizeListener()
        {
            JSRef resizeListenerRef = CreateListenerRef();
            List<(string eventName, JSRef listenerRef)> removedEvents = new();

            HTMLElement.EventListenerRemover = (_, eventName, listenerRef) =>
            {
                removedEvents.Add((eventName, listenerRef));
            };
            HTMLVideoElement.TextureDestroyer = _ => { };
            HTMLVideoElement.NativeTextureDestroyer = _ => { };

            HTMLVideoElement element = CreateVideoElement((IntPtr)401);
            SetField(element, "_resizeListenerRef", resizeListenerRef);
            SetField(element, "m_TextureId", 0);

            InvokeDispose(element);

            Assert.AreEqual(1, removedEvents.Count);
            Assert.AreEqual("resize", removedEvents[0].eventName);
            Assert.AreSame(resizeListenerRef, removedEvents[0].listenerRef);
        }

        private static WebGLAudioStream CreateUninitializedStream()
        {
            var stream = (WebGLAudioStream)FormatterServices.GetUninitializedObject(typeof(WebGLAudioStream));
            SetField(stream, "_registeredElements",
                Activator.CreateInstance(GetFieldInfo(typeof(WebGLAudioStream), "_registeredElements").FieldType));
            SetField(stream, "_playingElementHandles", new HashSet<IntPtr>());
            SetField(stream, "_disposed", false);
            SetField(stream, "_playbackTrackingInitialized", false);
            return stream;
        }

        private static void ConfigureElementListenerStubs()
        {
            HTMLElement.EventListenerRegistrar = (_, _, _, _) => CreateListenerRef();
            HTMLElement.EventListenerRemover = (_, _, _) => { };
            WebGLAudioStream.ElementPlayingEvaluator = _ => false;
        }

        private static HTMLAudioElement CreateAudioElement(IntPtr handleValue)
        {
            var element = (HTMLAudioElement)FormatterServices.GetUninitializedObject(typeof(HTMLAudioElement));
            SetNativeHandle(element, handleValue);
            return element;
        }

        private static HTMLVideoElement CreateVideoElement(IntPtr handleValue)
        {
            var element = (HTMLVideoElement)FormatterServices.GetUninitializedObject(typeof(HTMLVideoElement));
            SetNativeHandle(element, handleValue);
            return element;
        }

        private static void SetNativeHandle(object target, IntPtr handleValue)
        {
            SetField(target, "NativeHandle", new JSHandle(handleValue, false), typeof(JSRef));
        }

        private static HashSet<IntPtr> GetPlayingElementHandles(WebGLAudioStream stream) =>
            (HashSet<IntPtr>)GetFieldInfo(typeof(WebGLAudioStream), "_playingElementHandles").GetValue(stream);

        private static JSRef CreateListenerRef() =>
            (JSRef)FormatterServices.GetUninitializedObject(typeof(JSRef));

        private sealed class TestDisposable : IDisposable
        {
            public void Dispose() { }
        }

        private static void InvokeDispose(HTMLVideoElement element)
        {
            MethodInfo disposeMethod = typeof(HTMLVideoElement).GetMethod("Dispose", s_instanceFlags);
            disposeMethod.Invoke(element, new object[] { true });
        }

        private static void InvokePrivate(object target, string methodName, params object[] args)
        {
            MethodInfo method = target.GetType().GetMethod(methodName, s_instanceFlags);
            Assert.NotNull(method, $"Could not find method '{methodName}'.");
            method.Invoke(target, args);
        }

        private static void SetField(object target, string fieldName, object value, Type declaringType = null)
        {
            GetFieldInfo(declaringType ?? target.GetType(), fieldName).SetValue(target, value);
        }

        private static FieldInfo GetFieldInfo(Type type, string fieldName)
        {
            FieldInfo field = type.GetField(fieldName, s_instanceFlags);
            Assert.NotNull(field, $"Could not find field '{fieldName}' on '{type.Name}'.");
            return field;
        }
    }
}
