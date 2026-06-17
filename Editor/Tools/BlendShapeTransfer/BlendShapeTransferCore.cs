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
            public readonly List<TargetResult> Targets = new List<TargetResult>();
            public bool ConfigurationError;
            public int ProcessedCount => Targets.Count(t => t.Processed);
            public int SkippedCount => Targets.Count(t => t.Skipped);
        }

        internal sealed class TargetResult {
            public SkinnedMeshRenderer Renderer;
            public bool Processed;
            public bool Skipped;
            public string Reason = "";
            public int TotalVertices;
            public int AffectedVertices;
            public float MaxObservedDistance;
            public float MaxObservedDelta;
            public readonly List<BlendShapeUtility.Frame> Frames = new List<BlendShapeUtility.Frame>();
        }

        internal sealed class ApplyResult {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> CreatedPaths = new List<string>();
            public int RenderersUpdated;
            public int RenderersSkipped;
            public bool ConfigurationError;
        }

        private sealed class SourceData {
            public Mesh Mesh;
            public int ShapeIndex;
            public Vector3[] WorldVertices;
            public Vector3[][] WorldDeltasByFrame;
            public float[] FrameWeights;
            public int[] Triangles;
            public Vector3[] FaceNormals;
            public MeshSpatialGrid Grid;
            public Bounds ActiveBounds;
            public bool HasActiveDelta;
        }

        internal static PreviewResult Preview(Options options) {
            var result = new PreviewResult();
            string error = ValidateOptions(options);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            SourceData source = BuildSource(options, out error);
            if (!string.IsNullOrEmpty(error)) {
                result.ConfigurationError = true;
                result.Summary = error;
                return result;
            }

            var targets = DistinctTargets(options.TargetRenderers);
            foreach (var target in targets) {
                result.Targets.Add(ComputeTarget(options, source, target));
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

        private static TargetResult ComputeTarget(Options options, SourceData source, SkinnedMeshRenderer target) {
            var result = new TargetResult { Renderer = target };
            if (target == null) return Skip(result, "Renderer is missing.");
            if (target == options.SourceRenderer) return Skip(result, "Source renderer.");
            var mesh = target.sharedMesh;
            if (mesh == null) return Skip(result, "Renderer has no mesh.");
            if (!mesh.isReadable) return Skip(result, "Mesh is not readable.");
            if (!source.HasActiveDelta) return Skip(result, "Source blendshape has no active delta.");

            result.TotalVertices = mesh.vertexCount;
            var targetSkin = SkinDeform.ComputeSkinMatrices(target, mesh);
            var targetWorldVertices = SkinDeform.TransformPoints(targetSkin, mesh.vertices);
            var targetBounds = SkinDeform.ComputeBounds(targetWorldVertices);
            float maxDistance = Mathf.Max(0f, options.MaxDistance);
            var expandedSourceBounds = SkinDeform.Expand(source.ActiveBounds, maxDistance);
            if (!expandedSourceBounds.Intersects(targetBounds)) {
                return Skip(result, "Outside active source shape bounds.");
            }

            var targetNormals = SkinDeform.TransformVectors(targetSkin, MeshGeometry.ResolveNormals(mesh));
            int frameCount = source.WorldDeltasByFrame.Length;
            var localDeltasByFrame = new Vector3[frameCount][];
            for (int frame = 0; frame < frameCount; frame++) {
                localDeltasByFrame[frame] = new Vector3[mesh.vertexCount];
            }

            float maxDist = 0f;
            float maxDelta = 0f;
            int affected = 0;
            float epsilonSq = Mathf.Max(options.DeltaEpsilon, 0f) * Mathf.Max(options.DeltaEpsilon, 0f);

            for (int v = 0; v < targetWorldVertices.Length; v++) {
                var hit = ResolveHit(options, source, targetWorldVertices[v], NormalAt(targetNormals, v));
                if (hit.triangleIndex < 0) continue;
                if (maxDistance > 0f && hit.distance > maxDistance) continue;
                if (hit.distance > maxDist) maxDist = hit.distance;

                int i0 = source.Triangles[hit.triangleIndex * 3];
                int i1 = source.Triangles[hit.triangleIndex * 3 + 1];
                int i2 = source.Triangles[hit.triangleIndex * 3 + 2];
                bool anyDelta = false;

                for (int frame = 0; frame < frameCount; frame++) {
                    Vector3 worldDelta =
                        source.WorldDeltasByFrame[frame][i0] * hit.wa +
                        source.WorldDeltasByFrame[frame][i1] * hit.wb +
                        source.WorldDeltasByFrame[frame][i2] * hit.wc;
                    if (worldDelta.sqrMagnitude <= epsilonSq) continue;
                    if (!SkinDeform.InverseMultiplyVector(targetSkin[v], worldDelta, out Vector3 localDelta)) continue;
                    localDeltasByFrame[frame][v] = localDelta;
                    float mag = localDelta.magnitude;
                    if (mag > maxDelta) maxDelta = mag;
                    anyDelta = true;
                }
                if (anyDelta) affected++;
            }

            result.MaxObservedDistance = maxDist;
            result.MaxObservedDelta = maxDelta;
            result.AffectedVertices = affected;
            if (affected == 0) return Skip(result, "No vertices close enough to the active source shape.");

            for (int frame = 0; frame < frameCount; frame++) {
                result.Frames.Add(new BlendShapeUtility.Frame {
                    Weight = source.FrameWeights[frame],
                    DeltaVertices = localDeltasByFrame[frame],
                    DeltaNormals = null,
                    DeltaTangents = null,
                });
            }
            result.Processed = true;
            result.Reason = $"affected {affected}/{mesh.vertexCount}";
            return result;
        }

        private static MeshClosestHit ResolveHit(
                Options options,
                SourceData source,
                Vector3 targetPoint,
                Vector3 targetNormal) {
            switch (options.CorrespondenceMode) {
                case BlendShapeTransferCorrespondenceMode.NormalRaycast:
                case BlendShapeTransferCorrespondenceMode.BidirectionalNormalRaycast:
                    var projected = MeshSpatialQueries.QueryProjected(
                        source.Grid,
                        source.WorldVertices,
                        source.Triangles,
                        source.FaceNormals,
                        targetPoint,
                        targetNormal,
                        Mathf.Max(options.RayFrontalDistance, options.MaxDistance),
                        Mathf.Max(options.RayRearDistance, options.MaxDistance),
                        MeshGeometry.MinNormalDot(options.NormalAngleLimitDegrees),
                        options.RejectBackfaces,
                        options.CorrespondenceMode == BlendShapeTransferCorrespondenceMode.BidirectionalNormalRaycast);
                    return new MeshClosestHit {
                        triangleIndex = projected.triangleIndex,
                        point = projected.point,
                        wa = projected.wa,
                        wb = projected.wb,
                        wc = projected.wc,
                        distance = projected.distance,
                    };
                default:
                    return MeshSpatialQueries.QueryClosest(
                        source.Grid,
                        source.WorldVertices,
                        source.Triangles,
                        targetPoint);
            }
        }

        private static SourceData BuildSource(Options options, out string error) {
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
            bool activeInitialised = false;
            var activeBounds = new Bounds();
            float epsilonSq = Mathf.Max(options.DeltaEpsilon, 0f) * Mathf.Max(options.DeltaEpsilon, 0f);

            for (int frame = 0; frame < frameCount; frame++) {
                var localDeltas = new Vector3[mesh.vertexCount];
                var dn = new Vector3[mesh.vertexCount];
                var dt = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(shapeIndex, frame, localDeltas, dn, dt);
                var worldDeltas = SkinDeform.TransformVectors(skin, localDeltas);
                worldDeltasByFrame[frame] = worldDeltas;
                frameWeights[frame] = mesh.GetBlendShapeFrameWeight(shapeIndex, frame);

                for (int v = 0; v < worldDeltas.Length; v++) {
                    if (worldDeltas[v].sqrMagnitude <= epsilonSq) continue;
                    if (!activeInitialised) {
                        activeBounds = new Bounds(worldVertices[v], Vector3.zero);
                        activeInitialised = true;
                    } else {
                        activeBounds.Encapsulate(worldVertices[v]);
                    }
                }
            }

            var triangles = mesh.triangles;
            return new SourceData {
                Mesh = mesh,
                ShapeIndex = shapeIndex,
                WorldVertices = worldVertices,
                WorldDeltasByFrame = worldDeltasByFrame,
                FrameWeights = frameWeights,
                Triangles = triangles,
                FaceNormals = MeshGeometry.ComputeFaceNormals(worldVertices, triangles),
                Grid = MeshSpatialQueries.BuildGrid(worldVertices, triangles, MeshGeometry.PickGridDim(triangles.Length / 3)),
                ActiveBounds = activeBounds,
                HasActiveDelta = activeInitialised,
            };
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

        private static TargetResult Skip(TargetResult result, string reason) {
            result.Skipped = true;
            result.Processed = false;
            result.Reason = reason;
            return result;
        }

        private static Vector3 NormalAt(Vector3[] normals, int index) {
            if (normals == null || index < 0 || index >= normals.Length) return Vector3.up;
            return MeshGeometry.SafeNormalize(normals[index], Vector3.up);
        }

        private static string ResolveOutputName(Options options) {
            if (!string.IsNullOrEmpty(options.OutputBlendShapeName)) return options.OutputBlendShapeName;
            return options.SourceBlendShapeName;
        }

        private static string RendererPath(SkinnedMeshRenderer renderer) {
            return renderer == null ? "(null)" : PathUtility.GetGameObjectPath(renderer.gameObject);
        }
    }
}
