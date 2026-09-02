#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using SharpGen.Runtime;
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    internal static class HdrRecordingBlit
    {
        public static unsafe void CopyBgraToBgr24(IntPtr data, int rowPitch, int srcX, int srcY, int copyW, int copyH, byte[] bgr24, int dstStrideWidth)
        {
            int rowBytesOut = dstStrideWidth * 3;

            for (int y = 0; y < copyH; y++)
            {
                byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * 4;
                int dstRow = y * rowBytesOut;

                for (int x = 0; x < copyW; x++)
                {
                    int si = x * 4;
                    int di = dstRow + x * 3;
                    bgr24[di] = src[si];
                    bgr24[di + 1] = src[si + 1];
                    bgr24[di + 2] = src[si + 2];
                }
            }
        }
    }

    public partial class Screenshot
    {
        public HdrRecordingCapture BeginHdrRecordingCapture(Rectangle captureRect)
        {
            ReleaseHdrDuplication();
            return new HdrRecordingCapture(captureRect, HdrTonemapMode, HdrExposure);
        }

        /// <summary>
        /// Keeps DXGI duplication and staging resources alive across recording frames.
        /// </summary>
        public sealed class HdrRecordingCapture : IDisposable
        {
            private readonly Rectangle captureRect;
            private readonly float sdrWhiteNits;
            private readonly string outputDeviceName;
            private readonly HdrTonemapMode requestedTonemapMode;
            private HdrTonemapMode resolvedTonemapMode;
            private HdrTonemapCurve tonemapCurve;
            private readonly float exposure;
            private bool tonemapResolved;
            private readonly int srcX;
            private readonly int srcY;
            private readonly int copyW;
            private readonly int copyH;
            private readonly int texFormat;

            private const int TonemapWarmupFrames = 15;
            private int tonemapWarmupCount;
            private long warmupSampleCount, warmupAboveOne, warmupAboveOneHalf, warmupHotUpperSdr;
            private float warmupMaxLum;
            private readonly int[] warmupHist = new int[HdrTonemap.HistogramSize];

            private IntPtr devicePtr;
            private IntPtr contextPtr;
            private Vortice.DXGI.IDXGIOutputDuplication duplication;
            private IntPtr stagingPtr;
            private bool initialized;
            private bool hasValidFrame;

            private Del_GetTexDesc getTexDesc;
            private Del_CreateTex2D createTex2D;
            private Del_CopyResource copyResource;
            private Del_Map mapSubresource;
            private Del_Unmap unmapSubresource;

            public int FrameBytes { get; }

            public HdrRecordingCapture(Rectangle captureRect, HdrTonemapMode tonemapMode = HdrTonemapMode.Auto,
                float exposure = HdrTonemap.ExposureDefault)
            {
                this.captureRect = captureRect;
                requestedTonemapMode = tonemapMode;
                this.exposure = HdrTonemap.ClampExposure(exposure);
                resolvedTonemapMode = requestedTonemapMode == HdrTonemapMode.WindowsWIC || requestedTonemapMode == HdrTonemapMode.Auto
                    ? HdrTonemapMode.Desktop
                    : requestedTonemapMode;
                FrameBytes = captureRect.Width * captureRect.Height * 3;

                if (!TryInitialize(out Rectangle monitorRect, out Rectangle intersection, out int format,
                    out float whiteNits, out string deviceName))
                {
                    throw new InvalidOperationException("HDR recording capture failed to initialize DXGI duplication.");
                }

                sdrWhiteNits = whiteNits;
                outputDeviceName = deviceName;
                tonemapCurve = HdrTonemap.CreateCurve(HdrTonemapMode.Desktop,
                    new HdrLuminanceStats(1, 0, 0, 0, 1f, 1f), this.exposure, sdrWhiteNits);
                texFormat = format;
                srcX = intersection.X - monitorRect.X;
                srcY = intersection.Y - monitorRect.Y;
                copyW = Math.Min(intersection.Width, captureRect.Width);
                copyH = Math.Min(intersection.Height, captureRect.Height);
                initialized = true;

                DebugHelper.WriteLine($"HDR recording: session {captureRect.Width}x{captureRect.Height}, format={texFormat}, sdrWhite={sdrWhiteNits:0.#} nits, tonemap={requestedTonemapMode}");
            }

            public bool TryCaptureFrame(byte[] bgr24)
            {
                if (!initialized || bgr24 == null || bgr24.Length < FrameBytes)
                {
                    return false;
                }

                if (TryAcquireAndConvert(bgr24))
                {
                    hasValidFrame = true;
                    return true;
                }

                return hasValidFrame;
            }

            public void Dispose()
            {
                if (duplication != null)
                {
                    try { duplication.ReleaseFrame(); } catch { }
                    duplication.Dispose();
                    duplication = null;
                }

                if (stagingPtr != IntPtr.Zero)
                {
                    Marshal.Release(stagingPtr);
                    stagingPtr = IntPtr.Zero;
                }

                devicePtr = IntPtr.Zero;
                contextPtr = IntPtr.Zero;
            }

            private bool TryInitialize(out Rectangle monitorRect, out Rectangle intersection, out int format,
                out float whiteNits, out string deviceName)
            {
                monitorRect = Rectangle.Empty;
                intersection = Rectangle.Empty;
                format = DXGI_FORMAT_B8G8R8A8_UNORM;
                whiteNits = HdrPixelConvert.SceneReferredWhiteNits;
                deviceName = null;

                if (!TryEnsureSharedVorticeDevice(out ID3D11Device vorticeDevice, out ID3D11DeviceContext vorticeContext))
                {
                    return false;
                }

                devicePtr = vorticeDevice.NativePointer;
                contextPtr = vorticeContext.NativePointer;

                string bestDeviceName = null;
                int bestArea = 0;

                using (IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
                {
                    for (uint adapterIndex = 0; ; adapterIndex++)
                    {
                        if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Failure)
                        {
                            break;
                        }

                        using (adapter)
                        {
                            for (uint outputIdx = 0; ; outputIdx++)
                            {
                                if (adapter.EnumOutputs(outputIdx, out Vortice.DXGI.IDXGIOutput output).Failure)
                                {
                                    break;
                                }

                                using (output)
                                {
                                    OutputDescription outputDesc = output.Description;
                                    Rectangle outputRect = Rectangle.FromLTRB(
                                        outputDesc.DesktopCoordinates.Left,
                                        outputDesc.DesktopCoordinates.Top,
                                        outputDesc.DesktopCoordinates.Right,
                                        outputDesc.DesktopCoordinates.Bottom);

                                    Rectangle currentIntersection = Rectangle.Intersect(captureRect, outputRect);
                                    int area = currentIntersection.Width * currentIntersection.Height;
                                    if (area > bestArea)
                                    {
                                        bestArea = area;
                                        monitorRect = outputRect;
                                        intersection = currentIntersection;
                                        bestDeviceName = outputDesc.DeviceName;
                                        whiteNits = DisplayConfigHelper.GetSdrWhiteNits(outputDesc.DeviceName);
                                    }
                                }
                            }
                        }
                    }
                }

                if (string.IsNullOrEmpty(bestDeviceName) || intersection.Width <= 0 || intersection.Height <= 0)
                {
                    return false;
                }

                deviceName = bestDeviceName;

                using (IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>())
                {
                    for (uint adapterIndex = 0; ; adapterIndex++)
                    {
                        if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Failure)
                        {
                            break;
                        }

                        using (adapter)
                        {
                            for (uint outputIdx = 0; ; outputIdx++)
                            {
                                if (adapter.EnumOutputs(outputIdx, out Vortice.DXGI.IDXGIOutput output).Failure)
                                {
                                    break;
                                }

                                using (output)
                                {
                                    OutputDescription outputDesc = output.Description;
                                    if (!string.Equals(outputDesc.DeviceName, bestDeviceName, StringComparison.Ordinal))
                                    {
                                        continue;
                                    }

                                    if (!TryCreateDuplication(output, vorticeDevice, out duplication, out format))
                                    {
                                        return false;
                                    }

                                    createTex2D = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5));
                                    copyResource = Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47));
                                    mapSubresource = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14));
                                    unmapSubresource = Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15));
                                    return true;
                                }
                            }
                        }
                    }
                }

                return false;
            }

            private bool TryAcquireAndConvert(byte[] bgr24)
            {
                IDXGIResource desktopResource = null;
                int maxAttempts = hasValidFrame ? 1 : 8;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    uint timeoutMs = hasValidFrame ? 0 : (uint)(attempt == 0 ? 50 : 8);
                    Result acquireResult = duplication.AcquireNextFrame(timeoutMs, out OutduplFrameInfo frameInfo, out desktopResource);

                    if (acquireResult.Success)
                    {
                        // After the duplication is populated, LastPresentTime == 0 means
                        // "nothing changed", not "blank surface".
                        if (!hasValidFrame && frameInfo.LastPresentTime == 0 && attempt < 3)
                        {
                            desktopResource?.Dispose();
                            desktopResource = null;
                            duplication.ReleaseFrame();
                            Thread.Sleep(5);
                            continue;
                        }

                        break;
                    }

                    if ((int)acquireResult.Code == DXGI_ERROR_ACCESS_LOST)
                    {
                        return false;
                    }

                    if ((int)acquireResult.Code == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        return false;
                    }

                    Thread.Sleep(1);
                }

                if (desktopResource == null)
                {
                    return false;
                }

                try
                {
                    IntPtr resourcePtr = desktopResource.NativePointer;
                    Guid texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                    Marshal.QueryInterface(resourcePtr, in texGuid, out IntPtr texPtr);

                    try
                    {
                        if (getTexDesc == null)
                        {
                            getTexDesc = Marshal.GetDelegateForFunctionPointer<Del_GetTexDesc>(VT(texPtr, 10));
                        }

                        getTexDesc(texPtr, out D3D11_TEXTURE2D_DESC td);

                        if (stagingPtr == IntPtr.Zero)
                        {
                            td.Usage = D3D11_USAGE_STAGING;
                            td.BindFlags = 0;
                            td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                            td.MiscFlags = 0;
                            int createHr = createTex2D(devicePtr, ref td, IntPtr.Zero, out stagingPtr);
                            if (createHr != 0)
                            {
                                return false;
                            }
                        }

                        copyResource(contextPtr, stagingPtr, texPtr);

                        int mapHr = mapSubresource(contextPtr, stagingPtr, 0, D3D11_MAP_READ, 0, out D3D11_MAPPED_SUBRESOURCE mapped);
                        if (mapHr != 0)
                        {
                            return false;
                        }

                        try
                        {
                            UpdateTonemapFromFrame(mapped.pData, (int)mapped.RowPitch);

                            BlitHDRToBgr24(mapped.pData, (int)mapped.RowPitch, texFormat, srcX, srcY, copyW, copyH,
                                bgr24, captureRect.Width, sdrWhiteNits, tonemapCurve);
                            return true;
                        }
                        finally
                        {
                            unmapSubresource(contextPtr, stagingPtr, 0);
                        }
                    }
                    finally
                    {
                        Marshal.Release(texPtr);
                    }
                }
                finally
                {
                    desktopResource?.Dispose();
                    duplication.ReleaseFrame();
                }
            }

            private unsafe void UpdateTonemapFromFrame(IntPtr data, int rowPitch)
            {
                if (texFormat != DXGI_FORMAT_R16G16B16A16_FLOAT && texFormat != DXGI_FORMAT_R10G10B10A2_UNORM)
                {
                    return;
                }

                int bpp = HdrPixelConvert.BytesPerPixel(texFormat);
                int stepY = Math.Max(1, copyH / 64);
                int stepX = Math.Max(1, copyW / 64);

                Span<int> histSpan = warmupHist;

                for (int y = 0; y < copyH; y += stepY)
                {
                    byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * bpp;
                    for (int x = 0; x < copyW; x += stepX)
                    {
                        HdrPixelConvert.DecodeToSdrNormalized(texFormat, src + x * bpp, sdrWhiteNits,
                            out float r, out float g, out float b);
                        HdrTonemap.AccumulateSample(r, g, b, ref warmupSampleCount, ref warmupAboveOne, ref warmupAboveOneHalf,
                            ref warmupHotUpperSdr, ref warmupMaxLum, histSpan);
                    }
                }

                tonemapWarmupCount++;

                if (!tonemapResolved)
                {
                    if (requestedTonemapMode != HdrTonemapMode.Auto && tonemapWarmupCount >= 1)
                    {
                        tonemapResolved = true;
                    }
                    else if (tonemapWarmupCount >= TonemapWarmupFrames)
                    {
                        tonemapResolved = true;
                    }
                }

                if (warmupSampleCount == 0)
                {
                    return;
                }

                HdrLuminanceStats stats = HdrTonemap.BuildStats(warmupSampleCount, warmupAboveOne, warmupAboveOneHalf,
                    warmupHotUpperSdr, warmupMaxLum, warmupHist);

                HdrTonemapMode newMode = requestedTonemapMode == HdrTonemapMode.Auto
                    ? HdrTonemap.ResolveForRecording(requestedTonemapMode, stats, outputDeviceName,
                        hdrDxgiCapture: texFormat != DXGI_FORMAT_B8G8R8A8_UNORM)
                    : (requestedTonemapMode == HdrTonemapMode.WindowsWIC ? HdrTonemapMode.Desktop : requestedTonemapMode);

                if (!tonemapResolved)
                {
                    if (newMode != resolvedTonemapMode || tonemapWarmupCount == 1)
                    {
                        DebugHelper.WriteLine($"HDR recording: tonemap {requestedTonemapMode} -> {newMode} " +
                            $"(frame {tonemapWarmupCount}, P99={stats.P99Estimate:0.00}, max={stats.MaxLuminance:0.00}, hot={stats.FractionHotUpperSdr:P0})");
                    }

                    resolvedTonemapMode = newMode;
                }

                tonemapCurve = HdrTonemap.CreateCurve(resolvedTonemapMode, stats, exposure, sdrWhiteNits);
            }
        }

        private static unsafe void BlitHDRToBgr24(IntPtr data, int rowPitch, int fmt, int srcX, int srcY, int copyW, int copyH,
            byte[] bgr24, int dstStrideWidth, float sdrWhiteNits, HdrTonemapCurve curve)
        {
            if (fmt == DXGI_FORMAT_B8G8R8A8_UNORM)
            {
                HdrRecordingBlit.CopyBgraToBgr24(data, rowPitch, srcX, srcY, copyW, copyH, bgr24, dstStrideWidth);
                return;
            }

            if (fmt != DXGI_FORMAT_R16G16B16A16_FLOAT && fmt != DXGI_FORMAT_R10G10B10A2_UNORM)
            {
                return;
            }

            int bpp = HdrPixelConvert.BytesPerPixel(fmt);
            int rowBytesOut = dstStrideWidth * 3;

            System.Threading.Tasks.Parallel.For(0, copyH, y =>
            {
                byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * bpp;
                int dstRow = y * rowBytesOut;

                for (int x = 0; x < copyW; x++)
                {
                    HdrPixelConvert.DecodeToSdrNormalized(fmt, src + x * bpp, sdrWhiteNits, out float r, out float g, out float b);
                    curve.Map(ref r, ref g, ref b);

                    int di = dstRow + x * 3;
                    bgr24[di] = curve.Encode(b, x, y);
                    bgr24[di + 1] = curve.Encode(g, x, y);
                    bgr24[di + 2] = curve.Encode(r, x, y);
                }
            });
        }
    }
}
