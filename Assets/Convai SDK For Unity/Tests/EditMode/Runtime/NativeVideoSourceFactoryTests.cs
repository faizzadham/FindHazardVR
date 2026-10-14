using System;
using Convai.Infrastructure.Networking.Native;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Runtime
{
    [TestFixture]
    public class NativeVideoSourceFactoryTests
    {
        [Test]
        public void CreateFromCamera_ThrowsUnsupportedException()
        {
            var gameObject = new GameObject("NativeVideoSourceFactoryCameraTest");
            try
            {
                var camera = gameObject.AddComponent<Camera>();
                var factory = new NativeVideoSourceFactory();

                Assert.Throws<NotSupportedException>(() =>
                    factory.CreateFromCamera(camera, 640, 480, "camera"));
                Assert.That(camera.targetTexture, Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(gameObject);
            }
        }
    }
}
