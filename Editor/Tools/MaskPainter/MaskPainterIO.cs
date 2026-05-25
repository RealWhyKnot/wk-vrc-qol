// MaskPainterIO.cs
//
// File I/O and pure math for the Mask Painter tool. Lives in its own
// file partly to keep MaskPainterWindow focused on UI/lifecycle, and
// partly so the unit-testable helpers (ray-triangle, barycentric, mirror)
// can be exercised from the test asmdef without touching Editor state.
//
// InternalsVisibleTo lets the Tests.Editor asmdef see internal members
// without forcing them to be public on the package's editor surface.

using System;
using System.IO;
using System.Runtime.CompilerServices;
using UnityEditor;
using UnityEngine;

[assembly: InternalsVisibleTo("dev.whyknot.wk-vrc-qol.Tests.Editor")]

namespace WhyKnot.AvatarQol.Tools {

    internal static class MaskPainterIO {

        // -----------------------------------------------------------------
        // Shader loading
        // -----------------------------------------------------------------

        private const string ShaderFolder = "Packages/dev.whyknot.wk-vrc-qol/Editor/Tools/MaskPainter/Shaders";

        private static Shader _brushShader;
        private static Shader _previewShader;
        private static Shader _dilateShader;

        internal static Shader BrushShader   => _brushShader   != null ? _brushShader   : (_brushShader   = LoadShader("UvSpaceBrush.shader"));
        internal static Shader PreviewShader => _previewShader != null ? _previewShader : (_previewShader = LoadShader("MaskPreviewOverlay.shader"));
        internal static Shader DilateShader  => _dilateShader  != null ? _dilateShader  : (_dilateShader  = LoadShader("MaskDilate.shader"));

        private static Shader LoadShader(string fileName) {
            var path = $"{ShaderFolder}/{fileName}";
            var s = AssetDatabase.LoadAssetAtPath<Shader>(path);
            if (s == null) {
                AvatarQolLogger.Instance.Error(
                    $"MaskPainter: shader not found at {path}. Reimport the package or check that the file exists.");
            }
            return s;
        }

        // -----------------------------------------------------------------
        // Pure math (testable from Tests.Editor)
        // -----------------------------------------------------------------

        /// <summary>
        /// Moller-Trumbore ray vs triangle, two-sided. On hit returns t
        /// (distance along the ray) and the barycentric coords (u, v) for
        /// vertices 1 and 2; the coord for vertex 0 is w = 1 - u - v.
        /// Returns false for parallel rays, hits behind the origin, and
        /// degenerate triangles.
        ///
        /// Two-sided is the right behaviour for a paint tool: the user
        /// clicks on what they see, regardless of whether the nearest
        /// polygon happens to face the camera. Avatars often ship with
        /// inside-out body geometry (transparent shaders, hair cards,
        /// inverted-normal stylisations) that a one-sided test would
        /// silently reject. The depth-min sort below picks the closest
        /// triangle either way, so painting through a sleeve onto the
        /// body underneath isn't an issue in practice -- the sleeve's
        /// closer triangle wins.
        /// </summary>
        internal static bool RayTriangle(
                Vector3 rayOrigin, Vector3 rayDir,
                Vector3 v0, Vector3 v1, Vector3 v2,
                out float t, out float u, out float v) {
            t = 0f; u = 0f; v = 0f;
            const float EPS = 1e-7f;
            Vector3 e1 = v1 - v0;
            Vector3 e2 = v2 - v0;
            Vector3 p  = Vector3.Cross(rayDir, e2);
            float   det = Vector3.Dot(e1, p);
            // Parallel ray or degenerate triangle: |det| approaches 0.
            if (det > -EPS && det < EPS) return false;
            float invDet = 1f / det;
            Vector3 s = rayOrigin - v0;
            u = Vector3.Dot(s, p) * invDet;
            if (u < 0f || u > 1f) return false;
            Vector3 q = Vector3.Cross(s, e1);
            v = Vector3.Dot(rayDir, q) * invDet;
            if (v < 0f || u + v > 1f) return false;
            t = Vector3.Dot(e2, q) * invDet;
            return t > 0f;
        }

