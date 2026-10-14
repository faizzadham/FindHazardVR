using System;
using System.Threading;
using System.Threading.Tasks;
using Convai.Editor.SceneTagging;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerThumbnailCacheTests
    {
        private static byte[] SolidPng(int size, Color color)
        {
            var texture = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++) pixels[i] = color;
            texture.SetPixels(pixels); texture.Apply();
            byte[] png = texture.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(texture);
            return png;
        }

        [Test]
        public void Initials_take_first_two_words()
        {
            Assert.AreEqual("AS", SceneTaggerThumbnailCache.Initials("Assembly Structure Advisor"));
            Assert.AreEqual("E", SceneTaggerThumbnailCache.Initials("elara"));
            Assert.AreEqual("?", SceneTaggerThumbnailCache.Initials("   "));
            Assert.AreEqual("?", SceneTaggerThumbnailCache.Initials(null));
        }

        [Test]
        public void Downscale_returns_requested_size()
        {
            Texture2D small = SceneTaggerThumbnailCache.Downscale(SolidPng(256, Color.red), 64);
            try
            {
                Assert.AreEqual(64, small.width);
                Assert.AreEqual(64, small.height);
            }
            finally { UnityEngine.Object.DestroyImmediate(small); }
        }

        [Test]
        public void Local_caches_by_key_and_evicts_oldest_at_capacity()
        {
            using var cache = new SceneTaggerThumbnailCache((_, _) => Task.FromResult<byte[]>(null), capacity: 2);
            byte[] png = SolidPng(8, Color.green);
            Texture2D a = cache.Local("a", png, 4);
            Assert.AreSame(a, cache.Local("a", png, 4));
            cache.Local("b", png, 4);
            cache.Local("c", png, 4);
            Assert.AreEqual(2, cache.Count);
            Assert.IsTrue(a == null, "oldest entry should be destroyed");
        }

        [Test] public void CacheHitsKeepRecentlyViewedTexturesResident()
        {
            byte[] png = SolidPng(8, Color.green);
            using var cache = new SceneTaggerThumbnailCache((_, _) => Task.FromResult(png), capacity: 2);
            Texture2D first = cache.Local("first", png, 4), second = cache.Local("second", png, 4);
            Assert.That(cache.Local("first", png, 4), Is.SameAs(first));
            cache.Local("third", png, 4);
            Assert.That(first != null, Is.True); Assert.That(second == null, Is.True);
        }

        [Test] public async Task AvatarDownloadsHaveBoundedConcurrency()
        {
            int active = 0, peak = 0;
            var release = new TaskCompletionSource<byte[]>();
            using var cache = new SceneTaggerThumbnailCache(async (_, _) =>
            {
                active++; peak = Math.Max(peak, active);
                try { return await release.Task; } finally { active--; }
            });
            for (int i = 0; i < 12; i++) cache.Remote("avatar-" + i, CancellationToken.None);
            Assert.That(active, Is.EqualTo(4));
            release.SetResult(SolidPng(8, Color.blue));
            while (cache.Pending("avatar-11")) await Task.Yield();
            Assert.That(peak, Is.EqualTo(4)); Assert.That(cache.Count, Is.EqualTo(12));
        }

        [TestCase(20, 35)]
        [TestCase(60, 65)]
        public void Remote_frames_the_head_despite_transparent_padding(int topPadding, int centerX)
        {
            const int width = 100, height = 200;
            var source = new Texture2D(width, height, TextureFormat.RGBA32, false);
            var pixels = new Color32[width * height];
            int top = height - topPadding;
            for (int y = top - 100; y < top; y++)
            {
                bool head = y >= top - 24;
                int halfWidth = head ? 12 : 24;
                for (int x = centerX - halfWidth; x < centerX + halfWidth; x++)
                    pixels[y * width + x] = head ? (Color32)Color.red : (Color32)Color.blue;
            }
            source.SetPixels32(pixels);
            source.Apply();
            byte[] png = source.EncodeToPNG();
            UnityEngine.Object.DestroyImmediate(source);
            using var cache = new SceneTaggerThumbnailCache((_, _) => Task.FromResult(png));
            cache.Remote("portrait", CancellationToken.None);
            Texture2D thumbnail = cache.Remote("portrait", CancellationToken.None);
            Color32[] result = ReadPixels(thumbnail);
            int count = 0, topVisible = -1;
            float sumX = 0, sumY = 0;
            for (int y = 0; y < thumbnail.height; y++)
            for (int x = 0; x < thumbnail.width; x++)
            {
                Color32 pixel = result[y * thumbnail.width + x];
                if (pixel.a < 128) continue;
                topVisible = Math.Max(topVisible, y);
                if (pixel.r <= pixel.b) continue;
                count++; sumX += x; sumY += y;
            }
            Assert.Greater(count, thumbnail.width * thumbnail.height / 5, "The head must be visible at a useful size.");
            Assert.That(sumX / count / thumbnail.width, Is.EqualTo(0.5f).Within(0.03f), "Center the visible avatar, not the source canvas.");
            Assert.That(sumY / count / thumbnail.height, Is.InRange(0.45f, 0.75f), "Keep the face in the middle of the portrait.");
            Assert.That((float)topVisible / thumbnail.height, Is.InRange(0.85f, 0.97f), "Keep a small, consistent margin above the head.");
        }

        [Test] public void OpaqueAvatarRetainsItsRightEdgeAtStridedResolution()
        {
            const int side = 1024;
            var source = new Texture2D(side, side, TextureFormat.RGBA32, false);
            var pixels = new Color32[side * side];
            for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++) pixels[y * side + x] = x >= side - 3 ? (Color32)Color.red : (Color32)Color.blue;
            source.SetPixels32(pixels); source.Apply();
            byte[] png = source.EncodeToPNG(); UnityEngine.Object.DestroyImmediate(source);
            using var cache = new SceneTaggerThumbnailCache((_, _) => Task.FromResult(png));
            cache.Remote("edge", CancellationToken.None);
            Texture2D thumbnail = cache.Remote("edge", CancellationToken.None);
            Color32[] result = ReadPixels(thumbnail);
            Assert.That(result[(thumbnail.height / 2 + 1) * thumbnail.width - 1].r, Is.GreaterThan(80),
                "The final source column must survive the silhouette crop.");
        }

        private static Color32[] ReadPixels(Texture2D texture)
        {
            RenderTexture previous = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(texture.width, texture.height);
            var readable = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);
            try
            {
                Graphics.Blit(texture, target);
                RenderTexture.active = target;
                readable.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
                return readable.GetPixels32();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(readable);
            }
        }

        [Test]
        public async Task Remote_downloads_once_and_does_not_retry_failures()
        {
            int calls = 0;
            using var cache = new SceneTaggerThumbnailCache((url, _) =>
            {
                calls++;
                return url == "ok" ? Task.FromResult(SolidPng(8, Color.blue)) : Task.FromException<byte[]>(new Exception("boom"));
            });
            Assert.IsNull(cache.Remote(null, CancellationToken.None));
            Assert.IsNull(cache.Remote("ok", CancellationToken.None));
            await Task.Delay(50);
            Assert.IsNotNull(cache.Remote("ok", CancellationToken.None));
            cache.Remote("bad", CancellationToken.None);
            await Task.Delay(50);
            cache.Remote("bad", CancellationToken.None);
            Assert.AreEqual(2, calls);
        }
    }
}
