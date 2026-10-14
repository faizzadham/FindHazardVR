using System.Collections.Generic;
using Convai.Modules.Gaze.Providers;
using Convai.Runtime.Embodiment;
using Convai.Tests.EditMode.Fixtures;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Gaze
{
    /// <summary>
    ///     E7 referential glances: the mention matcher (word boundaries, multi-word, case,
    ///     punctuation, greedy-longest, word limit), the per-object cooldown, the
    ///     newest-replaces queue, and seeded delay determinism. Backend event wiring is
    ///     covered manually.
    /// </summary>
    public sealed class GazeReferentialGlancesTests
    {
        private readonly List<GameObject> _spawned = new();

        [TearDown]
        public void TearDown()
        {
            for (int i = _spawned.Count - 1; i >= 0; i--)
                if (_spawned[i] != null) Object.DestroyImmediate(_spawned[i]);
            _spawned.Clear();
        }

        // ── Matcher ──────────────────────────────────────────────────────────

        [Test]
        public void Matcher_MatchesWholeWord_IgnoringCaseAndPunctuation()
        {
            string[] names = { "painting" };
            Assert.IsTrue(GazeReferentialGlances.TryMatchMention(
                "Take a look at the Painting!", names, 4, out string matched));
            Assert.AreEqual("painting", matched);
        }

        [Test]
        public void Matcher_RespectsWordBoundaries()
        {
            Assert.IsFalse(GazeReferentialGlances.TryMatchMention(
                "I really love paintings", new[] { "painting" }, 4, out _),
                "'paintings' must not match the object 'painting'.");
        }

        [Test]
        public void Matcher_MultiWordName_MustBeContiguous()
        {
            Assert.IsTrue(GazeReferentialGlances.TryMatchMention(
                "check the magic painting over there", new[] { "magic painting" }, 4, out string matched));
            Assert.AreEqual("magic painting", matched);

            Assert.IsFalse(GazeReferentialGlances.TryMatchMention(
                "magic is not the same as a painting", new[] { "magic painting" }, 4, out _),
                "Non-contiguous words must not match a multi-word name.");
        }

        [Test]
        public void Matcher_GreedyLongestNameWins()
        {
            Assert.IsTrue(GazeReferentialGlances.TryMatchMention(
                "point at the magic painting", new[] { "painting", "magic painting" }, 4, out string matched));
            Assert.AreEqual("magic painting", matched,
                "When both a bare and a longer name match, the longer (more specific) one wins.");
        }

        [Test]
        public void Matcher_SkipsNamesLongerThanWordLimit()
        {
            Assert.IsFalse(GazeReferentialGlances.TryMatchMention(
                "the big red car is here", new[] { "big red car" }, 2, out _),
                "A 3-word name is ignored when the word limit is 2.");
            Assert.IsTrue(GazeReferentialGlances.TryMatchMention(
                "the big red car is here", new[] { "big red car" }, 3, out _));
        }

        [Test]
        public void Matcher_NoMentionOrEmpty_ReturnsFalse()
        {
            Assert.IsFalse(GazeReferentialGlances.TryMatchMention(
                "nothing relevant here", new[] { "painting" }, 4, out _));
            Assert.IsFalse(GazeReferentialGlances.TryMatchMention(
                "", new[] { "painting" }, 4, out _));
        }

        // ── Cooldown ─────────────────────────────────────────────────────────

        [Test]
        public void Cooldown_BlocksReglanceWithinWindow_ThenAllows()
        {
            GazeReferentialGlances glances = NewComponent();
            var names = new List<string> { "painting" };

            Assert.IsTrue(glances.TryDecideGlance("look at the painting", names, 0f, out _));
            glances.MarkGlanced("painting", 0f);

            Assert.IsFalse(glances.TryDecideGlance("the painting again", names, 5f, out _),
                "Within the 10 s cooldown the same object is not re-glanced.");
            Assert.IsTrue(glances.TryDecideGlance("the painting again", names, 11f, out _),
                "After the cooldown the object can be glanced again.");
        }

        // ── Newest-replaces queue ────────────────────────────────────────────

        [Test]
        public void Schedule_NewestMentionReplacesPendingGlance()
        {
            GazeReferentialGlances glances = NewComponent();
            var names = new List<string> { "painting", "statue" };
            DeterministicEmbodimentRandom random = DeterministicEmbodimentRandomFixture.Create();

            glances.ScheduleGlanceForMention("look at the painting", names, 0f, ref random);
            Assert.AreEqual("painting", glances.PendingName);

            glances.ScheduleGlanceForMention("now look at the statue", names, 0.1f, ref random);
            Assert.AreEqual("statue", glances.PendingName,
                "The newest mention replaces a still-pending glance (at most one queued).");
        }

        // ── Seeded delay ─────────────────────────────────────────────────────

        [Test]
        public void NextDelay_IsDeterministicForSeed_AndWithinRange()
        {
            DeterministicEmbodimentRandom a = DeterministicEmbodimentRandomFixture.Create(777u);
            DeterministicEmbodimentRandom b = DeterministicEmbodimentRandomFixture.Create(777u);
            for (int i = 0; i < 6; i++)
                Assert.AreEqual(
                    GazeReferentialGlances.NextDelay(0.3f, 0.8f, ref a),
                    GazeReferentialGlances.NextDelay(0.3f, 0.8f, ref b), 1e-6f,
                    "Same seed → identical delay stream.");

            DeterministicEmbodimentRandom r = DeterministicEmbodimentRandomFixture.Create(1u);
            float delay = GazeReferentialGlances.NextDelay(0.3f, 0.8f, ref r);
            Assert.That(delay, Is.InRange(0.3f, 0.8f));
        }

        // ── Helpers ──────────────────────────────────────────────────────────

        private GazeReferentialGlances NewComponent()
        {
            var go = new GameObject("Referential");
            _spawned.Add(go);
            return go.AddComponent<GazeReferentialGlances>();
        }
    }
}
