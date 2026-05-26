// MaskPainterWindow.Session.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

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
            _firstMouseDownLogged = false;
            _bakeConventionLogged = false;
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
                $"  brush shader    : {(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.name : "(null)")} shader.passCount={(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.passCount.ToString() : "0")} material.passCount={(_brushMaterial != null ? _brushMaterial.passCount.ToString() : "0")} supported={(_brushMaterial != null && _brushMaterial.shader != null ? _brushMaterial.shader.isSupported.ToString() : "?")}\n" +
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
            // BakeMesh(useScale: true) outputs vertices in the renderer's
            // pre-localToWorld local space; localToWorldMatrix below takes
            // them to world. The opposite pairing (false + localToWorldMatrix)
            // silently double-counts scale on non-unit SMRs: BakeMesh(false)
            // already factors out the SMR's local scale at the source, so
            // re-applying it through localToWorldMatrix lands the snapshot
            // ~100x off on a typical 100x Blender-import avatar and every
            // SceneView ray misses.
            _renderer.BakeMesh(_snapshotMesh, useScale: true);

            // Force-copy the UV0 channel from the source mesh to the snapshot.
            // The brush shader rasterises triangles into the mask RT by
            // emitting clip coords from v.uv, so a missing or corrupted UV0
            // channel on the snapshot means every triangle collapses to the
            // wrong pixel and the brush appears to "paint the whole visible
            // mesh". BakeMesh has historically been UV-preserving regardless
            // of useScale, but the copy is cheap and removes the dependence.
            // Same for normals, in case anything downstream needs them.
            var sharedSource = _renderer.sharedMesh;
            int snapshotVerts = _snapshotMesh.vertexCount;
            if (sharedSource != null) {
                if (sharedSource.uv != null && sharedSource.uv.Length == snapshotVerts) {
                    _snapshotMesh.uv = sharedSource.uv;
                } else if (sharedSource.uv != null) {
                    Diag(LogLevel.Warn,
                        $"Source mesh UV0 length ({sharedSource.uv.Length}) != snapshot vertex count ({snapshotVerts}); skipping UV copy. The brush shader needs UV0 to land strokes in the right texels -- expect wrong-location painting.");
                }
                if (sharedSource.normals != null && sharedSource.normals.Length == snapshotVerts) {
                    _snapshotMesh.normals = sharedSource.normals;
                }
            }

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

            // Build the world-space paint mesh that ApplyStroke draws via
            // CommandBuffer with an identity model matrix. Vertices live in
            // world space so the brush shader can compute distance directly
            // from POSITION without going through unity_ObjectToWorld, which
            // historically leaks SceneView camera/model state into editor
            // draws issued outside a normal camera render.
            if (_paintWorldMesh == null) {
                _paintWorldMesh = new Mesh {
                    name = "WhyKnotMaskPainter_PaintWorld",
                    hideFlags = HideFlags.HideAndDontSave,
                };
            }
            _paintWorldMesh.Clear();
            _paintWorldMesh.indexFormat = _snapshotMesh.indexFormat;
            _paintWorldMesh.vertices = _snapshotWorldVerts;
            if (_snapshotMesh.uv != null && _snapshotMesh.uv.Length == _snapshotWorldVerts.Length) {
                _paintWorldMesh.uv = _snapshotMesh.uv;
            }
            if (_snapshotMesh.normals != null && _snapshotMesh.normals.Length == _snapshotWorldVerts.Length) {
                _paintWorldMesh.normals = _snapshotMesh.normals;
            }
            _paintWorldMesh.subMeshCount = _snapshotMesh.subMeshCount;
            for (int s = 0; s < _snapshotMesh.subMeshCount; s++) {
                _paintWorldMesh.SetTriangles(_snapshotMesh.GetTriangles(s), s, calculateBounds: false);
            }
            _paintWorldMesh.RecalculateBounds();

            var mesh = _renderer.sharedMesh;
            int shapeCount = mesh != null ? mesh.blendShapeCount : 0;
            _bakedBlendShapes = new float[shapeCount];
            for (int i = 0; i < shapeCount; i++) _bakedBlendShapes[i] = _renderer.GetBlendShapeWeight(i);
            _renderer.transform.hasChanged = false;
            _firstHitLogged = false;
            // Re-arm the submesh-drift warning -- if the user selected
            // submesh N against a 4-submesh mesh and the new bake came
            // back with 3, we want exactly one warning, not none.
            _submeshDriftWarned = false;
            // Snap the stored selector into range against the freshly
            // baked submesh count too, so the UI catches the drift on its
            // next OnGUI even when the user hadn't reopened the picker.
            if (_submeshIndex >= 0 && _submeshIndex >= _snapshotMesh.subMeshCount) {
                Diag(LogLevel.Warn,
                    $"Submesh selector ({_submeshIndex}) is out of range for the freshly-baked snapshot " +
                    $"(subMeshCount={_snapshotMesh.subMeshCount}); resetting to All.");
                _submeshIndex = -1;
            }
            VerifyBakeConvention();
            Diag(LogLevel.Trace,
                $"Bake complete: verts={verts.Length}, submeshes={_snapshotMesh.subMeshCount}, worldBounds=({_snapshotWorldBounds.min} .. {_snapshotWorldBounds.max}), size={_snapshotWorldBounds.size}");

            // UV range probe -- fires at INFO so it surfaces without
            // Verbose Log. The brush shader emits clip coords from v.uv;
            // UVs outside [0,1] would put triangles offscreen, and UVs
            // all clustered at a single value would smush everything
            // into a single pixel.
            var snapUvsForRange = _snapshotMesh.uv;
            if (snapUvsForRange != null && snapUvsForRange.Length > 0) {
                float uMin = float.PositiveInfinity, uMax = float.NegativeInfinity;
                float vMin = float.PositiveInfinity, vMax = float.NegativeInfinity;
                int outOfUnitBox = 0;
                for (int i = 0; i < snapUvsForRange.Length; i++) {
                    var uv = snapUvsForRange[i];
                    if (uv.x < uMin) uMin = uv.x;
                    if (uv.x > uMax) uMax = uv.x;
                    if (uv.y < vMin) vMin = uv.y;
                    if (uv.y > vMax) vMax = uv.y;
                    if (uv.x < -0.01f || uv.x > 1.01f || uv.y < -0.01f || uv.y > 1.01f) outOfUnitBox++;
                }
                Diag(LogLevel.Info,
                    $"Snapshot UV0 range: u=[{uMin:0.000}..{uMax:0.000}], v=[{vMin:0.000}..{vMax:0.000}], samples outside [0,1]: {outOfUnitBox} of {snapUvsForRange.Length}. First UV: {snapUvsForRange[0]}.");
            } else {
                Diag(LogLevel.Warn,
                    "Snapshot UV0 channel is null or empty -- the brush shader can't rasterise to the mask RT without UVs. Strokes will land at clip (-1,-1) and the painter will appear to do nothing.");
            }

            // Detailed verbose-only dump that pins down the brush dispatch
            // inputs: snapshot UV channel sanity, snapshot local bounds vs
            // the world bounds we just computed, and the matrix used. If a
            // future user reports "brush paints in the wrong place", this
            // tells us whether v.uv, v.vertex, or unity_ObjectToWorld is
            // the culprit. (See also DumpFirstMouseDown for ray-side.)
            var snapUvs = _snapshotMesh.uv;
            var srcUvs  = sharedSource != null ? sharedSource.uv : null;
            int uvMatchSamples = 0;
            int uvMismatchSamples = 0;
            if (snapUvs != null && srcUvs != null) {
                int n = Mathf.Min(snapUvs.Length, srcUvs.Length);
                int step = Mathf.Max(1, n / 16);
                for (int i = 0; i < n; i += step) {
                    if (snapUvs[i] == srcUvs[i]) uvMatchSamples++;
                    else uvMismatchSamples++;
                }
            }
            string firstSnapVert = verts.Length > 0
                ? verts[0].ToString("F5")
                : "(none)";
            string firstWorldVert = verts.Length > 0
                ? _snapshotWorldVerts[0].ToString("F5")
                : "(none)";
            string firstSrcUv = (srcUvs != null && srcUvs.Length > 0)
                ? srcUvs[0].ToString("F5")
                : "(none)";
            string firstSnapUv = (snapUvs != null && snapUvs.Length > 0)
                ? snapUvs[0].ToString("F5")
                : "(none)";
            Diag(LogLevel.Trace,
                $"Bake detail:\n" +
                $"  snapshot mesh.bounds (local) : {_snapshotMesh.bounds}\n" +
                $"  snapshot vertexCount         : {snapshotVerts}, source vertexCount: {(sharedSource != null ? sharedSource.vertexCount : 0)}\n" +
                $"  snapshot UV array length     : {(snapUvs != null ? snapUvs.Length : 0)}, source UV length: {(srcUvs != null ? srcUvs.Length : 0)}\n" +
                $"  UV sample match (1/16th step): {uvMatchSamples} matched, {uvMismatchSamples} mismatched\n" +
                $"  first source UV              : {firstSrcUv}\n" +
                $"  first snapshot UV            : {firstSnapUv}\n" +
                $"  first snapshot vertex (local): {firstSnapVert}\n" +
                $"  first snapshot vertex (world): {firstWorldVert}\n" +
                $"  matrix (localToWorld)        : {matrix}");
            if (uvMismatchSamples > 0) {
                Diag(LogLevel.Warn,
                    $"Snapshot UV0 channel does not match the source mesh's UV0 ({uvMismatchSamples} of {uvMatchSamples + uvMismatchSamples} samples differ). Brush strokes will land in the wrong texels until the cause is found.");
            }
        }

        // One-shot per session: confirm the BakeMesh(true) + localToWorldMatrix
        // pairing produced world bounds that actually overlap _renderer.bounds.
        // A wildly mismatched ratio is the smoking gun for the rootBone-vs-SMR
        // coordinate-space class of bug, where the SMR transform has scale 1
        // but the bones inherit a 100x parent scale (a common VRChat hierarchy).
        // Logs INFO once on first bake, then stays quiet.
        private bool _bakeConventionLogged;
        private void VerifyBakeConvention() {
            if (_bakeConventionLogged) return;
            _bakeConventionLogged = true;
            var rb = _renderer.bounds;
            float snapDiag = _snapshotWorldBounds.size.magnitude;
            float rendDiag = rb.size.magnitude;
            float ratio    = rendDiag > 0.0001f ? snapDiag / rendDiag : 0f;
            string verdict = (ratio >= 0.5f && ratio <= 2f) ? "OK" : "MISMATCH";
            Diag(LogLevel.Info,
                $"Bake convention check [{verdict}]: snapshotWorldBounds size={_snapshotWorldBounds.size} " +
                $"(diag {snapDiag:F3}m), renderer.bounds size={rb.size} (diag {rendDiag:F3}m), " +
                $"ratio={ratio:F3} (expect ~1.0). SMR lossyScale={_renderer.transform.lossyScale}, " +
                $"rootBone={(_renderer.rootBone != null ? _renderer.rootBone.name : "(null)")}.");
            if (verdict == "MISMATCH") {
                Diag(LogLevel.Warn,
                    "Bake convention MISMATCH: snapshot bounds and renderer.bounds disagree by >2x. " +
                    "Clicks may miss because triangles aren't in the world space the SceneView ray walks. " +
                    "Common cause: rootBone parent carries a scale (e.g. 100x Blender import) while the SMR " +
                    "transform sits at scale 1. Try setting the avatar root scale to 1, or report this with " +
                    "the Dump State output.");
            }
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
            if (_maskRT == null || _brushMaterial == null || _paintWorldMesh == null || _renderer == null) return;

            // Upload uniforms onto the material itself (not a MaterialPropertyBlock)
            // so the GetVector/GetFloat readback below still reflects what's on the
            // GPU. CommandBuffer.DrawMesh consults the material's properties when
            // no per-draw MPB is supplied.
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

            int passIndex = PassForMode();

            // First-dispatch uniform readback. SetVector / SetFloat silently
            // no-op if the shader doesn't declare the uniform under that
            // exact name; reading back through the same material confirms
            // the upload reached the GPU side.
            if (_dispatchCount == 0) {
                var rbCenter = _brushMaterial.GetVector("_BrushCenter");
                var rbRadius = _brushMaterial.GetFloat("_BrushRadius");
                var rbSym    = _brushMaterial.GetFloat("_SymmetryEnabled");
                var rbStr    = _brushMaterial.GetFloat("_Strength");
                var rbHard   = _brushMaterial.GetFloat("_BrushHardness");
                Diag(LogLevel.Info,
                    $"Brush uniform readback:\n" +
                    $"  _BrushCenter (sent {_hitWorld})       -> {rbCenter}\n" +
                    $"  _BrushRadius (sent {_radius:F6})      -> {rbRadius:F6}\n" +
                    $"  _SymmetryEnabled (sent {(_symmetryEnabled ? 1f : 0f)}) -> {rbSym}\n" +
                    $"  _Strength (sent {_strength:F3})       -> {rbStr:F3}\n" +
                    $"  _BrushHardness (sent {_hardness:F3})  -> {rbHard:F3}\n" +
                    $"  renderer localToWorld (baked into _paintWorldMesh) -> {_renderer.transform.localToWorldMatrix}\n" +
                    $"  dispatch matrix (passed to CommandBuffer.DrawMesh) -> identity (mesh verts are already world-space)\n" +
                    $"  pass index                              -> {passIndex}");
            }

            var range = MaskPainterIO.SubmeshRange(_submeshIndex, _paintWorldMesh.subMeshCount, WarnSubmeshDrift);

            // Route the draw through a CommandBuffer rather than
            // Graphics.SetRenderTarget + material.SetPass + Graphics.DrawMeshNow.
            // The immediate-mode path inherits GPU state (most importantly model
            // and view matrices) from whatever the SceneView render loop drew
            // most recently, which made the brush stamp the camera-visible UV
            // region instead of the small world-radius patch around the click.
            // CommandBuffer.SetRenderTarget restores the previous active target
            // after ExecuteCommandBuffer, so the manual RenderTexture.active
            // save/restore that wrapped the old DrawMeshNow call is no longer
            // needed.
            var cmd = new CommandBuffer { name = "MaskPainter Apply Stroke" };
            try {
                cmd.SetRenderTarget(_maskRT);
                cmd.SetViewport(new Rect(0, 0, _maskRT.width, _maskRT.height));
                // Identity view/projection scrubs any SceneView VP state that
                // might have stuck. The brush shader's vert writes clip coords
                // directly from UV so these matrices shouldn't matter, but
                // setting them makes the contract explicit and shuts down one
                // more potential leak channel.
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                for (int s = range.start; s < range.end; s++) {
                    cmd.DrawMesh(_paintWorldMesh, Matrix4x4.identity, _brushMaterial, s, passIndex);
                }
                Graphics.ExecuteCommandBuffer(cmd);
            } finally {
                cmd.Release();
            }
            _lastStrokeTime = EditorApplication.timeSinceStartup;
            _dispatchCount++;
            _strokeDispatches++;
            if (_dispatchCount == 1) {
                Diag(LogLevel.Info,
                    $"First stroke dispatched. center={_hitWorld}, radius={_radius:F4}m, mode={(_erase ? "erase" : "paint")}, pass={PassForMode()}, submeshes={(_submeshIndex < 0 ? "all" : _submeshIndex.ToString())}");

                // Snapshot-positioning sanity: find the snapshot vertex
                // closest to _BrushCenter in world space, log its distance.
                // If the closest vertex is hundreds of metres away, the
                // brush will land outside every triangle's radius and the
                // user will see nothing. If it's millimetres away (typical),
                // the shader's interpolation should put fragments near the
                // brush center within the radius -- and only those should
                // pass the clip.
                if (_snapshotWorldVerts != null && _snapshotWorldVerts.Length > 0) {
                    float minDist = float.PositiveInfinity;
                    int minIdx = -1;
                    for (int i = 0; i < _snapshotWorldVerts.Length; i++) {
                        float d = Vector3.Distance(_snapshotWorldVerts[i], _hitWorld);
                        if (d < minDist) { minDist = d; minIdx = i; }
                    }
                    var mesh = _renderer != null ? _renderer.sharedMesh : null;
                    Vector2 closestUv = (mesh != null && mesh.uv != null && minIdx >= 0 && minIdx < mesh.uv.Length) ? mesh.uv[minIdx] : Vector2.zero;
                    Diag(LogLevel.Info,
                        $"  closest snapshot vertex to brush center: idx={minIdx}, vert(world)={_snapshotWorldVerts[minIdx]}, distance={minDist:F4}m, uv={closestUv}, hitUv={_hitUv}.");
                }

                // Sanity check at first dispatch: if the brush world-radius
                // is the same order of magnitude (or larger) than the
                // snapshot's world bounds diagonal, distance-based clipping
                // in the shader will let every fragment through and the
                // brush will appear to paint the entire visible mesh.
                // Past bug signature, kept as a guard.
                float boundsDiag = _snapshotWorldBounds.size.magnitude;
                if (boundsDiag > 0.0001f && _radius >= boundsDiag * 0.25f) {
                    Diag(LogLevel.Warn,
                        $"Brush radius ({_radius:F3}m) is >=25% of snapshot world-bounds diagonal ({boundsDiag:F3}m). Strokes will cover most or all of the visible mesh -- check that the SMR scale and brush radius are in compatible units.");
                }

                // Auto-probe the mask RT immediately after the first
                // stroke so we don't depend on the user clicking the
                // Probe button. The readback is ~5 ms at 1024x1024;
                // running it once per session is fine. Coverage > a
                // few percent on a single small stroke is a red flag.
                ProbeMaskRT();
            }
            // Per-stroke verbose trace. Helps when the user reports
            // "strokes land in the wrong place" -- correlates dispatch
            // count with brush parameters and hit location. Promoted to
            // INFO for the first 3 dispatches so it surfaces without
            // requiring Verbose Log to be enabled.
            var perStrokeLine =
                $"Stroke dispatch #{_dispatchCount} (in-stroke #{_strokeDispatches}): center={_hitWorld}, hitUv={_hitUv}, radius={_radius:F4}m, strength={_strength:F2}, hardness={_hardness:F2}, pass={PassForMode()}, symmetry={(_symmetryEnabled ? "on" : "off")}.";
            if (_dispatchCount <= 3) Diag(LogLevel.Info, perStrokeLine);
            else                     Diag(LogLevel.Trace, perStrokeLine);
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
    }
}
