// UvTextureTransferTests.cs
//
// Pure-math coverage for UvTextureTransferCore. The end-to-end Transfer
// test wires a tiny quad-to-itself round-trip to confirm the rasterizer,
// the spatial grid, the closest-point query, and the texture sample all
// agree on the same identity. UI, FBX import, and PNG persistence live
// in sibling files and are exercised manually.

using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class UvTextureTransferTests {

        // ----------------------------------------------------------------
        // ClosestPointOnTriangle
        // ----------------------------------------------------------------

        [Test]
        public void ClosestPoint_InteriorProjection_LandsOnTrianglePlane() {
            var a = new Vector3(0, 0, 0);
            var b = new Vector3(1, 0, 0);
            var c = new Vector3(0, 1, 0);
            var p = new Vector3(0.25f, 0.25f, 5f);
            var cp = UvTextureTransferCore.ClosestPointOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
            Assert.AreEqual(0.25f, cp.x, 1e-5f);
            Assert.AreEqual(0.25f, cp.y, 1e-5f);
            Assert.AreEqual(0f,    cp.z, 1e-5f);
            Assert.AreEqual(1f, wa + wb + wc, 1e-5f, "Barycentric weights must sum to 1.");
            Assert.AreEqual(0.5f,  wa, 1e-5f);
            Assert.AreEqual(0.25f, wb, 1e-5f);
            Assert.AreEqual(0.25f, wc, 1e-5f);
        }

        [Test]
        public void ClosestPoint_VertexRegion_ProjectsToVertex() {
            var a = new Vector3(0, 0, 0);
            var b = new Vector3(1, 0, 0);
            var c = new Vector3(0, 1, 0);
            // Far outside the triangle in vertex A's region (-x, -y).
            var p = new Vector3(-2f, -3f, 1f);
            var cp = UvTextureTransferCore.ClosestPointOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
            Assert.AreEqual(a, cp);
            Assert.AreEqual(1f, wa, 1e-5f);
            Assert.AreEqual(0f, wb, 1e-5f);
            Assert.AreEqual(0f, wc, 1e-5f);
        }

        [Test]
        public void ClosestPoint_EdgeRegion_ProjectsToEdgeMidpoint() {
            var a = new Vector3(0, 0, 0);
            var b = new Vector3(1, 0, 0);
            var c = new Vector3(0, 1, 0);
            // Below the AB edge, halfway across. The closest point on
            // the triangle is the midpoint of AB.
            var p = new Vector3(0.5f, -1f, 0f);
            var cp = UvTextureTransferCore.ClosestPointOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
            Assert.AreEqual(0.5f, cp.x, 1e-5f);
            Assert.AreEqual(0f,   cp.y, 1e-5f);
            Assert.AreEqual(0f,   cp.z, 1e-5f);
            Assert.AreEqual(0.5f, wa, 1e-5f);
            Assert.AreEqual(0.5f, wb, 1e-5f);
            Assert.AreEqual(0f,   wc, 1e-5f);
        }

        [Test]
        public void ClosestPoint_PointInsideTriangle_ReturnsItself() {
            var a = new Vector3(0, 0, 0);
            var b = new Vector3(1, 0, 0);
            var c = new Vector3(0, 1, 0);
            var p = new Vector3(0.2f, 0.2f, 0f);
            var cp = UvTextureTransferCore.ClosestPointOnTriangle(p, a, b, c, out float wa, out float wb, out float wc);
            Assert.AreEqual(p, cp);
            Assert.AreEqual(1f, wa + wb + wc, 1e-5f);
        }

        // ----------------------------------------------------------------
        // SpatialGrid + QueryClosest
        // ----------------------------------------------------------------

        [Test]
        public void SpatialGrid_QueryAtVertex_ReturnsContainingTriangle() {
            // Two-triangle quad in XY plane.
            var verts = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 4);
            // Query a point inside the first triangle (0,1,2).
            var hit = UvTextureTransferCore.QueryClosest(grid, verts, triangles, new Vector3(0.6f, 0.3f, 0f));
            Assert.GreaterOrEqual(hit.triangleIndex, 0, "Query must find a triangle.");
            Assert.Less(hit.distance, 1e-4f, "Point is already on the surface; distance should be ~0.");
        }

        [Test]
        public void SpatialGrid_QueryAbovePlane_ProjectsDownToNearestTriangle() {
            var verts = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            };
            var triangles = new[] { 0, 1, 2 };
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 2);
            var hit = UvTextureTransferCore.QueryClosest(grid, verts, triangles, new Vector3(0.25f, 0.25f, 3f));
            Assert.AreEqual(0, hit.triangleIndex);
            Assert.AreEqual(3f, hit.distance, 1e-4f);
            Assert.AreEqual(0.25f, hit.point.x, 1e-5f);
            Assert.AreEqual(0.25f, hit.point.y, 1e-5f);
            Assert.AreEqual(0f,    hit.point.z, 1e-5f);
        }

        [Test]
        public void SpatialGrid_EmptyMesh_QueryReturnsNoHit() {
            var grid = UvTextureTransferCore.BuildSpatialGrid(new Vector3[0], new int[0], dim: 4);
            var hit = UvTextureTransferCore.QueryClosest(grid, new Vector3[0], new int[0], Vector3.zero);
            Assert.AreEqual(-1, hit.triangleIndex);
            Assert.IsTrue(float.IsPositiveInfinity(hit.distance));
        }

        [Test]
        public void SpatialGrid_TriangleInsertedInAtLeastOneCell() {
            // A single triangle covering a chunk of the mesh bbox must
            // appear in the flat cellTriangles store, with at least one
            // cell carrying a non-empty slice.
            var verts = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0.5f, 1, 0),
            };
            var triangles = new[] { 0, 1, 2 };
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 4);
            int totalEntries = grid.cellTriangles.Length;
            Assert.Greater(totalEntries, 0, "Flat triangle store must have at least one entry.");
            int nonEmptyCells = 0;
            for (int i = 0; i + 1 < grid.cellOffsets.Length; i++) {
                if (grid.cellOffsets[i + 1] > grid.cellOffsets[i]) nonEmptyCells++;
            }
            Assert.Greater(nonEmptyCells, 0, "At least one cell must carry the triangle.");
        }

        [Test]
        public void SpatialGrid_PrefixSumOffsetsAreMonotonic() {
            // Invariant: cellOffsets is non-decreasing and the final
            // entry equals the length of the flat triangle store. A
            // regression here would corrupt every query.
            var verts = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            };
            var triangles = new[] { 0, 1, 2, 0, 2, 3 };
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 4);
            for (int i = 0; i + 1 < grid.cellOffsets.Length; i++) {
                Assert.GreaterOrEqual(grid.cellOffsets[i + 1], grid.cellOffsets[i],
                    $"cellOffsets[{i + 1}] must be >= cellOffsets[{i}]");
            }
            Assert.AreEqual(grid.cellTriangles.Length,
                grid.cellOffsets[grid.cellOffsets.Length - 1],
                "Last offset must equal the flat triangle store's length.");
        }

        [Test]
        public void SpatialGrid_QueryFarOutsideMeshBounds_StillFindsNearestTriangle() {
            // A query way outside the source mesh must still return the
            // closest triangle (not -1). The closest-point query is the
            // backbone of mesh-to-mesh transfer; "no triangle visited"
            // would silently leave texels unmapped.
            var verts = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
            };
            var triangles = new[] { 0, 1, 2 };
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 4);
            var hit = UvTextureTransferCore.QueryClosest(grid, verts, triangles, new Vector3(50, 0, 0));
            Assert.AreEqual(0, hit.triangleIndex);
            // Closest point on the triangle to (50, 0, 0) is vertex (1, 0, 0); distance 49.
            Assert.AreEqual(1f, hit.point.x, 1e-3f);
            Assert.AreEqual(49f, hit.distance, 1e-3f);
        }

        [Test]
        public void SpatialGrid_QueryAgainstBruteForce_DenseMesh() {
            // Build a ~50-triangle blob, sample 32 random query points,
            // and confirm the grid's closest-point query reports the
            // same hit (triangle + distance) as an O(N) brute-force scan.
            // This catches AABB-early-out bugs that would otherwise
            // silently skip the actual closest triangle.
            var verts = new Vector3[64];
            for (int i = 0; i < verts.Length; i++) {
                float u = (i / 8) / 7f;
                float v = (i % 8) / 7f;
                verts[i] = new Vector3(u, v, Mathf.Sin(u * 6f) * 0.1f + Mathf.Cos(v * 4f) * 0.1f);
            }
            var tris = new System.Collections.Generic.List<int>();
            for (int y = 0; y < 7; y++) {
                for (int x = 0; x < 7; x++) {
                    int i0 = y * 8 + x;
                    int i1 = y * 8 + x + 1;
                    int i2 = (y + 1) * 8 + x;
                    int i3 = (y + 1) * 8 + x + 1;
                    tris.Add(i0); tris.Add(i1); tris.Add(i2);
                    tris.Add(i1); tris.Add(i3); tris.Add(i2);
                }
            }
            var triangles = tris.ToArray();
            var grid = UvTextureTransferCore.BuildSpatialGrid(verts, triangles, dim: 6);
            // Fixed seed so a CI flake stays reproducible.
            var rng = new System.Random(31415);
            for (int q = 0; q < 32; q++) {
                var query = new Vector3(
                    (float)rng.NextDouble() * 1.4f - 0.2f,
                    (float)rng.NextDouble() * 1.4f - 0.2f,
                    (float)rng.NextDouble() * 0.6f - 0.3f);
                var gridHit = UvTextureTransferCore.QueryClosest(grid, verts, triangles, query);
                // Brute force.
                float bruteDist = float.PositiveInfinity;
                for (int t = 0; t < triangles.Length / 3; t++) {
                    var cp = UvTextureTransferCore.ClosestPointOnTriangle(query,
                        verts[triangles[t * 3]], verts[triangles[t * 3 + 1]], verts[triangles[t * 3 + 2]],
                        out _, out _, out _);
                    float d = (query - cp).magnitude;
                    if (d < bruteDist) bruteDist = d;
                }
                Assert.AreEqual(bruteDist, gridHit.distance, 1e-4f,
                    $"Grid query must match brute force at query #{q} = {query}.");
            }
        }

        [Test]
        public void PickGridDim_ScalesWithTriangleCount() {
            Assert.AreEqual(4, UvTextureTransferCore.PickGridDim(0));
            Assert.AreEqual(4, UvTextureTransferCore.PickGridDim(10));
            int big = UvTextureTransferCore.PickGridDim(100_000);
            Assert.GreaterOrEqual(big, 16);
            Assert.LessOrEqual(big, 64);
        }

        // ----------------------------------------------------------------
        // RasterizeTargetUv
        // ----------------------------------------------------------------

        [Test]
        public void Rasterize_QuadCoversAllTexels() {
            var uvs = new[] {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1),
            };
            var tris = new[] { 0, 1, 2, 0, 2, 3 };
            var samples = UvTextureTransferCore.RasterizeTargetUv(uvs, tris, 16);
            int covered = 0;
            for (int i = 0; i < samples.Length; i++) if (samples[i].triangleIndex >= 0) covered++;
            Assert.AreEqual(16 * 16, covered, "A unit-quad UV layout must cover every output texel.");
        }

        [Test]
        public void Rasterize_TriangleEntirelyOutsideUnitSquare_IsDropped() {
            var uvs = new[] {
                new Vector2(2, 2), new Vector2(3, 2), new Vector2(2, 3),
            };
            var tris = new[] { 0, 1, 2 };
            var samples = UvTextureTransferCore.RasterizeTargetUv(uvs, tris, 8);
            for (int i = 0; i < samples.Length; i++) {
                Assert.AreEqual(-1, samples[i].triangleIndex);
            }
        }

        [Test]
        public void Rasterize_FirstWriteWins_OnOverlappingTriangles() {
            // Two triangles whose UVs overlap entirely. First-write-wins
            // means every covered texel reports triangle 0, not 1.
            var uvs = new[] {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
            };
            var tris = new[] { 0, 1, 2, 3, 4, 5 };
            var samples = UvTextureTransferCore.RasterizeTargetUv(uvs, tris, 8);
            for (int i = 0; i < samples.Length; i++) {
                if (samples[i].triangleIndex >= 0) {
                    Assert.AreEqual(0, samples[i].triangleIndex,
                        "Overlapping target UVs must resolve to the first-touching triangle.");
                }
            }
        }

        [Test]
        public void Rasterize_DegenerateUvTriangle_ProducesNoCoverage() {
            // All three UVs at the same point -> zero-area triangle.
            var uvs = new[] {
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
                new Vector2(0.5f, 0.5f),
            };
            var tris = new[] { 0, 1, 2 };
            var samples = UvTextureTransferCore.RasterizeTargetUv(uvs, tris, 4);
            for (int i = 0; i < samples.Length; i++) {
                Assert.AreEqual(-1, samples[i].triangleIndex,
                    "Degenerate (zero-area) UV triangle must not write any texel.");
            }
        }

        [Test]
        public void Rasterize_BarycentricWeightsSumToOne() {
            var uvs = new[] {
                new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
            };
            var tris = new[] { 0, 1, 2 };
            var samples = UvTextureTransferCore.RasterizeTargetUv(uvs, tris, 16);
            for (int i = 0; i < samples.Length; i++) {
                if (samples[i].triangleIndex < 0) continue;
                float sum = samples[i].wa + samples[i].wb + samples[i].wc;
                Assert.AreEqual(1f, sum, 1e-3f, "Barycentric weights stored in a sample must sum to 1.");
            }
        }

        // ----------------------------------------------------------------
        // ComputeAlignment
        // ----------------------------------------------------------------

        [Test]
        public void ComputeAlignment_Identity_PassesThroughInputMatrices() {
            var s = Matrix4x4.Translate(new Vector3(1, 2, 3));
            var t = Matrix4x4.Translate(new Vector3(4, 5, 6));
            var aligned = UvTextureTransferCore.ComputeAlignment(
                UvTextureTransferCore.AlignmentMode.Identity, null, null, s, t);
            Assert.AreEqual(s, aligned.source);
            Assert.AreEqual(t, aligned.target);
        }

        [Test]
        public void ComputeAlignment_BBox_MapsSourceCenterToTargetCenter() {
            // Source mesh: cube at origin sized 1m. Target mesh: cube at
            // (10, 0, 0) sized 2m. BoundingBox alignment should produce a
            // source matrix that scales by 2 and translates to (10, 0, 0).
            var source = BuildAxisAlignedCubeMesh(center: Vector3.zero, size: 1f);
            var target = BuildAxisAlignedCubeMesh(center: new Vector3(10, 0, 0), size: 2f);
            try {
                var aligned = UvTextureTransferCore.ComputeAlignment(
                    UvTextureTransferCore.AlignmentMode.BoundingBox,
                    source, target, Matrix4x4.identity, Matrix4x4.identity);
                // Source's (0,0,0) should map to target's (10, 0, 0).
                var origin = aligned.source.MultiplyPoint3x4(Vector3.zero);
                Assert.AreEqual(10f, origin.x, 1e-3f);
                Assert.AreEqual(0f,  origin.y, 1e-3f);
                Assert.AreEqual(0f,  origin.z, 1e-3f);
                // Source's (0.5, 0, 0) should map to target's (10 + 1, 0, 0).
                var px = aligned.source.MultiplyPoint3x4(new Vector3(0.5f, 0, 0));
                Assert.AreEqual(11f, px.x, 1e-3f);
                Assert.AreEqual(Matrix4x4.identity, aligned.target);
            } finally {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
            }
        }

        // ----------------------------------------------------------------
        // Transfer end-to-end
        //
        // Identity round-trip: source mesh = target mesh = unit quad, the
        // source texture is a 4x4 checkerboard. Every target texel that
        // gets covered should sample the same checkerboard pattern in
        // the same position because closest-point on the same mesh is
        // the point itself. Subject to small floating-point bilinear
        // interpolation drift along texel boundaries.
        // ----------------------------------------------------------------

        [Test]
        public void Transfer_QuadToItself_OutputPixelMatchesSourceAtThatUv() {
            // Identity quad-to-itself: each output texel at center UV
            // (u, v) must sample the source texture at the SAME (u, v).
            // We compare against the source's bilinear value at the
            // texel center, NOT the output's bilinear, because the
            // output is a discrete grid -- bilinear over the output
            // averages neighbouring texels and would not match the
            // continuous source bilinear at the same UV.
            var quad = BuildUnitQuadMesh();
            var src = BuildTwoByTwoTexture();
            try {
                int res = 8;
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = quad,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = quad,
                    targetSubmesh     = -1,
                    outputResolution  = res,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.identity,
                    maxDistance       = 0f,
                    gridDim           = 4,
                    fallbackColor     = new Color(0, 0, 0, 0),
                };
                var result = UvTextureTransferCore.Transfer(opt);
                Assert.IsNotNull(result.output);
                Assert.AreEqual(res, result.output.width);
                Assert.AreEqual(res, result.output.height);
                Assert.Greater(result.coveredTexels, res * res * 3 / 4,
                    "Quad-to-itself must cover the vast majority of output texels.");
                // Spot-check several texels across the output. For each,
                // the value must equal the source bilinear at that
                // texel's UV center, within a small tolerance.
                int[] xs = { 1, 3, res - 2 };
                int[] ys = { 1, 4, res - 2 };
                // Tolerance accounts for RGBA32 8-bit quantization
                // (~1/255 = 0.004 per channel) plus the rasterizer's
                // half-texel sampling offset relative to the edge of the
                // triangle's UV bounding box.
                const float tol = 0.01f;
                foreach (int y in ys) foreach (int x in xs) {
                    float u = (x + 0.5f) / res;
                    float v = (y + 0.5f) / res;
                    var expected = src.GetPixelBilinear(u, v);
                    var actual = result.output.GetPixel(x, y);
                    Assert.AreEqual(expected.r, actual.r, tol, $"R at ({x},{y})");
                    Assert.AreEqual(expected.g, actual.g, tol, $"G at ({x},{y})");
                    Assert.AreEqual(expected.b, actual.b, tol, $"B at ({x},{y})");
                }
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(quad);
                Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void Transfer_QuadToItself_FlatSourceProducesFlatOutput() {
            // The cleanest invariant: a single-color source yields a
            // single-color output at every covered texel. Tests the
            // rasterizer + closest-point + sample chain without the
            // bilinear-edge gotchas of a graded source.
            var quad = BuildUnitQuadMesh();
            var src = BuildFlatColorTexture(new Color(0.7f, 0.3f, 0.2f, 1f));
            try {
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = quad,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = quad,
                    targetSubmesh     = -1,
                    outputResolution  = 16,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.identity,
                    maxDistance       = 0f,
                    gridDim           = 4,
                    fallbackColor     = new Color(0, 0, 0, 0),
                };
                var result = UvTextureTransferCore.Transfer(opt);
                // Sample every interior texel; expect the source color.
                // Tolerance: 1/255 ~= 0.004 for 8-bit channel storage.
                const float tol = 0.01f;
                for (int y = 1; y < result.output.height - 1; y++) {
                    for (int x = 1; x < result.output.width - 1; x++) {
                        var c = result.output.GetPixel(x, y);
                        Assert.AreEqual(0.7f, c.r, tol, $"R at ({x},{y})");
                        Assert.AreEqual(0.3f, c.g, tol, $"G at ({x},{y})");
                        Assert.AreEqual(0.2f, c.b, tol, $"B at ({x},{y})");
                    }
                }
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(quad);
                Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void Transfer_RejectsTexelsBeyondMaxDistance() {
            // Source is a quad at the origin; target is the same quad
            // translated 1 m away (Identity alignment so the translation
            // sticks). Max distance 0.1 m means every target texel is
            // beyond the cap and gets the fallback color.
            var source = BuildUnitQuadMesh();
            var target = BuildUnitQuadMesh();
            var src = BuildTwoByTwoTexture();
            try {
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = source,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = target,
                    targetSubmesh     = -1,
                    outputResolution  = 4,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.Translate(new Vector3(1, 0, 0)),
                    maxDistance       = 0.1f,
                    gridDim           = 4,
                    fallbackColor     = new Color(0.9f, 0.1f, 0.1f, 1f),
                };
                var result = UvTextureTransferCore.Transfer(opt);
                Assert.AreEqual(0, result.coveredTexels,
                    "Max-distance cap should reject every texel when the meshes are 1 m apart and the cap is 10 cm.");
                Assert.Greater(result.rejectedByDistance, 0);
                var c = result.output.GetPixel(2, 2);
                Assert.AreEqual(0.9f, c.r, 1e-2f);
                Assert.AreEqual(0.1f, c.g, 1e-2f);
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(source);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void Transfer_SupersamplingAntiAliasesPartialUvCoverage() {
            var mesh = BuildSmallCornerTriangleMesh();
            var src = BuildFlatColorTexture(Color.white);
            try {
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = mesh,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = mesh,
                    targetSubmesh     = -1,
                    outputResolution  = 4,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.identity,
                    maxDistance       = 0f,
                    gridDim           = 4,
                    fallbackColor     = Color.black,
                    supersample       = 2,
                    paddingPixels     = 0,
                };
                var result = UvTextureTransferCore.Transfer(opt);
                var edge = result.output.GetPixel(1, 0);
                Assert.Greater(edge.r, 0.60f, "Three of four subpixels should be covered.");
                Assert.Less(edge.r, 0.90f, "One uncovered subpixel should pull the edge below solid white.");
                Assert.AreEqual(2, result.supersample);
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void Transfer_UvIslandPaddingExtendsCoveredEdgeColor() {
            var mesh = BuildLeftHalfUvMesh();
            var src = BuildFlatColorTexture(Color.green);
            try {
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = mesh,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = mesh,
                    targetSubmesh     = -1,
                    outputResolution  = 4,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.identity,
                    maxDistance       = 0f,
                    gridDim           = 4,
                    fallbackColor     = Color.magenta,
                    supersample       = 1,
                    paddingPixels     = 1,
                };
                var result = UvTextureTransferCore.Transfer(opt);
                var padded = result.output.GetPixel(2, 1);
                var untouched = result.output.GetPixel(3, 1);
                Assert.Less(padded.r, 0.1f, "One-pixel padding should overwrite the first fallback column.");
                Assert.Greater(padded.g, 0.9f);
                Assert.Greater(untouched.r, 0.9f, "Pixels beyond the padding distance should keep fallback color.");
                Assert.Less(untouched.g, 0.1f);
                Assert.Greater(result.paddedTexels, 0);
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(src);
            }
        }

        // ----------------------------------------------------------------
        // SampleBilinearClamp32 -- thread-safe stand-in for Texture2D.GetPixelBilinear.
        // ----------------------------------------------------------------

        [Test]
        public void SampleBilinearClamp_MatchesUnityGetPixelBilinear_OnUniformlyVaryingTexture() {
            // Build a small graded texture, then sample the same UVs
            // through Unity's GetPixelBilinear and through our manual
            // sampler. Tolerance covers the byte-rounding step at the
            // end of the manual path.
            const int size = 4;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear: true) {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            for (int y = 0; y < size; y++) {
                for (int x = 0; x < size; x++) {
                    tex.SetPixel(x, y, new Color(x / (size - 1f), y / (size - 1f), 0.25f, 1f));
                }
            }
            tex.Apply();
            var pixels = tex.GetPixels32();

            float[] us = { 0f, 0.05f, 0.25f, 0.5f, 0.75f, 0.95f, 1f };
            float[] vs = { 0f, 0.10f, 0.33f, 0.50f, 0.67f, 0.90f, 1f };
            try {
                foreach (var u in us) foreach (var v in vs) {
                    var unity = tex.GetPixelBilinear(u, v);
                    var manual = UvTextureTransferCore.SampleBilinearClamp32(pixels, size, size, u, v);
                    Assert.AreEqual(unity.r * 255f, manual.r, 1.5f, $"R at uv ({u},{v})");
                    Assert.AreEqual(unity.g * 255f, manual.g, 1.5f, $"G at uv ({u},{v})");
                    Assert.AreEqual(unity.b * 255f, manual.b, 1.5f, $"B at uv ({u},{v})");
                    Assert.AreEqual(unity.a * 255f, manual.a, 1.5f, $"A at uv ({u},{v})");
                }
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void SampleBilinearClamp_OutOfRangeUvsClampToEdgePixel() {
            // UVs < 0 or > 1 should clamp to the nearest edge pixel
            // (matches Texture2D wrap=Clamp). Without this, queries at
            // the boundary of a target UV island would smear values
            // sampled from outside [0,1].
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear: true);
            tex.SetPixel(0, 0, Color.red);
            tex.SetPixel(1, 0, Color.green);
            tex.SetPixel(0, 1, Color.blue);
            tex.SetPixel(1, 1, Color.yellow);
            tex.Apply();
            var pixels = tex.GetPixels32();
            try {
                var left  = UvTextureTransferCore.SampleBilinearClamp32(pixels, 2, 2, -1.5f, 0f);
                var right = UvTextureTransferCore.SampleBilinearClamp32(pixels, 2, 2,  2.5f, 0f);
                // Both clamp to the same row in v (y=0); horizontal
                // clamp pins us to column 0 or 1 respectively.
                Assert.AreEqual(255, left.r); Assert.AreEqual(0,   left.g);
                Assert.AreEqual(0,   right.r); Assert.AreEqual(255, right.g);
            } finally {
                Object.DestroyImmediate(tex);
            }
        }

        [Test]
        public void ColorToColor32_ClampsAndRounds() {
            var c = UvTextureTransferCore.ColorToColor32(new Color(0f, 0.5f, 1.5f, 1f));
            Assert.AreEqual(0, c.r);
            Assert.AreEqual(128, c.g);    // 0.5 * 255 + 0.5 = 128
            Assert.AreEqual(255, c.b);    // clamped to 1
            Assert.AreEqual(255, c.a);
        }

        // ----------------------------------------------------------------
        // Parallel-path consistency
        //
        // Transfer() now runs the per-texel work in Parallel.For. These
        // tests confirm the parallel implementation produces the same
        // output regardless of how the workload partitions across
        // threads.
        // ----------------------------------------------------------------

        [Test]
        public void Transfer_ParallelBakeIsDeterministicAcrossRuns() {
            // Same inputs => byte-for-byte identical output. Catches
            // accidental thread-shared state in the per-row body.
            var quad = BuildUnitQuadMesh();
            var src = BuildFlatColorTexture(new Color(0.42f, 0.65f, 0.18f, 1f));
            try {
                var opt = MakeIdentityQuadOptions(quad, src, resolution: 64);
                var run1 = UvTextureTransferCore.Transfer(opt);
                var run2 = UvTextureTransferCore.Transfer(opt);
                var p1 = run1.output.GetPixels32();
                var p2 = run2.output.GetPixels32();
                Assert.AreEqual(p1.Length, p2.Length);
                for (int i = 0; i < p1.Length; i++) {
                    Assert.AreEqual(p1[i].r, p2[i].r, $"R mismatch at pixel {i}");
                    Assert.AreEqual(p1[i].g, p2[i].g, $"G mismatch at pixel {i}");
                    Assert.AreEqual(p1[i].b, p2[i].b, $"B mismatch at pixel {i}");
                    Assert.AreEqual(p1[i].a, p2[i].a, $"A mismatch at pixel {i}");
                }
                Object.DestroyImmediate(run1.output);
                Object.DestroyImmediate(run2.output);
            } finally {
                Object.DestroyImmediate(quad);
                Object.DestroyImmediate(src);
            }
        }

        [Test]
        public void Transfer_FourTriangleStripParallelOutputCoversExpectedTexels() {
            // A larger output (256x256) with a 4-triangle strip mesh,
            // big enough that Parallel.For partitions across cores.
            // Asserts (a) total covered pixels is sensible, (b) every
            // covered pixel got the source color (not the fallback).
            // Together they show the parallel core wired the row index
            // and texel index correctly.
            var mesh = BuildTwoTriangleStripMesh();
            var src = BuildFlatColorTexture(new Color(0.20f, 0.50f, 0.80f, 1f));
            try {
                var opt = new UvTextureTransferCore.TransferOptions {
                    sourceMesh        = mesh,
                    sourceSubmesh     = -1,
                    sourceTexture     = src,
                    targetMesh        = mesh,
                    targetSubmesh     = -1,
                    outputResolution  = 256,
                    alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                    sourceWorldMatrix = Matrix4x4.identity,
                    targetWorldMatrix = Matrix4x4.identity,
                    maxDistance       = 0f,
                    gridDim           = 0,
                    fallbackColor     = new Color(0.99f, 0.01f, 0.99f, 1f),
                };
                var result = UvTextureTransferCore.Transfer(opt);
                Assert.Greater(result.coveredTexels, 256 * 256 / 4,
                    "A strip covering ~half the UV square should cover a sizable fraction of the output.");
                var pixels = result.output.GetPixels32();
                int correct = 0, wrong = 0;
                for (int i = 0; i < pixels.Length; i++) {
                    // Either the fallback colour or the source colour;
                    // anything else means the parallel core wrote
                    // garbage.
                    bool isSource   = pixels[i].r >= 40 && pixels[i].r <= 65
                                   && pixels[i].g >= 115 && pixels[i].g <= 140
                                   && pixels[i].b >= 195 && pixels[i].b <= 215;
                    bool isFallback = pixels[i].r >= 245 && pixels[i].g <= 10 && pixels[i].b >= 245;
                    if (isSource) correct++;
                    else if (isFallback) { /* uncovered texel, fine */ }
                    else wrong++;
                }
                Assert.AreEqual(0, wrong, "No pixel should hold an unexpected colour.");
                Assert.AreEqual(result.coveredTexels, correct, "Covered count must agree with source-coloured pixel count.");
                Object.DestroyImmediate(result.output);
            } finally {
                Object.DestroyImmediate(mesh);
                Object.DestroyImmediate(src);
            }
        }

        // ----------------------------------------------------------------
        // Helpers
        // ----------------------------------------------------------------

        private static Mesh BuildUnitQuadMesh() {
            var mesh = new Mesh { name = "TestQuad" };
            mesh.vertices = new[] {
                new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                new Vector3(1, 1, 0), new Vector3(0, 1, 0),
            };
            mesh.uv = new[] {
                new Vector2(0, 0), new Vector2(1, 0),
                new Vector2(1, 1), new Vector2(0, 1),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildAxisAlignedCubeMesh(Vector3 center, float size) {
            // 8 verts, 12 triangles; UVs not needed for bbox alignment test.
            float h = size * 0.5f;
            var verts = new Vector3[8];
            int i = 0;
            for (int z = -1; z <= 1; z += 2)
            for (int y = -1; y <= 1; y += 2)
            for (int x = -1; x <= 1; x += 2)
                verts[i++] = center + new Vector3(x * h, y * h, z * h);
            var mesh = new Mesh { name = "TestCube" };
            mesh.vertices = verts;
            mesh.triangles = new[] {
                0, 1, 2, 1, 3, 2,   4, 6, 5, 5, 6, 7,
                0, 2, 4, 2, 6, 4,   1, 5, 3, 3, 5, 7,
                0, 4, 1, 1, 4, 5,   2, 3, 6, 3, 7, 6,
            };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static UvTextureTransferCore.TransferOptions MakeIdentityQuadOptions(
                Mesh mesh, Texture2D src, int resolution) {
            return new UvTextureTransferCore.TransferOptions {
                sourceMesh        = mesh,
                sourceSubmesh     = -1,
                sourceTexture     = src,
                targetMesh        = mesh,
                targetSubmesh     = -1,
                outputResolution  = resolution,
                alignment         = UvTextureTransferCore.AlignmentMode.Identity,
                sourceWorldMatrix = Matrix4x4.identity,
                targetWorldMatrix = Matrix4x4.identity,
                maxDistance       = 0f,
                gridDim           = 4,
                fallbackColor     = new Color(0, 0, 0, 0),
            };
        }

        private static Mesh BuildTwoTriangleStripMesh() {
            // A 4-vertex 2-triangle strip whose UVs occupy a half of
            // the unit square. Big enough for Parallel.For to actually
            // shard rows across worker threads.
            var mesh = new Mesh { name = "TestStrip" };
            mesh.vertices = new[] {
                new Vector3(0,   0, 0), new Vector3(1, 0, 0),
                new Vector3(0, 0.5f, 0), new Vector3(1, 0.5f, 0),
            };
            mesh.uv = new[] {
                new Vector2(0,   0), new Vector2(1,   0),
                new Vector2(0, 0.5f), new Vector2(1, 0.5f),
            };
            mesh.triangles = new[] { 0, 1, 2, 1, 3, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildSmallCornerTriangleMesh() {
            var mesh = new Mesh { name = "SmallCornerTriangle" };
            mesh.vertices = new[] {
                new Vector3(0, 0, 0),
                new Vector3(0.52f, 0, 0),
                new Vector3(0, 0.52f, 0),
            };
            mesh.uv = new[] {
                new Vector2(0, 0),
                new Vector2(0.52f, 0),
                new Vector2(0, 0.52f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh BuildLeftHalfUvMesh() {
            var mesh = new Mesh { name = "LeftHalfUvQuad" };
            mesh.vertices = new[] {
                new Vector3(0, 0, 0), new Vector3(0.5f, 0, 0),
                new Vector3(0.5f, 1, 0), new Vector3(0, 1, 0),
            };
            mesh.uv = new[] {
                new Vector2(0, 0), new Vector2(0.5f, 0),
                new Vector2(0.5f, 1), new Vector2(0, 1),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Texture2D BuildFlatColorTexture(Color color) {
            var t = new Texture2D(4, 4, TextureFormat.RGBA32, mipChain: false, linear: true) {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            var px = new Color[16];
            for (int i = 0; i < px.Length; i++) px[i] = color;
            t.SetPixels(px);
            t.Apply();
            return t;
        }

        private static Texture2D BuildTwoByTwoTexture() {
            // 2x2 checkerboard: (0,0)=red, (1,0)=green, (0,1)=blue, (1,1)=yellow.
            // Linear color space; the Transfer pipeline treats textures as
            // data, not gamma-encoded.
            var t = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false, linear: true) {
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            t.SetPixel(0, 0, Color.red);
            t.SetPixel(1, 0, Color.green);
            t.SetPixel(0, 1, Color.blue);
            t.SetPixel(1, 1, Color.yellow);
            t.Apply();
            return t;
        }
    }
}
