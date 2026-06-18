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
                        int reweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, meshInitial, useUndo: true, settings);
                        result.VerticesReweighted = reweighted;
                        result.FixPasses = reweighted > 0 ? 1 : 0;
                    } else {
                        ApplyInitialAndFollowupPasses(
                            targetRenderer,
                            meshInitial,
                            result,
                            useUndo: true,
                            settings);
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
            result.VerticesReweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, WeightEditIssues(issues), useUndo, null);
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
                    useUndo: false,
                    settings);
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
                    useUndo,
                    settings);
            }
            result.Summary = BuildSummary(result);
            return result;
        }

        private static void ApplyInitialAndFollowupPasses(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> meshInitial,
                Result result,
                bool useUndo,
                Settings settings) {

            if (meshInitial == null || meshInitial.Count == 0) return;

            int reweighted = ApplyIssueWeightsToCurrentMesh(targetRenderer, meshInitial, useUndo, settings);
            result.VerticesReweighted += reweighted;
            if (reweighted > 0) result.FixPasses++;
        }

        private static int ApplyIssueWeightsToCurrentMesh(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> issues,
                bool useUndo,
                Settings settings) {

            settings = settings ?? new Settings();
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
                if (issue.Kind == IssueKind.PhysBoneMotion) continue;
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
            reweighted += ApplyPhysBoneMotionWeights(targetRenderer, targetBones, editable, changed, issues, settings);

            if (reweighted == 0) return 0;
            WriteWeights(mesh, editable, useUndo);
            return reweighted;
        }

        private static int ApplyPhysBoneMotionWeights(
                SkinnedMeshRenderer targetRenderer,
                Transform[] targetBones,
                List<BoneWeight1>[] editable,
                bool[] changed,
                IList<Issue> issues,
                Settings settings) {

            if (targetRenderer == null || targetBones == null || editable == null || changed == null) return 0;
            if (issues == null || issues.Count == 0) return 0;
            float pinStrength = Mathf.Clamp01(settings != null ? settings.PhysBoneMotionPinStrength : 0.65f);
            if (pinStrength <= 0.0001f) return 0;
            float brushRadius = Mathf.Max(0f, settings != null ? settings.PhysBoneMotionBrushRadius : 0.035f);
            float weightFloor = Mathf.Max(0f, settings != null ? settings.PhysBoneWeightFloor : 0.03f);

            RendererSnapshot snapshot = null;
            if (brushRadius > 0.0001f) snapshot = RendererSnapshot.Build(targetRenderer);
            var edits = new Dictionary<MotionWeightKey, float>();

            foreach (var issue in issues) {
                if (issue == null || issue.Kind != IssueKind.PhysBoneMotion) continue;
                if (!TryResolveMotionPin(issue, targetBones, out var root, out int destinationBone)) continue;

                AddAffectedMotionPins(edits, issue, editable.Length, root, destinationBone, pinStrength);
                if (brushRadius <= 0.0001f || snapshot == null || snapshot.WorldVertices == null) continue;

                Vector3 center = MotionBrushCenter(issue, snapshot);
                if (!IsFinite(center)) continue;
                float radiusSqr = brushRadius * brushRadius;
                for (int v = 0; v < snapshot.WorldVertices.Length && v < editable.Length; v++) {
                    var weights = editable[v];
                    if (!HasMotionWeight(weights, targetBones, root, weightFloor)) continue;
                    float distSqr = (snapshot.WorldVertices[v] - center).sqrMagnitude;
                    if (distSqr > radiusSqr) continue;
                    float dist = Mathf.Sqrt(distSqr);
                    float falloff = 1f - Mathf.SmoothStep(0f, 1f, brushRadius > 0f ? dist / brushRadius : 1f);
                    AddMotionPin(edits, v, root, destinationBone, pinStrength * falloff);
                }
            }

            if (edits.Count == 0) return 0;

            int reweighted = 0;
            foreach (var edit in edits) {
                int vertex = edit.Key.VertexIndex;
                if (vertex < 0 || vertex >= editable.Length) continue;
                if (TryApplyMotionPin(
                        editable[vertex],
                        targetBones,
                        edit.Key.Root,
                        edit.Key.DestinationBone,
                        edit.Value,
                        out var next) &&
                    !AreSameWeights(editable[vertex], next)) {
                    editable[vertex] = next;
                    if (!changed[vertex]) {
                        changed[vertex] = true;
                        reweighted++;
                    }
                }
            }

            return reweighted;
        }

        private static void AddAffectedMotionPins(
                Dictionary<MotionWeightKey, float> edits,
                Issue issue,
                int vertexCount,
                Transform root,
                int destinationBone,
                float pinStrength) {

            var affected = issue.AffectedVertexIndices;
            if (affected == null || affected.Length == 0) {
                if (issue.VertexIndex >= 0) affected = new[] { issue.VertexIndex };
                else return;
            }

            foreach (int vertex in affected) {
                if (vertex < 0 || vertex >= vertexCount) continue;
                AddMotionPin(edits, vertex, root, destinationBone, pinStrength);
            }
        }

        private static void AddMotionPin(
                Dictionary<MotionWeightKey, float> edits,
                int vertex,
                Transform root,
                int destinationBone,
                float fraction) {

            if (edits == null || vertex < 0 || root == null || destinationBone < 0) return;
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0.0001f) return;
            var key = new MotionWeightKey(vertex, root, destinationBone);
            if (!edits.TryGetValue(key, out float current) || fraction > current) {
                edits[key] = fraction;
            }
        }

        private static bool TryResolveMotionPin(
                Issue issue,
                Transform[] targetBones,
                out Transform root,
                out int destinationBone) {

            root = null;
            destinationBone = -1;
            if (issue == null || targetBones == null || targetBones.Length == 0) return false;
            root = issue.PhysBoneRoot != null ? issue.PhysBoneRoot : issue.DrivenBone;
            if (root == null) return false;

            var ancestor = root.parent;
            while (ancestor != null) {
                int index = FindBoneIndex(targetBones, ancestor);
                if (index >= 0 && !BoneIsInSubtree(ancestor, root)) {
                    destinationBone = index;
                    return true;
                }
                ancestor = ancestor.parent;
            }
            return false;
        }

        private static Vector3 MotionBrushCenter(Issue issue, RendererSnapshot snapshot) {
            if (issue != null &&
                snapshot != null &&
                snapshot.WorldVertices != null &&
                issue.VertexIndex >= 0 &&
                issue.VertexIndex < snapshot.WorldVertices.Length) {
                return snapshot.WorldVertices[issue.VertexIndex];
            }
            return issue != null ? issue.WorldPosition : Vector3.zero;
        }

        private static bool TryApplyMotionPin(
                List<BoneWeight1> source,
                Transform[] targetBones,
                Transform root,
                int destinationBone,
                float fraction,
                out List<BoneWeight1> result) {

            result = source;
            if (source == null || targetBones == null || root == null || destinationBone < 0) return false;
            fraction = Mathf.Clamp01(fraction);
            if (fraction <= 0.0001f) return false;

            var weights = new Dictionary<int, float>();
            float moved = 0f;
            foreach (var bw in source) {
                if (bw.boneIndex < 0 || bw.boneIndex >= targetBones.Length || bw.weight <= 0f) continue;
                var bone = targetBones[bw.boneIndex];
                bool moving = BoneIsInSubtree(bone, root);
                float kept = moving ? bw.weight * (1f - fraction) : bw.weight;
                if (kept > 0.000001f) AddWeight(weights, bw.boneIndex, kept);
                if (moving) moved += bw.weight * fraction;
            }

            if (moved <= 0.000001f) return false;
            AddWeight(weights, destinationBone, moved);
            result = NormalizeWeights(weights);
            return result.Count > 0;
        }

        private static bool HasMotionWeight(
                List<BoneWeight1> weights,
                Transform[] targetBones,
                Transform root,
                float weightFloor) {

            if (weights == null || targetBones == null || root == null) return false;
            foreach (var bw in weights) {
                if (bw.boneIndex < 0 || bw.boneIndex >= targetBones.Length) continue;
                if (bw.weight < weightFloor) continue;
                if (BoneIsInSubtree(targetBones[bw.boneIndex], root)) return true;
            }
            return false;
        }

        private static bool BoneIsInSubtree(Transform bone, Transform root) {
            if (bone == null || root == null) return false;
            return bone == root || bone.IsChildOf(root);
        }

        private static void AddWeight(Dictionary<int, float> weights, int boneIndex, float weight) {
            if (weights == null || boneIndex < 0 || weight <= 0f) return;
            if (weights.TryGetValue(boneIndex, out float current)) weights[boneIndex] = current + weight;
            else weights[boneIndex] = weight;
        }

        private static bool IsFinite(Vector3 value) {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        private static bool IsFinite(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private readonly struct MotionWeightKey : IEquatable<MotionWeightKey> {
            public readonly int VertexIndex;
            public readonly Transform Root;
            public readonly int DestinationBone;

            public MotionWeightKey(int vertexIndex, Transform root, int destinationBone) {
                VertexIndex = vertexIndex;
                Root = root;
                DestinationBone = destinationBone;
            }

            public bool Equals(MotionWeightKey other) {
                return VertexIndex == other.VertexIndex &&
                       Root == other.Root &&
                       DestinationBone == other.DestinationBone;
            }

            public override bool Equals(object obj) {
                return obj is MotionWeightKey other && Equals(other);
            }

            public override int GetHashCode() {
                unchecked {
                    int hash = VertexIndex;
                    hash = (hash * 397) ^ (Root != null ? Root.GetHashCode() : 0);
                    hash = (hash * 397) ^ DestinationBone;
                    return hash;
                }
            }
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
                PhysBoneMotionPinStrength = source.PhysBoneMotionPinStrength,
                PhysBoneMotionBrushRadius = source.PhysBoneMotionBrushRadius,
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
