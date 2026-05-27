// PhysBoneClippingAnalyzer.Sources.cs

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
        private sealed class PhysBoneInfo {
            public int Index;
            public Component Component;
            public string SourceLabel;
            public Transform Root;
            public HashSet<Transform> DrivenBones = new HashSet<Transform>();
            public bool HasEffectiveColliders;
            public float EstimatedSwingDegrees;
            public float Radius;
            public float MaxStretch;
            public float Pull;
            public float Spring;
            public float Stiffness;
            public float Gravity;
            public float GravityFalloff;
        }

        private sealed class SurfaceSample {
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 Position;
            public readonly HashSet<int> Controllers = new HashSet<int>();
        }

        private sealed class Candidate {
            public PhysBoneInfo PhysBone;
            public Transform DrivenBone;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public int VertexIndex;
            public Vector3 Position;
            public float Weight;
        }

        private static List<PhysBoneInfo> DiscoverPhysBoneSources(Animator animator, Settings settings, StringBuilder log) {
            var infos = new List<PhysBoneInfo>();
            if (animator == null) return infos;

            var seen = new HashSet<Component>();
            foreach (var pb in animator.GetComponentsInChildren<VRCPhysBone>(true)) {
                if (!IsUsableComponent(pb)) continue;
                seen.Add(pb);
                var info = CreateInfo(infos.Count, pb);
                if (info.Root == null || info.DrivenBones.Count == 0) continue;
                infos.Add(info);
                settings.NativePhysBoneCount++;
            }

            var customTypes = new Dictionary<string, int>();
            foreach (var component in animator.GetComponentsInChildren<Component>(true)) {
                if (!IsUsableComponent(component) || seen.Contains(component)) continue;

                int added = TryAddMarshmallowPhysBoneSources(component, infos);
                if (added > 0) {
                    seen.Add(component);
                    settings.CustomPhysBoneCount += added;
                    AddTypeCount(customTypes, component.GetType(), added);
                    continue;
                }

                if (!LooksLikeReflectedPhysBoneSource(component)) continue;
                var reflected = CreateInfoFromReflectedComponent(infos.Count, component, "Custom PhysBone");
                if (reflected == null || reflected.Root == null || reflected.DrivenBones.Count == 0) continue;
                infos.Add(reflected);
                seen.Add(component);
                settings.CustomPhysBoneCount++;
                AddTypeCount(customTypes, component.GetType(), 1);
            }

            log?.AppendLine($"  PhysBone clipping sources: {DescribeSourceCounts(settings)}.");
            foreach (var pair in customTypes.OrderBy(p => p.Key)) {
                log?.AppendLine($"    custom/generated: {pair.Value} from {pair.Key}");
            }
            return infos;
        }

        private static bool IsUsableComponent(Component component) {
            if (component == null) return false;
            if (component is Behaviour behaviour && !behaviour.enabled) return false;
            return true;
        }

        private static void AddTypeCount(Dictionary<string, int> counts, Type type, int amount) {
            var name = type != null ? type.FullName ?? type.Name : "Unknown";
            counts[name] = counts.TryGetValue(name, out var existing) ? existing + amount : amount;
        }

        private static string DescribeSourceCounts(Settings settings) {
            int total = settings.NativePhysBoneCount + settings.CustomPhysBoneCount;
            if (settings.CustomPhysBoneCount == 0) return $"{total} PhysBone source(s)";
            return $"{total} PhysBone source(s): {settings.NativePhysBoneCount} live, {settings.CustomPhysBoneCount} generated/custom";
        }

        private static PhysBoneInfo CreateInfo(int index, VRCPhysBone pb) {
            var root = pb.rootTransform != null ? pb.rootTransform : pb.transform;
            bool collisionDisabled = pb.allowCollision.ToString() == "False";
            bool hasColliders = !collisionDisabled && pb.colliders != null && pb.colliders.Any(c => c != null);
            return new PhysBoneInfo {
                Index = index,
                Component = pb,
                SourceLabel = "VRCPhysBone",
                Root = root,
                DrivenBones = BuildDrivenBoneSet(root, TransformEnumerableToSet(pb.ignoreTransforms)),
                HasEffectiveColliders = hasColliders,
                EstimatedSwingDegrees = EstimateSwingDegrees(
                    pb.pull,
                    pb.stiffness,
                    pb.spring,
                    pb.gravity,
                    pb.gravityFalloff,
                    pb.allowGrabbing.ToString() != "False",
                    pb.allowPosing.ToString() != "False",
                    TryGetReflectedLimitAngle(pb)),
                Radius = Mathf.Max(0f, pb.radius),
                MaxStretch = Mathf.Max(0f, pb.maxStretch),
                Pull = Mathf.Clamp01(pb.pull),
                Spring = Mathf.Clamp01(pb.spring),
                Stiffness = Mathf.Clamp01(pb.stiffness),
                Gravity = pb.gravity,
                GravityFalloff = Mathf.Clamp01(pb.gravityFalloff),
            };
        }

        private static int TryAddMarshmallowPhysBoneSources(Component component, List<PhysBoneInfo> infos) {
            if (component == null || !LooksLikeMarshmallowAuthoringComponent(component)) return 0;

            var roots = new List<Transform>();
            var left = ReadTransform(component, "_Breast_L", "Breast_L", "breast_L");
            var right = ReadTransform(component, "_Breast_R", "Breast_R", "breast_R");
            if (left != null) roots.Add(left);
            if (right != null && right != left) roots.Add(right);
            if (roots.Count == 0) return 0;

            float pull = ReadFloat(component, 0.1f, "_PhysBone_Pull", "PhysBone_Pull", "pull");
            float spring = ReadFloat(component, 0.5f, "_PhysBone_Momentum", "PhysBone_Momentum", "spring");
            float stiffness = ReadFloat(component, 0.25f, "_PhysBone_Stiffness", "PhysBone_Stiffness", "stiffness");
            float gravity = ReadFloat(component, 0.02f, "_PhysBone_Gravity", "PhysBone_Gravity", "gravity");
            float gravityFalloff = ReadFloat(component, 1f, "_PhysBone_GravityFalloff", "PhysBone_GravityFalloff", "gravityFalloff");
            float radius = ReadFloat(component, 0.06f, "_PhysBone_Collision_Radius", "PhysBone_Collision_Radius", "radius");
            float maxStretch = ReadFloat(component, 0.3f, "_PhysBone_Max_Stretch", "PhysBone_Max_Stretch", "maxStretch");
            float limitAngle = ReadFloat(component, 35f, "_PhysBone_Limit_Angle", "PhysBone_Limit_Angle", "maxAngleX", "limitAngle");
            bool allowCollision = ReadAdvancedBool(component, true, "_PhysBone_AllowCollision", "PhysBone_AllowCollision", "allowCollision");
            bool allowGrabbing = ReadAdvancedBool(component, true, "_PhysBone_AllowGrabbing", "PhysBone_AllowGrabbing", "allowGrabbing");
            bool allowPosing = ReadAdvancedBool(component, false, "_PhysBone_AllowPosing", "PhysBone_AllowPosing", "allowPosing");

            bool noSquish = ReadBool(component, false, "_nosquish", "nosquish");
            bool onlySquish = ReadBool(component, false, "_onlysquish", "onlysquish");
            if (noSquish || onlySquish) maxStretch = 0f;
            if (onlySquish) {
                pull = Mathf.Max(pull, 0.85f);
                stiffness = Mathf.Max(stiffness, 0.75f);
            }

            int added = 0;
            foreach (var root in roots) {
                var driven = BuildDrivenBoneSet(root, null);
                if (driven.Count == 0) continue;
                infos.Add(new PhysBoneInfo {
                    Index = infos.Count,
                    Component = component,
                    SourceLabel = "Marshmallow PB generated PhysBone",
                    Root = root,
                    DrivenBones = driven,
                    HasEffectiveColliders = allowCollision,
                    EstimatedSwingDegrees = EstimateSwingDegrees(
                        pull,
                        stiffness,
                        spring,
                        gravity,
                        gravityFalloff,
                        allowGrabbing,
                        allowPosing,
                        limitAngle),
                    Radius = Mathf.Max(0f, radius),
                    MaxStretch = Mathf.Max(0f, maxStretch),
                    Pull = Mathf.Clamp01(pull),
                    Spring = Mathf.Clamp01(spring),
                    Stiffness = Mathf.Clamp01(stiffness),
                    Gravity = gravity,
                    GravityFalloff = Mathf.Clamp01(gravityFalloff),
                });
                added++;
            }
            return added;
        }

        private static bool LooksLikeMarshmallowAuthoringComponent(Component component) {
            var typeText = GetTypeText(component.GetType());
            if (typeText.IndexOf("marshmallow", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeText.IndexOf("cake_PB", StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
            return HasMember(component, "_Breast_L", "_Breast_R") &&
                   HasMember(component, "_PhysBone_Pull", "_PhysBone_Collision_Radius", "_PhysBone_Max_Stretch");
        }

        private static bool LooksLikeReflectedPhysBoneSource(Component component) {
            if (component == null) return false;
            var typeText = GetTypeText(component.GetType());
            if (typeText.IndexOf("Collider", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            if (typeText.IndexOf("PhysBone", StringComparison.OrdinalIgnoreCase) < 0 &&
                typeText.IndexOf("DynamicBone", StringComparison.OrdinalIgnoreCase) < 0) {
                return false;
            }
            return HasMember(component, "rootTransform", "root", "physBoneRoot", "targetTransform") &&
                   HasMember(component, "pull", "spring", "stiffness", "gravity", "radius", "maxStretch");
        }

        private static PhysBoneInfo CreateInfoFromReflectedComponent(int index, Component component, string sourceLabel) {
            var root = ReadTransform(component, "rootTransform", "RootTransform", "root", "Root", "_rootTransform", "_root", "physBoneRoot", "targetTransform");
            if (root == null) return null;

            var ignored = ReadTransformSet(component, "ignoreTransforms", "_ignoreTransforms", "ignoredTransforms", "exclusions");
            var driven = BuildDrivenBoneSet(root, ignored);
            if (driven.Count == 0) return null;

            float pull = ReadFloat(component, 0f, "pull", "_pull", "m_Pull");
            float spring = ReadFloat(component, 0f, "spring", "_spring", "momentum", "_momentum", "m_Elasticity");
            float stiffness = ReadFloat(component, 0f, "stiffness", "_stiffness", "m_Stiffness");
            float gravity = ReadFloat(component, 0f, "gravity", "_gravity", "m_Gravity");
            float gravityFalloff = ReadFloat(component, 0f, "gravityFalloff", "_gravityFalloff");
            float radius = ReadFloat(component, 0f, "radius", "_radius", "m_Radius");
            float maxStretch = ReadFloat(component, 0f, "maxStretch", "_maxStretch", "stretch", "max_stretch");
            bool allowCollision = ReadAdvancedBool(component, true, "allowCollision", "_allowCollision");
            bool allowGrabbing = ReadAdvancedBool(component, true, "allowGrabbing", "_allowGrabbing");
            bool allowPosing = ReadAdvancedBool(component, false, "allowPosing", "_allowPosing");
            bool hasColliders = allowCollision && CountObjectReferences(GetMemberValue(component, "colliders", "_colliders", "collisionColliders")) > 0;

            return new PhysBoneInfo {
                Index = index,
                Component = component,
                SourceLabel = sourceLabel,
                Root = root,
                DrivenBones = driven,
                HasEffectiveColliders = hasColliders,
                EstimatedSwingDegrees = EstimateSwingDegrees(
                    pull,
                    stiffness,
                    spring,
                    gravity,
                    gravityFalloff,
                    allowGrabbing,
                    allowPosing,
                    TryGetReflectedLimitAngle(component)),
                Radius = Mathf.Max(0f, radius),
                MaxStretch = Mathf.Max(0f, maxStretch),
                Pull = Mathf.Clamp01(pull),
                Spring = Mathf.Clamp01(spring),
                Stiffness = Mathf.Clamp01(stiffness),
                Gravity = gravity,
                GravityFalloff = Mathf.Clamp01(gravityFalloff),
            };
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
            if (ignored == null || ignored.Count == 0) return false;
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
