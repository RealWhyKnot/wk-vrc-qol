// PhysBoneClippingRiskWindow.Setup.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void DrawSetup() {
            using (WkStyles.Section("1. Moving mesh",
                    "Pick the mesh that moves from PhysBones. The scan checks that mesh against itself and against any comparison meshes you add below.")) {
                WkStyles.LabeledField(
                    new GUIContent("Animator",
                        "The avatar Animator. The scan looks under this object for live VRCPhysBones and supported generated/custom PhysBone setup components."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (next != _animator) {
                            _animator = next;
                            ClearResults();
                        }
                    });
                WkStyles.LabeledField(
                    new GUIContent("Mesh to check",
                        "The one SkinnedMeshRenderer to scan for PhysBone-driven vertices. Pick hair, tail, skirt, sleeves, or another mesh that moves from PhysBones."),
                    () => {
                        var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_targetRenderer, typeof(SkinnedMeshRenderer), true);
                        if (next != _targetRenderer) {
                            _targetRenderer = next;
                            if (_targetRenderer != null && _animator == null) {
                                _animator = _targetRenderer.GetComponentInParent<Animator>(true);
                            }
                            ClearResults();
                        }
                    });

                if (!PhysBoneClippingAnalyzer.SdkAvailable) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "VRChat SDK 3 PhysBone types are not available in this project, so this scan cannot run.");
                } else if (_targetRenderer != null && (_targetRenderer.sharedMesh == null || !_targetRenderer.sharedMesh.isReadable)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The target mesh is not readable. Enable Read/Write on its model importer before scanning.");
                } else if (_animator != null && _targetRenderer != null && !_targetRenderer.transform.IsChildOf(_animator.transform)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The target mesh is not under the selected Animator. The scan can still run, but PhysBone ownership may not match what you expect.");
                }
            }
        }

        private void DrawTuning() {
            using (WkStyles.Section("2. Comparison meshes",
                    "Add body, clothing, accessories, or any other readable SkinnedMeshRenderer that the moving mesh should not pass through.")) {
                EditorGUILayout.LabelField(
                    "The moving mesh is always included, so self-clipping is checked even when this list is empty.",
                    WkStyles.Muted);
                DrawComparisonRendererList();
            }

            EditorGUILayout.Space(2);
            using (WkStyles.Section("3. Scan options",
                    "Defaults are tuned for a quick first pass. Raise the margin or lower the weight floor if you want a more sensitive scan.")) {
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log",
                        "Print scan counts and timing to the Console. Useful when performance is still too slow on a large mesh."),
                    _verboseLog);
                _showGizmos = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show gizmos in Scene view",
                        "Draw orange markers on risky vertices and a line to the nearest surface sample."),
                    _showGizmos);

                WkStyles.LabeledField(
                    new GUIContent("Driven weight floor",
                        "A vertex must have at least this much weight to a PhysBone-driven transform before it is considered part of the moving surface."),
                    () => _weightFloor = EditorGUILayout.Slider(_weightFloor, 0.001f, 0.5f));
                WkStyles.LabeledField(
                    new GUIContent("Clearance margin",
                        "How much empty space nearby mesh should have before the motion envelope is considered risky. 0.025 m is 2.5 cm."),
                    () => _clearanceMargin = EditorGUILayout.Slider(_clearanceMargin, 0.005f, 0.15f));
                WkStyles.LabeledField(
                    new GUIContent("Max rows per PhysBone",
                        "Caps repeated warnings from one PhysBone so one skirt or hair chain does not flood the list."),
                    () => _maxIssuesPerPhysBone = EditorGUILayout.IntSlider(_maxIssuesPerPhysBone, 1, 25));
            }
        }

        private void DrawComparisonRendererList() {
            if (_comparisonRenderers == null) _comparisonRenderers = new List<SkinnedMeshRenderer>();

            int unreadableCount = 0;
            for (int i = 0; i < _comparisonRenderers.Count; i++) {
                using (new EditorGUILayout.HorizontalScope()) {
                    var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(
                        new GUIContent($"Mesh {i + 1}",
                            "A mesh to use as nearby surface, such as body, clothing, hair, accessories, or another area you want checked."),
                        _comparisonRenderers[i],
                        typeof(SkinnedMeshRenderer),
                        true);
                    if (next != _comparisonRenderers[i]) {
                        _comparisonRenderers[i] = next;
                        ClearResults();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Remove", "Remove this comparison mesh from the scan."),
                            EditorStyles.miniButton, GUILayout.Width(62))) {
                        _comparisonRenderers.RemoveAt(i);
                        ClearResults();
                        i--;
                        continue;
                    }
                }

                var renderer = i >= 0 && i < _comparisonRenderers.Count ? _comparisonRenderers[i] : null;
                if (renderer != null && (renderer.sharedMesh == null || !renderer.sharedMesh.isReadable)) {
                    unreadableCount++;
                }
            }

            using (new EditorGUILayout.HorizontalScope()) {
                if (GUILayout.Button(
                        new GUIContent("Add comparison mesh",
                            "Add another empty slot where you can drag a body, clothing, accessory, or any other SkinnedMeshRenderer."),
                        GUILayout.Width(160))) {
                    _comparisonRenderers.Add(null);
                    ClearResults();
                }
                using (new EditorGUI.DisabledScope(_comparisonRenderers.Count == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Clear list", "Remove every comparison mesh."),
                            GUILayout.Width(80))) {
                        _comparisonRenderers.Clear();
                        ClearResults();
                    }
                }
                GUILayout.FlexibleSpace();
            }

            if (_comparisonRenderers.Count == 0) {
                EditorGUILayout.LabelField("No extra comparison meshes. The scan will check the moving mesh against itself.", WkStyles.Muted);
            }
            if (unreadableCount > 0) {
                WkStyles.Notice(NoticeKind.Warning,
                    $"{unreadableCount} comparison mesh(es) are not readable and will be skipped. Enable Read/Write on those model imports to include them.");
            }
        }

        private bool CanScan() {
            return PhysBoneClippingAnalyzer.SdkAvailable &&
                   _animator != null &&
                   _targetRenderer != null &&
                   _targetRenderer.sharedMesh != null &&
                   _targetRenderer.sharedMesh.isReadable;
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            var renderer = go.GetComponent<SkinnedMeshRenderer>() ?? go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            var animator = go.GetComponent<Animator>() ??
                           go.GetComponentInParent<Animator>(true) ??
                           go.GetComponentInChildren<Animator>(true);
            if (renderer != null) _targetRenderer = renderer;
            if (animator == null && renderer != null) animator = renderer.GetComponentInParent<Animator>(true);
            if (animator != null) _animator = animator;
            ClearResults();
        }
    }
}
