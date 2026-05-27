// MeshWeightWriter.cs
//
// Durable weight writes go to a generated mesh clone. The original model
// or shared mesh asset remains untouched unless it already lives in the
// AvatarQoL generated folder.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Weighting {

    internal static class MeshWeightWriter {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal struct WriteResult {
            public Mesh Mesh;
            public bool CreatedClone;
            public string AssetPath;
        }

        public static bool WriteWeightsToGeneratedMesh(
                SkinnedMeshRenderer renderer,
                SkinWeightBuffer weights,
                int maxInfluences,
                float pruneThreshold,
                int fallbackBone,
                string cloneSuffix,
                string undoLabel,
                out WriteResult result,
                out string error) {
            result = new WriteResult();
            error = null;
            if (renderer == null) {
                error = "Pick a target renderer first.";
                return false;
            }
            var source = renderer.sharedMesh;
            if (source == null) {
                error = "Target renderer has no mesh.";
                return false;
            }
            if (weights == null || weights.VertexCount != source.vertexCount) {
                error = "Weight buffer does not match the target mesh.";
                return false;
            }

            Mesh editable = source;
            string path = AssetDatabase.GetAssetPath(source);
            if (!IsGeneratedEditablePath(path)) {
                EnsureFolder(GeneratedFolder);
                editable = UnityEngine.Object.Instantiate(source);
                editable.name = source.name + " " + cloneSuffix;
                string safeName = SanitizeFileName(renderer.gameObject.name + "_" + source.name + "_" + cloneSuffix);
                path = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedFolder}/{safeName}.asset");
                AssetDatabase.CreateAsset(editable, path);
                Undo.RegisterCreatedObjectUndo(editable, undoLabel);

                Undo.RecordObject(renderer, undoLabel);
                renderer.sharedMesh = editable;
                EditorUtility.SetDirty(renderer);
                PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);
                result.CreatedClone = true;
            }

            Undo.RegisterCompleteObjectUndo(editable, undoLabel);
            weights.WriteToMesh(editable, maxInfluences, pruneThreshold, fallbackBone);
            EditorUtility.SetDirty(editable);
            AssetDatabase.SaveAssets();

            result.Mesh = editable;
            result.AssetPath = path;
            return true;
        }

        internal static bool IsGeneratedEditablePath(string path) {
            if (string.IsNullOrEmpty(path)) return false;
            return path.StartsWith(GeneratedFolder + "/", StringComparison.OrdinalIgnoreCase)
                || path.Equals(GeneratedFolder, StringComparison.OrdinalIgnoreCase);
        }

        private static void EnsureFolder(string folder) {
            if (string.IsNullOrEmpty(folder) || AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            if (parts.Length == 0 || parts[0] != "Assets") return;
            var current = "Assets";
            for (int i = 1; i < parts.Length; i++) {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next)) AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name)) return "Weights";
            foreach (var ch in Path.GetInvalidFileNameChars()) name = name.Replace(ch, '_');
            name = name.Replace("(", "").Replace(")", "").Trim();
            return string.IsNullOrEmpty(name) ? "Weights" : name;
        }
    }
}
