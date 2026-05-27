// ClippingFixer.cs
//
// Shared scan and mesh-edit path for the Clipping Fixer window and the
// build/play component. The scanner checks actual skinned mesh positions
// against comparison mesh surfaces and can also include PhysBone motion
// envelopes for weighted vertices.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Intent;
using WhyKnot.AvatarQol.Internal.Utilities;
using WhyKnot.AvatarQol.Tools;

namespace WhyKnot.AvatarQol.Clipping {

    internal static class ClippingFixer {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal enum IssueKind {
            ComparisonMesh,
            SurfaceIntersection,
            SelfIntersection,
            PhysBoneMotion,
        }

        internal sealed class Settings {
            public Animator Animator;
            public bool CheckSelf = true;
            public bool IncludePhysBoneMotion = true;
            public float InsideTolerance = 0.001f;
            public float SurfacePadding = 0.005f;
            public float PhysBoneWeightFloor = 0.03f;
            public float PhysBoneClearanceMargin = 0.025f;
            public int MaxFixPasses = 4;
            public int MaxWarnings = 250;
            public int MaxIssuesPerPhysBone = 8;
        }

        internal sealed class Issue {
            public IssueKind Kind;
            public SkinnedMeshRenderer Renderer;
            public string RendererPath;
            public SkinnedMeshRenderer ComparisonRenderer;
            public string ComparisonPath;
            public int VertexIndex = -1;
            public int TargetTriangleIndex = -1;
            public int ComparisonTriangleIndex = -1;
            public int[] AffectedVertexIndices = Array.Empty<int>();
            public Vector3 WorldPosition;
            public Vector3 NearestSurfacePosition;
            public Vector3 SurfaceNormal;
            public Vector3 PushWorld;
            public float PenetrationDepth;
            public float Score;
            public string Reason;
            public Component PhysBoneComponent;
            public string PhysBoneSourceLabel;
            public Transform PhysBoneRoot;
            public Transform DrivenBone;
            public float PhysBoneWeight;
            public float EstimatedMotion;
            public float Clearance;
            public bool HasEffectiveColliders;
            public bool PhysBoneHighSeverity;
        }

        internal sealed class Result {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> ClonedPaths = new List<string>();
            public int IssuesFound;
            public int VerticesMoved;
            public int FixPasses;
            public int MeshesCloned;
            public int RenderersTouched;
            public int UnreadableRenderers;
            public int PhysBoneWarningsHandled;
            public int PhysBoneSourcesAdjusted;
            public int UnsupportedPhysBoneSources;
            public bool ConfigurationError;

            public bool DidAnything => VerticesMoved > 0 || MeshesCloned > 0 || RenderersTouched > 0 || PhysBoneSourcesAdjusted > 0;
        }

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
                    PushWorld = Vector3.zero,
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

        internal static Result ApplyDestructive(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                IList<Issue> selectedIssues = null) {

            var result = new Result();
            if (AvatarIntentSessionState.IsAnyIntentSessionActive()) {
                result.ConfigurationError = true;
                result.Summary = "Stop the active preview/play/build mesh session before applying a destructive clipping fix.";
                return result;
            }

            settings = UnlimitedWarnings(settings);
            var initial = selectedIssues != null && selectedIssues.Count > 0
                ? selectedIssues.Where(i => i != null).ToList()
                : Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (initial.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            var meshInitial = MeshEditIssues(initial);
            var motionInitial = PhysBoneIssues(initial);
            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Pick a target renderer with a readable mesh.";
                return result;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: Fix mesh clipping");
            try {
                ApplyPhysBoneMotionReduction(motionInitial, null, true, result, null);

                Mesh clone = null;
                if (meshInitial.Count > 0) {
                    clone = CloneMeshAssetAndAssign(targetRenderer, targetRenderer.sharedMesh, result);
                    if (clone == null) {
                        result.ConfigurationError = true;
                        result.Summary = "Could not create an editable mesh asset.";
                        Undo.RevertAllInCurrentGroup();
                        return result;
                    }

                    result.MeshesCloned = 1;
                    result.RenderersTouched = 1;
                    if (selectedIssues != null && selectedIssues.Count > 0) {
                        int moved = ApplyIssuesToCurrentMesh(targetRenderer, meshInitial, useUndo: true);
                        result.VerticesMoved = moved;
                        result.FixPasses = moved > 0 ? 1 : 0;
                    } else {
                        RunFixPasses(targetRenderer, comparisonRenderers, WithoutPhysBoneMotion(settings), result, useUndo: true);
                    }
                }
                if (result.VerticesMoved > 0 && clone != null) {
                    EditorUtility.SetDirty(clone);
                    EditorUtility.SetDirty(targetRenderer);
                }
                if (result.VerticesMoved > 0 || result.PhysBoneSourcesAdjusted > 0) {
                    AssetDatabase.SaveAssets();
                }
                Undo.CollapseUndoOperations(undoGroup);
                result.Summary = BuildSummary(result);
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex);
                result.ConfigurationError = true;
                result.Summary = "Clipping fix failed. See the console for details.";
            }
            return result;
        }

