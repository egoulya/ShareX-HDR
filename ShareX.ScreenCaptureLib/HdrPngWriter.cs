#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Writes 16-bit RGB PNG with PNG Third Edition HDR signalling (cICP + cLLI).
    /// </summary>
    public static class HdrPngWriter
    {
        private static readonly byte[] PngSignature = { 137, 80, 78, 71, 13, 10, 26, 10 };

        public static void Save(string path, HdrMasterImage master)
        {
            ArgumentNullException.ThrowIfNull(path);
            ArgumentNullException.ThrowIfNull(master);

            using FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            Write(fs, master);
        }

        public static void Write(Stream stream, HdrMasterImage master)
        {
            stream.Write(PngSignature);

            Span<byte> ihdr = stackalloc byte[13];
            BinaryPrimitives.WriteUInt32BigEndian(ihdr, (uint)master.Width);
            BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4), (uint)master.Height);
            ihdr[8] = 16; // bit depth
            ihdr[9] = 2;  // truecolor
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(stream, "IHDR"u8, ihdr);

            // BT.2100 PQ, identity matrix, full range — PNG Third Edition cICP.
            Span<byte> cicp = stackalloc byte[4] { 9, 16, 0, 1 };
            WriteChunk(stream, "cICP"u8, cicp);

            uint maxCll = (uint)Math.Clamp(Math.Round(master.MaxCLL * 10000.0), 0, uint.MaxValue);
            uint maxFall = (uint)Math.Clamp(Math.Round(master.MaxFALL * 10000.0), 0, uint.MaxValue);
            Span<byte> clli = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(clli, maxCll);
            BinaryPrimitives.WriteUInt32BigEndian(clli.Slice(4), maxFall);
            WriteChunk(stream, "cLLI"u8, clli);

            byte[] idat = BuildIdat(master);
            WriteChunk(stream, "IDAT"u8, idat);
            WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        private static byte[] BuildIdat(HdrMasterImage master)
        {
            int rowBytes = 1 + master.Width * 6; // filter + RGB16
            byte[] raw = GC.AllocateUninitializedArray<byte>(checked(rowBytes * master.Height));

            for (int y = 0; y < master.Height; y++)
            {
                int rawRow = y * rowBytes;
                raw[rawRow] = 0; // None filter
                int rgbRow = y * master.Width * 3;
                int o = rawRow + 1;
                for (int x = 0; x < master.Width; x++)
                {
                    int i = rgbRow + x * 3;
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o), master.Rgb[i]);
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o + 2), master.Rgb[i + 1]);
                    BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(o + 4), master.Rgb[i + 2]);
                    o += 6;
                }
            }

            using MemoryStream ms = new MemoryStream();
            using (ZLibStream zlib = new ZLibStream(ms, CompressionLevel.Optimal, leaveOpen: true))
            {
                zlib.Write(raw);
            }

            return ms.ToArray();
        }

        private static void WriteChunk(Stream stream, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
            stream.Write(len);
            stream.Write(type);
            stream.Write(data);

            uint crc = Crc32(type, data);
            Span<byte> crcBytes = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
            stream.Write(crcBytes);
        }

        private static uint Crc32(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
        {
            uint crc = 0xFFFFFFFFu;
            foreach (byte b in type)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            foreach (byte b in data)
            {
                crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
            }

            return crc ^ 0xFFFFFFFFu;
        }

        private static readonly uint[] CrcTable = CreateCrcTable();

        private static uint[] CreateCrcTable()
        {
            uint[] table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++)
                {
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                }

                table[n] = c;
            }

            return table;
        }
    }
}
