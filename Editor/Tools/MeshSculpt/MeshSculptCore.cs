// MeshSculptCore.cs
//
// Pure mesh/math helpers for the Mesh Sculpt window. These methods avoid
// EditorWindow state so tests can pin topology, brush, and adjacency
// behavior without requiring a SceneView.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class MeshSculptCore {

        internal struct MeshHit {
            public int Submesh;
            public int TriangleOffset;
            public int I0;
            public int I1;
            public int I2;
            public float T;
            public float U;
            public float V;
            public Vector3 WorldPosition;
            public Vector3 WorldNormal;
        }

        internal sealed class VertexAdjacency {
            private readonly int[][] _neighbors;

            internal VertexAdjacency(int[][] neighbors) {
                _neighbors = neighbors ?? Array.Empty<int[]>();
            }

            public int VertexCount => _neighbors.Length;

            public int[] NeighborsOf(int vertexIndex) {
                if (vertexIndex < 0 || vertexIndex >= _neighbors.Length) return Array.Empty<int>();
                return _neighbors[vertexIndex] ?? Array.Empty<int>();
            }
        }

        internal static VertexAdjacency BuildAdjacency(Mesh mesh) {
            if (mesh == null || mesh.vertexCount == 0) return new VertexAdjacency(Array.Empty<int[]>());
            var lists = new List<int>[mesh.vertexCount];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>(6);

            for (int s = 0; s < mesh.subMeshCount; s++) {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int a = tris[i];
                    int b = tris[i + 1];
                    int c = tris[i + 2];
                    AddEdge(lists, a, b);
                    AddEdge(lists, b, c);
                    AddEdge(lists, c, a);
                }
            }

            var neighbors = new int[lists.Length][];
            for (int i = 0; i < lists.Length; i++) neighbors[i] = lists[i].ToArray();
            return new VertexAdjacency(neighbors);
        }

        internal static bool TryRaycast(
                Mesh mesh,
                Vector3[] worldVertices,
                Ray ray,
                out MeshHit hit) {
            hit = default;
            if (mesh == null || worldVertices == null || worldVertices.Length == 0) return false;

            bool any = false;
            float bestT = float.PositiveInfinity;
            for (int s = 0; s < mesh.subMeshCount; s++) {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int i0 = tris[i];
                    int i1 = tris[i + 1];
                    int i2 = tris[i + 2];
                    if (!IsVertexIndexValid(worldVertices, i0)
                            || !IsVertexIndexValid(worldVertices, i1)
                            || !IsVertexIndexValid(worldVertices, i2)) {
                        continue;
                    }

                    if (!MaskPainterIO.RayTriangle(ray.origin, ray.direction,
                            worldVertices[i0], worldVertices[i1], worldVertices[i2],
                            out float t, out float u, out float v)) {
                        continue;
                    }
                    if (t >= bestT) continue;

                    any = true;
                    bestT = t;
                    var a = worldVertices[i0];
                    var b = worldVertices[i1];
                    var c = worldVertices[i2];
                    var normal = Vector3.Cross(b - a, c - a).normalized;
                    if (Vector3.Dot(normal, ray.direction) > 0f) normal = -normal;

                    hit = new MeshHit {
                        Submesh = s,
                        TriangleOffset = i,
                        I0 = i0,
                        I1 = i1,
                        I2 = i2,
                        T = t,
                        U = u,
                        V = v,
                        WorldPosition = ray.origin + ray.direction * t,
                        WorldNormal = normal,
                    };
                }
            }
            return any;
        }

        internal static int NearestVertexOnHit(MeshHit hit, Vector3[] worldVertices) {
            if (worldVertices == null) return -1;
            int best = -1;
            float bestSq = float.PositiveInfinity;
            TryCandidate(hit.I0);
            TryCandidate(hit.I1);
            TryCandidate(hit.I2);
            return best;

            void TryCandidate(int index) {
                if (!IsVertexIndexValid(worldVertices, index)) return;
                float d = (worldVertices[index] - hit.WorldPosition).sqrMagnitude;
                if (d < bestSq) {
                    bestSq = d;
                    best = index;
                }
            }
        }

        internal static float BrushWeight(float distance, float radius) {
            if (radius <= 0f || distance >= radius) return 0f;
            float t = Mathf.Clamp01(1f - distance / radius);
            return t * t * (3f - 2f * t);
        }

        internal static void ApplyGrab(
                Vector3[] vertices,
                IReadOnlyList<int> indices,
                Vector3 delta,
                IReadOnlyList<float> weights = null) {
            if (vertices == null || indices == null) return;
            for (int i = 0; i < indices.Count; i++) {
                int v = indices[i];
                if (v < 0 || v >= vertices.Length) continue;
                float w = weights != null && i < weights.Count ? weights[i] : 1f;
                vertices[v] += delta * w;
            }
        }

        internal static void ApplySmooth(
                Vector3[] vertices,
                IReadOnlyList<int> indices,
                VertexAdjacency adjacency,
                float strength,
                IReadOnlyList<float> weights = null) {
            if (vertices == null || indices == null || adjacency == null) return;
            strength = Mathf.Clamp01(strength);
            var original = new Vector3[vertices.Length];
            Array.Copy(vertices, original, vertices.Length);

            for (int i = 0; i < indices.Count; i++) {
                int v = indices[i];
                if (v < 0 || v >= vertices.Length) continue;
                var neighbors = adjacency.NeighborsOf(v);
                if (neighbors.Length == 0) continue;

                var avg = Vector3.zero;
                int count = 0;
                for (int n = 0; n < neighbors.Length; n++) {
                    int ni = neighbors[n];
                    if (ni < 0 || ni >= original.Length) continue;
                    avg += original[ni];
                    count++;
                }
                if (count == 0) continue;
                avg /= count;
                float w = weights != null && i < weights.Count ? weights[i] : 1f;
                vertices[v] = Vector3.Lerp(original[v], avg, strength * w);
            }
        }

        internal static bool TryAppendFace(
                Mesh mesh,
                IReadOnlyList<int> orderedVertices,
                int submesh,
                bool flip,
                out string error) {
            error = null;
            if (mesh == null) {
                error = "Mesh is missing.";
                return false;
            }
            if (orderedVertices == null || orderedVertices.Count < 3) {
                error = "Select at least three vertices.";
                return false;
            }
            if (orderedVertices.Count > 4) {
                error = "This first pass supports triangle and quad fills only.";
                return false;
            }
            if (submesh < 0 || submesh >= mesh.subMeshCount) {
                error = "Pick a valid submesh.";
                return false;
            }

            var seen = new HashSet<int>();
            for (int i = 0; i < orderedVertices.Count; i++) {
                int v = orderedVertices[i];
                if (v < 0 || v >= mesh.vertexCount) {
                    error = $"Vertex {v} is outside the mesh.";
                    return false;
                }
                if (!seen.Add(v)) {
                    error = "Selected vertices must be unique.";
                    return false;
                }
            }

            var positions = mesh.vertices;
            var newTris = new List<int>();
            if (orderedVertices.Count == 3) {
                AddTriangleIfValid(orderedVertices[0], orderedVertices[1], orderedVertices[2], positions, flip, newTris, ref error);
            } else {
                int a = orderedVertices[0];
                int b = orderedVertices[1];
                int c = orderedVertices[2];
                int d = orderedVertices[3];
                float diagAC = (positions[a] - positions[c]).sqrMagnitude;
                float diagBD = (positions[b] - positions[d]).sqrMagnitude;
                if (diagAC <= diagBD) {
                    AddTriangleIfValid(a, b, c, positions, flip, newTris, ref error);
                    AddTriangleIfValid(a, c, d, positions, flip, newTris, ref error);
                } else {
                    AddTriangleIfValid(a, b, d, positions, flip, newTris, ref error);
                    AddTriangleIfValid(b, c, d, positions, flip, newTris, ref error);
                }
            }

            if (!string.IsNullOrEmpty(error)) return false;
            if (newTris.Count == 0) {
                error = "Selected vertices form a degenerate face.";
                return false;
            }

            var tris = new List<int>(mesh.GetTriangles(submesh));
            tris.AddRange(newTris);
            mesh.SetTriangles(tris, submesh, calculateBounds: false);
            mesh.RecalculateBounds();
            return true;
        }

        private static void AddTriangleIfValid(
                int a,
                int b,
                int c,
                Vector3[] positions,
                bool flip,
                List<int> output,
                ref string error) {
            if (!string.IsNullOrEmpty(error)) return;
            var area = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]).magnitude * 0.5f;
            if (area <= 1e-8f) {
                error = "Selected vertices form a degenerate face.";
                return;
            }
            if (flip) {
                output.Add(a);
                output.Add(c);
                output.Add(b);
            } else {
                output.Add(a);
                output.Add(b);
                output.Add(c);
            }
        }

        private static void AddEdge(List<int>[] lists, int a, int b) {
            if (lists == null || a < 0 || b < 0 || a >= lists.Length || b >= lists.Length || a == b) return;
            AddUnique(lists[a], b);
            AddUnique(lists[b], a);
        }

        private static void AddUnique(List<int> list, int value) {
            for (int i = 0; i < list.Count; i++) {
                if (list[i] == value) return;
            }
            list.Add(value);
        }

        private static bool IsVertexIndexValid(Vector3[] vertices, int index) {
            return vertices != null && index >= 0 && index < vertices.Length;
        }
    }
}
