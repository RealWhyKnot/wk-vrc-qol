// WeightFixer.cs
//
// Applies fixes to weight-contamination issues found by the Weight Sanity
// Check. Two strategies, picked per issue:
//
//   1. Mirror — transfer the offending weight to the offending bone's
//      Humanoid mirror (RightUpperLeg → LeftUpperLeg). This is what the
//      user typically wanted: Blender's robust weight transfer pulled the
//      weight from the wrong side; redirecting to the correct side
//      restores the intended skin.
//
//   2. Zero + renormalize — when the offending bone isn't a Humanoid bone
//      (or has no mirror, or the mirror isn't in the renderer's bones
//      array), the weight is set to 0 and the remaining weights on the
//      same vertex are scaled up proportionally so the per-vertex sum
//      stays at 1.
//
// Mesh asset handling:
//   FBX-imported meshes are sub-assets of the model file and can't be
//   edited in place (the importer overwrites them on every reimport).
//   When we detect such a mesh, we clone it to a fresh `.mesh` asset in
//   `Assets/AvatarQol Generated/` (created on demand), assign the clone
//   to renderer.sharedMesh, and apply fixes to the clone. The original
//   FBX is untouched. Cloned meshes are cached per-fix-batch so a
//   renderer with N issues only gets cloned once.
//
// Undo: AssetDatabase mutations (CreateAsset, SaveAndReimport) aren't
// undoable through the standard Undo system. Per-renderer assignments
// of the new sharedMesh ARE undoable. We register Undo on the renderer
// before assigning, so the user can Ctrl+Z to revert the renderer back
// to the original FBX-subasset mesh; the cloned `.mesh` file remains on
// disk but is harmless (and they can delete it manually).

using System.Collections.Generic;
using System.IO;
using Unity.Collections;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;
using WhyKnot.AvatarQol.Intent;

namespace WhyKnot.AvatarQol.Tools {

    internal static class WeightFixer {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal sealed class IssueRef {
            // The data the fixer needs from a Weight Sanity Check issue.
            public SkinnedMeshRenderer Renderer;
            public int VertexIndex;
            public Transform OffendingBone;
            public float Weight;
        }

        internal sealed class FixResult {
            public int Fixed;                  // number of weights successfully redirected or zeroed
            public int Mirrored;               // subset of Fixed where the weight got transferred to a mirror
            public int Zeroed;                 // subset of Fixed where the weight got zeroed + renormalized
            public int Skipped;                // weight wasn't found on the vertex (already fixed?)
            public int RenderersTouched;       // distinct renderers we mutated
            public int MeshesCloned;           // FBX-subasset meshes we cloned to fresh .mesh files
            public List<string> ClonedMeshPaths = new List<string>();
        }

        /// <summary>
        /// Apply fixes to every issue in <paramref name="issues"/>. Returns
        /// summary stats; per-renderer mesh cloning is handled internally.
        /// </summary>
        internal static FixResult ApplyFixes(IList<IssueRef> issues, Animator animator) {
            var result = new FixResult();
            if (issues == null || issues.Count == 0 || animator == null) return result;

            // If an Avatar QoL intent preview, play-mode run, or upload is
            // currently swapping renderer.sharedMesh to an in-memory clone,
            // writing durable weight fixes to that clone would silently
            // disappear when the session restores. Refuse with a clear
            // dialog and let the user stop the session first.
            if (AvatarIntentSessionState.IsAnyIntentSessionActive()) {
                EditorUtility.DisplayDialog(
                    "Avatar QoL - Weight Fixer",
                    "An Avatar QoL intent preview, play-mode run, or upload is currently swapping mesh data on these renderers. " +
                    "Weight fixes written now would land on the temporary clone and be lost when the session ends.\n\n" +
                    "Stop the preview / exit play mode / let the upload finish, then run Fix again.",
                    "OK");
                return result;
            }

            // Group by renderer so we clone each mesh at most once.
            var byRenderer = new Dictionary<SkinnedMeshRenderer, List<IssueRef>>();
            foreach (var i in issues) {
                if (i == null || i.Renderer == null) continue;
                if (!byRenderer.TryGetValue(i.Renderer, out var list)) {
                    list = new List<IssueRef>();
                    byRenderer[i.Renderer] = list;
                }
                list.Add(i);
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Fix weight contamination");
            try {
                foreach (var kv in byRenderer) {
                    if (FixOneRenderer(kv.Key, kv.Value, animator, result)) {
                        result.RenderersTouched++;
                    }
                }
                Undo.CollapseUndoOperations(undoGroup);
            } catch (System.Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex);
            }
            return result;
        }

