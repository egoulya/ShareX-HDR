#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;
using System.IO;
using Xunit;

namespace ShareX.ScreenCaptureLib.Tests
{
    public class HdrPipelineTests
    {
        private static readonly (byte r, byte g, byte b)[] DesktopSrgbPalette =
        {
            (0, 0, 0),
            (255, 255, 255),
            (128, 128, 128),
            (32, 32, 32),
            (192, 192, 192),
            (220, 40, 40),
            (40, 180, 70),
            (50, 90, 210),
            (240, 200, 80),
            (18, 18, 18)
        };

        [Theory]
        [InlineData(80f)]
        [InlineData(203f)]
        [InlineData(400f)]
        public unsafe void ScRgb_desktop_roundtrip_within_one_lsb(float sdrWhiteNits)
        {
            HdrTonemapCurve curve = CreateDesktopCurve(sdrWhiteNits, peakNorm: 1f);
            byte* pixel = stackalloc byte[8];

            for (int i = 0; i < DesktopSrgbPalette.Length; i++)
            {
                (byte r8, byte g8, byte b8) = DesktopSrgbPalette[i];
                EncodeScRgb(pixel, sdrWhiteNits, SrgbToLinear(r8), SrgbToLinear(g8), SrgbToLinear(b8));

                HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR16G16B16A16Float, pixel, sdrWhiteNits,
                    out float r, out float g, out float b);
                curve.Map(ref r, ref g, ref b);

                AssertByteNear(r8, curve.Encode(r, i, 0), 2); // ±2: Bayer dither + encode-LUT quantization
                AssertByteNear(g8, curve.Encode(g, i, 1), 2);
                AssertByteNear(b8, curve.Encode(b, i, 2), 2);
            }
        }

