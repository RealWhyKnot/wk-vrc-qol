// MeshSpatialQueries.cs
//
// Flat-grid triangle indexing plus nearest and projected correspondence
// queries for editor mesh tools.

using System;
using UnityEngine;

namespace WhyKnot.AvatarQol.Geometry {

    internal sealed class MeshSpatialGrid {
        public int dim;
        public Vector3 origin;
        public Vector3 cellSize;
        public int[] cellOffsets;
        public int[] cellTriangles;
        public float minCellExtent;
    }

    internal struct MeshClosestHit {
        public int triangleIndex;
        public Vector3 point;
        public float wa, wb, wc;
        public float distance;
    }

    internal enum MeshProjectionRejectReason {
        None,
        RayMiss,
        Distance,
        NormalAngle,
        Backface,
    }

    internal struct MeshProjectionHit {
        public int triangleIndex;
        public Vector3 point;
        public float wa, wb, wc;
        public float distance;
        public float normalDot;
        public MeshProjectionRejectReason rejectReason;
    }

    internal static class MeshSpatialQueries {

        internal static MeshSpatialGrid BuildGrid(Vector3[] verts, int[] triangles, int dim) {
            dim = Mathf.Max(1, dim);
            int cellCount = dim * dim * dim;

            if (verts == null || verts.Length == 0 || triangles == null || triangles.Length < 3) {
                return new MeshSpatialGrid {
                    dim = dim,
                    origin = Vector3.zero,
                    cellSize = Vector3.one,
                    cellOffsets = new int[cellCount + 1],
                    cellTriangles = Array.Empty<int>(),
                    minCellExtent = 1f,
                };
            }

            Vector3 min = verts[0];
            Vector3 max = verts[0];
            for (int i = 1; i < verts.Length; i++) {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            Vector3 size = max - min;
            Vector3 pad = new Vector3(
                Mathf.Max(size.x, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.y, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.z, 1e-4f) * 0.001f + 1e-4f);
            min -= pad;
            max += pad;
            Vector3 cell = (max - min) / dim;
            cell.x = Mathf.Max(cell.x, 1e-6f);
            cell.y = Mathf.Max(cell.y, 1e-6f);
            cell.z = Mathf.Max(cell.z, 1e-6f);

            int triCount = triangles.Length / 3;
            var counts = new int[cellCount];

            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                Vector3 a = verts[i0];
                Vector3 b = verts[i1];
                Vector3 c = verts[i2];
                Vector3 tMin = Vector3.Min(Vector3.Min(a, b), c);
                Vector3 tMax = Vector3.Max(Vector3.Max(a, b), c);
                int x0 = Mathf.Clamp(Mathf.FloorToInt((tMin.x - min.x) / cell.x), 0, dim - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((tMin.y - min.y) / cell.y), 0, dim - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((tMin.z - min.z) / cell.z), 0, dim - 1);
                int x1 = Mathf.Clamp(Mathf.FloorToInt((tMax.x - min.x) / cell.x), 0, dim - 1);
                int y1 = Mathf.Clamp(Mathf.FloorToInt((tMax.y - min.y) / cell.y), 0, dim - 1);
                int z1 = Mathf.Clamp(Mathf.FloorToInt((tMax.z - min.z) / cell.z), 0, dim - 1);
                for (int z = z0; z <= z1; z++) {
                    for (int y = y0; y <= y1; y++) {
                        int rowBase = (z * dim + y) * dim;
                        for (int x = x0; x <= x1; x++) counts[rowBase + x]++;
                    }
                }
            }

            var offsets = new int[cellCount + 1];
            int sum = 0;
            for (int i = 0; i < cellCount; i++) {
                offsets[i] = sum;
                sum += counts[i];
            }
            offsets[cellCount] = sum;

            var cellTriangles = new int[sum];
            Array.Clear(counts, 0, cellCount);
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                Vector3 a = verts[i0];
                Vector3 b = verts[i1];
                Vector3 c = verts[i2];
                Vector3 tMin = Vector3.Min(Vector3.Min(a, b), c);
                Vector3 tMax = Vector3.Max(Vector3.Max(a, b), c);
                int x0 = Mathf.Clamp(Mathf.FloorToInt((tMin.x - min.x) / cell.x), 0, dim - 1);
                int y0 = Mathf.Clamp(Mathf.FloorToInt((tMin.y - min.y) / cell.y), 0, dim - 1);
                int z0 = Mathf.Clamp(Mathf.FloorToInt((tMin.z - min.z) / cell.z), 0, dim - 1);
                int x1 = Mathf.Clamp(Mathf.FloorToInt((tMax.x - min.x) / cell.x), 0, dim - 1);
                int y1 = Mathf.Clamp(Mathf.FloorToInt((tMax.y - min.y) / cell.y), 0, dim - 1);
                int z1 = Mathf.Clamp(Mathf.FloorToInt((tMax.z - min.z) / cell.z), 0, dim - 1);
                for (int z = z0; z <= z1; z++) {
                    for (int y = y0; y <= y1; y++) {
                        int rowBase = (z * dim + y) * dim;
                        for (int x = x0; x <= x1; x++) {
                            int idx = rowBase + x;
                            cellTriangles[offsets[idx] + counts[idx]++] = t;
                        }
                    }
                }
            }