        // ---- per-renderer batch ------------------------------------------

        private static bool FixOneRenderer(SkinnedMeshRenderer renderer, List<IssueRef> rendererIssues,
                                           Animator animator, FixResult result) {
            if (renderer == null || rendererIssues == null || rendererIssues.Count == 0) return false;
            var sharedMesh = renderer.sharedMesh;
            if (sharedMesh == null) return false;

            // If the mesh is an FBX/OBJ/DAE sub-asset, we have to clone to a
            // separately-owned .mesh asset. Otherwise modify in place.
            var resolved = FbxMeshUtility.ResolveEditableMesh(
                renderer, sharedMesh, "(FixedWeights)", "Reassign mesh after fix", GeneratedFolder);
            if (resolved.WasCloned) {
                result.MeshesCloned++;
                result.ClonedMeshPaths.Add(resolved.ClonedPath);
            }
            Mesh editableMesh = resolved.Mesh;
            if (editableMesh == null) return false;

            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) return false;

            int fixedBefore = result.Fixed;
            // Register the mesh for undo BEFORE writing so the destructive
            // path keeps its Ctrl+Z behaviour.
            Undo.RegisterCompleteObjectUndo(editableMesh, "Fix weight contamination");
            ApplyFixesToMeshInPlace(editableMesh, bones, rendererIssues, animator, result);
            EditorUtility.SetDirty(editableMesh);
            return result.Fixed > fixedBefore;
        }

        // ---- shared apply primitive --------------------------------------

