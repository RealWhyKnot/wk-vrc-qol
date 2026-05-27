// WeightSanityCheckWindow.Scan.cs

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

        // ------ Scan -------------------------------------------------------

        private void Scan() {
            _issues.Clear();
            _selectedIssueIndices.Clear();
            _nonReadableRenderers.Clear();
            _scanSummary = "";
            _lastScanLeftRightVerts = 0;
            _lastScanCenterVerts = 0;
            if (_animator == null || !_animator.isHuman) return;

            var sideMap = new HumanoidSideMap(_animator);
            if (!sideMap.IsValid) {
                _scanSummary = "Animator has no usable Humanoid bindings (Hips missing).";
                return;
            }

            // Honor "Limit scan to" by skipping everything else under the avatar.
            // Without a limit, walk every
            // SkinnedMeshRenderer in the hierarchy.
            SkinnedMeshRenderer[] renderers;
            if (_limitToRenderer != null) {
                renderers = new[] { _limitToRenderer };
            } else {
                renderers = _animator.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            }
            int verticesScanned = 0;
            int renderersScanned = 0;
            var globalLog = _verboseLog ? new StringBuilder() : null;
            globalLog?.AppendLine($"Weight Sanity Check verbose log");
            globalLog?.AppendLine($"  weightFloor={_weightFloor:F4}, centerMargin={_centerMargin:F3}, scanCenterBand={_scanCenterBand}, centerCrossSideFloor={_centerCrossSideFloor:F3}");
            globalLog?.AppendLine($"  avatar={_animator.gameObject.name}, leftSign={sideMap.LeftSignInHipsLocal}");
            if (_limitToRenderer != null) {
                globalLog?.AppendLine($"  filter: limit-to-renderer={PathUtility.GetGameObjectPath(_limitToRenderer.gameObject)}");
            }

            var p = new ScanParameters {
                WeightFloor = _weightFloor,
                CenterMargin = _centerMargin,
                ScanCenterBand = _scanCenterBand,
                CenterCrossSideFloor = _centerCrossSideFloor,
            };

            foreach (var r in renderers) {
                if (r == null || r.sharedMesh == null) continue;
                if (_excludedRenderers.Contains(r)) {
                    globalLog?.AppendLine($"  SKIP renderer (excluded): {PathUtility.GetGameObjectPath(r.gameObject)}");
                    continue;
                }
                var detect = WeightCrossSideDetector.Detect(r, sideMap, p, globalLog);
                verticesScanned += detect.VerticesScanned;
                renderersScanned++;
                if (detect.MeshUnreadable) _nonReadableRenderers.Add(r);
                _issues.AddRange(detect.Issues);
                _lastScanLeftRightVerts += detect.LeftVerts + detect.RightVerts;
                _lastScanCenterVerts += detect.CenterVerts;
            }

            _issues.Sort((a, b) => {
                // RendererPath is cached at scan time, so this survives a
                // renderer being destroyed mid-comparison.
                int rcmp = string.Compare(a.RendererPath, b.RendererPath, System.StringComparison.Ordinal);
                if (rcmp != 0) return rcmp;
                return a.VertexIndex.CompareTo(b.VertexIndex);
            });

            _scanSummary = $"Scanned {verticesScanned} vertices across {renderersScanned} renderer(s); flagged {_issues.Count}.";
            if (globalLog != null) {
                globalLog.AppendLine();
                globalLog.AppendLine($"  total issues flagged: {_issues.Count}");
                AvatarQolLogger.Instance.Info(globalLog.ToString());
            }
            SceneView.RepaintAll();
        }

        // ScanRenderer was moved into WeightFixes/WeightCrossSideDetector
        // (Detect) so the runtime apply hook shares the exact same
        // detection path used by this window. Scan() above now calls into
        // the detector once per renderer.

        // ------ Debug dump for a single renderer ---------------------------

        private void DumpSelectedRendererWeights() {
            var go = Selection.activeGameObject;
            if (go == null) {
                EditorUtility.DisplayDialog("Dump weights", "Select a SkinnedMeshRenderer in the hierarchy first.", "OK");
                return;
            }
            var smr = go.GetComponent<SkinnedMeshRenderer>();
            if (smr == null) {
                EditorUtility.DisplayDialog("Dump weights",
                    $"'{go.name}' has no SkinnedMeshRenderer. Select the actual renderer GameObject.", "OK");
                return;
            }
            if (_animator == null || !_animator.isHuman) {
                EditorUtility.DisplayDialog("Dump weights",
                    "Pick a Humanoid Animator at the top of the window first; we need it for side classification.", "OK");
                return;
            }
            var sideMap = new HumanoidSideMap(_animator);
            var mesh = smr.sharedMesh;
            if (mesh == null || !mesh.isReadable) {
                EditorUtility.DisplayDialog("Dump weights",
                    "The renderer's mesh is null or not readable. Enable Read/Write in the model importer if needed.", "OK");
                return;
            }

            var bones = smr.bones;
            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            var sb = new StringBuilder();
            sb.AppendLine($"Weight dump for {PathUtility.GetGameObjectPath(smr.gameObject)}");
            sb.AppendLine($"  vertices={mesh.vertexCount}, bones={bones.Length}");

            // First, list every bone with its classification - handy when
            // tracking down "why didn't a custom bone get flagged?"
            sb.AppendLine("  bones:");
            for (int b = 0; b < bones.Length; b++) {
                if (bones[b] == null) { sb.AppendLine($"    [{b}] (null)"); continue; }
                var humanoid = sideMap.GetSide(bones[b]);
                var spatial  = sideMap.ClassifyWorldPosition(bones[b].position, _centerMargin);
                sb.AppendLine($"    [{b}] {bones[b].name}  humanoid={humanoid}  spatial={spatial}");
            }

            // Then dump every vertex with its top weights. Limited to the
            // first 200 vertices per renderer to keep the log readable -
            // bump if needed for specific debugging.
            int limit = Mathf.Min(mesh.vertexCount, 200);
            sb.AppendLine($"  first {limit} vertices:");
            int cursor = 0;
            // Same bindpose-based world position as Scan: pick the highest-
            // weight bone, transform mesh-local -> bone-local via bindpose,
            // bone-local -> world via the bone's current transform.
            var bindposes = mesh.bindposes;
            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];
                if (v < limit) {
                    int primaryIdx = -1;
                    float primaryWeight = 0f;
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                        if (bones[bw.boneIndex] == null) continue;
                        if (bw.weight > primaryWeight) { primaryWeight = bw.weight; primaryIdx = bw.boneIndex; }
                    }
                    Vector3 worldPos;
                    if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                        var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(verts[v]);
                        worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                    } else {
                        worldPos = smr.transform.TransformPoint(verts[v]);
                    }
                    var side = sideMap.ClassifyWorldPosition(worldPos, _centerMargin);
                    sb.Append($"    v#{v} on {side} ({worldPos.x:F3},{worldPos.y:F3},{worldPos.z:F3}): ");
                    for (int w = 0; w < wCount; w++) {
                        var bw = weights[cursor + w];
                        var name = bw.boneIndex < bones.Length && bones[bw.boneIndex] != null
                            ? bones[bw.boneIndex].name : "?";
                        sb.Append($"{name}={bw.weight:F3} ");
                    }
                    sb.AppendLine();
                }
                cursor += wCount;
            }
            AvatarQolLogger.Instance.Info(sb.ToString());
            _showConsoleNoticeAfterDump = true;
        }
    }
}