        internal static Result ApplySelectedToCurrentMeshInPlace(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> issues,
                bool useUndo) {

            var result = new Result();
            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Pick a target renderer with a readable mesh.";
                return result;
            }
            if (issues == null || issues.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            result.IssuesFound = issues.Count(i => i != null);
            result.RenderersTouched = 1;
            result.VerticesMoved = ApplyIssuesToCurrentMesh(targetRenderer, MeshEditIssues(issues), useUndo);
            result.FixPasses = result.VerticesMoved > 0 ? 1 : 0;
            result.Summary = BuildSummary(result);
            return result;
        }

        internal static Result ApplyNonDestructive(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                AvatarIntentSession session) {

            var result = new Result();
            if (session == null) {
                result.ConfigurationError = true;
                result.Summary = "No mesh session was supplied.";
                return result;
            }

            settings = UnlimitedWarnings(settings);
            var initial = Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (initial.Count == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }

            if (targetRenderer == null || targetRenderer.sharedMesh == null) {
                result.ConfigurationError = true;
                result.Summary = "Target renderer has no mesh.";
                return result;
            }

            var motionInitial = PhysBoneIssues(initial);
            ApplyPhysBoneMotionReduction(motionInitial, session, false, result, null);

            var meshInitial = MeshEditIssues(initial);
            if (meshInitial.Count > 0) {
                session.Capture(targetRenderer);
                var clone = UnityEngine.Object.Instantiate(targetRenderer.sharedMesh);
                clone.name = targetRenderer.sharedMesh.name + " (ClippingFixed)";
                clone.hideFlags = HideFlags.DontSave;
                session.Adopt(clone);
                targetRenderer.sharedMesh = clone;
                result.MeshesCloned = 1;
                result.RenderersTouched = 1;

                RunFixPasses(targetRenderer, comparisonRenderers, WithoutPhysBoneMotion(settings), result, useUndo: false);
            }
            result.Summary = BuildSummary(result);
            return result;
        }

        internal static Result ApplyToCurrentMeshInPlace(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                bool useUndo) {

            var result = new Result();
            settings = UnlimitedWarnings(settings);
            var initial = Scan(targetRenderer, comparisonRenderers, settings);
            result.IssuesFound = initial.Count;
            if (result.IssuesFound == 0) {
                result.Summary = "No clipping warnings to fix.";
                return result;
            }
            ApplyPhysBoneMotionReduction(PhysBoneIssues(initial), null, useUndo, result, null);
            var meshInitial = MeshEditIssues(initial);
            if (meshInitial.Count > 0) {
                result.RenderersTouched = 1;
                RunFixPasses(targetRenderer, comparisonRenderers, WithoutPhysBoneMotion(settings), result, useUndo);
            }
            result.Summary = BuildSummary(result);
            return result;
        }

