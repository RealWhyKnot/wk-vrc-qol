// MeshSculptEditableMesh.cs
//
// Generated-mesh ownership for Mesh Sculpt. The sculptor edits only a
// renderer-owned generated mesh asset; imported model sub-assets and
// shared source meshes are cloned before mutation.

using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class MeshSculptEditableMesh {

        internal const string GeneratedFolder = "Assets/AvatarQol Generated";

        internal struct Result {
            public Mesh Mesh;
            public bool CreatedClone;
            public string AssetPath;
            public string Message;
        }

        internal static bool EnsureEditable(
                SkinnedMeshRenderer renderer,
                string cloneSuffix,
                string undoLabel,
                out Result result) {
            result = new Result();
            if (renderer == null) {
                result.Message = "Pick a SkinnedMeshRenderer first.";
                return false;
            }

            var source = renderer.sharedMesh;
            if (source == null) {
                result.Message = "Renderer has no shared mesh.";
                return false;
            }

            if (!source.isReadable) {
                result.Message = "Mesh has Read/Write disabled. Enable Read/Write on the model importer, then try again.";
                return false;
            }

            var sourcePath = AssetDatabase.GetAssetPath(source);
            if (IsGeneratedEditablePath(sourcePath)) {
                result.Mesh = source;
                result.AssetPath = sourcePath;
                result.Message = "Using the existing generated mesh asset.";
                return true;
            }

            EnsureFolder(GeneratedFolder);

            var clone = UnityEngine.Object.Instantiate(source);
            clone.name = source.name + " " + cloneSuffix;
            clone.MarkDynamic();

            string safeName = SanitizeFileName(renderer.gameObject.name + "_" + source.name + "_" + cloneSuffix);
            string targetPath = AssetDatabase.GenerateUniqueAssetPath($"{GeneratedFolder}/{safeName}.asset");
            AssetDatabase.CreateAsset(clone, targetPath);
            Undo.RegisterCreatedObjectUndo(clone, undoLabel);

            Undo.RecordObject(renderer, undoLabel);
            renderer.sharedMesh = clone;
            EditorUtility.SetDirty(renderer);
            PrefabUtility.RecordPrefabInstancePropertyModifications(renderer);

            result.Mesh = clone;
            result.CreatedClone = true;
            result.AssetPath = targetPath;
            result.Message = $"Created editable mesh at {targetPath}.";
            return true;
        }

        internal static void EnableReadWriteIfPossible(Mesh mesh) {
            if (mesh == null) return;
            var path = AssetDatabase.GetAssetPath(mesh);
            if (string.IsNullOrEmpty(path)) {
                AvatarQolLogger.Instance.Warning(
                    "Mesh Sculpt: selected mesh has no asset path; Read/Write must be enabled manually.");
                return;
            }

            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) {
                AvatarQolLogger.Instance.Warning(
                    $"Mesh Sculpt: mesh at {path} has no ModelImporter; Read/Write must be enabled manually.");
                return;
            }

            importer.isReadable = true;
            importer.SaveAndReimport();
            AvatarQolLogger.Instance.Info($"Mesh Sculpt: Read/Write enabled on {path}.");
        }

        private static bool IsGeneratedEditablePath(string path) {
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
                if (!AssetDatabase.IsValidFolder(next)) {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }

        private static string SanitizeFileName(string name) {
            if (string.IsNullOrEmpty(name)) return "MeshSculpt";
            foreach (var ch in Path.GetInvalidFileNameChars()) {
                name = name.Replace(ch, '_');
            }
            name = name.Replace("(", "").Replace(")", "").Trim();
            return string.IsNullOrEmpty(name) ? "MeshSculpt" : name;
        }
    }
}
