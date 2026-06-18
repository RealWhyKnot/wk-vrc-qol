// ClippingFixerWindow.Scan.cs

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Clipping;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class ClippingFixerWindow {

        private void Scan() {
            _issues.Clear();
            _selectedIssueIndices.Clear();
            _scanSummary = "";
            if (!CanScan()) return;

            var surfaces = BuildSurfaceList();
            _lastSurfaceRendererCount = surfaces.Count;
            var log = _verboseLog ? new StringBuilder() : null;
            log?.AppendLine("Clipping Fixer verbose log");
            log?.AppendLine($"  target={PathUtility.GetGameObjectPath(_targetRenderer.gameObject)}");
            log?.AppendLine($"  comparisonRenderers={surfaces.Count}, checkSelf={_checkSelf}, includePhysBoneMotion={_includePhysBoneMotion}");
            log?.AppendLine($"  insideTolerance={_insideTolerance:F4}, surfacePadding={_surfacePadding:F3}, maxWarnings={_maxWarnings}");
            log?.AppendLine($"  physBoneWeightFloor={_physBoneWeightFloor:F3}, physBoneClearance={_physBoneClearanceMargin:F3}, maxIssuesPerPhysBone={_maxIssuesPerPhysBone}");

            double start = EditorApplication.timeSinceStartup;
            var settings = BuildFixerSettings();
            _issues.AddRange(ClippingFixer.Scan(
                _targetRenderer,
                surfaces,
                settings,
                log));
            double elapsed = EditorApplication.timeSinceStartup - start;

            string surfaceText = surfaces.Count == 0
                ? (_checkSelf ? "self only" : "no comparison meshes")
                : $"{surfaces.Count} comparison mesh(es)" + (_checkSelf ? " + self" : "");
            int motionWarnings = 0;
            foreach (var issue in _issues) {
                if (issue != null && issue.Kind == ClippingFixer.IssueKind.PhysBoneMotion) motionWarnings++;
            }
            string motionText = _includePhysBoneMotion
                ? $", including {motionWarnings} PhysBone warning(s)"
                : "";
            _scanSummary = $"Scanned 1 mesh vs {surfaceText} in {elapsed:0.0}s; found {_issues.Count} clipping warning(s){motionText}.";
            if (log != null) {
                log.AppendLine($"  elapsed={elapsed:0.000}s");
                log.AppendLine($"  warnings={_issues.Count}");
                AvatarQolLogger.Instance.Info(log.ToString());
            }
            SceneView.RepaintAll();
        }

        private List<SkinnedMeshRenderer> BuildSurfaceList() {
            var list = new List<SkinnedMeshRenderer>();
            foreach (var renderer in _comparisonRenderers) {
                if (renderer == null || renderer.sharedMesh == null || !renderer.sharedMesh.isReadable) continue;
                if (!list.Contains(renderer)) list.Add(renderer);
            }
            return list;
        }

        private ClippingFixer.Settings BuildFixerSettings() {
            return new ClippingFixer.Settings {
                Animator = _animator,
                CheckSelf = _checkSelf,
                IncludePhysBoneMotion = _includePhysBoneMotion,
                InsideTolerance = _insideTolerance,
                SurfacePadding = _surfacePadding,
                PhysBoneWeightFloor = _physBoneWeightFloor,
                PhysBoneClearanceMargin = _physBoneClearanceMargin,
                PhysBoneMotionPinStrength = _physBoneMotionPinStrength,
                PhysBoneMotionBrushRadius = _physBoneMotionBrushRadius,
                MaxWarnings = _maxWarnings,
                MaxIssuesPerPhysBone = _maxIssuesPerPhysBone,
            };
        }
    }
}
