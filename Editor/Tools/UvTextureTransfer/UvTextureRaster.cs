// UvTextureRaster.cs
//
// Target-UV rasterization, CPU texture sampling, and island padding for
// UV texture transfer.

using System;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace WhyKnot.AvatarQol.Tools {

    internal static class UvTextureRaster {

        internal struct UvSample {
            public int triangleIndex;
            public float wa, wb, wc;
        }

        internal static UvSample[] RasterizeTargetUv(Vector2[] uvs, int[] triangles, int resolution) {
            return RasterizeTargetUv(uvs, triangles, resolution, 0.5f, 0.5f);
        }

        internal static UvSample[] RasterizeTargetUv(
                Vector2[] uvs,
                int[] triangles,
                int resolution,
                float sampleOffsetX,
                float sampleOffsetY) {
            var samples = new UvSample[resolution * resolution];
            for (int i = 0; i < samples.Length; i++) samples[i].triangleIndex = -1;
            if (uvs == null || uvs.Length == 0 || triangles == null || triangles.Length < 3) {
                return samples;
            }

            sampleOffsetX = Mathf.Clamp01(sampleOffsetX);
            sampleOffsetY = Mathf.Clamp01(sampleOffsetY);
            int triCount = triangles.Length / 3;
            float res = resolution;
            for (int t = 0; t < triCount; t++) {
                int i0 = triangles[t * 3];
                int i1 = triangles[t * 3 + 1];
                int i2 = triangles[t * 3 + 2];
                if (i0 >= uvs.Length || i1 >= uvs.Length || i2 >= uvs.Length) continue;
                Vector2 a = uvs[i0];
                Vector2 b = uvs[i1];
                Vector2 c = uvs[i2];
                float minU = Mathf.Min(Mathf.Min(a.x, b.x), c.x);
                float maxU = Mathf.Max(Mathf.Max(a.x, b.x), c.x);
                float minV = Mathf.Min(Mathf.Min(a.y, b.y), c.y);
                float maxV = Mathf.Max(Mathf.Max(a.y, b.y), c.y);
                if (maxU < 0f || minU > 1f || maxV < 0f || minV > 1f) continue;

                int xMin = Mathf.Max(0, Mathf.FloorToInt(minU * res));
                int xMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(maxU * res));
                int yMin = Mathf.Max(0, Mathf.FloorToInt(minV * res));
                int yMax = Mathf.Min(resolution - 1, Mathf.CeilToInt(maxV * res));

                Vector2 v0 = b - a;
                Vector2 v1 = c - a;
                float d00 = Vector2.Dot(v0, v0);
                float d01 = Vector2.Dot(v0, v1);
                float d11 = Vector2.Dot(v1, v1);
                float denom = d00 * d11 - d01 * d01;
                if (Mathf.Abs(denom) < 1e-12f) continue;
                float invDenom = 1f / denom;

                for (int y = yMin; y <= yMax; y++) {
                    float fy = (y + sampleOffsetY) / res;
                    for (int x = xMin; x <= xMax; x++) {
                        float fx = (x + sampleOffsetX) / res;
                        Vector2 v2 = new Vector2(fx - a.x, fy - a.y);
                        float d20 = Vector2.Dot(v2, v0);
                        float d21 = Vector2.Dot(v2, v1);
                        float vBary = (d11 * d20 - d01 * d21) * invDenom;
                        if (vBary < 0f || vBary > 1f) continue;
                        float wBary = (d00 * d21 - d01 * d20) * invDenom;
                        if (wBary < 0f || vBary + wBary > 1f) continue;
                        int idx = y * resolution + x;
                        if (samples[idx].triangleIndex >= 0) continue;
                        samples[idx].triangleIndex = t;
                        samples[idx].wa = 1f - vBary - wBary;
                        samples[idx].wb = vBary;
                        samples[idx].wc = wBary;
                    }
                }
            }
            return samples;
        }

        internal static Color32 SampleBilinearClamp32(Color32[] pixels, int w, int h, float u, float v) {
            if (u < 0f) u = 0f; else if (u > 1f) u = 1f;
            if (v < 0f) v = 0f; else if (v > 1f) v = 1f;
            float fx = u * w;
            float fy = v * h;
            int x0 = (int)Math.Floor(fx);
            int y0 = (int)Math.Floor(fy);
            if (x0 >= w) x0 = w - 1;
            if (y0 >= h) y0 = h - 1;
            if (x0 < 0) x0 = 0;
            if (y0 < 0) y0 = 0;
            float tx = fx - x0;
            float ty = fy - y0;
            if (tx < 0f) tx = 0f; else if (tx > 1f) tx = 1f;
            if (ty < 0f) ty = 0f; else if (ty > 1f) ty = 1f;
            int x1 = x0 + 1; if (x1 >= w) x1 = w - 1;
            int y1 = y0 + 1; if (y1 >= h) y1 = h - 1;

            Color32 c00 = pixels[y0 * w + x0];
            Color32 c10 = pixels[y0 * w + x1];
            Color32 c01 = pixels[y1 * w + x0];
            Color32 c11 = pixels[y1 * w + x1];

            float w00 = (1f - tx) * (1f - ty);
            float w10 = tx * (1f - ty);
            float w01 = (1f - tx) * ty;
            float w11 = tx * ty;
            float r = c00.r * w00 + c10.r * w10 + c01.r * w01 + c11.r * w11;
            float g = c00.g * w00 + c10.g * w10 + c01.g * w01 + c11.g * w11;
            float b = c00.b * w00 + c10.b * w10 + c01.b * w01 + c11.b * w11;
            float a = c00.a * w00 + c10.a * w10 + c01.a * w01 + c11.a * w11;
            return new Color32((byte)(r + 0.5f), (byte)(g + 0.5f), (byte)(b + 0.5f), (byte)(a + 0.5f));
        }

        internal static Color32 ColorToColor32(Color c) {
            return new Color32(
                (byte)(Mathf.Clamp01(c.r) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.g) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.b) * 255f + 0.5f),
                (byte)(Mathf.Clamp01(c.a) * 255f + 0.5f));
        }

        internal static int DilateUvIslandPadding(
                Color32[] pixels,
                bool[] validPixels,
                int resolution,
                int paddingPixels) {
            if (pixels == null || validPixels == null) return 0;
            if (pixels.Length != validPixels.Length) {
                throw new ArgumentException("pixels and validPixels must have the same length");
            }
            if (resolution <= 0 || pixels.Length != resolution * resolution) {
                throw new ArgumentException("resolution does not match pixel buffer length");
            }
            if (paddingPixels <= 0) return 0;

            int totalAdded = 0;
            var currentPixels = pixels;
            var currentValid = validPixels;
            var nextPixels = new Color32[pixels.Length];
            var nextValid = new bool[validPixels.Length];

            for (int iter = 0; iter < paddingPixels; iter++) {
                Array.Copy(currentPixels, nextPixels, currentPixels.Length);
                Array.Copy(currentValid, nextValid, currentValid.Length);
                int addedThisPass = 0;

                Parallel.For(0, resolution, () => 0, (y, state, localAdded) => {
                    int rowBase = y * resolution;
                    for (int x = 0; x < resolution; x++) {
                        int idx = rowBase + x;
                        if (currentValid[idx]) continue;

                        int count = 0;
                        int r = 0, g = 0, b = 0, a = 0;
                        for (int oy = -1; oy <= 1; oy++) {
                            int ny = y + oy;
                            if (ny < 0 || ny >= resolution) continue;
                            int nRow = ny * resolution;
                            for (int ox = -1; ox <= 1; ox++) {
                                if (ox == 0 && oy == 0) continue;
                                int nx = x + ox;
                                if (nx < 0 || nx >= resolution) continue;
                                int nIdx = nRow + nx;
                                if (!currentValid[nIdx]) continue;
                                var c = currentPixels[nIdx];
                                r += c.r; g += c.g; b += c.b; a += c.a;
                                count++;
                            }
                        }
                        if (count == 0) continue;

                        nextPixels[idx] = new Color32(
                            AverageByte(r, count),
                            AverageByte(g, count),
                            AverageByte(b, count),
                            AverageByte(a, count));
                        nextValid[idx] = true;
                        localAdded++;
                    }
                    return localAdded;
                }, localAdded => Interlocked.Add(ref addedThisPass, localAdded));

                if (addedThisPass == 0) break;
                totalAdded += addedThisPass;

                var pixelSwap = currentPixels;
                currentPixels = nextPixels;
                nextPixels = pixelSwap;

                var validSwap = currentValid;
                currentValid = nextValid;
                nextValid = validSwap;
            }

            if (!ReferenceEquals(currentPixels, pixels)) {
                Array.Copy(currentPixels, pixels, pixels.Length);
                Array.Copy(currentValid, validPixels, validPixels.Length);
            }
            return totalAdded;
        }

        internal static byte AverageByte(int numerator, int denominator) {
            if (denominator <= 1) return (byte)Mathf.Clamp(numerator, 0, 255);
            return (byte)Mathf.Clamp((numerator + denominator / 2) / denominator, 0, 255);
        }
    }
}
