// MaskPainterCpuSimulationTests.cs
//
// CPU simulator of the brush shader's rasterization + worldPos
// interpolation + distance test. The actual shader runs on the GPU and
// can't be exercised in batchmode reliably; this test reproduces the
// same math on the CPU so we have a ground-truth to compare against.
//
// Reproducing the bug from the user's editor: a 5 cm brush on a 100x
// scaled cube SMR. If the CPU sim shows a tight patch but the GPU shows
// "paints whatever the camera sees", the shader's worldPos pipeline is
// the bug, not the C# / math side.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class MaskPainterCpuSimulationTests {

        private const int MaskSize = 256;

        // Simulates: for each triangle, rasterise its UV layout into the
        // mask, computing world position at each fragment via barycentric
        // interpolation, then keep fragments inside the brush radius.
        // Mirrors what the shader is *supposed* to do.
        private static int CpuSimulateBrush(
                Mesh mesh,
                Vector3[] worldVerts,
                Vector3 brushCenter,
                float brushRadius,
                int maskSize,
                out int totalTrianglesProcessed,
                out float closestFragmentDistance) {
            int lit = 0;
            totalTrianglesProcessed = 0;
            closestFragmentDistance = float.PositiveInfinity;
            var uvs = mesh.uv;
            for (int sub = 0; sub < mesh.subMeshCount; sub++) {
                var tris = mesh.GetTriangles(sub);
                for (int t = 0; t + 2 < tris.Length; t += 3) {
                    int i0 = tris[t], i1 = tris[t + 1], i2 = tris[t + 2];
                    if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                    totalTrianglesProcessed++;
                    var uv0 = uvs[i0]; var uv1 = uvs[i1]; var uv2 = uvs[i2];
                    var w0  = worldVerts[i0]; var w1 = worldVerts[i1]; var w2 = worldVerts[i2];
                    // UV-space bounding box (in pixel coords) for the triangle.
                    float uMin = Mathf.Min(uv0.x, uv1.x, uv2.x);
                    float uMax = Mathf.Max(uv0.x, uv1.x, uv2.x);
                    float vMin = Mathf.Min(uv0.y, uv1.y, uv2.y);
                    float vMax = Mathf.Max(uv0.y, uv1.y, uv2.y);
                    int xMin = Mathf.Max(0, Mathf.FloorToInt(uMin * maskSize));
                    int xMax = Mathf.Min(maskSize - 1, Mathf.CeilToInt(uMax * maskSize));
                    int yMin = Mathf.Max(0, Mathf.FloorToInt(vMin * maskSize));
                    int yMax = Mathf.Min(maskSize - 1, Mathf.CeilToInt(vMax * maskSize));
                    // Barycentric denominator (cross product of edges in UV).
                    float d00 = uv1.x - uv0.x, d01 = uv1.y - uv0.y;
                    float d10 = uv2.x - uv0.x, d11 = uv2.y - uv0.y;
                    float denom = d00 * d11 - d01 * d10;
                    if (Mathf.Abs(denom) < 1e-12f) continue;
                    float invDenom = 1f / denom;
                    for (int py = yMin; py <= yMax; py++) {
                        for (int px = xMin; px <= xMax; px++) {
                            float pu = (px + 0.5f) / maskSize;
                            float pv = (py + 0.5f) / maskSize;
                            // Barycentric for the (pu, pv) point.
                            float dx = pu - uv0.x;
                            float dy = pv - uv0.y;
                            float b1 = (dx * d11 - dy * d10) * invDenom;
                            float b2 = (dy * d00 - dx * d01) * invDenom;
                            float b0 = 1f - b1 - b2;
                            if (b0 < 0f || b1 < 0f || b2 < 0f) continue;
                            // Interpolated world position.
                            var worldP = b0 * w0 + b1 * w1 + b2 * w2;
                            float dist = Vector3.Distance(worldP, brushCenter);
                            if (dist < closestFragmentDistance) closestFragmentDistance = dist;
                            if (dist <= brushRadius) lit++;
                        }
                    }
                }
            }
            return lit;
        }

        private static (GameObject rig, SkinnedMeshRenderer smr, Mesh source, Mesh snapshot, Vector3[] worldVerts)
                BuildUmeLikeRig() {
            var rig = new GameObject("CpuSimRig");
            rig.transform.position = Vector3.zero;
            rig.transform.rotation = Quaternion.identity;
            rig.transform.localScale = Vector3.one;

            var boneGo = new GameObject("Bone");
            boneGo.transform.parent = rig.transform;
            boneGo.transform.localPosition = Vector3.zero;
            boneGo.transform.localRotation = Quaternion.identity;
            boneGo.transform.localScale = Vector3.one;

            var smr = rig.AddComponent<SkinnedMeshRenderer>();
            var sourceMesh = BuildPerFaceUvCube(boneGo.transform);
            smr.sharedMesh = sourceMesh;
            smr.bones = new[] { boneGo.transform };
            smr.rootBone = boneGo.transform;

            // Scale rig 100x after bind, like a typical VRChat avatar.
            rig.transform.localScale = Vector3.one * 100f;

            // Mirror the painter's Bake() flow exactly.
            var snapshot = new Mesh { name = "CpuSimSnapshot", hideFlags = HideFlags.HideAndDontSave };
            smr.BakeMesh(snapshot, useScale: true);
            snapshot.uv = sourceMesh.uv;
            snapshot.normals = sourceMesh.normals;

            var verts = snapshot.vertices;
            var matrix = smr.transform.localToWorldMatrix;
            var worldVerts = new Vector3[verts.Length];
            for (int i = 0; i < verts.Length; i++) {
                worldVerts[i] = matrix.MultiplyPoint3x4(verts[i]);
            }
            return (rig, smr, sourceMesh, snapshot, worldVerts);
        }

        [Test]
        public void CpuSim_5cmBrush_OnPlusXFace_PaintsSmallArea() {
            var (rig, _, source, snapshot, worldVerts) = BuildUmeLikeRig();
            try {
                // Cube spans -50..+50 on each axis. The +X face is at x=50.
                // Brush center on the +X face surface.
                var brushCenter = new Vector3(50f, 0f, 0f);
                float radius = 5f; // 5 cm at avatar scale = 5 cm world
                int lit = CpuSimulateBrush(snapshot, worldVerts, brushCenter, radius, MaskSize, out int tris, out float closest);
                float coverage = lit / (float)(MaskSize * MaskSize);
                TestContext.Out.WriteLine($"CpuSim 5cm-on-+X: lit={lit}, coverage={coverage:P3}, tris={tris}, closestDist={closest:F4}m");
                Assert.Greater(lit, 0, "Brush right on the +X face should paint at least one fragment in the CPU sim.");
                Assert.Less(coverage, 0.10f, $"5cm-equivalent brush should paint <10% of the texture. Got {coverage:P2}, closest fragment was {closest:F4}m from brush center.");
            } finally {
                if (snapshot != null) Object.DestroyImmediate(snapshot);
                if (source != null) Object.DestroyImmediate(source);
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void CpuSim_FarBrush_PaintsZero() {
            var (rig, _, source, snapshot, worldVerts) = BuildUmeLikeRig();
            try {
                var brushCenter = new Vector3(1000f, 1000f, 1000f);
                int lit = CpuSimulateBrush(snapshot, worldVerts, brushCenter, 5f, MaskSize, out _, out float closest);
                TestContext.Out.WriteLine($"CpuSim far brush: lit={lit}, closestDist={closest:F2}m");
                Assert.AreEqual(0, lit, $"Brush 1km from mesh painted {lit} fragments; closest was {closest:F2}m.");
            } finally {
                if (snapshot != null) Object.DestroyImmediate(snapshot);
                if (source != null) Object.DestroyImmediate(source);
                Object.DestroyImmediate(rig);
            }
        }

        [Test]
        public void CpuSim_TinyBrush_PaintsAtMostAFewPixels() {
            // 1 mm brush -- matches what the user has been clicking with.
            // Even on the surface, this should paint at most a handful
            // of pixels in the CPU sim. The user's GPU paints 17-52% of
            // the texture; if the CPU sim shows the same coverage, the
            // bug is in our math, not the shader.
            var (rig, _, source, snapshot, worldVerts) = BuildUmeLikeRig();
            try {
                var brushCenter = new Vector3(50f, 0f, 0f);
                int lit = CpuSimulateBrush(snapshot, worldVerts, brushCenter, 0.1f, MaskSize, out int tris, out float closest);
                float coverage = lit / (float)(MaskSize * MaskSize);
                TestContext.Out.WriteLine($"CpuSim 1mm-on-+X: lit={lit}, coverage={coverage:P4}, tris={tris}, closestDist={closest:F4}m");
                Assert.Less(coverage, 0.01f, $"1mm-equivalent brush should paint <1% of the texture. Got {coverage:P3} -- if higher, the C# math is wrong (not the GPU).");
            } finally {
                if (snapshot != null) Object.DestroyImmediate(snapshot);
                if (source != null) Object.DestroyImmediate(source);
                Object.DestroyImmediate(rig);
            }
        }

        // -------------------------------------------------------------------
        // Per-face-UV unit cube. Cleanly separated UV islands per face so
        // a brush on one face doesn't accidentally hit another's UV region.
        // -------------------------------------------------------------------

        private static Mesh BuildPerFaceUvCube(Transform bone) {
            var mesh = new Mesh {
                name = "WkMaskPainterTestPerFaceCube",
                hideFlags = HideFlags.HideAndDontSave,
            };
            var corners = new Vector3[8];
            int idx = 0;
            for (int z = 0; z < 2; z++) {
                for (int y = 0; y < 2; y++) {
                    for (int x = 0; x < 2; x++) {
                        corners[idx++] = new Vector3(x - 0.5f, y - 0.5f, z - 0.5f);
                    }
                }
            }
            var faces = new (int a, int b, int c, int d)[] {
                (0, 4, 6, 2),  // -X
                (1, 3, 7, 5),  // +X
                (0, 1, 5, 4),  // -Y
                (2, 6, 7, 3),  // +Y
                (0, 2, 3, 1),  // -Z
                (4, 5, 7, 6),  // +Z
            };
            var newVerts = new System.Collections.Generic.List<Vector3>(24);
            var newUvs   = new System.Collections.Generic.List<Vector2>(24);
            var newTris  = new System.Collections.Generic.List<int>(36);
            for (int f = 0; f < 6; f++) {
                int baseVert = newVerts.Count;
                newVerts.Add(corners[faces[f].a]);
                newVerts.Add(corners[faces[f].b]);
                newVerts.Add(corners[faces[f].c]);
                newVerts.Add(corners[faces[f].d]);
                int col = f % 3, row = f / 3;
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
