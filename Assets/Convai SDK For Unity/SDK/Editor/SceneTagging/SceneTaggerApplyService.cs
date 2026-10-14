using Convai.Editor.Compatibility;
using System;
using System.Collections.Generic;
using System.Linq;
using Convai.Runtime.Actions;
using Convai.Runtime.SceneMetadata;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace Convai.Editor.SceneTagging
{
    internal static class SceneTaggerApplyService
    {
        // Optional modules register here; the base Editor assembly has no dependency on Gaze/BodyAnimation.
        internal static event Action<GameObject, ConvaiAutoTagOwnership, SceneTaggerSettings> ConfigureCompanions;

        internal static string Apply(List<List<SceneTaggerRow>> groups, SceneTaggerSettings settings)
        {
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Apply Convai Scene Tags");
            int applied = 0, conflicts = 0, points = 0;
            // One scene traversal per Apply, independent of the number of duplicate groups.
            var groupLookup = groups.SelectMany(group => group).Where(row => row.IsCurrent).Select(row => row.Target.scene).Distinct()
                .SelectMany(scene => scene.GetRootGameObjects()).SelectMany(root => root.GetComponentsInChildren<ConvaiLogicalObjectGroup>(true))
                .Select(g => (group: g, id: g.GetComponent<ConvaiAutoTagOwnership>()?.LogicalGroupId))
                .Where(item => !string.IsNullOrEmpty(item.id))
                .GroupBy(item => (item.group.gameObject.scene, item.id))
                .ToDictionary(group => group.Key, group => group.First().group);
            try
            {
                foreach (List<SceneTaggerRow> group in groups)
                {
                    var approved = new List<SceneTaggerRow>();
                    foreach (SceneTaggerRow row in group.Where(r => r.CanApply))
                    {
                        if (SceneTaggerDiscovery.Validate(row)) approved.Add(row);
                        else { row.Error = "Object changed since capture. Discover or add it again before applying."; conflicts++; }
                    }
                    if (approved.Count == 0) continue;
                    SceneTaggerRow proposal = approved[0];
                    if (approved.Any(r => r.Name != proposal.Name || r.Description != proposal.Description))
                    {
                        foreach (SceneTaggerRow row in approved) row.Error = "Grouped objects have different approved text. Give them one shared description or keep them separate.";
                        conflicts++; continue;
                    }
                    string groupId = proposal.LogicalGroupId;
                    var scene = proposal.Target.scene;
                    int beforeGroup = applied;
                    ConvaiLogicalObjectGroup existing = null;
                    if (!string.IsNullOrEmpty(groupId)) groupLookup.TryGetValue((scene, groupId), out existing);
                    if (existing)
                    {
                        var previousOwner = existing.GetComponent<ConvaiAutoTagOwnership>();
                        if (!previousOwner.MetadataUnchanged || !ComponentsUnchanged(previousOwner) || existing.Members.Any(member => member &&
                            (!member.TryGetComponent<ConvaiAutoTagOwnership>(out var memberOwner) || !memberOwner.MetadataUnchanged || !ComponentsUnchanged(memberOwner))))
                        {
                            foreach (var row in group.Where(r => r.CanApply)) row.Error = "The logical group was edited manually. Its members have been preserved.";
                            conflicts++; continue;
                        }
                    }
                    var members = existing ? existing.Members.Where(m => m).ToList() : new List<ConvaiObjectMetadata>();
                    foreach (SceneTaggerRow row in group)
                    {
                        if (row.Status == SceneTaggerStatus.Applied && row.IsCurrent)
                        {
                            var appliedOwner = row.Target.GetComponent<ConvaiAutoTagOwnership>();
                            if (appliedOwner && appliedOwner.MetadataUnchanged && ComponentsUnchanged(appliedOwner)) members.Add(appliedOwner.Metadata);
                            continue;
                        }
                        if (!approved.Contains(row)) continue;
                        ConvaiObjectMetadata metadata = row.Target.GetComponent<ConvaiObjectMetadata>();
                        ConvaiAutoTagOwnership ownership = row.Target.GetComponent<ConvaiAutoTagOwnership>();
                        if (metadata && (!ownership || ownership.Metadata != metadata || !ownership.MetadataUnchanged))
                        { row.Error = "Manually authored metadata is preserved. Review the existing World Object component."; conflicts++; continue; }
                        if (ownership && !ComponentsUnchanged(ownership))
                        { row.Error = "A generated companion was edited manually. It has been preserved."; conflicts++; continue; }
                        ownership = ownership ? ownership : Undo.AddComponent<ConvaiAutoTagOwnership>(row.Target);
                        Undo.RecordObject(ownership, "Record generated scene tag");
                        if (!metadata) { metadata = Undo.AddComponent<ConvaiObjectMetadata>(row.Target); ownership.CreatedMetadata = true; }
                        Undo.RecordObject(metadata, "Set object description");
                        metadata.ObjectName = row.Name;
                        metadata.ObjectDescription = row.Description;
                        metadata.IncludeInMetadata = true;
                        metadata.IncludeSpatialContext = settings.Spatial;
                        metadata.GazeEnabled = settings.Gaze;
                        ownership.Metadata = metadata;
                        ownership.LogicalGroupId = row.LogicalGroupId;
                        ownership.RecordMetadata();
                        if (!settings.Actions) RemoveOwnedTarget(row.Target, ownership);
                        else if (group.Count == 1) ConfigureTarget(row.Target, metadata, ownership);
                        ConfigureCompanions?.Invoke(row.Target, ownership, settings);
                        if (settings.GenerateMovementPoints) points += SceneTaggerMovementGenerator.Generate(row.Target, false);
                        RecordOwnedComponents(ownership);
                        Dirty(metadata);
                        Dirty(ownership);
                        members.Add(metadata);
                        row.Status = SceneTaggerStatus.Applied;
                        row.Error = "";
                        applied++;
                    }
                    if (applied == beforeGroup) continue;
                    members = members.Distinct().ToList();
                    if (members.Count == 1 && settings.Actions && !existing)
                    {
                        var owner = members[0].GetComponent<ConvaiAutoTagOwnership>();
                        ConfigureTarget(members[0].gameObject, members[0], owner);
                        RecordOwnedComponents(owner); Dirty(owner);
                    }
                    if (members.Count > 1 || members.Count > 0 && existing)
                    {
                        GameObject root;
                        ConvaiLogicalObjectGroup logical;
                        if (existing)
                        {
                            var previousOwner = existing.GetComponent<ConvaiAutoTagOwnership>();
                            if (!previousOwner || !previousOwner.MetadataUnchanged || !ComponentsUnchanged(previousOwner))
                            { conflicts++; continue; }
                            logical = existing; root = existing.gameObject;
                        }
                        else
                        {
                            root = new GameObject("Convai group: " + proposal.Name);
                            SceneManager.MoveGameObjectToScene(root, members[0].gameObject.scene);
                            Undo.RegisterCreatedObjectUndo(root, "Create logical object group");
                            logical = Undo.AddComponent<ConvaiLogicalObjectGroup>(root);
                        }
                        Undo.RecordObject(logical, "Set logical members");
                        logical.Members = logical.Members.Where(m => m).Concat(members).Distinct().ToList();
                        ConvaiObjectMetadata metadata = logical.Metadata;
                        Undo.RecordObject(metadata, "Set group description");
                        metadata.ObjectName = proposal.Name;
                        metadata.ObjectDescription = proposal.Description;
                        metadata.IncludeSpatialContext = settings.Spatial;
                        metadata.GazeEnabled = settings.Gaze;
                        ConvaiAutoTagOwnership owner = root.GetComponent<ConvaiAutoTagOwnership>() ?? Undo.AddComponent<ConvaiAutoTagOwnership>(root);
                        Undo.RecordObject(owner, "Record group ownership");
                        owner.Metadata = metadata;
                        owner.LogicalGroupId = groupId;
                        groupLookup[(logical.gameObject.scene, groupId)] = logical;
                        owner.CreatedMetadata = true;
                        owner.RecordMetadata();
                        Own(owner, logical);
                        if (settings.Actions) ConfigureTarget(root, metadata, owner);
                        else RemoveOwnedTarget(root, owner);
                        foreach (var member in members)
                        {
                            var memberOwner = member.GetComponent<ConvaiAutoTagOwnership>();
                            if (!memberOwner) continue;
                            Undo.RecordObject(memberOwner, "Record logical membership");
                            memberOwner.LogicalGroupId = groupId;
                            RemoveOwnedTarget(member.gameObject, memberOwner);
                            Dirty(memberOwner);
                        }
                        RecordOwnedComponents(owner);
                        logical.NotifyMembershipChanged();
                        Dirty(logical); Dirty(metadata); Dirty(owner);
                    }
                }
            }
            finally { Undo.CollapseUndoOperations(undo); }
            return $"Applied {applied} objects; {conflicts} conflicts preserved; generated {points} standing points. Scenes are ready to save.";
        }

        private static void RemoveOwnedTarget(GameObject target, ConvaiAutoTagOwnership owner)
        {
            var action = target.GetComponent<ConvaiActionTarget>();
            var record = owner.Components.FirstOrDefault(c => c.Component == action);
            if (!action || record == null || !string.IsNullOrEmpty(record.Json) && Snapshot(action) != record.Json) return;
            Undo.RecordObject(owner, "Remove generated action target");
            owner.Components.Remove(record);
            Undo.DestroyObjectImmediate(action);
        }

        private static void ConfigureTarget(GameObject target, ConvaiObjectMetadata metadata, ConvaiAutoTagOwnership owner)
        {
            ConvaiActionTarget action = target.GetComponent<ConvaiActionTarget>();
            if (action && !owner.Components.Any(c => c.Component == action)) return;
            if (!action) { action = Undo.AddComponent<ConvaiActionTarget>(target); Own(owner, action); }
            Undo.RecordObject(action, "Connect object metadata");
            action.MetadataSource = metadata;
            Dirty(action);
        }
        internal static void Own(ConvaiAutoTagOwnership owner, Component component)
        {
            if (!owner.Components.Any(c => c.Component == component)) owner.Components.Add(new ConvaiAutoTagOwnership.OwnedComponent { Component = component });
        }
        // Editor JSON contains process-local instance IDs. Canonicalize references so a
        // saved/reopened scene does not falsely turn untouched generated fields into conflicts.
        internal static string Snapshot(Component component)
        {
            var json = JObject.Parse(EditorJsonUtility.ToJson(component));
            foreach (string field in new[] { "m_ObjectHideFlags", "m_CorrespondingSourceObject", "m_PrefabInstance", "m_PrefabAsset", "m_GameObject", "m_Script", "m_EditorHideFlags" }) json.Remove(field);
            foreach (JObject reference in json.Descendants().OfType<JObject>().Where(value => value.Count == 1 && value["instanceID"] != null).ToArray())
            {
                ConvaiEditorObjectId.TryResolve((long)reference["instanceID"], out Object value);
                reference.RemoveAll();
                reference["reference"] = ReferenceIdentity(value);
            }
            return json.ToString(Formatting.None);
        }
        private static string ReferenceIdentity(Object value)
        {
            if (!value) return "null";
            if (EditorUtility.IsPersistent(value)) return GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
            GameObject gameObject = value is Component component ? component.gameObject : value as GameObject;
            ConvaiAutoTagOwnership owner = gameObject ? gameObject.GetComponent<ConvaiAutoTagOwnership>() : null;
            if (!owner) return GlobalObjectId.GetGlobalObjectIdSlow(value).ToString();
            string identity = "generated:" + owner.ObjectId;
            if (value is Component typed)
                identity += ":" + typed.GetType().FullName + ":" + Array.IndexOf(gameObject.GetComponents(typed.GetType()), typed);
            return identity;
        }
        internal static bool ComponentsUnchanged(ConvaiAutoTagOwnership owner) => owner.Components.All(c => c.Component && (c.Component is ConvaiObjectMovementPoints || string.IsNullOrEmpty(c.Json) || Snapshot(c.Component) == c.Json));
        internal static void RecordOwnedComponents(ConvaiAutoTagOwnership owner)
        {
            foreach (var item in owner.Components) if (item.Component && (!(item.Component is ConvaiObjectMovementPoints) || string.IsNullOrEmpty(item.Json))) item.Json = Snapshot(item.Component);
        }
        internal static void Dirty(Component component)
        {
            EditorUtility.SetDirty(component);
            if (PrefabUtility.IsPartOfPrefabInstance(component)) PrefabUtility.RecordPrefabInstancePropertyModifications(component);
            EditorSceneManager.MarkSceneDirty(component.gameObject.scene);
        }
        internal static int RemoveGenerated(IEnumerable<GameObject> targets)
        {
            Undo.IncrementCurrentGroup();
            int undo = Undo.GetCurrentGroup(), removed = 0;
            Undo.SetCurrentGroupName("Remove generated Convai scene data");
            try
            {
                foreach (GameObject target in targets)
                {
                    if (!target || !target.scene.IsValid() || EditorUtility.IsPersistent(target)) continue;
                    ConvaiAutoTagOwnership owner = target.GetComponent<ConvaiAutoTagOwnership>();
                    if (!owner) continue;
                    var removable = owner.Components.Where(item => item.Component && Snapshot(item.Component) == item.Json).ToArray();
                    bool tracked = owner.Metadata && new SerializedObject(owner.Metadata).FindProperty("_trackedProperties").arraySize > 0;
                    bool preserve = !owner.MetadataUnchanged || tracked || !ComponentsUnchanged(owner);
                    SceneTaggerMovementGenerator.Clear(owner);
                    foreach (var item in owner.Components.ToArray())
                    {
                        if (!item.Component) continue;
                        if (item.Component is ConvaiObjectMovementPoints movement && movement.Points.Count > 0) { preserve = true; continue; }
                        if (!removable.Contains(item)) { preserve = true; continue; }
                        Undo.DestroyObjectImmediate(item.Component);
                        Undo.RecordObject(owner, "Update generated ownership");
                        owner.Components.Remove(item);
                    }
                    if (!preserve && owner.CreatedMetadata && owner.MetadataUnchanged) Undo.DestroyObjectImmediate(owner.Metadata);
                    if (!preserve) Undo.DestroyObjectImmediate(owner);
                    EditorSceneManager.MarkSceneDirty(target.scene);
                    removed++;
                }
            }
            finally { Undo.CollapseUndoOperations(undo); }
            ConvaiSceneObjectProjection.NotifyChanged();
            return removed;
        }
    }
}
