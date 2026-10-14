using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Convai.Editor.Diagnostics;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Positive public-surface guard for Setup Health (mirrors
    ///     <c>ActionsPublicSurfaceGuardTests</c> and <c>EmbodimentPublicSurfaceGuardTests</c>).
    /// </summary>
    /// <remarks>
    ///     Setup Health is the one part of <c>Convai.Editor.Diagnostics</c> that is deliberately
    ///     customer API: a studio writing its own character checks implements
    ///     <see cref="IConvaiSetupHealthProvider" /> and its findings appear in the Troubleshooter
    ///     beside Convai's own. Everything else in the namespace draws that report and is internal.
    ///     Adding a public type here is therefore a deliberate review gate — extend the approved list
    ///     in the same change, with XML docs and a CHANGELOG entry.
    /// </remarks>
    public sealed class SetupHealthPublicSurfaceGuardTests
    {
        private static readonly string[] ApprovedDiagnosticsTypes =
        {
            "Convai.Editor.Diagnostics.ConvaiSetupFinding",
            "Convai.Editor.Diagnostics.ConvaiSetupHealthRegistry",
            "Convai.Editor.Diagnostics.ConvaiSetupHealthResult",
            "Convai.Editor.Diagnostics.ConvaiSetupHealthSnapshot",
            "Convai.Editor.Diagnostics.IConvaiSetupHealthProvider"
        };

        [Test]
        [Category("Architecture")]
        public void DiagnosticsNamespace_PublicTypes_MatchApprovedList()
        {
            Assembly editor = typeof(ConvaiSetupHealthRegistry).Assembly;

            string[] actual = editor
                .GetTypes()
                .Where(type => type.IsPublic)
                .Where(type => type.Namespace == "Convai.Editor.Diagnostics")
                .Select(type => type.FullName)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            string[] approved = ApprovedDiagnosticsTypes
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray();

            CollectionAssert.AreEqual(
                approved, actual,
                "The public Setup Health surface changed. Everything public in a shipped package " +
                "assembly is customer API: add the type to the approved list here, document it, and " +
                "record it in CHANGELOG.md — or make it internal.");
        }

        /// <summary>
        ///     The window and its view models draw the report; they are not the contract a studio
        ///     writes against, and making one public would invite subclassing we do not support.
        /// </summary>
        [Test]
        [Category("Architecture")]
        public void TroubleshooterPresentation_StaysInternal()
        {
            Assembly editor = typeof(ConvaiSetupHealthRegistry).Assembly;
            var leaked = new List<string>();

            foreach (string typeName in new[]
                     {
                         "Convai.Editor.Diagnostics.ConvaiTroubleshooterWindow",
                         "Convai.Editor.Diagnostics.ConvaiTroubleshooterView",
                         "Convai.Editor.Diagnostics.ConvaiTroubleshooterModuleView",
                         "Convai.Editor.Diagnostics.ConvaiFindingView"
                     })
            {
                Type type = editor.GetType(typeName);
                Assert.IsNotNull(type, $"{typeName} not found; update this guard if it moved.");
                if (type.IsPublic)
                    leaked.Add(typeName);
            }

            CollectionAssert.IsEmpty(
                leaked,
                "The Troubleshooter's presentation types must stay internal. The supported way to put " +
                "a finding in front of a user is IConvaiSetupHealthProvider, not drawing the window.");
        }

        /// <summary>
        ///     A provider must reach the survey tools too, so the Troubleshooter and the MCP tools
        ///     cannot report different things about the same character.
        /// </summary>
        [Test]
        [Category("Architecture")]
        public void SetupHealthResult_ProjectsIntoTheSurveyShape()
        {
            MethodInfo project = typeof(ConvaiSetupHealthResult)
                .GetMethod(nameof(ConvaiSetupHealthResult.ToSurveyResult), BindingFlags.Public | BindingFlags.Instance);

            Assert.IsNotNull(
                project,
                "ConvaiSetupHealthResult.ToSurveyResult is how one check engine feeds both the editor " +
                "and the MCP tools. Removing it lets the two surfaces disagree.");
        }
    }
}
