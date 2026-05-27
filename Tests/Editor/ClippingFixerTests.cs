// ClippingFixerTests.cs

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Clipping;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tests {

    public sealed class ClippingFixerTests {

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

            var issues = ClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));

            Assert.IsTrue(issues.Any(i =>
                i.Kind == ClippingFixer.IssueKind.ComparisonMesh &&
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

            var issues = ClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));

            Assert.IsTrue(issues.Any(i =>
                i.Kind == ClippingFixer.IssueKind.SurfaceIntersection &&
                i.ComparisonRenderer == body));
        }

        [Test]
        public void Scan_FlagsSelfIntersectingTriangles() {
            var target = CreateRenderer("Moving", CreateSelfIntersectingMesh("SelfClip"));

            var issues = ClippingFixer.Scan(target, new SkinnedMeshRenderer[0], Settings(checkSelf: true));

            Assert.IsTrue(issues.Any(i => i.Kind == ClippingFixer.IssueKind.SelfIntersection));
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

            var result = ClippingFixer.ApplyToCurrentMeshInPlace(
                target,
                new[] { body },
                Settings(checkSelf: false),
                useUndo: false);

            Assert.Greater(result.VerticesMoved, 0);
            Assert.AreNotEqual(before, mesh.vertices[0]);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_MovesOnlySelectedWarning() {
            var body = CreateRenderer("Body", CreateCube("BodyMesh"));
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateRenderer("Moving", mesh);
            var before = mesh.vertices;

            var warnings = ClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));
            var selected = warnings
                .Where(i => i.Kind == ClippingFixer.IssueKind.ComparisonMesh && i.VertexIndex == 0)
                .Take(1)
                .ToList();
            Assert.AreEqual(1, selected.Count);

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            var after = mesh.vertices;
            Assert.AreEqual(1, result.VerticesMoved);
            Assert.AreNotEqual(before[0], after[0]);
            Assert.AreEqual(before[1], after[1]);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_DoesNotMovePhysBoneMotionWarning() {
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateRenderer("Moving", mesh);
            var before = mesh.vertices;
            var selected = new[] {
                new ClippingFixer.Issue {
                    Kind = ClippingFixer.IssueKind.PhysBoneMotion,
                    Renderer = target,
                    VertexIndex = 0,
                    AffectedVertexIndices = new[] { 0 },
                    PushWorld = new Vector3(10f, 10f, 10f),
                },
            };

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            Assert.AreEqual(0, result.VerticesMoved);
            CollectionAssert.AreEqual(before, mesh.vertices);
        }

#if VRC_SDK_VRCSDK3
        [Test]
        public void Scan_IncludesPhysBoneMotionWarning() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var animator = avatar.AddComponent<Animator>();

            var physRoot = CreateChild("PhysRoot", avatar.transform, Vector3.zero);
            var physBoneTransform = CreateChild("PhysBone", physRoot.transform, new Vector3(0f, 0.05f, 0f));
            var physBone = physRoot.AddComponent<VRCPhysBone>();
            physBone.rootTransform = physRoot.transform;
            physBone.pull = 0f;
            physBone.stiffness = 0f;
            physBone.spring = 1f;
            physBone.radius = 0.02f;
            physBone.maxStretch = 0.25f;

            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero);
            var target = CreateWeightedRenderer("Moving",
                CreateTriangle("MovingMesh",
                    new Vector3(0f, 0.05f, 0f),
                    new Vector3(0.02f, 0.05f, 0f),
                    new Vector3(0f, 0.07f, 0f)),
                physBoneTransform.transform);
            var body = CreateWeightedRenderer("Body",
                CreateTriangle("BodyMesh",
                    new Vector3(0f, 0.056f, 0f),
                    new Vector3(0.02f, 0.056f, 0f),
                    new Vector3(0f, 0.076f, 0f)),
                bodyBone.transform);

            var settings = Settings(checkSelf: false);
            settings.Animator = animator;
            settings.IncludePhysBoneMotion = true;
            settings.PhysBoneClearanceMargin = 0.01f;
            settings.MaxIssuesPerPhysBone = 4;

            var issues = ClippingFixer.Scan(target, new[] { body }, settings);

            Assert.IsTrue(issues.Any(i =>
                i.Kind == ClippingFixer.IssueKind.PhysBoneMotion &&
                i.DrivenBone == physBoneTransform.transform &&
                i.ComparisonRenderer == body));
        }

        [Test]
        public void ApplyToCurrentMeshInPlace_ReducesPhysBoneMotionWithoutMovingMesh() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var animator = avatar.AddComponent<Animator>();

            var physRoot = CreateChild("PhysRoot", avatar.transform, Vector3.zero);
            var physBoneTransform = CreateChild("PhysBone", physRoot.transform, new Vector3(0f, 0.05f, 0f));
            var physBone = physRoot.AddComponent<VRCPhysBone>();
            physBone.rootTransform = physRoot.transform;
            physBone.pull = 0f;
            physBone.stiffness = 0f;
            physBone.spring = 1f;
            physBone.radius = 0.02f;
            physBone.maxStretch = 0.25f;

            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero);
            var targetMesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0.05f, 0f),
                new Vector3(0.02f, 0.05f, 0f),
                new Vector3(0f, 0.07f, 0f));
            var target = CreateWeightedRenderer("Moving", targetMesh, physBoneTransform.transform);
            var body = CreateWeightedRenderer("Body",
                CreateTriangle("BodyMesh",
                    new Vector3(0f, 0.056f, 0f),
                    new Vector3(0.02f, 0.056f, 0f),
                    new Vector3(0f, 0.076f, 0f)),
                bodyBone.transform);

            var beforeVerts = targetMesh.vertices;
            float beforePull = physBone.pull;
            var settings = Settings(checkSelf: false);
            settings.Animator = animator;
            settings.IncludePhysBoneMotion = true;
            settings.PhysBoneClearanceMargin = 0.01f;
            settings.MaxIssuesPerPhysBone = 4;

            var result = ClippingFixer.ApplyToCurrentMeshInPlace(target, new[] { body }, settings, useUndo: false);

            Assert.AreEqual(0, result.VerticesMoved);
            Assert.Greater(result.PhysBoneSourcesAdjusted, 0);
            Assert.Greater(physBone.pull, beforePull);
            CollectionAssert.AreEqual(beforeVerts, targetMesh.vertices);
        }
#endif

        private ClippingFixer.Settings Settings(bool checkSelf) {
            return new ClippingFixer.Settings {
                CheckSelf = checkSelf,
                IncludePhysBoneMotion = false,
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

        private SkinnedMeshRenderer CreateWeightedRenderer(string name, Mesh mesh, Transform bone) {
            var renderer = CreateRenderer(name, mesh);
            renderer.rootBone = bone;
            renderer.bones = new[] { bone };
            mesh.bindposes = new[] { bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix };
            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++) {
                weights[i] = new BoneWeight {
                    boneIndex0 = 0,
                    weight0 = 1f,
                };
            }
            mesh.boneWeights = weights;
            return renderer;
        }

        private GameObject CreateChild(string name, Transform parent, Vector3 localPosition) {
            var go = new GameObject(name);
            _created.Add(go);
            go.transform.SetParent(parent, false);
            go.transform.localPosition = localPosition;
            return go;
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
