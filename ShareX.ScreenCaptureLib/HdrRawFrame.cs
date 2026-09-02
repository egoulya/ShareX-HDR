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

using Newtonsoft.Json;
using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Which of the three HDR capture scenarios a corpus frame belongs to. Evaluation
    /// thresholds differ per scenario: Desktop has a byte-exact ground truth, AutoHdr has a
    /// derivable one (the same scene captured with Auto-HDR off), NativeHdr has none.
    /// </summary>
    public enum HdrCorpusScenario
    {
        Unknown = 0,
        Desktop = 1,
        AutoHdr = 2,
        NativeHdr = 3
    }

    /// <summary>
    /// Serializable rectangle. <see cref="Rectangle"/> round-trips poorly through JSON
    /// (Location/Size/IsEmpty), so corpus metadata stores plain integers.
    /// </summary>
    public struct HdrFrameRect
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }

        public HdrFrameRect(int x, int y, int width, int height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        public static HdrFrameRect From(Rectangle r) => new HdrFrameRect(r.X, r.Y, r.Width, r.Height);

        public Rectangle ToRectangle() => new Rectangle(X, Y, Width, Height);

        public override string ToString() => X + "," + Y + " " + Width + "x" + Height;
    }

    /// <summary>
    /// Everything needed to replay a captured HDR frame through the tonemap pipeline offline
    /// and to interpret the result. Capture is display-referred, so a frame without the
    /// display's SDR white level and luminance range is not reproducible.
    /// </summary>
    public sealed class HdrFrameMetadata
    {
        public int FormatVersion { get; set; } = HdrRawFrame.CurrentFormatVersion;

        public int Width { get; set; }
        public int Height { get; set; }

        /// <summary>Bytes per row in the payload. Always tightly packed (Width * bytes-per-pixel).</summary>
        public int Stride { get; set; }

        /// <summary>DXGI_FORMAT of the payload: 10 = R16G16B16A16_FLOAT (scRGB), 24 = R10G10B10A2_UNORM (HDR10).</summary>
        public int DxgiFormat { get; set; }

        /// <summary>The display's SDR white level in nits. 1.0 after decode == this luminance.</summary>
        public float SdrWhiteNits { get; set; }

        /// <summary>DXGI_COLOR_SPACE_TYPE of the source output (12 = HDR10, 16 = scRGB).</summary>
        public uint ColorSpace { get; set; }

        public float MonitorMinLuminanceNits { get; set; }
        public float MonitorMaxLuminanceNits { get; set; }
        public float MonitorMaxFullFrameLuminanceNits { get; set; }

        public float RedPrimaryX { get; set; }
        public float RedPrimaryY { get; set; }
        public float GreenPrimaryX { get; set; }
        public float GreenPrimaryY { get; set; }
        public float BluePrimaryX { get; set; }
        public float BluePrimaryY { get; set; }
        public float WhitePointX { get; set; }
        public float WhitePointY { get; set; }
        public bool HasMasteringDisplay { get; set; }

        public string DeviceName { get; set; }

        /// <summary>
        /// Written as a name rather than an ordinal: frame headers and the corpus manifest are meant
        /// to be readable by humans and other tooling. StringEnumConverter still accepts integers, so
        /// frames written before this change continue to load.
        /// </summary>
        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public HdrCorpusScenario Scenario { get; set; }

        /// <summary>Short human label, e.g. "cyberpunk-nightcity-neon" or "explorer-dark-theme".</summary>
        public string Label { get; set; }

        /// <summary>Free-form notes: game, settings, what the frame is meant to stress.</summary>
        public string Notes { get; set; }

        public DateTime CapturedUtc { get; set; }

        /// <summary>The full monitor rect in virtual desktop coordinates.</summary>
        public HdrFrameRect MonitorRect { get; set; }

        /// <summary>The rect the user asked ShareX to capture.</summary>
        public HdrFrameRect CaptureRect { get; set; }

        /// <summary>The region of the source texture this payload holds.</summary>
        public HdrFrameRect SourceRegion { get; set; }

        public bool Compressed { get; set; }

        /// <summary>Uncompressed payload length in bytes.</summary>
        public int PayloadBytes { get; set; }

        /// <summary>SHA-256 of the uncompressed payload, lowercase hex. The frame's identity in a manifest.</summary>
        public string PayloadSha256 { get; set; }

        public HdrMasteringDisplay ToMasteringDisplay() => new HdrMasteringDisplay
        {
            RedX = RedPrimaryX,
            RedY = RedPrimaryY,
            GreenX = GreenPrimaryX,
            GreenY = GreenPrimaryY,
            BlueX = BluePrimaryX,
            BlueY = BluePrimaryY,
            WhiteX = WhitePointX,
            WhiteY = WhitePointY,
            MinLuminanceNits = MonitorMinLuminanceNits,
            MaxLuminanceNits = MonitorMaxLuminanceNits,
            HasValue = HasMasteringDisplay
        };

        public void SetMasteringDisplay(in HdrMasteringDisplay mastering)
        {
            RedPrimaryX = mastering.RedX;
            RedPrimaryY = mastering.RedY;
            GreenPrimaryX = mastering.GreenX;
            GreenPrimaryY = mastering.GreenY;
            BluePrimaryX = mastering.BlueX;
            BluePrimaryY = mastering.BlueY;
            WhitePointX = mastering.WhiteX;
            WhitePointY = mastering.WhiteY;
            HasMasteringDisplay = mastering.HasValue;

            if (mastering.HasValue)
            {
                MonitorMinLuminanceNits = mastering.MinLuminanceNits;
                MonitorMaxLuminanceNits = mastering.MaxLuminanceNits;
            }
        }

        public HdrFrameMetadata Clone() =>
            JsonConvert.DeserializeObject<HdrFrameMetadata>(JsonConvert.SerializeObject(this));
    }

    /// <summary>
    /// A byte-exact recording of one HDR frame as the capture pipeline saw it, plus the display
    /// metadata needed to replay it.
    ///
    /// The payload deliberately holds the raw DXGI buffer rather than the PQ
    /// <see cref="HdrMasterImage"/>: the master has already been through
    /// <c>EncodeToPqBt2020</c>, which is lossy and is itself under test. Storing the DXGI
    /// bytes keeps the whole pipeline (decode, stats, curve, encode) inside the test boundary.
    ///
    /// File layout:
    ///   0    : 8 bytes  magic "SHXHDRF" + format version byte
    ///   8    : int32    UTF-8 JSON header length (little endian)
    ///   12   : n bytes  JSON <see cref="HdrFrameMetadata"/>
    ///   12+n : rest     payload, raw or deflated per <see cref="HdrFrameMetadata.Compressed"/>
    /// </summary>
    public sealed class HdrRawFrame
    {
        public const int CurrentFormatVersion = 1;
        public const string FileExtension = ".hdrframe";

        /// <summary>DXGI_FORMAT_R16G16B16A16_FLOAT: scRGB half-float, 8 bytes per pixel.</summary>
        public const int FormatScRgbFp16 = 10;

        /// <summary>DXGI_FORMAT_R10G10B10A2_UNORM: HDR10 PQ in BT.2020, 4 bytes per pixel.</summary>
        public const int FormatHdr10 = 24;

        /// <summary>scRGB's scene-referred white: 1.0 in an fp16 buffer is this many nits.</summary>
        public const float SceneReferredWhiteNits = 80f;

        public static int BytesPerPixel(int dxgiFormat) => dxgiFormat == FormatScRgbFp16 ? 8 : 4;

        public static string FormatName(int dxgiFormat) => dxgiFormat switch
        {
            FormatScRgbFp16 => "scRGB fp16",
            FormatHdr10 => "HDR10 PQ",
            _ => "format " + dxgiFormat.ToString(System.Globalization.CultureInfo.InvariantCulture)
        };

        private static readonly byte[] MagicPrefix = Encoding.ASCII.GetBytes("SHXHDRF");
        private const int MagicLength = 8;
        private const int MaxHeaderBytes = 16 * 1024 * 1024;

        public HdrFrameMetadata Metadata { get; }

        /// <summary>Tightly packed pixels in <see cref="HdrFrameMetadata.DxgiFormat"/>.</summary>
        public byte[] Pixels { get; }

        public int Width => Metadata.Width;
        public int Height => Metadata.Height;
        public int Stride => Metadata.Stride;
        public int DxgiFormat => Metadata.DxgiFormat;
        public float SdrWhiteNits => Metadata.SdrWhiteNits;

        public HdrRawFrame(HdrFrameMetadata metadata, byte[] pixels)
        {
            Metadata = metadata ?? throw new ArgumentNullException(nameof(metadata));
            Pixels = pixels ?? throw new ArgumentNullException(nameof(pixels));
        }

        /// <summary>
        /// Copies a region out of a mapped DXGI staging texture into a tightly packed frame.
        /// <paramref name="template"/> supplies display metadata; geometry fields are overwritten.
        /// </summary>
        public static unsafe HdrRawFrame FromMappedRegion(IntPtr data, int rowPitch, int dxgiFormat,
            int srcX, int srcY, int width, int height, HdrFrameMetadata template)
        {
            if (data == IntPtr.Zero)
            {
                throw new ArgumentNullException(nameof(data));
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width),
                    "Region must be non-empty, got " + width + "x" + height + ".");
            }

            int bpp = HdrPixelConvert.BytesPerPixel(dxgiFormat);
            int stride = checked(width * bpp);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(checked(stride * height));

            byte* srcBase = (byte*)data;
            fixed (byte* dstBase = pixels)
            {
                for (int y = 0; y < height; y++)
                {
                    byte* src = srcBase + (long)(srcY + y) * rowPitch + (long)srcX * bpp;
                    Buffer.MemoryCopy(src, dstBase + (long)y * stride, stride, stride);
                }
            }

            HdrFrameMetadata metadata = template?.Clone() ?? new HdrFrameMetadata();
            metadata.FormatVersion = CurrentFormatVersion;
            metadata.Width = width;
            metadata.Height = height;
            metadata.Stride = stride;
            metadata.DxgiFormat = dxgiFormat;
            metadata.SourceRegion = new HdrFrameRect(srcX, srcY, width, height);
            metadata.PayloadBytes = pixels.Length;

            if (metadata.CapturedUtc == default)
            {
                metadata.CapturedUtc = DateTime.UtcNow;
            }

            return new HdrRawFrame(metadata, pixels);
        }

        public static void Write(string path, HdrRawFrame frame, bool compress = true)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                System.IO.Directory.CreateDirectory(directory);
            }

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None);
            Write(fs, frame, compress);
        }

        public static void Write(Stream stream, HdrRawFrame frame, bool compress = true)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            HdrFrameMetadata metadata = frame.Metadata;
            metadata.FormatVersion = CurrentFormatVersion;
            metadata.PayloadBytes = frame.Pixels.Length;
            metadata.PayloadSha256 = Sha256Hex(frame.Pixels);
            metadata.Compressed = compress;

            byte[] header = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(metadata, Formatting.Indented));

            byte[] magic = new byte[MagicLength];
            Buffer.BlockCopy(MagicPrefix, 0, magic, 0, MagicPrefix.Length);
            magic[MagicLength - 1] = (byte)CurrentFormatVersion;
            stream.Write(magic, 0, magic.Length);

            byte[] headerLength = BitConverter.GetBytes(header.Length);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(headerLength);
            }

            stream.Write(headerLength, 0, headerLength.Length);
            stream.Write(header, 0, header.Length);

            if (compress)
            {
                using DeflateStream deflate = new DeflateStream(stream, CompressionLevel.Fastest, leaveOpen: true);
                deflate.Write(frame.Pixels, 0, frame.Pixels.Length);
            }
            else
            {
                stream.Write(frame.Pixels, 0, frame.Pixels.Length);
            }
        }

        public static HdrRawFrame Read(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(fs);
        }

        public static HdrRawFrame Read(Stream stream)
        {
            HdrFrameMetadata metadata = ReadHeader(stream);
            byte[] pixels = GC.AllocateUninitializedArray<byte>(metadata.PayloadBytes);

            if (metadata.Compressed)
            {
                using DeflateStream deflate = new DeflateStream(stream, CompressionMode.Decompress, leaveOpen: true);
                ReadExactly(deflate, pixels);
            }
            else
            {
                ReadExactly(stream, pixels);
            }

            if (!string.IsNullOrEmpty(metadata.PayloadSha256))
            {
                string actual = Sha256Hex(pixels);
                if (!string.Equals(actual, metadata.PayloadSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("HDR frame payload hash mismatch: header says " +
                        metadata.PayloadSha256 + ", payload is " + actual + ".");
                }
            }

            return new HdrRawFrame(metadata, pixels);
        }

        /// <summary>Reads only the JSON header, so tools can list a corpus without loading payloads.</summary>
        public static HdrFrameMetadata ReadMetadata(string path)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ReadHeader(fs);
        }

        private static HdrFrameMetadata ReadHeader(Stream stream)
        {
            byte[] magic = new byte[MagicLength];
            ReadExactly(stream, magic);

            for (int i = 0; i < MagicPrefix.Length; i++)
            {
                if (magic[i] != MagicPrefix[i])
                {
                    throw new InvalidDataException("Not an HDR raw frame file (bad magic).");
                }
            }

            int version = magic[MagicLength - 1];
            if (version > CurrentFormatVersion)
            {
                throw new InvalidDataException("HDR frame format version " + version +
                    " is newer than supported version " + CurrentFormatVersion + ".");
            }

            byte[] headerLength = new byte[sizeof(int)];
            ReadExactly(stream, headerLength);
            if (!BitConverter.IsLittleEndian)
            {
                Array.Reverse(headerLength);
            }

            int length = BitConverter.ToInt32(headerLength, 0);
            if (length <= 0 || length > MaxHeaderBytes)
            {
                throw new InvalidDataException("HDR frame header length " + length + " is out of range.");
            }

            byte[] header = new byte[length];
            ReadExactly(stream, header);

            HdrFrameMetadata metadata = JsonConvert.DeserializeObject<HdrFrameMetadata>(Encoding.UTF8.GetString(header));
            if (metadata == null)
            {
                throw new InvalidDataException("HDR frame header did not deserialize.");
            }

            if (metadata.Width <= 0 || metadata.Height <= 0)
            {
                throw new InvalidDataException("HDR frame has invalid dimensions " +
                    metadata.Width + "x" + metadata.Height + ".");
            }

            if (metadata.DxgiFormat != HdrPixelConvert.FormatR16G16B16A16Float &&
                metadata.DxgiFormat != HdrPixelConvert.FormatR10G10B10A2Unorm)
            {
                throw new InvalidDataException("HDR frame has unsupported DXGI format " + metadata.DxgiFormat + ".");
            }

            int expectedStride = metadata.Width * HdrPixelConvert.BytesPerPixel(metadata.DxgiFormat);
            if (metadata.Stride != expectedStride)
            {
                throw new InvalidDataException("HDR frame stride " + metadata.Stride +
                    " is not tightly packed; expected " + expectedStride + " for " + metadata.Width +
                    "px of format " + metadata.DxgiFormat + ".");
            }

            long expectedPayload = (long)metadata.Stride * metadata.Height;
            if (metadata.PayloadBytes != expectedPayload)
            {
                throw new InvalidDataException("HDR frame payload length " + metadata.PayloadBytes +
                    " does not match " + metadata.Stride + " x " + metadata.Height + " = " + expectedPayload + ".");
            }

            return metadata;
        }

        private static void ReadExactly(Stream stream, byte[] buffer)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read <= 0)
                {
                    throw new EndOfStreamException("HDR frame file truncated: wanted " +
                        buffer.Length + " bytes, got " + offset + ".");
                }

                offset += read;
            }
        }

        private static string Sha256Hex(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    }
}
