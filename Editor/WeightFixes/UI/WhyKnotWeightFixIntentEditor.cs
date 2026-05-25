// WhyKnotWeightFixIntentEditor.cs
//
// Inspector for WhyKnotWeightFixIntent. Layers:
//   1. Renderer + on-the-fly dry-run summary (live issue count using the
//      same detector that play-mode and upload use, so the inspector
//      doubles as a fast feedback loop).
//   2. Detection thresholds (folded; defaults are fine in 95% of cases).
//   3. When-to-run + verbose log toggles.
//
// The "Dry-run scan" line refreshes only when something on the
// SerializedObject changed -- no per-frame scans. Cached behind an
// EditorPrefs-keyed bool so it survives domain reload.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Tools;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.Core.Styling;
using WhyKnot.Core.Utilities;

namespace WhyKnot.AvatarQol.WeightFixes.UI {

    [CustomEditor(typeof(WhyKnotWeightFixIntent))]
    [CanEditMultipleObjects]
    internal sealed class WhyKnotWeightFixIntentEditor : Editor {

        private SerializedProperty _targetRenderer;
        private SerializedProperty _weightFloor;
        private SerializedProperty _centerMargin;
        private SerializedProperty _scanCenterBand;
        private SerializedProperty _centerCrossSideFloor;
        private SerializedProperty _processInPlayMode;
        private SerializedProperty _processOnUpload;
        private SerializedProperty _verboseLog;

        private bool _thresholdsOpen;
        private int _cachedIssueCount = -1;
        private string _cachedSummary = "";

