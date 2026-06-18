// ClippingFixerTests.cs

using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEngine;
using WhyKnot.AvatarQol.Clipping;
using WhyKnot.AvatarQol.Intent;

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
        public void ApplyToCurrentMeshInPlace_ReweightsClippingVertex() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var movingBone = CreateChild("MovingBone", avatar.transform, Vector3.zero).transform;
            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero).transform;
            var bones = new[] { movingBone, bodyBone };
            var body = CreateWeightedRenderer("Body", CreateCube("BodyMesh"), bones, 1);
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(1.0f, 0f, 0f),
                new Vector3(0f, 1.0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var before = mesh.vertices;

            var result = ClippingFixer.ApplyToCurrentMeshInPlace(
                target,
                new[] { body },
                Settings(checkSelf: false),
                useUndo: false);

            Assert.Greater(result.VerticesReweighted, 0);
            CollectionAssert.AreEqual(before, mesh.vertices);
            Assert.Greater(GetWeight(mesh, 0, 1), 0.99f);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_ReweightsOnlySelectedWarning() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var movingBone = CreateChild("MovingBone", avatar.transform, Vector3.zero).transform;
            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero).transform;
            var bones = new[] { movingBone, bodyBone };
            var body = CreateWeightedRenderer("Body", CreateCube("BodyMesh"), bones, 1);
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var before = mesh.vertices;

            var warnings = ClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));
            var selected = warnings
                .Where(i => i.Kind == ClippingFixer.IssueKind.ComparisonMesh && i.VertexIndex == 0)
                .Take(1)
                .ToList();
            Assert.AreEqual(1, selected.Count);

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            var after = mesh.vertices;
            Assert.AreEqual(1, result.VerticesReweighted);
            CollectionAssert.AreEqual(before, after);
            Assert.Greater(GetWeight(mesh, 0, 1), 0.99f);
            Assert.Greater(GetWeight(mesh, 1, 0), 0.99f);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_ReweightsPhysBoneMotionWarning() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var stableBone = CreateChild("StableBone", avatar.transform, Vector3.zero).transform;
            var physRoot = CreateChild("PhysRoot", stableBone, Vector3.zero).transform;
            var movingBone = CreateChild("MovingBone", physRoot, Vector3.zero).transform;
            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero).transform;
            var bones = new[] { movingBone, stableBone, bodyBone };
            var body = CreateWeightedRenderer("Body", CreateTriangle("BodyMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f)), bones, 2);
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var before = mesh.vertices;
            var selected = new[] {
                new ClippingFixer.Issue {
                    Kind = ClippingFixer.IssueKind.PhysBoneMotion,
                    Renderer = target,
                    ComparisonRenderer = body,
                    PhysBoneRoot = physRoot,
                    DrivenBone = movingBone,
                    VertexIndex = 0,
                    ComparisonTriangleIndex = 0,
                    AffectedVertexIndices = new[] { 0 },
                    NearestSurfacePosition = Vector3.zero,
                },
            };

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            Assert.AreEqual(1, result.VerticesReweighted);
            CollectionAssert.AreEqual(before, mesh.vertices);
            Assert.That(GetWeight(mesh, 0, 1), Is.InRange(0.60f, 0.70f));
            Assert.That(GetWeight(mesh, 0, 0), Is.InRange(0.30f, 0.40f));
            Assert.AreEqual(0f, GetWeight(mesh, 0, 2), 0.0001f);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_PhysBoneMotionPaintsNearbySameChainVertices() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var stableBone = CreateChild("StableBone", avatar.transform, Vector3.zero).transform;
            var physRoot = CreateChild("PhysRoot", stableBone, Vector3.zero).transform;
            var movingBone = CreateChild("MovingBone", physRoot, Vector3.zero).transform;
            var bones = new[] { movingBone, stableBone };
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.01f, 0f, 0f),
                new Vector3(0.08f, 0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var selected = new[] {
                new ClippingFixer.Issue {
                    Kind = ClippingFixer.IssueKind.PhysBoneMotion,
                    Renderer = target,
                    PhysBoneRoot = physRoot,
                    DrivenBone = movingBone,
                    VertexIndex = 0,
                    AffectedVertexIndices = new[] { 0 },
                },
            };

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            Assert.AreEqual(2, result.VerticesReweighted);
            Assert.Greater(GetWeight(mesh, 0, 1), 0.60f);
            Assert.Greater(GetWeight(mesh, 1, 1), 0.30f);
            Assert.AreEqual(0f, GetWeight(mesh, 2, 1), 0.0001f);
        }

        [Test]
        public void ApplySelectedToCurrentMeshInPlace_PhysBoneMotionWithoutStableAncestorDoesNotCloneSurfaceWeights() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var movingBone = CreateChild("MovingBone", avatar.transform, Vector3.zero).transform;
            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero).transform;
            var bones = new[] { movingBone, bodyBone };
            var body = CreateWeightedRenderer("Body", CreateTriangle("BodyMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f)), bones, 1);
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var selected = new[] {
                new ClippingFixer.Issue {
                    Kind = ClippingFixer.IssueKind.PhysBoneMotion,
                    Renderer = target,
                    ComparisonRenderer = body,
                    PhysBoneRoot = movingBone,
                    DrivenBone = movingBone,
                    VertexIndex = 0,
                    ComparisonTriangleIndex = 0,
                    AffectedVertexIndices = new[] { 0 },
                    NearestSurfacePosition = Vector3.zero,
                },
            };

            var result = ClippingFixer.ApplySelectedToCurrentMeshInPlace(target, selected, useUndo: false);

            Assert.AreEqual(0, result.VerticesReweighted);
            Assert.Greater(GetWeight(mesh, 0, 0), 0.99f);
            Assert.AreEqual(0f, GetWeight(mesh, 0, 1), 0.0001f);
        }

        [Test]
        public void ApplyNonDestructive_UsesPrecomputedIssuesForFirstPass() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var movingBone = CreateChild("MovingBone", avatar.transform, Vector3.zero).transform;
            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero).transform;
            var bones = new[] { movingBone, bodyBone };
            var body = CreateWeightedRenderer("Body", CreateCube("BodyMesh"), bones, 1);
            var mesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0f, 0f),
                new Vector3(0.1f, 0f, 0f),
                new Vector3(1.0f, 1.0f, 0f));
            var target = CreateWeightedRenderer("Moving", mesh, bones, 0);
            var before = mesh.vertices;
            var warnings = ClippingFixer.Scan(target, new[] { body }, Settings(checkSelf: false));
            var selected = warnings
                .Where(i => i.Kind == ClippingFixer.IssueKind.ComparisonMesh && i.VertexIndex == 0)
                .Take(1)
                .ToList();
            Assert.AreEqual(1, selected.Count);

            var settings = Settings(checkSelf: false);
            using (var session = new AvatarIntentSession()) {
                var result = ClippingFixer.ApplyNonDestructive(
                    target,
                    new SkinnedMeshRenderer[0],
                    settings,
                    session,
                    selected);

                Assert.AreEqual(1, result.VerticesReweighted);
                Assert.AreNotSame(mesh, target.sharedMesh);
                CollectionAssert.AreEqual(before, target.sharedMesh.vertices);
                Assert.Greater(GetWeight(target.sharedMesh, 0, 1), 0.99f);
                CollectionAssert.AreEqual(before, mesh.vertices);
            }

            Assert.AreSame(mesh, target.sharedMesh);
        }