        /// <summary>
        /// Write fixes for <paramref name="rendererIssues"/> directly into
        /// <paramref name="editableMesh"/>'s bone-weight buffer. Caller owns
        /// the mesh lifetime (asset clone, in-memory clone, etc) and any
        /// undo registration; this method only touches mesh data, never the
        /// asset database. Used by both the destructive
        /// <see cref="ApplyFixes"/> path and the runtime
        /// WeightFixApplyHook session.
        /// </summary>
        internal static void ApplyFixesToMeshInPlace(
                Mesh editableMesh,
                Transform[] bones,
                List<IssueRef> rendererIssues,
                Animator animator,
                FixResult result) {
            if (editableMesh == null || bones == null || rendererIssues == null) return;
            if (rendererIssues.Count == 0) return;

            // Build the (boneIndex -> mirror boneIndex) lookup once per
            // renderer. -1 means "no mirror in this renderer's bones array;
            // fix by zero+renormalize."
            var mirrorIndex = new int[bones.Length];
            for (int i = 0; i < bones.Length; i++) {
                mirrorIndex[i] = ResolveMirrorIndexInBones(bones[i], bones, animator);
            }

            // Read current weights into mutable buffers.
            var srcWeights = editableMesh.GetAllBoneWeights();
            var srcBpv = editableMesh.GetBonesPerVertex();
            var dstWeights = new NativeArray<BoneWeight1>(srcWeights.Length, Allocator.Temp);
            var dstBpv = new NativeArray<byte>(srcBpv.Length, Allocator.Temp);
            srcWeights.CopyTo(dstWeights);
            srcBpv.CopyTo(dstBpv);

            // Pre-compute the start offset of each vertex's weights.
            var weightStart = new int[srcBpv.Length];
            int cursor = 0;
            for (int v = 0; v < srcBpv.Length; v++) { weightStart[v] = cursor; cursor += srcBpv[v]; }

            // Process issues. Each issue points at a specific (vertex, bone)
            // weight entry that needs fixing. Sort by vertex to keep memory
            // access predictable.
            rendererIssues.Sort((a, b) => a.VertexIndex.CompareTo(b.VertexIndex));
            foreach (var issue in rendererIssues) {
                if (issue.VertexIndex < 0 || issue.VertexIndex >= dstBpv.Length) { result.Skipped++; continue; }
                int origBoneIdx = FindBoneIndex(bones, issue.OffendingBone);
                if (origBoneIdx < 0) { result.Skipped++; continue; }

                int wStart = weightStart[issue.VertexIndex];
                int wCount = dstBpv[issue.VertexIndex];
                int weightSlot = -1;
                for (int w = 0; w < wCount; w++) {
                    if (dstWeights[wStart + w].boneIndex == origBoneIdx) {
                        weightSlot = wStart + w;
                        break;
                    }
                }
                if (weightSlot < 0) { result.Skipped++; continue; }

                int mirror = mirrorIndex[origBoneIdx];
                if (mirror >= 0) {
                    // Transfer the entire weight to the mirror bone.
                    var bw = dstWeights[weightSlot];
                    bw.boneIndex = mirror;
                    dstWeights[weightSlot] = bw;
                    result.Mirrored++;
                    result.Fixed++;
                } else {
                    // Zero and redistribute. Scale remaining weights by
                    // (1 + lost / remaining) so the per-vertex sum is
                    // preserved. If the vertex's other weights sum to zero
                    // (degenerate), we just leave the entry zeroed -- Unity
                    // handles unnormalised weights without crashing, and
                    // the result is at worst a vertex that sticks to its
                    // local frame.
                    float lost = dstWeights[weightSlot].weight;
                    var bw = dstWeights[weightSlot];
                    bw.weight = 0f;
                    dstWeights[weightSlot] = bw;
                    float remaining = 0f;
                    for (int w = 0; w < wCount; w++) {
                        if (wStart + w == weightSlot) continue;
                        remaining += dstWeights[wStart + w].weight;
                    }
                    if (remaining > 1e-5f) {
                        float scale = (remaining + lost) / remaining;
                        for (int w = 0; w < wCount; w++) {
                            if (wStart + w == weightSlot) continue;
                            var b = dstWeights[wStart + w];
                            b.weight *= scale;
                            dstWeights[wStart + w] = b;
                        }
                    }
                    result.Zeroed++;
                    result.Fixed++;
                }
            }

            editableMesh.SetBoneWeights(dstBpv, dstWeights);

            dstWeights.Dispose();
            dstBpv.Dispose();
        }

        // ---- mirror-bone resolution --------------------------------------

        private static int ResolveMirrorIndexInBones(Transform bone, Transform[] bones, Animator animator) {
            if (bone == null || animator == null) return -1;
            // Identify the bone's HumanBodyBones if any.
            HumanBodyBones? selfHbb = null;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++) {
                var b = (HumanBodyBones)i;
                if (animator.GetBoneTransform(b) == bone) { selfHbb = b; break; }
            }
            if (!selfHbb.HasValue) return -1;
            var name = selfHbb.Value.ToString();
            string mirrorName;
            if (name.StartsWith("Left"))       mirrorName = "Right" + name.Substring(4);
            else if (name.StartsWith("Right")) mirrorName = "Left" + name.Substring(5);
            else                                return -1;
            if (!System.Enum.TryParse<HumanBodyBones>(mirrorName, out var mirrorHbb)) return -1;
            var mirrorTransform = animator.GetBoneTransform(mirrorHbb);
            if (mirrorTransform == null) return -1;
            return FindBoneIndex(bones, mirrorTransform);
        }

        private static int FindBoneIndex(Transform[] bones, Transform target) {
            if (bones == null || target == null) return -1;
            for (int i = 0; i < bones.Length; i++) if (bones[i] == target) return i;
            return -1;
        }

    }
}
