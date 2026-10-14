using Convai.Runtime.Actions;
using Convai.Shared.Types;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Actions
{
    /// <summary>
    ///     Covers the authoring category on <see cref="ConvaiActionDefinition" /> : normalization,
    ///     case-insensitive identity, survival through <see cref="ConvaiActionDefinition.Clone" />,
    ///     and — the invariant the whole feature rests on — that a category never reaches the wire
    ///     template sent to the backend.
    /// </summary>
    [TestFixture]
    public class ConvaiActionCategoryTests
    {
        // ── Normalization ──────────────────────────────────────────────────────

        [Test]
        public void Normalize_NullOrWhitespace_IsUncategorized()
        {
            Assert.AreEqual(string.Empty, ConvaiActionCategory.Normalize(null));
            Assert.AreEqual(string.Empty, ConvaiActionCategory.Normalize("   "));
            Assert.AreEqual(string.Empty, ConvaiActionCategory.Normalize("\t\n"));
            Assert.IsTrue(ConvaiActionCategory.IsUncategorized(" "));
        }

        [Test]
        public void Normalize_AlreadyCleanName_IsReturnedUnchanged()
        {
            const string name = "Shop Counter";
            Assert.AreSame(name, ConvaiActionCategory.Normalize(name));
        }

        [Test]
        public void Normalize_TrimsAndCollapsesInnerWhitespace()
        {
            Assert.AreEqual("Shop Counter", ConvaiActionCategory.Normalize("  Shop   Counter  "));
            Assert.AreEqual("Shop Counter", ConvaiActionCategory.Normalize("Shop\tCounter"));
            Assert.AreEqual("Shop Counter", ConvaiActionCategory.Normalize("Shop \n Counter"));
        }

        [Test]
        public void Normalize_TreatsControlCharactersAsSeparators()
        {
            Assert.AreEqual("Tour", ConvaiActionCategory.Normalize("Tour" + (char)7));
            Assert.AreEqual("Look At", ConvaiActionCategory.Normalize("Look" + (char)0 + "At"));
        }

        [Test]
        public void Normalize_CapsLengthAndDoesNotEndOnASpace()
        {
            string overlong = new string('x', ConvaiActionCategory.MaxLength + 20);
            Assert.AreEqual(ConvaiActionCategory.MaxLength, ConvaiActionCategory.Normalize(overlong).Length);

            // The cut must not leave a dangling separator behind.
            string wordAtTheBoundary = new string('x', ConvaiActionCategory.MaxLength - 1) + "  tail";
            string normalized = ConvaiActionCategory.Normalize(wordAtTheBoundary);
            Assert.LessOrEqual(normalized.Length, ConvaiActionCategory.MaxLength);
            Assert.AreEqual(normalized.TrimEnd(), normalized);
        }

        // ── Identity ───────────────────────────────────────────────────────────

        [Test]
        public void AreSame_IgnoresCasingAndSurroundingWhitespace()
        {
            Assert.IsTrue(ConvaiActionCategory.AreSame("Tour", "tour"));
            Assert.IsTrue(ConvaiActionCategory.AreSame(" Tour ", "TOUR"));
            Assert.IsTrue(ConvaiActionDefinition.IsSameCategory("Tour", "tour"));
            Assert.IsFalse(ConvaiActionDefinition.IsSameCategory("Tour", "Tours"));
        }

        [Test]
        public void AreSame_NullAndEmptyAreTheSameUncategorizedBucket()
        {
            Assert.IsTrue(ConvaiActionCategory.AreSame(null, string.Empty));
            Assert.IsTrue(ConvaiActionCategory.AreSame(null, "  "));
        }

        // ── The definition ─────────────────────────────────────────────────────

        [Test]
        public void Category_DefaultsToUncategorizedAndNeverReturnsNull()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Greet" };
            Assert.AreEqual(string.Empty, definition.Category);

            definition.Category = null;
            Assert.AreEqual(string.Empty, definition.Category);
        }

        [Test]
        public void Category_IsNormalizedOnAssignment()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Greet", Category = "  Shop   Counter " };
            Assert.AreEqual("Shop Counter", definition.Category);
            Assert.AreEqual("Shop Counter", ConvaiActionDefinition.NormalizeCategory("  Shop   Counter "));
        }

        [Test]
        public void Clone_CarriesTheCategory()
        {
            var definition = new ConvaiActionDefinition { ActionName = "Greet", Category = "Counter" };
            ConvaiActionDefinition clone = definition.Clone();

            Assert.AreEqual("Counter", clone.Category);

            clone.Category = "Tour";
            Assert.AreEqual("Counter", definition.Category, "The clone must not share the original's category.");
        }

        // ── The invariant: organization only ───────────────────────────────────

        [Test]
        public void Category_IsNeverRenderedIntoTheWireTemplate()
        {
            var uncategorized = new ConvaiActionDefinition
            {
                ActionName = "Walk To Target",
                Description = "Walks to a target.",
                TargetRequirement = ConvaiActionTargetRequirement.Object
            };
            ConvaiActionDefinition categorized = uncategorized.Clone();
            categorized.Category = "Movement";

            Assert.AreEqual(uncategorized.ToActionConfigString(), categorized.ToActionConfigString());
            Assert.IsFalse(categorized.ToActionConfigString().Contains("Movement"));
        }
    }
}
