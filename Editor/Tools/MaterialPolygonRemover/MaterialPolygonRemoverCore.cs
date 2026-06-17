// MaterialPolygonRemoverCore.cs

using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using WhyKnot.AvatarQol.Geometry;
using WhyKnot.AvatarQol.Internal.Utilities;

namespace WhyKnot.AvatarQol.Tools {

    internal static class MaterialPolygonRemoverCore {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal sealed class Result {
            public string Summary = "";
            public readonly List<string> Detail = new List<string>();
            public readonly List<string> CreatedPaths = new List<string>();
            public int RemovedSubmeshes;
            public int RemovedTriangles;
            public int RemovedVertices;
            public bool ConfigurationError;
        }

        internal sealed class Plan {
            public bool CanApply;
            public string Error = "";
            public readonly List<int[]> KeptSubmeshTriangles = new List<int[]>();
            public readonly List<Material> KeptMaterials = new List<Material>();
            public int RemovedSubmeshes;
            public int RemovedTriangles;
            public int OriginalTriangles;
        }

        internal static Plan BuildPlan(SkinnedMeshRenderer renderer, IList<bool> removeSlots) {
            var plan = new Plan();
            if (renderer == null) {
                plan.Error = "Pick a SkinnedMeshRenderer first.";
                return plan;
            }
            var mesh = renderer.sharedMesh;
            if (mesh == null) {
                plan.Error = "Renderer has no sharedMesh.";
                return plan;
            }
            if (!mesh.isReadable) {
                plan.Error = "Mesh is not readable. Enable Read/Write on the model importer.";
                return plan;
            }
            if (removeSlots == null || removeSlots.Count == 0) {
                plan.Error = "Select at least one material slot to remove.";
                return plan;
            }

            var materials = renderer.sharedMaterials ?? Array.Empty<Material>();
            int removed = 0;
            int kept = 0;
            int slots = mesh.subMeshCount;
            for (int s = 0; s < slots; s++) {
                var triangles = mesh.GetTriangles(s);
                plan.OriginalTriangles += triangles.Length / 3;
                bool remove = s < removeSlots.Count && removeSlots[s];
                if (remove) {
                    removed++;
                    plan.RemovedTriangles += triangles.Length / 3;
                    continue;
                }
                kept++;
                plan.KeptSubmeshTriangles.Add(triangles);
                plan.KeptMaterials.Add(s < materials.Length ? materials[s] : null);
            }

            if (removed == 0) {
                plan.Error = "Select at least one material slot to remove.";
                return plan;
            }
            if (kept == 0) {
                plan.Error = "This would remove every material slot. Leave at least one slot enabled.";
                return plan;
            }

            plan.RemovedSubmeshes = removed;
            plan.CanApply = true;
            return plan;
        }

        internal static Result Apply(SkinnedMeshRenderer renderer, IList<bool> removeSlots) {
            var result = new Result();
            var plan = BuildPlan(renderer, removeSlots);
            if (!plan.CanApply) {
                result.ConfigurationError = true;
                result.Summary = plan.Error;
                return result;
            }

            int undoGroup = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar QoL: remove material polygons");
            try {
                var source = renderer.sharedMesh;
                var compacted = MeshCompactor.BuildKeepingTriangles(
                    source,
                    plan.KeptSubmeshTriangles,
                    source.name + " (MaterialPolygonsRemoved)");

                var write = FbxMeshUtility.WriteNewMeshAsset(
                    renderer,
                    compacted.Mesh,
                    "(MaterialPolygonsRemoved)",
                    "Create material-pruned mesh",
                    GeneratedFolder);
                if (!string.IsNullOrEmpty(write.ClonedPath)) result.CreatedPaths.Add(write.ClonedPath);

                Undo.RecordObject(renderer, "Assign material-pruned materials");
                renderer.sharedMaterials = plan.KeptMaterials.ToArray();
                EditorUtility.SetDirty(renderer);
                AssetDatabase.SaveAssets();
                Undo.CollapseUndoOperations(undoGroup);

                result.RemovedSubmeshes = plan.RemovedSubmeshes;
                result.RemovedTriangles = plan.RemovedTriangles;
                result.RemovedVertices = compacted.DroppedVertexCount;
                result.Summary = $"{result.RemovedSubmeshes} material slot(s), {result.RemovedTriangles} triangle(s), and {result.RemovedVertices} vertices removed.";
                result.Detail.Add($"OK   {PathUtility.GetGameObjectPath(renderer.gameObject)} -- created mesh with {compacted.KeptVertexCount} vertices and {plan.KeptSubmeshTriangles.Count} material slot(s).");
            } catch (Exception ex) {
                Undo.RevertAllInCurrentGroup();
                AvatarQolLogger.Instance.Exception(ex, "Material Polygon Remover");
                result.Summary = "Removal failed -- nothing was changed. See console for details.";
            }
            return result;
        }
    }
}
