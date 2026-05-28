// ClippingFixer.Apply.cs
//
// Mesh cloning, skin-weight transfer, and result summaries for clipping fixes.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Clipping {

    internal static partial class ClippingFixer {

        internal static Result ApplyDestructive(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                IList<Issue> selectedIssues = null) {

            var result = new Result();
            if (AvatarIntentSessionState.IsAnyIntentSessionActive()) {
                result.ConfigurationError = true;
                result.Summary = "Stop the active preview/play/build mesh session before applying a destructive clipping fix.";
                return result;
            }

            settings = UnlimitedWarnings(settings);
            var initial = selectedIssues != null && selectedIssues.Count > 0
                ? selectedIssues.Where(i => i != null).ToList()
                : Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (initial.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            var meshInitial = WeightEditIssues(initial);
            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Pick a target renderer with a readable mesh.";
                return result;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Fix mesh clipping");
            try {
                Mesh clone = null;
                if (meshInitial.Count > 0) {
                    clone = CloneMeshAssetAndAssign(targetRenderer, targetRenderer.sharedMesh, result);
                    if (clone == null) {
                        result.ConfigurationError = true;
                        result.Summary = "Could not create an editable mesh asset.";
                        Undo.RevertAllInCurrentGroup();
                        return result;
                    }

                    result.MeshesCloned = 1;
                    result.RenderersTouched = 1;
                    if (selectedIssues != null && selectedIssues.Count > 0) {
                        int reweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, meshInitial, useUndo: true);
                        result.VerticesReweighted = reweighted;
                        result.FixPasses = reweighted > 0 ? 1 : 0;
                    } else {
                        ApplyInitialAndFollowupPasses(
                            targetRenderer,
                            meshInitial,
                            result,
                            useUndo: true);
                    }
                }
                if (result.VerticesReweighted > 0 && clone != null) {
                    EditorUtility.SetDirty(clone);
                    EditorUtility.SetDirty(targetRenderer);
                }
                if (result.VerticesReweighted > 0 || result.MeshesCloned > 0) {
                    AssetDatabase.SaveAssets();
                }
                Undo.CollapseUndoOperations(undoGroup);
                result.Summary = BuildSummary(result);
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex);
                result.ConfigurationError = true;
                result.Summary = "Clipping fix failed. See the console for details.";
            }
            return result;
        }

        internal static Result ApplySelectedToCurrentMeshInPlace(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> issues,
                bool useUndo) {

            var result = new Result();
            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Pick a target renderer with a readable mesh.";
                return result;
            }
            if (issues == null || issues.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            result.IssuesFound = issues.Count(i => i != null);
            result.RenderersTouched = 1;
            result.VerticesReweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, WeightEditIssues(issues), useUndo);
            result.FixPasses = result.VerticesReweighted > 0 ? 1 : 0;
            result.Summary = BuildSummary(result);
            return result;
        }

        internal static Result ApplyNonDestructive(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                AvatarIntentSession session,
                IList<Issue> precomputedInitial = null) {

            var result = new Result();
            if (session == null) {
                result.ConfigurationError = true;
                result.Summary = "No mesh session was supplied.";
                return result;
            }

            settings = UnlimitedWarnings(settings);
            var initial = precomputedInitial != null
                ? precomputedInitial.Where(i => i != null).ToList()
                : Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (initial.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Target renderer has no mesh.";
                return result;
            }

            var meshInitial = WeightEditIssues(initial);
            if (meshInitial.Count > 0) {
                session.Capture(targetRenderer);
                var clone = UnityEngine.Object.Instantiate(targetRenderer.sharedMesh);
                clone.name = targetRenderer.sharedMesh.name + " (ClippingFixed)";
                clone.hideFlags = HideFlags.DontSave;
                session.Adopt(clone);
                targetRenderer.sharedMesh = clone;
                result.MeshesCloned = 1;
                result.RenderersTouched = 1;

                ApplyInitialAndFollowupPasses(
                    targetRenderer,
                    meshInitial,
                    result,
                    useUndo: false);
            }
            result.Summary = BuildSummary(result);
            return result;
        }

        internal static Result ApplyToCurrentMeshInPlace(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                bool useUndo) {

            var result = new Result();
            settings = UnlimitedWarnings(settings);
            var initial = Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (result.IssuesFound == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }
            var meshInitial = WeightEditIssues(initial);
            if (meshInitial.Count > 0) {
                result.RenderersTouched = 1;
                ApplyInitialAndFollowupPasses(
                    targetRenderer,
                    meshInitial,
                    result,
                    useUndo);
            }
            result.Summary = BuildSummary(result);
            return result;
        }

        private static void ApplyInitialAndFollowupPasses(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> meshInitial,
                Result result,
                bool useUndo) {

            if (meshInitial == null || meshInitial.Count == 0) return;

            int reweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, meshInitial, useUndo);
            result.VerticesReweighted += reweighted;
            if (reweighted > 0) result.FixPasses++;
        }

        private static int ApplyIssueWeightsToCurrentMesh(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> issues,
                bool useUndo) {

            if (targetRenderer == null || targetRenderer.sharedMesh == null) return 0;
            var mesh = targetRenderer.sharedMesh;
            if (mesh == null || !mesh.isReadable) return 0;
            var targetBones = targetRenderer.bones ?? Array.Empty<Transform>();
            if (targetBones.Length == 0) return 0;

            var bonesPerVertex = mesh.GetBonesPerVertex();
            if (bonesPerVertex.Length != mesh.vertexCount) return 0;

            var allWeights = mesh.GetAllBoneWeights();
            var editable = new List<BoneWeight1>[mesh.vertexCount];
            int cursor = 0;
            for (int v = 0; v < editable.Length; v++) {
                int count = bonesPerVertex[v];
                var list = new List<BoneWeight1>(count);
                for (int w = 0; w < count && cursor < allWeights.Length; w++) {
                    var bw = allWeights[cursor++];
                    if (bw.boneIndex >= 0 && bw.boneIndex < targetBones.Length && bw.weight > 0f) {
                        list.Add(bw);
                    }
                }
                editable[v] = NormalizeWeights(list);
            }

            var sourceCache = new Dictionary<SkinnedMeshRenderer, WeightSourceCache>();
            var changed = new bool[mesh.vertexCount];
            int reweighted = 0;
            foreach (var issue in issues) {
                if (issue == null) continue;
                if (!TryBuildReferenceWeights(issue, targetRenderer, targetBones, sourceCache, out var reference)) {
                    continue;
                }
                var affected = issue.AffectedVertexIndices;
                if (affected == null || affected.Length == 0) {
                    if (issue.VertexIndex >= 0) affected = new[] { issue.VertexIndex };
                    else continue;
                }
                foreach (int idx in affected) {
                    if (idx < 0 || idx >= editable.Length) continue;
                    if (AreSameWeights(editable[idx], reference)) continue;
                    editable[idx] = CloneWeights(reference);
                    if (!changed[idx]) {
                        changed[idx] = true;
                        reweighted++;
                    }
                }
            }

            if (reweighted == 0) return 0;
            WriteWeights(mesh, editable, useUndo);
            return reweighted;
        }

        private static bool TryBuildReferenceWeights(
                Issue issue,
                SkinnedMeshRenderer targetRenderer,
                Transform[] targetBones,
                Dictionary<SkinnedMeshRenderer, WeightSourceCache> sourceCache,
                out List<BoneWeight1> reference) {

            reference = null;
            if (issue == null || targetRenderer == null || targetBones == null) return false;
            var sourceRenderer = issue.ComparisonRenderer != null ? issue.ComparisonRenderer : issue.Renderer;
            if (sourceRenderer == null) sourceRenderer = targetRenderer;

            if (!sourceCache.TryGetValue(sourceRenderer, out var source)) {
                source = WeightSourceCache.Build(sourceRenderer, targetBones);
                sourceCache[sourceRenderer] = source;
            }
            if (source == null || source.Surface == null || source.Surface.Triangles.Count == 0) return false;

            Triangle tri;
            if (issue.ComparisonTriangleIndex >= 0 && issue.ComparisonTriangleIndex < source.Surface.Triangles.Count) {
                tri = source.Surface.Triangles[issue.ComparisonTriangleIndex];
            } else if (source.Surface.TryFindClosest(issue.NearestSurfacePosition, out var closest)) {
                tri = source.Surface.Triangles[closest.TriangleIndex];
            } else {
                return false;
            }

            var bary = Barycentric(issue.NearestSurfacePosition, tri);
            var weights = new Dictionary<int, float>();
            source.AddMappedVertexWeights(tri.AIndex, bary.x, weights);
            source.AddMappedVertexWeights(tri.BIndex, bary.y, weights);
            source.AddMappedVertexWeights(tri.CIndex, bary.z, weights);
            reference = NormalizeWeights(weights);
            return reference.Count > 0;
        }

        private static void WriteWeights(Mesh mesh, List<BoneWeight1>[] weightsByVertex, bool useUndo) {
            int totalWeights = 0;
            for (int v = 0; v < weightsByVertex.Length; v++) {
                totalWeights += Mathf.Min(weightsByVertex[v] != null ? weightsByVertex[v].Count : 0, 255);
            }

            var bonesPerVertex = new NativeArray<byte>(weightsByVertex.Length, Allocator.Temp);
            var weights = new NativeArray<BoneWeight1>(totalWeights, Allocator.Temp);
            try {
                int cursor = 0;
                for (int v = 0; v < weightsByVertex.Length; v++) {
                    var list = weightsByVertex[v] ?? new List<BoneWeight1>();
                    int count = Mathf.Min(list.Count, 255);
                    bonesPerVertex[v] = (byte)count;
                    for (int i = 0; i < count; i++) {
                        weights[cursor++] = list[i];
                    }
                }

                if (useUndo) Undo.RegisterCompleteObjectUndo(mesh, "Fix clipping weights");
                mesh.SetBoneWeights(bonesPerVertex, weights);
            } finally {
                weights.Dispose();
                bonesPerVertex.Dispose();
            }
            if (useUndo) EditorUtility.SetDirty(mesh);
        }

        private static Vector3 Barycentric(Vector3 point, Triangle tri) {
            var v0 = tri.B - tri.A;
            var v1 = tri.C - tri.A;
            var v2 = point - tri.A;
            float d00 = Vector3.Dot(v0, v0);
            float d01 = Vector3.Dot(v0, v1);
            float d11 = Vector3.Dot(v1, v1);
            float d20 = Vector3.Dot(v2, v0);
            float d21 = Vector3.Dot(v2, v1);
            float denom = d00 * d11 - d01 * d01;
            if (Mathf.Abs(denom) < 0.0000001f) return new Vector3(1f / 3f, 1f / 3f, 1f / 3f);

            float inv = 1f / denom;
            float y = (d11 * d20 - d01 * d21) * inv;
            float z = (d00 * d21 - d01 * d20) * inv;
            float x = 1f - y - z;
            x = Mathf.Max(0f, x);
            y = Mathf.Max(0f, y);
            z = Mathf.Max(0f, z);
            float sum = x + y + z;
            if (sum <= 0.000001f) return new Vector3(1f / 3f, 1f / 3f, 1f / 3f);
            return new Vector3(x / sum, y / sum, z / sum);
        }

        private static List<BoneWeight1> NormalizeWeights(Dictionary<int, float> weights) {
            var list = new List<BoneWeight1>();
            if (weights == null) return list;
            foreach (var kv in weights) {
                if (kv.Key < 0 || kv.Value <= 0f) continue;
                list.Add(new BoneWeight1 { boneIndex = kv.Key, weight = kv.Value });
            }
            return NormalizeWeights(list);
        }

        private static List<BoneWeight1> NormalizeWeights(List<BoneWeight1> weights) {
            var list = weights != null ? new List<BoneWeight1>(weights.Count) : new List<BoneWeight1>();
            if (weights != null) {
                foreach (var bw in weights) {
                    if (bw.boneIndex < 0 || bw.weight <= 0f) continue;
                    int existing = list.FindIndex(i => i.boneIndex == bw.boneIndex);
                    if (existing >= 0) {
                        var current = list[existing];
                        current.weight += bw.weight;
                        list[existing] = current;
                    } else {
                        list.Add(bw);
                    }
                }
            }

            float total = 0f;
            for (int i = 0; i < list.Count; i++) total += list[i].weight;
            if (total <= 0.000001f) {
                list.Clear();
                return list;
            }

            float scale = 1f / total;
            for (int i = 0; i < list.Count; i++) {
                var bw = list[i];
                bw.weight *= scale;
                list[i] = bw;
            }
            list.Sort((a, b) => b.weight.CompareTo(a.weight));
            return list;
        }

        private static List<BoneWeight1> CloneWeights(List<BoneWeight1> weights) {
            return weights != null ? new List<BoneWeight1>(weights) : new List<BoneWeight1>();
        }

        private static bool AreSameWeights(List<BoneWeight1> a, List<BoneWeight1> b) {
            if (a == null || b == null) return a == b;
            if (a.Count != b.Count) return false;
            for (int i = 0; i < a.Count; i++) {
                if (a[i].boneIndex != b[i].boneIndex) return false;
                if (Mathf.Abs(a[i].weight - b[i].weight) > 0.0001f) return false;
            }
            return true;
        }

        private static int FindBoneIndex(Transform[] bones, Transform target) {
            if (bones == null || target == null) return -1;
            for (int i = 0; i < bones.Length; i++) {
                if (bones[i] == target) return i;
            }
            return -1;
        }

        private static List<Issue> WeightEditIssues(IList<Issue> issues) {
            if (issues == null) return new List<Issue>();
            return issues
                .Where(i => i != null && (i.VertexIndex >= 0 || (i.AffectedVertexIndices != null && i.AffectedVertexIndices.Length > 0)))
                .ToList();
        }

        private static Settings UnlimitedWarnings(Settings source) {
            source = source ?? new Settings();
            return new Settings {
                Animator = source.Animator,
                CheckSelf = source.CheckSelf,
                IncludePhysBoneMotion = source.IncludePhysBoneMotion,
                InsideTolerance = source.InsideTolerance,
                SurfacePadding = source.SurfacePadding,
                PhysBoneWeightFloor = source.PhysBoneWeightFloor,
                PhysBoneClearanceMargin = source.PhysBoneClearanceMargin,
                MaxWarnings = 0,
                MaxIssuesPerPhysBone = source.MaxIssuesPerPhysBone,
            };
        }

        private static Mesh CloneMeshAssetAndAssign(
                SkinnedMeshRenderer renderer,
                Mesh sharedMesh,
                Result result) {

            if (renderer == null || sharedMesh == null) return null;
            EnsureFolder(GeneratedFolder);
            var clone = UnityEngine.Object.Instantiate(sharedMesh);
            clone.name = sharedMesh.name + " (ClippingFixed)";
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedFolder}/{SanitizeFileName(renderer.gameObject.name + "_" + sharedMesh.name + "_ClippingFixed")}.asset");
            AssetDatabase.CreateAsset(clone, path);
            Undo.RegisterCreatedObjectUndo(clone, "Create clipping-fixed mesh");
            Undo.RecordObject(renderer, "Assign clipping-fixed mesh");
            renderer.sharedMesh = clone;
            EditorUtility.SetDirty(renderer);
            result.ClonedPaths.Add(path);
            return clone;
        }

        private static void EnsureFolder(string folder) {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            if (parts.Length < 2 || parts[0] != "Assets") return;
            var current = "Assets";
            for (int i = 1; i < parts.Length; i++) {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name)) return "mesh";
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Replace("(", "").Replace(")", "").Trim();
            return string.IsNullOrEmpty(name) ? "mesh" : name;
        }

        private static string BuildSummary(Result result) {
            if (result == null) return "";
            var parts = new List<string>();
            parts.Add($"{result.VerticesReweighted} vertices reweighted");
            parts.Add($"{result.FixPasses} pass(es)");
            if (result.MeshesCloned > 0) parts.Add($"{result.MeshesCloned} mesh(es) cloned");
            if (result.UnreadableRenderers > 0) parts.Add($"{result.UnreadableRenderers} renderer(s) skipped (mesh not readable)");
            return string.Join(", ", parts) + ".";
        }
    }
}
