#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Decodes DXGI HDR pixels into linear RGB normalized so 1.0 = the output's SDR white.
    /// scRGB and HDR10 both emit that space so tonemap LUTs can be shared.
    /// </summary>
    internal static class HdrPixelConvert
    {
        public const int FormatR16G16B16A16Float = 10;
        public const int FormatR10G10B10A2Unorm = 24;
        public const float SceneReferredWhiteNits = 80f;

        public static float NormalizationScale(float sdrWhiteNits)
        {
            if (sdrWhiteNits < SceneReferredWhiteNits)
            {
                sdrWhiteNits = SceneReferredWhiteNits;
            }

            return SceneReferredWhiteNits / sdrWhiteNits;
        }

        public static unsafe void DecodeToSdrNormalized(int format, byte* pixel, float sdrWhiteNits,
            out float r, out float g, out float b)
        {
            if (sdrWhiteNits < SceneReferredWhiteNits)
            {
                sdrWhiteNits = SceneReferredWhiteNits;
            }

            if (format == FormatR16G16B16A16Float)
            {
                ushort* p = (ushort*)pixel;
                float scale = SceneReferredWhiteNits / sdrWhiteNits;
                r = Math.Max(HalfToFloat(p[0]) * scale, 0f);
                g = Math.Max(HalfToFloat(p[1]) * scale, 0f);
                b = Math.Max(HalfToFloat(p[2]) * scale, 0f);
                return;
            }

            if (format == FormatR10G10B10A2Unorm)
            {
                uint packed = *(uint*)pixel;
                float rN = PqEotf((packed & 0x3FFu) / 1023f);
                float gN = PqEotf(((packed >> 10) & 0x3FFu) / 1023f);
                float bN = PqEotf(((packed >> 20) & 0x3FFu) / 1023f);

                // BT.2020 nits -> BT.709 nits, then divide by this output's SDR white.
                r = Math.Max((1.6605f * rN - 0.5877f * gN - 0.0728f * bN) / sdrWhiteNits, 0f);
                g = Math.Max((-0.1246f * rN + 1.1330f * gN - 0.0084f * bN) / sdrWhiteNits, 0f);
                b = Math.Max((-0.0182f * rN - 0.1006f * gN + 1.1187f * bN) / sdrWhiteNits, 0f);
                return;
            }

            r = g = b = 0f;
        }

        public static int BytesPerPixel(int format)
        {
            return format == FormatR16G16B16A16Float ? 8 : 4;
        }

        /// <summary>
        /// Encode a DXGI HDR pixel to BT.2100 PQ 16-bit RGB. <paramref name="nits"/> is
        /// approximate luminance used for cLLI (MaxCLL / MaxFALL).
        /// </summary>
        public static unsafe void EncodeToPqBt2020(int format, byte* pixel, float sdrWhiteNits,
            out ushort rPq, out ushort gPq, out ushort bPq, out float nits)
        {
            if (sdrWhiteNits < SceneReferredWhiteNits)
            {
                sdrWhiteNits = SceneReferredWhiteNits;
            }

            float r2020, g2020, b2020;

            if (format == FormatR10G10B10A2Unorm)
            {
                uint packed = *(uint*)pixel;
                float rN = (packed & 0x3FFu) / 1023f;
                float gN = ((packed >> 10) & 0x3FFu) / 1023f;
                float bN = ((packed >> 20) & 0x3FFu) / 1023f;
                float rn = PqEotf(rN);
                float gn = PqEotf(gN);
                float bn = PqEotf(bN);
                rPq = QuantizePq(rn);
                gPq = QuantizePq(gn);
                bPq = QuantizePq(bn);
                nits = 0.2627f * rn + 0.6780f * gn + 0.0593f * bn;
                return;
            }

            if (format == FormatR16G16B16A16Float)
            {
                ushort* p = (ushort*)pixel;
                // scRGB half: 1.0 ≈ 80 nits scene-referred in BT.709 primaries.
                float r709 = Math.Max(HalfToFloat(p[0]) * SceneReferredWhiteNits, 0f);
                float g709 = Math.Max(HalfToFloat(p[1]) * SceneReferredWhiteNits, 0f);
                float b709 = Math.Max(HalfToFloat(p[2]) * SceneReferredWhiteNits, 0f);
                Bt709NitsToBt2020(r709, g709, b709, out r2020, out g2020, out b2020);
            }
            else
            {
                r2020 = g2020 = b2020 = 0f;
            }

            nits = 0.2627f * r2020 + 0.6780f * g2020 + 0.0593f * b2020;
            rPq = QuantizePq(r2020);
            gPq = QuantizePq(g2020);
            bPq = QuantizePq(b2020);
        }

        public static void EncodeSrgb8ToPqBt2020(byte r8, byte g8, byte b8, float sdrWhiteNits,
            out ushort rPq, out ushort gPq, out ushort bPq, out float nits)
        {
            if (sdrWhiteNits < SceneReferredWhiteNits)
            {
                sdrWhiteNits = SceneReferredWhiteNits;
            }

            float r709 = SrgbToLinear(r8) * sdrWhiteNits;
            float g709 = SrgbToLinear(g8) * sdrWhiteNits;
            float b709 = SrgbToLinear(b8) * sdrWhiteNits;
            Bt709NitsToBt2020(r709, g709, b709, out float r2020, out float g2020, out float b2020);
            nits = 0.2627f * r2020 + 0.6780f * g2020 + 0.0593f * b2020;
            rPq = QuantizePq(r2020);
            gPq = QuantizePq(g2020);
            bPq = QuantizePq(b2020);
        }

        private static void Bt709NitsToBt2020(float r709, float g709, float b709,
            out float r2020, out float g2020, out float b2020)
        {
            r2020 = Math.Max(0.6274f * r709 + 0.3293f * g709 + 0.0433f * b709, 0f);
            g2020 = Math.Max(0.0691f * r709 + 0.9195f * g709 + 0.0114f * b709, 0f);
            b2020 = Math.Max(0.0164f * r709 + 0.0880f * g709 + 0.8956f * b709, 0f);
        }

        private static ushort QuantizePq(float nits)
        {
            float N = PqOetf(nits);
            return (ushort)Math.Clamp((int)MathF.Round(N * 65535f), 0, 65535);
        }

        private static float PqOetf(float nits)
        {
            const float m1 = 0.1593017578125f;
            const float m2 = 78.84375f;
            const float c1 = 0.8359375f;
            const float c2 = 18.8515625f;
            const float c3 = 18.6875f;

            float Y = Math.Clamp(nits / 10000f, 0f, 1f);
            float Ym = MathF.Pow(Y, m1);
            float num = c1 + c2 * Ym;
            float den = 1f + c3 * Ym;
            return MathF.Pow(num / den, m2);
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

        public static float Pq16LuminanceNits(ushort r, ushort g, ushort b)
        {
            float rn = PqEotf(r / 65535f);
            float gn = PqEotf(g / 65535f);
            float bn = PqEotf(b / 65535f);
            return 0.2627f * rn + 0.6780f * gn + 0.0593f * bn;
        }

        private static float PqEotf(float N)
        {
            const float m1 = 0.1593017578125f;
            const float m2 = 78.84375f;
            const float c1 = 0.8359375f;
            const float c2 = 18.8515625f;
            const float c3 = 18.6875f;

            float Np = MathF.Pow(Math.Max(N, 0f), 1f / m2);
            float num = Math.Max(Np - c1, 0f);
            float den = c2 - c3 * Np;
            if (den <= 0f) return 0f;
            return MathF.Pow(num / den, 1f / m1) * 10000f;
        }

        private static unsafe float HalfToFloat(ushort h)
        {
            return (float)BitConverter.UInt16BitsToHalf(h);
        }
    }
}
