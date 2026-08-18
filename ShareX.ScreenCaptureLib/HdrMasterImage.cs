#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib
{
    public struct HdrMasteringDisplay
    {
        public float RedX, RedY, GreenX, GreenY, BlueX, BlueY, WhiteX, WhiteY;
        public float MinLuminanceNits, MaxLuminanceNits;
        public bool HasValue;
    }

    /// <summary>
    /// Pre-tonemap BT.2100 PQ RGB (16-bit) companion for an HDR capture.
    /// </summary>
    public sealed class HdrMasterImage
    {
        public int Width { get; }
        public int Height { get; }

        /// <summary>Interleaved R,G,B PQ code values, 0..65535.</summary>
        public ushort[] Rgb { get; }

        public float MaxCLL { get; private set; }
        public double SumNits { get; private set; }
        public long SampleCount { get; private set; }
        public HdrMasteringDisplay MasteringDisplay { get; set; }

        public float MaxFALL => SampleCount > 0 ? (float)(SumNits / SampleCount) : 0f;

        public HdrMasterImage(int width, int height)
        {
            Width = width;
            Height = height;
            Rgb = GC.AllocateUninitializedArray<ushort>(checked(width * height * 3));
            Array.Clear(Rgb, 0, Rgb.Length);
        }

        public HdrMasterImage Crop(Rectangle absoluteRegion, Rectangle canvasAbsoluteBounds)
        {
            if (Width <= 0 || Height <= 0 || canvasAbsoluteBounds.Width <= 0 || canvasAbsoluteBounds.Height <= 0 ||
                absoluteRegion.Width <= 0 || absoluteRegion.Height <= 0)
            {
                DebugHelper.WriteLine(
                    $"HDR master: crop {absoluteRegion} is empty against canvas {canvasAbsoluteBounds} ({Width}x{Height}); dropping companion.");
                return null;
            }

            double scaleX = (double)Width / canvasAbsoluteBounds.Width;
            double scaleY = (double)Height / canvasAbsoluteBounds.Height;
            int rawX = absoluteRegion.X - canvasAbsoluteBounds.X;
            int rawY = absoluteRegion.Y - canvasAbsoluteBounds.Y;

            int srcX = (int)Math.Floor(rawX * scaleX);
            int srcY = (int)Math.Floor(rawY * scaleY);
            int srcRight = (int)Math.Ceiling((rawX + absoluteRegion.Width) * scaleX);
            int srcBottom = (int)Math.Ceiling((rawY + absoluteRegion.Height) * scaleY);

            srcX = Math.Clamp(srcX, 0, Width);
            srcY = Math.Clamp(srcY, 0, Height);
            srcRight = Math.Clamp(srcRight, srcX, Width);
            srcBottom = Math.Clamp(srcBottom, srcY, Height);

            int w = srcRight - srcX;
            int h = srcBottom - srcY;
            if (w <= 0 || h <= 0)
            {
                DebugHelper.WriteLine(
                    $"HDR master: crop {absoluteRegion} is outside canvas {canvasAbsoluteBounds} ({Width}x{Height}); dropping companion.");
                return null;
            }

            if (srcX != rawX || srcY != rawY || w != absoluteRegion.Width || h != absoluteRegion.Height ||
                Math.Abs(scaleX - 1.0) > 0.001 || Math.Abs(scaleY - 1.0) > 0.001)
            {
                DebugHelper.WriteLine(
                    $"HDR master: crop clamped from {absoluteRegion} on {canvasAbsoluteBounds} to {srcX},{srcY} {w}x{h} of {Width}x{Height}.");
            }

            HdrMasterImage cropped = new HdrMasterImage(w, h)
            {
                MasteringDisplay = MasteringDisplay
            };

            for (int y = 0; y < h; y++)
            {
                int srcRow = ((srcY + y) * Width + srcX) * 3;
                int dstRow = y * w * 3;
                Array.Copy(Rgb, srcRow, cropped.Rgb, dstRow, w * 3);
            }

            cropped.RecomputeLightLevels();
            return cropped;
        }

        public void RecomputeLightLevels()
        {
            MaxCLL = 0f;
            SumNits = 0;
            SampleCount = 0;

            ushort[] rgb = Rgb;
            for (int i = 0; i < rgb.Length; i += 3)
            {
                TrackNits(HdrPixelConvert.Pq16LuminanceNits(rgb[i], rgb[i + 1], rgb[i + 2]));
            }
        }

        public unsafe void WriteFromDxgiPixel(int dstX, int dstY, int format, byte* pixel, float sdrWhiteNits)
        {
            if ((uint)dstX >= (uint)Width || (uint)dstY >= (uint)Height)
            {
                return;
            }

            HdrPixelConvert.EncodeToPqBt2020(format, pixel, sdrWhiteNits, out ushort r, out ushort g, out ushort b, out float nits);
            int i = (dstY * Width + dstX) * 3;
            Rgb[i] = r;
            Rgb[i + 1] = g;
            Rgb[i + 2] = b;
            TrackNits(nits);
        }

        public unsafe void WriteFromSdrBitmap(Bitmap sdr, int dstX, int dstY, float sdrWhiteNits)
        {
            if (sdr == null)
            {
                return;
            }

            int copyW = Math.Min(sdr.Width, Width - dstX);
            int copyH = Math.Min(sdr.Height, Height - dstY);
            if (copyW <= 0 || copyH <= 0)
            {
                return;
            }

            BitmapData bd = sdr.LockBits(new Rectangle(0, 0, copyW, copyH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* srcBase = (byte*)bd.Scan0;
                for (int y = 0; y < copyH; y++)
                {
                    byte* row = srcBase + y * bd.Stride;
                    for (int x = 0; x < copyW; x++)
                    {
                        byte b8 = row[x * 4 + 0];
                        byte g8 = row[x * 4 + 1];
                        byte r8 = row[x * 4 + 2];
                        HdrPixelConvert.EncodeSrgb8ToPqBt2020(r8, g8, b8, sdrWhiteNits,
                            out ushort r, out ushort g, out ushort b, out float nits);
                        int i = ((dstY + y) * Width + (dstX + x)) * 3;
                        Rgb[i] = r;
                        Rgb[i + 1] = g;
                        Rgb[i + 2] = b;
                        TrackNits(nits);
                    }
                }
            }
            finally
            {
                sdr.UnlockBits(bd);
            }
        }

        private void TrackNits(float nits)
        {
            if (nits > MaxCLL)
            {
                MaxCLL = nits;
            }

            SumNits += nits;
            SampleCount++;
        }
    }
}
