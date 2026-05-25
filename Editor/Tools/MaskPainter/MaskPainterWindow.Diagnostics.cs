// MaskPainterWindow.Diagnostics.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class MaskPainterWindow {

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
