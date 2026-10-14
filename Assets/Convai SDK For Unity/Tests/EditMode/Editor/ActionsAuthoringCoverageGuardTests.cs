using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Editor.Actions;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using Convai.Tests.EditMode.Architecture;
using NUnit.Framework;

namespace Convai.Tests.EditMode.Editor
{
    /// <summary>
    ///     The authoring-parity guard ("never again"): every serialized field of every
    ///     user-addable Actions component must be claimed by exactly one authoring surface in
    ///     <see cref="ConvaiActionsAuthoringCoverage" /> — a window mode, the component's Convai
    ///     inspector, or an explicit Hidden-with-reason entry. A new serialized field without a
    ///     claim fails here with a message naming the field and the choices, turning "some features
    ///     are not shown in the UI" from a recurring audit into a guard test. Executors are exempt:
    ///     the second fixture asserts they all inherit the fallback Action Behavior inspector via
    ///     <see cref="ConvaiActionExecutorBase" /> instead.
    /// </summary>
    /// <remarks>
    ///     The guarded set is <see cref="ConvaiActionsAuthoringCoverage.GetGuardedSerializedFields" />
    ///     — every serialized field, <see cref="UnityEngine.HideInInspector" /> ones included.
    ///     Hiding a field is an authoring decision, not an exemption from one: it is exactly the
    ///     Hidden claim, which owes a reason. The narrower "what Unity draws" set belongs to the
    ///     tooltip guard, where a field nobody sees genuinely has nothing to explain.
    /// </remarks>
    [TestFixture]
    public sealed class ActionsAuthoringCoverageGuardTests
    {
        /// <summary>
        ///     The user-addable Actions components (and the Action Set asset) under guard, plus the
        ///     nested <see cref="ConvaiActionDefinition" /> authoring type — its serialized fields
        ///     are the action list's actual editing surface, so a new field there (for example the
        ///     availability flag) must claim an authoring surface exactly like a component field.
        /// </summary>
        private static IEnumerable<Type> GuardedTypes()
        {
            yield return typeof(ConvaiActionDispatcher);
            yield return typeof(ConvaiActionConfigSource);
            yield return typeof(ConvaiActionTarget);
            yield return typeof(ConvaiActionFeedbackRelay);
            yield return typeof(ConvaiActionDebugProbe);
            yield return typeof(ConvaiActionSet);
            yield return typeof(ConvaiActionDefinition);
            yield return typeof(ConvaiActionTargetGroup);

            // The Scene Knowledge entry types: their serialized fields are the Known Objects /
            // Known Characters editing surface, and one of them (the scene object link) shipped
            // with no authoring UI at all because nothing guarded them.
            yield return typeof(Convai.Shared.Actions.ConvaiActionObjectDefinition);
            yield return typeof(Convai.Shared.Actions.ConvaiActionCharacterDefinition);
        }

        [Test]
        public void EverySerializedField_IsClaimedByAnAuthoringSurface()
        {
            var violations = new List<string>();
            int fieldCount = 0;

            foreach (Type type in GuardedTypes())
            {
                Assert.IsTrue(
                    ConvaiActionsAuthoringCoverage.Map.TryGetValue(
                        type, out IReadOnlyDictionary<string, ConvaiActionsAuthoringEntry> claims),
                    $"'{type.Name}' has no entry in ConvaiActionsAuthoringCoverage.Map at all. " +
                    "Add a per-field claim table for it.");

                foreach (FieldInfo field in ConvaiActionsAuthoringCoverage.GetGuardedSerializedFields(type))
                {
                    fieldCount++;
                    if (!claims.ContainsKey(field.Name))
                    {
                        violations.Add(
                            $"{type.Name}.{field.Name} is serialized but claimed by no authoring surface. " +
                            "Either give it authoring UI and claim it in ConvaiActionsAuthoringCoverage " +
                            "(WindowActions / WindowSceneKnowledge / WindowCharacterSettings / " +
                            "ComponentInspector), or claim it as Hidden with a non-empty reason.");
                    }
                }
            }

            Assert.Greater(fieldCount, 0, "Expected the guarded Actions components to expose serialized fields.");
            Assert.IsEmpty(violations, string.Join(Environment.NewLine, violations));
        }

