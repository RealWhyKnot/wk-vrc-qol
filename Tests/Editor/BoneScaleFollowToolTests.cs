// BoneScaleFollowToolTests.cs

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class BoneScaleFollowToolTests {

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown() {
            for (int i = _objects.Count - 1; i >= 0; i--) {
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [Test]
        public void BoneScaleFollow_AnalyticalMatchesTemporaryMutationAt115() {
            var rig = BuildSingleBoneRig();
            var row = Row(rig.Bone, Vector3.one, Vector3.one * 1.15f);
            var options = Options(rig.Renderer, row, BuildPlainTarget("Skirt", 1.02f));

            var field = BoneScaleFollowCore.BuildSourceField(options, new[] { row }, out string error);
            Assert.IsNull(error);
            Assert.IsTrue(field.HasActiveDelta);

            var originalScale = rig.Bone.localScale;
            try {
                rig.Bone.localScale = Vector3.one * 1.15f;
                var mutatedSkin = SkinDeform.ComputeSkinMatrices(rig.Renderer, rig.Renderer.sharedMesh);
                var mutatedWorld = SkinDeform.TransformPoints(mutatedSkin, rig.Renderer.sharedMesh.vertices);
                for (int i = 0; i < mutatedWorld.Length; i++) {
                    AssertVector(mutatedWorld[i], field.WorldVertices[i] + field.WorldDeltasByFrame[0][i], 0.0001f);
                }
            } finally {
                rig.Bone.localScale = originalScale;
            }
        }

        [Test]
        public void BoneScaleFollow_DescendantBoneInheritsScaledAncestor() {
            var rig = BuildSingleBoneRig(weightedChild: true);
            var row = Row(rig.Bone, Vector3.one, Vector3.one * 1.15f);
            var options = Options(rig.Renderer, row, BuildPlainTarget("Skirt", 1.02f));

            var field = BoneScaleFollowCore.BuildSourceField(options, new[] { row }, out string error);
            Assert.IsNull(error);
            Assert.Greater(field.WorldDeltasByFrame[0][0].x, 0.149f);
        }

        [Test]
        public void BoneScaleFollow_MultipleDisjointRowsAffectExpectedVertices() {
            var rig = BuildTwoBoneRig();
            var rowA = Row(rig.Bone, Vector3.one, new Vector3(1.10f, 1f, 1f));
            var rowB = Row(rig.SecondBone, Vector3.one, new Vector3(1.20f, 1f, 1f));
            var options = Options(rig.Renderer, rowA, BuildPlainTarget("Skirt", 1.02f));
            options.BoneRows = new[] { rowA, rowB };

            var field = BoneScaleFollowCore.BuildSourceField(options, new[] { rowA, rowB }, out string error);
            Assert.IsNull(error);
            Assert.AreEqual(0.10f, field.WorldDeltasByFrame[0][0].x, 0.0001f);
            Assert.AreEqual(0.20f, field.WorldDeltasByFrame[0][1].x, 0.0001f);
        }

        [Test]
        public void BoneScaleFollow_AncestorDescendantRowsAreRejected() {
            var rig = BuildSingleBoneRig(weightedChild: true);
            var target = BuildPlainTarget("Skirt", 1.02f);
            var options = Options(rig.Renderer, Row(rig.Bone, Vector3.one, Vector3.one * 1.15f), target);
            options.BoneRows = new[] {
                Row(rig.Bone, Vector3.one, Vector3.one * 1.15f),
                Row(rig.ChildBone, Vector3.one, Vector3.one * 1.10f),
            };

            var preview = BoneScaleFollowCore.Preview(options);
            Assert.IsTrue(preview.ConfigurationError);
            StringAssert.Contains("disjoint", preview.Summary);
        }

        [Test]
        public void BoneScaleFollow_EqualScaleProducesNoTargetDeltas() {
            var rig = BuildSingleBoneRig();
            var target = BuildPlainTarget("Skirt", 1.02f);
            var row = Row(rig.Bone, Vector3.one, Vector3.one);

            var preview = BoneScaleFollowCore.Preview(Options(rig.Renderer, row, target));

            Assert.IsFalse(preview.ConfigurationError);
            Assert.AreEqual(0, preview.ProcessedCount);
            Assert.AreEqual(1, preview.SkippedCount);
            StringAssert.Contains("did not move", preview.Targets[0].Reason);
        }

        [Test]
        public void BoneScaleFollow_NullSourceReportsConfigurationError() {
            var preview = BoneScaleFollowCore.Preview(new BoneScaleFollowCore.Options {
                BoneRows = new[] { new BoneScaleFollowRow() },
                TargetRenderers = new[] { BuildPlainTarget("Skirt", 1.02f) },
            });

            Assert.IsTrue(preview.ConfigurationError);
            StringAssert.Contains("source renderer", preview.Summary);
        }

        [Test]
        public void BoneScaleFollow_NegativeScaleIsRejected() {
            var rig = BuildSingleBoneRig();
            var row = Row(rig.Bone, Vector3.one, new Vector3(-1f, 1f, 1f));

            var preview = BoneScaleFollowCore.Preview(Options(rig.Renderer, row, BuildPlainTarget("Skirt", 1.02f)));

            Assert.IsTrue(preview.ConfigurationError);
            StringAssert.Contains("invalid target scale", preview.Summary);
        }

        [Test]
        public void BoneScaleFollow_FarTargetSkipsBeforePerVertexWork() {
            var rig = BuildSingleBoneRig();
            var row = Row(rig.Bone, Vector3.one, Vector3.one * 1.15f);
            var target = BuildPlainTarget("FarSkirt", 5f);

            var preview = BoneScaleFollowCore.Preview(Options(rig.Renderer, row, target));

            Assert.AreEqual(0, preview.ProcessedCount);
            Assert.AreEqual(1, preview.SkippedCount);
            StringAssert.Contains("Outside active source shape bounds", preview.Targets[0].Reason);
        }

        [Test]
        public void BoneScaleFollow_NearbySkirtGets115FollowWithoutOvershoot() {
            var rig = BuildSingleBoneRig();
            var row = Row(rig.Bone, Vector3.one, Vector3.one * 1.15f);
            var skirt = BuildPlainTarget("Skirt", 1.02f);

            var preview = BoneScaleFollowCore.Preview(Options(rig.Renderer, row, skirt));

            Assert.AreEqual(1, preview.ProcessedCount);
            var delta = preview.Targets[0].Frames[0].DeltaVertices[0];
            float finalX = skirt.sharedMesh.vertices[0].x + delta.x;
            Assert.GreaterOrEqual(finalX, 1.14f, "Skirt should move close enough to the scaled body to avoid clipping.");
            Assert.LessOrEqual(finalX, 1.18f, "Skirt should preserve its small offset instead of pushing far past the body.");
        }

        [Test]
        public void BoneScaleFollow_OwnResponseCompensationAvoidsDoubleMovement() {
            var rig = BuildSingleBoneRig();
            var row = Row(rig.Bone, Vector3.one, Vector3.one * 1.15f);
            var weightedSkirt = BuildWeightedTarget("WeightedSkirt", rig.Bone);

            var uncompensated = Options(rig.Renderer, row, weightedSkirt);
            uncompensated.OwnResponseCompensation = false;
            Assert.AreEqual(1, BoneScaleFollowCore.Preview(uncompensated).ProcessedCount);

            var compensated = Options(rig.Renderer, row, weightedSkirt);
            compensated.OwnResponseCompensation = true;
            var preview = BoneScaleFollowCore.Preview(compensated);
            Assert.AreEqual(0, preview.ProcessedCount);
            StringAssert.Contains("No vertices close enough", preview.Targets[0].Reason);
        }

        private Rig BuildSingleBoneRig(bool weightedChild = false) {
            var root = Track(new GameObject("Avatar"));
            root.AddComponent<Animator>();
            var boneGo = Track(new GameObject("Butt"));
            boneGo.transform.SetParent(root.transform, false);
            var childGo = Track(new GameObject("ButtChild"));
            childGo.transform.SetParent(boneGo.transform, false);

            var rendererGo = Track(new GameObject("Body"));
            rendererGo.transform.SetParent(root.transform, false);
            var renderer = rendererGo.AddComponent<SkinnedMeshRenderer>();
            var weightedBone = weightedChild ? childGo.transform : boneGo.transform;
            renderer.bones = new[] { weightedBone };
            renderer.sharedMesh = Track(BuildWeightedPlaneMesh("BodyMesh", weightedBone, renderer.transform));

            return new Rig {
                Renderer = renderer,
                Bone = boneGo.transform,
                ChildBone = childGo.transform,
            };
        }

        private Rig BuildTwoBoneRig() {
            var root = Track(new GameObject("Avatar2"));
            root.AddComponent<Animator>();
            var boneA = Track(new GameObject("ButtA"));
            var boneB = Track(new GameObject("ButtB"));
            boneA.transform.SetParent(root.transform, false);
            boneB.transform.SetParent(root.transform, false);
            var rendererGo = Track(new GameObject("Body2"));
            rendererGo.transform.SetParent(root.transform, false);
            var renderer = rendererGo.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = new[] { boneA.transform, boneB.transform };

            var mesh = Track(new Mesh { name = "TwoBoneMesh" });
            mesh.vertices = new[] {
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0.2f, 0f),
                new Vector3(1f, 0f, 0.2f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] {
                boneA.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
                boneB.transform.worldToLocalMatrix * renderer.transform.localToWorldMatrix,
            };
            using (var bpv = new NativeArray<byte>(new byte[] { 1, 1, 1 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 { boneIndex = 0, weight = 1f },
                new BoneWeight1 { boneIndex = 1, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
            }, Allocator.Temp)) {
                mesh.SetBoneWeights(bpv, weights);
            }
            renderer.sharedMesh = mesh;
            return new Rig { Renderer = renderer, Bone = boneA.transform, SecondBone = boneB.transform };
        }

        private Mesh BuildWeightedPlaneMesh(string name, Transform bone, Transform rendererTransform) {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] {
                new Vector3(1f, 0f, 0f),
                new Vector3(1f, 0.5f, 0f),
                new Vector3(1f, 0f, 0.5f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.normals = new[] { Vector3.right, Vector3.right, Vector3.right };
            mesh.bindposes = new[] { bone.worldToLocalMatrix * rendererTransform.localToWorldMatrix };
            using (var bpv = new NativeArray<byte>(new byte[] { 1, 1, 1 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 { boneIndex = 0, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
            }, Allocator.Temp)) {
                mesh.SetBoneWeights(bpv, weights);
            }
            return mesh;
        }

        private SkinnedMeshRenderer BuildPlainTarget(string name, float x) {
            var go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = Track(BuildPlaneMesh(name + "Mesh", x));
            return renderer;
        }

        private SkinnedMeshRenderer BuildWeightedTarget(string name, Transform bone) {
            var go = Track(new GameObject(name));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = new[] { bone };
            renderer.sharedMesh = Track(BuildWeightedPlaneMesh(name + "Mesh", bone, renderer.transform));
            return renderer;
        }

        private Mesh BuildPlaneMesh(string name, float x) {
            var mesh = new Mesh { name = name };
            mesh.vertices = new[] {
                new Vector3(x, 0f, 0f),
                new Vector3(x, 0.2f, 0f),
                new Vector3(x, 0f, 0.2f),
            };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.normals = new[] { Vector3.right, Vector3.right, Vector3.right };
            mesh.RecalculateBounds();
            return mesh;
        }

        private BoneScaleFollowCore.Options Options(
                SkinnedMeshRenderer source,
                BoneScaleFollowRow row,
                SkinnedMeshRenderer target) {

            return new BoneScaleFollowCore.Options {
                SourceRenderer = source,
                BoneRows = new[] { row },
                TargetRenderers = new[] { target },
                OutputBlendShapeName = "ButtScaleFollow",
                MaxDistance = 0.05f,
                DeltaEpsilon = 0.00001f,
                OwnResponseCompensation = true,
            };
        }

        private static BoneScaleFollowRow Row(Transform bone, Vector3 baseScale, Vector3 targetScale) {
            return new BoneScaleFollowRow {
                Enabled = true,
                Bone = bone,
                BaseScale = baseScale,
                TargetScale = targetScale,
            };
        }

        private void AssertVector(Vector3 actual, Vector3 expected, float tolerance) {
            Assert.AreEqual(expected.x, actual.x, tolerance);
            Assert.AreEqual(expected.y, actual.y, tolerance);
            Assert.AreEqual(expected.z, actual.z, tolerance);
        }

        private T Track<T>(T obj) where T : Object {
            _objects.Add(obj);
            return obj;
        }

        private sealed class Rig {
            public SkinnedMeshRenderer Renderer;
            public Transform Bone;
            public Transform ChildBone;
            public Transform SecondBone;
        }
    }
}
