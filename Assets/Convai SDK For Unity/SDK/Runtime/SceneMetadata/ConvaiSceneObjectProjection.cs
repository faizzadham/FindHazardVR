using System.Collections.Generic;
using Convai.Runtime.Behaviors;
using Convai.Runtime.Actions;
using Convai.Runtime.Components;
using UnityEngine;

namespace Convai.Runtime.SceneMetadata
{
    /// <summary>Resolves shared logical identity while retaining physical geometry and lifecycle.</summary>
    public static class ConvaiSceneObjectProjection
    {
        private static readonly List<Renderer> Renderers = new();
        public static bool Include(ConvaiObjectMetadata metadata)
        {
            if (!metadata || ConvaiLogicalObjectGroup.ForMember(metadata)) return false;
            ConvaiLogicalObjectGroup group = ActiveGroup(metadata.gameObject);
            return !group || group.HasActiveMembers;
        }
        /// <summary>
        ///     A disabled group has already left the member registry, so treating it as absent here keeps
        ///     the root and its members from being published twice with different identities.
        /// </summary>
        private static ConvaiLogicalObjectGroup ActiveGroup(GameObject owner)
        {
            ConvaiLogicalObjectGroup group = owner.GetComponent<ConvaiLogicalObjectGroup>();
            return group && group.isActiveAndEnabled ? group : null;
        }
        public static ConvaiObjectMetadata Logical(ConvaiObjectMetadata metadata)
        {
            ConvaiLogicalObjectGroup group = ConvaiLogicalObjectGroup.ForMember(metadata);
            return group ? group.Metadata : metadata;
        }
        public static GameObject Physical(GameObject logical, Vector3 observer)
        {
            if (!logical) return null;
            ConvaiLogicalObjectGroup group = ActiveGroup(logical);
            return group ? group.ClosestMember(observer)?.gameObject : logical;
        }
        /// <summary>Uses the physical member's authored interaction point when present.</summary>
        public static Transform InteractionPoint(GameObject target)
        {
            if (!target) return null;
            Transform point = target.GetComponent<ConvaiActionTarget>()?.InteractionPoint;
            return point ? point : target.transform;
        }

        /// <summary>Resolves a logical target and its matching point, preserving explicit points for ordinary targets.</summary>
        public static Transform ResolveInteractionPoint(GameObject logical, Vector3 observer, Transform authoredPoint,
            out GameObject physical)
        {
            physical = Physical(logical, observer);
            if (!physical) return null;
            return physical == logical && authoredPoint ? authoredPoint : InteractionPoint(physical);
        }

        public static Vector3 AimPoint(GameObject target)
        {
            if (!target) return Vector3.zero;
            Renderers.Clear();
            target.GetComponentsInChildren(Renderers);
            bool found = false;
            Bounds bounds = default;
            foreach (Renderer renderer in Renderers)
            {
                if (!renderer.enabled) continue;
                if (!found) { bounds = renderer.bounds; found = true; } else bounds.Encapsulate(renderer.bounds);
            }
            Renderers.Clear();
            return found ? bounds.center : target.transform.position;
        }
        public static void NotifyChanged()
        {
            ConvaiManager manager = ConvaiManager.ActiveManager;
            if (!manager || !manager.TryGetAgentRegistry(out IAgentRegistry registry) || registry.Characters == null) return;
            foreach (IConvaiCharacterAgent agent in registry.Characters)
                if (agent is ConvaiCharacter character) { character.MarkPendingSceneMetadataSync(); character.MarkPendingActionConfigSync(); }
        }
    }
}
