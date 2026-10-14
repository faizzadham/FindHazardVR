using System.Collections.Generic;
using System.Linq;
using Convai.Editor.ConfigurationWindow.Components;
using NUnit.Framework;

namespace Convai.Tests.EditMode
{
    public class ConfigurationSectionRegistryTests
    {
        [Test]
        public void AllSections_HaveUniqueIds()
        {
            IReadOnlyList<ConfigurationSectionDescriptor> allSections = ConfigurationSectionRegistry.AllSections;

            int distinctCount = allSections.Select(section => section.SectionId).Distinct().Count();
            Assert.AreEqual(allSections.Count, distinctCount, "Section IDs must be unique.");
        }

        [Test]
        public void EnabledSections_ContainCoreDashboardSections()
        {
            IReadOnlyList<ConfigurationSectionDescriptor> enabledSections =
                ConfigurationSectionRegistry.GetEnabledSections();
            HashSet<string> sectionIds = enabledSections.Select(section => section.SectionId).ToHashSet();

            Assert.That(sectionIds.Contains("welcome"));
            Assert.That(sectionIds.Contains("account"));
            Assert.That(sectionIds.Contains("settings"));
            Assert.That(sectionIds.Contains("ai-coding"));
            Assert.That(sectionIds.Contains("config-assets"));
            Assert.That(sectionIds.Contains("ltm"));
            Assert.That(sectionIds.Contains("contact-us"));
        }

        [Test]
        public void CoreDashboardSections_FollowConvaiMenuOrder()
        {
            string[] expectedOrder =
            {
                "welcome",
                "account",
                "settings",
                "ai-coding",
                "config-assets",
                "ltm",
                "contact-us"
            };

            HashSet<string> coreSectionIds = expectedOrder.ToHashSet();
            string[] actualOrder = ConfigurationSectionRegistry.GetEnabledSections()
                .Where(section => coreSectionIds.Contains(section.SectionId))
                .Select(section => section.SectionId)
                .ToArray();

            CollectionAssert.AreEqual(expectedOrder, actualOrder);
        }

        [Test]
        public void FeatureGatedSections_RespectCompileDefines()
        {
            IReadOnlyList<ConfigurationSectionDescriptor> enabledSections =
                ConfigurationSectionRegistry.GetEnabledSections();
            HashSet<string> sectionIds = enabledSections.Select(section => section.SectionId).ToHashSet();

#if CONVAI_ENABLE_UPDATES_SECTION
            Assert.That(sectionIds.Contains("updates"));
#else
            Assert.That(!sectionIds.Contains("updates"));
#endif

#if CONVAI_ENABLE_SERVER_ANIMATION
            Assert.That(sectionIds.Contains("server-animation"));
#else
            Assert.That(!sectionIds.Contains("server-animation"));
#endif
        }
    }
}
