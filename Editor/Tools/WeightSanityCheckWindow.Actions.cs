// WeightSanityCheckWindow.Actions.cs

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class WeightSanityCheckWindow {

        // ------ Preview ----------------------------------------------------

        private void StartPreview(Transform bone) {
            if (bone == null) return;
            if (_previewBone == bone) return;
            // Restore any prior preview before starting a new one.
            StopPreview();
            _previewBone = bone;
            _previewRestRotation = bone.localRotation;
            _previewStart = EditorApplication.timeSinceStartup;
            // Mark the bone undo-recorded so accidental scene save doesn't
            // immortalise the wobbled rotation. We also restore on stop.
            Undo.RegisterCompleteObjectUndo(bone, "Avatar QoL preview");
        }

        private void StopPreview() {
            // The Unity-fake-null check is the right read here: if the bone
            // was destroyed, we can't restore it, but we should still clear
            // our reference so we don't keep wobbling against a dead object
            // every editor update.
            if (_previewBone == null) { _previewBone = null; return; }
            _previewBone.localRotation = _previewRestRotation;
            _previewBone = null;
            SceneView.RepaintAll();
        }

        private void OnEditorUpdate() {
            if (_previewBone == null) {
                // Bone may have been destroyed - drop our reference so the
                // next OnGUI doesn't try to draw a Stop button against it.
                _previewBone = null;
                return;
            }
            // Wobble around the bone's primary swing axes. Most rigs deform
            // legibly when rotated around their local X (forward bend) and
            // local Z (side splay). We combine the two so the mesh moves
            // visibly even when the rig's primary axis happens to be one or
            // the other.
            float t = (float)(EditorApplication.timeSinceStartup - _previewStart);
            float angle = Mathf.Sin(t * Mathf.PI) * 30f;
            float zAngle = Mathf.Cos(t * Mathf.PI) * 18f;
            _previewBone.localRotation = _previewRestRotation * Quaternion.Euler(angle, 0f, zAngle);
            SceneView.RepaintAll();
        }

        // ------ Preview on a clone (nondestructive) ----------------------

        private void StartPreview(List<DetectedIssue> issues) {
            if (_animator == null || issues == null || issues.Count == 0) return;
            var animator = _animator;
            // Group by renderer; remap each renderer to the preview clone via
            // hierarchy path so the in-memory fix lands on the clone instead
            // of the source.
            var byRenderer = new Dictionary<SkinnedMeshRenderer, List<DetectedIssue>>();
            foreach (var i in issues) {
                if (i == null || i.Renderer == null) continue;
                if (!byRenderer.TryGetValue(i.Renderer, out var list)) {
                    list = new List<DetectedIssue>();
                    byRenderer[i.Renderer] = list;
                }
                list.Add(i);
            }
            if (byRenderer.Count == 0) return;

            AvatarPreviewController.StartPreview(animator.gameObject, (cloneRoot, session) => {
                var cloneAnimator = cloneRoot.GetComponentInChildren<Animator>(true);
                if (cloneAnimator == null) return;
                foreach (var kv in byRenderer) {
                    var sourceRenderer = kv.Key;
                    var cloneRenderer = AvatarPreviewController.MapToPreview(sourceRenderer.transform)?.GetComponent<SkinnedMeshRenderer>();
                    if (cloneRenderer == null || cloneRenderer.sharedMesh == null) continue;
                    session.Capture(cloneRenderer);
                    var clone = UnityEngine.Object.Instantiate(cloneRenderer.sharedMesh);
                    clone.name = cloneRenderer.sharedMesh.name + " (WeightFix Preview)";
                    clone.hideFlags = HideFlags.DontSave;
                    session.Adopt(clone);
                    var refs = new List<WeightFixer.IssueRef>(kv.Value.Count);
                    foreach (var issue in kv.Value) {
                        var cloneBone = AvatarPreviewController.MapToPreview(issue.OffendingBone);
                        if (cloneBone == null) continue;
                        refs.Add(new WeightFixer.IssueRef {
                            Renderer = cloneRenderer,
                            VertexIndex = issue.VertexIndex,
                            OffendingBone = cloneBone,
                            Weight = issue.Weight,
                        });
                    }
                    var fixResult = new WeightFixer.FixResult();
                    WeightFixer.ApplyFixesToMeshInPlace(clone, cloneRenderer.bones, refs, cloneAnimator, fixResult);
                    cloneRenderer.sharedMesh = clone;
                }
            });
        }

        // ------ Save as component (nondestructive) ------------------------

        private void SaveIssuesAsComponents() {
            if (_issues.Count == 0) return;

            // Bucket issues by their renderer so each renderer gets one
            // component, parameterised once. We don't store the issue list
            // on the component as a fixed selection. The component keeps a
            // precomputed detection list and refreshes it when inputs change.
            var renderers = new HashSet<SkinnedMeshRenderer>();
            var byRenderer = new Dictionary<SkinnedMeshRenderer, List<DetectedIssue>>();
            foreach (var i in _issues) {
                if (i.Renderer == null) continue;
                renderers.Add(i.Renderer);
                if (!byRenderer.TryGetValue(i.Renderer, out var list)) {
                    list = new List<DetectedIssue>();
                    byRenderer[i.Renderer] = list;
                }
                list.Add(i);
            }
            if (renderers.Count == 0) {
                EditorUtility.DisplayDialog("Save fixes as component",
                    "All issues in the list reference destroyed renderers; nothing to save.", "OK");
                return;
            }

            string msg = $"Add (or update) a WhyKnotWeightFixIntent component on " +
                         $"{renderers.Count} renderer(s)?\n\n" +
                         "At play-mode entry and at avatar upload, each intent uses its precomputed " +
                         "issue list while the renderer mesh, bones, pose, and settings are unchanged. " +
                         "If any input changes, it refreshes the precompute before applying fixes to " +
                         "an in-memory clone. The source mesh asset is never modified.\n\n" +
                         "The component stores your current scan parameters (weight floor, " +
                         "centre margin, scan centre band, centre threshold) so play / build " +
                         "produces the same set of fixes you see in this list right now. Row selection " +
                         "is ignored for the component.";
            if (!EditorUtility.DisplayDialog("Save fixes as component", msg,
                    $"Add to {renderers.Count} renderer(s)", "Cancel")) return;

            int created = 0;
            int updated = 0;
            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Save weight fixes as component");
            foreach (var renderer in renderers) {
                if (renderer == null) continue;
                var existing = renderer.GetComponent<WhyKnotWeightFixIntent>();
                if (existing == null) {
                    existing = Undo.AddComponent<WhyKnotWeightFixIntent>(renderer.gameObject);
                    created++;
                } else {
                    Undo.RecordObject(existing, "Update WeightFix intent");
                    updated++;
                }
                existing.targetRenderer       = renderer;
                existing.weightFloor          = _weightFloor;
                existing.centerMargin         = _centerMargin;
                existing.scanCenterBand       = _scanCenterBand;
                existing.centerCrossSideFloor = _centerCrossSideFloor;
                var parameters = new ScanParameters {
                    WeightFloor = _weightFloor,
                    CenterMargin = _centerMargin,
                    ScanCenterBand = _scanCenterBand,
                    CenterCrossSideFloor = _centerCrossSideFloor,
                };
                string signature = IntentPrecomputeUtility.BuildWeightFixSignature(
                    existing,
                    _animator,
                    renderer,
                    parameters);
                byRenderer.TryGetValue(renderer, out var cachedIssues);
                WeightFixPrecomputeCache.Store(existing, signature, cachedIssues ?? new List<DetectedIssue>());
                EditorUtility.SetDirty(existing);
            }
            Undo.CollapseUndoOperations(undoGroup);

            AvatarQolLogger.Instance.Info(
                $"WeightFix intent saved: {created} created, {updated} updated, " +
                $"across {renderers.Count} renderer(s). " +
                $"Fixes will apply at play mode and at upload.");
        }

        // ------ Fix --------------------------------------------------------

        private void FixIssues(List<DetectedIssue> issues, string description) {
            if (issues == null || issues.Count == 0) return;
            // Single confirmation up-front; the apply runs in a single Undo
            // group so Ctrl+Z reverts everything in one step.
            var rendererCount = new HashSet<SkinnedMeshRenderer>();
            foreach (var i in issues) if (i.Renderer != null) rendererCount.Add(i.Renderer);
            string msg = $"Fix {description} across {rendererCount.Count} renderer(s)?\n\n" +
                         "Each offending weight will be redirected to its Humanoid mirror bone " +
                         "(e.g. RightUpperLeg -> LeftUpperLeg). Weights with no mirror are zeroed " +
                         "and the remaining weights on the same vertex are scaled up.\n\n" +
                         "FBX-imported meshes will be cloned to editable .mesh assets in " +
                         $"{WeightFixer.GeneratedFolder}/ - the original FBX is never modified.\n\n" +
                         "Prefer the nondestructive component flow ('Save fixes as component' " +
                         "below the issue list) when you expect to re-import the model from " +
                         "Blender; the destructive path here writes into the clone .mesh and " +
                         "the reference is lost if the FBX subasset is regenerated.\n\n" +
                         "Ctrl+Z reverts the operation.";
            if (!EditorUtility.DisplayDialog("Fix weight contamination", msg, "Fix", "Cancel")) return;

            // Translate detector Issue -> fixer IssueRef so the fixer
            // module stays free of detection-side types.
            var refs = new List<WeightFixer.IssueRef>(issues.Count);
            foreach (var i in issues) {
                refs.Add(new WeightFixer.IssueRef {
                    Renderer       = i.Renderer,
                    VertexIndex    = i.VertexIndex,
                    OffendingBone  = i.OffendingBone,
                    Weight         = i.Weight,
                });
            }
            var result = WeightFixer.ApplyFixes(refs, _animator);

            // Drop the just-fixed issues from the visible list and refresh
            // the gizmo overlay. Avoid auto-rescanning so before/after
            // comparison remains visible, and Ctrl+Z is more
            // useful when the issue list still shows what was done.
            var fixedSet = new HashSet<DetectedIssue>(issues);
            _issues.RemoveAll(fixedSet.Contains);
            _selectedIssueIndices.Clear();
            SceneView.RepaintAll();

            string clonedNote = result.MeshesCloned > 0
                ? $" Cloned {result.MeshesCloned} mesh(es) to {WeightFixer.GeneratedFolder}/."
                : "";
            string skipNote = result.Skipped > 0
                ? $" Skipped {result.Skipped} (weight no longer present)."
                : "";
            AvatarQolLogger.Instance.Info(
                $"Weight fix: {result.Fixed} weight(s) corrected — " +
                $"{result.Mirrored} mirrored, {result.Zeroed} zeroed + renormalised, " +
                $"across {result.RenderersTouched} renderer(s).{clonedNote}{skipNote}");
            AssetDatabase.SaveAssets();
        }

        private int SelectedIssueCount() {
            PruneSelectedIssues();
            return _selectedIssueIndices.Count;
        }

        private void PruneSelectedIssues() {
            if (_selectedIssueIndices.Count == 0) return;
            _selectedIssueIndices.RemoveWhere(i => i < 0 || i >= _issues.Count);
        }

        private void SelectAllIssues() {
            _selectedIssueIndices.Clear();
            for (int i = 0; i < _issues.Count; i++) _selectedIssueIndices.Add(i);
        }

        private List<DetectedIssue> SelectedOrAllIssues() {
            PruneSelectedIssues();
            if (_selectedIssueIndices.Count == 0) return new List<DetectedIssue>(_issues);
            return _selectedIssueIndices
                .OrderBy(i => i)
                .Where(i => i >= 0 && i < _issues.Count)
                .Select(i => _issues[i])
                .Where(i => i != null)
                .ToList();
        }
    }
}
