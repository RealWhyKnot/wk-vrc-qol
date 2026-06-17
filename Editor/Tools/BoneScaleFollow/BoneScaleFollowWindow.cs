// BoneScaleFollowWindow.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class BoneScaleFollowWindow : EditorWindow {

        [SerializeField] private SkinnedMeshRenderer _sourceRenderer;
        [SerializeField] private string _outputBlendShapeName = BoneScaleFollowCore.DefaultBlendShapeName;
        [SerializeField] private List<BoneScaleFollowRow> _boneRows = new List<BoneScaleFollowRow>();
        [SerializeField] private BoneScaleFollowTargetMode _targetMode = BoneScaleFollowTargetMode.EntireAvatar;
        [SerializeField] private List<SkinnedMeshRenderer> _manualTargets = new List<SkinnedMeshRenderer>();
        [SerializeField] private float _maxDistance = 0.03f;
        [SerializeField] private float _deltaEpsilon = 0.0001f;
        [SerializeField] private BlendShapeTransferCorrespondenceMode _correspondenceMode = BlendShapeTransferCorrespondenceMode.ClosestPoint;
        [SerializeField] private float _rayFrontalDistance = 0.08f;
        [SerializeField] private float _rayRearDistance = 0.08f;
        [SerializeField] private float _normalAngleLimit = 75f;
        [SerializeField] private bool _rejectBackfaces;
        [SerializeField] private bool _ownResponseCompensation = true;
        [SerializeField] private bool _useDistanceFalloff;
        [SerializeField] private float _falloffStartDistance = 0.02f;

        private BoneScaleFollowCore.PreviewResult _preview;
        private readonly HashSet<int> _includedRendererIds = new HashSet<int>();
        private readonly List<string> _applyDetail = new List<string>();
        private string _applySummary = "";
        private Vector2 _pageScroll;
        private Vector2 _resultScroll;
        private string _autoSizeSignature;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#bone-scale-follow";

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<BoneScaleFollowWindow>(false, "Bone Scale Follow", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Bone Scale Follow");
            w.minSize = new Vector2(720, 620);
            w.EnsureAtLeastOneRow();
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            _sourceRenderer = go.GetComponent<SkinnedMeshRenderer>();
            ClearResults();
        }

        private void OnGUI() {
            EnsureAtLeastOneRow();
            using var _theme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                using (var s = new EditorGUILayout.ScrollViewScope(_pageScroll, false, false)) {
                    _pageScroll = s.scrollPosition;
                    WkStyles.Notice(NoticeKind.Info,
                        "Generate a target-mesh blendshape from a source bone scale, without changing target bones or PhysBone components.");
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
                    new GUIContent("Bone Scale Follow",
                        "Generate blendshape deltas for nearby meshes from a source bone-scale deformation."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("?", "Open the wiki page for this tool."), EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawSource() {
            using (WkStyles.Section("1. Source", "Renderer, source bone scale rows, and output blendshape name.")) {
                var prev = _sourceRenderer;
                WkStyles.LabeledField(
                    new GUIContent("Renderer", "SkinnedMeshRenderer whose bone-scale deformation should be sampled."),
                    () => _sourceRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_sourceRenderer, typeof(SkinnedMeshRenderer), true));
                if (prev != _sourceRenderer) ClearResults();

                WkStyles.LabeledField(
                    new GUIContent("Output name", "Blendshape name to add or replace on target meshes."),
                    () => _outputBlendShapeName = EditorGUILayout.TextField(_outputBlendShapeName));
                DrawBoneRows();
            }
        }

        private void DrawBoneRows() {
            int removeAt = -1;
            EditorGUILayout.LabelField("Bone scale rows", WkStyles.SubsectionTitle);
            for (int i = 0; i < _boneRows.Count; i++) {
                var row = _boneRows[i];
                if (row == null) {
                    row = new BoneScaleFollowRow();
                    _boneRows[i] = row;
                }
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox)) {
                    using (new EditorGUILayout.HorizontalScope()) {
                        row.Enabled = EditorGUILayout.Toggle(row.Enabled, GUILayout.Width(18));
                        EditorGUILayout.LabelField($"Row {i + 1}", WkStyles.SubsectionTitle, GUILayout.Width(54));
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(new GUIContent("X", "Remove this row."), EditorStyles.miniButton, GUILayout.Width(22))) removeAt = i;
                    }
                    WkStyles.LabeledField(
                        new GUIContent("Bone", "Bone whose local scale should be sampled."),
                        () => row.Bone = (Transform)EditorGUILayout.ObjectField(row.Bone, typeof(Transform), true));
                    WkStyles.LabeledField(
                        new GUIContent("Base scale", "Resting local scale for the source bone."),
                        () => row.BaseScale = EditorGUILayout.Vector3Field(GUIContent.none, row.BaseScale));
                    WkStyles.LabeledField(
                        new GUIContent("Target scale", "Scaled local scale to turn into a follow blendshape."),
                        () => row.TargetScale = EditorGUILayout.Vector3Field(GUIContent.none, row.TargetScale));
                    using (new EditorGUILayout.HorizontalScope()) {
                        GUILayout.Space(EditorGUIUtility.labelWidth);
                        using (new EditorGUI.DisabledScope(row.Bone == null)) {
                            if (GUILayout.Button(new GUIContent("Base <- Current", "Copy this bone's current localScale into Base scale."), EditorStyles.miniButton)) {
                                row.BaseScale = row.Bone.localScale;
                            }
                            if (GUILayout.Button(new GUIContent("Target <- Current", "Copy this bone's current localScale into Target scale."), EditorStyles.miniButton)) {
                                row.TargetScale = row.Bone.localScale;
                            }
                        }
                        if (GUILayout.Button(new GUIContent("Target = Base", "Reset Target scale to match Base scale."), EditorStyles.miniButton)) {
                            row.TargetScale = row.BaseScale;
                        }
                    }
                }
            }
            if (removeAt >= 0 && _boneRows.Count > 1) _boneRows.RemoveAt(removeAt);
            if (GUILayout.Button(new GUIContent("Add bone row", "Append another bone scale row."), GUILayout.Width(110))) {
                _boneRows.Add(new BoneScaleFollowRow());
                ClearResults();
            }
        }

        private void DrawTargets() {
            using (WkStyles.Section("2. Targets", "Process the whole avatar or a manually curated list.")) {
                WkStyles.LabeledField(
                    new GUIContent("Mode", "Entire avatar scans every SkinnedMeshRenderer under the source avatar root."),
                    () => _targetMode = (BoneScaleFollowTargetMode)EditorGUILayout.EnumPopup(_targetMode));
                if (_targetMode == BoneScaleFollowTargetMode.Manual) {
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
                    int count = BoneScaleFollowCore.ResolveWholeAvatarTargets(_sourceRenderer).Count;
                    EditorGUILayout.LabelField($"{count} renderer(s) under the source avatar root.", WkStyles.Muted);
                }
            }
        }

        private void DrawOptions() {
            using (WkStyles.Section("3. Options", "Distance, correspondence, and compensation controls.")) {
                WkStyles.LabeledField(
                    new GUIContent("Max distance (m)", "Target vertices farther than this from the source surface receive no delta."),
                    () => _maxDistance = EditorGUILayout.Slider(_maxDistance, 0.001f, 0.2f));
                WkStyles.LabeledField(
                    new GUIContent("Delta epsilon", "Deltas smaller than this are treated as unchanged."),
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
                _ownResponseCompensation = EditorGUILayout.ToggleLeft(
                    new GUIContent("Own-response compensation", "Subtract each target vertex's own response to the same bone scale so partial bone weights do not double-move."),
                    _ownResponseCompensation);
                _useDistanceFalloff = EditorGUILayout.ToggleLeft(
                    new GUIContent("Distance falloff", "Fade transferred deltas down near Max distance instead of cutting off sharply."),
                    _useDistanceFalloff);
                if (_useDistanceFalloff) {
                    WkStyles.LabeledField(
                        new GUIContent("Falloff start (m)", "Distance where fade-out begins. Vertices closer than this receive full delta."),
                        () => _falloffStartDistance = EditorGUILayout.Slider(_falloffStartDistance, 0f, _maxDistance));
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
                using (new EditorGUI.DisabledScope(_preview == null || IncludedProcessedCount() == 0)) {
                    if (GUILayout.Button(
                            new GUIContent("Apply",
                                "Create new mesh assets for checked targets and add or replace the generated blendshape."),
                            GUILayout.Width(100), GUILayout.Height(32))) {
                        RunApply();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear result", "Clear preview and apply results."), GUILayout.Width(96), GUILayout.Height(32))) {
                    ClearResults();
                }
            }
            if (!canPreview) EditorGUILayout.LabelField("Pick a source renderer, enabled bone row, and at least one target to enable Preview.", WkStyles.Muted);
        }

        private void DrawResults() {
            if (_preview == null && string.IsNullOrEmpty(_applySummary)) return;
            using (WkStyles.Section("Result", "Preview and apply output.")) {
                if (_preview != null) {
                    EditorGUILayout.LabelField(_preview.Summary, WkStyles.Muted);
                    using (new EditorGUILayout.HorizontalScope()) {
                        if (GUILayout.Button(new GUIContent("Check processed", "Include every processed target in Apply."), EditorStyles.miniButton, GUILayout.Width(112))) {
                            foreach (var target in _preview.Targets) {
                                if (target.Processed && target.Renderer != null) _includedRendererIds.Add(target.Renderer.GetInstanceID());
                            }
                        }
                        if (GUILayout.Button(new GUIContent("Uncheck all", "Exclude every processed target from Apply."), EditorStyles.miniButton, GUILayout.Width(86))) {
                            _includedRendererIds.Clear();
                        }
                    }
                    using (var s = new EditorGUILayout.ScrollViewScope(
                            _resultScroll,
                            GUILayout.Height(WkStyles.CappedListHeight(_preview.Targets.Count, 22f, 100f, 300f)))) {
                        _resultScroll = s.scrollPosition;
                        foreach (var target in _preview.Targets) {
                            DrawPreviewRow(target);
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

        private void DrawPreviewRow(SurfaceDeltaTargetResult target) {
            string prefix = target.Processed ? "OK  " : "SKIP";
            string name = target.Renderer != null ? target.Renderer.name : "(missing)";
            if (target.Processed && target.Renderer != null) {
                int id = target.Renderer.GetInstanceID();
                bool included = _includedRendererIds.Contains(id);
                using (new EditorGUILayout.HorizontalScope()) {
                    bool next = EditorGUILayout.Toggle(included, GUILayout.Width(18));
                    if (next) _includedRendererIds.Add(id); else _includedRendererIds.Remove(id);
                    EditorGUILayout.LabelField(
                        $"{prefix} {name} -- {target.Reason}  dist={target.MaxObservedDistance:F4} delta={target.MaxObservedDelta:F4}",
                        WkStyles.Mono);
                }
            } else {
                EditorGUILayout.LabelField(
                    $"{prefix} {name} -- {target.Reason}  dist={target.MaxObservedDistance:F4} delta={target.MaxObservedDelta:F4}",
                    WkStyles.Mono);
            }
        }

        private bool CanPreview() {
            if (_sourceRenderer == null || _sourceRenderer.sharedMesh == null) return false;
            if (EnabledRowCount() == 0) return false;
            return ResolveTargets().Count > 0;
        }

        private void RunPreview() {
            _applySummary = "";
            _applyDetail.Clear();
            _preview = BoneScaleFollowCore.Preview(BuildOptions(includeSelection: false));
            _includedRendererIds.Clear();
            if (_preview != null) {
                foreach (var target in _preview.Targets) {
                    if (target.Processed && target.Renderer != null) {
                        _includedRendererIds.Add(target.Renderer.GetInstanceID());
                    }
                }
            }
        }

        private void RunApply() {
            _applySummary = "";
            _applyDetail.Clear();
            var result = BoneScaleFollowCore.Apply(BuildOptions(includeSelection: true));
            _applySummary = result.Summary;
            _applyDetail.AddRange(result.Detail);
            if (result.CreatedPaths.Count > 0) {
                _applyDetail.Add("");
                _applyDetail.Add("Created meshes:");
                foreach (var path in result.CreatedPaths) _applyDetail.Add("  " + path);
            }
            _preview = null;
        }

        private BoneScaleFollowCore.Options BuildOptions(bool includeSelection) {
            return new BoneScaleFollowCore.Options {
                SourceRenderer = _sourceRenderer,
                OutputBlendShapeName = _outputBlendShapeName,
                BoneRows = _boneRows,
                TargetRenderers = ResolveTargets(),
                IncludedRendererInstanceIds = includeSelection ? new HashSet<int>(_includedRendererIds) : null,
                MaxDistance = _maxDistance,
                DeltaEpsilon = _deltaEpsilon,
                CorrespondenceMode = _correspondenceMode,
                RayFrontalDistance = _rayFrontalDistance,
                RayRearDistance = _rayRearDistance,
                NormalAngleLimitDegrees = _normalAngleLimit,
                RejectBackfaces = _rejectBackfaces,
                OwnResponseCompensation = _ownResponseCompensation,
                UseDistanceFalloff = _useDistanceFalloff,
                FalloffStartDistance = _falloffStartDistance,
            };
        }

        private List<SkinnedMeshRenderer> ResolveTargets() {
            return _targetMode == BoneScaleFollowTargetMode.EntireAvatar
                ? BoneScaleFollowCore.ResolveWholeAvatarTargets(_sourceRenderer)
                : new List<SkinnedMeshRenderer>(_manualTargets);
        }

        private int EnabledRowCount() {
            int count = 0;
            for (int i = 0; i < _boneRows.Count; i++) {
                if (_boneRows[i] != null && _boneRows[i].Enabled) count++;
            }
            return count;
        }

        private int IncludedProcessedCount() {
            if (_preview == null) return 0;
            int count = 0;
            foreach (var target in _preview.Targets) {
                if (target.Processed && target.Renderer != null
                        && _includedRendererIds.Contains(target.Renderer.GetInstanceID())) {
                    count++;
                }
            }
            return count;
        }

        private void EnsureAtLeastOneRow() {
            if (_boneRows == null) _boneRows = new List<BoneScaleFollowRow>();
            if (_boneRows.Count == 0) _boneRows.Add(new BoneScaleFollowRow());
        }

        private void ClearResults() {
            _preview = null;
            _includedRendererIds.Clear();
            _applySummary = "";
            _applyDetail.Clear();
        }

        private void RequestAutoSize() {
            int previewCount = _preview != null ? _preview.Targets.Count : 0;
            var signature = $"{(_sourceRenderer != null ? _sourceRenderer.GetInstanceID() : 0)}|{_targetMode}|{_boneRows.Count}|{previewCount}|{_applySummary}";
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(720f, 620f),
                new Vector2(920f, 700f + WkStyles.CappedListHeight(previewCount, 22f, 0f, 260f)),
                new Vector2(1120f, 900f));
        }
    }
}
