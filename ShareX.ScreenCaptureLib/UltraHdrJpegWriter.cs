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

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Writes a Google Ultra HDR JPEG: an ordinary SDR JPEG with a gain map appended as a second
    /// image, described by XMP and indexed by an MPF segment.
    ///
    /// The file is a valid baseline JPEG to anything that does not know about gain maps - the extra
    /// image sits after the primary's EOI, where a decoder stops reading. That is what makes this
    /// safe to share: a platform that strips or ignores the map yields the SDR base, which is the
    /// output we would have shipped regardless.
    ///
    /// Structure, in order:
    ///   primary: SOI, APP0/JFIF (as produced), APP1 XMP (GContainer directory), APP2 MPF, ... EOI
    ///   gain map: SOI, APP1 XMP (hdrgm parameters), ... EOI
    ///
    /// Both the XMP directory and the MPF index are written because decoders disagree about which to
    /// trust - Chrome reads the XMP GContainer, Android's decoder reads MPF.
    /// </summary>
    public static class UltraHdrJpegWriter
    {
        private const string XmpNamespaceHeader = "http://ns.adobe.com/xap/1.0/\0";
        private const string MpfIdentifier = "MPF\0";

        /// <summary>Quality for the SDR base image.</summary>
        public const int DefaultBaseQuality = 95;

        /// <summary>
        /// Quality for the gain map image. Kept high because the map is the sole carrier of detail
        /// wherever the base clipped, so ringing here shows up as artifacts in exactly the highlights
        /// this is meant to rescue. It compresses well regardless - the map is chroma-free and mostly
        /// low-frequency.
        /// </summary>
        public const int DefaultGainMapQuality = 95;

        // The MPF block is a fixed size for two images, which is what makes the self-referential
        // offsets below computable in one pass rather than needing a second fixup.
        private const int MpfIfdEntryCount = 3;
        private const int MpfTiffHeaderSize = 8;                                    // byte order, magic, IFD offset
        private const int MpfIfdSize = 2 + (MpfIfdEntryCount * 12) + 4;             // count, entries, next-IFD
        private const int MpfEntrySize = 16;                                        // per image
        private const int MpfImageCount = 2;
        private const int MpfEntriesOffset = MpfTiffHeaderSize + MpfIfdSize;        // within the TIFF block
        private const int MpfTiffTotal = MpfEntriesOffset + (MpfEntrySize * MpfImageCount);
        private const int MpfPayloadSize = 4 + MpfTiffTotal;                        // "MPF\0" + TIFF
        private const int MpfSegmentTotal = 2 + 2 + MpfPayloadSize;                 // marker + length + payload

        /// <summary>
        /// Encodes an SDR base and its gain map into a single Ultra HDR JPEG.
        /// </summary>
        public static void Save(string path, Bitmap sdr, HdrGainMapData map,
            int baseQuality = DefaultBaseQuality, int gainMapQuality = DefaultGainMapQuality)
        {
            if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("Path is required.", nameof(path));

            byte[] bytes = Build(sdr, map, baseQuality, gainMapQuality);

            // Encode fully before touching the destination, so a failure never truncates an existing
            // file.
            string directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllBytes(path, bytes);
        }

        /// <summary>
        /// Builds the complete Ultra HDR JPEG in memory. Separated from <see cref="Save"/> so tests can
        /// assert on the bytes without going through the filesystem.
        /// </summary>
        public static byte[] Build(Bitmap sdr, HdrGainMapData map,
            int baseQuality = DefaultBaseQuality, int gainMapQuality = DefaultGainMapQuality)
        {
            if (sdr == null) throw new ArgumentNullException(nameof(sdr));
            if (map == null) throw new ArgumentNullException(nameof(map));

            byte[] baseJpeg = EncodeJpeg(sdr, baseQuality);

            byte[] gainMapJpeg;
            using (Bitmap mapBitmap = ToBitmap(map))
            {
                byte[] raw = EncodeJpeg(mapBitmap, gainMapQuality);
                gainMapJpeg = InsertSegments(raw, new[] { BuildXmpSegment(BuildGainMapXmp(map)) });
            }

            byte[] primaryXmp = BuildXmpSegment(BuildPrimaryXmp(gainMapJpeg.Length));

            // The MPF entries need the primary's final length, which depends on the segments being
            // inserted - including MPF itself. Every part is a known size at this point, so it
            // resolves without a fixup pass.
            int insertionPoint = FindInsertionPoint(baseJpeg);
            int primaryLength = baseJpeg.Length + primaryXmp.Length + MpfSegmentTotal;

            // MPF offsets are measured from the first byte of the TIFF block, i.e. just past "MPF\0".
            int tiffStart = insertionPoint + primaryXmp.Length + 2 + 2 + 4;
            int gainMapOffset = primaryLength - tiffStart;

            byte[] mpf = BuildMpfSegment(primaryLength, gainMapJpeg.Length, gainMapOffset);

            byte[] primary = InsertSegments(baseJpeg, new[] { primaryXmp, mpf });

            if (primary.Length != primaryLength)
            {
                throw new InvalidOperationException(
                    $"Ultra HDR primary length mismatch: predicted {primaryLength}, produced {primary.Length}. " +
                    "The MPF offsets would be wrong, so refusing to write a corrupt file.");
            }

            byte[] result = new byte[primary.Length + gainMapJpeg.Length];
            Buffer.BlockCopy(primary, 0, result, 0, primary.Length);
            Buffer.BlockCopy(gainMapJpeg, 0, result, primary.Length, gainMapJpeg.Length);
            return result;
        }

        /// <summary>
        /// Renders the single-channel map as a 24bpp bitmap with R=G=B.
        ///
        /// A true single-channel JPEG would be smaller, but GDI+ has no reliable path to one; the
        /// chroma planes here are constant and cost little after compression. Worth revisiting only if
        /// the map turns out to dominate file size.
        /// </summary>
        private static unsafe Bitmap ToBitmap(HdrGainMapData map)
        {
            Bitmap bitmap = new Bitmap(map.Width, map.Height, PixelFormat.Format24bppRgb);
            BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, map.Width, map.Height),
                ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
            try
            {
                byte* scan0 = (byte*)bd.Scan0;
                for (int y = 0; y < map.Height; y++)
                {
                    byte* dst = scan0 + (long)y * bd.Stride;
                    int row = y * map.Width;
                    for (int x = 0; x < map.Width; x++)
                    {
                        byte v = map.Gain[row + x];
                        dst[x * 3 + 0] = v;
                        dst[x * 3 + 1] = v;
                        dst[x * 3 + 2] = v;
                    }
                }
            }
            finally
            {
                bitmap.UnlockBits(bd);
            }

            return bitmap;
        }

        private static byte[] EncodeJpeg(Bitmap bitmap, int quality)
        {
            ImageCodecInfo codec = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (codec == null)
            {
                throw new InvalidOperationException("No JPEG encoder is available.");
            }

            using EncoderParameters parameters = new EncoderParameters(1);
            using EncoderParameter parameter = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
            parameters.Param[0] = parameter;

            using MemoryStream stream = new MemoryStream();
            bitmap.Save(stream, codec, parameters);
            return stream.ToArray();
        }

        /// <summary>
        /// Returns the byte offset at which application segments should be inserted: after SOI, and
        /// after an immediately following APP0/JFIF so that JFIF stays first as the spec expects.
        /// </summary>
        private static int FindInsertionPoint(byte[] jpeg)
        {
            if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
            {
                throw new InvalidOperationException("Encoder did not produce a JPEG (missing SOI).");
            }

            if (jpeg.Length >= 6 && jpeg[2] == 0xFF && jpeg[3] == 0xE0)
            {
                int length = (jpeg[4] << 8) | jpeg[5];
                int end = 4 + length;
                if (end <= jpeg.Length)
                {
                    return end;
                }
            }

            return 2;
        }

        private static byte[] InsertSegments(byte[] jpeg, IReadOnlyList<byte[]> segments)
        {
            int insertionPoint = FindInsertionPoint(jpeg);
            int extra = segments.Sum(s => s.Length);

            byte[] result = new byte[jpeg.Length + extra];
            Buffer.BlockCopy(jpeg, 0, result, 0, insertionPoint);

            int offset = insertionPoint;
            foreach (byte[] segment in segments)
            {
                Buffer.BlockCopy(segment, 0, result, offset, segment.Length);
                offset += segment.Length;
            }

            Buffer.BlockCopy(jpeg, insertionPoint, result, offset, jpeg.Length - insertionPoint);
            return result;
        }

        private static byte[] BuildXmpSegment(string xmp)
        {
            byte[] header = Encoding.ASCII.GetBytes(XmpNamespaceHeader);
            byte[] body = Encoding.UTF8.GetBytes(xmp);
            int payload = header.Length + body.Length;
            int length = payload + 2;

            if (length > 0xFFFF)
            {
                throw new InvalidOperationException(
                    $"XMP packet is {payload} bytes, which does not fit in a single APP1 segment.");
            }

            byte[] segment = new byte[payload + 4];
            segment[0] = 0xFF;
            segment[1] = 0xE1;
            segment[2] = (byte)(length >> 8);
            segment[3] = (byte)(length & 0xFF);
            Buffer.BlockCopy(header, 0, segment, 4, header.Length);
            Buffer.BlockCopy(body, 0, segment, 4 + header.Length, body.Length);
            return segment;
        }

        private static byte[] BuildMpfSegment(int primaryLength, int gainMapLength, int gainMapOffset)
        {
            byte[] segment = new byte[MpfSegmentTotal];
            int p = 0;

            segment[p++] = 0xFF;
            segment[p++] = 0xE2;

            int length = 2 + MpfPayloadSize;
            segment[p++] = (byte)(length >> 8);
            segment[p++] = (byte)(length & 0xFF);

            segment[p++] = (byte)'M';
            segment[p++] = (byte)'P';
            segment[p++] = (byte)'F';
            segment[p++] = 0x00;

            int tiff = p;

            // Little-endian TIFF ("II"), magic 42, first IFD immediately after the header.
            segment[p++] = (byte)'I';
            segment[p++] = (byte)'I';
            WriteUInt16(segment, ref p, 0x002A);
            WriteUInt32(segment, ref p, MpfTiffHeaderSize);

            WriteUInt16(segment, ref p, MpfIfdEntryCount);

            // MPFVersion: type 7 (undefined), 4 bytes, stored inline as "0100".
            WriteUInt16(segment, ref p, 0xB000);
            WriteUInt16(segment, ref p, 7);
            WriteUInt32(segment, ref p, 4);
            segment[p++] = (byte)'0';
            segment[p++] = (byte)'1';
            segment[p++] = (byte)'0';
            segment[p++] = (byte)'0';

            // NumberOfImages: type 4 (long), inline.
            WriteUInt16(segment, ref p, 0xB001);
            WriteUInt16(segment, ref p, 4);
            WriteUInt32(segment, ref p, 1);
            WriteUInt32(segment, ref p, MpfImageCount);

            // MPEntry: type 7, 16 bytes per image, stored out of line.
            WriteUInt16(segment, ref p, 0xB002);
            WriteUInt16(segment, ref p, 7);
            WriteUInt32(segment, ref p, MpfEntrySize * MpfImageCount);
            WriteUInt32(segment, ref p, MpfEntriesOffset);

            WriteUInt32(segment, ref p, 0); // no further IFD

            if (p - tiff != MpfEntriesOffset)
            {
                throw new InvalidOperationException(
                    $"MPF IFD ended at {p - tiff} but entries were declared at {MpfEntriesOffset}.");
            }

            // Primary: representative image, and its offset must be zero by definition.
            WriteUInt32(segment, ref p, 0x00030000);
            WriteUInt32(segment, ref p, (uint)primaryLength);
            WriteUInt32(segment, ref p, 0);
            WriteUInt16(segment, ref p, 0);
            WriteUInt16(segment, ref p, 0);

            // Gain map: an ordinary auxiliary image, offset relative to the TIFF block.
            WriteUInt32(segment, ref p, 0x00000000);
            WriteUInt32(segment, ref p, (uint)gainMapLength);
            WriteUInt32(segment, ref p, (uint)gainMapOffset);
            WriteUInt16(segment, ref p, 0);
            WriteUInt16(segment, ref p, 0);

            if (p != MpfSegmentTotal)
            {
                throw new InvalidOperationException(
                    $"MPF segment ended at {p} but was sized {MpfSegmentTotal}.");
            }

            return segment;
        }

        private static void WriteUInt16(byte[] buffer, ref int offset, int value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
        }

        private static void WriteUInt32(byte[] buffer, ref int offset, uint value)
        {
            buffer[offset++] = (byte)(value & 0xFF);
            buffer[offset++] = (byte)((value >> 8) & 0xFF);
            buffer[offset++] = (byte)((value >> 16) & 0xFF);
            buffer[offset++] = (byte)((value >> 24) & 0xFF);
        }

        private static string BuildPrimaryXmp(int gainMapLength)
        {
            return
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\"" +
                " xmlns:Container=\"http://ns.google.com/photos/1.0/container/\"" +
                " xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\"" +
                " xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
                " hdrgm:Version=\"1.0\">" +
                "<Container:Directory>" +
                "<rdf:Seq>" +
                "<rdf:li rdf:parseType=\"Resource\">" +
                "<Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/>" +
                "</rdf:li>" +
                "<rdf:li rdf:parseType=\"Resource\">" +
                "<Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\"" +
                " Item:Length=\"" + gainMapLength.ToString(CultureInfo.InvariantCulture) + "\"/>" +
                "</rdf:li>" +
                "</rdf:Seq>" +
                "</Container:Directory>" +
                "</rdf:Description>" +
                "</rdf:RDF>" +
                "</x:xmpmeta>" +
                "<?xpacket end=\"w\"?>";
        }

        private static string BuildGainMapXmp(HdrGainMapData map)
        {
            return
                "<?xpacket begin=\"﻿\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?>" +
                "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\">" +
                "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
                "<rdf:Description rdf:about=\"\"" +
                " xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\"" +
                " hdrgm:Version=\"1.0\"" +
                " hdrgm:GainMapMin=\"" + Num(map.GainMapMin) + "\"" +
                " hdrgm:GainMapMax=\"" + Num(map.GainMapMax) + "\"" +
                " hdrgm:Gamma=\"" + Num(map.Gamma) + "\"" +
                " hdrgm:OffsetSDR=\"" + Num(map.OffsetSdr) + "\"" +
                " hdrgm:OffsetHDR=\"" + Num(map.OffsetHdr) + "\"" +
                " hdrgm:HDRCapacityMin=\"" + Num(map.HdrCapacityMin) + "\"" +
                " hdrgm:HDRCapacityMax=\"" + Num(map.HdrCapacityMax) + "\"" +
                " hdrgm:BaseRenditionIsHDR=\"False\"/>" +
                "</rdf:RDF>" +
                "</x:xmpmeta>" +
                "<?xpacket end=\"w\"?>";
        }

        private static string Num(float value)
        {
            return value.ToString("0.######", CultureInfo.InvariantCulture);
        }
    }
}
