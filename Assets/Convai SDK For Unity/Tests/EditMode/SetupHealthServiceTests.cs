using System.Collections.Generic;
using System.Linq;
using Convai.Editor.ConfigurationWindow.Services;
using NUnit.Framework;

namespace Convai.Tests.EditMode
{
    public class SetupHealthServiceTests
    {
        [Test]
        public void BuildReport_ReturnsAllExpectedCheckIds()
        {
            SetupHealthReport report = SetupHealthService.BuildReport();
            HashSet<string> checkIds = report.Results.Select(result => result.Id).ToHashSet();

            Assert.That(checkIds.Contains("api-key"));
            Assert.That(checkIds.Contains("required-scene-components"));
            Assert.That(checkIds.Contains("characters"));
            Assert.That(checkIds.Contains("players"));
        }

    }
}
