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
using System.IO;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Writes synthetic HDR test patterns as a corpus.
    ///
    /// These are not a substitute for real captures - they cannot tell you how a curve handles an
    /// actual game frame. What they give you is <em>known-value targets</em>: a paper-white field
    /// must come back at 255, an sRGB palette must round-trip to its own bytes, a shallow gradient
    /// must not band. That turns "does the shoulder look right" into an actual residual, and it
    /// lets you smoke-test the runner before collecting anything.
    /// </summary>
    public static class CorpusSynth
    {
        private const int Width = 1024;
        private const int Height = 576;
        private const float DefaultSdrWhiteNits = 203f;

        private static readonly (byte r, byte g, byte b)[] Palette =
        {
            (0, 0, 0), (255, 255, 255), (128, 128, 128), (32, 32, 32),
            (192, 192, 192), (220, 40, 40), (40, 180, 70), (50, 90, 210),
            (240, 200, 80), (18, 18, 18), (7, 90, 90), (250, 250, 200),
            (1, 1, 1), (254, 128, 3), (77, 77, 77), (200, 12, 190)
        };

        public static int Write(string directory)
        {
            Directory.CreateDirectory(directory);

            List<HdrRawFrame> frames = new List<HdrRawFrame>
            {
                SrgbPalette(),
                PaperWhiteField(),
                ShallowGradient(),
                LuminanceRamp(),
                SaturatedHighlights(),
                AutoHdrLike(),
                Hdr10Palette()
            };

            foreach (HdrRawFrame frame in frames)
            {
                string path = Path.Combine(directory,
                    $"synth_{frame.Metadata.Scenario}_{frame.Metadata.Label}{HdrRawFrame.FileExtension}");
                HdrRawFrame.Write(path, frame);
                Console.WriteLine($"  {frame.Metadata.Scenario,-10} {frame.Metadata.Label,-22} " +
                    $"{frame.Width}x{frame.Height}  {Path.GetFileName(path)}");
            }

            HdrFrameCorpus.WriteManifest(directory);
            return frames.Count;
        }

        /// <summary>
        /// Vertical bars of known sRGB colours at exactly paper white. Ground truth: the Desktop
        /// curve must return these bytes unchanged.
        /// </summary>
        private static HdrRawFrame SrgbPalette()
        {
            return BuildScRgb("srgb-palette", HdrCorpusScenario.Desktop,
                "Known sRGB bars composited at SDR white. Desktop mode must round-trip these exactly.",
                (x, y, set) =>
                {
                    (byte r, byte g, byte b) = Palette[x * Palette.Length / Width];
                    set(SrgbToLinear(r), SrgbToLinear(g), SrgbToLinear(b));
                });
        }

        /// <summary>Flat paper white. Must land at 255 and must classify as Desktop, not Auto-HDR.</summary>
        private static HdrRawFrame PaperWhiteField()
        {
            return BuildScRgb("paper-white-field", HdrCorpusScenario.Desktop,
                "Full-frame SDR white. Exercises the flat-paper-white branch of Auto classification.",
                (x, y, set) => set(1f, 1f, 1f));
        }

        /// <summary>
        /// A gradient spanning only a few percent of the range. Any coarse quantization in the
        /// curve or a broken dither shows up here as banding.
        /// </summary>
        private static HdrRawFrame ShallowGradient()
        {
            return BuildScRgb("shallow-gradient", HdrCorpusScenario.Desktop,
                "0.18 to 0.26 linear across the frame. Stresses banding and the Bayer dither.",
                (x, y, set) =>
                {
                    float t = x / (float)(Width - 1);
                    float value = 0.18f + t * 0.08f;
                    set(value, value, value);
                });
        }

        /// <summary>Neutral ramp from black to 10x paper white: the highlight roll-off under test.</summary>
        private static HdrRawFrame LuminanceRamp()
        {
            return BuildScRgb("luminance-ramp", HdrCorpusScenario.NativeHdr,
                "Neutral ramp, black to 10x SDR white. The highlight shoulder under test.",
                (x, y, set) =>
                {
                    float t = x / (float)(Width - 1);
                    float value = t * t * 10f;
                    set(value, value, value);
                });
        }

        /// <summary>
        /// Saturated primaries and secondaries driven well above paper white. This is where the
        /// per-channel clamp in ApplyPqLut desaturates, so it is the frame that makes hue drift
        /// and chroma loss measurable.
        /// </summary>
        private static HdrRawFrame SaturatedHighlights()
        {
            (float r, float g, float b)[] hues =
            {
                (1f, 0.05f, 0.05f), (0.05f, 1f, 0.05f), (0.05f, 0.05f, 1f),
                (1f, 1f, 0.05f), (1f, 0.05f, 1f), (0.05f, 1f, 1f),
                (1f, 0.45f, 0.05f), (0.6f, 0.05f, 1f)
            };

            return BuildScRgb("saturated-highlights", HdrCorpusScenario.NativeHdr,
                "Saturated hues from 1x to 6x SDR white. Makes hue drift and chroma loss measurable.",
                (x, y, set) =>
                {
                    (float r, float g, float b) = hues[x * hues.Length / Width];
                    float gain = 1f + 5f * (y / (float)(Height - 1));
                    set(r * gain, g * gain, b * gain);
                });
        }

        /// <summary>
        /// SDR-looking content with highlights lifted just above paper white, which is the
        /// signature Auto-HDR produces and the case Auto classification has to separate from a
        /// true-HDR desktop.
        /// </summary>
        private static HdrRawFrame AutoHdrLike()
        {
            return BuildScRgb("autohdr-like", HdrCorpusScenario.AutoHdr,
                "Positive control for Auto classification: peak 1.20x, P99 below 1.35, ~30% of the " +
                "frame in the 0.75-1.15 hot band. Should resolve to AutoHDR, not Desktop.",
                (x, y, set) =>
                {
                    float tx = x / (float)(Width - 1);
                    float ty = y / (float)(Height - 1);

                    // Tuned against HdrTonemap.ClassifyContent so this frame is a genuine positive
                    // control. A peak of 1.20 keeps P99 under the 1.35 that would make
                    // looksLikeTrueHdr fire first and short-circuit to Desktop, while the base
                    // sweep puts roughly 30% of pixels in the hot upper-SDR band that
                    // LooksLikeAutoHdrGame keys on.
                    float baseValue = 0.35f + 0.60f * tx;
                    float value = ty > 0.90f ? 1.20f : baseValue;

                    // Gentle warm tint so it is not a pure neutral.
                    set(value * 1.03f, value, value * 0.96f);
                });
        }

        /// <summary>The same palette in HDR10 PQ, so the other decode path is covered too.</summary>
        private static HdrRawFrame Hdr10Palette()
        {
            const int bpp = 4;
            byte[] pixels = new byte[Width * Height * bpp];

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    (byte r, byte g, byte b) = Palette[x * Palette.Length / Width];
                    HdrPixelConvert.EncodeSrgb8ToPqBt2020(r, g, b, DefaultSdrWhiteNits,
                        out ushort rPq, out ushort gPq, out ushort bPq, out _);

                    // 16-bit PQ down to the 10 bits R10G10B10A2 actually carries.
                    uint packed = (uint)(rPq >> 6) | ((uint)(gPq >> 6) << 10) | ((uint)(bPq >> 6) << 20) | (3u << 30);
                    BitConverter.GetBytes(packed).CopyTo(pixels, (y * Width + x) * bpp);
                }
            }

            // Desktop, not NativeHdr: the content is known in-range sRGB, so it has the same exact
            // ground truth as the fp16 palette and should be held to the round-trip budget. Only the
            // container is HDR10.
            HdrFrameMetadata metadata = Metadata("hdr10-palette", HdrCorpusScenario.Desktop,
                "Known sRGB bars encoded as HDR10 PQ. Covers the R10G10B10A2 decode path against " +
                "the same exact ground truth as the fp16 palette.",
                HdrRawFrame.FormatHdr10, bpp, HdrDisplayProbe.ColorSpaceHdr10);

            return new HdrRawFrame(metadata, pixels);
        }

        // ====================================================================

        private delegate void SetPixel(float r, float g, float b);

        private static HdrRawFrame BuildScRgb(string label, HdrCorpusScenario scenario, string notes,
            Action<int, int, SetPixel> shade)
        {
            const int bpp = 8;
            byte[] pixels = new byte[Width * Height * bpp];
            float scale = DefaultSdrWhiteNits / HdrRawFrame.SceneReferredWhiteNits;

            for (int y = 0; y < Height; y++)
            {
                for (int x = 0; x < Width; x++)
                {
                    int offset = (y * Width + x) * bpp;
                    shade(x, y, (r, g, b) =>
                    {
                        WriteHalf(pixels, offset, r * scale);
                        WriteHalf(pixels, offset + 2, g * scale);
                        WriteHalf(pixels, offset + 4, b * scale);
                        WriteHalf(pixels, offset + 6, 1f);
                    });
                }
            }

            HdrFrameMetadata metadata = Metadata(label, scenario, notes,
                HdrRawFrame.FormatScRgbFp16, bpp, HdrDisplayProbe.ColorSpaceScRgb);

            return new HdrRawFrame(metadata, pixels);
        }

        private static HdrFrameMetadata Metadata(string label, HdrCorpusScenario scenario, string notes,
            int dxgiFormat, int bpp, uint colorSpace)
        {
            return new HdrFrameMetadata
            {
                Width = Width,
                Height = Height,
                Stride = Width * bpp,
                DxgiFormat = dxgiFormat,
                SdrWhiteNits = DefaultSdrWhiteNits,
                ColorSpace = colorSpace,
                Scenario = scenario,
                Label = label,
                Notes = "Synthetic test pattern. " + notes,
                DeviceName = "SYNTH",
                CapturedUtc = DateTime.UtcNow,
                PayloadBytes = Width * Height * bpp,
                MonitorRect = new HdrFrameRect(0, 0, Width, Height),
                CaptureRect = new HdrFrameRect(0, 0, Width, Height),
                SourceRegion = new HdrFrameRect(0, 0, Width, Height),
                MonitorMaxLuminanceNits = 1000f,
                MonitorMinLuminanceNits = 0.005f
            };
        }

        private static void WriteHalf(byte[] destination, int offset, float value)
        {
            ushort bits = BitConverter.HalfToUInt16Bits((Half)value);
            destination[offset] = (byte)(bits & 0xFF);
            destination[offset + 1] = (byte)(bits >> 8);
        }

        private static float SrgbToLinear(byte value)
        {
            float c = value / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }
}
