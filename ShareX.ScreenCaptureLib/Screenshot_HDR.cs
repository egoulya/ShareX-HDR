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
using SharpGen.Runtime;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using DxgiFormat = Vortice.DXGI.Format;

namespace ShareX.ScreenCaptureLib
{
    public partial class Screenshot
    {
        public HdrCaptureMode HdrCaptureMode { get; set; } = HdrCaptureMode.Off;
        public bool CaptureHDREnabled { get; set; } = false;
        public HdrTonemapMode HdrTonemapMode { get; set; } = HdrTonemapMode.Auto;
        public float HdrExposure { get; set; } = HdrTonemap.ExposureDefault;

        /// <summary>
        /// When true, CaptureRectangleHDR also builds a PQ BT.2100 companion
        /// (<see cref="LastHdrMaster"/>). Clipboard / shareable output stay tonemapped SDR.
        /// </summary>
        public bool SaveHdrMasterPng { get; set; }

        /// <summary>
        /// Retain the PQ master so a gain map can be derived from it at save time. The master is the
        /// HDR layer the gain map needs, so this implies producing one even when it is not itself
        /// being saved.
        /// </summary>
        public bool SaveUltraHdrJpeg { get; set; }

        /// <summary>Whether a PQ master has to be produced for this capture, for either consumer.</summary>
        private bool NeedsHdrMaster => SaveHdrMasterPng || SaveUltraHdrJpeg;

        public HdrMasterImage LastHdrMaster { get; private set; }

        [ThreadStatic]
        private static HdrMasterImage PendingHdrMaster;

        public HdrMasterImage TakeLastHdrMaster()
        {
            HdrMasterImage master = LastHdrMaster;
            LastHdrMaster = null;
            if (ReferenceEquals(PendingHdrMaster, master))
            {
                PendingHdrMaster = null;
            }

            return master;
        }

        public static HdrMasterImage ConsumePendingHdrMaster()
        {
            HdrMasterImage master = PendingHdrMaster;
            PendingHdrMaster = null;
            return master;
        }

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
        private interface LegacyIDXGIDevice
        {
            // IDXGIObject methods (4 methods: SetPrivateData, SetPrivateDataInterface, GetPrivateData, GetParent)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyIDXGIDevice methods
            int GetAdapter(out LegacyIDXGIAdapter adapter);
        }

