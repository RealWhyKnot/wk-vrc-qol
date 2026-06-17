// MaterialPolygonRemoverWindow.cs

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class MaterialPolygonRemoverWindow : EditorWindow {

        [SerializeField] private SkinnedMeshRenderer _renderer;
        [SerializeField] private List<bool> _removeSlots = new List<bool>();

        private readonly List<string> _resultDetail = new List<string>();
        private string _resultSummary = "";
        private Vector2 _pageScroll;
        private Vector2 _resultScroll;
        private string _autoSizeSignature;

        private const string WikiUrl = "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#material-polygon-remover";

        internal static void Open(bool prefillFromSelection) {
            var w = GetWindow<MaterialPolygonRemoverWindow>(false, "Material Polygon Remover", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Material Polygon Remover");
            w.minSize = new Vector2(560, 440);
            if (prefillFromSelection) w.PrefillFromSelection();
            w.Show();
            w.Focus();
        }

        private void PrefillFromSelection() {
            var go = Selection.activeGameObject;
            if (go == null) return;
            _renderer = go.GetComponent<SkinnedMeshRenderer>() ?? go.GetComponentInChildren<SkinnedMeshRenderer>(true);
            SyncSlotList();
            ClearResult();
        }

        private void OnGUI() {
            using var _theme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                using (var s = new EditorGUILayout.ScrollViewScope(_pageScroll, false, false)) {
                    _pageScroll = s.scrollPosition;
                    WkStyles.Notice(NoticeKind.Info,
                        "Deletes all polygons assigned to selected material slots, compacts the mesh, and preserves weights, UVs, colors, normals, tangents, and blendshapes.");
                    DrawTarget();
                    EditorGUILayout.Space(2);
                    DrawSlots();
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
                    new GUIContent("Material Polygon Remover",
                        "Create a new mesh with polygons from selected material slots removed."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("?", "Open the wiki page for this tool."), EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawTarget() {
            using (WkStyles.Section("1. Renderer", "The SkinnedMeshRenderer whose material slots should be pruned.")) {
                var prev = _renderer;
                WkStyles.LabeledField(
                    new GUIContent("Renderer", "Drop a SkinnedMeshRenderer from the hierarchy."),
                    () => _renderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_renderer, typeof(SkinnedMeshRenderer), true));
                if (prev != _renderer) {
                    SyncSlotList();
                    ClearResult();
                }
                if (_renderer != null && _renderer.sharedMesh != null) {
                    EditorGUILayout.LabelField(
                        $"{_renderer.sharedMesh.vertexCount} vertices, {_renderer.sharedMesh.subMeshCount} submesh(es).",
                        WkStyles.Muted);
                    if (!_renderer.sharedMesh.isReadable) {
                        WkStyles.Notice(NoticeKind.Warning,
                            "Mesh is not readable. Enable Read/Write in the model importer before applying.");
                    }
                }
            }
        }

        private void DrawSlots() {
            using (WkStyles.Section("2. Material slots", "Checked slots are removed. Unchecked slots are kept and remapped in order.")) {
                if (_renderer == null || _renderer.sharedMesh == null) {
                    EditorGUILayout.LabelField("(pick a renderer first)", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                SyncSlotList();
                var mesh = _renderer.sharedMesh;
                var materials = _renderer.sharedMaterials;
                for (int i = 0; i < mesh.subMeshCount; i++) {
                    string matName = i < materials.Length && materials[i] != null ? materials[i].name : "(missing material)";
                    int triCount = mesh.GetTriangles(i).Length / 3;
                    _removeSlots[i] = EditorGUILayout.ToggleLeft(
                        new GUIContent($"[{i}] {matName}  ({triCount} tris)",
                            "Checked material slots are removed."),
                        _removeSlots[i]);
                }
            }
        }

        private void DrawActions() {
            bool canApply = _renderer != null && _renderer.sharedMesh != null && AnySelected();
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!canApply)) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Remove polygons",
                                "Create a new mesh asset without the checked material slots."),
                            GUILayout.MinWidth(160), GUILayout.Height(32))) {
                        RunApply();
                    }
                }
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(new GUIContent("Clear checks", "Uncheck every material slot."), GUILayout.Width(96), GUILayout.Height(32))) {
                    for (int i = 0; i < _removeSlots.Count; i++) _removeSlots[i] = false;
                }
            }
            if (!canApply) EditorGUILayout.LabelField("Pick a renderer and select at least one slot to remove.", WkStyles.Muted);
        }

        private void DrawResults() {
            if (string.IsNullOrEmpty(_resultSummary) && _resultDetail.Count == 0) return;
            using (WkStyles.Section("Result", "Last material-removal run.")) {
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
            var result = MaterialPolygonRemoverCore.Apply(_renderer, _removeSlots);
            _resultSummary = result.Summary;
            _resultDetail.AddRange(result.Detail);
            if (result.CreatedPaths.Count > 0) {
                _resultDetail.Add("");
                _resultDetail.Add("Created meshes:");
                foreach (var path in result.CreatedPaths) _resultDetail.Add("  " + path);
            }
        }

        private void SyncSlotList() {
            int count = _renderer != null && _renderer.sharedMesh != null ? _renderer.sharedMesh.subMeshCount : 0;
            while (_removeSlots.Count < count) _removeSlots.Add(false);
            while (_removeSlots.Count > count) _removeSlots.RemoveAt(_removeSlots.Count - 1);
        }

        private bool AnySelected() {
            for (int i = 0; i < _removeSlots.Count; i++) if (_removeSlots[i]) return true;
            return false;
        }

        private void ClearResult() {
            _resultSummary = "";
            _resultDetail.Clear();
        }

        private void RequestAutoSize() {
            var signature = $"{(_renderer != null ? _renderer.GetInstanceID() : 0)}|{_removeSlots.Count}|{AnySelected()}|{_resultSummary}|{_resultDetail.Count}";
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(560f, 440f),
                new Vector2(700f, 520f + WkStyles.CappedListHeight(_resultDetail.Count, 16f, 0f, 180f)),
                new Vector2(900f, 740f));
        }
    }
}
