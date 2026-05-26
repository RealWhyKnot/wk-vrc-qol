// MaskPainterMathTests.cs
//
// Pure-math coverage for the Mask Painter helpers. The painting loop
// itself, the GPU shader passes, and the Scene view brush UX are all
// hardware/Editor-bound and are exercised manually with the verification
// steps in the plan doc.
//
// The triangle (v0,v1,v2) = ((0,0,0),(1,0,0),(0,1,0)) has its normal
// pointing +Z (right-hand rule on v0->v1->v2 winding). For our ray-tri
// test (two-sided, picks closest intersection), the side of the triangle
// the ray comes from doesn't matter -- only that the ray crosses the
// plane and the hit lands inside the triangle. That matches what avatar
// painters expect: clicking on what's visible, regardless of whether the
// nearest mesh polygon happens to face the camera.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class MaskPainterMathTests {

        // ----------------------------------------------------------------
        // Ray-triangle intersection (Moller-Trumbore, two-sided)
        // ----------------------------------------------------------------

        [Test]
        public void RayTriangle_HitsCenterFromFrontSide() {
            // Triangle plane at z=0, normal +Z. Ray comes from +Z (in front
            // of the triangle's normal direction), fires toward -Z.
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            var origin = new Vector3(0.25f, 0.25f, 5f);
            var dir    = -Vector3.forward;

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out float t, out float u, out float v);

            Assert.IsTrue(hit, "Ray from the front-normal side should hit.");
            Assert.AreEqual(5f, t, 1e-4f, "t should equal the distance from origin to the plane.");
            Assert.AreEqual(0.25f, u, 1e-4f, "u should equal the x barycentric weight on v1.");
            Assert.AreEqual(0.25f, v, 1e-4f, "v should equal the y barycentric weight on v2.");
        }

        [Test]
        public void RayTriangle_HitsCenterFromBackSide() {
            // Same triangle, but ray comes from -Z (behind the normal),
            // fires toward +Z. Two-sided test should still register a hit.
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            var origin = new Vector3(0.25f, 0.25f, -5f);
            var dir    = Vector3.forward;

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out float t, out _, out _);

            Assert.IsTrue(hit, "Two-sided test should register the back-side hit too.");
            Assert.AreEqual(5f, t, 1e-4f);
        }

        [Test]
        public void RayTriangle_MissesWhenRayPointsAway() {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            // Origin in front of triangle, ray fires further away.
            var origin = new Vector3(0.25f, 0.25f, 5f);
            var dir    = Vector3.forward;

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out _, out _, out _);
            Assert.IsFalse(hit, "Ray pointing away from triangle should miss.");
        }

        [Test]
        public void RayTriangle_MissesWhenOriginIsPastTriangleAndRayContinues() {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            var origin = new Vector3(0.25f, 0.25f, -5f); // already past the triangle in -Z
            var dir    = -Vector3.forward;                // continues in -Z

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out _, out _, out _);
            Assert.IsFalse(hit, "Ray origin past the triangle, firing further away, must miss.");
        }

        [Test]
        public void RayTriangle_MissesWhenRayIsParallelToPlane() {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            var origin = new Vector3(0.25f, 0.25f, -5f);
            var dir    = Vector3.right; // parallel to the z=0 plane

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out _, out _, out _);
            Assert.IsFalse(hit, "Ray parallel to the triangle plane must miss.");
        }

        [Test]
        public void RayTriangle_MissesOutsideBarycentricRange() {
            var v0 = new Vector3(0, 0, 0);
            var v1 = new Vector3(1, 0, 0);
            var v2 = new Vector3(0, 1, 0);
            var origin = new Vector3(2f, 2f, 5f); // ray would land far outside the triangle
            var dir    = -Vector3.forward;

            bool hit = MaskPainterIO.RayTriangle(origin, dir, v0, v1, v2, out _, out _, out _);
            Assert.IsFalse(hit, "Hit outside the triangle's barycentric region must miss.");
        }

        // ----------------------------------------------------------------
        // Barycentric -> UV interpolation
        // ----------------------------------------------------------------

        [Test]
        public void InterpolateUv_AtVertexCornersReturnsThoseVertices() {
            var uv0 = new Vector2(0.1f, 0.2f);
            var uv1 = new Vector2(0.7f, 0.3f);
            var uv2 = new Vector2(0.4f, 0.9f);

            Assert.AreEqual(uv0, MaskPainterIO.InterpolateUv(uv0, uv1, uv2, 0f, 0f), "u=v=0 -> uv0");
            Assert.AreEqual(uv1, MaskPainterIO.InterpolateUv(uv0, uv1, uv2, 1f, 0f), "u=1 -> uv1");
            Assert.AreEqual(uv2, MaskPainterIO.InterpolateUv(uv0, uv1, uv2, 0f, 1f), "v=1 -> uv2");
        }

        [Test]
        public void InterpolateUv_AtCentroidIsAverageOfVertices() {
            var uv0 = new Vector2(0f, 0f);
            var uv1 = new Vector2(1f, 0f);
            var uv2 = new Vector2(0f, 1f);
            // Centroid: barycentric (1/3, 1/3, 1/3) -> u = 1/3, v = 1/3
            var result = MaskPainterIO.InterpolateUv(uv0, uv1, uv2, 1f / 3f, 1f / 3f);
            Assert.AreEqual(1f / 3f, result.x, 1e-5f);
            Assert.AreEqual(1f / 3f, result.y, 1e-5f);
        }

        // ----------------------------------------------------------------
        // Mirror across local X
        // ----------------------------------------------------------------

        [Test]
        public void Mirror_NullRoot_FlipsWorldX() {
            var p = new Vector3(0.5f, 1.0f, 0.25f);
            var m = MaskPainterIO.MirrorAcrossLocalX(p, null);
            Assert.AreEqual(-0.5f, m.x, 1e-5f);
            Assert.AreEqual( 1.0f, m.y, 1e-5f);
            Assert.AreEqual( 0.25f, m.z, 1e-5f);
        }

        [Test]
        public void Mirror_IdentityRoot_FlipsWorldX() {
            var root = new GameObject("SymRoot").transform;
            try {
                var p = new Vector3(0.5f, 1.0f, 0.25f);
                var m = MaskPainterIO.MirrorAcrossLocalX(p, root);
                Assert.AreEqual(-0.5f, m.x, 1e-5f);
                Assert.AreEqual( 1.0f, m.y, 1e-5f);
                Assert.AreEqual( 0.25f, m.z, 1e-5f);
            } finally {
                Object.DestroyImmediate(root.gameObject);
            }
        }

        [Test]
        public void Mirror_TranslatedRoot_MirrorsAboutRootLocalX() {
            // Root translated to x=10. Mirroring a world point at x=12 through
            // root-local X should land at world x=8 (root.x - 2).
            var rootGo = new GameObject("SymRoot");
            rootGo.transform.position = new Vector3(10f, 0f, 0f);
            try {
                var p = new Vector3(12f, 1f, 0f);
                var m = MaskPainterIO.MirrorAcrossLocalX(p, rootGo.transform);
                Assert.AreEqual(8f, m.x, 1e-4f);
                Assert.AreEqual(1f, m.y, 1e-5f);
                Assert.AreEqual(0f, m.z, 1e-5f);
            } finally {
                Object.DestroyImmediate(rootGo);
            }
        }

        [Test]
        public void Mirror_RootRotated90Y_MirrorsAcrossLocalXFacingZ() {
            // Rotate root 90 deg around Y -- the root's local X axis now
            // points along world +Z. A point at world (0,0,3) is at local
            // x=3; mirroring flips local x to -3, which becomes world (0,0,-3).
            var rootGo = new GameObject("SymRoot");
            rootGo.transform.rotation = Quaternion.Euler(0f, 90f, 0f);
            try {
                var p = new Vector3(0f, 1f, 3f);
                var m = MaskPainterIO.MirrorAcrossLocalX(p, rootGo.transform);
                Assert.AreEqual( 0f, m.x, 1e-4f);
                Assert.AreEqual( 1f, m.y, 1e-5f);
                Assert.AreEqual(-3f, m.z, 1e-4f);
            } finally {
                Object.DestroyImmediate(rootGo);
            }
        }

        // ----------------------------------------------------------------
        // Submesh range resolver
        //
        // The painter used to compute (subStart, subEnd) inline at three
        // call sites with `Mathf.Min(_submeshIndex + 1, subMeshCount)`,
        // which silently produced an empty range when the stored index
        // drifted past the snapshot mesh's submesh count -- every click
        // missed, no exception, no warning. SubmeshRange is the central
        // fix; these tests pin its behaviour so the regression can't come
        // back.
        // ----------------------------------------------------------------

        [Test]
        public void SubmeshRange_NegOneIteratesAllSubmeshes() {
            var range = MaskPainterIO.SubmeshRange(-1, 4);
            Assert.AreEqual(0, range.start);
            Assert.AreEqual(4, range.end);
            Assert.IsFalse(range.fellBackToAll, "Negative-one is the explicit 'all' sentinel, not a fallback.");
        }

        [Test]
        public void SubmeshRange_InRangeIndexIteratesJustThatSubmesh() {
            var range = MaskPainterIO.SubmeshRange(2, 4);
            Assert.AreEqual(2, range.start);
            Assert.AreEqual(3, range.end);
            Assert.IsFalse(range.fellBackToAll);
        }

        [Test]
        public void SubmeshRange_OutOfRangeFallsBackToAllAndWarns() {
            string warning = null;
            // requestedIndex == subMeshCount is the precise footgun case.
            var range = MaskPainterIO.SubmeshRange(3, 3, msg => warning = msg);
            Assert.AreEqual(0, range.start);
            Assert.AreEqual(3, range.end, "Fallback must iterate every submesh, not zero.");
            Assert.IsTrue(range.fellBackToAll);
            Assert.IsNotNull(warning, "Caller must be told when its stored index drifted out of range.");
            StringAssert.Contains("3", warning);
            StringAssert.Contains("subMeshCount", warning);
        }

        [Test]
        public void SubmeshRange_NegativeOtherThanMinusOneFallsBack() {
            string warning = null;
            var range = MaskPainterIO.SubmeshRange(-5, 2, msg => warning = msg);
            Assert.AreEqual(0, range.start);
            Assert.AreEqual(2, range.end);
            Assert.IsTrue(range.fellBackToAll);
            Assert.IsNotNull(warning);
        }

        [Test]
        public void SubmeshRange_ZeroSubmeshMeshReturnsEmptyRange() {
            // A degenerate snapshot (no submeshes) returns (0,0); the
            // caller's for-loop body simply doesn't execute. We do NOT
            // warn here because there's nothing the user could do about
            // the empty mesh.
            string warning = null;
            var range = MaskPainterIO.SubmeshRange(-1, 0, msg => warning = msg);
            Assert.AreEqual(0, range.start);
            Assert.AreEqual(0, range.end);
            Assert.IsFalse(range.fellBackToAll);
            Assert.IsNull(warning);
        }

        // ----------------------------------------------------------------
        // UV wireframe line rasterizer
        //
        // GenerateUvWireframe uses DrawUvLine to trace triangle edges.
        // The line clipping rule -- drop any line whose endpoints exit
        // [0,1]^2 -- exists so UVs that tile or wrap don't smear false
        // edges along the texture border.
        // ----------------------------------------------------------------

        [Test]
        public void DrawUvLine_InBoundsHorizontalLineWritesPixels() {
            const int size = 16;
            var pixels = new Color32[size * size];
            // y = 0.5 horizontal line from x=0.1 to x=0.9.
            MaskPainterIO.DrawUvLine(pixels, size, new Vector2(0.1f, 0.5f), new Vector2(0.9f, 0.5f),
                new Color32(255, 255, 255, 255));
            int lit = 0;
            for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 0) lit++;
            Assert.Greater(lit, 0, "An in-bounds horizontal segment must light at least one texel.");
        }

        [Test]
        public void DrawUvLine_BothEndpointsOutsideAreDropped() {
            const int size = 16;
            var pixels = new Color32[size * size];
            MaskPainterIO.DrawUvLine(pixels, size, new Vector2(-0.5f, 0.5f), new Vector2(1.5f, 0.5f),
                new Color32(255, 255, 255, 255));
            for (int i = 0; i < pixels.Length; i++) {
                Assert.AreEqual(0, pixels[i].a,
                    "A line whose endpoints both sit outside [0,1] would smear a fake border across the preview if drawn.");
            }
        }

        [Test]
        public void DrawUvLine_OneEndpointOutsideIsDropped() {
            // The current rule is conservative: if either endpoint is
            // outside [0,1] the whole edge is dropped. Documenting that
            // explicitly so a future tweak (clip-to-border) has to update
            // the test rather than silently change behaviour.
            const int size = 16;
            var pixels = new Color32[size * size];
            MaskPainterIO.DrawUvLine(pixels, size, new Vector2(0.5f, 0.5f), new Vector2(1.5f, 0.5f),
                new Color32(255, 255, 255, 255));
            for (int i = 0; i < pixels.Length; i++) {
                Assert.AreEqual(0, pixels[i].a);
            }
        }

        [Test]
        public void DrawUvLine_DegenerateZeroLengthLightsOnePixel() {
            const int size = 8;
            var pixels = new Color32[size * size];
            MaskPainterIO.DrawUvLine(pixels, size, new Vector2(0.5f, 0.5f), new Vector2(0.5f, 0.5f),
                new Color32(255, 255, 255, 255));
            int lit = 0;
            for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 0) lit++;
            Assert.AreEqual(1, lit, "A zero-length segment should light exactly the endpoint pixel.");
        }

        // ----------------------------------------------------------------
        // GenerateUvWireframe end-to-end
        // ----------------------------------------------------------------

        [Test]
        public void GenerateUvWireframe_QuadProducesNonEmptyTexture() {
            // Two-triangle quad whose UVs fill [0,1]^2. Every triangle
            // edge sits inside the box, so the wireframe must have lit
            // pixels along all four sides plus the diagonal.
            var mesh = new Mesh {
                vertices = new[] {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0),
                    new Vector3(1, 1, 0), new Vector3(0, 1, 0),
                },
                uv = new[] {
                    new Vector2(0, 0), new Vector2(1, 0),
                    new Vector2(1, 1), new Vector2(0, 1),
                },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
            };
            try {
                var tex = MaskPainterIO.GenerateUvWireframe(mesh, submeshIndex: -1, size: 32);
                Assert.IsNotNull(tex);
                Assert.AreEqual(32, tex.width);
                Assert.AreEqual(32, tex.height);
                var pixels = tex.GetPixels32();
                int lit = 0;
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 0) lit++;
                Assert.Greater(lit, 32 * 3,
                    "A 1-unit quad with the diagonal should light at least ~4 borders worth of pixels.");
                Object.DestroyImmediate(tex);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void GenerateUvWireframe_NullMeshIsTransparentTexture() {
            var tex = MaskPainterIO.GenerateUvWireframe(null, submeshIndex: -1, size: 16);
            Assert.IsNotNull(tex);
            var pixels = tex.GetPixels32();
            for (int i = 0; i < pixels.Length; i++) {
                Assert.AreEqual(0, pixels[i].a,
                    "A null mesh must yield a fully transparent wireframe, not a partial / random texture.");
            }
            Object.DestroyImmediate(tex);
        }

        // ----------------------------------------------------------------
        // Bake convention
        //
        // The painter snapshots the deformed mesh via
        // SkinnedMeshRenderer.BakeMesh(useScale: true) and multiplies the
        // baked vertices by transform.localToWorldMatrix to reach world
        // space. The previous pairing (useScale: false + localToWorldMatrix)
        // silently broke for SMRs at non-unit scale: vertices came out at
        // world-rendered size already, and re-applying the SMR scale through
        // localToWorldMatrix landed the snapshot ~100x off on a typical
        // 100x Blender-import avatar. Every SceneView ray missed the AABB
        // and the painter said "off mesh" everywhere.
        //
        // This test pins the corrected pairing against an SMR at scale 100.
        // ----------------------------------------------------------------

        [Test]
        public void BakeConvention_TrueScaleTimesLocalToWorld_MatchesRenderedSize() {
            var rig = new GameObject("WkMaskPainterBakeTestRig");
            Mesh sourceMesh = null;
            Mesh baked = null;
            try {
                // Bind weights while the rig is at unit scale -- bindpose
                // captures bone.worldToLocalMatrix at that moment, so the
                // identity transform here gives an identity bindpose.
                rig.transform.position = Vector3.zero;
                rig.transform.rotation = Quaternion.identity;
                rig.transform.localScale = Vector3.one;

                var boneGo = new GameObject("Bone");
                boneGo.transform.parent = rig.transform;
                boneGo.transform.localPosition = Vector3.zero;
                boneGo.transform.localRotation = Quaternion.identity;
                boneGo.transform.localScale = Vector3.one;
                var bone = boneGo.transform;

                var smr = rig.AddComponent<SkinnedMeshRenderer>();
                sourceMesh = BuildUnitCubeSkinnedMesh(bone);
                smr.sharedMesh = sourceMesh;
                smr.bones = new[] { bone };
                smr.rootBone = bone;

                // Scale the rig 100x after binding. The rendered cube now
                // fills a 100-unit world box.
                rig.transform.localScale = Vector3.one * 100f;

                baked = new Mesh { hideFlags = HideFlags.HideAndDontSave };
                smr.BakeMesh(baked, useScale: true);
                var verts = baked.vertices;
                Assert.AreEqual(8, verts.Length, "Unit cube should bake to 8 vertices.");

                var m = smr.transform.localToWorldMatrix;
                var b = new Bounds(m.MultiplyPoint3x4(verts[0]), Vector3.zero);
                for (int i = 1; i < verts.Length; i++) {
                    b.Encapsulate(m.MultiplyPoint3x4(verts[i]));
                }

                // Unit cube at SMR scale 100 -> world size ~100. 5% slack
                // matches the runtime convention-check threshold in
                // VerifyBakeConvention. A result near 10000 indicates the
                // old double-counting bug.
                Assert.GreaterOrEqual(b.size.x, 95f,
                    $"Baked world bounds {b.size} too small. Expected ~100 for a unit cube at SMR scale 100.");
                Assert.LessOrEqual(b.size.x, 105f,
                    $"Baked world bounds {b.size} too large. Expected ~100; a result near 10000 indicates BakeMesh + localToWorldMatrix is double-counting the SMR scale.");
                Assert.GreaterOrEqual(b.size.y, 95f);
                Assert.LessOrEqual(b.size.y, 105f);
                Assert.GreaterOrEqual(b.size.z, 95f);
                Assert.LessOrEqual(b.size.z, 105f);
            } finally {
                if (baked != null) Object.DestroyImmediate(baked);
                if (sourceMesh != null) Object.DestroyImmediate(sourceMesh);
                Object.DestroyImmediate(rig);
            }
        }

        private static Mesh BuildUnitCubeSkinnedMesh(Transform bone) {
            var mesh = new Mesh {
                name = "WkMaskPainterTestUnitCube",
                hideFlags = HideFlags.HideAndDontSave,
            };
            var verts = new Vector3[8];
            int idx = 0;
            for (int z = 0; z < 2; z++) {
                for (int y = 0; y < 2; y++) {
                    for (int x = 0; x < 2; x++) {
                        verts[idx++] = new Vector3(x - 0.5f, y - 0.5f, z - 0.5f);
                    }
                }
            }
            mesh.vertices = verts;
            mesh.triangles = new int[] {
                0, 2, 1,  1, 2, 3,
                4, 5, 6,  5, 7, 6,
                0, 1, 4,  1, 5, 4,
                2, 6, 3,  3, 6, 7,
                0, 4, 2,  2, 4, 6,
                1, 3, 5,  3, 7, 5,
            };
            var weights = new BoneWeight[8];
            for (int i = 0; i < 8; i++) {
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bone.worldToLocalMatrix };
            mesh.RecalculateBounds();
            return mesh;
        }

        [Test]
        public void GenerateUvWireframe_OutOfRangeSubmeshFallsBackToAll() {
            // SubmeshRange's fallback path runs inside GenerateUvWireframe
            // too. A quad with one submesh, asked for submesh #5, should
            // still produce a wireframe (all submeshes) instead of a
            // blank texture.
            var mesh = new Mesh {
                vertices = new[] {
                    new Vector3(0, 0, 0), new Vector3(1, 0, 0), new Vector3(0, 1, 0),
                },
                uv = new[] {
                    new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1),
                },
                triangles = new[] { 0, 1, 2 },
            };
            try {
                var tex = MaskPainterIO.GenerateUvWireframe(mesh, submeshIndex: 5, size: 32);
                var pixels = tex.GetPixels32();
                int lit = 0;
                for (int i = 0; i < pixels.Length; i++) if (pixels[i].a > 0) lit++;
                Assert.Greater(lit, 0,
                    "Out-of-range submesh must fall back to 'all' so the preview isn't mysteriously blank.");
                Object.DestroyImmediate(tex);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }
    }
}
