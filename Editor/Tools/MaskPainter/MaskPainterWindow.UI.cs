// MaskPainterWindow.UI.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

        // ---- GUI ----

        private void OnGUI() {
            using var _wkTheme = WkStyles.Scope(WkTheme.WhyKnot);
            using (new EditorGUILayout.VerticalScope(GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true))) {
                DrawStatusBanner();
                EditorGUILayout.Space(2);
                DrawTitleBar();

                using (var s = new EditorGUILayout.ScrollViewScope(
                        _pageScroll, false, false,
                        GUILayout.ExpandWidth(true),
                        GUILayout.ExpandHeight(true))) {
                    _pageScroll = s.scrollPosition;
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

                WkStyles.WindowFooter();
            }
            RequestAutoSize();
        }

        private void RequestAutoSize() {
            var rendererId = _renderer != null ? _renderer.GetInstanceID() : 0;
            var signature = $"{rendererId}|{_painting}|{_maskRT != null}|{_advancedOpen}|{_strokeCount}|{_resolution}|{_mode}|{_channel}";
            var preferred = new Vector2(
                _painting || _maskRT != null ? 620f : 500f,
                _painting || _maskRT != null ? 800f : 720f);
            WkStyles.AutoSizeWindow(
                this,
                ref _autoSizeSignature,
                signature,
                new Vector2(440f, 640f),
                preferred,
                new Vector2(820f, 820f));
        }

        private void DrawStatusBanner() {
            // Coloured banner at the very top. Green = painting active,
            // amber = needs attention, slate = idle. Shows paint-session state
            // at a glance.
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
                                // Re-bake against the new renderer immediately so the
                                // paint session stays continuous.
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
            // Re-validate selection -- submesh count can change when the mesh changes underneath.
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
                    // size and softness before painting.
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
                    "Current mask RT, with the active channel isolated when painting per-channel. The UV map view overlays the mesh's UV0 islands so it's obvious which painted texels correspond to which mesh region.")) {
                if (_maskRT == null) {
                    EditorGUILayout.LabelField("(start painting to allocate the mask buffer)", EditorStyles.centeredGreyMiniLabel);
                    DrawUvMapToggles();
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
                DrawUvMapToggles();
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
                    // UV map overlays (grid then wireframe then crosshair).
                    if (_showUvGrid)      DrawUvGrid(rect);
                    if (_showUvWireframe) DrawUvWireframe(rect);
                    if (_showUvCrosshair && _hasHit) DrawUvCrosshair(rect, _hitUv);
                    // Border
                    DrawRectBorder(rect, new Color(0.0f, 0.0f, 0.0f, 0.7f), 1);
                }
                if (_showUvCrosshair) {
                    string uvLine = _hasHit
                        ? $"cursor UV  ·  u = {_hitUv.x:0.000}   v = {_hitUv.y:0.000}"
                        : "cursor UV  ·  (hover the mesh in the Scene view)";
                    EditorGUILayout.LabelField(uvLine, WkStyles.Muted);
                }
            }
        }

        private void DrawUvMapToggles() {
            using (new EditorGUILayout.HorizontalScope()) {
                EditorGUILayout.LabelField(
                    new GUIContent("UV map",
                        "Overlay the mesh's UV0 layout on the preview. Useful for seeing which body region a painted texel maps to, finding seams, and confirming the brush is landing where you expect."),
                    GUILayout.Width(WkStyles.LabelColumn));
                bool prevWire = _showUvWireframe;
                _showUvWireframe = GUILayout.Toggle(_showUvWireframe,
                    new GUIContent("Islands",
                        "Trace each UV0 triangle's edges over the painted mask so island boundaries are visible. Generated once per (mesh, submesh, resolution) and cached for the rest of the session."),
                    EditorStyles.miniButton, GUILayout.Width(70));
                if (prevWire != _showUvWireframe) SaveEditorPrefs();

                bool prevCross = _showUvCrosshair;
                _showUvCrosshair = GUILayout.Toggle(_showUvCrosshair,
                    new GUIContent("Cursor",
                        "Mark the UV0 coordinate currently under the Scene view raycast with a crosshair on the preview. Lets you confirm a click maps to the texel you expect before painting."),
                    EditorStyles.miniButton, GUILayout.Width(70));
                if (prevCross != _showUvCrosshair) SaveEditorPrefs();

                bool prevGrid = _showUvGrid;
                _showUvGrid = GUILayout.Toggle(_showUvGrid,
                    new GUIContent("Grid",
                        "Draw an 8x8 reference grid over the preview at UV 1/8 increments. Useful for spotting which atlas tile a region lives in when several submeshes share a [0,1] UV space."),
                    EditorStyles.miniButton, GUILayout.Width(70));
                if (prevGrid != _showUvGrid) SaveEditorPrefs();

                GUILayout.FlexibleSpace();
            }
        }

        private void EnsureUvWireframeCache() {
            if (_renderer == null) return;
            var mesh = _renderer.sharedMesh;
            if (mesh == null) return;
            int meshId = mesh.GetInstanceID();
            // Single source of truth for "what submesh did the cache bake".
            int cacheSubmesh = _submeshIndex;
            int cacheSize    = Mathf.Clamp(_resolution, 256, 2048);
            if (_uvWireframeTex != null
                    && _uvWireframeMeshId == meshId
                    && _uvWireframeSubmesh == cacheSubmesh
                    && _uvWireframeSize == cacheSize) {
                return;
            }
            if (_uvWireframeTex != null) DestroyImmediate(_uvWireframeTex);
            _uvWireframeTex = MaskPainterIO.GenerateUvWireframe(mesh, cacheSubmesh, cacheSize);
            _uvWireframeMeshId  = meshId;
            _uvWireframeSubmesh = cacheSubmesh;
            _uvWireframeSize    = cacheSize;
        }

        private void DrawUvWireframe(Rect rect) {
            EnsureUvWireframeCache();
            if (_uvWireframeTex == null) return;
            // White-alpha texture tinted via GUI.DrawTexture's tint param.
            // Cyan reads against the orange grayscale mask and the per-channel
            // tints without clashing; alpha 0.55 keeps the painted mask
            // visible underneath.
            var tint = new Color(0.55f, 0.95f, 1f, 0.85f);
            GUI.DrawTexture(rect, _uvWireframeTex, ScaleMode.ScaleToFit, true, 1f, tint, 0f, 0f);
        }

        private static void DrawUvCrosshair(Rect rect, Vector2 uv) {
            if (Event.current.type != EventType.Repaint) return;
            // Map UV [0,1] -> rect pixel space, flipping V so islands sit
            // upright the same way they would in a texture browser.
            float px = rect.x + uv.x * rect.width;
            float py = rect.y + (1f - uv.y) * rect.height;
            // Out-of-canvas UV (rare with InterpolateUv but possible with
            // tiled UVs) just renders nothing.
            if (px < rect.x || px > rect.xMax || py < rect.y || py > rect.yMax) return;
            var c = new Color(1f, 0.95f, 0.20f, 0.95f);
            // 1px crosshair lines, gap in the center so the exact texel is visible.
            EditorGUI.DrawRect(new Rect(px - 8, py, 6, 1), c);
            EditorGUI.DrawRect(new Rect(px + 3, py, 6, 1), c);
            EditorGUI.DrawRect(new Rect(px, py - 8, 1, 6), c);
            EditorGUI.DrawRect(new Rect(px, py + 3, 1, 6), c);
            // Center pixel
            EditorGUI.DrawRect(new Rect(px - 0.5f, py - 0.5f, 1, 1), c);
        }

        private static void DrawUvGrid(Rect rect) {
            if (Event.current.type != EventType.Repaint) return;
            var line = new Color(1f, 1f, 1f, 0.10f);
            // 8x8 grid -> 7 inner lines per axis. Skip the outer border
            // since the rect's own border handles that.
            for (int i = 1; i < 8; i++) {
                float fx = rect.x + rect.width * (i / 8f);
                float fy = rect.y + rect.height * (i / 8f);
                EditorGUI.DrawRect(new Rect(fx, rect.y, 1, rect.height), line);
                EditorGUI.DrawRect(new Rect(rect.x, fy, rect.width, 1), line);
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
    }
}
