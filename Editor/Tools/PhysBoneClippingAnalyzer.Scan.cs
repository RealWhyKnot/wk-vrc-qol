// PhysBoneClippingAnalyzer.Scan.cs

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

        internal static List<Issue> Scan(
            Animator animator,
            IList<SkinnedMeshRenderer> renderers,
            IList<SkinnedMeshRenderer> excludedRenderers,
            Settings settings,
            StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            log?.AppendLine("  PhysBone clipping: VRChat SDK 3 not available; skipped.");
            return new List<Issue>();
#else
            settings = settings ?? new Settings();
            settings.ResetStats();
            var output = new List<Issue>();
            if (animator == null || renderers == null || renderers.Count == 0) return output;

            var physBones = DiscoverPhysBoneSources(animator, settings, log);
            if (physBones.Count == 0) {
                log?.AppendLine("  PhysBone clipping: no active PhysBone or supported generated PhysBone sources found.");
                return output;
            }

            var excluded = new HashSet<SkinnedMeshRenderer>(
                excludedRenderers == null
                    ? Enumerable.Empty<SkinnedMeshRenderer>()
                    : excludedRenderers.Where(r => r != null));

            var boneToPhysBones = new Dictionary<Transform, List<PhysBoneInfo>>();
            foreach (var info in physBones) {
                foreach (var bone in info.DrivenBones) {
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var list)) {
                        list = new List<PhysBoneInfo>();
                        boneToPhysBones[bone] = list;
                    }
                    list.Add(info);
                }
            }

            var samples = new List<SurfaceSample>();
            var candidates = new List<Candidate>();
            foreach (var renderer in renderers) {
                ScanRenderer(renderer, excluded, settings, boneToPhysBones, samples, candidates, log);
            }
            settings.SurfaceSampleCount = samples.Count;
            settings.DrivenVertexSampleCount = candidates.Count;
            if (candidates.Count == 0 || samples.Count == 0) {
                log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, no weighted mesh vertices above the PhysBone floor.");
                return output;
            }

            var hash = new SpatialHash(Mathf.Max(0.025f, settings.ClearanceMargin));
            foreach (var sample in samples) hash.Add(sample);

            foreach (var candidate in candidates) {
                var info = candidate.PhysBone;
                var estimatedMotion = EstimateMotion(info, candidate.DrivenBone, candidate.Position);
                var searchRadius = Mathf.Clamp(estimatedMotion + settings.ClearanceMargin, settings.ClearanceMargin, 0.18f);
                SurfaceSample nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var sample in hash.Query(candidate.Position, searchRadius)) {
                    if (ReferenceEquals(sample.Renderer, candidate.Renderer) && sample.VertexIndex == candidate.VertexIndex) continue;
                    if (sample.Controllers.Contains(info.Index)) continue;

                    var dist = Vector3.Distance(candidate.Position, sample.Position);
                    if (dist < 0.001f) continue;
                    if (dist < nearestDistance) {
                        nearestDistance = dist;
                        nearest = sample;
                    }
                }
                if (nearest == null) continue;

                float motionFactor = info.HasEffectiveColliders ? 0.35f : 0.75f;
                float unsafeDistance = settings.ClearanceMargin + estimatedMotion * motionFactor;
                if (nearestDistance >= unsafeDistance) continue;

                var overlap = unsafeDistance - nearestDistance;
                var severe = !info.HasEffectiveColliders || nearestDistance < settings.ClearanceMargin;
                output.Add(new Issue {
                    Severity = severe ? Severity.High : Severity.Medium,
                    PhysBoneComponent = info.Component,
                    PhysBoneSourceLabel = info.SourceLabel,
                    PhysBoneRoot = info.Root,
                    DrivenBone = candidate.DrivenBone,
                    Renderer = candidate.Renderer,
                    RendererPath = candidate.RendererPath,
                    VertexIndex = candidate.VertexIndex,
                    WorldPosition = candidate.Position,
                    NearestSurfacePosition = nearest.Position,
                    NearestSurfaceRenderer = nearest.Renderer,
                    NearestSurfacePath = nearest.RendererPath,
                    Weight = candidate.Weight,
                    EstimatedMotion = estimatedMotion,
                    Clearance = nearestDistance,
                    HasEffectiveColliders = info.HasEffectiveColliders,
                    Score = overlap,
                    Reason = BuildReason(info, estimatedMotion, nearestDistance, settings.ClearanceMargin),
                });
            }

            output = output
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Score)
                .GroupBy(i => i.PhysBoneRoot != null ? (UnityEngine.Object)i.PhysBoneRoot : i.PhysBoneComponent)
                .SelectMany(g => g.Take(Mathf.Max(1, settings.MaxIssuesPerPhysBone)))
                .ToList();

            log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, {candidates.Count} driven vertex sample(s), {output.Count} risk(s).");
            return output;
