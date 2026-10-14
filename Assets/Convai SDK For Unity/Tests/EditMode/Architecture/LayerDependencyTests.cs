using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Tests that verify the 5-layer Clean Architecture dependency rules are enforced.
    ///     Architecture layers (inner to outer):
    ///     1. Domain (no dependencies)
    ///     2. Shared (depends only on Domain)
    ///     3. Infrastructure (depends on Domain, Shared)
    ///     4. Application (depends on Domain, Shared)
    ///     5. Runtime (can depend on all layers)
    ///     Key rule: Dependencies flow inward. Inner layers never depend on outer layers.
    /// </summary>
    public class LayerDependencyTests
    {
        #region Runtime Layer Tests (Allowed to reference all layers)

        [Test]
        [Category("Architecture")]
        public void Runtime_CanReferenceAllLayers()
        {
            Assembly runtime = FindAssemblyByName(RuntimeAssembly);
            if (runtime == null)
            {
                Assert.Fail(
                    $"Required assembly '{RuntimeAssembly}' is not loaded; runtime-layer architecture guard cannot run.");
            }

            HashSet<string> references = GetReferencedAssemblyNames(runtime);

            Assert.IsTrue(
                references.Contains(DomainAssembly) ||
                references.Contains(ApplicationAssembly) ||
                references.Contains(InfrastructureAssembly),
                "Runtime layer should reference at least one inner layer.");
        }

        #endregion

        #region Assembly Name Constants

        private const string DomainAssembly = "Convai.Domain";
        private const string SharedAssembly = "Convai.Shared";
        private const string InfrastructureAssembly = "Convai.Infrastructure";
        private const string ApplicationAssembly = "Convai.Application";
        private const string RuntimeAssembly = "Convai.Runtime";

        private const string InfrastructureNetworkingAssembly = "Convai.Infrastructure.Networking";
        private const string InfrastructureProtocolAssembly = "Convai.Infrastructure.Protocol";
        private const string RuntimeBehaviorsAssembly = "Convai.Runtime.Behaviors";

        #endregion

        #region Forbidden Dependencies

        private static readonly string[] DomainForbiddenReferences =
        {
            SharedAssembly, InfrastructureAssembly, InfrastructureNetworkingAssembly,
            InfrastructureProtocolAssembly, ApplicationAssembly, RuntimeAssembly, RuntimeBehaviorsAssembly
        };

        private static readonly string[] SharedForbiddenReferences =
        {
            InfrastructureAssembly, InfrastructureNetworkingAssembly, InfrastructureProtocolAssembly,
            ApplicationAssembly, RuntimeAssembly, RuntimeBehaviorsAssembly
        };

        private static readonly string[] InfrastructureForbiddenReferences =
        {
            ApplicationAssembly, RuntimeAssembly, RuntimeBehaviorsAssembly
        };

        private static readonly string[] ApplicationForbiddenReferences =
        {
            InfrastructureAssembly, InfrastructureNetworkingAssembly, InfrastructureProtocolAssembly,
            RuntimeAssembly, RuntimeBehaviorsAssembly
        };

        #endregion

        #region Helper Methods

        private static Assembly FindAssemblyByName(string assemblyName)
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == assemblyName);
        }

        private static HashSet<string> GetReferencedAssemblyNames(Assembly assembly)
        {
            if (assembly == null)
                return new HashSet<string>();

            return assembly.GetReferencedAssemblies()
                .Select(a => a.Name)
                .ToHashSet();
        }

        private static void AssertNoForbiddenReferences(
            string layerName,
            Assembly assembly,
            string[] forbiddenReferences)
        {
            if (assembly == null)
            {
                Assert.Fail(
                    $"Required assembly '{layerName}' is not loaded; layer-dependency architecture guard cannot run.");
            }

            HashSet<string> actualReferences = GetReferencedAssemblyNames(assembly);
            List<string> violations = forbiddenReferences
                .Where(forbidden => actualReferences.Contains(forbidden))
                .ToList();

            Assert.IsEmpty(violations,
                $"Layer '{layerName}' has forbidden dependencies: [{string.Join(", ", violations)}]. " +
                "This violates Clean Architecture principles.");
        }

        #endregion

        #region Domain Layer Tests

        [Test]
        [Category("Architecture")]
        public void Domain_HasNoConvaiDependencies()
        {
            Assembly domain = FindAssemblyByName(DomainAssembly);
            AssertNoForbiddenReferences(DomainAssembly, domain, DomainForbiddenReferences);
        }

        [Test]
        [Category("Architecture")]
        public void Domain_HasNoUnityEngineReferences()
        {
            Assembly domain = FindAssemblyByName(DomainAssembly);
            if (domain == null)
            {
                Assert.Fail(
                    $"Required assembly '{DomainAssembly}' is not loaded; Unity-reference architecture guard cannot run.");
            }

            HashSet<string> references = GetReferencedAssemblyNames(domain);

            List<string> unityReferences = references
                .Where(r => r.StartsWith("UnityEngine", StringComparison.Ordinal))
                .ToList();

            Assert.IsEmpty(unityReferences,
                $"Domain layer should not reference Unity assemblies. Found: [{string.Join(", ", unityReferences)}]");
        }

        #endregion

        #region Cross-Layer Validation

        [Test]
        [Category("Architecture")]
        public void DependencyFlow_IsUnidirectional()
        {
            Assembly domain = FindAssemblyByName(DomainAssembly);
            if (domain != null)
            {
                AssertNoForbiddenReferences("Domain", domain,
                    new[] { SharedAssembly, InfrastructureAssembly, ApplicationAssembly, RuntimeAssembly });
            }

            Assembly shared = FindAssemblyByName(SharedAssembly);
            if (shared != null)
            {
                AssertNoForbiddenReferences("Shared", shared,
                    new[] { InfrastructureAssembly, ApplicationAssembly, RuntimeAssembly });
            }

            Assembly infrastructure = FindAssemblyByName(InfrastructureAssembly);
            if (infrastructure != null)
            {
                AssertNoForbiddenReferences("Infrastructure", infrastructure,
                    new[] { ApplicationAssembly, RuntimeAssembly });
            }

            Assembly application = FindAssemblyByName(ApplicationAssembly);
            if (application != null) AssertNoForbiddenReferences("Application", application, new[] { RuntimeAssembly });
        }

        #endregion
    }
}
