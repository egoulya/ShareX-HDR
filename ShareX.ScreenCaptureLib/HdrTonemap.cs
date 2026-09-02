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

        /// <summary>
        /// Mean sampled luminance, relative to SDR white. Estimated from the histogram rather than
        /// summed separately, so no capture path had to change to provide it; the bins are
        /// sqrt-spaced, which puts the fine resolution where most content actually sits.
        /// </summary>
        public readonly float MeanLuminance;

        /// <summary>
        /// High-percentile luminance, used as the curve's peak instead of the single brightest
        /// sample. One stray specular should not define the curve for the whole frame.
        /// </summary>
        public readonly float PeakEstimate;

        public HdrLuminanceStats(long sampleCount, long aboveOneCount, long aboveOneHalfCount,
            long hotUpperSdrCount, float maxLuminance, float p99Estimate, float meanLuminance = 0f,
            float peakEstimate = 0f)
        {
            MeanLuminance = meanLuminance;
            PeakEstimate = peakEstimate > 0f ? peakEstimate : maxLuminance;
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

        /// <summary>
        /// Applies exposure and the tone curve to a luminance value alone, without the gamut fit.
        /// A local operator curves the low-frequency base layer and then re-adds detail, so it needs
        /// the curve's luminance response separately from its per-pixel colour handling.
        /// </summary>
        public float MapLuminance(float lum)
        {
            lum = MathF.Max(lum * exposure, 0f);

            if (mode == HdrTonemapMode.Filmic)
            {
                float shouldered = lum <= HdrTonemap.FilmicKneeValue
                    ? lum
                    : HdrTonemap.ToneMapShoulderValue(lum, HdrTonemap.FilmicKneeValue, 6.0f);
                return Math.Clamp(HdrTonemap.FilmicChannelValue(shouldered), 0f, 1f);
            }

            return HdrTonemap.MapLuminanceViaLut(lum, toneMapLut, maxInputNorm, lutScale);
        }

        /// <summary>Brings an out-of-gamut colour back in without changing hue or saturation.</summary>
        public static void FitToGamut(ref float r, ref float g, ref float b)
        {
            float maxChannel = MathF.Max(r, MathF.Max(g, b));
            if (maxChannel > 1f)
            {
                float fit = 1f / maxChannel;
                r *= fit;
                g *= fit;
                b *= fit;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
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

        /// <summary>
        /// Where the EETF puts the brightest content, as a fraction of SDR white.
        ///
        /// 1.0 means the curve's ceiling *is* paper white, so everything near the top rounds into
        /// 255. Measured on real captures that costs about a third of all above-paper-white pixels,
        /// and up to 96% on frames whose highlights are small and very bright. Backing the ceiling
        /// off a little is what buys the shoulder room to separate those values.
        ///
        /// Any ratio below 1.0 avoids the clipping structurally rather than statistically: the top
        /// of the curve then encodes to about 249 instead of 255, so no amount of peak brightness
        /// pushes it into the top code. What the exact value trades is in-range fidelity against how
        /// finely the highlights are separated.
        ///
        /// Measured at full resolution over a 10-frame corpus (YouTube HDR video, Plague Tale,
        /// Iron Nest, peaks 1.9x to 6.7x paper white), Desktop mode, HDR frames only:
        ///
        ///   ratio   round-trip P99   clipping   distinct levels
        ///   0.85          5.5          0.00%          15.6
        ///   0.95          3.6          0.00%          16.7
        ///   1.00          n/a         31.30%          13.4
        ///
        /// 0.95 is better on every axis, so it is the default. An earlier revision used 0.85 on the
        /// strength of a sweep run through --png-divisor: bicubic downscaling pushes near-white
        /// pixels to 255, which inflated the apparent clipping at higher ratios and made a lower
        /// ceiling look necessary. Clipping analysis has to be done on unscaled output.
        /// </summary>
        private const float AutoHdrTargetRatio = 0.85f;

        /// <summary>
        /// Ceiling used for Desktop once a frame carries real highlight content. Deliberately higher
        /// than <see cref="AutoHdrTargetRatio"/> so the two modes stay distinguishable: Desktop keeps
        /// white bright and closer to the source, AutoHDR gives up brightness for a longer, softer
        /// highlight roll-off. Both landing on the same value made the mode picker meaningless.
        /// </summary>
        private const float HighlightHeadroomRatio = 0.95f;

        /// <summary>
        /// Below this fraction above paper white a frame is treated as having no HDR content, and
        /// the ceiling stays at 1.0 so <see cref="BuildToneMapLut"/> takes its identity early-out and
        /// desktop captures round-trip byte-exact. Real desktop frames measure exactly 0 here;
        /// the dimmest HDR frames measured 0.0012, so the band between is comfortably empty.
        /// </summary>
        private const float HeadroomFractionLow = 0.0002f;

        /// <summary>At or above this fraction the full headroom ratio applies.</summary>
        private const float HeadroomFractionHigh = 0.002f;
        private const float FilmicKnee = 0.60f;
        /// <summary>
        /// How far above the scene average the shoulder must start, in normalized PQ. 0 restores the
        /// plain BT.2390 knee.
        ///
        /// Swept over three real corpora. The gain is concentrated on bright frames and is mostly in
        /// in-range fidelity rather than contrast - on the frames whose shoulder previously fell
        /// below their own average, round-trip error went 19 -> 1, 13 -> 1 and 11 -> 1 codes, with
        /// contrast P10 up 0.03 to 0.06. Frames with a low average were left untouched at 0.999 and
        /// 1 code, which is the point: it only raises the shoulder where the picture needed it.
        /// Above 0.15 everything plateaus and highlight clipping starts creeping up, so 0.15 takes
        /// the whole benefit.
        /// </summary>
        private const float KneeSceneAverageMargin = 0.15f;

        /// <summary>
        /// Percentile used as the curve's peak. 1.0 means the brightest sample.
        ///
        /// The original code read <c>Math.Max(MaxLuminance, Math.Max(P99Estimate, 1))</c>, in which
        /// the maximum always wins over the percentile - so the percentile term was dead, and the
        /// intent behind it (stopping one specular from defining the curve) was never in effect.
        /// libplacebo's high-quality preset uses 99.995% for exactly that reason.
        ///
        /// Swept at 1.0 / 0.99995 / 0.999 / 0.99 over two real corpora, and it made no useful
        /// difference: contrast P10 identical to three decimals on nine of ten frames, round-trip
        /// unchanged, and 0.99 traded 3.5 points more highlight clipping for nothing. The reason is
        /// that <see cref="KneeSceneAverageMargin"/> already floors the shoulder above the scene
        /// average, so an outlier specular can no longer drag the mid-tones down - the knee fix
        /// removed the problem this was aimed at.
        ///
        /// Left at 1.0 rather than shipping a change with no measured benefit. The plumbing stays
        /// because it replaces the misleading Math.Max with something explicit, and because a
        /// pathological frame (a sun at 10000 nits) is not represented in the corpus yet.
        /// </summary>
        private const float PeakPercentile = 1.0f;

        /// <summary>Shoulder bounds, matching libplacebo's knee_minimum / knee_maximum.</summary>
        private const float KneeMinimum = 0.10f;
        private const float KneeMaximum = 0.80f;

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

        /// <summary>
        /// Luminance histogram bin count. 256 rather than 64 so a high percentile can actually be
        /// resolved: the bins are sqrt-spaced, and at 64 the topmost ones each spanned a large
        /// luminance range, which is enough for a P99 but not for the P99.9 the curve peak uses.
        /// </summary>
        public const int HistogramSize = 256;
        private const float MaxTrackedNorm = 16f;

        /// <summary>BT.709 luminance of a linear RGB triple, normalized so 1.0 is SDR white.</summary>
        public static float Luminance(float r, float g, float b) => LumR * r + LumG * g + LumB * b;

        public static float ClampExposure(float exposure) => Math.Clamp(exposure, ExposureMin, ExposureMax);

        /// <summary>
        /// The modes worth offering in the UI. Every member of <see cref="HdrTonemapMode"/> stays
        /// reachable in code - the evaluation harness needs all of them to compare against, and
        /// <see cref="HdrTonemapMode.AutoHDR"/> is still what <see cref="ClassifyContent"/> can select
        /// - but two of them should not be presented as choices:
        ///
        /// <see cref="HdrTonemapMode.AutoHDR"/> is measurably worse than Desktop on real captures
        /// (highlight span 87.4 against 95.0 and 49.2 against 56.9 over two corpora, reference 113.9
        /// and 88.1) because its ceiling is fixed at <see cref="AutoHdrTargetRatio"/>, the value a
        /// sweep showed losing to 0.95 on every axis. It was never selected by Auto on 17 of 17 real
        /// frames either, since Windows Auto-HDR content on a real display peaks well past the 1.75
        /// threshold that reads as true HDR.
        ///
        /// <see cref="HdrTonemapMode.WindowsWIC"/> loses on every measured axis - round-trip error
        /// 18.2 code values against 1.8, highlight hue drift 9.17 degrees against 0.44, highlight
        /// chroma 0.854 against 1.006 - and it refuses HDR10 outputs outright with
        /// WINCODEC_ERR_UNSUPPORTEDPIXELFORMAT, so on some displays it does not work at all.
        /// </summary>
        public static readonly HdrTonemapMode[] SelectableModes =
        {
            HdrTonemapMode.Auto,
            HdrTonemapMode.Desktop,
            HdrTonemapMode.Filmic
        };

        /// <summary>
        /// Maps a stored mode onto something the UI offers, so a setting saved before a mode was
        /// withdrawn does not leave the combo displaying one value while the settings file holds
        /// another.
        /// </summary>
        public static HdrTonemapMode CoerceSelectableMode(HdrTonemapMode mode)
        {
            return Array.IndexOf(SelectableModes, mode) >= 0 ? mode : HdrTonemapMode.Auto;
        }

        /// <summary>
        /// Clears the per-display Auto-mode hysteresis memory. Auto decisions are sticky by
        /// design, which makes any test touching <see cref="ResolveMode"/> with a hysteresis key
        /// order-dependent; call this first to get a deterministic starting state.
        /// </summary>
        public static void ResetAutoModeMemory() => AutoModeMemory.Clear();

        /// <summary>Drops the remembered Auto mode for one display only.</summary>
        public static void ResetAutoModeMemory(string hysteresisKey)
        {
            if (!string.IsNullOrEmpty(hysteresisKey))
            {
                AutoModeMemory.TryRemove(hysteresisKey, out _);
            }
        }

        /// <summary>The Auto mode currently remembered for a display, if any. Diagnostics only.</summary>
        public static bool TryGetRememberedAutoMode(string hysteresisKey, out HdrTonemapMode mode)
        {
            if (string.IsNullOrEmpty(hysteresisKey))
            {
                mode = default;
                return false;
            }

            return AutoModeMemory.TryGetValue(hysteresisKey, out mode);
        }

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

            float targetRatio = ResolveTargetRatio(resolvedMode, stats);
            float targetNits = sdrWhiteNits * targetRatio;
            // A high percentile rather than the single brightest sample: one specular highlight
            // should not stretch the curve for the whole frame. Content above the estimate is
            // clamped to the LUT's top entry, which costs the top fraction of a percent of pixels
            // their separation - measurably cheaper than compressing everything else to accommodate
            // them.
            float peakNorm = Math.Max(stats.PeakEstimate, 1f);
            float maxContentNits = Math.Clamp(peakNorm * sdrWhiteNits, targetNits, 10000f);

            float[] lut = BuildToneMapLut(sdrWhiteNits, targetNits, maxContentNits,
                stats.MeanLuminance * sdrWhiteNits);
            float maxInputNorm = MathF.Max(maxContentNits / sdrWhiteNits, 1f);
            float lutScale = (ToneMapLutSize - 1) / MathF.Sqrt(maxInputNorm);

            return new HdrTonemapCurve(resolvedMode, exposure, lut, maxInputNorm, lutScale);
        }

        /// <summary>
        /// Picks the EETF ceiling. Desktop ramps from 1.0 to <see cref="HighlightHeadroomRatio"/> as
        /// a frame gains content above paper white, rather than switching abruptly: two near-identical
        /// frames either side of a hard threshold would otherwise be graded visibly differently.
        /// </summary>
        internal static float ResolveTargetRatio(HdrTonemapMode resolvedMode, in HdrLuminanceStats stats)
        {
            if (resolvedMode == HdrTonemapMode.AutoHDR)
            {
                return AutoHdrTargetRatio;
            }

            float fraction = stats.FractionAboveOne;

            if (fraction <= HeadroomFractionLow)
            {
                // No HDR content: keep the ceiling at paper white so the curve stays an identity and
                // SDR content survives the round trip unchanged.
                return 1f;
            }

            if (fraction >= HeadroomFractionHigh)
            {
                return HighlightHeadroomRatio;
            }

            float t = (fraction - HeadroomFractionLow) / (HeadroomFractionHigh - HeadroomFractionLow);
            t = t * t * (3f - 2f * t);
            return 1f + (HighlightHeadroomRatio - 1f) * t;
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

            if (IsFlatPaperWhite(stats))
            {
                return HdrTonemapMode.Desktop;
            }

            if (LooksLikeAutoHdrGame(stats))
            {
                return HdrTonemapMode.AutoHDR;
            }

            if (!hdrDxgiCapture && (stats.FractionHotUpperSdr >= 0.08f || stats.P99Estimate >= 0.92f))
            {
                return HdrTonemapMode.AutoHDR;
            }

            return HdrTonemapMode.Desktop;
        }

        /// <summary>
        /// Full-screen paper white or flat SDR swatches (e.g. white bar in a test pattern).
        /// </summary>
        private static bool IsFlatPaperWhite(in HdrLuminanceStats stats)
        {
            return stats.FractionHotUpperSdr >= 0.90f &&
                stats.MaxLuminance <= 1.02f &&
                stats.P99Estimate <= 1.02f &&
                stats.FractionAboveOneHalf < 0.0005f;
        }

        /// <summary>
        /// Auto-HDR games lift SDR into the upper band without true-HDR peaks (max ~1.0–1.6, P99 ~1.0+).
        /// Distinct from desktop UI where max≈1.05 and hot-band fraction is lower.
        /// </summary>
        private static bool LooksLikeAutoHdrGame(in HdrLuminanceStats stats)
        {
            if (stats.MaxLuminance < 1.06f || stats.MaxLuminance >= 1.75f)
            {
                return false;
            }

            if (stats.P99Estimate < 0.94f)
            {
                return false;
            }

            if (stats.FractionHotUpperSdr >= 0.90f)
            {
                return false;
            }

            if (stats.FractionHotUpperSdr >= 0.13f)
            {
                return true;
            }

            return stats.FractionHotUpperSdr >= 0.08f &&
                stats.MaxLuminance >= 1.10f &&
                stats.P99Estimate >= 0.98f;
        }

        private static HdrTonemapMode ApplyHysteresis(HdrTonemapMode previous, HdrTonemapMode candidate,
            in HdrLuminanceStats stats, bool hdrDxgiCapture)
        {
            if (previous == candidate)
            {
                return candidate;
            }

            if (IsFlatPaperWhite(stats) || stats.MaxLuminance >= 1.75f)
            {
                return HdrTonemapMode.Desktop;
            }

            if (previous == HdrTonemapMode.Desktop && candidate == HdrTonemapMode.AutoHDR)
            {
                bool stillLooksHdr = stats.MaxLuminance >= 1.50f || stats.FractionAboveOneHalf >= 0.001f;
                if (stillLooksHdr)
                {
                    return HdrTonemapMode.Desktop;
                }

                return LooksLikeAutoHdrGame(stats) ? HdrTonemapMode.AutoHDR : HdrTonemapMode.Desktop;
            }

            if (previous == HdrTonemapMode.AutoHDR && candidate == HdrTonemapMode.Desktop)
            {
                bool strongTrueHdr = stats.MaxLuminance >= 2.00f || stats.FractionAboveOneHalf >= 0.004f;
                if (strongTrueHdr)
                {
                    return HdrTonemapMode.Desktop;
                }

                return LooksLikeAutoHdrGame(stats) ? HdrTonemapMode.AutoHDR : HdrTonemapMode.Desktop;
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
            float p99 = Percentile(histogram, sampleCount, 0.99f, maxLum);
            float peakEstimate = Percentile(histogram, sampleCount, PeakPercentile, maxLum);

            float mean = 0f;
            if (sampleCount > 0)
            {
                double weighted = 0;
                long counted = 0;
                for (int i = 0; i < histogram.Length; i++)
                {
                    if (histogram[i] == 0)
                    {
                        continue;
                    }

                    // Invert the sqrt bin spacing at the bin centre.
                    float t = (i + 0.5f) / histogram.Length;
                    weighted += (double)t * t * MaxTrackedNorm * histogram[i];
                    counted += histogram[i];
                }

                if (counted > 0)
                {
                    mean = (float)(weighted / counted);
                }
            }

            return new HdrLuminanceStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, p99,
                mean, peakEstimate);
        }

        /// <summary>
        /// Luminance at the given percentile, read off the sqrt-spaced histogram and capped at the
        /// observed maximum.
        /// </summary>
        private static float Percentile(ReadOnlySpan<int> histogram, long sampleCount, float percentile,
            float maxLum)
        {
            if (sampleCount <= 0)
            {
                return 0f;
            }

            long target = Math.Max(1, (long)(sampleCount * percentile));
            long cumulative = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (cumulative >= target)
                {
                    float t = (i + 1) / (float)histogram.Length;
                    return Math.Min(t * t * MaxTrackedNorm, Math.Max(maxLum, 0f));
                }
            }

            return Math.Max(maxLum, 0f);
        }

        public static HdrLuminanceStats MergeStats(in HdrLuminanceStats a, in HdrLuminanceStats b)
        {
            if (a.SampleCount == 0) return b;
            if (b.SampleCount == 0) return a;

            long samples = a.SampleCount + b.SampleCount;
            float maxLum = Math.Max(a.MaxLuminance, b.MaxLuminance);
            float p99 = Math.Max(a.P99Estimate, b.P99Estimate);
            float mean = (float)((a.MeanLuminance * (double)a.SampleCount +
                b.MeanLuminance * (double)b.SampleCount) / samples);
            float peakEstimate = Math.Max(a.PeakEstimate, b.PeakEstimate);
            return new HdrLuminanceStats(
                samples,
                a.AboveOneCount + b.AboveOneCount,
                a.AboveOneHalfCount + b.AboveOneHalfCount,
                a.HotUpperSdrCount + b.HotUpperSdrCount,
                maxLum,
                p99,
                mean,
                peakEstimate);
        }

        internal static void ApplyFilmic(ref float r, ref float g, ref float b)
        {
            r = Math.Max(r, 0f);
            g = Math.Max(g, 0f);
            b = Math.Max(b, 0f);

            float lum = LumR * r + LumG * g + LumB * b;
            if (lum <= 1e-5f)
            {
                r = Math.Clamp(r, 0f, 1f);
                g = Math.Clamp(g, 0f, 1f);
                b = Math.Clamp(b, 0f, 1f);
                return;
            }

            // The curve runs on luminance and the colour is carried through by the same ratio, which
            // is the structure the PQ path uses. Running it per channel instead - as this did - sends
            // each channel through a different part of the curve, so a saturated colour comes out
            // both desaturated and hue-shifted. Measured on real captures that was chroma 0.35 and
            // 11 degrees of drift: worse than the defect the PQ path started with.
            // ToneMapShoulder only defines the region above the knee - below it the normalized term
            // clamps to zero and the function returns the knee itself, so calling it unconditionally
            // crushes every shadow and mid-tone to one value. Contrast P10 measured 0.021 that way.
            float shouldered = lum <= FilmicKnee ? lum : ToneMapShoulder(lum, FilmicKnee, 6.0f);
            float mappedLum = FilmicChannel(shouldered);

            float scale = mappedLum / lum;
            r *= scale;
            g *= scale;
            b *= scale;

            // Same gamut fit as the PQ path: scale the whole colour so the brightest channel lands
            // at 1.0, keeping hue and saturation and spending luminance instead.
            float maxChannel = MathF.Max(r, MathF.Max(g, b));
            if (maxChannel > 1f)
            {
                float fit = 1f / maxChannel;
                r *= fit;
                g *= fit;
                b *= fit;
            }

            r = Math.Clamp(r, 0f, 1f);
            g = Math.Clamp(g, 0f, 1f);
            b = Math.Clamp(b, 0f, 1f);
        }

        internal const float FilmicKneeValue = FilmicKnee;

        internal static float ToneMapShoulderValue(float value, float knee, float relativePeak) =>
            ToneMapShoulder(value, knee, relativePeak);

        internal static float FilmicChannelValue(float x) => FilmicChannel(x);

        /// <summary>Luminance response of the tone curve LUT, with no colour handling.</summary>
        internal static float MapLuminanceViaLut(float lum, float[] toneMapLut, float maxInputNorm,
            float lutScale)
        {
            if (toneMapLut == null)
            {
                return Math.Clamp(lum, 0f, 1f);
            }

            float lutPos = MathF.Sqrt(MathF.Min(lum, maxInputNorm)) * lutScale;
            int lutIndex = (int)lutPos;
            float frac = lutPos - lutIndex;
            int lutNext = Math.Min(lutIndex + 1, ToneMapLutSize - 1);
            return toneMapLut[lutIndex] * (1f - frac) + toneMapLut[lutNext] * frac;
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

            // A colour whose brightest channel still exceeds 1.0 after the luminance mapping has to
            // be brought into gamut somehow, and the choice of how is visible. Three options,
            // measured over real captures (highlight chroma retained / mean hue drift):
            //
            //   pull every channel toward the mapped luminance   0.47-0.64 / 2.4-3.3 deg
            //   clip each channel independently                  0.92-0.94 / 6.6-8.6 deg
            //   scale the whole colour by 1/maxChannel           1.000     / 0.4 deg
            //
            // The first holds luminance exactly and pays in chroma, which is what turned a vivid
            // orange UI bar into pale pink. The second keeps chroma but lets channels clip at
            // different points, twisting hue. Uniform scaling keeps the channel ratios untouched, so
            // hue and saturation both survive, and spends luminance instead.
            //
            // Spending luminance costs little in practice: for a near-neutral highlight - snow,
            // cloud, white UI - maxChannel is close to the luminance already, so the scale is nearly
            // a no-op. It only bites on saturated out-of-gamut colour, which is exactly where
            // keeping the colour matters more than keeping the exact brightness.
            float maxChannel = MathF.Max(r, MathF.Max(g, b));
            if (maxChannel > 1f)
            {
                // Scale the whole colour so the brightest channel lands at 1.0. The channel ratios
                // are untouched, so hue and saturation both survive exactly; the cost is luminance.
                //
                // That cost is small in practice: for a near-neutral highlight - snow, cloud, white
                // UI - maxChannel is already close to the luminance, so the scale is nearly a no-op.
                // It only bites on saturated colour, which is where keeping the colour matters more
                // than keeping the exact brightness.
                //
                // A hybrid was tried that capped the luminance loss and desaturated the remainder,
                // to remove transfer-curve reversals this appeared to introduce. It was not needed:
                // those reversals were an artifact of measuring monotonicity across all hues at
                // once, which pairs a saturated colour with an equally-luminant white and calls the
                // legitimate ordering an inversion. Measured on near-neutral pixels, where luminance
                // ordering is what the eye actually tracks, there are none.
                float fit = 1f / maxChannel;
                r *= fit;
                g *= fit;
                b *= fit;
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
        private static float[] BuildToneMapLut(float sdrWhiteNits, float targetNits, float maxContentNits,
            float sceneAverageNits)
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

            // BT.2390 puts the shoulder at 1.5 * maxLumNorm - 0.5, which depends only on how far the
            // peak has to travel. It ignores where the picture actually sits, and on bright scenes
            // that lands the shoulder *below* the scene average - so most of the image ends up in the
            // compressed region and mid-tone contrast collapses. Measured on real captures, the two
            // frames whose shoulder fell below their average were exactly the two with the worst
            // contrast retention (0.11 and 0.55, against 0.90+ for every frame whose shoulder cleared
            // its average).
            //
            // So the shoulder must also clear the scene average by a margin. This only ever raises
            // it, and only on the frames that were flattening. The bounds follow libplacebo's
            // knee_minimum / knee_maximum, which bracket the same quantity in the same PQ space.
            float ks = 1.5f * maxLumNorm - 0.5f;
            if (ks < 0f) ks = 0f;

            if (sceneAverageNits > 0f && KneeSceneAverageMargin > 0f)
            {
                float averageNorm = PqEncode(sceneAverageNits) / pqSourceMax;
                float wanted = Math.Clamp(averageNorm + KneeSceneAverageMargin, KneeMinimum, KneeMaximum);
                ks = MathF.Max(ks, wanted);
            }

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
