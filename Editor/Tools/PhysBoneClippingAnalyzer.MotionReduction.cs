// PhysBoneClippingAnalyzer.MotionReduction.cs

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

        internal static bool CanReduceMotion(Issue issue) {
#if !VRC_SDK_VRCSDK3
            return false;
#else
            if (issue == null || issue.PhysBoneComponent == null) return false;
            var component = issue.PhysBoneComponent;
            if (component is VRCPhysBone) return true;
            if (LooksLikeMarshmallowAuthoringComponent(component)) return true;
            return HasWritableFloat(component, "pull", "_pull", "m_Pull", "spring", "_spring", "momentum", "_momentum",
                "stiffness", "_stiffness", "m_Stiffness", "gravity", "_gravity", "maxStretch", "_maxStretch");
#endif
        }

        internal static MotionReductionResult ReduceMotionIssues(IList<Issue> issues, StringBuilder log = null) {
#if !VRC_SDK_VRCSDK3
            return new MotionReductionResult {
                UnsupportedSources = issues == null ? 0 : issues.Count,
                Summary = "Motion reduction needs VRChat SDK 3 PhysBone types.",
            };
#else
            var result = new MotionReductionResult();
            if (issues == null || issues.Count == 0) {
                result.Summary = "No risks selected for motion reduction.";
                return result;
            }

            var fixedComponents = new HashSet<Component>();
            var unsupportedComponents = new HashSet<Component>();
            foreach (var issue in issues.Where(i => i != null && i.PhysBoneComponent != null)) {
                var component = issue.PhysBoneComponent;
                if (fixedComponents.Contains(component) || unsupportedComponents.Contains(component)) continue;

                bool changed = ApplyMotionReduction(component, issue, log);
                if (changed) {
                    fixedComponents.Add(component);
                } else {
                    unsupportedComponents.Add(component);
                }
            }

            result.SourcesChanged = fixedComponents.Count;
            result.UnsupportedSources = unsupportedComponents.Count;
            result.IssuesCovered = issues.Count(i => i != null && i.PhysBoneComponent != null && fixedComponents.Contains(i.PhysBoneComponent));
            result.Summary = result.SourcesChanged == 0
                ? "No supported PhysBone settings could be adjusted."
                : $"Motion reduction adjusted {result.SourcesChanged} PhysBone source(s) covering {result.IssuesCovered} risk row(s).";
            return result;
#endif
        }

