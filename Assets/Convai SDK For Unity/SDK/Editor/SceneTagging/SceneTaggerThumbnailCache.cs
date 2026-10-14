using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Editor.SceneTagging
{
    /// <summary>Bounded texture cache for character avatars (remote) and evidence thumbnails (local downscale).</summary>
    internal sealed class SceneTaggerThumbnailCache : IDisposable
    {
        private readonly Func<string, CancellationToken, Task<byte[]>> _download;
        private readonly int _capacity;
        private readonly Dictionary<string, Texture2D> _textures = new();
        private readonly LinkedList<string> _order = new();
        private readonly HashSet<string> _pending = new(), _failed = new();
        private bool _disposed;
        private readonly SemaphoreSlim _downloads = new(4);

        /// <summary>Remote avatars are cropped to a square and stored at this size; full-size PNGs can be 8 MB each.</summary>
        internal const int RemoteSize = 256;

        internal event Action Changed;
        internal int Count => _textures.Count;
        internal bool Pending(string url) => !string.IsNullOrEmpty(url) && _pending.Contains(url);

        internal SceneTaggerThumbnailCache(Func<string, CancellationToken, Task<byte[]>> download, int capacity = 96)
        { _download = download; _capacity = Math.Max(1, capacity); }

        /// <summary>True once a remote download or decode has failed for this URL; callers can try a fallback.</summary>
        internal bool Failed(string url) => !string.IsNullOrEmpty(url) && _failed.Contains(url);

        internal Texture2D Remote(string url, CancellationToken token)
        {
            if (string.IsNullOrEmpty(url) || _disposed) return null;
            if (_textures.TryGetValue(url, out Texture2D texture)) { Touch(url); return texture; }
            if (_failed.Contains(url) || !_pending.Add(url)) return null;
            _ = FetchAsync(url, token);
            return null;
        }

        private async Task FetchAsync(string url, CancellationToken token)
        {
            Texture2D texture = null;
            try
            {
                byte[] png;
                await _downloads.WaitAsync(token);
                try
                {
                    token.ThrowIfCancellationRequested();
                    if (_disposed) return;
                    png = await _download(url, token);
                }
                finally { _downloads.Release(); }
                token.ThrowIfCancellationRequested();
                if (_disposed || png == null) { _failed.Add(url); return; }
                texture = Downscale(png, RemoteSize, frameAvatar: true);
                if (!texture)
                {
                    _failed.Add(url);
                    Debug.LogWarning($"[SceneTagger] Thumbnail is not a PNG or JPEG that Unity can decode: {url}");
                    return;
                }
                Put(url, texture);
            }
            catch (Exception exception)
            {
                if (texture) Object.DestroyImmediate(texture);
                _failed.Add(url);
                if (!(exception is OperationCanceledException)) Debug.LogWarning($"[SceneTagger] Thumbnail download failed for {url}: {exception.Message}");
            }
            finally { _pending.Remove(url); Changed?.Invoke(); }
        }

        internal Texture2D Local(string key, byte[] png, int size)
        {
            if (_disposed || png == null) return null;
            if (_textures.TryGetValue(key, out Texture2D texture) && texture) { Touch(key); return texture; }
            texture = Downscale(png, size);
            if (texture) Put(key, texture);
            return texture;
        }

        private void Touch(string key) { _order.Remove(key); _order.AddLast(key); }

        private void Put(string key, Texture2D texture)
        {
            if (_textures.TryGetValue(key, out Texture2D previous)) { if (previous) Object.DestroyImmediate(previous); _order.Remove(key); }
            _textures[key] = texture;
            _order.AddLast(key);
            while (_order.Count > _capacity)
            {
                string oldest = _order.First.Value;
                _order.RemoveFirst();
                if (_textures.Remove(oldest, out Texture2D evicted) && evicted) Object.DestroyImmediate(evicted);
            }
        }

        internal static string Initials(string name)
        {
            string[] words = (name ?? "").Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "?";
            string initials = char.ToUpperInvariant(words[0][0]).ToString();
            if (words.Length > 1) initials += char.ToUpperInvariant(words[1][0]);
            return initials;
        }

        /// <summary>
        ///     Decodes a PNG or JPEG and returns a <paramref name="size" />² texture. Portrait images are
        ///     cropped to their top square (head and shoulders of a full-body avatar); landscape images are
        ///     centre-cropped. Avatars are framed relative to their visible bounds to ignore transparent
        ///     canvas padding. Evidence thumbnails retain their original framing. Returns null for invalid bytes.
        /// </summary>
        internal static Texture2D Downscale(byte[] png, int size, bool frameAvatar = false)
        {
            var full = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!full.LoadImage(png)) { Object.DestroyImmediate(full); return null; }
            float aspect = (float)full.width / full.height;
            // Full-body renders (about 1:2) get a tighter head-and-shoulders crop; other portraits take their top square.
            float span = aspect <= 0.6f ? 0.7f : 1f;
            Vector2 scale = aspect < 1f ? new Vector2(span, span * aspect) : new Vector2(1f / aspect, 1f);
            Vector2 offset = aspect < 1f ? new Vector2((1f - span) * 0.5f, 1f - (span * aspect) - (aspect <= 0.6f ? 0.03f : 0f)) : new Vector2((1f - (1f / aspect)) * 0.5f, 0f);
            if (frameAvatar)
            {
                Rect crop = AvatarCrop(full);
                scale = new Vector2(crop.width / full.width, crop.height / full.height);
                offset = new Vector2(crop.x / full.width, crop.y / full.height);
            }
            RenderTexture target = RenderTexture.GetTemporary(size, size, 0, RenderTextureFormat.ARGB32);
            RenderTexture previous = RenderTexture.active;
            try
            {
                Graphics.Blit(full, target, scale, offset);
                RenderTexture.active = target;
                var small = new Texture2D(size, size, TextureFormat.RGBA32, false) { hideFlags = HideFlags.HideAndDontSave };
                small.ReadPixels(new Rect(0, 0, size, size), 0, 0);
                small.Apply(false, true);
                return small;
            }
            finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Object.DestroyImmediate(full); }
        }

        private static Rect AvatarCrop(Texture2D texture)
        {
            var pixels = texture.GetPixelData<byte>(0);
            // LoadImage may decode PNG into ARGB32, while JPEG is opaque RGB24.
            int alphaOffset = texture.format == TextureFormat.ARGB32 ? 0 :
                texture.format == TextureFormat.RGBA32 || texture.format == TextureFormat.BGRA32 ? 3 : -1;
            int left = texture.width, bottom = texture.height, right = -1, top = -1;
            // Bound silhouette work to roughly 65k samples even for full-size 2k avatars.
            int step = Math.Max(1, Mathf.CeilToInt(Math.Max(texture.width, texture.height) / 256f));
            for (int sy = 0; sy <= (texture.height - 1 + step - 1) / step; sy++)
            for (int sx = 0; sx <= (texture.width - 1 + step - 1) / step; sx++)
            {
                int x = Math.Min(sx * step, texture.width - 1), y = Math.Min(sy * step, texture.height - 1);
                // Ignore almost-transparent edge pixels when measuring the silhouette.
                if (alphaOffset >= 0 && pixels[(y * texture.width + x) * 4 + alphaOffset] <= 16) continue;
                left = Math.Min(left, x); right = Math.Max(right, x);
                bottom = Math.Min(bottom, y); top = Math.Max(top, y);
            }
            if (right < left) return new Rect(0, 0, texture.width, texture.height);
            // The silhouette can extend between samples. Keep the full possible edge cell.
            left = Math.Max(0, left - step + 1); right = Math.Min(texture.width - 1, right + step - 1);
            bottom = Math.Max(0, bottom - step + 1); top = Math.Min(texture.height - 1, top + step - 1);
            float width = right - left + 1, height = top - bottom + 1;
            // Tall renders need head-and-shoulders framing; shorter portraits keep their full width.
            float side = Mathf.Min(width, height) * (width / height <= 0.6f ? 0.7f : 1f);
            float xMin = Mathf.Clamp((left + right + 1) * 0.5f - side * 0.5f, 0, texture.width - side);
            float yMin = Mathf.Clamp(top + 1 + side * 0.08f - side, 0, texture.height - side);
            return new Rect(xMin, yMin, side, side);
        }

        public void Dispose()
        {
            _disposed = true;
            foreach (Texture2D texture in _textures.Values) if (texture) Object.DestroyImmediate(texture);
            _textures.Clear(); _order.Clear(); _pending.Clear(); _failed.Clear();
        }
    }
}
