// OrphanedBoneWeightCleanerWindow.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class OrphanedBoneWeightCleanerWindow : EditorWindow {

        [SerializeField] private Animator _animator;
        [SerializeField] private SkinnedMeshRenderer _renderer;
        [SerializeField] private bool _wholeAvatar = true;
        [SerializeField] private OrphanedBoneCleanupMode _mode = OrphanedBoneCleanupMode.DropInvalidWeights;
        [SerializeField] private bool _growDeletion;
        [SerializeField] private List<Transform> _removedBones = new List<Transform>();

        private readonly List<string> _resultDetail = new List<string>();
        private string _resultSummary = "";
        private Vector2 _pageScroll;
        private Vector2 _resultScroll;
        private string _autoSizeSignature;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#orphaned-bone-weight-cleaner";

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<OrphanedBoneWeightCleanerWindow>(false, "Orphaned Bone Weight Cleaner", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Orphaned Bone Weight Cleaner");
            w.minSize = new Vector2(620, 500);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            _renderer = go.GetComponent<SkinnedMeshRenderer>() ?? go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            _animator = go.GetComponent<Animator>() ?? go.GetComponentInParent<Animator>(true) ?? go.GetComponentInChildren<Animator>(true);
            if (_renderer != null && _animator == null) _animator = _renderer.GetComponentInParent<Animator>(true);
            _wholeAvatar = _animator != null;
            ClearResult();
        }

        private void OnGUI() {
            using var _theme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                using (var s = new EditorGUILayout.ScrollViewScope(_pageScroll, false, false)) {
                    _pageScroll = s.scrollPosition;
                    WkStyles.Notice(NoticeKind.Info,
                        "Cleans mesh weights that point at missing, null, or explicitly removed bones. The default keeps vertices with valid remaining weights and renormalizes them.");
                    DrawTargetSection();
                    EditorGUILayout.Space(2);
                    DrawOptions();
                    EditorGUILayout.Space(2);
                    DrawActions();
                    EditorGUILayout.Space(2);
                    DrawResults();
                }
                WkStyles.WindowFooter();
            }
            RequestAutoSize();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Orphaned Bone Weight Cleaner",
                        "Drop invalid bone-weight slots and remove any geometry left with no valid weights."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("?", "Open the wiki page for this tool."), EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawTargetSection() {
            using (WkStyles.Section("1. Target", "Pick one renderer, or clean every SkinnedMeshRenderer under an avatar Animator.")) {
                _wholeAvatar = EditorGUILayout.ToggleLeft(
                    new GUIContent("Clean every SkinnedMeshRenderer under the Animator",
                        "When off, only the single renderer field is processed."),
                    _wholeAvatar);
                using (new EditorGUI.DisabledScope(!_wholeAvatar)) {
                    WkStyles.LabeledField(
                        new GUIContent("Animator", "Avatar root Animator."),
                        () => _animator = (Animator)EditorGUILayout.ObjectField(_animator, typeof(Animator), true));
                }
                using (new EditorGUI.DisabledScope(_wholeAvatar)) {
                    WkStyles.LabeledField(
                        new GUIContent("Renderer", "Single SkinnedMeshRenderer to clean."),
                        () => _renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_renderer, typeof(SkinnedMeshRenderer), true));
                }
            }
        }

        private void DrawOptions() {
            using (WkStyles.Section("2. Cleanup", "Invalid influences are weights pointing at out-of-range, null, or listed bones.")) {
                WkStyles.LabeledField(
                    new GUIContent("Mode", "Default keeps geometry when a valid weight remains. Delete mode matches the older aggressive cleaner."),
                    () => _mode = (OrphanedBoneCleanupMode)EditorGUILayout.EnumPopup(_mode));
                _growDeletion = EditorGUILayout.ToggleLeft(
                    new GUIContent("Grow deletion across connected triangles",
                        "Aggressive cleanup. Any triangle touching a deleted vertex pulls its connected island into the deletion set."),
                    _growDeletion);

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField(
                    new GUIContent("Treat these bones as removed",
                        "Optional. Weights pointing at these bones are cleaned even if the Transform still exists."),
                    WkStyles.SubsectionTitle);
                int removeAt = -1;
                for (int i = 0; i < _removedBones.Count; i++) {
                    using (new EditorGUILayout.HorizontalScope()) {
                        _removedBones[i] = (Transform)EditorGUILayout.ObjectField(_removedBones[i], typeof(Transform), true);
                        if (GUILayout.Button(new GUIContent("X", "Remove this row."), EditorStyles.miniButton, GUILayout.Width(22))) removeAt = i;
                    }
                }
                if (removeAt >= 0) _removedBones.RemoveAt(removeAt);
                if (GUILayout.Button(new GUIContent("Add bone", "Add a bone to treat as removed."), GUILayout.Width(90))) {
                    _removedBones.Add(null);
                }
            }
        }

        private void DrawActions() {
            bool canApply = _wholeAvatar ? _animator != null : _renderer != null;
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canApply)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Clean weights",
                                "Create cleaned mesh assets and assign them to affected renderers. Wrapped in one Undo step."),
                            GUILayout.MinWidth(160), GUILayout.Height(32))) {
                        RunApply();
                    }
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(_resultSummary) && _resultDetail.Count == 0)) {
                    if (GUILayout.Button(new GUIContent("Clear result", "Clear the last run summary."), GUILayout.Width(96), GUILayout.Height(32))) {
                        ClearResult();
                    }
                }
            }
            if (!canApply) EditorGUILayout.LabelField("Pick an Animator or renderer to enable cleanup.", WkStyles.Muted);
        }

        private void DrawResults() {
            if (string.IsNullOrEmpty(_resultSummary) && _resultDetail.Count == 0) return;
            using (WkStyles.Section("Result", "Last cleanup run.")) {
                if (!string.IsNullOrEmpty(_resultSummary)) EditorGUILayout.LabelField(_resultSummary, WkStyles.Muted);
                if (_resultDetail.Count == 0) return;
                using (var s = new EditorGUILayout.ScrollViewScope(
                        _resultScroll,
                        GUILayout.Height(WkStyles.CappedListHeight(_resultDetail.Count, 16f, 80f, 220f)))) {
                    _resultScroll = s.scrollPosition;
                    foreach (var line in _resultDetail) EditorGUILayout.LabelField(line, WkStyles.Mono);
                }
            }
        }

        private void RunApply() {
            ClearResult();
            var result = OrphanedBoneWeightCleanerCore.Apply(
                _animator,
                _renderer,
                _wholeAvatar,
                _removedBones,
                _mode,
                _growDeletion);
            _resultSummary = result.Summary;
            _resultDetail.AddRange(result.Detail);
            if (result.ClonedPaths.Count > 0) {
                _resultDetail.Add("");
                _resultDetail.Add("Created meshes:");
                foreach (var path in result.ClonedPaths) _resultDetail.Add("  " + path);
            }
        }

        private void ClearResult() {
            _resultSummary = "";
            _resultDetail.Clear();
        }

        private void RequestAutoSize() {
            var signature = $"{_wholeAvatar}|{(_animator != null ? _animator.GetInstanceID() : 0)}|{(_renderer != null ? _renderer.GetInstanceID() : 0)}|{_removedBones.Count}|{_resultDetail.Count}|{_resultSummary}";
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(620f, 500f),
                new Vector2(760f, 560f + WkStyles.CappedListHeight(_resultDetail.Count, 16f, 0f, 180f)),
                new Vector2(920f, 760f));
        }
    }
}
