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
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Writes the untonemapped frame as an HDR PNG so the report can show the original next to the
    /// candidates.
    ///
    /// This reuses <see cref="HdrPngWriter"/>, the same writer behind the app's HDR master PNG
    /// feature, so the file carries cICP (9, 16, 0, 1) - BT.2020 primaries, PQ transfer, full range.
    /// Chrome renders that as genuine HDR on an HDR display. Two consequences worth knowing:
    ///
    /// - On an SDR display, or with Windows HDR off, the browser applies its own tonemap. That is
    ///   still a useful comparison (the browser's curve against yours), but it is not the original.
    /// - Because it is the shipping writer, a reference image that looks wrong means the app's HDR
    ///   PNG export is wrong too. The report exercises that path for free.
    /// </summary>
    public static class ReferenceImage
    {
        private const float PqM1 = 2610f / 16384f;
        private const float PqM2 = 2523f / 4096f * 128f;
        private const float PqC1 = 3424f / 4096f;
        private const float PqC2 = 2413f / 4096f * 32f;
        private const float PqC3 = 2392f / 4096f * 32f;

        /// <summary>
        /// Writes the HDR reference at full resolution, or divided by <paramref name="divisor"/>.
        /// 16-bit RGB runs to roughly 25 MB for an undivided 4K frame.
        /// </summary>
        public static void WriteFull(HdrMasterImage master, string path, int divisor = 1)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            if (divisor > 1)
            {
                master = Downscale(master, Math.Max(1, master.Width / divisor));
            }

            HdrPngWriter.Save(path, master);
        }

        /// <summary>Writes a downscaled HDR reference for the contact sheet.</summary>
        public static void WriteThumbnail(HdrMasterImage master, string path, int targetWidth)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            HdrMasterImage thumb = Downscale(master, targetWidth);
            HdrPngWriter.Save(path, thumb);
        }

        /// <summary>
        /// Box-downsamples a PQ master. Averaging happens in linear light: PQ is a heavily non-linear
        /// encoding, so averaging code values directly would darken every edge and quietly change the
        /// very highlights the reference exists to show.
        /// </summary>
        public static HdrMasterImage Downscale(HdrMasterImage source, int targetWidth)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            if (targetWidth >= source.Width || targetWidth <= 0)
            {
                return source;
            }

            int width = targetWidth;
            int height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));

            HdrMasterImage result = new HdrMasterImage(width, height)
            {
                MasteringDisplay = source.MasteringDisplay
            };

            int sourceWidth = source.Width;
            int sourceHeight = source.Height;
            ushort[] src = source.Rgb;
            ushort[] dst = result.Rgb;

            Parallel.For(0, height, y =>
            {
                int y0 = (int)((long)y * sourceHeight / height);
                int y1 = Math.Max(y0 + 1, (int)((long)(y + 1) * sourceHeight / height));

                for (int x = 0; x < width; x++)
                {
                    int x0 = (int)((long)x * sourceWidth / width);
                    int x1 = Math.Max(x0 + 1, (int)((long)(x + 1) * sourceWidth / width));

                    double r = 0, g = 0, b = 0;
                    int n = 0;

                    for (int sy = y0; sy < y1; sy++)
                    {
                        int row = sy * sourceWidth * 3;
                        for (int sx = x0; sx < x1; sx++)
                        {
                            int i = row + sx * 3;
                            r += PqDecode(src[i] / 65535f);
                            g += PqDecode(src[i + 1] / 65535f);
                            b += PqDecode(src[i + 2] / 65535f);
                            n++;
                        }
                    }

                    int di = (y * width + x) * 3;
                    dst[di] = PqEncode((float)(r / n));
                    dst[di + 1] = PqEncode((float)(g / n));
                    dst[di + 2] = PqEncode((float)(b / n));
                }
            });

            result.RecomputeLightLevels();
            return result;
        }

        /// <summary>
        /// Two mechanical SDR views of the same untonemapped frame, so the original is inspectable
        /// without an HDR display. Neither is a candidate curve, which is the point - each is a fixed
        /// transform with no free parameters, so it cannot flatter or penalise any mode.
        ///
        /// - <c>inrange</c>: hard clip at paper white. Shows SDR-range content exactly as captured,
        ///   with everything above paper white blown to white by construction. Answers "did the
        ///   capture preserve what was already in range?"
        /// - <c>fullrange</c>: linear scale so the frame's peak lands at 1.0. Uniformly darker than
        ///   intended, but nothing clips, so it shows what detail exists up in the highlights.
        ///   Answers "what was there to lose?"
        /// </summary>
        public static void WriteSdrViews(HdrLinearFrame linear, string inRangePath, string fullRangePath,
            int targetWidth)
        {
            if (linear == null)
            {
                throw new ArgumentNullException(nameof(linear));
            }

            float peak = 0f;
            for (int i = 0; i < linear.Rgb.Length; i++)
            {
                float v = linear.Rgb[i];
                if (v > peak && float.IsFinite(v))
                {
                    peak = v;
                }
            }

            float fullRangeScale = peak > 1f ? 1f / peak : 1f;

            WriteSdrView(linear, inRangePath, 1f, targetWidth);
            WriteSdrView(linear, fullRangePath, fullRangeScale, targetWidth);
        }

        private static void WriteSdrView(HdrLinearFrame linear, string path, float scale, int targetWidth)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));

            int width = linear.Width;
            int height = linear.Height;

            using Bitmap full = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            BitmapData bd = full.LockBits(new Rectangle(0, 0, width, height),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            try
            {
                unsafe
                {
                    byte* baseAddr = (byte*)bd.Scan0;
                    int stride = bd.Stride;
                    float[] rgb = linear.Rgb;

                    Parallel.For(0, height, y =>
                    {
                        byte* row = baseAddr + (long)y * stride;
                        int src = y * width * 3;

                        for (int x = 0; x < width; x++)
                        {
                            int i = src + x * 3;
                            row[x * 4 + 0] = SrgbByte(rgb[i + 2] * scale);
                            row[x * 4 + 1] = SrgbByte(rgb[i + 1] * scale);
                            row[x * 4 + 2] = SrgbByte(rgb[i] * scale);
                            row[x * 4 + 3] = 255;
                        }
                    });
                }
            }
            finally
            {
                full.UnlockBits(bd);
            }

            if (targetWidth > 0 && targetWidth < width)
            {
                int thumbHeight = Math.Max(1, (int)Math.Round(height * (targetWidth / (double)width)));
                using Bitmap thumb = new Bitmap(targetWidth, thumbHeight, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(thumb))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                    g.DrawImage(full, new Rectangle(0, 0, targetWidth, thumbHeight));
                }

                thumb.Save(path, ImageFormat.Png);
                return;
            }

            full.Save(path, ImageFormat.Png);
        }

        private static byte SrgbByte(float linear)
        {
            linear = Math.Clamp(linear, 0f, 1f);
            float encoded = linear <= 0.0031308f
                ? 12.92f * linear
                : 1.055f * MathF.Pow(linear, 1f / 2.4f) - 0.055f;
            return (byte)Math.Clamp((int)MathF.Round(encoded * 255f), 0, 255);
        }

        private static float PqDecode(float code)
        {
            float e = MathF.Pow(MathF.Max(code, 0f), 1f / PqM2);
            float num = MathF.Max(e - PqC1, 0f);
            float den = PqC2 - PqC3 * e;
            if (den <= 0f)
            {
                return 0f;
            }

            return 10000f * MathF.Pow(num / den, 1f / PqM1);
        }

        private static ushort PqEncode(float nits)
        {
            float y = Math.Clamp(nits / 10000f, 0f, 1f);
            float ym = MathF.Pow(y, PqM1);
            float code = MathF.Pow((PqC1 + PqC2 * ym) / (1f + PqC3 * ym), PqM2);
            return (ushort)Math.Clamp((int)MathF.Round(code * 65535f), 0, 65535);
        }
    }
}
