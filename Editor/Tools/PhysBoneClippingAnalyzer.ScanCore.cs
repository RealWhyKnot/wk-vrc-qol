// PhysBoneClippingAnalyzer.ScanCore.cs

using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static partial class PhysBoneClippingAnalyzer {

#if VRC_SDK_VRCSDK3
        private static void ScanRenderer(
            SkinnedMeshRenderer renderer,
            HashSet<SkinnedMeshRenderer> excluded,
            Settings settings,
            Dictionary<Transform, List<PhysBoneInfo>> boneToPhysBones,
            List<SurfaceSample> samples,
            List<Candidate> candidates,
            StringBuilder log,
            bool collectCandidates = true) {
            if (renderer == null || renderer.sharedMesh == null) return;
            if (excluded.Contains(renderer)) return;
            var mesh = renderer.sharedMesh;
            if (!mesh.isReadable) return;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return;

            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var bindposes = mesh.bindposes;
            if (verts.Length != mesh.vertexCount || bonesPerVertex.Length != mesh.vertexCount) return;

            int cursor = 0;
            var path = PathUtility.GetGameObjectPath(renderer.gameObject);
            for (int v = 0; v < mesh.vertexCount; v++) {
                int weightCount = bonesPerVertex[v];
                var world = ComputeWorldPosition(verts[v], cursor, weightCount);
                var sample = new SurfaceSample {
                    Renderer = renderer,
                    RendererPath = path,
                    VertexIndex = v,
                    Position = world,
                };

                var bestByPhysBone = collectCandidates ? new Dictionary<int, Candidate>() : null;
                for (int w = 0; w < weightCount; w++) {
                    var bw = weights[cursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bw.weight < settings.WeightFloor) continue;
                    var bone = bones[bw.boneIndex];
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var owners)) continue;
                    foreach (var info in owners) {
                        sample.Controllers.Add(info.Index);
                        if (!collectCandidates) continue;
                        if (!bestByPhysBone.TryGetValue(info.Index, out var existing) || bw.weight > existing.Weight) {
                            bestByPhysBone[info.Index] = new Candidate {
                                PhysBone = info,
                                DrivenBone = bone,
                                Renderer = renderer,
                                RendererPath = path,
                                VertexIndex = v,
                                Position = world,
                                Weight = bw.weight,
                            };
                        }
                    }
                }

                samples.Add(sample);
                if (collectCandidates) candidates.AddRange(bestByPhysBone.Values);
                cursor += weightCount;
            }

            Vector3 ComputeWorldPosition(Vector3 meshLocal, int weightCursor, int weightCountForVertex) {
                int primaryIdx = -1;
                float primaryWeight = 0f;
                for (int w = 0; w < weightCountForVertex; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bones[bw.boneIndex] == null) continue;
                    if (bw.weight > primaryWeight) {
                        primaryWeight = bw.weight;
                        primaryIdx = bw.boneIndex;
                    }
                }

                if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                    var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                    return bones[primaryIdx].TransformPoint(boneLocal);
                }
                return renderer.transform.TransformPoint(meshLocal);
            }
        }

        private static float EstimateMotion(PhysBoneInfo info, Transform drivenBone, Vector3 vertexWorld) {
            if (info.Root == null || drivenBone == null) return 0f;
            float lever = Mathf.Max(
                Mathf.Max(Vector3.Distance(info.Root.position, drivenBone.position),
                    Vector3.Distance(info.Root.position, vertexWorld)),
                0.01f);
            float radians = info.EstimatedSwingDegrees * Mathf.Deg2Rad;
            float swing = 2f * lever * Mathf.Sin(radians * 0.5f);
            float stretch = lever * info.MaxStretch;
            return Mathf.Clamp(swing + stretch + info.Radius, 0f, Mathf.Max(0.05f, lever * 1.5f + info.Radius));
        }

        private static float EstimateSwingDegrees(
            float rawPull,
            float rawStiffness,
            float rawSpring,
            float rawGravity,
            float rawGravityFalloff,
            bool allowGrabbing,
            bool allowPosing,
            float reflectedLimit) {
            float pull = Mathf.Clamp01(rawPull);
            float stiffness = Mathf.Clamp01(rawStiffness);
            float spring = Mathf.Clamp01(rawSpring);
            float gravity = Mathf.Abs(rawGravity) * (1f - Mathf.Clamp01(rawGravityFalloff) * 0.5f);

            float angle = 72f;
            angle -= pull * 28f;
            angle -= stiffness * 30f;
            angle += spring * 18f;
            angle += gravity * 30f;
            if (allowGrabbing || allowPosing) {
                angle += 15f;
            }

            if (reflectedLimit > 0.01f) angle = Mathf.Min(angle, reflectedLimit);
            return Mathf.Clamp(angle, 8f, 110f);
        }

        private static float TryGetReflectedLimitAngle(object source) {
            var limitType = GetMemberValue(source, "limitType")?.ToString();
            if (!string.IsNullOrEmpty(limitType) && limitType.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0) {
                return -1f;
            }

            float best = -1f;
            foreach (var name in new[] { "maxAngleX", "maxAngleZ", "maxAngle", "limitAngle", "_PhysBone_Limit_Angle", "_inertia_LimitAngle" }) {
                var value = ReadFloat(source, -1f, name);
                if (value > best) best = value;
            }
            return best;
        }

        private static string BuildReason(PhysBoneInfo info, float estimatedMotion, float clearance, float margin) {
            var colliderText = info.HasEffectiveColliders
                ? "colliders are assigned, but the clearance is still small"
                : "no effective PhysBone colliders are assigned";
            var sourcePrefix = info.SourceLabel == "VRCPhysBone" || string.IsNullOrEmpty(info.SourceLabel)
                ? ""
                : $"{info.SourceLabel}: ";
            return $"{sourcePrefix}{ToCm(estimatedMotion)} cm estimated motion envelope vs {ToCm(clearance)} cm nearest mesh clearance ({colliderText}; margin {ToCm(margin)} cm).";
        }

#endif
    }
}
