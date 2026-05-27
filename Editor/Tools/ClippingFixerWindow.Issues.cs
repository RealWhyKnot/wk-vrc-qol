// ClippingFixerWindow.Issues.cs

using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using System.Linq;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Clipping;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class ClippingFixerWindow {

        private void DrawScanBar() {
            int selectedCount = SelectedWarningCount();
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanScan())) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan for clipping",
                                "Check the target mesh for actual penetration, self-intersection, and enabled PhysBone motion risk."),
                            GUILayout.MinWidth(140))) {
                        Scan();
                    }
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent(selectedCount > 0 ? $"Add component ({selectedCount} selected)" : "Add fix component",
                                "Non-destructive. Adds or updates a WhyKnotClippingFixIntent on the target mesh. If warnings are selected, only those warning sources and motion checks are saved."),
                            GUILayout.Width(190))) {
                        SaveFixAsComponent();
                    }
                    if (GUILayout.Button(
                            new GUIContent(selectedCount > 0 ? $"Apply selected ({selectedCount})" : "Apply destructive",
                                "Clone the target mesh to a generated .asset now, rewire the renderer to that clone, and write the clipping fix into the clone. If warnings are selected, only those warnings are applied."),
                            GUILayout.Height(28), GUILayout.Width(140))) {
                        ApplyDestructiveFix();
                    }
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0 && string.IsNullOrEmpty(_scanSummary))) {
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current warning list and clear Scene view markers."),
                            GUILayout.Height(28), GUILayout.Width(70))) {
                        ClearResults();
                    }
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Stop the active driven-bone wobble preview and restore its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(96))) {
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
            PruneSelectedWarnings();
            int selectedCount = SelectedWarningCount();
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(
                        _issues.Count > 0 && selectedCount > 0
                            ? $"Warnings ({_issues.Count}, {selectedCount} selected)"
                            : (_issues.Count > 0 ? $"Warnings ({_issues.Count})" : "Warnings"),
                        "Rows where the target mesh clips, self-intersects, or can be moved into a nearby surface by PhysBones."),
                    WkStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_issues.Count == 0 || selectedCount == _issues.Count)) {
                    if (GUILayout.Button(
                            new GUIContent("Select all", "Select every warning row."),
                            EditorStyles.miniButton, GUILayout.Width(74))) {
                        SelectAllWarnings();
                    }
                }
                using (new EditorGUI.DisabledScope(selectedCount == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Clear selection", "Clear every selected warning row."),
                            EditorStyles.miniButton, GUILayout.Width(100))) {
                        _selectedIssueIndices.Clear();
                    }
                }
                if (_lastSurfaceRendererCount > 0) {
                    EditorGUILayout.LabelField($"{_lastSurfaceRendererCount} comparison mesh(es)", WkStyles.Muted, GUILayout.Width(170));
                }
            }

            if (_issues.Count > 0) {
                string selectionText = selectedCount > 0
                    ? $" {selectedCount} selected warning(s) will be used by Add component and destructive apply."
                    : " With nothing selected, Add component and destructive apply use all warnings.";
                WkStyles.Notice(NoticeKind.Warning,
                    $"{_issues.Count} clipping warning(s) found.{selectionText} Add component also uses the current selection.");
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick one target mesh, add a body/comparison mesh, then scan." : "No clipping warnings found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    for (int i = 0; i < _issues.Count; i++) {
                        DrawIssueRow(_issues[i], i);
                        WkStyles.Divider();
                    }
                }
            }
        }

        private void DrawIssueRow(ClippingFixer.Issue issue, int issueIndex) {
            var severityColor = IssueColor(issue);
            var severityText = IssueBadge(issue);
            string comparison = string.IsNullOrEmpty(issue.ComparisonPath) ? "(none)" : issue.ComparisonPath;
            using (new EditorGUILayout.HorizontalScope()) {
                bool selected = _selectedIssueIndices.Contains(issueIndex);
                bool nextSelected = EditorGUILayout.Toggle(
                    new GUIContent(GUIContent.none.image, "Select this warning for the next destructive apply."),
                    selected,
                    GUILayout.Width(18));
                if (nextSelected != selected) {
                    if (nextSelected) _selectedIssueIndices.Add(issueIndex);
                    else _selectedIssueIndices.Remove(issueIndex);
                }
                WkStyles.BadgePill(severityText, severityColor,
                    IssueTooltip(issue));
                EditorGUILayout.LabelField(
                    new GUIContent(
                        IssueRowText(issue, comparison),
                        issue.Reason),
                    WkStyles.Mono);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(issue.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("Ping", "Ping the target renderer in the hierarchy."),
                            WkStyles.MiniRowButton, GUILayout.Width(42))) {
                        Selection.activeObject = issue.Renderer;
                        EditorGUIUtility.PingObject(issue.Renderer);
                    }
                }
                if (GUILayout.Button(new GUIContent("Frame", "Frame the clipping point in Scene view."),
                        WkStyles.MiniRowButton, GUILayout.Width(48))) {
                    Frame(issue.WorldPosition, 0.18f);
                }
                using (new EditorGUI.DisabledScope(issue.ComparisonRenderer == null)) {
                    if (GUILayout.Button(new GUIContent("Surface", "Ping the comparison renderer or self-intersecting target surface."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        Selection.activeObject = issue.ComparisonRenderer;
                        EditorGUIUtility.PingObject(issue.ComparisonRenderer);
                    }
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    bool isPreviewing = issue.DrivenBone != null && _previewBone == issue.DrivenBone;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            isPreviewing
                                ? "Stop wobbling this driven bone and restore its rest rotation."
                                : "Temporarily rotate the driven bone to preview the motion direction."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else StartPreview(issue.DrivenBone);
                    }
                }
            }
            EditorGUILayout.LabelField("   " + issue.Reason, WkStyles.Muted);
            if (issue.Kind == ClippingFixer.IssueKind.PhysBoneMotion) {
                string source = string.IsNullOrEmpty(issue.PhysBoneSourceLabel)
                    ? "PhysBone"
                    : issue.PhysBoneSourceLabel;
                string driven = issue.DrivenBone != null ? issue.DrivenBone.name : "(unknown bone)";
                EditorGUILayout.LabelField($"   source: {source}; driven bone: {driven}; nearest surface: {comparison}", WkStyles.Muted);
            } else {
                EditorGUILayout.LabelField($"   nearest surface: {comparison}", WkStyles.Muted);
            }
        }

        private static Color IssueColor(ClippingFixer.Issue issue) {
            if (issue == null) return AvatarQolCategoryColors.Center;
            switch (issue.Kind) {
                case ClippingFixer.IssueKind.SelfIntersection:
                    return AvatarQolCategoryColors.Spatial;
                case ClippingFixer.IssueKind.PhysBoneMotion:
                    return AvatarQolCategoryColors.Center;
                default:
                    return AvatarQolCategoryColors.Humanoid;
            }
        }

        private static string IssueBadge(ClippingFixer.Issue issue) {
            if (issue == null) return "clip";
            switch (issue.Kind) {
                case ClippingFixer.IssueKind.SelfIntersection:
                    return "self";
                case ClippingFixer.IssueKind.PhysBoneMotion:
                    return "motion";
                default:
                    return "clip";
            }
        }

        private static string IssueTooltip(ClippingFixer.Issue issue) {
            if (issue == null) return "Clipping warning.";
            switch (issue.Kind) {
                case ClippingFixer.IssueKind.SelfIntersection:
                    return "The target mesh has intersecting non-adjacent triangles.";
                case ClippingFixer.IssueKind.PhysBoneMotion:
                    return "A PhysBone-driven vertex can move into a nearby mesh surface.";
                default:
                    return "The target mesh is inside or intersecting a comparison mesh.";
            }
        }

        private static string IssueRowText(ClippingFixer.Issue issue, string comparison) {
            if (issue == null) return "";
            if (issue.Kind == ClippingFixer.IssueKind.PhysBoneMotion) {
                return $"v#{issue.VertexIndex}  motion {issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm  vs {comparison}";
            }
            return $"v#{issue.VertexIndex}  depth {issue.PenetrationDepth * 1000f:0.0}mm  vs {comparison}";
        }

        private void ClearResults() {
            _issues.Clear();
            _selectedIssueIndices.Clear();
            _scanSummary = "";
            _lastSurfaceRendererCount = 0;
            SceneView.RepaintAll();
        }

        private static void Frame(Vector3 worldPosition, float size) {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            sv.LookAt(worldPosition, sv.rotation, size);
            sv.Repaint();
        }

        private void FlashHighlight(Vector3 worldPos) {
            _flashPos = worldPos;
            _flashUntil = EditorApplication.timeSinceStartup + 2.0;
            SceneView.RepaintAll();
        }

        private void SaveFixAsComponent() {
            if (_targetRenderer == null || _issues.Count == 0) return;
            var selectedIssues = SelectedOrAllWarnings();
            int selectedCount = SelectedWarningCount();
            string msg =
                "Add or update a Clipping Fix component on the target mesh?\n\n" +
                "At play-mode entry and avatar upload, it re-scans this renderer's current mesh against the saved comparison mesh list. Mesh clipping warnings clone the target mesh in memory and push vertices out. PhysBone motion warnings temporarily tighten the matching PhysBone source during the run. The source mesh asset is never modified.\n\n" +
                (selectedCount > 0
                    ? $"The component will use only the {selectedCount} selected warning(s)."
                    : "No warnings are selected, so the current comparison mesh list and scan options are stored.");
            if (!EditorUtility.DisplayDialog("Add fix component", msg, "Add component", "Cancel")) return;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Add clipping fix component");
            var intent = _targetRenderer.GetComponent<WhyKnotClippingFixIntent>();
            if (intent == null) {
                intent = Undo.AddComponent<WhyKnotClippingFixIntent>(_targetRenderer.gameObject);
            } else {
                Undo.RecordObject(intent, "Update clipping fix component");
            }

            intent.targetRenderer = _targetRenderer;
            intent.animator = _animator;
            if (intent.comparisonRenderers == null) intent.comparisonRenderers = new System.Collections.Generic.List<SkinnedMeshRenderer>();
            intent.comparisonRenderers.Clear();
            foreach (var renderer in BuildSurfaceListForComponent(selectedIssues, selectedCount > 0)) {
                if (renderer != null && renderer != _targetRenderer && !intent.comparisonRenderers.Contains(renderer)) {
                    intent.comparisonRenderers.Add(renderer);
                }
            }
            intent.checkSelf = selectedCount > 0
                ? selectedIssues.Any(i => i != null && i.Kind == ClippingFixer.IssueKind.SelfIntersection)
                : _checkSelf;
            intent.includePhysBoneMotion = selectedCount > 0
                ? selectedIssues.Any(i => i != null && i.Kind == ClippingFixer.IssueKind.PhysBoneMotion)
                : _includePhysBoneMotion;
            intent.insideTolerance = _insideTolerance;
            intent.surfacePadding = _surfacePadding;
            intent.physBoneWeightFloor = _physBoneWeightFloor;
            intent.physBoneClearanceMargin = _physBoneClearanceMargin;
            intent.maxFixPasses = _maxFixPasses;
            intent.maxIssuesPerPhysBone = _maxIssuesPerPhysBone;
            EditorUtility.SetDirty(intent);
            Undo.CollapseUndoOperations(undoGroup);

            AvatarQolLogger.Instance.Info(
                $"clipping fix component saved on {_targetRenderer.name}: " +
                $"{intent.comparisonRenderers.Count} comparison renderer(s), checkSelf={intent.checkSelf}, includePhysBoneMotion={intent.includePhysBoneMotion}.");
        }

        private void ApplyDestructiveFix() {
            if (_targetRenderer == null || _issues.Count == 0) return;
            var selectedIssues = SelectedOrAllWarnings();
            int selectedCount = SelectedWarningCount();
            if (AvatarIntentSessionState.IsAnyIntentSessionActive()) {
                EditorUtility.DisplayDialog("Apply destructive fix",
                    "Stop the active preview/play/build mesh session before writing a generated mesh asset.", "OK");
                return;
            }

            string msg =
                $"Apply a destructive clipping fix to {_targetRenderer.name}?\n\n" +
                $"Mesh clipping warnings clone the target mesh to {ClippingFixer.GeneratedFolder}/, rewire the renderer, and write vertex fixes into the clone. PhysBone motion warnings adjust the matching PhysBone source settings with Undo support.\n\n" +
                (selectedCount > 0
                    ? $"{selectedCount} selected warning(s) will be applied. Unselected warnings are left for a later pass.\n\n"
                    : "No warnings are selected, so every current warning will be applied.\n\n") +
                "Ctrl+Z reverts the operation.";
            if (!EditorUtility.DisplayDialog("Apply destructive fix", msg, "Apply", "Cancel")) return;

            var result = ClippingFixer.ApplyDestructive(
                _targetRenderer,
                BuildSurfaceList(),
                BuildFixerSettings(),
                selectedCount > 0 ? selectedIssues : null);
            if (result.ConfigurationError) {
                EditorUtility.DisplayDialog("Apply destructive fix", result.Summary, "OK");
                return;
            }

            AvatarQolLogger.Instance.Info(
                $"mesh clipping destructive fix: {result.Summary} " +
                (result.ClonedPaths.Count > 0 ? $"Created {string.Join(", ", result.ClonedPaths)}." : ""));
            Scan();
        }

        private int SelectedWarningCount() {
            PruneSelectedWarnings();
            return _selectedIssueIndices.Count;
        }

        private void PruneSelectedWarnings() {
            if (_selectedIssueIndices.Count == 0) return;
            _selectedIssueIndices.RemoveWhere(i => i < 0 || i >= _issues.Count);
        }

        private void SelectAllWarnings() {
            _selectedIssueIndices.Clear();
            for (int i = 0; i < _issues.Count; i++) _selectedIssueIndices.Add(i);
        }

        private List<ClippingFixer.Issue> SelectedOrAllWarnings() {
            PruneSelectedWarnings();
            if (_selectedIssueIndices.Count == 0) return new List<ClippingFixer.Issue>(_issues);
            return _selectedIssueIndices
                .OrderBy(i => i)
                .Where(i => i >= 0 && i < _issues.Count)
                .Select(i => _issues[i])
                .Where(i => i != null)
                .ToList();
        }

        private List<SkinnedMeshRenderer> BuildSurfaceListForComponent(
                List<ClippingFixer.Issue> issues,
                bool useSelectedSources) {
            if (!useSelectedSources) return BuildSurfaceList();
            var output = new List<SkinnedMeshRenderer>();
            foreach (var issue in issues) {
                if (issue == null || issue.ComparisonRenderer == null || issue.ComparisonRenderer == _targetRenderer) continue;
                if (!output.Contains(issue.ComparisonRenderer)) output.Add(issue.ComparisonRenderer);
            }
            return output;
        }
    }
}
