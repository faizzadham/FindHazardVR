using System;
using System.Collections.Generic;
using UnityEngine;

namespace Convai.Runtime.SceneMetadata
{
    /// <summary>Tracks exactly which values the Scene Auto Tagger may update or remove.</summary>
    [DisallowMultipleComponent, AddComponentMenu("")]
    public sealed class ConvaiAutoTagOwnership : MonoBehaviour
    {
        public int Schema = 1;
        [HideInInspector] public string ObjectId = Guid.NewGuid().ToString("N");
        public ConvaiObjectMetadata Metadata;
        public string Name, Description;
        public string LogicalGroupId;
        public bool IncludeInMetadata, Spatial, Gaze;
        public bool CreatedMetadata;
        public List<OwnedComponent> Components = new();
        public List<OwnedMovementPoint> MovementPoints = new();

        public bool MetadataUnchanged => Metadata && Metadata.ObjectName == Name && Metadata.ObjectDescription == Description &&
            Metadata.IncludeInMetadata == IncludeInMetadata && Metadata.IncludeSpatialContext == Spatial && Metadata.GazeEnabled == Gaze;

        public void RecordMetadata()
        {
            Name = Metadata.ObjectName;
            Description = Metadata.ObjectDescription;
            IncludeInMetadata = Metadata.IncludeInMetadata;
            Spatial = Metadata.IncludeSpatialContext;
            Gaze = Metadata.GazeEnabled;
        }
        [Serializable] public sealed class OwnedComponent { public Component Component; public string Json; }
        [Serializable] public sealed class OwnedMovementPoint { public string Id, Json; }
    }
}
