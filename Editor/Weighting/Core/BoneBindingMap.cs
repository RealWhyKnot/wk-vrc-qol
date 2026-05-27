// BoneBindingMap.cs
//
// Source and target SkinnedMeshRenderer bone arrays are independent. This
// map converts source bone indices into target bone indices before any
// copied weight can be written to the target mesh.

using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal enum BoneBindingKind {
        Unmapped,
        SameTransform,
        RelativePath,
        CanonicalName,
        ParentFallback,
    }

    internal sealed class BoneBindingMap {

        private readonly int[] _sourceToTarget;
        private readonly BoneBindingKind[] _kinds;

        private BoneBindingMap(int[] sourceToTarget, BoneBindingKind[] kinds) {
            _sourceToTarget = sourceToTarget ?? Array.Empty<int>();
            _kinds = kinds ?? Array.Empty<BoneBindingKind>();
        }

        public int SourceBoneCount => _sourceToTarget.Length;

        public int MappedCount {
            get {
                int count = 0;
                for (int i = 0; i < _sourceToTarget.Length; i++) if (_sourceToTarget[i] >= 0) count++;
                return count;
            }
        }

        public int UnmappedCount => SourceBoneCount - MappedCount;

        public bool TryMap(int sourceBoneIndex, out int targetBoneIndex) {
            targetBoneIndex = -1;
            if (sourceBoneIndex < 0 || sourceBoneIndex >= _sourceToTarget.Length) return false;
            targetBoneIndex = _sourceToTarget[sourceBoneIndex];
            return targetBoneIndex >= 0;
        }

        public BoneBindingKind GetKind(int sourceBoneIndex) {
            if (sourceBoneIndex < 0 || sourceBoneIndex >= _kinds.Length) return BoneBindingKind.Unmapped;
            return _kinds[sourceBoneIndex];
        }

        public string Summary() {
            var counts = new Dictionary<BoneBindingKind, int>();
            for (int i = 0; i < _kinds.Length; i++) {
                var kind = _kinds[i];
                if (!counts.ContainsKey(kind)) counts[kind] = 0;
                counts[kind]++;
            }

            var sb = new StringBuilder();
            sb.Append(MappedCount).Append('/').Append(SourceBoneCount).Append(" source bones mapped");
            AppendKind(sb, counts, BoneBindingKind.SameTransform, "same transform");
            AppendKind(sb, counts, BoneBindingKind.RelativePath, "path");
            AppendKind(sb, counts, BoneBindingKind.CanonicalName, "name");
            AppendKind(sb, counts, BoneBindingKind.ParentFallback, "parent");
            AppendKind(sb, counts, BoneBindingKind.Unmapped, "unmapped");
            return sb.ToString();
        }

        public static BoneBindingMap Build(
                SkinnedMeshRenderer source,
                SkinnedMeshRenderer target,
                Transform avatarRoot = null) {
            var sourceBones = source != null ? source.bones : null;
            var targetBones = target != null ? target.bones : null;
            if (sourceBones == null) sourceBones = Array.Empty<Transform>();
            if (targetBones == null) targetBones = Array.Empty<Transform>();

            var direct = new Dictionary<Transform, int>();
            var byPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var byName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < targetBones.Length; i++) {
                var bone = targetBones[i];
                if (bone == null) continue;
                if (!direct.ContainsKey(bone)) direct[bone] = i;

                string path = RelativePath(avatarRoot, bone);
                if (!string.IsNullOrEmpty(path) && !byPath.ContainsKey(path)) byPath[path] = i;

                string canonical = CanonicalName(bone.name);
                if (!string.IsNullOrEmpty(canonical) && !byName.ContainsKey(canonical)) byName[canonical] = i;
            }

            var map = new int[sourceBones.Length];
            var kinds = new BoneBindingKind[sourceBones.Length];
            for (int i = 0; i < map.Length; i++) {
                map[i] = -1;
                kinds[i] = BoneBindingKind.Unmapped;
                var sourceBone = sourceBones[i];
                if (sourceBone == null) continue;

                if (direct.TryGetValue(sourceBone, out int targetIndex)) {
                    map[i] = targetIndex;
                    kinds[i] = BoneBindingKind.SameTransform;
                    continue;
                }

                string path = RelativePath(avatarRoot, sourceBone);
                if (!string.IsNullOrEmpty(path) && byPath.TryGetValue(path, out targetIndex)) {
                    map[i] = targetIndex;
                    kinds[i] = BoneBindingKind.RelativePath;
                    continue;
                }

                string canonical = CanonicalName(sourceBone.name);
                if (!string.IsNullOrEmpty(canonical) && byName.TryGetValue(canonical, out targetIndex)) {
                    map[i] = targetIndex;
                    kinds[i] = BoneBindingKind.CanonicalName;
                    continue;
                }

                var parent = sourceBone.parent;
                while (parent != null) {
                    if (direct.TryGetValue(parent, out targetIndex)) {
                        map[i] = targetIndex;
                        kinds[i] = BoneBindingKind.ParentFallback;
                        break;
                    }
                    path = RelativePath(avatarRoot, parent);
                    if (!string.IsNullOrEmpty(path) && byPath.TryGetValue(path, out targetIndex)) {
                        map[i] = targetIndex;
                        kinds[i] = BoneBindingKind.ParentFallback;
                        break;
                    }
                    parent = parent.parent;
                }
            }

            return new BoneBindingMap(map, kinds);
        }

        internal static string CanonicalName(string name) {
            if (string.IsNullOrEmpty(name)) return "";
            string s = name.ToLowerInvariant();
            s = s.Replace("armature", "");
            s = s.Replace("mixamorig:", "");
            s = s.Replace("bip001", "");
            s = s.Replace("j_bip_", "");
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; i++) {
                char ch = s[i];
                if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            }
            return sb.ToString();
        }

        private static string RelativePath(Transform root, Transform bone) {
            if (bone == null) return "";
            if (root == null) return bone.name;
            var stack = new Stack<string>();
            var current = bone;
            while (current != null && current != root) {
                stack.Push(current.name);
                current = current.parent;
            }
            if (current != root) return "";
            return string.Join("/", stack.ToArray());
        }

        private static void AppendKind(
                StringBuilder sb,
                Dictionary<BoneBindingKind, int> counts,
                BoneBindingKind kind,
                string label) {
            if (!counts.TryGetValue(kind, out int count) || count <= 0) return;
            sb.Append(" - ").Append(count).Append(' ').Append(label);
        }
    }
}
