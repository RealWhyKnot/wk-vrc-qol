// BoneMergerOp.cs
//
// Shared math for Bone Merger. Folds the merge-from bone's weight slots
// onto the merge-into bone for every SkinnedMeshRenderer under an
// avatar, collapsing duplicate slots, sorting by weight descending, and
// dropping zero-weight slots. Two entry points share one implementation:
//
//   ApplyDestructive: writes the merged mesh as a .mesh asset under
//     Assets/AvatarQol Generated/, rewires renderer.sharedMesh, and
//     (optionally) destroys the merged-away bone GameObjects. Registers
//     every step with Undo so Ctrl+Z reverts. This is what the
//     Bone Merger window's Apply button calls.
//
//   ApplyNonDestructive: clones the renderer's current sharedMesh in
//     memory only, writes merged weights into the clone, hands the
//     original to the supplied AvatarIntentSession for restore on
//     dispose. Never touches bone GameObjects -- the build / play hooks
//     run this against an avatar whose hierarchy must remain intact.
//
// Mathematical caveat: weight-merging is visually identical to the
// original skinning only when the two bones share the same rest pose
// (i.e. the merge-from bone sits at local zero under the merge-into).
// When the rest poses differ, vertices that were weighted to the
// merge-from bone will snap to the merge-into bone's frame at the rig's
// rest pose. That's inherent to linear blend skinning, not a tool bug.

