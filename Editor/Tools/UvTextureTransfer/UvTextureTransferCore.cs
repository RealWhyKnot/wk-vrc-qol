// UvTextureTransferCore.cs
//
// Mesh-to-mesh texture transfer orchestration. Geometry queries live in
// WhyKnot.AvatarQol.Geometry and UV raster/padding helpers live beside
// this file in UvTextureRaster.cs.

using System;
using System.Threading.Tasks;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;

namespace WhyKnot.AvatarQol.Tools {

    internal static class UvTextureTransferCore {

        internal enum AlignmentMode {
            Identity,
            BoundingBox,
        }

        internal enum CorrespondenceMode {
            LegacyClosestPoint,
            NormalRaycast,
            BidirectionalNormalRaycast,
            NormalAwareNearest,
        }

        internal struct TransferOptions {
            public Mesh sourceMesh;
            public int sourceSubmesh;
            public Texture2D sourceTexture;
            public Mesh targetMesh;
            public int targetSubmesh;
            public int outputResolution;
            public AlignmentMode alignment;
            public Matrix4x4 sourceWorldMatrix;
            public Matrix4x4 targetWorldMatrix;
            public float maxDistance;
            public int gridDim;
            public Color fallbackColor;
            public int supersample;
            public int paddingPixels;
            public CorrespondenceMode correspondenceMode;
            public float rayFrontalDistance;
            public float rayRearDistance;
            public float normalAngleLimitDegrees;
            public bool rejectBackfaces;
            public bool writeDiagnosticMaps;
            public Action<float> onProgress;
        }

        internal struct TransferResult {
            public Texture2D output;
            public int coveredTexels;
            public int totalTexels;
            public int rejectedByDistance;
            public float maxObservedDistance;
            public int supersample;
            public int paddingPixels;
            public int paddedTexels;
            public CorrespondenceMode correspondenceMode;
            public int rejectedByRayMiss;
            public int rejectedByNormalAngle;
            public int rejectedByBackface;
            public int nearestFallbackUsed;
            public Texture2D hitDistanceMap;
            public Texture2D normalDotMap;
            public Texture2D rejectReasonMap;
            public Texture2D sourceTriangleMap;
        }

