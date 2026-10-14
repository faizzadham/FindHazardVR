using System.Collections.Generic;
using Convai.Editor.AI;
using Convai.Editor.Diagnostics;
using Convai.Runtime.Components;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     Guards the Setup Health contract — the one finding model every Convai capability projects
    ///     into, and the rules that keep a finding worth showing a beginner.
    /// </summary>
    /// <remarks>
    ///     Two of these are a ratchet rather than ordinary coverage. <b>Actionability</b>: an error a
    ///     user cannot act on is a defect in the finding, so a provider that reports one fails here
    ///     rather than in someone's editor. <b>Agreement</b>: a provider must be a projection of its
    ///     module's own check engine and never a second opinion, so registering one must make it
    ///     visible to the survey tools in the same act.
    /// </remarks>
    [TestFixture]
    public class ConvaiSetupHealthTests
    {
        private const string ProbeModuleId = "convai.tests.setuphealth";

        private readonly List<Object> _cleanup = new();
        private GameObject _characterObject;

        [SetUp]
        public void SetUp()
        {
            _characterObject = new GameObject("Setup Health Test Character");
            _cleanup.Add(_characterObject);
            _characterObject.AddComponent<ConvaiCharacter>();
            ConvaiSetupHealthRegistry.Invalidate();
        }

        [TearDown]
        public void TearDown()
        {
            foreach (Object created in _cleanup)
            {
                if (created != null)
                    Object.DestroyImmediate(created);
            }

            _cleanup.Clear();
            ConvaiSetupHealthRegistry.Invalidate();
        }

        [Test]
        public void EveryFinding_CarriesAnIdTitleAndMessage()
        {
            ConvaiSetupHealthSnapshot snapshot = ConvaiSetupHealthRegistry.Refresh(_characterObject);

            foreach (ConvaiSetupFinding finding in AllFindings(snapshot))
            {
                Assert.That(
                    finding.Id, Is.Not.Null.And.Not.Empty,
                    "A finding with no id cannot be named by a fix, a test or a support thread.");
                Assert.That(finding.Title, Is.Not.Null.And.Not.Empty, $"Finding '{finding.Id}' has no title.");
                Assert.That(finding.Message, Is.Not.Null.And.Not.Empty, $"Finding '{finding.Id}' has no message.");
            }
        }

        [Test]
        public void FindingIds_AreUniqueWithinOneReport()
        {
            _characterObject.AddComponent<ConvaiActionConfigSource>();
            ConvaiSetupHealthSnapshot snapshot = ConvaiSetupHealthRegistry.Refresh(_characterObject);

            var seen = new HashSet<string>();
            foreach (ConvaiSetupFinding finding in AllFindings(snapshot))
            {
                Assert.That(
                    seen.Add(finding.Id), Is.True,
                    $"Duplicate finding id '{finding.Id}'. Findings that repeat per action or per target " +
                    "must carry the subject in their id.");
            }
        }

        /// <summary>
        ///     The actionability ratchet, applied to capabilities that report through
        ///     <see cref="IConvaiSetupHealthProvider" />.
        /// </summary>
        /// <remarks>
        ///     A capability that still reports through the older survey interface has no way to carry a
        ///     fix or a target — that is precisely what moving it to
        ///     <see cref="IConvaiSetupHealthProvider" /> adds. Holding those to this rule would fail the
        ///     suite for work that has not been done rather than for a defect, so the rule binds exactly
        ///     the capabilities that can satisfy it, and binds every one of them.
        /// </remarks>
        [Test]
        public void EveryErrorFromAProviderCapability_CanBeFixedOrLocated()
        {
            ConvaiSetupHealthSnapshot snapshot = ConvaiSetupHealthRegistry.Refresh(_characterObject);

            var byProvider = new HashSet<string>();
            foreach (IConvaiSetupHealthProvider provider in ConvaiSetupHealthRegistry.All)
                byProvider.Add(provider.ModuleId);

            for (int i = 0; i < snapshot.Results.Count; i++)
            {
                ConvaiSetupHealthResult result = snapshot.Results[i];
                if (!byProvider.Contains(result.ModuleId))
                    continue;

                IReadOnlyList<ConvaiSetupFinding> findings = result.Findings;
                for (int f = 0; f < findings.Count; f++)
                {
                    ConvaiSetupFinding finding = findings[f];
                    if (finding.Severity != ConvaiModuleFindingSeverity.Error)
                        continue;

                    Assert.That(
                        finding.IsFixable || finding.CanLocate, Is.True,
                        $"Error '{finding.Id}' offers neither a fix nor somewhere to look. An error a user " +
                        "cannot act on is a defect in the finding, not a finding.");
                }
            }
        }

        [Test]
        public void Snapshot_ForNullCharacter_IsEmptyRatherThanThrowing()
        {
            ConvaiSetupHealthSnapshot snapshot = ConvaiSetupHealthRegistry.Get(null);

            Assert.That(snapshot.Results.Count, Is.EqualTo(0));
            Assert.That(snapshot.IsHealthy, Is.True);
            Assert.That(snapshot.IssueCount, Is.EqualTo(0));
        }

        [Test]
        public void RegisteredProvider_IsReportedForTheCharacterItClaims()
        {
            RegisterProbe(Error("probe.broken", fix: () => { }));

            ConvaiSetupHealthSnapshot snapshot = ConvaiSetupHealthRegistry.Refresh(Probe());
            ConvaiSetupHealthResult result = snapshot.Find(ProbeModuleId);

            Assert.That(result, Is.Not.Null, "A registered provider must report into the shared registry.");
            Assert.That(result.ErrorCount, Is.EqualTo(1));
            Assert.That(snapshot.IsHealthy, Is.False);
        }

        [Test]
        public void RegisteredProvider_IsAlsoVisibleToTheSurveyTools()
        {
            // The editor and the MCP tools must read one list. Registering a provider registers it
            // with the survey registry too, so InspectScene and ValidateSetup cannot fall behind.
            RegisterProbe(Error("probe.broken", fix: () => { }));
            GameObject probe = Probe();

            ConvaiModuleSurveyResult survey = default;
            var found = false;
            foreach (IConvaiModuleSurveyor surveyor in ConvaiModuleSurveyRegistry.All)
            {
                if (surveyor.ModuleId != ProbeModuleId)
                    continue;

                survey = surveyor.Survey(probe);
                found = true;
                break;
            }

            Assert.That(found, Is.True, "A provider must appear to the survey tools as well.");
            Assert.That(survey.DisplayName, Is.EqualTo("Setup Health Probe"));
            Assert.That(survey.Findings.Count, Is.EqualTo(1), "The survey projection must carry the findings.");
        }

        [Test]
        public void IssueCount_CountsErrorsAndWarningsButNotInfoOrOk()
        {
            RegisterProbe(
                Error("probe.error", fix: () => { }),
                new ConvaiSetupFinding("probe.warning", ConvaiModuleFindingSeverity.Warning, "Warning", "Body."),
                new ConvaiSetupFinding("probe.info", ConvaiModuleFindingSeverity.Info, "Info", "Body."),
                new ConvaiSetupFinding("probe.ok", ConvaiModuleFindingSeverity.Ok, "Ok", "Body."));

            ConvaiSetupHealthResult result = ConvaiSetupHealthRegistry.Refresh(Probe()).Find(ProbeModuleId);

            Assert.That(result.ErrorCount, Is.EqualTo(1));
            Assert.That(result.WarningCount, Is.EqualTo(1));
            Assert.That(
                result.IssueCount, Is.EqualTo(2),
                "Info and Ok are explanation, not work, and must never inflate the \"N to fix\" count.");
        }

        /// <summary>
        ///     An <see cref="ConvaiModuleFindingSeverity.Info" /> finding must end up somewhere a user
        ///     can read it.
        /// </summary>
        /// <remarks>
        ///     It is not work, so it is correctly absent from the issue list — which left it drawn in
        ///     no list at all, and a module's own text unreachable. It belongs with the passes.
        /// </remarks>
        [Test]
        public void InfoFindings_AreCarriedWithThePassesRatherThanDropped()
        {
            RegisterProbe(
                new ConvaiSetupFinding("probe.info", ConvaiModuleFindingSeverity.Info, "Info", "Body."),
                new ConvaiSetupFinding("probe.ok", ConvaiModuleFindingSeverity.Ok, "Ok", "Body."));

            ConvaiSetupHealthResult result = ConvaiSetupHealthRegistry.Refresh(Probe()).Find(ProbeModuleId);

            var notWork = new List<string>();
            foreach (ConvaiSetupFinding finding in result.Findings)
            {
                if (!finding.IsIssue)
                    notWork.Add(finding.Id);
            }

            Assert.That(
                notWork, Is.EquivalentTo(new[] { "probe.info", "probe.ok" }),
                "The Troubleshooter's \"Checked And Fine\" list is built from every finding that is not " +
                "an issue. Matching only Ok here is what made Info findings invisible.");
        }

        [Test]
        public void RegisteringTwiceWithOneModuleId_ReplacesRatherThanDuplicates()
        {
            // A domain reload re-runs every InitializeOnLoad registration, so this is the normal path,
            // not an edge case: a module that appeared twice would double every count it reports.
            RegisterProbe(Error("probe.first", fix: () => { }));
            RegisterProbe(Error("probe.second", fix: () => { }));

            var matches = 0;
            foreach (IConvaiSetupHealthProvider provider in ConvaiSetupHealthRegistry.All)
            {
                if (provider.ModuleId == ProbeModuleId)
                    matches++;
            }

            Assert.That(matches, Is.EqualTo(1), "Registering the same module id twice must replace, not append.");

            ConvaiSetupHealthResult result = ConvaiSetupHealthRegistry.Refresh(Probe()).Find(ProbeModuleId);
            Assert.That(result.Findings.Count, Is.EqualTo(1));
            Assert.That(result.Findings[0].Id, Is.EqualTo("probe.second"), "The later registration must win.");
        }

        [Test]
        public void Refresh_SeesAChangeTheCacheWouldHaveHidden()
        {
            RegisterProbe();
            GameObject probe = Probe();

            ConvaiSetupHealthSnapshot before = ConvaiSetupHealthRegistry.Get(probe);
            Assert.That(before.Find(ProbeModuleId).IssueCount, Is.EqualTo(0));

            RegisterProbe(Error("probe.appeared", fix: () => { }));

            // Within the cache window: without Refresh this would still report the earlier answer.
            ConvaiSetupHealthSnapshot after = ConvaiSetupHealthRegistry.Refresh(probe);

            Assert.That(
                after.Find(ProbeModuleId).IssueCount, Is.EqualTo(1),
                "Refresh must rebuild rather than serve the cached snapshot.");
        }

        private static ConvaiSetupFinding Error(string id, System.Action fix) =>
            new(id, ConvaiModuleFindingSeverity.Error, "Probe", "Something the probe reports.",
                "Fix It", fix);

        private static IEnumerable<ConvaiSetupFinding> AllFindings(ConvaiSetupHealthSnapshot snapshot)
        {
            for (int i = 0; i < snapshot.Results.Count; i++)
            {
                IReadOnlyList<ConvaiSetupFinding> findings = snapshot.Results[i].Findings;
                for (int f = 0; f < findings.Count; f++)
                    yield return findings[f];
            }
        }

        private GameObject Probe()
        {
            if (_characterObject.GetComponent<SetupHealthProbeMarker>() == null)
                _characterObject.AddComponent<SetupHealthProbeMarker>();

            return _characterObject;
        }

        private void RegisterProbe(params ConvaiSetupFinding[] findings)
        {
            Probe();
            ConvaiSetupHealthRegistry.Register(new ProbeProvider(findings));
            ConvaiSetupHealthRegistry.Invalidate();
        }

        /// <summary>
        ///     Marks the one character this fixture's provider will speak about.
        /// </summary>
        /// <remarks>
        ///     The registry has no unregister, so a provider survives the fixture for the rest of the
        ///     editor session. Claiming only a marked object keeps it inert for every other test rather
        ///     than adding a phantom capability to their characters.
        /// </remarks>
        private sealed class SetupHealthProbeMarker : MonoBehaviour
        {
        }

        private sealed class ProbeProvider : IConvaiSetupHealthProvider
        {
            private readonly ConvaiSetupFinding[] _findings;

            internal ProbeProvider(ConvaiSetupFinding[] findings) =>
                _findings = findings ?? System.Array.Empty<ConvaiSetupFinding>();

            public string ModuleId => ProbeModuleId;

            public string DisplayName => "Setup Health Probe";

            public int Order => 999;

            public bool AppliesTo(GameObject characterRoot) =>
                characterRoot != null && characterRoot.GetComponent<SetupHealthProbeMarker>() != null;

            public ConvaiSetupHealthResult Inspect(GameObject characterRoot)
            {
                var readiness = _findings.Length == 0
                    ? ConvaiCapabilityReadiness.Working
                    : ConvaiCapabilityReadiness.Blocked;

                return new ConvaiSetupHealthResult(
                    ModuleId, DisplayName, readiness, "What the probe reports.", string.Empty, _findings, Order);
            }
        }
    }
}
