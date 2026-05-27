// PhysBoneClippingRiskWindow.Issues.cs

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void DrawScanBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanScan())) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan for clipping",
                                "Run the PhysBone clipping-risk estimate for the moving mesh and the comparison meshes listed above."),
                            GUILayout.MinWidth(140))) {
                        Scan();
                    }
                }
                using (new EditorGUI.DisabledScope(_previewBone == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop wobble",
                                "Restore the currently-wobbled bone to its rest rotation."),
                            GUILayout.Height(28), GUILayout.Width(110))) {
                        StopPreview();
                    }
                }
                using (new EditorGUI.DisabledScope(_issues.Count == 0 && string.IsNullOrEmpty(_scanSummary))) {
                    if (GUILayout.Button(
                            new GUIContent("Clear",
                                "Drop the current risk list and clear Scene view markers."),
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
                    new GUIContent(_issues.Count > 0 ? $"Risks ({_issues.Count})" : "Risks",
                        "Rows where the estimated PhysBone motion envelope reaches nearby mesh surface."),
                    WkStyles.SubsectionTitle);
                GUILayout.FlexibleSpace();
                if (_lastSurfaceRendererCount > 1) {
                    EditorGUILayout.LabelField($"{_lastSurfaceRendererCount} surface meshes sampled", WkStyles.Muted, GUILayout.Width(170));
                }
            }

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true))) {
                if (_issues.Count == 0) {
                    EditorGUILayout.LabelField(
                        _scanSummary == "" ? "Pick one moving mesh, add any comparison meshes, then scan." : "No likely PhysBone clipping risks found.",
                        EditorStyles.centeredGreyMiniLabel);
                } else {
                    foreach (var issue in _issues) {
                        DrawIssueRow(issue);
                        WkStyles.Divider();
                    }
                }
            }
        }

        private void DrawIssueRow(PhysBoneClippingAnalyzer.Issue issue) {
            var severityColor = issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                ? AvatarQolCategoryColors.Humanoid
                : WkStyles.ColorWarning;
            var severityText = issue.Severity == PhysBoneClippingAnalyzer.Severity.High ? "high" : "medium";
            string boneName = issue.DrivenBone != null ? issue.DrivenBone.name : "(destroyed)";
            using (new EditorGUILayout.HorizontalScope()) {
                WkStyles.BadgePill(severityText, severityColor,
                    issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                        ? "No effective collider coverage or already-small clearance. This deserves attention."
                        : "Collider coverage exists or the estimated overlap is smaller, but the area is still worth checking.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"v#{issue.VertexIndex}  {boneName}  move~{issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm",
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
                if (GUILayout.Button(new GUIContent("Frame", "Frame the risky vertex in Scene view."),
                        WkStyles.MiniRowButton, GUILayout.Width(48))) {
                    Frame(issue.WorldPosition, 0.18f);
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    if (GUILayout.Button(new GUIContent("Reveal", "Select and ping the PhysBone-driven transform."),
                            WkStyles.MiniRowButton, GUILayout.Width(52))) {
                        Selection.activeObject = issue.DrivenBone;
                        EditorGUIUtility.PingObject(issue.DrivenBone);
                        FlashHighlight(issue.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == issue.DrivenBone && issue.DrivenBone != null;
                    if (GUILayout.Button(
                            new GUIContent(isPreviewing ? "Stop" : "Wobble",
                                "Temporarily wobble the driven transform so you can inspect likely clipping. This does not move the Scene camera."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else StartPreview(issue.DrivenBone);
                    }
                }
            }
            EditorGUILayout.LabelField("   " + issue.Reason, WkStyles.Muted);
            EditorGUILayout.LabelField($"   nearest surface: {issue.NearestSurfacePath}", WkStyles.Muted);
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
    }
}
