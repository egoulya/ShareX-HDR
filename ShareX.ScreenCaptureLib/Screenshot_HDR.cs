// File: ShareX.ScreenCaptureLib/Screenshot_HDR.cs
//
// HDR screenshot capture using DXGI Desktop Duplication.
// Uses [ComImport] interface declarations for DXGI interfaces (safe vtable
// dispatch via CLR) and raw vtable calls only for the few D3D11 methods
// needed (ID3D11Device has ~43 methods, making full [ComImport] brittle).
//
// Multi-monitor: DXGI Desktop Duplication is per-output. ShareX capture rects
// use virtual desktop coordinates that can span multiple monitors. This code
// enumerates all DXGI outputs, captures each monitor that intersects the
// requested rect, and composites the results onto a single bitmap.
//
// Capture sequence:
//   Cached D3D11 device + per-output DuplicateOutput (invalidated on
//   WM_DISPLAYCHANGE / DXGI_ERROR_ACCESS_LOST). Warm sessions accept
//   LastPresentTime == 0. HDR outputs: copy pixels, merge luminance
//   stats, one BT.2390 curve, Parallel.For convert. SDR outputs: GDI.

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
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        public bool CaptureHDREnabled { get; set; } = false;
        public HdrTonemapMode HdrTonemapMode { get; set; } = HdrTonemapMode.Auto;
        public float HdrExposure { get; set; } = HdrTonemap.ExposureDefault;

        // ====================================================================
        // COM Interface Declarations (proper [ComImport])
        // ====================================================================
        // The CLR uses these to build correct vtables automatically.
        // Method order must exactly match the IDL/header order.
        // We use [ComImport] for all DXGI interfaces (where correct vtable
        // dispatch is critical) and raw vtable calls for the few D3D11
        // methods we need (ID3D11Device has ~43 methods, making full
        // [ComImport] declaration impractical and error-prone).

        #region COM Interfaces

        // --- DXGI Interfaces ---

        [ComImport, Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIDevice
        {
            // IDXGIObject methods (4 methods: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIDevice methods
            int GetAdapter(out IDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter
        {
            // IDXGIObject methods
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIAdapter methods
            int EnumOutputs(uint index, out IDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            // IDXGIObject (4 methods)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput methods (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [ComImport, Guid("00cddea8-939b-4b83-a340-a685226666cc"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput1
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12 methods)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4 methods)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
        }

        // IDXGIOutput5 inherits: IDXGIOutput4 -> IDXGIOutput3 -> IDXGIOutput2 -> IDXGIOutput1
        // IDXGIOutput2 adds: SupportsOverlays (1 method)
        // IDXGIOutput3 adds: CheckOverlaySupport (1 method)
        // IDXGIOutput4 adds: CheckOverlayColorSpaceSupport (1 method)
        // IDXGIOutput5 adds: DuplicateOutput1 (1 method)
        [ComImport, Guid("80A07424-AB52-42EB-833C-0C42FD282D98"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput5
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutput (12)
            int GetDesc(out DXGI_OUTPUT_DESC desc);
            int GetDisplayModeList(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int WaitForVBlank();
            int TakeOwnership([MarshalAs(UnmanagedType.IUnknown)] object device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            void ReleaseOwnership();
            int GetGammaControlCapabilities(IntPtr gammaCaps);
            int SetGammaControl(IntPtr gamma);
            int GetGammaControl(IntPtr gamma);
            int SetDisplaySurface([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetDisplaySurfaceData([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int GetFrameStatistics(IntPtr stats);

            // IDXGIOutput1 (4)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);

            // IDXGIOutput2 (1)
            [PreserveSig] int SupportsOverlays();

            // IDXGIOutput3 (1)
            int CheckOverlaySupport(uint format, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput4 (1)
            int CheckOverlayColorSpaceSupport(uint format, uint colorSpace, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // IDXGIOutput5 (1)
            int DuplicateOutput1([MarshalAs(UnmanagedType.IUnknown)] object device, uint flags,
                uint formatCount, [MarshalAs(UnmanagedType.LPArray)] int[] formats,
                out IDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutputDuplication
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // IDXGIOutputDuplication methods
            void GetDesc(out DXGI_OUTDUPL_DESC desc);

            [PreserveSig]
            int AcquireNextFrame(uint timeoutMs, out DXGI_OUTDUPL_FRAME_INFO frameInfo,
                [MarshalAs(UnmanagedType.IUnknown)] out object resource);

            int GetFrameDirtyRects(uint dirtyRectsBufferSize, IntPtr dirtyRectsBuffer, out uint dirtyRectsBufferSizeRequired);
            int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr moveRectsBuffer, out uint moveRectsBufferSizeRequired);
            int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pointerShapeBuffer,
                out uint pointerShapeBufferSizeRequired, IntPtr pointerShapeInfo);
            int MapDesktopSurface(out DXGI_MAPPED_RECT mappedRect);
            int UnMapDesktopSurface();

            [PreserveSig]
            int ReleaseFrame();
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_DESC
        {
            public DXGI_MODE_DESC ModeDesc;
            public uint Rotation;
            [MarshalAs(UnmanagedType.Bool)]
            public bool DesktopImageInSystemMemory;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MODE_DESC
        {
            public uint Width, Height;
            public DXGI_RATIONAL RefreshRate;
            public uint Format;
            public uint ScanlineOrdering;
            public uint Scaling;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_RATIONAL { public uint Numerator, Denominator; }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ProtectedContentMaskedOut;
            public int PointerPositionX;
            public int PointerPositionY;
            [MarshalAs(UnmanagedType.Bool)]
            public bool PointerPositionVisible;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_MAPPED_RECT
        {
            public int Pitch;
            public IntPtr pBits;
        }

        #endregion

        // ====================================================================
        // D3D11 Types and Imports (raw vtable for the few calls we need)
        // ====================================================================

        #region D3D11

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            IntPtr pAdapter, int DriverType, IntPtr Software, uint Flags,
            int[] pFeatureLevels, uint FeatureLevels, uint SDKVersion,
            out IntPtr ppDevice, out int pFeatureLevel, out IntPtr ppImmediateContext);

        private const int D3D_DRIVER_TYPE_HARDWARE = 1;
        private const uint D3D11_SDK_VERSION = 7;

        private const int DXGI_FORMAT_R16G16B16A16_FLOAT = 10;
        private const int DXGI_FORMAT_R10G10B10A2_UNORM = 24;
        private const int DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);

        private const int D3D11_USAGE_STAGING = 3;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const int D3D11_MAP_READ = 1;

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width, Height, MipLevels, ArraySize;
            public int Format;
            public uint SampleCount, SampleQuality;
            public int Usage;
            public uint BindFlags, CPUAccessFlags, MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr pData;
            public uint RowPitch, DepthPitch;
        }

        // ID3D11Texture2D::GetDesc -- vtable slot 10
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_GetTexDesc(IntPtr self, out D3D11_TEXTURE2D_DESC desc);

        // ID3D11Device::CreateTexture2D -- vtable slot 5
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_CreateTex2D(IntPtr self, ref D3D11_TEXTURE2D_DESC desc, IntPtr init, out IntPtr tex);

        // ID3D11DeviceContext::Map -- vtable slot 14
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Del_Map(IntPtr self, IntPtr res, uint sub, int type, uint flags, out D3D11_MAPPED_SUBRESOURCE mapped);

        // ID3D11DeviceContext::Unmap -- vtable slot 15
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_Unmap(IntPtr self, IntPtr res, uint sub);

        // ID3D11DeviceContext::CopyResource -- vtable slot 47
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void Del_CopyResource(IntPtr self, IntPtr dst, IntPtr src);

        private static IntPtr VT(IntPtr obj, int slot) => Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj), slot * IntPtr.Size);

        #endregion

        // ====================================================================
        // Main HDR Capture (multi-monitor aware)
        // ====================================================================

        public static float GetSdrWhiteLevelNits() => DisplayConfigHelper.GetSdrWhiteNits();

        public static float GetSdrWhiteLevelNits(string deviceName) => DisplayConfigHelper.GetSdrWhiteNits(deviceName);

        public static float GetSdrWhiteNormalizationScale(string deviceName = null) =>
            HdrPixelConvert.NormalizationScale(DisplayConfigHelper.GetSdrWhiteNits(deviceName));

        public static void WarmHdrCapture() => EnsureDuplicationCache();

        /// <summary>
        /// Captures the specified rectangle in virtual desktop coordinates using
        /// DXGI Desktop Duplication. Handles multi-monitor setups by enumerating
        /// all outputs, capturing each intersecting monitor, and compositing the
        /// results onto a single bitmap.
        /// </summary>
        public Bitmap CaptureRectangleHDR(Rectangle rect)
        {
            EnsureDuplicationCache();

            try
            {
                if (!TryGetSharedDevice(out IntPtr devicePtr, out IntPtr contextPtr, out object deviceUnk))
                {
                    return null;
                }

                object dxgiDeviceObj = deviceUnk;
                var dxgiDevice = (IDXGIDevice)dxgiDeviceObj;
                int hr = dxgiDevice.GetAdapter(out IDXGIAdapter adapter);
                if (hr != 0)
                {
                    DebugHelper.WriteLine($"HDR: GetAdapter failed 0x{hr:X8}");
                    return null;
                }

                Bitmap result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                bool anyOutputCaptured = false;
                List<HdrPendingOutput> pendingHdr = new List<HdrPendingOutput>();

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
                    if (hr == DXGI_ERROR_NOT_FOUND || output == null)
                    {
                        break;
                    }
                    if (hr != 0)
                    {
                        DebugHelper.WriteLine($"HDR: EnumOutputs({outputIdx}) failed 0x{hr:X8}");
                        break;
                    }

                    try
                    {
                        output.GetDesc(out DXGI_OUTPUT_DESC outputDesc);
                        Rectangle monitorRect = new Rectangle(
                            outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Top,
                            outputDesc.DesktopCoordinates.Right - outputDesc.DesktopCoordinates.Left,
                            outputDesc.DesktopCoordinates.Bottom - outputDesc.DesktopCoordinates.Top);

                        Rectangle intersection = Rectangle.Intersect(rect, monitorRect);
                        if (intersection.Width <= 0 || intersection.Height <= 0)
                        {
                            continue;
                        }

                        DebugHelper.WriteLine($"HDR: Output {outputIdx} ({outputDesc.DeviceName}) {monitorRect}, intersection={intersection}");

                        HdrDuplSession session = GetOrCreateSession(outputDesc.DeviceName, output, devicePtr, contextPtr);
                        if (session == null || session.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
                        {
                            DebugHelper.WriteLine($"HDR: Output {outputIdx} is SDR, using GDI fast path.");
                            if (CaptureOutputGDI(intersection, rect, result))
                            {
                                anyOutputCaptured = true;
                            }
                            continue;
                        }

                        float sdrWhiteNits = DisplayConfigHelper.GetSdrWhiteNits(outputDesc.DeviceName);
                        DebugHelper.WriteLine($"HDR: Output {outputIdx} HDR format={session.Format}, sdrWhite={sdrWhiteNits:0.#} nits, warm={session.Warm}");

                        if (HdrTonemapMode == HdrTonemapMode.WindowsWIC)
                        {
                            if (CaptureOutputHdrWic(session, monitorRect, rect, intersection, result))
                            {
                                anyOutputCaptured = true;
                            }
                            continue;
                        }

                        pendingHdr.Add(new HdrPendingOutput
                        {
                            Session = session,
                            MonitorRect = monitorRect,
                            Intersection = intersection,
                            SdrWhiteNits = sdrWhiteNits
                        });
                    }
                    catch (Exception e)
                    {
                        DebugHelper.WriteException(e, $"HDR: Output {outputIdx} capture failed, continuing.");
                    }
                }

                if (pendingHdr.Count == 1)
                {
                    if (CaptureOutputHdrDirect(pendingHdr[0], rect, result))
                    {
                        anyOutputCaptured = true;
                    }
                }
                else if (pendingHdr.Count > 1)
                {
                    List<HdrCpuSlice> hdrSlices = new List<HdrCpuSlice>(pendingHdr.Count);
                    foreach (HdrPendingOutput pending in pendingHdr)
                    {
                        HdrCpuSlice slice = CaptureOutputHdrSlice(pending, rect);
                        if (slice != null)
                        {
                            hdrSlices.Add(slice);
                        }
                    }

                    if (hdrSlices.Count > 0)
                    {
                        HdrLuminanceAccumulator acc = new HdrLuminanceAccumulator();
                        double whiteAcc = 0, areaAcc = 0;
                        foreach (HdrCpuSlice slice in hdrSlices)
                        {
                            acc.AddFromPacked(slice.Packed, slice.PackedStride, slice.Format, slice.CopyW, slice.CopyH,
                                slice.SdrWhiteNits);
                            double area = (double)slice.CopyW * slice.CopyH;
                            whiteAcc += slice.SdrWhiteNits * area;
                            areaAcc += area;
                        }

                        // Pixels are already normalized per output (1.0 = that monitor's paper white).
                        // CreateCurve still converts peakNorm back to nits for the PQ EETF, so it
                        // needs one paperwhite. Area-weight so a 203-nit panel that owns most of the
                        // region is not evaluated as if it were the 400-nit neighbor. Math.Max would
                        // shift the knee on the dimmer display.
                        float curveWhiteNits = areaAcc > 0
                            ? (float)(whiteAcc / areaAcc)
                            : HdrPixelConvert.SceneReferredWhiteNits;

                        HdrLuminanceStats merged = acc.Build();
                        string hysteresisKey = "span";
                        HdrTonemapMode resolvedMode = HdrTonemap.ResolveMode(HdrTonemapMode, merged, hysteresisKey);
                        HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, merged, HdrExposure, curveWhiteNits);
                        DebugHelper.WriteLine($"HDR: merged tonemap {HdrTonemapMode} -> {resolvedMode} (P99={merged.P99Estimate:0.00}, max={merged.MaxLuminance:0.00}, curveWhite={curveWhiteNits:0.#})");

                        foreach (HdrCpuSlice slice in hdrSlices)
                        {
                            BlitPackedSlice(slice, result, curve);
                        }

                        anyOutputCaptured = true;
                    }
                }

                if (!anyOutputCaptured)
                {
                    DebugHelper.WriteLine("HDR: No outputs captured successfully.");
                    result.Dispose();
                    return null;
                }

                DebugHelper.WriteLine($"HDR: Composite output {result.Width}x{result.Height}");
                return result;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR capture failed.");
                return null;
            }
        }

        private sealed class HdrPendingOutput
        {
            public HdrDuplSession Session;
            public Rectangle MonitorRect;
            public Rectangle Intersection;
            public float SdrWhiteNits;
        }

        private sealed class HdrCpuSlice
        {
            public byte[] Packed;
            public int PackedStride;
            public int Format;
            public int CopyW;
            public int CopyH;
            public int DstX;
            public int DstY;
            public float SdrWhiteNits;
            public string DeviceName;
        }

        private sealed class HdrLuminanceAccumulator
        {
            private long sampleCount, aboveOne, aboveOneHalf, hotUpperSdr;
            private float maxLum;
            private readonly int[] hist = new int[HdrTonemap.HistogramSize];

            public unsafe void AddFromPacked(byte[] packed, int packedStride, int format, int copyW, int copyH,
                float sdrWhiteNits)
            {
                int bpp = HdrPixelConvert.BytesPerPixel(format);
                int stepY = Math.Max(1, copyH / 64);
                int stepX = Math.Max(1, copyW / 64);

                fixed (byte* packedPtr = packed)
                {
                    Add(packedPtr, packedStride, format, 0, 0, copyW, copyH, sdrWhiteNits, stepX, stepY);
                }
            }

            public unsafe void AddFromMapped(IntPtr data, int rowPitch, int format, int srcX, int srcY,
                int copyW, int copyH, float sdrWhiteNits)
            {
                int stepY = Math.Max(1, copyH / 64);
                int stepX = Math.Max(1, copyW / 64);
                Add((byte*)data, rowPitch, format, srcX, srcY, copyW, copyH, sdrWhiteNits, stepX, stepY);
            }

            private unsafe void Add(byte* basePtr, int stride, int format, int srcX, int srcY,
                int copyW, int copyH, float sdrWhiteNits, int stepX, int stepY)
            {
                int bpp = HdrPixelConvert.BytesPerPixel(format);
                for (int y = 0; y < copyH; y += stepY)
                {
                    byte* srcRow = basePtr + (long)(srcY + y) * stride + (long)srcX * bpp;
                    for (int x = 0; x < copyW; x += stepX)
                    {
                        HdrPixelConvert.DecodeToSdrNormalized(format, srcRow + x * bpp, sdrWhiteNits,
                            out float r, out float g, out float b);
                        HdrTonemap.AccumulateSample(r, g, b, ref sampleCount, ref aboveOne, ref aboveOneHalf,
                            ref hotUpperSdr, ref maxLum, hist);
                    }
                }
            }

            public HdrLuminanceStats Build() =>
                HdrTonemap.BuildStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, hist);
        }

        private sealed class HdrDuplSession
        {
            public string DeviceName;
            public int Format;
            public bool Warm;
            public IDXGIOutputDuplication Duplication;
            public IntPtr Staging;
            public IntPtr Device;
            public IntPtr Context;
            public Del_CreateTex2D CreateTex;
            public Del_CopyResource Copy;
            public Del_Map Map;
            public Del_Unmap Unmap;
            public Del_GetTexDesc GetDesc;
            public byte[] PackedBuffer;
        }

        private static readonly object SessionLock = new();
        private static IntPtr SharedDevicePtr;
        private static IntPtr SharedContextPtr;
        private static object SharedDeviceUnk;
        private static readonly Dictionary<string, HdrDuplSession> Sessions = new();
        private static bool DisplayHooked;

        private static void EnsureDuplicationCache()
        {
            if (DisplayHooked)
            {
                return;
            }

            lock (SessionLock)
            {
                if (DisplayHooked)
                {
                    return;
                }

                SystemEvents.DisplaySettingsChanged += (_, _) => InvalidateDuplicationCache();
                DisplayHooked = true;
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    TryGetSharedDevice(out IntPtr devicePtr, out IntPtr contextPtr, out object deviceUnk);
                });
            }
        }

        private static void InvalidateDuplicationCache()
        {
            lock (SessionLock)
            {
                foreach (HdrDuplSession session in Sessions.Values)
                {
                    DisposeSession(session);
                }

                Sessions.Clear();
                DisposeSharedDeviceUnlocked();
            }
        }

        private static void DisposeSharedDeviceUnlocked()
        {
            if (SharedDeviceUnk != null)
            {
                try { Marshal.ReleaseComObject(SharedDeviceUnk); } catch { }
                SharedDeviceUnk = null;
            }

            if (SharedContextPtr != IntPtr.Zero)
            {
                Marshal.Release(SharedContextPtr);
                SharedContextPtr = IntPtr.Zero;
            }

            if (SharedDevicePtr != IntPtr.Zero)
            {
                Marshal.Release(SharedDevicePtr);
                SharedDevicePtr = IntPtr.Zero;
            }
        }

        private static bool TryGetSharedDevice(out IntPtr devicePtr, out IntPtr contextPtr, out object deviceUnk)
        {
            lock (SessionLock)
            {
                if (SharedDevicePtr != IntPtr.Zero)
                {
                    devicePtr = SharedDevicePtr;
                    contextPtr = SharedContextPtr;
                    deviceUnk = SharedDeviceUnk;
                    return true;
                }

                int[] levels = { 0xb100, 0xb000 };
                int hr = D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, 0,
                    levels, (uint)levels.Length, D3D11_SDK_VERSION,
                    out SharedDevicePtr, out _, out SharedContextPtr);
                if (hr != 0)
                {
                    DebugHelper.WriteLine($"HDR: D3D11CreateDevice failed 0x{hr:X8}");
                    devicePtr = IntPtr.Zero;
                    contextPtr = IntPtr.Zero;
                    deviceUnk = null;
                    return false;
                }

                SharedDeviceUnk = Marshal.GetObjectForIUnknown(SharedDevicePtr);
                devicePtr = SharedDevicePtr;
                contextPtr = SharedContextPtr;
                deviceUnk = SharedDeviceUnk;
                return true;
            }
        }

        private static HdrDuplSession GetOrCreateSession(string deviceName, IDXGIOutput output, IntPtr devicePtr, IntPtr contextPtr)
        {
            lock (SessionLock)
            {
                if (!string.IsNullOrEmpty(deviceName) && Sessions.TryGetValue(deviceName, out HdrDuplSession existing) &&
                    existing.Duplication != null)
                {
                    return existing;
                }
            }

            if (!TryCreateDuplication(output, devicePtr, out IDXGIOutputDuplication duplication, out int format))
            {
                return null;
            }

            HdrDuplSession session = new HdrDuplSession
            {
                DeviceName = deviceName,
                Format = format,
                Duplication = duplication,
                Device = devicePtr,
                Context = contextPtr,
                CreateTex = Marshal.GetDelegateForFunctionPointer<Del_CreateTex2D>(VT(devicePtr, 5)),
                Copy = Marshal.GetDelegateForFunctionPointer<Del_CopyResource>(VT(contextPtr, 47)),
                Map = Marshal.GetDelegateForFunctionPointer<Del_Map>(VT(contextPtr, 14)),
                Unmap = Marshal.GetDelegateForFunctionPointer<Del_Unmap>(VT(contextPtr, 15))
            };

            lock (SessionLock)
            {
                if (!string.IsNullOrEmpty(deviceName))
                {
                    if (Sessions.TryGetValue(deviceName, out HdrDuplSession raced) && raced.Duplication != null)
                    {
                        DisposeSession(session);
                        return raced;
                    }

                    Sessions[deviceName] = session;
                }
            }

            return session;
        }

        private static bool TryCreateDuplication(IDXGIOutput output, IntPtr devicePtr,
            out IDXGIOutputDuplication duplication, out int format)
        {
            duplication = null;
            format = DXGI_FORMAT_B8G8R8A8_UNORM;
            object deviceUnk = Marshal.GetObjectForIUnknown(devicePtr);
            try
            {
                try
                {
                    var output5 = (IDXGIOutput5)output;
                    int[] formats = { DXGI_FORMAT_R16G16B16A16_FLOAT, DXGI_FORMAT_R10G10B10A2_UNORM, DXGI_FORMAT_B8G8R8A8_UNORM };
                    int hr = output5.DuplicateOutput1(deviceUnk, 0, (uint)formats.Length, formats, out duplication);
                    if (hr == 0 && duplication != null)
                    {
                        duplication.GetDesc(out DXGI_OUTDUPL_DESC dd);
                        format = (int)dd.ModeDesc.Format;
                        return true;
                    }
                }
                catch (InvalidCastException)
                {
                }

                var output1 = (IDXGIOutput1)output;
                int legacyHr = output1.DuplicateOutput(deviceUnk, out duplication);
                if (legacyHr != 0 || duplication == null)
                {
                    return false;
                }

                duplication.GetDesc(out DXGI_OUTDUPL_DESC legacyDesc);
                format = (int)legacyDesc.ModeDesc.Format;
                return true;
            }
            finally
            {
                Marshal.ReleaseComObject(deviceUnk);
            }
        }

        private static void DropSession(string deviceName)
        {
            lock (SessionLock)
            {
                if (deviceName != null && Sessions.TryGetValue(deviceName, out HdrDuplSession session))
                {
                    Sessions.Remove(deviceName);
                    DisposeSession(session);
                }
            }
        }

        private static void DisposeSession(HdrDuplSession session)
        {
            if (session == null)
            {
                return;
            }

            if (session.Duplication != null)
            {
                try { session.Duplication.ReleaseFrame(); } catch { }
                try { Marshal.ReleaseComObject(session.Duplication); } catch { }
                session.Duplication = null;
            }

            if (session.Staging != IntPtr.Zero)
            {
                Marshal.Release(session.Staging);
                session.Staging = IntPtr.Zero;
            }

            session.PackedBuffer = null;
        }

        private static bool CaptureOutputHdrWic(HdrDuplSession session, Rectangle monitorRect, Rectangle captureRect,
            Rectangle intersection, Bitmap composite)
        {
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return false;
            }

            try
            {
                int srcX = intersection.X - monitorRect.X;
                int srcY = intersection.Y - monitorRect.Y;
                int copyW = Math.Min(intersection.Width, texW - srcX);
                int copyH = Math.Min(intersection.Height, texH - srcY);
                int dstX = intersection.X - captureRect.X;
                int dstY = intersection.Y - captureRect.Y;
                if (copyW <= 0 || copyH <= 0)
                {
                    return false;
                }

                return HdrWicTonemap.TryBlitToBitmap(mapped.pData, (int)mapped.RowPitch, texW, texH, session.Format,
                    srcX, srcY, copyW, copyH, composite, dstX, dstY);
            }
            finally
            {
                session.Unmap(session.Context, session.Staging, 0);
                try { session.Duplication.ReleaseFrame(); } catch { }
            }
        }

        private bool CaptureOutputHdrDirect(HdrPendingOutput pending, Rectangle captureRect, Bitmap composite)
        {
            HdrDuplSession session = pending.Session;
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return false;
            }

            try
            {
                if (!TryGetCopyRect(pending, captureRect, texW, texH,
                    out int srcX, out int srcY, out int copyW, out int copyH, out int dstX, out int dstY))
                {
                    return false;
                }

                HdrLuminanceAccumulator acc = new HdrLuminanceAccumulator();
                acc.AddFromMapped(mapped.pData, (int)mapped.RowPitch, session.Format, srcX, srcY, copyW, copyH,
                    pending.SdrWhiteNits);
                HdrLuminanceStats stats = acc.Build();
                HdrTonemapMode resolvedMode = HdrTonemap.ResolveMode(HdrTonemapMode, stats, session.DeviceName);
                HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, stats, HdrExposure, pending.SdrWhiteNits);
                DebugHelper.WriteLine($"HDR: direct tonemap {HdrTonemapMode} -> {resolvedMode} (P99={stats.P99Estimate:0.00}, max={stats.MaxLuminance:0.00}, sdrWhite={pending.SdrWhiteNits:0.#})");

                BlitMapped(mapped.pData, (int)mapped.RowPitch, session.Format, srcX, srcY, copyW, copyH,
                    pending.SdrWhiteNits, composite, dstX, dstY, curve);
                return true;
            }
            finally
            {
                session.Unmap(session.Context, session.Staging, 0);
                try { session.Duplication.ReleaseFrame(); } catch { }
            }
        }

        private static HdrCpuSlice CaptureOutputHdrSlice(HdrPendingOutput pending, Rectangle captureRect)
        {
            HdrDuplSession session = pending.Session;
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return null;
            }

            try
            {
                if (!TryGetCopyRect(pending, captureRect, texW, texH,
                    out int srcX, out int srcY, out int copyW, out int copyH, out int dstX, out int dstY))
                {
                    return null;
                }

                int bpp = HdrPixelConvert.BytesPerPixel(session.Format);
                int packedStride = copyW * bpp;
                int packedBytes = packedStride * copyH;
                byte[] packed = EnsureSessionBuffer(session, packedBytes);
                unsafe
                {
                    for (int y = 0; y < copyH; y++)
                    {
                        byte* src = (byte*)mapped.pData + (long)(srcY + y) * mapped.RowPitch + (long)srcX * bpp;
                        Marshal.Copy((IntPtr)src, packed, y * packedStride, packedStride);
                    }
                }

                return new HdrCpuSlice
                {
                    Packed = packed,
                    PackedStride = packedStride,
                    Format = session.Format,
                    CopyW = copyW,
                    CopyH = copyH,
                    DstX = dstX,
                    DstY = dstY,
                    SdrWhiteNits = pending.SdrWhiteNits,
                    DeviceName = session.DeviceName
                };
            }
            finally
            {
                session.Unmap(session.Context, session.Staging, 0);
                try { session.Duplication.ReleaseFrame(); } catch { }
            }
        }

        private static bool TryGetCopyRect(HdrPendingOutput pending, Rectangle captureRect, int texW, int texH,
            out int srcX, out int srcY, out int copyW, out int copyH, out int dstX, out int dstY)
        {
            srcX = pending.Intersection.X - pending.MonitorRect.X;
            srcY = pending.Intersection.Y - pending.MonitorRect.Y;
            copyW = Math.Min(pending.Intersection.Width, texW - srcX);
            copyH = Math.Min(pending.Intersection.Height, texH - srcY);
            dstX = pending.Intersection.X - captureRect.X;
            dstY = pending.Intersection.Y - captureRect.Y;
            return copyW > 0 && copyH > 0;
        }

        private static byte[] EnsureSessionBuffer(HdrDuplSession session, int bytes)
        {
            if (session.PackedBuffer == null || session.PackedBuffer.Length < bytes)
            {
                session.PackedBuffer = GC.AllocateUninitializedArray<byte>(bytes);
            }

            return session.PackedBuffer;
        }

        private static bool TryAcquireMapped(HdrDuplSession session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH)
        {
            mapped = default;
            texW = texH = 0;

            if (!TryAcquireDesktopResource(session, out object resourceObj))
            {
                return false;
            }

            IntPtr resourcePtr = Marshal.GetIUnknownForObject(resourceObj);
            try
            {
                Guid texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
                Marshal.QueryInterface(resourcePtr, in texGuid, out IntPtr texPtr);
                try
                {
                    session.GetDesc ??= Marshal.GetDelegateForFunctionPointer<Del_GetTexDesc>(VT(texPtr, 10));
                    session.GetDesc(texPtr, out D3D11_TEXTURE2D_DESC td);
                    texW = (int)td.Width;
                    texH = (int)td.Height;

                    if (session.Staging == IntPtr.Zero)
                    {
                        td.Usage = D3D11_USAGE_STAGING;
                        td.BindFlags = 0;
                        td.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
                        td.MiscFlags = 0;
                        int createHr = session.CreateTex(session.Device, ref td, IntPtr.Zero, out session.Staging);
                        if (createHr != 0)
                        {
                            return false;
                        }
                    }

                    session.Copy(session.Context, session.Staging, texPtr);
                    int mapHr = session.Map(session.Context, session.Staging, 0, D3D11_MAP_READ, 0, out mapped);
                    return mapHr == 0;
                }
                finally
                {
                    Marshal.Release(texPtr);
                }
            }
            finally
            {
                Marshal.Release(resourcePtr);
                Marshal.ReleaseComObject(resourceObj);
            }
        }

        private static bool TryAcquireDesktopResource(HdrDuplSession session, out object resourceObj)
        {
            resourceObj = null;
            DXGI_OUTDUPL_FRAME_INFO frameInfo = default;

            if (session.Warm)
            {
                int hr = session.Duplication.AcquireNextFrame(16, out frameInfo, out resourceObj);
                if (hr == DXGI_ERROR_ACCESS_LOST)
                {
                    DropSession(session.DeviceName);
                    return false;
                }

                if (hr == 0 && resourceObj != null)
                {
                    return true;
                }

                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    hr = session.Duplication.AcquireNextFrame(50, out frameInfo, out resourceObj);
                    return hr == 0 && resourceObj != null;
                }

                return false;
            }

            const int perAcquireMs = 100;
            const int presentBudgetMs = 600;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < presentBudgetMs)
            {
                int acquireHr = session.Duplication.AcquireNextFrame(perAcquireMs, out frameInfo, out resourceObj);
                if (acquireHr == DXGI_ERROR_ACCESS_LOST)
                {
                    DropSession(session.DeviceName);
                    return false;
                }

                if (acquireHr == 0)
                {
                    if (frameInfo.LastPresentTime != 0 || frameInfo.AccumulatedFrames > 0)
                    {
                        session.Warm = true;
                        return true;
                    }

                    Marshal.ReleaseComObject(resourceObj);
                    resourceObj = null;
                    session.Duplication.ReleaseFrame();
                    continue;
                }

                if (acquireHr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    continue;
                }

                break;
            }

            if (resourceObj == null)
            {
                Thread.Sleep(120);
                for (int i = 0; i < 5 && resourceObj == null; i++)
                {
                    int hr = session.Duplication.AcquireNextFrame(200, out frameInfo, out resourceObj);
                    if (hr == 0)
                    {
                        break;
                    }

                    resourceObj = null;
                }
            }

            if (resourceObj != null)
            {
                session.Warm = true;
                return true;
            }

            return false;
        }

        /// <summary>
        /// Captures an SDR output region using GDI BitBlt and draws it into the
        /// composite bitmap. Much faster than the full DXGI duplication pipeline
        /// for monitors that don't need HDR conversion.
        /// </summary>
        private bool CaptureOutputGDI(Rectangle intersection, Rectangle captureRect, Bitmap composite)
        {
            int dstX = intersection.X - captureRect.X;
            int dstY = intersection.Y - captureRect.Y;

            try
            {
                // Use GDI BitBlt to capture the SDR region. CaptureRectangleNative
                // uses virtual desktop coordinates, which is exactly what we have.
                using (Bitmap sdrCapture = CaptureRectangleNative(intersection, false))
                {
                    if (sdrCapture == null) return false;

                    using (Graphics g = Graphics.FromImage(composite))
                    {
                        g.DrawImageUnscaled(sdrCapture, dstX, dstY);
                    }
                }

                DebugHelper.WriteLine($"HDR: SDR output captured via GDI ({intersection.Width}x{intersection.Height})");
                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: GDI fallback for SDR output failed.");
                return false;
            }
        }

        private static unsafe void BlitPackedSlice(HdrCpuSlice slice, Bitmap composite, HdrTonemapCurve curve)
        {
            fixed (byte* packedPtr = slice.Packed)
            {
                BlitHdrRegion(packedPtr, slice.PackedStride, slice.Format, 0, 0, slice.CopyW, slice.CopyH,
                    slice.SdrWhiteNits, composite, slice.DstX, slice.DstY, curve);
            }
        }

        private static unsafe void BlitMapped(IntPtr data, int rowPitch, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits, Bitmap composite, int dstX, int dstY, HdrTonemapCurve curve)
        {
            BlitHdrRegion((byte*)data, rowPitch, format, srcX, srcY, copyW, copyH, sdrWhiteNits,
                composite, dstX, dstY, curve);
        }

        private static unsafe void BlitHdrRegion(byte* srcBase, int srcStride, int format, int srcX, int srcY,
            int copyW, int copyH, float sdrWhiteNits, Bitmap composite, int dstX, int dstY, HdrTonemapCurve curve)
        {
            copyW = Math.Min(copyW, composite.Width - dstX);
            copyH = Math.Min(copyH, composite.Height - dstY);
            if (copyW <= 0 || copyH <= 0)
            {
                return;
            }

            int bpp = HdrPixelConvert.BytesPerPixel(format);
            BitmapData bd = composite.LockBits(new Rectangle(dstX, dstY, copyW, copyH),
                ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                byte* dstBase = (byte*)bd.Scan0;
                int dstStride = bd.Stride;

                System.Threading.Tasks.Parallel.For(0, copyH, y =>
                {
                    byte* dst = dstBase + y * dstStride;
                    byte* srcRow = srcBase + (long)(srcY + y) * srcStride + (long)srcX * bpp;
                    int py = dstY + y;

                    for (int x = 0; x < copyW; x++)
                    {
                        HdrPixelConvert.DecodeToSdrNormalized(format, srcRow + x * bpp, sdrWhiteNits,
                            out float r, out float g, out float b);
                        curve.Map(ref r, ref g, ref b);
                        int px = dstX + x;
                        dst[x * 4 + 0] = curve.Encode(b, px, py);
                        dst[x * 4 + 1] = curve.Encode(g, px, py);
                        dst[x * 4 + 2] = curve.Encode(r, px, py);
                        dst[x * 4 + 3] = 255;
                    }
                });
            }
            finally
            {
                composite.UnlockBits(bd);
            }
        }

        public static void TonemapLinearScRgbFrameToBgr24(byte[] gbrpf32le, byte[] bgr24, int width, int height,
            bool applyNormalization = true, HdrTonemapMode tonemapMode = HdrTonemapMode.Desktop,
            float exposure = HdrTonemap.ExposureDefault)
        {
            float sdrWhiteNits = DisplayConfigHelper.GetSdrWhiteNits();
            float normScale = applyNormalization ? HdrPixelConvert.NormalizationScale(sdrWhiteNits) : 1f;
            int rowBytesIn = width * 12;
            int rowBytesOut = width * 3;

            long sampleCount = 0, aboveOne = 0, aboveOneHalf = 0, hotUpperSdr = 0;
            float maxLum = 0f;
            int[] hist = new int[HdrTonemap.HistogramSize];
            int step = Math.Max(1, width * height / 4096);

            for (int i = 0; i < width * height; i += step)
            {
                int si = i * 12;
                float g = BitConverter.ToSingle(gbrpf32le, si);
                float b = BitConverter.ToSingle(gbrpf32le, si + 4);
                float r = BitConverter.ToSingle(gbrpf32le, si + 8);
                r = Math.Max(r * normScale, 0f);
                g = Math.Max(g * normScale, 0f);
                b = Math.Max(b * normScale, 0f);
                HdrTonemap.AccumulateSample(r, g, b, ref sampleCount, ref aboveOne, ref aboveOneHalf,
                    ref hotUpperSdr, ref maxLum, hist);
            }

            HdrLuminanceStats stats = HdrTonemap.BuildStats(sampleCount, aboveOne, aboveOneHalf, hotUpperSdr, maxLum, hist);
            HdrTonemapMode resolvedMode = HdrTonemap.ResolveForRecording(tonemapMode, stats);
            HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, stats, exposure, sdrWhiteNits);

            System.Threading.Tasks.Parallel.For(0, height, y =>
            {
                int rowIn = y * rowBytesIn;
                int rowOut = y * rowBytesOut;

                for (int x = 0; x < width; x++)
                {
                    int si = rowIn + x * 12;
                    int di = rowOut + x * 3;

                    float g = BitConverter.ToSingle(gbrpf32le, si);
                    float b = BitConverter.ToSingle(gbrpf32le, si + 4);
                    float r = BitConverter.ToSingle(gbrpf32le, si + 8);

                    r = Math.Max(r * normScale, 0f);
                    g = Math.Max(g * normScale, 0f);
                    b = Math.Max(b * normScale, 0f);

                    curve.Map(ref r, ref g, ref b);

                    bgr24[di] = curve.Encode(b, x, y);
                    bgr24[di + 1] = curve.Encode(g, x, y);
                    bgr24[di + 2] = curve.Encode(r, x, y);
                }
            });
        }
    }
}
