using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.CharacterApi.Contracts.Model;
using Newtonsoft.Json.Linq;

namespace Convai.Editor.SceneTagging
{
    internal delegate Task<PageCharacterListItem> SceneTaggerListPage(int limit, int offset, CancellationToken token);

    /// <summary>Account character list for the Setup page: paged browsing, whole-account search, thumbnail lookup. No Unity UI.</summary>
    internal sealed class SceneTaggerCharacterCatalog
    {
        internal const int PageSize = 24;
        private const int SweepPageSize = 50;
        private const string AvatarKey = "avatar_image", SquareAvatarKey = "avatar_image_square", PlaceholderKey = "modelPlaceholder";

        private readonly SceneTaggerListPage _list;
        private readonly List<CharacterListItem> _pages = new();
        private List<CharacterListItem> _everything;
        private string _query = "";

        internal SceneTaggerCharacterCatalog(SceneTaggerListPage list) => _list = list;

        internal IReadOnlyList<CharacterListItem> Visible { get; private set; } = Array.Empty<CharacterListItem>();
        internal int Total { get; private set; }
        internal bool Loading { get; private set; }
        internal bool Loaded { get; private set; }
        internal bool HasMore => _everything == null && Loaded && _pages.Count < Total;
        internal bool NeedsSearchLoad => _query.Length > 0 && _everything == null;

        internal string Query
        {
            get => _query;
            set { value ??= ""; if (_query == value) return; _query = value; Filter(); }
        }

        internal async Task LoadMoreAsync(CancellationToken token)
        {
            if (Loading) return;
            Loading = true;
            try
            {
                if (NeedsSearchLoad) await LoadEverythingAsync(token);
                else if (_everything == null)
                {
                    PageCharacterListItem page = await _list(PageSize, _pages.Count, token);
                    _pages.AddRange(page.Items ?? new List<CharacterListItem>());
                    Total = page.Total;
                }
                // A query typed while that page was in flight arrives too late for the branch above and
                // its own LoadMoreAsync call is dropped by the Loading guard, so this load owns it.
                if (NeedsSearchLoad) await LoadEverythingAsync(token);
                Loaded = true;
                Filter();
            }
            finally { Loading = false; }
        }

        private async Task LoadEverythingAsync(CancellationToken token)
        {
            var all = new List<CharacterListItem>();
            for (int offset = 0; ;)
            {
                PageCharacterListItem page = await _list(SweepPageSize, offset, token);
                if (page.Items == null || page.Items.Count == 0) break;
                all.AddRange(page.Items);
                offset += page.Items.Count;
                if (offset >= page.Total) break;
            }
            _everything = all;
            Total = all.Count;
        }

        private void Filter()
        {
            IEnumerable<CharacterListItem> source = _everything ?? _pages;
            Visible = _query.Length == 0
                ? source.ToList()
                : source.Where(c => (c.CharacterName ?? "").IndexOf(_query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
        }

        /// <summary>
        ///     Candidate thumbnail URLs, best first. The full-body <c>avatar_image</c> under the model-type
        ///     object is a PNG; <c>modelPlaceholder</c> and <c>avatar_image_square</c> follow as fallbacks
        ///     (the square avatar is served as WebP, which Unity cannot decode, and the placeholder host is
        ///     not always reachable).
        /// </summary>
        internal static IEnumerable<string> ThumbnailUrls(CharacterListItem item)
        {
            if (item?.ModelDetails == null) yield break;
            string avatar = Nested(item, AvatarKey);
            if (IsHttp(avatar)) yield return avatar;
            if (item.ModelDetails.TryGetValue(PlaceholderKey, out object placeholder) && IsHttp(placeholder as string)) yield return (string)placeholder;
            string square = Nested(item, SquareAvatarKey);
            if (IsHttp(square)) yield return square;
        }

        /// <summary>The best thumbnail URL, or null. See <see cref="ThumbnailUrls" />.</summary>
        internal static string ThumbnailUrl(CharacterListItem item) => ThumbnailUrls(item).FirstOrDefault();

        private static string Nested(CharacterListItem item, string key)
        {
            foreach (object value in item.ModelDetails.Values)
            {
                string url = value switch
                {
                    JObject json => json.Value<string>(key),
                    IDictionary<string, object> dictionary => dictionary.TryGetValue(key, out object nested) ? nested as string : null,
                    _ => null
                };
                if (IsHttp(url)) return url;
            }
            return null;
        }

        private static bool IsHttp(string url) => !string.IsNullOrEmpty(url) && url.StartsWith("http", StringComparison.OrdinalIgnoreCase);
    }
}
