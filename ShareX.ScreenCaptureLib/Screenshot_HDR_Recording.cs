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
using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
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

            private IntPtr devicePtr;
            private IntPtr contextPtr;
            private IDXGIOutputDuplication duplication;
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
                tonemapCurve = HdrTonemap.CreateCurve(resolvedTonemapMode, new HdrLuminanceStats(0, 0, 0, 0, 4f, 4f),
                    this.exposure, sdrWhiteNits);
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
                    Marshal.ReleaseComObject(duplication);
                    duplication = null;
                }

                if (stagingPtr != IntPtr.Zero)
                {
                    Marshal.Release(stagingPtr);
                    stagingPtr = IntPtr.Zero;
                }

                if (contextPtr != IntPtr.Zero)
                {
                    Marshal.Release(contextPtr);
                    contextPtr = IntPtr.Zero;
                }

                if (devicePtr != IntPtr.Zero)
                {
                    Marshal.Release(devicePtr);
                    devicePtr = IntPtr.Zero;
                }
            }

            private bool TryInitialize(out Rectangle monitorRect, out Rectangle intersection, out int format,
                out float whiteNits, out string deviceName)
            {
                monitorRect = Rectangle.Empty;
                intersection = Rectangle.Empty;
                format = DXGI_FORMAT_B8G8R8A8_UNORM;
                whiteNits = HdrPixelConvert.SceneReferredWhiteNits;
                deviceName = null;

                int[] levels = { 0xb100, 0xb000 };
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    levels, (uint)levels.Length, D3D11_SDK_VERSION,
                    out devicePtr, out _, out contextPtr);
                if (hr != 0)
                {
                    return false;
                }

                object deviceUnk = Marshal.GetObjectForIUnknown(devicePtr);
                try
                {
                    var dxgiDevice = (IDXGIDevice)deviceUnk;
                    hr = dxgiDevice.GetAdapter(out IDXGIAdapter adapter);
                    if (hr != 0)
                    {
                        return false;
                    }

                    IDXGIOutput bestOutput = null;
                    int bestArea = 0;

                    for (uint outputIdx = 0; ; outputIdx++)
                    {
                        IDXGIOutput output;
                        try
                        {
                            hr = adapter.EnumOutputs(outputIdx, out output);
                        }
                        catch (COMException ex) when (ex.HResult == DXGI_ERROR_NOT_FOUND)
                        {
                            break;
                        }

                        if (hr != 0 || output == null)
                        {
                            break;
                        }

                        output.GetDesc(out DXGI_OUTPUT_DESC outputDesc);
                        Rectangle outputRect = new Rectangle(
                            outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Top,
                            outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top);

                        Rectangle currentIntersection = Rectangle.Intersect(captureRect, outputRect);
                        int area = currentIntersection.Width * currentIntersection.Height;
                        if (area > bestArea)
                        {
                            if (bestOutput != null)
                            {
                                Marshal.ReleaseComObject(bestOutput);
                            }

                            bestArea = area;
                            bestOutput = output;
                            monitorRect = outputRect;
                            intersection = currentIntersection;
                            deviceName = outputDesc.DeviceName;
                            whiteNits = DisplayConfigHelper.GetSdrWhiteNits(outputDesc.DeviceName);
                        }
                        else
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }

                    if (bestOutput == null || intersection.Width <= 0 || intersection.Height <= 0)
                    {
                        return false;
                    }

                    try
                    {
                        if (!TryCreateDuplication(bestOutput, deviceUnk, out duplication, out format))
                        {
                            return false;
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(bestOutput);
                    }

                    createTex2D = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5));
                    copyResource = Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47));
                    mapSubresource = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14));
                    unmapSubresource = Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15));

                    return true;
                }
                finally
                {
                    Marshal.ReleaseComObject(deviceUnk);
                }
            }

            private bool TryCreateDuplication(IDXGIOutput output, object deviceUnk,
                out IDXGIOutputDuplication duplicationOut, out int formatOut)
            {
                duplicationOut = null;
                formatOut = DXGI_FORMAT_B8G8R8A8_UNORM;

                try
                {
                    var output5 = (IDXGIOutput5)output;
                    int[] formats = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
                    int duplHr = output5.DuplicateOutput1(deviceUnk, 0, (uint)formats.Length, formats, out duplicationOut);
                    if (duplHr == 0 && duplicationOut != null)
                    {
                        duplicationOut.GetDesc(out DXGI_OUTDUPL_DESC dd);
                        formatOut = (int)dd.ModeDesc.Format;
                        return true;
                    }
                }
                catch (InvalidCastException)
                {
                }

                var output1 = (IDXGIOutput1)output;
                int legacyHr = output1.DuplicateOutput(deviceUnk, out duplicationOut);
                if (legacyHr != 0 || duplicationOut == null)
                {
                    return false;
                }

                duplicationOut.GetDesc(out DXGI_OUTDUPL_DESC legacyDesc);
                formatOut = (int)legacyDesc.ModeDesc.Format;
                return true;
            }

            private bool TryAcquireAndConvert(byte[] bgr24)
            {
                object resourceObj = null;
                int maxAttempts = hasValidFrame ? 1 : 8;

                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    uint timeoutMs = hasValidFrame ? 0 : (uint)(attempt == 0 ? 50 : 8);
                    int acquireHr = duplication.AcquireNextFrame(timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out resourceObj);

                    if (acquireHr == 0)
                    {
                        // After the duplication is populated, LastPresentTime == 0 means
                        // "nothing changed", not "blank surface".
                        if (!hasValidFrame && frameInfo.LastPresentTime == 0 && attempt < 3)
                        {
                            ReleaseResource(resourceObj);
                            resourceObj = null;
                            duplication.ReleaseFrame();
                            Thread.Sleep(5);
                            continue;
                        }

                        break;
                    }

                    if (acquireHr == DXGI_ERROR_ACCESS_LOST)
                    {
                        return false;
                    }

                    if (acquireHr == DXGI_ERROR_WAIT_TIMEOUT)
                    {
                        return false;
                    }

                    Thread.Sleep(1);
                }

                if (resourceObj == null)
                {
                    return false;
                }

                try
                {
                    IntPtr resourcePtr = Marshal.GetIUnknownForObject(resourceObj);
                    try
                    {
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
                                if (!tonemapResolved)
                                {
                                    ResolveRecordingTonemapAndCurve(mapped.pData, (int)mapped.RowPitch);
                                    tonemapResolved = true;
                                    DebugHelper.WriteLine($"HDR recording: resolved tonemap {requestedTonemapMode} -> {resolvedTonemapMode}");
                                }

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
                        Marshal.Release(resourcePtr);
                    }
                }
                finally
                {
                    ReleaseResource(resourceObj);
                    duplication.ReleaseFrame();
                }
            }

            private static void ReleaseResource(object resourceObj)
            {
                if (resourceObj != null)
                {
                    Marshal.ReleaseComObject(resourceObj);
                }
            }

            private unsafe void ResolveRecordingTonemapAndCurve(IntPtr data, int rowPitch)
            {
                long sampleCount = 0, aboveOne = 0, aboveOneHalf = 0, hotUpperSdr = 0;
                float maxLum = 0f;
                Span<int> hist = stackalloc int[HdrTonemap.HistogramSize];
                hist.Clear();

                if (texFormat == DXGI_FORMAT_R16G16B16A16_FLOAT || texFormat == DXGI_FORMAT_R10G10B10A2_UNORM)
                {
                    int bpp = HdrPixelConvert.BytesPerPixel(texFormat);
                    int stepY = Math.Max(1, copyH / 64);
                    int stepX = Math.Max(1, copyW / 64);

                    for (int y = 0; y < copyH; y += stepY)
                    {
                        byte* src = (byte*)data + (long)(srcY + y) * rowPitch + (long)srcX * bpp;
                        for (int x = 0; x < copyW; x += stepX)
                        {
                            HdrPixelConvert.DecodeToSdrNormalized(texFormat, src + x * bpp, sdrWhiteNits,
                                out float r, out float g, out float b);
                            HdrTonemap.AccumulateSample(r, g, b, ref sampleCount, ref aboveOne, ref aboveOneHalf,
                                ref hotUpperSdr, ref maxLum, hist);
                        }
                    }
                }

                HdrLuminanceStats stats = HdrTonemap.BuildStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, hist);
                resolvedTonemapMode = HdrTonemap.ResolveForRecording(requestedTonemapMode, stats, outputDeviceName);
                tonemapCurve = HdrTonemap.CreateCurve(resolvedTonemapMode, stats, exposure, sdrWhiteNits);
            }
        }

        private static unsafe void BlitHDRToBgr24(IntPtr data, int rowPitch, int fmt, int srcX, int srcY, int copyW, int copyH,
            byte[] bgr24, int dstStrideWidth, float sdrWhiteNits, HdrTonemapCurve curve)
        {
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