        [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface LegacyIDXGIAdapter
        {
            // IDXGIObject methods
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyIDXGIAdapter methods
            int EnumOutputs(uint index, out LegacyIDXGIOutput output);
        }

        [ComImport, Guid("ae02eedb-c735-4690-8d52-5a8dc20213aa"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface LegacyIDXGIOutput
        {
            // IDXGIObject (4 methods)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyIDXGIOutput methods (12 methods)
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
        private interface LegacyLegacyIDXGIOutput1
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyIDXGIOutput (12 methods)
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

            // LegacyLegacyIDXGIOutput1 (4 methods)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out LegacyLegacyIDXGIOutputDuplication duplication);
        }

        // LegacyLegacyIDXGIOutput5 inherits: LegacyIDXGIOutput4 -> LegacyIDXGIOutput3 -> LegacyIDXGIOutput2 -> LegacyLegacyIDXGIOutput1
        // LegacyIDXGIOutput2 adds: SupportsOverlays (1 method)
        // LegacyIDXGIOutput3 adds: CheckOverlaySupport (1 method)
        // LegacyIDXGIOutput4 adds: CheckOverlayColorSpaceSupport (1 method)
        // LegacyLegacyIDXGIOutput5 adds: DuplicateOutput1 (1 method)
        [ComImport, Guid("80A07424-AB52-42EB-833C-0C42FD282D98"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface LegacyLegacyIDXGIOutput5
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyIDXGIOutput (12)
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

            // LegacyLegacyIDXGIOutput1 (4)
            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out LegacyLegacyIDXGIOutputDuplication duplication);

            // LegacyIDXGIOutput2 (1)
            [PreserveSig] int SupportsOverlays();

            // LegacyIDXGIOutput3 (1)
            int CheckOverlaySupport(uint format, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // LegacyIDXGIOutput4 (1)
            int CheckOverlayColorSpaceSupport(uint format, uint colorSpace, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);

            // LegacyLegacyIDXGIOutput5 (1)
            int DuplicateOutput1([MarshalAs(UnmanagedType.IUnknown)] object device, uint flags,
                uint formatCount, [MarshalAs(UnmanagedType.LPArray)] int[] formats,
                out LegacyLegacyIDXGIOutputDuplication duplication);
        }

        [ComImport, Guid("068346e8-aaec-4b84-add7-137f513f77a1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface LegacyLegacyIDXGIOutput6
        {
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

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

            int GetDisplayModeList1(uint format, uint flags, ref uint numModes, IntPtr descs);
            int FindClosestMatchingMode1(IntPtr modeToMatch, IntPtr closestMatch, IntPtr device);
            int GetDisplaySurfaceData1([MarshalAs(UnmanagedType.IUnknown)] object surface);
            int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out LegacyLegacyIDXGIOutputDuplication duplication);

            [PreserveSig] int SupportsOverlays();
            int CheckOverlaySupport(uint format, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);
            int CheckOverlayColorSpaceSupport(uint format, uint colorSpace, [MarshalAs(UnmanagedType.IUnknown)] object device, out uint flags);
            int DuplicateOutput1([MarshalAs(UnmanagedType.IUnknown)] object device, uint flags,
                uint formatCount, [MarshalAs(UnmanagedType.LPArray)] int[] formats,
                out LegacyLegacyIDXGIOutputDuplication duplication);

            int GetDesc1(out DXGI_OUTPUT_DESC1 desc);
            int CheckHardwareCompositionSupport(out uint flags);
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC1
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
            public uint BitsPerColor;
            public uint ColorSpace;
            public float RedPrimaryX, RedPrimaryY;
            public float GreenPrimaryX, GreenPrimaryY;
            public float BluePrimaryX, BluePrimaryY;
            public float WhitePointX, WhitePointY;
            public float MinLuminance;
            public float MaxLuminance;
            public float MaxFullFrameLuminance;
        }

        [ComImport, Guid("191cfac3-a341-470d-b26e-a864f428319c"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface LegacyLegacyIDXGIOutputDuplication
        {
            // IDXGIObject (4)
            int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            int SetPrivateDataInterface(ref Guid name, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
            int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            int GetParent(ref Guid riid, out IntPtr parent);

            // LegacyLegacyIDXGIOutputDuplication methods
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
        private const int DXGI_ERROR_ACCESS_DENIED = unchecked((int)0x887A002B);
        private const int DXGI_ERROR_INVALID_CALL = unchecked((int)0x887A0001);

        /// <summary>
        /// True when a duplication can no longer be used and must be recreated. ACCESS_LOST covers a
        /// mode change or another client taking over; ACCESS_DENIED covers a protected or exclusive
        /// fullscreen surface; INVALID_CALL shows up when a frame is still held from a previous
        /// acquire. All three are permanent for this duplication object.
        /// </summary>
        private static bool IsDuplicationLost(Result result)
        {
            int code = (int)result.Code;
            return code == DXGI_ERROR_ACCESS_LOST ||
                code == DXGI_ERROR_ACCESS_DENIED ||
                code == DXGI_ERROR_INVALID_CALL;
        }
        private const int DXGI_ERROR_NOT_CURRENTLY_AVAILABLE = unchecked((int)0x887A0022);

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

        /// <summary>
        /// Override for the display's SDR white level in nits; zero uses what Windows reports. See
        /// <see cref="DisplayConfigHelper.PaperWhiteNitsOverride"/> for why it is applied at the probe
        /// rather than at the curve.
        /// </summary>
        public static float PaperWhiteNitsOverride
        {
            get => DisplayConfigHelper.PaperWhiteNitsOverride;
            set => DisplayConfigHelper.PaperWhiteNitsOverride = value;
        }

        public static float MinPaperWhiteNits => DisplayConfigHelper.MinPaperWhiteNits;

        public static float MaxPaperWhiteNits => DisplayConfigHelper.MaxPaperWhiteNits;

        /// <summary>What Windows reports for the SDR white level, ignoring any override.</summary>
        public static float GetProbedSdrWhiteLevelNits() => DisplayConfigHelper.GetProbedSdrWhiteNits();

        public static float GetSdrWhiteLevelNits(string deviceName) => DisplayConfigHelper.GetSdrWhiteNits(deviceName);

        public static float GetSdrWhiteNormalizationScale(string deviceName = null) =>
            HdrPixelConvert.NormalizationScale(DisplayConfigHelper.GetSdrWhiteNits(deviceName));

        public static void WarmHdrCapture() => EnsureDuplicationCache();

        /// <summary>
        /// DXGI allows only one Desktop Duplication per output. Recording needs its own
        /// duplication, so drop any screenshot-session hold first.
        /// </summary>
        public static void ReleaseHdrDuplication() => InvalidateDuplicationCache();

        /// <summary>
        /// Captures the specified rectangle in virtual desktop coordinates using
        /// DXGI Desktop Duplication. Handles multi-monitor setups by enumerating
        /// all outputs, capturing each intersecting monitor, and compositing the
        /// results onto a single bitmap.
        /// </summary>
        public Bitmap CaptureRectangleHDR(Rectangle rect)
        {
            CaptureHdrGate.Wait();
            try
            {
                int dropsBefore = System.Threading.Volatile.Read(ref SessionDropCount);
                Bitmap result = CaptureRectangleHDRCore(rect);

                // A duplication lost mid-capture (alt-tab, exclusive fullscreen, a mode change) is
                // ordinary rather than exceptional, and the session has just been dropped. Rebuilding
                // it costs one cold acquire, so recover inside this capture instead of handing back
                // nothing and making the user press the key again.
                if (result == null &&
                    System.Threading.Volatile.Read(ref SessionDropCount) != dropsBefore)
                {
                    DebugHelper.WriteLine("HDR: a duplication was lost mid-capture; retrying once with a fresh session.");
                    result = CaptureRectangleHDRCore(rect);
                }

                return result;
            }
            finally
            {
                CaptureHdrGate.Release();
            }
        }

        private Bitmap CaptureRectangleHDRCore(Rectangle rect)
        {
            EnsureDuplicationCache();
            LastHdrMaster = null;
            PendingHdrMaster = null;

            try
            {
                if (!TryEnsureSharedVorticeDevice(out ID3D11Device vorticeDevice, out ID3D11DeviceContext vorticeContext))
                {
                    return null;
                }

                IntPtr devicePtr = vorticeDevice.NativePointer;
                IntPtr contextPtr = vorticeContext.NativePointer;

                Bitmap result = new Bitmap(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
                HdrMasterImage master = NeedsHdrMaster ? new HdrMasterImage(rect.Width, rect.Height) : null;
                HdrMasteringDisplay bestMastering = default;
                int bestMasteringArea = 0;
                bool anyOutputCaptured = false;
                List<HdrPendingOutput> pendingHdr = new List<HdrPendingOutput>();

                using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();

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
                                try
                                {
                                    OutputDescription outputDesc = output.Description;
                                    Rectangle monitorRect = Rectangle.FromLTRB(
                                        outputDesc.DesktopCoordinates.Left,
                                        outputDesc.DesktopCoordinates.Top,
                                        outputDesc.DesktopCoordinates.Right,
                                        outputDesc.DesktopCoordinates.Bottom);

                                    Rectangle intersection = Rectangle.Intersect(rect, monitorRect);
                                    if (intersection.Width <= 0 || intersection.Height <= 0)
                                    {
                                        continue;
                                    }

                                    string deviceName = outputDesc.DeviceName;
                                    DebugHelper.WriteLine($"HDR: Output {outputIdx} ({deviceName}) {monitorRect}, intersection={intersection}");

                                    // Raw-frame dumps record the display's colour space and mastering
                                    // block regardless of the companion settings, so probe when any consumer
                                    // is active. Capture is display-referred: a corpus frame without this
                                    // metadata cannot be replayed faithfully.
                                    HdrMasteringDisplay mastering = default;
                                    uint outputColorSpace = HdrDisplayProbe.ColorSpaceSdr;
                                    if (master != null || HdrFrameDump.Enabled)
                                    {
                                        bool gotMastering = TryGetMasteringDisplay(output, out mastering, out outputColorSpace);
                                        if (gotMastering && master != null)
                                        {
                                            int area = intersection.Width * intersection.Height;
                                            if (area > bestMasteringArea)
                                            {
                                                bestMasteringArea = area;
                                                bestMastering = mastering;
                                            }
                                        }
                                    }

                                    HdrDuplSession session = GetOrCreateSession(deviceName, output, vorticeDevice, vorticeContext);
                                    if (session == null || session.Format == DXGI_FORMAT_B8G8R8A8_UNORM)
                                    {
                                        DebugHelper.WriteLine($"HDR: Output {outputIdx} is SDR, using GDI fast path.");
                                        if (CaptureOutputGDI(intersection, rect, result, master, DisplayConfigHelper.GetSdrWhiteNits(deviceName)))
                                        {
                                            anyOutputCaptured = true;
                                        }

                                        continue;
                                    }

                                    float sdrWhiteNits = DisplayConfigHelper.GetSdrWhiteNits(deviceName);
                                    DebugHelper.WriteLine($"HDR: Output {outputIdx} HDR format={session.Format}, sdrWhite={sdrWhiteNits:0.#} nits, warm={session.Warm}");

                                    if (HdrTonemapMode == HdrTonemapMode.WindowsWIC)
                                    {
                                        if (CaptureOutputHdrWic(session, monitorRect, rect, intersection, result, master,
                                            sdrWhiteNits, outputColorSpace, mastering))
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
                                        SdrWhiteNits = sdrWhiteNits,
                                        ColorSpace = outputColorSpace,
                                        Mastering = mastering
                                    });
                                }
                                catch (Exception e)
                                {
                                    DebugHelper.WriteException(e, $"HDR: Output {outputIdx} capture failed, continuing.");
                                }
                            }
                        }
                    }
                }

                if (pendingHdr.Count == 1)
                {
                    if (CaptureOutputHdrDirect(pendingHdr[0], rect, result, master))
                    {
                        anyOutputCaptured = true;
                    }
                }
                else if (pendingHdr.Count > 1)
                {
                    List<HdrCpuSlice> hdrSlices = new List<HdrCpuSlice>(pendingHdr.Count);
                    HdrLuminanceAccumulator tonemapStatsAcc = new HdrLuminanceAccumulator();
                    foreach (HdrPendingOutput pending in pendingHdr)
                    {
                        HdrCpuSlice slice = CaptureOutputHdrSlice(pending, rect, tonemapStatsAcc);
                        if (slice != null)
                        {
                            hdrSlices.Add(slice);
                        }
                    }

                    if (hdrSlices.Count > 0)
                    {
                        double whiteAcc = 0, areaAcc = 0;
                        foreach (HdrCpuSlice slice in hdrSlices)
                        {
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

                        HdrLuminanceStats merged = tonemapStatsAcc.Build();
                        string hysteresisKey = "span";
                        HdrTonemapMode resolvedMode = HdrTonemap.ResolveMode(HdrTonemapMode, merged, hysteresisKey, hdrDxgiCapture: true);
                        HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, merged, HdrExposure, curveWhiteNits);
                        DebugHelper.WriteLine($"HDR: merged tonemap {HdrTonemapMode} -> {resolvedMode} (stats=full output(s), P99={merged.P99Estimate:0.00}, max={merged.MaxLuminance:0.00}, curveWhite={curveWhiteNits:0.#})");

                        foreach (HdrCpuSlice slice in hdrSlices)
                        {
                            BlitPackedSlice(slice, result, curve, master);
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

                LastHdrMaster = master;
                if (master != null)
                {
                    master.MasteringDisplay = bestMastering;
                }
                PendingHdrMaster = master;
                DebugHelper.WriteLine($"HDR: Composite output {result.Width}x{result.Height}" +
                    (master != null ? $", HDR master MaxCLL={master.MaxCLL:0.#}" : ""));
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
            public uint ColorSpace;
            public HdrMasteringDisplay Mastering;
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

        private sealed class HdrDuplSession
        {
            public string DeviceName;
            public int Format;
            public bool Warm;
            public Vortice.DXGI.IDXGIOutputDuplication VorticeDuplication;
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
        private static readonly SemaphoreSlim CaptureHdrGate = new(1, 1);
        private static IntPtr SharedDevicePtr;
        private static IntPtr SharedContextPtr;
        private static ID3D11Device SharedVorticeDevice;
        private static ID3D11DeviceContext SharedVorticeContext;
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
                    TryEnsureSharedVorticeDevice(out ID3D11Device _, out ID3D11DeviceContext _);
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
            SharedVorticeContext?.Dispose();
            SharedVorticeContext = null;
            SharedVorticeDevice?.Dispose();
            SharedVorticeDevice = null;
            SharedContextPtr = IntPtr.Zero;
            SharedDevicePtr = IntPtr.Zero;
        }

        private static bool TryEnsureSharedVorticeDevice(out ID3D11Device device, out ID3D11DeviceContext context)
        {
            lock (SessionLock)
            {
                if (SharedVorticeDevice != null)
                {
                    device = SharedVorticeDevice;
                    context = SharedVorticeContext;
                    return true;
                }

                FeatureLevel[] levels = { FeatureLevel.Level_11_1, FeatureLevel.Level_11_0 };
                Result result = D3D11.D3D11CreateDevice(null, DriverType.Hardware, DeviceCreationFlags.BgraSupport,
                    levels, out device, out context);
                if (result.Failure || device == null || context == null)
                {
                    DebugHelper.WriteLine($"HDR: D3D11CreateDevice failed {result.Code}");
                    device = null;
                    context = null;
                    return false;
                }

                SharedVorticeDevice = device;
                SharedVorticeContext = context;
                SharedDevicePtr = device.NativePointer;
                SharedContextPtr = context.NativePointer;
                return true;
            }
        }

        private static bool TryGetSharedDevice(out IntPtr devicePtr, out IntPtr contextPtr, out object deviceUnk)
        {
            deviceUnk = null;
            if (!TryEnsureSharedVorticeDevice(out _, out _))
            {
                devicePtr = IntPtr.Zero;
                contextPtr = IntPtr.Zero;
                return false;
            }

            devicePtr = SharedDevicePtr;
            contextPtr = SharedContextPtr;
            return true;
        }

        private static HdrDuplSession GetOrCreateSession(string deviceName, Vortice.DXGI.IDXGIOutput output, ID3D11Device device, ID3D11DeviceContext context)
        {
            IntPtr devicePtr = device.NativePointer;
            IntPtr contextPtr = context.NativePointer;

            lock (SessionLock)
            {
                if (!string.IsNullOrEmpty(deviceName) && Sessions.TryGetValue(deviceName, out HdrDuplSession existing) &&
                    existing.VorticeDuplication != null)
                {
                    return existing;
                }
            }

            if (!TryCreateDuplication(output, device, out Vortice.DXGI.IDXGIOutputDuplication duplication, out int format))
            {
                return null;
            }

            HdrDuplSession session = new HdrDuplSession
            {
                DeviceName = deviceName,
                Format = format,
                VorticeDuplication = duplication,
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
                    if (Sessions.TryGetValue(deviceName, out HdrDuplSession raced) && raced.VorticeDuplication != null)
                    {
                        DisposeSession(session);
                        return raced;
                    }

                    Sessions[deviceName] = session;
                }
            }

            return session;
        }

        private static bool TryCreateDuplication(Vortice.DXGI.IDXGIOutput output, ID3D11Device device,
            out Vortice.DXGI.IDXGIOutputDuplication duplication, out int format)
        {
            duplication = null;
            format = DXGI_FORMAT_B8G8R8A8_UNORM;

            try
            {
                using Vortice.DXGI.IDXGIOutput5 output5 = output.QueryInterface<Vortice.DXGI.IDXGIOutput5>();
                DxgiFormat[] hdrFormats =
                {
                    DxgiFormat.R16G16B16A16_Float,
                    DxgiFormat.R10G10B10A2_UNorm
                };
                duplication = output5.DuplicateOutput1(device, hdrFormats);
                if (duplication != null)
                {
                    format = (int)duplication.Description.ModeDescription.Format;
                    if (IsHdrDuplicationFormat(format))
                    {
                        return true;
                    }

                    duplication.Dispose();
                    duplication = null;
                    DebugHelper.WriteLine(
                        $"HDR: DuplicateOutput1 returned SDR format {format}; retrying without BGRA in the format list did not help.");
                }
            }
            catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
            {
                DebugHelper.WriteLine(
                    "HDR: DuplicateOutput1 returned DXGI_ERROR_NOT_CURRENTLY_AVAILABLE — another client " +
                    "(often ShareX HDR recording) already holds this output. Screenshot will fall back to GDI.");
                return false;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "HDR: DuplicateOutput1 failed, trying legacy DuplicateOutput.");
            }

            try
            {
                using Vortice.DXGI.IDXGIOutput1 output1 = output.QueryInterface<Vortice.DXGI.IDXGIOutput1>();
                duplication = output1.DuplicateOutput(device);
                if (duplication == null)
                {
                    return false;
                }

                format = (int)duplication.Description.ModeDescription.Format;
                if (IsHdrDuplicationFormat(format))
                {
                    return true;
                }

                duplication.Dispose();
                duplication = null;
                DebugHelper.WriteLine($"HDR: DuplicateOutput returned SDR format {format}; HDR pixel path unavailable.");
                return false;
            }
            catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_NOT_CURRENTLY_AVAILABLE)
            {
                DebugHelper.WriteLine(
                    "HDR: DuplicateOutput returned DXGI_ERROR_NOT_CURRENTLY_AVAILABLE — another client " +
                    "(often ShareX HDR recording) already holds this output. Screenshot will fall back to GDI.");
                return false;
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "HDR: DuplicateOutput failed.");
                return false;
            }
        }

        private static bool IsHdrDuplicationFormat(int format) =>
            format == DXGI_FORMAT_R16G16B16A16_FLOAT || format == DXGI_FORMAT_R10G10B10A2_UNORM;

        private static bool TryGetMasteringDisplay(Vortice.DXGI.IDXGIOutput output, out HdrMasteringDisplay mastering)
        {
            return TryGetMasteringDisplay(output, out mastering, out _);
        }

        private static bool TryGetMasteringDisplay(Vortice.DXGI.IDXGIOutput output,
            out HdrMasteringDisplay mastering, out uint colorSpace)
        {
            mastering = default;
            colorSpace = HdrDisplayProbe.ColorSpaceSdr;
            try
            {
                using Vortice.DXGI.IDXGIOutput6 output6 = output.QueryInterface<Vortice.DXGI.IDXGIOutput6>();
                OutputDescription1 desc = output6.Description1;
                colorSpace = (uint)desc.ColorSpace;
                mastering = new HdrMasteringDisplay
                {
                    RedX = desc.RedPrimary[0],
                    RedY = desc.RedPrimary[1],
                    GreenX = desc.GreenPrimary[0],
                    GreenY = desc.GreenPrimary[1],
                    BlueX = desc.BluePrimary[0],
                    BlueY = desc.BluePrimary[1],
                    WhiteX = desc.WhitePoint[0],
                    WhiteY = desc.WhitePoint[1],
                    MinLuminanceNits = desc.MinLuminance,
                    MaxLuminanceNits = desc.MaxLuminance,
                    HasValue = desc.MaxLuminance > 0
                };
                return mastering.HasValue;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR: mastering display metadata unavailable.");
                return false;
            }
        }

        private static int SessionDropCount;

        private static void DropSession(string deviceName)
        {
            System.Threading.Interlocked.Increment(ref SessionDropCount);

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

            if (session.VorticeDuplication != null)
            {
                try { session.VorticeDuplication.ReleaseFrame(); } catch { }
                try { session.VorticeDuplication.Dispose(); } catch { }
                session.VorticeDuplication = null;
            }

            if (session.Staging != IntPtr.Zero)
            {
                Marshal.Release(session.Staging);
                session.Staging = IntPtr.Zero;
            }

            session.PackedBuffer = null;
        }

        private static bool CaptureOutputHdrWic(HdrDuplSession session, Rectangle monitorRect, Rectangle captureRect,
            Rectangle intersection, Bitmap composite, HdrMasterImage master, float sdrWhiteNits,
            uint colorSpace, in HdrMasteringDisplay mastering)
        {
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return false;
            }

            try
            {
                DumpRawFrame(mapped, session, texW, texH, monitorRect, captureRect, sdrWhiteNits, colorSpace, mastering);

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

                bool ok = HdrWicTonemap.TryBlitToBitmap(mapped.pData, (int)mapped.RowPitch, texW, texH, session.Format,
                    srcX, srcY, copyW, copyH, composite, dstX, dstY);
                if (ok && master != null)
                {
                    HdrFrameTonemapper.FillMasterFromMapped(mapped.pData, (int)mapped.RowPitch, session.Format,
                        srcX, srcY, copyW, copyH, dstX, dstY, sdrWhiteNits, master);
                }

                return ok;
            }
            finally
            {
                session.Unmap(session.Context, session.Staging, 0);
                try { session.VorticeDuplication?.ReleaseFrame(); } catch { }
            }
        }

        private bool CaptureOutputHdrDirect(HdrPendingOutput pending, Rectangle captureRect, Bitmap composite, HdrMasterImage master)
        {
            HdrDuplSession session = pending.Session;
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return false;
            }

            try
            {
                DumpRawFrame(mapped, session, texW, texH, pending.MonitorRect, captureRect,
                    pending.SdrWhiteNits, pending.ColorSpace, pending.Mastering);

                if (!TryGetCopyRect(pending, captureRect, texW, texH,
                    out int srcX, out int srcY, out int copyW, out int copyH, out int dstX, out int dstY))
                {
                    return false;
                }

                HdrLuminanceAccumulator acc = new HdrLuminanceAccumulator();
                acc.AddFromMapped(mapped.pData, (int)mapped.RowPitch, session.Format, 0, 0, texW, texH,
                    pending.SdrWhiteNits);
                HdrLuminanceStats stats = acc.Build();
                HdrTonemapMode resolvedMode = HdrTonemap.ResolveMode(HdrTonemapMode, stats, session.DeviceName, hdrDxgiCapture: true);
                HdrTonemapCurve curve = HdrTonemap.CreateCurve(resolvedMode, stats, HdrExposure, pending.SdrWhiteNits);
                DebugHelper.WriteLine($"HDR: direct tonemap {HdrTonemapMode} -> {resolvedMode} (stats=full output {texW}x{texH}, P99={stats.P99Estimate:0.00}, max={stats.MaxLuminance:0.00}, sdrWhite={pending.SdrWhiteNits:0.#})");

                HdrFrameTonemapper.BlitMapped(mapped.pData, (int)mapped.RowPitch, session.Format, srcX, srcY, copyW, copyH,
                    pending.SdrWhiteNits, composite, dstX, dstY, curve, master);
                return true;
            }
            finally
            {
                session.Unmap(session.Context, session.Staging, 0);
                try { session.VorticeDuplication?.ReleaseFrame(); } catch { }
            }
        }

        private static HdrCpuSlice CaptureOutputHdrSlice(HdrPendingOutput pending, Rectangle captureRect,
            HdrLuminanceAccumulator tonemapStatsAcc = null)
        {
            HdrDuplSession session = pending.Session;
            if (!TryAcquireMapped(session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH))
            {
                return null;
            }

            try
            {
                DumpRawFrame(mapped, session, texW, texH, pending.MonitorRect, captureRect,
                    pending.SdrWhiteNits, pending.ColorSpace, pending.Mastering);

                tonemapStatsAcc?.AddFromMapped(mapped.pData, (int)mapped.RowPitch, session.Format, 0, 0, texW, texH,
                    pending.SdrWhiteNits);

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
                try { session.VorticeDuplication?.ReleaseFrame(); } catch { }
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

        /// <summary>
        /// Records the acquired frame to the raw-frame corpus when <see cref="HdrFrameDump"/> is
        /// armed. No-op otherwise, and never able to fail a capture.
        /// </summary>
        private static void DumpRawFrame(in D3D11_MAPPED_SUBRESOURCE mapped, HdrDuplSession session,
            int texW, int texH, Rectangle monitorRect, Rectangle captureRect, float sdrWhiteNits,
            uint colorSpace, in HdrMasteringDisplay mastering)
        {
            if (!HdrFrameDump.Enabled)
            {
                return;
            }

            HdrFrameMetadata template = HdrFrameDump.CreateTemplate(session.DeviceName, sdrWhiteNits,
                colorSpace, monitorRect, captureRect, mastering);

            // The full output is dumped rather than the requested crop: Auto-mode stats are
            // computed over the whole output, so a corpus frame must carry it for offline replay
            // to reach the same decision.
            HdrFrameDump.TryDump(mapped.pData, (int)mapped.RowPitch, session.Format, 0, 0, texW, texH, template);
        }

        private static bool TryAcquireMapped(HdrDuplSession session, out D3D11_MAPPED_SUBRESOURCE mapped, out int texW, out int texH)
        {
            mapped = default;
            texW = texH = 0;

            if (!TryAcquireDesktopResource(session, out IDXGIResource desktopResource))
            {
                return false;
            }

            IntPtr resourcePtr = desktopResource.NativePointer;
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
                desktopResource.Dispose();
            }
        }

        private static bool TryAcquireDesktopResource(HdrDuplSession session, out IDXGIResource desktopResource)
        {
            desktopResource = null;
            Vortice.DXGI.IDXGIOutputDuplication duplication = session.VorticeDuplication;
            if (duplication == null)
            {
                return false;
            }

            if (session.Warm)
            {
                try
                {
                    Result quick = duplication.AcquireNextFrame(16, out OutduplFrameInfo frameInfo, out desktopResource);
                    if (quick.Success && desktopResource != null)
                    {
                        return true;
                    }

                    // AcquireNextFrame reports a lost duplication by returning the code as often as
                    // by throwing, and only the cold path used to check for that. A warm session that
                    // hit it therefore stayed warm and broken: every later capture failed instantly
                    // against the same dead duplication until the app restarted. Going fullscreen in
                    // a game is one of the ordinary ways to trigger it.
                    if (IsDuplicationLost(quick))
                    {
                        DebugHelper.WriteLine($"HDR: warm duplication lost on {session.DeviceName} " +
                            $"(0x{(uint)quick.Code:X8}); dropping the session so it is rebuilt.");
                        desktopResource?.Dispose();
                        desktopResource = null;
                        DropSession(session.DeviceName);
                        return false;
                    }
                }
                catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
                {
                    DropSession(session.DeviceName);
                    return false;
                }

                desktopResource?.Dispose();
                desktopResource = null;

                try
                {
                    Result retry = duplication.AcquireNextFrame(50, out _, out desktopResource);
                    if (retry.Success && desktopResource != null)
                    {
                        return true;
                    }

                    if (IsDuplicationLost(retry))
                    {
                        DebugHelper.WriteLine($"HDR: warm duplication lost on {session.DeviceName} " +
                            $"(0x{(uint)retry.Code:X8}); dropping the session so it is rebuilt.");
                        desktopResource?.Dispose();
                        desktopResource = null;
                        DropSession(session.DeviceName);
                        return false;
                    }

                    DebugHelper.WriteLine($"HDR: warm acquire on {session.DeviceName} returned " +
                        $"0x{(uint)retry.Code:X8} with no frame.");
                    return false;
                }
                catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
                {
                    DropSession(session.DeviceName);
                    return false;
                }
            }

            const int perAcquireMs = 100;
            const int presentBudgetMs = 600;
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < presentBudgetMs)
            {
                try
                {
                    Result acquireResult = duplication.AcquireNextFrame((uint)perAcquireMs, out OutduplFrameInfo frameInfo, out desktopResource);
                    if ((int)acquireResult.Code == DXGI_ERROR_ACCESS_LOST)
                    {
                        DropSession(session.DeviceName);
                        return false;
                    }

                    if (acquireResult.Success)
                    {
                        if (frameInfo.LastPresentTime != 0 || frameInfo.AccumulatedFrames > 0)
                        {
                            session.Warm = true;
                            return true;
                        }

                        desktopResource?.Dispose();
                        desktopResource = null;
                        duplication.ReleaseFrame();
                        continue;
                    }

                    if (acquireResult.Code == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        continue;
                    }
                }
                catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
                {
                    DropSession(session.DeviceName);
                    return false;
                }

                break;
            }

            if (desktopResource == null)
            {
                Thread.Sleep(120);
                for (int i = 0; i < 5 && desktopResource == null; i++)
                {
                    try
                    {
                        if (duplication.AcquireNextFrame(200, out _, out desktopResource).Success && desktopResource != null)
                        {
                            break;
                        }
                    }
                    catch (SharpGenException ex) when (ex.HResult == DXGI_ERROR_ACCESS_LOST)
                    {
                        DropSession(session.DeviceName);
                        return false;
                    }

                    desktopResource = null;
                }
            }

            if (desktopResource != null)
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
        private bool CaptureOutputGDI(Rectangle intersection, Rectangle captureRect, Bitmap composite,
            HdrMasterImage master = null, float sdrWhiteNits = HdrPixelConvert.SceneReferredWhiteNits)
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

                    master?.WriteFromSdrBitmap(sdrCapture, dstX, dstY, sdrWhiteNits);
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

        private static unsafe void BlitPackedSlice(HdrCpuSlice slice, Bitmap composite, HdrTonemapCurve curve, HdrMasterImage master)
        {
            fixed (byte* packedPtr = slice.Packed)
            {
                HdrFrameTonemapper.BlitRegion(packedPtr, slice.PackedStride, slice.Format, 0, 0, slice.CopyW, slice.CopyH,
                    slice.SdrWhiteNits, composite, slice.DstX, slice.DstY, curve, master);
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
            // 4096 samples badly underestimates the peak - it is an extremum, so a sparse grid just
            // misses the bright pixels, and everything above the estimate is then clamped flat by the
            // tone curve. Screenshots scan every pixel (see HdrLuminanceAccumulator); recording
            // cannot afford that per frame, so it samples 16x denser instead, which recovers most of
            // the peak for a still-negligible cost.
            int step = Math.Max(1, width * height / 65536);

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
