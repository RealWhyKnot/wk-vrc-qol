// MeshCleanupToolTests.cs

using System.Collections.Generic;
using NUnit.Framework;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Tests {

    public sealed class MeshCleanupToolTests {

        private readonly List<Object> _objects = new List<Object>();

        [TearDown]
        public void TearDown() {
            for (int i = _objects.Count - 1; i >= 0; i--) {
                if (_objects[i] != null) Object.DestroyImmediate(_objects[i]);
            }
            _objects.Clear();
        }

        [Test]
        public void MeshCompactor_PreservesStreamsWeightsAndBlendShapes() {
            var mesh = Track(BuildStreamMesh());
            var result = MeshCompactor.BuildKeepingTriangles(
                mesh,
                new[] { mesh.GetTriangles(1) },
                "Compacted");

            Assert.AreEqual(3, result.KeptVertexCount);
            Assert.AreEqual(1, result.DroppedVertexCount);
            Assert.AreEqual(new[] { 0, 2, 3 }, result.KeptOldVertexIndices);
            Assert.AreEqual(new Vector3(0, 0, 0), result.Mesh.vertices[0]);
            Assert.AreEqual(new Vector3(1, 1, 0), result.Mesh.vertices[1]);

            var uv2 = new List<Vector4>();
            result.Mesh.GetUVs(2, uv2);
            Assert.AreEqual(3, uv2.Count);
            Assert.AreEqual(new Vector4(2, 2, 2, 2), uv2[1]);

            Assert.AreEqual(1, result.Mesh.blendShapeCount);
            var deltas = new Vector3[result.Mesh.vertexCount];
            var normals = new Vector3[result.Mesh.vertexCount];
            var tangents = new Vector3[result.Mesh.vertexCount];
            result.Mesh.GetBlendShapeFrameVertices(0, 0, deltas, normals, tangents);
            Assert.AreEqual(new Vector3(0, 0.2f, 0), deltas[1]);

            var bpv = result.Mesh.GetBonesPerVertex();
            var weights = result.Mesh.GetAllBoneWeights();
            Assert.AreEqual(5, bpv[1], "Modern >4 influence weights must survive compaction.");
            Assert.AreEqual(7, weights.Length);
        }

        [Test]
        public void MeshCompactor_UsesUInt32WhenKeptVerticesExceedUInt16() {
            var mesh = Track(new Mesh { name = "Large" });
            int count = 65538;
            var verts = new Vector3[count];
            var tris = new int[count];
            for (int i = 0; i < count; i++) {
                verts[i] = new Vector3(i, 0, 0);
                tris[i] = i;
            }
            mesh.vertices = verts;
            mesh.triangles = tris;

            var result = MeshCompactor.BuildKeepingTriangles(mesh, new[] { tris }, "LargeOut");
            Assert.AreEqual(count, result.KeptVertexCount);
            Assert.AreEqual(IndexFormat.UInt32, result.Mesh.indexFormat);
        }

        [Test]
        public void OrphanCleaner_DefaultDropsInvalidSlotsAndKeepsValidVertices() {
            var rig = BuildWeightedRenderer();
            var plan = OrphanedBoneWeightCleanerCore.BuildPlan(
                rig.Renderer,
                null,
                OrphanedBoneCleanupMode.DropInvalidWeights,
                growDeletionAcrossConnectedTriangles: false);

            Assert.IsTrue(plan.HasChanges);
            Assert.AreEqual(2, plan.InvalidWeightSlots);
            Assert.AreEqual(1, plan.VerticesDeleted);
            Assert.AreEqual(1, plan.TrianglesDeleted);
            Assert.AreEqual(1, plan.CleanedWeights[0].Count);
            Assert.AreEqual(1f, plan.CleanedWeights[0][0].weight, 1e-5f);
        }

        [Test]
        public void OrphanCleaner_DeleteModeDeletesAnyTouchedVertex() {
            var rig = BuildWeightedRenderer();
            var plan = OrphanedBoneWeightCleanerCore.BuildPlan(
                rig.Renderer,
                null,
                OrphanedBoneCleanupMode.DeleteVerticesWithInvalidWeights,
                growDeletionAcrossConnectedTriangles: false);

            Assert.AreEqual(2, plan.VerticesDeleted);
            Assert.AreEqual(1, plan.TrianglesDeleted);
        }

        [Test]
        public void OrphanCleaner_CleanMeshNoOps() {
            var rig = BuildWeightedRenderer(allValid: true);
            var plan = OrphanedBoneWeightCleanerCore.BuildPlan(
                rig.Renderer,
                null,
                OrphanedBoneCleanupMode.DropInvalidWeights,
                growDeletionAcrossConnectedTriangles: false);

            Assert.IsFalse(plan.HasChanges);
            Assert.AreEqual(0, plan.InvalidWeightSlots);
        }

        [Test]
        public void OrphanCleaner_NullRendererReportsConfigurationError() {
            var plan = OrphanedBoneWeightCleanerCore.BuildPlan(
                null,
                null,
                OrphanedBoneCleanupMode.DropInvalidWeights,
                growDeletionAcrossConnectedTriangles: false);

            Assert.IsTrue(plan.ConfigurationError);
        }

        [Test]
        public void MaterialPolygonRemover_RemovesSelectedSlotAndCompactsMesh() {
            var renderer = BuildMaterialRenderer();
            var plan = MaterialPolygonRemoverCore.BuildPlan(renderer, new[] { false, true, false });
            Assert.IsTrue(plan.CanApply);
            Assert.AreEqual(1, plan.RemovedSubmeshes);
            Assert.AreEqual(1, plan.RemovedTriangles);
            Assert.AreEqual(2, plan.KeptSubmeshTriangles.Count);
            Assert.AreEqual("Mat0", plan.KeptMaterials[0].name);
            Assert.AreEqual("Mat2", plan.KeptMaterials[1].name);

            var compacted = MeshCompactor.BuildKeepingTriangles(
                renderer.sharedMesh,
                plan.KeptSubmeshTriangles,
                "MatPruned");
            Assert.AreEqual(4, compacted.KeptVertexCount);
            Assert.AreEqual(2, compacted.Mesh.subMeshCount);
        }

        [Test]
        public void MaterialPolygonRemover_RefusesRemoveAll() {
            var renderer = BuildMaterialRenderer();
            var plan = MaterialPolygonRemoverCore.BuildPlan(renderer, new[] { true, true, true });
            Assert.IsFalse(plan.CanApply);
            StringAssert.Contains("every material slot", plan.Error);
        }

        [Test]
        public void SkinDeform_FallbackLocalToWorldAndInverseVectorRoundTrip() {
            var go = Track(new GameObject("Renderer"));
            go.transform.position = new Vector3(2, 0, 0);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(new Mesh { name = "Unskinned" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right };
            mesh.triangles = new[] { 0, 1, 1 };
            renderer.sharedMesh = mesh;

            var skin = SkinDeform.ComputeSkinMatrices(renderer, mesh);
            var world = SkinDeform.TransformPoints(skin, mesh.vertices);
            Assert.AreEqual(new Vector3(2, 0, 0), world[0]);
            Assert.IsTrue(SkinDeform.InverseMultiplyVector(skin[0], new Vector3(0, 3, 0), out var local));
            Assert.AreEqual(new Vector3(0, 3, 0), local);
        }

        [Test]
        public void BlendShapeTransfer_IdentityRoundTripTransfersDeltas() {
            var source = BuildBlendShapeRenderer("Source", Vector3.zero);
            var target = BuildPlainRenderer("Target", Vector3.zero);

            var preview = BlendShapeTransferCore.Preview(new BlendShapeTransferCore.Options {
                SourceRenderer = source,
                SourceBlendShapeName = "ButtScale",
                OutputBlendShapeName = "ButtScale",
                TargetRenderers = new[] { target },
                MaxDistance = 0.05f,
                DeltaEpsilon = 0.00001f,
            });

            Assert.AreEqual(1, preview.ProcessedCount);
            var frame = preview.Targets[0].Frames[0];
            Assert.AreEqual(new Vector3(0, 0.1f, 0), frame.DeltaVertices[0]);
            Assert.Greater(preview.Targets[0].AffectedVertices, 0);
        }

        [Test]
        public void BlendShapeTransfer_FarTargetSkipsBeforePerVertexWork() {
            var source = BuildBlendShapeRenderer("Source", Vector3.zero);
            var target = BuildPlainRenderer("FarTarget", new Vector3(10, 0, 0));

            var preview = BlendShapeTransferCore.Preview(new BlendShapeTransferCore.Options {
                SourceRenderer = source,
                SourceBlendShapeName = "ButtScale",
                TargetRenderers = new[] { target },
                MaxDistance = 0.02f,
            });

            Assert.AreEqual(0, preview.ProcessedCount);
            Assert.AreEqual(1, preview.SkippedCount);
            StringAssert.Contains("Outside active source shape bounds", preview.Targets[0].Reason);
        }

        [Test]
        public void BlendShapeTransfer_PartialOverlapAffectsOnlyNearVertices() {
            var source = BuildBlendShapeRenderer("Source", Vector3.zero);
            var targetGo = Track(new GameObject("PartialTarget"));
            var target = targetGo.AddComponent<SkinnedMeshRenderer>();
            target.sharedMesh = Track(BuildTriangleMesh("PartialMesh",
                new[] { Vector3.zero, new Vector3(0.01f, 0, 0), new Vector3(2.0f, 0, 0) }));

            var preview = BlendShapeTransferCore.Preview(new BlendShapeTransferCore.Options {
                SourceRenderer = source,
                SourceBlendShapeName = "ButtScale",
                TargetRenderers = new[] { target },
                MaxDistance = 0.03f,
                DeltaEpsilon = 0.00001f,
            });

            Assert.AreEqual(1, preview.ProcessedCount);
            Assert.Greater(preview.Targets[0].AffectedVertices, 0);
            Assert.Less(preview.Targets[0].AffectedVertices, target.sharedMesh.vertexCount);
        }

        [Test]
        public void BlendShapeTransfer_MaxDistanceRejectsBeyondThreshold() {
            var source = BuildBlendShapeRenderer("Source", Vector3.zero);
            var target = BuildPlainRenderer("OffsetTarget", new Vector3(0, 0, 0.1f));

            var preview = BlendShapeTransferCore.Preview(new BlendShapeTransferCore.Options {
                SourceRenderer = source,
                SourceBlendShapeName = "ButtScale",
                TargetRenderers = new[] { target },
                MaxDistance = 0.01f,
                DeltaEpsilon = 0.00001f,
            });

            Assert.AreEqual(0, preview.ProcessedCount);
        }

        private Mesh BuildStreamMesh() {
            var mesh = new Mesh { name = "StreamMesh" };
            mesh.vertices = new[] {
                new Vector3(0, 0, 0),
                new Vector3(1, 0, 0),
                new Vector3(1, 1, 0),
                new Vector3(0, 1, 0),
            };
            mesh.normals = new[] { Vector3.up, Vector3.up, Vector3.up, Vector3.up };
            mesh.tangents = new[] { Vector4.one, Vector4.one, Vector4.one, Vector4.one };
            mesh.colors32 = new[] {
                new Color32(1, 2, 3, 4),
                new Color32(5, 6, 7, 8),
                new Color32(9, 10, 11, 12),
                new Color32(13, 14, 15, 16),
            };
            mesh.SetUVs(0, new List<Vector4> {
                new Vector4(0, 0, 0, 0), new Vector4(1, 0, 0, 0),
                new Vector4(1, 1, 0, 0), new Vector4(0, 1, 0, 0),
            });
            mesh.SetUVs(2, new List<Vector4> {
                Vector4.zero, Vector4.one, new Vector4(2, 2, 2, 2), new Vector4(3, 3, 3, 3),
            });
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
            mesh.bindposes = new[] {
                Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity,
                Matrix4x4.identity, Matrix4x4.identity,
            };
            using (var bpv = new NativeArray<byte>(new byte[] { 1, 1, 5, 1 }, Allocator.Temp))
            using (var weights = new NativeArray<BoneWeight1>(new[] {
                new BoneWeight1 { boneIndex = 0, weight = 1f },
                new BoneWeight1 { boneIndex = 1, weight = 1f },
                new BoneWeight1 { boneIndex = 0, weight = 0.2f },
                new BoneWeight1 { boneIndex = 1, weight = 0.2f },
                new BoneWeight1 { boneIndex = 2, weight = 0.2f },
                new BoneWeight1 { boneIndex = 3, weight = 0.2f },
                new BoneWeight1 { boneIndex = 4, weight = 0.2f },
                new BoneWeight1 { boneIndex = 0, weight = 1f },
            }, Allocator.Temp)) {
                mesh.SetBoneWeights(bpv, weights);
            }
            mesh.AddBlendShapeFrame("Scale", 100f, new[] {
                Vector3.zero,
                new Vector3(0, 0.1f, 0),
                new Vector3(0, 0.2f, 0),
                new Vector3(0, 0.3f, 0),
            }, null, null);
            return mesh;
        }

        private Rig BuildWeightedRenderer(bool allValid = false) {
            var root = Track(new GameObject("Rig"));
            var boneA = Track(new GameObject("BoneA"));
            var boneB = Track(new GameObject("BoneB"));
            boneA.transform.SetParent(root.transform);
            boneB.transform.SetParent(root.transform);
            var rendererGo = Track(new GameObject("Renderer"));
            rendererGo.transform.SetParent(root.transform);
            var renderer = rendererGo.AddComponent<SkinnedMeshRenderer>();
            renderer.bones = new[] { boneA.transform, boneB.transform };
            var mesh = Track(new Mesh { name = "Weighted" });
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.bindposes = new[] {
                Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity, Matrix4x4.identity,
            };
            if (allValid) {
                using (var bpv = new NativeArray<byte>(new byte[] { 1, 1, 1 }, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(new[] {
                    new BoneWeight1 { boneIndex = 0, weight = 1f },
                    new BoneWeight1 { boneIndex = 1, weight = 1f },
                    new BoneWeight1 { boneIndex = 1, weight = 1f },
                }, Allocator.Temp)) {
                    mesh.SetBoneWeights(bpv, weights);
                }
            } else {
                using (var bpv = new NativeArray<byte>(new byte[] { 2, 1, 1 }, Allocator.Temp))
                using (var weights = new NativeArray<BoneWeight1>(new[] {
                    new BoneWeight1 { boneIndex = 0, weight = 0.75f },
                    new BoneWeight1 { boneIndex = 3, weight = 0.25f },
                    new BoneWeight1 { boneIndex = 1, weight = 1f },
                    new BoneWeight1 { boneIndex = 3, weight = 1f },
                }, Allocator.Temp)) {
                    mesh.SetBoneWeights(bpv, weights);
                }
            }
            renderer.sharedMesh = mesh;
            return new Rig { Renderer = renderer };
        }

        private SkinnedMeshRenderer BuildMaterialRenderer() {
            var go = Track(new GameObject("MaterialRenderer"));
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            var mesh = Track(new Mesh { name = "MaterialMesh" });
            mesh.vertices = new[] {
                Vector3.zero, Vector3.right, Vector3.up, new Vector3(1, 1, 0),
            };
            mesh.subMeshCount = 3;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 0);
            mesh.SetTriangles(new[] { 0, 2, 3 }, 1);
            mesh.SetTriangles(new[] { 1, 2, 3 }, 2);
            renderer.sharedMesh = mesh;
            renderer.sharedMaterials = new[] {
                Track(new Material(Shader.Find("Standard")) { name = "Mat0" }),
                Track(new Material(Shader.Find("Standard")) { name = "Mat1" }),
                Track(new Material(Shader.Find("Standard")) { name = "Mat2" }),
            };
            return renderer;
        }

        private SkinnedMeshRenderer BuildBlendShapeRenderer(string name, Vector3 position) {
            var renderer = BuildPlainRenderer(name, position);
            var mesh = Object.Instantiate(renderer.sharedMesh);
            mesh.name = name + "MeshWithShape";
            Track(mesh);
            mesh.AddBlendShapeFrame("ButtScale", 100f, new[] {
                new Vector3(0, 0.1f, 0),
                new Vector3(0, 0.1f, 0),
                new Vector3(0, 0.1f, 0),
            }, null, null);
            renderer.sharedMesh = mesh;
            return renderer;
        }

        private SkinnedMeshRenderer BuildPlainRenderer(string name, Vector3 position) {
            var go = Track(new GameObject(name));
            go.transform.position = position;
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = Track(BuildTriangleMesh(name + "Mesh", new[] {
                Vector3.zero, Vector3.right, Vector3.up,
            }));
            return renderer;
        }

        private Mesh BuildTriangleMesh(string name, Vector3[] vertices) {
            var mesh = new Mesh { name = name };
            mesh.vertices = vertices;
            mesh.triangles = new[] { 0, 1, 2 };
            mesh.normals = new[] { Vector3.forward, Vector3.forward, Vector3.forward };
            mesh.RecalculateBounds();
            return mesh;
        }

        private T Track<T>(T obj) where T : Object {
            _objects.Add(obj);
            return obj;
        }

        private sealed class Rig {
            public SkinnedMeshRenderer Renderer;
        }
    }
}
