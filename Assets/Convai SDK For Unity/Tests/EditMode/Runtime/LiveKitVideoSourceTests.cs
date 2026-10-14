using System;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text.RegularExpressions;
using LiveKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Convai.Tests.EditMode.Runtime
{
    public class LiveKitVideoSourceTests
    {
        private sealed class CountingTextureSource : TextureVideoSource
        {
            private CountingTextureSource() : base(null) { }
            public int Reads { get; private set; }
            public int Sends { get; private set; }
            protected override bool ReadBuffer() { Reads++; return false; }
            protected override bool SendFrame() { Sends++; return false; }
        }

        [Test]
        public void Update_DestroyedTexture_StopsReadingAndSending_ReportsOnce()
        {
            // Exercise the real capture loop without creating an FFI source.
            var source = (CountingTextureSource)FormatterServices.GetUninitializedObject(typeof(CountingTextureSource));
            GC.SuppressFinalize(source);
            var texture = new Texture2D(2, 2);
            typeof(TextureVideoSource).GetField("<Texture>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
                .SetValue(source, texture);
            source.TargetFrameRate = 0;
            source.Start();
            var capture = source.Update();
            try
            {
                Assert.That(capture.MoveNext(), Is.True);
                Assert.That(capture.MoveNext(), Is.True);
                Assert.That(source.Reads, Is.EqualTo(1));
                Assert.That(source.Sends, Is.EqualTo(1));

                UnityEngine.Object.DestroyImmediate(texture);
                LogAssert.Expect(LogType.Error, new Regex("CountingTextureSource: the source it captures from is no longer readable"));
                Assert.That(capture.MoveNext(), Is.True);
                Assert.That(capture.MoveNext(), Is.True);
                Assert.That(source.Reads, Is.EqualTo(1));
                Assert.That(source.Sends, Is.EqualTo(1));
                source.Stop();
                Assert.That(capture.MoveNext(), Is.False);
            }
            finally
            {
                source.Stop();
                if (texture != null) UnityEngine.Object.DestroyImmediate(texture);
            }
        }
    }
}