            return new MeshSpatialGrid {
                dim = dim,
                origin = min,
                cellSize = cell,
                cellOffsets = offsets,
                cellTriangles = cellTriangles,
                minCellExtent = Mathf.Min(cell.x, Mathf.Min(cell.y, cell.z)),
            };
        }

        internal static MeshClosestHit QueryClosest(
                MeshSpatialGrid grid, Vector3[] verts, int[] triangles, Vector3 query) {
            var hit = new MeshClosestHit { triangleIndex = -1, distance = float.PositiveInfinity };
            if (grid == null || grid.cellTriangles == null || grid.cellTriangles.Length == 0) return hit;

            int dim = grid.dim;
            int qx = Mathf.Clamp(Mathf.FloorToInt((query.x - grid.origin.x) / grid.cellSize.x), 0, dim - 1);
            int qy = Mathf.Clamp(Mathf.FloorToInt((query.y - grid.origin.y) / grid.cellSize.y), 0, dim - 1);
            int qz = Mathf.Clamp(Mathf.FloorToInt((query.z - grid.origin.z) / grid.cellSize.z), 0, dim - 1);

            float bestDistSq = float.PositiveInfinity;
            int radius = 0;
            int maxRadius = dim;
            while (radius <= maxRadius) {
                int x0 = Mathf.Max(0, qx - radius);
                int y0 = Mathf.Max(0, qy - radius);
                int z0 = Mathf.Max(0, qz - radius);
                int x1 = Mathf.Min(dim - 1, qx + radius);
                int y1 = Mathf.Min(dim - 1, qy + radius);
                int z1 = Mathf.Min(dim - 1, qz + radius);

                for (int z = z0; z <= z1; z++) {
                    for (int y = y0; y <= y1; y++) {
                        int rowBase = (z * dim + y) * dim;
                        for (int x = x0; x <= x1; x++) {
                            if (radius > 0
                                    && x > qx - radius && x < qx + radius
                                    && y > qy - radius && y < qy + radius
                                    && z > qz - radius && z < qz + radius) {
                                continue;
                            }
                            int idx = rowBase + x;
                            int start = grid.cellOffsets[idx];
                            int end = grid.cellOffsets[idx + 1];
                            for (int j = start; j < end; j++) {
                                int t = grid.cellTriangles[j];
                                int i0 = triangles[t * 3];
                                int i1 = triangles[t * 3 + 1];
                                int i2 = triangles[t * 3 + 2];
                                Vector3 a = verts[i0];
                                Vector3 b = verts[i1];
                                Vector3 c = verts[i2];

                                float ax = a.x < b.x ? a.x : b.x; if (c.x < ax) ax = c.x;
                                float bx = a.x > b.x ? a.x : b.x; if (c.x > bx) bx = c.x;
                                float ay = a.y < b.y ? a.y : b.y; if (c.y < ay) ay = c.y;
                                float by = a.y > b.y ? a.y : b.y; if (c.y > by) by = c.y;
                                float az = a.z < b.z ? a.z : b.z; if (c.z < az) az = c.z;
                                float bz = a.z > b.z ? a.z : b.z; if (c.z > bz) bz = c.z;
                                float dxAabb = query.x < ax ? ax - query.x : (query.x > bx ? query.x - bx : 0f);
                                float dyAabb = query.y < ay ? ay - query.y : (query.y > by ? query.y - by : 0f);
                                float dzAabb = query.z < az ? az - query.z : (query.z > bz ? query.z - bz : 0f);
                                float aabbDistSq = dxAabb * dxAabb + dyAabb * dyAabb + dzAabb * dzAabb;
                                if (aabbDistSq > bestDistSq) continue;

                                Vector3 cp = MeshGeometry.ClosestPointOnTriangle(
                                    query, a, b, c, out float w0, out float w1, out float w2);
                                float dSq = (query - cp).sqrMagnitude;
                                if (dSq < bestDistSq) {
                                    bestDistSq = dSq;
                                    hit.triangleIndex = t;
                                    hit.point = cp;
                                    hit.wa = w0;
                                    hit.wb = w1;
                                    hit.wc = w2;
                                }
                            }
                        }
                    }
                }

                if (hit.triangleIndex >= 0) {
                    float shellMin = radius * grid.minCellExtent;
                    if (shellMin * shellMin >= bestDistSq) break;
                }
                radius++;
            }

            hit.distance = hit.triangleIndex >= 0 ? Mathf.Sqrt(bestDistSq) : float.PositiveInfinity;
            return hit;
        }

