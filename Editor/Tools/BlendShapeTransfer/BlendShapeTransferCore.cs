// BlendShapeTransferCore.cs

using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal enum BlendShapeTransferTargetMode {
        EntireAvatar,
        Manual,
    }

    internal enum BlendShapeTransferCorrespondenceMode {
        ClosestPoint,
        NormalRaycast,
        BidirectionalNormalRaycast,
    }

    internal static class BlendShapeTransferCore {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal sealed class Options {
            public SkinnedMeshRenderer SourceRenderer;
            public string SourceBlendShapeName;
            public string OutputBlendShapeName;
            public IList<SkinnedMeshRenderer> TargetRenderers;
            public float MaxDistance = 0.03f;
            public float DeltaEpsilon = 0.0001f;
            public BlendShapeTransferCorrespondenceMode CorrespondenceMode = BlendShapeTransferCorrespondenceMode.ClosestPoint;
            public float RayFrontalDistance = 0.08f;
            public float RayRearDistance = 0.08f;
            public float NormalAngleLimitDegrees = 75f;
            public bool RejectBackfaces;
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
            string error = ValidateOptions(options);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            SurfaceDeltaField source = BuildSource(options, out error);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            var targets = DistinctTargets(options.TargetRenderers);
            var transferOptions = BuildTransferOptions(options);
            foreach (var target in targets) {
                result.Targets.Add(SurfaceDeltaTransfer.ComputeTarget(transferOptions, source, target));
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
            Undo.SetCurrentGroupName("Avatar QoL: transfer blendshape");
            try {
                foreach (var target in preview.Targets) {
                    if (!target.Processed || target.Renderer == null) {
                        apply.RenderersSkipped++;
                        apply.Detail.Add($"SKIP {RendererPath(target.Renderer)} -- {target.Reason}");
                        continue;
                    }

                    var originalMesh = target.Renderer.sharedMesh;
                    var weightsByName = BlendShapeUtility.CaptureWeights(target.Renderer, originalMesh);
                    var editable = UnityEngine.Object.Instantiate(originalMesh);
                    editable.name = originalMesh.name + " (BlendShapeTransferred)";
                    if (!BlendShapeUtility.AddOrReplaceFrames(editable, ResolveOutputName(options), target.Frames)) {
                        UnityEngine.Object.DestroyImmediate(editable);
                        apply.Detail.Add($"SKIP {RendererPath(target.Renderer)} -- blendshape frame data did not match vertex count.");
                        apply.RenderersSkipped++;
                        continue;
                    }

                    var write = FbxMeshUtility.WriteNewMeshAsset(
                        target.Renderer,
                        editable,
                        "(BlendShapeTransferred)",
                        "Create transferred blendshape mesh",
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
                AvatarQolLogger.Instance.Exception(ex, "BlendShape Transfer");
                apply.Summary = "Transfer failed -- nothing was changed. See console for details.";
            }
            return apply;
        }

        internal static List<SkinnedMeshRenderer> ResolveWholeAvatarTargets(SkinnedMeshRenderer sourceRenderer) {
            var root = AvatarUtility.FindAvatarRoot(sourceRenderer);
            return AvatarUtility.GetSkinnedMeshes(root).ToList();
        }

        private static SurfaceDeltaField BuildSource(Options options, out string error) {
            error = null;
            var renderer = options.SourceRenderer;
            var mesh = renderer.sharedMesh;
            int shapeIndex = mesh.GetBlendShapeIndex(options.SourceBlendShapeName);
            if (shapeIndex < 0) {
                error = "Source blendshape was not found.";
                return null;
            }

            var skin = SkinDeform.ComputeSkinMatrices(renderer, mesh);
            var worldVertices = SkinDeform.TransformPoints(skin, mesh.vertices);
            int frameCount = mesh.GetBlendShapeFrameCount(shapeIndex);
            var worldDeltasByFrame = new Vector3[frameCount][];
            var frameWeights = new float[frameCount];

            for (int frame = 0; frame < frameCount; frame++) {
                var localDeltas = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                var dt = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(shapeIndex, frame, localDeltas, dn, dt);
                var worldDeltas = SkinDeform.TransformVectors(skin, localDeltas);
                worldDeltasByFrame[frame] = worldDeltas;
                frameWeights[frame] = mesh.GetBlendShapeFrameWeight(shapeIndex, frame);
            }

            var triangles = mesh.triangles;
            return SurfaceDeltaField.Build(
                renderer,
                worldVertices,
                worldDeltasByFrame,
                frameWeights,
                triangles,
                options.DeltaEpsilon,
                "Source blendshape has no active delta.");
        }

        private static string ValidateOptions(Options options) {
            if (options == null) return "Options are missing.";
            if (options.SourceRenderer == null) return "Pick a source renderer.";
            if (options.SourceRenderer.sharedMesh == null) return "Source renderer has no mesh.";
            if (!options.SourceRenderer.sharedMesh.isReadable) return "Source mesh is not readable.";
            if (string.IsNullOrEmpty(options.SourceBlendShapeName)) return "Pick a source blendshape.";
            if (options.TargetRenderers == null || options.TargetRenderers.Count == 0) return "Add at least one target renderer.";
            return null;
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
            if (!string.IsNullOrEmpty(options.OutputBlendShapeName)) return options.OutputBlendShapeName;
            return options.SourceBlendShapeName;
        }

        private static SurfaceDeltaTransferOptions BuildTransferOptions(Options options) {
            return new SurfaceDeltaTransferOptions {
                SourceRenderer = options.SourceRenderer,
                MaxDistance = options.MaxDistance,
                DeltaEpsilon = options.DeltaEpsilon,
                CorrespondenceMode = MapCorrespondence(options.CorrespondenceMode),
                RayFrontalDistance = options.RayFrontalDistance,
                RayRearDistance = options.RayRearDistance,
                NormalAngleLimitDegrees = options.NormalAngleLimitDegrees,
                RejectBackfaces = options.RejectBackfaces,
            };
        }

        internal static SurfaceDeltaTransferCorrespondenceMode MapCorrespondence(BlendShapeTransferCorrespondenceMode mode) {
            switch (mode) {
                case BlendShapeTransferCorrespondenceMode.NormalRaycast:
                    return SurfaceDeltaTransferCorrespondenceMode.NormalRaycast;
                case BlendShapeTransferCorrespondenceMode.BidirectionalNormalRaycast:
                    return SurfaceDeltaTransferCorrespondenceMode.BidirectionalNormalRaycast;
                default:
                    return SurfaceDeltaTransferCorrespondenceMode.ClosestPoint;
            }
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            return renderer == null ? "(null)" : PathUtility.GetGameObjectPath(renderer.gameObject);
        }
    }
}
