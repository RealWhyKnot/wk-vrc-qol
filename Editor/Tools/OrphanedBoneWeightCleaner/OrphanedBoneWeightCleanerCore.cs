// OrphanedBoneWeightCleanerCore.cs
//
// Mesh cleanup after bones were deleted or detached from a renderer's
// bones[] list. The default path keeps geometry when a vertex still has at
// least one valid influence, dropping invalid slots and renormalizing the
// rest.

using System;
using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal enum OrphanedBoneCleanupMode {
        DropInvalidWeights,
        DeleteVerticesWithInvalidWeights,
    }

    internal static class OrphanedBoneWeightCleanerCore {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal sealed class Result {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> ClonedPaths = new List<string>();
            public int RenderersScanned;
            public int RenderersTouched;
            public int MeshesCreated;
            public int InvalidWeightSlots;
            public int VerticesDeleted;
            public int TrianglesDeleted;
            public int UnreadableRenderers;
            public bool ConfigurationError;
        }

        internal sealed class RendererPlan {
            public bool HasChanges;
            public bool ConfigurationError;
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<int[]> KeptSubmeshTriangles = new List<int[]>();
            public List<BoneWeight1>[] CleanedWeights;
            public int InvalidWeightSlots;
            public int VerticesDeleted;
            public int TrianglesDeleted;
        }

        internal static Result Apply(
                Animator animator,
                SkinnedMeshRenderer singleRenderer,
                bool wholeAvatar,
                IList<Transform> explicitRemovedBones,
                OrphanedBoneCleanupMode mode,
                bool growDeletionAcrossConnectedTriangles) {

            var result = new Result();
            var renderers = ResolveRenderers(animator, singleRenderer, wholeAvatar);
            if (renderers.Count == 0) {
                result.Summary = wholeAvatar
                    ? "Pick an Animator with SkinnedMeshRenderers underneath."
                    : "Pick a SkinnedMeshRenderer first.";
                result.ConfigurationError = true;
                return result;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: clean orphaned bone weights");
            try {
                foreach (var renderer in renderers) {
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    result.RenderersScanned++;
                    var mesh = renderer.sharedMesh;
                    if (!mesh.isReadable) {
                        result.UnreadableRenderers++;
                        result.Detail.Add($"SKIP {RendererPath(renderer)} -- mesh not readable. Enable Read/Write on the model importer.");
                        continue;
                    }

                    var plan = BuildPlan(renderer, explicitRemovedBones, mode, growDeletionAcrossConnectedTriangles);
                    result.Detail.AddRange(plan.Detail);
                    if (plan.ConfigurationError) continue;
                    result.InvalidWeightSlots += plan.InvalidWeightSlots;
                    result.VerticesDeleted += plan.VerticesDeleted;
                    result.TrianglesDeleted += plan.TrianglesDeleted;
                    if (!plan.HasChanges) continue;

                    var compacted = MeshCompactor.BuildKeepingTriangles(
                        mesh,
                        plan.KeptSubmeshTriangles,
                        mesh.name + " (OrphanCleaned)");
                    if (compacted.KeptVertexCount == 0) {
                        result.Detail.Add($"SKIP {RendererPath(renderer)} -- cleanup would leave no vertices.");
                        continue;
                    }

                    RewriteWeights(compacted.Mesh, compacted.KeptOldVertexIndices, plan.CleanedWeights);
                    var write = FbxMeshUtility.WriteNewMeshAsset(
                        renderer,
                        compacted.Mesh,
                        "(OrphanCleaned)",
                        "Create orphan-cleaned mesh",
                        GeneratedFolder);
                    if (!string.IsNullOrEmpty(write.ClonedPath)) result.ClonedPaths.Add(write.ClonedPath);
                    result.RenderersTouched++;
                    result.MeshesCreated++;
                    result.Detail.Add($"OK   {RendererPath(renderer)} -- deleted {plan.VerticesDeleted} vert(s), {plan.TrianglesDeleted} triangle(s), dropped {plan.InvalidWeightSlots} invalid weight slot(s).");
                }

                if (result.MeshesCreated > 0) AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                result.Summary = BuildSummary(result);
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex, "Orphaned Bone Weight Cleaner");
                result.Summary = "Cleanup failed -- nothing was changed. See console for details.";
            }
            return result;
        }

        internal static RendererPlan BuildPlan(
                SkinnedMeshRenderer renderer,
                IList<Transform> explicitRemovedBones,
                OrphanedBoneCleanupMode mode,
                bool growDeletionAcrossConnectedTriangles) {

            var plan = new RendererPlan();
            if (renderer == null || renderer.sharedMesh == null) {
                plan.ConfigurationError = true;
                plan.Summary = "Renderer or mesh is missing.";
                return plan;
            }

            var mesh = renderer.sharedMesh;
            var bones = renderer.bones;
            if (bones == null) bones = Array.Empty<Transform>();
            var removedIds = BuildRemovedIdSet(explicitRemovedBones);
            var bpv = mesh.GetBonesPerVertex();
            var weights = mesh.GetAllBoneWeights();
            if (!bpv.IsCreated || bpv.Length != mesh.vertexCount) {
                plan.ConfigurationError = true;
                plan.Summary = "Mesh has no readable modern bone-weight buffer.";
                return plan;
            }

            var starts = BuildWeightStarts(bpv);
            var deleteVertex = new bool[mesh.vertexCount];
            plan.CleanedWeights = new List<BoneWeight1>[mesh.vertexCount];

            for (int v = 0; v < mesh.vertexCount; v++) {
                int count = bpv[v];
                var valid = new List<BoneWeight1>(count);
                bool sawInvalid = false;
                for (int k = 0; k < count; k++) {
                    var bw = weights[starts[v] + k];
                    if (IsInvalid(renderer, bones, bw, removedIds)) {
                        if (bw.weight > 0f) {
                            sawInvalid = true;
                            plan.InvalidWeightSlots++;
                        }
                        continue;
                    }
                    if (bw.weight > 0f) valid.Add(bw);
                }

                if ((mode == OrphanedBoneCleanupMode.DeleteVerticesWithInvalidWeights && sawInvalid)
                        || valid.Count == 0) {
                    deleteVertex[v] = true;
                    continue;
                }

                Renormalize(valid);
                plan.CleanedWeights[v] = valid;
            }

            if (growDeletionAcrossConnectedTriangles) GrowDeletion(mesh, deleteVertex);
            int deletedBeforeTriangles = 0;
            for (int i = 0; i < deleteVertex.Length; i++) if (deleteVertex[i]) deletedBeforeTriangles++;
            plan.VerticesDeleted = deletedBeforeTriangles;

            int originalTriangles = 0;
            int keptTriangles = 0;
            for (int s = 0; s < mesh.subMeshCount; s++) {
                var src = mesh.GetTriangles(s);
                originalTriangles += src.Length / 3;
                var kept = new List<int>(src.Length);
                for (int i = 0; i + 2 < src.Length; i += 3) {
                    int a = src[i];
                    int b = src[i + 1];
                    int c = src[i + 2];
                    if (IsDeleted(deleteVertex, a) || IsDeleted(deleteVertex, b) || IsDeleted(deleteVertex, c)) continue;
                    kept.Add(a);
                    kept.Add(b);
                    kept.Add(c);
                }
                keptTriangles += kept.Count / 3;
                plan.KeptSubmeshTriangles.Add(kept.ToArray());
            }
            plan.TrianglesDeleted = originalTriangles - keptTriangles;
            plan.HasChanges = plan.InvalidWeightSlots > 0 || plan.VerticesDeleted > 0 || plan.TrianglesDeleted > 0;
            if (!plan.HasChanges) {
                plan.Detail.Add($"OK   {RendererPath(renderer)} -- no orphaned or removed-bone weights found.");
            }
            return plan;
        }

        private static List<SkinnedMeshRenderer> ResolveRenderers(
                Animator animator,
                SkinnedMeshRenderer singleRenderer,
                bool wholeAvatar) {
            if (!wholeAvatar) {
                return singleRenderer != null
                    ? new List<SkinnedMeshRenderer> { singleRenderer }
                    : new List<SkinnedMeshRenderer>();
            }
            if (animator == null) return new List<SkinnedMeshRenderer>();
            return animator.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r != null)
                .ToList();
        }

        private static HashSet<int> BuildRemovedIdSet(IList<Transform> explicitRemovedBones) {
            var set = new HashSet<int>();
            if (explicitRemovedBones == null) return set;
            foreach (var t in explicitRemovedBones) {
                if (t != null) set.Add(t.GetInstanceID());
            }
            return set;
        }

        private static bool IsInvalid(
                SkinnedMeshRenderer renderer,
                Transform[] bones,
                BoneWeight1 weight,
                HashSet<int> removedIds) {
            int idx = weight.boneIndex;
            if (idx < 0 || idx >= bones.Length) return true;
            var bone = bones[idx];
            if (bone == null) return true;
            return removedIds != null && removedIds.Contains(bone.GetInstanceID());
        }

        private static int[] BuildWeightStarts(NativeArray<byte> bonesPerVertex) {
            var starts = new int[bonesPerVertex.Length];
            int cursor = 0;
            for (int i = 0; i < bonesPerVertex.Length; i++) {
                starts[i] = cursor;
                cursor += bonesPerVertex[i];
            }
            return starts;
        }

        private static void Renormalize(List<BoneWeight1> weights) {
            float sum = 0f;
            for (int i = 0; i < weights.Count; i++) sum += weights[i].weight;
            if (sum <= 1e-6f) return;
            for (int i = 0; i < weights.Count; i++) {
                var w = weights[i];
                w.weight /= sum;
                weights[i] = w;
            }
            weights.Sort((a, b) => b.weight.CompareTo(a.weight));
        }

        private static void GrowDeletion(Mesh mesh, bool[] deleteVertex) {
            bool changed;
            do {
                changed = false;
                for (int s = 0; s < mesh.subMeshCount; s++) {
                    var tris = mesh.GetTriangles(s);
                    for (int i = 0; i + 2 < tris.Length; i += 3) {
                        int a = tris[i];
                        int b = tris[i + 1];
                        int c = tris[i + 2];
                        bool any = IsDeleted(deleteVertex, a) || IsDeleted(deleteVertex, b) || IsDeleted(deleteVertex, c);
                        bool all = IsDeleted(deleteVertex, a) && IsDeleted(deleteVertex, b) && IsDeleted(deleteVertex, c);
                        if (!any || all) continue;
                        if (!IsDeleted(deleteVertex, a)) { deleteVertex[a] = true; changed = true; }
                        if (!IsDeleted(deleteVertex, b)) { deleteVertex[b] = true; changed = true; }
                        if (!IsDeleted(deleteVertex, c)) { deleteVertex[c] = true; changed = true; }
                    }
                }
            } while (changed);
        }

        private static bool IsDeleted(bool[] deleteVertex, int index) {
            return index < 0 || index >= deleteVertex.Length || deleteVertex[index];
        }

        private static void RewriteWeights(
                Mesh mesh,
                int[] keptOldVertexIndices,
                List<BoneWeight1>[] cleanedWeights) {
            var bpv = new NativeArray<byte>(keptOldVertexIndices.Length, Allocator.Temp);
            var output = new List<BoneWeight1>();
            try {
                for (int i = 0; i < keptOldVertexIndices.Length; i++) {
                    int old = keptOldVertexIndices[i];
                    var list = old >= 0 && old < cleanedWeights.Length ? cleanedWeights[old] : null;
                    int count = list != null ? list.Count : 0;
                    bpv[i] = (byte)count;
                    if (list == null) continue;
                    for (int k = 0; k < list.Count; k++) output.Add(list[k]);
                }
                using (var weights = new NativeArray<BoneWeight1>(output.ToArray(), Allocator.Temp)) {
                    mesh.SetBoneWeights(bpv, weights);
                }
            } finally {
                bpv.Dispose();
            }
        }

        private static string BuildSummary(Result result) {
            if (result.RenderersTouched == 0 && result.UnreadableRenderers == 0) {
                return "No orphaned bone weights found.";
            }
            var parts = new List<string> {
                $"{result.InvalidWeightSlots} invalid weight slot(s)",
                $"{result.VerticesDeleted} vertices deleted",
                $"{result.TrianglesDeleted} triangle(s) deleted",
                $"{result.RenderersTouched} renderer(s) updated",
            };
            if (result.UnreadableRenderers > 0) parts.Add($"{result.UnreadableRenderers} renderer(s) skipped");
            return string.Join(", ", parts) + ".";
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            return renderer == null ? "(null)" : PathUtility.GetGameObjectPath(renderer.gameObject);
        }
    }
}
