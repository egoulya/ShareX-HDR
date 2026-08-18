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
    /// Writes 16-bit RGB PNG with PNG Third Edition HDR signalling (cICP, cLLI, mDCV).
    /// </summary>
    public static class HdrPngWriter
    {
        private const int BytesPerPixel = 6; // 16-bit RGB
        private const int IdatChunkSize = 1024 * 1024;
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
            ihdr[8] = 16;
            ihdr[9] = 2;
            ihdr[10] = 0;
            ihdr[11] = 0;
            ihdr[12] = 0;
            WriteChunk(stream, "IHDR"u8, ihdr);

            Span<byte> cicp = stackalloc byte[4] { 9, 16, 0, 1 };
            WriteChunk(stream, "cICP"u8, cicp);

            uint maxCll = ToClli(master.MaxCLL);
            uint maxFall = ToClli(master.MaxFALL);
            Span<byte> clli = stackalloc byte[8];
            BinaryPrimitives.WriteUInt32BigEndian(clli, maxCll);
            BinaryPrimitives.WriteUInt32BigEndian(clli.Slice(4), maxFall);
            WriteChunk(stream, "cLLI"u8, clli);

            WriteMdcv(stream, master);

            using (IdatSink idat = new IdatSink(stream))
            using (ZLibStream zlib = new ZLibStream(idat, CompressionLevel.Fastest, leaveOpen: true))
            {
                WriteFilteredRows(zlib, master);
            }
            WriteChunk(stream, "IEND"u8, ReadOnlySpan<byte>.Empty);
        }

        private static void WriteMdcv(Stream stream, HdrMasterImage master)
        {
            HdrMasteringDisplay md = master.MasteringDisplay;
            float redX = md.HasValue ? md.RedX : 0.708f;
            float redY = md.HasValue ? md.RedY : 0.292f;
            float greenX = md.HasValue ? md.GreenX : 0.170f;
            float greenY = md.HasValue ? md.GreenY : 0.797f;
            float blueX = md.HasValue ? md.BlueX : 0.131f;
            float blueY = md.HasValue ? md.BlueY : 0.046f;
            float whiteX = md.HasValue ? md.WhiteX : 0.3127f;
            float whiteY = md.HasValue ? md.WhiteY : 0.3290f;
            float maxNits = md.HasValue && md.MaxLuminanceNits > 0 ? md.MaxLuminanceNits : Math.Max(master.MaxCLL, 1000f);
            float minNits = md.HasValue && md.MinLuminanceNits > 0 ? md.MinLuminanceNits : 0.0001f;

            Span<byte> mdcv = stackalloc byte[24];
            BinaryPrimitives.WriteUInt16BigEndian(mdcv, ToCie(redX));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(2), ToCie(redY));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(4), ToCie(greenX));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(6), ToCie(greenY));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(8), ToCie(blueX));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(10), ToCie(blueY));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(12), ToCie(whiteX));
            BinaryPrimitives.WriteUInt16BigEndian(mdcv.Slice(14), ToCie(whiteY));
            BinaryPrimitives.WriteUInt32BigEndian(mdcv.Slice(16), ToClli(maxNits));
            BinaryPrimitives.WriteUInt32BigEndian(mdcv.Slice(20), ToClli(minNits));
            WriteChunk(stream, "mDCV"u8, mdcv);
        }

        private static void WriteFilteredRows(Stream zlib, HdrMasterImage master)
        {
            int stride = master.Width * BytesPerPixel;
            byte[] curr = new byte[stride];
            byte[] prev = new byte[stride];
            byte[] filtered = new byte[1 + stride];
            byte[] trial = new byte[stride];

            for (int y = 0; y < master.Height; y++)
            {
                PackRow(master, y, curr);
                byte type = ChooseFilter(curr, prev, trial);
                filtered[0] = type;
                ApplyFilter(type, curr, prev, filtered.AsSpan(1));
                zlib.Write(filtered);
                (prev, curr) = (curr, prev);
            }
        }

        private static void PackRow(HdrMasterImage master, int y, byte[] dest)
        {
            int rgbRow = y * master.Width * 3;
            int o = 0;
            for (int x = 0; x < master.Width; x++)
            {
                int i = rgbRow + x * 3;
                BinaryPrimitives.WriteUInt16BigEndian(dest.AsSpan(o), master.Rgb[i]);
                BinaryPrimitives.WriteUInt16BigEndian(dest.AsSpan(o + 2), master.Rgb[i + 1]);
                BinaryPrimitives.WriteUInt16BigEndian(dest.AsSpan(o + 4), master.Rgb[i + 2]);
                o += BytesPerPixel;
            }
        }

        private static byte ChooseFilter(byte[] curr, byte[] prev, byte[] trial)
        {
            int bestSad = int.MaxValue;
            byte best = 0;
            for (byte type = 0; type <= 4; type++)
            {
                ApplyFilter(type, curr, prev, trial);
                int sad = 0;
                for (int i = 0; i < trial.Length; i++)
                {
                    int v = trial[i];
                    sad += v < 128 ? v : 256 - v;
                }

                if (sad < bestSad)
                {
                    bestSad = sad;
                    best = type;
                }
            }

            return best;
        }

        private static void ApplyFilter(byte type, byte[] curr, byte[] prev, Span<byte> dest)
        {
            int stride = curr.Length;
            switch (type)
            {
                case 1: // Sub
                    for (int i = 0; i < stride; i++)
                    {
                        byte left = i >= BytesPerPixel ? curr[i - BytesPerPixel] : (byte)0;
                        dest[i] = (byte)(curr[i] - left);
                    }
                    return;
                case 2: // Up
                    for (int i = 0; i < stride; i++)
                    {
                        dest[i] = (byte)(curr[i] - prev[i]);
                    }
                    return;
                case 3: // Average
                    for (int i = 0; i < stride; i++)
                    {
                        int left = i >= BytesPerPixel ? curr[i - BytesPerPixel] : 0;
                        dest[i] = (byte)(curr[i] - ((left + prev[i]) / 2));
                    }
                    return;
                case 4: // Paeth
                    for (int i = 0; i < stride; i++)
                    {
                        int a = i >= BytesPerPixel ? curr[i - BytesPerPixel] : 0;
                        int b = prev[i];
                        int c = i >= BytesPerPixel ? prev[i - BytesPerPixel] : 0;
                        dest[i] = (byte)(curr[i] - PaethPredictor(a, b, c));
                    }
                    return;
                default: // None
                    curr.CopyTo(dest);
                    return;
            }
        }

        private static int PaethPredictor(int a, int b, int c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        private static uint ToClli(float nits) =>
            (uint)Math.Clamp(Math.Round(nits * 10000.0), 0, uint.MaxValue);

        private static ushort ToCie(float value) =>
            (ushort)Math.Clamp(Math.Round(value / 0.00002), 0, 65535);

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

        private sealed class IdatSink : Stream
        {
            private readonly Stream png;
            private readonly byte[] buffer = new byte[IdatChunkSize];
            private int filled;
            private bool finished;

            public IdatSink(Stream png) => this.png = png;

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

            public override void Flush() { }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] data, int offset, int count)
            {
                while (count > 0)
                {
                    int n = Math.Min(buffer.Length - filled, count);
                    Buffer.BlockCopy(data, offset, buffer, filled, n);
                    filled += n;
                    offset += n;
                    count -= n;
                    if (filled == buffer.Length)
                    {
                        FlushChunk();
                    }
                }
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && !finished)
                {
                    if (filled > 0)
                    {
                        FlushChunk();
                    }

                    finished = true;
                }

                base.Dispose(disposing);
            }

            private void FlushChunk()
            {
                WriteChunk(png, "IDAT"u8, buffer.AsSpan(0, filled));
                filled = 0;
            }
        }
    }
}
