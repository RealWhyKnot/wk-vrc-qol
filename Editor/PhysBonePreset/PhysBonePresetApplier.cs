// PhysBonePresetApplier.cs
//
// Driver for WhyKnotPhysBonePresetIntent at play / build. Looks up the
// preset by stable id, rebuilds the BoneSelectionAnalysis from the
// intent's bone list, asks the preset to build a plan, scales every
// numeric parameter by the intent's tweak multipliers, and spawns the
// VRCPhysBone + VRCPhysBoneCollider components.
//
// Two entry points. Both compute the same plan; only the spawn step
// differs:
//
//   ApplyDestructive: forwards the plan to PhysBonePlanApplier, which
//     wraps the spawn in a single Undo group. Used by the PhysBone
//     Preset window's Apply button.
//
//   ApplyNonDestructive: spawns the same components via direct
//     AddComponent / new GameObject, tracks every spawned component
//     and collider holder via AvatarIntentSession so Dispose tears the
//     entire setup back down at upload-post-process or play-mode exit.
//     Never calls Undo.* -- the build hook is not user-initiated.
//
// SDK gate: VRCPhysBone / VRCPhysBoneCollider live in the VRChat SDK.
// Wrapped in #if VRC_SDK_VRCSDK3 so this file compiles without the SDK;
// outside the guard ApplyNonDestructive becomes a no-op that logs once.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;

