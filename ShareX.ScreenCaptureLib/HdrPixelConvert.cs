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
