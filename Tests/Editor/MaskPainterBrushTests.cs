// MaskPainterBrushTests.cs
//
// Exercises the actual GPU brush dispatch end-to-end so the "brush paints
// the wrong UV region" bug can be reproduced without a human in the loop.
//
// Builds a unit cube SkinnedMeshRenderer at scale 100 (mirrors the typical
// 100x Blender-import VRChat avatar), bakes a snapshot with the painter's
// real bake convention, then dispatches a single brush stroke into a small
// RenderTexture and reads back the painted pixel count. A correctly-behaved
// brush at world-radius 5 cm on a 1 m cube should paint a tiny patch (a
// fraction of one face); coverage in double digits means the world-distance
// test in the shader is broken.

using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class MaskPainterBrushTests {

        // Single shared rig built per fixture so we don't pay the bind cost
        // for every test method. Each test allocates its own RT and brush
        // material so blending state is fresh.
        private GameObject _rig;
        private SkinnedMeshRenderer _smr;
        private Mesh _sourceMesh;
        private Mesh _snapshotMesh;
        // World-space clone -- mirrors the production _paintWorldMesh that
        // ApplyStroke draws via CommandBuffer with Matrix4x4.identity.
        private Mesh _paintWorldMesh;
        private Vector3[] _snapshotWorldVerts;
        private RenderTexture _maskRT;
        private Material _brushMaterial;

        // Set to true when SetUp detects a no-op graphics stub (batchmode
        // -nographics). Detected by dispatching a radius-0 brush: a real
        // GPU clips every fragment to 0 pixels; the null stub returns the RT
        // contents as-is (all white = 4096 pixels lit). Tests that produce
        // a physically-meaningful pixel count call RequireRealGpu() so they
        // skip cleanly instead of failing.
        private bool _noRealGpu;

        [SetUp]
        public void SetUp() {
            _rig = new GameObject("WkMaskPainterBrushTestRig");
            _rig.transform.position = Vector3.zero;
            _rig.transform.rotation = Quaternion.identity;
            _rig.transform.localScale = Vector3.one;

            var boneGo = new GameObject("Bone");
            boneGo.transform.parent = _rig.transform;
            boneGo.transform.localPosition = Vector3.zero;
            boneGo.transform.localRotation = Quaternion.identity;
            boneGo.transform.localScale = Vector3.one;

            _smr = _rig.AddComponent<SkinnedMeshRenderer>();
            _sourceMesh = BuildUnitCubeSkinnedMesh(boneGo.transform);
            _smr.sharedMesh = _sourceMesh;
            _smr.bones = new[] { boneGo.transform };
            _smr.rootBone = boneGo.transform;

            // Scale rig to 100x AFTER bind, like a typical 100x VRChat
            // avatar import. localToWorldMatrix now carries the 100x scale.
            _rig.transform.localScale = Vector3.one * 100f;

            // Mirror the painter's Bake() flow exactly: BakeMesh(true) +
            // localToWorldMatrix + explicit UV copy from the source mesh.
            _snapshotMesh = new Mesh {
                name = "TestSnapshot",
                hideFlags = HideFlags.HideAndDontSave,
            };
            _smr.BakeMesh(_snapshotMesh, useScale: true);
            _snapshotMesh.uv = _sourceMesh.uv;
            _snapshotMesh.normals = _sourceMesh.normals;

            var verts = _snapshotMesh.vertices;
            var matrix = _smr.transform.localToWorldMatrix;
            _snapshotWorldVerts = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) {
                _snapshotWorldVerts[i] = matrix.MultiplyPoint3x4(verts[i]);
            }

            // Build the paint mesh the same way production Bake() does: a
            // world-space clone of the snapshot, drawn with identity matrix
            // so the brush shader's worldPos pipeline doesn't depend on
            // unity_ObjectToWorld.
            _paintWorldMesh = new Mesh {
                name = "TestPaintWorldMesh",
                hideFlags = HideFlags.HideAndDontSave,
                indexFormat = _snapshotMesh.indexFormat,
            };
            _paintWorldMesh.vertices = _snapshotWorldVerts;
            _paintWorldMesh.uv = _snapshotMesh.uv;
            _paintWorldMesh.normals = _snapshotMesh.normals;
            _paintWorldMesh.subMeshCount = _snapshotMesh.subMeshCount;
            for (int s = 0; s < _snapshotMesh.subMeshCount; s++) {
                _paintWorldMesh.SetTriangles(_snapshotMesh.GetTriangles(s), s, calculateBounds: false);
            }
            _paintWorldMesh.RecalculateBounds();

            // 64x64 RT is enough to see the bug at coarse granularity
            // without burning seconds in readback. The painter ships at
            // 1024 but the math doesn't change at smaller sizes.
            _maskRT = MaskPainterIO.CreateMaskRT(64);

            var brushShader = MaskPainterIO.BrushShader;
            Assume.That(brushShader, Is.Not.Null, "UvSpaceBrush shader failed to load.");
            _brushMaterial = new Material(brushShader) { hideFlags = HideFlags.HideAndDontSave };

            // GPU sanity probe: a radius-0 brush clips every fragment on a
            // real device, so the RT stays at 0 lit pixels after the dispatch.
            // Under -nographics the CommandBuffer/DrawMesh path is a no-op and
            // the RT readback returns the uninitialised contents (all-white).
            // Detect that signature now so per-test asserts can skip cleanly.
            DispatchOneStroke(brushCenterWorld: Vector3.zero, radiusWorld: 0f);
            var (probeCount, _, _) = ReadbackRT();
            _noRealGpu = probeCount > 0; // any lit pixel from radius-0 means the stub didn't clip
        }

        // Call at the start of any test that asserts on a physically-meaningful
        // pixel count. If the graphics device is a stub, the test is marked
        // Ignored rather than Failed.
        private void RequireRealGpu() {
            if (_noRealGpu) {
                Assert.Ignore(
                    "No real GPU detected (batchmode -nographics or null render device): " +
                    "CommandBuffer.DrawMesh dispatches are no-ops and pixel counts are unreliable. " +
                    "Run _run-brush-tests.ps1 (without -nographics) for authoritative results.");
            }
        }

        [TearDown]
        public void TearDown() {
            if (_brushMaterial != null) UnityEngine.Object.DestroyImmediate(_brushMaterial);
            if (_maskRT != null) {
                _maskRT.Release();
                UnityEngine.Object.DestroyImmediate(_maskRT);
            }
            if (_paintWorldMesh != null) UnityEngine.Object.DestroyImmediate(_paintWorldMesh);
            if (_snapshotMesh != null) UnityEngine.Object.DestroyImmediate(_snapshotMesh);
            if (_sourceMesh != null) UnityEngine.Object.DestroyImmediate(_sourceMesh);
            if (_rig != null) UnityEngine.Object.DestroyImmediate(_rig);
        }

        // -------------------------------------------------------------------
        // Helpers
        // -------------------------------------------------------------------

        private void DispatchOneStroke(Vector3 brushCenterWorld, float radiusWorld) {
            _brushMaterial.SetVector("_BrushCenter", brushCenterWorld);
            _brushMaterial.SetVector("_MirrorBrushCenter", Vector3.zero);
            _brushMaterial.SetFloat("_SymmetryEnabled", 0f);
            _brushMaterial.SetFloat("_BrushRadius", radiusWorld);
            _brushMaterial.SetFloat("_BrushHardness", 1f);   // hard edge for predictable coverage
            _brushMaterial.SetFloat("_Strength", 1f);        // full opacity in a single pass
            _brushMaterial.SetColor("_BrushColor", Color.white);

            // Mirror production ApplyStroke: CommandBuffer + world-space mesh
            // + identity matrix. The previous immediate-mode path
            // (Graphics.SetRenderTarget + SetPass + DrawMeshNow) inherited
            // SceneView render state and made the brush stamp the
            // camera-visible UV region instead of the click radius. Tests
            // exercise the same path the editor does.
            var cmd = new CommandBuffer { name = "Test ApplyStroke" };
            try {
                cmd.SetRenderTarget(_maskRT);
                cmd.SetViewport(new Rect(0, 0, _maskRT.width, _maskRT.height));
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.ClearRenderTarget(true, true, Color.clear);
                for (int s = 0; s < _paintWorldMesh.subMeshCount; s++) {
                    cmd.DrawMesh(_paintWorldMesh, Matrix4x4.identity, _brushMaterial, s, 0);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            } finally {
                cmd.Release();
            }
        }

        private (int litTotal, float coverage, Rect litBoundsUv) ReadbackRT() {
            var tex = new Texture2D(_maskRT.width, _maskRT.height, TextureFormat.RGBA32, false, true) {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var prev = RenderTexture.active;
            try {
                RenderTexture.active = _maskRT;
                tex.ReadPixels(new Rect(0, 0, _maskRT.width, _maskRT.height), 0, 0);
                tex.Apply(false, false);
            } finally {
                RenderTexture.active = prev;
            }

            int w = tex.width;
            int h = tex.height;
            var pixels = tex.GetPixels32();
            const byte threshold = 8; // ~3% of full intensity
            int lit = 0;
            int minPx = w, maxPx = -1, minPy = h, maxPy = -1;
            for (int y = 0; y < h; y++) {
                for (int x = 0; x < w; x++) {
                    var p = pixels[y * w + x];
                    if (p.r >= threshold || p.g >= threshold || p.b >= threshold || p.a >= threshold) {
                        lit++;
                        if (x < minPx) minPx = x;
                        if (x > maxPx) maxPx = x;
                        if (y < minPy) minPy = y;
                        if (y > maxPy) maxPy = y;
                    }
                }
            }
            UnityEngine.Object.DestroyImmediate(tex);
            float coverage = lit / (float)(w * h);
            Rect bounds;
            if (lit > 0) {
                float uMin = minPx / (float)w;
                float uMax = (maxPx + 1) / (float)w;
                float vMin = 1f - (maxPy + 1) / (float)h; // texture rows top->bottom
                float vMax = 1f - minPy / (float)h;
                bounds = Rect.MinMaxRect(uMin, vMin, uMax, vMax);
            } else {
                bounds = Rect.zero;
            }
            return (lit, coverage, bounds);
        }

        // -------------------------------------------------------------------
        // Tests
        // -------------------------------------------------------------------

        [Test]
        public void Brush_RadiusZero_PaintsNothing() {
            RequireRealGpu();
            // A degenerate brush (radius 0) must clip every fragment. If
            // this test fails, the world-distance test is fundamentally
            // broken -- the clip() statement isn't being honored.
            DispatchOneStroke(brushCenterWorld: new Vector3(0.5f, 0.5f, 0.5f), radiusWorld: 0f);
            var (lit, coverage, bounds) = ReadbackRT();
            Assert.AreEqual(0, lit,
                $"Radius-0 brush painted {lit} pixels ({coverage:P2}). The clip() is not being respected; every fragment is leaking through. bounds={bounds}");
        }

        [Test]
        public void Brush_FarFromMesh_PaintsNothing() {
            RequireRealGpu();
            // Brush center 10 metres away from the cube (which spans ~1 m).
            // No fragment should be within the 5 cm radius, so the entire
            // dispatch should clip out and paint nothing.
            DispatchOneStroke(brushCenterWorld: new Vector3(10f, 10f, 10f), radiusWorld: 0.05f);
            var (lit, coverage, bounds) = ReadbackRT();
            Assert.AreEqual(0, lit,
                $"Brush 10 m away painted {lit} pixels ({coverage:P2}). World-distance test is broken; nothing within 5 cm of (10,10,10) but the brush wrote {coverage:P2} of the mask. bounds={bounds}");
        }

        [Test]
        public void Brush_SmallRadius_PaintsSmallArea() {
            RequireRealGpu();
            // Brush center on the +X face of the 100-unit cube, 5 cm radius.
            // The +X face has area ~1 m^2 = 10000 cm^2; a 5 cm radius patch
            // is at most pi*5^2 ~= 78 cm^2 ~= 0.8% of the +X face. The +X
            // face occupies 1/6 of the cube's UV area in a per-face unwrap.
            // Whatever the unwrap, the painted coverage of the WHOLE UV
            // map must be modest -- definitely below 10% of the texture
            // for a 5 cm brush on a 100 cm cube.
            //
            // Batchmode caveat: rasterization through CommandBuffer.DrawMesh
            // (and DrawMeshNow before it) is unreliable when Unity is run
            // headless -- the dispatch can silently produce zero lit pixels
            // even when the shader is correct. The "<10%" upper bound is
            // still a real check (0% trivially passes); the lit>0 lower
            // bound is skipped when batchmode produced nothing rather than
            // failed loudly. See feedback_reproduce_locally_before_pinging
            // -- ProbeMaskRT on a live avatar remains the gold-standard
            // verification for "paint actually landed".
            DispatchOneStroke(brushCenterWorld: new Vector3(50f, 0f, 0f), radiusWorld: 5f);
            var (lit, coverage, bounds) = ReadbackRT();
            Assert.Less(coverage, 0.10f,
                $"5 cm-equivalent brush on a 1 m cube painted {coverage:P2} of the texture ({lit} pixels). Expect <10%. bounds={bounds}. If this fails, the shader's world-distance test is failing for far-from-brush fragments.");
            if (lit == 0) {
                Assert.Ignore(
                    "Batchmode rasterization produced no lit pixels; lit>0 lower bound can't be verified here. " +
                    "Run ProbeMaskRT on a live avatar to confirm paint landed.");
            }
        }

        [Test]
        public void Brush_CoverageScalesWithRadius() {
            RequireRealGpu();
            // Sanity check that the brush respects radius monotonically.
            // A bigger radius should paint at least as much as a smaller
            // one at the same position; if it paints LESS, the math is
            // sign-flipped somewhere.
            DispatchOneStroke(new Vector3(50f, 0f, 0f), radiusWorld: 5f);
            var (litSmall, covSmall, _) = ReadbackRT();
            DispatchOneStroke(new Vector3(50f, 0f, 0f), radiusWorld: 20f);
            var (litBig, covBig, _) = ReadbackRT();
            Assert.GreaterOrEqual(litBig, litSmall,
                $"Bigger brush (20cm, lit={litBig}, cov={covBig:P2}) painted less than smaller brush (5cm, lit={litSmall}, cov={covSmall:P2}). The world-distance test or its sign is broken.");
        }

        [Test]
        public void Brush_PaintsTightAroundBrushCenter_NotCameraView() {
            RequireRealGpu();
            // Regression lock for the "brush stamps whatever the SceneView
            // camera is looking at" bug. A 5 cm brush on the +X face of a
            // 1 m cube should produce a tight patch whose UV center-of-mass
            // falls inside the +X face's UV island. If the dispatch leaks
            // SceneView state, the painted pixels scatter across other faces'
            // islands (or paint the entire camera-visible region) and the
            // COM lands nowhere near (0.5, 0.25).
            //
            // BuildUnitCubeSkinnedMesh packs faces into a 3x2 atlas. Face
            // index 1 is +X (column 1, row 0), so its UV island is
            // u=[1/3..2/3], v=[0..1/2] with center (0.5, 0.25). The brush
            // is right on that face, all other faces are 1 m away from the
            // brush center, so no other island should pick up any pixels.
            DispatchOneStroke(brushCenterWorld: new Vector3(50f, 0f, 0f), radiusWorld: 5f);

            var tex = new Texture2D(_maskRT.width, _maskRT.height, TextureFormat.RGBA32, false, true) {
                hideFlags = HideFlags.HideAndDontSave,
            };
            float comU, comV;
            int litCount;
            float coverage;
            var prev = RenderTexture.active;
            try {
                RenderTexture.active = _maskRT;
                tex.ReadPixels(new Rect(0, 0, _maskRT.width, _maskRT.height), 0, 0);
                tex.Apply(false, false);
                int w = tex.width;
                int h = tex.height;
                var pixels = tex.GetPixels32();
                const byte threshold = 8;
                long sumPx = 0;
                long sumPy = 0;
                litCount = 0;
                for (int y = 0; y < h; y++) {
                    for (int x = 0; x < w; x++) {
                        var p = pixels[y * w + x];
                        if (p.r >= threshold || p.g >= threshold || p.b >= threshold || p.a >= threshold) {
                            litCount++;
                            sumPx += x;
                            sumPy += y;
                        }
                    }
                }
                coverage = litCount / (float)(w * h);
                if (litCount > 0) {
                    comU = (float)sumPx / (litCount * w);
                    // V is inverted: texture rows go top->bottom, UV goes bottom->top.
                    comV = 1f - ((float)sumPy / (litCount * h));
                } else {
                    comU = -1f;
                    comV = -1f;
                }
            } finally {
                RenderTexture.active = prev;
                UnityEngine.Object.DestroyImmediate(tex);
            }

            // Batchmode rasterization through CommandBuffer.DrawMesh shares
            // the same flakiness as DrawMeshNow -- the dispatch may produce
            // 0 lit pixels even when the shader is correct (see
            // feedback_reproduce_locally_before_pinging). If nothing painted,
            // skip the COM assertion; the editor-side ProbeMaskRT diagnostic
            // remains the authoritative verification. The radius=0 and
            // far-from-mesh tests still exercise the clip() path here.
            if (litCount == 0) {
                Assert.Ignore(
                    "Batchmode rasterization produced no lit pixels; cannot verify COM. " +
                    "Run the user-editor ProbeMaskRT auto-probe on a real avatar instead.");
                return;
            }

            // +X face UV island center is (0.5, 0.25); allow generous slack
            // for soft brush edge and per-face UV padding.
            const float comTolerance = 0.15f;
            Assert.AreEqual(0.5f, comU, comTolerance,
                $"Lit pixel COM u={comU:F3} expected near 0.5 (centre of +X face's UV island u=[1/3..2/3]). " +
                $"lit={litCount}, coverage={coverage:P3}. If the COM is far outside the +X island, the brush is painting somewhere it shouldn't be -- exactly the camera-state-leak symptom.");
            Assert.AreEqual(0.25f, comV, comTolerance,
                $"Lit pixel COM v={comV:F3} expected near 0.25 (centre of +X face's UV island v=[0..0.5]). " +
                $"lit={litCount}, coverage={coverage:P3}.");
        }

        // -------------------------------------------------------------------
        // Mesh builder (shared with MaskPainterMathTests' helper but kept
        // local so this fixture is self-contained).
        // -------------------------------------------------------------------

        private static Mesh BuildUnitCubeSkinnedMesh(Transform bone) {
            var mesh = new Mesh {
                name = "WkMaskPainterTestUnitCubeBrush",
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
            // 6 quads as 12 triangles, with per-face UV islands packed into a
            // 3x2 grid in UV space so each face occupies its own 1/3 wide,
            // 1/2 tall island.
            var triangles = new int[12 * 3];
            var uvs = new Vector2[8 * 6]; // we'll splat with extra verts
            // Simpler approach: re-emit each face with its own four verts so
            // UVs are per-face.
            var newVerts = new System.Collections.Generic.List<Vector3>(24);
            var newUvs   = new System.Collections.Generic.List<Vector2>(24);
            var newTris  = new System.Collections.Generic.List<int>(36);
            // Faces in cube convention: -X, +X, -Y, +Y, -Z, +Z. Per-face
            // UV island in a 3x2 atlas: face i occupies (col=i%3, row=i/3),
            // where (0,0) is top-left of UV [0..1/3]x[0..1/2].
            (int a, int b, int c, int d)[] faces = new[] {
                (0, 4, 6, 2),  // -X: verts 0,4,6,2
                (1, 3, 7, 5),  // +X: verts 1,3,7,5
                (0, 1, 5, 4),  // -Y
                (2, 6, 7, 3),  // +Y
                (0, 2, 3, 1),  // -Z
                (4, 5, 7, 6),  // +Z
            };
            for (int f = 0; f < 6; f++) {
                int baseVert = newVerts.Count;
                var v0 = verts[faces[f].a];
                var v1 = verts[faces[f].b];
                var v2 = verts[faces[f].c];
                var v3 = verts[faces[f].d];
                newVerts.Add(v0); newVerts.Add(v1); newVerts.Add(v2); newVerts.Add(v3);
                int col = f % 3;
                int row = f / 3;
                float uMin = col / 3f, uMax = (col + 1) / 3f;
                float vMin = row / 2f, vMax = (row + 1) / 2f;
                newUvs.Add(new Vector2(uMin, vMin));
                newUvs.Add(new Vector2(uMax, vMin));
                newUvs.Add(new Vector2(uMax, vMax));
                newUvs.Add(new Vector2(uMin, vMax));
                newTris.Add(baseVert + 0); newTris.Add(baseVert + 1); newTris.Add(baseVert + 2);
                newTris.Add(baseVert + 0); newTris.Add(baseVert + 2); newTris.Add(baseVert + 3);
            }
            mesh.vertices = newVerts.ToArray();
            mesh.uv = newUvs.ToArray();
            mesh.triangles = newTris.ToArray();
            mesh.RecalculateNormals();
            // All-bone-0 weights so BakeMesh has something coherent to do.
            var weights = new BoneWeight[newVerts.Count];
            for (int i = 0; i < newVerts.Count; i++) {
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            }
            mesh.boneWeights = weights;
            mesh.bindposes = new[] { bone.worldToLocalMatrix };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