        [Test]
        public void CoverageMap_HasNoStaleEntries()
        {
            var stale = new List<string>();
            foreach (Type type in GuardedTypes())
            {
                if (!ConvaiActionsAuthoringCoverage.Map.TryGetValue(
                        type, out IReadOnlyDictionary<string, ConvaiActionsAuthoringEntry> claims))
                    continue;

                var actualFieldNames = new HashSet<string>(StringComparer.Ordinal);
                foreach (FieldInfo field in ConvaiActionsAuthoringCoverage.GetGuardedSerializedFields(type))
                    actualFieldNames.Add(field.Name);

                foreach (KeyValuePair<string, ConvaiActionsAuthoringEntry> claim in claims)
                {
                    if (!actualFieldNames.Contains(claim.Key))
                    {
                        stale.Add(
                            $"{type.Name}.{claim.Key} is claimed in ConvaiActionsAuthoringCoverage but no such " +
                            "serialized field exists (renamed or removed?). Update the coverage map.");
                    }
                }
            }

            Assert.IsEmpty(stale, string.Join(Environment.NewLine, stale));
        }

        [Test]
        public void CoverageMap_ContainsOnlyGuardedTypes()
        {
            var guarded = new HashSet<Type>(GuardedTypes());
            foreach (Type mapped in ConvaiActionsAuthoringCoverage.Map.Keys)
            {
                Assert.IsTrue(
                    guarded.Contains(mapped),
                    $"'{mapped.Name}' appears in ConvaiActionsAuthoringCoverage.Map but not in the guard test's " +
                    "GuardedTypes() list — add it there so its fields are actually verified.");
            }
        }

        [Test]
        public void HiddenClaims_CarryANonEmptyReason()
        {
            foreach (KeyValuePair<Type, IReadOnlyDictionary<string, ConvaiActionsAuthoringEntry>> typeClaims
                     in ConvaiActionsAuthoringCoverage.Map)
            {
                foreach (KeyValuePair<string, ConvaiActionsAuthoringEntry> claim in typeClaims.Value)
                {
                    if (claim.Value.Surface != ConvaiActionsAuthoringSurface.Hidden)
                        continue;

                    Assert.IsFalse(
                        string.IsNullOrWhiteSpace(claim.Value.HiddenReason),
                        $"{typeClaims.Key.Name}.{claim.Key} is claimed as Hidden without a reason. A Hidden " +
                        "claim must say why the field deliberately has no authoring UI.");
                }
            }
        }

        /// <summary>
        ///     The explicit executor exemption: every shipped Action Behavior derives from
        ///     <see cref="ConvaiActionExecutorBase" /> (so the fallback inspector auto-covers all of
        ///     its serialized fields) and therefore must NOT appear in the coverage map.
        /// </summary>
        [Test]
        public void Executors_AreExempt_BecauseTheFallbackInspectorCoversThem()
        {
            int count = 0;
            foreach (Type executorType in ActionExecutorArchitectureGuardTests.ShippedExecutorTypes())
            {
                count++;
                Assert.IsTrue(
                    typeof(ConvaiActionExecutorBase).IsAssignableFrom(executorType),
                    $"'{executorType.FullName}' does not derive from ConvaiActionExecutorBase, so the fallback " +
                    "Action Behavior inspector cannot auto-cover its serialized fields — the coverage exemption " +
                    "does not hold for it.");

                Assert.IsFalse(
                    ConvaiActionsAuthoringCoverage.Map.ContainsKey(executorType),
                    $"'{executorType.Name}' is an Action Behavior and is auto-covered by the fallback inspector; " +
                    "it must not appear in ConvaiActionsAuthoringCoverage.Map.");
            }

            Assert.Greater(count, 0, "Expected at least one shipped Action Behavior type to be discovered.");
        }
    }
}
