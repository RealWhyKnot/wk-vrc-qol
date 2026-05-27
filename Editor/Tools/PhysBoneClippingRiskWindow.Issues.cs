// PhysBoneClippingRiskWindow.Issues.cs

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.PhysBoneClipping;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanScan())) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan for clipping",
                                "Check the moving mesh for actual penetration into itself and the comparison meshes listed above."),
                            GUILayout.MinWidth(140))) {
                        Scan();
                    }
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Add fix component",
                                "Non-destructive. Adds or updates a WhyKnotPhysBoneClippingFixIntent on the moving mesh. At play mode and avatar upload it clones the current mesh in memory and pushes it out of the comparison meshes."),
                            GUILayout.Width(160))) {
                        SaveFixAsComponent();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Apply destructive",
                                "Clone the moving mesh to a generated .asset now, rewire the renderer to that clone, and write the clipping fix into the clone. The original imported mesh asset is not modified."),
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
                GUILayout.FlexibleSpace();
                if (!string.IsNullOrEmpty(_scanSummary)) {
                    EditorGUILayout.LabelField(_scanSummary, WkStyles.Muted);
                }
            }
        }

        private void DrawIssues() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent(_issues.Count > 0 ? $"Warnings ({_issues.Count})" : "Warnings",
                        "Rows where the moving mesh is inside or intersecting a comparison surface."),
                    WkStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                if (_lastSurfaceRendererCount > 0) {
                    EditorGUILayout.LabelField($"{_lastSurfaceRendererCount} comparison mesh(es)", WkStyles.Muted, GUILayout.Width(170));
                }
            }

            if (_issues.Count > 0) {
                WkStyles.Notice(NoticeKind.Warning,
                    $"{_issues.Count} clipping warning(s) found. Use the component flow for fixes that should survive mesh re-imports; use destructive apply for a generated mesh asset you can inspect now.");
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick one moving mesh, add a body/comparison mesh, then scan." : "No clipping warnings found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    foreach (var issue in _issues) {
                        DrawIssueRow(issue);
                        WkStyles.Divider();
                    }
                }
            }
        }

        private void DrawIssueRow(PhysBoneClippingFixer.Issue issue) {
            var severityColor = issue.Kind == PhysBoneClippingFixer.IssueKind.SelfIntersection
                ? AvatarQolCategoryColors.Spatial
                : AvatarQolCategoryColors.Humanoid;
            var severityText = issue.Kind == PhysBoneClippingFixer.IssueKind.SelfIntersection ? "self" : "clip";
            string comparison = string.IsNullOrEmpty(issue.ComparisonPath) ? "(none)" : issue.ComparisonPath;
            using (new EditorGUILayout.HorizontalScope()) {
                WkStyles.BadgePill(severityText, severityColor,
                    issue.Kind == PhysBoneClippingFixer.IssueKind.SelfIntersection
                        ? "The target mesh has intersecting non-adjacent triangles."
                        : "The target mesh is inside or intersecting a comparison mesh.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"v#{issue.VertexIndex}  depth {issue.PenetrationDepth * 1000f:0.0}mm  vs {comparison}",
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
            }
            EditorGUILayout.LabelField("   " + issue.Reason, WkStyles.Muted);
            EditorGUILayout.LabelField($"   nearest surface: {comparison}", WkStyles.Muted);
        }

        private void ClearResults() {
            _issues.Clear();
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
            string msg =
                "Add or update a PhysBone Clipping Fix component on the moving mesh?\n\n" +
                "At play-mode entry and avatar upload, it re-scans this renderer's current mesh against the saved comparison mesh list, clones the target mesh in memory, and pushes clipping vertices out. The source mesh asset is never modified.";
            if (!EditorUtility.DisplayDialog("Add fix component", msg, "Add component", "Cancel")) return;

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Add PhysBone clipping fix component");
            var intent = _targetRenderer.GetComponent<WhyKnotPhysBoneClippingFixIntent>();
            if (intent == null) {
                intent = Undo.AddComponent<WhyKnotPhysBoneClippingFixIntent>(_targetRenderer.gameObject);
            } else {
                Undo.RecordObject(intent, "Update PhysBone clipping fix component");
            }

            intent.targetRenderer = _targetRenderer;
            if (intent.comparisonRenderers == null) intent.comparisonRenderers = new System.Collections.Generic.List<SkinnedMeshRenderer>();
            intent.comparisonRenderers.Clear();
            foreach (var renderer in BuildSurfaceList()) {
                if (renderer != null && renderer != _targetRenderer && !intent.comparisonRenderers.Contains(renderer)) {
                    intent.comparisonRenderers.Add(renderer);
                }
            }
            intent.checkSelf = _checkSelf;
            intent.insideTolerance = _insideTolerance;
            intent.surfacePadding = _surfacePadding;
            intent.maxFixPasses = _maxFixPasses;
            EditorUtility.SetDirty(intent);
            Undo.CollapseUndoOperations(undoGroup);

            AvatarQolLogger.Instance.Info(
                $"PhysBone clipping fix component saved on {_targetRenderer.name}: " +
                $"{intent.comparisonRenderers.Count} comparison renderer(s), checkSelf={intent.checkSelf}.");
        }

        private void ApplyDestructiveFix() {
            if (_targetRenderer == null || _issues.Count == 0) return;
            if (AvatarIntentSessionState.IsAnyIntentSessionActive()) {
                EditorUtility.DisplayDialog("Apply destructive fix",
                    "Stop the active preview/play/build mesh session before writing a generated mesh asset.", "OK");
                return;
            }

            string msg =
                $"Apply a destructive clipping fix to {_targetRenderer.name}?\n\n" +
                $"The target mesh will be cloned to {PhysBoneClippingFixer.GeneratedFolder}/, the renderer will be rewired to that clone, and the fix will be written into the clone. The original mesh asset is not modified.\n\n" +
                "Ctrl+Z reverts the operation.";
            if (!EditorUtility.DisplayDialog("Apply destructive fix", msg, "Apply", "Cancel")) return;

            var result = PhysBoneClippingFixer.ApplyDestructive(
                _targetRenderer,
                BuildSurfaceList(),
                BuildFixerSettings());
            if (result.ConfigurationError) {
                EditorUtility.DisplayDialog("Apply destructive fix", result.Summary, "OK");
                return;
            }

            AvatarQolLogger.Instance.Info(
                $"PhysBone clipping destructive fix: {result.Summary} " +
                (result.ClonedPaths.Count > 0 ? $"Created {string.Join(", ", result.ClonedPaths)}." : ""));
            Scan();
        }
    }
}
