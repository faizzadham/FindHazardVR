using System;
using System.Collections.Generic;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Guards the action-behavior tooltip rule: every <c>[SerializeField]</c> on a shipped
    ///     action executor (including fields inherited from the shared executor bases) must carry a
    ///     non-empty <see cref="TooltipAttribute" />. The Convai Action Behavior inspector renders
    ///     tooltips on every field row, so a missing tooltip is an authoring bug, not a cosmetic gap.
    /// </summary>
    public sealed class ExecutorTooltipGuardTests
    {
        [Test]
        [Category("Architecture")]
        public void EverySerializedExecutorField_HasANonEmptyTooltip()
        {
            var violations = new List<string>();
            int fieldCount = 0;

            foreach (Type type in ActionExecutorArchitectureGuardTests.ShippedExecutorTypes())
            {
                for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                {
                    FieldInfo[] fields = current.GetFields(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);

                    foreach (FieldInfo field in fields)
                    {
                        if (field.GetCustomAttribute<SerializeField>() == null)
                            continue;

                        fieldCount++;
                        var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                        if (tooltip == null || string.IsNullOrWhiteSpace(tooltip.tooltip))
                            violations.Add($"{current.FullName}.{field.Name} (via {type.Name})");
                    }
                }
            }

            Assert.Greater(fieldCount, 0, "Expected at least one serialized executor field to be discovered.");
            Assert.IsEmpty(violations,
                "Every [SerializeField] on a shipped action executor needs a beginner-readable [Tooltip]:\n" +
                string.Join(Environment.NewLine, violations));
        }
    }
}
