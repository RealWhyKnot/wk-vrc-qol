// IntentPrecomputeUtility.cs
//
// Builds change signatures for editor-only intent components. The
// signatures are intentionally cheaper than the full tools: they hash mesh
// identity/import state, renderer bone poses, relevant settings, and source
// component state so build/play hooks can reuse cached precompute data until
// one of those inputs changes.

using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.BoneMerger;
using WhyKnot.AvatarQol.Clipping;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Internal.Utilities;
using WhyKnot.AvatarQol.WeightFixes;

namespace WhyKnot.AvatarQol.Intent {

    internal static class IntentPrecomputeUtility {
        public const int ClippingVersion = 4;
        public const int WeightFixVersion = 1;
        public const int BoneMergerVersion = 1;
        public const int PhysBonePresetVersion = 1;

        public static string BuildClippingSignature(
                WhyKnotClippingFixIntent intent,
                SkinnedMeshRenderer renderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                ClippingFixer.Settings settings) {

            var sb = Begin("clipping", ClippingVersion);
            AppendObject(sb, intent);
            AppendRenderer(sb, renderer);
            AppendAnimator(sb, settings != null ? settings.Animator : null);
            AppendBool(sb, settings != null && settings.CheckSelf);
            AppendBool(sb, settings != null && settings.IncludePhysBoneMotion);
            AppendFloat(sb, settings != null ? settings.InsideTolerance : 0f);
            AppendFloat(sb, settings != null ? settings.SurfacePadding : 0f);
            AppendFloat(sb, settings != null ? settings.PhysBoneWeightFloor : 0f);
            AppendFloat(sb, settings != null ? settings.PhysBoneClearanceMargin : 0f);
            AppendFloat(sb, settings != null ? settings.PhysBoneMotionPinStrength : 0f);
            AppendFloat(sb, settings != null ? settings.PhysBoneMotionBrushRadius : 0f);
            AppendInt(sb, settings != null ? settings.MaxIssuesPerPhysBone : 0);
            AppendRendererList(sb, comparisonRenderers);
            if (settings != null && settings.IncludePhysBoneMotion) {
                AppendPhysBoneComponentState(sb, settings.Animator);
            }
            return Finish(sb);
        }

        public static string BuildWeightFixSignature(
                WhyKnotWeightFixIntent intent,
                Animator animator,
                SkinnedMeshRenderer renderer,
                ScanParameters settings) {

            var sb = Begin("weight", WeightFixVersion);
            AppendObject(sb, intent);
            AppendAnimator(sb, animator);
            AppendRenderer(sb, renderer);
            AppendFloat(sb, settings.WeightFloor);
            AppendFloat(sb, settings.CenterMargin);
            AppendBool(sb, settings.ScanCenterBand);
            AppendFloat(sb, settings.CenterCrossSideFloor);
            return Finish(sb);
        }

        public static string BuildBoneMergerSignature(
                WhyKnotBoneMergerIntent intent,
                Animator animator,
                IList<BoneMergerPair> pairs) {

            var sb = Begin("bone-merger", BoneMergerVersion);
            AppendObject(sb, intent);
            AppendAnimator(sb, animator);
            AppendBonePairs(sb, pairs);
            if (animator != null) {
                var renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
                AppendInt(sb, renderers.Length);
                foreach (var renderer in renderers) AppendRenderer(sb, renderer);
            } else {
                AppendInt(sb, 0);
            }
            return Finish(sb);
        }

        public static string BuildPhysBonePresetSignature(WhyKnotPhysBonePresetIntent intent) {
            var sb = Begin("physbone-preset", PhysBonePresetVersion);
            AppendObject(sb, intent);
            AppendString(sb, intent != null ? intent.presetId : "");
            AppendFloat(sb, intent != null ? intent.tweakPull : 0f);
            AppendFloat(sb, intent != null ? intent.tweakSpring : 0f);
            AppendFloat(sb, intent != null ? intent.tweakStiff : 0f);
            AppendFloat(sb, intent != null ? intent.tweakGravity : 0f);
            AppendFloat(sb, intent != null ? intent.tweakRadius : 0f);
            if (intent == null || intent.bones == null) {
                AppendInt(sb, 0);
            } else {
                AppendInt(sb, intent.bones.Count);
                foreach (var bone in intent.bones) AppendTransformTree(sb, bone);
            }
            return Finish(sb);
        }

