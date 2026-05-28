// UvTextureTransferWindow.cs
//
// Bake a texture authored against one mesh's UV layout into a second
// mesh's UV layout. Use case: a hat / outfit / accessory texture was
// painted against a base avatar's mesh and you want the equivalent
// pixels remapped to fit a different avatar's UVs without having to
// repaint the texture by hand.
//
// Flow:
//   1. Pick a source FBX. Either drag the existing model asset into the
//      ObjectField or click Browse to pull one in from anywhere on
//      disk -- external files are copied into
//      Assets/_WhyKnotUvTransfer/imported/ and Unity-imported there.
//   2. Pick a mesh from that FBX's sub-assets and (optionally) a
//      submesh.
//   3. Pick the source texture authored against that mesh's UV0.
//   4. Pick the target SkinnedMeshRenderer in the open scene and a
//      submesh.
//   5. Pick alignment (Identity uses the world transforms; Bounding
//      box matches sizes for different-scale avatars), output
//      resolution, optional max-correspondence-distance cap, and
//      sRGB-on-save.
//   6. Bake. Preview thumb shows up; Save PNG writes it next to the
//      target's mesh asset by default.
//
// The math is in UvTextureTransferCore (testable). FBX import and PNG
// persistence are in UvTextureTransferIO. This file is UI only.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed class UvTextureTransferWindow : EditorWindow {

        // ---- Persisted ----
        [SerializeField] private UnityEngine.Object _sourceModelAsset;  // FBX / .asset model whose sub-assets we'll enumerate
        [SerializeField] private Mesh      _sourceMesh;
        [SerializeField] private int       _sourceSubmesh = -1;
        [SerializeField] private Texture2D _sourceTexture;

        [SerializeField] private SkinnedMeshRenderer _targetRenderer;
        [SerializeField] private int _targetSubmesh = -1;

        [SerializeField] private UvTextureTransferCore.AlignmentMode _alignment = UvTextureTransferCore.AlignmentMode.BoundingBox;
        [SerializeField] private UvTextureTransferCore.CorrespondenceMode _correspondenceMode = UvTextureTransferCore.CorrespondenceMode.BidirectionalNormalRaycast;
        [SerializeField] private int   _resolution = 1024;
        [SerializeField] private float _maxDistance = 0f;     // 0 = no cap
        [SerializeField] private float _rayFrontalDistance = 0.08f;
        [SerializeField] private float _rayRearDistance = 0.08f;
        [SerializeField] private float _normalAngleLimit = 65f;
        [SerializeField] private bool  _rejectBackfaces;
        [SerializeField] private bool  _writeDiagnosticMaps = true;
        [SerializeField] private int   _supersample = 2;      // samples per axis; 2 = 4 subpixel samples
        [SerializeField] private int   _paddingPixels = 8;    // UV island dilation after bake
        [SerializeField] private bool  _sRGBOnSave = true;    // for color textures
        [SerializeField] private Color _fallbackColor = new Color(0, 0, 0, 0);
        [SerializeField] private bool  _verboseLog;

        // ---- Volatile ----
        private List<Mesh> _sourceMeshOptions = new List<Mesh>();
        private Texture2D _previewTex;
        private string _lastSavedPath;
        private UvTextureTransferCore.TransferResult _lastResult;
        private bool _hasResult;
        private Vector2 _pageScroll;
        private string _autoSizeSignature;

        // ---- Prefs keys ----
        private const string PrefsPrefix     = "dev.whyknot.wk-vrc-qol.UvTextureTransfer.";
        private const string PrefsAlignment  = PrefsPrefix + "Alignment";
        private const string PrefsCorrespondence = PrefsPrefix + "CorrespondenceMode";
        private const string PrefsResolution = PrefsPrefix + "Resolution";
        private const string PrefsMaxDist    = PrefsPrefix + "MaxDistance";
        private const string PrefsRayFrontal = PrefsPrefix + "RayFrontalDistance";
        private const string PrefsRayRear    = PrefsPrefix + "RayRearDistance";
        private const string PrefsNormalAngle = PrefsPrefix + "NormalAngleLimit";
        private const string PrefsRejectBackfaces = PrefsPrefix + "RejectBackfaces";
        private const string PrefsDiagnostics = PrefsPrefix + "WriteDiagnosticMaps";
        private const string PrefsSupersample = PrefsPrefix + "Supersample";
        private const string PrefsPadding    = PrefsPrefix + "PaddingPixels";
        private const string PrefsSRGB       = PrefsPrefix + "SRGB";
        private const string PrefsVerbose    = PrefsPrefix + "Verbose";

        private const string WikiUrl =
            "https://github.com/RealWhyKnot/wk-vrc-qol/wiki/Tools-Overview#uv-texture-transfer";

        // ---- Lifecycle ----

        internal static void Open(SkinnedMeshRenderer prefillTargetRenderer) {
            var w = GetWindow<UvTextureTransferWindow>(false, "UV Texture Transfer", true);
            w.titleContent = WkStyles.TitleContent("Avatar QoL - UV Texture Transfer");
            w.minSize = new Vector2(460, 640);
            if (prefillTargetRenderer != null) w._targetRenderer = prefillTargetRenderer;
            w.LoadPrefs();
            w.Show();
            w.Focus();
        }

        private void OnEnable() {
            LoadPrefs();
            RefreshSourceMeshOptions();
        }

        private void OnDisable() {
            ReleaseResultTextures();
            SavePrefs();
        }

        // ---- GUI ----

        private void OnGUI() {
            using var _theme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawTitleBar();
                WkStyles.AnimatedAccentLine();

                using (var s = new EditorGUILayout.ScrollViewScope(
                        _pageScroll, false, false,
                        GUILayout.ExpandWidth(true),
                        GUILayout.ExpandHeight(true))) {
                    _pageScroll = s.scrollPosition;
                    DrawHelpNotice();
                    DrawSourceSection();
                    EditorGUILayout.Space(2);
                    DrawTargetSection();
                    EditorGUILayout.Space(2);
                    DrawBakeOptionsSection();
                    EditorGUILayout.Space(2);
                    DrawBakeBar();
                    EditorGUILayout.Space(2);
                    DrawPreviewSection();
                }

                WkStyles.WindowFooter();
            }
            RequestAutoSize();
        }

        private void DrawTitleBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("UV Texture Transfer",
                        "Rebake a texture from one mesh's UV layout into another mesh's UV layout using projected surface correspondence."),
                    WkStyles.SectionTitle);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button(
                        new GUIContent("?", "Open the Avatar QoL wiki page for this tool."),
                        EditorStyles.miniButton, GUILayout.Width(22), GUILayout.Height(18))) {
                    Application.OpenURL(WikiUrl);
                }
            }
        }

        private void DrawHelpNotice() {
            WkStyles.Notice(NoticeKind.Info,
                "Pick a source mesh and the texture authored for it, pick the target renderer in your scene, choose alignment, then Bake. Projected modes avoid grabbing unrelated nearby surfaces; Legacy closest point is available only for comparison.");
        }

        private void RequestAutoSize() {
            var sourceId = _sourceMesh != null ? _sourceMesh.GetInstanceID() : 0;
            var targetId = _targetRenderer != null ? _targetRenderer.GetInstanceID() : 0;
            var previewId = _previewTex != null ? _previewTex.GetInstanceID() : 0;
            var signature = $"{sourceId}|{targetId}|{_sourceMeshOptions.Count}|{_sourceTexture != null}|{_hasResult}|{previewId}|{_lastSavedPath}";
            var preferred = new Vector2(
                _hasResult ? 720f : 560f,
                _hasResult ? 760f : 640f);
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(460f, 640f),
                preferred,
                new Vector2(920f, 800f));
        }

        private void DrawSourceSection() {
            using (WkStyles.Section("1. Source",
                    "Where the texture comes from. Pick an FBX (drag the existing asset in or Browse to pull one in from disk), then a mesh inside it, then the texture authored against that mesh's UV0.")) {
                using (new EditorGUILayout.HorizontalScope()) {
                    EditorGUILayout.LabelField(
                        new GUIContent("FBX / model",
                            "Either an FBX (or .asset) already in the project, or use Browse to import one from anywhere on disk. External files are copied into Assets/_WhyKnotUvTransfer/imported/."),
                        GUILayout.Width(WkStyles.LabelColumn));
                    var prev = _sourceModelAsset;
                    _sourceModelAsset = EditorGUILayout.ObjectField(_sourceModelAsset, typeof(UnityEngine.Object), false);
                    if (prev != _sourceModelAsset) {
                        _sourceMesh = null;
                        _sourceSubmesh = -1;
                        RefreshSourceMeshOptions();
                    }
                    if (GUILayout.Button(
                            new GUIContent("Browse...",
                                "Pick an FBX from anywhere on disk; it'll be copied into Assets/_WhyKnotUvTransfer/imported/ and imported there."),
                            GUILayout.Width(80))) {
                        BrowseForExternalFbx();
                    }
                }

                if (_sourceMeshOptions.Count == 0 && _sourceModelAsset != null) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "The picked asset doesn't have any Mesh sub-assets. Pick an FBX or model file.");
                }

                if (_sourceMeshOptions.Count > 0) {
                    DrawSourceMeshPicker();
                }

                WkStyles.LabeledField(
                    new GUIContent("Texture",
                        "The texture authored against the source mesh's UV0. Should be readable (importer's Read/Write Enabled) so GetPixelBilinear works at bake time."),
                    () => {
                        var prev = _sourceTexture;
                        _sourceTexture = (Texture2D)EditorGUILayout.ObjectField(_sourceTexture, typeof(Texture2D), false);
                        if (_sourceTexture != null && !_sourceTexture.isReadable) {
                            WkStyles.Notice(NoticeKind.Warning,
                                "Texture is not marked Read/Write Enabled in the importer; sampling will return uniform color. Enable Read/Write in the texture importer.");
                        }
                        if (prev != _sourceTexture) { /* nothing additional yet */ }
                    });
            }
        }

        private void DrawSourceMeshPicker() {
            var labels = new GUIContent[_sourceMeshOptions.Count];
            int selectedIndex = -1;
            for (int i = 0; i < _sourceMeshOptions.Count; i++) {
                var m = _sourceMeshOptions[i];
                labels[i] = new GUIContent(m != null ? m.name : "(null)",
                    m != null ? $"{m.vertexCount} verts, {m.subMeshCount} submesh(es)" : "");
                if (m == _sourceMesh) selectedIndex = i;
            }
            if (selectedIndex < 0) selectedIndex = 0;
            WkStyles.LabeledField(
                new GUIContent("Mesh",
                    "Which mesh inside the picked FBX to read UVs and triangles from. Most avatar FBXes ship one Body mesh; outfits / hair / accessories may have several."),
                () => {
                    int next = EditorGUILayout.Popup(selectedIndex, labels);
                    if (next != selectedIndex) {
                        _sourceMesh = _sourceMeshOptions[next];
                        _sourceSubmesh = -1;
                    }
                });
            if (_sourceMesh != null && _sourceMesh.subMeshCount > 1) {
                DrawSubmeshPicker(_sourceMesh, ref _sourceSubmesh, "Source submesh",
                    "Limit the source to one of the mesh's submeshes if its texture is authored per-submesh.");
            }
        }

        private void DrawTargetSection() {
            using (WkStyles.Section("2. Target",
                    "Where the texture lands. Pick the renderer in your scene whose UV layout you want the rebaked texture to fit.")) {
                WkStyles.LabeledField(
                    new GUIContent("Renderer",
                        "The SkinnedMeshRenderer in the open scene whose UV0 the baked texture will be mapped against."),
                    () => {
                        var prev = _targetRenderer;
                        _targetRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField(_targetRenderer, typeof(SkinnedMeshRenderer), true);
                        if (prev != _targetRenderer) _targetSubmesh = -1;
                    });
                if (_targetRenderer != null) {
                    var mesh = _targetRenderer.sharedMesh;
                    if (mesh == null) {
                        WkStyles.Notice(NoticeKind.Warning, "Target renderer has no mesh assigned.");
                    } else if (!mesh.isReadable) {
                        WkStyles.Notice(NoticeKind.Warning,
                            "Target mesh has Read/Write disabled in the importer; the bake reads triangle / UV data and needs it.");
                    } else if (mesh.uv == null || mesh.uv.Length == 0) {
                        WkStyles.Notice(NoticeKind.Warning,
                            "Target mesh has no UV0 channel.");
                    } else if (mesh.subMeshCount > 1) {
                        DrawSubmeshPicker(mesh, ref _targetSubmesh, "Target submesh",
                            "Limit the target to one submesh if you only want to rebake into that submesh's UV area.");
                    }
                }
            }
        }

        private void DrawSubmeshPicker(Mesh mesh, ref int submeshIndex, string label, string tip) {
            var options = new List<GUIContent>();
            options.Add(new GUIContent("All submeshes", "Bake into every submesh's UV area at once."));
            for (int s = 0; s < mesh.subMeshCount; s++) {
                options.Add(new GUIContent($"{s}", $"Submesh #{s}"));
            }
            if (submeshIndex >= mesh.subMeshCount) submeshIndex = -1;
            int displayIndex = submeshIndex < 0 ? 0 : submeshIndex + 1;
            int chosen = displayIndex;
            WkStyles.LabeledField(new GUIContent(label, tip), () => {
                chosen = EditorGUILayout.Popup(displayIndex, options.ToArray());
            });
            int newIndex = chosen == 0 ? -1 : chosen - 1;
            if (newIndex != submeshIndex) submeshIndex = newIndex;
        }

        private void DrawBakeOptionsSection() {
            using (WkStyles.Section("3. Bake options",
                    "How the two meshes are aligned, how big the output is, and what to do with target texels that have no source nearby.")) {
                WkStyles.LabeledField(
                    new GUIContent("Alignment",
                        "Identity treats both meshes as already living in the same coordinate space (right when both come from the open scene). Bounding box uniformly scales and translates the source mesh so its AABB matches the target's; pick this when the two avatars are different sizes."),
                    () => {
                        var prev = _alignment;
                        _alignment = (UvTextureTransferCore.AlignmentMode)EditorGUILayout.EnumPopup(_alignment);
                        if (prev != _alignment) SavePrefs();
                    });

                WkStyles.LabeledField(
                    new GUIContent("Correspondence",
                        "How each target surface point finds its source surface point. Raycast modes avoid the global nearest-surface failure that can pull torso or pelvis tattoos onto arms."),
                    () => {
                        var prev = _correspondenceMode;
                        _correspondenceMode = (UvTextureTransferCore.CorrespondenceMode)EditorGUILayout.EnumPopup(_correspondenceMode);
                        if (prev != _correspondenceMode) SavePrefs();
                    });

                if (_correspondenceMode == UvTextureTransferCore.CorrespondenceMode.LegacyClosestPoint) {
                    WkStyles.Notice(NoticeKind.Warning,
                        "Legacy closest point can pick nearby but unrelated body parts. Use a raycast mode for avatar tattoos.");
                } else {
                    WkStyles.LabeledField(
                        new GUIContent("Ray out / in",
                            "Projection envelope in metres. The ray starts this far along the target normal and travels through the surface by the rear amount."),
                        () => {
                            using (new EditorGUILayout.HorizontalScope()) {
                                float prevFront = _rayFrontalDistance;
                                float prevRear = _rayRearDistance;
                                _rayFrontalDistance = EditorGUILayout.Slider(_rayFrontalDistance, 0.005f, 0.5f);
                                _rayRearDistance = EditorGUILayout.Slider(_rayRearDistance, 0.005f, 0.5f);
                                if (!Mathf.Approximately(prevFront, _rayFrontalDistance)
                                        || !Mathf.Approximately(prevRear, _rayRearDistance)) {
                                    SavePrefs();
                                }
                            }
                        });

                    WkStyles.LabeledField(
                        new GUIContent("Normal angle",
                            "Reject source hits whose face normal differs too much from the target normal. Higher values are more permissive; 0 disables this filter."),
                        () => {
                            float prev = _normalAngleLimit;
                            _normalAngleLimit = EditorGUILayout.Slider(_normalAngleLimit, 0f, 120f);
                            if (!Mathf.Approximately(prev, _normalAngleLimit)) SavePrefs();
                        });

                    bool prevBackfaces = _rejectBackfaces;
                    _rejectBackfaces = EditorGUILayout.ToggleLeft(
                        new GUIContent("Reject backfaces",
                            "Ignore ray hits that strike the back side of a source triangle. Leave off for meshes with inconsistent normals."),
                        _rejectBackfaces);
                    if (prevBackfaces != _rejectBackfaces) SavePrefs();
                }

                WkStyles.LabeledField(
                    new GUIContent("Resolution",
                        "Output texture size. Match the target material's texture resolution so the baked PNG drops in without resampling."),
                    () => {
                        var resolutions = new[] { 256, 512, 1024, 2048, 4096 };
                        var labels = new[] { "256", "512", "1024", "2048", "4096" };
                        int idx = Array.IndexOf(resolutions, _resolution);
                        if (idx < 0) idx = 2;
                        int next = EditorGUILayout.Popup(idx, labels, GUILayout.Width(80));
                        if (next != idx) { _resolution = resolutions[next]; SavePrefs(); }
                    });

                DrawResolutionHint();

                WkStyles.LabeledField(
                    new GUIContent("Anti-aliasing",
                        "Subpixel samples per output texel. Higher values reduce jagged thin masks and triangle-edge stair-steps at the cost of bake time."),
                    () => {
                        var options = new[] { 1, 2, 3, 4 };
                        var labels = new[] { "1x (fast)", "2x (4 samples)", "3x (9 samples)", "4x (16 samples)" };
                        int idx = Array.IndexOf(options, Mathf.Clamp(_supersample, 1, 4));
                        if (idx < 0) idx = 1;
                        int next = EditorGUILayout.Popup(idx, labels, GUILayout.Width(142));
                        if (next != idx) { _supersample = options[next]; SavePrefs(); }
                    });

                WkStyles.LabeledField(
                    new GUIContent("Island padding",
                        "Dilate covered target UV texels outward by this many pixels after the bake. Prevents transparent/black fallback pixels from bleeding into seams under bilinear filtering."),
                    () => {
                        int prev = _paddingPixels;
                        _paddingPixels = EditorGUILayout.IntSlider(_paddingPixels, 0, 32);
                        if (prev != _paddingPixels) SavePrefs();
                    });

                WkStyles.LabeledField(
                    new GUIContent("Max distance (m)",
                        "Drop target texels whose accepted source hit is further than this. 0 disables this extra cap; raycast modes are still bounded by Ray out / in."),
                    () => {
                        float prev = _maxDistance;
                        _maxDistance = EditorGUILayout.Slider(_maxDistance, 0f, 0.5f);
                        if (!Mathf.Approximately(prev, _maxDistance)) SavePrefs();
                    });

                bool prevDiagnostics = _writeDiagnosticMaps;
                _writeDiagnosticMaps = EditorGUILayout.ToggleLeft(
                    new GUIContent("Write diagnostic maps on save",
                        "Keep hit-distance, normal-dot, reject-reason, and source-triangle debug textures from the bake, then save them next to the PNG."),
                    _writeDiagnosticMaps);
                if (prevDiagnostics != _writeDiagnosticMaps) SavePrefs();

                WkStyles.LabeledField(
                    new GUIContent("Fallback color",
                        "Pixels with no source correspondence (or beyond max distance) get this color. Default is fully transparent; pick a neutral color when the output will sit on a material slot that ignores alpha."),
                    () => {
                        _fallbackColor = EditorGUILayout.ColorField(_fallbackColor);
                    });

                bool prevSRGB = _sRGBOnSave;
                _sRGBOnSave = EditorGUILayout.ToggleLeft(
                    new GUIContent("Save as sRGB",
                        "On for color textures (albedo / emission). Off for mask / normal / metallic / occlusion outputs."),
                    _sRGBOnSave);
                if (prevSRGB != _sRGBOnSave) SavePrefs();
            }
        }

        private void DrawResolutionHint() {
            if (_sourceTexture == null) return;
            int sourceMax = Mathf.Max(_sourceTexture.width, _sourceTexture.height);
            if (sourceMax <= _resolution) return;
            using (new EditorGUILayout.HorizontalScope()) {
                GUILayout.Space(WkStyles.LabelColumn);
                EditorGUILayout.LabelField(
                    new GUIContent($"Source is {_sourceTexture.width}x{_sourceTexture.height}; use {sourceMax} for the sharpest mask bake.",
                        "Baking below the source resolution downsamples the source texture. That can be fine for soft color maps, but thin black/white masks usually look better at source resolution."),
                    WkStyles.Muted);
                if (GUILayout.Button(
                        new GUIContent("Match source", "Set output resolution to the source texture's larger dimension."),
                        EditorStyles.miniButton, GUILayout.Width(94))) {
                    _resolution = Mathf.Clamp(sourceMax, 256, 4096);
                    SavePrefs();
                }
            }
        }

        private void DrawBakeBar() {
            using (new EditorGUILayout.HorizontalScope()) {
                using (new EditorGUI.DisabledScope(!CanBake())) {
                    if (WkStyles.PrimaryButtonInline(
                            new GUIContent("Bake",
                                "Run the transfer and produce the output texture. Cost scales with output resolution x covered triangles -- 1024^2 on a typical avatar takes a few seconds."),
                            GUILayout.Height(34), GUILayout.MinWidth(160))) {
                        RunBake();
                    }
                }
                GUILayout.FlexibleSpace();
                using (new EditorGUI.DisabledScope(_previewTex == null)) {
                    if (GUILayout.Button(
                            new GUIContent("Save PNG...",
                                "Write the baked texture to disk and configure its importer for typical material-slot use."),
                            GUILayout.Width(110), GUILayout.Height(34))) {
                        SaveBakedPng();
                    }
                }
            }
            if (!CanBake()) {
                EditorGUILayout.LabelField("Pick a source mesh + texture and a target renderer to enable Bake.",
                    WkStyles.Muted);
            }
        }

        private bool CanBake() {
            return _sourceMesh != null
                && _sourceTexture != null
                && _targetRenderer != null
                && _targetRenderer.sharedMesh != null;
        }

        private void DrawPreviewSection() {
            using (WkStyles.Section("4. Preview",
                    "Output texture from the last bake.")) {
                if (_previewTex == null) {
                    EditorGUILayout.LabelField("(bake to populate)", EditorStyles.centeredGreyMiniLabel);
                    return;
                }
                if (_hasResult) {
                    EditorGUILayout.LabelField(
                        $"{_resolution}²  ·  {_lastResult.correspondenceMode}  ·  AA {_lastResult.supersample}x  ·  covered {_lastResult.coveredTexels:N0} / {_lastResult.totalTexels:N0}  ·  padded {_lastResult.paddedTexels:N0}  ·  dist rejects {_lastResult.rejectedByDistance:N0}  ·  ray miss {_lastResult.rejectedByRayMiss:N0}  ·  normal rejects {_lastResult.rejectedByNormalAngle:N0}  ·  max dist {_lastResult.maxObservedDistance:F4} m",
                        WkStyles.Muted);
                }
                const float previewSize = 320f;
                using (new EditorGUILayout.HorizontalScope()) {
                    GUILayout.FlexibleSpace();
                    var rect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.Width(previewSize), GUILayout.Height(previewSize));
                    GUILayout.FlexibleSpace();
                    DrawCheckerBackground(rect);
                    GUI.DrawTexture(rect, _previewTex, ScaleMode.ScaleToFit, true);
                }
            }
        }

        // ---- Actions ----

        private void BrowseForExternalFbx() {
            string startDir = string.IsNullOrEmpty(_lastSavedPath)
                ? Application.dataPath
                : Path.GetDirectoryName(_lastSavedPath);
            string picked = EditorUtility.OpenFilePanel("Pick a source FBX",
                startDir ?? Application.dataPath, "fbx");
            if (string.IsNullOrEmpty(picked)) return;
            string projectRel = UvTextureTransferIO.ImportExternalFbx(picked);
            if (string.IsNullOrEmpty(projectRel)) return;
            var asset = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(projectRel);
            if (asset != null) {
                _sourceModelAsset = asset;
                _sourceMesh = null;
                _sourceSubmesh = -1;
                RefreshSourceMeshOptions();
            }
        }

        private void RefreshSourceMeshOptions() {
            _sourceMeshOptions.Clear();
            if (_sourceModelAsset == null) return;
            string path = AssetDatabase.GetAssetPath(_sourceModelAsset);
            if (string.IsNullOrEmpty(path)) return;
            _sourceMeshOptions = UvTextureTransferIO.EnumerateMeshes(path);
            if (_sourceMesh == null && _sourceMeshOptions.Count > 0) {
                _sourceMesh = _sourceMeshOptions[0];
            }
        }

        private void RunBake() {
            if (!CanBake()) return;
            Matrix4x4 sourceMatrix = Matrix4x4.identity;
            // Source comes from an asset, no scene transform; bbox
            // alignment uses the mesh.bounds directly. Identity treats
            // the source mesh's local space as the common space; the
            // target then carries its own world transform on top.
            Matrix4x4 targetMatrix = _targetRenderer.transform.localToWorldMatrix;

            // Identity is interpreted as "both meshes in the same frame",
            // which we model here as the SOURCE mesh's local space. The
            // target's world transform on top of that puts the body in a
            // potentially different place; for Identity alignment we use
            // the renderer's localToWorldMatrix on the target which is
            // right when the source already sits where the target sits in
            // world space. For BoundingBox we ignore world transforms
            // entirely.
            if (_alignment == UvTextureTransferCore.AlignmentMode.BoundingBox) {
                targetMatrix = Matrix4x4.identity;
            }

            var opt = new UvTextureTransferCore.TransferOptions {
                sourceMesh          = _sourceMesh,
                sourceSubmesh       = _sourceSubmesh,
                sourceTexture       = _sourceTexture,
                targetMesh          = _targetRenderer.sharedMesh,
                targetSubmesh       = _targetSubmesh,
                outputResolution    = _resolution,
                alignment           = _alignment,
                sourceWorldMatrix   = sourceMatrix,
                targetWorldMatrix   = targetMatrix,
                maxDistance         = _maxDistance,
                gridDim             = 0,
                fallbackColor       = _fallbackColor,
                supersample         = _supersample,
                paddingPixels       = _paddingPixels,
                correspondenceMode  = _correspondenceMode,
                rayFrontalDistance  = _rayFrontalDistance,
                rayRearDistance     = _rayRearDistance,
                normalAngleLimitDegrees = _normalAngleLimit,
                rejectBackfaces     = _rejectBackfaces,
                writeDiagnosticMaps = _writeDiagnosticMaps,
                onProgress          = ReportProgress,
            };

            try {
                EditorUtility.DisplayProgressBar("UV Texture Transfer", "Baking...", 0f);
                var t0 = EditorApplication.timeSinceStartup;
                var result = UvTextureTransferCore.Transfer(opt);
                double elapsed = EditorApplication.timeSinceStartup - t0;
                ReleaseResultTextures();
                _previewTex = result.output;
                _lastResult = result;
                _hasResult  = true;
                AvatarQolLogger.Instance.Info(
                    $"UV Texture Transfer bake done in {elapsed:F2}s -- " +
                    $"covered {result.coveredTexels:N0}/{result.totalTexels:N0} texels, " +
                    $"mode {result.correspondenceMode}, padded {result.paddedTexels:N0}, " +
                    $"distance rejects {result.rejectedByDistance:N0}, ray misses {result.rejectedByRayMiss:N0}, " +
                    $"normal rejects {result.rejectedByNormalAngle:N0}, backface rejects {result.rejectedByBackface:N0}, " +
                    $"aa {result.supersample}x, padding {result.paddingPixels}px, " +
                    $"max observed distance {result.maxObservedDistance:F4} m, " +
                    $"output {_previewTex.width}x{_previewTex.height}.");
            } catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, "UV Texture Transfer bake");
                EditorUtility.DisplayDialog("UV Texture Transfer",
                    $"Bake failed: {ex.Message}\n\nSee the package log for details.", "OK");
            } finally {
                EditorUtility.ClearProgressBar();
            }
        }

        private void ReportProgress(float p) {
            // The progress callback fires on the bake worker (currently
            // the main thread, but the rasterizer / closest-point loops
            // are slow enough that the user would otherwise see a frozen
            // Editor). Update Unity's standard progress bar.
            EditorUtility.DisplayProgressBar("UV Texture Transfer",
                $"Baking texels ({p * 100f:F0}%)", p);
        }

        private void SaveBakedPng() {
            if (_previewTex == null) return;
            string folder = ResolveDefaultSaveFolder();
            string name = DefaultFilename();
            string path = EditorUtility.SaveFilePanel("Save baked texture", folder, name, "png");
            if (string.IsNullOrEmpty(path)) return;
            if (UvTextureTransferIO.SavePng(_previewTex, path, _sRGBOnSave)) {
                _lastSavedPath = path;
                SaveDiagnosticMaps(path);
            }
        }

        private void SaveDiagnosticMaps(string outputPath) {
            if (!_writeDiagnosticMaps || !_hasResult || string.IsNullOrEmpty(outputPath)) return;
            SaveDiagnosticMap(_lastResult.hitDistanceMap, outputPath, "HitDistance");
            SaveDiagnosticMap(_lastResult.normalDotMap, outputPath, "NormalDot");
            SaveDiagnosticMap(_lastResult.rejectReasonMap, outputPath, "RejectReason");
            SaveDiagnosticMap(_lastResult.sourceTriangleMap, outputPath, "SourceTriangle");
        }

        private static void SaveDiagnosticMap(Texture2D texture, string outputPath, string suffix) {
            if (texture == null) return;
            string dir = Path.GetDirectoryName(outputPath);
            string name = Path.GetFileNameWithoutExtension(outputPath);
            if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name)) return;
            string path = Path.Combine(dir, $"{name}_{suffix}.png");
            UvTextureTransferIO.SavePng(texture, path, sRGB: false);
        }

        private void ReleaseResultTextures() {
            if (_previewTex != null) {
                DestroyImmediate(_previewTex);
                _previewTex = null;
            }
            DestroyResultTexture(_lastResult.hitDistanceMap);
            DestroyResultTexture(_lastResult.normalDotMap);
            DestroyResultTexture(_lastResult.rejectReasonMap);
            DestroyResultTexture(_lastResult.sourceTriangleMap);
            _lastResult = default;
            _hasResult = false;
        }

        private static void DestroyResultTexture(Texture2D texture) {
            if (texture != null) DestroyImmediate(texture);
        }

        private string ResolveDefaultSaveFolder() {
            if (!string.IsNullOrEmpty(_lastSavedPath)) {
                var dir = Path.GetDirectoryName(_lastSavedPath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir)) return dir;
            }
            if (_targetRenderer != null && _targetRenderer.sharedMesh != null) {
                var assetPath = AssetDatabase.GetAssetPath(_targetRenderer.sharedMesh);
                if (!string.IsNullOrEmpty(assetPath)) {
                    var dir = Path.GetDirectoryName(assetPath);
                    if (!string.IsNullOrEmpty(dir)) return dir;
                }
            }
            return Application.dataPath;
        }

        private string DefaultFilename() {
            string baseName = _targetRenderer != null && _targetRenderer.sharedMesh != null
                ? _targetRenderer.sharedMesh.name
                : "Transferred";
            string sourceTag = _sourceTexture != null ? _sourceTexture.name : "src";
            return $"{baseName}_From_{sourceTag}.png";
        }

        // ---- Helpers ----

        private static void DrawCheckerBackground(Rect rect) {
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

        // ---- Prefs ----

        private void LoadPrefs() {
            _alignment   = (UvTextureTransferCore.AlignmentMode)EditorPrefs.GetInt(PrefsAlignment, (int)_alignment);
            _correspondenceMode = (UvTextureTransferCore.CorrespondenceMode)EditorPrefs.GetInt(PrefsCorrespondence, (int)_correspondenceMode);
            _resolution  = EditorPrefs.GetInt(PrefsResolution, _resolution);
            _maxDistance = EditorPrefs.GetFloat(PrefsMaxDist, _maxDistance);
            _rayFrontalDistance = EditorPrefs.GetFloat(PrefsRayFrontal, _rayFrontalDistance);
            _rayRearDistance = EditorPrefs.GetFloat(PrefsRayRear, _rayRearDistance);
            _normalAngleLimit = EditorPrefs.GetFloat(PrefsNormalAngle, _normalAngleLimit);
            _rejectBackfaces = EditorPrefs.GetBool(PrefsRejectBackfaces, _rejectBackfaces);
            _writeDiagnosticMaps = EditorPrefs.GetBool(PrefsDiagnostics, _writeDiagnosticMaps);
            _supersample = Mathf.Clamp(EditorPrefs.GetInt(PrefsSupersample, _supersample), 1, 4);
            _paddingPixels = Mathf.Clamp(EditorPrefs.GetInt(PrefsPadding, _paddingPixels), 0, 32);
            _sRGBOnSave  = EditorPrefs.GetBool(PrefsSRGB, _sRGBOnSave);
            _verboseLog  = EditorPrefs.GetBool(PrefsVerbose, _verboseLog);
        }

        private void SavePrefs() {
            EditorPrefs.SetInt(PrefsAlignment, (int)_alignment);
            EditorPrefs.SetInt(PrefsCorrespondence, (int)_correspondenceMode);
            EditorPrefs.SetInt(PrefsResolution, _resolution);
            EditorPrefs.SetFloat(PrefsMaxDist, _maxDistance);
            EditorPrefs.SetFloat(PrefsRayFrontal, _rayFrontalDistance);
            EditorPrefs.SetFloat(PrefsRayRear, _rayRearDistance);
            EditorPrefs.SetFloat(PrefsNormalAngle, _normalAngleLimit);
            EditorPrefs.SetBool(PrefsRejectBackfaces, _rejectBackfaces);
            EditorPrefs.SetBool(PrefsDiagnostics, _writeDiagnosticMaps);
            EditorPrefs.SetInt(PrefsSupersample, _supersample);
            EditorPrefs.SetInt(PrefsPadding, _paddingPixels);
            EditorPrefs.SetBool(PrefsSRGB, _sRGBOnSave);
            EditorPrefs.SetBool(PrefsVerbose, _verboseLog);
        }
    }
}
