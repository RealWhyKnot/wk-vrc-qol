// MeshSculptWindow.cs
//
// SceneView mesh sculpting for SkinnedMeshRenderer avatar meshes. The
// durable edit target is a generated mesh asset assigned to sharedMesh;
// imported model sub-assets are never mutated in place.

using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class MeshSculptWindow : WkToolWindow {

        private enum SculptMode {
            Select = 0,
            Move = 1,
            Grab = 2,
            Smooth = 3,
            Inflate = 4,
        }

        [SerializeField] private SkinnedMeshRenderer _renderer;
        [SerializeField] private SculptMode _mode = SculptMode.Select;
        [SerializeField] private float _radius = 0.045f;
        [SerializeField] private float _strength = 0.65f;
        [SerializeField] private int _submesh = 0;
        [SerializeField] private bool _flipFill;
        [SerializeField] private bool _showHelp = true;

        private MeshSculptSession _session;
        private string _status = "";
        private bool _brushActive;
        private Vector3 _lastBrushWorld;
        private MeshSculptCore.MeshHit _hoverHit;
        private bool _hasHoverHit;

        protected override string Title => "Mesh Sculpt";
        protected override Vector2 InitialMinSize => new Vector2(520, 520);

        internal static void Open(SkinnedMeshRenderer prefillRenderer) {
            var w = GetWindow<MeshSculptWindow>(false, "Mesh Sculpt", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - Mesh Sculpt");
            if (prefillRenderer != null) w._renderer = prefillRenderer;
            w.Show();
            w.Focus();
        }

        protected override void OnEnable() {
            base.OnEnable();
            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
        }

        protected override void OnDisable() {
            SceneView.duringSceneGui -= OnSceneGui;
            StopSession();
            base.OnDisable();
        }

        protected override void OnBodyGUI() {
            DrawStatus();
            DrawTargetSection();
            EditorGUILayout.Space(2);
            DrawToolSection();
            EditorGUILayout.Space(2);
            DrawSelectionSection();
            EditorGUILayout.Space(2);
            DrawTopologySection();
            EditorGUILayout.Space(2);
            DrawNotesSection();
        }

        private void DrawStatus() {
            if (_session != null && _session.IsValid) {
                WkStyles.StatusBanner($"SCULPTING  -  {RendererSummary()}  -  {_session.SelectedCount} selected", NoticeKind.Success, height: 24);
            } else if (_renderer == null) {
                WkStyles.StatusBanner("IDLE  -  pick a SkinnedMeshRenderer", NoticeKind.Info, height: 24);
            } else if (_renderer.sharedMesh == null || !_renderer.sharedMesh.isReadable) {
                WkStyles.StatusBanner("NOT READY  -  mesh needs attention", NoticeKind.Warning, height: 24);
            } else {
                WkStyles.StatusBanner("READY  -  make the mesh editable to begin", NoticeKind.Info, height: 24);
            }
            if (!string.IsNullOrEmpty(_status)) {
                EditorGUILayout.LabelField(_status, WkStyles.Muted);
            }
        }

        private void DrawTargetSection() {
            using (WkStyles.Section("1. Target",
                    "The SkinnedMeshRenderer whose mesh will be cloned to a generated editable asset before sculpting.")) {
                var prev = _renderer;
                _renderer = WkStyles.ObjectFieldRow(
                    new GUIContent("Renderer", "The SkinnedMeshRenderer to edit."),
                    _renderer,
                    allowSceneObjects: true);
                if (_renderer != prev) {
                    StopSession();
                    _submesh = 0;
                    _status = "";
                }

                var mesh = _renderer != null ? _renderer.sharedMesh : null;
                if (_renderer == null) {
                    WkStyles.Notice(NoticeKind.Info, "Select a renderer in the scene or open this from a renderer's hierarchy menu.");
                    return;
                }
                if (mesh == null) {
                    WkStyles.Notice(NoticeKind.Warning, "Renderer has no mesh assigned.");
                    return;
                }
                if (!mesh.isReadable) {
                    if (WkStyles.Notice(NoticeKind.Warning,
                            "The selected mesh has Read/Write disabled. Mesh Sculpt needs readable vertices, triangles, and bone weights.",
                            "Enable Read/Write",
                            "Set the source model importer's Read/Write flag and reimport.")) {
                        MeshSculptEditableMesh.EnableReadWriteIfPossible(mesh);
                    }
                    return;
                }

                EditorGUILayout.LabelField(
                    new GUIContent("Mesh", $"{mesh.vertexCount} vertices, {mesh.subMeshCount} submesh(es), {mesh.blendShapeCount} blendshape(s)."),
                    new GUIContent($"{mesh.name}  -  {mesh.vertexCount} verts  -  {mesh.subMeshCount} submeshes"));

                DrawSubmeshPicker(mesh);

                using (new EditorGUILayout.HorizontalScope()) {
                    using (new EditorGUI.DisabledScope(_session != null)) {
                        if (WkStyles.PrimaryButtonInline(
                                new GUIContent("Make Editable / Start",
                                    "Clone the mesh to Assets/AvatarQol Generated if needed, assign it to this renderer, and start Scene view sculpting."),
                                GUILayout.MinWidth(180))) {
                            StartSession();
                        }
                    }
                    using (new EditorGUI.DisabledScope(_session == null)) {
                        if (GUILayout.Button(
                                new GUIContent("Rebake",
                                    "Rebuild the posed SceneView picking snapshot from the current renderer pose."),
                                GUILayout.Height(28), GUILayout.Width(84))) {
                            _session.RebuildSnapshot();
                            _status = "Baked the current posed mesh snapshot.";
                            SceneView.RepaintAll();
                        }
                        if (GUILayout.Button(
                                new GUIContent("Stop",
                                    "Stop Scene view sculpting. The generated mesh asset remains assigned."),
                                GUILayout.Height(28), GUILayout.Width(70))) {
                            StopSession();
                        }
                    }
                }
            }
        }

        private void DrawSubmeshPicker(Mesh mesh) {
            if (mesh == null) return;
            if (mesh.subMeshCount <= 0) {
                _submesh = 0;
                return;
            }

            var labels = new GUIContent[mesh.subMeshCount];
            for (int i = 0; i < labels.Length; i++) {
                string mat = "(no material)";
                if (_renderer != null && _renderer.sharedMaterials != null
                        && i < _renderer.sharedMaterials.Length
                        && _renderer.sharedMaterials[i] != null) {
                    mat = _renderer.sharedMaterials[i].name;
                }
                labels[i] = new GUIContent($"{i}: {mat}", "New filled faces are appended to this submesh/material slot.");
            }
            _submesh = Mathf.Clamp(_submesh, 0, mesh.subMeshCount - 1);
            WkStyles.LabeledField(
                new GUIContent("Fill submesh", "Material/submesh slot used by Fill Face."),
                () => _submesh = EditorGUILayout.Popup(_submesh, labels));
        }

        private void DrawToolSection() {
            using (WkStyles.Section("2. Tool",
                    "Scene view mode and brush settings.")) {
                _mode = (SculptMode)WkStyles.TabBar((int)_mode,
                    new GUIContent("Select", "Click mesh vertices to select them. Shift-click toggles without clearing."),
                    new GUIContent("Move", "Move selected vertices with a Scene view position handle."),
                    new GUIContent("Grab", "Drag nearby vertices with a falloff brush."),
                    new GUIContent("Smooth", "Smooth nearby vertices toward their mesh neighbors."),
                    new GUIContent("Inflate", "Push nearby vertices along the baked surface normal."));

                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Radius", "Brush radius in metres."), GUILayout.Width(WkStyles.LabelColumn));
                    _radius = EditorGUILayout.Slider(_radius, 0.001f, 0.5f);
                }
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(new GUIContent("Strength", "Brush strength. Grab scales the drag delta; Smooth and Inflate use this as per-stroke intensity."), GUILayout.Width(WkStyles.LabelColumn));
                    _strength = EditorGUILayout.Slider(_strength, 0.01f, 1f);
                }

                EditorGUILayout.LabelField(SceneHint(), WkStyles.Muted);
            }
        }

        private void DrawSelectionSection() {
            using (WkStyles.Section("3. Selection",
                    "Selected vertices can be moved directly or used as the ordered vertex list for Fill Face.")) {
                int count = _session != null ? _session.SelectedCount : 0;
                EditorGUILayout.LabelField(new GUIContent("Selected", "Number of selected vertices."), new GUIContent(count.ToString()));
                using (new EditorGUILayout.HorizontalScope()) {
                    using (new EditorGUI.DisabledScope(_session == null || count == 0)) {
                        if (GUILayout.Button(
                                new GUIContent("Clear selection", "Clear the selected vertex list."),
                                GUILayout.Height(24), GUILayout.Width(120))) {
                            _session.ClearSelection();
                            SceneView.RepaintAll();
                        }
                    }
                    using (new EditorGUI.DisabledScope(_session == null)) {
                        if (GUILayout.Button(
                                new GUIContent("Frame selection", "Move the Scene view camera to the selected vertices."),
                                GUILayout.Height(24), GUILayout.Width(120))) {
                            FrameSelection();
                        }
                    }
                }
            }
        }

        private void DrawTopologySection() {
            using (WkStyles.Section("4. Topology",
                    "Fill a face between three or four selected existing vertices. This appends triangles only; no new vertices are created.")) {
                _flipFill = EditorGUILayout.ToggleLeft(
                    new GUIContent("Flip filled face winding",
                        "Reverse the triangle winding when the new face appears inside-out."),
                    _flipFill);
                using (new EditorGUILayout.HorizontalScope()) {
                    using (new EditorGUI.DisabledScope(_session == null || _session.SelectedCount < 3 || _session.SelectedCount > 4)) {
                        if (WkStyles.PrimaryButtonInline(
                                new GUIContent("Fill Face",
                                    "Append one triangle or two quad triangles to the chosen submesh using the selected vertex order."),
                                GUILayout.Height(30), GUILayout.Width(120))) {
                            FillFace();
                        }
                    }
                    EditorGUILayout.LabelField("Triangle and quad fills only in this first pass.", WkStyles.Muted);
                }
            }
        }

        private void DrawNotesSection() {
            _showHelp = WkStyles.FoldoutHeaderRow("Notes", _showHelp,
                "Current behavior and limits for the first Mesh Sculpt pass.");
            if (!_showHelp) return;
            WkStyles.Notice(NoticeKind.Info,
                "This first pass edits a generated mesh clone assigned to sharedMesh. It does not write imported model sub-assets, does not create new vertices, and does not store edits as blendshapes.");
        }

        private void StartSession() {
            if (_renderer == null) return;
            if (!MeshSculptEditableMesh.EnsureEditable(
                    _renderer,
                    "(MeshSculpt)",
                    "Avatar QoL: Create sculpt mesh",
                    out var result)) {
                _status = result.Message;
                return;
            }
            StopSession();
            _session = new MeshSculptSession(_renderer);
            _submesh = Mathf.Clamp(_submesh, 0, Mathf.Max(0, _session.Mesh.subMeshCount - 1));
            _status = result.Message;
            AvatarQolLogger.Instance.Info($"Mesh Sculpt: {result.Message}");
            SceneView.RepaintAll();
        }

        private void StopSession() {
            _brushActive = false;
            _session?.Dispose();
            _session = null;
            SceneView.RepaintAll();
        }

        private void FillFace() {
            if (_session == null) return;
            if (_session.FillSelectedFace(_submesh, _flipFill, out string error)) {
                _status = $"Filled face on submesh {_submesh}.";
                AvatarQolLogger.Instance.Info($"Mesh Sculpt: {_status}");
            } else {
                _status = error;
                AvatarQolLogger.Instance.Warning($"Mesh Sculpt: fill failed: {error}");
            }
        }

        private void OnSceneGui(SceneView sceneView) {
            if (_session == null || !_session.IsValid) return;
            sceneView.wantsMouseMove = true;

            var e = Event.current;
            var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            _hasHoverHit = _session.Raycast(ray, out _hoverHit);
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            if (e.type == EventType.Layout && _mode != SculptMode.Move) {
                HandleUtility.AddDefaultControl(controlId);
            }

            if (_mode == SculptMode.Move && _session.SelectedCount > 0) {
                DrawMoveHandle();
            }

            if (e.type == EventType.Repaint) {
                DrawSceneOverlay(sceneView);
            }

            if (_mode == SculptMode.Select) {
                HandleSelectEvents(e);
            } else if (_mode == SculptMode.Grab || _mode == SculptMode.Smooth || _mode == SculptMode.Inflate) {
                HandleBrushEvents(e);
            }

            if (e.type == EventType.MouseMove) sceneView.Repaint();
        }

        private void DrawMoveHandle() {
            var center = _session.SelectionCenterWorld();
            EditorGUI.BeginChangeCheck();
            var next = Handles.PositionHandle(center, Quaternion.identity);
            if (EditorGUI.EndChangeCheck()) {
                _session.MoveSelected(next - center, "Avatar QoL: Move sculpt vertices");
                _status = $"Moved {_session.SelectedCount} selected vertex/vertices.";
            }
        }

        private void HandleSelectEvents(Event e) {
            if (e.type != EventType.MouseDown || e.button != 0 || !_hasHoverHit) return;
            int nearest = _session.NearestVertexOnHit(_hoverHit);
            bool additive = e.shift || e.control || e.command;
            bool toggle = e.shift;
            _session.SelectVertex(nearest, additive, toggle);
            _status = $"Selected {_session.SelectedCount} vertex/vertices.";
            e.Use();
            Repaint();
            SceneView.RepaintAll();
        }

        private void HandleBrushEvents(Event e) {
            if (e.type == EventType.MouseDown && e.button == 0 && _hasHoverHit) {
                _brushActive = true;
                _lastBrushWorld = _hoverHit.WorldPosition;
                if (_mode == SculptMode.Smooth || _mode == SculptMode.Inflate) ApplyBrush(_hoverHit.WorldPosition, Vector3.zero);
                e.Use();
                return;
            }
            if (e.type == EventType.MouseDrag && e.button == 0 && _brushActive && _hasHoverHit) {
                var delta = _hoverHit.WorldPosition - _lastBrushWorld;
                ApplyBrush(_hoverHit.WorldPosition, delta);
                _lastBrushWorld = _hoverHit.WorldPosition;
                e.Use();
                return;
            }
            if (e.type == EventType.MouseUp && e.button == 0 && _brushActive) {
                _brushActive = false;
                e.Use();
            }
        }

        private void ApplyBrush(Vector3 center, Vector3 worldDelta) {
            if (_session == null) return;
            switch (_mode) {
                case SculptMode.Grab:
                    _session.ApplyGrabBrush(center, _radius, worldDelta, _strength, "Avatar QoL: Grab sculpt brush");
                    _status = "Grab brush applied.";
                    break;
                case SculptMode.Smooth:
                    _session.ApplySmoothBrush(center, _radius, Mathf.Clamp01(_strength * 0.35f), "Avatar QoL: Smooth sculpt brush");
                    _status = "Smooth brush applied.";
                    break;
                case SculptMode.Inflate:
                    _session.ApplyInflateBrush(center, _radius, _radius * _strength * 0.08f, "Avatar QoL: Inflate sculpt brush");
                    _status = "Inflate brush applied.";
                    break;
            }
            Repaint();
        }

        private void DrawSceneOverlay(SceneView sceneView) {
            DrawSelectedVertices();
            if (_hasHoverHit && (_mode == SculptMode.Grab || _mode == SculptMode.Smooth || _mode == SculptMode.Inflate)) {
                var prev = Handles.color;
                Handles.color = _mode == SculptMode.Smooth
                    ? new Color(0.40f, 0.95f, 0.45f, 0.95f)
                    : _mode == SculptMode.Inflate
                        ? new Color(1f, 0.70f, 0.25f, 0.95f)
                        : new Color(0.35f, 0.85f, 1f, 0.95f);
                Handles.DrawWireDisc(_hoverHit.WorldPosition, _hoverHit.WorldNormal, _radius);
                Handles.DrawSolidDisc(_hoverHit.WorldPosition, _hoverHit.WorldNormal, HandleUtility.GetHandleSize(_hoverHit.WorldPosition) * 0.012f);
                Handles.color = prev;
            }
            DrawHud(sceneView);
        }

        private void DrawSelectedVertices() {
            if (_session == null || _session.SelectedCount == 0) return;
            var prev = Handles.color;
            Handles.color = new Color(0.30f, 0.85f, 1f, 1f);
            foreach (var v in _session.SelectionOrder) {
                var p = _session.BakedWorldVertex(v);
                float size = HandleUtility.GetHandleSize(p) * 0.035f;
                Handles.SphereHandleCap(0, p, Quaternion.identity, size, EventType.Repaint);
            }
            Handles.color = prev;
        }

        private void DrawHud(SceneView sceneView) {
            Handles.BeginGUI();
            try {
                var rect = new Rect(10, 10, 280, 76);
                GUI.Box(rect, GUIContent.none);
                var style = new GUIStyle(EditorStyles.miniLabel) {
                    normal = { textColor = Color.white },
                    wordWrap = false,
                };
                GUI.Label(new Rect(rect.x + 10, rect.y + 6, rect.width - 20, 16), "MESH SCULPT", EditorStyles.boldLabel);
                GUI.Label(new Rect(rect.x + 10, rect.y + 24, rect.width - 20, 16), $"mode {_mode}  radius {_radius * 100f:0.0} cm", style);
                GUI.Label(new Rect(rect.x + 10, rect.y + 42, rect.width - 20, 16), $"{_session.SelectedCount} selected  -  {(_hasHoverHit ? "on mesh" : "off mesh")}", style);
            } finally {
                Handles.EndGUI();
            }
        }

        private void FrameSelection() {
            if (_session == null || _session.SelectedCount == 0) return;
            var sv = SceneView.lastActiveSceneView;
            if (sv == null) return;
            var center = _session.SelectionCenterWorld();
            sv.LookAt(center, sv.rotation, Mathf.Max(0.12f, _radius * 4f));
            sv.Repaint();
        }

        private string RendererSummary() {
            if (_renderer == null) return "(no renderer)";
            var mesh = _renderer.sharedMesh;
            string meshText = mesh != null ? mesh.name : "(no mesh)";
            return $"{PathUtility.GetGameObjectPath(_renderer.gameObject)}  -  {meshText}";
        }

        private string SceneHint() {
            switch (_mode) {
                case SculptMode.Select:
                    return "Scene view: click a mesh vertex to select; Shift-click toggles without clearing.";
                case SculptMode.Move:
                    return "Scene view: drag the position handle to move selected vertices.";
                case SculptMode.Grab:
                    return "Scene view: drag on the mesh to pull nearby vertices with brush falloff.";
                case SculptMode.Smooth:
                    return "Scene view: drag on the mesh to smooth nearby vertices toward their neighbors.";
                case SculptMode.Inflate:
                    return "Scene view: drag on the mesh to push nearby vertices along baked normals.";
            }
            return "";
        }
    }
}
