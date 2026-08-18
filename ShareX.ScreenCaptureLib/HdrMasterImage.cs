#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;
using System.Drawing;
using System.Drawing.Imaging;

namespace ShareX.ScreenCaptureLib
{
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
            int srcX = absoluteRegion.X - canvasAbsoluteBounds.X;
            int srcY = absoluteRegion.Y - canvasAbsoluteBounds.Y;
            int w = absoluteRegion.Width;
            int h = absoluteRegion.Height;

            if (srcX < 0 || srcY < 0 || srcX + w > Width || srcY + h > Height || w <= 0 || h <= 0)
            {
                return null;
            }

            HdrMasterImage cropped = new HdrMasterImage(w, h);
            for (int y = 0; y < h; y++)
            {
                int srcRow = ((srcY + y) * Width + srcX) * 3;
                int dstRow = y * w * 3;
                Array.Copy(Rgb, srcRow, cropped.Rgb, dstRow, w * 3);
            }

            cropped.MaxCLL = MaxCLL;
            cropped.SumNits = SumNits;
            cropped.SampleCount = SampleCount;
            return cropped;
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
