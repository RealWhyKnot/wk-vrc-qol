// MeshSculptSession.cs
//
// Active edit state for one SkinnedMeshRenderer. Owns cached mesh arrays,
// a baked posed snapshot for SceneView interaction, selection order, and
// base-space mutation helpers.

using System;
using System.Collections.Generic;
using Unity.Collections;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class MeshSculptSession : IDisposable {

        private readonly SkinnedMeshRenderer _renderer;
        private readonly Mesh _mesh;
        private readonly Mesh _bakedMesh;
        private readonly HashSet<int> _selectionSet = new HashSet<int>();
        private readonly List<int> _selectionOrder = new List<int>();

        private Vector3[] _vertices;
        private Vector3[] _bakedWorldVertices;
        private Vector3[] _bakedWorldNormals;
        private Matrix4x4[] _bindposes;
        private Transform[] _bones;
        private byte[] _bonesPerVertex;
        private BoneWeight1[] _allWeights;
        private int[] _weightOffsets;
        private MeshSculptCore.VertexAdjacency _adjacency;

        public MeshSculptSession(SkinnedMeshRenderer renderer) {
            _renderer = renderer;
            _mesh = renderer != null ? renderer.sharedMesh : null;
            _bakedMesh = new Mesh {
                name = "WhyKnotMeshSculpt_BakedSnapshot",
                hideFlags = HideFlags.HideAndDontSave,
            };
            ReloadMeshData();
            RebuildSnapshot();
        }

        public SkinnedMeshRenderer Renderer => _renderer;
        public Mesh Mesh => _mesh;
        public bool IsValid => _renderer != null && _mesh != null && _mesh.isReadable;
        public int VertexCount => _mesh != null ? _mesh.vertexCount : 0;
        public int SelectedCount => _selectionOrder.Count;
        public IReadOnlyList<int> SelectionOrder => _selectionOrder;

        public void Dispose() {
            if (_bakedMesh != null) UnityEngine.Object.DestroyImmediate(_bakedMesh);
        }

        public void ReloadMeshData() {
            if (_mesh == null || !_mesh.isReadable) return;
            _vertices = _mesh.vertices;
            _bindposes = _mesh.bindposes;
            _bones = _renderer != null ? _renderer.bones : null;

            var bpv = _mesh.GetBonesPerVertex();
            _bonesPerVertex = bpv.IsCreated ? bpv.ToArray() : Array.Empty<byte>();
            var weights = _mesh.GetAllBoneWeights();
            _allWeights = weights.IsCreated ? weights.ToArray() : Array.Empty<BoneWeight1>();
            _weightOffsets = new int[_bonesPerVertex.Length];
            int cursor = 0;
            for (int i = 0; i < _bonesPerVertex.Length; i++) {
                _weightOffsets[i] = cursor;
                cursor += _bonesPerVertex[i];
            }

            _adjacency = MeshSculptCore.BuildAdjacency(_mesh);
        }

        public void RebuildSnapshot() {
            if (!IsValid) return;
            _renderer.BakeMesh(_bakedMesh, useScale: true);

            var baked = _bakedMesh.vertices;
            var matrix = _renderer.transform.localToWorldMatrix;
            _bakedWorldVertices = new Vector3[baked.Length];
            for (int i = 0; i < baked.Length; i++) {
                _bakedWorldVertices[i] = matrix.MultiplyPoint3x4(baked[i]);
            }

            var normals = _bakedMesh.normals;
            _bakedWorldNormals = new Vector3[baked.Length];
            for (int i = 0; i < _bakedWorldNormals.Length; i++) {
                if (normals != null && i < normals.Length) {
                    _bakedWorldNormals[i] = matrix.MultiplyVector(normals[i]).normalized;
                } else {
                    _bakedWorldNormals[i] = Vector3.up;
                }
            }
        }

        public bool Raycast(Ray ray, out MeshSculptCore.MeshHit hit) {
            if (_bakedMesh == null || _bakedWorldVertices == null) {
                hit = default;
                return false;
            }
            return MeshSculptCore.TryRaycast(_bakedMesh, _bakedWorldVertices, ray, out hit);
        }

        public int NearestVertexOnHit(MeshSculptCore.MeshHit hit) {
            return MeshSculptCore.NearestVertexOnHit(hit, _bakedWorldVertices);
        }

        public Vector3 BakedWorldVertex(int index) {
            if (_bakedWorldVertices == null || index < 0 || index >= _bakedWorldVertices.Length) return Vector3.zero;
            return _bakedWorldVertices[index];
        }

        public Vector3 SelectionCenterWorld() {
            if (_selectionOrder.Count == 0) return Vector3.zero;
            var sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < _selectionOrder.Count; i++) {
                int v = _selectionOrder[i];
                if (_bakedWorldVertices == null || v < 0 || v >= _bakedWorldVertices.Length) continue;
                sum += _bakedWorldVertices[v];
                count++;
            }
            return count > 0 ? sum / count : Vector3.zero;
        }

        public void SelectVertex(int index, bool additive, bool toggle) {
            if (index < 0 || index >= VertexCount) return;
            if (!additive && !toggle) ClearSelection();

            if (toggle && _selectionSet.Contains(index)) {
                _selectionSet.Remove(index);
                _selectionOrder.Remove(index);
                return;
            }

            if (_selectionSet.Add(index)) _selectionOrder.Add(index);
        }

        public void ClearSelection() {
            _selectionSet.Clear();
            _selectionOrder.Clear();
        }

        public IReadOnlyList<int> VerticesInRadius(Vector3 worldCenter, float radius, List<float> weights) {
            var list = new List<int>();
            weights?.Clear();
            if (_bakedWorldVertices == null || radius <= 0f) return list;
            for (int i = 0; i < _bakedWorldVertices.Length; i++) {
                float dist = Vector3.Distance(_bakedWorldVertices[i], worldCenter);
                float w = MeshSculptCore.BrushWeight(dist, radius);
                if (w <= 0f) continue;
                list.Add(i);
                weights?.Add(w);
            }
            return list;
        }

        public void MoveSelected(Vector3 worldDelta, string undoLabel) {
            ApplyWorldDeltas(_selectionOrder, null, worldDelta, 1f, undoLabel);
        }

        public void ApplyGrabBrush(Vector3 worldCenter, float radius, Vector3 worldDelta, float strength, string undoLabel) {
            var weights = new List<float>();
            var indices = VerticesInRadius(worldCenter, radius, weights);
            ApplyWorldDeltas(indices, weights, worldDelta, strength, undoLabel);
        }

        public void ApplyInflateBrush(Vector3 worldCenter, float radius, float amount, string undoLabel) {
            var weights = new List<float>();
            var indices = VerticesInRadius(worldCenter, radius, weights);
            if (indices.Count == 0) return;
            RegisterMeshUndo(undoLabel);
            for (int i = 0; i < indices.Count; i++) {
                int v = indices[i];
                var normal = _bakedWorldNormals != null && v >= 0 && v < _bakedWorldNormals.Length
                    ? _bakedWorldNormals[v]
                    : Vector3.up;
                _vertices[v] += WorldDeltaToBaseDelta(v, normal * (amount * weights[i]));
            }
            CommitVertices();
        }

        public void ApplySmoothBrush(Vector3 worldCenter, float radius, float strength, string undoLabel) {
            var weights = new List<float>();
            var indices = VerticesInRadius(worldCenter, radius, weights);
            if (indices.Count == 0) return;
            RegisterMeshUndo(undoLabel);
            MeshSculptCore.ApplySmooth(_vertices, indices, _adjacency, strength, weights);
            CommitVertices();
        }

        public bool FillSelectedFace(int submesh, bool flip, out string error) {
            error = null;
            if (_selectionOrder.Count < 3) {
                error = "Select three or four vertices first.";
                return false;
            }
            RegisterMeshUndo("Avatar QoL: Fill sculpt face");
            bool ok = MeshSculptCore.TryAppendFace(_mesh, _selectionOrder, submesh, flip, out error);
            if (!ok) return false;
            EditorUtility.SetDirty(_mesh);
            UpdateRendererBounds();
            ReloadMeshData();
            RebuildSnapshot();
            return true;
        }

        private void ApplyWorldDeltas(
                IReadOnlyList<int> indices,
                IReadOnlyList<float> weights,
                Vector3 worldDelta,
                float strength,
                string undoLabel) {
            if (indices == null || indices.Count == 0 || worldDelta.sqrMagnitude <= 0f) return;
            RegisterMeshUndo(undoLabel);
            for (int i = 0; i < indices.Count; i++) {
                int v = indices[i];
                if (v < 0 || v >= _vertices.Length) continue;
                float w = weights != null && i < weights.Count ? weights[i] : 1f;
                _vertices[v] += WorldDeltaToBaseDelta(v, worldDelta * (strength * w));
            }
            CommitVertices();
        }

        private void CommitVertices() {
            _mesh.vertices = _vertices;
            _mesh.RecalculateBounds();
            EditorUtility.SetDirty(_mesh);
            UpdateRendererBounds();
            RebuildSnapshot();
            SceneView.RepaintAll();
        }

        private void RegisterMeshUndo(string undoLabel) {
            if (_mesh == null) return;
            Undo.RegisterCompleteObjectUndo(_mesh, undoLabel);
        }

        private void UpdateRendererBounds() {
            if (_renderer == null || _mesh == null) return;
            var bounds = _mesh.bounds;
            bounds.Expand(0.02f);
            Undo.RecordObject(_renderer, "Avatar QoL: Update sculpt bounds");
            _renderer.localBounds = bounds;
            EditorUtility.SetDirty(_renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(_renderer);
        }

        private Vector3 WorldDeltaToBaseDelta(int vertexIndex, Vector3 worldDelta) {
            if (worldDelta.sqrMagnitude <= 0f) return Vector3.zero;
            if (_bones == null || _bindposes == null || _bonesPerVertex == null || _allWeights == null
                    || vertexIndex < 0 || vertexIndex >= _bonesPerVertex.Length
                    || vertexIndex >= _weightOffsets.Length) {
                return _renderer.transform.worldToLocalMatrix.MultiplyVector(worldDelta);
            }

            int start = _weightOffsets[vertexIndex];
            int count = _bonesPerVertex[vertexIndex];
            if (count <= 0 || start < 0 || start + count > _allWeights.Length) {
                return _renderer.transform.worldToLocalMatrix.MultiplyVector(worldDelta);
            }

            var blended = new Matrix4x4();
            blended.m33 = 1f;
            int dominantBone = -1;
            float dominantWeight = -1f;
            float total = 0f;
            for (int i = 0; i < count; i++) {
                var bw = _allWeights[start + i];
                int b = bw.boneIndex;
                if (b < 0 || _bones == null || b >= _bones.Length || b >= _bindposes.Length) continue;
                if (_bones[b] == null) continue;
                float w = Mathf.Max(0f, bw.weight);
                if (w <= 0f) continue;
                if (w > dominantWeight) {
                    dominantWeight = w;
                    dominantBone = b;
                }
                AddLinearWeighted(ref blended, _bones[b].localToWorldMatrix * _bindposes[b], w);
                total += w;
            }

            if (total <= 1e-5f || dominantBone < 0) {
                return _renderer.transform.worldToLocalMatrix.MultiplyVector(worldDelta);
            }

            ScaleLinear(ref blended, 1f / total);
            var baseDelta = blended.inverse.MultiplyVector(worldDelta);
            if (IsUsableDelta(baseDelta, worldDelta)) return baseDelta;

            var dominant = _bones[dominantBone].localToWorldMatrix * _bindposes[dominantBone];
            baseDelta = dominant.inverse.MultiplyVector(worldDelta);
            if (IsUsableDelta(baseDelta, worldDelta)) return baseDelta;

            return _renderer.transform.worldToLocalMatrix.MultiplyVector(worldDelta);
        }

        private static bool IsUsableDelta(Vector3 baseDelta, Vector3 sourceDelta) {
            if (float.IsNaN(baseDelta.x) || float.IsNaN(baseDelta.y) || float.IsNaN(baseDelta.z)) return false;
            if (float.IsInfinity(baseDelta.x) || float.IsInfinity(baseDelta.y) || float.IsInfinity(baseDelta.z)) return false;
            float max = Mathf.Max(10f, sourceDelta.magnitude * 1000f);
            return baseDelta.magnitude <= max;
        }

        private static void AddLinearWeighted(ref Matrix4x4 dst, Matrix4x4 src, float weight) {
            dst.m00 += src.m00 * weight; dst.m01 += src.m01 * weight; dst.m02 += src.m02 * weight;
            dst.m10 += src.m10 * weight; dst.m11 += src.m11 * weight; dst.m12 += src.m12 * weight;
            dst.m20 += src.m20 * weight; dst.m21 += src.m21 * weight; dst.m22 += src.m22 * weight;
        }

        private static void ScaleLinear(ref Matrix4x4 m, float scale) {
            m.m00 *= scale; m.m01 *= scale; m.m02 *= scale;
            m.m10 *= scale; m.m11 *= scale; m.m12 *= scale;
            m.m20 *= scale; m.m21 *= scale; m.m22 *= scale;
        }
    }
}
