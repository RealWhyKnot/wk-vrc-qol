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
    }
}
