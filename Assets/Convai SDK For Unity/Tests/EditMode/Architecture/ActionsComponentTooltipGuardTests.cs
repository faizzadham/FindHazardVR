using System;
using System.Collections.Generic;
using System.Reflection;
using Convai.Editor.Actions;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.Architecture
{
    /// <summary>
    ///     Completes the tooltip rule beyond executors
    ///     (<see cref="ExecutorTooltipGuardTests" /> covers those): every serialized field Unity
    ///     shows for every user-addable Actions component — the exact type list the authoring
    ///     parity map (<see cref="ConvaiActionsAuthoringCoverage" />) guards, using the same
    ///     <see cref="ConvaiActionsAuthoringCoverage.GetAuthorableSerializedFields" /> definition of
    ///     "a serialized field" — must carry a non-empty <see cref="TooltipAttribute" />. The two
    ///     guards share one consequence: a new Actions field cannot ship without hover help.
    /// </summary>
    public sealed class ActionsComponentTooltipGuardTests
    {
        [Test]
        [Category("Architecture")]
        public void EveryAuthorableActionsField_HasANonEmptyTooltip()
        {
            var violations = new List<string>();
            int fieldCount = 0;

            foreach (Type type in ConvaiActionsAuthoringCoverage.Map.Keys)
            {
                foreach (FieldInfo field in ConvaiActionsAuthoringCoverage.GetAuthorableSerializedFields(type))
                {
                    fieldCount++;
                    var tooltip = field.GetCustomAttribute<TooltipAttribute>();
                    if (tooltip == null || string.IsNullOrWhiteSpace(tooltip.tooltip))
                        violations.Add($"{type.FullName}.{field.Name}");
                }
            }

            Assert.Greater(fieldCount, 0, "Expected the authoring coverage map to expose serialized fields.");
            Assert.IsEmpty(violations,
                "Every serialized field on a user-addable Actions component needs a beginner-readable [Tooltip]:\n" +
                string.Join(Environment.NewLine, violations));
        }
    }
}
