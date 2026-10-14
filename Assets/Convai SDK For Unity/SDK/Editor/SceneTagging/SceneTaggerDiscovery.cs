using Convai.Shared.Compatibility;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Unity.Collections;
using Convai.Runtime.Components;
using Convai.Runtime.SceneMetadata;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Convai.Editor.SceneTagging
{
    internal sealed class SceneTaggerDiscoveryReport
    {
        internal string SceneName;
        internal bool SelectedOnly;
        internal int AlreadyTagged, BasicShapes, BelowRelevance;

        internal string EmptyMessage
        {
            get
            {
                if (SelectedOnly) return "Select active scene objects with supported mesh geometry. Characters, players and unsupported renderers are excluded.";
                var reasons = new List<string> { $"Scanned active scene: {SceneName}." };
                if (AlreadyTagged > 0) reasons.Add($"Already tagged: {AlreadyTagged}. Enable Re-analyze tagged objects to scan them again.");
                if (BasicShapes > 0) reasons.Add($"Plain engine shapes: {BasicShapes}. Turn off Exclude plain engine shapes in Settings, or use Selected Objects.");
                if (BelowRelevance > 0) reasons.Add($"Below minimum relevance: {BelowRelevance}. Lower Minimum local relevance in Settings, or use Selected Objects.");
                if (reasons.Count == 1) reasons.Add("No supported, active mesh objects were found. Characters, players and unsupported renderers are excluded.");
                return string.Join("\n", reasons);
            }
        }
    }

    internal static class SceneTaggerDiscovery
    {
        internal static List<SceneTaggerRow> Discover(SceneTaggerSettings settings, IEnumerable<GameObject> selection = null) =>
            Discover(settings, out _, selection);

        internal static List<SceneTaggerRow> Discover(SceneTaggerSettings settings, out SceneTaggerDiscoveryReport report, IEnumerable<GameObject> selection = null)
        {
            report = new SceneTaggerDiscoveryReport { SceneName = SceneManager.GetActiveScene().name, SelectedOnly = settings.SelectedOnly };
            if (PrefabStageUtility.GetCurrentPrefabStage() != null) throw new InvalidOperationException("Open a scene to use Scene Auto Tagger. Prefab assets are not edited by this tool.");
            var roots = new HashSet<GameObject>();
            var containers = new Dictionary<GameObject, bool>();
            if (settings.SelectedOnly)
            {
                var selected = new HashSet<GameObject>((selection ?? Selection.gameObjects).Where(EligibleObject));
                foreach (GameObject go in selected)
                    if (!Ancestors(go.transform.parent).Any(t => selected.Contains(t.gameObject))) roots.Add(go);
            }
            else
            {
                foreach (GameObject root in SceneManager.GetActiveScene().GetRootGameObjects())
                foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
                {
                    if (!EligibleRenderer(renderer)) continue;
                    ConvaiObjectMetadata metadata = renderer.GetComponentInParent<ConvaiObjectMetadata>();
                    GameObject owner = metadata ? metadata.gameObject : PrefabUtility.GetNearestPrefabInstanceRoot(renderer.gameObject);
                    while (owner && !metadata && renderer.transform != owner.transform)
                    {
                        if (!containers.TryGetValue(owner, out bool container))
                            containers[owner] = container = IsEnvironmentContainer(owner);
                        if (!container) break;
                        Transform child = renderer.transform;
                        while (child.parent && child.parent != owner.transform) child = child.parent;
                        owner = child.gameObject;
                    }
                    if (!owner)
                    {
                        LODGroup lod = renderer.GetComponentInParent<LODGroup>();
                        owner = lod ? lod.gameObject : renderer.gameObject;
                    }
                    if (EligibleObject(owner)) roots.Add(owner);
                }
            }
            var rows = new List<SceneTaggerRow>();
            foreach (GameObject root in roots.OrderBy(StablePath, StringComparer.Ordinal))
            {
                if (root.GetComponentInParent<ConvaiCharacter>() || root.GetComponentInParent<ConvaiPlayer>()) continue;
                if (!settings.SelectedOnly && !settings.IncludeTagged && root.GetComponent<ConvaiObjectMetadata>())
                { report.AlreadyTagged++; continue; }
                SceneTaggerRow row = Create(root, settings.SelectedOnly, roots);
                if (row == null) continue;
                if (!row.Explicit && settings.ExcludeBasicShapes && row.ExcludedBasicShape)
                { report.BasicShapes++; continue; }
                if (!row.Explicit && !(settings.IncludeTagged && root.GetComponent<ConvaiObjectMetadata>()) && row.Significance < settings.Relevance)
                { report.BelowRelevance++; continue; }
                rows.Add(row);

            }
            return rows.OrderByDescending(r => r.Significance).ThenBy(r => StablePath(r.Target), StringComparer.Ordinal).Take(Mathf.Clamp(settings.CandidateLimit, 1, 300)).ToList();
        }

        private static bool IsEnvironmentContainer(GameObject root)
        {
            if (root.transform.childCount < 2 || root.GetComponent<LODGroup>()) return false;
            var tokens = LabelTokens(root.name);
            if (new[] { "apartment", "building", "environment", "house", "level", "room", "scene" }.Any(tokens.Contains)) return true;

            // Spatially separate child branches form a collection even when the prefab has an
            // arbitrary name. Touching parts (for example a chair's seat, back and legs) stay together.
            var bounds = new List<Bounds>();
            foreach (Transform child in root.transform)
            {
                Renderer[] renderers = child.GetComponentsInChildren<Renderer>(false).Where(EligibleRenderer).ToArray();
                if (renderers.Length == 0) continue;
                Bounds branch = renderers[0].bounds;
                foreach (Renderer renderer in renderers.Skip(1)) branch.Encapsulate(renderer.bounds);
                branch.Expand(.05f); // Allow small assembly gaps between touching parts (metres).
                bounds.Add(branch);
            }
            if (bounds.Count < 2) return false;
            var connected = new HashSet<int> { 0 };
            var pending = new Queue<int>();
            pending.Enqueue(0);
            while (pending.Count > 0)
            {
                int next = pending.Dequeue();
                for (int i = 1; i < bounds.Count; i++)
                    if (!connected.Contains(i) && bounds[next].Intersects(bounds[i]))
                    { connected.Add(i); pending.Enqueue(i); }
            }
            return connected.Count != bounds.Count;
        }

        internal static SceneTaggerRow Create(GameObject root, bool explicitSelection, HashSet<GameObject> boundaries = null)
        {
            if (!EligibleObject(root)) return null;
            Renderer[] renderers = CollectRenderers(root, boundaries);
            if (renderers.Length == 0) return null;
            var row = new SceneTaggerRow { Target = root, Explicit = explicitSelection, Renderers = renderers,
                Boundaries = boundaries,
                WorldMatrix = root.transform.localToWorldMatrix, SceneId = ConvaiSceneId.Of(root.scene),
                GlobalId = GlobalObjectId.GetGlobalObjectIdSlow(root).ToString() };
            var corners = new List<Vector3[]>();
            foreach (Renderer renderer in renderers)
            {
                Bounds bounds = LocalBounds(renderer);
                Vector3[] worldCorners = BoundsCorners(bounds).Select(renderer.transform.TransformPoint).ToArray();
                if (corners.Count == 0) row.Bounds = new Bounds(worldCorners[0], Vector3.zero);
                foreach (Vector3 point in worldCorners) row.Bounds.Encapsulate(point);
                corners.Add(worldCorners.Select(p => Quaternion.Inverse(root.transform.rotation) * (p - root.transform.position)).ToArray());
            }
            if (!Finite(row.Bounds.center) || !Finite(row.Bounds.size) || row.Bounds.size.sqrMagnitude < 1e-8f) return null;
            row.Corners = corners.ToArray();
            Material[] surfaces = renderers.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToArray();
            row.Metallic = surfaces.Length == 1 && surfaces[0].HasProperty("_Metallic") && surfaces[0].GetFloat("_Metallic") >= .5f;
            Score(row);
            row.Fingerprint = Fingerprint(row);
            row.ProofGroup = row.Fingerprint;
            ConvaiObjectMetadata metadata = root.GetComponent<ConvaiObjectMetadata>();
            row.Name = metadata ? metadata.ObjectName : "";
            row.Description = metadata ? metadata.ObjectDescription : "";
            row.LogicalGroupId = root.GetComponent<ConvaiAutoTagOwnership>()?.LogicalGroupId;
            return row;
        }

        internal static Renderer[] CollectRenderers(GameObject root, HashSet<GameObject> boundaries = null)
        {
            var result = new List<Renderer>();
            foreach (Renderer renderer in root.GetComponentsInChildren<Renderer>(false))
            {
                if (!EligibleRenderer(renderer)) continue;
                bool belongs = true;
                foreach (Transform parent in Ancestors(renderer.transform))
                {
                    if (parent == root.transform) break;
                    if (parent.GetComponent<ConvaiObjectMetadata>() || boundaries != null && boundaries.Contains(parent.gameObject)) { belongs = false; break; }
                }
                if (!belongs) continue;
                LODGroup lod = renderer.GetComponentInParent<LODGroup>();
                if (lod)
                {
                    LOD[] levels = lod.GetLODs();
                    if (levels.Length > 0 && !levels[0].renderers.Contains(renderer) &&
                        levels.Skip(1).Any(level => level.renderers.Contains(renderer))) continue;
                }
                result.Add(renderer);
            }
            return result.OrderBy(r => StablePath(r.gameObject), StringComparer.Ordinal).ToArray();
        }

        internal static bool Validate(SceneTaggerRow row)
        {
            if (!row.IsCurrent || row.Renderers.Any(r => !r) || ConvaiSceneId.Of(row.Target.scene) != row.SceneId || row.Target.transform.localToWorldMatrix != row.WorldMatrix) return false;
            var current = Create(row.Target, row.Explicit, row.Boundaries);
            return current != null && current.Fingerprint == row.Fingerprint;
        }

        internal static string Fingerprint(SceneTaggerRow row)
        {
            var text = new StringBuilder("unity-scene-tagger-geometry-v2|");
            foreach (Renderer renderer in row.Renderers)
            {
                Mesh mesh = renderer is SkinnedMeshRenderer skinned ? skinned.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (!mesh) continue;
                string path = AssetDatabase.GetAssetPath(mesh);
                text.Append(path).Append('|');
                if (AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string guid, out long localId)) text.Append(guid).Append(':').Append(localId);
                else text.Append(ConvaiObjectId.Of(mesh));
                if (!string.IsNullOrEmpty(path)) text.Append(AssetDatabase.GetAssetDependencyHash(path));
                else
                {
                    text.Append(ConvaiObjectId.Of(row.Target)); // Never reuse procedural geometry between objects.
                    if (mesh.isReadable)
                    {
                        text.Append(HashMeshBytes(mesh));
                    }
                    text.Append(mesh.bounds.ToString("R"));
                }
                Matrix4x4 relative = row.Target.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix;
                for (int i = 0; i < 16; i++) text.Append('|').Append(relative[i].ToString("R", CultureInfo.InvariantCulture));
                Vector3 scale = row.Target.transform.lossyScale;
                text.Append('|').Append(scale.x.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(scale.y.ToString("R", CultureInfo.InvariantCulture)).Append('|').Append(scale.z.ToString("R", CultureInfo.InvariantCulture));
                foreach (Material material in renderer.sharedMaterials)
                {
                    if (!material) { text.Append("|missing"); continue; }
                    // Unrecognized shaders can read world position, time or external buffers.
                    // Their visual equivalence cannot be established from mesh/material identity.
                    string shader = material.shader ? material.shader.name : "";
                    if (shader != "Standard" && shader != "Universal Render Pipeline/Lit" && shader != "Universal Render Pipeline/Simple Lit" && shader != "HDRP/Lit" ||
                        material.HasProperty("_UVBase") && material.GetFloat("_UVBase") != 0)
                        text.Append('|').Append(ConvaiObjectId.Of(row.Target));
                    text.Append('|').Append(EditorJsonUtility.ToJson(material));
                    string materialPath = AssetDatabase.GetAssetPath(material);
                    if (!string.IsNullOrEmpty(materialPath)) text.Append(AssetDatabase.GetAssetDependencyHash(materialPath));
                }
                if (renderer.HasPropertyBlock())
                {
                    text.Append('|').Append(ConvaiObjectId.Of(row.Target)); // Unknown shader properties cannot prove cross-object equivalence.
                    var block = new MaterialPropertyBlock();
                    renderer.GetPropertyBlock(block);
                    AppendBlock(block, renderer.sharedMaterials, text);
                    for (int materialIndex = 0; materialIndex < renderer.sharedMaterials.Length; materialIndex++)
                    { block.Clear(); renderer.GetPropertyBlock(block, materialIndex); AppendBlock(block, renderer.sharedMaterials, text); }
                }
                if (renderer is SkinnedMeshRenderer skin)
                {
                    text.Append('|').Append(ConvaiObjectId.Of(row.Target));
                    foreach (Transform bone in skin.bones) if (bone) text.Append(bone.localToWorldMatrix.ToString("R"));
                    for (int blend = 0; blend < mesh.blendShapeCount; blend++) text.Append('|').Append(skin.GetBlendShapeWeight(blend));
                }
            }
            using var hash = SHA256.Create();
            return BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(text.ToString()))).Replace("-", "");
        }

        internal static string HashMeshBytes(Mesh mesh)
        {
            using Mesh.MeshDataArray snapshot = MeshUtility.AcquireReadOnlyMeshData(mesh);
            Mesh.MeshData data = snapshot[0];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            void Append(NativeArray<byte> bytes)
            {
                for (int offset = 0; offset < bytes.Length; offset += buffer.Length)
                {
                    int count = Math.Min(buffer.Length, bytes.Length - offset);
                    NativeArray<byte>.Copy(bytes, offset, buffer, 0, count);
                    hash.AppendData(buffer, 0, count);
                }
            }
            for (int stream = 0; stream < data.vertexBufferCount; stream++)
            {
                hash.AppendData(BitConverter.GetBytes(data.GetVertexBufferStride(stream)));
                Append(data.GetVertexData<byte>(stream));
            }
            Append(data.GetIndexData<byte>());
            var layout = new StringBuilder().Append(data.indexFormat);
            foreach (var attribute in mesh.GetVertexAttributes()) layout.Append('|').Append(attribute);
            for (int i = 0; i < data.subMeshCount; i++)
            {
                var sub = data.GetSubMesh(i);
                layout.Append('|').Append(sub.topology).Append(':').Append(sub.indexStart)
                    .Append(':').Append(sub.indexCount).Append(':').Append(sub.baseVertex);
            }
            hash.AppendData(Encoding.UTF8.GetBytes(layout.ToString()));
            return BitConverter.ToString(hash.GetHashAndReset()).Replace("-", "");
        }

        internal static HashSet<string> LabelTokens(string value)
        {
            string[] words = Regex.Split(Regex.Replace(value ?? "", @"([a-z])([A-Z])", "$1 $2").ToLowerInvariant(), @"[^a-z]+");
            var tokens = new HashSet<string>(words, StringComparer.Ordinal);
            // Only join complete adjacent words. Substrings would turn waterproof into roof.
            for (int i = 1; i < words.Length; i++) tokens.Add(words[i - 1] + words[i]);
            foreach (string word in tokens.ToArray())
                if (word.Length > 1 && word.EndsWith("s", StringComparison.Ordinal)) tokens.Add(word[..^1]);
            return tokens;
        }

        private static void AppendBlock(MaterialPropertyBlock block, Material[] materials, StringBuilder text)
        {
            foreach (Material material in materials)
            {
                if (!material || !material.shader) continue;
                Shader shader = material.shader;
                for (int property = 0; property < shader.GetPropertyCount(); property++)
                {
                    int id = shader.GetPropertyNameId(property);
                    if (!block.HasProperty(id)) continue;
                    text.Append('|').Append(id);
                    switch (shader.GetPropertyType(property))
                    {
                        case UnityEngine.Rendering.ShaderPropertyType.Color: text.Append(block.GetColor(id).ToString("R")); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Vector: text.Append(block.GetVector(id).ToString("R")); break;
                        case UnityEngine.Rendering.ShaderPropertyType.Texture: text.Append(ConvaiObjectId.Of(block.GetTexture(id))); break;
                        default: text.Append(block.GetFloat(id).ToString("R", CultureInfo.InvariantCulture)); break;
                    }
                }
            }
        }

        private static readonly string[] Important = { "artifact", "artwork", "bust", "display", "exhibit", "painting", "plaque", "portrait", "sculpture", "statue", "screen", "relic", "monument" };
        private static readonly string[] Structural = { "floor", "wall", "ceiling", "roof", "baseboard", "trim", "navmesh", "recast", "collision", "blockingvolume", "postprocess", "skylight", "rectlight", "spotlight" };
        private static Mesh MeshOf(Renderer renderer) => renderer is SkinnedMeshRenderer skin ? skin.sharedMesh : renderer.GetComponent<MeshFilter>()?.sharedMesh;
        private static void Score(SceneTaggerRow row)
        {
            Mesh[] meshes = row.Renderers.Select(MeshOf).Where(m => m).ToArray();
            Material[] materials = row.Renderers.SelectMany(r => r.sharedMaterials).Where(m => m).Distinct().ToArray();
            string label = row.Target.name.ToLowerInvariant();
            var tokens = LabelTokens(row.Target.name + " " + string.Join(" ", meshes.Select(m => m.name)));
            // A mounting location does not make a prop architecture (for example, a wall clock or floor lamp).
            bool important = Important.Any(tokens.Contains), structural = Structural.Any(tokens.Contains) &&
                !tokens.Contains("lamp") && !tokens.Contains("clock");
            bool basic = meshes.All(m => AssetDatabase.GetAssetPath(m).Contains("unity default resources"));
            bool authored = materials.Any(m => !m.name.StartsWith("Default-", StringComparison.OrdinalIgnoreCase));
            Vector3 panelSize = row.Renderers.Length == 1 ? Vector3.Scale(LocalBounds(row.Renderers[0]).size, row.Renderers[0].transform.lossyScale) : row.Bounds.size;
            float[] dimensions = { Mathf.Abs(panelSize.x), Mathf.Abs(panelSize.y), Mathf.Abs(panelSize.z) }; Array.Sort(dimensions);
            bool panel = dimensions[1] >= .2f && dimensions[2] >= .2f && dimensions[0] <= Mathf.Max(.06f, dimensions[1] * .15f);
            bool lowInformation = row.Renderers.Length == 1 && SceneTaggerGeometryInformation.IsLowInformation(meshes[0]);
            bool surface = authored || materials.Length > 1 || materials.Any(m => m.GetTexturePropertyNames().Any(name => m.GetTexture(name)));
            row.ExcludedBasicShape = basic && !(authored && panel) && !important;
            float longest = Mathf.Max(row.Bounds.size.x, row.Bounds.size.y, row.Bounds.size.z);
            float score = .42f + (longest >= .12f && longest <= 12 ? .12f : longest > 12 ? .03f : -.28f);
            if (row.Renderers.Length > 1) score += Mathf.Min(.12f, .03f * (row.Renderers.Length - 1));
            if (materials.Length > 1) score += Mathf.Min(.10f, .025f * (materials.Length - 1));
            if (important) score += .36f;
            if (structural && !important) score -= .48f;
            if (!label.StartsWith("gameobject") && !new[] { "cube", "sphere", "capsule", "cylinder", "plane", "quad" }.Any(label.StartsWith)) score += .04f;
            if (basic && !authored) score -= .35f;
            if (lowInformation && !important && !(panel && surface)) score = surface ? Mathf.Min(score - .18f, .44f) : Mathf.Min(score - .46f, .12f);
            row.Significance = Mathf.Clamp01(score);
            uint reasons = longest >= .12f && longest <= 12 ? 1u : longest > 12 ? 2u : 4u;
            if (row.Renderers.Length > 1) reasons |= 1u << 3;
            if (materials.Length > 1) reasons |= 1u << 4;
            if (important) reasons |= 1u << 5;
            if (structural && !important) reasons |= 1u << 6;
            if (!label.StartsWith("gameobject") && !new[] { "cube", "sphere", "capsule", "cylinder", "plane", "quad" }.Any(label.StartsWith)) reasons |= 1u << 7;
            if (basic && !authored) reasons |= 1u << 8;
            if (basic && authored && panel) reasons |= 1u << 9;
            if (lowInformation && !important && !(panel && surface)) reasons |= surface ? 1u << 10 : 1u << 11;
            row.SignificanceReasons = reasons;
        }

        internal static Bounds LocalBounds(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer skin)
            {
                var mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                try { skin.BakeMesh(mesh); mesh.RecalculateBounds(); return mesh.bounds; }
                finally { UnityEngine.Object.DestroyImmediate(mesh); }
            }
            return MeshOf(renderer).bounds;
        }

        internal static Vector3[] BoundsCorners(Bounds bounds)
        {
            var corners = new Vector3[8];
            for (int i = 0; i < 8; i++) corners[i] = bounds.center + Vector3.Scale(bounds.extents, new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1));
            return corners;
        }
        private static bool EligibleObject(GameObject go) => go && go.scene.IsValid() && go.scene.isLoaded && go.activeInHierarchy && !EditorUtility.IsPersistent(go) && (go.hideFlags & HideFlags.DontSave) == 0;
        private static bool EligibleRenderer(Renderer r) => r && r.enabled && EligibleObject(r.gameObject) &&
            (r is SkinnedMeshRenderer s && s.sharedMesh || r is MeshRenderer && r.GetComponent<MeshFilter>()?.sharedMesh);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        private static IEnumerable<Transform> Ancestors(Transform t) { for (; t; t = t.parent) yield return t; }
        internal static string StablePath(GameObject go) => go.scene.path + "/" + string.Join("/", Ancestors(go.transform).Reverse().Select(t => t.GetSiblingIndex().ToString("D6") + ":" + t.name));
    }
}
