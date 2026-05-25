// UvTextureTransferCore.cs
//
// Mesh-to-mesh texture transfer. Bake a texture authored against a
// source mesh's UV0 layout into the UV0 layout of a different target
// mesh by closest-point correspondence on the meshes' 3D geometry.
//
// Pipeline:
//   1. UV-rasterize the target mesh in UV space. For every covered
//      texel store (target triangle index, barycentric weights).
//   2. Build a uniform spatial grid over the source mesh's triangles
//      for cheap nearest-triangle queries.
//   3. For each covered target texel, interpolate the target mesh's
//      vertex positions to get a world-space sample point, query the
//      grid for the closest source triangle, interpolate the source
//      UV at the closest point, and sample the source texture.
//
// Pure math + Unity primitives only (Mesh, Texture2D). All entry
// points are static and side-effect-free aside from writing the
// returned Texture2D; the algorithm is exercised directly from the
// Tests.Editor asmdef. UI, FBX import, and PNG persistence live in
// sibling files so this stays trivially unit-testable.

using System;
using System.Collections.Generic;
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
        // Uniform spatial grid over a triangle list. Trades closest-point
        // queries from O(N triangles) to roughly O(triangles-per-shell),
        // which is enough to make a 1024^2 bake on a 50k-tri avatar finish
        // in seconds instead of minutes.
        //
        // Construction inserts each triangle into every grid cell its AABB
        // overlaps. Query expands shells around the source cell until the
        // nearest hit found is closer than the inner radius of the next
        // shell, at which point no further shell can beat it.
        // -----------------------------------------------------------------

        internal sealed class SpatialGrid {
            public int dim;
            public Vector3 origin;
            public Vector3 cellSize;
            // cells[z * dim * dim + y * dim + x]; null entries are empty.
            public List<int>[] cells;
            public float minCellExtent; // smallest of cellSize.x/y/z, for early-out math.
        }

        internal static SpatialGrid BuildSpatialGrid(Vector3[] verts, int[] triangles, int dim) {
            if (verts == null || verts.Length == 0 || triangles == null || triangles.Length < 3) {
                return new SpatialGrid {
                    dim = Mathf.Max(1, dim),
                    origin = Vector3.zero,
                    cellSize = Vector3.one,
                    cells = new List<int>[Mathf.Max(1, dim * dim * dim)],
                    minCellExtent = 1f,
                };
            }
            dim = Mathf.Max(1, dim);
            Vector3 min = verts[0]; Vector3 max = verts[0];
            for (int i = 1; i < verts.Length; i++) {
                min = Vector3.Min(min, verts[i]);
                max = Vector3.Max(max, verts[i]);
            }
            // Inflate slightly so a triangle whose AABB touches a cell
            // boundary lands cleanly inside a cell.
            Vector3 size = max - min;
            Vector3 pad = new Vector3(
                Mathf.Max(size.x, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.y, 1e-4f) * 0.001f + 1e-4f,
                Mathf.Max(size.z, 1e-4f) * 0.001f + 1e-4f);
            min -= pad; max += pad;
            Vector3 cell = (max - min) / dim;
            // Floor the cell size so dim=1 still works on a near-flat mesh.
            cell.x = Mathf.Max(cell.x, 1e-6f);
            cell.y = Mathf.Max(cell.y, 1e-6f);
            cell.z = Mathf.Max(cell.z, 1e-6f);

            var grid = new SpatialGrid {
                dim = dim,
                origin = min,
                cellSize = cell,
                cells = new List<int>[dim * dim * dim],
                minCellExtent = Mathf.Min(cell.x, Mathf.Min(cell.y, cell.z)),
            };

            int triCount = triangles.Length / 3;
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
                        for (int x = x0; x <= x1; x++) {
                            int idx = (z * dim + y) * dim + x;
                            var list = grid.cells[idx] ?? (grid.cells[idx] = new List<int>(2));
                            list.Add(t);
                        }
                    }
                }
            }
            return grid;
        }

        internal struct ClosestHit {
            public int triangleIndex;  // -1 if no triangle visited.
            public Vector3 point;
            public float wa, wb, wc;
            public float distance;
        }

        internal static ClosestHit QueryClosest(
                SpatialGrid grid, Vector3[] verts, int[] triangles, Vector3 query) {
            var hit = new ClosestHit { triangleIndex = -1, distance = float.PositiveInfinity };
            if (grid == null || grid.cells == null || grid.cells.Length == 0) return hit;

            int qx = Mathf.Clamp(Mathf.FloorToInt((query.x - grid.origin.x) / grid.cellSize.x), 0, grid.dim - 1);
            int qy = Mathf.Clamp(Mathf.FloorToInt((query.y - grid.origin.y) / grid.cellSize.y), 0, grid.dim - 1);
            int qz = Mathf.Clamp(Mathf.FloorToInt((query.z - grid.origin.z) / grid.cellSize.z), 0, grid.dim - 1);

            int radius = 0;
            int maxRadius = grid.dim;
            float bestDistSq = float.PositiveInfinity;
            while (radius <= maxRadius) {
                int x0 = Mathf.Max(0, qx - radius);
                int y0 = Mathf.Max(0, qy - radius);
                int z0 = Mathf.Max(0, qz - radius);
                int x1 = Mathf.Min(grid.dim - 1, qx + radius);
                int y1 = Mathf.Min(grid.dim - 1, qy + radius);
                int z1 = Mathf.Min(grid.dim - 1, qz + radius);

                for (int z = z0; z <= z1; z++) {
                    for (int y = y0; y <= y1; y++) {
                        for (int x = x0; x <= x1; x++) {
                            // Skip interior cells on shell expansion -- we
                            // visited them on smaller radii.
                            if (radius > 0
                                    && x > qx - radius && x < qx + radius
                                    && y > qy - radius && y < qy + radius
                                    && z > qz - radius && z < qz + radius) {
                                continue;
                            }
                            int idx = (z * grid.dim + y) * grid.dim + x;
                            var list = grid.cells[idx];
                            if (list == null) continue;
                            for (int li = 0; li < list.Count; li++) {
                                int t = list[li];
                                int i0 = triangles[t * 3];
                                int i1 = triangles[t * 3 + 1];
                                int i2 = triangles[t * 3 + 2];
                                Vector3 cp = ClosestPointOnTriangle(
                                    query, verts[i0], verts[i1], verts[i2],
                                    out float w0, out float w1, out float w2);
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

                // Early-out: once any hit has been found, the next shell
                // can only beat it if (radius+1) * minCellExtent <
                // sqrt(bestDistSq). Stop when that's no longer possible.
                if (hit.triangleIndex >= 0) {
                    float shellNext = (radius + 1) * grid.minCellExtent;
                    if (shellNext * shellNext > bestDistSq) break;
                }
                radius++;
            }

            hit.distance = hit.triangleIndex >= 0 ? Mathf.Sqrt(bestDistSq) : float.PositiveInfinity;
            return hit;
        }

        // -----------------------------------------------------------------
        // Target UV rasterizer.
        //
        // Walks every target triangle, finds its UV-space AABB, and for
        // each output texel inside that AABB tests UV-barycentric to fill
        // it. First-write-wins: when two target triangles share a UV cell
        // (mirrored islands, atlas overlap) the first one to touch the
        // texel keeps it. Texels outside [0,1] are dropped because the
        // output texture's wrap is Clamp; nothing outside [0,1] can be
        // sampled anyway.
        // -----------------------------------------------------------------

        internal struct UvSample {
            public int triangleIndex; // -1 = no triangle covers this texel.
            public float wa, wb, wc;
        }

        internal static UvSample[] RasterizeTargetUv(Vector2[] uvs, int[] triangles, int resolution) {
            var samples = new UvSample[resolution * resolution];
            for (int i = 0; i < samples.Length; i++) samples[i].triangleIndex = -1;
            if (uvs == null || uvs.Length == 0 || triangles == null || triangles.Length < 3) {
                return samples;
            }
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

                // Pre-compute UV-barycentric basis: maintain v0, v1, d00,
                // d01, d11, invDenom outside the inner loop. Avoids 3 dot
                // products per texel.
                Vector2 v0 = b - a;
                Vector2 v1 = c - a;
                float d00 = Vector2.Dot(v0, v0);
                float d01 = Vector2.Dot(v0, v1);
                float d11 = Vector2.Dot(v1, v1);
                float denom = d00 * d11 - d01 * d01;
                if (Mathf.Abs(denom) < 1e-12f) continue; // degenerate UV triangle
                float invDenom = 1f / denom;

                for (int y = yMin; y <= yMax; y++) {
                    float fy = (y + 0.5f) / res;
                    for (int x = xMin; x <= xMax; x++) {
                        float fx = (x + 0.5f) / res;
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
        // High-level Transfer.
        // -----------------------------------------------------------------

        internal enum AlignmentMode {
            // Source and target are already in the same coordinate frame.
            // Right when both come from scene transforms or both meshes
            // sit at their author origin and have the same proportions.
            Identity,
            // Translate + uniform-scale the source so its mesh.bounds maps
            // onto target.bounds. Good default for "two avatars of
            // different size".
            BoundingBox,
        }

        internal struct TransferOptions {
            public Mesh sourceMesh;
            public int sourceSubmesh;        // -1 = every submesh
            public Texture2D sourceTexture;
            public Mesh targetMesh;
            public int targetSubmesh;        // -1 = every submesh
            public int outputResolution;     // e.g. 1024
            public AlignmentMode alignment;
            public Matrix4x4 sourceWorldMatrix;  // used by Identity (and as base for BoundingBox).
            public Matrix4x4 targetWorldMatrix;
            public float maxDistance;        // 0 = no cap; >0 = skip target texels whose nearest source point is further.
            public int gridDim;              // 0 -> auto-pick from triangle count.
            public Color fallbackColor;      // pixels with no source correspondence (or > maxDistance) get this.
            public Action<float> onProgress; // optional, 0..1.
        }

        internal struct TransferResult {
            public Texture2D output;
            public int coveredTexels;
            public int totalTexels;
            public int rejectedByDistance;
            public float maxObservedDistance;
        }

        internal static TransferResult Transfer(TransferOptions opt) {
            if (opt.outputResolution <= 0) throw new ArgumentException("outputResolution must be positive");
            if (opt.sourceMesh == null) throw new ArgumentNullException(nameof(opt.sourceMesh));
            if (opt.targetMesh == null) throw new ArgumentNullException(nameof(opt.targetMesh));
            if (opt.sourceTexture == null) throw new ArgumentNullException(nameof(opt.sourceTexture));

            int res = opt.outputResolution;
            var (sourceToCommon, targetToCommon) = ComputeAlignment(
                opt.alignment, opt.sourceMesh, opt.targetMesh,
                opt.sourceWorldMatrix, opt.targetWorldMatrix);

            var sourceVerts = TransformVertices(opt.sourceMesh.vertices, sourceToCommon);
            var sourceUvs   = opt.sourceMesh.uv;
            var sourceTris  = ResolveTriangles(opt.sourceMesh, opt.sourceSubmesh);
            var targetVerts = TransformVertices(opt.targetMesh.vertices, targetToCommon);
            var targetUvs   = opt.targetMesh.uv;
            var targetTris  = ResolveTriangles(opt.targetMesh, opt.targetSubmesh);

            int gridDim = opt.gridDim > 0
                ? opt.gridDim
                : PickGridDim(sourceTris.Length / 3);
            var grid = BuildSpatialGrid(sourceVerts, sourceTris, gridDim);

            var samples = RasterizeTargetUv(targetUvs, targetTris, res);

            var output = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: false, linear: true) {
                hideFlags  = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
                name       = "WkUvTextureTransfer_Output",
            };
            var pixels = new Color[res * res];
            Color fallback = opt.fallbackColor;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = fallback;

            int covered = 0;
            int rejected = 0;
            float maxDist = 0f;
            int total = samples.Length;
            int progressTick = Mathf.Max(1, total / 100);

            for (int i = 0; i < total; i++) {
                if (samples[i].triangleIndex < 0) continue;
                int t = samples[i].triangleIndex;
                int i0 = targetTris[t * 3];
                int i1 = targetTris[t * 3 + 1];
                int i2 = targetTris[t * 3 + 2];
                Vector3 tp = targetVerts[i0] * samples[i].wa
                           + targetVerts[i1] * samples[i].wb
                           + targetVerts[i2] * samples[i].wc;

                var hit = QueryClosest(grid, sourceVerts, sourceTris, tp);
                if (hit.triangleIndex < 0) continue;
                if (hit.distance > maxDist) maxDist = hit.distance;
                if (opt.maxDistance > 0f && hit.distance > opt.maxDistance) {
                    rejected++;
                    continue;
                }
                int s0 = sourceTris[hit.triangleIndex * 3];
                int s1 = sourceTris[hit.triangleIndex * 3 + 1];
                int s2 = sourceTris[hit.triangleIndex * 3 + 2];
                Vector2 srcUv = sourceUvs[s0] * hit.wa
                              + sourceUvs[s1] * hit.wb
                              + sourceUvs[s2] * hit.wc;
                pixels[i] = opt.sourceTexture.GetPixelBilinear(srcUv.x, srcUv.y);
                covered++;
                if (opt.onProgress != null && (i % progressTick) == 0) {
                    opt.onProgress((float)i / total);
                }
            }

            output.SetPixels(pixels);
            output.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            if (opt.onProgress != null) opt.onProgress(1f);

            return new TransferResult {
                output              = output,
                coveredTexels       = covered,
                totalTexels         = total,
                rejectedByDistance  = rejected,
                maxObservedDistance = maxDist,
            };
        }

        // -----------------------------------------------------------------
        // Helpers
        // -----------------------------------------------------------------

        internal static (Matrix4x4 source, Matrix4x4 target) ComputeAlignment(
                AlignmentMode mode, Mesh source, Mesh target,
                Matrix4x4 sourceWorld, Matrix4x4 targetWorld) {
            switch (mode) {
                case AlignmentMode.Identity:
                    return (sourceWorld, targetWorld);
                case AlignmentMode.BoundingBox:
                    return ComputeBBoxAlignment(source, target, sourceWorld, targetWorld);
                default:
                    return (sourceWorld, targetWorld);
            }
        }

        // Map source.bounds onto target.bounds with a uniform scale (avoid
        // axis-independent stretching, which would distort a humanoid). The
        // chosen scale uses the largest axis ratio so the source covers
        // the target even when one axis happens to be smaller.
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
            // T_target_center * S * T_-source_center maps source local
            // points so that source.bounds.center ends up on
            // target.bounds.center with a uniform scale.
            Matrix4x4 align =
                Matrix4x4.Translate(tb.center) *
                Matrix4x4.Scale(new Vector3(scale, scale, scale)) *
                Matrix4x4.Translate(-sb.center);
            // We're operating in the target's local space, so target stays
            // identity and the source carries the alignment.
            return (align, Matrix4x4.identity);
        }

        internal static Vector3[] TransformVertices(Vector3[] src, Matrix4x4 m) {
            if (src == null) return Array.Empty<Vector3>();
            if (m.isIdentity) return src;
            var dst = new Vector3[src.Length];
            for (int i = 0; i < src.Length; i++) dst[i] = m.MultiplyPoint3x4(src[i]);
            return dst;
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
