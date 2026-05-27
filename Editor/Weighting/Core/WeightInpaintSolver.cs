// WeightInpaintSolver.cs
//
// Lightweight graph-diffusion inpainting for target vertices whose source
// correspondence was rejected. Confident transferred vertices remain
// fixed; unknown vertices are filled from neighboring target topology.

using System.Collections.Generic;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal static class WeightInpaintSolver {

        internal struct Result {
            public int Inpainted;
            public int Unresolved;
        }

        internal static Result FillRejected(
                SkinWeightBuffer output,
                bool[] accepted,
                MeshAdjacency adjacency,
                SkinWeightBuffer fallback,
                int iterations,
                int maxInfluences,
                float pruneThreshold,
                int fallbackBone) {
            var result = new Result();
            if (output == null || accepted == null || adjacency == null) return result;

            int count = output.VertexCount;
            var seeded = new bool[count];
            for (int i = 0; i < count && i < accepted.Length; i++) {
                seeded[i] = accepted[i] && output.HasAnyWeight(i);
            }

            // Expand seeds through topology. This makes disconnected
            // components without accepted anchors visible as unresolved.
            bool changed = true;
            int guard = 0;
            while (changed && guard++ < count) {
                changed = false;
                for (int v = 0; v < count; v++) {
                    if (seeded[v]) continue;
                    var avg = AverageSeededNeighbors(output, seeded, adjacency.NeighborsOf(v));
                    if (avg == null || avg.Count == 0) continue;
                    output.SetVertexFromDictionary(v, avg);
                    output.PruneVertex(v, maxInfluences, pruneThreshold, fallbackBone);
                    seeded[v] = true;
                    changed = true;
                    result.Inpainted++;
                }
            }

            iterations = Mathf.Clamp(iterations, 0, 256);
            for (int iter = 0; iter < iterations; iter++) {
                var pending = new Dictionary<int, float>[count];
                for (int v = 0; v < count; v++) {
                    if (v < accepted.Length && accepted[v]) continue;
                    if (!seeded[v]) continue;
                    pending[v] = AverageSeededNeighbors(output, seeded, adjacency.NeighborsOf(v));
                }
                for (int v = 0; v < count; v++) {
                    if (pending[v] == null || pending[v].Count == 0) continue;
                    output.SetVertexFromDictionary(v, pending[v]);
                    output.PruneVertex(v, maxInfluences, pruneThreshold, fallbackBone);
                }
            }

            for (int v = 0; v < count; v++) {
                if (seeded[v]) continue;
                if (fallback != null && fallback.HasAnyWeight(v)) {
                    output.CopyVertexFrom(fallback, v, v);
                    output.PruneVertex(v, maxInfluences, pruneThreshold, fallbackBone);
                } else {
                    output.NormalizeVertex(v, fallbackBone);
                }
                result.Unresolved++;
            }

            return result;
        }

        private static Dictionary<int, float> AverageSeededNeighbors(
                SkinWeightBuffer output,
                bool[] seeded,
                int[] neighbors) {
            if (output == null || seeded == null || neighbors == null || neighbors.Length == 0) return null;
            var avg = new Dictionary<int, float>();
            int contributors = 0;
            for (int i = 0; i < neighbors.Length; i++) {
                int n = neighbors[i];
                if (n < 0 || n >= seeded.Length || !seeded[n]) continue;
                output.AddScaledVertexToDictionary(n, 1f, avg);
                contributors++;
            }
            if (contributors <= 0) return null;
            float scale = 1f / contributors;
            var keys = new List<int>(avg.Keys);
            for (int i = 0; i < keys.Count; i++) {
                avg[keys[i]] *= scale;
            }
            return avg;
        }
    }
}