        internal static MeshProjectionHit QueryProjected(
                MeshSpatialGrid grid,
                Vector3[] verts,
                int[] triangles,
                Vector3[] faceNormals,
                Vector3 targetPoint,
                Vector3 targetNormal,
                float frontalDistance,
                float rearDistance,
                float minNormalDot,
                bool rejectBackfaces,
                bool bidirectional) {
            var best = QueryProjectedOneWay(
                grid, verts, triangles, faceNormals,
                targetPoint, targetNormal, targetNormal,
                frontalDistance, rearDistance,
                minNormalDot, rejectBackfaces);

            if (!bidirectional || best.triangleIndex >= 0) return best;

            var reverse = QueryProjectedOneWay(
                grid, verts, triangles, faceNormals,
                targetPoint, -targetNormal, targetNormal,
                rearDistance, frontalDistance,
                minNormalDot, rejectBackfaces);

            if (reverse.triangleIndex >= 0) return reverse;
            return best.rejectReason != MeshProjectionRejectReason.RayMiss ? best : reverse;
        }

        private static MeshProjectionHit QueryProjectedOneWay(
                MeshSpatialGrid grid,
                Vector3[] verts,
                int[] triangles,
                Vector3[] faceNormals,
                Vector3 targetPoint,
                Vector3 projectionNormal,
                Vector3 compareNormal,
                float frontalDistance,
                float rearDistance,
                float minNormalDot,
                bool rejectBackfaces) {
            var result = new MeshProjectionHit {
                triangleIndex = -1,
                distance = float.PositiveInfinity,
                normalDot = -1f,
                rejectReason = MeshProjectionRejectReason.RayMiss,
            };
            if (grid == null || grid.cellTriangles == null || grid.cellTriangles.Length == 0) return result;

            projectionNormal = MeshGeometry.SafeNormalize(projectionNormal, Vector3.up);
            compareNormal = MeshGeometry.SafeNormalize(compareNormal, projectionNormal);
            frontalDistance = Mathf.Max(0.0001f, frontalDistance);
            rearDistance = Mathf.Max(0.0001f, rearDistance);

            Vector3 rayOrigin = targetPoint + projectionNormal * frontalDistance;
            Vector3 rayDir = -projectionNormal;
            float rayLength = frontalDistance + rearDistance;
            Vector3 rayEnd = rayOrigin + rayDir * rayLength;

            int dim = grid.dim;
            Vector3 min = Vector3.Min(rayOrigin, rayEnd);
            Vector3 max = Vector3.Max(rayOrigin, rayEnd);
            float pad = Mathf.Max(grid.minCellExtent * 0.5f, 1e-5f);
            min -= new Vector3(pad, pad, pad);
            max += new Vector3(pad, pad, pad);

            int x0 = Mathf.Clamp(Mathf.FloorToInt((min.x - grid.origin.x) / grid.cellSize.x), 0, dim - 1);
            int y0 = Mathf.Clamp(Mathf.FloorToInt((min.y - grid.origin.y) / grid.cellSize.y), 0, dim - 1);
            int z0 = Mathf.Clamp(Mathf.FloorToInt((min.z - grid.origin.z) / grid.cellSize.z), 0, dim - 1);
            int x1 = Mathf.Clamp(Mathf.FloorToInt((max.x - grid.origin.x) / grid.cellSize.x), 0, dim - 1);
            int y1 = Mathf.Clamp(Mathf.FloorToInt((max.y - grid.origin.y) / grid.cellSize.y), 0, dim - 1);
            int z1 = Mathf.Clamp(Mathf.FloorToInt((max.z - grid.origin.z) / grid.cellSize.z), 0, dim - 1);

            float bestRayT = float.PositiveInfinity;
            bool sawBackfaceReject = false;
            bool sawNormalReject = false;
            bool sawRawHit = false;

            for (int z = z0; z <= z1; z++) {
                for (int y = y0; y <= y1; y++) {
                    int rowBase = (z * dim + y) * dim;
                    for (int x = x0; x <= x1; x++) {
                        int cell = rowBase + x;
                        int start = grid.cellOffsets[cell];
                        int end = grid.cellOffsets[cell + 1];
                        for (int j = start; j < end; j++) {
                            int tri = grid.cellTriangles[j];
                            int i0 = triangles[tri * 3];
                            int i1 = triangles[tri * 3 + 1];
                            int i2 = triangles[tri * 3 + 2];
                            if (!MeshGeometry.RayTriangle(rayOrigin, rayDir, verts[i0], verts[i1], verts[i2],
                                    rayLength, out float rayT, out float wa, out float wb, out float wc)) {
                                continue;
                            }
                            sawRawHit = true;
                            if (rayT >= bestRayT) continue;

                            Vector3 sourceNormal = faceNormals != null && tri < faceNormals.Length
                                ? faceNormals[tri]
                                : Vector3.zero;
                            sourceNormal = MeshGeometry.SafeNormalize(sourceNormal,
                                Vector3.Cross(verts[i1] - verts[i0], verts[i2] - verts[i0]));

                            if (rejectBackfaces && Vector3.Dot(rayDir, sourceNormal) >= -1e-5f) {
                                sawBackfaceReject = true;
                                continue;
                            }

                            float normalDot = Vector3.Dot(compareNormal, sourceNormal);
                            if (minNormalDot > -1.01f && normalDot < minNormalDot) {
                                sawNormalReject = true;
                                continue;
                            }

                            Vector3 point = rayOrigin + rayDir * rayT;
                            bestRayT = rayT;
                            result.triangleIndex = tri;
                            result.point = point;
                            result.wa = wa;
                            result.wb = wb;
                            result.wc = wc;
                            result.distance = (point - targetPoint).magnitude;
                            result.normalDot = normalDot;
                            result.rejectReason = MeshProjectionRejectReason.None;
                        }
                    }
                }
            }

            if (result.triangleIndex >= 0) return result;
            if (sawNormalReject) result.rejectReason = MeshProjectionRejectReason.NormalAngle;
            else if (sawBackfaceReject) result.rejectReason = MeshProjectionRejectReason.Backface;
            else if (sawRawHit) result.rejectReason = MeshProjectionRejectReason.NormalAngle;
            else result.rejectReason = MeshProjectionRejectReason.RayMiss;
            return result;
        }
    }
}
