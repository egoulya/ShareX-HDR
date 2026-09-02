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
    /// Collects the luminance histogram and counters that drive Auto mode detection and the
    /// tonemap curve's peak. Samples on a ~64x64 grid over the region, matching what the live
    /// capture path does, so offline replay produces identical stats.
    /// </summary>
    public sealed class HdrLuminanceAccumulator
    {
        private const int TargetSamplesPerAxis = 64;

        /// <summary>
        /// Whether the peak is taken over every pixel rather than the histogram's sampling grid.
        ///
        /// The distribution is statistical, so a 64x64 grid describes it perfectly well. A peak is
        /// an extremum, and subsampling an extremum is not an approximation - it simply misses. On a
        /// 3440x1440 frame the grid reads 4096 of 4.95M pixels, so a small specular highlight is
        /// almost certain to be skipped, and everything above the underestimated peak is then clamped
        /// to the tone curve's top entry and comes out flat. Measured on real captures, the peak was
        /// underestimated by 16-52% on half the frames.
        ///
        /// Screenshots scan every pixel for the peak; recording keeps the grid, where per-frame cost
        /// matters more than a slightly clipped specular.
        /// </summary>
        public bool FullResolutionPeak { get; init; } = true;

        private long sampleCount, aboveOne, aboveOneHalf, hotUpperSdr;
        private float maxLum;
        private readonly int[] hist = new int[HdrTonemap.HistogramSize];

        public unsafe void AddFromPacked(byte[] packed, int packedStride, int format, int copyW, int copyH,
            float sdrWhiteNits)
        {
            int stepY = Math.Max(1, copyH / TargetSamplesPerAxis);
            int stepX = Math.Max(1, copyW / TargetSamplesPerAxis);
            fixed (byte* packedPtr = packed)
            {
                Add(packedPtr, packedStride, format, 0, 0, copyW, copyH, sdrWhiteNits, stepX, stepY);
            }
        }

        public unsafe void AddFromMapped(IntPtr data, int rowPitch, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits)
        {
            int stepY = Math.Max(1, copyH / TargetSamplesPerAxis);
            int stepX = Math.Max(1, copyW / TargetSamplesPerAxis);
            Add((byte*)data, rowPitch, format, srcX, srcY, copyW, copyH, sdrWhiteNits, stepX, stepY);
        }

        internal unsafe void Add(byte* basePtr, int stride, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits, int stepX, int stepY)
        {
            int bpp = HdrPixelConvert.BytesPerPixel(format);

            if (!FullResolutionPeak)
            {
                for (int y = 0; y < copyH; y += stepY)
                {
                    byte* gridRow = basePtr + (long)(srcY + y) * stride + (long)srcX * bpp;
                    for (int x = 0; x < copyW; x += stepX)
                    {
                        HdrPixelConvert.DecodeToSdrNormalized(format, gridRow + x * bpp, sdrWhiteNits,
                            out float gr, out float gg, out float gb);
                        HdrTonemap.AccumulateSample(gr, gg, gb, ref sampleCount, ref aboveOne,
                            ref aboveOneHalf, ref hotUpperSdr, ref maxLum, hist);
                    }
                }

                return;
            }

            // One pass: every pixel contributes to the peak, every grid pixel also to the histogram
            // and the counters. Walking the whole frame costs a decode per pixel, which is worth it
            // for a screenshot and is why recording opts out.
            float peak = maxLum;
            for (int y = 0; y < copyH; y++)
            {
                byte* srcRow = basePtr + (long)(srcY + y) * stride + (long)srcX * bpp;
                bool histogramRow = (y % stepY) == 0;

                for (int x = 0; x < copyW; x++)
                {
                    HdrPixelConvert.DecodeToSdrNormalized(format, srcRow + x * bpp, sdrWhiteNits,
                        out float r, out float g, out float b);

                    if (histogramRow && (x % stepX) == 0)
                    {
                        HdrTonemap.AccumulateSample(r, g, b, ref sampleCount, ref aboveOne,
                            ref aboveOneHalf, ref hotUpperSdr, ref maxLum, hist);
                        if (maxLum > peak)
                        {
                            peak = maxLum;
                        }

                        continue;
                    }

                    float lum = HdrTonemap.Luminance(r, g, b);
                    if (float.IsFinite(lum) && lum > peak)
                    {
                        peak = lum;
                    }
                }
            }

            maxLum = peak;
        }

        public HdrLuminanceStats Build() =>
            HdrTonemap.BuildStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, hist);
    }

    /// <summary>
    /// A frame decoded to linear BT.709 RGB, normalized so 1.0 is the display's SDR white.
    /// Interleaved R,G,B per pixel. Values above 1.0 are HDR headroom.
    /// </summary>
    public sealed class HdrLinearFrame
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Interleaved linear R,G,B. Length is <c>Width * Height * 3</c>.</summary>
        public float[] Rgb { get; }

        /// <summary>The SDR white level 1.0 corresponds to, in nits.</summary>
        public float SdrWhiteNits { get; }

        public HdrLinearFrame(int width, int height, float[] rgb, float sdrWhiteNits)
        {
            Width = width;
            Height = height;
            Rgb = rgb;
            SdrWhiteNits = sdrWhiteNits;
        }

        public const float LumR = 0.2126f;
        public const float LumG = 0.7152f;
        public const float LumB = 0.0722f;

        public float LuminanceAt(int index)
        {
            int i = index * 3;
            return LumR * Rgb[i] + LumG * Rgb[i + 1] + LumB * Rgb[i + 2];
        }
    }

    /// <summary>
    /// Outcome of tonemapping one whole frame. <see cref="ResolvedMode"/> and <see cref="Stats"/>
    /// are what Auto mode actually decided, which is the interesting part when evaluating results.
    /// </summary>
    public sealed class HdrTonemapResult : IDisposable
    {
        /// <summary>The tonemapped SDR bitmap, 32bppArgb.</summary>
        public Bitmap Sdr { get; init; }

        /// <summary>Pre-tonemap BT.2100 PQ reference, or null when it was not requested.</summary>
        public HdrMasterImage Master { get; init; }

        /// <summary>The mode Auto resolved to, or the requested mode when it was explicit.</summary>
        public HdrTonemapMode ResolvedMode { get; init; }

        public HdrTonemapMode RequestedMode { get; init; }

        public HdrLuminanceStats Stats { get; init; }

        /// <summary>The SDR white level the curve was built against.</summary>
        public float SdrWhiteNits { get; init; }

        public float Exposure { get; init; }

        public void Dispose() => Sdr?.Dispose();
    }

    /// <summary>
    /// The seam between HDR frame acquisition and HDR-to-SDR tonemapping.
    ///
    /// <see cref="TonemapFrame"/> takes a decoded-from-nothing raw DXGI frame and produces the
    /// same SDR bitmap the live capture path would, using the same primitives below. That makes
    /// a captured <see cref="HdrRawFrame"/> corpus a stand-in for the display: curve changes can
    /// be evaluated across desktop, Auto-HDR and native-HDR content without booting anything.
    /// </summary>
    public static class HdrFrameTonemapper
    {
        /// <summary>
        /// Replays a captured frame through the full tonemap pipeline. Stats are gathered over the
        /// whole frame, matching the live path, which samples the entire output rather than the
        /// requested crop.
        /// </summary>
        /// <param name="hysteresisKey">
        /// Auto-mode memory key. Leave null for offline evaluation so results do not depend on the
        /// order frames are processed in.
        /// </param>
        public static unsafe HdrTonemapResult TonemapFrame(HdrRawFrame frame, HdrTonemapMode requestedMode,
            float exposure = HdrTonemap.ExposureDefault, bool buildMaster = false,
            string hysteresisKey = null, bool hdrDxgiCapture = true)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            int width = frame.Width;
            int height = frame.Height;
            int stride = frame.Stride;
            int format = frame.DxgiFormat;
            float sdrWhiteNits = frame.SdrWhiteNits > 0f ? frame.SdrWhiteNits : HdrPixelConvert.SceneReferredWhiteNits;

            Bitmap sdr = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            HdrMasterImage master = null;

            try
            {
                if (buildMaster)
                {
                    master = new HdrMasterImage(width, height)
                    {
                        MasteringDisplay = frame.Metadata.ToMasteringDisplay()
                    };
                }

                if (requestedMode == HdrTonemapMode.WindowsWIC)
                {
                    fixed (byte* pixels = frame.Pixels)
                    {
                        bool ok = HdrWicTonemap.TryBlitToBitmap((IntPtr)pixels, stride, width, height, format,
                            0, 0, width, height, sdr, 0, 0);
                        if (!ok)
                        {
                            throw new InvalidOperationException(
                                "Windows WIC tonemap failed for DXGI format " + format + ".");
                        }

                        if (master != null)
                        {
                            FillMaster(pixels, stride, format, 0, 0, width, height, 0, 0, sdrWhiteNits, master);
                        }
                    }

                    return new HdrTonemapResult
                    {
                        Sdr = sdr,
                        Master = master,
                        RequestedMode = HdrTonemapMode.WindowsWIC,
                        ResolvedMode = HdrTonemapMode.WindowsWIC,
                        Stats = default,
                        SdrWhiteNits = sdrWhiteNits,
                        Exposure = exposure
                    };
                }

                HdrLuminanceAccumulator accumulator = new HdrLuminanceAccumulator();
                accumulator.AddFromPacked(frame.Pixels, stride, format, width, height, sdrWhiteNits);
                HdrLuminanceStats stats = accumulator.Build();

                HdrTonemapMode resolvedMode = HdrTonemap.ResolveMode(requestedMode, stats, hysteresisKey, hdrDxgiCapture);
                HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, stats, exposure, sdrWhiteNits);

                fixed (byte* pixels = frame.Pixels)
                {
                    BlitRegion(pixels, stride, format, 0, 0, width, height, sdrWhiteNits, sdr, 0, 0, curve, master);
                }

                return new HdrTonemapResult
                {
                    Sdr = sdr,
                    Master = master,
                    RequestedMode = requestedMode,
                    ResolvedMode = resolvedMode,
                    Stats = stats,
                    SdrWhiteNits = sdrWhiteNits,
                    Exposure = exposure
                };
            }
            catch
            {
                sdr.Dispose();
                throw;
            }
        }

        /// <summary>
        /// Decodes a whole frame to linear BT.709 RGB normalized so 1.0 is the display's SDR
        /// white. This is the exact space the tonemap curve consumes, which makes it the right
        /// reference for measuring what a curve did to a frame.
        ///
        /// Allocates <c>width * height * 3</c> floats - about 100 MB for 4K - so it is meant for
        /// offline analysis, not the capture path.
        /// </summary>
        public static unsafe HdrLinearFrame DecodeToLinear(HdrRawFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            int width = frame.Width;
            int height = frame.Height;
            int format = frame.DxgiFormat;
            int bpp = HdrPixelConvert.BytesPerPixel(format);
            int stride = frame.Stride;
            float sdrWhiteNits = frame.SdrWhiteNits > 0f ? frame.SdrWhiteNits : HdrPixelConvert.SceneReferredWhiteNits;

            float[] rgb = GC.AllocateUninitializedArray<float>(checked(width * height * 3));

            fixed (byte* srcBase = frame.Pixels)
            fixed (float* dstBase = rgb)
            {
                byte* src = srcBase;
                float* dst = dstBase;

                Parallel.For(0, height, y =>
                {
                    byte* srcRow = src + (long)y * stride;
                    float* dstRow = dst + (long)y * width * 3;

                    for (int x = 0; x < width; x++)
                    {
                        HdrPixelConvert.DecodeToSdrNormalized(format, srcRow + x * bpp, sdrWhiteNits,
                            out float r, out float g, out float b);
                        dstRow[x * 3] = r;
                        dstRow[x * 3 + 1] = g;
                        dstRow[x * 3 + 2] = b;
                    }
                });
            }

            return new HdrLinearFrame(width, height, rgb, sdrWhiteNits);
        }

        /// <summary>
        /// Encodes a frame to its pre-tonemap BT.2100 PQ reference without tonemapping. The result
        /// does not depend on mode or exposure, so a sweep can build it once per frame.
        /// </summary>
        public static unsafe HdrMasterImage BuildMaster(HdrRawFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            float sdrWhiteNits = frame.SdrWhiteNits > 0f ? frame.SdrWhiteNits : HdrPixelConvert.SceneReferredWhiteNits;
            HdrMasterImage master = new HdrMasterImage(frame.Width, frame.Height)
            {
                MasteringDisplay = frame.Metadata.ToMasteringDisplay()
            };

            fixed (byte* pixels = frame.Pixels)
            {
                FillMaster(pixels, frame.Stride, frame.DxgiFormat, 0, 0, frame.Width, frame.Height,
                    0, 0, sdrWhiteNits, master);
            }

            return master;
        }

        /// <summary>Gathers whole-frame stats without producing a bitmap.</summary>
        public static HdrLuminanceStats BuildStats(HdrRawFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            float sdrWhiteNits = frame.SdrWhiteNits > 0f ? frame.SdrWhiteNits : HdrPixelConvert.SceneReferredWhiteNits;
            HdrLuminanceAccumulator accumulator = new HdrLuminanceAccumulator();
            accumulator.AddFromPacked(frame.Pixels, frame.Stride, frame.DxgiFormat, frame.Width, frame.Height, sdrWhiteNits);
            return accumulator.Build();
        }

        /// <summary>
        /// Encodes a raw DXGI region into an <see cref="HdrMasterImage"/> without tonemapping.
        /// Used by the WIC path, which produces no curve of its own.
        /// </summary>
        internal static unsafe void FillMaster(byte* srcBase, int srcStride, int format,
            int srcX, int srcY, int copyW, int copyH, int dstX, int dstY, float sdrWhiteNits, HdrMasterImage master)
        {
            int bpp = HdrPixelConvert.BytesPerPixel(format);
            for (int y = 0; y < copyH; y++)
            {
                byte* srcRow = srcBase + (long)(srcY + y) * srcStride + (long)srcX * bpp;
                for (int x = 0; x < copyW; x++)
                {
                    master.WriteFromDxgiPixel(dstX + x, dstY + y, format, srcRow + x * bpp, sdrWhiteNits);
                }
            }
        }

        internal static unsafe void FillMasterFromMapped(IntPtr data, int rowPitch, int format,
            int srcX, int srcY, int copyW, int copyH, int dstX, int dstY, float sdrWhiteNits, HdrMasterImage master)
        {
            FillMaster((byte*)data, rowPitch, format, srcX, srcY, copyW, copyH, dstX, dstY, sdrWhiteNits, master);
        }

        internal static unsafe void BlitMapped(IntPtr data, int rowPitch, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits, Bitmap composite, int dstX, int dstY, HdrTonemapCurve curve,
            HdrMasterImage master)
        {
            BlitRegion((byte*)data, rowPitch, format, srcX, srcY, copyW, copyH, sdrWhiteNits,
                composite, dstX, dstY, curve, master);
        }

        internal static unsafe void BlitRegion(byte* srcBase, int srcStride, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits, Bitmap composite, int dstX, int dstY, HdrTonemapCurve curve,
            HdrMasterImage master)
        {
            copyW = Math.Min(copyW, composite.Width - dstX);
            copyH = Math.Min(copyH, composite.Height - dstY);
            if (copyW <= 0 || copyH <= 0)
            {
                return;
            }

            int bpp = HdrPixelConvert.BytesPerPixel(format);
            BitmapData bd = composite.LockBits(new Rectangle(dstX, dstY, copyW, copyH),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                byte* dstBase = (byte*)bd.Scan0;
                int dstStride = bd.Stride;

                if (master != null)
                {
                    // Master MaxCLL/MaxFALL tracking is not thread-safe; rows stay sequential.
                    for (int y = 0; y < copyH; y++)
                    {
                        BlitRow(srcBase, srcStride, format, bpp, srcX, srcY, copyW, y, sdrWhiteNits,
                            dstBase, dstStride, dstX, dstY, curve, master);
                    }
                }
                else
                {
                    Parallel.For(0, copyH, y =>
                    {
                        BlitRow(srcBase, srcStride, format, bpp, srcX, srcY, copyW, y, sdrWhiteNits,
                            dstBase, dstStride, dstX, dstY, curve, null);
                    });
                }
            }
            finally
            {
                composite.UnlockBits(bd);
            }
        }

        private static unsafe void BlitRow(byte* srcBase, int srcStride, int format, int bpp,
            int srcX, int srcY, int copyW, int y, float sdrWhiteNits,
            byte* dstBase, int dstStride, int dstX, int dstY, HdrTonemapCurve curve, HdrMasterImage master)
        {
            byte* dst = dstBase + y * dstStride;
            byte* srcRow = srcBase + (long)(srcY + y) * srcStride + (long)srcX * bpp;
            int py = dstY + y;

            for (int x = 0; x < copyW; x++)
            {
                byte* px = srcRow + x * bpp;
                master?.WriteFromDxgiPixel(dstX + x, py, format, px, sdrWhiteNits);

                HdrPixelConvert.DecodeToSdrNormalized(format, px, sdrWhiteNits,
                    out float r, out float g, out float b);
                curve.Map(ref r, ref g, ref b);
                int pxCoord = dstX + x;
                dst[x * 4 + 0] = curve.Encode(b, pxCoord, py);
                dst[x * 4 + 1] = curve.Encode(g, pxCoord, py);
                dst[x * 4 + 2] = curve.Encode(r, pxCoord, py);
                dst[x * 4 + 3] = 255;
            }
        }
    }
}