using System.Collections.Generic;
using System.Linq;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.BoneMerger {

    internal static class BoneMergerOp {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal sealed class Result {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> ClonedPaths = new List<string>();
            public int RenderersTouched;
            public int WeightsRedirected;
            public int MeshesCloned;
            public int BonesDeleted;
            public int ChildrenReparented;
            public int UnreadableRenderers;
            public bool ConfigurationError;

            public bool DidAnything =>
                RenderersTouched > 0 || MeshesCloned > 0 || BonesDeleted > 0 || ChildrenReparented > 0;
        }

        public static Result ApplyDestructive(
                Animator animator,
                IList<BoneMergerPair> pairs,
                bool deleteMergedBones,
                bool reparentChildren) {
            return Apply(animator, pairs, deleteMergedBones, reparentChildren,
                useUndo: true, session: null, undoLabel: "Avatar QoL: Merge bones");
        }

        public static Result ApplyNonDestructive(
                Animator animator,
                IList<BoneMergerPair> pairs,
                AvatarIntentSession session) {
            // Non-destructive path runs against an in-memory mesh clone tracked
            // by the session, and never touches bone GameObjects. The deletion
            // / reparent flags from the intent component are deliberately
            // ignored here -- those are scene mutations that belong to the
            // destructive window flow.
            return Apply(animator, pairs, deleteMergedBones: false, reparentChildren: false,
                useUndo: false, session: session, undoLabel: null);
        }

        private static Result Apply(
                Animator animator,
                IList<BoneMergerPair> pairs,
                bool deleteMergedBones,
                bool reparentChildren,
                bool useUndo,
                AvatarIntentSession session,
                string undoLabel) {

            var result = new Result();
            if (animator == null) {
                result.Summary = "Pick an Animator first.";
                result.ConfigurationError = true;
                return result;
            }

            var validPairs = pairs == null
                ? new List<BoneMergerPair>()
                : pairs.Where(p => p != null && p.mergeFrom != null && p.mergeInto != null && p.mergeFrom != p.mergeInto).ToList();
            if (validPairs.Count == 0) {
                result.Summary = "No valid pairs to apply.";
                result.ConfigurationError = true;
                return result;
            }

            if (!ValidatePairs(validPairs, out string conflictMessage)) {
                result.Summary = conflictMessage;
                result.ConfigurationError = true;
                return result;
            }

            var renderers = animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            if (renderers.Length == 0) {
                result.Summary = "The picked Animator has no SkinnedMeshRenderers underneath.";
                result.ConfigurationError = true;
                return result;
            }

            int undoGroup = useUndo ? Undo.GetCurrentGroup() : -1;
            if (useUndo) Undo.SetCurrentGroupName(undoLabel);

            var perPairFlaggedRenderers = new Dictionary<BoneMergerPair, int>();

            try {
                foreach (var renderer in renderers) {
                    if (renderer == null || renderer.sharedMesh == null) continue;
                    if (!renderer.sharedMesh.isReadable) {
                        result.UnreadableRenderers++;
                        result.Detail.Add($"SKIP {RendererPath(renderer)} -- mesh not readable. Enable Read/Write on the model importer.");
                        continue;
                    }
                    var rendererResult = MergeOnRenderer(
                        renderer, validPairs, useUndo, session,
                        result.Detail, result.ClonedPaths, ref result.MeshesCloned);
                    if (rendererResult.RedirectsApplied > 0) {
                        result.RenderersTouched++;
                        result.WeightsRedirected += rendererResult.RedirectsApplied;
                        foreach (var pair in rendererResult.PairsThatMatched) {
                            perPairFlaggedRenderers.TryGetValue(pair, out var n);
                            perPairFlaggedRenderers[pair] = n + 1;
                        }
                        result.Detail.Add($"OK   {RendererPath(renderer)} -- redirected {rendererResult.RedirectsApplied} weight slot(s){(rendererResult.WasCloned ? " (cloned mesh)" : "")}.");
                    }
                }

                if (deleteMergedBones) {
                    foreach (var pair in validPairs) {
                        if (pair.mergeFrom == null || pair.mergeInto == null) continue;
                        if (reparentChildren) {
                            var kids = new List<Transform>();
                            foreach (Transform c in pair.mergeFrom) kids.Add(c);
                            foreach (var c in kids) {
                                if (useUndo) Undo.SetTransformParent(c, pair.mergeInto, "Re-parent under kept bone");
                                else c.SetParent(pair.mergeInto, worldPositionStays: true);
                                result.ChildrenReparented++;
                            }
                        }
                        if (useUndo) Undo.DestroyObjectImmediate(pair.mergeFrom.gameObject);
                        else Object.DestroyImmediate(pair.mergeFrom.gameObject);
                        result.BonesDeleted++;
                    }
                }

                if (result.MeshesCloned > 0 && useUndo) AssetDatabase.SaveAssets();
                if (useUndo) Undo.CollapseUndoOperations(undoGroup);

                var unusedPairs = new List<string>();
                foreach (var pair in validPairs) {
                    if (!perPairFlaggedRenderers.ContainsKey(pair)) {
                        unusedPairs.Add($"{NameOrDestroyed(pair.mergeFrom)} -> {NameOrDestroyed(pair.mergeInto)}");
                    }
                }
                if (unusedPairs.Count > 0) {
                    result.Detail.Add($"WARN {unusedPairs.Count} pair(s) didn't match any renderer's bone list: {string.Join(", ", unusedPairs)}. The bones may not be skinned to any mesh under this Animator, or the merge-into bone may be absent from the renderer's bone array.");
                }

                result.Summary = BuildSummary(
                    result.RenderersTouched, result.WeightsRedirected, result.MeshesCloned,
                    result.BonesDeleted, result.ChildrenReparented, result.UnreadableRenderers);
            } catch (System.Exception ex) {
                if (useUndo) Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex);
                result.Summary = "Merge failed -- nothing was changed. See console for the exception.";
            }
            return result;
        }

        private struct RendererResult {
            public int RedirectsApplied;
            public bool WasCloned;
            public List<BoneMergerPair> PairsThatMatched;
        }

        private static RendererResult MergeOnRenderer(
                SkinnedMeshRenderer renderer,
                IList<BoneMergerPair> validPairs,
                bool useUndo,
                AvatarIntentSession session,
                List<string> detail,
                List<string> clonedPaths,
                ref int meshesCloned) {

            var result = new RendererResult { PairsThatMatched = new List<BoneMergerPair>() };
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return result;

            var redirect = new Dictionary<int, int>(validPairs.Count);
            var pairsForRenderer = new List<BoneMergerPair>();
            foreach (var pair in validPairs) {
                int srcIdx = IndexOf(bones, pair.mergeFrom);
                if (srcIdx < 0) continue;
                int dstIdx = IndexOf(bones, pair.mergeInto);
                if (dstIdx < 0) {
                    detail.Add($"WARN {RendererPath(renderer)} -- bone \"{NameOrDestroyed(pair.mergeInto)}\" is not in this renderer's bones[]. Skipping that pair for this mesh.");
                    continue;
                }
                redirect[srcIdx] = dstIdx;
                pairsForRenderer.Add(pair);
            }
            if (redirect.Count == 0) return result;

            // Collapse chains a -> b -> c so every key maps to its final
            // destination, with a cycle guard.
            foreach (var key in redirect.Keys.ToList()) {
                int v = redirect[key];
                var visited = new HashSet<int> { key };
                while (redirect.TryGetValue(v, out int next) && visited.Add(v)) {
                    v = next;
                }
                redirect[key] = v;
            }

            var sharedMesh = renderer.sharedMesh;
            Mesh editableMesh;
            if (session != null) {
                // Non-destructive: capture the original sharedMesh on the
                // renderer, clone in memory, assign clone to the renderer.
                // Session.Dispose restores both pieces.
                session.Capture(renderer);
                editableMesh = Object.Instantiate(sharedMesh);
                editableMesh.name = sharedMesh.name + " (BoneMerged)";
                editableMesh.hideFlags = HideFlags.DontSave;
                session.Adopt(editableMesh);
                renderer.sharedMesh = editableMesh;
                result.WasCloned = true;
                meshesCloned++;
            } else {
                // Destructive: clone to .mesh asset if the source belongs to
                // an importer sub-asset; otherwise modify in place. Either
                // way, Undo on the renderer's sharedMesh swap is registered
                // by ResolveEditableMesh.
                var resolved = FbxMeshUtility.ResolveEditableMesh(
                    renderer, sharedMesh, "(MergedBones)", "Create merged mesh", GeneratedFolder);
                editableMesh = resolved.Mesh;
                if (editableMesh == null) return result;
                if (resolved.WasCloned) {
                    meshesCloned++;
                    clonedPaths.Add(resolved.ClonedPath);
                }
                result.WasCloned = resolved.WasCloned;
            }

            var srcBpv = editableMesh.GetBonesPerVertex();
            var srcWeights = editableMesh.GetAllBoneWeights();
            int vertCount = srcBpv.Length;
            int totalWeights = srcWeights.Length;

            var newBpv = new NativeArray<byte>(vertCount, Allocator.Temp);
            try {
                var newWeights = new List<BoneWeight1>(totalWeights);

                const int Scratch = 64;
                var scratchIdx = new int[Scratch];
                var scratchWt  = new float[Scratch];

                int cursor = 0;
                int redirectsApplied = 0;

                for (int v = 0; v < vertCount; v++) {
                    int count = srcBpv[v];
                    if (count == 0) { newBpv[v] = 0; continue; }

                    bool touched = false;
                    for (int k = 0; k < count; k++) {
                        if (redirect.ContainsKey(srcWeights[cursor + k].boneIndex)) { touched = true; break; }
                    }
                    if (!touched) {
                        for (int k = 0; k < count; k++) newWeights.Add(srcWeights[cursor + k]);
                        newBpv[v] = (byte)count;
                        cursor += count;
                        continue;
                    }

                    int n = 0;
                    for (int k = 0; k < count; k++) {
                        var bw = srcWeights[cursor + k];
                        int idx = bw.boneIndex;
                        if (redirect.TryGetValue(idx, out int newIdx)) {
                            idx = newIdx;
                            redirectsApplied++;
                        }
                        int existing = -1;
                        for (int s = 0; s < n; s++) {
                            if (scratchIdx[s] == idx) { existing = s; break; }
                        }
                        if (existing >= 0) {
                            scratchWt[existing] += bw.weight;
                        } else if (n < Scratch) {
                            scratchIdx[n] = idx;
                            scratchWt[n]  = bw.weight;
                            n++;
                        }
                    }
                    cursor += count;

                    for (int a = 0; a < n - 1; a++) {
                        int maxK = a;
                        for (int b = a + 1; b < n; b++) if (scratchWt[b] > scratchWt[maxK]) maxK = b;
                        if (maxK != a) {
                            (scratchWt[a],  scratchWt[maxK])  = (scratchWt[maxK],  scratchWt[a]);
                            (scratchIdx[a], scratchIdx[maxK]) = (scratchIdx[maxK], scratchIdx[a]);
                        }
                    }

                    int emitted = 0;
                    for (int s = 0; s < n; s++) {
                        if (scratchWt[s] <= 0f) continue;
                        newWeights.Add(new BoneWeight1 { boneIndex = scratchIdx[s], weight = scratchWt[s] });
                        emitted++;
                    }
                    newBpv[v] = (byte)emitted;
                }

                if (redirectsApplied > 0) {
                    using (var newWeightsNative = new NativeArray<BoneWeight1>(newWeights.ToArray(), Allocator.Temp)) {
                        if (useUndo) Undo.RegisterCompleteObjectUndo(editableMesh, "Merge bone weights");
                        editableMesh.SetBoneWeights(newBpv, newWeightsNative);
                        if (useUndo) EditorUtility.SetDirty(editableMesh);
                    }
                    result.RedirectsApplied = redirectsApplied;
                    result.PairsThatMatched = pairsForRenderer;
                }
                return result;
            } finally {
                newBpv.Dispose();
            }
        }

        // Reject pair lists that can't be sequenced cleanly:
        //   - Same bone listed as the merge-from in two pairs with DIFFERENT
        //     destinations -- ambiguous.
        //   - A cycle through mergeFrom -> mergeInto edges. Cycles cause the
        //     chain-collapse step to no-op every weight redirect AND the
        //     deletion phase would still remove the bones, leaving orphaned
        //     weights pointing at destroyed slots. Easier to refuse upfront.
        internal static bool ValidatePairs(IList<BoneMergerPair> pairs, out string message) {
            message = "";
            var fromToInto = new Dictionary<Transform, Transform>();
            foreach (var p in pairs) {
                if (fromToInto.TryGetValue(p.mergeFrom, out var existing)) {
                    if (existing != p.mergeInto) {
                        message = $"\"{p.mergeFrom.name}\" is set to merge into two different bones (\"{existing.name}\" and \"{p.mergeInto.name}\"). Pick one and try again.";
                        return false;
                    }
                    continue;
                }
                fromToInto[p.mergeFrom] = p.mergeInto;
            }
            foreach (var start in fromToInto.Keys) {
                var visited = new HashSet<Transform>();
                var cur = start;
                while (fromToInto.TryGetValue(cur, out var next)) {
                    if (!visited.Add(cur)) {
                        message = $"Cycle in the pair list (e.g. \"{cur.name}\" is reachable from itself). Break the loop and try again.";
                        return false;
                    }
                    cur = next;
                }
            }
            return true;
        }

        private static string BuildSummary(int renderersTouched, int weightsRedirected, int meshesCloned,
                                           int bonesDeleted, int childrenReparented, int unreadable) {
            var parts = new List<string>();
            parts.Add($"{weightsRedirected} weight(s) on {renderersTouched} renderer(s)");
            if (meshesCloned > 0) parts.Add($"{meshesCloned} mesh(es) cloned");
            if (bonesDeleted > 0) parts.Add($"{bonesDeleted} bone(s) deleted");
            if (childrenReparented > 0) parts.Add($"{childrenReparented} child(ren) re-parented");
            if (unreadable > 0)   parts.Add($"{unreadable} renderer(s) skipped (mesh not readable)");
            return string.Join(", ", parts) + ".";
        }

        private static int IndexOf(Transform[] arr, Transform value) {
            if (arr == null || value == null) return -1;
            for (int i = 0; i < arr.Length; i++) if (arr[i] == value) return i;
            return -1;
        }

        private static string NameOrDestroyed(Transform t) {
            return t == null ? "(destroyed)" : t.name;
        }

        private static string RendererPath(SkinnedMeshRenderer r) {
            return r == null ? "(null)" : PathUtility.GetGameObjectPath(r.gameObject);
        }
    }
}
