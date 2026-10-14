using System.Collections.Generic;
using Convai.Runtime.Actions;
using UnityEngine;
using UnityEngine.AI;

namespace Convai.Runtime.SceneMetadata
{
    public readonly struct ConvaiObjectMovementDestination
    {
        public readonly GameObject PhysicalObject;
        public readonly Vector3 Position;
        public readonly bool IsExactStandingPoint;
        public readonly Quaternion Facing;
        public ConvaiObjectMovementDestination(GameObject physicalObject, Vector3 position, bool exact, Quaternion facing)
        { PhysicalObject = physicalObject; Position = position; IsExactStandingPoint = exact; Facing = facing; }
    }

    /// <summary>Resolves physical group members and standing positions using complete NavMesh paths.</summary>
    public static class ConvaiObjectMovementResolver
    {
        private const float StandingPointToleranceMetres = .15f;
        private const float FallbackProjectionRadiusMetres = 2f;
        private const float PathTieToleranceMetres = .001f;

        public static bool TryResolve(GameObject target, NavMeshAgent agent, Transform authoredFallback,
            out ConvaiObjectMovementDestination destination)
        {
            destination = default;
            if (!target || !agent || !agent.enabled || !agent.isOnNavMesh) return false;
            var members = new List<GameObject>();
            ConvaiLogicalObjectGroup group = target.GetComponent<ConvaiLogicalObjectGroup>();
            if (group)
            {
                foreach (ConvaiObjectMetadata member in group.Members)
                    if (member && member.isActiveAndEnabled && !members.Contains(member.gameObject)) members.Add(member.gameObject);
            }
            else members.Add(target);
            var path = new NavMeshPath();
            float shortest = float.PositiveInfinity;
            ConvaiObjectMovementDestination chosen = default;
            var filter = new NavMeshQueryFilter { agentTypeID = agent.agentTypeID, areaMask = agent.areaMask };
            for (int area = 0; area < 32; area++) filter.SetAreaCost(area, agent.GetAreaCost(area));
            // Serialized member and point order supplies deterministic tie-breaking.
            foreach (GameObject member in members)
            {
                ConvaiObjectMovementPoints points = member.GetComponent<ConvaiObjectMovementPoints>();
                bool foundStandingPoint = false;
                if (points && points.isActiveAndEnabled)
                    foreach (ConvaiMovementPoint point in points.Points)
                    {
                        if (!point.Enabled) continue;
                        Vector3 position = points.WorldPosition(point);
                        if (!NavMesh.SamplePosition(position, out NavMeshHit hit, StandingPointToleranceMetres, filter) || Vector3.Distance(hit.position, position) > StandingPointToleranceMetres) continue;
                        if (!Reachable(hit.position, out float length)) continue;
                        foundStandingPoint = true;
                        Consider(member, position, true, length, point.WorldSpace ? point.Rotation : points.transform.rotation * point.Rotation);
                    }
                if (foundStandingPoint) continue;
                Transform fallback = member == target ? authoredFallback : member.GetComponent<ConvaiActionTarget>()?.InteractionPoint;
                Vector3 fallbackPosition = fallback ? fallback.position : member.transform.position;
                if (NavMesh.SamplePosition(fallbackPosition, out NavMeshHit fallbackHit, FallbackProjectionRadiusMetres, filter) && Reachable(fallbackHit.position, out float fallbackLength))
                    Consider(member, fallbackHit.position, false, fallbackLength, member.transform.rotation);
            }
            destination = chosen;
            return chosen.PhysicalObject;

            bool Reachable(Vector3 position, out float length)
            {
                length = 0;
                if (!NavMesh.CalculatePath(agent.transform.position, position, filter, path) || path.status != NavMeshPathStatus.PathComplete) return false;
                Vector3[] corners = path.corners;
                for (int i = 1; i < corners.Length; i++) length += Vector3.Distance(corners[i - 1], corners[i]);
                return true;
            }
            void Consider(GameObject member, Vector3 position, bool exact, float length, Quaternion facing)
            {
                if (length >= shortest - PathTieToleranceMetres) return;
                shortest = length;
                chosen = new ConvaiObjectMovementDestination(member, position, exact, facing);
            }
        }
    }
}
