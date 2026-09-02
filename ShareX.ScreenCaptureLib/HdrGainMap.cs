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
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading.Tasks;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Gain map computation, per ISO 21496-1 / Google Ultra HDR.
    ///
    /// A gain map is a side channel, not a different picture. It ships an ordinary SDR image plus a
    /// small map of per-pixel ratios; an HDR-capable viewer multiplies the two back into HDR, and a
    /// viewer that knows nothing about it shows the SDR base and ignores the rest. That asymmetry is
    /// the whole reason to prefer it over emitting HDR directly: a platform that does not cooperate
    /// costs nothing, because the base is what we would have shipped anyway.
    ///
    /// Measured, that matters. A cICP PQ/BT.2020 PNG uploaded to Discord came back with the hues
    /// wrecked (re-encoded without a real PQ tonemap) and to Telegram as a flat grey (PQ rendered as
    /// if it were sRGB). Neither failure is reachable through this path, because nothing a naive
    /// decoder touches is PQ or BT.2020 - the primary image is a plain sRGB JPEG.
    ///
    /// What the map buys us specifically: a global curve has to place every pixel of a given
    /// luminance at one output level, and sRGB has roughly six code values between linear 0.9 and
    /// 1.0, so bright detail is crushed no matter how the curve is tuned (measured: white card
    /// interiors spanning 48 source codes came out spanning 14). The gain map carries that ratio
    /// separately, so the detail travels intact and is reconstructed by the display rather than
    /// faked in SDR. Faking it locally was tried and removed: a base/detail split did restore the
    /// span (14 -> 52 code values) but brought unsharp halos and a measured vignette, and the
    /// reconstruction here supersedes it outright.
    /// </summary>
    public static class HdrGainMap
    {
        /// <summary>
        /// Stabiliser added to both layers before dividing, per the Ultra HDR spec's default. Without
        /// it the ratio explodes wherever the SDR base is near black, and 8 bits of log range then get
        /// spent resolving noise in shadows nobody can see.
        /// </summary>
        public const float DefaultOffset = 1f / 64f;

        /// <summary>
        /// Encoding gamma for the stored map. 1.0 spends code values uniformly across the log-gain
        /// range, which is what we want when the interesting content (clipped highlights) sits at the
        /// top of that range rather than the bottom.
        /// </summary>
        public const float DefaultGamma = 1.0f;

        /// <summary>
        /// Hard ceiling on the stored log2 range. Real captures measured 0.9 to 2.8 stops of headroom;
        /// this only guards against a single stray pixel stretching the range and coarsening every
        /// other code value.
        /// </summary>
        public const float MaxLogRange = 6.0f;

        /// <summary>
        /// Percentile of the log-gain distribution that sets the top of the stored range, rather than
        /// the outright maximum.
        ///
        /// The extreme is not set by highlights but by saturated pixels: the curve maps luminance and
        /// then <see cref="HdrTonemapCurve.FitToGamut"/> scales all three channels together to hold
        /// hue, which drops actual SDR luminance well below the curve's target. Measured on one frame
        /// the maximum was 1.885 stops while p99.9 was 0.957 and p99 was 0.227 - a few hundred pixels
        /// out of 4.95M doubling the range every other pixel has to be quantised across. Those few
        /// clamp to the ceiling and reconstruct a shade dim, which is the better trade.
        /// </summary>
        public const float RangePercentile = 99.99f;

        /// <summary>
        /// How far below unity the stored range is allowed to reach, in stops.
        ///
        /// A gain map encodes brightening: with the curve at or near identity in range, legitimate
        /// ratios sit at or just above 1.0, and a real capture measured a genuine spread of about 0.09
        /// stops. A substantially *negative* ratio means the SDR base is brighter than the HDR layer,
        /// which only happens for pixels composited in after the master was built - the mouse cursor
        /// being the common case.
        ///
        /// A percentile cannot catch those: a cursor is roughly 0.02% of a 4.95M-pixel frame, so any
        /// percentile loose enough to clip it would also clip real content. A fixed floor does, at the
        /// cost of reconstructing those few pixels up to 2^-0.5 dimmer than the base on an HDR display
        /// - far cheaper than spending the whole 8-bit budget on a span nothing uses.
        /// </summary>
        public const float LowRangeFloor = -0.5f;

        private const int PercentileBins = 8192;
        private const float PercentileLow = -8f;
        private const float PercentileHigh = 12f;

        private const float LumEpsilon = 1e-6f;

        /// <summary>
        /// Computes a single-channel gain map from an HDR frame and the SDR image actually derived
        /// from it.
        ///
        /// The ratio is taken against the *quantised* SDR base, not the curve's unrounded output,
        /// because the base is what ships: a decoder multiplies the bytes in the file, so those are
        /// the bytes the ratio has to be correct against. This also means the base's ordered dither
        /// is cancelled by construction rather than left as reconstruction error.
        ///
        /// One channel rather than three is sufficient here because the tonemap fits out-of-gamut
        /// pixels by scaling all three channels together
        /// (<see cref="HdrTonemapCurve.FitToGamut"/>) instead of clipping them independently. The SDR
        /// base therefore keeps its hue everywhere, and a uniform per-pixel gain restores luminance
        /// without disturbing it. A per-channel clip would have needed a three-channel map.
        /// </summary>
        /// <param name="hdr">Linear BT.709 HDR frame, 1.0 = SDR white.</param>
        /// <param name="sdr">The tonemapped 32bppArgb base image, same dimensions as <paramref name="hdr"/>.</param>
        /// <param name="divisor">
        /// Resolution divisor for the stored map. 1 keeps full resolution.
        ///
        /// Note this is not the free size win it is for photographic gain maps. Where the SDR base
        /// clipped, the base carries no detail at all and the map is the *only* carrier, so
        /// downsampling discards exactly the highlight detail this is meant to preserve. Left at 1 by
        /// default for that reason; raise it only against measurements.
        /// </param>
        public static unsafe HdrGainMapData Compute(HdrLinearFrame hdr, Bitmap sdr, int divisor = 1,
            float offsetSdr = DefaultOffset, float offsetHdr = DefaultOffset, float gamma = DefaultGamma)
        {
            if (hdr == null) throw new ArgumentNullException(nameof(hdr));
            if (sdr == null) throw new ArgumentNullException(nameof(sdr));
            if (divisor < 1) throw new ArgumentOutOfRangeException(nameof(divisor), "Divisor must be at least 1.");
            if (gamma <= 0f) throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be positive.");

            if (sdr.Width != hdr.Width || sdr.Height != hdr.Height)
            {
                throw new ArgumentException(
                    $"SDR base is {sdr.Width}x{sdr.Height} but the HDR frame is {hdr.Width}x{hdr.Height}; " +
                    "the gain map is a per-pixel ratio and needs both layers on the same grid.", nameof(sdr));
            }

            int width = hdr.Width;
            int height = hdr.Height;

            // Log2 of the ratio, at full resolution. Kept as a separate pass because the quantisation
            // range is not known until every pixel has been visited.
            float[] logGain = GC.AllocateUninitializedArray<float>(width * height);

            // The content's own peak, which is what display headroom has to be compared against. Taken
            // over every pixel: a maximum cannot be subsampled.
            float hdrPeak = 0f;
            object peakLock = new object();

            BitmapData bd = sdr.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bd.Scan0;
                int stride = bd.Stride;
                float[] rgb = hdr.Rgb;

                Parallel.For(0, height, () => 0f, (y, _, rowPeak) =>
                {
                    byte* src = scan0 + (long)y * stride;
                    int row = y * width;

                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x;
                        int ri = i * 3;

                        float hdrLum = HdrLinearFrame.LumR * rgb[ri]
                            + HdrLinearFrame.LumG * rgb[ri + 1]
                            + HdrLinearFrame.LumB * rgb[ri + 2];

                        // BT.2020 content converted to BT.709 can land slightly negative out of gamut;
                        // it carries no recoverable highlight, so floor it rather than feed log2 a
                        // negative.
                        if (!float.IsFinite(hdrLum) || hdrLum < 0f) hdrLum = 0f;
                        if (hdrLum > rowPeak) rowPeak = hdrLum;

                        float sdrLum = HdrLinearFrame.LumR * SrgbToLinear(src[x * 4 + 2])
                            + HdrLinearFrame.LumG * SrgbToLinear(src[x * 4 + 1])
                            + HdrLinearFrame.LumB * SrgbToLinear(src[x * 4 + 0]);

                        float recovery = (hdrLum + offsetHdr) / MathF.Max(sdrLum + offsetSdr, LumEpsilon);
                        logGain[i] = MathF.Log2(MathF.Max(recovery, LumEpsilon));
                    }

                    return rowPeak;
                },
                rowPeak =>
                {
                    lock (peakLock)
                    {
                        if (rowPeak > hdrPeak) hdrPeak = rowPeak;
                    }
                });
            }
            finally
            {
                sdr.UnlockBits(bd);
            }

            return Finish(logGain, hdrPeak, width, height, divisor, offsetSdr, offsetHdr, gamma);
        }

        /// <summary>
        /// Computes a gain map from the BT.2100 PQ master rather than a decoded linear frame.
        ///
        /// This is the path the live capture uses. The master already exists, is already carried
        /// through to save time, and already follows crops via <see cref="HdrMasterImage.Crop"/> - so
        /// deriving from it keeps the base and the map on the same grid without duplicating crop
        /// handling, and without touching the per-output composite loop in the capture path.
        ///
        /// Only luminance is needed, and luminance is invariant to primaries: CIE Y computed with
        /// BT.2020 coefficients on BT.2020 values is the same quantity as Y from BT.709 coefficients on
        /// BT.709 values. So no gamut conversion is involved, and 16-bit PQ carries far more precision
        /// than 8 bits of log gain can spend.
        /// </summary>
        /// <param name="sdrWhiteNits">The paper white the SDR base was graded against.</param>
        public static unsafe HdrGainMapData ComputeFromMaster(HdrMasterImage master, Bitmap sdr,
            float sdrWhiteNits, int divisor = 1, float offsetSdr = DefaultOffset,
            float offsetHdr = DefaultOffset, float gamma = DefaultGamma)
        {
            if (master == null) throw new ArgumentNullException(nameof(master));
            if (sdr == null) throw new ArgumentNullException(nameof(sdr));
            if (divisor < 1) throw new ArgumentOutOfRangeException(nameof(divisor), "Divisor must be at least 1.");
            if (gamma <= 0f) throw new ArgumentOutOfRangeException(nameof(gamma), "Gamma must be positive.");

            if (sdrWhiteNits < HdrPixelConvert.SceneReferredWhiteNits)
            {
                sdrWhiteNits = HdrPixelConvert.SceneReferredWhiteNits;
            }

            if (sdr.Width != master.Width || sdr.Height != master.Height)
            {
                throw new ArgumentException(
                    $"SDR base is {sdr.Width}x{sdr.Height} but the HDR master is {master.Width}x{master.Height}; " +
                    "the gain map is a per-pixel ratio and needs both layers on the same grid.", nameof(sdr));
            }

            int width = master.Width;
            int height = master.Height;
            float[] logGain = GC.AllocateUninitializedArray<float>(width * height);
            float hdrPeak = 0f;
            object peakLock = new object();

            BitmapData bd = sdr.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bd.Scan0;
                int stride = bd.Stride;
                ushort[] pq = master.Rgb;

                Parallel.For(0, height, () => 0f, (y, _, rowPeak) =>
                {
                    byte* src = scan0 + (long)y * stride;
                    int row = y * width;

                    for (int x = 0; x < width; x++)
                    {
                        int i = row + x;
                        int ri = i * 3;

                        // Normalised so 1.0 is paper white, matching the SDR base's convention.
                        float hdrLum = HdrPixelConvert.Pq16LuminanceNits(pq[ri], pq[ri + 1], pq[ri + 2])
                            / sdrWhiteNits;
                        if (!float.IsFinite(hdrLum) || hdrLum < 0f) hdrLum = 0f;
                        if (hdrLum > rowPeak) rowPeak = hdrLum;

                        float sdrLum = HdrLinearFrame.LumR * SrgbToLinear(src[x * 4 + 2])
                            + HdrLinearFrame.LumG * SrgbToLinear(src[x * 4 + 1])
                            + HdrLinearFrame.LumB * SrgbToLinear(src[x * 4 + 0]);

                        float recovery = (hdrLum + offsetHdr) / MathF.Max(sdrLum + offsetSdr, LumEpsilon);
                        logGain[i] = MathF.Log2(MathF.Max(recovery, LumEpsilon));
                    }

                    return rowPeak;
                },
                rowPeak =>
                {
                    lock (peakLock)
                    {
                        if (rowPeak > hdrPeak) hdrPeak = rowPeak;
                    }
                });
            }
            finally
            {
                sdr.UnlockBits(bd);
            }

            return Finish(logGain, hdrPeak, width, height, divisor, offsetSdr, offsetHdr, gamma);
        }

        /// <summary>
        /// Shared tail: reduce, choose the quantisation range, quantise, and derive the metadata. Both
        /// entry points differ only in how they obtain the per-pixel ratio.
        /// </summary>
        private static HdrGainMapData Finish(float[] logGain, float hdrPeak, int width, int height,
            int divisor, float offsetSdr, float offsetHdr, float gamma)
        {
            int mapWidth = Math.Max(1, (width + divisor - 1) / divisor);
            int mapHeight = Math.Max(1, (height + divisor - 1) / divisor);

            // Average in log space, which is where the quantity is stored and where a ratio averages
            // meaningfully. Dither noise in the base is zero-mean, so this also removes it.
            float[] reduced = divisor == 1
                ? logGain
                : Downsample(logGain, width, height, mapWidth, mapHeight, divisor);

            float min = float.PositiveInfinity;
            float trueMax = float.NegativeInfinity;
            int[] histogram = new int[PercentileBins];
            float binScale = PercentileBins / (PercentileHigh - PercentileLow);

            for (int i = 0; i < reduced.Length; i++)
            {
                float v = reduced[i];
                if (v < min) min = v;
                if (v > trueMax) trueMax = v;

                int bin = (int)((v - PercentileLow) * binScale);
                histogram[Math.Clamp(bin, 0, PercentileBins - 1)]++;
            }

            if (!float.IsFinite(min) || !float.IsFinite(trueMax))
            {
                min = 0f;
                trueMax = 0f;
            }

            // Both ends of the stored range come from percentiles rather than the extremes, so a
            // handful of pixels cannot coarsen quantisation for the whole image.
            //
            // The top is stretched by saturated pixels, as described on RangePercentile. The bottom is
            // stretched by anything composited into the SDR base after the master was built - most
            // often the mouse cursor, which ShareX draws in when ShowCursor is on. Those pixels are
            // bright over a master that reads black, so their ratio is tiny: measured on a real
            // capture, a cursor dragged the low end to -5.64 stops against a genuine spread of about
            // 0.09, spending 8 bits of log gain over a range 60x wider than the picture needs.
            float trueMin = min;
            float max = Percentile(histogram, reduced.Length, RangePercentile);
            min = PercentileFromBottom(histogram, reduced.Length, 100f - RangePercentile);

            if (min < trueMin) min = trueMin;
            if (min < LowRangeFloor) min = LowRangeFloor;
            if (max > trueMax) max = trueMax;
            if (max < min) max = min;

            // A base that is already HDR-complete gives a degenerate range; keep it representable
            // rather than dividing by zero below.
            if (max - min < 1e-4f)
            {
                max = min + 1e-4f;
            }

            if (max - min > MaxLogRange)
            {
                min = MathF.Max(min, max - MaxLogRange);
            }

            byte[] gain = GC.AllocateUninitializedArray<byte>(reduced.Length);
            float span = max - min;
            float invGamma = 1f / gamma;

            Parallel.For(0, mapHeight, y =>
            {
                int row = y * mapWidth;
                for (int x = 0; x < mapWidth; x++)
                {
                    int i = row + x;
                    float normalized = (reduced[i] - min) / span;
                    normalized = Math.Clamp(normalized, 0f, 1f);

                    if (gamma != 1f)
                    {
                        normalized = MathF.Pow(normalized, invGamma);
                    }

                    gain[i] = (byte)Math.Clamp((int)MathF.Round(normalized * 255f), 0, 255);
                }
            });

            return new HdrGainMapData
            {
                Width = mapWidth,
                Height = mapHeight,
                Gain = gain,
                GainMapMin = min,
                GainMapMax = max,
                Gamma = gamma,
                OffsetSdr = offsetSdr,
                OffsetHdr = offsetHdr,
                // Capacity is the display headroom at which the map is applied in full, and a decoder
                // scales its weight by it - so it has to describe the *content*, not the extreme of the
                // ratio. Deriving it from the peak (log2 of content peak over paper white) is the
                // physically meaningful quantity: applying the whole map reproduces the HDR image, and
                // that image needs exactly this much headroom.
                //
                // Taking it from the ratio maximum instead was measurably wrong. On one frame that put
                // capacity at 3.44 stops off a few hundred saturated pixels while the content peak
                // needed 2.40, so every display below 3.44 stops under-applied the map across the whole
                // image and reconstructed it dim.
                HdrCapacityMin = 0f,
                HdrCapacityMax = MathF.Max(MathF.Log2(MathF.Max(hdrPeak, 1f)), 0f),
                HdrPeak = hdrPeak,
                LogGainMax = trueMax,
                LogGainMin = trueMin,
                LogGainStored = max
            };
        }

        private static float[] Downsample(float[] source, int width, int height,
            int mapWidth, int mapHeight, int divisor)
        {
            float[] result = GC.AllocateUninitializedArray<float>(mapWidth * mapHeight);

            Parallel.For(0, mapHeight, my =>
            {
                int y0 = my * divisor;
                int y1 = Math.Min(y0 + divisor, height);

                for (int mx = 0; mx < mapWidth; mx++)
                {
                    int x0 = mx * divisor;
                    int x1 = Math.Min(x0 + divisor, width);

                    double sum = 0;
                    int n = 0;
                    for (int y = y0; y < y1; y++)
                    {
                        int row = y * width;
                        for (int x = x0; x < x1; x++)
                        {
                            sum += source[row + x];
                            n++;
                        }
                    }

                    result[my * mapWidth + mx] = n > 0 ? (float)(sum / n) : 0f;
                }
            });

            return result;
        }

        /// <summary>
        /// Reconstructs HDR luminance the way a decoder will, given a stored code value. Exposed so the
        /// evaluation harness can measure round-trip error against the source frame rather than assume
        /// the encode is right.
        /// </summary>
        public static float Reconstruct(HdrGainMapData map, byte stored, float sdrLinearLuminance,
            float displayHeadroomStops = float.PositiveInfinity)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            float normalized = stored / 255f;
            if (map.Gamma != 1f)
            {
                normalized = MathF.Pow(normalized, map.Gamma);
            }

            float logRecovery = normalized * (map.GainMapMax - map.GainMapMin) + map.GainMapMin;

            // Displays apply a fraction of the map according to their own headroom, which is why a
            // gain map degrades across a range of hardware instead of switching on and off.
            if (float.IsFinite(displayHeadroomStops))
            {
                float capacity = map.HdrCapacityMax - map.HdrCapacityMin;
                if (capacity > 0f)
                {
                    float weight = Math.Clamp(
                        (displayHeadroomStops - map.HdrCapacityMin) / capacity, 0f, 1f);
                    logRecovery *= weight;
                }
            }

            float recovery = MathF.Pow(2f, logRecovery);
            return (sdrLinearLuminance + map.OffsetSdr) * recovery - map.OffsetHdr;
        }

        /// <summary>
        /// Walks a log-gain histogram down from the top to find the value below which
        /// <paramref name="percentile"/> percent of samples fall.
        /// </summary>
        private static float Percentile(int[] histogram, int sampleCount, float percentile)
        {
            if (sampleCount <= 0) return 0f;

            long target = (long)(sampleCount * (1.0 - (percentile / 100.0)));
            long seen = 0;
            float binWidth = (PercentileHigh - PercentileLow) / PercentileBins;

            for (int bin = histogram.Length - 1; bin >= 0; bin--)
            {
                seen += histogram[bin];
                if (seen > target)
                {
                    return PercentileLow + ((bin + 1) * binWidth);
                }
            }

            return PercentileLow;
        }

        /// <summary>
        /// Walks the histogram up from the bottom to find the value above which
        /// <paramref name="percentile"/> percent of samples fall. The mirror of
        /// <see cref="Percentile"/>, used to keep the low end of the range off outliers.
        /// </summary>
        private static float PercentileFromBottom(int[] histogram, int sampleCount, float percentile)
        {
            if (sampleCount <= 0) return 0f;

            long target = (long)(sampleCount * (percentile / 100.0));
            long seen = 0;
            float binWidth = (PercentileHigh - PercentileLow) / PercentileBins;

            for (int bin = 0; bin < histogram.Length; bin++)
            {
                seen += histogram[bin];
                if (seen > target)
                {
                    return PercentileLow + (bin * binWidth);
                }
            }

            return PercentileHigh;
        }

        internal static float SrgbToLinear(byte value)
        {
            float s = value / 255f;
            return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }
    }

    /// <summary>
    /// A computed gain map and the metadata a decoder needs to invert it.
    /// </summary>
    public sealed class HdrGainMapData
    {
        public int Width { get; init; }

        public int Height { get; init; }

        /// <summary>Single-channel stored gain, row-major, <c>Width * Height</c> bytes.</summary>
        public byte[] Gain { get; init; }

        /// <summary>Log2 of the smallest ratio present.</summary>
        public float GainMapMin { get; init; }

        /// <summary>Log2 of the largest ratio present.</summary>
        public float GainMapMax { get; init; }

        public float Gamma { get; init; }

        public float OffsetSdr { get; init; }

        public float OffsetHdr { get; init; }

        /// <summary>Log2 display headroom at which none of the map is applied.</summary>
        public float HdrCapacityMin { get; init; }

        /// <summary>Log2 display headroom at which all of the map is applied.</summary>
        public float HdrCapacityMax { get; init; }

        /// <summary>Peak HDR luminance in the source frame, 1.0 = SDR white. Diagnostic.</summary>
        public float HdrPeak { get; init; }

        /// <summary>The largest log2 ratio actually present, before percentile clamping. Diagnostic.</summary>
        public float LogGainMax { get; init; }

        /// <summary>The smallest log2 ratio actually present, before percentile clamping. Diagnostic.</summary>
        public float LogGainMin { get; init; }

        /// <summary>The log2 ratio the stored range tops out at. Diagnostic.</summary>
        public float LogGainStored { get; init; }

        /// <summary>Display headroom needed to render this content in full, in stops.</summary>
        public float HeadroomStops => HdrCapacityMax;
    }
}
