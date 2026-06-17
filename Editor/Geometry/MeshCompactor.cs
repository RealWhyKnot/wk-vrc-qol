// MeshCompactor.cs
//
// Rebuilds a mesh from a caller-supplied set of surviving triangles while
// preserving vertex streams, modern bone weights, bindposes, and blendshapes.

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace WhyKnot.AvatarQol.Geometry {

    internal sealed class MeshCompactionResult {
        public Mesh Mesh;
        public int[] OldToNew;
        public int[] KeptOldVertexIndices;
        public int KeptVertexCount;
        public int DroppedVertexCount;
    }

    internal static class MeshCompactor {

        internal static MeshCompactionResult BuildKeepingTriangles(
                Mesh source,
                IReadOnlyList<int[]> keptSubmeshTriangles,
                string newName) {

            if (source == null) throw new ArgumentNullException(nameof(source));
            if (keptSubmeshTriangles == null) throw new ArgumentNullException(nameof(keptSubmeshTriangles));

            int vertexCount = source.vertexCount;
            var used = new bool[vertexCount];
            for (int s = 0; s < keptSubmeshTriangles.Count; s++) {
                var triangles = keptSubmeshTriangles[s];
                if (triangles == null) continue;
                for (int i = 0; i < triangles.Length; i++) {
                    int idx = triangles[i];
                    if (idx >= 0 && idx < vertexCount) used[idx] = true;
                }
            }

            var oldToNew = new int[vertexCount];
            for (int i = 0; i < oldToNew.Length; i++) oldToNew[i] = -1;
            var kept = new List<int>(vertexCount);
            for (int i = 0; i < vertexCount; i++) {
                if (!used[i]) continue;
                oldToNew[i] = kept.Count;
                kept.Add(i);
            }

            var mesh = new Mesh { name = string.IsNullOrEmpty(newName) ? source.name + "_compacted" : newName };
            mesh.indexFormat = kept.Count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16;

            CopyVertices(source, mesh, kept);
            CopyNormals(source, mesh, kept);
            CopyTangents(source, mesh, kept);
            CopyColors(source, mesh, kept);
            CopyUvs(source, mesh, kept);
            CopyBoneWeights(source, mesh, kept);

            mesh.bindposes = source.bindposes;
            mesh.subMeshCount = keptSubmeshTriangles.Count;
            for (int s = 0; s < keptSubmeshTriangles.Count; s++) {
                mesh.SetTriangles(RemapTriangles(keptSubmeshTriangles[s], oldToNew), s, true);
            }
            CopyBlendShapes(source, mesh, kept);
            mesh.RecalculateBounds();

            return new MeshCompactionResult {
                Mesh = mesh,
                OldToNew = oldToNew,
                KeptOldVertexIndices = kept.ToArray(),
                KeptVertexCount = kept.Count,
                DroppedVertexCount = vertexCount - kept.Count,
            };
        }

        private static void CopyVertices(Mesh source, Mesh target, List<int> kept) {
            var src = source.vertices;
            var dst = new List<Vector3>(kept.Count);
            for (int i = 0; i < kept.Count; i++) dst.Add(src[kept[i]]);
            target.SetVertices(dst);
        }

        private static void CopyNormals(Mesh source, Mesh target, List<int> kept) {
            var src = source.normals;
            if (src == null || src.Length != source.vertexCount) return;
            var dst = new List<Vector3>(kept.Count);
            for (int i = 0; i < kept.Count; i++) dst.Add(src[kept[i]]);
            target.SetNormals(dst);
        }

        private static void CopyTangents(Mesh source, Mesh target, List<int> kept) {
            var src = source.tangents;
            if (src == null || src.Length != source.vertexCount) return;
            var dst = new List<Vector4>(kept.Count);
            for (int i = 0; i < kept.Count; i++) dst.Add(src[kept[i]]);
            target.SetTangents(dst);
        }

        private static void CopyColors(Mesh source, Mesh target, List<int> kept) {
            var colors32 = source.colors32;
            if (colors32 != null && colors32.Length == source.vertexCount) {
                var dst = new List<Color32>(kept.Count);
                for (int i = 0; i < kept.Count; i++) dst.Add(colors32[kept[i]]);
                target.SetColors(dst);
                return;
            }

            var colors = source.colors;
            if (colors == null || colors.Length != source.vertexCount) return;
            var colorDst = new List<Color>(kept.Count);
            for (int i = 0; i < kept.Count; i++) colorDst.Add(colors[kept[i]]);
            target.SetColors(colorDst);
        }

        private static void CopyUvs(Mesh source, Mesh target, List<int> kept) {
            for (int channel = 0; channel < 8; channel++) {
                var src = new List<Vector4>();
                source.GetUVs(channel, src);
                if (src.Count != source.vertexCount) continue;
                var dst = new List<Vector4>(kept.Count);
                for (int i = 0; i < kept.Count; i++) dst.Add(src[kept[i]]);
                target.SetUVs(channel, dst);
            }
        }

        private static void CopyBoneWeights(Mesh source, Mesh target, List<int> kept) {
            var sourceBpv = source.GetBonesPerVertex();
            if (!sourceBpv.IsCreated || sourceBpv.Length != source.vertexCount) return;

            var sourceWeights = source.GetAllBoneWeights();
            var starts = BuildWeightStarts(sourceBpv);
            var newBpv = new NativeArray<byte>(kept.Count, Allocator.Temp);
            var weights = new List<BoneWeight1>(sourceWeights.Length);

            try {
                for (int i = 0; i < kept.Count; i++) {
                    int oldVertex = kept[i];
                    int count = sourceBpv[oldVertex];
                    newBpv[i] = (byte)count;
                    int start = starts[oldVertex];
                    for (int k = 0; k < count; k++) weights.Add(sourceWeights[start + k]);
                }

                using (var newWeights = new NativeArray<BoneWeight1>(weights.ToArray(), Allocator.Temp)) {
                    target.SetBoneWeights(newBpv, newWeights);
                }
            } finally {
                newBpv.Dispose();
            }
        }

        private static int[] BuildWeightStarts(NativeArray<byte> bonesPerVertex) {
            var starts = new int[bonesPerVertex.Length];
            int cursor = 0;
            for (int i = 0; i < bonesPerVertex.Length; i++) {
                starts[i] = cursor;
                cursor += bonesPerVertex[i];
            }
            return starts;
        }

        private static int[] RemapTriangles(int[] triangles, int[] oldToNew) {
            if (triangles == null || triangles.Length == 0) return Array.Empty<int>();
            var remapped = new List<int>(triangles.Length);
            for (int i = 0; i + 2 < triangles.Length; i += 3) {
                int a = Lookup(oldToNew, triangles[i]);
                int b = Lookup(oldToNew, triangles[i + 1]);
                int c = Lookup(oldToNew, triangles[i + 2]);
                if (a < 0 || b < 0 || c < 0) continue;
                remapped.Add(a);
                remapped.Add(b);
                remapped.Add(c);
            }
            return remapped.ToArray();
        }

        private static int Lookup(int[] oldToNew, int oldIndex) {
            if (oldIndex < 0 || oldIndex >= oldToNew.Length) return -1;
            return oldToNew[oldIndex];
        }

        private static void CopyBlendShapes(Mesh source, Mesh target, List<int> kept) {
            int sourceVertexCount = source.vertexCount;
            for (int shape = 0; shape < source.blendShapeCount; shape++) {
                string shapeName = source.GetBlendShapeName(shape);
                int frameCount = source.GetBlendShapeFrameCount(shape);
                for (int frame = 0; frame < frameCount; frame++) {
                    var dv = new Vector3[sourceVertexCount];
                    var dn = new Vector3[sourceVertexCount];
                    var dt = new Vector3[sourceVertexCount];
                    source.GetBlendShapeFrameVertices(shape, frame, dv, dn, dt);

                    var ndv = new Vector3[kept.Count];
                    var ndn = new Vector3[kept.Count];
                    var ndt = new Vector3[kept.Count];
                    for (int i = 0; i < kept.Count; i++) {
                        int old = kept[i];
                        ndv[i] = dv[old];
                        ndn[i] = dn[old];
                        ndt[i] = dt[old];
                    }

                    target.AddBlendShapeFrame(
                        shapeName,
                        source.GetBlendShapeFrameWeight(shape, frame),
                        ndv,
                        ndn,
                        ndt);
                }
            }
        }
    }
}