        internal static TransferResult Transfer(TransferOptions opt) {
            if (opt.outputResolution <= 0) throw new ArgumentException("outputResolution must be positive");
            if (opt.sourceMesh == null) throw new ArgumentNullException(nameof(opt.sourceMesh));
            if (opt.targetMesh == null) throw new ArgumentNullException(nameof(opt.targetMesh));
            if (opt.sourceTexture == null) throw new ArgumentNullException(nameof(opt.sourceTexture));
            if (!opt.sourceTexture.isReadable) {
                throw new InvalidOperationException(
                    "Source texture is not Read/Write Enabled in the importer; the bake can't sample " +
                    "it from CPU. Open the source texture asset and enable Read/Write in the importer.");
            }

            int res = opt.outputResolution;
            opt.onProgress?.Invoke(0f);

            var (sourceToCommon, targetToCommon) = ComputeAlignment(
                opt.alignment, opt.sourceMesh, opt.targetMesh,
                opt.sourceWorldMatrix, opt.targetWorldMatrix);

            var sourceVerts = MeshGeometry.TransformVertices(opt.sourceMesh.vertices, sourceToCommon);
            var sourceUvs = opt.sourceMesh.uv;
            var sourceTris = MeshGeometry.ResolveTriangles(opt.sourceMesh, opt.sourceSubmesh);
            var sourceFaceNormals = MeshGeometry.ComputeFaceNormals(sourceVerts, sourceTris);
            var targetVerts = MeshGeometry.TransformVertices(opt.targetMesh.vertices, targetToCommon);
            var targetUvs = opt.targetMesh.uv;
            var targetTris = MeshGeometry.ResolveTriangles(opt.targetMesh, opt.targetSubmesh);
            var targetNormals = MeshGeometry.TransformNormals(MeshGeometry.ResolveNormals(opt.targetMesh), targetToCommon);

            int gridDim = opt.gridDim > 0
                ? opt.gridDim
                : MeshGeometry.PickGridDim(sourceTris.Length / 3);
            var grid = MeshSpatialQueries.BuildGrid(sourceVerts, sourceTris, gridDim);

            int samplesPerAxis = Mathf.Clamp(opt.supersample <= 0 ? 1 : opt.supersample, 1, 4);
            int sampleCount = samplesPerAxis * samplesPerAxis;
            int paddingPixels = Mathf.Clamp(opt.paddingPixels, 0, 128);
            var mode = opt.correspondenceMode;
            float frontalDistance = opt.rayFrontalDistance > 0f
                ? opt.rayFrontalDistance
                : Mathf.Max(opt.maxDistance, 0.08f);
            float rearDistance = opt.rayRearDistance > 0f
                ? opt.rayRearDistance
                : Mathf.Max(opt.maxDistance, 0.08f);
            float minNormalDot = MeshGeometry.MinNormalDot(opt.normalAngleLimitDegrees);
            opt.onProgress?.Invoke(0.15f);

            Color32[] srcPixels = opt.sourceTexture.GetPixels32();
            int srcW = opt.sourceTexture.width;
            int srcH = opt.sourceTexture.height;

            var outputPixels = new Color32[res * res];
            Color32 fallback32 = UvTextureRaster.ColorToColor32(opt.fallbackColor);
            var sumR = new ushort[outputPixels.Length];
            var sumG = new ushort[outputPixels.Length];
            var sumB = new ushort[outputPixels.Length];
            var sumA = new ushort[outputPixels.Length];
            var acceptedSamples = new byte[outputPixels.Length];
            Color32[] debugDistance = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugNormal = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugReject = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;
            Color32[] debugTriangle = opt.writeDiagnosticMaps ? new Color32[outputPixels.Length] : null;

            int rejectedCounter = 0;
            int rayMissCounter = 0;
            int normalRejectedCounter = 0;
            int backfaceRejectedCounter = 0;
            int nearestFallbackCounter = 0;
            float maxDistObserved = 0f;
            object statsLock = new object();

            for (int sy = 0; sy < samplesPerAxis; sy++) {
                for (int sx = 0; sx < samplesPerAxis; sx++) {
                    float offsetX = (sx + 0.5f) / samplesPerAxis;
                    float offsetY = (sy + 0.5f) / samplesPerAxis;
                    var samples = UvTextureRaster.RasterizeTargetUv(targetUvs, targetTris, res, offsetX, offsetY);
                    float passProgress = (sy * samplesPerAxis + sx) / (float)sampleCount;
                    opt.onProgress?.Invoke(0.15f + passProgress * 0.75f);

                    Parallel.For(0, res, row => {
                        int localRejected = 0;
                        int localRayMiss = 0;
                        int localNormalRejected = 0;
                        int localBackfaceRejected = 0;
                        int localFallback = 0;
                        float localMaxDist = 0f;
                        int rowBase = row * res;
                        for (int x = 0; x < res; x++) {
                            int idx = rowBase + x;
                            var sample = samples[idx];
                            if (sample.triangleIndex < 0) continue;
                            int t = sample.triangleIndex;
                            int ti0 = targetTris[t * 3];
                            int ti1 = targetTris[t * 3 + 1];
                            int ti2 = targetTris[t * 3 + 2];
                            Vector3 tp = targetVerts[ti0] * sample.wa
                                       + targetVerts[ti1] * sample.wb
                                       + targetVerts[ti2] * sample.wc;
                            Vector3 targetNormal = MeshGeometry.InterpolateNormal(
                                targetNormals, targetVerts, targetTris,
                                t, ti0, ti1, ti2,
                                sample.wa, sample.wb, sample.wc);

                            MeshProjectionHit hit = ResolveCorrespondence(
                                mode, grid, sourceVerts, sourceTris, sourceFaceNormals,
                                tp, targetNormal,
                                frontalDistance, rearDistance,
                                minNormalDot, opt.rejectBackfaces);

                            if (hit.triangleIndex < 0) {
                                CountReject(hit.rejectReason, ref localRayMiss, ref localNormalRejected, ref localBackfaceRejected);
                                WriteRejectDebug(debugReject, idx, hit.rejectReason);
                                continue;
                            }

                            if (hit.distance > localMaxDist) localMaxDist = hit.distance;
                            if (opt.maxDistance > 0f && hit.distance > opt.maxDistance) {
                                localRejected++;
                                WriteRejectDebug(debugReject, idx, MeshProjectionRejectReason.Distance);
                                continue;
                            }

                            if (mode == CorrespondenceMode.NormalAwareNearest) localFallback++;

                            int si0 = sourceTris[hit.triangleIndex * 3];
                            int si1 = sourceTris[hit.triangleIndex * 3 + 1];
                            int si2 = sourceTris[hit.triangleIndex * 3 + 2];
                            Vector2 srcUv = sourceUvs[si0] * hit.wa
                                          + sourceUvs[si1] * hit.wb
                                          + sourceUvs[si2] * hit.wc;
                            var color = UvTextureRaster.SampleBilinearClamp32(srcPixels, srcW, srcH, srcUv.x, srcUv.y);
                            sumR[idx] = (ushort)(sumR[idx] + color.r);
                            sumG[idx] = (ushort)(sumG[idx] + color.g);
                            sumB[idx] = (ushort)(sumB[idx] + color.b);
                            sumA[idx] = (ushort)(sumA[idx] + color.a);
                            acceptedSamples[idx]++;
                            WriteAcceptDebug(
                                debugDistance, debugNormal, debugReject, debugTriangle,
                                idx, hit, Mathf.Max(opt.maxDistance, frontalDistance + rearDistance));
                        }
                        if (localRejected > 0
                                || localRayMiss > 0
                                || localNormalRejected > 0
                                || localBackfaceRejected > 0
                                || localFallback > 0
                                || localMaxDist > 0f) {
                            lock (statsLock) {
                                rejectedCounter += localRejected;
                                rayMissCounter += localRayMiss;
                                normalRejectedCounter += localNormalRejected;
                                backfaceRejectedCounter += localBackfaceRejected;
                                nearestFallbackCounter += localFallback;
                                if (localMaxDist > maxDistObserved) maxDistObserved = localMaxDist;
                            }
                        }
                    });
                }
            }

            int coveredCounter = 0;
            var validPixels = new bool[outputPixels.Length];
            for (int i = 0; i < outputPixels.Length; i++) {
                int accepted = acceptedSamples[i];
                if (accepted > 0) {
                    coveredCounter++;
                    validPixels[i] = true;
                    int fallbackSamples = sampleCount - accepted;
                    outputPixels[i] = new Color32(
                        UvTextureRaster.AverageByte(sumR[i] + fallback32.r * fallbackSamples, sampleCount),
                        UvTextureRaster.AverageByte(sumG[i] + fallback32.g * fallbackSamples, sampleCount),
                        UvTextureRaster.AverageByte(sumB[i] + fallback32.b * fallbackSamples, sampleCount),
                        UvTextureRaster.AverageByte(sumA[i] + fallback32.a * fallbackSamples, sampleCount));
                } else {
                    outputPixels[i] = fallback32;
                }
            }

            int paddedTexels = UvTextureRaster.DilateUvIslandPadding(outputPixels, validPixels, res, paddingPixels);

            opt.onProgress?.Invoke(0.95f);

            var output = new Texture2D(res, res, TextureFormat.RGBA32, mipChain: false, linear: true) {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                name = "WkUvTextureTransfer_Output",
            };
            output.SetPixels32(outputPixels);
            output.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            Texture2D hitDistanceMap = CreateDebugTexture(debugDistance, res, "WkUvTextureTransfer_HitDistance");
            Texture2D normalDotMap = CreateDebugTexture(debugNormal, res, "WkUvTextureTransfer_NormalDot");
            Texture2D rejectReasonMap = CreateDebugTexture(debugReject, res, "WkUvTextureTransfer_RejectReason");
            Texture2D sourceTriangleMap = CreateDebugTexture(debugTriangle, res, "WkUvTextureTransfer_SourceTriangle");
            opt.onProgress?.Invoke(1f);

            return new TransferResult {
                output = output,
                coveredTexels = coveredCounter,
                totalTexels = outputPixels.Length,
                rejectedByDistance = rejectedCounter,
                maxObservedDistance = maxDistObserved,
                supersample = samplesPerAxis,
                paddingPixels = paddingPixels,
                paddedTexels = paddedTexels,
                correspondenceMode = mode,
                rejectedByRayMiss = rayMissCounter,
                rejectedByNormalAngle = normalRejectedCounter,
                rejectedByBackface = backfaceRejectedCounter,
                nearestFallbackUsed = nearestFallbackCounter,
                hitDistanceMap = hitDistanceMap,
                normalDotMap = normalDotMap,
                rejectReasonMap = rejectReasonMap,
                sourceTriangleMap = sourceTriangleMap,
            };
        }

