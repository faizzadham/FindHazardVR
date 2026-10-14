using System;
using System.Collections.Generic;
using UnityEngine;
using Convai.Runtime.Actions;

namespace Convai.Runtime.SceneMetadata
{
    /// <summary>Authored or generated standing positions; independent of capture cameras and gaze aim.</summary>
    [DisallowMultipleComponent, AddComponentMenu("Convai/Object Movement Points")]
    public sealed class ConvaiObjectMovementPoints : MonoBehaviour
    {
        [ConvaiInspectorSection("Standing Positions"), Tooltip("Alternative positions for approaching this physical object. Runtime movement picks the shortest complete reachable path.")]
        public List<ConvaiMovementPoint> Points = new();
        public Vector3 WorldPosition(ConvaiMovementPoint point) => point.WorldSpace ? point.Position : transform.TransformPoint(point.Position);
    }

    [Serializable]
    public sealed class ConvaiMovementPoint
    {
        public string Id = Guid.NewGuid().ToString("N");
        public bool Enabled = true;
        public string Name = "";
        public bool WorldSpace;
        public Vector3 Position;
        public Quaternion Rotation = Quaternion.identity;
    }
}
