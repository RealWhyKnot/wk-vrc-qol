// PhysBonePresetWindow.Selection.cs

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

#if VRC_SDK_VRCSDK3
using VRC.SDK3.Dynamics.PhysBone.Components;
#endif

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBonePresetWindow {

        // -------- Selection panel --------

        private void DrawSelection() {
            using (WkStyles.Section($"1. Choose bones ({_selection.Count})",
                    "Drop the bones the preset will set up. Each top-level bone you drop in becomes a chain root; descendants are walked automatically. Pick the ear roots, the tail base, the skirt panel anchors, etc.")) {
                using (new EditorGUILayout.VerticalScope(GUILayout.MinHeight(60), GUILayout.MaxHeight(150))) {
                    _selectionScroll = EditorGUILayout.BeginScrollView(_selectionScroll);
                    if (_selection.Count == 0) {
                        EditorGUILayout.LabelField(
                            new GUIContent("(empty — pick bones in the Hierarchy and click 'Use selection')",
                                "Drag any number of bone Transforms here, or select them in the Hierarchy and click Use selection."),
                            EditorStyles.centeredGreyMiniLabel);
                    } else {
                        int removeIndex = -1;
                        bool dirty = false;
                        for (int i = 0; i < _selection.Count; i++) {
                            using (new EditorGUILayout.HorizontalScope()) {
                                var newT = (Transform)EditorGUILayout.ObjectField(
                                    new GUIContent(GUIContent.none.image, "A bone Transform that will receive a PhysBone (or be folded into a chain that gets one)."),
                                    _selection[i], typeof(Transform), allowSceneObjects: true);
                                if (newT != _selection[i]) { _selection[i] = newT; dirty = true; }
                                if (GUILayout.Button(new GUIContent("×", "Remove this bone from the list."),
                                        EditorStyles.miniButton, GUILayout.Width(22))) removeIndex = i;
                            }
                        }
                        if (removeIndex >= 0) { _selection.RemoveAt(removeIndex); dirty = true; }
                        if (dirty) RebuildAnalysis();
                    }
                    EditorGUILayout.EndScrollView();
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(new GUIContent("Use selected",
                            "Replace the bone list with the currently selected GameObjects."))) {
                        _selection = Selection.gameObjects.Where(g => g != null).Select(g => g.transform).Distinct().ToList();
                        RebuildAnalysis();
                    }
                    if (GUILayout.Button(new GUIContent("Add selected",
                            "Append the currently selected GameObjects to the bone list."))) {
                        foreach (var g in Selection.gameObjects) {
                            if (g == null) continue;
                            var t = g.transform;
                            if (!_selection.Contains(t)) _selection.Add(t);
                        }
                        RebuildAnalysis();
                    }
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(new GUIContent("Clear",
                            "Empty the bone list."), GUILayout.Width(70))) {
                        _selection.Clear();
                        RebuildAnalysis();
                    }
                }
            }
        }

        // -------- Analysis summary --------

        private void DrawAnalysisSummary() {
            if (_analysis == null) return;
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                if (_analysis.Chains.Count == 0) {
                    EditorGUILayout.LabelField(
                        new GUIContent("No detectable chains.",
                            "A 'chain' is a Transform plus its straight descendants. Drop hair/ear/tail roots here, not the avatar root."),
                        WkStyles.Muted);
                    return;
                }
                EditorGUILayout.LabelField(
                    new GUIContent(
                        $"{_analysis.Chains.Count} chain(s)  •  avg {_analysis.AverageChainBoneCount} bones  •  avg length {_analysis.AverageChainLengthMetres:F3} m  •  avg bone size {_analysis.AverageBoneSize:F3} m",
                        "Auto-measured from your selection. Used to score which preset best fits."),
                    WkStyles.Muted);
                if (_analysis.HostAnimator != null) {
                    var bone = _analysis.NearestHumanoidBoneType?.ToString() ?? "n/a";
                    var msg = _analysis.HostAnimator.isHuman
                        ? $"Host: {_analysis.HostAnimator.gameObject.name} (Humanoid; nearest bone: {bone}; dominant side: {_analysis.DominantSide})"
                        : $"Host: {_analysis.HostAnimator.gameObject.name} (NOT Humanoid — ear / tail / hair / dress presets need Humanoid)";
                    EditorGUILayout.LabelField(
                        new GUIContent(msg, "Host avatar's Animator. Side classification and humanoid-mirror lookups depend on a Humanoid rig."),
                        WkStyles.Muted);
                } else {
                    EditorGUILayout.LabelField(
                        new GUIContent("No Animator found in the parent chain — limited adaptation possible.",
                            "Without an Animator the analysis can't classify side, find Hips, or auto-add leg colliders."),
                        WkStyles.Muted);
                }
            }
        }

        // -------- Preset picker - cards --------

        private void DrawPresetPicker() {
            EditorGUILayout.LabelField(
                new GUIContent("2. Pick a preset",
                    "A preset writes parameter defaults tuned for a specific use case (ears, tail, hair, dress). Pick one to see its plan."),
                WkStyles.SubsectionTitle);

            if (_presets.Count == 0) {
                EditorGUILayout.LabelField("No presets discovered.", EditorStyles.centeredGreyMiniLabel);
                return;
            }

            // Determine the suggestion winner.
            IPhysBonePreset best = null;
            float bestScore = -1f;
            if (_analysis != null && _analysis.Chains.Count > 0) {
                foreach (var p in _presets) {
                    if (!_suggestionScores.TryGetValue(p.Id, out var s)) s = 0;
                    if (s > bestScore) { best = p; bestScore = s; }
                }
            }

            // Flow cards into rows that fit the window width.
            const float cardWidth = 200f;
            const float cardHeight = 110f;
            const float gutter = 6f;
            float available = position.width - 24f;
            int perRow = Mathf.Max(1, Mathf.FloorToInt((available + gutter) / (cardWidth + gutter)));
            for (int i = 0; i < _presets.Count; i += perRow) {
                using (new EditorGUILayout.HorizontalScope()) {
                    for (int j = 0; j < perRow && i + j < _presets.Count; j++) {
                        var preset = _presets[i + j];
                        DrawPresetCard(preset, isSuggested: preset == best, cardWidth, cardHeight);
                        if (j + 1 < perRow && i + j + 1 < _presets.Count) GUILayout.Space(gutter);
                    }
                    GUILayout.FlexibleSpace();
                }
                GUILayout.Space(gutter);
            }
        }

        private void DrawPresetCard(IPhysBonePreset preset, bool isSuggested, float width, float height) {
            float score = _suggestionScores.TryGetValue(preset.Id, out var s) ? s : 0f;
            bool isSelected = preset.Id == _selectedPresetId;

            // Reserve the card rect + draw background fills + border + content.
            var rect = GUILayoutUtility.GetRect(width, height, GUILayout.Width(width), GUILayout.Height(height));

            // Selection tint (under the helpBox so border still reads).
            if (isSelected) {
                var accent = WkStyles.ColorAccent;
                EditorGUI.DrawRect(rect, new Color(accent.r, accent.g, accent.b, 0.10f));
            }

            // Suggested ribbon at top of card.
            if (isSuggested) {
                var ribbon = new Rect(rect.x, rect.y, rect.width, 14);
                var accent = WkStyles.ColorAccent;
                EditorGUI.DrawRect(ribbon, new Color(accent.r, accent.g, accent.b, 0.55f));
                GUI.Label(ribbon, "SUGGESTED", new GUIStyle(EditorStyles.miniLabel) {
                    alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.white },
                });
            }

            // Border via four thin slabs (simpler than custom GUIStyle).
            float thick = isSuggested ? 2f : 1f;
            var border = isSuggested ? WkStyles.ColorAccent : WkStyles.ColorDivider;
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, rect.width, thick), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.yMax - thick, rect.width, thick), border);
            EditorGUI.DrawRect(new Rect(rect.x, rect.y, thick, rect.height), border);
            EditorGUI.DrawRect(new Rect(rect.xMax - thick, rect.y, thick, rect.height), border);

            // Inner padding-aware content area.
            var inner = new Rect(rect.x + 8, rect.y + (isSuggested ? 18 : 8), rect.width - 16, rect.height - (isSuggested ? 26 : 16));

            // Title + percentage row.
            var titleRect = new Rect(inner.x, inner.y, inner.width - 40, 16);
            var pctRect   = new Rect(inner.x + inner.width - 40, inner.y, 40, 16);
            GUI.Label(titleRect, new GUIContent(preset.DisplayName, preset.Description), EditorStyles.boldLabel);
            GUI.Label(pctRect, $"{Mathf.RoundToInt(score * 100)}%", new GUIStyle(EditorStyles.miniLabel) {
                alignment = TextAnchor.MiddleRight, fontStyle = FontStyle.Bold,
            });

            // Score bar with breakdown tooltip.
            var barBg = new Rect(inner.x, inner.y + 18, inner.width, 6);
            EditorGUI.DrawRect(barBg, new Color(0, 0, 0, 0.18f));
            var barFill = new Rect(barBg.x, barBg.y, barBg.width * Mathf.Clamp01(score), barBg.height);
            var fillColor = isSuggested ? WkStyles.ColorAccent : new Color(0.3f, 0.6f, 0.9f, 0.7f);
            EditorGUI.DrawRect(barFill, fillColor);
            // Hover-over-tooltip on the score area.
            string scoreTooltip = BuildScoreTooltip(preset);
            GUI.Label(barBg, new GUIContent("", scoreTooltip));

            // Description (capped to remaining space).
            var descRect = new Rect(inner.x, inner.y + 28, inner.width, inner.height - 28);
            GUI.Label(descRect, preset.Description, WkStyles.Muted);

            // Whole-card click handler (drawn last so it's on top of visuals
            // but transparent - visual layers underneath remain readable).
            if (GUI.Button(rect, new GUIContent("", $"Pick this preset to use for the plan below.\n\n{preset.Description}"), GUIStyle.none)) {
                if (preset.Id != _selectedPresetId) {
                    _selectedPresetId = preset.Id;
                    RebuildPlan();
                }
            }
        }

        private string BuildScoreTooltip(IPhysBonePreset preset) {
            if (_analysis == null || _analysis.Chains.Count == 0) return "Selection has no detectable chains.";
            if (!_suggestionExplanations.TryGetValue(preset.Id, out var signals) || signals.Count == 0) {
                return $"Score: {Mathf.RoundToInt(_suggestionScores[preset.Id] * 100)}%. (No breakdown available for this preset.)";
            }
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"Why {Mathf.RoundToInt(_suggestionScores[preset.Id] * 100)}%:");
            foreach (var s in signals) {
                sb.AppendLine($"  {(s.Contribution >= 0 ? "+" : "")}{s.Contribution:F2}  {s.Name}");
            }
            return sb.ToString().TrimEnd();
        }
    }
}
