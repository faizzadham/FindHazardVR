using System;
using System.Runtime.Serialization;
using Convai.Infrastructure.Networking.WebGL;
using LiveKit;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Infrastructure
{
    [TestFixture]
    public class WebGLCanvasVideoSourceTests
    {
        private sealed class TestCanvasCaptureBridge : IWebGLCanvasCaptureBridge
        {
            public MediaStreamTrack TrackToReturn { get; set; }
            public int CaptureCallCount { get; private set; }
            public int StopCallCount { get; private set; }
            public MediaStreamTrack LastStoppedTrack { get; private set; }

            public MediaStreamTrack CaptureVideoTrack(int targetFrameRate)
            {
                CaptureCallCount++;
                return TrackToReturn;
            }

            public void StopVideoTrack(MediaStreamTrack track)
            {
                StopCallCount++;
                LastStoppedTrack = track;
            }
        }

        [Test]
        public void StartCapture_WhenBridgeReturnsTrack_BeginsCapturing()
        {
            TestCanvasCaptureBridge bridge = new() { TrackToReturn = CreateTrack() };
            WebGLCanvasVideoSource source = new(bridge, 15, "webgl-scene");

            source.StartCapture();

            Assert.IsTrue(source.IsCapturing);
            Assert.AreEqual(1, bridge.CaptureCallCount);
            Assert.That(source.MediaStreamTrack, Is.Not.Null);
            source.Dispose();
        }

        [Test]
        public void StopCapture_WhenCapturing_StopsBridgeTrack()
        {
            MediaStreamTrack track = CreateTrack();
            TestCanvasCaptureBridge bridge = new() { TrackToReturn = track };
            WebGLCanvasVideoSource source = new(bridge, 15, "webgl-scene");
            source.StartCapture();

            source.StopCapture();

            Assert.IsFalse(source.IsCapturing);
            Assert.AreEqual(1, bridge.StopCallCount);
            Assert.AreSame(track, bridge.LastStoppedTrack);
            Assert.That(source.MediaStreamTrack, Is.Null);
            source.Dispose();
        }

        [Test]
        public void StartCapture_WhenBridgeReturnsNull_ThrowsClearError()
        {
            TestCanvasCaptureBridge bridge = new();
            WebGLCanvasVideoSource source = new(bridge, 15, "webgl-scene");

            var ex = Assert.Throws<InvalidOperationException>(() => source.StartCapture());

            Assert.That(ex.Message, Does.Contain("Unity WebGL canvas"));
            source.Dispose();
        }

        [Test]
        public void Dispose_WhenCapturing_StopsTrackOnce()
        {
            MediaStreamTrack track = CreateTrack();
            TestCanvasCaptureBridge bridge = new() { TrackToReturn = track };
            WebGLCanvasVideoSource source = new(bridge, 15, "webgl-scene");
            source.StartCapture();

            source.Dispose();
            source.Dispose();

            Assert.AreEqual(1, bridge.StopCallCount);
            Assert.AreSame(track, bridge.LastStoppedTrack);
        }

#pragma warning disable SYSLIB0050
        private static MediaStreamTrack CreateTrack() =>
            (MediaStreamTrack)FormatterServices.GetUninitializedObject(typeof(MediaStreamTrack));
#pragma warning restore SYSLIB0050
    }
}
