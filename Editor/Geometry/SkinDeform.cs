// SkinDeform.cs
//
// Per-vertex skinning matrices for edit-time mesh correspondence.

using Unity.Collections;
using UnityEngine;

namespace WhyKnot.AvatarQol.Geometry {

    internal static class SkinDeform {

        internal static Matrix4x4[] ComputeSkinMatrices(SkinnedMeshRenderer renderer, Mesh mesh) {
            if (renderer == null || mesh == null) return new Matrix4x4[0];
            int vertexCount = mesh.vertexCount;
            var output = new Matrix4x4[vertexCount];
            var fallback = renderer.localToWorldMatrix;

            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (!bpv.IsCreated || bpv.Length != vertexCount
                    || bones == null || bindposes == null
                    || bones.Length == 0 || bindposes.Length == 0) {
                Fill(output, fallback);
                return output;
            }

            int cursor = 0;
            for (int v = 0; v < vertexCount; v++) {
                int count = bpv[v];
                if (count == 0) {
                    output[v] = fallback;
                    continue;
                }

                var skin = new Matrix4x4();
                float sum = 0f;
                for (int k = 0; k < count && cursor + k < weights.Length; k++) {
                    var bw = weights[cursor + k];
                    int boneIndex = bw.boneIndex;
                    if (boneIndex < 0 || boneIndex >= bones.Length || boneIndex >= bindposes.Length) continue;
                    var bone = bones[boneIndex];
                    if (bone == null) continue;
                    AddWeighted(ref skin, bone.localToWorldMatrix * bindposes[boneIndex], bw.weight);
                    sum += bw.weight;
                }
                cursor += count;

                if (sum <= 1e-6f) {
                    output[v] = fallback;
                } else {
                    if (!Mathf.Approximately(sum, 1f)) Scale(ref skin, 1f / sum);
                    output[v] = skin;
                }
            }
            return output;
        }

        internal static Vector3[] TransformPoints(Matrix4x4[] skinMatrices, Vector3[] localPoints) {
            if (skinMatrices == null || localPoints == null) return new Vector3[0];
            int count = Mathf.Min(skinMatrices.Length, localPoints.Length);
            var output = new Vector3[count];
            for (int i = 0; i < count; i++) output[i] = skinMatrices[i].MultiplyPoint3x4(localPoints[i]);
            return output;
        }

        internal static Vector3[] TransformVectors(Matrix4x4[] skinMatrices, Vector3[] localVectors) {
            if (skinMatrices == null || localVectors == null) return new Vector3[0];
            int count = Mathf.Min(skinMatrices.Length, localVectors.Length);
            var output = new Vector3[count];
            for (int i = 0; i < count; i++) output[i] = skinMatrices[i].MultiplyVector(localVectors[i]);
            return output;
        }

        internal static bool InverseMultiplyVector(Matrix4x4 matrix, Vector3 worldVector, out Vector3 localVector) {
            localVector = Vector3.zero;
            if (Mathf.Abs(matrix.determinant) < 1e-8f) return false;
            localVector = matrix.inverse.MultiplyVector(worldVector);
            return true;
        }

        internal static Bounds ComputeBounds(Vector3[] points) {
            if (points == null || points.Length == 0) return new Bounds(Vector3.zero, Vector3.zero);
            var bounds = new Bounds(points[0], Vector3.zero);
            for (int i = 1; i < points.Length; i++) bounds.Encapsulate(points[i]);
            return bounds;
        }

        internal static Bounds Expand(Bounds bounds, float amount) {
            if (amount <= 0f) return bounds;
            bounds.Expand(amount * 2f);
            return bounds;
        }

        private static void Fill(Matrix4x4[] output, Matrix4x4 value) {
            for (int i = 0; i < output.Length; i++) output[i] = value;
        }

        private static void AddWeighted(ref Matrix4x4 dst, Matrix4x4 src, float weight) {
            for (int i = 0; i < 16; i++) dst[i] += src[i] * weight;
        }

        private static void Scale(ref Matrix4x4 dst, float scale) {
            for (int i = 0; i < 16; i++) dst[i] *= scale;
        }
    }
}