        /// <summary>
        /// Interpolate per-vertex Vector2 (UV) at a barycentric hit. u
        /// and v come from <see cref="RayTriangle"/>; the third weight
        /// is 1 - u - v on vertex 0.
        /// </summary>
        internal static Vector2 InterpolateUv(Vector2 uv0, Vector2 uv1, Vector2 uv2, float u, float v) {
            float w = 1f - u - v;
            return uv0 * w + uv1 * u + uv2 * v;
        }

        /// <summary>
        /// Resolve the submesh iteration window for one paint pass.
        ///
        /// <paramref name="requestedIndex"/> == -1 means "every submesh". A
        /// non-negative index in range runs only that one. Anything else --
        /// a negative value other than -1, or an index >= subMeshCount --
        /// is treated as "every submesh" so the painter never silently
        /// produces a zero-triangle pass after the snapshot mesh changes
        /// shape underneath the stored selection. <paramref name="onWarning"/>
        /// fires once per call when the fallback kicks in so the drift is
        /// visible in the log instead of looking like a silent miss.
        ///
        /// Returns (start, end, fellBackToAll): iterate [start, end) and
        /// branch on fellBackToAll if the caller wants to flag the
        /// fallback in the UI as well.
        /// </summary>
        internal static (int start, int end, bool fellBackToAll) SubmeshRange(
                int requestedIndex, int subMeshCount, Action<string> onWarning = null) {
            if (subMeshCount <= 0) return (0, 0, false);
            if (requestedIndex == -1) return (0, subMeshCount, false);
            if (requestedIndex >= 0 && requestedIndex < subMeshCount) {
                return (requestedIndex, requestedIndex + 1, false);
            }
            onWarning?.Invoke(
                $"Submesh selector ({requestedIndex}) is out of range for the snapshot mesh " +
                $"(subMeshCount={subMeshCount}); falling back to all submeshes for this pass.");
            return (0, subMeshCount, true);
        }

        /// <summary>
        /// Reflect a world-space point across the symmetry-root's local
        /// X axis. Falls back to a world-X mirror when the root is null,
        /// which suits avatars sitting at the world origin facing +Z.
        /// </summary>
        internal static Vector3 MirrorAcrossLocalX(Vector3 worldPos, Transform symmetryRoot) {
            if (symmetryRoot == null) {
                return new Vector3(-worldPos.x, worldPos.y, worldPos.z);
            }
            Vector3 local = symmetryRoot.InverseTransformPoint(worldPos);
            local.x = -local.x;
            return symmetryRoot.TransformPoint(local);
        }

        // -----------------------------------------------------------------
        // UV wireframe (UV island visualization)
        // -----------------------------------------------------------------

        /// <summary>
        /// Build a Texture2D where pixels lit to white-alpha trace the
        /// mesh's UV0 triangle edges and clear-alpha everywhere else.
        /// Caller tints at draw time via <c>GUI.DrawTexture</c>'s tint
        /// parameter so the same texture can serve light and dark mask
        /// previews without a rebuild.
        ///
        /// CPU Bresenham rasterizer with submesh filtering: edges whose
        /// endpoints sit outside [0, 1]^2 are dropped so UVs that wrap or
        /// tile don't smear lines along the texture border. Linear color
        /// space because the result is data (mask wireframe), not a
        /// gamma-encoded swatch.
        /// </summary>
        internal static Texture2D GenerateUvWireframe(Mesh mesh, int submeshIndex, int size) {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false, linear: true) {
                hideFlags  = HideFlags.HideAndDontSave,
                name       = $"WkMaskPainter_UvWireframe_{(mesh != null ? mesh.GetInstanceID() : 0)}_{submeshIndex}_{size}",
                filterMode = FilterMode.Bilinear,
                wrapMode   = TextureWrapMode.Clamp,
            };
            var pixels = new Color32[size * size];
            // Color32 defaults to (0,0,0,0); leave it that way.
            if (mesh == null || mesh.uv == null || mesh.uv.Length == 0) {
                tex.SetPixels32(pixels);
                tex.Apply(false, false);
                return tex;
            }
            var uvs   = mesh.uv;
            var range = SubmeshRange(submeshIndex, mesh.subMeshCount, null);
            var line  = new Color32(255, 255, 255, 255);
            for (int s = range.start; s < range.end; s++) {
                if (mesh.GetTopology(s) != MeshTopology.Triangles) continue;
                var tris = mesh.GetTriangles(s);
                for (int i = 0; i + 2 < tris.Length; i += 3) {
                    int i0 = tris[i], i1 = tris[i + 1], i2 = tris[i + 2];
                    if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                    var a = uvs[i0]; var b = uvs[i1]; var c = uvs[i2];
                    DrawUvLine(pixels, size, a, b, line);
                    DrawUvLine(pixels, size, b, c, line);
                    DrawUvLine(pixels, size, c, a, line);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);
            return tex;
        }

