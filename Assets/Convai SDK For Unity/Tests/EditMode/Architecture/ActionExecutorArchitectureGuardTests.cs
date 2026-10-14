using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Runtime.Actions;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the executor root-class rule: every shipped action executor
    ///     (non-abstract <see cref="MonoBehaviour" /> implementing <see cref="IConvaiActionExecutor" />
    ///     in <c>Convai.Runtime</c> or a <c>Convai.Modules.*</c> assembly) must derive from
    ///     <see cref="ConvaiActionExecutorBase" /> so the fallback Action Behavior inspector covers
    ///     it automatically. Samples and tests are exempt (they intentionally demonstrate/exercise
    ///     the raw interface).
    /// </summary>
    public sealed class ActionExecutorArchitectureGuardTests
    {
        [Test]
        [Category("Architecture")]
        public void ShippedActionExecutors_DeriveFromConvaiActionExecutorBase()
        {
            var violations = new List<string>();
            int count = 0;

            foreach (Type type in ShippedExecutorTypes())
            {
                count++;
                if (!typeof(ConvaiActionExecutorBase).IsAssignableFrom(type))
                    violations.Add(type.FullName);
            }

            Assert.Greater(count, 0, "Expected at least one shipped action executor type to be discovered.");
            Assert.IsEmpty(violations,
                "Every shipped action executor must derive from ConvaiActionExecutorBase (directly or via " +
                "ConvaiTargetedActionExecutor / ConvaiActionExecutor<T>) so the Convai Action Behavior " +
                "inspector covers it automatically:\n" + string.Join(Environment.NewLine, violations));
        }

        /// <summary>
        ///     Non-abstract MonoBehaviour executor types in shipped SDK assemblies (samples,
        ///     shared-sample, and test assemblies excluded). Shared with the tooltip guard.
        /// </summary>
        internal static IEnumerable<Type> ShippedExecutorTypes()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string name = assembly.GetName().Name;
                if (name == null || !(name == "Convai.Runtime" || name.StartsWith("Convai.Modules.", StringComparison.Ordinal)))
                    continue;

                foreach (Type type in SafeGetTypes(assembly))
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;

                    if (!typeof(MonoBehaviour).IsAssignableFrom(type))
                        continue;

                    if (!typeof(IConvaiActionExecutor).IsAssignableFrom(type))
                        continue;

                    yield return type;
                }
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var types = new List<Type>();
                foreach (Type type in ex.Types)
                {
                    if (type != null)
                        types.Add(type);
                }

                return types;
            }
        }
    }
}
