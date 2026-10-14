using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    public sealed class TalkMotionFragmentTests
    {
        [Test]
        public void ValidFragments_EnableLimitedClipRepertoire()
        {
            var entry = new TalkEntry();
            var first = new TalkMotionFragment();
            first.Initialize(0.05f, 0.3f, label: "First");
            var second = new TalkMotionFragment();
            second.Initialize(0.4f, 0.7f, label: "Second");
            entry.ReplaceFragments(new List<TalkMotionFragment> { first, second });

            Assert.That(entry.HasFragments, Is.True);
            Assert.That(entry.Fragments.Count, Is.EqualTo(2));
            Assert.That(entry.Fragments[0].EndNormalized, Is.GreaterThan(entry.Fragments[0].StartNormalized));
        }

        [Test]
        public void ZeroWeightFragment_DoesNotEnableFragmentPlayback()
        {
            var entry = new TalkEntry();
            var fragment = new TalkMotionFragment();
            fragment.Initialize(0.1f, 0.5f, 0f);
            entry.ReplaceFragments(new List<TalkMotionFragment> { fragment });
            Assert.That(entry.HasFragments, Is.False);
        }
    }
}