#if VRC_SDK_VRCSDK3
        [Test]
        public void Scan_IncludesPhysBoneMotionWarning() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var animator = avatar.AddComponent<Animator>();

            var stableBone = CreateChild("StableBone", avatar.transform, Vector3.zero);
            var physRoot = CreateChild("PhysRoot", stableBone.transform, Vector3.zero);
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
        public void ApplyToCurrentMeshInPlace_FixesPhysBoneMotionByReweightingOnly() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var animator = avatar.AddComponent<Animator>();

            var stableBone = CreateChild("StableBone", avatar.transform, Vector3.zero);
            var physRoot = CreateChild("PhysRoot", stableBone.transform, Vector3.zero);
            var physBoneTransform = CreateChild("PhysBone", physRoot.transform, new Vector3(0f, 0.05f, 0f));
            var physBone = physRoot.AddComponent<VRCPhysBone>();
            physBone.rootTransform = physRoot.transform;
            physBone.pull = 0f;
            physBone.stiffness = 0f;
            physBone.spring = 1f;
            physBone.radius = 0.02f;
            physBone.maxStretch = 0.25f;

            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero);
            var bones = new[] { physBoneTransform.transform, stableBone.transform, bodyBone.transform };
            var targetMesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0.05f, 0f),
                new Vector3(0.02f, 0.05f, 0f),
                new Vector3(0f, 0.07f, 0f));
            var target = CreateWeightedRenderer("Moving", targetMesh, bones, 0);
            var body = CreateWeightedRenderer("Body",
                CreateTriangle("BodyMesh",
                    new Vector3(0f, 0.056f, 0f),
                    new Vector3(0.02f, 0.056f, 0f),
                    new Vector3(0f, 0.076f, 0f)),
                bones,
                2);

            var beforeVerts = targetMesh.vertices;
            float beforePull = physBone.pull;
            var settings = Settings(checkSelf: false);
            settings.Animator = animator;
            settings.IncludePhysBoneMotion = true;
            settings.PhysBoneClearanceMargin = 0.01f;
            settings.PhysBoneMotionBrushRadius = 0f;
            settings.MaxIssuesPerPhysBone = 4;

            var result = ClippingFixer.ApplyToCurrentMeshInPlace(target, new[] { body }, settings, useUndo: false);

            Assert.Greater(result.VerticesReweighted, 0);
            Assert.AreEqual(beforePull, physBone.pull);
            var afterVerts = targetMesh.vertices;
            CollectionAssert.AreEqual(beforeVerts, afterVerts);
            Assert.IsTrue(Enumerable.Range(0, targetMesh.vertexCount).Any(v =>
                GetWeight(targetMesh, v, 1) > 0.40f &&
                GetWeight(targetMesh, v, 0) < 0.60f &&
                GetWeight(targetMesh, v, 2) < 0.01f));
        }

        [Test]
        public void ApplyNonDestructive_DoesNotRepeatPhysBoneScanAfterPrecomputedPass() {
            var avatar = new GameObject("Avatar");
            _created.Add(avatar);
            var animator = avatar.AddComponent<Animator>();

            var stableBone = CreateChild("StableBone", avatar.transform, Vector3.zero);
            var physRoot = CreateChild("PhysRoot", stableBone.transform, Vector3.zero);
            var physBoneTransform = CreateChild("PhysBone", physRoot.transform, new Vector3(0f, 0.05f, 0f));
            var physBone = physRoot.AddComponent<VRCPhysBone>();
            physBone.rootTransform = physRoot.transform;
            physBone.pull = 0f;
            physBone.stiffness = 0f;
            physBone.spring = 1f;
            physBone.radius = 0.02f;
            physBone.maxStretch = 0.25f;

            var bodyBone = CreateChild("BodyBone", avatar.transform, Vector3.zero);
            var bones = new[] { physBoneTransform.transform, stableBone.transform, bodyBone.transform };
            var targetMesh = CreateTriangle("MovingMesh",
                new Vector3(0f, 0.05f, 0f),
                new Vector3(0.02f, 0.05f, 0f),
                new Vector3(0f, 0.07f, 0f));
            var target = CreateWeightedRenderer("Moving", targetMesh, bones, 0);
            var body = CreateWeightedRenderer("Body",
                CreateTriangle("BodyMesh",
                    new Vector3(0f, 0.056f, 0f),
                    new Vector3(0.02f, 0.056f, 0f),
                    new Vector3(0f, 0.076f, 0f)),
                bones,
                2);

            var settings = Settings(checkSelf: false);
            settings.Animator = animator;
            settings.IncludePhysBoneMotion = true;
            settings.PhysBoneClearanceMargin = 0.01f;
            settings.PhysBoneMotionBrushRadius = 0f;
            settings.MaxIssuesPerPhysBone = 4;

            var physWarnings = ClippingFixer.Scan(target, new[] { body }, settings)
                .Where(i => i.Kind == ClippingFixer.IssueKind.PhysBoneMotion)
                .ToList();
            Assert.GreaterOrEqual(physWarnings.Count, 2);
            int selectedVertex = physWarnings[0].VertexIndex;

            using (var session = new AvatarIntentSession()) {
                var result = ClippingFixer.ApplyNonDestructive(
                    target,
                    new[] { body },
                    settings,
                    session,
                    physWarnings.Take(1).ToList());

                Assert.AreEqual(1, result.VerticesReweighted);
                CollectionAssert.AreEqual(targetMesh.vertices, target.sharedMesh.vertices);
                Assert.Greater(GetWeight(target.sharedMesh, selectedVertex, 1), 0.40f);
                Assert.Less(GetWeight(target.sharedMesh, selectedVertex, 2), 0.01f);
            }
        }