        public static bool HasValidClippingCache(WhyKnotClippingFixIntent intent, string signature) {
            return intent != null
                && intent.precomputeVersion == ClippingVersion
                && intent.precomputeSignature == signature
                && intent.precomputedIssues != null;
        }

        public static bool HasValidWeightFixCache(WhyKnotWeightFixIntent intent, string signature) {
            return intent != null
                && intent.precomputeVersion == WeightFixVersion
                && intent.precomputeSignature == signature
                && intent.precomputedIssues != null;
        }

        public static bool HasValidBoneMergerCache(WhyKnotBoneMergerIntent intent, string signature) {
            return intent != null
                && intent.precomputeVersion == BoneMergerVersion
                && intent.precomputeSignature == signature
                && intent.precomputedRenderers != null;
        }

        public static bool HasValidPhysBonePresetCache(WhyKnotPhysBonePresetIntent intent, string signature) {
            return intent != null
                && intent.precomputeVersion == PhysBonePresetVersion
                && intent.precomputeSignature == signature
                && intent.precomputedPlan != null
                && intent.precomputedPlan.physBones != null
                && intent.precomputedPlan.physBones.Count > 0;
        }

        private static StringBuilder Begin(string kind, int version) {
            var sb = new StringBuilder(4096);
            AppendString(sb, kind);
            AppendInt(sb, version);
            return sb;
        }