        private static MeshProjectionHit ResolveCorrespondence(
                CorrespondenceMode mode,
                MeshSpatialGrid grid,
                Vector3[] sourceVerts,
                int[] sourceTris,
                Vector3[] sourceFaceNormals,
                Vector3 targetPoint,
                Vector3 targetNormal,
                float frontalDistance,
                float rearDistance,
                float minNormalDot,
                bool rejectBackfaces) {
            switch (mode) {
                case CorrespondenceMode.NormalRaycast:
                    return MeshSpatialQueries.QueryProjected(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal,
                        frontalDistance, rearDistance,
                        minNormalDot, rejectBackfaces,
                        bidirectional: false);

                case CorrespondenceMode.BidirectionalNormalRaycast:
                    return MeshSpatialQueries.QueryProjected(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal,
                        frontalDistance, rearDistance,
                        minNormalDot, rejectBackfaces,
                        bidirectional: true);

                case CorrespondenceMode.NormalAwareNearest:
                    return QueryNormalAwareNearest(
                        grid, sourceVerts, sourceTris, sourceFaceNormals,
                        targetPoint, targetNormal, minNormalDot);

                case CorrespondenceMode.LegacyClosestPoint:
                default:
                    var hit = MeshSpatialQueries.QueryClosest(grid, sourceVerts, sourceTris, targetPoint);
                    return new MeshProjectionHit {
                        triangleIndex = hit.triangleIndex,
                        point = hit.point,
                        wa = hit.wa,
                        wb = hit.wb,
                        wc = hit.wc,
                        distance = hit.distance,
                        normalDot = 1f,
                        rejectReason = hit.triangleIndex >= 0
                            ? MeshProjectionRejectReason.None
                            : MeshProjectionRejectReason.RayMiss,
                    };
            }
        }

