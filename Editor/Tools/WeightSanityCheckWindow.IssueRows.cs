// WeightSanityCheckWindow.IssueRows.cs

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

#if false
        private void DrawPhysBoneIssueRow(PhysBoneClippingAnalyzer.Issue issue) {
            var severityColor = issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                ? AvatarQolCategoryColors.Humanoid
                : WkStyles.ColorWarning;
            var severityText = issue.Severity == PhysBoneClippingAnalyzer.Severity.High ? "high" : "medium";
            string boneName = issue.DrivenBone != null ? issue.DrivenBone.name : "(destroyed)";
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                WkStyles.BadgePill(severityText, severityColor,
                    issue.Severity == PhysBoneClippingAnalyzer.Severity.High
                        ? "No effective collider coverage or already-small clearance. This deserves attention."
                        : "Collider coverage exists or the estimated overlap is smaller, but the area is still worth checking.");
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"{issue.RendererPath}  v#{issue.VertexIndex}  {boneName}  move~{issue.EstimatedMotion * 100f:0.0}cm  clearance {issue.Clearance * 100f:0.0}cm",
                        issue.Reason),
                    WkStyles.Mono);
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(issue.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.Renderer;
                        EditorGUIUtility.PingObject(issue.Renderer);
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame the risky vertex in the Scene view."),
                        WkStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(issue.WorldPosition, sv.rotation, 0.18f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(issue.DrivenBone == null)) {
                    if (GUILayout.Button(new GUIContent("R", "Reveal the PhysBone-driven transform."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = issue.DrivenBone;
                        EditorGUIUtility.PingObject(issue.DrivenBone);
                        FlashHighlight(issue.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == issue.DrivenBone && issue.DrivenBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
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

#endif
        private void DrawIssueRowCompact(DetectedIssue i, int issueIndex) {
            string boneName = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
            Color tag; string tagText; string tagTooltip;
            switch (i.Category) {
                case IssueCategory.HumanoidCrossSide:
                    tag = AvatarQolCategoryColors.Humanoid; tagText = "humanoid";
                    tagTooltip = "Bone has a Humanoid ancestor on the avatar's opposite side from the vertex. Highest-confidence flag.";
                    break;
                case IssueCategory.SpatialCrossSide:
                    tag = AvatarQolCategoryColors.Spatial; tagText = "spatial";
                    tagTooltip = "Bone has no Humanoid ancestor; its world pivot sits on the opposite side of the vertex.";
                    break;
                case IssueCategory.CenterBandSideBleed:
                    tag = AvatarQolCategoryColors.Center; tagText = "center";
                    tagTooltip = "Vertex is in the centre stripe; a Left or Right bone exceeded the higher centre threshold.";
                    break;
                default:
                    tag = WkStyles.ColorInfo; tagText = "?"; tagTooltip = ""; break;
            }
            bool expanded = _expandedIssueRows.Contains(issueIndex);

            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(6);
                // Foldout caret on the far left toggles the per-row details.
                bool now = EditorGUILayout.Foldout(expanded, GUIContent.none, true);
                if (now != expanded) {
                    if (now) _expandedIssueRows.Add(issueIndex);
                    else _expandedIssueRows.Remove(issueIndex);
                }
                WkStyles.BadgePill(tagText, tag, tagTooltip);
                EditorGUILayout.LabelField(
                    new GUIContent($"v#{i.VertexIndex}  {i.VertexSide} → {boneName} ({i.BoneSide})  w={i.Weight:F3}",
                        $"Vertex #{i.VertexIndex} on the avatar's {i.VertexSide} side has weight {i.Weight:F3} on {boneName}, which is classified {i.BoneSide}. Click ∨ for the world position; use the row buttons to investigate or fix."),
                    WkStyles.Mono);
                GUILayout.FlexibleSpace();

                using (new EditorGUI.DisabledScope(i.Renderer == null)) {
                    if (GUILayout.Button(new GUIContent("P", "Ping the renderer in the hierarchy."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        if (i.Renderer != null) {
                            Selection.activeObject = i.Renderer;
                            EditorGUIUtility.PingObject(i.Renderer);
                        }
                    }
                }
                if (GUILayout.Button(new GUIContent("F", "Frame: move Scene camera to the vertex."),
                        WkStyles.MiniRowButton, GUILayout.Width(22))) {
                    var sv = SceneView.lastActiveSceneView;
                    if (sv != null) {
                        sv.LookAt(i.WorldPosition, sv.rotation, 0.15f);
                        sv.Repaint();
                    }
                }
                using (new EditorGUI.DisabledScope(i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("R",
                            "Reveal: select the offending bone, frame the vertex, and flash a marker disc in the Scene view for two seconds."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        Selection.activeObject = i.OffendingBone;
                        EditorGUIUtility.PingObject(i.OffendingBone);
                        var sv = SceneView.lastActiveSceneView;
                        if (sv != null) { sv.LookAt(i.WorldPosition, sv.rotation, 0.15f); sv.Repaint(); }
                        FlashHighlight(i.WorldPosition);
                    }
                    bool isPreviewing = _previewBone == i.OffendingBone && i.OffendingBone != null;
                    if (GUILayout.Button(new GUIContent(isPreviewing ? "Stop" : "Wobble",
                            "Temporarily wobble the offending bone so you can see how the bad weights deform the mesh. This does not move the Scene camera."),
                            WkStyles.MiniRowButton, GUILayout.Width(58))) {
                        if (isPreviewing) StopPreview();
                        else if (i.OffendingBone != null) StartPreview(i.OffendingBone);
                    }
                }
                using (new EditorGUI.DisabledScope(i.Renderer == null || i.OffendingBone == null)) {
                    if (GUILayout.Button(new GUIContent("?",
                            "Why? Send this vertex to the Inspect Vertex panel and run a per-weight verdict — useful for understanding why a related weight didn't flag. Result prints to the Unity console."),
                            WkStyles.MiniRowButton, GUILayout.Width(22))) {
                        _inspectRenderer = i.Renderer;
                        _inspectVertexIndex = i.VertexIndex;
                        InspectVertex();
                    }
                    if (GUILayout.Button(new GUIContent("Fix",
                            "Redirect this offending weight to the bone's Humanoid mirror (e.g. RightUpperLeg → LeftUpperLeg). When no mirror is available, zero the weight and renormalise the rest. FBX-imported meshes are cloned to an editable .mesh in Assets/AvatarQol Generated/ before any change."),
                            WkStyles.MiniRowButton, GUILayout.Width(34))) {
                        var name = i.OffendingBone != null ? i.OffendingBone.name : "(destroyed)";
                        FixIssues(new List<DetectedIssue> { i }, $"weight on {name}");
                    }
                }
            }
            if (expanded) {
                EditorGUILayout.LabelField(
                    $"   world pos:  ({i.WorldPosition.x:F4}, {i.WorldPosition.y:F4}, {i.WorldPosition.z:F4})",
                    WkStyles.Muted);
                EditorGUILayout.LabelField(
                    $"   bone path:  {(i.OffendingBone != null ? PathUtility.GetGameObjectPath(i.OffendingBone.gameObject) : "(destroyed)")}",
                    WkStyles.Muted);
            }
        }

        // Scene-view fade-out marker. Stores a single hit; the gizmo
        // overlay polls _flashUntil and renders a disc until then.
        private Vector3 _flashPos;
        private double _flashUntil;

        private void FlashHighlight(Vector3 worldPos) {
            _flashPos = worldPos;
            _flashUntil = EditorApplication.timeSinceStartup + 2.0;
            SceneView.RepaintAll();
        }

        private void DrawNonReadableBanner() {
            if (_nonReadableRenderers.Count == 0) return;
            if (WkStyles.Notice(NoticeKind.Warning,
                    $"{_nonReadableRenderers.Count} renderer(s) skipped — mesh has Read/Write disabled in importer.",
                    "Enable Read/Write & rescan",
                    "For every skipped renderer, find its source asset, set Read/Write Enabled in the model importer, reimport, then re-run the scan.")) {
                EnableReadWriteOnSkippedAndRescan();
            }
        }

        private void DrawVertexInspector() {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                EditorGUILayout.LabelField(
                    new GUIContent("Inspect specific vertex",
                        "When an issue you expect isn't flagged, drop the renderer here and type the vertex index. The console gets a per-weight verdict explaining exactly which gate every weight passed or failed against the current thresholds."),
                    EditorStyles.boldLabel);
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Renderer",
                            "The SkinnedMeshRenderer whose vertex you want to inspect. Auto-fills when you change 'Limit scan to' or click 'From selection' below."),
                        GUILayout.Width(64));
                    _inspectRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        new GUIContent(GUIContent.none.image, "Drop the SkinnedMeshRenderer to inspect."),
                        _inspectRenderer, typeof(SkinnedMeshRenderer), true);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Vertex #",
                            "The mesh vertex index to inspect. Find candidate indices via 'Dump weights for selection' below, or click 'Why?' on any flagged issue to auto-fill this field with that issue's vertex."),
                        GUILayout.Width(64));
                    _inspectVertexIndex = EditorGUILayout.IntField(_inspectVertexIndex);
                    using (new EditorGUI.DisabledScope(_inspectRenderer == null || _animator == null || !_animator.isHuman)) {
                        if (GUILayout.Button(
                                new GUIContent("Inspect",
                                    "Print the verdict for this vertex against current thresholds. Output goes to the Unity console."),
                                GUILayout.Width(80))) {
                            InspectVertex();
                        }
                    }
                    if (GUILayout.Button(
                            new GUIContent("From selection",
                                "Set the renderer to whatever's currently selected in the hierarchy."),
                            GUILayout.Width(110))) {
                        var go = Selection.activeGameObject;
                        if (go != null) _inspectRenderer = go.GetComponent<SkinnedMeshRenderer>();
                    }
                }
            }
        }
    }
}
