// BoneScaleFollowCore.cs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal enum BoneScaleFollowTargetMode {
        EntireAvatar,
        Manual,
    }

    [Serializable]
    internal sealed class BoneScaleFollowRow {
        public bool Enabled = true;
        public Transform Bone;
        public Vector3 BaseScale = Vector3.one;
        public Vector3 TargetScale = new Vector3(1.15f, 1.15f, 1.15f);
    }

    internal static class BoneScaleFollowCore {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";
        internal const string DefaultBlendShapeName = "BoneScaleFollow";

        internal sealed class Options {
            public SkinnedMeshRenderer SourceRenderer;
            public string OutputBlendShapeName = DefaultBlendShapeName;
            public IList<BoneScaleFollowRow> BoneRows;
            public IList<SkinnedMeshRenderer> TargetRenderers;
            public ISet<int> IncludedRendererInstanceIds;
            public float MaxDistance = 0.03f;
            public float DeltaEpsilon = 0.0001f;
            public BlendShapeTransferCorrespondenceMode CorrespondenceMode = BlendShapeTransferCorrespondenceMode.ClosestPoint;
            public float RayFrontalDistance = 0.08f;
            public float RayRearDistance = 0.08f;
            public float NormalAngleLimitDegrees = 75f;
            public bool RejectBackfaces;
            public bool OwnResponseCompensation = true;
            public bool UseDistanceFalloff;
            public float FalloffStartDistance = 0.02f;
        }

        internal sealed class PreviewResult {
            public string Summary = "";
            public readonly List<SurfaceDeltaTargetResult> Targets = new List<SurfaceDeltaTargetResult>();
            public bool ConfigurationError;
            public int ProcessedCount => Targets.Count(t => t.Processed);
            public int SkippedCount => Targets.Count(t => t.Skipped);
        }

        internal sealed class ApplyResult {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> CreatedPaths = new List<string>();
            public int RenderersUpdated;
            public int RenderersSkipped;
            public bool ConfigurationError;
        }

        internal static PreviewResult Preview(Options options) {
            var result = new PreviewResult();
            var rows = ActiveRows(options);
            string error = ValidateOptions(options, rows);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            SurfaceDeltaField source = BuildSourceField(options, rows, out error);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            var transferOptions = BuildTransferOptions(options);
            var targets = DistinctTargets(options.TargetRenderers);
            foreach (var target in targets) {
                Vector3[] ownBias = options.OwnResponseCompensation
                    ? BuildRendererOwnWorldDelta(target, rows)
                    : null;
                result.Targets.Add(SurfaceDeltaTransfer.ComputeTarget(transferOptions, source, target, ownBias));
            }
            result.Summary = $"{result.ProcessedCount} renderer(s) would be processed, {result.SkippedCount} skipped.";
            return result;
        }

        internal static ApplyResult Apply(Options options) {
            var apply = new ApplyResult();
            var preview = Preview(options);
            if (preview.ConfigurationError) {
                apply.ConfigurationError = true;
                apply.Summary = preview.Summary;
                return apply;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: bone scale follow");
            try {
                foreach (var target in preview.Targets) {
                    if (!target.Processed || target.Renderer == null) {
                        apply.RenderersSkipped++;
                        apply.Detail.Add($"SKIP {RendererPath(target.Renderer)} -- {target.Reason}");
                        continue;
                    }
                    if (options.IncludedRendererInstanceIds != null
                            && !options.IncludedRendererInstanceIds.Contains(target.Renderer.GetInstanceID())) {
                        apply.RenderersSkipped++;
                        apply.Detail.Add($"SKIP {RendererPath(target.Renderer)} -- unchecked in preview.");
                        continue;
                    }

                    var originalMesh = target.Renderer.sharedMesh;
                    var weightsByName = BlendShapeUtility.CaptureWeights(target.Renderer, originalMesh);
                    var editable = UnityEngine.Object.Instantiate(originalMesh);
                    editable.name = originalMesh.name + " (BoneScaleFollow)";
                    if (!BlendShapeUtility.AddOrReplaceFrames(editable, ResolveOutputName(options), target.Frames)) {
                        UnityEngine.Object.DestroyImmediate(editable);
                        apply.Detail.Add($"SKIP {RendererPath(target.Renderer)} -- blendshape frame data did not match vertex count.");
                        apply.RenderersSkipped++;
                        continue;
                    }

                    var write = FbxMeshUtility.WriteNewMeshAsset(
                        target.Renderer,
                        editable,
                        "(BoneScaleFollow)",
                        "Create bone scale follow mesh",
                        GeneratedFolder);
                    BlendShapeUtility.RestoreWeights(target.Renderer, editable, weightsByName);
                    if (!string.IsNullOrEmpty(write.ClonedPath)) apply.CreatedPaths.Add(write.ClonedPath);
                    apply.RenderersUpdated++;
                    apply.Detail.Add($"OK   {RendererPath(target.Renderer)} -- affected {target.AffectedVertices}/{target.TotalVertices} vertices.");
                }

                if (apply.RenderersUpdated > 0) AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);
                apply.Summary = $"{apply.RenderersUpdated} renderer(s) updated, {apply.RenderersSkipped} skipped.";
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex, "Bone Scale Follow");
                apply.Summary = "Bone scale follow failed -- nothing was changed. See console for details.";
            }
            return apply;
        }

        internal static List<SkinnedMeshRenderer> ResolveWholeAvatarTargets(SkinnedMeshRenderer sourceRenderer) {
            var root = AvatarUtility.FindAvatarRoot(sourceRenderer);
            return AvatarUtility.GetSkinnedMeshes(root).ToList();
        }

        internal static SurfaceDeltaField BuildSourceField(
                Options options,
                IList<BoneScaleFollowRow> activeRows,
                out string error) {

            error = null;
            if (options == null || options.SourceRenderer == null) {
                error = "Pick a source renderer.";
                return null;
            }
            var mesh = options.SourceRenderer.sharedMesh;
            if (mesh == null) {
                error = "Source renderer has no mesh.";
                return null;
            }

            var baseBoneWorld = BuildBoneWorldMatrices(options.SourceRenderer, activeRows, targetScale: false);
            var targetBoneWorld = BuildBoneWorldMatrices(options.SourceRenderer, activeRows, targetScale: true);
            var baseSkin = SkinDeform.ComputeSkinMatrices(options.SourceRenderer, mesh, baseBoneWorld);
            var targetSkin = SkinDeform.ComputeSkinMatrices(options.SourceRenderer, mesh, targetBoneWorld);
            var baseWorld = SkinDeform.TransformPoints(baseSkin, mesh.vertices);
            var targetWorld = SkinDeform.TransformPoints(targetSkin, mesh.vertices);
            var deltas = new Vector3[Mathf.Min(baseWorld.Length, targetWorld.Length)];
            for (int i = 0; i < deltas.Length; i++) deltas[i] = targetWorld[i] - baseWorld[i];

            return SurfaceDeltaField.Build(
                options.SourceRenderer,
                baseWorld,
                new[] { deltas },
                new[] { 100f },
                mesh.triangles,
                options.DeltaEpsilon,
                "Bone scale rows did not move the source mesh.");
        }

        internal static Matrix4x4[] BuildBoneWorldMatrices(
                SkinnedMeshRenderer renderer,
                IList<BoneScaleFollowRow> activeRows,
                bool targetScale) {

            if (renderer == null || renderer.bones == null) return new Matrix4x4[0];
            var overrides = new Dictionary<Transform, Vector3>();
            if (activeRows != null) {
                for (int i = 0; i < activeRows.Count; i++) {
                    var row = activeRows[i];
                    if (row == null || row.Bone == null) continue;
                    overrides[row.Bone] = targetScale ? row.TargetScale : row.BaseScale;
                }
            }

            var cache = new Dictionary<Transform, Matrix4x4>();
            var output = new Matrix4x4[renderer.bones.Length];
            for (int i = 0; i < renderer.bones.Length; i++) {
                output[i] = ComputeWorldMatrix(renderer.bones[i], overrides, cache);
            }
            return output;
        }

        internal static Vector3[] BuildRendererOwnWorldDelta(
                SkinnedMeshRenderer renderer,
                IList<BoneScaleFollowRow> activeRows) {

            if (renderer == null || renderer.sharedMesh == null) return null;
            var mesh = renderer.sharedMesh;
            var baseBoneWorld = BuildBoneWorldMatrices(renderer, activeRows, targetScale: false);
            var targetBoneWorld = BuildBoneWorldMatrices(renderer, activeRows, targetScale: true);
            var baseSkin = SkinDeform.ComputeSkinMatrices(renderer, mesh, baseBoneWorld);
            var targetSkin = SkinDeform.ComputeSkinMatrices(renderer, mesh, targetBoneWorld);
            var baseWorld = SkinDeform.TransformPoints(baseSkin, mesh.vertices);
            var targetWorld = SkinDeform.TransformPoints(targetSkin, mesh.vertices);
            int count = Mathf.Min(baseWorld.Length, targetWorld.Length);
            var deltas = new Vector3[count];
            for (int i = 0; i < count; i++) deltas[i] = targetWorld[i] - baseWorld[i];
            return deltas;
        }

        private static Matrix4x4 ComputeWorldMatrix(
                Transform transform,
                Dictionary<Transform, Vector3> scaleOverrides,
                Dictionary<Transform, Matrix4x4> cache) {

            if (transform == null) return Matrix4x4.identity;
            if (cache.TryGetValue(transform, out Matrix4x4 cached)) return cached;
            Vector3 scale = scaleOverrides != null && scaleOverrides.TryGetValue(transform, out Vector3 overrideScale)
                ? overrideScale
                : transform.localScale;
            var local = Matrix4x4.TRS(transform.localPosition, transform.localRotation, scale);
            var parent = transform.parent != null
                ? ComputeWorldMatrix(transform.parent, scaleOverrides, cache)
                : Matrix4x4.identity;
            var world = parent * local;
            cache[transform] = world;
            return world;
        }

        private static string ValidateOptions(Options options, IList<BoneScaleFollowRow> activeRows) {
            if (options == null) return "Options are missing.";
            if (options.SourceRenderer == null) return "Pick a source renderer.";
            if (options.SourceRenderer.sharedMesh == null) return "Source renderer has no mesh.";
            if (!options.SourceRenderer.sharedMesh.isReadable) return "Source mesh is not readable.";
            if (options.TargetRenderers == null || options.TargetRenderers.Count == 0) return "Add at least one target renderer.";
            if (activeRows == null || activeRows.Count == 0) return "Add at least one enabled bone scale row.";

            for (int i = 0; i < activeRows.Count; i++) {
                var row = activeRows[i];
                if (row.Bone == null) return $"Bone scale row {i + 1} is missing a bone.";
                if (!IsValidScale(row.BaseScale)) return $"Bone scale row {i + 1} has an invalid base scale.";
                if (!IsValidScale(row.TargetScale)) return $"Bone scale row {i + 1} has an invalid target scale.";
                if (!AffectsRenderer(row.Bone, options.SourceRenderer)) {
                    return $"Bone scale row {i + 1} does not affect the source renderer bones.";
                }
            }

            for (int i = 0; i < activeRows.Count; i++) {
                for (int j = i + 1; j < activeRows.Count; j++) {
                    var a = activeRows[i].Bone;
                    var b = activeRows[j].Bone;
                    if (IsAncestorOrSame(a, b) || IsAncestorOrSame(b, a)) {
                        return "Bone scale rows must be disjoint; remove ancestor/descendant overlaps.";
                    }
                }
            }
            return null;
        }

        private static List<BoneScaleFollowRow> ActiveRows(Options options) {
            var list = new List<BoneScaleFollowRow>();
            if (options == null || options.BoneRows == null) return list;
            for (int i = 0; i < options.BoneRows.Count; i++) {
                var row = options.BoneRows[i];
                if (row == null || !row.Enabled) continue;
                list.Add(row);
            }
            return list;
        }

        private static bool AffectsRenderer(Transform bone, SkinnedMeshRenderer renderer) {
            if (bone == null || renderer == null || renderer.bones == null) return false;
            for (int i = 0; i < renderer.bones.Length; i++) {
                if (IsAncestorOrSame(bone, renderer.bones[i])) return true;
            }
            return false;
        }

        private static bool IsAncestorOrSame(Transform ancestor, Transform descendant) {
            if (ancestor == null || descendant == null) return false;
            var cursor = descendant;
            while (cursor != null) {
                if (cursor == ancestor) return true;
                cursor = cursor.parent;
            }
            return false;
        }

        private static bool IsValidScale(Vector3 scale) {
            return IsFinitePositive(scale.x) && IsFinitePositive(scale.y) && IsFinitePositive(scale.z);
        }

        private static bool IsFinitePositive(float value) {
            return !float.IsNaN(value) && !float.IsInfinity(value) && value > 0f;
        }

        private static SurfaceDeltaTransferOptions BuildTransferOptions(Options options) {
            return new SurfaceDeltaTransferOptions {
                SourceRenderer = options.SourceRenderer,
                MaxDistance = options.MaxDistance,
                DeltaEpsilon = options.DeltaEpsilon,
                CorrespondenceMode = BlendShapeTransferCore.MapCorrespondence(options.CorrespondenceMode),
                RayFrontalDistance = options.RayFrontalDistance,
                RayRearDistance = options.RayRearDistance,
                NormalAngleLimitDegrees = options.NormalAngleLimitDegrees,
                RejectBackfaces = options.RejectBackfaces,
                UseDistanceFalloff = options.UseDistanceFalloff,
                FalloffStartDistance = options.FalloffStartDistance,
            };
        }

        private static List<SkinnedMeshRenderer> DistinctTargets(IList<SkinnedMeshRenderer> targets) {
            var list = new List<SkinnedMeshRenderer>();
            if (targets == null) return list;
            var seen = new HashSet<int>();
            foreach (var target in targets) {
                if (target == null) {
                    list.Add(null);
                    continue;
                }
                int id = target.GetInstanceID();
                if (!seen.Add(id)) continue;
                list.Add(target);
            }
            return list;
        }

        private static string ResolveOutputName(Options options) {
            if (options != null && !string.IsNullOrEmpty(options.OutputBlendShapeName)) return options.OutputBlendShapeName;
            return DefaultBlendShapeName;
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            return renderer == null ? "(null)" : PathUtility.GetGameObjectPath(renderer.gameObject);
        }
    }
}