        private static void RunFixPasses(
                SkinnedMeshRenderer targetRenderer,
                IList<SkinnedMeshRenderer> comparisonRenderers,
                Settings settings,
                Result result,
                bool useUndo) {

            int maxPasses = Mathf.Clamp(settings.MaxFixPasses, 1, 8);
            for (int pass = 0; pass < maxPasses; pass++) {
                var issues = Scan(targetRenderer, comparisonRenderers, settings);
                if (issues.Count == 0) break;
                int moved = ApplyIssuesToCurrentMesh(targetRenderer, issues, useUndo);
                result.VerticesMoved += moved;
                result.FixPasses++;
                if (moved == 0) break;
            }
        }

        private static int ApplyIssuesToCurrentMesh(
                SkinnedMeshRenderer targetRenderer,
                IList<Issue> issues,
                bool useUndo) {

            if (!TryBuildSnapshot(targetRenderer, out var snapshot, null, "target")) return 0;
            var mesh = targetRenderer.sharedMesh;
            if (mesh == null || !mesh.isReadable) return 0;

            var corrections = new Vector3[snapshot.VertexCount];
            var hasCorrection = new bool[snapshot.VertexCount];
            foreach (var issue in issues) {
                if (issue == null || issue.PushWorld.sqrMagnitude <= 0f) continue;
                if (issue.Kind == IssueKind.PhysBoneMotion) continue;
                var affected = issue.AffectedVertexIndices;
                if (affected == null || affected.Length == 0) {
                    if (issue.VertexIndex >= 0) affected = new[] { issue.VertexIndex };
                    else continue;
                }
                foreach (int idx in affected) {
                    if (idx < 0 || idx >= corrections.Length) continue;
                    if (!hasCorrection[idx] || issue.PushWorld.sqrMagnitude > corrections[idx].sqrMagnitude) {
                        corrections[idx] = issue.PushWorld;
                        hasCorrection[idx] = true;
                    }
                }
            }

            var verts = mesh.vertices;
            int moved = 0;
            for (int i = 0; i < verts.Length && i < hasCorrection.Length; i++) {
                if (!hasCorrection[i]) continue;
                var nextWorld = snapshot.WorldVertices[i] + corrections[i];
                verts[i] = snapshot.MeshLocalFromWorld(i, nextWorld);
                moved++;
            }

            if (moved == 0) return 0;
            if (useUndo) Undo.RegisterCompleteObjectUndo(mesh, "Fix mesh clipping");
            mesh.vertices = verts;
            mesh.RecalculateBounds();
            if (useUndo) EditorUtility.SetDirty(mesh);
            return moved;
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

        private static List<Issue> MeshEditIssues(IList<Issue> issues) {
            if (issues == null) return new List<Issue>();
            return issues
                .Where(i => i != null && i.Kind != IssueKind.PhysBoneMotion)
                .ToList();
        }

        private static List<Issue> PhysBoneIssues(IList<Issue> issues) {
            if (issues == null) return new List<Issue>();
            return issues
                .Where(i => i != null && i.Kind == IssueKind.PhysBoneMotion)
                .ToList();
        }

        private static Settings WithoutPhysBoneMotion(Settings source) {
            var settings = UnlimitedWarnings(source);
            settings.IncludePhysBoneMotion = false;
            return settings;
        }

        private static void ApplyPhysBoneMotionReduction(
                IList<Issue> motionIssues,
                AvatarIntentSession session,
                bool useUndo,
                Result result,
                StringBuilder log) {

            if (motionIssues == null || motionIssues.Count == 0 || result == null) return;
            var analyzerIssues = new List<PhysBoneClippingAnalyzer.Issue>();
            var captured = new HashSet<Component>();
            foreach (var issue in motionIssues) {
                if (issue == null || issue.PhysBoneComponent == null) continue;
                if (session != null && captured.Add(issue.PhysBoneComponent)) {
                    session.Capture(issue.PhysBoneComponent);
                }
                analyzerIssues.Add(ToAnalyzerIssue(issue));
            }

            result.PhysBoneWarningsHandled += motionIssues.Count;
            if (analyzerIssues.Count == 0) {
                result.UnsupportedPhysBoneSources += motionIssues.Count;
                return;
            }

            var reduced = PhysBoneClippingAnalyzer.ReduceMotionIssues(analyzerIssues, log, useUndo);
            result.PhysBoneSourcesAdjusted += reduced.SourcesChanged;
            result.UnsupportedPhysBoneSources += reduced.UnsupportedSources;
            if (!string.IsNullOrEmpty(reduced.Summary)) result.Detail.Add(reduced.Summary);
        }

        private static PhysBoneClippingAnalyzer.Issue ToAnalyzerIssue(Issue issue) {
            return new PhysBoneClippingAnalyzer.Issue {
                Severity = issue.PhysBoneHighSeverity
                    ? PhysBoneClippingAnalyzer.Severity.High
                    : PhysBoneClippingAnalyzer.Severity.Medium,
                PhysBoneComponent = issue.PhysBoneComponent,
                PhysBoneSourceLabel = issue.PhysBoneSourceLabel,
                PhysBoneRoot = issue.PhysBoneRoot,
                DrivenBone = issue.DrivenBone,
                Renderer = issue.Renderer,
                RendererPath = issue.RendererPath,
                VertexIndex = issue.VertexIndex,
                WorldPosition = issue.WorldPosition,
                NearestSurfacePosition = issue.NearestSurfacePosition,
                NearestSurfaceRenderer = issue.ComparisonRenderer,
                NearestSurfacePath = issue.ComparisonPath,
                Weight = issue.PhysBoneWeight,
                EstimatedMotion = issue.EstimatedMotion,
                Clearance = issue.Clearance,
                HasEffectiveColliders = issue.HasEffectiveColliders,
                Reason = issue.Reason,
                Score = issue.Score,
            };
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

        private static Settings UnlimitedWarnings(Settings source) {
            source = source ?? new Settings();
            return new Settings {
                Animator = source.Animator,
                CheckSelf = source.CheckSelf,
                IncludePhysBoneMotion = source.IncludePhysBoneMotion,
                InsideTolerance = source.InsideTolerance,
                SurfacePadding = source.SurfacePadding,
                PhysBoneWeightFloor = source.PhysBoneWeightFloor,
                PhysBoneClearanceMargin = source.PhysBoneClearanceMargin,
                MaxFixPasses = source.MaxFixPasses,
                MaxWarnings = 0,
                MaxIssuesPerPhysBone = source.MaxIssuesPerPhysBone,
            };
        }

        private static Mesh CloneMeshAssetAndAssign(
                SkinnedMeshRenderer renderer,
                Mesh sharedMesh,
                Result result) {

            if (renderer == null || sharedMesh == null) return null;
            EnsureFolder(GeneratedFolder);
            var clone = UnityEngine.Object.Instantiate(sharedMesh);
            clone.name = sharedMesh.name + " (ClippingFixed)";
            string path = AssetDatabase.GenerateUniqueAssetPath(
                $"{GeneratedFolder}/{SanitizeFileName(renderer.gameObject.name + "_" + sharedMesh.name + "_ClippingFixed")}.asset");
            AssetDatabase.CreateAsset(clone, path);
            Undo.RegisterCreatedObjectUndo(clone, "Create clipping-fixed mesh");
            Undo.RecordObject(renderer, "Assign clipping-fixed mesh");
            renderer.sharedMesh = clone;
            EditorUtility.SetDirty(renderer);
            result.ClonedPaths.Add(path);
            return clone;
        }

        private static void EnsureFolder(string folder) {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            if (parts.Length < 2 || parts[0] != "Assets") return;
            var current = "Assets";
            for (int i = 1; i < parts.Length; i++) {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name)) return "mesh";
            foreach (char ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Replace("(", "").Replace(")", "").Trim();
            return string.IsNullOrEmpty(name) ? "mesh" : name;
        }

        private static string BuildSummary(Result result) {
            if (result == null) return "";
            var parts = new List<string>();
            parts.Add($"{result.VerticesMoved} vertices moved");
            parts.Add($"{result.FixPasses} pass(es)");
            if (result.MeshesCloned > 0) parts.Add($"{result.MeshesCloned} mesh(es) cloned");
            if (result.PhysBoneSourcesAdjusted > 0) parts.Add($"{result.PhysBoneSourcesAdjusted} PhysBone source(s) adjusted");
            if (result.UnsupportedPhysBoneSources > 0) parts.Add($"{result.UnsupportedPhysBoneSources} PhysBone source(s) skipped");
            if (result.UnreadableRenderers > 0) parts.Add($"{result.UnreadableRenderers} renderer(s) skipped (mesh not readable)");
            return string.Join(", ", parts) + ".";
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

        private sealed class RendererSnapshot {
            public SkinnedMeshRenderer Renderer;
            public Mesh Mesh;
            public string RendererPath;
            public Vector3[] LocalVertices;
            public Vector3[] WorldVertices;
            public int[] PrimaryBoneIndices;
            public Transform[] Bones;
            public Matrix4x4[] Bindposes;

            public int VertexCount => WorldVertices != null ? WorldVertices.Length : 0;

            public static RendererSnapshot Build(SkinnedMeshRenderer renderer) {
                var mesh = renderer != null ? renderer.sharedMesh : null;
                if (mesh == null || !mesh.isReadable) return null;
                var snapshot = new RendererSnapshot {
                    Renderer = renderer,
                    Mesh = mesh,
                    RendererPath = PathUtility.GetGameObjectPath(renderer.gameObject),
                    LocalVertices = mesh.vertices,
                    Bones = renderer.bones ?? Array.Empty<Transform>(),
                    Bindposes = mesh.bindposes ?? Array.Empty<Matrix4x4>(),
                };
                snapshot.WorldVertices = new Vector3[snapshot.LocalVertices.Length];
                snapshot.PrimaryBoneIndices = new int[snapshot.LocalVertices.Length];
                for (int i = 0; i < snapshot.PrimaryBoneIndices.Length; i++) snapshot.PrimaryBoneIndices[i] = -1;
                snapshot.ComputeWorldVertices();
                return snapshot;
            }

            public Vector3 MeshLocalFromWorld(int vertexIndex, Vector3 world) {
                if (vertexIndex >= 0 && vertexIndex < PrimaryBoneIndices.Length) {
                    int boneIndex = PrimaryBoneIndices[vertexIndex];
                    if (boneIndex >= 0 && boneIndex < Bones.Length && boneIndex < Bindposes.Length && Bones[boneIndex] != null) {
                        var boneLocal = Bones[boneIndex].InverseTransformPoint(world);
                        return Bindposes[boneIndex].inverse.MultiplyPoint3x4(boneLocal);
                    }
                }
                return Renderer.transform.InverseTransformPoint(world);
            }

            private void ComputeWorldVertices() {
                var mesh = Mesh;
                var bones = Bones;
                var bindposes = Bindposes;
                var bonesPerVertex = mesh.GetBonesPerVertex();
                var weights = mesh.GetAllBoneWeights();
                bool canSkin = bones != null && bones.Length > 0 &&
                               bindposes != null && bindposes.Length > 0 &&
                               bonesPerVertex.Length == LocalVertices.Length &&
                               weights.Length > 0;
                if (!canSkin) {
                    for (int i = 0; i < LocalVertices.Length; i++) {
                        WorldVertices[i] = Renderer.transform.TransformPoint(LocalVertices[i]);
                    }
                    return;
                }

                int cursor = 0;
                for (int v = 0; v < LocalVertices.Length; v++) {
                    int count = bonesPerVertex[v];
                    Vector3 blended = Vector3.zero;
                    float sum = 0f;
                    int primary = -1;
                    float primaryWeight = 0f;
                    for (int w = 0; w < count; w++) {
                        var bw = weights[cursor + w];
                        int boneIndex = bw.boneIndex;
                        if (boneIndex < 0 || boneIndex >= bones.Length || boneIndex >= bindposes.Length) continue;
                        var bone = bones[boneIndex];
                        if (bone == null || bw.weight <= 0f) continue;
                        var matrix = bone.localToWorldMatrix * bindposes[boneIndex];
                        blended += matrix.MultiplyPoint3x4(LocalVertices[v]) * bw.weight;
                        sum += bw.weight;
                        if (bw.weight > primaryWeight) {
                            primaryWeight = bw.weight;
                            primary = boneIndex;
                        }
                    }
                    cursor += count;

                    if (sum > 0.00001f) {
                        WorldVertices[v] = blended / sum;
                        PrimaryBoneIndices[v] = primary;
                    } else {
                        WorldVertices[v] = Renderer.transform.TransformPoint(LocalVertices[v]);
                    }
                }
            }
        }

        private sealed class SurfaceMesh {
            public RendererSnapshot Snapshot;
            public readonly List<Triangle> Triangles = new List<Triangle>();
            private TriangleHash _hash;

            public static SurfaceMesh Build(RendererSnapshot snapshot) {
                var surface = new SurfaceMesh { Snapshot = snapshot };
                if (snapshot == null || snapshot.Mesh == null) return surface;
                var indices = snapshot.Mesh.triangles;
                var verts = snapshot.WorldVertices;
                for (int i = 0; i + 2 < indices.Length; i += 3) {
                    int a = indices[i];
                    int b = indices[i + 1];
                    int c = indices[i + 2];
                    if (a < 0 || b < 0 || c < 0 || a >= verts.Length || b >= verts.Length || c >= verts.Length) continue;
                    var tri = new Triangle(surface.Triangles.Count, a, b, c, verts[a], verts[b], verts[c]);
                    if (tri.AreaSquared <= 0.0000000001f) continue;
                    surface.Triangles.Add(tri);
                }
                surface._hash = new TriangleHash(surface.Triangles);
                return surface;
            }

            public IEnumerable<Triangle> Query(Bounds bounds, float padding) {
                if (_hash == null) return Triangles;
                return _hash.Query(bounds, padding);
            }

            public bool TryFindClosest(Vector3 point, out ClosestTriangle closest) {
                closest = default;
                if (Triangles.Count == 0) return false;
                float best = float.PositiveInfinity;
                bool found = false;
                foreach (var tri in _hash.QueryPoint(point)) {
                    var p = ClosestPointOnTriangle(point, tri.A, tri.B, tri.C);
                    float d = (point - p).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    closest = new ClosestTriangle {
                        TriangleIndex = tri.Index,
                        Point = p,
                        Normal = tri.Normal,
                        SqrDistance = d,
                    };
                    found = true;
                }
                if (found) return true;

                foreach (var tri in Triangles) {
                    var p = ClosestPointOnTriangle(point, tri.A, tri.B, tri.C);
                    float d = (point - p).sqrMagnitude;
                    if (d >= best) continue;
                    best = d;
                    closest = new ClosestTriangle {
                        TriangleIndex = tri.Index,
                        Point = p,
                        Normal = tri.Normal,
                        SqrDistance = d,
                    };
                    found = true;
                }
                return found;
            }
        }

        private readonly struct Triangle {
            public readonly int Index;
            public readonly int AIndex;
            public readonly int BIndex;
            public readonly int CIndex;
            public readonly Vector3 A;
            public readonly Vector3 B;
            public readonly Vector3 C;
            public readonly Vector3 Normal;
            public readonly Bounds Bounds;
            public readonly float AreaSquared;

            public Triangle(int index, int aIndex, int bIndex, int cIndex, Vector3 a, Vector3 b, Vector3 c) {
                Index = index;
                AIndex = aIndex;
                BIndex = bIndex;
                CIndex = cIndex;
                A = a;
                B = b;
                C = c;
                var cross = Vector3.Cross(b - a, c - a);
                AreaSquared = cross.sqrMagnitude;
                Normal = AreaSquared > 0.0000000001f ? cross.normalized : Vector3.up;
                var bounds = new Bounds(a, Vector3.zero);
                bounds.Encapsulate(b);
                bounds.Encapsulate(c);
                Bounds = bounds;
            }
        }

        private struct ClosestTriangle {
            public int TriangleIndex;
            public Vector3 Point;
            public Vector3 Normal;
            public float SqrDistance;
        }

        private sealed class TriangleHash {
            private const float CellSize = 0.05f;
            private readonly List<Triangle> _triangles;
            private readonly Dictionary<Vector3Int, List<int>> _cells = new Dictionary<Vector3Int, List<int>>();

            public TriangleHash(List<Triangle> triangles) {
                _triangles = triangles ?? new List<Triangle>();
                for (int i = 0; i < _triangles.Count; i++) {
                    var b = _triangles[i].Bounds;
                    var min = Cell(b.min);
                    var max = Cell(b.max);
                    for (int x = min.x; x <= max.x; x++) {
                        for (int y = min.y; y <= max.y; y++) {
                            for (int z = min.z; z <= max.z; z++) {
                                var key = new Vector3Int(x, y, z);
                                if (!_cells.TryGetValue(key, out var list)) {
                                    list = new List<int>();
                                    _cells[key] = list;
                                }
                                list.Add(i);
                            }
                        }
                    }
                }
            }

            public IEnumerable<Triangle> Query(Bounds bounds, float padding) {
                bounds.Expand(padding * 2f);
                var min = Cell(bounds.min);
                var max = Cell(bounds.max);
                var seen = new HashSet<int>();
                for (int x = min.x; x <= max.x; x++) {
                    for (int y = min.y; y <= max.y; y++) {
                        for (int z = min.z; z <= max.z; z++) {
                            if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                            foreach (int idx in list) {
                                if (seen.Add(idx)) yield return _triangles[idx];
                            }
                        }
                    }
                }
            }

            public IEnumerable<Triangle> QueryPoint(Vector3 point) {
                var seen = new HashSet<int>();
                for (int radius = 0; radius <= 6; radius++) {
                    var center = Cell(point);
                    for (int x = center.x - radius; x <= center.x + radius; x++) {
                        for (int y = center.y - radius; y <= center.y + radius; y++) {
                            for (int z = center.z - radius; z <= center.z + radius; z++) {
                                if (radius > 0 &&
                                    x > center.x - radius && x < center.x + radius &&
                                    y > center.y - radius && y < center.y + radius &&
                                    z > center.z - radius && z < center.z + radius) {
                                    continue;
                                }
                                if (!_cells.TryGetValue(new Vector3Int(x, y, z), out var list)) continue;
                                foreach (int idx in list) {
                                    if (seen.Add(idx)) yield return _triangles[idx];
                                }
                            }
                        }
                    }
                    if (seen.Count > 0) yield break;
                }
            }

            private static Vector3Int Cell(Vector3 p) {
                return new Vector3Int(
                    Mathf.FloorToInt(p.x / CellSize),
                    Mathf.FloorToInt(p.y / CellSize),
                    Mathf.FloorToInt(p.z / CellSize));
            }
        }

        private static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c) {
            var ab = b - a;
            var ac = c - a;
            var ap = p - a;
            float d1 = Vector3.Dot(ab, ap);
            float d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0f && d2 <= 0f) return a;

            var bp = p - b;
            float d3 = Vector3.Dot(ab, bp);
            float d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0f && d4 <= d3) return b;

            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0f && d1 >= 0f && d3 <= 0f) {
                float v = d1 / (d1 - d3);
                return a + ab * v;
            }

            var cp = p - c;
            float d5 = Vector3.Dot(ab, cp);
            float d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0f && d5 <= d6) return c;

            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0f && d2 >= 0f && d6 <= 0f) {
                float w = d2 / (d2 - d6);
                return a + ac * w;
            }

            float va = d3 * d6 - d5 * d4;
            if (va <= 0f && (d4 - d3) >= 0f && (d5 - d6) >= 0f) {
                float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
                return b + (c - b) * w;
            }

            float denom = 1f / (va + vb + vc);
            float v2 = vb * denom;
            float w2 = vc * denom;
            return a + ab * v2 + ac * w2;
        }
    }
}
