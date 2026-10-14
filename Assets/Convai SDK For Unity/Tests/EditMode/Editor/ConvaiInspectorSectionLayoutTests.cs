using System.Collections.Generic;
using Convai.Editor.Inspectors.Framework;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Covers the Convai inspector base's section-model builder:
    ///     unattributed fields land in the default "Settings" section, sections and fields honor
    ///     their declared orders with declaration order as the tiebreak, and only a section whose
    ///     fields are all marked advanced renders as an advanced (collapsed) section — which always
    ///     sorts after the visible sections.
    /// </summary>
    [TestFixture]
    public class ConvaiInspectorSectionLayoutTests
    {
        private static ConvaiInspectorFieldMetadata Field(
            string name, string section = null, int order = 0, bool advanced = false) =>
            new(name, section, order, advanced);

        [Test]
        public void UnattributedFields_LandInDefaultSection_InDeclarationOrder()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_alpha"),
                Field("_beta"),
                Field("_gamma")
            });

            Assert.AreEqual(1, sections.Count);
            Assert.AreEqual(ConvaiInspectorSectionLayout.DefaultSectionName, sections[0].Name);
            Assert.IsFalse(sections[0].Advanced);
            CollectionAssert.AreEqual(new[] { "_alpha", "_beta", "_gamma" }, sections[0].FieldNames);
        }

        [Test]
        public void BlankSectionName_RoutesToDefaultSection()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_alpha", "   "),
                Field("_beta", null)
            });

            Assert.AreEqual(1, sections.Count);
            Assert.AreEqual(ConvaiInspectorSectionLayout.DefaultSectionName, sections[0].Name);
        }

        [Test]
        public void Sections_OrderBySmallestFieldOrder_ThenFirstDeclaration()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_late", "Later", 5),
                Field("_early", "Earlier", 1),
                Field("_tied", "TiedWithEarlier", 1)
            });

            Assert.AreEqual(3, sections.Count);
            Assert.AreEqual("Earlier", sections[0].Name);
            Assert.AreEqual("TiedWithEarlier", sections[1].Name, "Equal min orders keep declaration order.");
            Assert.AreEqual("Later", sections[2].Name);
        }

        [Test]
        public void FieldsWithinASection_OrderByOrder_ThenDeclaration()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_third", "Timing", 2),
                Field("_first", "Timing", 1),
                Field("_second", "Timing", 1)
            });

            Assert.AreEqual(1, sections.Count);
            CollectionAssert.AreEqual(new[] { "_first", "_second", "_third" }, sections[0].FieldNames);
        }

        [Test]
        public void AllAdvancedSection_IsAdvanced_AndSortsAfterVisibleSections()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_tuning", "Fine Tuning", -10, advanced: true),
                Field("_speed")
            });

            Assert.AreEqual(2, sections.Count);
            Assert.AreEqual(ConvaiInspectorSectionLayout.DefaultSectionName, sections[0].Name);
            Assert.AreEqual("Fine Tuning", sections[1].Name,
                "An advanced section sorts after visible sections even with a smaller order.");
            Assert.IsTrue(sections[1].Advanced);
        }

        [Test]
        public void MixedAdvancedSection_StaysVisible()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_shown", "Timing", 0, advanced: false),
                Field("_expert", "Timing", 1, advanced: true)
            });

            Assert.AreEqual(1, sections.Count);
            Assert.IsFalse(sections[0].Advanced,
                "A section with any non-advanced field must stay visible so unmarked fields are never hidden.");
        }

        [Test]
        public void SectionNames_AreTrimmed()
        {
            List<ConvaiInspectorSectionModel> sections = ConvaiInspectorSectionLayout.Build(new[]
            {
                Field("_alpha", "  Timing  "),
                Field("_beta", "Timing")
            });

            Assert.AreEqual(1, sections.Count);
            Assert.AreEqual("Timing", sections[0].Name);
            Assert.AreEqual(2, sections[0].FieldNames.Count);
        }

        [Test]
        public void NullOrEmptyInput_YieldsNoSections()
        {
            Assert.AreEqual(0, ConvaiInspectorSectionLayout.Build(null).Count);
            Assert.AreEqual(0, ConvaiInspectorSectionLayout.Build(new ConvaiInspectorFieldMetadata[0]).Count);
            Assert.AreEqual(0, ConvaiInspectorSectionLayout.Build(new[] { Field(null), Field("  ") }).Count,
                "Blank field names are skipped entirely.");
        }
    }
}