        /// <summary>
        /// Bresenham line into a Color32 buffer indexed (y * size + x).
        /// Skips lines whose endpoints sit outside [0,1]^2 -- UV tiling
        /// would otherwise paint a fake border along the edge of the
        /// preview. Y axis matches UV space (origin bottom-left) by
        /// flipping the texture-row index so islands display the right
        /// way up against the painted mask.
        /// </summary>
        internal static void DrawUvLine(Color32[] pixels, int size, Vector2 a, Vector2 b, Color32 color) {
            const float eps = 0.005f;
            if (a.x < -eps || a.x > 1f + eps || a.y < -eps || a.y > 1f + eps) return;
            if (b.x < -eps || b.x > 1f + eps || b.y < -eps || b.y > 1f + eps) return;
            int max = size - 1;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(a.x * max), 0, max);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(a.y * max), 0, max);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(b.x * max), 0, max);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(b.y * max), 0, max);
            int dx = Mathf.Abs(x1 - x0); int sx = x0 < x1 ? 1 : -1;
            int dy = -Mathf.Abs(y1 - y0); int sy = y0 < y1 ? 1 : -1;
            int err = dx + dy;
            // Hard cap on iterations so a pathological mesh can't lock the
            // Editor up if a UV passes the [0,1] filter but the rasterised
            // endpoints diverge by more pixels than the texture has.
            int safety = 4 * size;
            while (safety-- > 0) {
                pixels[y0 * size + x0] = color;
                if (x0 == x1 && y0 == y1) break;
                int e2 = err * 2;
                if (e2 >= dy) { err += dy; x0 += sx; }
                if (e2 <= dx) { err += dx; y0 += sy; }
            }
        }

        // -----------------------------------------------------------------
        // RenderTexture helpers
        // -----------------------------------------------------------------

        internal static RenderTexture CreateMaskRT(int size) {
            // Linear color space: the mask is data, not photographic
            // colour. sRGB would gamma-bend a 50% strength stroke.
            var desc = new RenderTextureDescriptor(size, size, RenderTextureFormat.ARGB32, 0) {
                sRGB        = false,
                autoGenerateMips = false,
                useMipMap   = false,
            };
            var rt = new RenderTexture(desc) {
                name = $"WhyKnotMaskPainter_{size}",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            rt.Create();
            ClearRT(rt, Color.clear);
            return rt;
        }

        internal static void ClearRT(RenderTexture rt, Color color) {
            if (rt == null) return;
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            GL.Clear(true, true, color);
            RenderTexture.active = prev;
        }

        /// <summary>
        /// Ping-pong dilation through MaskDilate.shader for N iterations.
        /// Returns a newly allocated temporary RT; caller must release
        /// via RenderTexture.ReleaseTemporary.
        /// </summary>
        internal static RenderTexture Dilate(RenderTexture source, int iterations) {
            if (source == null || iterations <= 0) {
                var copy = RenderTexture.GetTemporary(source.descriptor);
                Graphics.Blit(source, copy);
                return copy;
            }
            var shader = DilateShader;
            if (shader == null) {
                var copy = RenderTexture.GetTemporary(source.descriptor);
                Graphics.Blit(source, copy);
                return copy;
            }
            var mat = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            try {
                var a = RenderTexture.GetTemporary(source.descriptor);
                var b = RenderTexture.GetTemporary(source.descriptor);
                Graphics.Blit(source, a);
                for (int i = 0; i < iterations; i++) {
                    Graphics.Blit(a, b, mat);
                    var tmp = a; a = b; b = tmp;
                }
                RenderTexture.ReleaseTemporary(b);
                return a;
            } finally {
                UnityEngine.Object.DestroyImmediate(mat);
            }
        }

        // -----------------------------------------------------------------
        // PNG save / load
        // -----------------------------------------------------------------

        internal struct SaveOptions {
            public string Path;
            public bool   Dilate;
            public int    DilationIterations;
            public bool   SRGB;
        }

        /// <summary>
        /// Save the RT to a PNG. When the target sits under Assets/, the
        /// resulting Texture importer is configured for typical mask use
        /// (linear unless overridden, no alpha-is-transparency, mipmaps
        /// on, clamp wrap).
        /// </summary>
        internal static bool SavePng(RenderTexture rt, SaveOptions opt) {
            if (rt == null || string.IsNullOrEmpty(opt.Path)) return false;
            RenderTexture dilated = null;
            var source = rt;
            try {
                if (opt.Dilate && opt.DilationIterations > 0) {
                    dilated = Dilate(rt, opt.DilationIterations);
                    source = dilated;
                }
                var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false, linear: true);
                var prev = RenderTexture.active;
                try {
                    RenderTexture.active = source;
                    tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
                    tex.Apply(updateMipmaps: false, makeNoLongerReadable: false);
                } finally {
                    RenderTexture.active = prev;
                }
                byte[] bytes = tex.EncodeToPNG();
                UnityEngine.Object.DestroyImmediate(tex);
                if (bytes == null || bytes.Length == 0) return false;

                var dir = Path.GetDirectoryName(opt.Path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllBytes(opt.Path, bytes);

                var rel = ToProjectRelative(opt.Path);
                if (rel != null) {
                    AssetDatabase.ImportAsset(rel);
                    var importer = AssetImporter.GetAtPath(rel) as TextureImporter;
                    if (importer != null) {
                        importer.sRGBTexture        = opt.SRGB;
                        importer.alphaIsTransparency = false;
                        importer.mipmapEnabled       = true;
                        importer.wrapMode            = TextureWrapMode.Clamp;
                        importer.SaveAndReimport();
                    }
                    AvatarQolLogger.Instance.Info(
                        $"Mask saved to {rel} ({rt.width}x{rt.height}, dilate={opt.Dilate}/{opt.DilationIterations}, sRGB={opt.SRGB}).");
                } else {
                    AvatarQolLogger.Instance.Info(
                        $"Mask saved to {opt.Path} (outside the project; importer not configured).");
                }
                return true;
            } finally {
                if (dilated != null) RenderTexture.ReleaseTemporary(dilated);
            }
        }

        /// <summary>
        /// Load a PNG into an existing RT (Blit into the target). The
        /// PNG can be any size; it will be resampled to the RT's
        /// resolution by the blit's bilinear filter.
        /// </summary>
        internal static bool LoadPng(string path, RenderTexture into) {
            if (into == null || string.IsNullOrEmpty(path) || !File.Exists(path)) return false;
            byte[] bytes;
            try { bytes = File.ReadAllBytes(path); }
            catch (Exception ex) {
                AvatarQolLogger.Instance.Exception(ex, $"Reading {path}");
                return false;
            }
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear: true);
            if (!tex.LoadImage(bytes)) {
                UnityEngine.Object.DestroyImmediate(tex);
                AvatarQolLogger.Instance.Warning($"Mask load: {path} is not a readable image.");
                return false;
            }
            Graphics.Blit(tex, into);
            UnityEngine.Object.DestroyImmediate(tex);
            AvatarQolLogger.Instance.Info($"Mask loaded from {path} into {into.width}x{into.height} buffer.");
            return true;
        }

        // -----------------------------------------------------------------
        // Path utilities
        // -----------------------------------------------------------------

        private static string ToProjectRelative(string absolutePath) {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            var project = Path.GetFullPath(Application.dataPath + "/..").Replace('\\', '/').TrimEnd('/');
            var abs     = Path.GetFullPath(absolutePath).Replace('\\', '/');
            if (abs.StartsWith(project + "/", StringComparison.OrdinalIgnoreCase)) {
                return abs.Substring(project.Length + 1);
            }
            return null;
        }
    }
}
