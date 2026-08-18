#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.
*/

#endregion License Information (GPL v3)

using System;
using System.ComponentModel;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Tonemap profile used after HDR capture is normalized to SDR white = 1.0.
    /// Auto picks Desktop vs AutoHDR from a quick luminance histogram of the frame
    /// (Windows does not expose a reliable "this is Auto-HDR" flag). For HDR DXGI
    /// captures, stats are sampled from the full monitor output; hysteresis is keyed
    /// per display device name (or "span" when multiple outputs contribute).
    /// Windows WIC is screenshots-only; recording falls back to Desktop.
    /// </summary>
    public enum HdrTonemapMode
    {
        [Description("Auto (detect from content)")]
        Auto,

        [Description("Desktop / true HDR (preserve UI white)")]
        Desktop,

        [Description("Auto-HDR games (softer highlights)")]
        AutoHDR,

        [Description("Filmic")]
        Filmic,

        [Description("Windows WIC (screenshots only)")]
        WindowsWIC
    }

    public readonly struct HdrLuminanceStats
    {
        public readonly long SampleCount;
        public readonly long AboveOneCount;
        public readonly long AboveOneHalfCount;
        public readonly long HotUpperSdrCount;
        public readonly float MaxLuminance;
        public readonly float P99Estimate;

        public HdrLuminanceStats(long sampleCount, long aboveOneCount, long aboveOneHalfCount,
            long hotUpperSdrCount, float maxLuminance, float p99Estimate)
        {
            SampleCount = sampleCount;
            AboveOneCount = aboveOneCount;
            AboveOneHalfCount = aboveOneHalfCount;
            HotUpperSdrCount = hotUpperSdrCount;
            MaxLuminance = maxLuminance;
            P99Estimate = p99Estimate;
        }

        public float FractionAboveOne => SampleCount > 0 ? AboveOneCount / (float)SampleCount : 0f;
        public float FractionAboveOneHalf => SampleCount > 0 ? AboveOneHalfCount / (float)SampleCount : 0f;
        public float FractionHotUpperSdr => SampleCount > 0 ? HotUpperSdrCount / (float)SampleCount : 0f;
    }

    /// <summary>
    /// Per-frame CPU tonemap: exposure, BT.2390 PQ (Desktop/AutoHDR) or filmic, then dithered sRGB encode.
    /// </summary>
    public sealed class HdrTonemapCurve
    {
        private readonly HdrTonemapMode mode;
        private readonly float exposure;
        private readonly float[] toneMapLut;
        private readonly float maxInputNorm;
        private readonly float lutScale;

        internal HdrTonemapCurve(HdrTonemapMode mode, float exposure, float[] toneMapLut, float maxInputNorm, float lutScale)
        {
            this.mode = mode;
            this.exposure = exposure;
            this.toneMapLut = toneMapLut;
            this.maxInputNorm = maxInputNorm;
            this.lutScale = lutScale;
        }

        public void Map(ref float r, ref float g, ref float b)
        {
            r = Math.Max(r * exposure, 0f);
            g = Math.Max(g * exposure, 0f);
            b = Math.Max(b * exposure, 0f);

            if (mode == HdrTonemapMode.Filmic)
            {
                HdrTonemap.ApplyFilmic(ref r, ref g, ref b);
                return;
            }

            HdrTonemap.ApplyPqLut(ref r, ref g, ref b, toneMapLut, maxInputNorm, lutScale);
        }

        public byte Encode(float linear, int x, int y) => HdrTonemap.EncodeSrgbDithered(linear, x, y);
    }

    /// <summary>
    /// Shared HDR→SDR tonemap used by DXGI screenshots and recording.
    /// </summary>
    public static class HdrTonemap
    {
        public const float ExposureMin = 0.70f;
        public const float ExposureMax = 1.30f;
        public const float ExposureDefault = 1.00f;

        private const float AutoHdrTargetRatio = 0.75f;
        private const float FilmicKnee = 0.60f;
        private const int ToneMapLutSize = 4096;
        private const int EncodeLutSize = 16384;

        private const float LumR = 0.2126f, LumG = 0.7152f, LumB = 0.0722f;

        private const float PqM1 = 2610f / 16384f;
        private const float PqM2 = 2523f / 4096f * 128f;
        private const float PqC1 = 3424f / 4096f;
        private const float PqC2 = 2413f / 4096f * 32f;
        private const float PqC3 = 2392f / 4096f * 32f;

        private static readonly float[] EncodeLut = BuildEncodeLut();
        private static readonly float[] Bayer8 = BuildBayerMatrix();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, HdrTonemapMode> AutoModeMemory = new();

        public const int HistogramSize = 64;
        private const float MaxTrackedNorm = 16f;

        public static float ClampExposure(float exposure) => Math.Clamp(exposure, ExposureMin, ExposureMax);

        public static HdrTonemapMode ResolveMode(HdrTonemapMode requested, in HdrLuminanceStats stats,
            string hysteresisKey = null, bool hdrDxgiCapture = false)
        {
            if (requested != HdrTonemapMode.Auto)
            {
                return requested;
            }

            HdrTonemapMode candidate = ClassifyContent(stats, hdrDxgiCapture);

            if (!string.IsNullOrEmpty(hysteresisKey) &&
                AutoModeMemory.TryGetValue(hysteresisKey, out HdrTonemapMode previous))
            {
                candidate = ApplyHysteresis(previous, candidate, stats, hdrDxgiCapture);
            }

            if (!string.IsNullOrEmpty(hysteresisKey))
            {
                AutoModeMemory[hysteresisKey] = candidate;
            }

            return candidate;
        }

        public static HdrTonemapMode ResolveForRecording(HdrTonemapMode requested, in HdrLuminanceStats stats,
            string hysteresisKey = null, bool hdrDxgiCapture = false)
        {
            HdrTonemapMode resolved = ResolveMode(requested, stats, hysteresisKey, hdrDxgiCapture);
            return resolved == HdrTonemapMode.WindowsWIC ? HdrTonemapMode.Desktop : resolved;
        }

        public static HdrTonemapCurve CreateCurve(HdrTonemapMode resolvedMode, in HdrLuminanceStats stats,
            float exposure, float sdrWhiteNits)
        {
            exposure = ClampExposure(exposure);
            if (sdrWhiteNits < 80f)
            {
                sdrWhiteNits = 80f;
            }

            if (resolvedMode == HdrTonemapMode.WindowsWIC)
            {
                resolvedMode = HdrTonemapMode.Desktop;
            }

            if (resolvedMode == HdrTonemapMode.Filmic)
            {
                return new HdrTonemapCurve(HdrTonemapMode.Filmic, exposure, null, 1f, 1f);
            }

            float targetRatio = resolvedMode == HdrTonemapMode.AutoHDR ? AutoHdrTargetRatio : 1f;
            float targetNits = sdrWhiteNits * targetRatio;
            float peakNorm = Math.Max(stats.MaxLuminance, Math.Max(stats.P99Estimate, 1f));
            float maxContentNits = Math.Clamp(peakNorm * sdrWhiteNits, targetNits, 10000f);

            float[] lut = BuildToneMapLut(sdrWhiteNits, targetNits, maxContentNits);
            float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);
            float lutScale = (ToneMapLutSize - 1) / MathF.Sqrt(maxInputNorm);

            return new HdrTonemapCurve(resolvedMode, exposure, lut, maxInputNorm, lutScale);
        }

        private static HdrTonemapMode ClassifyContent(in HdrLuminanceStats stats, bool hdrDxgiCapture)
        {
            bool looksLikeTrueHdr =
                stats.MaxLuminance >= 1.75f ||
                stats.FractionAboveOneHalf >= 0.002f ||
                (stats.FractionAboveOne >= 0.01f && stats.P99Estimate >= 1.35f);

            if (looksLikeTrueHdr)
            {
                return HdrTonemapMode.Desktop;
            }

            if (hdrDxgiCapture)
            {
                // Luminance stats cannot distinguish Windows HDR desktop (SDR apps at paper
                // white) from Auto-HDR games — both show max≈1, P99≈1. DXGI HDR capture always
                // uses Desktop; pick Auto-HDR games manually when needed.
                return HdrTonemapMode.Desktop;
            }

            if (stats.FractionHotUpperSdr >= 0.08f || stats.P99Estimate >= 0.92f)
            {
                return HdrTonemapMode.AutoHDR;
            }

            return HdrTonemapMode.Desktop;
        }

        private static HdrTonemapMode ApplyHysteresis(HdrTonemapMode previous, HdrTonemapMode candidate,
            in HdrLuminanceStats stats, bool hdrDxgiCapture)
        {
            if (hdrDxgiCapture)
            {
                return HdrTonemapMode.Desktop;
            }

            if (previous == candidate)
            {
                return candidate;
            }

            if (previous == HdrTonemapMode.Desktop && candidate == HdrTonemapMode.AutoHDR)
            {
                bool stillLooksHdr = stats.MaxLuminance >= 1.50f || stats.FractionAboveOneHalf >= 0.001f;
                if (stillLooksHdr)
                {
                    return HdrTonemapMode.Desktop;
                }

                bool strongAutoHdr = stats.FractionHotUpperSdr >= 0.12f && stats.MaxLuminance < 1.50f;
                return strongAutoHdr ? HdrTonemapMode.AutoHDR : HdrTonemapMode.Desktop;
            }

            if (previous == HdrTonemapMode.AutoHDR && candidate == HdrTonemapMode.Desktop)
            {
                bool strongTrueHdr = stats.MaxLuminance >= 2.00f || stats.FractionAboveOneHalf >= 0.004f;
                return strongTrueHdr ? HdrTonemapMode.Desktop : HdrTonemapMode.AutoHDR;
            }

            return candidate;
        }

        public static void AccumulateSample(float r, float g, float b,
            ref long sampleCount, ref long aboveOne, ref long aboveOneHalf,
            ref long hotUpperSdr, ref float maxLum, Span<int> histogram)
        {
            float lum = LumR * r + LumG * g + LumB * b;
            if (!float.IsFinite(lum) || lum < 0f)
            {
                return;
            }

            sampleCount++;
            if (lum > maxLum) maxLum = lum;
            if (lum > 1f) aboveOne++;
            if (lum > 1.5f) aboveOneHalf++;
            if (lum >= 0.75f && lum <= 1.15f) hotUpperSdr++;

            float t = MathF.Sqrt(MathF.Min(lum, MaxTrackedNorm) / MaxTrackedNorm);
            int bin = (int)Math.Clamp(t * (histogram.Length - 1), 0f, histogram.Length - 1);
            histogram[bin]++;
        }

        public static HdrLuminanceStats BuildStats(long sampleCount, long aboveOne, long aboveOneHalf,
            long hotUpperSdr, float maxLum, ReadOnlySpan<int> histogram)
        {
            float p99 = 0f;
            if (sampleCount > 0)
            {
                long target = Math.Max(1, (long)(sampleCount * 0.99));
                long cumulative = 0;
                for (int i = 0; i < histogram.Length; i++)
                {
                    cumulative += histogram[i];
                    if (cumulative >= target)
                    {
                        float t = (i + 1) / (float)histogram.Length;
                        p99 = t * t * MaxTrackedNorm;
                        break;
                    }
                }

                p99 = Math.Min(p99, Math.Max(maxLum, 0f));
            }

            return new HdrLuminanceStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, p99);
        }

        public static HdrLuminanceStats MergeStats(in HdrLuminanceStats a, in HdrLuminanceStats b)
        {
            if (a.SampleCount == 0) return b;
            if (b.SampleCount == 0) return a;

            long samples = a.SampleCount + b.SampleCount;
            float maxLum = Math.Max(a.MaxLuminance, b.MaxLuminance);
            float p99 = Math.Max(a.P99Estimate, b.P99Estimate);
            return new HdrLuminanceStats(
                samples,
                a.AboveOneCount + b.AboveOneCount,
                a.AboveOneHalfCount + b.AboveOneHalfCount,
                a.HotUpperSdrCount + b.HotUpperSdrCount,
                maxLum,
                p99);
        }

        internal static void ApplyFilmic(ref float r, ref float g, ref float b)
        {
            ApplyKnee(ref r, ref g, ref b, FilmicKnee, 6.0f);
            r = FilmicChannel(r);
            g = FilmicChannel(g);
            b = FilmicChannel(b);
        }

        internal static void ApplyPqLut(ref float r, ref float g, ref float b,
            float[] toneMapLut, float maxInputNorm, float lutScale)
        {
            if (toneMapLut == null)
            {
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                return;
            }

            float lum = LumR * r + LumG * g + LumB * b;
            if (lum <= 1e-6f)
            {
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                return;
            }

            float lutPos = MathF.Sqrt(MathF.Min(lum, maxInputNorm)) * lutScale;
            int lutIndex = (int)lutPos;
            float frac = lutPos - lutIndex;
            int lutNext = Math.Min(lutIndex + 1, ToneMapLutSize - 1);
            float mappedLum = toneMapLut[lutIndex] * (1f - frac) + toneMapLut[lutNext] * frac;

            float scale = mappedLum / lum;
            r *= scale;
            g *= scale;
            b *= scale;

            float maxChannel = MathF.Max(r, MathF.Max(g, b));
            if (maxChannel > 1f)
            {
                float t = (maxChannel - 1f) / MathF.Max(maxChannel - mappedLum, 1e-6f);
                if (t > 1f) t = 1f;
                r += (mappedLum - r) * t;
                g += (mappedLum - g) * t;
                b += (mappedLum - b) * t;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        internal static byte EncodeSrgbDithered(float linear, int x, int y)
        {
            linear = Math.Clamp(linear, 0f, 1f);
            float pos = linear * (EncodeLutSize - 1);
            int index = (int)pos;
            float frac = pos - index;
            int next = Math.Min(index + 1, EncodeLutSize - 1);
            float dither = Bayer8[((y & 7) << 3) + (x & 7)];
            float encoded = EncodeLut[index] * (1f - frac) + EncodeLut[next] * frac + dither;

            if (encoded <= 0f) return 0;
            if (encoded >= 255f) return 255;
            return (byte)(encoded + 0.5f);
        }

        /// <summary>
        /// BT.2390-4 EETF in PQ. Output is scene luminance relative to <paramref name="sdrWhiteNits"/> (1.0 = UI white).
        /// <paramref name="targetNits"/> is the EETF peak (SDR white for Desktop, ~0.75× for Auto-HDR).
        /// </summary>
        private static float[] BuildToneMapLut(float sdrWhiteNits, float targetNits, float maxContentNits)
        {
            float[] lut = new float[ToneMapLutSize];
            float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);

            float pqSourceMax = PqEncode(maxContentNits);
            float pqTargetMax = PqEncode(Math.Max(targetNits, 80f * 0.5f));

            if (pqSourceMax <= pqTargetMax + 1e-6f)
            {
                for (int i = 0; i < ToneMapLutSize; i++)
                {
                    lut[i] = MathF.Min(LutIndexToLuminance(i, maxInputNorm), 1f);
                }

                return lut;
            }

            float maxLumNorm = pqTargetMax / pqSourceMax;
            float ks = 1.5f * maxLumNorm - 0.5f;
            if (ks < 0f) ks = 0f;

            for (int i = 0; i < ToneMapLutSize; i++)
            {
                float yNorm = LutIndexToLuminance(i, maxInputNorm);
                float nits = yNorm * sdrWhiteNits;
                float e1 = PqEncode(nits) / pqSourceMax;

                float e2;
                if (e1 < ks)
                {
                    e2 = e1;
                }
                else
                {
                    float t = (e1 - ks) / Math.Max(1f - ks, 1e-6f);
                    float t2 = t * t;
                    float t3 = t2 * t;
                    e2 = (2f * t3 - 3f * t2 + 1f) * ks
                        + (t3 - 2f * t2 + t) * (1f - ks)
                        + (-2f * t3 + 3f * t2) * maxLumNorm;
                }

                float mappedNits = PqDecode(e2 * pqSourceMax);
                lut[i] = MathF.Min(mappedNits / sdrWhiteNits, 1f);
            }

            return lut;
        }

        private static float LutIndexToLuminance(int index, float maxInputNorm)
        {
            float t = index / (float)(ToneMapLutSize - 1);
            return t * t * maxInputNorm;
        }

        private static void ApplyKnee(ref float r, ref float g, ref float b, float knee, float relativePeak)
        {
            r = Math.Max(r, 0f);
            g = Math.Max(g, 0f);
            b = Math.Max(b, 0f);

            float lum = LumR * r + LumG * g + LumB * b;
            if (lum <= knee || lum < 1e-5f)
            {
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                return;
            }

            float mappedLum = ToneMapShoulder(lum, knee, relativePeak);
            float scale = mappedLum / lum;
            r *= scale;
            g *= scale;
            b *= scale;

            float maximum = Math.Max(r, Math.Max(g, b));
            if (maximum > 1f)
            {
                float gray = Math.Clamp(mappedLum, 0f, 1f);
                float chromaScale = maximum > gray ? (1f - gray) / (maximum - gray) : 0f;
                r = gray + (r - gray) * chromaScale;
                g = gray + (g - gray) * chromaScale;
                b = gray + (b - gray) * chromaScale;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        private static float FilmicChannel(float x)
        {
            x = Math.Clamp(x, 0f, 1f);
            float a = 2.51f, bb = 0.03f, c = 2.43f, d = 0.59f, e = 0.14f;
            return Math.Clamp((x * (a * x + bb)) / (x * (c * x + d) + e), 0f, 1f);
        }

        private static float ToneMapShoulder(float value, float knee, float relativePeak)
        {
            float peak = Math.Max(relativePeak, knee + 0.001f);
            float normalized = Math.Clamp((value - knee) / (peak - knee), 0f, 1f);
            float curveStrength = (peak - knee) / Math.Max(1f - knee, 0.001f);
            float shoulder = curveStrength * normalized / (1f + (curveStrength - 1f) * normalized);
            return knee + (1f - knee) * shoulder;
        }

        private static float[] BuildEncodeLut()
        {
            float[] lut = new float[EncodeLutSize];
            for (int i = 0; i < EncodeLutSize; i++)
            {
                float linear = i / (float)(EncodeLutSize - 1);
                lut[i] = SrgbEncode(linear) * 255f;
            }

            return lut;
        }

        private static float[] BuildBayerMatrix()
        {
            int[] bayer =
            {
                0, 32, 8, 40, 2, 34, 10, 42,
                48, 16, 56, 24, 50, 18, 58, 26,
                12, 44, 4, 36, 14, 46, 6, 38,
                60, 28, 52, 20, 62, 30, 54, 22,
                3, 35, 11, 43, 1, 33, 9, 41,
                51, 19, 59, 27, 49, 17, 57, 25,
                15, 47, 7, 39, 13, 45, 5, 37,
                63, 31, 55, 23, 61, 29, 53, 21
            };

            float[] matrix = new float[64];
            for (int i = 0; i < 64; i++)
            {
                matrix[i] = (bayer[i] + 0.5f) / 64f - 0.5f;
            }

            return matrix;
        }

        private static float SrgbEncode(float linear)
        {
            if (linear <= 0.0031308f)
            {
                return 12.92f * linear;
            }

            return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        }

        private static float PqEncode(float nits)
        {
            float y = MathF.Max(nits, 0f) / 10000f;
            float ym = MathF.Pow(y, PqM1);
            return MathF.Pow((PqC1 + PqC2 * ym) / (1f + PqC3 * ym), PqM2);
        }

        private static float PqDecode(float pq)
        {
            float e = MathF.Pow(MathF.Max(pq, 0f), 1f / PqM2);
            float num = MathF.Max(e - PqC1, 0f);
            float den = PqC2 - PqC3 * e;
            return 10000f * MathF.Pow(num / den, 1f / PqM1);
        }
    }
}
