using System;
using Convai.Domain.Logging;
using Convai.Runtime.Logging;
using Convai.Runtime.Utilities;
using UnityEditor;
using UnityEngine;
using Glyphs = Convai.Editor.UI.ConvaiEditorGlyphs;
using Object = UnityEngine.Object;

namespace Convai.Editor.PropertyDrawers
{
    /// <summary>
    ///     Property drawer for the RequireInterfaceAttribute.
    ///     Filters the object picker to only show components implementing the required interface.
    /// </summary>
    [CustomPropertyDrawer(typeof(RequireInterfaceAttribute))]
    internal sealed class RequireInterfaceDrawer : PropertyDrawer
    {
        /// <inheritdoc />
        public override void OnGUI(Rect position, SerializedProperty property, GUIContent label)
        {
            var requireInterface = (RequireInterfaceAttribute)attribute;
            Type interfaceType = requireInterface.InterfaceType;

            if (property.propertyType != SerializedPropertyType.ObjectReference)
            {
                EditorGUI.LabelField(position, label.text, "RequireInterface only works with object references.");
                return;
            }

            var labelWithTooltip = new GUIContent(
                label.text,
                $"Requires: {interfaceType.Name}"
            );

            EditorGUI.BeginChangeCheck();

            Object currentValue = property.objectReferenceValue;

            // The warning mark gets its own space at the end of the row. Drawn over the field, it
            // sat on top of the object picker button.
            bool showWarning = currentValue != null && !interfaceType.IsInstanceOfType(currentValue);
            Rect fieldRect = position;
            if (showWarning)
                fieldRect.width = Mathf.Max(0f, fieldRect.width - WarningWidth);

            Object newValue = EditorGUI.ObjectField(
                fieldRect,
                labelWithTooltip,
                currentValue,
                typeof(MonoBehaviour),
                true
            );

            if (EditorGUI.EndChangeCheck())
            {
                if (newValue == null)
                    property.objectReferenceValue = null;
                else if (newValue is GameObject go)
                {
                    Component validComponent = FindComponentWithInterface(go, interfaceType);
                    if (validComponent != null)
                        property.objectReferenceValue = validComponent;
                    else
                    {
                        ConvaiLogger.Warning(
                            $"GameObject '{go.name}' has no component implementing {interfaceType.Name}.",
                            LogCategory.Editor);
                    }
                }
                else if (newValue is Component component)
                {
                    if (interfaceType.IsInstanceOfType(component))
                        property.objectReferenceValue = component;
                    else
                    {
                        Component validComponent = FindComponentWithInterface(component.gameObject, interfaceType);
                        if (validComponent != null)
                            property.objectReferenceValue = validComponent;
                        else
                        {
                            ConvaiLogger.Warning(
                                $"Component '{component.GetType().Name}' does not implement {interfaceType.Name}.",
                                LogCategory.Editor);
                        }
                    }
                }
            }

            if (showWarning)
            {
                var warningRect = new Rect(position.xMax - WarningWidth, position.y, WarningWidth, position.height);
                GUI.Label(warningRect, new GUIContent(Glyphs.Status.Warn, $"Does not implement {interfaceType.Name}"));
            }
        }

        /// <summary>Width kept at the end of the row for the does-not-implement mark.</summary>
        private const float WarningWidth = 20f;

        /// <summary>
        ///     Finds a component on the GameObject that implements the specified interface.
        /// </summary>
        private Component FindComponentWithInterface(GameObject go, Type interfaceType)
        {
            if (go == null) return null;

            Component[] components = go.GetComponents<Component>();
            foreach (Component component in components)
            {
                if (component != null && interfaceType.IsInstanceOfType(component))
                    return component;
            }

            return null;
        }
    }
}
