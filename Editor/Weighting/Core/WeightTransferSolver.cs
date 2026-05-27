// WeightTransferSolver.cs
//
// Blender-style weight transfer core: source surface correspondence,
// barycentric source-weight interpolation, source-to-target bone mapping,
// strict confidence gates, then topology inpainting for rejected vertices.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal enum WeightTransferMode {
        HybridSurface,
        ProjectedBodySurface,
        NearestSurface,
        ExactTopology,
    }

    internal sealed class WeightTransferSettings {
        public SkinnedMeshRenderer Source;
        public SkinnedMeshRenderer Target;
        public Transform SpaceRoot;
        public WeightTransferMode Mode = WeightTransferMode.HybridSurface;
        public int SourceSubmesh = -1;
        public float MaxClosestDistance = 0.06f;
        public float MaxProjectionDistance = 0.12f;
        public float NormalAngleLimit = 35f;
        public bool AllowFlippedNormals;
        public bool InpaintRejectedVertices = true;
        public int InpaintIterations = 48;
        public int MaxInfluences = 4;
        public float PruneThreshold = 0.001f;
        public int FallbackBone = 0;
    }

    internal sealed class WeightTransferResult {
        public SkinWeightBuffer Weights;
        public BoneBindingMap BoneMap;
        public bool[] Accepted;
        public int AcceptedCount;
        public int RejectedCount;
        public int InpaintedCount;
        public int UnresolvedCount;
        public string Message;
    }

    internal static class WeightTransferSolver {

        public static WeightTransferResult Transfer(WeightTransferSettings settings) {
            var result = new WeightTransferResult {
                Message = "Transfer was not run.",
            };
            if (settings == null || settings.Source == null || settings.Target == null) {
                result.Message = "Pick both source and target renderers.";
                return result;
            }

            var sourceMesh = settings.Source.sharedMesh;
            var targetMesh = settings.Target.sharedMesh;
            if (sourceMesh == null || targetMesh == null) {
                result.Message = "Source or target renderer has no mesh.";
                return result;
            }
            if (!sourceMesh.isReadable || !targetMesh.isReadable) {
                result.Message = "Source and target meshes must be readable.";
                return result;
            }

            int targetBoneCount = settings.Target.bones != null ? settings.Target.bones.Length : 0;
            int sourceBoneCount = settings.Source.bones != null ? settings.Source.bones.Length : 0;
            if (targetBoneCount <= 0 || sourceBoneCount <= 0) {
                result.Message = "Source and target renderers must have bones.";
                return result;
            }

            var sourceWeights = SkinWeightBuffer.FromMesh(sourceMesh, sourceBoneCount, 0);
            var targetOriginal = SkinWeightBuffer.FromMesh(targetMesh, targetBoneCount, settings.FallbackBone);
            var output = new SkinWeightBuffer(targetMesh.vertexCount, targetBoneCount);
            var accepted = new bool[targetMesh.vertexCount];
            var boneMap = BoneBindingMap.Build(settings.Source, settings.Target, settings.SpaceRoot);

            if (settings.Mode == WeightTransferMode.ExactTopology) {
                if (sourceMesh.vertexCount != targetMesh.vertexCount) {
                    result.Message = "Exact Topology requires matching source and target vertex counts.";
                    return result;
                }
                TransferByTopology(sourceWeights, output, boneMap, settings, accepted, result);
            } else {
                TransferBySurface(sourceWeights, output, boneMap, settings, accepted, result);
            }

            result.RejectedCount = targetMesh.vertexCount - result.AcceptedCount;
            if (settings.InpaintRejectedVertices && result.RejectedCount > 0) {
                var inpaint = WeightInpaintSolver.FillRejected(
                    output,
                    accepted,
                    MeshAdjacency.Build(targetMesh),
                    targetOriginal,
                    settings.InpaintIterations,
                    settings.MaxInfluences,
                    settings.PruneThreshold,
                    settings.FallbackBone);
                result.InpaintedCount = inpaint.Inpainted;
                result.UnresolvedCount = inpaint.Unresolved;
            } else {
                for (int v = 0; v < targetMesh.vertexCount; v++) {
                    if (accepted[v]) continue;
                    output.CopyVertexFrom(targetOriginal, v, v);
                    output.PruneVertex(v, settings.MaxInfluences, settings.PruneThreshold, settings.FallbackBone);
                    result.UnresolvedCount++;
                }
            }

            output.PruneAll(settings.MaxInfluences, settings.PruneThreshold, settings.FallbackBone);
            result.Weights = output;
            result.BoneMap = boneMap;
            result.Accepted = accepted;
            result.Message =
                $"{result.AcceptedCount:N0} accepted, {result.InpaintedCount:N0} inpainted, " +
                $"{result.UnresolvedCount:N0} preserved from target. {boneMap.Summary()}.";
            return result;
        }

        private static void TransferByTopology(
                SkinWeightBuffer sourceWeights,
                SkinWeightBuffer output,
                BoneBindingMap boneMap,
                WeightTransferSettings settings,
                bool[] accepted,
                WeightTransferResult result) {
            int count = Mathf.Min(sourceWeights.VertexCount, output.VertexCount);
            for (int v = 0; v < count; v++) {
                var mapped = new Dictionary<int, float>();
                AccumulateMappedSourceVertex(sourceWeights, v, 1f, boneMap, mapped);
                output.SetVertexFromDictionary(v, mapped);
                output.PruneVertex(v, settings.MaxInfluences, settings.PruneThreshold, settings.FallbackBone);
                accepted[v] = output.HasAnyWeight(v);
                if (accepted[v]) result.AcceptedCount++;
            }
        }

        private static void TransferBySurface(
                SkinWeightBuffer sourceWeights,
                SkinWeightBuffer output,
                BoneBindingMap boneMap,
                WeightTransferSettings settings,
                bool[] accepted,
                WeightTransferResult result) {
            var sourceSnapshot = SkinnedMeshSnapshot.Build(settings.Source, settings.SpaceRoot);
            var targetSnapshot = SkinnedMeshSnapshot.Build(settings.Target, settings.SpaceRoot);
            var index = SourceSurfaceIndex.Build(sourceSnapshot, settings.SourceSubmesh);
            float minDot = Mathf.Cos(Mathf.Clamp(settings.NormalAngleLimit, 0f, 179f) * Mathf.Deg2Rad);

            for (int v = 0; v < targetSnapshot.Positions.Length; v++) {
                var p = targetSnapshot.Positions[v];
                var n = v < targetSnapshot.Normals.Length ? targetSnapshot.Normals[v] : Vector3.up;
                if (!TryFindBestMatch(index, p, n, settings, minDot, out var match)) continue;

                var mapped = new Dictionary<int, float>();
                AccumulateMappedSourceVertex(sourceWeights, match.I0, match.Barycentric.x, boneMap, mapped);
                AccumulateMappedSourceVertex(sourceWeights, match.I1, match.Barycentric.y, boneMap, mapped);
                AccumulateMappedSourceVertex(sourceWeights, match.I2, match.Barycentric.z, boneMap, mapped);
                output.SetVertexFromDictionary(v, mapped);
                output.PruneVertex(v, settings.MaxInfluences, settings.PruneThreshold, settings.FallbackBone);
                if (!output.HasAnyWeight(v)) continue;

                accepted[v] = true;
                result.AcceptedCount++;
            }
        }

        private static bool TryFindBestMatch(
                SourceSurfaceIndex index,
                Vector3 point,
                Vector3 normal,
                WeightTransferSettings settings,
                float minDot,
                out SurfaceMatch best) {
            best = default;
            bool any = false;
            float bestScore = float.PositiveInfinity;

            bool useProjection = settings.Mode == WeightTransferMode.HybridSurface
                || settings.Mode == WeightTransferMode.ProjectedBodySurface;
            bool useClosest = settings.Mode == WeightTransferMode.HybridSurface
                || settings.Mode == WeightTransferMode.NearestSurface;

            if (useProjection && index.TryProjected(
                    point,
                    normal,
                    settings.MaxProjectionDistance * 0.5f,
                    settings.MaxProjectionDistance * 0.5f,
                    minDot,
                    settings.AllowFlippedNormals,
                    bidirectional: true,
                    out var projected)) {
                float score = Score(projected, settings.MaxProjectionDistance, settings.AllowFlippedNormals) - 0.15f;
                any = true;
                best = projected;
                bestScore = score;
            }

            if (useClosest && index.TryClosest(
                    point,
                    normal,
                    settings.MaxClosestDistance,
                    minDot,
                    settings.AllowFlippedNormals,
                    out var closest)) {
                float score = Score(closest, settings.MaxClosestDistance, settings.AllowFlippedNormals);
                if (!any || score < bestScore) {
                    any = true;
                    best = closest;
                }
            }

            return any;
        }

        private static float Score(SurfaceMatch match, float maxDistance, bool allowFlippedNormals) {
            float distanceScore = maxDistance > 1e-5f ? Mathf.Clamp01(match.Distance / maxDistance) : 0f;
            float dot = allowFlippedNormals ? Mathf.Abs(match.NormalDot) : Mathf.Max(0f, match.NormalDot);
            float normalScore = 1f - Mathf.Clamp01(dot);
            return distanceScore + normalScore * 1.25f;
        }

        private static void AccumulateMappedSourceVertex(
                SkinWeightBuffer sourceWeights,
                int sourceVertex,
                float scale,
                BoneBindingMap boneMap,
                Dictionary<int, float> mapped) {
            if (sourceWeights == null || boneMap == null || mapped == null || scale <= 0f) return;
            var influences = sourceWeights.Get(sourceVertex);
            for (int i = 0; i < influences.Count; i++) {
                var influence = influences[i];
                if (!boneMap.TryMap(influence.BoneIndex, out int targetBone)) continue;
                if (mapped.TryGetValue(targetBone, out float current)) {
                    mapped[targetBone] = current + influence.Weight * scale;
                } else {
                    mapped[targetBone] = influence.Weight * scale;
                }
            }
        }
    }
}
