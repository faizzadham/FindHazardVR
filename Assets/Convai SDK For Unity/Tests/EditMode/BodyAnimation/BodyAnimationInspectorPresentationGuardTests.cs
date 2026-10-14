using System.Collections.Generic;
using System.Reflection;
using Convai.Modules.BodyAnimation.Components;
using Convai.Modules.BodyAnimation.Data;
using NUnit.Framework;
using UnityEngine;

namespace Convai.Tests.EditMode.BodyAnimation
{
    /// <summary>
    ///     Presentation guards for the two Body Animation types whose inspectors curate their own
    ///     field list.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <c>EditorDesignSystemGuardTests.ConvaiInspectorTargets_CarryNoHeaderAttributes</c>
    ///         skips every editor that overrides <c>DrawBody</c>, on the reasoning that such an
    ///         editor curates which fields it draws and a <c>[Header]</c> on a field it never draws
    ///         is harmless. Both editors here override <c>DrawBody</c> **and** draw every field
    ///         below through <c>EditorGUILayout.PropertyField</c>, which runs the decorator drawer —
    ///         so the general guard's exemption is exactly wrong for them, and 26 headers rendered
    ///         as unstyled bold text inside the Convai section cards until this test existed.
    ///     </para>
    ///     <para>
    ///         Scoped deliberately to these two types rather than tightening the general guard:
    ///         other modules have their own header-bearing types whose inspectors have not been
    ///         audited, and turning this into a package-wide assertion would fail the build on work
    ///         that is not Body Animation's.
    ///     </para>
    /// </remarks>
    public sealed class BodyAnimationInspectorPresentationGuardTests
    {
        private static readonly System.Type[] CuratedInspectorTargets =
        {
            typeof(ConvaiBodyAnimationConfig),
            typeof(ConvaiNavMeshLocomotion)
        };

        [Test]
        public void CuratedInspectorTargets_CarryNoHeaderAttributes()
        {
            var offenders = new List<string>();

            foreach (System.Type target in CuratedInspectorTargets)
            {
                foreach (FieldInfo field in SerializedFieldsOf(target))
                {
                    var header = field.GetCustomAttribute<HeaderAttribute>();
                    if (header == null) continue;

                    offenders.Add($"{target.Name}.{field.Name} → \"{header.header}\"");
                }
            }

            Assert.IsEmpty(
                offenders,
                "These inspectors draw every serialized field explicitly, so a [Header] renders a " +
                "second, unstyled title inside the Convai section that already names the group. " +
                "Grouping belongs to the inspector's own section table.\n" +
                string.Join("\n", offenders));
        }

        [Test]
        public void CuratedInspectorTargets_EverySerializedFieldExplainsItself()
        {
            var offenders = new List<string>();

            foreach (System.Type target in CuratedInspectorTargets)
            {
                foreach (FieldInfo field in SerializedFieldsOf(target))
                {
                    // The label table can rename a field, but it reads the tooltip straight off the
                    // field — so a field with no [Tooltip] reaches the user with no explanation
                    // anywhere in the editor.
                    if (field.GetCustomAttribute<TooltipAttribute>() != null) continue;

                    offenders.Add($"{target.Name}.{field.Name}");
                }
            }

            Assert.IsEmpty(
                offenders,
                "Every serialized field on a curated Body Animation inspector needs a [Tooltip]: " +
                "BodyAnimationConfigLabels supplies the label but reads the tooltip from the field, " +
                "so a field without one is unexplained on every surface that draws it.\n" +
                string.Join("\n", offenders));
        }

        /// <summary>
        ///     Fields Unity would serialize and an inspector would therefore draw: private with
        ///     <c>[SerializeField]</c>, or public without <c>[System.NonSerialized]</c>.
        /// </summary>
        private static IEnumerable<FieldInfo> SerializedFieldsOf(System.Type type)
        {
            FieldInfo[] fields = type.GetFields(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (field.IsLiteral || field.IsInitOnly) continue;
                if (field.GetCustomAttribute<System.NonSerializedAttribute>() != null) continue;

                bool serialized = field.IsPublic || field.GetCustomAttribute<SerializeField>() != null;
                if (serialized) yield return field;
            }
        }
    }
}
