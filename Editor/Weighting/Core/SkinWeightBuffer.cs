// SkinWeightBuffer.cs
//
// Sparse, editable BoneWeight1 storage shared by transfer, paint, and
// cleanup tools. Unity's mesh stream stays the on-disk format; this type
// is the normalized working representation.

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal struct SkinWeightInfluence {
        public int BoneIndex;
        public float Weight;

        public SkinWeightInfluence(int boneIndex, float weight) {
            BoneIndex = boneIndex;
            Weight = weight;
        }
    }

    internal sealed class SkinWeightBuffer {

        private readonly List<SkinWeightInfluence>[] _weights;

        public SkinWeightBuffer(int vertexCount, int boneCount) {
            VertexCount = Mathf.Max(0, vertexCount);
            BoneCount = Mathf.Max(0, boneCount);
            _weights = new List<SkinWeightInfluence>[VertexCount];
            for (int i = 0; i < _weights.Length; i++) {
                _weights[i] = new List<SkinWeightInfluence>(4);
            }
        }

        public int VertexCount { get; }
        public int BoneCount { get; }

        public IReadOnlyList<SkinWeightInfluence> Get(int vertex) {
            return IsVertexValid(vertex) ? _weights[vertex] : Array.Empty<SkinWeightInfluence>();
        }

        public bool HasAnyWeight(int vertex) {
            return IsVertexValid(vertex) && _weights[vertex].Count > 0;
        }

        public float GetWeight(int vertex, int bone) {
            if (!IsVertexValid(vertex)) return 0f;
            var list = _weights[vertex];
            for (int i = 0; i < list.Count; i++) {
                if (list[i].BoneIndex == bone) return list[i].Weight;
            }
            return 0f;
        }

        public void SetWeight(int vertex, int bone, float value) {
            if (!IsVertexValid(vertex) || bone < 0 || bone >= BoneCount) return;
            var list = _weights[vertex];
            value = Mathf.Max(0f, value);
            for (int i = 0; i < list.Count; i++) {
                if (list[i].BoneIndex != bone) continue;
                if (value <= 0f) {
                    list.RemoveAt(i);
                } else {
                    list[i] = new SkinWeightInfluence(bone, value);
                }
                return;
            }
            if (value > 0f) list.Add(new SkinWeightInfluence(bone, value));
        }

        public void AddWeight(int vertex, int bone, float delta) {
            if (!IsVertexValid(vertex) || bone < 0 || bone >= BoneCount || Mathf.Approximately(delta, 0f)) return;
            SetWeight(vertex, bone, GetWeight(vertex, bone) + delta);
        }

        public void ClearVertex(int vertex) {
            if (IsVertexValid(vertex)) _weights[vertex].Clear();
        }

        public void SetVertexWeights(int vertex, IEnumerable<SkinWeightInfluence> influences) {
            if (!IsVertexValid(vertex)) return;
            var list = _weights[vertex];
            list.Clear();
            if (influences == null) return;
            foreach (var influence in influences) {
                if (influence.BoneIndex < 0 || influence.BoneIndex >= BoneCount || influence.Weight <= 0f) continue;
                AddWeight(vertex, influence.BoneIndex, influence.Weight);
            }
        }

        public void CopyVertexFrom(SkinWeightBuffer source, int sourceVertex, int targetVertex) {
            if (source == null || !source.IsVertexValid(sourceVertex) || !IsVertexValid(targetVertex)) return;
            SetVertexWeights(targetVertex, source.Get(sourceVertex));
        }

        public void NormalizeVertex(int vertex, int fallbackBone) {
            if (!IsVertexValid(vertex)) return;
            var list = _weights[vertex];
            float total = 0f;
            for (int i = list.Count - 1; i >= 0; i--) {
                var influence = list[i];
                if (influence.BoneIndex < 0 || influence.BoneIndex >= BoneCount || influence.Weight <= 0f) {
                    list.RemoveAt(i);
                    continue;
                }
                total += influence.Weight;
            }

            if (total <= 1e-6f) {
                list.Clear();
                int fallback = ResolveFallbackBone(fallbackBone);
                if (fallback >= 0) list.Add(new SkinWeightInfluence(fallback, 1f));
                return;
            }

            float scale = 1f / total;
            for (int i = 0; i < list.Count; i++) {
                var influence = list[i];
                influence.Weight *= scale;
                list[i] = influence;
            }
            SortVertexDescending(vertex);
        }

        public void PruneVertex(int vertex, int maxInfluences, float threshold, int fallbackBone) {
            if (!IsVertexValid(vertex)) return;
            var list = _weights[vertex];
            threshold = Mathf.Max(0f, threshold);
            for (int i = list.Count - 1; i >= 0; i--) {
                if (list[i].Weight <= threshold) list.RemoveAt(i);
            }
            SortVertexDescending(vertex);
            if (maxInfluences > 0 && list.Count > maxInfluences) {
                list.RemoveRange(maxInfluences, list.Count - maxInfluences);
            }
            NormalizeVertex(vertex, fallbackBone);
        }

        public void NormalizeAll(int fallbackBone) {
            for (int v = 0; v < VertexCount; v++) NormalizeVertex(v, fallbackBone);
        }

        public void PruneAll(int maxInfluences, float threshold, int fallbackBone) {
            for (int v = 0; v < VertexCount; v++) PruneVertex(v, maxInfluences, threshold, fallbackBone);
        }

        public void SortVertexDescending(int vertex) {
            if (!IsVertexValid(vertex)) return;
            _weights[vertex].Sort((a, b) => b.Weight.CompareTo(a.Weight));
        }

        public SkinWeightBuffer Clone() {
            var clone = new SkinWeightBuffer(VertexCount, BoneCount);
            for (int v = 0; v < VertexCount; v++) clone.SetVertexWeights(v, _weights[v]);
            return clone;
        }

        public void WriteToMesh(Mesh mesh, int maxInfluences, float threshold, int fallbackBone) {
            if (mesh == null) return;
            int vertexCount = mesh.vertexCount;
            if (vertexCount != VertexCount) {
                throw new ArgumentException("Weight buffer vertex count does not match mesh vertex count.");
            }

            PruneAll(maxInfluences, threshold, fallbackBone);

            int totalWeights = 0;
            for (int v = 0; v < VertexCount; v++) {
                int count = Mathf.Min(_weights[v].Count, 255);
                totalWeights += count;
            }

            var bpvArray = new byte[VertexCount];
            var weightArray = new BoneWeight1[totalWeights];
            int cursor = 0;
            for (int v = 0; v < VertexCount; v++) {
                var list = _weights[v];
                int count = Mathf.Min(list.Count, 255);
                bpvArray[v] = (byte)count;
                for (int i = 0; i < count; i++) {
                    weightArray[cursor++] = new BoneWeight1 {
                        boneIndex = list[i].BoneIndex,
                        weight = list[i].Weight,
                    };
                }
            }

            using (var bonesPerVertex = new NativeArray<byte>(VertexCount, Allocator.Temp))
            using (var allWeights = new NativeArray<BoneWeight1>(totalWeights, Allocator.Temp)) {
                bonesPerVertex.CopyFrom(bpvArray);
                allWeights.CopyFrom(weightArray);
                mesh.SetBoneWeights(bonesPerVertex, allWeights);
            }
        }

        public static SkinWeightBuffer FromMesh(Mesh mesh, int boneCount, int fallbackBone = 0) {
            if (mesh == null) return new SkinWeightBuffer(0, boneCount);
            var buffer = new SkinWeightBuffer(mesh.vertexCount, boneCount);
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var allWeights = mesh.GetAllBoneWeights();
            int cursor = 0;
            int count = Mathf.Min(mesh.vertexCount, bonesPerVertex.Length);
            for (int v = 0; v < count; v++) {
                int influenceCount = bonesPerVertex[v];
                for (int i = 0; i < influenceCount && cursor < allWeights.Length; i++) {
                    var influence = allWeights[cursor++];
                    buffer.AddWeight(v, influence.boneIndex, influence.weight);
                }
                buffer.NormalizeVertex(v, fallbackBone);
            }
            for (int v = count; v < mesh.vertexCount; v++) {
                buffer.NormalizeVertex(v, fallbackBone);
            }
            return buffer;
        }

        internal void AddScaledVertexToDictionary(
                int vertex,
                float scale,
                Dictionary<int, float> destination) {
            if (!IsVertexValid(vertex) || destination == null || scale <= 0f) return;
            var list = _weights[vertex];
            for (int i = 0; i < list.Count; i++) {
                var influence = list[i];
                if (destination.TryGetValue(influence.BoneIndex, out float current)) {
                    destination[influence.BoneIndex] = current + influence.Weight * scale;
                } else {
                    destination[influence.BoneIndex] = influence.Weight * scale;
                }
            }
        }

        internal void SetVertexFromDictionary(int vertex, Dictionary<int, float> weights) {
            if (!IsVertexValid(vertex)) return;
            var list = _weights[vertex];
            list.Clear();
            if (weights == null) return;
            foreach (var kv in weights) {
                if (kv.Key < 0 || kv.Key >= BoneCount || kv.Value <= 0f) continue;
                list.Add(new SkinWeightInfluence(kv.Key, kv.Value));
            }
        }

        private bool IsVertexValid(int vertex) {
            return vertex >= 0 && vertex < VertexCount;
        }

        private int ResolveFallbackBone(int fallbackBone) {
            if (BoneCount <= 0) return -1;
            if (fallbackBone >= 0 && fallbackBone < BoneCount) return fallbackBone;
            return 0;
        }
    }
}
