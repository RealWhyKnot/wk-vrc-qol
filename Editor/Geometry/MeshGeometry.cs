// MeshGeometry.cs
//
// Reusable mesh-space math for editor tools that need triangle
// correspondence, normal reconstruction, and mesh/submesh extraction.

using System;
using UnityEngine;

namespace WhyKnot.AvatarQol.Geometry {

    internal static class MeshGeometry {

        internal static Vector3 ClosestPointOnTriangle(
                Vector3 p, Vector3 a, Vector3 b, Vector3 c,
                out float wa, out float wb, out float wc) {
            Vector3 ab = b - a;
            Vector3 ac = c - a;
            Vector3 ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) {
                wa = 1f; wb = 0f; wc = 0f;
                return a;
            }

            Vector3 bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) {
                wa = 0f; wb = 1f; wc = 0f;
                return b;
            }

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
                float v = d1 / (d1 - d3);
                wa = 1f - v; wb = v; wc = 0f;
                return a + v * ab;
            }

            Vector3 cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) {
                wa = 0f; wb = 0f; wc = 1f;
                return c;
            }

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
                float w = d2 / (d2 - d6);
                wa = 1f - w; wb = 0f; wc = w;
                return a + w * ac;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                wa = 0f; wb = 1f - w; wc = w;
                return b + w * (c - b);
            }

            float denom = 1f / (va + vb + vc);
            float vBary = vb * denom;
            float wBary = vc * denom;
            wa = 1f - vBary - wBary; wb = vBary; wc = wBary;
            return a + ab * vBary + ac * wBary;
        }

        internal static bool RayTriangle(
                Vector3 origin,
                Vector3 dir,
                Vector3 a,
                Vector3 b,
                Vector3 c,
                float maxDistance,
                out float rayT,
                out float wa,
                out float wb,
                out float wc) {
            rayT = 0f;
            wa = wb = wc = 0f;

            Vector3 edge1 = b - a;
            Vector3 edge2 = c - a;
            Vector3 pvec = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, pvec);
            if (Mathf.Abs(det) < 1e-8f) return false;

            float invDet = 1f / det;
            Vector3 tvec = origin - a;
            float u = Vector3.Dot(tvec, pvec) * invDet;
            if (u < -1e-6f || u > 1f + 1e-6f) return false;

            Vector3 qvec = Vector3.Cross(tvec, edge1);
            float v = Vector3.Dot(dir, qvec) * invDet;
            if (v < -1e-6f || u + v > 1f + 1e-6f) return false;

            float t = Vector3.Dot(edge2, qvec) * invDet;
            if (t < -1e-6f || t > maxDistance + 1e-6f) return false;

            rayT = Mathf.Max(0f, t);
            wb = Mathf.Clamp01(u);
            wc = Mathf.Clamp01(v);
            wa = Mathf.Clamp01(1f - wb - wc);
            float sum = wa + wb + wc;
            if (sum > 1e-6f) {
                wa /= sum;
                wb /= sum;
                wc /= sum;
            }
            return true;
        }

        internal static Vector3[] TransformVertices(Vector3[] src, Matrix4x4 m) {
            if (src == null) return Array.Empty<Vector3>();
            if (m.isIdentity) return src;
            var dst = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = m.MultiplyPoint3x4(src[i]);
            return dst;
        }

        internal static Vector3[] TransformNormals(Vector3[] src, Matrix4x4 m) {
            if (src == null) return Array.Empty<Vector3>();
            var dst = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) {
                dst[i] = SafeNormalize(m.MultiplyVector(src[i]), Vector3.up);
            }
            return dst;
        }

        internal static Vector3[] ResolveNormals(Mesh mesh) {
            if (mesh == null) return Array.Empty<Vector3>();
            var normals = mesh.normals;
            if (normals != null && normals.Length == mesh.vertexCount) return normals;
            return ComputeAveragedNormals(mesh.vertices, mesh.triangles);
        }

        internal static Vector3[] ComputeAveragedNormals(Vector3[] verts, int[] triangles) {
            if (verts == null) return Array.Empty<Vector3>();
            var normals = new Vector3[verts.Length];
            if (triangles != null) {
                int triCount = triangles.Length / 3;
                for (int t = 0; t < triCount; t++) {
                    int i0 = triangles[t * 3];
                    int i1 = triangles[t * 3 + 1];
                    int i2 = triangles[t * 3 + 2];
                    if (i0 < 0 || i0 >= verts.Length
                            || i1 < 0 || i1 >= verts.Length
                            || i2 < 0 || i2 >= verts.Length) {
                        continue;
                    }
                    Vector3 n = Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]);
                    normals[i0] += n;
                    normals[i1] += n;
                    normals[i2] += n;
                }
            }
            for (int i = 0; i < normals.Length; i++) {
                normals[i] = SafeNormalize(normals[i], Vector3.up);
            }
            return normals;
        }

        internal static Vector3[] ComputeFaceNormals(Vector3[] verts, int[] triangles) {
            if (verts == null || triangles == null) return Array.Empty<Vector3>();
            int triCount = triangles.Length / 3;
            var normals = new Vector3[triCount];
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 < 0 || i0 >= verts.Length
                        || i1 < 0 || i1 >= verts.Length
                        || i2 < 0 || i2 >= verts.Length) {
                    normals[t] = Vector3.up;
                    continue;
                }
                normals[t] = SafeNormalize(
                    Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]),
                    Vector3.up);
            }
            return normals;
        }

        internal static Vector3 InterpolateNormal(
                Vector3[] normals,
                Vector3[] verts,
                int[] triangles,
                int triangleIndex,
                int i0,
                int i1,
                int i2,
                float wa,
                float wb,
                float wc) {
            if (normals != null
                    && i0 >= 0 && i0 < normals.Length
                    && i1 >= 0 && i1 < normals.Length
                    && i2 >= 0 && i2 < normals.Length) {
                return SafeNormalize(normals[i0] * wa + normals[i1] * wb + normals[i2] * wc, Vector3.up);
            }
            if (verts != null && triangles != null
                    && triangleIndex >= 0
                    && triangleIndex * 3 + 2 < triangles.Length
                    && i0 >= 0 && i0 < verts.Length
                    && i1 >= 0 && i1 < verts.Length
                    && i2 >= 0 && i2 < verts.Length) {
                return SafeNormalize(Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]), Vector3.up);
            }
            return Vector3.up;
        }

        internal static float MinNormalDot(float angleDegrees) {
            if (angleDegrees <= 0f || angleDegrees >= 179.9f) return -2f;
            return Mathf.Cos(angleDegrees * Mathf.Deg2Rad);
        }

        internal static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
            float sqr = value.sqrMagnitude;
            if (sqr < 1e-12f || float.IsNaN(sqr)) return fallback;
            return value / Mathf.Sqrt(sqr);
        }

        internal static int[] ResolveTriangles(Mesh mesh, int submeshIndex) {
            if (mesh == null) return Array.Empty<int>();
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return mesh.triangles;
            return mesh.GetTriangles(submeshIndex);
        }

        internal static int PickGridDim(int triangleCount) {
            if (triangleCount <= 0) return 4;
            int dim = Mathf.RoundToInt(Mathf.Pow(triangleCount / 6f, 1f / 3f));
            return Mathf.Clamp(dim, 4, 64);
        }
    }
}
