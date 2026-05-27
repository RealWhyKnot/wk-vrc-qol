// BoneMergerOpTests.cs
//
// Verifies the non-destructive Bone Merger flow leaves the source mesh
// asset alone but still produces the merged result on the renderer
// during the session, then puts the renderer back when the session
// disposes. This is the property the build hook depends on -- a
// regression here would silently leak modified meshes onto an avatar
// after a play / upload cycle.

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using WhyKnot.AvatarQol.BoneMerger;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class BoneMergerOpTests {

        private GameObject _avatarRoot;
        private Animator _animator;
        private Transform _boneA;
        private Transform _boneB;
        private SkinnedMeshRenderer _renderer;
        private Mesh _sourceMesh;

        [SetUp]
        public void SetUp() {
            _avatarRoot = new GameObject("BoneMergerTestAvatar");
            _animator = _avatarRoot.AddComponent<Animator>();

            _boneA = new GameObject("BoneA").transform;
            _boneA.SetParent(_avatarRoot.transform);
            _boneB = new GameObject("BoneB").transform;
            _boneB.SetParent(_avatarRoot.transform);

            var rendererGO = new GameObject("Renderer");
            rendererGO.transform.SetParent(_avatarRoot.transform);
            _renderer = rendererGO.AddComponent<SkinnedMeshRenderer>();
            _renderer.bones = new[] { _boneA, _boneB };

            _sourceMesh = BuildSingleVertexMesh(weightOnBoneIndex: 0);
            _renderer.sharedMesh = _sourceMesh;
        }

        [TearDown]
        public void TearDown() {
            if (_avatarRoot != null) Object.DestroyImmediate(_avatarRoot);
            if (_sourceMesh != null) Object.DestroyImmediate(_sourceMesh);
            _avatarRoot = null;
            _sourceMesh = null;
        }

        [Test]
        public void ApplyNonDestructive_SwapsMeshOnRenderer_LeavesSourceUnchanged() {
            var pair = new BoneMergerPair { mergeFrom = _boneA, mergeInto = _boneB };
            var session = new AvatarIntentSession();
            try {
                var sourceBoneIndexBefore = ReadFirstVertexBoneIndex(_sourceMesh);
                Assert.AreEqual(0, sourceBoneIndexBefore, "Source mesh starts weighted to bone index 0.");

                var result = BoneMergerOp.ApplyNonDestructive(
                    _animator, new[] { pair }, session);

                Assert.Greater(result.WeightsRedirected, 0, "At least one weight should have been redirected.");
                Assert.AreNotSame(_sourceMesh, _renderer.sharedMesh, "Non-destructive apply must swap a clone onto the renderer, not mutate the source.");
                Assert.AreEqual(1, ReadFirstVertexBoneIndex(_renderer.sharedMesh), "Clone's vertex should now weight bone index 1.");
                Assert.AreEqual(0, ReadFirstVertexBoneIndex(_sourceMesh), "Source mesh must remain untouched.");

                // Bone GameObjects must remain even though deleteMergedBones
                // was conceptually true on the intent: the non-destructive
                // path always ignores that flag.
                Assert.IsTrue(_boneA != null && _boneA.gameObject != null, "Non-destructive apply must never destroy bone GameObjects.");
            } finally {
                session.Dispose();
            }

            Assert.AreSame(_sourceMesh, _renderer.sharedMesh, "Dispose must put the original sharedMesh back on the renderer.");
        }

        [Test]
        public void ApplyNonDestructive_NoPairs_ReportsConfigurationError() {
            var session = new AvatarIntentSession();
            try {
                var result = BoneMergerOp.ApplyNonDestructive(_animator, new BoneMergerPair[0], session);
                Assert.IsTrue(result.ConfigurationError);
                Assert.AreSame(_sourceMesh, _renderer.sharedMesh, "Empty pair list must not touch the renderer.");
            } finally {
                session.Dispose();
            }
        }

        [Test]
        public void ApplyNonDestructive_UsesPrecomputedRendererPlan() {
            var pair = new BoneMergerPair { mergeFrom = _boneA, mergeInto = _boneB };
            var plan = BoneMergerOp.PrecomputeRenderers(_animator, new[] { pair }, out var error);
            Assert.IsTrue(string.IsNullOrEmpty(error));
            Assert.AreEqual(1, plan.Count);
            Assert.AreSame(_renderer, plan[0].renderer);

            var session = new AvatarIntentSession();
            try {
                var result = BoneMergerOp.ApplyNonDestructive(
                    _animator,
                    new[] { pair },
                    session,
                    plan);

                Assert.Greater(result.WeightsRedirected, 0);
                Assert.AreEqual(1, ReadFirstVertexBoneIndex(_renderer.sharedMesh));
                Assert.AreEqual(0, ReadFirstVertexBoneIndex(_sourceMesh));
            } finally {
                session.Dispose();
            }
        }

        // ---- Helpers ----------------------------------------------------

        private static Mesh BuildSingleVertexMesh(byte weightOnBoneIndex) {
            var mesh = new Mesh { name = "BoneMergerTestMesh" };
            mesh.vertices = new[] { Vector3.zero };
            mesh.bindposes = new[] { Matrix4x4.identity, Matrix4x4.identity };
            mesh.triangles = new[] { 0, 0, 0 };

            // Don't `using (var x = ...)` and then index-assign x[i] = ...:
            // the using-statement local is readonly, and NativeArray's
            // indexer setter requires non-readonly access (CS1654). Use
            // CopyFrom which is a method call and works fine on a
            // readonly local.
            using (var bpv = new NativeArray<byte>(1, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(1, Allocator.Temp)) {
                bpv.CopyFrom(new byte[] { 1 });
                weights.CopyFrom(new[] {
                    new BoneWeight1 { boneIndex = weightOnBoneIndex, weight = 1f },
                });
                mesh.SetBoneWeights(bpv, weights);
            }
            return mesh;
        }

        private static int ReadFirstVertexBoneIndex(Mesh mesh) {
            var weights = mesh.GetAllBoneWeights();
            return weights.Length == 0 ? -1 : weights[0].boneIndex;
        }
    }
}
