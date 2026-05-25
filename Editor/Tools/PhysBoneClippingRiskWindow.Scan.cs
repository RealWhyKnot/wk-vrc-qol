// PhysBoneClippingRiskWindow.Scan.cs

using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
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
            log?.AppendLine("PhysBone Clipping Risks verbose log");
            log?.AppendLine($"  target={PathUtility.GetGameObjectPath(_targetRenderer.gameObject)}");
            log?.AppendLine($"  comparisonRenderers={surfaces.Count - 1}, surfaceRenderers={surfaces.Count}");
            log?.AppendLine($"  weightFloor={_weightFloor:F4}, clearanceMargin={_clearanceMargin:F3}, maxIssuesPerPhysBone={_maxIssuesPerPhysBone}");

            double start = EditorApplication.timeSinceStartup;
            var settings = new PhysBoneClippingAnalyzer.Settings {
                WeightFloor = _weightFloor,
                ClearanceMargin = _clearanceMargin,
                MaxIssuesPerPhysBone = _maxIssuesPerPhysBone,
            };
            _issues.AddRange(PhysBoneClippingAnalyzer.ScanOneMesh(
                _animator,
                _targetRenderer,
                surfaces,
                settings,
                log));
            double elapsed = EditorApplication.timeSinceStartup - start;

            int totalSources = settings.NativePhysBoneCount + settings.CustomPhysBoneCount;
            string sourceText = settings.CustomPhysBoneCount > 0
                ? $"{totalSources} PhysBone sources ({settings.CustomPhysBoneCount} generated/custom)"
                : $"{totalSources} PhysBone sources";
            string surfaceText = surfaces.Count == 1 ? "self only" : $"{surfaces.Count} surface meshes";
            _scanSummary = $"Scanned 1 mesh vs {surfaceText} in {elapsed:0.0}s; {sourceText}; found {_issues.Count} risk(s).";
            if (log != null) {
                log.AppendLine($"  elapsed={elapsed:0.000}s");
                log.AppendLine($"  livePhysBones={settings.NativePhysBoneCount}");
                log.AppendLine($"  generatedOrCustomPhysBones={settings.CustomPhysBoneCount}");
                log.AppendLine($"  drivenVertexSamples={settings.DrivenVertexSampleCount}");
                log.AppendLine($"  surfaceSamples={settings.SurfaceSampleCount}");
                log.AppendLine($"  risks={_issues.Count}");
                AvatarQolLogger.Instance.Info(log.ToString());
            }
            SceneView.RepaintAll();
        }

        private List<SkinnedMeshRenderer> BuildSurfaceList() {
            var list = new List<SkinnedMeshRenderer> { _targetRenderer };
            foreach (var renderer in _comparisonRenderers) {
                if (renderer == null || renderer.sharedMesh == null || !renderer.sharedMesh.isReadable) continue;
                if (!list.Contains(renderer)) list.Add(renderer);
            }
            return list;
        }
    }
}