#endif
        }

        internal static List<Issue> ScanOneMesh(
            Animator animator,
            SkinnedMeshRenderer targetRenderer,
            IList<SkinnedMeshRenderer> surfaceRenderers,
            Settings settings,
            StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            log?.AppendLine("  PhysBone clipping: VRChat SDK 3 not available; skipped.");
            return new List<Issue>();
#else
            settings = settings ?? new Settings();
            settings.ResetStats();
            var output = new List<Issue>();
            if (animator == null || targetRenderer == null || targetRenderer.sharedMesh == null) return output;

            var physBones = DiscoverPhysBoneSources(animator, settings, log);
            if (physBones.Count == 0) {
                log?.AppendLine("  PhysBone clipping: no active PhysBone or supported generated PhysBone sources found.");
                return output;
            }

            var boneToPhysBones = new Dictionary<Transform, List<PhysBoneInfo>>();
            foreach (var info in physBones) {
                foreach (var bone in info.DrivenBones) {
                    if (bone == null) continue;
                    if (!boneToPhysBones.TryGetValue(bone, out var list)) {
                        list = new List<PhysBoneInfo>();
                        boneToPhysBones[bone] = list;
                    }
                    list.Add(info);
                }
            }

            var surfaceSet = new HashSet<SkinnedMeshRenderer>();
            surfaceSet.Add(targetRenderer);
            if (surfaceRenderers != null) {
                foreach (var renderer in surfaceRenderers) {
                    if (renderer != null) surfaceSet.Add(renderer);
                }
            }

            var samples = new List<SurfaceSample>();
            var candidates = new List<Candidate>();
            var excluded = new HashSet<SkinnedMeshRenderer>();
            foreach (var renderer in surfaceSet) {
                ScanRenderer(
                    renderer,
                    excluded,
                    settings,
                    boneToPhysBones,
                    samples,
                    candidates,
                    log,
                    collectCandidates: renderer == targetRenderer);
            }
            settings.SurfaceSampleCount = samples.Count;
            settings.DrivenVertexSampleCount = candidates.Count;
            if (candidates.Count == 0 || samples.Count == 0) {
                log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, no weighted vertices above the PhysBone floor on {PathUtility.GetGameObjectPath(targetRenderer.gameObject)}.");
                return output;
            }

            var hash = new SpatialHash(Mathf.Max(0.025f, settings.ClearanceMargin));
            foreach (var sample in samples) hash.Add(sample);

            foreach (var candidate in candidates) {
                var info = candidate.PhysBone;
                var estimatedMotion = EstimateMotion(info, candidate.DrivenBone, candidate.Position);
                var searchRadius = Mathf.Clamp(estimatedMotion + settings.ClearanceMargin, settings.ClearanceMargin, 0.18f);
                SurfaceSample nearest = null;
                float nearestDistance = float.MaxValue;
                foreach (var sample in hash.Query(candidate.Position, searchRadius)) {
                    if (ReferenceEquals(sample.Renderer, candidate.Renderer) && sample.VertexIndex == candidate.VertexIndex) continue;
                    if (sample.Controllers.Contains(info.Index)) continue;

                    var dist = Vector3.Distance(candidate.Position, sample.Position);
                    if (dist < 0.001f) continue;
                    if (dist < nearestDistance) {
                        nearestDistance = dist;
                        nearest = sample;
                    }
                }
                if (nearest == null) continue;

                float motionFactor = info.HasEffectiveColliders ? 0.35f : 0.75f;
                float unsafeDistance = settings.ClearanceMargin + estimatedMotion * motionFactor;
                if (nearestDistance >= unsafeDistance) continue;

                var overlap = unsafeDistance - nearestDistance;
                var severe = !info.HasEffectiveColliders || nearestDistance < settings.ClearanceMargin;
                output.Add(new Issue {
                    Severity = severe ? Severity.High : Severity.Medium,
                    PhysBoneComponent = info.Component,
                    PhysBoneSourceLabel = info.SourceLabel,
                    PhysBoneRoot = info.Root,
                    DrivenBone = candidate.DrivenBone,
                    Renderer = candidate.Renderer,
                    RendererPath = candidate.RendererPath,
                    VertexIndex = candidate.VertexIndex,
                    WorldPosition = candidate.Position,
                    NearestSurfacePosition = nearest.Position,
                    NearestSurfaceRenderer = nearest.Renderer,
                    NearestSurfacePath = nearest.RendererPath,
                    Weight = candidate.Weight,
                    EstimatedMotion = estimatedMotion,
                    Clearance = nearestDistance,
                    HasEffectiveColliders = info.HasEffectiveColliders,
                    Score = overlap,
                    Reason = BuildReason(info, estimatedMotion, nearestDistance, settings.ClearanceMargin),
                });
            }

            output = output
                .OrderByDescending(i => i.Severity)
                .ThenByDescending(i => i.Score)
                .GroupBy(i => i.PhysBoneRoot != null ? (UnityEngine.Object)i.PhysBoneRoot : i.PhysBoneComponent)
                .SelectMany(g => g.Take(Mathf.Max(1, settings.MaxIssuesPerPhysBone)))
                .ToList();

            log?.AppendLine($"  PhysBone clipping: {DescribeSourceCounts(settings)}, {candidates.Count} driven vertex sample(s) on one mesh, {samples.Count} surface sample(s), {output.Count} risk(s).");
            return output;
#endif
        }
    }
}
