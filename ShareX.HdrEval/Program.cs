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
using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Replays a corpus of captured HDR frames through every tonemap mode and exposure, measures
    /// each result against the frame's own pre-tonemap reference, and writes a CSV plus an HTML
    /// contact sheet.
    ///
    /// This is a workbench, not a gate. It answers "what changed and where", not "is this good".
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            EvalOptions options = EvalOptions.Parse(args, Console.Error);
            if (options == null)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine(EvalOptions.Usage);
                return args.Length == 0 || args.Contains("-h") || args.Contains("--help") ? 0 : 2;
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(options.SynthesizeDirectory))
                {
                    Console.WriteLine($"Writing synthetic test patterns to {options.SynthesizeDirectory}");
                    int written = CorpusSynth.Write(options.SynthesizeDirectory);
                    Console.WriteLine($"{written} pattern(s) written.");
                    return 0;
                }

                if (options.RetagScenarioOrNull.HasValue)
                {
                    return Retag.Run(options);
                }

                return Run(options);
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"Evaluation failed: {e}");
                return 1;
            }
        }

        private static int Run(EvalOptions options)
        {
            List<string> frames = SelectFrames(options);
            if (frames.Count == 0)
            {
                Console.Error.WriteLine($"No .hdrframe files matched in {options.CorpusDirectory}.");
                Console.Error.WriteLine("Capture some first - see ShareX.ScreenCaptureLib.Tests/README.md.");
                return 2;
            }

            Directory.CreateDirectory(options.OutputDirectory);

            // Without this, the library's diagnostics go to Debug.WriteLine and vanish. The log is
            // where a WIC failure or a dropped HDR master explains itself.
            DebugHelper.Init(options.LogPath);

            if (options.WritePng)
            {
                Directory.CreateDirectory(options.PngDirectory);
            }

            if (options.WriteThumbnails)
            {
                Directory.CreateDirectory(options.ThumbDirectory);
            }

            if (options.WriteReference)
            {
                Directory.CreateDirectory(options.ReferenceDirectory);
            }

            if (options.UltraHdr)
            {
                Directory.CreateDirectory(options.UltraHdrDirectory);
            }

            int combinations = options.Modes.Count * options.Exposures.Count;
            Console.WriteLine($"{frames.Count} frame(s) x {options.Modes.Count} mode(s) x " +
                $"{options.Exposures.Count} exposure(s) = {frames.Count * combinations} measurement(s)");
            Console.WriteLine($"output: {options.OutputDirectory}");
            Console.WriteLine();

            Dictionary<string, MetricRow> reusable = LoadReusableRows(options, frames);
            List<MetricRow> rows = new List<MetricRow>();
            Stopwatch total = Stopwatch.StartNew();

            for (int f = 0; f < frames.Count; f++)
            {
                string path = frames[f];
                string name = Path.GetFileName(path);
                Console.Write($"[{f + 1}/{frames.Count}] {name} ... ");

                HdrRawFrame frame;
                try
                {
                    frame = HdrRawFrame.Read(path);
                }
                catch (Exception e)
                {
                    Console.WriteLine($"unreadable: {e.Message}");
                    rows.Add(new MetricRow { FrameFile = name, Error = "unreadable: " + e.Message });
                    continue;
                }

                Stopwatch frameTimer = Stopwatch.StartNew();

                // Every combination already measured by an identical build can be reused verbatim,
                // which is what makes extending a session cost one frame rather than all of them.
                if (reusable.Count > 0 && TryReuseWholeFrame(reusable, name, options, rows, out int reused))
                {
                    Console.WriteLine($"reused {reused} measurement(s)");
                    continue;
                }

                // The reference does not depend on mode or exposure, so build it once per frame.
                HdrLinearFrame reference = HdrFrameTonemapper.DecodeToLinear(frame);
                ReferencePaths references = WriteReference(frame, reference, name, options);
                int flaggedForFrame = 0;

                foreach (HdrTonemapMode mode in options.Modes)
                {
                    foreach (float exposure in options.Exposures)
                    {
                        MetricRow row = Measure(frame, reference, mode, exposure, name, options);
                        row.ReferencePath = references.Hdr;
                        row.ReferenceThumbPath = references.HdrThumb;
                        row.ReferenceInRangePath = references.InRange;
                        row.ReferenceFullRangePath = references.FullRange;
                        rows.Add(row);
                        if (row.Flags.Count > 0)
                        {
                            flaggedForFrame++;
                        }
                    }
                }

                frameTimer.Stop();
                Console.WriteLine($"{combinations} measurement(s) in " +
                    $"{frameTimer.Elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s" +
                    (flaggedForFrame > 0 ? $", {flaggedForFrame} flagged" : string.Empty));
            }

            total.Stop();

            List<MetricRow> baseline = null;
            if (!string.IsNullOrEmpty(options.BaselineCsv))
            {
                baseline = ReportWriter.ReadCsv(options.BaselineCsv);
                Console.WriteLine($"baseline: {baseline.Count} row(s) from {options.BaselineCsv}");
            }

            DebugHelper.Flush();
            ReportWriter.WriteCsv(options.CsvPath, rows);
            ReportWriter.WriteHtml(options.HtmlPath, rows, options, baseline, total.Elapsed);

            Console.WriteLine();
            PrintSummary(rows, options, total.Elapsed);

            int flagged = rows.Count(r => r.Flags.Count > 0);
            int errors = rows.Count(r => !string.IsNullOrEmpty(r.Error));

            // Exit 0 means "the run completed", not "everything passed". Individual measurement
            // failures are expected in normal use - WindowsWIC cannot tonemap an HDR10 frame at all -
            // and they are reported in the console summary and the report. Making them non-zero by
            // default would leave every corpus containing an HDR10 frame looking like a broken run
            // and would break shell chains. --fail-on-flags is how you ask for strictness; exit 2 is
            // reserved for "could not run".
            return options.FailOnFlags && (flagged > 0 || errors > 0) ? 1 : 0;
        }

        private static MetricRow Measure(HdrRawFrame frame, HdrLinearFrame reference, HdrTonemapMode mode,
            float exposure, string frameName, EvalOptions options)
        {
            MetricRow row = new MetricRow
            {
                FrameFile = frameName,
                Label = frame.Metadata.Label,
                Scenario = frame.Metadata.Scenario,
                Width = frame.Width,
                Height = frame.Height,
                DxgiFormat = frame.DxgiFormat,
                SdrWhiteNits = frame.SdrWhiteNits,
                RequestedMode = mode,
                Exposure = exposure,
                ToolBuildId = MetricRow.CurrentBuildId
            };

            Stopwatch timer = Stopwatch.StartNew();

            try
            {
                // hysteresisKey stays null: Auto decisions must not depend on frame order here.
                using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, mode, exposure);

                row.ResolvedMode = result.ResolvedMode;
                row.StatsMaxLuminance = result.Stats.MaxLuminance;
                row.StatsP99 = result.Stats.P99Estimate;
                row.StatsFractionAboveOne = result.Stats.FractionAboveOne;
                row.StatsFractionHotUpperSdr = result.Stats.FractionHotUpperSdr;

                byte[] output = ReadBgra(result.Sdr);
                HdrMetrics.Compute(reference, output, row, options.SampleStride);
                MetricThresholds.For(row.Scenario).Apply(row);

                string stem = BuildStem(frameName, mode, exposure);

                if (options.WritePng)
                {
                    string pngPath = Path.Combine(options.PngDirectory, stem + ".png");
                    SavePng(result.Sdr, pngPath, options.PngDivisor);
                    row.PngPath = "png/" + stem + ".png";
                }

                if (options.UltraHdr)
                {
                    // Computed against result.Sdr rather than the downscaled PNG on purpose: the gain
                    // map is a per-pixel ratio against the base that actually ships, and --png-divisor
                    // resampling would decorrelate the two layers.
                    HdrGainMapData gainMap = HdrGainMap.Compute(reference, result.Sdr, options.GainMapDivisor);
                    string uhdrPath = Path.Combine(options.UltraHdrDirectory, stem + ".jpg");
                    UltraHdrJpegWriter.Save(uhdrPath, result.Sdr, gainMap);

                    Console.WriteLine($"  ultrahdr {stem}.jpg  " +
                        $"peak {gainMap.HdrPeak:0.00}x  " +
                        $"capacity {gainMap.HdrCapacityMax:0.00} stops  " +
                        $"range [{gainMap.GainMapMin:0.00}..{gainMap.LogGainStored:0.00}] " +
                        $"(max {gainMap.LogGainMax:0.00})  " +
                        $"map {gainMap.Width}x{gainMap.Height}  " +
                        $"{new FileInfo(uhdrPath).Length / 1024} KB");
                }

                bool wantThumb = options.WriteThumbnails &&
                    Math.Abs(exposure - options.ThumbnailExposure) < 0.001f;
                if (wantThumb)
                {
                    string thumbPath = Path.Combine(options.ThumbDirectory, stem + ".jpg");
                    SaveThumbnail(result.Sdr, thumbPath, options.ThumbnailWidth);
                    row.ThumbPath = "thumb/" + stem + ".jpg";
                }
            }
            catch (Exception e)
            {
                row.Error = e.Message;
                Console.Error.WriteLine($"  {mode} @{exposure.ToString("0.00", CultureInfo.InvariantCulture)}: {e.Message}");
            }

            timer.Stop();
            row.ElapsedMs = timer.ElapsedMilliseconds;
            return row;
        }

        /// <summary>
        /// Writes the untonemapped frame as an HDR PNG (full size and contact-sheet size) so the
        /// report can put the original beside the candidates. Returns relative paths for the HTML.
        /// </summary>
        private readonly record struct ReferencePaths(string Hdr, string HdrThumb, string InRange, string FullRange);

        private static ReferencePaths WriteReference(HdrRawFrame frame, HdrLinearFrame linear,
            string frameName, EvalOptions options)
        {
            if (!options.WriteReference)
            {
                return default;
            }

            string stem = Path.GetFileNameWithoutExtension(frameName);
            string hdr = null, hdrThumb = null, inRange = null, fullRange = null;

            try
            {
                HdrMasterImage master = HdrFrameTonemapper.BuildMaster(frame);

                if (options.WritePng)
                {
                    ReferenceImage.WriteFull(master, Path.Combine(options.ReferenceDirectory, stem + ".png"),
                        options.PngDivisor);
                    hdr = "reference/" + stem + ".png";
                }

                if (options.WriteThumbnails)
                {
                    ReferenceImage.WriteThumbnail(master,
                        Path.Combine(options.ReferenceDirectory, stem + "__thumb.png"), options.ThumbnailWidth);
                    hdrThumb = "reference/" + stem + "__thumb.png";
                }
            }
            catch (Exception e)
            {
                // A missing reference degrades the report; it must not lose the measurements.
                Console.Error.WriteLine($"  HDR reference for {frameName}: {e.Message}");
            }

            try
            {
                // The HDR reference only reads correctly in an HDR browser. These two always do, so
                // the report stays usable on an SDR display and the folder is shareable as-is.
                // Written at PNG resolution rather than thumbnail size, so clicking one in the report
                // gives something worth looking at. The contact sheet constrains display width in CSS.
                int sdrWidth = options.PngDivisor > 1 ? Math.Max(1, frame.Width / options.PngDivisor) : 0;
                string inRangeFile = Path.Combine(options.ReferenceDirectory, stem + "__sdr-inrange.png");
                string fullRangeFile = Path.Combine(options.ReferenceDirectory, stem + "__sdr-fullrange.png");
                ReferenceImage.WriteSdrViews(linear, inRangeFile, fullRangeFile, sdrWidth);
                inRange = "reference/" + stem + "__sdr-inrange.png";
                fullRange = "reference/" + stem + "__sdr-fullrange.png";
            }
            catch (Exception e)
            {
                Console.Error.WriteLine($"  SDR reference for {frameName}: {e.Message}");
            }

            return new ReferencePaths(hdr, hdrThumb, inRange, fullRange);
        }

        /// <summary>
        /// Rows from a previous run that are still valid: same build, same frame still on disk, and
        /// the mode/exposure combination is still being asked for.
        /// </summary>
        private static Dictionary<string, MetricRow> LoadReusableRows(EvalOptions options, List<string> frames)
        {
            Dictionary<string, MetricRow> reusable = new Dictionary<string, MetricRow>();
            if (!options.Incremental || !File.Exists(options.CsvPath))
            {
                return reusable;
            }

            HashSet<string> onDisk = new HashSet<string>(frames.Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);
            int staleBuild = 0;

            foreach (MetricRow row in ReportWriter.ReadCsv(options.CsvPath))
            {
                if (!onDisk.Contains(row.FrameFile))
                {
                    continue;
                }

                // A rebuilt ScreenCaptureLib is exactly when tonemap output can change. Reusing
                // across that would report stale numbers as current, which is worse than being slow.
                if (!string.Equals(row.ToolBuildId, MetricRow.CurrentBuildId, StringComparison.Ordinal))
                {
                    staleBuild++;
                    continue;
                }

                if (!options.Modes.Contains(row.RequestedMode) ||
                    !options.Exposures.Any(e => Math.Abs(e - row.Exposure) < 0.001f))
                {
                    continue;
                }

                reusable[row.Key] = row;
            }

            if (staleBuild > 0)
            {
                Console.WriteLine($"incremental: {staleBuild} row(s) came from a different build; re-measuring those.");
            }

            if (reusable.Count > 0)
            {
                Console.WriteLine($"incremental: reusing {reusable.Count} measurement(s) from {options.CsvPath}");
            }

            return reusable;
        }

        /// <summary>
        /// Reuses a frame only when every requested combination is present, so a frame is never
        /// half-measured across two builds.
        /// </summary>
        private static bool TryReuseWholeFrame(Dictionary<string, MetricRow> reusable, string frameName,
            EvalOptions options, List<MetricRow> rows, out int reused)
        {
            reused = 0;
            List<MetricRow> candidates = new List<MetricRow>();

            foreach (HdrTonemapMode mode in options.Modes)
            {
                foreach (float exposure in options.Exposures)
                {
                    string key = $"{frameName}|{mode}|{exposure.ToString("0.00", CultureInfo.InvariantCulture)}";
                    if (!reusable.TryGetValue(key, out MetricRow row))
                    {
                        return false;
                    }

                    candidates.Add(row);
                }
            }

            // Flags are recomputed rather than trusted from the CSV, so a threshold change takes
            // effect on reused rows too.
            foreach (MetricRow row in candidates)
            {
                MetricThresholds.For(row.Scenario).Apply(row);
                rows.Add(row);
            }

            reused = candidates.Count;
            return true;
        }

        private static List<string> SelectFrames(EvalOptions options)
        {
            List<string> selected = new List<string>();

            foreach (HdrFrameCorpus.Entry entry in HdrFrameCorpus.Enumerate(options.CorpusDirectory))
            {
                if (options.Scenarios.Count > 0 && !options.Scenarios.Contains(entry.Metadata.Scenario))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(options.Filter))
                {
                    bool matches =
                        entry.FileName.Contains(options.Filter, StringComparison.OrdinalIgnoreCase) ||
                        (entry.Metadata.Label ?? string.Empty)
                            .Contains(options.Filter, StringComparison.OrdinalIgnoreCase);
                    if (!matches)
                    {
                        continue;
                    }
                }

                selected.Add(Path.Combine(options.CorpusDirectory, entry.FileName));

                if (options.MaxFrames > 0 && selected.Count >= options.MaxFrames)
                {
                    break;
                }
            }

            return selected;
        }

        private static void PrintSummary(List<MetricRow> rows, EvalOptions options, TimeSpan elapsed)
        {
            Console.WriteLine($"{rows.Count} measurement(s) in " +
                $"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s");

            foreach (var group in rows.Where(r => string.IsNullOrEmpty(r.Error))
                .GroupBy(r => r.RequestedMode)
                .OrderBy(g => g.Key.ToString()))
            {
                List<MetricRow> list = group.ToList();
                int flagged = list.Count(r => r.Flags.Count > 0);
                Console.WriteLine($"  {group.Key,-12} {list.Count,4} row(s), {flagged,3} flagged" +
                    $"   blewOut {Avg(list, r => r.ClipBelowCeilingFraction) * 100f,6:0.00}%" +
                    $"   roundTrip {AvgInt(list, r => r.PassthroughP99CodeDelta),4:0.0}" +
                    $"   hue {Avg(list, r => r.HueDriftDegrees),5:0.00}deg" +
                    $"   chroma {Avg(list, r => r.ChromaRatio),5:0.000}" +
                    $"   contrastP10 {Avg(list, r => r.LocalContrastRatioP10),5:0.000}");
            }

            // Auto's classification is the thing most likely to drift silently.
            var autoRows = rows.Where(r => r.RequestedMode == HdrTonemapMode.Auto &&
                string.IsNullOrEmpty(r.Error)).ToList();
            if (autoRows.Count > 0)
            {
                Console.WriteLine();
                Console.WriteLine("  Auto resolved to:");
                foreach (var group in autoRows.GroupBy(r => new { r.Scenario, r.ResolvedMode })
                    .OrderBy(g => g.Key.Scenario.ToString()).ThenBy(g => g.Key.ResolvedMode.ToString()))
                {
                    Console.WriteLine($"    {group.Key.Scenario,-10} -> {group.Key.ResolvedMode,-12} {group.Count(),4}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"  csv    {options.CsvPath}");
            Console.WriteLine($"  report {options.HtmlPath}");
            Console.WriteLine($"  log    {options.LogPath}");

            int errors = rows.Count(r => !string.IsNullOrEmpty(r.Error));
            if (errors > 0)
            {
                Console.WriteLine();
                Console.WriteLine($"  {errors} measurement(s) failed - see the report.");
            }
        }

        private static float AvgInt(List<MetricRow> rows, Func<MetricRow, int> selector)
        {
            double sum = 0;
            int n = 0;
            foreach (MetricRow row in rows)
            {
                int value = selector(row);
                if (value >= 0)
                {
                    sum += value;
                    n++;
                }
            }

            return n > 0 ? (float)(sum / n) : float.NaN;
        }

        private static float Avg(List<MetricRow> rows, Func<MetricRow, float> selector)
        {
            double sum = 0;
            int n = 0;
            foreach (MetricRow row in rows)
            {
                float value = selector(row);
                if (!float.IsNaN(value))
                {
                    sum += value;
                    n++;
                }
            }

            return n > 0 ? (float)(sum / n) : float.NaN;
        }

        private static string BuildStem(string frameName, HdrTonemapMode mode, float exposure)
        {
            string stem = Path.GetFileNameWithoutExtension(frameName);
            string exposureTag = exposure.ToString("0.00", CultureInfo.InvariantCulture).Replace('.', 'p');
            return $"{stem}__{mode}__e{exposureTag}";
        }

        private static byte[] ReadBgra(Bitmap bitmap)
        {
            byte[] pixels = new byte[bitmap.Width * bitmap.Height * 4];
            BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                int rowBytes = bitmap.Width * 4;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(bd.Scan0 + y * bd.Stride, pixels, y * rowBytes, rowBytes);
                }
            }
            finally
            {
                bitmap.UnlockBits(bd);
            }

            return pixels;
        }

        /// <summary>Saves a PNG, optionally divided by <paramref name="divisor"/> in each direction.</summary>
        private static void SavePng(Bitmap source, string path, int divisor)
        {
            if (divisor <= 1)
            {
                source.Save(path, ImageFormat.Png);
                return;
            }

            int width = Math.Max(1, source.Width / divisor);
            int height = Math.Max(1, source.Height / divisor);

            using Bitmap scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(scaled))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            scaled.Save(path, ImageFormat.Png);
        }

        private static void SaveThumbnail(Bitmap source, string path, int targetWidth)
        {
            int width = Math.Min(targetWidth, source.Width);
            int height = Math.Max(1, (int)Math.Round(source.Height * (width / (double)source.Width)));

            using Bitmap thumb = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(thumb))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
                g.DrawImage(source, new Rectangle(0, 0, width, height));
            }

            ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (jpeg == null)
            {
                thumb.Save(path, ImageFormat.Png);
                return;
            }

            using EncoderParameters parameters = new EncoderParameters(1);
            parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 88L);
            thumb.Save(path, jpeg, parameters);
        }
    }
}
