// MaskPainterWindow.Session.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
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
    }
}
