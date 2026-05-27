// MeshSculptCoreTests.cs
//
// Pure mesh/math coverage for the Mesh Sculpt MVP. SceneView event
// routing is manual-verified; the data operations here are deterministic
// and should stay covered by EditMode tests.

using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class MeshSculptCoreTests {

        [Test]
        public void BrushWeight_ZeroAtRadiusAndFullAtCenter() {
            Assert.AreEqual(1f, MeshSculptCore.BrushWeight(0f, 1f), 1e-5f);
            Assert.AreEqual(0f, MeshSculptCore.BrushWeight(1f, 1f), 1e-5f);
            Assert.AreEqual(0f, MeshSculptCore.BrushWeight(2f, 1f), 1e-5f);
            Assert.Greater(MeshSculptCore.BrushWeight(0.25f, 1f), MeshSculptCore.BrushWeight(0.75f, 1f));
        }

        [Test]
        public void BuildAdjacency_QuadLinksTriangleNeighbors() {
            var mesh = BuildQuadMesh();
            try {
                var adjacency = MeshSculptCore.BuildAdjacency(mesh);

                CollectionAssert.AreEquivalent(new[] { 1, 2, 3 }, adjacency.NeighborsOf(0));
                CollectionAssert.AreEquivalent(new[] { 0, 2 }, adjacency.NeighborsOf(1));
                CollectionAssert.AreEquivalent(new[] { 0, 1, 3 }, adjacency.NeighborsOf(2));
                CollectionAssert.AreEquivalent(new[] { 0, 2 }, adjacency.NeighborsOf(3));
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void ApplyGrab_MovesOnlyIndexedVerticesWithWeights() {
            var verts = new[] {
                Vector3.zero,
                Vector3.right,
                Vector3.up,
            };

            MeshSculptCore.ApplyGrab(
                verts,
                new[] { 0, 2 },
                new Vector3(2f, 0f, 0f),
                new[] { 0.5f, 1f });

            Assert.AreEqual(new Vector3(1f, 0f, 0f), verts[0]);
            Assert.AreEqual(Vector3.right, verts[1]);
            Assert.AreEqual(new Vector3(2f, 1f, 0f), verts[2]);
        }

        [Test]
        public void ApplySmooth_MovesSelectedVertexTowardNeighborAverage() {
            var mesh = BuildQuadMesh();
            try {
                var adjacency = MeshSculptCore.BuildAdjacency(mesh);
                var verts = mesh.vertices;

                MeshSculptCore.ApplySmooth(verts, new[] { 1 }, adjacency, 1f);

                // Vertex 1 is connected to vertices 0 and 2.
                Assert.AreEqual(new Vector3(0.5f, 0.5f, 0f), verts[1]);
                Assert.AreEqual(Vector3.zero, verts[0]);
                Assert.AreEqual(new Vector3(1f, 1f, 0f), verts[2]);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TryRaycast_HitsNearestTriangleAndReportsSubmesh() {
            var mesh = BuildTwoSubmeshQuad();
            try {
                var world = mesh.vertices;
                var ray = new Ray(new Vector3(0.2f, 0.2f, -1f), Vector3.forward);

                bool hit = MeshSculptCore.TryRaycast(mesh, world, ray, out var result);

                Assert.IsTrue(hit);
                Assert.AreEqual(0, result.Submesh);
                Assert.AreEqual(new Vector3(0.2f, 0.2f, 0f), result.WorldPosition);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TryAppendFace_TriangleAppendsOnlyChosenSubmesh() {
            var mesh = BuildTwoSubmeshQuad();
            try {
                var beforeSubmesh0 = mesh.GetTriangles(0);
                bool ok = MeshSculptCore.TryAppendFace(mesh, new[] { 0, 2, 3 }, 1, flip: false, out string error);

                Assert.IsTrue(ok, error);
                CollectionAssert.AreEqual(beforeSubmesh0, mesh.GetTriangles(0), "Filling submesh 1 must not touch submesh 0.");
                CollectionAssert.AreEqual(new[] { 0, 2, 3 }, mesh.GetTriangles(1));
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TryAppendFace_FlipReversesTriangleWinding() {
            var mesh = BuildTwoSubmeshQuad();
            try {
                bool ok = MeshSculptCore.TryAppendFace(mesh, new[] { 0, 2, 3 }, 1, flip: true, out string error);

                Assert.IsTrue(ok, error);
                CollectionAssert.AreEqual(new[] { 0, 3, 2 }, mesh.GetTriangles(1));
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TryAppendFace_QuadCreatesTwoTriangles() {
            var mesh = BuildEmptySubmeshQuad();
            try {
                bool ok = MeshSculptCore.TryAppendFace(mesh, new[] { 0, 1, 2, 3 }, 0, flip: false, out string error);

                Assert.IsTrue(ok, error);
                Assert.AreEqual(6, mesh.GetTriangles(0).Length);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void TryAppendFace_DuplicateVertexIsRejected() {
            var mesh = BuildEmptySubmeshQuad();
            try {
                bool ok = MeshSculptCore.TryAppendFace(mesh, new[] { 0, 1, 1 }, 0, flip: false, out string error);

                Assert.IsFalse(ok);
                StringAssert.Contains("unique", error);
                Assert.AreEqual(0, mesh.GetTriangles(0).Length);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        private static Mesh BuildQuadMesh() {
            var mesh = new Mesh { name = "MeshSculptTestQuad" };
            mesh.vertices = new[] {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
            return mesh;
        }

        private static Mesh BuildTwoSubmeshQuad() {
            var mesh = new Mesh { name = "MeshSculptTwoSubmeshQuad" };
            mesh.vertices = new[] {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(System.Array.Empty<int>(), 1);
            return mesh;
        }

        private static Mesh BuildEmptySubmeshQuad() {
            var mesh = new Mesh { name = "MeshSculptEmptySubmeshQuad" };
            mesh.vertices = new[] {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 1f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.subMeshCount = 1;
            mesh.SetTriangles(System.Array.Empty<int>(), 0);
            return mesh;
        }
    }
}
