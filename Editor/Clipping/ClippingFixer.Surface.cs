// ClippingFixer.Surface.cs
//
// Skinned renderer snapshots and triangle spatial lookup for clipping scans.

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Clipping {

    internal static partial class ClippingFixer {

        private sealed class RendererSnapshot {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public string RendererPath;
            public Vector3[] LocalVertices;
            public Vector3[] WorldVertices;
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;

            public int VertexCount => WorldVertices != null ? WorldVertices.Length : 0;

            public static RendererSnapshot Build(SkinnedMeshRenderer renderer) {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) return null;
                var snapshot = new RendererSnapshot {
                    Renderer = renderer,
                    Mesh = mesh,
                    RendererPath = PathUtility.GetGameObjectPath(renderer.gameObject),
                    LocalVertices = mesh.vertices,
                    Bones = renderer.bones ?? Array.Empty<Transform>(),
                    Bindposes = mesh.bindposes ?? Array.Empty<Matrix4x4>(),
                };
                snapshot.WorldVertices = new Vector3[snapshot.LocalVertices.Length];
                snapshot.ComputeWorldVertices();
                return snapshot;
            }

            private void ComputeWorldVertices() {
                var mesh = Mesh;
                var bones = Bones;
                var bindposes = Bindposes;
                var bonesPerVertex = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                bool canSkin = bones != null && bones.Length > 0 &&
                               bindposes != null && bindposes.Length > 0 &&
                               bonesPerVertex.Length == LocalVertices.Length &&
                               weights.Length > 0;
                if (!canSkin) {
                    for (int i = 0; i < LocalVertices.Length; i++) {
                        WorldVertices[i] = Renderer.transform.TransformPoint(LocalVertices[i]);
                    }
                    return;
                }

                int cursor = 0;
                for (int v = 0; v < LocalVertices.Length; v++) {
                    int count = bonesPerVertex[v];
                    Vector3 blended = Vector3.zero;
                    float sum = 0f;
                    for (int w = 0; w < count; w++) {
                        var bw = weights[cursor + w];
                        int boneIndex = bw.boneIndex;
                        if (boneIndex < 0 || boneIndex >= bones.Length || boneIndex >= bindposes.Length) continue;
                        var bone = bones[boneIndex];
                        if (bone == null || bw.weight <= 0f) continue;
                        var matrix = bone.localToWorldMatrix * bindposes[boneIndex];
                        blended += matrix.MultiplyPoint3x4(LocalVertices[v]) * bw.weight;
                        sum += bw.weight;
                    }
                    cursor += count;

                    if (sum > 0.00001f) {
                        WorldVertices[v] = blended / sum;
                    } else {
                        WorldVertices[v] = Renderer.transform.TransformPoint(LocalVertices[v]);
                    }
                }
            }
        }

        private sealed class WeightSourceCache {
            public SurfaceMesh Surface;
            public byte[] BonesPerVertex;
            public BoneWeight1[] Weights;
            public int[] WeightStarts;
            public int[] SourceBoneToTargetBone;

            public static WeightSourceCache Build(SkinnedMeshRenderer renderer, Transform[] targetBones) {
                if (!TryBuildSnapshot(renderer, out var snapshot, null, "weight source")) return null;
                var mesh = renderer.sharedMesh;
                if (mesh == null || !mesh.isReadable) return null;

                var sourceBones = renderer.bones ?? Array.Empty<Transform>();
                var sourceToTarget = new int[sourceBones.Length];
                for (int i = 0; i < sourceToTarget.Length; i++) {
                    sourceToTarget[i] = FindBoneIndex(targetBones, sourceBones[i]);
                }

                var bonesPerVertex = mesh.GetBonesPerVertex().ToArray();
                var weights = mesh.GetAllBoneWeights().ToArray();
                var starts = new int[bonesPerVertex.Length];
                int cursor = 0;
                for (int v = 0; v < bonesPerVertex.Length; v++) {
                    starts[v] = cursor;
                    cursor += bonesPerVertex[v];
                }

                return new WeightSourceCache {
                    Surface = SurfaceMesh.Build(snapshot),
                    BonesPerVertex = bonesPerVertex,
                    Weights = weights,
                    WeightStarts = starts,
                    SourceBoneToTargetBone = sourceToTarget,
                };
            }

            public void AddMappedVertexWeights(int vertexIndex, float scale, Dictionary<int, float> output) {
                if (output == null || scale <= 0f) return;
                if (vertexIndex < 0 || vertexIndex >= BonesPerVertex.Length || vertexIndex >= WeightStarts.Length) return;
                int start = WeightStarts[vertexIndex];
                int count = BonesPerVertex[vertexIndex];
                for (int i = 0; i < count && start + i < Weights.Length; i++) {
                    var bw = Weights[start + i];
                    if (bw.boneIndex < 0 || bw.boneIndex >= SourceBoneToTargetBone.Length || bw.weight <= 0f) continue;
                    int targetBone = SourceBoneToTargetBone[bw.boneIndex];
                    if (targetBone < 0) continue;
                    if (output.TryGetValue(targetBone, out float current)) {
                        output[targetBone] = current + bw.weight * scale;
                    } else {
                        output[targetBone] = bw.weight * scale;
                    }
                }
            }
        }

        private sealed class SurfaceMesh {
            public RendererSnapshot Snapshot;
            public readonly List<Triangle> Triangles = new List<Triangle>();
            private TriangleHash _hash;

            public static SurfaceMesh Build(RendererSnapshot snapshot) {
                var surface = new SurfaceMesh { Snapshot = snapshot };
                if (snapshot == null || snapshot.Mesh == null) return surface;
                var indices = snapshot.Mesh.triangles;
                var verts = snapshot.WorldVertices;
                for (int i = 0; i + 2 < indices.Length; i += 3) {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;
                    var tri = new Triangle(surface.Triangles.Count, a, b, c, verts[a], verts[b], verts[c]);
                    if (tri.AreaSquared <= 0.0000000001f) continue;
                    surface.Triangles.Add(tri);
                }
                surface._hash = new TriangleHash(surface.Triangles);
                return surface;
            }

            public IEnumerable<Triangle> Query(Bounds bounds, float padding) {
                if (_hash == null) return Triangles;
                return _hash.Query(bounds, padding);
            }

            public bool TryFindClosest(Vector3 point, out ClosestTriangle closest) {
                closest = default;
                if (Triangles.Count == 0) return false;
                float best = float.PositiveInfinity;
                bool found = false;
                foreach (var tri in _hash.QueryPoint(point)) {
                    var p = ClosestPoint(point, tri);
                    float d = (point - p).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    closest = new ClosestTriangle {
                        TriangleIndex = tri.Index,
                        Point = p,
                        Normal = tri.Normal,
                        SqrDistance = d,
                    };
                    found = true;
                }
                if (found) return true;

                foreach (var tri in Triangles) {
                    var p = ClosestPoint(point, tri);
                    float d = (point - p).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    closest = new ClosestTriangle {
                        TriangleIndex = tri.Index,
                        Point = p,
                        Normal = tri.Normal,
                        SqrDistance = d,
                    };
                    found = true;
                }
                return found;
            }

            private static Vector3 ClosestPoint(Vector3 point, Triangle tri) {
                return MeshGeometry.ClosestPointOnTriangle(point, tri.A, tri.B, tri.C, out _, out _, out _);
            }
        }

        private readonly struct Triangle {
            public readonly int Index;
            public readonly int AIndex;
            public readonly int BIndex;
            public readonly int CIndex;
            public readonly Vector3 A;
            public readonly Vector3 B;
            public readonly Vector3 C;
            public readonly Vector3 Normal;
            public readonly Bounds Bounds;
            public readonly float AreaSquared;

            public Triangle(int index, int aIndex, int bIndex, int cIndex, Vector3 a, Vector3 b, Vector3 c) {
                Index = index;
                AIndex = aIndex;
                BIndex = bIndex;
                CIndex = cIndex;
                A = a;
                B = b;
                C = c;
                var cross = Vector3.Cross(b - a, c - a);
                AreaSquared = cross.sqrMagnitude;
                Normal = AreaSquared > 0.0000000001f ? cross.normalized : Vector3.up;
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b);
                bounds.Encapsulate(c);
                Bounds = bounds;
            }
        }

        private struct ClosestTriangle {
            public int TriangleIndex;
            public Vector3 Point;
            public Vector3 Normal;
            public float SqrDistance;
        }

        private sealed class TriangleHash {
            private const float CellSize = 0.05f;
            private readonly List<Triangle> _triangles;
            private readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();

            public TriangleHash(List<Triangle> triangles) {
                _triangles = triangles ?? new List<Triangle>();
                for (int i = 0; i < _triangles.Count; i++) {
                    var b = _triangles[i].Bounds;
                    var min = Cell(b.min);
                    var max = Cell(b.max);
                    for (int x = min.x; x <= max.x; x++) {
                        for (int y = min.y; y <= max.y; y++) {
                            for (int z = min.z; z <= max.z; z++) {
                                var key = new Vector3Int(x, y, z);
                                if (!_cells.TryGetValue(key, out var list)) {
                                    list = new List<int>();
                                    _cells[key] = list;
                                }
                                list.Add(i);
                            }
                        }
                    }
                }
            }

            public IEnumerable<Triangle> Query(Bounds bounds, float padding) {
                bounds.Expand(padding * 2f);
                var min = Cell(bounds.min);
                var max = Cell(bounds.max);
                var seen = new HashSet<int>();
                for (int x = min.x; x <= max.x; x++) {
                    for (int y = min.y; y <= max.y; y++) {
                        for (int z = min.z; z <= max.z; z++) {
                            if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                            foreach (int idx in list) {
                                if (seen.Add(idx)) yield return _triangles[idx];
                            }
                        }
                    }
                }
            }

            public IEnumerable<Triangle> QueryPoint(Vector3 point) {
                var seen = new HashSet<int>();
                for (int radius = 0; radius <= 6; radius++) {
                    var center = Cell(point);
                    for (int x = center.x - radius; x <= center.x + radius; x++) {
                        for (int y = center.y - radius; y <= center.y + radius; y++) {
                            for (int z = center.z - radius; z <= center.z + radius; z++) {
                                if (radius > 0 &&
                                    x > center.x - radius && x < center.x + radius &&
                                    y > center.y - radius && y < center.y + radius &&
                                    z > center.z - radius && z < center.z + radius) {
                                    continue;
                                }
                                if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                                foreach (int idx in list) {
                                    if (seen.Add(idx)) yield return _triangles[idx];
                                }
                            }
                        }
                    }
                    if (seen.Count > 0) yield break;
                }
            }

            private static Vector3Int Cell(Vector3 p) {
                return new Vector3Int(
                    Mathf.FloorToInt(p.x / CellSize),
                    Mathf.FloorToInt(p.y / CellSize),
                    Mathf.FloorToInt(p.z / CellSize));
            }
        }
    }
}
