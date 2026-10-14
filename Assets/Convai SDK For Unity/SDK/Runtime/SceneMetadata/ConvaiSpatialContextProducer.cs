using Convai.Shared.Compatibility;
using System.Collections.Generic;
using System.Globalization;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Components;
using Convai.Runtime.DynamicContext;
using UnityEngine;

namespace Convai.Runtime.SceneMetadata
{
    /// <summary>Owned spatial facts driven by the existing world-object polling clock.</summary>
    internal sealed class ConvaiSpatialContextProducer
    {
        private const float ContextRadiusMetres = 30f;
        private const float EyeHeightMetres = 1.6f;
        private const float MotionThresholdMetres = .02f;

        private readonly Dictionary<ConvaiCharacter, HashSet<string>> _owned = new();
        private readonly Dictionary<long, Vector3> _previousPositions = new();
        internal void Poll(IAgentRegistry registry)
        {
            ConvaiLogicalObjectGroup.PollMembership();
            var present = new HashSet<ConvaiCharacter>();
            ConvaiObjectMetadata[] metadata = ConvaiMetadataRegistry.GetValidMetadata();
            if (registry?.Characters != null)
            foreach (IConvaiCharacterAgent agent in registry.Characters)
            {
                if (agent is not ConvaiCharacter character || !character.isActiveAndEnabled) continue;
                present.Add(character);
                if (!_owned.TryGetValue(character, out HashSet<string> previous)) previous = new HashSet<string>();
                var current = new HashSet<string>();
                foreach (ConvaiObjectMetadata item in metadata)
                {
                    if (!item || !item.isActiveAndEnabled || !item.IncludeSpatialContext) continue;
                    GameObject physical = ConvaiSceneObjectProjection.Physical(item.gameObject, character.transform.position);
                    if (!physical) continue;
                    Vector3 point = ConvaiSceneObjectProjection.AimPoint(physical);
                    float distance = Vector3.Distance(character.transform.position, point);
                    if (distance > ContextRadiusMetres) continue;
                    Vector3 local = character.transform.InverseTransformPoint(point);
                    string direction = Mathf.Abs(local.x) > Mathf.Abs(local.z) ? local.x > 0 ? "right" : "left" : local.z >= 0 ? "ahead" : "behind";
                    Vector3 eye = character.transform.position + Vector3.up * EyeHeightMetres;
                    bool visible = !Physics.Linecast(eye, point, out RaycastHit hit, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || hit.transform.IsChildOf(physical.transform);
                    bool moving = _previousPositions.TryGetValue(ConvaiObjectId.Of(physical), out Vector3 prior) && Vector3.Distance(prior, point) > MotionThresholdMetres;
                    string key = "scene-object." + ConvaiObjectId.Of(item) + ".spatial";
                    string value = item.ObjectName + ": " + distance.ToString("F1", CultureInfo.InvariantCulture) + " m " + direction + "; " + (visible ? "visible" : "occluded") + "; " + (moving ? "moving" : "stationary");
                    character.DynamicContext.SetState(key, value, ConvaiRespondMode.Silent);
                    current.Add(key);
                }
                foreach (string key in previous) if (!current.Contains(key)) character.DynamicContext.RemoveState(key);
                _owned[character] = current;
            }
            var departed = new List<ConvaiCharacter>();
            foreach (var pair in _owned)
            {
                if (present.Contains(pair.Key)) continue;
                if (pair.Key) foreach (string key in pair.Value) pair.Key.DynamicContext.RemoveState(key);
                departed.Add(pair.Key);
            }
            foreach (ConvaiCharacter character in departed) _owned.Remove(character);
            _previousPositions.Clear();
            foreach (ConvaiObjectMetadata item in ConvaiMetadataRegistry.GetAllMetadata())
                if (item) _previousPositions[ConvaiObjectId.Of(item.gameObject)] = ConvaiSceneObjectProjection.AimPoint(item.gameObject);
        }
        internal void Clear()
        {
            foreach (var pair in _owned) if (pair.Key) foreach (string key in pair.Value) pair.Key.DynamicContext.RemoveState(key);
            _owned.Clear();
            _previousPositions.Clear();
        }
    }
}
