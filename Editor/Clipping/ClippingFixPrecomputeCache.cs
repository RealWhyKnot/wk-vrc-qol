// ClippingFixPrecomputeCache.cs

using System.Collections.Generic;
using UnityEditor;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Clipping {

    internal static class ClippingFixPrecomputeCache {

        public static bool TryLoad(
                WhyKnotClippingFixIntent intent,
                string signature,
                out List<ClippingFixer.Issue> issues) {

            issues = null;
            if (!IntentPrecomputeUtility.HasValidClippingCache(intent, signature)) return false;
            issues = FromCache(intent.precomputedIssues);
            return true;
        }

        public static void Store(
                WhyKnotClippingFixIntent intent,
                string signature,
                IList<ClippingFixer.Issue> issues) {

            if (intent == null) return;
            intent.precomputeSignature = signature;
            intent.precomputeVersion = IntentPrecomputeUtility.ClippingVersion;
            if (intent.precomputedIssues == null) {
                intent.precomputedIssues = new List<ClippingFixPrecomputedIssue>();
            } else {
                intent.precomputedIssues.Clear();
            }
            if (issues != null) {
                foreach (var issue in issues) {
                    if (issue == null) continue;
                    intent.precomputedIssues.Add(ToCache(issue));
                }
            }
            EditorUtility.SetDirty(intent);
        }

        public static List<ClippingFixer.Issue> FromCache(IList<ClippingFixPrecomputedIssue> cached) {
            var issues = new List<ClippingFixer.Issue>();
            if (cached == null) return issues;
            foreach (var issue in cached) {
                if (issue == null) continue;
                issues.Add(new ClippingFixer.Issue {
                    Kind = (ClippingFixer.IssueKind)issue.kind,
                    Renderer = issue.renderer,
                    RendererPath = issue.rendererPath,
                    ComparisonRenderer = issue.comparisonRenderer,
                    ComparisonPath = issue.comparisonPath,
                    VertexIndex = issue.vertexIndex,
                    TargetTriangleIndex = issue.targetTriangleIndex,
                    ComparisonTriangleIndex = issue.comparisonTriangleIndex,
                    AffectedVertexIndices = issue.affectedVertexIndices ?? new int[0],
                    WorldPosition = issue.worldPosition,
                    NearestSurfacePosition = issue.nearestSurfacePosition,
                    SurfaceNormal = issue.surfaceNormal,
                    PushWorld = issue.pushWorld,
                    PenetrationDepth = issue.penetrationDepth,
                    Score = issue.score,
                    Reason = issue.reason,
                    PhysBoneComponent = issue.physBoneComponent,
                    PhysBoneSourceLabel = issue.physBoneSourceLabel,
                    PhysBoneRoot = issue.physBoneRoot,
                    DrivenBone = issue.drivenBone,
                    PhysBoneWeight = issue.physBoneWeight,
                    EstimatedMotion = issue.estimatedMotion,
                    Clearance = issue.clearance,
                    HasEffectiveColliders = issue.hasEffectiveColliders,
                    PhysBoneHighSeverity = issue.physBoneHighSeverity,
                });
            }
            return issues;
        }

        public static ClippingFixPrecomputedIssue ToCache(ClippingFixer.Issue issue) {
            return new ClippingFixPrecomputedIssue {
                kind = (int)issue.Kind,
                renderer = issue.Renderer,
                rendererPath = issue.RendererPath,
                comparisonRenderer = issue.ComparisonRenderer,
                comparisonPath = issue.ComparisonPath,
                vertexIndex = issue.VertexIndex,
                targetTriangleIndex = issue.TargetTriangleIndex,
                comparisonTriangleIndex = issue.ComparisonTriangleIndex,
                affectedVertexIndices = issue.AffectedVertexIndices ?? new int[0],
                worldPosition = issue.WorldPosition,
                nearestSurfacePosition = issue.NearestSurfacePosition,
                surfaceNormal = issue.SurfaceNormal,
                pushWorld = issue.PushWorld,
                penetrationDepth = issue.PenetrationDepth,
                score = issue.Score,
                reason = issue.Reason,
                physBoneComponent = issue.PhysBoneComponent,
                physBoneSourceLabel = issue.PhysBoneSourceLabel,
                physBoneRoot = issue.PhysBoneRoot,
                drivenBone = issue.DrivenBone,
                physBoneWeight = issue.PhysBoneWeight,
                estimatedMotion = issue.EstimatedMotion,
                clearance = issue.Clearance,
                hasEffectiveColliders = issue.HasEffectiveColliders,
                physBoneHighSeverity = issue.PhysBoneHighSeverity,
            };
        }
    }
}
