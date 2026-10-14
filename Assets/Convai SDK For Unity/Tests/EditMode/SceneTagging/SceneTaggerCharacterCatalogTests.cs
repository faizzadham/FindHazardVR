using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Convai.CharacterApi.Contracts.Model;
using Convai.Editor.SceneTagging;
using NUnit.Framework;

namespace Convai.Tests.EditMode.SceneTagging
{
    public sealed class SceneTaggerCharacterCatalogTests
    {
        private static CharacterListItem Item(string name, Dictionary<string, object> details = null) =>
            new(characterId: Guid.NewGuid().ToString(), characterName: name, voiceType: "v", languageCode: "en", modelDetails: details);

        private static SceneTaggerListPage Account(params string[] names)
        {
            List<CharacterListItem> all = names.Select(n => Item(n)).ToList();
            return (limit, offset, _) => Task.FromResult(new PageCharacterListItem(
                items: all.Skip(offset).Take(limit).ToList(), total: all.Count, limit: limit, offset: offset));
        }

        [Test]
        public async Task LoadMore_appends_pages_and_reports_has_more()
        {
            string[] names = Enumerable.Range(0, 30).Select(i => "C" + i).ToArray();
            var catalog = new SceneTaggerCharacterCatalog(Account(names));
            Assert.IsFalse(catalog.Loaded);

            await catalog.LoadMoreAsync(CancellationToken.None);
            Assert.IsTrue(catalog.Loaded);
            Assert.AreEqual(SceneTaggerCharacterCatalog.PageSize, catalog.Visible.Count);
            Assert.AreEqual(30, catalog.Total);
            Assert.IsTrue(catalog.HasMore);

            await catalog.LoadMoreAsync(CancellationToken.None);
            Assert.AreEqual(30, catalog.Visible.Count);
            Assert.IsFalse(catalog.HasMore);
        }

        [Test]
        public async Task Query_loads_whole_account_once_then_filters_locally()
        {
            int calls = 0;
            SceneTaggerListPage inner = Account("Elara", "Sofia", "Antoni", "Stack Bot");
            SceneTaggerListPage counted = (l, o, t) => { calls++; return inner(l, o, t); };
            var catalog = new SceneTaggerCharacterCatalog(counted);

            catalog.Query = "so";
            Assert.IsTrue(catalog.NeedsSearchLoad);
            await catalog.LoadMoreAsync(CancellationToken.None);
            int callsAfterSearch = calls;
            CollectionAssert.AreEqual(new[] { "Sofia" }, catalog.Visible.Select(c => c.CharacterName));
            Assert.AreEqual(4, catalog.Total);
            Assert.IsFalse(catalog.HasMore);

            catalog.Query = "a";
            Assert.IsFalse(catalog.NeedsSearchLoad);
            CollectionAssert.AreEquivalent(new[] { "Elara", "Sofia", "Antoni", "Stack Bot" }, catalog.Visible.Select(c => c.CharacterName));
            Assert.AreEqual(callsAfterSearch, calls);

            catalog.Query = "";
            Assert.AreEqual(4, catalog.Visible.Count);
        }

        [Test]
        public async Task Query_set_while_the_first_page_is_in_flight_still_loads_the_whole_account()
        {
            // The one match sits past the first page, so a catalog that filtered only the page it had
            // already fetched would show nothing.
            string[] names = Enumerable.Range(0, 29).Select(i => "C" + i).Append("Sofia").ToArray();
            var firstPage = new TaskCompletionSource<PageCharacterListItem>();
            SceneTaggerListPage inner = Account(names);
            bool pageInFlight = false;
            SceneTaggerListPage gated = (limit, offset, token) =>
            {
                if (pageInFlight) return inner(limit, offset, token);
                pageInFlight = true;
                return firstPage.Task;
            };
            var catalog = new SceneTaggerCharacterCatalog(gated);

            Task load = catalog.LoadMoreAsync(CancellationToken.None);
            catalog.Query = "so";
            Assert.IsTrue(catalog.NeedsSearchLoad);
            await catalog.LoadMoreAsync(CancellationToken.None); // dropped by the Loading guard
            firstPage.SetResult(await inner(SceneTaggerCharacterCatalog.PageSize, 0, CancellationToken.None));
            await load;

            CollectionAssert.AreEqual(new[] { "Sofia" }, catalog.Visible.Select(c => c.CharacterName));
            Assert.IsFalse(catalog.NeedsSearchLoad);
            Assert.AreEqual(30, catalog.Total);
        }

        [Test]
        public void ThumbnailUrl_prefers_png_avatar_then_placeholder_then_square_avatar()
        {
            Assert.IsNull(SceneTaggerCharacterCatalog.ThumbnailUrl(Item("A")));
            Assert.IsNull(SceneTaggerCharacterCatalog.ThumbnailUrl(Item("A", new() { ["modelPlaceholder"] = "not a url" })));
            Assert.AreEqual("https://x/z.png", SceneTaggerCharacterCatalog.ThumbnailUrl(Item("A", new() { ["modelPlaceholder"] = "https://x/z.png" })));
            var nested = Newtonsoft.Json.Linq.JObject.FromObject(new { avatar_image = "https://x/a.png", avatar_image_square = "https://x/sq.png" });
            CollectionAssert.AreEqual(new[] { "https://x/a.png", "https://x/z.png", "https://x/sq.png" },
                SceneTaggerCharacterCatalog.ThumbnailUrls(Item("A", new() { ["modelPlaceholder"] = "https://x/z.png", ["METAHUMAN"] = nested })).ToList());
            var nestedDictionary = new Dictionary<string, object> { ["avatar_image_square"] = "https://x/d.png" };
            Assert.AreEqual("https://x/d.png", SceneTaggerCharacterCatalog.ThumbnailUrl(Item("A", new() { ["RPM"] = nestedDictionary })));
        }
    }
}