        private static MeshProjectionHit QueryNormalAwareNearest(
                MeshSpatialGrid grid,
                Vector3[] verts,
                int[] triangles,
                Vector3[] faceNormals,
                Vector3 targetPoint,
                Vector3 targetNormal,
                float minNormalDot) {
            var hit = MeshSpatialQueries.QueryClosest(grid, verts, triangles, targetPoint);
            if (hit.triangleIndex < 0) {
                return new MeshProjectionHit {
                    triangleIndex = -1,
                    rejectReason = MeshProjectionRejectReason.RayMiss,
                };
            }

            Vector3 sourceNormal = faceNormals != null && hit.triangleIndex < faceNormals.Length
                ? faceNormals[hit.triangleIndex]
                : Vector3.zero;
            sourceNormal = MeshGeometry.SafeNormalize(sourceNormal, Vector3.up);
            targetNormal = MeshGeometry.SafeNormalize(targetNormal, Vector3.up);
            float normalDot = Vector3.Dot(targetNormal, sourceNormal);
            if (minNormalDot > -1.01f && normalDot < minNormalDot) {
                return new MeshProjectionHit {
                    triangleIndex = -1,
                    rejectReason = MeshProjectionRejectReason.NormalAngle,
                    distance = hit.distance,
                    normalDot = normalDot,
                };
            }

            return new MeshProjectionHit {
                triangleIndex = hit.triangleIndex,
                point = hit.point,
                wa = hit.wa,
                wb = hit.wb,
                wc = hit.wc,
                distance = hit.distance,
                normalDot = normalDot,
                rejectReason = MeshProjectionRejectReason.None,
            };
        }

        private static void CountReject(
                MeshProjectionRejectReason reason,
                ref int rayMiss,
                ref int normalRejected,
                ref int backfaceRejected) {
            switch (reason) {
                case MeshProjectionRejectReason.NormalAngle:
                    normalRejected++;
                    break;
                case MeshProjectionRejectReason.Backface:
                    backfaceRejected++;
                    break;
                case MeshProjectionRejectReason.Distance:
                    break;
                case MeshProjectionRejectReason.RayMiss:
                default:
                    rayMiss++;
                    break;
            }
        }

