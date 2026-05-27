// SourceSurfaceIndex.cs
//
// Spatial correspondence over a source mesh surface. This adapts the UV
// texture-transfer grid/raycast helpers for weight transfer: each target
// vertex asks for a projected or nearest source triangle, then the solver
// barycentrically blends that triangle's source weights.

using System;
using System.Collections.Generic;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Weighting {

    internal struct SurfaceMatch {
        public int TriangleIndex;
        public int I0;
        public int I1;
        public int I2;
        public int Submesh;
        public Vector3 Point;
        public Vector3 Normal;
        public Vector3 Barycentric;
        public float Distance;
        public float NormalDot;
        public bool Projected;
    }

    internal sealed class SourceSurfaceIndex {

        private readonly Vector3[] _positions;
        private readonly int[] _triangles;
        private readonly int[] _triangleSubmesh;
        private readonly Vector3[] _faceNormals;
        private readonly UvTextureTransferCore.SpatialGrid _grid;

        private SourceSurfaceIndex(
                Vector3[] positions,
                int[] triangles,
                int[] triangleSubmesh,
                Vector3[] faceNormals,
                UvTextureTransferCore.SpatialGrid grid) {
            _positions = positions ?? Array.Empty<Vector3>();
            _triangles = triangles ?? Array.Empty<int>();
            _triangleSubmesh = triangleSubmesh ?? Array.Empty<int>();
            _faceNormals = faceNormals ?? Array.Empty<Vector3>();
            _grid = grid;
        }

        public int TriangleCount => _triangles.Length / 3;

        public static SourceSurfaceIndex Build(SkinnedMeshSnapshot snapshot, int sourceSubmesh = -1) {
            if (snapshot == null || snapshot.Mesh == null) {
                return new SourceSurfaceIndex(
                    Array.Empty<Vector3>(),
                    Array.Empty<int>(),
                    Array.Empty<int>(),
                    Array.Empty<Vector3>(),
                    null);
            }

            var mesh = snapshot.Mesh;
            var tris = new List<int>();
            var submeshes = new List<int>();
            for (int s = 0; s < mesh.subMeshCount; s++) {
                if (sourceSubmesh >= 0 && s != sourceSubmesh) continue;
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var sourceTris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < sourceTris.Length; i += 3) {
                    tris.Add(sourceTris[i]);
                    tris.Add(sourceTris[i + 1]);
                    tris.Add(sourceTris[i + 2]);
                    submeshes.Add(s);
                }
            }

            var triArray = tris.ToArray();
            var normals = UvTextureTransferCore.ComputeFaceNormals(snapshot.Positions, triArray);
            int dim = UvTextureTransferCore.PickGridDim(triArray.Length / 3);
            var grid = UvTextureTransferCore.BuildSpatialGrid(snapshot.Positions, triArray, dim);
            return new SourceSurfaceIndex(snapshot.Positions, triArray, submeshes.ToArray(), normals, grid);
        }

        public bool TryProjected(
                Vector3 targetPoint,
                Vector3 targetNormal,
                float frontalDistance,
                float rearDistance,
                float minNormalDot,
                bool allowFlippedNormals,
                bool bidirectional,
                out SurfaceMatch match) {
            match = default;
            if (_grid == null || TriangleCount <= 0) return false;

            float queryMinDot = allowFlippedNormals ? -1.01f : minNormalDot;
            var hit = UvTextureTransferCore.QueryProjected(
                _grid,
                _positions,
                _triangles,
                _faceNormals,
                targetPoint,
                targetNormal,
                frontalDistance,
                rearDistance,
                queryMinDot,
                rejectBackfaces: false,
                bidirectional: bidirectional);

            if (hit.triangleIndex < 0) return false;
            if (!TryMakeMatch(hit.triangleIndex, hit.point, hit.wa, hit.wb, hit.wc, hit.distance,
                    targetNormal, projected: true, out match)) {
                return false;
            }
            if (!NormalGate(match.NormalDot, minNormalDot, allowFlippedNormals)) return false;
            return true;
        }

        public bool TryClosest(
                Vector3 targetPoint,
                Vector3 targetNormal,
                float maxDistance,
                float minNormalDot,
                bool allowFlippedNormals,
                out SurfaceMatch match) {
            match = default;
            if (_grid == null || TriangleCount <= 0) return false;

            var hit = UvTextureTransferCore.QueryClosest(_grid, _positions, _triangles, targetPoint);
            if (hit.triangleIndex < 0 || hit.distance > maxDistance) return false;
            if (!TryMakeMatch(hit.triangleIndex, hit.point, hit.wa, hit.wb, hit.wc, hit.distance,
                    targetNormal, projected: false, out match)) {
                return false;
            }
            if (!NormalGate(match.NormalDot, minNormalDot, allowFlippedNormals)) return false;
            return true;
        }

        private bool TryMakeMatch(
                int triangleIndex,
                Vector3 point,
                float wa,
                float wb,
                float wc,
                float distance,
                Vector3 targetNormal,
                bool projected,
                out SurfaceMatch match) {
            match = default;
            int offset = triangleIndex * 3;
            if (offset < 0 || offset + 2 >= _triangles.Length) return false;
            int i0 = _triangles[offset];
            int i1 = _triangles[offset + 1];
            int i2 = _triangles[offset + 2];
            if (i0 < 0 || i0 >= _positions.Length
                    || i1 < 0 || i1 >= _positions.Length
                    || i2 < 0 || i2 >= _positions.Length) {
                return false;
            }
            Vector3 normal = triangleIndex >= 0 && triangleIndex < _faceNormals.Length
                ? _faceNormals[triangleIndex]
                : Vector3.up;
            if (normal.sqrMagnitude < 1e-8f) normal = Vector3.up;
            normal.Normalize();
            targetNormal = targetNormal.sqrMagnitude > 1e-8f ? targetNormal.normalized : normal;

            match = new SurfaceMatch {
                TriangleIndex = triangleIndex,
                I0 = i0,
                I1 = i1,
                I2 = i2,
                Submesh = triangleIndex >= 0 && triangleIndex < _triangleSubmesh.Length ? _triangleSubmesh[triangleIndex] : 0,
                Point = point,
                Normal = normal,
                Barycentric = new Vector3(wa, wb, wc),
                Distance = distance,
                NormalDot = Vector3.Dot(targetNormal, normal),
                Projected = projected,
            };
            return true;
        }

        private static bool NormalGate(float dot, float minDot, bool allowFlippedNormals) {
            if (minDot <= -1f) return true;
            return allowFlippedNormals ? Mathf.Abs(dot) >= minDot : dot >= minDot;
        }
    }
}
