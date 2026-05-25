// PhysBoneClippingRiskWindow.Issues.cs

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void DrawScanBar() {
            WkStyles.Notice(NoticeKind.Info,
                "Auto Mesh Fixes was removed in this release. The analyzer still finds clipping risks and can still reduce PhysBone motion; the mesh-fix workflow is no longer offered.");

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanScan())) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Scan for clipping",
                                "Run the PhysBone clipping-risk estimate for the moving mesh and the comparison meshes listed above."),
                            GUILayout.MinWidth(140))) {
                        Scan();
                    }
                }
                using (new EditorGUI.DisabledScope(!CanReduceMotionAny())) {
                    if (GUILayout.Button(
                            new GUIContent("Reduce motion",
                                "Immediate fallback: tighten the PhysBone or supported authoring component settings on every supported risk row."),
                            GUILayout.Height(28), GUILayout.Width(120))) {
                        ReduceMotion(_issues);
                    }
                }
                using (new EditorGUI.DisabledGroupScope(true)) {
                    GUILayout.Button(
                        new GUIContent("Auto Mesh Fixes (removed)",
                            "Tombstone: the Auto Mesh Fixes mesh-fix workflow was removed in this release because the garment-tighten pipeline never produced a usable result. The button is left here disabled so the workflow is not silently re-added."),
                        GUILayout.Height(28), GUILayout.Width(196));
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
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
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
                EditorGUILayout.EndScrollView();
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
                using (new EditorGUI.DisabledScope(!PhysBoneClippingAnalyzer.CanReduceMotion(issue))) {
                    if (GUILayout.Button(
                            new GUIContent("Motion",
                                "Immediate fallback: tighten this PhysBone source's motion settings."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        ReduceMotion(new[] { issue });
                    }
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

        private bool CanReduceMotionAny() {
            return _issues.Any(PhysBoneClippingAnalyzer.CanReduceMotion);
        }

        private void ReduceMotion(IEnumerable<PhysBoneClippingAnalyzer.Issue> issues) {
            var list = issues == null ? new List<PhysBoneClippingAnalyzer.Issue>() : issues.Where(i => i != null).ToList();
            if (list.Count == 0) return;

            var log = _verboseLog ? new StringBuilder() : null;
            log?.AppendLine("PhysBone Clipping Risks motion reduction");
            var result = PhysBoneClippingAnalyzer.ReduceMotionIssues(list, log);
            _scanSummary = result.SourcesChanged > 0
                ? $"{result.Summary} Scan again to verify."
                : result.Summary;
            if (result.UnsupportedSources > 0) {
                _scanSummary += $" {result.UnsupportedSources} source(s) were not supported.";
            }
            if (log != null) {
                log.AppendLine($"  sourcesChanged={result.SourcesChanged}");
                log.AppendLine($"  issuesCovered={result.IssuesCovered}");
                log.AppendLine($"  unsupportedSources={result.UnsupportedSources}");
                AvatarQolLogger.Instance.Info(log.ToString());
            }
            SceneView.RepaintAll();
            Repaint();
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