#if VRC_SDK_VRCSDK3
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal static class PhysBonePresetApplier {

        internal sealed class Result {
            public string Summary = "";
            public int PhysBonesSpawned;
            public int CollidersSpawned;
            public bool ConfigurationError;

            public bool DidAnything => PhysBonesSpawned > 0 || CollidersSpawned > 0;
        }

        internal static bool SdkAvailable {
            get {
#if VRC_SDK_VRCSDK3
                return true;
#else
                return false;
#endif
            }
        }

        /// <summary>
        /// Destructive entry. Builds the plan, applies tweaks, forwards to
        /// PhysBonePlanApplier.Apply (which uses Undo). Used by the
        /// "Apply as Intent + run now" path from the window.
        /// </summary>
        public static Result ApplyDestructive(IList<Transform> bones, string presetId,
                                              float pullMult, float springMult, float stiffMult,
                                              float gravityMult, float radiusMult) {
            var result = new Result();
            if (!TryBuildPlan(bones, presetId, pullMult, springMult, stiffMult, gravityMult, radiusMult,
                              out var plan, out var error)) {
                result.Summary = error;
                result.ConfigurationError = true;
                return result;
            }
            int created = PhysBonePlanApplier.Apply(plan, out var applyError);
            if (created < 0) {
                result.Summary = "Apply failed: " + (applyError ?? "unknown");
                result.ConfigurationError = true;
                return result;
            }
            result.PhysBonesSpawned = created;
            result.CollidersSpawned = plan.Colliders.Count;
            result.Summary = $"Spawned {created} PhysBone(s), {plan.Colliders.Count} collider(s) from preset \"{plan.PresetDisplayName}\".";
            return result;
        }

        /// <summary>
        /// Non-destructive entry. Builds the plan and spawns everything into
        /// the avatar in memory; the session destroys every spawned component
        /// and collider holder GameObject on Dispose.
        /// </summary>
        public static Result ApplyNonDestructive(IList<Transform> bones, string presetId,
                                                 float pullMult, float springMult, float stiffMult,
                                                 float gravityMult, float radiusMult,
                                                 AvatarIntentSession session) {
            var result = new Result();
#if !VRC_SDK_VRCSDK3
            result.Summary = "VRChat SDK 3 (PhysBone) is not installed.";
            result.ConfigurationError = true;
            return result;
#else
            if (session == null) {
                result.Summary = "Internal error: no session supplied.";
                result.ConfigurationError = true;
                return result;
            }
            if (!TryBuildPlan(bones, presetId, pullMult, springMult, stiffMult, gravityMult, radiusMult,
                              out var plan, out var error)) {
                result.Summary = error;
                result.ConfigurationError = true;
                return result;
            }
            return SpawnNonDestructivePlan(plan, session);
#endif
        }

        public static Result ApplyNonDestructive(WhyKnotPhysBonePresetIntent intent, AvatarIntentSession session) {
            var result = new Result();
#if !VRC_SDK_VRCSDK3
            result.Summary = "VRChat SDK 3 (PhysBone) is not installed.";
            result.ConfigurationError = true;
            return result;
#else
            if (intent == null) {
                result.Summary = "No PhysBone preset intent supplied.";
                result.ConfigurationError = true;
                return result;
            }
            if (session == null) {
                result.Summary = "Internal error: no session supplied.";
                result.ConfigurationError = true;
                return result;
            }

            string signature = IntentPrecomputeUtility.BuildPhysBonePresetSignature(intent);
            PhysBonePlan plan;
            string error;
            if (IntentPrecomputeUtility.HasValidPhysBonePresetCache(intent, signature)
                    && TryBuildPlanFromCache(intent.precomputedPlan, out plan, out error)) {
                return SpawnNonDestructivePlan(plan, session);
            }

            if (!TryBuildPlan(intent.bones, intent.presetId,
                              intent.tweakPull, intent.tweakSpring, intent.tweakStiff,
                              intent.tweakGravity, intent.tweakRadius,
                              out plan, out error)) {
                result.Summary = error;
                result.ConfigurationError = true;
                return result;
            }
            StorePrecompute(intent, signature, plan);
            return SpawnNonDestructivePlan(plan, session);
#endif
        }

        internal static bool TryRefreshPrecompute(WhyKnotPhysBonePresetIntent intent, out string error) {
            error = null;
            if (intent == null) {
                error = "No PhysBone preset intent supplied.";
                return false;
            }
            if (!TryBuildPlan(intent.bones, intent.presetId,
                              intent.tweakPull, intent.tweakSpring, intent.tweakStiff,
                              intent.tweakGravity, intent.tweakRadius,
                              out var plan, out error)) {
                return false;
            }
            string signature = IntentPrecomputeUtility.BuildPhysBonePresetSignature(intent);
            StorePrecompute(intent, signature, plan);
            return true;
        }

        private static Result SpawnNonDestructivePlan(PhysBonePlan plan, AvatarIntentSession session) {
            var result = new Result();
#if !VRC_SDK_VRCSDK3
            result.Summary = "VRChat SDK 3 (PhysBone) is not installed.";
            result.ConfigurationError = true;
            return result;
#else
            // Step 1: spawn collider GameObjects + components. Track the
            // holder GameObject through session.Adopt so Dispose destroys
            // the GameObject (which sweeps up the component too).
            var colliderRefs = new List<VRCPhysBoneColliderBase>(plan.Colliders.Count);
            foreach (var spec in plan.Colliders) {
                var holder = new GameObject(spec.Name);
                holder.transform.SetParent(spec.AttachTo, worldPositionStays: false);
                holder.transform.localPosition = Vector3.zero;
                holder.transform.localRotation = Quaternion.identity;
                session.Adopt(holder);

                var col = holder.AddComponent<VRCPhysBoneCollider>();
                col.shapeType    = ToShape(spec.Shape);
                col.rootTransform = spec.RootTransform;
                col.radius       = spec.Radius;
                col.height       = spec.Height;
                col.position     = spec.Position;
                col.rotation     = Quaternion.Euler(spec.EulerRotation);
                col.insideBounds = spec.InsideBounds;
                colliderRefs.Add(col);
                result.CollidersSpawned++;
            }

            // Step 2: spawn PhysBones. Track the component through
            // session.RememberSpawnedComponent so Dispose removes the
            // component (the bone GameObject was already there; we do NOT
            // touch it).
            foreach (var spec in plan.PhysBones) {
                if (spec.Root == null) continue;
                var pb = spec.Root.gameObject.AddComponent<VRCPhysBone>();
                ApplySpec(pb, spec, colliderRefs);
                session.RememberSpawnedComponent(pb);
                result.PhysBonesSpawned++;
            }

            result.Summary = $"Spawned {result.PhysBonesSpawned} PhysBone(s), {result.CollidersSpawned} collider(s) from preset \"{plan.PresetDisplayName}\".";
            return result;
#endif
        }

        internal static bool TryBuildPlan(IList<Transform> bones, string presetId,
                                         float pullMult, float springMult, float stiffMult,
                                         float gravityMult, float radiusMult,
                                         out PhysBonePlan plan, out string error) {
            plan = null;
            error = null;
            if (bones == null || bones.Count == 0) {
                error = "No bones recorded on the intent.";
                return false;
            }
            if (string.IsNullOrEmpty(presetId)) {
                error = "No preset id recorded on the intent.";
                return false;
            }
            var preset = PhysBonePresetRegistry.FindById(presetId);
            if (preset == null) {
                error = $"Preset \"{presetId}\" is not registered.";
                return false;
            }
            var liveBones = new List<Transform>(bones.Count);
            foreach (var b in bones) if (b != null) liveBones.Add(b);
            if (liveBones.Count == 0) {
                error = "Every recorded bone is null (deleted or unmapped); nothing to apply.";
                return false;
            }
            var analysis = BoneSelectionAnalysis.Build(liveBones);
            try {
                plan = preset.BuildPlan(analysis);
            } catch (System.Exception ex) {
                AvatarQolLogger.Instance.Exception(ex);
                error = "Preset failed to build a plan: " + ex.Message;
                return false;
            }
            if (plan == null || plan.PhysBones.Count == 0) {
                error = $"Preset \"{presetId}\" produced an empty plan for the recorded bones.";
                return false;
            }
            ApplyTweaks(plan, pullMult, springMult, stiffMult, gravityMult, radiusMult);
            return true;
        }

        private static void StorePrecompute(
                WhyKnotPhysBonePresetIntent intent,
                string signature,
                PhysBonePlan plan) {

            if (intent == null || plan == null) return;
            intent.precomputeSignature = signature;
            intent.precomputeVersion = IntentPrecomputeUtility.PhysBonePresetVersion;
            intent.precomputedPlan = ToCache(plan);
            EditorUtility.SetDirty(intent);
        }

        private static PhysBonePresetPrecomputedPlan ToCache(PhysBonePlan plan) {
            var cache = new PhysBonePresetPrecomputedPlan {
                presetId = plan.PresetId,
                presetDisplayName = plan.PresetDisplayName,
            };
            foreach (var spec in plan.PhysBones) {
                if (spec == null) continue;
                cache.physBones.Add(new PhysBonePresetPrecomputedPhysBone {
                    root = spec.Root,
                    ignoreTransforms = new List<Transform>(spec.IgnoreTransforms),
                    colliderRefs = new List<int>(spec.ColliderRefs),
                    pull = spec.Pull,
                    spring = spec.Spring,
                    stiffness = spec.Stiffness,
                    gravity = spec.Gravity,
                    gravityFalloff = spec.GravityFalloff,
                    immobile = spec.Immobile,
                    immobileType = (int)spec.ImmobileType,
                    radius = spec.Radius,
                    allowCollision = (int)spec.AllowCollision,
                    allowGrabbing = (int)spec.AllowGrabbing,
                    allowPosing = (int)spec.AllowPosing,
                    maxStretch = spec.MaxStretch,
                    isAnimated = spec.IsAnimated,
                    parameter = spec.Parameter,
                });
            }
            foreach (var spec in plan.Colliders) {
                if (spec == null) continue;
                cache.colliders.Add(new PhysBonePresetPrecomputedCollider {
                    name = spec.Name,
                    attachTo = spec.AttachTo,
                    rootTransform = spec.RootTransform,
                    shape = (int)spec.Shape,
                    radius = spec.Radius,
                    height = spec.Height,
                    position = spec.Position,
                    eulerRotation = spec.EulerRotation,
                    insideBounds = spec.InsideBounds,
                });
            }
            return cache;
        }

        private static bool TryBuildPlanFromCache(
                PhysBonePresetPrecomputedPlan cache,
                out PhysBonePlan plan,
                out string error) {

            plan = null;
            error = null;
            if (cache == null || cache.physBones == null || cache.physBones.Count == 0) {
                error = "Precomputed PhysBone plan is empty.";
                return false;
            }
            plan = new PhysBonePlan {
                PresetId = cache.presetId,
                PresetDisplayName = cache.presetDisplayName,
            };
            if (cache.colliders != null) {
                foreach (var spec in cache.colliders) {
                    if (spec == null) continue;
                    if (spec.attachTo == null) {
                        error = "A precomputed collider target no longer exists.";
                        plan = null;
                        return false;
                    }
                    plan.Colliders.Add(new ColliderSpec {
                        Name = string.IsNullOrEmpty(spec.name) ? "PhysBoneCollider" : spec.name,
                        AttachTo = spec.attachTo,
                        RootTransform = spec.rootTransform,
                        Shape = (ColliderShape)spec.shape,
                        Radius = spec.radius,
                        Height = spec.height,
                        Position = spec.position,
                        EulerRotation = spec.eulerRotation,
                        InsideBounds = spec.insideBounds,
                    });
                }
            }
            foreach (var spec in cache.physBones) {
                if (spec == null) continue;
                if (spec.root == null) {
                    error = "A precomputed PhysBone root no longer exists.";
                    plan = null;
                    return false;
                }
                plan.PhysBones.Add(new PhysBoneSpec {
                    Root = spec.root,
                    IgnoreTransforms = spec.ignoreTransforms != null
                        ? new List<Transform>(spec.ignoreTransforms)
                        : new List<Transform>(),
                    ColliderRefs = spec.colliderRefs != null
                        ? new List<int>(spec.colliderRefs)
                        : new List<int>(),
                    Pull = spec.pull,
                    Spring = spec.spring,
                    Stiffness = spec.stiffness,
                    Gravity = spec.gravity,
                    GravityFalloff = spec.gravityFalloff,
                    Immobile = spec.immobile,
                    ImmobileType = (ImmobileTypeKind)spec.immobileType,
                    Radius = spec.radius,
                    AllowCollision = (AllowKind)spec.allowCollision,
                    AllowGrabbing = (AllowKind)spec.allowGrabbing,
                    AllowPosing = (AllowKind)spec.allowPosing,
                    MaxStretch = spec.maxStretch,
                    IsAnimated = spec.isAnimated,
                    Parameter = spec.parameter ?? "",
                });
            }
            if (plan.PhysBones.Count == 0) {
                error = "Precomputed PhysBone plan has no live roots.";
                plan = null;
                return false;
            }
            return true;
        }

        private static void ApplyTweaks(PhysBonePlan plan,
                                        float pullMult, float springMult, float stiffMult,
                                        float gravityMult, float radiusMult) {
            foreach (var spec in plan.PhysBones) {
                spec.Pull      *= pullMult;
                spec.Spring    *= springMult;
                spec.Stiffness *= stiffMult;
                spec.Gravity   *= gravityMult;
                spec.Radius    *= radiusMult;
            }
            foreach (var spec in plan.Colliders) {
                spec.Radius *= radiusMult;
            }
        }

#if VRC_SDK_VRCSDK3
        private static void ApplySpec(VRCPhysBone pb, PhysBoneSpec spec, List<VRCPhysBoneColliderBase> colliders) {
            pb.rootTransform = null;
            pb.ignoreTransforms = new List<Transform>(spec.IgnoreTransforms);

            pb.pull            = spec.Pull;
            pb.spring          = spec.Spring;
            pb.stiffness       = spec.Stiffness;
            pb.gravity         = spec.Gravity;
            pb.gravityFalloff  = spec.GravityFalloff;
            pb.immobile        = spec.Immobile;
            pb.immobileType    = ToImmobile(spec.ImmobileType);
            pb.radius          = spec.Radius;
            pb.allowCollision  = ToAdvanced(spec.AllowCollision);
            pb.allowGrabbing   = ToAdvanced(spec.AllowGrabbing);
            pb.allowPosing     = ToAdvanced(spec.AllowPosing);
            pb.maxStretch      = spec.MaxStretch;
            pb.isAnimated      = spec.IsAnimated;
            pb.parameter       = spec.Parameter ?? "";

            pb.colliders = new List<VRCPhysBoneColliderBase>(spec.ColliderRefs.Count);
            foreach (var idx in spec.ColliderRefs) {
                if (idx >= 0 && idx < colliders.Count && colliders[idx] != null) {
                    pb.colliders.Add(colliders[idx]);
                }
            }
        }

        private static VRCPhysBoneColliderBase.ShapeType ToShape(ColliderShape shape) {
            switch (shape) {
                case ColliderShape.Sphere:  return VRCPhysBoneColliderBase.ShapeType.Sphere;
                case ColliderShape.Capsule: return VRCPhysBoneColliderBase.ShapeType.Capsule;
                case ColliderShape.Plane:   return VRCPhysBoneColliderBase.ShapeType.Plane;
                default:                    return VRCPhysBoneColliderBase.ShapeType.Capsule;
            }
        }

        private static VRCPhysBoneBase.ImmobileType ToImmobile(ImmobileTypeKind kind) {
            int value;
            switch (kind) {
                case ImmobileTypeKind.AllMotion:     value = 0; break;
                case ImmobileTypeKind.WorldRotation: value = 1; break;
                default:                              value = 0; break;
            }
            return (VRCPhysBoneBase.ImmobileType)value;
        }

        private static VRCPhysBoneBase.AdvancedBool ToAdvanced(AllowKind kind) {
            switch (kind) {
                case AllowKind.True:  return VRCPhysBoneBase.AdvancedBool.True;
                case AllowKind.False: return VRCPhysBoneBase.AdvancedBool.False;
                default:              return VRCPhysBoneBase.AdvancedBool.Other;
            }
        }
#endif
    }
}
