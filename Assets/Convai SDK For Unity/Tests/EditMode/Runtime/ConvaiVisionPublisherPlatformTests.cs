using System.Reflection;
using Convai.Modules.Vision;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class ConvaiVisionPublisherPlatformTests
    {
        [TestCase(RuntimePlatform.WebGLPlayer, true)]
        [TestCase(RuntimePlatform.WindowsPlayer, false)]
        [TestCase(RuntimePlatform.OSXPlayer, false)]
        public void UsesWebGLCanvasPublishPath_RuntimePlatformGateIsExplicit(RuntimePlatform platform, bool expected)
        {
            MethodInfo method = typeof(ConvaiVisionPublisher).GetMethod(
                "UsesWebGLCanvasPublishPath",
                BindingFlags.NonPublic | BindingFlags.Static,
                null,
                new[] { typeof(RuntimePlatform) },
                null);

            Assert.IsNotNull(method, "Expected runtime-platform overload for UsesWebGLCanvasPublishPath.");

            object result = method.Invoke(null, new object[] { platform });
            Assert.That(result, Is.EqualTo(expected));
        }
    }
}
