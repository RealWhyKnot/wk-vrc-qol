// ClippingFixerWindow.Setup.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class ClippingFixerWindow {

        private void DrawSetup() {
            using (WkStyles.Section("1. Mesh to fix",
                    "Pick the mesh to scan. It can be PhysBone-driven, clothing, hair, or any mesh that should stay outside itself and the comparison meshes below.")) {
                WkStyles.LabeledField(
                    new GUIContent("Animator",
                        "Avatar Animator used for PhysBone motion warnings and selection prefill."),
                    () => {
                        var next = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true);
                        if (next != _animator) {
                            _animator = next;
                            ClearResults();
                        }
                    });
                WkStyles.LabeledField(
                    new GUIContent("Mesh to check",
                        "The SkinnedMeshRenderer to scan and fix. Pick hair, tail, skirt, sleeves, clothing, accessories, or another mesh that clips."),
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

                if (_targetRenderer != null && (_targetRenderer.sharedMesh == null || !_targetRenderer.sharedMesh.isReadable)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The target mesh is not readable. Enable Read/Write on its model importer before scanning.");
                } else if (_includePhysBoneMotion && !PhysBoneClippingAnalyzer.SdkAvailable) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "PhysBone motion warnings need VRChat SDK 3. Mesh intersection and self-clipping checks can still run.");
                } else if (_animator != null && _targetRenderer != null && !_targetRenderer.transform.IsChildOf(_animator.transform)) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The target mesh is not under the selected Animator. The scan can still run, but PhysBone ownership may not match what you expect.");
                } else if (_includePhysBoneMotion && _animator == null) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "PhysBone motion warnings need an Animator. Mesh intersection and self-clipping checks can still run.");
                }
            }
        }

        private void DrawTuning() {
            using (WkStyles.Section("2. Base / comparison meshes",
                    "Add the body mesh first, then any clothing, accessories, or other readable SkinnedMeshRenderer that the target mesh should not pass through.")) {
                EditorGUILayout.LabelField(
                    "Self-clipping is controlled below; comparison rows are for the body or other meshes the target should stay outside.",
                    WkStyles.Muted);
                DrawComparisonRendererList();
            }

            EditorGUILayout.Space(2);
            using (WkStyles.Section("3. Scan options",
                    "Defaults flag real surface penetration and leave a small gap when fixing.")) {
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log",
                        "Print scan counts and timing to the Console. Useful when checking a large mesh or build-time component."),
                    _verboseLog);
                _showGizmos = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show gizmos in Scene view",
                        "Draw orange markers on clipping vertices or intersecting triangles and a line to the comparison surface."),
                    _showGizmos);
                _checkSelf = EditorGUILayout.ToggleLeft(
                    new GUIContent("Check self-clipping",
                        "Also scan the target mesh for intersecting non-adjacent triangles."),
                    _checkSelf);
                _includePhysBoneMotion = EditorGUILayout.ToggleLeft(
                    new GUIContent("Check PhysBone motion",
                        "Also warn when native VRC PhysBones or supported generated/custom PhysBone sources can move weighted vertices into nearby surfaces."),
                    _includePhysBoneMotion);

                WkStyles.LabeledField(
                    new GUIContent("Inside tolerance",
                        "How far behind the comparison surface a vertex must be before it counts as clipping. 0.001 m is 1 mm."),
                    () => _insideTolerance = EditorGUILayout.Slider(_insideTolerance, 0f, 0.02f));
                WkStyles.LabeledField(
                    new GUIContent("Surface padding",
                        "Surface distance margin used when detecting triangle intersections. 0.005 m is 5 mm."),
                    () => _surfacePadding = EditorGUILayout.Slider(_surfacePadding, 0f, 0.05f));
                using (new EditorGUI.DisabledScope(!_includePhysBoneMotion)) {
                    WkStyles.LabeledField(
                        new GUIContent("PhysBone weight floor",
                            "Minimum skin weight for a vertex to be considered driven by a PhysBone source."),
                        () => _physBoneWeightFloor = EditorGUILayout.Slider(_physBoneWeightFloor, 0.005f, 0.20f));
                    WkStyles.LabeledField(
                        new GUIContent("PhysBone clearance",
                            "Extra clearance a PhysBone-driven vertex should keep from nearby mesh surfaces before it is considered risky."),
                        () => _physBoneClearanceMargin = EditorGUILayout.Slider(_physBoneClearanceMargin, 0.005f, 0.08f));
                    WkStyles.LabeledField(
                        new GUIContent("Warnings per PhysBone",
                            "Caps how many motion warnings one PhysBone source can add to the list."),
                        () => _maxIssuesPerPhysBone = EditorGUILayout.IntSlider(_maxIssuesPerPhysBone, 1, 24));
                    WkStyles.LabeledField(
                        new GUIContent("Pin strength",
                            "Fraction of risky PhysBone-driven skin weight to move onto the nearest stable parent bone when applying a fix."),
                        () => _physBoneMotionPinStrength = EditorGUILayout.Slider(_physBoneMotionPinStrength, 0f, 1f));
                    WkStyles.LabeledField(
                        new GUIContent("Paint radius",
                            "Local paint radius around each motion warning. Nearby vertices weighted to the same PhysBone chain receive a falloff repair."),
                        () => _physBoneMotionBrushRadius = EditorGUILayout.Slider(_physBoneMotionBrushRadius, 0f, 0.12f));
                }
                WkStyles.LabeledField(
                    new GUIContent("Max warning rows",
                        "Caps the visible warning list. Applying a fix still scans without this display cap."),
                    () => _maxWarnings = EditorGUILayout.IntSlider(_maxWarnings, 25, 1000));
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
                EditorGUILayout.LabelField(
                    _checkSelf
                        ? "No comparison meshes. The scan will check the target mesh against itself."
                        : "No comparison meshes. Add a body/comparison mesh or enable self-clipping.",
                    WkStyles.Muted);
            }
            if (unreadableCount > 0) {
                WkStyles.Notice(NoticeKind.Warning,
                    $"{unreadableCount} comparison mesh(es) are not readable and will be skipped. Enable Read/Write on those model imports to include them.");
            }
        }

        private bool CanScan() {
            return _targetRenderer != null &&
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
