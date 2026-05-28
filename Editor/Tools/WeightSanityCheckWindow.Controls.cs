// WeightSanityCheckWindow.Controls.cs

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

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Weight Sanity Check",
                        "Find mesh weights that pull part of the avatar toward the wrong left/right side, then review or fix them."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawHeader() {
            using (WkStyles.Section("1. Pick avatar",
                    "Choose the Humanoid avatar to scan. Optionally narrow the scan to one renderer while debugging an outfit or mesh.")) {
                WkStyles.LabeledField(
                    new GUIContent("Animator",
                        "The Humanoid Animator at the root of your avatar. The scan walks every SkinnedMeshRenderer underneath it and uses the Humanoid bone bindings (Hips, LeftUpperLeg, RightUpperLeg) to derive the avatar's left/right axis. Generic / non-Humanoid rigs aren't supported."),
                    () => {
                        var newAnim = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (newAnim != _animator) {
                            _animator = newAnim;
                            _issues.Clear();
                            _selectedIssueIndices.Clear();
                            _scanSummary = "";
                        }
                    });
                if (_animator != null && !_animator.isHuman) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "Animator is not Humanoid. The symmetry check needs Humanoid bone bindings (LeftUpperLeg, RightUpperLeg, Hips).");
                }
                WkStyles.LabeledField(
                    new GUIContent("Only scan renderer",
                        "Optional. When set, Scan only walks this single SkinnedMeshRenderer instead of every renderer under the avatar. Useful when debugging one outfit / mesh without touching the exclusion list. Auto-fills the Inspect Vertex renderer below."),
                    () => {
                        var newLimit = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_limitToRenderer, typeof(SkinnedMeshRenderer), true);
                        if (newLimit != _limitToRenderer) {
                            _limitToRenderer = newLimit;
                            if (newLimit != null && _inspectRenderer == null) _inspectRenderer = newLimit;
                        }
                    });
                if (_animator != null && _limitToRenderer != null
                        && !_limitToRenderer.transform.IsChildOf(_animator.transform)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The 'Limit scan to' renderer is not a descendant of the picked Animator. The scan will still run on it, but side classification uses the Animator's Hips so it may misbehave for renderers parented elsewhere.");
                }
            }
        }

        private void DrawAdvanced() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Detection thresholds, exclusion list, diagnostics (verbose log, gizmos), and the per-vertex inspector. Folded by default — most users only need Animator + Scan + Fix all."),
                true, WkStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            DrawTunables();
            EditorGUILayout.Space(2);
            DrawExclusions();
            EditorGUILayout.Space(2);
            DrawDiagnostics();
        }

        private void DrawTunables() {
            using (WkStyles.Section("Detection thresholds",
                    "Knobs that control how aggressive the scanner is. Sensible defaults; tune only when the issue list is too noisy or too quiet.")) {
                WkStyles.LabeledField(
                    new GUIContent("Weight floor",
                        "Weights below this fraction are ignored as noise. Default 0 surfaces every cross-side weight, however small -- bleed in the 0.0001..0.001 band still stretches the mesh visibly under motion. Raise toward 0.02 if a body mesh is flagging too much. Range 0..0.5."),
                    () => _weightFloor = EditorGUILayout.Slider(_weightFloor, 0f, 0.5f));
                WkStyles.LabeledField(
                    new GUIContent("Center margin",
                        "Half-width of the on-spine centre stripe in metres, in Hips local X. Vertices within +/- this distance of the spine count as Center. Default 0 disables the stripe so every vertex is Left or Right by sign of Hips-local X. Raise toward 0.005 only if bind-pose noise around the spine is producing false positives. Range 0..0.2 m."),
                    () => _centerMargin = EditorGUILayout.Slider(_centerMargin, 0f, 0.2f));
                using (new EditorGUILayout.HorizontalScope()) {
                    _scanCenterBand = EditorGUILayout.ToggleLeft(
                        new GUIContent("Scan centre-band vertices",
                            "When on, centre-stripe vertices are scanned for cross-side weights using the higher centre threshold below (rather than skipped). Off by default; only meaningful once Center margin is raised above 0 so centre-stripe vertices actually exist."),
                        _scanCenterBand, GUILayout.Width(220));
                }
                if (_scanCenterBand) {
                    WkStyles.LabeledField(
                        new GUIContent("Centre threshold",
                            "Minimum weight a centre-stripe vertex must have to a Left or Right bone before it's flagged. Higher than the regular floor because small bleed near the spine is usually fine."),
                        () => _centerCrossSideFloor = EditorGUILayout.Slider(_centerCrossSideFloor, 0f, 0.5f));
                }
            }
        }

        private void DrawDiagnostics() {
            using (WkStyles.Section("Diagnostics",
                    "Visualisation, logging, and the per-vertex Inspect tool. Helpful when the scan isn't flagging something you expected, or when triaging hundreds of issues.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    _showGizmos = EditorGUILayout.ToggleLeft(
                        new GUIContent("Show gizmos in Scene view",
                            "Draw a red marker at every flagged vertex's bind-pose world position. Helps you see where issues cluster on the avatar."),
                        _showGizmos, GUILayout.Width(220));
                    _verboseLog = EditorGUILayout.ToggleLeft(
                        new GUIContent("Verbose log",
                            "On scan, dump per-renderer stats and per-skipped-weight reasons to the Unity console. Useful for understanding why a weight you expected isn't being flagged."),
                        _verboseLog);
                }
                EditorGUILayout.Space(4);
                DrawVertexInspector();
                if (_showConsoleNoticeAfterInspect) {
                    if (WkStyles.ConsoleResultNotice("Inspect output")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterInspect = false;
                    }
                }
                EditorGUILayout.Space(2);
                if (GUILayout.Button(
                        new GUIContent("Dump weights for selection",
                            "Print every vertex's bone weights for the currently selected SkinnedMeshRenderer to the Unity console. Useful when an issue you expect isn't being flagged."),
                        GUILayout.Height(22))) {
                    DumpSelectedRendererWeights();
                }
                if (_showConsoleNoticeAfterDump) {
                    if (WkStyles.ConsoleResultNotice("Weight dump")) {
                        EditorApplication.ExecuteMenuItem("Window/General/Console");
                        _showConsoleNoticeAfterDump = false;
                    }
                }
            }
        }

        private void DrawExclusions() {
            using (WkStyles.Section("Exclude renderers (legit cross-side)",
                    "Add any SkinnedMeshRenderer that bridges left/right by design (capes, dresses, tails). They won't be scanned.")) {
                if (_excludedRenderers.Count == 0) {
                    EditorGUILayout.LabelField("(none)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    int removeIndex = -1;
                    for (int i = 0; i < _excludedRenderers.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            _excludedRenderers[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                                new GUIContent(GUIContent.none.image, "A SkinnedMeshRenderer the scan should ignore (e.g. capes, dresses, tails that legitimately bridge left and right)."),
                                _excludedRenderers[i], typeof(SkinnedMeshRenderer), true);
                            if (GUILayout.Button(new GUIContent("×", "Remove this renderer from the exclusion list."),
                                    EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                        }
                    }
                    if (removeIndex >= 0) _excludedRenderers.RemoveAt(removeIndex);
                }
                if (GUILayout.Button(new GUIContent("Add row", "Append an empty slot for a new renderer to exclude. Drop a SkinnedMeshRenderer onto it after."),
                        EditorStyles.miniButton, GUILayout.Width(80))) {
                    _excludedRenderers.Add(null);
                }
            }
        }

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canScan = _animator != null && _animator.isHuman;
                using (new EditorGUI.DisabledScope(!canScan)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan",
                                "Walk every SkinnedMeshRenderer under the Animator (or just the renderer selected above) and flag vertices weighted to a bone on the avatar's opposite side. Run again any time after a fix to refresh."),
                            GUILayout.MinWidth(140))) Scan();
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Restore the currently-wobbled bone to its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, WkStyles.Muted);
                }
            }
        }

        private void DrawIssues() {
            PruneSelectedIssues();
            int selectedCount = SelectedIssueCount();
            // Header bar: "Issues (N)" + Fix all + Clear, attached to the
            // list so the action sits next to what it acts on.
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        _issues.Count > 0 && selectedCount > 0
                            ? $"Issues ({_issues.Count}, {selectedCount} selected)"
                            : (_issues.Count > 0 ? $"Issues ({_issues.Count})" : "Issues"),
                        "Step 3. Each row is one suspicious bone weight on one vertex. The bracketed tag shows confidence: [humanoid] = bone is on the wrong Humanoid side, [spatial] = inferred from world position, [center] = mid-line bleed."),
                    WkStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                bool isPreviewing = AvatarPreviewController.IsPreviewing
                    && _animator != null
                    && AvatarPreviewController.SourceAvatar == _animator.gameObject;
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    using (new EditorGUI.DisabledScope(selectedCount == _issues.Count)) {
                        if (GUILayout.Button(
                                new GUIContent("Select all", "Select every issue row."),
                                EditorStyles.miniButton, GUILayout.Width(74))) {
                            SelectAllIssues();
                        }
                    }
                    using (new EditorGUI.DisabledScope(selectedCount == 0)) {
                        if (GUILayout.Button(
                                new GUIContent("Clear selection", "Clear every selected issue row."),
                                EditorStyles.miniButton, GUILayout.Width(100))) {
                            _selectedIssueIndices.Clear();
                        }
                    }
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Save fixes as component",
                                "Recommended for fixes you want to survive a Blender re-import. Adds (or updates) a WhyKnotWeightFixIntent component on each renderer with issues using the current scan settings. Row selection is ignored for this button."),
                            GUILayout.Width(190))) {
                        SaveIssuesAsComponents();
                    }
                    using (new EditorGUI.DisabledScope(_animator == null || isPreviewing)) {
                        if (GUILayout.Button(
                                new GUIContent(selectedCount > 0 ? $"Preview selected ({selectedCount})" : "Preview",
                                    "Non-destructive. Clone the avatar in place and apply selected fixes, or all listed fixes when nothing is selected, to the clone so you can see the deformation without committing changes."),
                                GUILayout.Height(28), GUILayout.Width(selectedCount > 0 ? 150 : 96))) {
                            StartPreview(SelectedOrAllIssues());
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent(selectedCount > 0 ? $"Fix selected ({selectedCount})" : $"Fix all ({_issues.Count})",
                                "Destructive: write corrected weights into a cloned .mesh asset under Assets/AvatarQol Generated/ and rewire the renderer to the clone now. Uses selected rows, or all rows when nothing is selected."),
                            GUILayout.Height(28), GUILayout.Width(130))) {
                        var selectedOrAll = SelectedOrAllIssues();
                        FixIssues(selectedOrAll, selectedCount > 0 ? $"{selectedCount} selected issue(s)" : $"{_issues.Count} issue(s)");
                    }
                    using (new EditorGUI.DisabledScope(!isPreviewing)) {
                        if (GUILayout.Button(
                                new GUIContent("Stop preview", "Destroy the preview clone and un-hide the source avatar."),
                                GUILayout.Height(28), GUILayout.Width(110))) {
                            AvatarPreviewController.StopPreview();
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current issue list and clear the gizmo overlay. Doesn't undo any fixes you've already applied."),
                            GUILayout.Height(28), GUILayout.Width(70))) {
                        _issues.Clear();
                        _selectedIssueIndices.Clear();
                        _scanSummary = "";
                        _expandedIssueRows.Clear();
                        SceneView.RepaintAll();
                    }
                }
            }

            // Inline legend so the bracket tags aren't mystery jargon.
            if (_issues.Count > 0) {
                if (selectedCount > 0) {
                    WkStyles.Notice(NoticeKind.Info,
                        $"{selectedCount} selected issue(s) will be used by Preview and Fix. Save fixes as component uses the current scan settings instead.");
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField("Legend:", WkStyles.Muted, GUILayout.Width(54));
                    WkStyles.BadgePill("humanoid", AvatarQolCategoryColors.Humanoid,
                        "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.");
                    WkStyles.BadgePill("spatial", AvatarQolCategoryColors.Spatial,
                        "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.");
                    WkStyles.BadgePill("center", AvatarQolCategoryColors.Center,
                        "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.");
                    GUILayout.FlexibleSpace();
                }
            }

            // Diagnostic banner for the "scan ran, found nothing, but most
            // of the geometry was filtered as Center" failure mode seen on
            // narrow accessory meshes before the default centerMargin came down.
            // Only fires
            // after a scan has actually run (_scanSummary populated) so
            // first-launch doesn't flash a help box at an empty list.
            if (_issues.Count == 0 && !string.IsNullOrEmpty(_scanSummary)
                    && _lastScanCenterVerts > 0
                    && _lastScanCenterVerts > (_lastScanLeftRightVerts + _lastScanCenterVerts) * 8 / 10) {
                int total = _lastScanLeftRightVerts + _lastScanCenterVerts;
                int pct = total > 0 ? (_lastScanCenterVerts * 100 / total) : 0;
                WkStyles.Notice(NoticeKind.Warning,
                    $"{pct}% of vertices ({_lastScanCenterVerts:N0} of {total:N0}) classified as Center and were filtered. " +
                    "Lower 'Center margin' in Advanced toward 0, or confirm the avatar is in T-pose during the scan (the bind-pose math assumes near-bind orientation).");
            }

            using (new EditorGUILayout.VerticalScope(
                    EditorStyles.helpBox,
                    GUILayout.Height(WkStyles.CappedListHeight(_issues.Count, 24f, 120f, 280f)))) {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick an Animator, then click Scan." : "No issues found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    // Pre-bucket counts once per draw - was O(n^2) before.
                    var perRendererCount = new Dictionary<SkinnedMeshRenderer, int>();
                    foreach (var i in _issues) {
                        if (i.Renderer == null) continue;
                        perRendererCount.TryGetValue(i.Renderer, out var n);
                        perRendererCount[i.Renderer] = n + 1;
                    }
                    SkinnedMeshRenderer lastRenderer = null;
                    bool currentCollapsed = false;
                    int issueIndex = 0;
                    foreach (var i in _issues) {
                        int captured = issueIndex++;
                        if (i.Renderer != lastRenderer) {
                            int count = i.Renderer != null && perRendererCount.TryGetValue(i.Renderer, out var n) ? n : 0;
                            currentCollapsed = _collapsedRenderers.Contains(i.RendererPath);
                            using (new EditorGUILayout.HorizontalScope()) {
                                bool now = EditorGUILayout.Foldout(!currentCollapsed,
                                    new GUIContent($"{i.RendererPath}  —  {count} issue(s)" + (i.Renderer == null ? "  (renderer destroyed)" : ""),
                                        "Click to collapse all issues from this renderer. Useful when one mesh has hundreds of issues you've already triaged."),
                                    true, WkStyles.FoldoutHeader);
                                bool nowCollapsed = !now;
                                if (nowCollapsed != currentCollapsed) {
                                    if (nowCollapsed) _collapsedRenderers.Add(i.RendererPath);
                                    else _collapsedRenderers.Remove(i.RendererPath);
                                    currentCollapsed = nowCollapsed;
                                }
                                GUILayout.FlexibleSpace();
                            }
                            lastRenderer = i.Renderer;
                        }
                        if (!currentCollapsed) DrawIssueRowCompact(i, captured);
                    }
                }
                EditorGUILayout.EndScrollView();
            }
        }

    }
}
