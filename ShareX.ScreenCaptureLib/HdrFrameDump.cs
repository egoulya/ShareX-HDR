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
using ShareX.HelpersLib;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    /// <summary>
    /// Opt-in recorder that writes the raw DXGI buffer of every HDR capture to a
    /// <see cref="HdrRawFrame"/> file before it is tonemapped. Off unless
    /// <see cref="Directory"/> is set.
    ///
    /// The point is to decouple "change the tonemap curve" from "boot a game": capture a
    /// scene once into the corpus, then replay it through
    /// <see cref="HdrFrameTonemapper.TonemapFrame"/> as many times as you like.
    ///
    /// Nothing here is allowed to break a capture, so every failure is logged and swallowed.
    /// </summary>
    public static class HdrFrameDump
    {
        public const string DirectoryVariable = "SHAREX_HDR_DUMP_DIR";
        public const string ScenarioVariable = "SHAREX_HDR_DUMP_SCENARIO";
        public const string LabelVariable = "SHAREX_HDR_DUMP_LABEL";
        public const string NotesVariable = "SHAREX_HDR_DUMP_NOTES";
        public const string MaxFramesVariable = "SHAREX_HDR_DUMP_MAX";
        public const string CompressVariable = "SHAREX_HDR_DUMP_COMPRESS";

        private static readonly object DumpLock = new();
        private static readonly List<string> Recent = new();
        private static int sequence;
        private static int dumpedCount;

        static HdrFrameDump()
        {
            ConfigureFromEnvironment();
        }

        /// <summary>Output directory. Null or empty disables dumping.</summary>
        public static string Directory { get; set; }

        /// <summary>Deflate the payload. scRGB half-float data compresses poorly (~1.2:1); HDR10 does better.</summary>
        public static bool Compress { get; set; } = true;

        /// <summary>Tagged onto every frame so the offline runner can pick per-scenario thresholds.</summary>
        public static HdrCorpusScenario Scenario { get; set; } = HdrCorpusScenario.Unknown;

        /// <summary>Short label for the scene, e.g. "explorer-dark" or "cyberpunk-neon-alley".</summary>
        public static string Label { get; set; }

        /// <summary>Free-form notes recorded with each frame: game, settings, what it stresses.</summary>
        public static string Notes { get; set; }

        /// <summary>Stop after this many frames. 0 means unlimited.</summary>
        public static int MaxFrames { get; set; }

        public static bool Enabled => !string.IsNullOrWhiteSpace(Directory);

        public static int DumpedCount => Volatile.Read(ref dumpedCount);

        public static string LastPath { get; private set; }

        public static IReadOnlyList<string> RecentPaths
        {
            get
            {
                lock (DumpLock)
                {
                    return Recent.ToArray();
                }
            }
        }

        /// <summary>
        /// Arms the dump from environment variables, so a corpus run needs no rebuild and no
        /// settings change - and so it can never be left on for an ordinary user:
        ///
        ///   SHAREX_HDR_DUMP_DIR       output directory; unset means off
        ///   SHAREX_HDR_DUMP_SCENARIO  Desktop | AutoHdr | NativeHdr
        ///   SHAREX_HDR_DUMP_LABEL     scene label baked into the filename and metadata
        ///   SHAREX_HDR_DUMP_NOTES     free-form notes recorded with each frame
        ///   SHAREX_HDR_DUMP_MAX       stop after this many frames (0 or unset = unlimited)
        ///   SHAREX_HDR_DUMP_COMPRESS  0/false to store payloads uncompressed
        ///
        /// Called from the static constructor. Call it again after changing the environment; the
        /// properties are also settable directly.
        /// </summary>
        public static void ConfigureFromEnvironment()
        {
            try
            {
                string directory = Environment.GetEnvironmentVariable(DirectoryVariable);
                Directory = string.IsNullOrWhiteSpace(directory) ? null : directory.Trim();

                string scenario = Environment.GetEnvironmentVariable(ScenarioVariable);
                Scenario = Enum.TryParse(scenario, true, out HdrCorpusScenario parsed)
                    ? parsed
                    : HdrCorpusScenario.Unknown;

                Label = NullIfBlank(Environment.GetEnvironmentVariable(LabelVariable));
                Notes = NullIfBlank(Environment.GetEnvironmentVariable(NotesVariable));

                MaxFrames = int.TryParse(Environment.GetEnvironmentVariable(MaxFramesVariable),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out int max) && max > 0
                    ? max
                    : 0;

                string compress = Environment.GetEnvironmentVariable(CompressVariable);
                Compress = string.IsNullOrWhiteSpace(compress) ||
                    !(compress.Trim().Equals("0", StringComparison.Ordinal) ||
                      compress.Trim().Equals("false", StringComparison.OrdinalIgnoreCase));

                if (Enabled)
                {
                    DebugHelper.WriteLine($"HDR dump: armed -> {Directory} " +
                        $"(scenario={Scenario}, label={Label ?? "none"}, max={(MaxFrames > 0 ? MaxFrames.ToString(CultureInfo.InvariantCulture) : "unlimited")}, compress={Compress})");
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR dump: could not read environment configuration; staying off.");
                Directory = null;
            }
        }

        private static string NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Clears the counters and the recent-path list. Does not touch files on disk.</summary>
        public static void Reset()
        {
            lock (DumpLock)
            {
                Recent.Clear();
                Volatile.Write(ref dumpedCount, 0);
                Volatile.Write(ref sequence, 0);
                LastPath = null;
            }
        }

        /// <summary>
        /// Builds the display-metadata template for a frame about to be dumped. Geometry is
        /// filled in by <see cref="HdrRawFrame.FromMappedRegion"/>.
        /// </summary>
        public static HdrFrameMetadata CreateTemplate(string deviceName, float sdrWhiteNits, uint colorSpace,
            Rectangle monitorRect, Rectangle captureRect, in HdrMasteringDisplay mastering)
        {
            HdrFrameMetadata metadata = new HdrFrameMetadata
            {
                DeviceName = deviceName,
                SdrWhiteNits = sdrWhiteNits,
                ColorSpace = colorSpace,
                MonitorRect = HdrFrameRect.From(monitorRect),
                CaptureRect = HdrFrameRect.From(captureRect),
                Scenario = Scenario,
                Label = Label,
                Notes = Notes,
                CapturedUtc = DateTime.UtcNow
            };

            metadata.SetMasteringDisplay(mastering);
            return metadata;
        }

        /// <summary>
        /// Writes one frame from mapped DXGI memory. Returns the path written, or null when
        /// dumping is off, the quota is reached, or the write failed.
        /// </summary>
        public static string TryDump(IntPtr data, int rowPitch, int dxgiFormat, int srcX, int srcY,
            int width, int height, HdrFrameMetadata template)
        {
            if (!Enabled || data == IntPtr.Zero || width <= 0 || height <= 0)
            {
                return null;
            }

            try
            {
                string directory = Directory;
                int seq;

                lock (DumpLock)
                {
                    if (MaxFrames > 0 && dumpedCount >= MaxFrames)
                    {
                        return null;
                    }

                    seq = ++sequence;
                }

                HdrRawFrame frame = HdrRawFrame.FromMappedRegion(data, rowPitch, dxgiFormat,
                    srcX, srcY, width, height, template);

                string path = Path.Combine(directory, BuildFileName(frame.Metadata, seq));
                HdrRawFrame.Write(path, frame, Compress);

                lock (DumpLock)
                {
                    dumpedCount++;
                    LastPath = path;
                    Recent.Add(path);
                    if (Recent.Count > 64)
                    {
                        Recent.RemoveAt(0);
                    }
                }

                DebugHelper.WriteLine($"HDR dump: wrote {width}x{height} fmt={dxgiFormat} " +
                    $"sdrWhite={frame.Metadata.SdrWhiteNits:0.#} -> {path}");
                return path;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR dump: failed to write raw frame; capture continues.");
                return null;
            }
        }

        private static string BuildFileName(HdrFrameMetadata metadata, int sequenceNumber)
        {
            StringBuilder name = new StringBuilder();
            name.Append(metadata.CapturedUtc.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            name.Append('-');
            name.Append(sequenceNumber.ToString("000", CultureInfo.InvariantCulture));
            name.Append('_');
            name.Append(metadata.Scenario);

            if (!string.IsNullOrWhiteSpace(metadata.Label))
            {
                name.Append('_');
                name.Append(Sanitize(metadata.Label));
            }

            string device = Sanitize(metadata.DeviceName);
            if (!string.IsNullOrEmpty(device))
            {
                name.Append('_');
                name.Append(device);
            }

            name.Append('_');
            name.Append(metadata.Width);
            name.Append('x');
            name.Append(metadata.Height);
            name.Append("_fmt");
            name.Append(metadata.DxgiFormat);
            name.Append(HdrRawFrame.FileExtension);
            return name.ToString();
        }

        /// <summary>Turns "\\.\DISPLAY1" and similar into a filename-safe token.</summary>
        private static string Sanitize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            StringBuilder safe = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    safe.Append(c);
                }
                else if ((c == '_' || c == ' ' || c == '.' || c == '\\' || c == '/') && safe.Length > 0 &&
                    safe[safe.Length - 1] != '-')
                {
                    safe.Append('-');
                }
            }

            return safe.ToString().Trim('-');
        }
    }

    /// <summary>Listing and manifest helpers for a directory of dumped <see cref="HdrRawFrame"/> files.</summary>
    public static class HdrFrameCorpus
    {
        public const string ManifestFileName = "manifest.json";

        public sealed class Entry
        {
            public string FileName { get; set; }
            public long FileBytes { get; set; }
            public HdrFrameMetadata Metadata { get; set; }
        }

        /// <summary>
        /// Reads every frame header in <paramref name="directory"/>. Unreadable files are skipped
        /// with a log line rather than aborting the listing.
        /// </summary>
        public static List<Entry> Enumerate(string directory)
        {
            List<Entry> entries = new List<Entry>();
            if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
            {
                return entries;
            }

            foreach (string path in System.IO.Directory
                .EnumerateFiles(directory, "*" + HdrRawFrame.FileExtension, SearchOption.TopDirectoryOnly)
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    entries.Add(new Entry
                    {
                        FileName = Path.GetFileName(path),
                        FileBytes = new FileInfo(path).Length,
                        Metadata = HdrRawFrame.ReadMetadata(path)
                    });
                }
                catch (Exception e)
                {
                    DebugHelper.WriteException(e, $"HDR corpus: skipping unreadable frame {path}.");
                }
            }

            return entries;
        }

        /// <summary>
        /// Writes <see cref="ManifestFileName"/> listing every frame with its payload hash. Commit
        /// the manifest even when the payloads live outside version control, so a corpus that
        /// changed underneath you is detectable.
        /// </summary>
        public static string WriteManifest(string directory)
        {
            List<Entry> entries = Enumerate(directory);
            string path = Path.Combine(directory, ManifestFileName);

            // No BOM: RFC 8259 forbids one, and strict JSON parsers reject it. The manifest is meant
            // to be read by other tooling, not just by us.
            File.WriteAllText(path, JsonConvert.SerializeObject(entries, Formatting.Indented),
                new UTF8Encoding(false));
            DebugHelper.WriteLine($"HDR corpus: manifest lists {entries.Count} frame(s) -> {path}");
            return path;
        }
    }
}
