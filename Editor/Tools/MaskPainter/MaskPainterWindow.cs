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
// AssemblyReloadEvents.beforeAssemblyReload. Autosave is intentionally not
// enabled; a one-time warning on first paint explains that
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
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow : EditorWindow {

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
        [SerializeField] private bool  _showUvWireframe  = true;
        [SerializeField] private bool  _showUvCrosshair  = true;
        [SerializeField] private bool  _showUvGrid       = false;

        // ---- Volatile state (re-built after every domain reload) ----

        private bool          _painting;
        private RenderTexture _maskRT;
        private Mesh          _snapshotMesh;
        // World-space clone of _snapshotMesh: vertices are pre-transformed
        // into world coordinates so the brush dispatch can draw with
        // Matrix4x4.identity and the brush shader never has to multiply
        // by unity_ObjectToWorld. The point is to remove the only path
        // through which SceneView camera/model-matrix state could leak
        // into the paint draw -- Graphics.DrawMeshNow has a long history
        // of inheriting SceneView matrices when called outside a normal
        // camera render, and the symptom (brush stamps whatever the
        // SceneView camera sees onto the UV map) maps cleanly to that
        // failure class. CommandBuffer.DrawMesh + identity matrix +
        // pre-baked world verts removes all three moving parts at once.
        private Mesh          _paintWorldMesh;
        private Vector3[]     _snapshotWorldVerts;
        private Bounds        _snapshotWorldBounds;
        private float[]       _bakedBlendShapes;
        private Material      _brushMaterial;
        private Material      _previewMaterial;
        private readonly List<Texture2D> _undoStack = new List<Texture2D>();

        private Vector3 _hitWorld;
        private Vector3 _hitNormal;
        private Vector2 _hitUv;             // UV0 coord at the raycast hit; valid only when _hasHit.
        private bool    _hasHit;

        // ---- UV map cache (regenerated on mesh / submesh / resolution change) ----
        private Texture2D _uvWireframeTex;
        private int       _uvWireframeMeshId;
        private int       _uvWireframeSubmesh;
        private int       _uvWireframeSize;
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
        // moment Refresh stats was pressed.
        private float _statsCoveragePct = -1f; // -1 = "not yet computed"
        private double _statsRefreshedAt;

        // ---- Prefs keys ----

        private const string PrefsPrefix              = "dev.whyknot.wk-vrc-qol.MaskPainter.";
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
        private const string PrefsShowUvWireframe     = PrefsPrefix + "ShowUvWireframe";
        private const string PrefsShowUvCrosshair     = PrefsPrefix + "ShowUvCrosshair";
        private const string PrefsShowUvGrid          = PrefsPrefix + "ShowUvGrid";

        private const string WikiUrl =
            "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#paint-mask";

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
            if (_paintWorldMesh != null) { DestroyImmediate(_paintWorldMesh); _paintWorldMesh = null; }
            if (_brushMaterial != null) { DestroyImmediate(_brushMaterial); _brushMaterial = null; }
            if (_previewMaterial != null) { DestroyImmediate(_previewMaterial); _previewMaterial = null; }
            if (_uvWireframeTex != null) { DestroyImmediate(_uvWireframeTex); _uvWireframeTex = null; }
            _uvWireframeMeshId  = 0;
            _uvWireframeSubmesh = int.MinValue;
            _uvWireframeSize    = 0;
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
            _showUvWireframe    = EditorPrefs.GetBool (PrefsShowUvWireframe,    _showUvWireframe);
            _showUvCrosshair    = EditorPrefs.GetBool (PrefsShowUvCrosshair,    _showUvCrosshair);
            _showUvGrid         = EditorPrefs.GetBool (PrefsShowUvGrid,         _showUvGrid);
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
            EditorPrefs.SetBool (PrefsShowUvWireframe,    _showUvWireframe);
            EditorPrefs.SetBool (PrefsShowUvCrosshair,    _showUvCrosshair);
            EditorPrefs.SetBool (PrefsShowUvGrid,         _showUvGrid);
        }

        // ---- Diagnostics helper ----
        //
        // Routes Info-level lines through Debug or Info based on the
        // _verboseLog toggle. Debug stays file-only; Info mirrors to the
        // Unity Console. State transitions (start, stop, first hit) go
        // Info either way -- the cost is small and they're useful even
        // outside debugging sessions.

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




    }
}
