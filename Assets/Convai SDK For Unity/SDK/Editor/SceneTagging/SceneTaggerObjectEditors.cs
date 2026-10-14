using Convai.Editor.Inspectors.Framework;
using Convai.Editor.UI;
using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEngine;

namespace Convai.Editor.SceneTagging
{
    [CustomEditor(typeof(ConvaiLogicalObjectGroup))]
    internal sealed class ConvaiLogicalObjectGroupEditor : ConvaiInspectorEditor
    {
        protected override string Title => "Logical Object Group";
        protected override string Subtitle => "Shared Scene Knowledge";
        protected override string Purpose => "Gives several physical objects one shared name and description. Gaze and movement still resolve a physical member.";
    }

    [CustomEditor(typeof(ConvaiObjectMovementPoints))]
    internal sealed class ConvaiObjectMovementPointsEditor : ConvaiInspectorEditor
    {
        protected override string Title => "Object Standing Positions";
        protected override string Subtitle => "Scene Navigation";
        protected override string Purpose => "Defines alternative places to stand while facing this object. Drag the handles in the Scene view to adjust a point; your edits survive regeneration.";
        private void OnSceneGUI()
        {
            var component = (ConvaiObjectMovementPoints)target;
            Color previous = Handles.color;
            try
            {
                Handles.color = ConvaiEditorTheme.Accent;
                foreach (ConvaiMovementPoint point in component.Points)
                {
                    if (!point.Enabled) continue;
                    Vector3 position = component.WorldPosition(point);
                    Quaternion rotation = point.WorldSpace ? point.Rotation : component.transform.rotation * point.Rotation;
                    EditorGUI.BeginChangeCheck();
                    Vector3 moved = Handles.PositionHandle(position, rotation);
                    Quaternion turned = Handles.RotationHandle(rotation, moved);
                    Handles.Label(moved, string.IsNullOrWhiteSpace(point.Name) ? "Standing position" : point.Name);
                    if (!EditorGUI.EndChangeCheck()) continue;
                    Undo.RecordObject(component, "Edit object standing position");
                    point.Position = point.WorldSpace ? moved : component.transform.InverseTransformPoint(moved);
                    point.Rotation = point.WorldSpace ? turned : Quaternion.Inverse(component.transform.rotation) * turned;
                    SceneTaggerApplyService.Dirty(component);
                }
            }
            finally { Handles.color = previous; }
        }
    }
}
