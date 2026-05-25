// MaskPainterWindow.cs
//
// Paint a mask onto an avatar in the Scene view; save it as a PNG.
// Useful for generating VRCFury toggle masks, shader emission masks,
// AvatarMask weight maps, decal masks, or any other white-on-black
// region map that pins to a SkinnedMeshRenderer's UV layout.
//
// How it works (short version):
//   1. User picks a SkinnedMeshRenderer, optionally a single submesh.
//   2. "Start Painting" bakes the live deformed mesh into a snapshot
//      and allocates a RenderTexture of the chosen resolution.
//   3. Painting in the Scene view dispatches a UV-space brush shader
//      that writes into the RT. A baked snapshot mesh is used for both
//      the ray-triangle picking and the shader stroke pass.
//   4. A scene overlay re-renders the snapshot mesh with a tint shader
//      sampled from the mask, so the painted region glows on the body.
//      A scene HUD (top-left) and a hotkey hint strip (bottom) keep the
//      live state visible without flipping back to the window.
//   5. "Save PNG..." writes the RT (optionally dilated to plug UV-island
//      bleed) and configures the importer for linear sampling.
//
// Diagnostics: every state transition (start/stop, bake, hit/miss flip,
// first stroke, save) logs to the package's WkLogger session file. Turn
// on "Verbose log" in Advanced to mirror those lines to the Unity Console
// while debugging. The Refresh stats button forces a Texture2D readback
// to compute the painted-coverage percentage on demand (the readback is
// too expensive to run every frame).
//
// Domain-reload survival: the in-progress RT is dropped on every
// AssemblyReloadEvents.beforeAssemblyReload. The user opted out of
// autosave (their explicit choice); we warn once on first paint that
// recompiles will lose unsaved work.
//
// Out of scope for this version:
//   - Persist the mask as a Texture2D asset across window opens.
//   - Per-channel submesh isolation (the submesh selector applies to
//     all channels in a given stroke).
//   - Multi-color painting beyond white (this is mask logic).
//   - Custom brush textures / shapes (uniform soft-edge disc only).

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.Core.Styling;
using WhyKnot.Core.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class MaskPainterWindow : EditorWindow {

        // ---- Persisted (survives domain reload via [SerializeField]) ----

        [SerializeField] private SkinnedMeshRenderer _renderer;
        [SerializeField] private int   _submeshIndex     = -1;      // -1 = all submeshes
        [SerializeField] private bool  _symmetryEnabled  = true;
        [SerializeField] private Transform _symmetryRoot;            // auto-detected; user can override
        [SerializeField] private MaskMode _mode          = MaskMode.Grayscale;
        [SerializeField] private MaskChannel _channel    = MaskChannel.R;
        [SerializeField] private int   _resolution       = 1024;
        [SerializeField] private float _radius           = 0.045f;   // metres, world space
        [SerializeField] private float _strength         = 0.6f;
        [SerializeField] private float _hardness         = 0.4f;
        [SerializeField] private bool  _erase            = false;
        [SerializeField] private bool  _dilateOnSave     = true;
        [SerializeField] private int   _dilationIterations = 4;
        [SerializeField] private bool  _sRGBOnSave       = false;
        [SerializeField] private bool  _advancedOpen;
        [SerializeField] private bool  _verboseLog;
        [SerializeField] private bool  _showSceneHud     = true;
        [SerializeField] private bool  _showHotkeyStrip  = true;
        [SerializeField] private bool  _showSymmetryPlane = false;

        // ---- Volatile state (re-built after every domain reload) ----

        private bool          _painting;
        private RenderTexture _maskRT;
        private Mesh          _snapshotMesh;
        private Vector3[]     _snapshotWorldVerts;
        private Bounds        _snapshotWorldBounds;
        private float[]       _bakedBlendShapes;
        private Material      _brushMaterial;
        private Material      _previewMaterial;
        private readonly List<Texture2D> _undoStack = new List<Texture2D>();

        private Vector3 _hitWorld;
        private Vector3 _hitNormal;
        private bool    _hasHit;
        private Vector2 _lastMousePos = new Vector2(-1, -1);
        private double  _lastStrokeTime;
        private const double StrokeIntervalSec = 1.0 / 60.0;

        private bool   _strokeInProgress;
        private int    _strokeCount;          // strokes started since paint session began
        private int    _dispatchCount;        // total shader stroke dispatches since session began
        private int    _strokeDispatches;     // dispatches in the current MouseDown..MouseUp stroke
        private bool   _firstHitLogged;
        private string _lastSavedPath;
        private bool   _previousToolsHidden;
        private UnityEditor.Tool _previousTool;
        private bool   _toolsHiddenByUs;

        // Stats panel: refreshed only on user request (Texture2D readback
        // is too expensive for an OnGUI tick). Holds the coverage % at the
        // moment the user pressed Refresh stats.
        private float _statsCoveragePct = -1f; // -1 = "not yet computed"
        private double _statsRefreshedAt;

        // ---- Prefs keys ----

        private const string PrefsPrefix              = "dev.whyknot.avatar-qol.MaskPainter.";
        private const string PrefsRadius              = PrefsPrefix + "Radius";
        private const string PrefsStrength            = PrefsPrefix + "Strength";
        private const string PrefsHardness            = PrefsPrefix + "Hardness";
        private const string PrefsResolution          = PrefsPrefix + "Resolution";
        private const string PrefsSymmetry            = PrefsPrefix + "Symmetry";
        private const string PrefsDilateOnSave        = PrefsPrefix + "DilateOnSave";
        private const string PrefsDilationIterations  = PrefsPrefix + "DilationIterations";
        private const string PrefsSRGB                = PrefsPrefix + "SRGB";
        private const string PrefsReloadWarningShown  = PrefsPrefix + "ReloadWarningShown";
        private const string PrefsVerbose             = PrefsPrefix + "VerboseLog";
        private const string PrefsShowHud             = PrefsPrefix + "ShowHud";
        private const string PrefsShowHotkeyStrip     = PrefsPrefix + "ShowHotkeyStrip";
        private const string PrefsShowSymmetryPlane   = PrefsPrefix + "ShowSymmetryPlane";

        private const string WikiUrl =
            "https://github.com/RealWhyKnot/vrc-avatar-qol/wiki/Tools-Overview#paint-mask";

        // ---- Types ----

        internal enum MaskMode { Grayscale, Channel }
        internal enum MaskChannel { R = 0, G = 1, B = 2, A = 3 }

        // Lazy HUD styles -- EditorStyles is null during static init.
        private GUIStyle _hudBoxStyle;
        private GUIStyle _hudLabelStyle;
        private GUIStyle _hudHeaderStyle;
        private GUIStyle _hudHintStyle;

        // ---- Entry points ----

        internal static void Open(SkinnedMeshRenderer prefillRenderer) {
            var w = GetWindow<MaskPainterWindow>(false, "Paint Mask", true);
            w.titleContent = new GUIContent("Avatar QoL - Paint Mask");
            w.minSize = new Vector2(440, 720);
            if (prefillRenderer != null) {
                w._renderer = prefillRenderer;
                w.AutoDetectSymmetryRoot();
            }
            w.Show();
            w.Focus();
        }

        // ---- Lifecycle ----

        private void OnEnable() {
            LoadEditorPrefs();
            AssemblyReloadEvents.beforeAssemblyReload += BeforeAssemblyReload;
            EditorApplication.quitting += ReleaseGpuResources;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private void OnDisable() {
            if (_painting) StopPainting(prompt: false);
            ReleaseGpuResources();
            AssemblyReloadEvents.beforeAssemblyReload -= BeforeAssemblyReload;
            EditorApplication.quitting -= ReleaseGpuResources;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            SceneView.duringSceneGui -= OnSceneGui;
            RestoreToolsState();
        }

        private void OnDestroy() {
            if (_painting) StopPainting(prompt: false);
            ReleaseGpuResources();
            RestoreToolsState();
        }

        private void BeforeAssemblyReload() {
            if (_painting) {
                Diag(LogLevel.Info, "Domain reload incoming; dropping paint session.");
                StopPainting(prompt: false);
            }
            ReleaseGpuResources();
            RestoreToolsState();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state) {
            if (state == PlayModeStateChange.ExitingEditMode && _painting) StopPainting(prompt: false);
        }

        private void ReleaseGpuResources() {
            SceneView.duringSceneGui -= OnSceneGui;
            if (_maskRT != null) { _maskRT.Release(); DestroyImmediate(_maskRT); _maskRT = null; }
            if (_snapshotMesh != null) { DestroyImmediate(_snapshotMesh); _snapshotMesh = null; }
            if (_brushMaterial != null) { DestroyImmediate(_brushMaterial); _brushMaterial = null; }
            if (_previewMaterial != null) { DestroyImmediate(_previewMaterial); _previewMaterial = null; }
            foreach (var t in _undoStack) if (t != null) DestroyImmediate(t);
            _undoStack.Clear();
            _snapshotWorldVerts = null;
            _bakedBlendShapes = null;
            _hasHit = false;
            _painting = false;
            _firstHitLogged = false;
            _statsCoveragePct = -1f;
        }

        // ---- Prefs ----

        private void LoadEditorPrefs() {
            _radius             = EditorPrefs.GetFloat(PrefsRadius,             _radius);
            _strength           = EditorPrefs.GetFloat(PrefsStrength,           _strength);
            _hardness           = EditorPrefs.GetFloat(PrefsHardness,           _hardness);
            _resolution         = EditorPrefs.GetInt  (PrefsResolution,         _resolution);
            _symmetryEnabled    = EditorPrefs.GetBool (PrefsSymmetry,           _symmetryEnabled);
            _dilateOnSave       = EditorPrefs.GetBool (PrefsDilateOnSave,       _dilateOnSave);
            _dilationIterations = EditorPrefs.GetInt  (PrefsDilationIterations, _dilationIterations);
            _sRGBOnSave         = EditorPrefs.GetBool (PrefsSRGB,               _sRGBOnSave);
            _verboseLog         = EditorPrefs.GetBool (PrefsVerbose,            _verboseLog);
            _showSceneHud       = EditorPrefs.GetBool (PrefsShowHud,            _showSceneHud);
            _showHotkeyStrip    = EditorPrefs.GetBool (PrefsShowHotkeyStrip,    _showHotkeyStrip);
            _showSymmetryPlane  = EditorPrefs.GetBool (PrefsShowSymmetryPlane,  _showSymmetryPlane);
        }

        private void SaveEditorPrefs() {
            EditorPrefs.SetFloat(PrefsRadius,             _radius);
            EditorPrefs.SetFloat(PrefsStrength,           _strength);
            EditorPrefs.SetFloat(PrefsHardness,           _hardness);
            EditorPrefs.SetInt  (PrefsResolution,         _resolution);
            EditorPrefs.SetBool (PrefsSymmetry,           _symmetryEnabled);
            EditorPrefs.SetBool (PrefsDilateOnSave,       _dilateOnSave);
            EditorPrefs.SetInt  (PrefsDilationIterations, _dilationIterations);
            EditorPrefs.SetBool (PrefsSRGB,               _sRGBOnSave);
            EditorPrefs.SetBool (PrefsVerbose,            _verboseLog);
            EditorPrefs.SetBool (PrefsShowHud,            _showSceneHud);
            EditorPrefs.SetBool (PrefsShowHotkeyStrip,    _showHotkeyStrip);
            EditorPrefs.SetBool (PrefsShowSymmetryPlane,  _showSymmetryPlane);
        }

        // ---- Diagnostics helper ----
        //
        // Routes Info-level lines through Debug or Info based on the
        // _verboseLog toggle. Debug stays file-only; Info mirrors to the
        // Unity Console. State transitions (start, stop, first hit) go
        // Info either way -- the cost is small and they're useful even
        // when the user isn't debugging.

        private enum LogLevel { Trace, Info, Warn }

        private void Diag(LogLevel level, string msg) {
            switch (level) {
                case LogLevel.Info: AvatarQolLogger.Instance.Info(msg); break;
                case LogLevel.Warn: AvatarQolLogger.Instance.Warning(msg); break;
                case LogLevel.Trace:
                    if (_verboseLog) AvatarQolLogger.Instance.Info(msg);
                    else             AvatarQolLogger.Instance.Debug(msg);
                    break;
            }
        }

        // ---- GUI ----

        private void OnGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            DrawStatusBanner();
            EditorGUILayout.Space(2);
            DrawTitleBar();
            DrawHelpNotice();
            DrawTargetSection();
            EditorGUILayout.Space(4);
            DrawStartStopBar();
            DrawDriftNotice();
            EditorGUILayout.Space(4);
            DrawMaskSection();
            EditorGUILayout.Space(2);
            DrawBrushSection();
            EditorGUILayout.Space(2);
            DrawPreviewSection();
            EditorGUILayout.Space(2);
            DrawAdvancedSection();
        }

        private void DrawStatusBanner() {
            // Coloured banner at the very top. Green = painting active,
            // amber = needs attention, slate = idle. Lets the user know
            // at a glance whether they're in a paint session.
            string headline;
            Color bg;
            if (_painting) {
                headline = $"●  PAINTING  —  {RendererSummary()}  —  {_strokeCount} stroke{(_strokeCount == 1 ? "" : "s")}, {_dispatchCount} dispatch{(_dispatchCount == 1 ? "" : "es")}";
                bg = new Color(0.10f, 0.65f, 0.30f, 1f);
            } else if (_renderer == null) {
                headline = "○  IDLE  —  pick a SkinnedMeshRenderer below to begin";
                bg = new Color(0.45f, 0.48f, 0.52f, 1f);
            } else if (!CanStart()) {
                headline = "▲  NOT READY  —  see warnings below";
                bg = new Color(0.85f, 0.55f, 0.15f, 1f);
            } else {
                headline = $"○  READY  —  {RendererSummary()}";
                bg = new Color(0.30f, 0.50f, 0.75f, 1f);
            }

            var rect = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none, GUILayout.Height(26), GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, bg);
            var inset = new Rect(rect.x + 10, rect.y + 3, rect.width - 20, rect.height - 4);
            var style = new GUIStyle(EditorStyles.boldLabel) {
                normal = { textColor = Color.white },
                fontSize = 11,
                alignment = TextAnchor.MiddleLeft,
            };
            GUI.Label(inset, headline, style);
        }

        private string RendererSummary() {
            if (_renderer == null) return "(no renderer)";
            string name = _renderer.gameObject.name;
            string submesh = _submeshIndex < 0 ? "all submeshes" : $"submesh #{_submeshIndex}";
            return $"{name} · {submesh} · {_resolution}²";
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Paint Mask",
                        "Paint a mask onto an avatar by clicking on it in the Scene view. The painted region is written into a RenderTexture in UV space; save it as a PNG to use as a VRCFury toggle mask, shader emission mask, decal mask, etc."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("Dump state",
                            "Print the current tool state (target, baked snapshot, RT, undo stack, brush settings, counters) to the Unity console. Useful for filing a bug report."),
                        EditorStyles.miniButton, GUILayout.Width(82), GUILayout.Height(18))) {
                    DumpState();
                }
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool in your browser."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawHelpNotice() {
            WkStyles.Notice(NoticeKind.Info,
                "Flow: pick a SkinnedMeshRenderer, click Start Painting, then paint on the avatar in the Scene view. " +
                "Hover the avatar to see the brush disc. [ and ] resize the brush, X toggles symmetry, E toggles erase, " +
                "Ctrl+Z undoes a stroke. Save PNG when done.");
        }

        private void DrawTargetSection() {
            using (WkStyles.Section("1. Target",
                    "Which renderer to paint on, which submesh (if you want to limit painting), and whether brush strokes mirror left/right.")) {
                WkStyles.LabeledField(
                    new GUIContent("Renderer",
                        "The SkinnedMeshRenderer to paint on. The mesh's UV0 layout determines where strokes land in the saved PNG."),
                    () => {
                        var prev = _renderer;
                        var next = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_renderer, typeof(SkinnedMeshRenderer), true);
                        if (next != prev) {
                            _renderer = next;
                            _submeshIndex = -1;
                            AutoDetectSymmetryRoot();
                            if (_painting) {
                                // Re-bake against the new renderer immediately so the user
                                // doesn't have to stop/start the session manually.
                                Diag(LogLevel.Info, $"Renderer swapped during paint session; rebaking. New target: {PathUtility.GetGameObjectPath(next != null ? next.gameObject : null)}");
                                Bake();
                            }
                        }
                    });

                if (_renderer != null) {
                    var mesh = _renderer.sharedMesh;
                    if (mesh == null) {
                        WkStyles.Notice(NoticeKind.Warning, "Renderer has no mesh assigned.");
                    } else if (!mesh.isReadable) {
                        if (WkStyles.Notice(NoticeKind.Warning,
                                "Mesh has Read/Write disabled in the importer; painting needs it for UV / triangle access.",
                                "Enable Read/Write & continue",
                                "Find the mesh's source asset, set Read/Write Enabled in the model importer, and reimport.")) {
                            EnableMeshReadWrite(mesh);
                        }
                    } else if (mesh.uv == null || mesh.uv.Length == 0) {
                        WkStyles.Notice(NoticeKind.Warning,
                            "Mesh has no UV0 channel. Painting requires UV0 to project strokes into texture space.");
                    } else {
                        DrawSubmeshPicker(mesh);
                    }
                }

                DrawSymmetryRow();
            }
        }

        private void DrawSubmeshPicker(Mesh mesh) {
            var options = new List<GUIContent>();
            options.Add(new GUIContent("All submeshes",
                "Paint affects every submesh. Use this when the renderer has one shared UV layout."));
            for (int s = 0; s < mesh.subMeshCount; s++) {
                string matName = "(no material)";
                if (_renderer != null && _renderer.sharedMaterials != null
                        && s < _renderer.sharedMaterials.Length
                        && _renderer.sharedMaterials[s] != null) {
                    matName = _renderer.sharedMaterials[s].name;
                }
                options.Add(new GUIContent($"{s}: {matName}",
                    "Limit painting to triangles in this submesh. Use when different submeshes share a UV space and you only want to mark one of them."));
            }
            // Re-validate selection -- submesh count can change when the user swaps the mesh underneath.
            if (_submeshIndex >= mesh.subMeshCount) _submeshIndex = -1;
            int displayIndex = _submeshIndex < 0 ? 0 : _submeshIndex + 1;
            WkStyles.LabeledField(
                new GUIContent("Submesh",
                    "Limit painting to a single submesh, or paint across all of them. Affects both the brush and the live preview overlay."),
                () => {
                    int next = EditorGUILayout.Popup(displayIndex, options.ToArray());
                    int newIndex = next == 0 ? -1 : next - 1;
                    if (newIndex != _submeshIndex) {
                        _submeshIndex = newIndex;
                        Diag(LogLevel.Trace, $"Submesh selector: {(newIndex < 0 ? "All" : newIndex.ToString())}.");
                    }
                });
        }

        private void DrawSymmetryRow() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool prev = _symmetryEnabled;
                _symmetryEnabled = EditorGUILayout.ToggleLeft(
                    new GUIContent("Symmetry (X mirror)",
                        "When on, every stroke also paints at the mirror position across the symmetry root's local X axis. Most VRChat avatars are symmetric, so painting one arm paints the other automatically. Toggle with X in the Scene view while painting."),
                    _symmetryEnabled, GUILayout.Width(180));
                if (prev != _symmetryEnabled) SaveEditorPrefs();

                using (new EditorGUI.DisabledScope(!_symmetryEnabled)) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Root",
                            "The transform whose local X axis defines the mirror plane. Auto-filled to the avatar root (Animator or VRCAvatarDescriptor parent). Override if your avatar's left/right axis is rotated relative to its root."),
                        GUILayout.Width(38));
                    _symmetryRoot = (Transform)EditorGUILayout.ObjectField(_symmetryRoot, typeof(Transform), true);
                }
            }
            if (_symmetryEnabled && _symmetryRoot == null && _renderer != null) {
                WkStyles.Notice(NoticeKind.Info,
                    "No symmetry root set; falling back to world-space X mirror. Works when the avatar sits at the world origin facing +Z.");
            }
        }

        private void DrawStartStopBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                bool canStart = CanStart();
                using (new EditorGUI.DisabledScope(!_painting && !canStart)) {
                    var label = _painting
                        ? new GUIContent("■  STOP PAINTING",
                            "End the paint session, release GPU resources, and stop receiving Scene view events. The current mask stays in memory until you Save or Clear.")
                        : new GUIContent("▶  START PAINTING",
                            "Bake a snapshot of the current deformed mesh, allocate the mask RenderTexture, and start receiving Scene view brush events. Stop and Save when done.");
                    if (WkStyles.PrimaryButtonInline(label, GUILayout.Height(34), GUILayout.MinWidth(180))) {
                        if (_painting) StopPainting(prompt: HasUnsavedWork());
                        else StartPainting();
                    }
                }
                if (_painting) {
                    GUILayout.FlexibleSpace();
                    var modeColor = _erase ? new Color(0.95f, 0.40f, 0.40f, 1f) : new Color(0.30f, 0.85f, 0.95f, 1f);
                    WkStyles.BadgePill(_erase ? "ERASE" : "PAINT", modeColor,
                        _erase ? "Strokes will reduce mask values toward zero. Press E in the Scene view to switch back."
                               : "Strokes will increase mask values toward one. Press E in the Scene view to erase.");
                }
            }
            if (_painting) {
                EditorGUILayout.LabelField(
                    "Scene view: LMB paints, [/] resizes brush, scroll-wheel-on-mesh resizes brush, X = symmetry, E = erase, Ctrl+Z = undo.",
                    WkStyles.Muted);
            }
        }

        private void DrawDriftNotice() {
            if (!_painting) return;
            if (!HasPoseDrift()) return;
            using (new EditorGUILayout.HorizontalScope()) {
                if (WkStyles.Notice(NoticeKind.Warning,
                        "Avatar pose / blendshapes changed since the last bake. Strokes will land in stale positions until you re-bake.",
                        "Re-bake",
                        "Re-snapshot the deformed mesh against the current pose. Existing mask content is preserved.")) {
                    Diag(LogLevel.Info, "Re-bake requested by drift notice.");
                    Bake();
                }
            }
        }

        private void DrawMaskSection() {
            using (WkStyles.Section("2. Mask",
                    "Output channel layout, texture resolution, and load/save controls.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("Mode",
                            "Grayscale paints all four channels equally - any channel of the saved PNG is the mask. " +
                            "Per-channel paints into only one of R/G/B/A, letting you pack several masks into one PNG."),
                        GUILayout.Width(WkStyles.LabelColumn));
                    var prevMode = _mode;
                    _mode = (MaskMode)EditorGUILayout.EnumPopup(_mode, GUILayout.Width(110));
                    if (prevMode != _mode) Diag(LogLevel.Trace, $"Mode -> {_mode}");
                    if (_mode == MaskMode.Channel) {
                        EditorGUILayout.LabelField(
                            new GUIContent("Channel",
                                "Which channel the brush writes into. The live preview overlay tints to match: R = red, G = green, B = blue, A = yellow."),
                            GUILayout.Width(60));
                        var prevChan = _channel;
                        _channel = (MaskChannel)EditorGUILayout.EnumPopup(_channel, GUILayout.Width(60));
                        if (prevChan != _channel) Diag(LogLevel.Trace, $"Channel -> {_channel}");
                    }
                }

                WkStyles.LabeledField(
                    new GUIContent("Resolution",
                        "Pixel size of the output mask (always square). Higher = sharper edges, more memory. 1024 is the sweet spot for body parts; bump to 2048 only if you can see the texel grid in your mask."),
                    () => {
                        var resolutions = new[] { 256, 512, 1024, 2048, 4096 };
                        var labels = new[] { "256", "512", "1024", "2048", "4096" };
                        int idx = Array.IndexOf(resolutions, _resolution);
                        if (idx < 0) idx = 2;
                        int next = EditorGUILayout.Popup(idx, labels, GUILayout.Width(80));
                        if (next != idx) {
                            int newRes = resolutions[next];
                            if (_painting) ChangeResolution(newRes);
                            else _resolution = newRes;
                            SaveEditorPrefs();
                        }
                    });

                using (new EditorGUILayout.HorizontalScope()) {
                    using (new EditorGUI.DisabledScope(_maskRT == null)) {
                        if (GUILayout.Button(
                                new GUIContent("Clear",
                                    "Wipe the mask to fully transparent (black, alpha=0). The current mask in the RT is pushed onto the undo stack first, so Ctrl+Z recovers it."),
                                GUILayout.Height(24))) {
                            ClearMask();
                        }
                    }
                    using (new EditorGUI.DisabledScope(_maskRT == null)) {
                        if (GUILayout.Button(
                                new GUIContent("Load PNG...",
                                    "Replace the current mask with a PNG from disk. The PNG is resampled to the current resolution."),
                                GUILayout.Height(24))) {
                            LoadMaskFromPng();
                        }
                    }
                    using (new EditorGUI.DisabledScope(_maskRT == null)) {
                        if (WkStyles.PrimaryButtonInline(
                                new GUIContent("Save PNG...",
                                    "Write the mask to disk as a PNG. Optionally dilates the painted regions outward to plug UV-island bleed (recommended)."),
                                GUILayout.Height(24))) {
                            SaveMaskToPng();
                        }
                    }
                }
            }
        }

        private void DrawBrushSection() {
            using (WkStyles.Section("3. Brush",
                    "Stroke shape and intensity. World-space radius keeps the brush footprint consistent regardless of UV stretch.")) {
                using (new EditorGUI.DisabledScope(!_painting)) {
                    // Brush radius slider with cm display
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField(
                            new GUIContent("Radius",
                                "Brush radius in world-space metres (NOT UV units). A 5 cm brush paints a 5 cm patch of skin regardless of how stretched the UVs are. Resize in the Scene view with [ / ] or scroll wheel."),
                            GUILayout.Width(WkStyles.LabelColumn));
                        float prev = _radius;
                        _radius = EditorGUILayout.Slider(_radius, 0.001f, 0.5f);
                        EditorGUILayout.LabelField($"{_radius * 100f:0.0} cm", WkStyles.Muted, GUILayout.Width(60));
                        if (!Mathf.Approximately(prev, _radius)) SaveEditorPrefs();
                    }

                    WkStyles.LabeledField(
                        new GUIContent("Strength",
                            "Opacity per stroke. 1 paints opaque white in one pass; lower values build up smoothly over multiple strokes."),
                        () => {
                            float prev = _strength;
                            _strength = EditorGUILayout.Slider(_strength, 0.01f, 1f);
                            if (!Mathf.Approximately(prev, _strength)) SaveEditorPrefs();
                        });
                    WkStyles.LabeledField(
                        new GUIContent("Hardness",
                            "0 = soft edge (linear falloff across the whole radius). 1 = hard edge (no falloff). Default 0.4 gives a natural soft brush like a paint app."),
                        () => {
                            float prev = _hardness;
                            _hardness = EditorGUILayout.Slider(_hardness, 0f, 1f);
                            if (!Mathf.Approximately(prev, _hardness)) SaveEditorPrefs();
                        });
                    using (new EditorGUILayout.HorizontalScope()) {
                        EditorGUILayout.LabelField(
                            new GUIContent("Mode",
                                "Paint adds toward the brush color; Erase removes toward zero. Toggle with E in the Scene view."),
                            GUILayout.Width(WkStyles.LabelColumn));
                        bool paint = !_erase;
                        if (GUILayout.Toggle(paint, new GUIContent("Paint", "Strokes increase mask values."),
                                EditorStyles.miniButtonLeft, GUILayout.Width(60))) _erase = false;
                        if (GUILayout.Toggle(_erase, new GUIContent("Erase", "Strokes decrease mask values toward zero."),
                                EditorStyles.miniButtonRight, GUILayout.Width(60))) _erase = true;
                    }

                    // Brush swatch -- a small visualisation of the brush
                    // size and softness so the user knows what each stroke
                    // looks like before they paint.
                    DrawBrushSwatch();
                }
            }
        }

        private void DrawBrushSwatch() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("Preview",
                        "Visual approximation of the brush footprint at the current strength and hardness."),
                    GUILayout.Width(WkStyles.LabelColumn));
                var rect = GUILayoutUtility.GetRect(64, 64, GUILayout.Width(64), GUILayout.Height(64));
                EditorGUI.DrawRect(rect, new Color(0.12f, 0.12f, 0.12f, 1f));
                if (Event.current.type == EventType.Repaint) {
                    DrawSwatchDisc(rect);
                }
                GUILayout.FlexibleSpace();
            }
        }

        private void DrawSwatchDisc(Rect rect) {
            // Soft disc approximation via concentric rings.
            var center = rect.center;
            float radius = Mathf.Min(rect.width, rect.height) * 0.5f - 2f;
            var brushColor = _erase ? new Color(0.95f, 0.30f, 0.30f) : new Color(0.30f, 0.85f, 0.95f);
            int rings = 16;
            for (int i = rings; i >= 1; i--) {
                float t = i / (float)rings;                          // 1 = outer ring, ~0 = center
                float d = t;                                          // distance from center, normalized
                float falloff = 1f - Mathf.SmoothStep(_hardness, 1f, d);
                float alpha = Mathf.Clamp01(falloff * _strength);
                if (alpha < 0.01f) continue;
                float r = radius * t;
                var ringColor = new Color(brushColor.r, brushColor.g, brushColor.b, alpha);
                EditorGUI.DrawRect(new Rect(center.x - r, center.y - r, r * 2f, r * 2f), ringColor);
            }
        }

        private void DrawPreviewSection() {
            using (WkStyles.Section("4. Preview",
                    "Current mask RT, with the active channel isolated when painting per-channel.")) {
                if (_maskRT == null) {
                    EditorGUILayout.LabelField("(start painting to allocate the mask buffer)", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                // Header row: stats + refresh
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField($"{_resolution}²  ·  {_strokeCount} stroke{(_strokeCount == 1 ? "" : "s")}  ·  {_dispatchCount} dispatch{(_dispatchCount == 1 ? "" : "es")}",
                        WkStyles.Muted, GUILayout.ExpandWidth(true));
                    if (_statsCoveragePct >= 0f) {
                        EditorGUILayout.LabelField($"coverage: {_statsCoveragePct:0.0}%", WkStyles.Muted, GUILayout.Width(110));
                    }
                    if (GUILayout.Button(
                            new GUIContent("Refresh stats",
                                "Read the mask back to CPU and compute coverage % (fraction of pixels with any value above 0.5%). Skip routinely; click only when you want a number."),
                            EditorStyles.miniButton, GUILayout.Width(96), GUILayout.Height(18))) {
                        RefreshCoverageStat();
                    }
                }
                EditorGUILayout.Space(2);
                // Centered preview rect, 300 x 300.
                const float previewSize = 300f;
                using (new EditorGUILayout.HorizontalScope()) {
                    GUILayout.FlexibleSpace();
                    var rect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
                    GUILayout.FlexibleSpace();
                    // Checker background so transparent / dark mask is visible.
                    DrawCheckerBg(rect);
                    if (_mode == MaskMode.Grayscale) {
                        GUI.DrawTexture(rect, _maskRT, ScaleMode.ScaleToFit, false);
                    } else {
                        var tint = Color.white;
                        switch (_channel) {
                            case MaskChannel.R: tint = new Color(1, 0.20f, 0.20f, 1); break;
                            case MaskChannel.G: tint = new Color(0.20f, 1, 0.30f, 1); break;
                            case MaskChannel.B: tint = new Color(0.30f, 0.55f, 1f, 1); break;
                            case MaskChannel.A: tint = new Color(1, 0.90f, 0.20f, 1); break;
                        }
                        GUI.DrawTexture(rect, _maskRT, ScaleMode.ScaleToFit, false, 1f, tint, 0f, 0f);
                    }
                    // Border
                    DrawRectBorder(rect, new Color(0.0f, 0.0f, 0.0f, 0.7f), 1);
                }
            }
        }

        private static void DrawCheckerBg(Rect rect) {
            if (Event.current.type != EventType.Repaint) return;
            const float cell = 12f;
            int cols = Mathf.CeilToInt(rect.width / cell);
            int rows = Mathf.CeilToInt(rect.height / cell);
            var a = new Color(0.16f, 0.16f, 0.16f, 1f);
            var b = new Color(0.22f, 0.22f, 0.22f, 1f);
            for (int y = 0; y < rows; y++) {
                for (int x = 0; x < cols; x++) {
                    var c = ((x + y) & 1) == 0 ? a : b;
                    var r = new Rect(rect.x + x * cell, rect.y + y * cell,
                                     Mathf.Min(cell, rect.xMax - (rect.x + x * cell)),
                                     Mathf.Min(cell, rect.yMax - (rect.y + y * cell)));
                    EditorGUI.DrawRect(r, c);
                }
            }
        }

        private static void DrawRectBorder(Rect r, Color color, float thickness) {
            if (Event.current.type != EventType.Repaint) return;
            EditorGUI.DrawRect(new Rect(r.x, r.y, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.yMax - thickness, r.width, thickness), color);
            EditorGUI.DrawRect(new Rect(r.x, r.y, thickness, r.height), color);
            EditorGUI.DrawRect(new Rect(r.xMax - thickness, r.y, thickness, r.height), color);
        }

        private void DrawAdvancedSection() {
            _advancedOpen = EditorGUILayout.Foldout(_advancedOpen,
                new GUIContent("Advanced",
                    "Save options, scene overlay toggles, diagnostics. Defaults are right for most masks."),
                true, WkStyles.FoldoutHeader);
            if (!_advancedOpen) return;
            using (WkStyles.Section("Save options",
                    "How the saved PNG is post-processed and re-imported.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    bool prev = _dilateOnSave;
                    _dilateOnSave = EditorGUILayout.ToggleLeft(
                        new GUIContent("Dilate on save",
                            "Bleed painted pixels N steps outward into empty UV-island gutter at save time. Almost always wanted - prevents black halos when the shader bilinear-samples near a UV island edge."),
                        _dilateOnSave, GUILayout.Width(180));
                    if (prev != _dilateOnSave) SaveEditorPrefs();
                    using (new EditorGUI.DisabledScope(!_dilateOnSave)) {
                        int prevIter = _dilationIterations;
                        _dilationIterations = EditorGUILayout.IntSlider(_dilationIterations, 1, 16);
                        if (prevIter != _dilationIterations) SaveEditorPrefs();
                    }
                }
                bool prevSRGB = _sRGBOnSave;
                _sRGBOnSave = EditorGUILayout.ToggleLeft(
                    new GUIContent("Import as sRGB",
                        "Off for masks (this is data, not photographic colour). Turn on only if the mask will be used as an albedo / colour input."),
                    _sRGBOnSave);
                if (prevSRGB != _sRGBOnSave) SaveEditorPrefs();
            }
            using (WkStyles.Section("Scene overlay",
                    "Toggle the in-scene HUD, hotkey strip, and symmetry plane visualisation.")) {
                bool prevHud = _showSceneHud;
                _showSceneHud = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show HUD (top-left)",
                        "Floating panel in the Scene view showing target, brush settings, and live counters."),
                    _showSceneHud);
                if (prevHud != _showSceneHud) { SaveEditorPrefs(); SceneView.RepaintAll(); }

                bool prevHints = _showHotkeyStrip;
                _showHotkeyStrip = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show hotkey hint strip (bottom)",
                        "Floating bar at the bottom of the Scene view showing keyboard shortcuts."),
                    _showHotkeyStrip);
                if (prevHints != _showHotkeyStrip) { SaveEditorPrefs(); SceneView.RepaintAll(); }

                bool prevPlane = _showSymmetryPlane;
                _showSymmetryPlane = EditorGUILayout.ToggleLeft(
                    new GUIContent("Show symmetry plane",
                        "Draw a faint quad at the symmetry root's local YZ plane so you can verify the mirror is where you expect."),
                    _showSymmetryPlane);
                if (prevPlane != _showSymmetryPlane) { SaveEditorPrefs(); SceneView.RepaintAll(); }
            }
            using (WkStyles.Section("Diagnostics",
                    "Visibility into the painting pipeline. Turn Verbose on if something isn't behaving and you want every state transition mirrored to the Console.")) {
                bool prev = _verboseLog;
                _verboseLog = EditorGUILayout.ToggleLeft(
                    new GUIContent("Verbose log (mirror trace to Console)",
                        "Off: trace lines go to the session log file only. On: they also appear in the Unity Console. State transitions (start, stop, save) always go to Console regardless."),
                    _verboseLog);
                if (prev != _verboseLog) SaveEditorPrefs();

                using (new EditorGUILayout.HorizontalScope()) {
                    if (GUILayout.Button(
                            new GUIContent("Dump state",
                                "Print the current tool state (target, baked snapshot, RT, undo stack, brush settings, counters) to the Unity console."),
                            GUILayout.Height(22))) {
                        DumpState();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Open log folder",
                                "Open the per-package session log directory in the system file browser."),
                            GUILayout.Height(22))) {
                        OpenLogFolder();
                    }
                }
            }
        }

        // ---- Capabilities ----

        private bool CanStart() {
            if (_renderer == null) return false;
            var mesh = _renderer.sharedMesh;
            return mesh != null && mesh.isReadable && mesh.uv != null && mesh.uv.Length > 0;
        }

        private bool HasUnsavedWork() {
            // We can't easily detect "modified" without snapshotting. A
            // proxy: if a mask exists, ask. Cheap and safe.
            return _maskRT != null;
        }

        // ---- Painting session ----

        private void StartPainting() {
            if (!CanStart()) return;
            if (MaskPainterIO.BrushShader == null || MaskPainterIO.PreviewShader == null) {
                EditorUtility.DisplayDialog("Paint Mask",
                    "Shaders failed to load. Reimport the package and try again. See the package log for details.", "OK");
                Diag(LogLevel.Warn, "StartPainting aborted: shaders failed to load.");
                return;
            }
            if (!EditorPrefs.GetBool(PrefsReloadWarningShown, false)) {
                EditorUtility.DisplayDialog("Paint Mask",
                    "Heads up: the in-progress mask lives in GPU memory only. " +
                    "It does NOT survive Unity domain reloads (script changes, etc.) - " +
                    "so save to a PNG before recompiling. This message won't show again.", "Got it");
                EditorPrefs.SetBool(PrefsReloadWarningShown, true);
            }

            _maskRT = MaskPainterIO.CreateMaskRT(_resolution);
            _brushMaterial = new Material(MaskPainterIO.BrushShader) { hideFlags = HideFlags.HideAndDontSave };
            _previewMaterial = new Material(MaskPainterIO.PreviewShader) { hideFlags = HideFlags.HideAndDontSave };

            Bake();

            SceneView.duringSceneGui -= OnSceneGui;
            SceneView.duringSceneGui += OnSceneGui;
            _painting = true;
            _strokeCount = 0;
            _dispatchCount = 0;
            _firstHitLogged = false;
            HideToolsForPainting();

            // Enable MouseMove dispatch on every existing scene view. Newly
            // created scene views default to false; we re-enable per call
            // inside OnSceneGui as a belt-and-braces.
            foreach (SceneView sv in SceneView.sceneViews) {
                if (sv != null) {
                    sv.wantsMouseMove = true;
                    sv.wantsMouseEnterLeaveWindow = true;
                    sv.Repaint();
                }
            }

            var mesh = _renderer.sharedMesh;
            Diag(LogLevel.Info,
                $"Paint session started.\n" +
                $"  target          : {PathUtility.GetGameObjectPath(_renderer.gameObject)}\n" +
                $"  source mesh     : {(mesh != null ? mesh.name : "(null)")}, verts={(mesh != null ? mesh.vertexCount : 0)}, submeshes={(mesh != null ? mesh.subMeshCount : 0)}, hasUV={(mesh != null && mesh.uv != null && mesh.uv.Length > 0)}, blendshapes={(mesh != null ? mesh.blendShapeCount : 0)}\n" +
                $"  baked snapshot  : verts={(_snapshotMesh != null ? _snapshotMesh.vertexCount : 0)}, submeshes={(_snapshotMesh != null ? _snapshotMesh.subMeshCount : 0)}, worldBounds=({_snapshotWorldBounds.min}..{_snapshotWorldBounds.max})\n" +
                $"  submesh filter  : {(_submeshIndex < 0 ? "All" : _submeshIndex.ToString())}\n" +
                $"  mask buffer     : {_resolution}x{_resolution} ARGB32 linear\n" +
                $"  symmetry        : {_symmetryEnabled} (root: {(_symmetryRoot != null ? PathUtility.GetGameObjectPath(_symmetryRoot.gameObject) : "world")})\n" +
                $"  brush           : radius={_radius:F4}m, strength={_strength:F2}, hardness={_hardness:F2}, mode={(_erase ? "ERASE" : "PAINT")}\n" +
                $"  output mode     : {(_mode == MaskMode.Grayscale ? "Grayscale (all channels)" : $"Channel {_channel}")}\n" +
                $"  brush shader    : {(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.name : "(null)")} passes={(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.passCount.ToString() : "0")}\n" +
                $"  preview shader  : {(_previewMaterial != null && _previewMaterial.shader != null ? _previewMaterial.shader.name : "(null)")}\n" +
                $"  scene views     : {SceneView.sceneViews.Count} (wantsMouseMove enabled on each)");
            SceneView.RepaintAll();
        }

        private void StopPainting(bool prompt) {
            if (prompt && HasUnsavedWork()) {
                if (!EditorUtility.DisplayDialog("Paint Mask",
                        "Stop painting? The in-progress mask is in GPU memory only - it'll be lost unless you Save PNG first.",
                        "Stop without saving", "Cancel")) {
                    return;
                }
            }
            int finalStrokes = _strokeCount;
            int finalDispatches = _dispatchCount;
            SceneView.duringSceneGui -= OnSceneGui;
            ReleaseGpuResources();
            RestoreToolsState();
            _painting = false;
            SceneView.RepaintAll();
            Diag(LogLevel.Info,
                $"Paint session stopped. Totals: {finalStrokes} stroke(s), {finalDispatches} dispatch(es).");
        }

        private void Bake() {
            if (_renderer == null) return;
            if (_snapshotMesh == null) {
                _snapshotMesh = new Mesh { name = "WhyKnotMaskPainter_Snapshot", hideFlags = HideFlags.HideAndDontSave };
            }
            _renderer.BakeMesh(_snapshotMesh);
            var verts = _snapshotMesh.vertices;
            var matrix = _renderer.transform.localToWorldMatrix;
            _snapshotWorldVerts = new Vector3[verts.Length];
            if (verts.Length > 0) {
                var first = matrix.MultiplyPoint3x4(verts[0]);
                _snapshotWorldBounds = new Bounds(first, Vector3.zero);
                _snapshotWorldVerts[0] = first;
                for (int i = 1; i < verts.Length; i++) {
                    var p = matrix.MultiplyPoint3x4(verts[i]);
                    _snapshotWorldVerts[i] = p;
                    _snapshotWorldBounds.Encapsulate(p);
                }
            } else {
                _snapshotWorldBounds = new Bounds();
            }

            var mesh = _renderer.sharedMesh;
            int shapeCount = mesh != null ? mesh.blendShapeCount : 0;
            _bakedBlendShapes = new float[shapeCount];
            for (int i = 0; i < shapeCount; i++) _bakedBlendShapes[i] = _renderer.GetBlendShapeWeight(i);
            _renderer.transform.hasChanged = false;
            _firstHitLogged = false;
            Diag(LogLevel.Trace,
                $"Bake complete: verts={verts.Length}, submeshes={_snapshotMesh.subMeshCount}, worldBounds=({_snapshotWorldBounds.min} .. {_snapshotWorldBounds.max}), size={_snapshotWorldBounds.size}");
        }

        private bool HasPoseDrift() {
            if (_renderer == null || _snapshotMesh == null) return false;
            if (_renderer.transform.hasChanged) return true;
            var mesh = _renderer.sharedMesh;
            if (mesh == null || _bakedBlendShapes == null) return false;
            int n = Mathf.Min(mesh.blendShapeCount, _bakedBlendShapes.Length);
            for (int i = 0; i < n; i++) {
                if (!Mathf.Approximately(_renderer.GetBlendShapeWeight(i), _bakedBlendShapes[i])) return true;
            }
            return mesh.blendShapeCount != _bakedBlendShapes.Length;
        }

        private void AutoDetectSymmetryRoot() {
            if (_renderer == null) { _symmetryRoot = null; return; }
            var root = AvatarUtility.FindAvatarRoot(_renderer);
            _symmetryRoot = root != null ? root.transform : null;
        }

        private void ChangeResolution(int newRes) {
            if (newRes == _resolution || _maskRT == null) return;
            var old = _maskRT;
            _maskRT = MaskPainterIO.CreateMaskRT(newRes);
            Graphics.Blit(old, _maskRT);
            old.Release();
            DestroyImmediate(old);
            _resolution = newRes;
            // Undo entries are bound to the old resolution; flush them so an
            // undo doesn't try to ReadPixels into a mismatched RT.
            foreach (var t in _undoStack) if (t != null) DestroyImmediate(t);
            _undoStack.Clear();
            _statsCoveragePct = -1f;
            Diag(LogLevel.Info, $"Resolution -> {newRes} (undo stack flushed).");
        }

        private void HideToolsForPainting() {
            if (_toolsHiddenByUs) return;
            _previousToolsHidden = UnityEditor.Tools.hidden;
            _previousTool = UnityEditor.Tools.current;
            UnityEditor.Tools.hidden = true;
            _toolsHiddenByUs = true;
        }

        private void RestoreToolsState() {
            if (!_toolsHiddenByUs) return;
            UnityEditor.Tools.hidden = _previousToolsHidden;
            _toolsHiddenByUs = false;
        }

        // ---- Stroke dispatch ----

        private int PassForMode() {
            if (_mode == MaskMode.Grayscale) return 0;
            return 1 + (int)_channel; // R=1, G=2, B=3, A=4
        }

        private void ApplyStroke() {
            if (_maskRT == null || _brushMaterial == null || _snapshotMesh == null || _renderer == null) return;
            var prev = RenderTexture.active;
            try {
                Graphics.SetRenderTarget(_maskRT);
                _brushMaterial.SetVector("_BrushCenter", _hitWorld);
                if (_symmetryEnabled) {
                    var mirror = MaskPainterIO.MirrorAcrossLocalX(_hitWorld, _symmetryRoot);
                    _brushMaterial.SetVector("_MirrorBrushCenter", mirror);
                    _brushMaterial.SetFloat("_SymmetryEnabled", 1f);
                } else {
                    _brushMaterial.SetFloat("_SymmetryEnabled", 0f);
                }
                _brushMaterial.SetFloat("_BrushRadius",   _radius);
                _brushMaterial.SetFloat("_BrushHardness", _hardness);
                _brushMaterial.SetFloat("_Strength",      _strength);
                _brushMaterial.SetColor("_BrushColor",    _erase ? new Color(0, 0, 0, 1) : Color.white);
                _brushMaterial.SetPass(PassForMode());

                var matrix = _renderer.transform.localToWorldMatrix;
                if (_submeshIndex < 0) {
                    for (int s = 0; s < _snapshotMesh.subMeshCount; s++) {
                        Graphics.DrawMeshNow(_snapshotMesh, matrix, s);
                    }
                } else if (_submeshIndex < _snapshotMesh.subMeshCount) {
                    Graphics.DrawMeshNow(_snapshotMesh, matrix, _submeshIndex);
                }
            } finally {
                RenderTexture.active = prev;
            }
            _lastStrokeTime = EditorApplication.timeSinceStartup;
            _dispatchCount++;
            _strokeDispatches++;
            if (_dispatchCount == 1) {
                Diag(LogLevel.Info,
                    $"First stroke dispatched. center={_hitWorld}, radius={_radius:F4}m, mode={(_erase ? "erase" : "paint")}, pass={PassForMode()}, submeshes={(_submeshIndex < 0 ? "all" : _submeshIndex.ToString())}");
            }
            Repaint();
        }

        private bool CanDispatch() {
            return EditorApplication.timeSinceStartup - _lastStrokeTime >= StrokeIntervalSec;
        }

        // ---- Undo ----

        private void PushUndo() {
            if (_maskRT == null) return;
            var tex = SnapshotRT();
            _undoStack.Add(tex);
            int cap = UndoCapForResolution(_resolution);
            while (_undoStack.Count > cap) {
                if (_undoStack[0] != null) DestroyImmediate(_undoStack[0]);
                _undoStack.RemoveAt(0);
            }
        }

        private void UndoLast() {
            if (_undoStack.Count == 0 || _maskRT == null) return;
            var tex = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);
            if (tex != null) {
                Graphics.Blit(tex, _maskRT);
                DestroyImmediate(tex);
            }
            Repaint();
            SceneView.RepaintAll();
            Diag(LogLevel.Trace, $"Undo: {_undoStack.Count} snapshot(s) remaining in stack.");
        }

        private Texture2D SnapshotRT() {
            var tex = new Texture2D(_maskRT.width, _maskRT.height, TextureFormat.RGBA32, false, true) {
                hideFlags = HideFlags.HideAndDontSave,
            };
            var prev = RenderTexture.active;
            RenderTexture.active = _maskRT;
            try {
                tex.ReadPixels(new Rect(0, 0, _maskRT.width, _maskRT.height), 0, 0);
                tex.Apply(false, false);
            } finally {
                RenderTexture.active = prev;
            }
            return tex;
        }

        private static int UndoCapForResolution(int res) {
            // Memory-bounded: each Texture2D is res*res*4 bytes.
            // 256/512 -> 20, 1024 -> 10, 2048 -> 5, 4096 -> 3.
            if (res <= 512)  return 20;
            if (res <= 1024) return 10;
            if (res <= 2048) return 5;
            return 3;
        }

        // ---- Mask ops ----

        private void ClearMask() {
            if (_maskRT == null) return;
            PushUndo();
            MaskPainterIO.ClearRT(_maskRT, Color.clear);
            _statsCoveragePct = -1f;
            Diag(LogLevel.Info, "Mask cleared.");
            Repaint();
            SceneView.RepaintAll();
        }

        private void SaveMaskToPng() {
            if (_maskRT == null) return;
            string folder = ResolveDefaultSaveFolder();
            string suggested = DefaultFilename();
            string path = EditorUtility.SaveFilePanel("Save mask PNG", folder, suggested, "png");
            if (string.IsNullOrEmpty(path)) return;
            bool ok = MaskPainterIO.SavePng(_maskRT, new MaskPainterIO.SaveOptions {
                Path               = path,
                Dilate             = _dilateOnSave,
                DilationIterations = _dilationIterations,
                SRGB               = _sRGBOnSave,
            });
            if (ok) _lastSavedPath = path;
        }

        private void LoadMaskFromPng() {
            if (_maskRT == null) return;
            string folder = ResolveDefaultSaveFolder();
            string path = EditorUtility.OpenFilePanel("Load mask PNG", folder, "png");
            if (string.IsNullOrEmpty(path)) return;
            PushUndo();
            MaskPainterIO.LoadPng(path, _maskRT);
            _statsCoveragePct = -1f;
            Repaint();
            SceneView.RepaintAll();
        }

        private string ResolveDefaultSaveFolder() {
            if (!string.IsNullOrEmpty(_lastSavedPath)) {
                var dir = System.IO.Path.GetDirectoryName(_lastSavedPath);
                if (!string.IsNullOrEmpty(dir) && System.IO.Directory.Exists(dir)) return dir;
            }
            if (_renderer != null) {
                var mesh = _renderer.sharedMesh;
                if (mesh != null) {
                    var assetPath = AssetDatabase.GetAssetPath(mesh);
                    if (!string.IsNullOrEmpty(assetPath)) {
                        var dir = System.IO.Path.GetDirectoryName(assetPath);
                        if (!string.IsNullOrEmpty(dir)) return dir;
                    }
                }
            }
            return Application.dataPath;
        }

        private string DefaultFilename() {
            string baseName = "Mask";
            if (_renderer != null && _renderer.sharedMesh != null) baseName = _renderer.sharedMesh.name;
            if (_mode == MaskMode.Channel) baseName = $"{baseName}_{_channel}";
            return $"{baseName}_Mask.png";
        }

        private static void EnableMeshReadWrite(Mesh mesh) {
            if (mesh == null) return;
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) {
                AvatarQolLogger.Instance.Warning(
                    "Mesh has no asset path; can't auto-enable Read/Write. " +
                    "Re-import the source asset with Read/Write Enabled in the model importer.");
                return;
            }
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) {
                AvatarQolLogger.Instance.Warning(
                    $"Mesh at {path} has no ModelImporter; can't auto-enable Read/Write.");
                return;
            }
            importer.isReadable = true;
            importer.SaveAndReimport();
            AvatarQolLogger.Instance.Info($"Read/Write enabled on {path}.");
        }

        // ---- Stats ----

        private void RefreshCoverageStat() {
            if (_maskRT == null) return;
            var tex = SnapshotRT();
            try {
                var pixels = tex.GetPixels32();
                int total = pixels.Length;
                int covered = 0;
                // For grayscale mode the R channel carries the value; for
                // per-channel mode we check the active channel.
                int channelIndex = _mode == MaskMode.Grayscale ? 0 : (int)_channel;
                byte threshold = 2; // > ~0.8%
                for (int i = 0; i < total; i++) {
                    byte v;
                    switch (channelIndex) {
                        case 0: v = pixels[i].r; break;
                        case 1: v = pixels[i].g; break;
                        case 2: v = pixels[i].b; break;
                        default: v = pixels[i].a; break;
                    }
                    if (v >= threshold) covered++;
                }
                _statsCoveragePct = total > 0 ? (covered * 100f / total) : 0f;
                _statsRefreshedAt = EditorApplication.timeSinceStartup;
                Diag(LogLevel.Info,
                    $"Coverage refreshed: {_statsCoveragePct:0.00}% ({covered:N0} / {total:N0} pixels above threshold on channel {channelIndex}).");
            } finally {
                DestroyImmediate(tex);
            }
        }

        // ---- Scene view ----

        private void OnSceneGui(SceneView sv) {
            if (!_painting) return;
            if (_renderer == null) {
                Diag(LogLevel.Warn, "Renderer was destroyed during paint session; stopping.");
                StopPainting(prompt: false);
                return;
            }
            // Belt-and-braces: newly-opened scene views default
            // wantsMouseMove=false. Keep both flags asserted while painting.
            sv.wantsMouseMove = true;
            sv.wantsMouseEnterLeaveWindow = true;

            Event e = Event.current;
            int controlID = GUIUtility.GetControlID(FocusType.Passive);

            // Picking: re-raycast whenever the mouse position has changed.
            // Runs on Layout/Repaint too because mousePosition is always
            // current. The change-detection gate keeps cost down -- a
            // 50k-tri raycast costs ~1ms, fine at hover but wasteful 60Hz.
            if (e.mousePosition != _lastMousePos) {
                var ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
                bool prevHit = _hasHit;
                UpdateRaycast(ray);
                if (_hasHit != prevHit) {
                    Diag(LogLevel.Trace,
                        _hasHit
                            ? $"Raycast HIT at {_hitWorld} (was miss). mouse={e.mousePosition}"
                            : $"Raycast miss (was hit). mouse={e.mousePosition}");
                }
                if (_hasHit && !_firstHitLogged) {
                    _firstHitLogged = true;
                    Diag(LogLevel.Info,
                        $"First raycast hit of session: world={_hitWorld}, normal={_hitNormal}, snapshotBounds=({_snapshotWorldBounds.min}..{_snapshotWorldBounds.max}).");
                }
                _lastMousePos = e.mousePosition;
                sv.Repaint();
            }

            // Repaint phase: draw overlays.
            if (e.type == EventType.Repaint) {
                DrawPreviewOverlay(sv);
                if (_hasHit) DrawBrushDisc(sv);
                if (_showSymmetryPlane && _symmetryEnabled) DrawSymmetryPlane(sv);
                if (_showSceneHud) DrawSceneHud(sv);
                if (_showHotkeyStrip) DrawHotkeyStrip(sv);
                if (!_hasHit) DrawOffMeshIndicator(sv, e.mousePosition);
            }

            switch (e.type) {
                case EventType.Layout:
                    HandleUtility.AddDefaultControl(controlID);
                    break;

                case EventType.MouseDown:
                    if (e.button == 0) {
                        Diag(LogLevel.Trace, $"MouseDown received. _hasHit={_hasHit}, mouse={e.mousePosition}");
                        if (_hasHit) {
                            _strokeInProgress = true;
                            _strokeDispatches = 0;
                            PushUndo();
                            ApplyStroke();
                            e.Use();
                        }
                    }
                    break;

                case EventType.MouseDrag:
                    if (e.button == 0 && _strokeInProgress && _hasHit && CanDispatch()) {
                        ApplyStroke();
                        e.Use();
                    }
                    break;

                case EventType.MouseUp:
                    if (e.button == 0 && _strokeInProgress) {
                        _strokeInProgress = false;
                        _strokeCount++;
                        Diag(LogLevel.Trace, $"Stroke complete. dispatches in stroke={_strokeDispatches}.");
                        Repaint();
                        e.Use();
                    }
                    break;

                case EventType.KeyDown:
                    if (HandleHotkey(e)) e.Use();
                    break;

                case EventType.ScrollWheel:
                    if (_hasHit) {
                        // Wheel up = smaller, wheel down = larger (matches most paint apps).
                        float factor = e.delta.y > 0 ? 1.1f : 0.9f;
                        _radius = Mathf.Clamp(_radius * factor, 0.001f, 1f);
                        SaveEditorPrefs();
                        sv.Repaint();
                        Repaint();
                        e.Use();
                    }
                    break;
            }
        }

        private bool HandleHotkey(Event e) {
            switch (e.keyCode) {
                case KeyCode.LeftBracket:
                    _radius = Mathf.Clamp(_radius * 0.9f, 0.001f, 1f);
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.RightBracket:
                    _radius = Mathf.Clamp(_radius * 1.1f, 0.001f, 1f);
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.X:
                    _symmetryEnabled = !_symmetryEnabled;
                    SaveEditorPrefs();
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.E:
                    _erase = !_erase;
                    SceneView.RepaintAll();
                    Repaint();
                    return true;
                case KeyCode.Z:
                    if (e.control || e.command) {
                        UndoLast();
                        return true;
                    }
                    return false;
            }
            return false;
        }

        private void UpdateRaycast(Ray ray) {
            _hasHit = false;
            if (_snapshotMesh == null || _snapshotWorldVerts == null) return;

            float bestT = float.PositiveInfinity;
            int bestI0 = 0, bestI1 = 0, bestI2 = 0;

            int subStart = _submeshIndex < 0 ? 0 : _submeshIndex;
            int subEnd   = _submeshIndex < 0 ? _snapshotMesh.subMeshCount : Mathf.Min(_submeshIndex + 1, _snapshotMesh.subMeshCount);

            for (int s = subStart; s < subEnd; s++) {
                var tris = _snapshotMesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    if (MaskPainterIO.RayTriangle(ray.origin, ray.direction,
                            _snapshotWorldVerts[i0], _snapshotWorldVerts[i1], _snapshotWorldVerts[i2],
                            out float t, out _, out _)
                            && t < bestT) {
                        bestT = t;
                        bestI0 = i0; bestI1 = i1; bestI2 = i2;
                    }
                }
            }
            if (bestT < float.PositiveInfinity) {
                _hasHit = true;
                _hitWorld = ray.origin + ray.direction * bestT;
                var e1 = _snapshotWorldVerts[bestI1] - _snapshotWorldVerts[bestI0];
                var e2 = _snapshotWorldVerts[bestI2] - _snapshotWorldVerts[bestI0];
                _hitNormal = Vector3.Cross(e1, e2).normalized;
                // The triangle's natural normal may face into the body if
                // the mesh has inverted winding. Flip toward the camera so
                // the brush disc and overlay sit on the correct side.
                if (Vector3.Dot(_hitNormal, ray.direction) > 0f) _hitNormal = -_hitNormal;
            }
        }

        // ---- Scene drawing helpers ----

        private void DrawBrushDisc(SceneView sv) {
            var paintColor = _erase ? new Color(1f, 0.30f, 0.30f, 1f) : new Color(0.30f, 0.85f, 1f, 1f);
            var prev = Handles.color;

            // Outer ring (radius) - solid
            Handles.color = paintColor;
            Handles.DrawWireDisc(_hitWorld, _hitNormal, _radius);
            // Inner hardness ring - dotted feel via second pass at half alpha
            if (_hardness > 0.01f && _hardness < 0.99f) {
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
                Handles.DrawWireDisc(_hitWorld, _hitNormal, _radius * _hardness);
            }
            // Center cross + dot
            Handles.color = paintColor;
            var size = HandleUtility.GetHandleSize(_hitWorld) * 0.04f;
            Handles.DrawSolidDisc(_hitWorld, _hitNormal, size * 0.4f);
            // Normal indicator (short stick out of the hit point)
            Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
            Handles.DrawLine(_hitWorld, _hitWorld + _hitNormal * (_radius * 0.5f));

            // Mirror disc
            if (_symmetryEnabled) {
                var mirror = MaskPainterIO.MirrorAcrossLocalX(_hitWorld, _symmetryRoot);
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.7f);
                Handles.DrawWireDisc(mirror, _hitNormal, _radius);
                if (_hardness > 0.01f && _hardness < 0.99f) {
                    Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.3f);
                    Handles.DrawWireDisc(mirror, _hitNormal, _radius * _hardness);
                }
                Handles.color = new Color(paintColor.r, paintColor.g, paintColor.b, 0.45f);
                Handles.DrawDottedLine(_hitWorld, mirror, 4f);
            }
            Handles.color = prev;
        }

        private void DrawSymmetryPlane(SceneView sv) {
            if (_symmetryRoot == null) return;
            var fwd = _symmetryRoot.forward;
            var up  = _symmetryRoot.up;
            var ctr = _symmetryRoot.position;
            float size = Mathf.Max(0.5f, _snapshotWorldBounds.size.magnitude * 0.7f);
            var c00 = ctr + (-fwd - up) * (size * 0.5f);
            var c01 = ctr + (-fwd + up) * (size * 0.5f);
            var c11 = ctr + ( fwd + up) * (size * 0.5f);
            var c10 = ctr + ( fwd - up) * (size * 0.5f);
            var face = new Color(0.30f, 0.85f, 1f, 0.06f);
            var edge = new Color(0.30f, 0.85f, 1f, 0.55f);
            Handles.DrawSolidRectangleWithOutline(new[] { c00, c01, c11, c10 }, face, edge);
        }

        private void DrawPreviewOverlay(SceneView sv) {
            if (_previewMaterial == null || _maskRT == null || _snapshotMesh == null || _renderer == null) return;
            _previewMaterial.SetTexture("_MaskTex", _maskRT);
            _previewMaterial.SetVector("_ChannelMask", ChannelMaskVector());
            _previewMaterial.SetColor("_TintColor", TintColorForChannel());
            _previewMaterial.SetFloat("_TintAlpha", 0.55f);

            // Tiny scale-up plus shader Offset -1, -1 wins the depth fight
            // against the real SkinnedMeshRenderer without visible inflation.
            var m = _renderer.transform.localToWorldMatrix * Matrix4x4.Scale(new Vector3(1.001f, 1.001f, 1.001f));
            int subStart = _submeshIndex < 0 ? 0 : _submeshIndex;
            int subEnd   = _submeshIndex < 0 ? _snapshotMesh.subMeshCount : Mathf.Min(_submeshIndex + 1, _snapshotMesh.subMeshCount);
            for (int s = subStart; s < subEnd; s++) {
                Graphics.DrawMesh(_snapshotMesh, m, _previewMaterial, 0, sv.camera, s);
            }
        }

        // Floating HUD top-left of the Scene view. Shows live tool state.
        private void DrawSceneHud(SceneView sv) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                const float w = 270f;
                const float h = 132f;
                var rect = new Rect(10, 10, w, h);
                GUI.Box(rect, GUIContent.none, _hudBoxStyle);
                float y = rect.y + 6;
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 18),
                    "●  MASK PAINTER", _hudHeaderStyle);
                y += 20;
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16),
                    RendererSummary(), _hudLabelStyle);
                y += 18;
                string brushLine = $"brush  {_radius * 100f:0.0} cm  ·  str {_strength:0.00}  ·  hard {_hardness:0.00}";
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), brushLine, _hudLabelStyle);
                y += 18;
                string modeLine = $"mode   {(_erase ? "ERASE" : "PAINT")}  ·  {(_mode == MaskMode.Grayscale ? "grayscale" : _channel.ToString() + " channel")}  ·  sym {(_symmetryEnabled ? "on" : "off")}";
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), modeLine, _hudLabelStyle);
                y += 18;
                string statLine = $"strokes {_strokeCount}  ·  dispatches {_dispatchCount}  ·  {(_hasHit ? "ON MESH" : "off mesh")}";
                var statStyle = _hudLabelStyle;
                if (!_hasHit) {
                    statStyle = new GUIStyle(_hudLabelStyle) { normal = { textColor = new Color(0.95f, 0.65f, 0.20f) } };
                }
                GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), statLine, statStyle);
                y += 18;
                if (_statsCoveragePct >= 0f) {
                    GUI.Label(new Rect(rect.x + 10, y, w - 20, 16), $"coverage {_statsCoveragePct:0.0}%", _hudHintStyle);
                }
            } finally {
                Handles.EndGUI();
            }
        }

        private void DrawHotkeyStrip(SceneView sv) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                var hint = "LMB paint   ·   [ / ]  size   ·   scroll  size   ·   X  symmetry   ·   E  erase   ·   Ctrl+Z  undo";
                var pos = sv.position;
                var rect = new Rect(0, pos.height - 28, pos.width, 22);
                GUI.Box(rect, GUIContent.none, _hudBoxStyle);
                var inset = new Rect(rect.x + 12, rect.y + 2, rect.width - 24, rect.height - 4);
                GUI.Label(inset, hint, _hudHintStyle);
            } finally {
                Handles.EndGUI();
            }
        }

        private void DrawOffMeshIndicator(SceneView sv, Vector2 mousePos) {
            EnsureHudStyles();
            Handles.BeginGUI();
            try {
                var rect = new Rect(mousePos.x + 14, mousePos.y + 6, 100, 18);
                var style = new GUIStyle(_hudHintStyle) {
                    alignment = TextAnchor.MiddleLeft,
                    normal = { textColor = new Color(0.95f, 0.65f, 0.20f, 0.95f) },
                };
                GUI.Label(rect, "(off mesh)", style);
            } finally {
                Handles.EndGUI();
            }
        }

        private void EnsureHudStyles() {
            if (_hudBoxStyle != null) return;
            _hudBoxStyle = new GUIStyle(GUI.skin.box) {
                normal = { background = MakeSolidTexture(new Color(0.08f, 0.08f, 0.08f, 0.78f)) },
            };
            _hudLabelStyle = new GUIStyle(EditorStyles.label) {
                normal = { textColor = new Color(0.92f, 0.92f, 0.92f, 1f) },
                fontSize = 11,
            };
            _hudHeaderStyle = new GUIStyle(_hudLabelStyle) {
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.40f, 0.90f, 1f, 1f) },
            };
            _hudHintStyle = new GUIStyle(EditorStyles.miniLabel) {
                normal = { textColor = new Color(0.85f, 0.85f, 0.85f, 0.95f) },
                fontSize = 10,
            };
        }

        private static Texture2D MakeSolidTexture(Color c) {
            var t = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            t.SetPixel(0, 0, c);
            t.Apply();
            return t;
        }

        private Vector4 ChannelMaskVector() {
            if (_mode == MaskMode.Grayscale) return new Vector4(1, 0, 0, 0);
            switch (_channel) {
                case MaskChannel.R: return new Vector4(1, 0, 0, 0);
                case MaskChannel.G: return new Vector4(0, 1, 0, 0);
                case MaskChannel.B: return new Vector4(0, 0, 1, 0);
                case MaskChannel.A: return new Vector4(0, 0, 0, 1);
            }
            return new Vector4(1, 0, 0, 0);
        }

        private Color TintColorForChannel() {
            if (_mode == MaskMode.Grayscale) return new Color(1f, 0.55f, 0.10f, 1f); // orange
            switch (_channel) {
                case MaskChannel.R: return new Color(1f, 0.20f, 0.20f, 1f);
                case MaskChannel.G: return new Color(0.20f, 1f, 0.30f, 1f);
                case MaskChannel.B: return new Color(0.30f, 0.55f, 1f, 1f);
                case MaskChannel.A: return new Color(1f, 0.90f, 0.20f, 1f);
            }
            return Color.white;
        }

        // ---- Diagnostics: dump state ----

        private void DumpState() {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Mask Painter state dump:");
            sb.AppendLine($"  painting              : {_painting}");
            sb.AppendLine($"  renderer              : {(_renderer != null ? PathUtility.GetGameObjectPath(_renderer.gameObject) : "(null)")}");
            if (_renderer != null) {
                var mesh = _renderer.sharedMesh;
                sb.AppendLine($"  shared mesh           : {(mesh != null ? mesh.name : "(null)")}");
                if (mesh != null) {
                    sb.AppendLine($"    verts               : {mesh.vertexCount}");
                    sb.AppendLine($"    submeshes           : {mesh.subMeshCount}");
                    sb.AppendLine($"    hasUV0              : {(mesh.uv != null && mesh.uv.Length > 0)}");
                    sb.AppendLine($"    isReadable          : {mesh.isReadable}");
                    sb.AppendLine($"    blendshapes         : {mesh.blendShapeCount}");
                }
                sb.AppendLine($"  transform position    : {_renderer.transform.position}");
                sb.AppendLine($"  transform scale       : {_renderer.transform.localScale}");
                sb.AppendLine($"  transform lossy scale : {_renderer.transform.lossyScale}");
            }
            sb.AppendLine($"  submesh filter        : {(_submeshIndex < 0 ? "All" : _submeshIndex.ToString())}");
            sb.AppendLine($"  symmetry              : {_symmetryEnabled} (root: {(_symmetryRoot != null ? PathUtility.GetGameObjectPath(_symmetryRoot.gameObject) : "world")})");
            sb.AppendLine($"  output mode           : {(_mode == MaskMode.Grayscale ? "Grayscale (RGBA)" : $"Channel {_channel}")}");
            sb.AppendLine($"  resolution            : {_resolution}x{_resolution}");
            sb.AppendLine($"  brush                 : radius={_radius:F4}m ({_radius * 100f:0.0}cm), strength={_strength:F2}, hardness={_hardness:F2}, mode={(_erase ? "ERASE" : "PAINT")}");
            sb.AppendLine($"  mask RT               : {(_maskRT != null ? $"alloc {_maskRT.width}x{_maskRT.height} {_maskRT.format}" : "(null)")}");
            sb.AppendLine($"  snapshot mesh         : {(_snapshotMesh != null ? $"verts={_snapshotMesh.vertexCount}, submeshes={_snapshotMesh.subMeshCount}" : "(null)")}");
            sb.AppendLine($"  snapshot world bounds : ({_snapshotWorldBounds.min} .. {_snapshotWorldBounds.max}), size={_snapshotWorldBounds.size}");
            sb.AppendLine($"  raycast state         : hasHit={_hasHit}, hitWorld={_hitWorld}, hitNormal={_hitNormal}");
            sb.AppendLine($"  counters              : strokes={_strokeCount}, dispatches={_dispatchCount}");
            sb.AppendLine($"  undo stack            : {_undoStack.Count} snapshot(s) (cap {UndoCapForResolution(_resolution)})");
            sb.AppendLine($"  coverage              : {(_statsCoveragePct >= 0f ? _statsCoveragePct.ToString("0.00") + "%" : "(unmeasured)")}");
            sb.AppendLine($"  brush shader          : {(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.name + " (" + _brushMaterial.shader.passCount + " passes)" : "(null)")}");
            sb.AppendLine($"  preview shader        : {(_previewMaterial != null && _previewMaterial.shader != null ? _previewMaterial.shader.name : "(null)")}");
            sb.AppendLine($"  scene views           : {SceneView.sceneViews.Count}");
            foreach (SceneView sv in SceneView.sceneViews) {
                if (sv == null) continue;
                sb.AppendLine($"    sv {sv.GetInstanceID()}    : wantsMouseMove={sv.wantsMouseMove}, focused={sv.hasFocus}");
            }
            sb.AppendLine($"  verboseLog            : {_verboseLog}");
            AvatarQolLogger.Instance.Info(sb.ToString());
        }

        private static void OpenLogFolder() {
            try {
                var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                var dir  = System.IO.Path.Combine(root, "WhyKnot", "Logs", AvatarQolLogger.PackageId);
                if (System.IO.Directory.Exists(dir)) {
                    EditorUtility.RevealInFinder(dir + "/.");
                } else {
                    EditorUtility.RevealInFinder(root);
                }
            } catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, "Opening log folder");
            }
        }
    }
}