#endif

        private ClippingFixer.Settings Settings(bool checkSelf) {
            return new ClippingFixer.Settings {
                CheckSelf = checkSelf,
                IncludePhysBoneMotion = false,
                InsideTolerance = 0.0001f,
                SurfacePadding = 0.01f,
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
            return CreateWeightedRenderer(name, mesh, new[] { bone }, 0);
        }

        private SkinnedMeshRenderer CreateWeightedRenderer(string name, Mesh mesh, Transform[] bones, int weightedBoneIndex) {
            var renderer = CreateRenderer(name, mesh);
            renderer.rootBone = bones != null && bones.Length > 0 ? bones[0] : null;
            renderer.bones = bones ?? new Transform[0];
            mesh.bindposes = renderer.bones
                .Select(bone => bone != null ? bone.worldToLocalMatrix * renderer.transform.localToWorldMatrix : Matrix4x4.identity)
                .ToArray();
            var weights = new BoneWeight[mesh.vertexCount];
            weightedBoneIndex = Mathf.Clamp(weightedBoneIndex, 0, Mathf.Max(0, renderer.bones.Length - 1));
            for (int i = 0; i < weights.Length; i++) {
                weights[i] = new BoneWeight {
                    boneIndex0 = weightedBoneIndex,
                    weight0 = 1f,
                };
            }
            mesh.boneWeights = weights;
            return renderer;
        }

        private static float GetWeight(Mesh mesh, int vertexIndex, int boneIndex) {
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            int cursor = 0;
            for (int v = 0; v < bonesPerVertex.Length; v++) {
                int count = bonesPerVertex[v];
                if (v == vertexIndex) {
                    for (int i = 0; i < count && cursor + i < weights.Length; i++) {
                        var bw = weights[cursor + i];
                        if (bw.boneIndex == boneIndex) return bw.weight;
                    }
                    return 0f;
                }
                cursor += count;
            }
            return 0f;
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
