using System.Collections.Generic;
using Convai.Modules.BodyAnimation.Core.Policy;
using NUnit.Framework;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Coverage for <see cref="ReferentialGestureMatcher" />: whole-word matching
    ///     for second-person/first-person/ordinal word classes, longest-name object-mention
    ///     matching, and no-match behavior for empty/unrelated lines.
    /// </summary>
    public sealed class ReferentialGestureMatcherTests
    {
        [TestCase("Would you like some tea?", true)]
        [TestCase("Is this yours?", true)]
        [TestCase("Take care of yourself.", true)]
        [TestCase("The young fox ran away.", false)]
        [TestCase("Nothing to see here.", false)]
        public void SecondPerson_MatchesWholeWordOnly(string line, bool expected)
        {
            ReferentialGestureMatcher.MatchResult result = ReferentialGestureMatcher.Match(line, null);
            Assert.That(result.SecondPerson, Is.EqualTo(expected));
        }

        [TestCase("I think that's mine.", true)]
        [TestCase("Give it to me.", true)]
        [TestCase("Let's visit the island.", false)]
        [TestCase("The team won together.", false)]
        public void FirstPerson_MatchesWholeWordOnly(string line, bool expected)
        {
            ReferentialGestureMatcher.MatchResult result = ReferentialGestureMatcher.Match(line, null);
            Assert.That(result.FirstPerson, Is.EqualTo(expected));
        }

        [TestCase("This is the first step.", true)]
        [TestCase("I have three ideas.", true)]
        [TestCase("Nothing numeric here.", false)]
        public void Ordinal_MatchesWholeWordOnly(string line, bool expected)
        {
            ReferentialGestureMatcher.MatchResult result = ReferentialGestureMatcher.Match(line, null);
            Assert.That(result.Ordinal, Is.EqualTo(expected));
        }

        [Test]
        public void ObjectMention_MatchesRegisteredName()
        {
            var names = new List<string> { "painting", "magic painting" };
            ReferentialGestureMatcher.MatchResult result =
                ReferentialGestureMatcher.Match("Look at the painting on the wall.", names);

            Assert.IsTrue(result.HasObjectMention);
            Assert.AreEqual("painting", result.ObjectName);
        }

        [Test]
        public void ObjectMention_LongestMultiWordNameWins()
        {
            var names = new List<string> { "painting", "magic painting" };
            ReferentialGestureMatcher.MatchResult result =
                ReferentialGestureMatcher.Match("The magic painting glows.", names);

            Assert.IsTrue(result.HasObjectMention);
            Assert.AreEqual("magic painting", result.ObjectName);
        }

        [Test]
        public void ObjectMention_NoMatch_WhenNameAbsent()
        {
            var names = new List<string> { "painting" };
            ReferentialGestureMatcher.MatchResult result =
                ReferentialGestureMatcher.Match("This room is quiet.", names);

            Assert.IsFalse(result.HasObjectMention);
            Assert.IsNull(result.ObjectName);
        }

        [Test]
        public void EmptyOrNullUtterance_NoMatch()
        {
            Assert.IsFalse(ReferentialGestureMatcher.Match(null, null).HasMatch);
            Assert.IsFalse(ReferentialGestureMatcher.Match(string.Empty, null).HasMatch);
            Assert.IsFalse(ReferentialGestureMatcher.Match("   ", null).HasMatch);
        }

        [Test]
        public void PlainStatement_NoClassMatches()
        {
            ReferentialGestureMatcher.MatchResult result =
                ReferentialGestureMatcher.Match("The weather is nice today.", null);

            Assert.IsFalse(result.HasMatch);
        }

        [Test]
        public void MultipleClasses_CanMatchSimultaneously()
        {
            var names = new List<string> { "painting" };
            ReferentialGestureMatcher.MatchResult result =
                ReferentialGestureMatcher.Match("I think you should look at the first painting.", names);

            Assert.IsTrue(result.FirstPerson);
            Assert.IsTrue(result.SecondPerson);
            Assert.IsTrue(result.Ordinal);
            Assert.IsTrue(result.HasObjectMention);
        }
    }
}
