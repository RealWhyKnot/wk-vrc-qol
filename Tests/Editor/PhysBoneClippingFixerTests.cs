// PhysBoneClippingFixerTests.cs

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.PhysBoneClipping;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class PhysBoneClippingFixerTests {

        private readonly List<Object> _created = new List<Object>();

        [TearDown]
        public void TearDown() {
            for (int i = _created.Count - 1; i >= 0; i--) {
                if (_created[i] != null) Object.DestroyImmediate(_created[i]);
            }
            _created.Clear();
        }

        [Test]
        public void Scan_FlagsTargetVertexInsideComparisonMesh() {
            var body = CreateRenderer("Body", CreateCube("BodyMesh"));
            var target = CreateRenderer("Moving", CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(1.0f, 0f, 0f),
                new Vector3(0f, 1.0f, 0f)));

            var issues = PhysBoneClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));

            Assert.IsTrue(issues.Any(i =>
                i.Kind == PhysBoneClippingFixer.IssueKind.ComparisonMesh &&
                i.VertexIndex == 0 &&
                i.ComparisonRenderer == body));
        }

        [Test]
        public void Scan_FlagsSurfaceIntersectionAgainstComparisonMesh() {
            var body = CreateRenderer("Body", CreateCube("BodyMesh"));
            var target = CreateRenderer("Moving", CreateTriangle("MovingMesh",
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f)));

            var issues = PhysBoneClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));

            Assert.IsTrue(issues.Any(i =>
                i.Kind == PhysBoneClippingFixer.IssueKind.SurfaceIntersection &&
                i.ComparisonRenderer == body));
        }

        [Test]
        public void Scan_FlagsSelfIntersectingTriangles() {
            var target = CreateRenderer("Moving", CreateSelfIntersectingMesh("SelfClip"));

            var issues = PhysBoneClippingFixer.Scan(target, new SkinnedMeshRenderer[0], Settings(checkSelf: true));

            Assert.IsTrue(issues.Any(i => i.Kind == PhysBoneClippingFixer.IssueKind.SelfIntersection));
        }

        [Test]
        public void ApplyToCurrentMeshInPlace_MovesClippingVertex() {
            var body = CreateRenderer("Body", CreateCube("BodyMesh"));
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(1.0f, 0f, 0f),
                new Vector3(0f, 1.0f, 0f));
            var target = CreateRenderer("Moving", mesh);
            var before = mesh.vertices[0];

            var result = PhysBoneClippingFixer.ApplyToCurrentMeshInPlace(
                target,
                new[] { body },
                Settings(checkSelf: false),
                useUndo: false);

            Assert.Greater(result.VerticesMoved, 0);
            Assert.AreNotEqual(before, mesh.vertices[0]);
        }

        private PhysBoneClippingFixer.Settings Settings(bool checkSelf) {
            return new PhysBoneClippingFixer.Settings {
                CheckSelf = checkSelf,
                InsideTolerance = 0.0001f,
                SurfacePadding = 0.01f,
                MaxFixPasses = 1,
                MaxWarnings = 0,
            };
        }

        private SkinnedMeshRenderer CreateRenderer(string name, Mesh mesh) {
            var go = new GameObject(name);
            _created.Add(go);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            _created.Add(mesh);
            return renderer;
        }

        private static Mesh CreateTriangle(string name, Vector3 a, Vector3 b, Vector3 c) {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] { a, b, c };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateSelfIntersectingMesh(string name) {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] {
                new Vector3(-1f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
                new Vector3(0f, -0.5f, -1f),
                new Vector3(0f, -0.5f, 1f),
                new Vector3(0f, 1.2f, 0f),
            };
            mesh.triangles = new[] { 0, 1, 2, 3, 4, 5 };
            mesh.RecalculateBounds();
            return mesh;
        }

        private static Mesh CreateCube(string name) {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] {
                new Vector3(-0.5f, -0.5f, -0.5f),
                new Vector3( 0.5f, -0.5f, -0.5f),
                new Vector3( 0.5f,  0.5f, -0.5f),
                new Vector3(-0.5f,  0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f,  0.5f),
                new Vector3( 0.5f, -0.5f,  0.5f),
                new Vector3( 0.5f,  0.5f,  0.5f),
                new Vector3(-0.5f,  0.5f,  0.5f),
            };
            mesh.triangles = new[] {
                0, 2, 1, 0, 3, 2,
                4, 5, 6, 4, 6, 7,
                0, 7, 3, 0, 4, 7,
                1, 2, 6, 1, 6, 5,
                0, 1, 5, 0, 5, 4,
                3, 6, 2, 3, 7, 6,
            };
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
