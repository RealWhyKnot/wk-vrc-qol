// ClippingFixer.Scan.cs
//
// Mesh and PhysBone warning detection for the Clipping Fixer facade.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using WhyKnot.AvatarQol.Internal.Utilities;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Clipping {

    internal static partial class ClippingFixer {

        internal static List<Issue> Scan(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                StringBuilder log = null) {

            settings = settings ?? new Settings();
            var output = new List<Issue>();
            if (!TryBuildSnapshot(targetRenderer, out var target, log, "target")) return output;

            var comparisons = BuildComparisonList(targetRenderer, comparisonRenderers);
            var targetSurface = SurfaceMesh.Build(target);

            foreach (var comparisonRenderer in comparisons) {
                if (!TryBuildSnapshot(comparisonRenderer, out var comparison, log, "comparison")) continue;
                var comparisonSurface = SurfaceMesh.Build(comparison);
                ScanTargetInsideComparison(target, comparisonSurface, settings, output);
                ScanTargetIntersections(targetSurface, comparisonSurface, settings, output);
            }

            if (settings.CheckSelf) {
                ScanSelfIntersections(targetSurface, settings, output);
            }

            if (settings.IncludePhysBoneMotion) {
                ScanPhysBoneMotion(targetRenderer, comparisons, settings, output, log);
            }

            output.Sort((a, b) => b.Score.CompareTo(a.Score));
            if (settings.MaxWarnings > 0 && output.Count > settings.MaxWarnings) {
                output = output.Take(settings.MaxWarnings).ToList();
            }

            log?.AppendLine(
                $"  clipping scan: target vertices={target.VertexCount}, target triangles={targetSurface.Triangles.Count}, " +
                $"comparison renderers={comparisons.Count}, warnings={output.Count}.");
            return output;
        }

        private static void ScanPhysBoneMotion(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                List<Issue> output,
                StringBuilder log) {

            if (targetRenderer == null) return;
            if (settings.Animator == null) {
                log?.AppendLine("  PhysBone motion: skipped because no Animator was supplied.");
                return;
            }

            var pbSettings = new PhysBoneClippingAnalyzer.Settings {
                WeightFloor = Mathf.Max(0f, settings.PhysBoneWeightFloor),
                ClearanceMargin = Mathf.Max(0.001f, settings.PhysBoneClearanceMargin),
                MaxIssuesPerPhysBone = Mathf.Max(1, settings.MaxIssuesPerPhysBone),
            };
            var motionIssues = PhysBoneClippingAnalyzer.ScanOneMesh(
                settings.Animator,
                targetRenderer,
                comparisonRenderers,
                pbSettings,
                log);

            foreach (var motion in motionIssues) {
                if (motion == null) continue;
                var dir = motion.WorldPosition - motion.NearestSurfacePosition;
                if (dir.sqrMagnitude < 0.0000001f && motion.DrivenBone != null && motion.PhysBoneRoot != null) {
                    dir = motion.DrivenBone.position - motion.PhysBoneRoot.position;
                }
                if (dir.sqrMagnitude < 0.0000001f) dir = Vector3.up;
                dir.Normalize();

                float depth = Mathf.Max(0.0001f, motion.Score);
                float padding = Mathf.Max(0.001f, settings.SurfacePadding);
                var push = dir * (depth + padding);
                var comparison = motion.NearestSurfaceRenderer != null
                    ? motion.NearestSurfaceRenderer
                    : targetRenderer;

                output.Add(new Issue {
                    Kind = IssueKind.PhysBoneMotion,
                    Renderer = motion.Renderer,
                    RendererPath = motion.RendererPath,
                    ComparisonRenderer = comparison,
                    ComparisonPath = !string.IsNullOrEmpty(motion.NearestSurfacePath)
                        ? motion.NearestSurfacePath
                        : (comparison != null ? PathUtility.GetGameObjectPath(comparison.gameObject) : ""),
                    VertexIndex = motion.VertexIndex,
                    AffectedVertexIndices = motion.VertexIndex >= 0
                        ? new[] { motion.VertexIndex }
                        : Array.Empty<int>(),
                    WorldPosition = motion.WorldPosition,
                    NearestSurfacePosition = motion.NearestSurfacePosition,
                    SurfaceNormal = dir,
                    PushWorld = push,
                    PenetrationDepth = depth,
                    Score = depth,
                    Reason = motion.Reason,
                    PhysBoneComponent = motion.PhysBoneComponent,
                    PhysBoneSourceLabel = motion.PhysBoneSourceLabel,
                    PhysBoneRoot = motion.PhysBoneRoot,
                    DrivenBone = motion.DrivenBone,
                    PhysBoneWeight = motion.Weight,
                    EstimatedMotion = motion.EstimatedMotion,
                    Clearance = motion.Clearance,
                    HasEffectiveColliders = motion.HasEffectiveColliders,
                    PhysBoneHighSeverity = motion.Severity == PhysBoneClippingAnalyzer.Severity.High,
                });
            }
        }

        private static void ScanTargetInsideComparison(
                RendererSnapshot target,
                SurfaceMesh comparison,
                Settings settings,
                List<Issue> output) {

            if (comparison == null || comparison.Triangles.Count == 0) return;
            float tolerance = Mathf.Max(0f, settings.InsideTolerance);
            float padding = Mathf.Max(0f, settings.SurfacePadding);
            var reported = new HashSet<int>();

            for (int i = 0; i < target.VertexCount; i++) {
                var world = target.WorldVertices[i];
                if (!comparison.TryFindClosest(world, out var closest)) continue;
                float signed = Vector3.Dot(world - closest.Point, closest.Normal);
                if (signed >= -tolerance) continue;

                float depth = -signed;
                var push = closest.Normal * (depth + padding);
                if (!reported.Add(i)) continue;

                output.Add(new Issue {
                    Kind = IssueKind.ComparisonMesh,
                    Renderer = target.Renderer,
                    RendererPath = target.RendererPath,
                    ComparisonRenderer = comparison.Snapshot.Renderer,
                    ComparisonPath = comparison.Snapshot.RendererPath,
                    VertexIndex = i,
                    ComparisonTriangleIndex = closest.TriangleIndex,
                    AffectedVertexIndices = new[] { i },
                    WorldPosition = world,
                    NearestSurfacePosition = closest.Point,
                    SurfaceNormal = closest.Normal,
                    PushWorld = push,
                    PenetrationDepth = depth,
                    Score = depth,
                    Reason = $"Target vertex is {ToMm(depth)} mm behind {comparison.Snapshot.RendererPath}.",
                });
            }
        }

        private static void ScanTargetIntersections(
                SurfaceMesh target,
                SurfaceMesh comparison,
                Settings settings,
                List<Issue> output) {

            if (target == null || comparison == null) return;
            float padding = Mathf.Max(0.001f, settings.SurfacePadding);
            var seen = new HashSet<long>();

            foreach (var tri in target.Triangles) {
                foreach (var other in comparison.Query(tri.Bounds, padding)) {
                    long key = PairKey(tri.Index, other.Index);
                    if (!seen.Add(key)) continue;
                    if (!BoundsOverlap(tri.Bounds, other.Bounds, padding)) continue;
                    if (!TrianglesIntersect(tri, other)) continue;

                    var normal = other.Normal.sqrMagnitude > 0f ? other.Normal : tri.Normal;
                    var center = (tri.A + tri.B + tri.C) / 3f;
                    var otherCenter = (other.A + other.B + other.C) / 3f;
                    var push = normal.normalized * padding;

                    output.Add(new Issue {
                        Kind = IssueKind.SurfaceIntersection,
                        Renderer = target.Snapshot.Renderer,
                        RendererPath = target.Snapshot.RendererPath,
                        ComparisonRenderer = comparison.Snapshot.Renderer,
                        ComparisonPath = comparison.Snapshot.RendererPath,
                        VertexIndex = tri.AIndex,
                        TargetTriangleIndex = tri.Index,
                        ComparisonTriangleIndex = other.Index,
                        AffectedVertexIndices = new[] { tri.AIndex, tri.BIndex, tri.CIndex },
                        WorldPosition = center,
                        NearestSurfacePosition = otherCenter,
                        SurfaceNormal = normal,
                        PushWorld = push,
                        PenetrationDepth = padding,
                        Score = padding,
                        Reason = $"Target triangle intersects {comparison.Snapshot.RendererPath}.",
                    });
                }
            }
        }

        private static void ScanSelfIntersections(
                SurfaceMesh target,
                Settings settings,
                List<Issue> output) {

            if (target == null) return;
            float padding = Mathf.Max(0.001f, settings.SurfacePadding);
            var seen = new HashSet<long>();

            foreach (var tri in target.Triangles) {
                foreach (var other in target.Query(tri.Bounds, padding)) {
                    if (other.Index <= tri.Index) continue;
                    if (SharesVertex(tri, other)) continue;
                    long key = PairKey(tri.Index, other.Index);
                    if (!seen.Add(key)) continue;
                    if (!BoundsOverlap(tri.Bounds, other.Bounds, padding)) continue;
                    if (!TrianglesIntersect(tri, other)) continue;

                    var center = (tri.A + tri.B + tri.C) / 3f;
                    var otherCenter = (other.A + other.B + other.C) / 3f;
                    var dir = center - otherCenter;
                    if (dir.sqrMagnitude < 0.000001f) dir = tri.Normal;
                    if (dir.sqrMagnitude < 0.000001f) dir = Vector3.up;
                    dir.Normalize();

                    output.Add(new Issue {
                        Kind = IssueKind.SelfIntersection,
                        Renderer = target.Snapshot.Renderer,
                        RendererPath = target.Snapshot.RendererPath,
                        ComparisonRenderer = target.Snapshot.Renderer,
                        ComparisonPath = target.Snapshot.RendererPath,
                        VertexIndex = tri.AIndex,
                        TargetTriangleIndex = tri.Index,
                        ComparisonTriangleIndex = other.Index,
                        AffectedVertexIndices = new[] { tri.AIndex, tri.BIndex, tri.CIndex },
                        WorldPosition = center,
                        NearestSurfacePosition = otherCenter,
                        SurfaceNormal = dir,
                        PushWorld = dir * padding,
                        PenetrationDepth = padding,
                        Score = padding,
                        Reason = "Target mesh has intersecting non-adjacent triangles.",
                    });
                }
            }
        }

        private static List<SkinnedMeshRenderer> BuildComparisonList(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers) {

            var output = new List<SkinnedMeshRenderer>();
            if (comparisonRenderers == null) return output;
            foreach (var renderer in comparisonRenderers) {
                if (renderer == null || renderer == targetRenderer || output.Contains(renderer)) continue;
                output.Add(renderer);
            }
            return output;
        }

        private static bool TryBuildSnapshot(
                SkinnedMeshRenderer renderer,
                out RendererSnapshot snapshot,
                StringBuilder log,
                string label) {

            snapshot = null;
            if (renderer == null || renderer.sharedMesh == null) {
                log?.AppendLine($"  SKIP {label}: missing renderer or mesh.");
                return false;
            }
            if (!renderer.sharedMesh.isReadable) {
                log?.AppendLine($"  SKIP {label}: {PathUtility.GetGameObjectPath(renderer.gameObject)} mesh is not readable.");
                return false;
            }
            snapshot = RendererSnapshot.Build(renderer);
            return snapshot != null;
        }

        private static string ToMm(float metres) {
            return (metres * 1000f).ToString("0.0");
        }

        private static bool BoundsOverlap(Bounds a, Bounds b, float padding) {
            a.Expand(padding * 2f);
            b.Expand(padding * 2f);
            return a.Intersects(b);
        }

        private static long PairKey(int a, int b) {
            int lo = Mathf.Min(a, b);
            int hi = Mathf.Max(a, b);
            return ((long)lo << 32) ^ (uint)hi;
        }

        private static bool SharesVertex(Triangle a, Triangle b) {
            return a.AIndex == b.AIndex || a.AIndex == b.BIndex || a.AIndex == b.CIndex ||
                   a.BIndex == b.AIndex || a.BIndex == b.BIndex || a.BIndex == b.CIndex ||
                   a.CIndex == b.AIndex || a.CIndex == b.BIndex || a.CIndex == b.CIndex;
        }

        private static bool TrianglesIntersect(Triangle a, Triangle b) {
            const float eps = 0.000001f;
            if (SegmentTriangle(a.A, a.B, b.A, b.B, b.C, eps)) return true;
            if (SegmentTriangle(a.B, a.C, b.A, b.B, b.C, eps)) return true;
            if (SegmentTriangle(a.C, a.A, b.A, b.B, b.C, eps)) return true;
            if (SegmentTriangle(b.A, b.B, a.A, a.B, a.C, eps)) return true;
            if (SegmentTriangle(b.B, b.C, a.A, a.B, a.C, eps)) return true;
            if (SegmentTriangle(b.C, b.A, a.A, a.B, a.C, eps)) return true;

            if (PointOnTriangle(a.A, b, eps) || PointOnTriangle(a.B, b, eps) || PointOnTriangle(a.C, b, eps)) return true;
            if (PointOnTriangle(b.A, a, eps) || PointOnTriangle(b.B, a, eps) || PointOnTriangle(b.C, a, eps)) return true;
            return false;
        }

        private static bool SegmentTriangle(
                Vector3 p0,
                Vector3 p1,
                Vector3 a,
                Vector3 b,
                Vector3 c,
                float eps) {

            var dir = p1 - p0;
            var edge1 = b - a;
            var edge2 = c - a;
            var h = Vector3.Cross(dir, edge2);
            float det = Vector3.Dot(edge1, h);
            if (Mathf.Abs(det) < eps) return false;
            float invDet = 1f / det;
            var s = p0 - a;
            float u = invDet * Vector3.Dot(s, h);
            if (u < -eps || u > 1f + eps) return false;
            var q = Vector3.Cross(s, edge1);
            float v = invDet * Vector3.Dot(dir, q);
            if (v < -eps || u + v > 1f + eps) return false;
            float t = invDet * Vector3.Dot(edge2, q);
            return t >= -eps && t <= 1f + eps;
        }

        private static bool PointOnTriangle(Vector3 p, Triangle tri, float eps) {
            float plane = Vector3.Dot(p - tri.A, tri.Normal);
            if (Mathf.Abs(plane) > eps) return false;
            return PointInTriangle(p, tri.A, tri.B, tri.C, eps);
        }

        private static bool PointInTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, float eps) {
            var v0 = c - a;
            var v1 = b - a;
            var v2 = p - a;
            float dot00 = Vector3.Dot(v0, v0);
            float dot01 = Vector3.Dot(v0, v1);
            float dot02 = Vector3.Dot(v0, v2);
            float dot11 = Vector3.Dot(v1, v1);
            float dot12 = Vector3.Dot(v1, v2);
            float denom = dot00 * dot11 - dot01 * dot01;
            if (Mathf.Abs(denom) < eps) return false;
            float inv = 1f / denom;
            float u = (dot11 * dot02 - dot01 * dot12) * inv;
            float v = (dot00 * dot12 - dot01 * dot02) * inv;
            return u >= -eps && v >= -eps && u + v <= 1f + eps;
        }
    }
}
