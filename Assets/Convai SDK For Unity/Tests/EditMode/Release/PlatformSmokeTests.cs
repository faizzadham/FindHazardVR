using System.IO;
using Convai.Infrastructure.Networking.Native;
using Convai.Infrastructure.Networking.Transport;
using Convai.Infrastructure.Networking.WebGL;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Release
{
    [Category("Release")]
    public sealed class PlatformSmokeTests
    {
        [Test]
        public void WebGLTransportAccessor_ExposesWebGLCapabilities()
        {
            using var accessor = new WebGLRealtimeTransportAccessor(createTransport: static () => null);

            Assert.AreEqual(TransportPlatform.WebGL, accessor.Capabilities.Platform);
            Assert.IsTrue(accessor.Capabilities.SupportsVideo);
            Assert.IsTrue(accessor.Capabilities.RequiresUserGestureForAudio);
            Assert.IsFalse(accessor.Capabilities.SupportsUnityAudioSource);
        }

        [Test]
        public void NativeTransportProvider_ExposesNativeCapabilities()
        {
            var provider = NativeTransportProvider.Instance;
            Assert.AreEqual(TransportPlatform.Desktop, provider.Capabilities.Platform);
            Assert.IsTrue(provider.Capabilities.SupportsVideo);
            Assert.IsFalse(provider.Capabilities.RequiresUserGestureForAudio);
        }

        [Test]
        public void WebGLTransportProvider_ExposesWebGLCapabilities()
        {
            var provider = WebGLTransportProvider.Instance;
            Assert.AreEqual(TransportPlatform.WebGL, provider.Capabilities.Platform);
            Assert.IsTrue(provider.Capabilities.SupportsVideo);
            Assert.IsTrue(provider.Capabilities.RequiresUserGestureForAudio);
            Assert.IsFalse(provider.Capabilities.SupportsUnityAudioSource);
        }

        [Test]
        public void WebGLBridge_DoesNotReferenceBuildTimeLibraryObjectAtRuntime()
        {
            const string bridgePath =
                "Packages/com.convai.convai-sdk-for-unity/Plugins/client-sdk-unity-web/Runtime/Plugins/livekit-bridge.jslib";
            string bridgeSource = File.ReadAllText(bridgePath);

            Assert.That(
                bridgeSource,
                Does.Not.Contain("NativeLib."),
                "Emscripten discards the NativeLib object after merging the .jslib; runtime code must call emitted functions.");
        }
    }
}
