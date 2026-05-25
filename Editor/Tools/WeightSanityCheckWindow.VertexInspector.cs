// WeightSanityCheckWindow.VertexInspector.cs

using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Components;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.WeightFixes;
using WhyKnot.AvatarQol.Internal.Styling;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class WeightSanityCheckWindow {

        // For each unique mesh referenced by the skipped renderers, find its
        // source asset's ModelImporter and flip Read/Write on. Reimport, then
        // re-run the scan automatically. We only touch ModelImporter assets -
        // procedurally-built or in-memory meshes (where there's no importer)
        // can't be fixed this way and are skipped with a warning.
        private void EnableReadWriteOnSkippedAndRescan() {
            var importersToReimport = new HashSet<string>();
            int unfixable = 0;
            foreach (var r in _nonReadableRenderers) {
                if (r == null || r.sharedMesh == null) continue;
                var path = AssetDatabase.GetAssetPath(r.sharedMesh);
                if (string.IsNullOrEmpty(path)) { unfixable++; continue; }
                var importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer == null) { unfixable++; continue; }
                if (!importer.isReadable) {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                }
                importersToReimport.Add(path);
            }
            if (unfixable > 0) {
                AvatarQolLogger.Instance.Warning(
                    $"{unfixable} skipped mesh(es) had no ModelImporter " +
                    $"(procedurally generated, or imported by a different pipeline). " +
                    $"Read/Write couldn't be auto-enabled on those.");
            }
            if (importersToReimport.Count > 0) {
                AvatarQolLogger.Instance.Info($"Enabled Read/Write on {importersToReimport.Count} model asset(s); rescanning.");
            }
            Scan();
        }

        // Walks every weight on a single vertex and prints the verdict each
        // weight got against current thresholds. The most direct answer to
        // "why didn't this get flagged?".
        private void InspectVertex() {
            var smr = _inspectRenderer;
            if (smr == null) {
                EditorUtility.DisplayDialog("Inspect vertex", "Drop a SkinnedMeshRenderer first.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    "The renderer's mesh is null or not readable. Use 'Enable Read/Write & rescan' above if needed.", "OK");
                return;
            }
            if (_inspectVertexIndex < 0 || _inspectVertexIndex >= mesh.vertexCount) {
                EditorUtility.DisplayDialog("Inspect vertex",
                    $"Vertex index {_inspectVertexIndex} is out of range (mesh has {mesh.vertexCount} vertices).", "OK");
                return;
            }

            var sideMap = new HumanoidSideMap(_animator);
            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var bindposes = mesh.bindposes;

            // Walk to the weight-cursor for the requested vertex.
            int cursor = 0;
            for (int v = 0; v < _inspectVertexIndex; v++) cursor += bonesPerVertex[v];
            int wCount = bonesPerVertex[_inspectVertexIndex];

            // Same bindpose-based world position as Scan: highest-weight
            // bone is the anchor. Falling back to renderer.transform is only
            // hit when the vertex has no usable weights (rare).
            int primaryIdx = -1;
            float primaryWeight = 0f;
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                if (bones[bw.boneIndex] == null) continue;
                if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
            }
            Vector3 worldPos;
            string anchorDesc;
            if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                var meshLocal = verts[_inspectVertexIndex];
                var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                anchorDesc = $"bindpose anchor={bones[primaryIdx].name} (weight {primaryWeight:F3})";
            } else {
                worldPos = smr.transform.TransformPoint(verts[_inspectVertexIndex]);
                anchorDesc = "fallback=renderer.transform (no usable bone weight)";
            }
            var vertexSide = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
            bool isCenter = vertexSide == BoneSide.Center;
            float floor = isCenter ? _centerCrossSideFloor : _weightFloor;

            var sb = new StringBuilder();
            sb.AppendLine($"Inspect vertex #{_inspectVertexIndex} of {PathUtility.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  world pos: ({worldPos.x:F4}, {worldPos.y:F4}, {worldPos.z:F4})  {anchorDesc}");
            sb.AppendLine($"  vertex side: {vertexSide} (isCenter={isCenter}, applicable floor={floor:F4})");
            sb.AppendLine($"  weights ({wCount}):");
            for (int w = 0; w < wCount; w++) {
                var bw = weights[cursor + w];
                Transform bone = bw.boneIndex >= 0 && bw.boneIndex < bones.Length ? bones[bw.boneIndex] : null;
                string boneName = bone != null ? bone.name : $"(invalid index {bw.boneIndex})";
                BoneSide humanoidSide = bone != null ? sideMap.GetSide(bone) : BoneSide.Unknown;
                BoneSide spatialSide = bone != null ? sideMap.ClassifyWorldPosition(bone.position, _centerMargin) : BoneSide.Unknown;
                BoneSide effectiveSide = humanoidSide != BoneSide.Unknown ? humanoidSide : spatialSide;
                string verdict;
                if (bone == null) {
                    verdict = "SKIPPED (invalid bone index)";
                } else if (bw.weight < floor) {
                    verdict = $"SKIPPED (weight {bw.weight:F4} < floor {floor:F4})";
                } else if (effectiveSide == BoneSide.Unknown) {
                    verdict = "SKIPPED (bone has no Humanoid ancestor and pivot is in centre band — Unknown side)";
                } else if (effectiveSide == BoneSide.Center) {
                    verdict = "SKIPPED (bone classified Center — same as central avatar mass)";
                } else if (!isCenter && effectiveSide == vertexSide) {
                    verdict = "SKIPPED (bone same side as vertex)";
                } else {
                    string cat = isCenter ? "center-band" : (humanoidSide != BoneSide.Unknown ? "humanoid" : "spatial");
                    verdict = $"FLAGGED [{cat}]  vertex={vertexSide} bone={effectiveSide}";
                }
                sb.AppendLine($"    {boneName}  weight={bw.weight:F4}  humanoid={humanoidSide}  spatial={spatialSide}  →  {verdict}");
            }
            AvatarQolLogger.Instance.Info(sb.ToString());
            _showConsoleNoticeAfterInspect = true;
        }

        private static void DrawDivider() {
            var rect = EditorGUILayout.GetControlRect(false, 1);
            EditorGUI.DrawRect(rect, new Color(0, 0, 0, 0.18f));
        }
    }
}
