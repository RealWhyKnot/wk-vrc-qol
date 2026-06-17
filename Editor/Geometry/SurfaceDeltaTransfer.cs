// SurfaceDeltaTransfer.cs
//
// Transfers a source mesh world-space delta field onto target renderers.

using System.Collections.Generic;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Geometry {

    internal enum SurfaceDeltaTransferCorrespondenceMode {
        ClosestPoint,
        NormalRaycast,
        BidirectionalNormalRaycast,
    }

    internal sealed class SurfaceDeltaField {
        public SkinnedMeshRenderer SourceRenderer;
        public Vector3[] WorldVertices;
        public Vector3[][] WorldDeltasByFrame;
        public float[] FrameWeights;
        public int[] Triangles;
        public Vector3[] FaceNormals;
        public MeshSpatialGrid Grid;
        public Bounds ActiveBounds;
        public bool HasActiveDelta;
        public string InactiveReason = "Source delta field has no active delta.";

        internal static SurfaceDeltaField Build(
                SkinnedMeshRenderer sourceRenderer,
                Vector3[] worldVertices,
                Vector3[][] worldDeltasByFrame,
                float[] frameWeights,
                int[] triangles,
                float deltaEpsilon,
                string inactiveReason) {

            worldVertices = worldVertices ?? new Vector3[0];
            worldDeltasByFrame = worldDeltasByFrame ?? new Vector3[0][];
            frameWeights = frameWeights ?? new float[0];
            triangles = triangles ?? new int[0];

            bool activeInitialised = false;
            var activeBounds = new Bounds();
            float epsilonSq = Mathf.Max(deltaEpsilon, 0f) * Mathf.Max(deltaEpsilon, 0f);

            for (int frame = 0; frame < worldDeltasByFrame.Length; frame++) {
                var deltas = worldDeltasByFrame[frame];
                if (deltas == null) continue;
                int count = Mathf.Min(worldVertices.Length, deltas.Length);
                for (int v = 0; v < count; v++) {
                    if (deltas[v].sqrMagnitude <= epsilonSq) continue;
                    if (!activeInitialised) {
                        activeBounds = new Bounds(worldVertices[v], Vector3.zero);
                        activeInitialised = true;
                    } else {
                        activeBounds.Encapsulate(worldVertices[v]);
                    }
                }
            }

            return new SurfaceDeltaField {
                SourceRenderer = sourceRenderer,
                WorldVertices = worldVertices,
                WorldDeltasByFrame = worldDeltasByFrame,
                FrameWeights = frameWeights,
                Triangles = triangles,
                FaceNormals = MeshGeometry.ComputeFaceNormals(worldVertices, triangles),
                Grid = MeshSpatialQueries.BuildGrid(worldVertices, triangles, MeshGeometry.PickGridDim(triangles.Length / 3)),
                ActiveBounds = activeBounds,
                HasActiveDelta = activeInitialised,
                InactiveReason = string.IsNullOrEmpty(inactiveReason) ? "Source delta field has no active delta." : inactiveReason,
            };
        }
    }

    internal sealed class SurfaceDeltaTransferOptions {
        public SkinnedMeshRenderer SourceRenderer;
        public float MaxDistance = 0.03f;
        public float DeltaEpsilon = 0.0001f;
        public SurfaceDeltaTransferCorrespondenceMode CorrespondenceMode = SurfaceDeltaTransferCorrespondenceMode.ClosestPoint;
        public float RayFrontalDistance = 0.08f;
        public float RayRearDistance = 0.08f;
        public float NormalAngleLimitDegrees = 75f;
        public bool RejectBackfaces;
        public bool UseDistanceFalloff;
        public float FalloffStartDistance = 0.02f;
    }

    internal sealed class SurfaceDeltaTargetResult {
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

    internal static class SurfaceDeltaTransfer {

        internal static SurfaceDeltaTargetResult ComputeTarget(
                SurfaceDeltaTransferOptions options,
                SurfaceDeltaField source,
                SkinnedMeshRenderer target,
                Vector3[] targetWorldDeltaBias = null) {

            var result = new SurfaceDeltaTargetResult { Renderer = target };
            if (options == null) return Skip(result, "Transfer options are missing.");
            if (source == null) return Skip(result, "Source delta field is missing.");
            if (target == null) return Skip(result, "Renderer is missing.");
            if (target == options.SourceRenderer || target == source.SourceRenderer) return Skip(result, "Source renderer.");
            var mesh = target.sharedMesh;
            if (mesh == null) return Skip(result, "Renderer has no mesh.");
            if (!mesh.isReadable) return Skip(result, "Mesh is not readable.");
            if (mesh.vertexCount == 0) return Skip(result, "Mesh has no vertices.");
            if (!source.HasActiveDelta) return Skip(result, source.InactiveReason);

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
            float epsilon = Mathf.Max(options.DeltaEpsilon, 0f);
            float epsilonSq = epsilon * epsilon;

            for (int v = 0; v < targetWorldVertices.Length; v++) {
                var hit = ResolveHit(options, source, targetWorldVertices[v], NormalAt(targetNormals, v));
                if (hit.triangleIndex < 0) continue;
                if (maxDistance > 0f && hit.distance > maxDistance) continue;
                if (hit.distance > maxDist) maxDist = hit.distance;

                int i0 = source.Triangles[hit.triangleIndex * 3];
                int i1 = source.Triangles[hit.triangleIndex * 3 + 1];
                int i2 = source.Triangles[hit.triangleIndex * 3 + 2];
                float falloff = DistanceFalloff(options, hit.distance);
                bool anyDelta = false;

                for (int frame = 0; frame < frameCount; frame++) {
                    Vector3 worldDelta =
                        source.WorldDeltasByFrame[frame][i0] * hit.wa +
                        source.WorldDeltasByFrame[frame][i1] * hit.wb +
                        source.WorldDeltasByFrame[frame][i2] * hit.wc;
                    worldDelta *= falloff;
                    if (targetWorldDeltaBias != null && v < targetWorldDeltaBias.Length) {
                        worldDelta -= targetWorldDeltaBias[v];
                    }
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
                    Weight = frame < source.FrameWeights.Length ? source.FrameWeights[frame] : 100f,
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
                SurfaceDeltaTransferOptions options,
                SurfaceDeltaField source,
                Vector3 targetPoint,
                Vector3 targetNormal) {

            switch (options.CorrespondenceMode) {
                case SurfaceDeltaTransferCorrespondenceMode.NormalRaycast:
                case SurfaceDeltaTransferCorrespondenceMode.BidirectionalNormalRaycast:
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
                        options.CorrespondenceMode == SurfaceDeltaTransferCorrespondenceMode.BidirectionalNormalRaycast);
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

        private static float DistanceFalloff(SurfaceDeltaTransferOptions options, float distance) {
            if (options == null || !options.UseDistanceFalloff) return 1f;
            float maxDistance = Mathf.Max(0f, options.MaxDistance);
            float start = Mathf.Clamp(options.FalloffStartDistance, 0f, maxDistance);
            if (maxDistance <= 0f || distance <= start) return 1f;
            float span = maxDistance - start;
            if (span <= 1e-6f) return 1f;
            float t = Mathf.Clamp01((distance - start) / span);
            return Mathf.SmoothStep(1f, 0f, t);
        }

        private static SurfaceDeltaTargetResult Skip(SurfaceDeltaTargetResult result, string reason) {
            result.Skipped = true;
            result.Processed = false;
            result.Reason = reason;
            return result;
        }

        private static Vector3 NormalAt(Vector3[] normals, int index) {
            if (normals == null || index < 0 || index >= normals.Length) return Vector3.up;
            return MeshGeometry.SafeNormalize(normals[index], Vector3.up);
        }
    }
}
