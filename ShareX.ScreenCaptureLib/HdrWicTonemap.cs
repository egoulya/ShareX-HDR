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

using ShareX.HelpersLib;
using System;
using System.Drawing;
using System.Drawing.Imaging;
using Vortice.Mathematics;
using Vortice.WIC;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ShareX.ScreenCaptureLib
{
    internal static class HdrWicTonemap
    {
        private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
        private const int DXGI_FORMAT_R10G10B10A2_UNORM = 24;

        public static unsafe bool TryBlitToBitmap(IntPtr data, int rowPitch, int texW, int texH, int dxgiFormat,
            int srcX, int srcY, int copyW, int copyH, Bitmap composite, int dstX, int dstY)
        {
            Guid sourcePixelFormat;
            switch (dxgiFormat)
            {
                case DXGI_FORMAT_R16G16B16A16_FLOAT:
                    sourcePixelFormat = Vortice.WIC.PixelFormat.Format64bppRGBAHalf;
                    break;
                case DXGI_FORMAT_R10G10B10A2_UNORM:
                    sourcePixelFormat = Vortice.WIC.PixelFormat.Format32bppR10G10B10A2HDR10;
                    break;
                default:
                    return false;
            }

            if (data == IntPtr.Zero || texW <= 0 || texH <= 0 || copyW <= 0 || copyH <= 0)
            {
                return false;
            }

            try
            {
                uint sourceBufferSize = checked((uint)(rowPitch * texH));

                using IWICImagingFactory factory = new IWICImagingFactory();
                using IWICImagingFactory3 factory3 = factory.QueryInterface<IWICImagingFactory3>();
                using IWICBitmap sourceBitmap = factory.CreateBitmapFromMemory((uint)texW, (uint)texH, sourcePixelFormat,
                    (uint)rowPitch, sourceBufferSize, data.ToPointer());
                using IWICBitmapToneMapper toneMapper = factory3.CreateBitmapToneMapper();

                toneMapper.InitializeForSdrTarget(sourceBitmap, Vortice.WIC.PixelFormat.Format32bppBGRA,
                    BitmapToneMappingMode.ToneMappingMode_Default);

                BitmapData bitmapData = composite.LockBits(new Rectangle(dstX, dstY, copyW, copyH),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try
                {
                    int destinationStride = Math.Abs(bitmapData.Stride);
                    uint destinationBufferSize = checked((uint)(destinationStride * copyH));
                    toneMapper.CopyPixels(new RectI(srcX, srcY, copyW, copyH),
                        (uint)destinationStride, destinationBufferSize, bitmapData.Scan0);
                    return true;
                }
                finally
                {
                    composite.UnlockBits(bitmapData);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: Windows WIC tonemap failed.");
                return false;
            }
        }
    }
}
