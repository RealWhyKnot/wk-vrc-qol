// WeightCrossSideDetector.cs
//
// Pure-function port of WeightSanityCheckWindow.ScanRenderer's per-vertex
// loop, broken out so the interactive UI and the runtime apply hook can
// share one detection path. Caller supplies the renderer, a built
// HumanoidSideMap, the scan parameters, and an optional log builder; the
// detector walks the mesh and returns every flagged cross-side weight.
//
// What the detector does NOT do:
//   - Skip renderers in any per-window exclusion list. The UI applies its
//     own exclude pass before calling.
//   - Handle mesh.isReadable=false. The UI handles the offer-to-enable
//     UX; the runtime hook just sees zero results from non-readable
//     meshes (and the verbose log line surfaces why).
//   - Mutate any state. Pure detection. The fixer is a separate step.
//
// Vertex world-position derivation matches the original ScanRenderer:
// take the highest-weight bone, transform mesh-local -> bone-local via
// mesh.bindposes[boneIdx], then bone.TransformPoint into world. Assumes
// the avatar is at or near bind pose during the scan; an animator-driven
// scene should be paused / scrubbed to T-pose before this is run. The
// runtime hook runs this at play-mode entry BEFORE the animator drives a
// frame (callbackOrder = -5000 + ExitingEditMode), so the bind-pose
// assumption holds for play-mode application without operator action.

using System.Collections.Generic;
using System.Text;
using UnityEngine;
using WhyKnot.Core.Utilities;

namespace WhyKnot.AvatarQol.WeightFixes {

    internal static class WeightCrossSideDetector {

        internal struct DetectorResult {
            public List<DetectedIssue> Issues;
            public int VerticesScanned;
            // Per-side breakdown of classified vertices. When LeftVerts +
            // RightVerts is much smaller than CenterVerts, the UI can
            // surface a "your centre margin is swallowing the mesh" hint.
            public int LeftVerts;
            public int RightVerts;
            public int CenterVerts;
            public bool MeshUnreadable;
            public bool NoBones;
            public bool EarlyExitNoCrossSide;
        }

