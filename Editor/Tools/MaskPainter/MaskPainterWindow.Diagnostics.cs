// MaskPainterWindow.Diagnostics.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

        // First click of every paint session: dump the full ray /
        // BakeMesh / bounds / submesh picture. Costs one extra BakeMesh
        // (two more if comparing useScale=true vs false) but only on the
        // first MouseDown, and only when the user has hit a class of bug
        // where every click misses -- they pay nothing in the steady
        // state.
        private bool _firstMouseDownLogged;

        // ---- Diagnostics: dump state ----

        private static Bounds TransformBoundsForDiag(Vector3[] verts, Matrix4x4 m) {
            if (verts == null || verts.Length == 0) return new Bounds();
            var p = m.MultiplyPoint3x4(verts[0]);
            var b = new Bounds(p, Vector3.zero);
            for (int i = 1; i < verts.Length; i++) {
                b.Encapsulate(m.MultiplyPoint3x4(verts[i]));
            }
            return b;
        }

        private void DumpFirstMouseDown(Ray ray, Vector2 mousePos) {
            if (_firstMouseDownLogged) return;
            _firstMouseDownLogged = true;
            if (_renderer == null || _snapshotMesh == null) return;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("Mask Painter first-click diagnostic dump:");
            sb.AppendLine($"  gui mouse        : {mousePos}");
            sb.AppendLine($"  ray.origin       : {ray.origin}");
            sb.AppendLine($"  ray.direction    : {ray.direction}");
            sb.AppendLine($"  renderer.bounds  : {_renderer.bounds}");
            sb.AppendLine($"  snapshot bounds  : {_snapshotWorldBounds}");

            bool snapshotHit = _snapshotWorldBounds.IntersectRay(ray, out float snapT);
            bool rendererHit = _renderer.bounds.IntersectRay(ray, out float rendT);
            sb.AppendLine($"  ray vs snapshotBounds : hit={snapshotHit} t={snapT:F3}");
            sb.AppendLine($"  ray vs renderer.bounds: hit={rendererHit} t={rendT:F3}");

            sb.AppendLine($"  lossyScale       : {_renderer.transform.lossyScale}");
            sb.AppendLine($"  rootBone         : {(_renderer.rootBone != null ? _renderer.rootBone.name : "(null)")}");
            sb.AppendLine($"  submesh selector : {(_submeshIndex < 0 ? "All" : _submeshIndex.ToString())}");
            sb.AppendLine($"  snapshot submeshes : {_snapshotMesh.subMeshCount}");

            // Bake both ways into throwaway meshes so the user can see in
            // one log whether either convention matches renderer.bounds.
            // The live snapshot mesh keeps its existing data; we don't
            // touch it.
            var diagFalse = new Mesh { name = "WhyKnotMaskPainter_DiagFalse", hideFlags = HideFlags.HideAndDontSave };
            var diagTrue  = new Mesh { name = "WhyKnotMaskPainter_DiagTrue",  hideFlags = HideFlags.HideAndDontSave };
            try {
                _renderer.BakeMesh(diagFalse, useScale: false);
                _renderer.BakeMesh(diagTrue,  useScale: true);
                var fullMatrix    = _renderer.transform.localToWorldMatrix;
                var noScaleMatrix = Matrix4x4.TRS(
                    _renderer.transform.position, _renderer.transform.rotation, Vector3.one);
                var rootMatrix    = _renderer.rootBone != null
                    ? _renderer.rootBone.localToWorldMatrix
                    : Matrix4x4.identity;

                var bFalseFull    = TransformBoundsForDiag(diagFalse.vertices, fullMatrix);
                var bFalseNoScale = TransformBoundsForDiag(diagFalse.vertices, noScaleMatrix);
                var bTrueFull     = TransformBoundsForDiag(diagTrue.vertices,  fullMatrix);
                var bTrueNoScale  = TransformBoundsForDiag(diagTrue.vertices,  noScaleMatrix);
                var bRootScale    = TransformBoundsForDiag(_snapshotMesh.vertices, rootMatrix);

                sb.AppendLine($"  BakeMesh(false) local bounds : {diagFalse.bounds}");
                sb.AppendLine($"    x localToWorldMatrix       : {bFalseFull}");
                sb.AppendLine($"    x TRS(pos, rot, one)       : {bFalseNoScale}");
                sb.AppendLine($"  BakeMesh(true)  local bounds : {diagTrue.bounds}");
                sb.AppendLine($"    x localToWorldMatrix       : {bTrueFull}");
                sb.AppendLine($"    x TRS(pos, rot, one)       : {bTrueNoScale}");
                sb.AppendLine($"  current snapshot vs rootBone.localToWorld : {bRootScale}");
            } finally {
                DestroyImmediate(diagFalse);
                DestroyImmediate(diagTrue);
            }

            // Submesh topology / index sanity per submesh.
            for (int s = 0; s < _snapshotMesh.subMeshCount; s++) {
                var topo = _snapshotMesh.GetTopology(s);
                var tris = _snapshotMesh.GetTriangles(s);
                int min = int.MaxValue, max = int.MinValue;
                for (int i = 0; i < tris.Length; i++) {
                    if (tris[i] < min) min = tris[i];
                    if (tris[i] > max) max = tris[i];
                }
                sb.AppendLine(
                    $"  submesh #{s}: topology={topo} indices={tris.Length} tris={tris.Length / 3} " +
                    $"vertIndexRange=[{(tris.Length == 0 ? -1 : min)}..{(tris.Length == 0 ? -1 : max)}] " +
                    $"vertexCount={_snapshotMesh.vertexCount}");
            }

            AvatarQolLogger.Instance.Info(sb.ToString());
        }

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

        // Read back the mask RT and dump a coverage histogram. The point is
        // to answer "where did the brush actually land?" -- if the painted
        // pixels are tightly clustered near where the user clicked, the
        // shader is working correctly. If they cover most of the RT, the
        // distance test is failing and every fragment is passing through.
        // Quadrant counts give a rough idea of which UV regions got painted
        // without needing a full pixel-by-pixel readout.
        private void ProbeMaskRT() {
            if (_maskRT == null) {
                Diag(LogLevel.Warn, "Probe RT: no mask buffer allocated (start a paint session first).");
                return;
            }
            Texture2D tex = null;
            try {
                tex = SnapshotRT();
                var pixels = tex.GetPixels32();
                int w = tex.width;
                int h = tex.height;

                // Per-channel non-zero counts (>= ~0.8% above zero) so the
                // user can tell which channels were actually written, and
                // a 4x4 grid histogram across UV space so the painted
                // region's shape is visible without a per-pixel dump.
                const byte threshold = 2;
                int rN = 0, gN = 0, bN = 0, aN = 0;
                int minPx = w, maxPx = -1, minPy = h, maxPy = -1;
                long sumPx = 0, sumPy = 0;
                int totalLit = 0;
                int[,] grid = new int[4, 4]; // 4x4 UV histogram (x,y)
                for (int y = 0; y < h; y++) {
                    int gy = (y * 4) / h;
                    for (int x = 0; x < w; x++) {
                        int gx = (x * 4) / w;
                        var p = pixels[y * w + x];
                        bool any = false;
                        if (p.r >= threshold) { rN++; any = true; }
                        if (p.g >= threshold) { gN++; any = true; }
                        if (p.b >= threshold) { bN++; any = true; }
                        if (p.a >= threshold) { aN++; any = true; }
                        if (any) {
                            totalLit++;
                            grid[gx, gy]++;
                            if (x < minPx) minPx = x;
                            if (x > maxPx) maxPx = x;
                            if (y < minPy) minPy = y;
                            if (y > maxPy) maxPy = y;
                            sumPx += x;
                            sumPy += y;
                        }
                    }
                }
                int totalPixels = w * h;
                float coverage = totalPixels > 0 ? (totalLit * 100f / totalPixels) : 0f;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"Probe Mask RT: {w}x{h} ({totalPixels:N0} pixels).");
                sb.AppendLine($"  any-channel lit       : {totalLit:N0} pixels ({coverage:0.00}% coverage)");
                sb.AppendLine($"  per-channel lit count : R={rN:N0}, G={gN:N0}, B={bN:N0}, A={aN:N0}");
                if (totalLit > 0) {
                    float uMin = (float)minPx / w;
                    float uMax = (float)(maxPx + 1) / w;
                    float vMin = (float)minPy / h;
                    float vMax = (float)(maxPy + 1) / h;
                    float uCom = (float)sumPx / (totalLit * w);
                    float vCom = (float)sumPy / (totalLit * h);
                    sb.AppendLine($"  lit bounding box (UV) : u=[{uMin:0.000}..{uMax:0.000}], v=[{vMin:0.000}..{vMax:0.000}]");
                    sb.AppendLine($"  lit centre of mass UV : ({uCom:0.000}, {vCom:0.000})");
                    // 4x4 grid -- read top row first (v=high) so the layout
                    // matches the on-screen preview orientation.
                    sb.AppendLine("  4x4 UV histogram (rows are v top->bottom, cols are u left->right):");
                    for (int gy = 3; gy >= 0; gy--) {
                        var row = new System.Text.StringBuilder("    ");
                        for (int gx = 0; gx < 4; gx++) {
                            int cell = grid[gx, gy];
                            float cellPct = totalLit > 0 ? cell * 100f / totalLit : 0f;
                            row.Append(string.Format("{0,7:0.0}% ", cellPct));
                        }
                        sb.AppendLine(row.ToString());
                    }
                } else {
                    sb.AppendLine("  (no lit pixels found -- nothing has been painted yet, or painting is going somewhere this probe can't see)");
                }
                AvatarQolLogger.Instance.Info(sb.ToString());
            } finally {
                if (tex != null) DestroyImmediate(tex);
            }
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
