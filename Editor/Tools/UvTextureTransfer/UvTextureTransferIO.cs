// UvTextureTransferIO.cs
//
// Filesystem and AssetDatabase glue for the UV Texture Transfer tool:
//   - import a .fbx from anywhere on disk into a project subfolder so
//     Unity will resolve its Mesh sub-assets;
//   - enumerate the Mesh objects inside an imported model asset;
//   - write the baked Texture2D out as a PNG and configure its
//     TextureImporter so the result is immediately usable as a
//     material slot input.
//
// Kept separate from UvTextureTransferCore so the algorithm has no
// AssetDatabase or filesystem dependency and stays trivially unit
// testable. UI lives in UvTextureTransferWindow.

using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class UvTextureTransferIO {

        // Project-relative scratch folder for FBXes the user pulled in from
        // outside the project. Lives under Assets/ so AssetDatabase will
        // import the model and surface its Mesh sub-assets. Kept under a
        // recognisable underscore-prefixed name so a casual project audit
        // can tell at a glance what this folder is for.
        internal const string ImportedFolder = "Assets/_WhyKnotUvTransfer/imported";

        /// <summary>
        /// Copy <paramref name="absoluteSourcePath"/> into the project's
        /// <see cref="ImportedFolder"/> and import it as a Unity asset.
        /// Returns the project-relative path of the imported file, or
        /// null on failure.
        ///
        /// If a file with the same name already exists in the imported
        /// folder, the imported copy is overwritten so re-picking the
        /// same FBX picks up any changes the user made externally
        /// without leaving stale duplicates behind.
        /// </summary>
        internal static string ImportExternalFbx(string absoluteSourcePath) {
            if (string.IsNullOrEmpty(absoluteSourcePath)) return null;
            if (!File.Exists(absoluteSourcePath)) {
                AvatarQolLogger.Instance.Warning($"UV Texture Transfer: file not found: {absoluteSourcePath}");
                return null;
            }
            EnsureImportFolder();
            string fileName = Path.GetFileName(absoluteSourcePath);
            string destRel = $"{ImportedFolder}/{fileName}";
            string destAbs = Path.GetFullPath(destRel);
            try {
                File.Copy(absoluteSourcePath, destAbs, overwrite: true);
            } catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, $"Copying {absoluteSourcePath} to {destAbs}");
                return null;
            }
            AssetDatabase.ImportAsset(destRel, ImportAssetOptions.ForceUpdate);
            AvatarQolLogger.Instance.Info($"UV Texture Transfer: imported {absoluteSourcePath} -> {destRel}");
            return destRel;
        }

        /// <summary>
        /// Every Mesh sub-asset stored under an FBX (or other model
        /// importer) asset path. Includes nested LODs and submeshes if
        /// the importer split them. Caller picks one for the source
        /// mesh dropdown.
        /// </summary>
        internal static List<Mesh> EnumerateMeshes(string projectRelativeAssetPath) {
            var list = new List<Mesh>();
            if (string.IsNullOrEmpty(projectRelativeAssetPath)) return list;
            var subAssets = AssetDatabase.LoadAllAssetsAtPath(projectRelativeAssetPath);
            if (subAssets == null) return list;
            for (int i = 0; i < subAssets.Length; i++) {
                if (subAssets[i] is Mesh m && m != null) list.Add(m);
            }
            return list;
        }

        /// <summary>
        /// Write <paramref name="tex"/> to disk at <paramref name="absolutePath"/>
        /// as a PNG, then configure the imported texture for typical use
        /// as a material slot input. Returns true on success.
        ///
        /// When the destination sits under Assets/ the importer is set
        /// for linear/sRGB per <paramref name="sRGB"/>, no
        /// alpha-as-transparency, mipmaps on, and bilinear sampling.
        /// </summary>
        internal static bool SavePng(Texture2D tex, string absolutePath, bool sRGB) {
            if (tex == null || string.IsNullOrEmpty(absolutePath)) return false;
            byte[] bytes;
            try {
                bytes = tex.EncodeToPNG();
            } catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, "Encoding PNG");
                return false;
            }
            if (bytes == null || bytes.Length == 0) return false;
            try {
                var dir = Path.GetDirectoryName(absolutePath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(absolutePath, bytes);
            } catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, $"Writing {absolutePath}");
                return false;
            }
            string rel = ToProjectRelative(absolutePath);
            if (rel == null) {
                AvatarQolLogger.Instance.Info(
                    $"UV Texture Transfer: wrote {absolutePath} (outside the project; importer not configured).");
                return true;
            }
            AssetDatabase.ImportAsset(rel);
            var importer = AssetImporter.GetAtPath(rel) as TextureImporter;
            if (importer != null) {
                importer.sRGBTexture         = sRGB;
                importer.alphaIsTransparency = false;
                importer.mipmapEnabled       = true;
                importer.filterMode          = FilterMode.Bilinear;
                importer.wrapMode            = TextureWrapMode.Clamp;
                importer.SaveAndReimport();
            }
            AvatarQolLogger.Instance.Info(
                $"UV Texture Transfer: saved {rel} ({tex.width}x{tex.height}, sRGB={sRGB}).");
            return true;
        }

        // -----------------------------------------------------------------

        private static void EnsureImportFolder() {
            // AssetDatabase prefers folder-by-folder creation; mkdir -p
            // would dodge a CreateFolder roundtrip that asset GUIDs depend on.
            if (!AssetDatabase.IsValidFolder("Assets/_WhyKnotUvTransfer")) {
                AssetDatabase.CreateFolder("Assets", "_WhyKnotUvTransfer");
            }
            if (!AssetDatabase.IsValidFolder(ImportedFolder)) {
                AssetDatabase.CreateFolder("Assets/_WhyKnotUvTransfer", "imported");
            }
        }

        private static string ToProjectRelative(string absolutePath) {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            string project = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/').TrimEnd('/');
            string abs = Path.GetFullPath(absolutePath).Replace('\\', '/');
            if (abs.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase)) {
                return abs.Substring(project.Length + 1);
            }
            return null;
        }
    }
}
