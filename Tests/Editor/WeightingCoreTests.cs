// WeightingCoreTests.cs
//
// Focused coverage for the shared weight-transfer core. SceneView brush
// editing and PhysBone cleanup build on these same buffer, mapping, and
// correspondence primitives.

using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using WhyKnot.AvatarQol.Weighting;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class WeightingCoreTests {

        private GameObject _root;

        [TearDown]
        public void TearDown() {
            if (_root != null) Object.DestroyImmediate(_root);
            _root = null;
        }

        [Test]
        public void SkinWeightBuffer_PrunesSortsNormalizesAndWritesBoneWeight1Stream() {
            var mesh = new Mesh { name = "WeightBufferTestMesh" };
            try {
                mesh.vertices = new[] { Vector3.zero };

                var buffer = new SkinWeightBuffer(vertexCount: 1, boneCount: 3);
                buffer.SetWeight(0, 0, 0.1f);
                buffer.SetWeight(0, 1, 0.7f);
                buffer.SetWeight(0, 2, 0.2f);

                buffer.WriteToMesh(mesh, maxInfluences: 2, threshold: 0.05f, fallbackBone: 0);

                var bpv = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                Assert.AreEqual(1, bpv.Length);
                Assert.AreEqual(2, bpv[0]);
                Assert.AreEqual(2, weights.Length);
                Assert.AreEqual(1, weights[0].boneIndex);
                Assert.AreEqual(2, weights[1].boneIndex);
                Assert.AreEqual(1f, weights[0].weight + weights[1].weight, 1e-5f);
                Assert.Greater(weights[0].weight, weights[1].weight);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        [Test]
        public void BoneBindingMap_MapsSameTransformAndParentFallback() {
            BuildRoot();
            var parent = NewBone("Chest", _root.transform);
            var child = NewBone("ChestAccessory", parent);
            var sourceRenderer = NewRenderer("Source", new[] { parent, child });
            var targetRenderer = NewRenderer("Target", new[] { parent });

            var map = BoneBindingMap.Build(sourceRenderer, targetRenderer, _root.transform);

            Assert.IsTrue(map.TryMap(0, out int same));
            Assert.AreEqual(0, same);
            Assert.AreEqual(BoneBindingKind.SameTransform, map.GetKind(0));

            Assert.IsTrue(map.TryMap(1, out int parentFallback));
            Assert.AreEqual(0, parentFallback);
            Assert.AreEqual(BoneBindingKind.ParentFallback, map.GetKind(1));
        }

        [Test]
        public void WeightTransferSolver_BarycentricallyBlendsSourceTriangleWeights() {
            BuildRoot();
            var hip = NewBone("Hips", _root.transform);
            var spine = NewBone("Spine", _root.transform);
            var chest = NewBone("Chest", _root.transform);
            var bones = new[] { hip, spine, chest };

            var sourceRenderer = NewRenderer("Source", bones);
            var targetRenderer = NewRenderer("Target", bones);
            sourceRenderer.sharedMesh = BuildSourceTriangleMesh();
            targetRenderer.sharedMesh = BuildTargetTriangleMesh();
            try {
                var result = WeightTransferSolver.Transfer(new WeightTransferSettings {
                    Source = sourceRenderer,
                    Target = targetRenderer,
                    SpaceRoot = _root.transform,
                    Mode = WeightTransferMode.NearestSurface,
                    MaxClosestDistance = 0.1f,
                    NormalAngleLimit = 45f,
                    InpaintRejectedVertices = false,
                    MaxInfluences = 4,
                    PruneThreshold = 0.0001f,
                    FallbackBone = 0,
                });

                Assert.IsNotNull(result.Weights);
                Assert.IsTrue(result.Accepted[0], result.Message);
                Assert.AreEqual(0.2f, result.Weights.GetWeight(0, 0), 1e-4f);
                Assert.AreEqual(0.3f, result.Weights.GetWeight(0, 1), 1e-4f);
                Assert.AreEqual(0.5f, result.Weights.GetWeight(0, 2), 1e-4f);
            } finally {
                Object.DestroyImmediate(sourceRenderer.sharedMesh);
                Object.DestroyImmediate(targetRenderer.sharedMesh);
            }
        }

        [Test]
        public void WeightInpaintSolver_FillsRejectedVertexFromAcceptedNeighbors() {
            var mesh = new Mesh { name = "WeightInpaintTriangle" };
            try {
                mesh.vertices = new[] {
                    Vector3.zero,
                    Vector3.right,
                    Vector3.up,
                };
                mesh.triangles = new[] { 0, 1, 2 };

                var output = new SkinWeightBuffer(vertexCount: 3, boneCount: 2);
                output.SetWeight(0, 0, 1f);
                output.SetWeight(2, 1, 1f);
                var accepted = new[] { true, false, true };

                var result = WeightInpaintSolver.FillRejected(
                    output,
                    accepted,
                    MeshAdjacency.Build(mesh),
                    fallback: null,
                    iterations: 0,
                    maxInfluences: 4,
                    pruneThreshold: 0.0001f,
                    fallbackBone: 0);

                Assert.AreEqual(1, result.Inpainted);
                Assert.AreEqual(0, result.Unresolved);
                Assert.AreEqual(0.5f, output.GetWeight(1, 0), 1e-5f);
                Assert.AreEqual(0.5f, output.GetWeight(1, 1), 1e-5f);
            } finally {
                Object.DestroyImmediate(mesh);
            }
        }

        private void BuildRoot() {
            _root = new GameObject("WeightingCoreTestRoot");
        }

        private Transform NewBone(string name, Transform parent) {
            var go = new GameObject(name);
            go.transform.SetParent(parent, worldPositionStays: false);
            return go.transform;
        }

        private SkinnedMeshRenderer NewRenderer(string name, Transform[] bones) {
            var go = new GameObject(name);
            go.transform.SetParent(_root.transform, worldPositionStays: false);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = bones;
            renderer.rootBone = bones.Length > 0 ? bones[0] : null;
            return renderer;
        }

        private static Mesh BuildSourceTriangleMesh() {
            var mesh = new Mesh { name = "WeightTransferSourceTriangle" };
            mesh.vertices = new[] {
                new Vector3(0f, 0f, 0f),
                new Vector3(1f, 0f, 0f),
                new Vector3(0f, 1f, 0f),
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
            using (var bpv = new NativeArray<byte>(3, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(3, Allocator.Temp)) {
                bpv.CopyFrom(new byte[] { 1, 1, 1 });
                weights.CopyFrom(new[] {
                    new BoneWeight1 { boneIndex = 0, weight = 1f },
                    new BoneWeight1 { boneIndex = 1, weight = 1f },
                    new BoneWeight1 { boneIndex = 2, weight = 1f },
                });
                mesh.SetBoneWeights(bpv, weights);
            }
            return mesh;
        }

        private static Mesh BuildTargetTriangleMesh() {
            var mesh = new Mesh { name = "WeightTransferTargetTriangle" };
            mesh.vertices = new[] {
                new Vector3(0.3f, 0.5f, 0.02f),
                new Vector3(0.5f, 0.5f, 0.02f),
                new Vector3(0.3f, 0.7f, 0.02f),
            };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity };
            using (var bpv = new NativeArray<byte>(3, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(3, Allocator.Temp)) {
                bpv.CopyFrom(new byte[] { 1, 1, 1 });
                weights.CopyFrom(new[] {
                    new BoneWeight1 { boneIndex = 0, weight = 1f },
                    new BoneWeight1 { boneIndex = 0, weight = 1f },
                    new BoneWeight1 { boneIndex = 0, weight = 1f },
                });
                mesh.SetBoneWeights(bpv, weights);
            }
            return mesh;
        }
    }
}
