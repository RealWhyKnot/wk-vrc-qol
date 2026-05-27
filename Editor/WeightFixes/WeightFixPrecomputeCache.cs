// WeightFixPrecomputeCache.cs

using System.Collections.Generic;
using UnityEditor;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.WeightFixes {

    internal static class WeightFixPrecomputeCache {

        public static bool TryLoad(
                WhyKnotWeightFixIntent intent,
                string signature,
                out List<DetectedIssue> issues) {

            issues = null;
            if (!IntentPrecomputeUtility.HasValidWeightFixCache(intent, signature)) return false;
            issues = FromCache(intent.precomputedIssues);
            return true;
        }

        public static void Store(
                WhyKnotWeightFixIntent intent,
                string signature,
                IList<DetectedIssue> issues) {

            if (intent == null) return;
            intent.precomputeSignature = signature;
            intent.precomputeVersion = IntentPrecomputeUtility.WeightFixVersion;
            if (intent.precomputedIssues == null) {
                intent.precomputedIssues = new List<WeightFixPrecomputedIssue>();
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

        public static List<DetectedIssue> FromCache(IList<WeightFixPrecomputedIssue> cached) {
            var issues = new List<DetectedIssue>();
            if (cached == null) return issues;
            foreach (var issue in cached) {
                if (issue == null) continue;
                issues.Add(new DetectedIssue {
                    Renderer = issue.renderer,
                    RendererPath = issue.rendererPath,
                    VertexIndex = issue.vertexIndex,
                    WorldPosition = issue.worldPosition,
                    VertexSide = (BoneSide)issue.vertexSide,
                    OffendingBone = issue.offendingBone,
                    BoneSide = (BoneSide)issue.boneSide,
                    Weight = issue.weight,
                    Category = (IssueCategory)issue.category,
                });
            }
            return issues;
        }

        public static WeightFixPrecomputedIssue ToCache(DetectedIssue issue) {
            return new WeightFixPrecomputedIssue {
                renderer = issue.Renderer,
                rendererPath = issue.RendererPath,
                vertexIndex = issue.VertexIndex,
                worldPosition = issue.WorldPosition,
                vertexSide = (int)issue.VertexSide,
                offendingBone = issue.OffendingBone,
                boneSide = (int)issue.BoneSide,
                weight = issue.Weight,
                category = (int)issue.Category,
            };
        }
    }
}
