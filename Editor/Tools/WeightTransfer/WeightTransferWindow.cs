// WeightTransferWindow.cs
//
// First weight-workbench workflow: source SkinnedMeshRenderer to target
// SkinnedMeshRenderer bone-weight transfer using surface correspondence,
// confidence rejection, and topology inpainting.

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Weighting;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class WeightTransferWindow : WkToolWindow {

        [SerializeField] private SkinnedMeshRenderer _source;
        [SerializeField] private SkinnedMeshRenderer _target;
        [SerializeField] private Transform _spaceRoot;
        [SerializeField] private WeightTransferMode _mode = WeightTransferMode.HybridSurface;
        [SerializeField] private int _sourceSubmesh = -1;
        [SerializeField] private float _maxClosestDistance = 0.06f;
        [SerializeField] private float _maxProjectionDistance = 0.12f;
        [SerializeField] private float _normalAngle = 35f;
        [SerializeField] private bool _allowFlippedNormals;
        [SerializeField] private bool _inpaint = true;
        [SerializeField] private int _inpaintIterations = 48;
        [SerializeField] private int _maxInfluences = 4;
        [SerializeField] private float _pruneThreshold = 0.001f;
        [SerializeField] private bool _showNotes = true;

        private WeightTransferResult _lastResult;
        private string _status = "";

        protected override string Title => "Weight Transfer";
        protected override Vector2 InitialMinSize => new Vector2(540, 620);

        internal static void Open(SkinnedMeshRenderer prefillTarget) {
            var w = GetWindow<WeightTransferWindow>(false, "Weight Transfer", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Weight Transfer");
            if (prefillTarget != null) w._target = prefillTarget;
            w.Show();
            w.Focus();
        }

        protected override void OnBodyGUI() {
            DrawStatus();
            DrawRendererSection();
            EditorGUILayout.Space(2);
            DrawMappingSection();
            EditorGUILayout.Space(2);
            DrawFinalizeSection();
            EditorGUILayout.Space(2);
            DrawRunSection();
            EditorGUILayout.Space(2);
            DrawResultSection();
        }

        private void DrawStatus() {
            if (_lastResult != null && _lastResult.Weights != null) {
                WkStyles.StatusBanner("PREVIEW READY  -  inspect counts, then apply to a generated mesh", NoticeKind.Success, height: 24);
            } else if (CanRun()) {
                WkStyles.StatusBanner("READY  -  run preview or apply", NoticeKind.Info, height: 24);
            } else {
                WkStyles.StatusBanner("WAITING  -  pick readable source and target renderers", NoticeKind.Warning, height: 24);
            }
            if (!string.IsNullOrEmpty(_status)) EditorGUILayout.LabelField(_status, WkStyles.Muted);
        }

        private void DrawRendererSection() {
            using (WkStyles.Section("1. Renderers",
                    "Transfer source mesh bone weights onto the target renderer. The target mesh is cloned to Assets/AvatarQol Generated on apply.")) {
                var prevSource = _source;
                var prevTarget = _target;
                _source = WkStyles.ObjectFieldRow(
                    new GUIContent("Source", "Renderer whose existing bone weights are sampled."),
                    _source,
                    allowSceneObjects: true);
                _target = WkStyles.ObjectFieldRow(
                    new GUIContent("Target", "Renderer that receives transferred bone weights."),
                    _target,
                    allowSceneObjects: true);
                _spaceRoot = WkStyles.ObjectFieldRow(
                    new GUIContent("Space root", "Optional common avatar root. Leave empty to compare in world space."),
                    _spaceRoot,
                    allowSceneObjects: true);
                if (_source != prevSource || _target != prevTarget) {
                    _lastResult = null;
                    _sourceSubmesh = -1;
                }

                DrawRendererNotice("Source", _source);
                DrawRendererNotice("Target", _target);
                DrawSourceSubmeshPicker();
            }
        }

        private void DrawMappingSection() {
            using (WkStyles.Section("2. Correspondence",
                    "How each target vertex finds source surface weights.")) {
                WkStyles.LabeledField(
                    new GUIContent("Mode", "Hybrid uses projected surface first, then nearest surface as fallback. Exact Topology copies by vertex index."),
                    () => _mode = (WeightTransferMode)EditorGUILayout.EnumPopup(_mode));

                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Closest max", "Maximum nearest-surface fallback distance in metres."), GUILayout.Width(WkStyles.LabelColumn));
                    _maxClosestDistance = EditorGUILayout.Slider(_maxClosestDistance, 0.001f, 0.5f);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Projected max", "Projected ray envelope in metres. Used by Hybrid and Projected modes."), GUILayout.Width(WkStyles.LabelColumn));
                    _maxProjectionDistance = EditorGUILayout.Slider(_maxProjectionDistance, 0.001f, 0.75f);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Normal angle", "Reject matches whose source face normal differs by more than this angle."), GUILayout.Width(WkStyles.LabelColumn));
                    _normalAngle = EditorGUILayout.Slider(_normalAngle, 0f, 120f);
                }
                _allowFlippedNormals = EditorGUILayout.ToggleLeft(
                    new GUIContent("Allow flipped normals", "Use absolute normal agreement for inside-out or layered meshes. Leave off for normal body-to-clothing transfer."),
                    _allowFlippedNormals);
                _inpaint = EditorGUILayout.ToggleLeft(
                    new GUIContent("Inpaint rejected vertices", "Fill unmatched target vertices by diffusing nearby accepted weights over target topology."),
                    _inpaint);
                using (new EditorGUI.DisabledScope(!_inpaint)) {
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField(new GUIContent("Inpaint passes", "Smoothing passes after unmatched vertices are seeded from accepted neighbors."), GUILayout.Width(WkStyles.LabelColumn));
                        _inpaintIterations = EditorGUILayout.IntSlider(_inpaintIterations, 0, 128);
                    }
                }
            }
        }

        private void DrawFinalizeSection() {
            using (WkStyles.Section("3. Final weights",
                    "Output cleanup before writing to the target mesh.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Max influences", "Maximum bones stored per vertex after transfer."), GUILayout.Width(WkStyles.LabelColumn));
                    _maxInfluences = EditorGUILayout.IntSlider(_maxInfluences, 1, 8);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Prune below", "Weights at or below this value are removed before final normalization."), GUILayout.Width(WkStyles.LabelColumn));
                    _pruneThreshold = EditorGUILayout.Slider(_pruneThreshold, 0f, 0.02f);
                }
            }
        }

        private void DrawRunSection() {
            using (WkStyles.Section("4. Run",
                    "Preview computes the transfer without touching the target mesh. Apply writes the same result to a generated mesh clone.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    using (new EditorGUI.DisabledScope(!CanRun())) {
                        if (GUILayout.Button(
                                new GUIContent("Preview",
                                    "Compute transfer diagnostics without changing renderer.sharedMesh."),
                                GUILayout.Height(32), GUILayout.Width(110))) {
                            RunPreview();
                        }
                        if (WkStyles.PrimaryButtonInline(
                                new GUIContent("Apply to Generated Mesh",
                                    "Run transfer and write weights to a generated mesh clone assigned to the target renderer."),
                                GUILayout.Height(32), GUILayout.MinWidth(190))) {
                            ApplyTransfer();
                        }
                    }
                }
                if (!CanRun()) {
                    EditorGUILayout.LabelField("Pick readable source and target meshes with bones to enable transfer.", WkStyles.Muted);
                }
            }
        }

        private void DrawResultSection() {
            using (WkStyles.Section("5. Result",
                    "Counts from the last preview or apply run.")) {
                if (_lastResult == null) {
                    EditorGUILayout.LabelField("(no preview yet)", EditorStyles.centeredGreyMiniLabel);
                } else {
                    EditorGUILayout.LabelField(new GUIContent("Accepted", "Vertices copied from a confident source surface match."), new GUIContent(_lastResult.AcceptedCount.ToString("N0")));
                    EditorGUILayout.LabelField(new GUIContent("Rejected", "Vertices without a confident direct match before inpainting."), new GUIContent(_lastResult.RejectedCount.ToString("N0")));
                    EditorGUILayout.LabelField(new GUIContent("Inpainted", "Rejected vertices filled from nearby accepted topology."), new GUIContent(_lastResult.InpaintedCount.ToString("N0")));
                    EditorGUILayout.LabelField(new GUIContent("Preserved", "Vertices that kept existing target weights because no inpaint anchor was available."), new GUIContent(_lastResult.UnresolvedCount.ToString("N0")));
                    if (_lastResult.BoneMap != null) {
                        EditorGUILayout.LabelField(new GUIContent("Bones", "Source-to-target bone mapping summary."), new GUIContent(_lastResult.BoneMap.Summary()));
                    }
                }

                _showNotes = WkStyles.FoldoutHeaderRow("Notes", _showNotes,
                    "Current Weight Transfer scope and safety behavior.");
                if (_showNotes) {
                    WkStyles.Notice(NoticeKind.Info,
                        "This first pass transfers weights only. It does not change PhysBone settings, add target bones, or mutate imported model meshes in place.");
                }
            }
        }

        private void DrawSourceSubmeshPicker() {
            var mesh = _source != null ? _source.sharedMesh : null;
            if (mesh == null || mesh.subMeshCount <= 1) {
                _sourceSubmesh = -1;
                return;
            }
            var labels = new GUIContent[mesh.subMeshCount + 1];
            labels[0] = new GUIContent("All submeshes", "Use all source triangles.");
            for (int i = 0; i < mesh.subMeshCount; i++) {
                string mat = "(no material)";
                if (_source.sharedMaterials != null && i < _source.sharedMaterials.Length && _source.sharedMaterials[i] != null) {
                    mat = _source.sharedMaterials[i].name;
                }
                labels[i + 1] = new GUIContent($"{i}: {mat}", "Limit source surface matching to this submesh.");
            }
            int display = _sourceSubmesh < 0 ? 0 : Mathf.Clamp(_sourceSubmesh + 1, 1, labels.Length - 1);
            WkStyles.LabeledField(
                new GUIContent("Source submesh", "Optional source material/submesh mask."),
                () => {
                    int next = EditorGUILayout.Popup(display, labels);
                    _sourceSubmesh = next == 0 ? -1 : next - 1;
                });
        }

        private void DrawRendererNotice(string label, SkinnedMeshRenderer renderer) {
            if (renderer == null) return;
            var mesh = renderer.sharedMesh;
            if (mesh == null) {
                WkStyles.Notice(NoticeKind.Warning, $"{label} renderer has no mesh.");
                return;
            }
            if (!mesh.isReadable) {
                WkStyles.Notice(NoticeKind.Warning, $"{label} mesh is not readable. Enable Read/Write on the importer before transfer.");
                return;
            }
            if (renderer.bones == null || renderer.bones.Length == 0) {
                WkStyles.Notice(NoticeKind.Warning, $"{label} renderer has no bones.");
            }
        }

        private void RunPreview() {
            try {
                EditorUtility.DisplayProgressBar("Weight Transfer", "Computing transfer...", 0.25f);
                _lastResult = WeightTransferSolver.Transfer(BuildSettings());
                _status = _lastResult.Message;
                AvatarQolLogger.Instance.Info($"Weight Transfer preview: {_status}");
            } finally {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ApplyTransfer() {
            try {
                EditorUtility.DisplayProgressBar("Weight Transfer", "Computing transfer...", 0.25f);
                _lastResult = WeightTransferSolver.Transfer(BuildSettings());
                _status = _lastResult.Message;
                if (_lastResult.Weights == null) return;

                EditorUtility.DisplayProgressBar("Weight Transfer", "Writing generated mesh...", 0.85f);
                if (MeshWeightWriter.WriteWeightsToGeneratedMesh(
                        _target,
                        _lastResult.Weights,
                        _maxInfluences,
                        _pruneThreshold,
                        0,
                        "(WeightTransfer)",
                        "Avatar QoL: Apply weight transfer",
                        out var write,
                        out string error)) {
                    _status = $"{_lastResult.Message} Wrote {write.AssetPath}.";
                    AvatarQolLogger.Instance.Info($"Weight Transfer applied: {_status}");
                } else {
                    _status = error;
                    AvatarQolLogger.Instance.Warning($"Weight Transfer apply failed: {error}");
                }
            } finally {
                EditorUtility.ClearProgressBar();
            }
        }

        private WeightTransferSettings BuildSettings() {
            return new WeightTransferSettings {
                Source = _source,
                Target = _target,
                SpaceRoot = _spaceRoot,
                Mode = _mode,
                SourceSubmesh = _sourceSubmesh,
                MaxClosestDistance = _maxClosestDistance,
                MaxProjectionDistance = _maxProjectionDistance,
                NormalAngleLimit = _normalAngle,
                AllowFlippedNormals = _allowFlippedNormals,
                InpaintRejectedVertices = _inpaint,
                InpaintIterations = _inpaintIterations,
                MaxInfluences = _maxInfluences,
                PruneThreshold = _pruneThreshold,
                FallbackBone = 0,
            };
        }

        private bool CanRun() {
            return RendererReady(_source) && RendererReady(_target);
        }

        private static bool RendererReady(SkinnedMeshRenderer renderer) {
            if (renderer == null || renderer.sharedMesh == null) return false;
            if (!renderer.sharedMesh.isReadable) return false;
            return renderer.bones != null && renderer.bones.Length > 0;
        }
    }
}
