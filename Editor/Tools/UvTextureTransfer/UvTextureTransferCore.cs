// UvTextureTransferCore.cs
//
// Mesh-to-mesh texture transfer. Bake a texture authored against a
// source mesh's UV0 layout into the UV0 layout of a different target
// mesh by reconstructing target surface points, resolving source surface
// correspondence, then sampling the source texture through source UV0.
//
// Pipeline:
//   1. UV-rasterize the target mesh. For every output texel sample store
//      (target triangle index, barycentric weights). Quality mode repeats
//      this at several subpixel offsets and averages the results.
//   2. Build a flat uniform spatial grid over the source mesh's
//      triangles for cheap ray and nearest-triangle queries. The grid stores
//      a single int[] of triangle indices plus a parallel int[] of
//      per-cell offsets -- no per-cell List<int> allocation.
//   3. For each covered target texel, interpolate the target mesh's
//      vertex positions and normals to get a sample point and projection
//      direction. In the default avatar-safe mode, cast a bounded ray into
//      source triangles and reject hits by distance, normal angle, and
//      backface policy. Legacy closest-point remains available for
//      diagnostics and already-identical meshes.
//   4. Optionally dilate covered texels into adjacent uncovered texels
//      for standard UV island padding. This prevents transparent/black
//      fallback pixels from bleeding into island borders under bilinear
//      filtering in Unity.
//
// Performance: the per-texel work in step 3 runs under Parallel.For
// over output rows; the source texture is pre-read into a Color32[]
// so the parallel loop never touches a Texture2D method (those would
// fight the main thread). The implementation uses only .NET base
// types and Unity primitives -- no Burst, no Collections, no Jobs --
// because adding those would force every consumer to install the
// matching packages before the package compiles.
//
// Pure-math entry points (ClosestPointOnTriangle, BuildSpatialGrid,
// QueryClosest, QueryProjected, RasterizeTargetUv, ComputeBBoxAlignment,
// SampleBilinearClamp32) are static and side-effect-free, exercised
// directly from the Tests.Editor asmdef.

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class UvTextureTransferCore {

        // -----------------------------------------------------------------
        // Closest point on triangle (Ericson, Real-Time Collision Detection
        // section 5.1.5). Returns the closest point and the barycentric
        // weights (wa, wb, wc) which sum to 1. Branch-heavy but exact and
        // free of trig / sqrt.
        // -----------------------------------------------------------------

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

        // -----------------------------------------------------------------
        // Flat uniform spatial grid over a triangle list.
        //
        // Layout:
        //   cellOffsets   : length = dim^3 + 1
        //   cellTriangles : length = sum of triangles-per-cell counts
        //   Cell i owns the slice cellTriangles[cellOffsets[i] .. cellOffsets[i+1]).
        //
        // Construction is two-pass with a prefix sum: count per cell,
        // then fill via running cursors. No List<int> indirection and no
        // per-cell allocation.
        //
        // Closest-point query expands shells from the query's home cell.
        // Stops once `radius * minCellExtent >= sqrt(bestDistSq)` -- the
        // minimum possible distance from the query to any cell in shell
        // (radius+1) or beyond, which can't beat the current best hit.
        // -----------------------------------------------------------------

        internal sealed class SpatialGrid {
            public int dim;
            public Vector3 origin;
            public Vector3 cellSize;
            public int[] cellOffsets;     // length dim^3 + 1
            public int[] cellTriangles;   // flat slice store
            public float minCellExtent;
        }

        internal static SpatialGrid BuildSpatialGrid(Vector3[] verts, int[] triangles, int dim) {
            dim = Mathf.Max(1, dim);
            int cellCount = dim * dim * dim;

            if (verts == null || verts.Length == 0 || triangles == null || triangles.Length < 3) {
                return new SpatialGrid {
                    dim            = dim,
                    origin         = Vector3.zero,
                    cellSize       = Vector3.one,
                    cellOffsets    = new int[cellCount + 1],
                    cellTriangles  = Array.Empty<int>(),
                    minCellExtent  = 1f,
                };
            }

            Vector3 min = verts[0]; Vector3 max = verts[0];
            for (int i = 1; i < verts.Length; i++) {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            Vector3 size = max - min;
            Vector3 pad = new Vector3(
                Mathf.Max(size.x, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.y, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.z, 1e-4f) * 0.001f + 1e-4f);
            min -= pad; max += pad;
            Vector3 cell = (max - min) / dim;
            cell.x = Mathf.Max(cell.x, 1e-6f);
            cell.y = Mathf.Max(cell.y, 1e-6f);
            cell.z = Mathf.Max(cell.z, 1e-6f);

            int triCount = triangles.Length / 3;
            var counts = new int[cellCount];

            // First pass: count triangles per cell.
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                Vector3 a = verts[i0]; Vector3 b = verts[i1]; Vector3 c = verts[i2];
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

            // Prefix sum into offsets.
            var offsets = new int[cellCount + 1];
            int sum = 0;
            for (int i = 0; i < cellCount; i++) {
                offsets[i] = sum;
                sum += counts[i];
            }
            offsets[cellCount] = sum;

            // Second pass: fill the flat triangle store, using counts as a
            // running cursor over each cell's slice.
            var cellTriangles = new int[sum];
            Array.Clear(counts, 0, cellCount);
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= verts.Length || i1 >= verts.Length || i2 >= verts.Length) continue;
                Vector3 a = verts[i0]; Vector3 b = verts[i1]; Vector3 c = verts[i2];
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

            return new SpatialGrid {
                dim            = dim,
                origin         = min,
                cellSize       = cell,
                cellOffsets    = offsets,
                cellTriangles  = cellTriangles,
                minCellExtent  = Mathf.Min(cell.x, Mathf.Min(cell.y, cell.z)),
            };
        }

        internal struct ClosestHit {
            public int triangleIndex;  // -1 if no triangle visited.
            public Vector3 point;
            public float wa, wb, wc;
            public float distance;
        }

        internal enum RejectReason {
            None,
            RayMiss,
            Distance,
            NormalAngle,
            Backface,
        }

        internal struct ProjectionHit {
            public int triangleIndex;  // -1 if no source triangle accepted.
            public Vector3 point;
            public float wa, wb, wc;
            public float distance;
            public float normalDot;
            public RejectReason rejectReason;
        }

        internal static ClosestHit QueryClosest(
                SpatialGrid grid, Vector3[] verts, int[] triangles, Vector3 query) {
            var hit = new ClosestHit { triangleIndex = -1, distance = float.PositiveInfinity };
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
                            int end   = grid.cellOffsets[idx + 1];
                            for (int j = start; j < end; j++) {
                                int t = grid.cellTriangles[j];
                                int i0 = triangles[t * 3];
                                int i1 = triangles[t * 3 + 1];
                                int i2 = triangles[t * 3 + 2];
                                Vector3 a = verts[i0];
                                Vector3 b = verts[i1];
                                Vector3 c = verts[i2];

                                // Per-triangle AABB early-out. Cheaper
                                // than the full Ericson test when the
                                // triangle is already further than what
                                // we've found.
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

                                Vector3 cp = ClosestPointOnTriangle(
                                    query, a, b, c, out float w0, out float w1, out float w2);
                                float dSq = (query - cp).sqrMagnitude;
                                if (dSq < bestDistSq) {
                                    bestDistSq = dSq;
                                    hit.triangleIndex = t;
                                    hit.point = cp;
                                    hit.wa = w0; hit.wb = w1; hit.wc = w2;
                                }
                            }
                        }
                    }
                }

                // Once any hit is found, the next shell's nearest cell
                // sits at least `radius * minCellExtent` away from the
                // query (a worst-case-corner argument on the query's
                // home cell). If that exceeds the current best
                // distance, no further shell can beat us.
                if (hit.triangleIndex >= 0) {
                    float shellMin = radius * grid.minCellExtent;
                    if (shellMin * shellMin >= bestDistSq) break;
                }
                radius++;
            }

            hit.distance = hit.triangleIndex >= 0 ? Mathf.Sqrt(bestDistSq) : float.PositiveInfinity;
            return hit;
        }

        internal static ProjectionHit QueryProjected(
                SpatialGrid grid,
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
            return best.rejectReason != RejectReason.RayMiss ? best : reverse;
        }

        private static ProjectionHit QueryProjectedOneWay(
                SpatialGrid grid,
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
            var result = new ProjectionHit {
                triangleIndex = -1,
                distance = float.PositiveInfinity,
                normalDot = -1f,
                rejectReason = RejectReason.RayMiss,
            };
            if (grid == null || grid.cellTriangles == null || grid.cellTriangles.Length == 0) return result;

            projectionNormal = SafeNormalize(projectionNormal, Vector3.up);
            compareNormal = SafeNormalize(compareNormal, projectionNormal);
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
                            if (!RayTriangle(rayOrigin, rayDir, verts[i0], verts[i1], verts[i2],
                                    rayLength, out float rayT, out float wa, out float wb, out float wc)) {
                                continue;
                            }
                            sawRawHit = true;
                            if (rayT >= bestRayT) continue;

                            Vector3 sourceNormal = faceNormals != null && tri < faceNormals.Length
                                ? faceNormals[tri]
                                : Vector3.zero;
                            sourceNormal = SafeNormalize(sourceNormal,
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
                            result.rejectReason = RejectReason.None;
                        }
                    }
                }
            }

            if (result.triangleIndex >= 0) return result;
            if (sawNormalReject) result.rejectReason = RejectReason.NormalAngle;
            else if (sawBackfaceReject) result.rejectReason = RejectReason.Backface;
            else if (sawRawHit) result.rejectReason = RejectReason.NormalAngle;
            else result.rejectReason = RejectReason.RayMiss;
            return result;
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

        // -----------------------------------------------------------------
        // Target UV rasterizer.
        // -----------------------------------------------------------------

        internal struct UvSample {
            public int triangleIndex; // -1 = no triangle covers this texel.
            public float wa, wb, wc;
        }

        internal static UvSample[] RasterizeTargetUv(Vector2[] uvs, int[] triangles, int resolution) {
            return RasterizeTargetUv(uvs, triangles, resolution, 0.5f, 0.5f);
        }

        internal static UvSample[] RasterizeTargetUv(
                Vector2[] uvs, int[] triangles, int resolution,
                float sampleOffsetX, float sampleOffsetY) {
            var samples = new UvSample[resolution * resolution];
            for (int i = 0; i < samples.Length; i++) samples[i].triangleIndex = -1;
            if (uvs == null || uvs.Length == 0 || triangles == null || triangles.Length < 3) {
                return samples;
            }
            sampleOffsetX = Mathf.Clamp01(sampleOffsetX);
            sampleOffsetY = Mathf.Clamp01(sampleOffsetY);
            int triCount = triangles.Length / 3;
            float res = resolution;
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                Vector2 a = uvs[i0]; Vector2 b = uvs[i1]; Vector2 c = uvs[i2];
                float minU = Mathf.Min(Mathf.Min(a.x, b.x), c.x);
                float maxU = Mathf.Max(Mathf.Max(a.x, b.x), c.x);
                float minV = Mathf.Min(Mathf.Min(a.y, b.y), c.y);
                float maxV = Mathf.Max(Mathf.Max(a.y, b.y), c.y);
                if (maxU < 0f || minU > 1f || maxV < 0f || minV > 1f) continue;

                int xMin = Mathf.Max(0, Mathf.FloorToInt(minU * res));
                int xMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(maxU * res));
                int yMin = Mathf.Max(0, Mathf.FloorToInt(minV * res));
                int yMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(maxV * res));

                Vector2 v0 = b - a;
                Vector2 v1 = c - a;
                float d00 = Vector2.Dot(v0, v0);
                float d01 = Vector2.Dot(v0, v1);
                float d11 = Vector2.Dot(v1, v1);
                float denom = d00 * d11 - d01 * d01;
                if (Mathf.Abs(denom) < 1e-12f) continue; // degenerate UV triangle
                float invDenom = 1f / denom;

                for (int y = yMin; y <= yMax; y++) {
                    float fy = (y + sampleOffsetY) / res;
                    for (int x = xMin; x <= xMax; x++) {
                        float fx = (x + sampleOffsetX) / res;
                        Vector2 v2 = new Vector2(fx - a.x, fy - a.y);
                        float d20 = Vector2.Dot(v2, v0);
                        float d21 = Vector2.Dot(v2, v1);
                        float vBary = (d11 * d20 - d01 * d21) * invDenom;
                        if (vBary < 0f || vBary > 1f) continue;
                        float wBary = (d00 * d21 - d01 * d20) * invDenom;
                        if (wBary < 0f || vBary + wBary > 1f) continue;
                        int idx = y * resolution + x;
                        if (samples[idx].triangleIndex >= 0) continue;
                        samples[idx].triangleIndex = t;
                        samples[idx].wa = 1f - vBary - wBary;
                        samples[idx].wb = vBary;
                        samples[idx].wc = wBary;
                    }
                }
            }
            return samples;
        }

        // -----------------------------------------------------------------
        // Thread-safe bilinear texture sampling.
        //
        // Texture2D.GetPixelBilinear isn't safe to call from worker
        // threads inside a Parallel.For. We GetPixels32 once on the
        // main thread, then sample manually from that buffer. The
        // result matches Unity's wrap=Clamp + filter=Bilinear behaviour
        // for in-range UVs and clamps to the nearest edge texel for
        // UVs outside [0,1].
        // -----------------------------------------------------------------

        internal static Color32 SampleBilinearClamp32(Color32[] pixels, int w, int h, float u, float v) {
            // Unity's Texture2D.GetPixelBilinear treats UV (0,0) as the
            // centre of pixel (0,0) and UV (1,1) as the centre of pixel
            // (w-1, h-1) -- texel position = uv * size, no -0.5 offset.
            // Differs from the OpenGL/DirectX hardware convention; verified
            // empirically against tex.GetPixelBilinear in the test suite.
            if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
            if (v < 0f) v = 0f; else if (v > 1f) v = 1f;
            float fx = u * w;
            float fy = v * h;
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            if (x0 >= w) x0 = w - 1;
            if (y0 >= h) y0 = h - 1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            float tx = fx - x0;
            float ty = fy - y0;
            if (tx < 0f) tx = 0f; else if (tx > 1f) tx = 1f;
            if (ty < 0f) ty = 0f; else if (ty > 1f) ty = 1f;
            int x1 = x0 + 1; if (x1 >= w) x1 = w - 1;
            int y1 = y0 + 1; if (y1 >= h) y1 = h - 1;

            Color32 c00 = pixels[y0 * w + x0];
            Color32 c10 = pixels[y0 * w + x1];
            Color32 c01 = pixels[y1 * w + x0];
            Color32 c11 = pixels[y1 * w + x1];

            float w00 = (1f - tx) * (1f - ty);
            float w10 = tx * (1f - ty);
            float w01 = (1f - tx) * ty;
            float w11 = tx * ty;
            float r = c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11;
            float g = c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11;
            float b = c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11;
            float a = c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11;
            return new Color32((byte)(r + 0.5f), (byte)(g + 0.5f), (byte)(b + 0.5f), (byte)(a + 0.5f));
        }

        internal static Color32 ColorToColor32(Color c) {
            return new Color32(
                (byte)(Mathf.Clamp01(c.r) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.a) * 255f + 0.5f));
        }

        // -----------------------------------------------------------------
        // High-level Transfer.
        // -----------------------------------------------------------------

        internal enum AlignmentMode {
            Identity,
            BoundingBox,
        }

        internal enum CorrespondenceMode {
            LegacyClosestPoint,
            NormalRaycast,
            BidirectionalNormalRaycast,
            NormalAwareNearest,
        }

        internal struct TransferOptions {
            public Mesh sourceMesh;
            public int sourceSubmesh;
            public Texture2D sourceTexture;
            public Mesh targetMesh;
            public int targetSubmesh;
            public int outputResolution;
            public AlignmentMode alignment;
            public Matrix4x4 sourceWorldMatrix;
            public Matrix4x4 targetWorldMatrix;
            public float maxDistance;
            public int gridDim;
            public Color fallbackColor;
            public int supersample;
            public int paddingPixels;
            public CorrespondenceMode correspondenceMode;
            public float rayFrontalDistance;
            public float rayRearDistance;
            public float normalAngleLimitDegrees;
            public bool rejectBackfaces;
            public bool writeDiagnosticMaps;
            // Coarse phase progress: 0.0 setup, ~0.2 after rasterize,
            // ~0.95 after parallel transfer, 1.0 done. Called only from
            // the main thread, not the worker threads.
            public Action<float> onProgress;
        }

        internal struct TransferResult {
            public Texture2D output;
            public int coveredTexels;
            public int totalTexels;
            public int rejectedByDistance;
            public float maxObservedDistance;
            public int supersample;
            public int paddingPixels;
            public int paddedTexels;
            public CorrespondenceMode correspondenceMode;
            public int rejectedByRayMiss;
            public int rejectedByNormalAngle;
            public int rejectedByBackface;
            public int nearestFallbackUsed;
            public Texture2D hitDistanceMap;
            public Texture2D normalDotMap;
            public Texture2D rejectReasonMap;
            public Texture2D sourceTriangleMap;
        }

        internal static TransferResult Transfer(TransferOptions opt) {
            if (opt.outputResolution <= 0) throw new ArgumentException("outputResolution must be positive");
            if (opt.sourceMesh == null) throw new ArgumentNullException(nameof(opt.sourceMesh));
            if (opt.targetMesh == null) throw new ArgumentNullException(nameof(opt.targetMesh));
            if (opt.sourceTexture == null) throw new ArgumentNullException(nameof(opt.sourceTexture));
            if (!opt.sourceTexture.isReadable) {
                throw new InvalidOperationException(
                    "Source texture is not Read/Write Enabled in the importer; the bake can't sample " +
                    "it from CPU. Open the source texture asset and enable Read/Write in the importer.");
            }

            int res = opt.outputResolution;
            opt.onProgress?.Invoke(0f);

            var (sourceToCommon, targetToCommon) = ComputeAlignment(
                opt.alignment, opt.sourceMesh, opt.targetMesh,
                opt.sourceWorldMatrix, opt.targetWorldMatrix);

            var sourceVerts = TransformVertices(opt.sourceMesh.vertices, sourceToCommon);
            var sourceUvs   = opt.sourceMesh.uv;
            var sourceTris  = ResolveTriangles(opt.sourceMesh, opt.sourceSubmesh);
            var sourceFaceNormals = ComputeFaceNormals(sourceVerts, sourceTris);
            var targetVerts = TransformVertices(opt.targetMesh.vertices, targetToCommon);
            var targetUvs   = opt.targetMesh.uv;
            var targetTris  = ResolveTriangles(opt.targetMesh, opt.targetSubmesh);
            var targetNormals = TransformNormals(ResolveNormals(opt.targetMesh), targetToCommon);

            int gridDim = opt.gridDim > 0
                ? opt.gridDim
                : PickGridDim(sourceTris.Length / 3);
            var grid = BuildSpatialGrid(sourceVerts, sourceTris, gridDim);

            int samplesPerAxis = Mathf.Clamp(opt.supersample <= 0 ? 1 : opt.supersample, 1, 4);
            int sampleCount = samplesPerAxis * samplesPerAxis;
            int paddingPixels = Mathf.Clamp(opt.paddingPixels, 0, 128);
            var mode = opt.correspondenceMode;
            float frontalDistance = opt.rayFrontalDistance > 0f
                ? opt.rayFrontalDistance
                : Mathf.Max(opt.maxDistance, 0.08f);
            float rearDistance = opt.rayRearDistance > 0f
                ? opt.rayRearDistance
                : Mathf.Max(opt.maxDistance, 0.08f);
            float minNormalDot = MinNormalDot(opt.normalAngleLimitDegrees);
            opt.onProgress?.Invoke(0.15f);

            // Cache source pixels for thread-safe bilinear sampling. The
            // isReadable check above means GetPixels32 will succeed.
            Color32[] srcPixels = opt.sourceTexture.GetPixels32();
            int srcW = opt.sourceTexture.width;
            int srcH = opt.sourceTexture.height;

            var outputPixels = new Color32[res * res];
            Color32 fallback32 = ColorToColor32(opt.fallbackColor);
            var sumR = new ushort[outputPixels.Length];
            var sumG = new ushort[outputPixels.Length];
            var sumB = new ushort[outputPixels.Length];
            var sumA = new ushort[outputPixels.Length];
            var acceptedSamples = new byte[outputPixels.Length];
            Color32[] debugDistance = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugNormal = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugReject = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugTriangle = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;

            int rejectedCounter = 0;
            int rayMissCounter = 0;
            int normalRejectedCounter = 0;
            int backfaceRejectedCounter = 0;
            int nearestFallbackCounter = 0;
            float maxDistObserved = 0f;
            object statsLock = new object();

            for (int sy = 0; sy < samplesPerAxis; sy++) {
                for (int sx = 0; sx < samplesPerAxis; sx++) {
                    float offsetX = (sx + 0.5f) / samplesPerAxis;
                    float offsetY = (sy + 0.5f) / samplesPerAxis;
                    var samples = RasterizeTargetUv(targetUvs, targetTris, res, offsetX, offsetY);
                    float passProgress = (sy * samplesPerAxis + sx) / (float)sampleCount;
                    opt.onProgress?.Invoke(0.15f + passProgress * 0.75f);

                    // Parallel core. Each row writes a disjoint stripe of the
                    // output buffer and reads only the immutable inputs, so we
                    // need no per-pixel synchronisation. Per-row stats merge
                    // through a single lock once per row.
                    Parallel.For(0, res, row => {
                        int localRejected = 0;
                        int localRayMiss = 0;
                        int localNormalRejected = 0;
                        int localBackfaceRejected = 0;
                        int localFallback = 0;
                        float localMaxDist = 0f;
                        int rowBase = row * res;
                        for (int x = 0; x < res; x++) {
                            int idx = rowBase + x;
                            var sample = samples[idx];
                            if (sample.triangleIndex < 0) continue;
                            int t = sample.triangleIndex;
                            int ti0 = targetTris[t * 3];
                            int ti1 = targetTris[t * 3 + 1];
                            int ti2 = targetTris[t * 3 + 2];
                            Vector3 tp = targetVerts[ti0] * sample.wa
                                       + targetVerts[ti1] * sample.wb
                                       + targetVerts[ti2] * sample.wc;
                            Vector3 targetNormal = InterpolateNormal(
                                targetNormals, targetVerts, targetTris,
                                t, ti0, ti1, ti2,
                                sample.wa, sample.wb, sample.wc);

                            ProjectionHit hit = ResolveCorrespondence(
                                mode, grid, sourceVerts, sourceTris, sourceFaceNormals,
                                tp, targetNormal,
                                frontalDistance, rearDistance,
                                minNormalDot, opt.rejectBackfaces);

                            if (hit.triangleIndex < 0) {
                                CountReject(hit.rejectReason, ref localRayMiss, ref localNormalRejected, ref localBackfaceRejected);
                                WriteRejectDebug(debugReject, idx, hit.rejectReason);
                                continue;
                            }

                            if (hit.distance > localMaxDist) localMaxDist = hit.distance;
                            if (opt.maxDistance > 0f && hit.distance > opt.maxDistance) {
                                localRejected++;
                                WriteRejectDebug(debugReject, idx, RejectReason.Distance);
                                continue;
                            }

                            if (mode == CorrespondenceMode.NormalAwareNearest) localFallback++;

                            int si0 = sourceTris[hit.triangleIndex * 3];
                            int si1 = sourceTris[hit.triangleIndex * 3 + 1];
                            int si2 = sourceTris[hit.triangleIndex * 3 + 2];
                            Vector2 srcUv = sourceUvs[si0] * hit.wa
                                          + sourceUvs[si1] * hit.wb
                                          + sourceUvs[si2] * hit.wc;
                            var color = SampleBilinearClamp32(srcPixels, srcW, srcH, srcUv.x, srcUv.y);
                            sumR[idx] = (ushort)(sumR[idx] + color.r);
                            sumG[idx] = (ushort)(sumG[idx] + color.g);
                            sumB[idx] = (ushort)(sumB[idx] + color.b);
                            sumA[idx] = (ushort)(sumA[idx] + color.a);
                            acceptedSamples[idx]++;
                            WriteAcceptDebug(
                                debugDistance, debugNormal, debugReject, debugTriangle,
                                idx, hit, Mathf.Max(opt.maxDistance, frontalDistance + rearDistance));
                        }
                        if (localRejected > 0
                                || localRayMiss > 0
                                || localNormalRejected > 0
                                || localBackfaceRejected > 0
                                || localFallback > 0
                                || localMaxDist > 0f) {
                            lock (statsLock) {
                                rejectedCounter += localRejected;
                                rayMissCounter += localRayMiss;
                                normalRejectedCounter += localNormalRejected;
                                backfaceRejectedCounter += localBackfaceRejected;
                                nearestFallbackCounter += localFallback;
                                if (localMaxDist > maxDistObserved) maxDistObserved = localMaxDist;
                            }
                        }
                    });
                }
            }

            int coveredCounter = 0;
            var validPixels = new bool[outputPixels.Length];
            for (int i = 0; i < outputPixels.Length; i++) {
                int accepted = acceptedSamples[i];
                if (accepted > 0) {
                    coveredCounter++;
                    validPixels[i] = true;
                    int fallbackSamples = sampleCount - accepted;
                    outputPixels[i] = new Color32(
                        AverageByte(sumR[i] + fallback32.r * fallbackSamples, sampleCount),
                        AverageByte(sumG[i] + fallback32.g * fallbackSamples, sampleCount),
                        AverageByte(sumB[i] + fallback32.b * fallbackSamples, sampleCount),
                        AverageByte(sumA[i] + fallback32.a * fallbackSamples, sampleCount));
                } else {
                    outputPixels[i] = fallback32;
                }
            }

            int paddedTexels = DilateUvIslandPadding(outputPixels, validPixels, res, paddingPixels);

            opt.onProgress?.Invoke(0.95f);

            var output = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: false, linear: true) {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
                name       = "WkUvTextureTransfer_Output",
            };
            output.SetPixels32(outputPixels);
            output.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Texture2D hitDistanceMap = CreateDebugTexture(debugDistance, res, "WkUvTextureTransfer_HitDistance");
            Texture2D normalDotMap = CreateDebugTexture(debugNormal, res, "WkUvTextureTransfer_NormalDot");
            Texture2D rejectReasonMap = CreateDebugTexture(debugReject, res, "WkUvTextureTransfer_RejectReason");
            Texture2D sourceTriangleMap = CreateDebugTexture(debugTriangle, res, "WkUvTextureTransfer_SourceTriangle");
            opt.onProgress?.Invoke(1f);

            return new TransferResult {
                output              = output,
                coveredTexels       = coveredCounter,
                totalTexels         = outputPixels.Length,
                rejectedByDistance  = rejectedCounter,
                maxObservedDistance = maxDistObserved,
                supersample         = samplesPerAxis,
                paddingPixels       = paddingPixels,
                paddedTexels        = paddedTexels,
                correspondenceMode  = mode,
                rejectedByRayMiss   = rayMissCounter,
                rejectedByNormalAngle = normalRejectedCounter,
                rejectedByBackface  = backfaceRejectedCounter,
                nearestFallbackUsed = nearestFallbackCounter,
                hitDistanceMap      = hitDistanceMap,
                normalDotMap        = normalDotMap,
                rejectReasonMap     = rejectReasonMap,
                sourceTriangleMap   = sourceTriangleMap,
            };
        }

        private static ProjectionHit ResolveCorrespondence(
                CorrespondenceMode mode,
                SpatialGrid grid,
                Vector3[] sourceVerts,
                int[] sourceTris,
                Vector3[] sourceFaceNormals,
                Vector3 targetPoint,
                Vector3 targetNormal,
                float frontalDistance,
                float rearDistance,
                float minNormalDot,
                bool rejectBackfaces) {
            switch (mode) {
                case CorrespondenceMode.NormalRaycast:
                    return QueryProjected(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal,
                        frontalDistance, rearDistance,
                        minNormalDot, rejectBackfaces,
                        bidirectional: false);

                case CorrespondenceMode.BidirectionalNormalRaycast:
                    return QueryProjected(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal,
                        frontalDistance, rearDistance,
                        minNormalDot, rejectBackfaces,
                        bidirectional: true);

                case CorrespondenceMode.NormalAwareNearest:
                    return QueryNormalAwareNearest(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal, minNormalDot, rejectBackfaces);

                case CorrespondenceMode.LegacyClosestPoint:
                default:
                    var hit = QueryClosest(grid, sourceVerts, sourceTris, targetPoint);
                    return new ProjectionHit {
                        triangleIndex = hit.triangleIndex,
                        point = hit.point,
                        wa = hit.wa,
                        wb = hit.wb,
                        wc = hit.wc,
                        distance = hit.distance,
                        normalDot = 1f,
                        rejectReason = hit.triangleIndex >= 0 ? RejectReason.None : RejectReason.RayMiss,
                    };
            }
        }

        private static ProjectionHit QueryNormalAwareNearest(
                SpatialGrid grid,
                Vector3[] verts,
                int[] triangles,
                Vector3[] faceNormals,
                Vector3 targetPoint,
                Vector3 targetNormal,
                float minNormalDot,
                bool rejectBackfaces) {
            var hit = QueryClosest(grid, verts, triangles, targetPoint);
            if (hit.triangleIndex < 0) {
                return new ProjectionHit { triangleIndex = -1, rejectReason = RejectReason.RayMiss };
            }

            Vector3 sourceNormal = faceNormals != null && hit.triangleIndex < faceNormals.Length
                ? faceNormals[hit.triangleIndex]
                : Vector3.zero;
            sourceNormal = SafeNormalize(sourceNormal, Vector3.up);
            targetNormal = SafeNormalize(targetNormal, Vector3.up);
            float normalDot = Vector3.Dot(targetNormal, sourceNormal);
            if (minNormalDot > -1.01f && normalDot < minNormalDot) {
                return new ProjectionHit {
                    triangleIndex = -1,
                    rejectReason = RejectReason.NormalAngle,
                    distance = hit.distance,
                    normalDot = normalDot,
                };
            }

            return new ProjectionHit {
                triangleIndex = hit.triangleIndex,
                point = hit.point,
                wa = hit.wa,
                wb = hit.wb,
                wc = hit.wc,
                distance = hit.distance,
                normalDot = normalDot,
                rejectReason = RejectReason.None,
            };
        }

        private static void CountReject(
                RejectReason reason,
                ref int rayMiss,
                ref int normalRejected,
                ref int backfaceRejected) {
            switch (reason) {
                case RejectReason.NormalAngle:
                    normalRejected++;
                    break;
                case RejectReason.Backface:
                    backfaceRejected++;
                    break;
                case RejectReason.Distance:
                    break;
                case RejectReason.RayMiss:
                default:
                    rayMiss++;
                    break;
            }
        }

        private static void WriteAcceptDebug(
                Color32[] distanceMap,
                Color32[] normalMap,
                Color32[] rejectMap,
                Color32[] triangleMap,
                int idx,
                ProjectionHit hit,
                float distanceScale) {
            if (distanceMap != null) {
                float t = distanceScale > 1e-6f ? Mathf.Clamp01(hit.distance / distanceScale) : 0f;
                distanceMap[idx] = new Color32(
                    (byte)(t * 255f + 0.5f),
                    (byte)((1f - t) * 255f + 0.5f),
                    0,
                    255);
            }
            if (normalMap != null) {
                float t = Mathf.Clamp01((hit.normalDot + 1f) * 0.5f);
                normalMap[idx] = new Color32(
                    (byte)((1f - t) * 255f + 0.5f),
                    (byte)(t * 255f + 0.5f),
                    0,
                    255);
            }
            if (rejectMap != null) {
                rejectMap[idx] = new Color32(0, 210, 70, 255);
            }
            if (triangleMap != null) {
                triangleMap[idx] = HashColor(hit.triangleIndex);
            }
        }

        private static void WriteRejectDebug(Color32[] rejectMap, int idx, RejectReason reason) {
            if (rejectMap == null) return;
            switch (reason) {
                case RejectReason.Distance:
                    rejectMap[idx] = new Color32(255, 150, 0, 255);
                    break;
                case RejectReason.NormalAngle:
                    rejectMap[idx] = new Color32(210, 0, 255, 255);
                    break;
                case RejectReason.Backface:
                    rejectMap[idx] = new Color32(0, 180, 255, 255);
                    break;
                case RejectReason.RayMiss:
                default:
                    rejectMap[idx] = new Color32(255, 0, 0, 255);
                    break;
            }
        }

        private static Color32 HashColor(int value) {
            unchecked {
                uint x = (uint)(value + 1) * 747796405u + 2891336453u;
                x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                byte r = (byte)(40 + (x & 0xBF));
                byte g = (byte)(40 + ((x >> 8) & 0xBF));
                byte b = (byte)(40 + ((x >> 16) & 0xBF));
                return new Color32(r, g, b, 255);
            }
        }

        private static Texture2D CreateDebugTexture(Color32[] pixels, int resolution, string name) {
            if (pixels == null) return null;
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, mipChain: false, linear: true) {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = name,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        private static byte AverageByte(int numerator, int denominator) {
            if (denominator <= 1) return (byte)Mathf.Clamp(numerator, 0, 255);
            return (byte)Mathf.Clamp((numerator + denominator / 2) / denominator, 0, 255);
        }

        internal static int DilateUvIslandPadding(
                Color32[] pixels, bool[] validPixels, int resolution, int paddingPixels) {
            if (pixels == null || validPixels == null) return 0;
            if (pixels.Length != validPixels.Length) {
                throw new ArgumentException("pixels and validPixels must have the same length");
            }
            if (resolution <= 0 || pixels.Length != resolution * resolution) {
                throw new ArgumentException("resolution does not match pixel buffer length");
            }
            if (paddingPixels <= 0) return 0;

            int totalAdded = 0;
            var currentPixels = pixels;
            var currentValid = validPixels;
            var nextPixels = new Color32[pixels.Length];
            var nextValid = new bool[validPixels.Length];

            for (int iter = 0; iter < paddingPixels; iter++) {
                Array.Copy(currentPixels, nextPixels, currentPixels.Length);
                Array.Copy(currentValid, nextValid, currentValid.Length);
                int addedThisPass = 0;

                Parallel.For(0, resolution, () => 0, (y, state, localAdded) => {
                    int rowBase = y * resolution;
                    for (int x = 0; x < resolution; x++) {
                        int idx = rowBase + x;
                        if (currentValid[idx]) continue;

                        int count = 0;
                        int r = 0, g = 0, b = 0, a = 0;
                        for (int oy = -1; oy <= 1; oy++) {
                            int ny = y + oy;
                            if (ny < 0 || ny >= resolution) continue;
                            int nRow = ny * resolution;
                            for (int ox = -1; ox <= 1; ox++) {
                                if (ox == 0 && oy == 0) continue;
                                int nx = x + ox;
                                if (nx < 0 || nx >= resolution) continue;
                                int nIdx = nRow + nx;
                                if (!currentValid[nIdx]) continue;
                                var c = currentPixels[nIdx];
                                r += c.r; g += c.g; b += c.b; a += c.a;
                                count++;
                            }
                        }
                        if (count == 0) continue;

                        nextPixels[idx] = new Color32(
                            AverageByte(r, count),
                            AverageByte(g, count),
                            AverageByte(b, count),
                            AverageByte(a, count));
                        nextValid[idx] = true;
                        localAdded++;
                    }
                    return localAdded;
                }, localAdded => Interlocked.Add(ref addedThisPass, localAdded));

                if (addedThisPass == 0) break;
                totalAdded += addedThisPass;

                var pixelSwap = currentPixels;
                currentPixels = nextPixels;
                nextPixels = pixelSwap;

                var validSwap = currentValid;
                currentValid = nextValid;
                nextValid = validSwap;
            }

            if (!ReferenceEquals(currentPixels, pixels)) {
                Array.Copy(currentPixels, pixels, pixels.Length);
                Array.Copy(currentValid, validPixels, validPixels.Length);
            }
            return totalAdded;
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        internal static (Matrix4x4 source, Matrix4x4 target) ComputeAlignment(
                AlignmentMode mode, Mesh source, Mesh target,
                Matrix4x4 sourceWorld, Matrix4x4 targetWorld) {
            switch (mode) {
                case AlignmentMode.Identity:    return (sourceWorld, targetWorld);
                case AlignmentMode.BoundingBox: return ComputeBBoxAlignment(source, target, sourceWorld, targetWorld);
                default:                        return (sourceWorld, targetWorld);
            }
        }

        internal static (Matrix4x4 source, Matrix4x4 target) ComputeBBoxAlignment(
                Mesh source, Mesh target,
                Matrix4x4 sourceWorld, Matrix4x4 targetWorld) {
            Bounds sb = source != null ? source.bounds : new Bounds(Vector3.zero, Vector3.one);
            Bounds tb = target != null ? target.bounds : new Bounds(Vector3.zero, Vector3.one);
            Vector3 sSize = sb.size;
            Vector3 tSize = tb.size;
            float sMax = Mathf.Max(sSize.x, Mathf.Max(sSize.y, sSize.z));
            float tMax = Mathf.Max(tSize.x, Mathf.Max(tSize.y, tSize.z));
            float scale = (sMax > 1e-6f) ? (tMax / sMax) : 1f;
            Matrix4x4 align =
                Matrix4x4.Translate(tb.center) *
                Matrix4x4.Scale(new Vector3(scale, scale, scale)) *
                Matrix4x4.Translate(-sb.center);
            return (align, Matrix4x4.identity);
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

        private static Vector3 InterpolateNormal(
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

        private static float MinNormalDot(float angleDegrees) {
            if (angleDegrees <= 0f || angleDegrees >= 179.9f) return -2f;
            return Mathf.Cos(angleDegrees * Mathf.Deg2Rad);
        }

        private static Vector3 SafeNormalize(Vector3 value, Vector3 fallback) {
            float sqr = value.sqrMagnitude;
            if (sqr < 1e-12f || float.IsNaN(sqr)) return fallback;
            return value / Mathf.Sqrt(sqr);
        }

        internal static int[] ResolveTriangles(Mesh mesh, int submeshIndex) {
            if (mesh == null) return Array.Empty<int>();
            if (submeshIndex < 0 || submeshIndex >= mesh.subMeshCount) return mesh.triangles;
            return mesh.GetTriangles(submeshIndex);
        }

        // Auto-pick a spatial grid dimension. Aims for ~4-8 triangles per
        // occupied cell on average, which keeps the per-query work bounded
        // without wasting memory on a 64^3 grid for tiny meshes.
        internal static int PickGridDim(int triangleCount) {
            if (triangleCount <= 0) return 4;
            int dim = Mathf.RoundToInt(Mathf.Pow(triangleCount / 6f, 1f / 3f));
            return Mathf.Clamp(dim, 4, 64);
        }
    }
}