        private static string Finish(StringBuilder sb) {
            unchecked {
                const ulong offset = 14695981039346656037UL;
                const ulong prime = 1099511628211UL;
                ulong hash = offset;
                string text = sb.ToString();
                for (int i = 0; i < text.Length; i++) {
                    hash ^= text[i];
                    hash *= prime;
                }
                return hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        private static void AppendRendererList(StringBuilder sb, IList<SkinnedMeshRenderer> renderers) {
            if (renderers == null) {
                AppendInt(sb, 0);
                return;
            }
            AppendInt(sb, renderers.Count);
            foreach (var renderer in renderers) AppendRenderer(sb, renderer);
        }

        private static void AppendRenderer(StringBuilder sb, SkinnedMeshRenderer renderer) {
            AppendObject(sb, renderer);
            if (renderer == null) return;
            AppendString(sb, PathUtility.GetGameObjectPath(renderer.gameObject));
            AppendTransform(sb, renderer.transform);
            AppendMesh(sb, renderer.sharedMesh);
            AppendObject(sb, renderer.rootBone);
            var bones = renderer.bones;
            AppendInt(sb, bones != null ? bones.Length : 0);
            if (bones == null) return;
            foreach (var bone in bones) {
                AppendObject(sb, bone);
                AppendTransform(sb, bone);
            }
        }

        private static void AppendAnimator(StringBuilder sb, Animator animator) {
            AppendObject(sb, animator);
            if (animator == null) return;
            AppendBool(sb, animator.isHuman);
            AppendTransform(sb, animator.transform);
            AppendString(sb, PathUtility.GetGameObjectPath(animator.gameObject));
        }

        private static void AppendMesh(StringBuilder sb, Mesh mesh) {
            AppendObject(sb, mesh);
            if (mesh == null) return;
            AppendString(sb, mesh.name);
            AppendInt(sb, mesh.vertexCount);
            AppendInt(sb, mesh.subMeshCount);
            AppendInt(sb, mesh.blendShapeCount);
            AppendBounds(sb, mesh.bounds);
            for (int i = 0; i < mesh.subMeshCount; i++) {
                AppendInt(sb, (int)mesh.GetIndexCount(i));
            }
            string path = AssetDatabase.GetAssetPath(mesh);
            AppendString(sb, path);
            if (!string.IsNullOrEmpty(path)) {
                AppendString(sb, AssetDatabase.GetAssetDependencyHash(path).ToString());
            }
        }

        private static void AppendTransformTree(StringBuilder sb, Transform transform) {
            AppendTransform(sb, transform);
            if (transform == null) return;
            AppendInt(sb, transform.childCount);
            foreach (Transform child in transform) AppendTransformTree(sb, child);
        }

        private static void AppendTransform(StringBuilder sb, Transform transform) {
            AppendObject(sb, transform);
            if (transform == null) return;
            AppendString(sb, PathUtility.GetGameObjectPath(transform.gameObject));
            AppendVector(sb, transform.localPosition);
            AppendQuaternion(sb, transform.localRotation);
            AppendVector(sb, transform.localScale);
            AppendMatrix(sb, transform.localToWorldMatrix);
        }

        private static void AppendBonePairs(StringBuilder sb, IList<BoneMergerPair> pairs) {
            if (pairs == null) {
                AppendInt(sb, 0);
                return;
            }
            AppendInt(sb, pairs.Count);
            foreach (var pair in pairs) {
                AppendObject(sb, pair != null ? pair.mergeFrom : null);
                AppendObject(sb, pair != null ? pair.mergeInto : null);
            }
        }

        private static void AppendPhysBoneComponentState(StringBuilder sb, Animator animator) {
            if (animator == null) {
                AppendInt(sb, 0);
                return;
            }

            var components = animator.GetComponentsInChildren<Component>(true);
            int startLength = sb.Length;
            int count = 0;
            sb.Append("physbones-count:000000|");
            foreach (var component in components) {
                if (component == null) continue;
                var type = component.GetType();
                var typeName = type.FullName ?? type.Name;
                if (typeName.IndexOf("PhysBone", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                count++;
                AppendObject(sb, component);
                AppendString(sb, typeName);
                try {
                    AppendString(sb, EditorJsonUtility.ToJson(component));
                } catch {
                    AppendString(sb, component.name);
                }
            }
            string countText = count.ToString("000000", CultureInfo.InvariantCulture);
            for (int i = 0; i < countText.Length; i++) {
                sb[startLength + "physbones-count:".Length + i] = countText[i];
            }
        }

        private static void AppendObject(StringBuilder sb, Object obj) {
            if (obj == null) {
                AppendString(sb, "null");
                return;
            }
            string id = null;
            try {
                id = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString();
            } catch {
                id = null;
            }
            if (string.IsNullOrEmpty(id)) {
                string path = AssetDatabase.GetAssetPath(obj);
                id = !string.IsNullOrEmpty(path)
                    ? path + "#" + obj.name
                    : obj.GetInstanceID().ToString(CultureInfo.InvariantCulture);
            }
            AppendString(sb, id);
        }

        private static void AppendString(StringBuilder sb, string value) {
            value = value ?? "";
            sb.Append(value.Length.ToString(CultureInfo.InvariantCulture));
            sb.Append(':');
            sb.Append(value);
            sb.Append('|');
        }

        private static void AppendBool(StringBuilder sb, bool value) {
            sb.Append(value ? "1|" : "0|");
        }

        private static void AppendInt(StringBuilder sb, int value) {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            sb.Append('|');
        }

        private static void AppendFloat(StringBuilder sb, float value) {
            sb.Append(value.ToString("R", CultureInfo.InvariantCulture));
            sb.Append('|');
        }

        private static void AppendVector(StringBuilder sb, Vector3 value) {
            AppendFloat(sb, value.x);
            AppendFloat(sb, value.y);
            AppendFloat(sb, value.z);
        }

        private static void AppendQuaternion(StringBuilder sb, Quaternion value) {
            AppendFloat(sb, value.x);
            AppendFloat(sb, value.y);
            AppendFloat(sb, value.z);
            AppendFloat(sb, value.w);
        }

        private static void AppendBounds(StringBuilder sb, Bounds value) {
            AppendVector(sb, value.center);
            AppendVector(sb, value.size);
        }

        private static void AppendMatrix(StringBuilder sb, Matrix4x4 value) {
            for (int i = 0; i < 16; i++) AppendFloat(sb, value[i]);
        }
    }
}
