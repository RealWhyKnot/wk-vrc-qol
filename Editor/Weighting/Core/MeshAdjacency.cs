// MeshAdjacency.cs
//
// Triangle-derived vertex neighbor graph used by weight inpainting and
// later brush smoothing.

using System;
using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal sealed class MeshAdjacency {

        private readonly int[][] _neighbors;

        private MeshAdjacency(int[][] neighbors) {
            _neighbors = neighbors ?? Array.Empty<int[]>();
        }

        public int VertexCount => _neighbors.Length;

        public int[] NeighborsOf(int vertex) {
            if (vertex < 0 || vertex >= _neighbors.Length) return Array.Empty<int>();
            return _neighbors[vertex] ?? Array.Empty<int>();
        }

        public static MeshAdjacency Build(Mesh mesh) {
            if (mesh == null || mesh.vertexCount <= 0) return new MeshAdjacency(Array.Empty<int[]>());
            var lists = new List<int>[mesh.vertexCount];
            for (int i = 0; i < lists.Length; i++) lists[i] = new List<int>(6);

            for (int s = 0; s < mesh.subMeshCount; s++) {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    AddEdge(lists, tris[i], tris[i + 1]);
                    AddEdge(lists, tris[i + 1], tris[i + 2]);
                    AddEdge(lists, tris[i + 2], tris[i]);
                }
            }

            var neighbors = new int[lists.Length][];
            for (int i = 0; i < lists.Length; i++) neighbors[i] = lists[i].ToArray();
            return new MeshAdjacency(neighbors);
        }

        private static void AddEdge(List<int>[] lists, int a, int b) {
            if (lists == null || a < 0 || b < 0 || a >= lists.Length || b >= lists.Length || a == b) return;
            AddUnique(lists[a], b);
            AddUnique(lists[b], a);
        }

        private static void AddUnique(List<int> list, int value) {
            for (int i = 0; i < list.Count; i++) if (list[i] == value) return;
            list.Add(value);
        }
    }
}