        private void OnEnable() {
            _targetRenderer       = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.targetRenderer));
            _weightFloor          = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.weightFloor));
            _centerMargin         = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.centerMargin));
            _scanCenterBand       = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.scanCenterBand));
            _centerCrossSideFloor = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.centerCrossSideFloor));
            _processInPlayMode    = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.processInPlayMode));
            _processOnUpload      = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.processOnUpload));
            _verboseLog           = serializedObject.FindProperty(nameof(WhyKnotWeightFixIntent.verboseLog));
            _thresholdsOpen = SessionState.GetBool("WhyKnot.AvatarQol.WeightFixIntent.ThresholdsOpen", false);
        }

        public override void OnInspectorGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            serializedObject.Update();

            EditorGUILayout.PropertyField(_targetRenderer,
                new GUIContent("Renderer", "The SkinnedMeshRenderer this intent fixes. Defaults to a renderer on this GameObject."));

            EditorGUILayout.Space(2);
            DrawDryRunSummary();

            EditorGUILayout.Space(4);
            _thresholdsOpen = EditorGUILayout.Foldout(_thresholdsOpen,
                new GUIContent("Detection thresholds",
                    "Knobs the same detector uses at play / build time. Tune only when the dry-run summary above misses real issues or flags noise."),
                true, WkStyles.FoldoutHeader);
            SessionState.SetBool("WhyKnot.AvatarQol.WeightFixIntent.ThresholdsOpen", _thresholdsOpen);
            if (_thresholdsOpen) {
                EditorGUILayout.PropertyField(_weightFloor,
                    new GUIContent("Weight floor", "Weights below this fraction are ignored as noise."));
                EditorGUILayout.PropertyField(_centerMargin,
                    new GUIContent("Centre margin (m)", "Half-width of the on-spine centre stripe in Hips local X. 0 disables the stripe so every vertex is Left or Right; raise to ~0.005 only if bind-pose noise around the spine produces false positives."));
                EditorGUILayout.PropertyField(_scanCenterBand,
                    new GUIContent("Scan centre band", "Scan vertices in the centre stripe with the higher centre threshold below. Only meaningful once Centre margin is raised above 0."));
                using (new EditorGUI.DisabledScope(!_scanCenterBand.boolValue)) {
                    EditorGUILayout.PropertyField(_centerCrossSideFloor,
                        new GUIContent("Centre threshold", "Minimum weight a centre-stripe vertex must have to a Left or Right bone before it counts as bleed."));
                }
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("When to run", EditorStyles.boldLabel);
            EditorGUILayout.PropertyField(_processInPlayMode,
                new GUIContent("Process in Play mode", "Apply fixes when entering Play mode. Mesh is cloned in memory; source asset stays untouched."));
            EditorGUILayout.PropertyField(_processOnUpload,
                new GUIContent("Process on Upload", "Apply fixes during avatar Build & Publish. Mesh is cloned in memory; source asset stays untouched."));
            EditorGUILayout.PropertyField(_verboseLog,
                new GUIContent("Verbose log", "Write per-renderer scan stats and per-issue fix actions to the WhyKnot log on each apply."));

            if (serializedObject.ApplyModifiedProperties()) {
                // Invalidate the cached dry-run on any change so the next
                // OnInspectorGUI re-scans.
                _cachedIssueCount = -1;
            }

            EditorGUILayout.Space(6);
            DrawPreviewButtons();
        }

        private void DrawPreviewButtons() {
            var intent = (WhyKnotWeightFixIntent)target;
            if (intent == null) return;
            var animator = intent.GetComponentInParent<Animator>(true);
            var avatarRoot = animator != null ? animator.gameObject : intent.gameObject;
            bool canPreview = animator != null && animator.isHuman;
            bool isPreviewing = AvatarPreviewController.IsPreviewing
                && AvatarPreviewController.SourceAvatar == avatarRoot;

            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canPreview || isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Preview fix",
                                "Clone the avatar in place and apply the weight fix to the clone so you can see the deformation without committing changes."),
                            GUILayout.Height(26))) {
                        StartPreview(avatarRoot, intent);
                    }
                }
                using (new EditorGUI.DisabledScope(!isPreviewing)) {
                    if (GUILayout.Button(
                            new GUIContent("Stop preview",
                                "Destroy the preview clone and un-hide the source avatar."),
                            GUILayout.Height(26), GUILayout.Width(110))) {
                        AvatarPreviewController.StopPreview();
                    }
                }
            }
        }

        private static void StartPreview(GameObject avatarRoot, WhyKnotWeightFixIntent intent) {
            if (avatarRoot == null || intent == null) return;
            // Walk the intent's hierarchy path under the source avatar so the
            // preview runner can find the equivalent intent on the clone.
            var intentRelativePath = TransformPath(intent.transform, avatarRoot.transform);

            AvatarPreviewController.StartPreview(avatarRoot, (cloneRoot, session) => {
                var cloneAnimator = cloneRoot.GetComponentInChildren<Animator>(true);
                if (cloneAnimator == null || !cloneAnimator.isHuman) return;
                var cloneIntent = ResolveCloneIntent(cloneRoot, intentRelativePath);
                if (cloneIntent == null) return;
                WeightFixApplyHook.RunIntentsWith(
                    new List<WhyKnotWeightFixIntent> { cloneIntent },
                    cloneAnimator,
                    capture: r => SessionAdapter.Capture(session, r),
                    cloneAndTrack: m => SessionAdapter.CloneAndTrack(session, m),
                    contextLabel: $"preview ({avatarRoot.name})");
            });
        }

        private static WhyKnotWeightFixIntent ResolveCloneIntent(GameObject cloneRoot, string relativePath) {
            var t = string.IsNullOrEmpty(relativePath)
                ? cloneRoot.transform
                : cloneRoot.transform.Find(relativePath);
            return t != null ? t.GetComponent<WhyKnotWeightFixIntent>() : null;
        }

        private static string TransformPath(Transform t, Transform stopAt) {
            if (t == null || stopAt == null) return string.Empty;
            var parts = new List<string>();
            while (t != null && t != stopAt) {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        private static class SessionAdapter {
            public static void Capture(AvatarIntentSession session, SkinnedMeshRenderer renderer) {
                session.Capture(renderer);
            }
            public static Mesh CloneAndTrack(AvatarIntentSession session, Mesh source) {
                if (source == null) return null;
                var clone = Object.Instantiate(source);
                clone.name = source.name + " (WeightFixed)";
                clone.hideFlags = HideFlags.DontSave;
                session.Adopt(clone);
                return clone;
            }
        }

        private void DrawDryRunSummary() {
            var intent = (WhyKnotWeightFixIntent)target;
            if (intent == null) return;
            var renderer = intent.targetRenderer != null ? intent.targetRenderer : intent.GetComponent<SkinnedMeshRenderer>();
            if (renderer == null) {
                EditorGUILayout.HelpBox("Assign a Renderer above. The fix will run against that renderer at play / build.", MessageType.Info);
                return;
            }
            var animator = intent.GetComponentInParent<Animator>(true);
            if (animator == null || !animator.isHuman) {
                EditorGUILayout.HelpBox("No Humanoid Animator found above this component. Cross-side detection needs Humanoid bone bindings.", MessageType.Warning);
                return;
            }

            if (_cachedIssueCount < 0) {
                var sideMap = new HumanoidSideMap(animator);
                if (!sideMap.IsValid) {
                    _cachedIssueCount = 0;
                    _cachedSummary = "Animator has no Hips binding; cannot scan.";
                } else {
                    var p = new ScanParameters {
                        WeightFloor = intent.weightFloor,
                        CenterMargin = intent.centerMargin,
                        ScanCenterBand = intent.scanCenterBand,
                        CenterCrossSideFloor = intent.centerCrossSideFloor,
                    };
                    var result = WeightCrossSideDetector.Detect(renderer, sideMap, p, log: null);
                    _cachedIssueCount = result.Issues.Count;
                    if (result.MeshUnreadable) {
                        _cachedSummary = "Mesh is not Read/Write enabled. Enable in the model importer to allow scan.";
                    } else if (result.NoBones) {
                        _cachedSummary = "Renderer has no bones; nothing to fix.";
                    } else if (result.EarlyExitNoCrossSide) {
                        _cachedSummary = "Renderer's bones don't span both Left and Right; no cross-side mismatch possible.";
                    } else {
                        _cachedSummary = _cachedIssueCount == 0
                            ? $"Dry-run: 0 cross-side weights at current thresholds (scanned {result.VerticesScanned} verts)."
                            : $"Dry-run: {_cachedIssueCount} cross-side weight(s) will be fixed at play / build.";
                    }
                }
            }
            var kind = _cachedIssueCount > 0 ? MessageType.Info : MessageType.None;
            if (kind == MessageType.None) {
                EditorGUILayout.LabelField(_cachedSummary, WkStyles.Muted);
            } else {
                EditorGUILayout.HelpBox(_cachedSummary, kind);
            }
        }
    }
}