        private static void WriteAcceptDebug(
                Color32[] distanceMap,
                Color32[] normalMap,
                Color32[] rejectMap,
                Color32[] triangleMap,
                int idx,
                MeshProjectionHit hit,
                float distanceScale) {
            if (distanceMap != null) {
                float t = distanceScale > 1e-6f ? Mathf.Clamp01(hit.distance / distanceScale) : 0f;
                distanceMap[idx] = new Color32(
                    (byte)(t * 255f + 0.5f),
                    (byte)((1f - t) * 255f + 0.5f),
                    0,
                    255);
            }
            if (normalMap != null) {
                float t = Mathf.Clamp01((hit.normalDot + 1f) * 0.5f);
                normalMap[idx] = new Color32(
                    (byte)((1f - t) * 255f + 0.5f),
                    (byte)(t * 255f + 0.5f),
                    0,
                    255);
            }
            if (rejectMap != null) {
                rejectMap[idx] = new Color32(0, 210, 70, 255);
            }
            if (triangleMap != null) {
                triangleMap[idx] = HashColor(hit.triangleIndex);
            }
        }

        private static void WriteRejectDebug(Color32[] rejectMap, int idx, MeshProjectionRejectReason reason) {
            if (rejectMap == null) return;
            switch (reason) {
                case MeshProjectionRejectReason.Distance:
                    rejectMap[idx] = new Color32(255, 150, 0, 255);
                    break;
                case MeshProjectionRejectReason.NormalAngle:
                    rejectMap[idx] = new Color32(210, 0, 255, 255);
                    break;
                case MeshProjectionRejectReason.Backface:
                    rejectMap[idx] = new Color32(0, 180, 255, 255);
                    break;
                case MeshProjectionRejectReason.RayMiss:
                default:
                    rejectMap[idx] = new Color32(255, 0, 0, 255);
                    break;
            }
        }

        private static Color32 HashColor(int value) {
            unchecked {
                uint x = (uint)(value + 1) * 747796405u + 2891336453u;
                x = ((x >> ((int)(x >> 28) + 4)) ^ x) * 277803737u;
                x = (x >> 22) ^ x;
                byte r = (byte)(40 + (x & 0xBF));
                byte g = (byte)(40 + ((x >> 8) & 0xBF));
                byte b = (byte)(40 + ((x >> 16) & 0xBF));
                return new Color32(r, g, b, 255);
            }
        }

        private static Texture2D CreateDebugTexture(Color32[] pixels, int resolution, string name) {
            if (pixels == null) return null;
            var texture = new Texture2D(resolution, resolution, TextureFormat.RGBA32, mipChain: false, linear: true) {
                hideFlags = HideFlags.HideAndDontSave,
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                name = name,
            };
            texture.SetPixels32(pixels);
            texture.Apply(updateMipmaps: false, makeNoLongerReadable: false);
            return texture;
        }

        internal static (Matrix4x4 source, Matrix4x4 target) ComputeAlignment(
                AlignmentMode mode,
                Mesh source,
                Mesh target,
                Matrix4x4 sourceWorld,
                Matrix4x4 targetWorld) {
            switch (mode) {
                case AlignmentMode.Identity:
                    return (sourceWorld, targetWorld);
                case AlignmentMode.BoundingBox:
                    return ComputeBBoxAlignment(source, target, sourceWorld, targetWorld);
                default:
                    return (sourceWorld, targetWorld);
            }
        }

        internal static (Matrix4x4 source, Matrix4x4 target) ComputeBBoxAlignment(
                Mesh source,
                Mesh target,
                Matrix4x4 sourceWorld,
                Matrix4x4 targetWorld) {
            Bounds sb = source != null ? source.bounds : new Bounds(Vector3.zero, Vector3.one);
            Bounds tb = target != null ? target.bounds : new Bounds(Vector3.zero, Vector3.one);
            Vector3 sSize = sb.size;
            Vector3 tSize = tb.size;
            float sMax = Mathf.Max(sSize.x, Mathf.Max(sSize.y, sSize.z));
            float tMax = Mathf.Max(tSize.x, Mathf.Max(tSize.y, tSize.z));
            float scale = (sMax > 1e-6f) ? (tMax / sMax) : 1f;
            Matrix4x4 align =
                Matrix4x4.Translate(tb.center) *
                Matrix4x4.Scale(new Vector3(scale, scale, scale)) *
                Matrix4x4.Translate(-sb.center);
            return (align, Matrix4x4.identity);
        }
    }
}
