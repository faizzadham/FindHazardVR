#if UNITY_EDITOR
using System.Globalization;
using Convai.Editor.UI;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Presentation
{
    /// <summary>
    ///     Pins the contract of <see cref="ConvaiEditorTextMetrics" />, the one place Convai's editor
    ///     UI measures text.
    /// </summary>
    /// <remarks>
    ///     Two properties matter and both are asserted here. <b>Correctness</b>: a memoised answer must
    ///     equal what the style would have reported, or every panel laid out against it is laid out
    ///     against a lie. <b>Boundedness</b>: the cache lives in a static field for the life of the
    ///     domain, so text that varies per frame — a live telemetry readout — must not be able to grow
    ///     it without limit.
    /// </remarks>
    public class ConvaiEditorTextMetricsTests
    {
        private const string SampleBody =
            "Configured and healthy — this component will work when you press Play, provided the " +
            "character it belongs to has a profile assigned and the scene carries a Convai Manager.";

        [SetUp]
        public void SetUp()
        {
            ConvaiEditorStyles.EnsureStyles();
            ConvaiEditorTextMetrics.Invalidate();
        }

        [TearDown]
        public void TearDown() => ConvaiEditorTextMetrics.Invalidate();

        [Test]
        public void WrappedHeight_MatchesDirectMeasurement()
        {
            GUIStyle style = ConvaiEditorStyles.MutedWrapped;
            const float width = 260f;

            float expected = style.CalcHeight(new GUIContent(SampleBody), width);
            float actual = ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, width);

            Assert.AreEqual(expected, actual, 0.0001f,
                "A memoised height must equal the measurement it replaces — a panel is laid out against it.");
        }

        [Test]
        public void WrappedHeight_IsStableAcrossRepeatedCalls()
        {
            GUIStyle style = ConvaiEditorStyles.MutedWrapped;

            float first = ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, 240f);
            float second = ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, 240f);
            float third = ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, 240f);

            Assert.AreEqual(first, second);
            Assert.AreEqual(second, third);
        }

        [Test]
        public void WrappedHeight_SeparatesEntriesByWidth()
        {
            GUIStyle style = ConvaiEditorStyles.MutedWrapped;

            ConvaiEditorTextMetrics.Invalidate();
            ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, 200f);
            int afterFirstWidth = ConvaiEditorTextMetrics.CachedEntryCount;

            ConvaiEditorTextMetrics.WrappedHeight(style, SampleBody, 400f);

            Assert.Greater(
                ConvaiEditorTextMetrics.CachedEntryCount, afterFirstWidth,
                "The same text at a different width is a different measurement and needs its own entry — " +
                "sharing one would make a resized inspector draw against the old width's height.");
        }

        [Test]
        public void Width_MatchesDirectMeasurement()
        {
            GUIStyle style = ConvaiEditorStyles.MicroLabel;
            var content = new GUIContent("Needs Attention");

            float expected = style.CalcSize(content).x;
            float actual = ConvaiEditorTextMetrics.Width(style, content);

            Assert.AreEqual(expected, actual, 0.0001f);
        }

        [Test]
        public void Width_IgnoresTooltip()
        {
            GUIStyle style = ConvaiEditorStyles.MicroLabel;

            float plain = ConvaiEditorTextMetrics.Width(style, new GUIContent("Ready"));
            float tooltipped = ConvaiEditorTextMetrics.Width(
                style, new GUIContent("Ready", "Configured and healthy."));

            Assert.AreEqual(plain, tooltipped,
                "A tooltip is not drawn inline, so it must not widen the pill that carries it.");
        }

        [Test]
        public void Cache_StaysBounded()
        {
            GUIStyle style = ConvaiEditorStyles.MutedWrapped;

            // Well past the internal capacity, in the shape that could actually reach it: text that
            // differs on every call, as a live telemetry readout does.
            for (int i = 0; i < 4000; i++)
            {
                ConvaiEditorTextMetrics.WrappedHeight(
                    style, i.ToString(CultureInfo.InvariantCulture), 200f);
            }

            Assert.LessOrEqual(
                ConvaiEditorTextMetrics.CachedEntryCount, 1024,
                "The metrics cache is a static field that survives until the next domain reload. " +
                "Per-frame-varying text must not be able to grow it without limit.");
        }

        [Test]
        public void Invalidate_ClearsEveryEntry()
        {
            ConvaiEditorTextMetrics.WrappedHeight(ConvaiEditorStyles.MutedWrapped, SampleBody, 220f);
            ConvaiEditorTextMetrics.Width(ConvaiEditorStyles.MicroLabel, new GUIContent("Live"));
            Assert.Greater(ConvaiEditorTextMetrics.CachedEntryCount, 0);

            ConvaiEditorTextMetrics.Invalidate();

            Assert.AreEqual(0, ConvaiEditorTextMetrics.CachedEntryCount);
        }

        [Test]
        public void Styles_PublishAGenerationThatMovesOnRebuild()
        {
            // The metrics cache watches this number to know a skin flip replaced the styles it
            // measured against. A generation that never moves is a cache that never invalidates.
            ConvaiEditorStyles.EnsureStyles();
            int generation = ConvaiEditorStyles.Generation;

            Assert.Greater(generation, 0, "The style set must report a generation once it has been built.");

            ConvaiEditorStyles.EnsureStyles();
            Assert.AreEqual(
                generation, ConvaiEditorStyles.Generation,
                "EnsureStyles must not bump the generation when it had nothing to rebuild — every " +
                "call would otherwise throw away the whole metrics cache.");
        }

        [Test]
        public void SectionHeaderStyles_ReuseOneInstancePerColour()
        {
            // The pooled styles replaced a single shared instance that was re-tinted per header per
            // repaint. Handing back the same instance for the same colour is what lets IMGUI keep its
            // generated text between two labels that look alike.
            GUIStyle first = ConvaiEditorStyles.SectionHeaderLabelTinted(ConvaiEditorTheme.Accent);
            GUIStyle again = ConvaiEditorStyles.SectionHeaderLabelTinted(ConvaiEditorTheme.Accent);

            Assert.AreSame(first, again);
        }

        [Test]
        public void SectionHeaderStyles_KeepColoursApart()
        {
            GUIStyle accent = ConvaiEditorStyles.SectionHeaderLabelTinted(ConvaiEditorTheme.Accent);
            GUIStyle warn = ConvaiEditorStyles.SectionHeaderLabelTinted(ConvaiEditorTheme.StatusWarn);

            Assert.AreNotSame(
                accent, warn,
                "Two sections in different accents must not share one style, or the second draw " +
                "would repaint the first section's title in the wrong colour.");
            Assert.AreNotEqual(accent.normal.textColor, warn.normal.textColor);
        }

        [Test]
        public void SectionHeaderStyles_TintEveryInteractionState()
        {
            // A header row that leaves hover or active on the previous section's colour flickers
            // between accents under the pointer. The pool must carry the whole state set the shared
            // instance used to be given.
            GUIStyle style = ConvaiEditorStyles.SectionHeaderLabelTinted(ConvaiEditorTheme.StatusError);
            Color expected = ConvaiEditorTheme.StatusError;

            Assert.AreEqual(expected, style.normal.textColor);
            Assert.AreEqual(expected, style.onNormal.textColor);
            Assert.AreEqual(expected, style.hover.textColor);
            Assert.AreEqual(expected, style.onHover.textColor);
            Assert.AreEqual(expected, style.active.textColor);
            Assert.AreEqual(expected, style.onActive.textColor);
            Assert.AreEqual(expected, style.focused.textColor);
            Assert.AreEqual(expected, style.onFocused.textColor);
        }

        [Test]
        public void SectionIconStyles_KeepFontSizesApart()
        {
            GUIStyle small = ConvaiEditorStyles.SectionIconTinted(ConvaiEditorTheme.Accent, 11);
            GUIStyle large = ConvaiEditorStyles.SectionIconTinted(ConvaiEditorTheme.Accent, 17);

            Assert.AreNotSame(small, large);
            Assert.AreEqual(11, small.fontSize);
            Assert.AreEqual(17, large.fontSize);
        }
    }
}
#endif
