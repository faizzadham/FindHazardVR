using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace Convai.Editor.SceneTagging
{
    internal static class SceneTaggerGeometryInformation
    {
        private struct Surface { internal Vector3 Normal; internal float Offset, Area; }
        private sealed class NormalCluster { internal Vector3 Weighted; internal float Area; }
        private sealed class PlaneCluster { internal int Normal; internal float WeightedOffset, Area; }

        // Area-weighted normal and supporting-plane clusters match the Unreal discovery policy.
        // Unknown CPU geometry is not evidence that an object is uninteresting.
        internal static bool IsLowInformation(Mesh mesh)
        {
            if (!mesh) return false;
            try
            {
                using Mesh.MeshDataArray snapshot = MeshUtility.AcquireReadOnlyMeshData(mesh);
                Mesh.MeshData data = snapshot[0];
                if (data.vertexCount == 0) return false;
                using var vertices = new NativeArray<Vector3>(data.vertexCount, Allocator.Temp);
                data.GetVertices(vertices);
                var indices = new List<int>();
                for (int sub = 0; sub < data.subMeshCount; sub++)
                {
                    SubMeshDescriptor descriptor = data.GetSubMesh(sub);
                    if (descriptor.topology != MeshTopology.Triangles) return false;
                    int stride = Mathf.Max(1, Mathf.CeilToInt(descriptor.indexCount / 3f / 4096));
                    for (int i = 0; i + 2 < descriptor.indexCount; i += 3 * stride)
                        for (int corner = 0; corner < 3; corner++)
                            indices.Add(descriptor.baseVertex + (data.indexFormat == IndexFormat.UInt16 ? data.GetIndexData<ushort>()[descriptor.indexStart + i + corner] : data.GetIndexData<int>()[descriptor.indexStart + i + corner]));
                }
                var samples = new List<Surface>();
                float area = 0;
                foreach (int triangle in Enumerable.Range(0, indices.Count / 3))
                {
                    Vector3 a = vertices[indices[triangle * 3]], b = vertices[indices[triangle * 3 + 1]], c = vertices[indices[triangle * 3 + 2]];
                    Vector3 normal = Vector3.Cross(b - a, c - a);
                    float doubleArea = normal.magnitude;
                    if (!float.IsFinite(doubleArea) || doubleArea <= 1e-10f) continue;
                    normal /= doubleArea;
                    if (normal.x < -.0001f || Mathf.Abs(normal.x) < .0001f && normal.y < -.0001f || Mathf.Abs(normal.x) < .0001f && Mathf.Abs(normal.y) < .0001f && normal.z < 0) normal = -normal;
                    samples.Add(new Surface { Normal = normal, Offset = Vector3.Dot(normal, (a + b + c) / 3), Area = doubleArea / 2 });
                    area += doubleArea / 2;
                }
                if (samples.Count < 2 || area <= 1e-10f) return false;
                var normals = new List<NormalCluster>();
                foreach (Surface sample in samples)
                {
                    NormalCluster cluster = normals.FirstOrDefault(n => Vector3.Dot(n.Weighted.normalized, sample.Normal) >= .984807753f);
                    if (cluster == null) { cluster = new NormalCluster(); normals.Add(cluster); }
                    cluster.Weighted += sample.Normal * sample.Area; cluster.Area += sample.Area;
                }
                normals = normals.Where(n => n.Area >= area * .015f).ToList();
                if (normals.Count == 0 || normals.Count > 4 || normals.Sum(n => n.Area) < area * .90f) return false;
                for (int a = 0; a < normals.Count; a++) for (int b = a + 1; b < normals.Count; b++)
                    if (Mathf.Abs(Vector3.Dot(normals[a].Weighted.normalized, normals[b].Weighted.normalized)) > .258819045f) return false;
                float tolerance = Mathf.Max(.0025f, Mathf.Max(mesh.bounds.size.x, mesh.bounds.size.y, mesh.bounds.size.z) * .005f);
                var planes = new List<PlaneCluster>();
                foreach (Surface sample in samples)
                {
                    int normal = normals.FindIndex(n => Vector3.Dot(n.Weighted.normalized, sample.Normal) >= .984807753f);
                    if (normal < 0) continue;
                    PlaneCluster plane = planes.FirstOrDefault(p => p.Normal == normal && Mathf.Abs(sample.Offset - p.WeightedOffset / p.Area) <= tolerance);
                    if (plane == null) { plane = new PlaneCluster { Normal = normal }; planes.Add(plane); }
                    plane.WeightedOffset += sample.Offset * sample.Area; plane.Area += sample.Area;
                }
                int significant = planes.Count(p => p.Area >= area * .015f);
                return significant > 0 && significant <= 12;
            }
            catch (Exception) { return false; }
        }
    }
}
