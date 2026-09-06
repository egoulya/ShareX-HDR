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

using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Reference-based measurements of what a tonemap curve did to a frame.
    ///
    /// The reference is the frame decoded to linear BT.709 with 1.0 = SDR white, which is the exact
    /// space the curve consumes. The result is the tonemapped 8-bit sRGB output decoded back to
    /// linear. Every metric is a comparison between those two.
    ///
    /// Two things worth knowing about how these are defined:
    ///
    /// 1. Bands are keyed on the reference's <em>maximum channel</em>, never its luminance.
    ///    Clipping is a per-channel phenomenon: a saturated red at luminance 0.21 already has
    ///    R = 1.0 and must clip. Banding a metric on luminance would call that a defect.
    ///
    /// 2. Some measurements have absolute meaning and some are only comparative. Paper white
    ///    landing at 255, an in-range colour surviving a round trip, a transfer curve that does not
    ///    reverse - those are pass/fail. Local contrast retention is <em>not</em>: compressing 10x
    ///    headroom into SDR necessarily reduces log-contrast, because that is what tonemapping is.
    ///    Metrics of the second kind are reported and diffed against a baseline, never thresholded.
    ///
    /// None of these say a tonemapper "looks good". They catch regressions and rank candidates on
    /// named axes. Preference needs blind A/B.
    /// </summary>
    public static class HdrMetrics
    {
        /// <summary>Tile edge for local-contrast analysis.</summary>
        private const int TileSize = 32;

        /// <summary>
        /// A reference pixel whose channels all sit at or below this is well clear of paper white,
        /// so clipping it is a real defect rather than correct behaviour at the top of the range.
        /// </summary>
        private const float NoClipCeiling = 0.90f;

        private const float MidToneLow = 0.10f;
        private const float MidToneHigh = 0.50f;

        /// <summary>How close to 1.0 a reference pixel must be to count as paper white.</summary>
        private const float PaperWhiteTolerance = 0.02f;

        /// <summary>
        /// Lab chroma below this is treated as neutral and excluded from hue statistics. Hue angle
        /// is numerically meaningless on a near-grey pixel: a warm-tinted white can report tens of
        /// degrees of "drift" from rounding alone.
        /// </summary>
        private const float NeutralChromaFloor = 2.0f;

        /// <summary>
        /// Resolution of the chromaticity-error histogram. The range covers 0 to
        /// <see cref="ChromaticityErrorMax"/> in xy units, which is far past anything a working
        /// pipeline produces.
        /// </summary>
        private const int ChromaticityErrorBins = 4096;

        private const float ChromaticityErrorMax = 0.2f;

        /// <summary>
        /// A just-noticeable difference in CIE xy is roughly 0.002 to 0.004 depending on where in the
        /// diagram it sits. Reported alongside the percentiles so the numbers can be read without
        /// looking the threshold up.
        /// </summary>
        public const float ChromaticityJustNoticeable = 0.003f;

        /// <summary>Log-luminance standard deviation below this makes a tile too flat to score.</summary>
        private const float FlatTileThreshold = 0.05f;

        private const int TransferBins = 192;

        private static readonly float[] SrgbToLinearLut = BuildSrgbToLinearLut();

        public static MetricRow Compute(HdrLinearFrame reference, byte[] outputBgra, MetricRow row, int sampleStride)
        {
            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (sampleStride < 1)
            {
                sampleStride = 1;
            }

            int width = reference.Width;
            int height = reference.Height;

            // Peak over the whole frame, needed up front so the reference span can be normalized the
            // same way the report's full-range view is.
            float peakForSpan = 0f;
            for (int i = 0; i < reference.Rgb.Length; i++)
            {
                float value = reference.Rgb[i];
                if (value > peakForSpan && float.IsFinite(value))
                {
                    peakForSpan = value;
                }
            }

            Accumulator total = new Accumulator();
            object gate = new object();

            List<float> midToneRefCodes = new List<float>();
            List<float> midToneOutCodes = new List<float>();
            List<float> paperWhiteCodes = new List<float>();

            double[] transferSum = new double[TransferBins];
            long[] transferCount = new long[TransferBins];
            int[] outLumHistogram = new int[256];
            int[] refLumHistogram = new int[256];
            int[] passthroughDeltaHistogram = new int[256];

            // Output and reference code histograms restricted to out-of-range pixels, so the span
            // each occupies can be compared. Level *count* is not the same thing: forty distinct
            // values packed into fifteen codes still reads as flat.
            int[] highlightOutHistogram = new int[256];
            int[] highlightRefHistogram = new int[256];

            Parallel.For(0, (height + sampleStride - 1) / sampleStride,
                () => new Accumulator(),
                (yi, _, local) =>
                {
                    int y = yi * sampleStride;

                    for (int x = 0; x < width; x += sampleStride)
                    {
                        int pixel = y * width + x;
                        int ri = pixel * 3;

                        float refR = reference.Rgb[ri];
                        float refG = reference.Rgb[ri + 1];
                        float refB = reference.Rgb[ri + 2];

                        if (!float.IsFinite(refR) || !float.IsFinite(refG) || !float.IsFinite(refB))
                        {
                            continue;
                        }

                        float refLum = HdrLinearFrame.LumR * refR + HdrLinearFrame.LumG * refG + HdrLinearFrame.LumB * refB;
                        if (refLum < 0f)
                        {
                            continue;
                        }

                        float refMaxChannel = MathF.Max(refR, MathF.Max(refG, refB));

                        int oi = pixel * 4;
                        byte outB8 = outputBgra[oi];
                        byte outG8 = outputBgra[oi + 1];
                        byte outR8 = outputBgra[oi + 2];

                        float outR = SrgbToLinearLut[outR8];
                        float outG = SrgbToLinearLut[outG8];
                        float outB = SrgbToLinearLut[outB8];
                        float outLum = HdrLinearFrame.LumR * outR + HdrLinearFrame.LumG * outG + HdrLinearFrame.LumB * outB;

                        local.Samples++;
                        local.RefLumSum += refLum;
                        local.OutLumSum += outLum;
                        local.RefLumMax = MathF.Max(local.RefLumMax, refLum);

                        byte outMax8 = Math.Max(outR8, Math.Max(outG8, outB8));
                        byte outMin8 = Math.Min(outR8, Math.Min(outG8, outB8));

                        if (outMax8 == 255)
                        {
                            local.ClipHigh++;

                            // Only content clear of the top of the range should never clip.
                            if (refMaxChannel <= NoClipCeiling)
                            {
                                local.ClipBelowCeiling++;
                            }
                        }

                        if (refMaxChannel <= NoClipCeiling)
                        {
                            local.BelowCeilingSamples++;
                        }

                        if (outMax8 == 0)
                        {
                            local.ClipLow++;
                            if (refLum > 0.005f)
                            {
                                local.CrushedShadow++;
                            }
                        }

                        bool inRange = refMaxChannel <= 1.0f;
                        if (inRange)
                        {
                            local.InRangeSamples++;

                            // The ground truth for scenario 1: content that fits inside SDR must
                            // survive the round trip unchanged. Only defined where it does fit.
                            int deltaR = PassthroughDelta(refR, outR8);
                            int deltaG = PassthroughDelta(refG, outG8);
                            int deltaB = PassthroughDelta(refB, outB8);
                            int delta = Math.Max(deltaR, Math.Max(deltaG, deltaB));

                            local.PassthroughSamples++;
                            local.PassthroughDeltaHistogram[Math.Min(delta, 255)]++;
                            if (delta > local.PassthroughMaxDelta)
                            {
                                local.PassthroughMaxDelta = delta;
                            }
                        }
                        else
                        {
                            local.HighlightSamples++;
                            if (outMax8 == 255)
                            {
                                local.HighlightClipped++;
                            }

                            int outCodeHi = (int)Math.Clamp(MathF.Round(SrgbEncode(outLum) * 255f), 0, 255);
                            local.HighlightOutHistogram[outCodeHi]++;

                            // The reference span is measured the way the report's full-range view is
                            // built - peak scaled to 1.0 - so the two are directly comparable.
                            float refScaled = peakForSpan > 0f ? refLum / peakForSpan : refLum;
                            int refCodeHi = (int)Math.Clamp(MathF.Round(SrgbEncode(refScaled) * 255f), 0, 255);
                            local.HighlightRefHistogram[refCodeHi]++;
                        }

                        // Hue and chroma are compared at unit luminance so the deliberate lightness
                        // change of tonemapping does not pollute them.
                        float refChromaForTransfer = float.MaxValue;
                        if (refLum > 1e-4f && outLum > 1e-4f)
                        {
                            LabFromNormalizedRgb(refR / refLum, refG / refLum, refB / refLum,
                                out float refA, out float refBb);

                            float refChroma = MathF.Sqrt(refA * refA + refBb * refBb);
                            refChromaForTransfer = refChroma;
                            if (refChroma > NeutralChromaFloor)
                            {
                                LabFromNormalizedRgb(outR / outLum, outG / outLum, outB / outLum,
                                    out float outA, out float outBb);

                                float outChroma = MathF.Sqrt(outA * outA + outBb * outBb);
                                float deltaHue = MathF.Abs(WrapRadians(
                                    MathF.Atan2(outBb, outA) - MathF.Atan2(refBb, refA))) * (180f / MathF.PI);

                                // Chroma-weighted: a rotation on a vivid pixel matters more than the
                                // same rotation on a nearly grey one.
                                if (inRange)
                                {
                                    local.HueWeightInRange += refChroma;
                                    local.HueDriftInRange += deltaHue * refChroma;
                                    local.ChromaRatioInRange += (outChroma / refChroma) * refChroma;
                                }
                                else
                                {
                                    local.HueWeightHighlight += refChroma;
                                    local.HueDriftHighlight += deltaHue * refChroma;
                                    local.ChromaRatioHighlight += (outChroma / refChroma) * refChroma;
                                }

                                // Chromaticity error, which is what actually tells us whether the
                                // colour moved. Taken on the same coloured pixels the Lab figures
                                // use, so the two are directly comparable.
                                float xyError = ChromaticityDistance(refR, refG, refB, outR, outG, outB);
                                int bin = (int)(xyError / ChromaticityErrorMax * ChromaticityErrorBins);
                                local.ChromaticityErrorHistogram[Math.Clamp(bin, 0, ChromaticityErrorBins - 1)]++;
                                local.ChromaticitySamples++;
                            }
                        }

                        // Only near-neutral pixels feed the transfer curve. Binning purely by
                        // luminance would otherwise put a saturated colour and a white of the same
                        // luminance in one bin, and a gamut fit that trades luminance for chroma then
                        // reads as a curve reversal even though no gradient inverted. Luminance
                        // ordering is only meaningful to the eye within a hue, and near-neutral
                        // content is where a genuine inversion would actually be visible.
                        if (refChromaForTransfer <= NeutralChromaFloor)
                        {
                            int bin = (int)Math.Clamp(
                                MathF.Sqrt(MathF.Min(refLum, 16f) / 16f) * (TransferBins - 1), 0, TransferBins - 1);
                            local.TransferSum[bin] += outLum;
                            local.TransferCount[bin]++;
                        }

                        int outCode = (int)Math.Clamp(MathF.Round(SrgbEncode(outLum) * 255f), 0, 255);
                        local.OutLumHistogram[outCode]++;

                        // The reference's own code distribution, so banding is only called out where
                        // the source was smooth to begin with.
                        int refCode = (int)Math.Clamp(MathF.Round(SrgbEncode(Math.Clamp(refLum, 0f, 1f)) * 255f), 0, 255);
                        local.RefLumHistogram[refCode]++;

                        if (refLum >= MidToneLow && refLum <= MidToneHigh)
                        {
                            local.MidToneRefCodes.Add(SrgbEncode(Math.Clamp(refLum, 0f, 1f)) * 255f);
                            local.MidToneOutCodes.Add(outCode);
                        }

                        if (MathF.Abs(refLum - 1f) <= PaperWhiteTolerance && outMax8 - outMin8 <= 6)
                        {
                            local.PaperWhiteCodes.Add(outMax8);
                        }
                    }

                    return local;
                },
                local =>
                {
                    lock (gate)
                    {
                        total.Merge(local);
                        midToneRefCodes.AddRange(local.MidToneRefCodes);
                        midToneOutCodes.AddRange(local.MidToneOutCodes);
                        paperWhiteCodes.AddRange(local.PaperWhiteCodes);

                        for (int i = 0; i < TransferBins; i++)
                        {
                            transferSum[i] += local.TransferSum[i];
                            transferCount[i] += local.TransferCount[i];
                        }

                        for (int i = 0; i < 256; i++)
                        {
                            outLumHistogram[i] += local.OutLumHistogram[i];
                            refLumHistogram[i] += local.RefLumHistogram[i];
                            passthroughDeltaHistogram[i] += local.PassthroughDeltaHistogram[i];
                            highlightOutHistogram[i] += local.HighlightOutHistogram[i];
                            highlightRefHistogram[i] += local.HighlightRefHistogram[i];
                        }
                    }
                });

            long samples = Math.Max(total.Samples, 1);

            row.SampleCount = total.Samples;
            row.RefPeakNits = total.RefLumMax * reference.SdrWhiteNits;
            row.RefMeanNits = (float)(total.RefLumSum / samples) * reference.SdrWhiteNits;
            row.OutMeanLuminance = (float)(total.OutLumSum / samples);
            row.HighlightFraction = total.HighlightSamples / (float)samples;

            row.ClipHighFraction = total.ClipHigh / (float)samples;
            row.HighlightClipFraction = total.HighlightSamples > 0
                ? total.HighlightClipped / (float)total.HighlightSamples
                : float.NaN;
            row.ClipLowFraction = total.ClipLow / (float)samples;
            row.ClipBelowCeilingFraction = total.BelowCeilingSamples > 0
                ? total.ClipBelowCeiling / (float)total.BelowCeilingSamples
                : 0f;
            row.CrushedShadowFraction = total.CrushedShadow / (float)samples;

            row.PassthroughSampleCount = total.PassthroughSamples;
            row.PassthroughMaxCodeDelta = total.PassthroughSamples > 0 ? total.PassthroughMaxDelta : -1;
            row.PassthroughP99CodeDelta = total.PassthroughSamples > 0
                ? Percentile(passthroughDeltaHistogram, total.PassthroughSamples, 0.99)
                : -1;
            row.InRangeFraction = total.InRangeSamples / (float)samples;

            row.HueDriftDegrees = total.HueWeightInRange > 0f
                ? total.HueDriftInRange / total.HueWeightInRange
                : float.NaN;
            row.ChromaRatio = total.HueWeightInRange > 0f
                ? total.ChromaRatioInRange / total.HueWeightInRange
                : float.NaN;
            row.HueDriftHighlightDegrees = total.HueWeightHighlight > 0f
                ? total.HueDriftHighlight / total.HueWeightHighlight
                : float.NaN;
            row.ChromaRatioHighlight = total.HueWeightHighlight > 0f
                ? total.ChromaRatioHighlight / total.HueWeightHighlight
                : float.NaN;
            // The former HueDriftMaxDegrees was the single worst CIELAB rotation over any pixel
            // above a chroma floor of 2 out of roughly 130 - so one barely tinted pixel produced
            // readings near 180 degrees on frames that were otherwise excellent. Percentiles of
            // chromaticity error replace it: bounded, interpretable, and comparable to a JND.
            row.ChromaticityErrorP50 = ChromaticityPercentile(total, 50f);
            row.ChromaticityErrorP95 = ChromaticityPercentile(total, 95f);
            row.ChromaticityErrorP99 = ChromaticityPercentile(total, 99f);
            row.ChromaticityAboveJndFraction = ChromaticityAboveJnd(total);

            row.PaperWhiteSampleCount = paperWhiteCodes.Count;
            row.PaperWhiteMeanCode = paperWhiteCodes.Count > 0 ? Mean(paperWhiteCodes) : float.NaN;

            row.MidToneSampleCount = midToneOutCodes.Count;
            row.MidToneShiftCode = midToneOutCodes.Count > 0
                ? Median(midToneOutCodes) - Median(midToneRefCodes)
                : float.NaN;

            row.HighlightSpanCodes = Span1To99(highlightOutHistogram, total.HighlightSamples);
            row.ReferenceHighlightSpanCodes = Span1To99(highlightRefHistogram, total.HighlightSamples);

            ComputeCodeCoverage(outLumHistogram, out int occupied, out int outGap);
            ComputeCodeCoverage(refLumHistogram, out _, out int refGap);
            row.OccupiedLuminanceCodes = occupied;
            row.MaxLuminanceCodeGap = outGap;
            row.ReferenceLuminanceCodeGap = refGap;

            row.MonotonicityViolations = CountMonotonicityViolations(transferSum, transferCount);

            ComputeLocalContrast(reference, outputBgra, out float meanRatio, out float p10Ratio, out int tiles);
            row.LocalContrastRatio = meanRatio;
            row.LocalContrastRatioP10 = p10Ratio;
            row.ContrastTileCount = tiles;

            return row;
        }

        /// <summary>
        /// Difference in 8-bit codes between the output and a pure passthrough of the reference
        /// channel. Zero means the pipeline returned exactly what went in.
        /// </summary>
        private static int PassthroughDelta(float refChannel, byte outCode)
        {
            int expected = (int)Math.Clamp(MathF.Round(SrgbEncode(Math.Clamp(refChannel, 0f, 1f)) * 255f), 0, 255);
            return Math.Abs(expected - outCode);
        }

        /// <summary>
        /// Per-tile ratio of log-luminance standard deviation, output over reference. Contrast is
        /// multiplicative, so the comparison happens in the log domain.
        ///
        /// COMPARATIVE ONLY. Compressing HDR headroom into SDR necessarily reduces log-contrast, so
        /// a low value on a 10x ramp is the tonemapper working, not failing. Useful for ranking two
        /// curves on the same frame and for spotting regressions; meaningless as a pass/fail bar.
        ///
        /// Always runs at full resolution: subsampling would destroy the detail being measured.
        /// </summary>
        private static void ComputeLocalContrast(HdrLinearFrame reference, byte[] outputBgra,
            out float meanRatio, out float p10Ratio, out int tileCount)
        {
            int width = reference.Width;
            int height = reference.Height;
            int tilesX = Math.Max(1, width / TileSize);
            int tilesY = Math.Max(1, height / TileSize);

            List<float> ratios = new List<float>(tilesX * tilesY);
            object gate = new object();

            Parallel.For(0, tilesY, ty =>
            {
                List<float> local = new List<float>(tilesX);

                for (int tx = 0; tx < tilesX; tx++)
                {
                    int x0 = tx * TileSize;
                    int y0 = ty * TileSize;
                    int x1 = Math.Min(x0 + TileSize, width);
                    int y1 = Math.Min(y0 + TileSize, height);

                    double refSum = 0, refSumSq = 0, outSum = 0, outSumSq = 0;
                    long n = 0;

                    for (int y = y0; y < y1; y++)
                    {
                        for (int x = x0; x < x1; x++)
                        {
                            int pixel = y * width + x;
                            int ri = pixel * 3;
                            float refLum = HdrLinearFrame.LumR * reference.Rgb[ri]
                                + HdrLinearFrame.LumG * reference.Rgb[ri + 1]
                                + HdrLinearFrame.LumB * reference.Rgb[ri + 2];

                            int oi = pixel * 4;
                            float outLum = HdrLinearFrame.LumR * SrgbToLinearLut[outputBgra[oi + 2]]
                                + HdrLinearFrame.LumG * SrgbToLinearLut[outputBgra[oi + 1]]
                                + HdrLinearFrame.LumB * SrgbToLinearLut[outputBgra[oi]];

                            double refLog = Math.Log(Math.Max(refLum, 1e-4f));
                            double outLog = Math.Log(Math.Max(outLum, 1e-4f));

                            refSum += refLog;
                            refSumSq += refLog * refLog;
                            outSum += outLog;
                            outSumSq += outLog * outLog;
                            n++;
                        }
                    }

                    if (n < 16)
                    {
                        continue;
                    }

                    double refStd = StdDev(refSum, refSumSq, n);
                    double outStd = StdDev(outSum, outSumSq, n);

                    // A tile with no reference contrast has no ratio to report.
                    if (refStd < FlatTileThreshold)
                    {
                        continue;
                    }

                    local.Add((float)(outStd / refStd));
                }

                if (local.Count > 0)
                {
                    lock (gate)
                    {
                        ratios.AddRange(local);
                    }
                }
            });

            tileCount = ratios.Count;
            if (ratios.Count == 0)
            {
                meanRatio = float.NaN;
                p10Ratio = float.NaN;
                return;
            }

            meanRatio = Mean(ratios);
            ratios.Sort();
            p10Ratio = ratios[(int)Math.Clamp(ratios.Count * 0.10, 0, ratios.Count - 1)];
        }

        private static double StdDev(double sum, double sumSq, long n)
        {
            double mean = sum / n;
            double variance = Math.Max(sumSq / n - mean * mean, 0);
            return Math.Sqrt(variance);
        }

        /// <summary>
        /// How many distinct luminance codes are used, and the widest gap between consecutive used
        /// codes. Compared against the same figure for the reference, because discrete source
        /// content (UI, a colour-bar pattern) has large gaps of its own that are not banding.
        /// </summary>
        private static void ComputeCodeCoverage(int[] histogram, out int occupied, out int maxGap)
        {
            occupied = 0;
            maxGap = 0;
            int last = -1;

            for (int i = 0; i < histogram.Length; i++)
            {
                if (histogram[i] <= 0)
                {
                    continue;
                }

                occupied++;
                if (last >= 0)
                {
                    maxGap = Math.Max(maxGap, i - last);
                }

                last = i;
            }
        }

        /// <summary>
        /// Counts places where the reference-to-output transfer curve goes backwards: brighter input
        /// producing darker output. Any non-zero count is a real artifact.
        /// </summary>
        private static int CountMonotonicityViolations(double[] sums, long[] counts)
        {
            int violations = 0;
            double previous = double.NaN;

            for (int i = 0; i < sums.Length; i++)
            {
                // Thin bins are noise, not evidence. Discrete source content (UI, colour bars) puts
                // unrelated hues in the same luminance bin, and a curve that maps luminance can
                // legitimately order two of them differently - so require a well-populated bin and a
                // drop that is large relative to the level, not just a fixed epsilon.
                if (counts[i] < 256)
                {
                    continue;
                }

                double mean = sums[i] / counts[i];
                if (!double.IsNaN(previous) && mean < previous - Math.Max(0.002, previous * 0.01))
                {
                    violations++;
                }

                previous = mean;
            }

            return violations;
        }

        /// <summary>
        /// a* and b* for a colour already divided by its own luminance. BT.709 primaries into XYZ,
        /// then the standard CIE Lab transform against D65.
        /// </summary>
        private static void LabFromNormalizedRgb(float r, float g, float b, out float aStar, out float bStar)
        {
            float x = 0.4124f * r + 0.3576f * g + 0.1805f * b;
            float y = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            float z = 0.0193f * r + 0.1192f * g + 0.9505f * b;

            float fx = LabF(x / 0.95047f);
            float fy = LabF(y);
            float fz = LabF(z / 1.08883f);

            aStar = 500f * (fx - fy);
            bStar = 200f * (fy - fz);
        }

        private static float LabF(float t)
        {
            const float delta = 6f / 29f;
            if (t > delta * delta * delta)
            {
                return MathF.Cbrt(t);
            }

            return t / (3f * delta * delta) + 4f / 29f;
        }

        /// <summary>
        /// Distance between two colours' CIE xy chromaticities. Zero means the hue and saturation
        /// were preserved exactly, whatever happened to luminance.
        /// </summary>
        private static float ChromaticityDistance(float r1, float g1, float b1, float r2, float g2, float b2)
        {
            ToXy(r1, g1, b1, out float x1, out float y1);
            ToXy(r2, g2, b2, out float x2, out float y2);
            float dx = x2 - x1;
            float dy = y2 - y1;
            return MathF.Sqrt((dx * dx) + (dy * dy));
        }

        private static void ToXy(float r, float g, float b, out float x, out float y)
        {
            float bigX = (0.4124f * r) + (0.3576f * g) + (0.1805f * b);
            float bigY = (0.2126f * r) + (0.7152f * g) + (0.0722f * b);
            float bigZ = (0.0193f * r) + (0.1192f * g) + (0.9505f * b);
            float sum = bigX + bigY + bigZ;

            if (sum <= 1e-9f)
            {
                x = 0f;
                y = 0f;
                return;
            }

            x = bigX / sum;
            y = bigY / sum;
        }

        private static float ChromaticityPercentile(Accumulator total, float percentile)
        {
            if (total.ChromaticitySamples <= 0) return float.NaN;

            long target = (long)(total.ChromaticitySamples * (percentile / 100.0));
            long seen = 0;
            float binWidth = ChromaticityErrorMax / ChromaticityErrorBins;

            for (int bin = 0; bin < ChromaticityErrorBins; bin++)
            {
                seen += total.ChromaticityErrorHistogram[bin];
                if (seen >= target)
                {
                    return (bin + 0.5f) * binWidth;
                }
            }

            return ChromaticityErrorMax;
        }

        private static float ChromaticityAboveJnd(Accumulator total)
        {
            if (total.ChromaticitySamples <= 0) return float.NaN;

            int threshold = (int)(ChromaticityJustNoticeable / ChromaticityErrorMax * ChromaticityErrorBins);
            long above = 0;
            for (int bin = Math.Min(threshold, ChromaticityErrorBins - 1); bin < ChromaticityErrorBins; bin++)
            {
                above += total.ChromaticityErrorHistogram[bin];
            }

            return above / (float)total.ChromaticitySamples;
        }

        private static float WrapRadians(float angle)
        {
            while (angle > MathF.PI)
            {
                angle -= 2f * MathF.PI;
            }

            while (angle < -MathF.PI)
            {
                angle += 2f * MathF.PI;
            }

            return angle;
        }

        private static float SrgbEncode(float linear)
        {
            linear = Math.Clamp(linear, 0f, 1f);
            if (linear <= 0.0031308f)
            {
                return 12.92f * linear;
            }

            return 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
        }

        private static float[] BuildSrgbToLinearLut()
        {
            float[] lut = new float[256];
            for (int i = 0; i < 256; i++)
            {
                float c = i / 255f;
                lut[i] = c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
            }

            return lut;
        }

        /// <summary>
        /// Code range between the 1st and 99th percentile of a histogram. This is the measure that
        /// corresponds to "detail in the bright parts": how many distinct output levels the highlight
        /// content is actually spread across. Distinct-level count misses it, because values crowded
        /// into a narrow band are still distinct and still look flat.
        /// </summary>
        private static int Span1To99(int[] histogram, long totalSamples)
        {
            if (totalSamples <= 0)
            {
                return -1;
            }

            int low = Percentile(histogram, totalSamples, 0.01);
            int high = Percentile(histogram, totalSamples, 0.99);
            return Math.Max(0, high - low);
        }

        private static int Percentile(int[] histogram, long totalSamples, double percentile)
        {
            long target = Math.Max(1, (long)(totalSamples * percentile));
            long cumulative = 0;
            for (int i = 0; i < histogram.Length; i++)
            {
                cumulative += histogram[i];
                if (cumulative >= target)
                {
                    return i;
                }
            }

            return histogram.Length - 1;
        }

        private static float Mean(List<float> values)
        {
            double sum = 0;
            foreach (float value in values)
            {
                sum += value;
            }

            return (float)(sum / values.Count);
        }

        private static float Median(List<float> values)
        {
            if (values.Count == 0)
            {
                return float.NaN;
            }

            List<float> copy = new List<float>(values);
            copy.Sort();
            int mid = copy.Count / 2;
            return copy.Count % 2 == 1 ? copy[mid] : (copy[mid - 1] + copy[mid]) * 0.5f;
        }

        private sealed class Accumulator
        {
            public long Samples;
            public double RefLumSum;
            public double OutLumSum;
            public float RefLumMax;

            public long ClipHigh;
            public long ClipBelowCeiling;
            public long BelowCeilingSamples;
            public long ClipLow;
            public long CrushedShadow;
            public long InRangeSamples;
            public long HighlightSamples;
            public long HighlightClipped;

            public long PassthroughSamples;
            public int PassthroughMaxDelta;

            public float HueWeightInRange;
            public float HueDriftInRange;
            public float ChromaRatioInRange;
            public float HueWeightHighlight;
            public float HueDriftHighlight;
            public float ChromaRatioHighlight;

            /// <summary>
            /// CIE xy distance between reference and output chromaticity, binned so a percentile can
            /// be taken. This is the measure that actually says whether colour survived: it is
            /// independent of any perceptual coordinate system, and has a meaningful threshold - a
            /// just-noticeable xy difference is roughly 0.002 to 0.004.
            ///
            /// It exists because the CIELAB hue drift beside it is misleading here. CIELAB is not
            /// hue-constant, so legitimately changing a pixel's luminance rotates its Lab hue even
            /// when chromaticity is untouched. Measured on the worst frame in the corpus, Lab
            /// reported 6.83 degrees of in-range drift while xy error was 0.0009 at the median -
            /// well inside a JND. Tuning against the Lab number would have chased an artifact.
            /// </summary>
            public readonly int[] ChromaticityErrorHistogram = new int[ChromaticityErrorBins];
            public long ChromaticitySamples;

            public readonly double[] TransferSum = new double[TransferBins];
            public readonly long[] TransferCount = new long[TransferBins];
            public readonly int[] OutLumHistogram = new int[256];
            public readonly int[] RefLumHistogram = new int[256];
            public readonly int[] PassthroughDeltaHistogram = new int[256];
            public readonly int[] HighlightOutHistogram = new int[256];
            public readonly int[] HighlightRefHistogram = new int[256];

            public readonly List<float> MidToneRefCodes = new();
            public readonly List<float> MidToneOutCodes = new();
            public readonly List<float> PaperWhiteCodes = new();

            public void Merge(Accumulator other)
            {
                Samples += other.Samples;
                RefLumSum += other.RefLumSum;
                OutLumSum += other.OutLumSum;
                RefLumMax = Math.Max(RefLumMax, other.RefLumMax);

                ClipHigh += other.ClipHigh;
                ClipBelowCeiling += other.ClipBelowCeiling;
                BelowCeilingSamples += other.BelowCeilingSamples;
                ClipLow += other.ClipLow;
                CrushedShadow += other.CrushedShadow;
                InRangeSamples += other.InRangeSamples;
                HighlightSamples += other.HighlightSamples;
                HighlightClipped += other.HighlightClipped;

                PassthroughSamples += other.PassthroughSamples;
                PassthroughMaxDelta = Math.Max(PassthroughMaxDelta, other.PassthroughMaxDelta);

                HueWeightInRange += other.HueWeightInRange;
                HueDriftInRange += other.HueDriftInRange;
                ChromaRatioInRange += other.ChromaRatioInRange;
                HueWeightHighlight += other.HueWeightHighlight;
                HueDriftHighlight += other.HueDriftHighlight;
                ChromaRatioHighlight += other.ChromaRatioHighlight;

                ChromaticitySamples += other.ChromaticitySamples;
                for (int i = 0; i < ChromaticityErrorBins; i++)
                {
                    ChromaticityErrorHistogram[i] += other.ChromaticityErrorHistogram[i];
                }
            }
        }
    }
}