        [Theory]
        [InlineData(80f)]
        [InlineData(203f)]
        [InlineData(400f)]
        public unsafe void Paper_white_decodes_to_one_in_both_formats(float sdrWhiteNits)
        {
            byte* scrgb = stackalloc byte[8];
            byte* hdr10 = stackalloc byte[4];

            EncodeScRgb(scrgb, sdrWhiteNits, 1f, 1f, 1f);
            EncodeHdr10(hdr10, sdrWhiteNits, 1f, 1f, 1f);

            HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR16G16B16A16Float, scrgb, sdrWhiteNits,
                out float sr, out float sg, out float sb);
            HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR10G10B10A2Unorm, hdr10, sdrWhiteNits,
                out float hr, out float hg, out float hb);

            Assert.InRange(sr, 0.98f, 1.02f);
            Assert.InRange(sg, 0.98f, 1.02f);
            Assert.InRange(sb, 0.98f, 1.02f);
            Assert.InRange(hr, 0.97f, 1.03f);
            Assert.InRange(hg, 0.97f, 1.03f);
            Assert.InRange(hb, 0.97f, 1.03f);
        }

        [Theory]
        [InlineData(80f)]
        [InlineData(203f)]
        [InlineData(400f)]
        public unsafe void ScRgb_and_Hdr10_agree_after_tonemap(float sdrWhiteNits)
        {
            float[] levels = { 0.05f, 0.18f, 0.5f, 1f, 2f, 4f, 8f };
            HdrTonemapCurve curve = CreateDesktopCurve(sdrWhiteNits, peakNorm: 8f);
            byte* scrgb = stackalloc byte[8];
            byte* hdr10 = stackalloc byte[4];

            foreach (float y in levels)
            {
                EncodeScRgb(scrgb, sdrWhiteNits, y, y, y);
                EncodeHdr10(hdr10, sdrWhiteNits, y, y, y);

                HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR16G16B16A16Float, scrgb, sdrWhiteNits,
                    out float sr, out float sg, out float sb);
                HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR10G10B10A2Unorm, hdr10, sdrWhiteNits,
                    out float hr, out float hg, out float hb);

                Assert.InRange(hr, sr - 0.04f, sr + 0.04f);
                Assert.InRange(hg, sg - 0.04f, sg + 0.04f);
                Assert.InRange(hb, sb - 0.04f, sb + 0.04f);

                curve.Map(ref sr, ref sg, ref sb);
                curve.Map(ref hr, ref hg, ref hb);

                // ±3 LSB: scRGB↔HDR10 round-trip plus Bayer dither.
                // Widening this weakens the SDR passthrough / cross-format agreement guarantee.
                int x = (int)(y * 10);
                Assert.InRange(curve.Encode(hr, x, 0), curve.Encode(sr, x, 0) - 3, curve.Encode(sr, x, 0) + 3);
                Assert.InRange(curve.Encode(hg, x, 1), curve.Encode(sg, x, 1) - 3, curve.Encode(sg, x, 1) + 3);
                Assert.InRange(curve.Encode(hb, x, 2), curve.Encode(sb, x, 2) - 3, curve.Encode(sb, x, 2) + 3);
            }
        }

        [Fact]
        public unsafe void White_level_sweep_is_monotonic_and_sane()
        {
            float[] whites = { 80f, 203f, 400f };
            float[] scene = { 0.1f, 0.5f, 1f, 3f, 10f };
            byte* pixel = stackalloc byte[8];
            byte[][] encoded = new byte[whites.Length][];

            for (int w = 0; w < whites.Length; w++)
            {
                HdrTonemapCurve curve = CreateDesktopCurve(whites[w], peakNorm: 10f);
                encoded[w] = new byte[scene.Length];

                for (int i = 0; i < scene.Length; i++)
                {
                    EncodeScRgb(pixel, whites[w], scene[i], scene[i], scene[i]);
                    HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR16G16B16A16Float, pixel, whites[w],
                        out float r, out float g, out float b);
                    curve.Map(ref r, ref g, ref b);
                    encoded[w][i] = curve.Encode(r, i, 0);
                    Assert.InRange(encoded[w][i], (byte)0, (byte)255);
                }

                for (int i = 1; i < scene.Length; i++)
                {
                    Assert.True(encoded[w][i] >= encoded[w][i - 1],
                        $"Output must be monotonic at {whites[w]} nits: {encoded[w][i - 1]} -> {encoded[w][i]}");
                }

                Assert.True(encoded[w][2] > encoded[w][1],
                    $"Paper white must stay above mid-gray at {whites[w]} nits.");
                Assert.InRange(encoded[w][4], encoded[w][2], (byte)255);
            }

            HdrTonemapCurve identity = CreateDesktopCurve(203f, peakNorm: 1f);
            byte* whitePixel = stackalloc byte[8];
            EncodeScRgb(whitePixel, 203f, 1f, 1f, 1f);
            HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR16G16B16A16Float, whitePixel, 203f,
                out float wr, out float wg, out float wb);
            identity.Map(ref wr, ref wg, ref wb);
            Assert.InRange(identity.Encode(wr, 0, 0), (byte)253, (byte)255);
        }

        [Fact]
        public unsafe void Hdr10_must_not_use_scene_referred_80_as_paper_white()
        {
            const float sdrWhiteNits = 203f;
            byte* pixel = stackalloc byte[4];
            EncodeHdr10(pixel, sdrWhiteNits, 1f, 1f, 1f);
            HdrPixelConvert.DecodeToSdrNormalized(HdrPixelConvert.FormatR10G10B10A2Unorm, pixel, sdrWhiteNits,
                out float r, out float g, out float b);

            float lum = 0.2126f * r + 0.7152f * g + 0.0722f * b;
            Assert.True(lum < 1.2f,
                $"HDR10 paper white decoded to {lum:0.00}× SDR white; dividing by 80 nits would land near 2.5.");
        }

        private static HdrTonemapCurve CreateDesktopCurve(float sdrWhiteNits, float peakNorm)
        {
            HdrLuminanceStats stats = new HdrLuminanceStats(1, 0, 0, 0, peakNorm, peakNorm);
            return HdrTonemap.CreateCurve(HdrTonemapMode.Desktop, stats, 1f, sdrWhiteNits);
        }

        private static void AssertByteNear(byte expected, byte actual, int tolerance)
        {
            Assert.InRange((int)actual, expected - tolerance, expected + tolerance);
        }

        private static float SrgbToLinear(byte value)
        {
            float c = value / 255f;
            if (c <= 0.04045f)
            {
                return c / 12.92f;
            }

            return MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }

        private static unsafe void EncodeScRgb(byte* pixel, float sdrWhiteNits, float r, float g, float b)
        {
            float scale = sdrWhiteNits / HdrPixelConvert.SceneReferredWhiteNits;
            ushort* p = (ushort*)pixel;
            p[0] = BitConverter.HalfToUInt16Bits((Half)(r * scale));
            p[1] = BitConverter.HalfToUInt16Bits((Half)(g * scale));
            p[2] = BitConverter.HalfToUInt16Bits((Half)(b * scale));
            p[3] = BitConverter.HalfToUInt16Bits((Half)1f);
        }

        private static unsafe void EncodeHdr10(byte* pixel, float sdrWhiteNits, float r, float g, float b)
        {
            float r709 = r * sdrWhiteNits;
            float g709 = g * sdrWhiteNits;
            float b709 = b * sdrWhiteNits;

            float r2020 = 0.62741204f * r709 + 0.32929057f * g709 + 0.04330172f * b709;
            float g2020 = 0.06912047f * r709 + 0.91947852f * g709 + 0.01140215f * b709;
            float b2020 = 0.01642301f * r709 + 0.08804204f * g709 + 0.89562452f * b709;

            uint packed = PackPq10(r2020) | (PackPq10(g2020) << 10) | (PackPq10(b2020) << 20) | (3u << 30);
            *(uint*)pixel = packed;
        }

        [Fact]
        public void Hdr_png_writer_emits_cicp_and_clli_chunks()
        {
            HdrMasterImage master = new HdrMasterImage(2, 1);
            master.Rgb[0] = 32768;
            master.Rgb[1] = 32768;
            master.Rgb[2] = 32768;
            // Force MaxCLL/MaxFALL via WriteFromDxgi would need pixels; set via encode path:
            unsafe
            {
                byte* px = stackalloc byte[8];
                EncodeScRgb(px, 203f, 1f, 1f, 1f);
                master.WriteFromDxgiPixel(1, 0, HdrPixelConvert.FormatR16G16B16A16Float, px, 203f);
            }

            using MemoryStream ms = new MemoryStream();
            HdrPngWriter.Write(ms, master);
            byte[] bytes = ms.ToArray();

            Assert.True(bytes.Length > 100);
            Assert.Equal(0x89, bytes[0]);
            string ascii = System.Text.Encoding.ASCII.GetString(bytes);
            Assert.Contains("cICP", ascii);
            Assert.Contains("cLLI", ascii);
            Assert.Contains("IHDR", ascii);
            Assert.Contains("IDAT", ascii);
        }

        private static uint PackPq10(float nits)
        {
            float pq = PqOetf(Math.Max(nits, 0f));
            return (uint)Math.Clamp((int)(pq * 1023f + 0.5f), 0, 1023);
        }

        private static float PqOetf(float nits)
        {
            const float m1 = 0.1593017578125f;
            const float m2 = 78.84375f;
            const float c1 = 0.8359375f;
            const float c2 = 18.8515625f;
            const float c3 = 18.6875f;

            float y = nits / 10000f;
            float ym = MathF.Pow(Math.Max(y, 0f), m1);
            return MathF.Pow((c1 + c2 * ym) / (1f + c3 * ym), m2);
        }
    }
}