#if VRC_SDK_VRCSDK3
        private static bool ApplyMotionReduction(Component component, Issue issue, StringBuilder log) {
            if (component == null) return false;
            if (component is VRCPhysBone pb) {
                return ApplyLivePhysBoneMotionReduction(pb, issue, log);
            }

            if (LooksLikeMarshmallowAuthoringComponent(component)) {
                return ApplyMarshmallowMotionReduction(component, issue, log);
            }

            return ApplyReflectedMotionReduction(component, issue, log);
        }

        private static bool ApplyLivePhysBoneMotionReduction(VRCPhysBone pb, Issue issue, StringBuilder log) {
            Undo.RecordObject(pb, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            var nextPull = Mathf.Clamp01(Mathf.Max(pb.pull + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f));
            if (!Mathf.Approximately(pb.pull, nextPull)) {
                pb.pull = nextPull;
                changed = true;
            }

            var nextStiffness = Mathf.Clamp01(Mathf.Max(pb.stiffness + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f));
            if (!Mathf.Approximately(pb.stiffness, nextStiffness)) {
                pb.stiffness = nextStiffness;
                changed = true;
            }

            var nextSpring = Mathf.Clamp01(pb.spring * (high ? 0.45f : 0.65f));
            if (!Mathf.Approximately(pb.spring, nextSpring)) {
                pb.spring = nextSpring;
                changed = true;
            }

            var nextGravity = Mathf.MoveTowards(pb.gravity, 0f, high ? 0.30f : 0.15f);
            if (!Mathf.Approximately(pb.gravity, nextGravity)) {
                pb.gravity = nextGravity;
                changed = true;
            }

            var nextMaxStretch = Mathf.Max(0f, pb.maxStretch * (high ? 0.40f : 0.60f));
            if (!Mathf.Approximately(pb.maxStretch, nextMaxStretch)) {
                pb.maxStretch = nextMaxStretch;
                changed = true;
            }
            if (issue != null && !issue.HasEffectiveColliders) {
                changed |= TrySetAdvancedBoolTrue(pb, "allowCollision");
            }

            if (changed) {
                EditorUtility.SetDirty(pb);
                log?.AppendLine($"  Motion reduction: tightened live PhysBone on {PathUtility.GetGameObjectPath(pb.gameObject)}.");
            }
            return changed;
        }

        private static bool ApplyMarshmallowMotionReduction(Component component, Issue issue, StringBuilder log) {
            Undo.RecordObject(component, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f)),
                "_PhysBone_Pull", "PhysBone_Pull");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f)),
                "_PhysBone_Stiffness", "PhysBone_Stiffness");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(v * (high ? 0.45f : 0.65f)),
                "_PhysBone_Momentum", "PhysBone_Momentum");
            changed |= TrySetFloat(component, v => Mathf.MoveTowards(v, 0f, high ? 0.30f : 0.15f),
                "_PhysBone_Gravity", "PhysBone_Gravity");
            changed |= TrySetFloat(component, v => Mathf.Max(0f, v * (high ? 0.40f : 0.60f)),
                "_PhysBone_Max_Stretch", "PhysBone_Max_Stretch");
            changed |= TrySetBool(component, true, "_breastInterference_BreakPreventionCollider", "breastInterference_BreakPreventionCollider");
            changed |= TrySetAdvancedBoolTrue(component, "_PhysBone_AllowCollision", "PhysBone_AllowCollision");

            if (changed) {
                EditorUtility.SetDirty(component);
                log?.AppendLine($"  Motion reduction: tightened Marshmallow PB settings on {PathUtility.GetGameObjectPath(component.gameObject)}.");
            }
            return changed;
        }

        private static bool ApplyReflectedMotionReduction(Component component, Issue issue, StringBuilder log) {
            Undo.RecordObject(component, "Avatar QoL PhysBone clipping motion reduction");
            bool high = issue != null && issue.Severity == Severity.High;
            bool changed = false;

            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.60f : 0.45f)),
                "pull", "_pull", "m_Pull");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(Mathf.Max(v + (high ? 0.25f : 0.15f), high ? 0.55f : 0.40f)),
                "stiffness", "_stiffness", "m_Stiffness");
            changed |= TrySetFloat(component, v => Mathf.Clamp01(v * (high ? 0.45f : 0.65f)),
                "spring", "_spring", "momentum", "_momentum", "m_Elasticity");
            changed |= TrySetFloat(component, v => Mathf.MoveTowards(v, 0f, high ? 0.30f : 0.15f),
                "gravity", "_gravity", "m_Gravity");
            changed |= TrySetFloat(component, v => Mathf.Max(0f, v * (high ? 0.40f : 0.60f)),
                "maxStretch", "_maxStretch", "stretch", "max_stretch");
            if (issue != null && !issue.HasEffectiveColliders) {
                changed |= TrySetAdvancedBoolTrue(component, "allowCollision", "_allowCollision");
            }

            if (changed) {
                EditorUtility.SetDirty(component);
                log?.AppendLine($"  Motion reduction: tightened reflected PhysBone settings on {PathUtility.GetGameObjectPath(component.gameObject)}.");
            }
            return changed;
        }

        private static HashSet<Transform> BuildDrivenBoneSet(Transform root, HashSet<Transform> ignored) {
            var driven = new HashSet<Transform>();
            if (root == null) return driven;
            ignored = ignored ?? new HashSet<Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true)) {
                if (t == null) continue;
                if (IsIgnoredByPhysBone(t, root, ignored)) continue;
                driven.Add(t);
            }
            return driven;
        }

        private static bool IsIgnoredByPhysBone(Transform t, Transform root, HashSet<Transform> ignored) {
            if (ignored.Count == 0) return false;
            var cur = t;
            while (cur != null) {
                if (ignored.Contains(cur)) return true;
                if (cur == root) break;
                cur = cur.parent;
            }
            return false;
        }

#endif
    }
}
