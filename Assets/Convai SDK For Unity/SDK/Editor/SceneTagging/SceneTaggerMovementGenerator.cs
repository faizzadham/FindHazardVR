using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEngine;
using UnityEngine.AI;

namespace Convai.Editor.SceneTagging
{
    internal static class SceneTaggerMovementGenerator
    {
        internal static int Generate(GameObject target, bool regenerate)
        {
            ConvaiAutoTagOwnership owner = target.GetComponent<ConvaiAutoTagOwnership>();
            if (!owner) return 0;
            ConvaiObjectMovementPoints component = target.GetComponent<ConvaiObjectMovementPoints>();
            if (component && component.Points.Count > 0 && !regenerate) return 0;
            if (regenerate && (component == null || !component.Points.Any(p => IsOwned(owner, p)))) return 0;
            Physics.SyncTransforms();
            Renderer[] renderers = SceneTaggerDiscovery.CollectRenderers(target);
            if (renderers.Length == 0) return 0;
            Bounds bounds = renderers[0].bounds;
            foreach (Renderer renderer in renderers.Skip(1)) bounds.Encapsulate(renderer.bounds);
            float radius = .34f, height = 1.76f;
            if (NavMesh.GetSettingsCount() == 0) return 0;
            NavMeshBuildSettings nav = NavMesh.GetSettingsByIndex(0);
            radius = Mathf.Max(radius, nav.agentRadius);
            height = Mathf.Max(height, nav.agentHeight);
            var filter = new NavMeshQueryFilter { agentTypeID = nav.agentTypeID, areaMask = NavMesh.AllAreas };
            float standOff = Mathf.Clamp(radius + .45f + Mathf.Clamp(bounds.extents.magnitude * .2f, 0, .6f), Mathf.Max(.9f, radius + .25f), Mathf.Max(1.6f, radius + .85f));
            var choices = new List<(Vector3 point, float score, int index)>();
            for (int i = 0; i < 12; i++)
            {
                Vector3 direction = Quaternion.Euler(0, i * 30, 0) * Vector3.forward;
                Vector3 seed = new(bounds.center.x + (Mathf.Abs(direction.x) < .0001f ? 0 : Mathf.Sign(direction.x) * bounds.extents.x) + direction.x * standOff,
                    bounds.min.y, bounds.center.z + (Mathf.Abs(direction.z) < .0001f ? 0 : Mathf.Sign(direction.z) * bounds.extents.z) + direction.z * standOff);
                if (!NavMesh.SamplePosition(seed, out NavMeshHit hit, Mathf.Max(2.5f, height), filter)) continue;
                Vector3 error = hit.position - seed;
                if (new Vector2(error.x, error.z).magnitude > Mathf.Max(.6f, standOff * .5f) || Mathf.Abs(error.y) > Mathf.Max(1.8f, height)) continue;
                Vector3 nearest = bounds.ClosestPoint(hit.position);
                float gap = Vector2.Distance(new Vector2(nearest.x, nearest.z), new Vector2(hit.position.x, hit.position.z));
                if (gap < radius + .25f || Mathf.Abs(gap - standOff) > .2f) continue;
                Vector3 foot = hit.position + Vector3.up * .03f;
                if (Physics.CheckCapsule(foot + Vector3.up * radius, foot + Vector3.up * Mathf.Max(radius, height - radius), radius, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore)) continue;
                Vector3 eye = hit.position + Vector3.up * Mathf.Max(height * .9f, 1.6f);
                Vector3 side = Vector3.Cross(Vector3.up, bounds.center - eye).normalized;
                float lateralLimit = Mathf.Min(Mathf.Abs(side.x) > .0001f ? bounds.extents.x / Mathf.Abs(side.x) : float.PositiveInfinity,
                    Mathf.Abs(side.z) > .0001f ? bounds.extents.z / Mathf.Abs(side.z) : float.PositiveInfinity);
                Vector3 offset = float.IsFinite(lateralLimit) ? side * Mathf.Max(0, lateralLimit) * .3f : Vector3.zero;
                Vector3[] proposed = { bounds.center, bounds.center + Vector3.up * bounds.extents.y * .35f,
                    bounds.center - Vector3.up * bounds.extents.y * .35f, bounds.center + offset, bounds.center - offset };
                var samples = new List<Vector3>();
                foreach (Vector3 sample in proposed) if (!samples.Any(p => (p - sample).sqrMagnitude <= .0001f)) samples.Add(sample);
                int visible = samples.Count(p => Visible(eye, p, target.transform));
                if (samples.Count < 3 || !Visible(eye, bounds.center, target.transform) || visible < Mathf.Max(3, Mathf.CeilToInt(samples.Count * .6f))) continue;
                float projectionQuality = Mathf.Clamp01(1 - new Vector2(error.x, error.z).magnitude / Mathf.Max(.6f, standOff * .5f));
                Vector3 candidateDirection = Vector3.ProjectOnPlane(hit.position - bounds.center, Vector3.up).normalized;
                float score = visible / (float)samples.Count * 1000 + projectionQuality * 100 + Mathf.Abs(Vector3.Dot(candidateDirection, target.transform.right)) * 250;
                choices.Add((hit.position, score, i));
            }
            choices = choices.OrderByDescending(c => c.score).ThenBy(c => c.index).ToList();
            if (choices.Count == 0) return 0;
            var positions = new List<Vector3> { choices[0].point };
            Vector3 first = Vector3.ProjectOnPlane(choices[0].point - bounds.center, Vector3.up).normalized;
            Vector3 lateral = Vector3.ProjectOnPlane(target.transform.right, Vector3.up).normalized;
            var second = choices.Skip(1).Where(c => Vector3.Angle(first, Horizontal(c.point)) >= 40)
                .Where(c => Vector3.Dot(Horizontal(c.point), lateral) * Vector3.Dot(first, lateral) < 0 || Vector3.Dot(first, Horizontal(c.point)) >= 0)
                .OrderByDescending(c => Vector3.Dot(Horizontal(c.point), lateral) * Vector3.Dot(first, lateral) < 0).ThenByDescending(c => c.score).ToList();
            Vector3 Horizontal(Vector3 p) => Vector3.ProjectOnPlane(p - bounds.center, Vector3.up).normalized;
            if (second.Count > 0) positions.Add(second[0].point);
            if (!component) { component = Undo.AddComponent<ConvaiObjectMovementPoints>(target); SceneTaggerApplyService.Own(owner, component); }
            Undo.RecordObject(owner, "Record generated standing positions");
            Undo.RecordObject(component, "Generate standing positions");
            if (regenerate) component.Points.RemoveAll(p => IsOwned(owner, p));
            owner.MovementPoints.RemoveAll(record => !component.Points.Any(p => p.Id == record.Id));
            foreach (Vector3 position in positions)
            {
                var point = new ConvaiMovementPoint { Position = target.transform.InverseTransformPoint(position), Rotation = Quaternion.Inverse(target.transform.rotation) * Quaternion.LookRotation(new Vector3(bounds.center.x - position.x, 0, bounds.center.z - position.z)) };
                component.Points.Add(point);
                owner.MovementPoints.Add(new ConvaiAutoTagOwnership.OwnedMovementPoint { Id = point.Id, Json = JsonUtility.ToJson(point) });
            }
            SceneTaggerApplyService.Dirty(owner);
            SceneTaggerApplyService.Dirty(component);
            var record = owner.Components.FirstOrDefault(item => item.Component == component);
            if (record != null && string.IsNullOrEmpty(record.Json)) record.Json = SceneTaggerApplyService.Snapshot(component);
            return positions.Count;
        }
        private static bool Visible(Vector3 eye, Vector3 sample, Transform target)
        {
            Vector3 direction = sample - eye;
            float distance = direction.magnitude;
            if (distance < .01f) return false;
            if (Physics.SphereCast(eye, .125f, direction / distance, out RaycastHit wide, Mathf.Max(0, distance - .15f), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) && !wide.transform.IsChildOf(target)) return false;
            return !Physics.Raycast(eye, direction / distance, out RaycastHit narrow, distance, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore) || narrow.transform.IsChildOf(target);
        }
        private static bool IsOwned(ConvaiAutoTagOwnership owner, ConvaiMovementPoint point) => owner.MovementPoints.Any(p => p.Id == point.Id && p.Json == JsonUtility.ToJson(point));
        internal static void Clear(ConvaiAutoTagOwnership owner)
        {
            ConvaiObjectMovementPoints points = owner.GetComponent<ConvaiObjectMovementPoints>();
            if (!points) return;
            Undo.RecordObject(points, "Clear generated standing positions");
            Undo.RecordObject(owner, "Clear standing position ownership");
            points.Points.RemoveAll(p => IsOwned(owner, p));
            owner.MovementPoints.Clear();
            SceneTaggerApplyService.Dirty(points);
            SceneTaggerApplyService.Dirty(owner);
        }
    }
}
