// BlendShapeTransferWindow.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class BlendShapeTransferWindow : EditorWindow {

        [SerializeField] private SkinnedMeshRenderer _sourceRenderer;
        [SerializeField] private string _sourceBlendShapeName = "";
        [SerializeField] private string _outputBlendShapeName = "";
        [SerializeField] private BlendShapeTransferTargetMode _targetMode = BlendShapeTransferTargetMode.EntireAvatar;
        [SerializeField] private List<SkinnedMeshRenderer> _manualTargets = new List<SkinnedMeshRenderer>();
        [SerializeField] private float _maxDistance = 0.03f;
        [SerializeField] private float _deltaEpsilon = 0.0001f;
        [SerializeField] private BlendShapeTransferCorrespondenceMode _correspondenceMode = BlendShapeTransferCorrespondenceMode.ClosestPoint;
        [SerializeField] private float _rayFrontalDistance = 0.08f;
        [SerializeField] private float _rayRearDistance = 0.08f;
        [SerializeField] private float _normalAngleLimit = 75f;
        [SerializeField] private bool _rejectBackfaces;

        private BlendShapeTransferCore.PreviewResult _preview;
        private readonly List<string> _applyDetail = new List<string>();
        private string _applySummary = "";
        private Vector2 _pageScroll;
        private Vector2 _resultScroll;
        private string _autoSizeSignature;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#blendshape-transfer";

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<BlendShapeTransferWindow>(false, "BlendShape Transfer", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - BlendShape Transfer");
            w.minSize = new Vector2(680, 560);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            _sourceRenderer = go.GetComponent<SkinnedMeshRenderer>();
            EnsureSourceShapeName();
            ClearResults();
        }

        private void OnGUI() {
            using var _theme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                using (var s = new EditorGUILayout.ScrollViewScope(_pageScroll, false, false)) {
                    _pageScroll = s.scrollPosition;
                    WkStyles.Notice(NoticeKind.Info,
                        "Transfer an existing source blendshape onto nearby body or clothing meshes. Preview first to see which renderers are close enough to process.");
                    DrawSource();
                    EditorGUILayout.Space(2);
                    DrawTargets();
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
                    new GUIContent("BlendShape Transfer",
                        "Propagate an authored blendshape from a source mesh to nearby meshes."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("?", "Open the wiki page for this tool."), EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawSource() {
            using (WkStyles.Section("1. Source", "Renderer and authored blendshape to transfer.")) {
                var prev = _sourceRenderer;
                WkStyles.LabeledField(
                    new GUIContent("Renderer", "SkinnedMeshRenderer that already has the source blendshape."),
                    () => _sourceRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_sourceRenderer, typeof(SkinnedMeshRenderer), true));
                if (prev != _sourceRenderer) {
                    EnsureSourceShapeName();
                    ClearResults();
                }
                DrawBlendShapePicker();
                WkStyles.LabeledField(
                    new GUIContent("Output name", "Name to create on target meshes. Empty uses the source blendshape name."),
                    () => _outputBlendShapeName = EditorGUILayout.TextField(_outputBlendShapeName));
            }
        }

        private void DrawBlendShapePicker() {
            var mesh = _sourceRenderer != null ? _sourceRenderer.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0) {
                EditorGUILayout.LabelField("Pick a source renderer with at least one blendshape.", WkStyles.Muted);
                return;
            }

            var labels = new GUIContent[mesh.blendShapeCount];
            int selected = 0;
            for (int i = 0; i < mesh.blendShapeCount; i++) {
                string name = mesh.GetBlendShapeName(i);
                labels[i] = new GUIContent(name, $"{mesh.GetBlendShapeFrameCount(i)} frame(s)");
                if (name == _sourceBlendShapeName) selected = i;
            }
            WkStyles.LabeledField(
                new GUIContent("Blendshape", "Authored source blendshape to transfer."),
                () => {
                    int next = EditorGUILayout.Popup(selected, labels);
                    _sourceBlendShapeName = mesh.GetBlendShapeName(next);
                    if (string.IsNullOrEmpty(_outputBlendShapeName)) _outputBlendShapeName = _sourceBlendShapeName;
                });
        }

        private void DrawTargets() {
            using (WkStyles.Section("2. Targets", "Process the whole avatar or a manually curated list.")) {
                WkStyles.LabeledField(
                    new GUIContent("Mode", "Entire avatar scans every SkinnedMeshRenderer under the source avatar root."),
                    () => _targetMode = (BlendShapeTransferTargetMode)EditorGUILayout.EnumPopup(_targetMode));
                if (_targetMode == BlendShapeTransferTargetMode.Manual) {
                    int removeAt = -1;
                    for (int i = 0; i < _manualTargets.Count; i++) {
                        using (new EditorGUILayout.HorizontalScope()) {
                            _manualTargets[i] = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_manualTargets[i], typeof(SkinnedMeshRenderer), true);
                            if (GUILayout.Button(new GUIContent("X", "Remove this row."), EditorStyles.miniButton, GUILayout.Width(22))) removeAt = i;
                        }
                    }
                    if (removeAt >= 0) _manualTargets.RemoveAt(removeAt);
                    if (GUILayout.Button(new GUIContent("Add target", "Append a target renderer row."), GUILayout.Width(96))) {
                        _manualTargets.Add(null);
                    }
                } else {
                    int count = BlendShapeTransferCore.ResolveWholeAvatarTargets(_sourceRenderer).Count;
                    EditorGUILayout.LabelField($"{count} renderer(s) under the source avatar root.", WkStyles.Muted);
                }
            }
        }

        private void DrawOptions() {
            using (WkStyles.Section("3. Options", "Distance and correspondence thresholds decide which meshes get processed.")) {
                WkStyles.LabeledField(
                    new GUIContent("Max distance (m)", "Target vertices farther than this from the source surface receive no delta."),
                    () => _maxDistance = EditorGUILayout.Slider(_maxDistance, 0.001f, 0.2f));
                WkStyles.LabeledField(
                    new GUIContent("Delta epsilon", "Source or transferred deltas smaller than this are treated as unchanged."),
                    () => _deltaEpsilon = EditorGUILayout.Slider(_deltaEpsilon, 0.000001f, 0.01f));
                WkStyles.LabeledField(
                    new GUIContent("Correspondence", "Closest point is fastest. Raycast modes are stricter when nearby unrelated surfaces exist."),
                    () => _correspondenceMode = (BlendShapeTransferCorrespondenceMode)EditorGUILayout.EnumPopup(_correspondenceMode));
                if (_correspondenceMode != BlendShapeTransferCorrespondenceMode.ClosestPoint) {
                    WkStyles.LabeledField(
                        new GUIContent("Ray out / in", "Projection envelope around each target vertex."),
                        () => {
                            using (new EditorGUILayout.HorizontalScope()) {
                                _rayFrontalDistance = EditorGUILayout.Slider(_rayFrontalDistance, 0.005f, 0.5f);
                                _rayRearDistance = EditorGUILayout.Slider(_rayRearDistance, 0.005f, 0.5f);
                            }
                        });
                    WkStyles.LabeledField(
                        new GUIContent("Normal angle", "Reject hits beyond this face-normal angle. 0 disables the filter."),
                        () => _normalAngleLimit = EditorGUILayout.Slider(_normalAngleLimit, 0f, 120f));
                    _rejectBackfaces = EditorGUILayout.ToggleLeft(
                        new GUIContent("Reject backfaces", "Ignore hits on the back side of source triangles."),
                        _rejectBackfaces);
                }
            }
        }

        private void DrawActions() {
            bool canPreview = CanPreview();
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canPreview)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Preview",
                                "Dry-run the transfer and list which meshes will be processed or skipped."),
                            GUILayout.MinWidth(130), GUILayout.Height(32))) {
                        RunPreview();
                    }
                }
                using (new EditorGUI.DisabledScope(_preview == null || _preview.ProcessedCount == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Apply",
                                "Create new mesh assets for processed targets and add or replace the transferred blendshape."),
                            GUILayout.Width(100), GUILayout.Height(32))) {
                        RunApply();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear result", "Clear preview and apply results."), GUILayout.Width(96), GUILayout.Height(32))) {
                    ClearResults();
                }
            }
            if (!canPreview) EditorGUILayout.LabelField("Pick a source blendshape and at least one target to enable Preview.", WkStyles.Muted);
        }

        private void DrawResults() {
            if (_preview == null && string.IsNullOrEmpty(_applySummary)) return;
            using (WkStyles.Section("Result", "Preview and apply output.")) {
                if (_preview != null) {
                    EditorGUILayout.LabelField(_preview.Summary, WkStyles.Muted);
                    using (var s = new EditorGUILayout.ScrollViewScope(
                            _resultScroll,
                            GUILayout.Height(WkStyles.CappedListHeight(_preview.Targets.Count, 20f, 100f, 260f)))) {
                        _resultScroll = s.scrollPosition;
                        foreach (var target in _preview.Targets) {
                            string prefix = target.Processed ? "OK  " : "SKIP";
                            string name = target.Renderer != null ? target.Renderer.name : "(missing)";
                            EditorGUILayout.LabelField(
                                $"{prefix} {name} -- {target.Reason}  dist={target.MaxObservedDistance:F4} delta={target.MaxObservedDelta:F4}",
                                WkStyles.Mono);
                        }
                    }
                }
                if (!string.IsNullOrEmpty(_applySummary)) {
                    EditorGUILayout.Space(2);
                    EditorGUILayout.LabelField(_applySummary, WkStyles.Muted);
                    foreach (var line in _applyDetail) EditorGUILayout.LabelField(line, WkStyles.Mono);
                }
            }
        }

        private bool CanPreview() {
            if (_sourceRenderer == null || _sourceRenderer.sharedMesh == null) return false;
            if (string.IsNullOrEmpty(_sourceBlendShapeName)) return false;
            return ResolveTargets().Count > 0;
        }

        private void RunPreview() {
            _applySummary = "";
            _applyDetail.Clear();
            _preview = BlendShapeTransferCore.Preview(BuildOptions());
        }

        private void RunApply() {
            _applySummary = "";
            _applyDetail.Clear();
            var result = BlendShapeTransferCore.Apply(BuildOptions());
            _applySummary = result.Summary;
            _applyDetail.AddRange(result.Detail);
            if (result.CreatedPaths.Count > 0) {
                _applyDetail.Add("");
                _applyDetail.Add("Created meshes:");
                foreach (var path in result.CreatedPaths) _applyDetail.Add("  " + path);
            }
            _preview = null;
        }

        private BlendShapeTransferCore.Options BuildOptions() {
            return new BlendShapeTransferCore.Options {
                SourceRenderer = _sourceRenderer,
                SourceBlendShapeName = _sourceBlendShapeName,
                OutputBlendShapeName = _outputBlendShapeName,
                TargetRenderers = ResolveTargets(),
                MaxDistance = _maxDistance,
                DeltaEpsilon = _deltaEpsilon,
                CorrespondenceMode = _correspondenceMode,
                RayFrontalDistance = _rayFrontalDistance,
                RayRearDistance = _rayRearDistance,
                NormalAngleLimitDegrees = _normalAngleLimit,
                RejectBackfaces = _rejectBackfaces,
            };
        }

        private List<SkinnedMeshRenderer> ResolveTargets() {
            return _targetMode == BlendShapeTransferTargetMode.EntireAvatar
                ? BlendShapeTransferCore.ResolveWholeAvatarTargets(_sourceRenderer)
                : new List<SkinnedMeshRenderer>(_manualTargets);
        }

        private void EnsureSourceShapeName() {
            var mesh = _sourceRenderer != null ? _sourceRenderer.sharedMesh : null;
            if (mesh == null || mesh.blendShapeCount == 0) {
                _sourceBlendShapeName = "";
                return;
            }
            if (mesh.GetBlendShapeIndex(_sourceBlendShapeName) < 0) {
                _sourceBlendShapeName = mesh.GetBlendShapeName(0);
                if (string.IsNullOrEmpty(_outputBlendShapeName)) _outputBlendShapeName = _sourceBlendShapeName;
            }
        }

        private void ClearResults() {
            _preview = null;
            _applySummary = "";
            _applyDetail.Clear();
        }

        private void RequestAutoSize() {
            int previewCount = _preview != null ? _preview.Targets.Count : 0;
            var signature = $"{(_sourceRenderer != null ? _sourceRenderer.GetInstanceID() : 0)}|{_sourceBlendShapeName}|{_targetMode}|{_manualTargets.Count}|{previewCount}|{_applySummary}";
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(680f, 560f),
                new Vector2(840f, 620f + WkStyles.CappedListHeight(previewCount, 20f, 0f, 220f)),
                new Vector2(1040f, 820f));
        }
    }
}