        /// <summary>
        /// Walks <paramref name="renderer"/>'s shared mesh and returns
        /// every cross-side weight at or above <paramref name="p"/>'s
        /// thresholds. Result populates a fresh List on each call; caller
        /// owns the lifetime.
        /// </summary>
        internal static DetectorResult Detect(
                SkinnedMeshRenderer renderer,
                HumanoidSideMap sideMap,
                ScanParameters p,
                StringBuilder log) {

            var result = new DetectorResult { Issues = new List<DetectedIssue>() };
            if (renderer == null || renderer.gameObject == null) return result;
            var mesh = renderer.sharedMesh;
            if (mesh == null) return result;
            var bones = renderer.bones;
            if (bones == null || bones.Length == 0) {
                log?.AppendLine($"  SKIP renderer (no bones array): {PathUtility.GetGameObjectPath(renderer.gameObject)}");
                result.NoBones = true;
                return result;
            }
            if (!mesh.isReadable) {
                log?.AppendLine($"  SKIP renderer (mesh not readable; enable Read/Write in the model importer): {PathUtility.GetGameObjectPath(renderer.gameObject)}");
                result.MeshUnreadable = true;
                return result;
            }

            // Tag every bone the renderer references. Layered: Humanoid
            // ancestor (high confidence), then spatial fallback by the
            // bone's own world position relative to the avatar's centre
            // axis (catches custom prop / skirt-rig bones that have no
            // Humanoid ancestor).
            var boneSides = new BoneSide[bones.Length];
            int countLeft = 0, countRight = 0, countCenter = 0, countUnknown = 0, countSpatial = 0;
            for (int i = 0; i < bones.Length; i++) {
                if (bones[i] == null) { boneSides[i] = BoneSide.Unknown; countUnknown++; continue; }
                var humanoidSide = sideMap.GetSide(bones[i]);
                if (humanoidSide != BoneSide.Unknown) {
                    boneSides[i] = humanoidSide;
                } else {
                    var spatial = sideMap.ClassifyWorldPosition(bones[i].position, p.CenterMargin);
                    boneSides[i] = spatial;
                    if (spatial == BoneSide.Left || spatial == BoneSide.Right) countSpatial++;
                }
                switch (boneSides[i]) {
                    case BoneSide.Left:    countLeft++;    break;
                    case BoneSide.Right:   countRight++;   break;
                    case BoneSide.Center:  countCenter++;  break;
                    case BoneSide.Unknown: countUnknown++; break;
                }
            }
            log?.AppendLine($"  RENDER {PathUtility.GetGameObjectPath(renderer.gameObject)}: " +
                            $"{bones.Length} bones (L={countLeft} R={countRight} C={countCenter} U={countUnknown}; " +
                            $"{countSpatial} of those by spatial fallback)");
            result.VerticesScanned = mesh.vertexCount;
            if (countLeft == 0 || countRight == 0) {
                log?.AppendLine($"    EARLY-EXIT: no cross-side mismatch possible (need both Left and Right bones in the renderer's bones array).");
                result.EarlyExitNoCrossSide = true;
                return result;
            }

            var verts = mesh.vertices;
            var weights = mesh.GetAllBoneWeights();
            var bonesPerVertex = mesh.GetBonesPerVertex();
            // bindposes[i] transforms a mesh-local point into bone-i-local
            // coordinates AT BIND POSE. We use this plus bone.TransformPoint
            // (current pose, assumed ~ bind pose) to derive each vertex's
            // world position correctly regardless of where the renderer's
            // GameObject sits in the hierarchy.
            var bindposes = mesh.bindposes;
            if (verts.Length != mesh.vertexCount || bonesPerVertex.Length != mesh.vertexCount) {
                log?.AppendLine($"    SKIP: vertex/bonesPerVertex length mismatch ({verts.Length}/{bonesPerVertex.Length} vs vertexCount={mesh.vertexCount}).");
                return result;
            }
            if (bindposes == null || bindposes.Length < bones.Length) {
                log?.AppendLine($"    WARN: bindposes incomplete ({bindposes?.Length ?? 0} for {bones.Length} bones); falling back to renderer.transform for affected vertices.");
            }

            int weightCursor = 0;
            int sLeftVerts = 0, sRightVerts = 0, sCenterVerts = 0;
            int wSkippedFloor = 0, wSkippedCenter = 0, wSkippedUnknown = 0, wSkippedSameSide = 0;
            int wFlaggedHumanoid = 0, wFlaggedSpatial = 0, wFlaggedCenterBand = 0;

            string rendererPath = PathUtility.GetGameObjectPath(renderer.gameObject);

            for (int v = 0; v < mesh.vertexCount; v++) {
                int wCount = bonesPerVertex[v];

                int primaryIdx = -1;
                float primaryWeight = 0f;
                for (int w = 0; w < wCount; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bones[bw.boneIndex] == null) continue;
                    if (bw.weight > primaryWeight) {
                        primaryWeight = bw.weight;
                        primaryIdx = bw.boneIndex;
                    }
                }
                Vector3 worldPos;
                if (primaryIdx >= 0 && bindposes != null && primaryIdx < bindposes.Length) {
                    var meshLocal = verts[v];
                    var boneLocal = bindposes[primaryIdx].MultiplyPoint3x4(meshLocal);
                    worldPos = bones[primaryIdx].TransformPoint(boneLocal);
                } else {
                    worldPos = renderer.transform.TransformPoint(verts[v]);
                }

                var vertexSide = sideMap.ClassifyWorldPosition(worldPos, p.CenterMargin);
                if (vertexSide == BoneSide.Left)        sLeftVerts++;
                else if (vertexSide == BoneSide.Right)  sRightVerts++;
                else                                     sCenterVerts++;

                bool isCenterVertex = vertexSide == BoneSide.Center;
                if (isCenterVertex && !p.ScanCenterBand) {
                    weightCursor += wCount;
                    continue;
                }
                float vertexFloor = isCenterVertex ? p.CenterCrossSideFloor : p.WeightFloor;

                for (int w = 0; w < wCount; w++) {
                    var bw = weights[weightCursor + w];
                    if (bw.boneIndex < 0 || bw.boneIndex >= bones.Length) continue;
                    if (bw.weight < vertexFloor) { wSkippedFloor++; continue; }
                    var bSide = boneSides[bw.boneIndex];
                    if (bSide == BoneSide.Unknown) { wSkippedUnknown++; continue; }
                    if (bSide == BoneSide.Center)  { wSkippedCenter++;  continue; }
                    // For Left/Right vertices: flag iff bone is the OPPOSITE side.
                    // For Center vertices: flag iff bone is Left OR Right.
                    if (!isCenterVertex && bSide == vertexSide) { wSkippedSameSide++; continue; }

                    var bone = bones[bw.boneIndex];
                    if (bone == null) continue;
                    bool spatialClassification = sideMap.GetSide(bone) == BoneSide.Unknown;
                    IssueCategory category;
                    if (isCenterVertex) {
                        category = IssueCategory.CenterBandSideBleed;
                        wFlaggedCenterBand++;
                    } else if (spatialClassification) {
                        category = IssueCategory.SpatialCrossSide;
                        wFlaggedSpatial++;
                    } else {
                        category = IssueCategory.HumanoidCrossSide;
                        wFlaggedHumanoid++;
                    }
                    result.Issues.Add(new DetectedIssue {
                        Renderer       = renderer,
                        RendererPath   = rendererPath,
                        VertexIndex    = v,
                        WorldPosition  = worldPos,
                        VertexSide     = vertexSide,
                        OffendingBone  = bone,
                        BoneSide       = bSide,
                        Weight         = bw.weight,
                        Category       = category,
                    });
                }
                weightCursor += wCount;
            }
            log?.AppendLine($"    verts L={sLeftVerts} R={sRightVerts} C={sCenterVerts}");
            log?.AppendLine($"    weights skipped: floor={wSkippedFloor} center-bone={wSkippedCenter} unknown-bone={wSkippedUnknown} same-side={wSkippedSameSide}");
            log?.AppendLine($"    weights flagged: humanoid={wFlaggedHumanoid} spatial={wFlaggedSpatial} center-band={wFlaggedCenterBand}");
            result.LeftVerts = sLeftVerts;
            result.RightVerts = sRightVerts;
            result.CenterVerts = sCenterVerts;
            return result;
        }
    }
}
