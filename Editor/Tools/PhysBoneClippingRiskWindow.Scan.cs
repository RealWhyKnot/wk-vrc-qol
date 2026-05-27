// PhysBoneClippingRiskWindow.Scan.cs

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.PhysBoneClipping;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal sealed partial class PhysBoneClippingRiskWindow {

        private void Scan() {
            _issues.Clear();
            _scanSummary = "";
            if (!CanScan()) return;

            var surfaces = BuildSurfaceList();
            _lastSurfaceRendererCount = surfaces.Count;
            var log = _verboseLog ? new StringBuilder() : null;
            log?.AppendLine("PhysBone Clipping Fixer verbose log");
            log?.AppendLine($"  target={PathUtility.GetGameObjectPath(_targetRenderer.gameObject)}");
            log?.AppendLine($"  comparisonRenderers={surfaces.Count}, checkSelf={_checkSelf}");
            log?.AppendLine($"  insideTolerance={_insideTolerance:F4}, surfacePadding={_surfacePadding:F3}, maxWarnings={_maxWarnings}");

            double start = EditorApplication.timeSinceStartup;
            var settings = BuildFixerSettings();
            _issues.AddRange(PhysBoneClippingFixer.Scan(
                _targetRenderer,
                surfaces,
                settings,
                log));
            double elapsed = EditorApplication.timeSinceStartup - start;

            string surfaceText = surfaces.Count == 0
                ? (_checkSelf ? "self only" : "no comparison meshes")
                : $"{surfaces.Count} comparison mesh(es)" + (_checkSelf ? " + self" : "");
            _scanSummary = $"Scanned 1 mesh vs {surfaceText} in {elapsed:0.0}s; found {_issues.Count} clipping warning(s).";
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

        private PhysBoneClippingFixer.Settings BuildFixerSettings() {
            return new PhysBoneClippingFixer.Settings {
                CheckSelf = _checkSelf,
                InsideTolerance = _insideTolerance,
                SurfacePadding = _surfacePadding,
                MaxFixPasses = _maxFixPasses,
                MaxWarnings = _maxWarnings,
            };
        }
    }
}
