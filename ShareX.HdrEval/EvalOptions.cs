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

using ShareX.ScreenCaptureLib;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace ShareX.HdrEval
{
    public sealed class EvalOptions
    {
        public string CorpusDirectory { get; set; }
        public string OutputDirectory { get; set; }

        public List<HdrTonemapMode> Modes { get; set; } = new()
        {
            HdrTonemapMode.Auto,
            HdrTonemapMode.Desktop,
            HdrTonemapMode.AutoHDR,
            HdrTonemapMode.Filmic,
            HdrTonemapMode.WindowsWIC
        };

        public List<float> Exposures { get; set; } = new() { 1.00f };

        /// <summary>Write a full-resolution PNG per measurement.</summary>
        public bool WritePng { get; set; } = true;

        /// <summary>Write a downscaled JPEG per measurement for the contact sheet.</summary>
        public bool WriteThumbnails { get; set; } = true;

        public int ThumbnailWidth { get; set; } = 320;

        /// <summary>Only this exposure appears in the contact sheet; the tables cover all of them.</summary>
        public float ThumbnailExposure { get; set; } = 1.00f;

        /// <summary>Sample every Nth pixel for the cheap metrics. Local contrast always runs full-res.</summary>
        public int SampleStride { get; set; } = 1;

        /// <summary>Only evaluate frames whose scenario is in this set. Empty means all.</summary>
        public HashSet<HdrCorpusScenario> Scenarios { get; set; } = new();

        /// <summary>Substring filter on the frame filename or label.</summary>
        public string Filter { get; set; }

        public int MaxFrames { get; set; }

        /// <summary>A metrics.csv from a previous run to diff against.</summary>
        public string BaselineCsv { get; set; }

        /// <summary>Exit non-zero when any measurement trips a threshold.</summary>
        public bool FailOnFlags { get; set; }

        /// <summary>Write synthetic test patterns into this directory and exit.</summary>
        public string SynthesizeDirectory { get; set; }

        /// <summary>
        /// Reuse results already in the output metrics.csv and evaluate only frames that are new, then
        /// rewrite the merged CSV and report. Lets a capture session extend one report at constant
        /// cost per capture instead of re-measuring everything already on disk.
        /// </summary>
        public bool Incremental { get; set; }

        /// <summary>
        /// Integer divisor for full-resolution PNG and HDR reference output. 5 turns 3840x2160 into
        /// 768x432, which is 25x fewer pixels - the difference between a shareable folder and a
        /// gigabyte. 1 keeps full resolution.
        /// </summary>
        public int PngDivisor { get; set; } = 1;

        /// <summary>
        /// Also emit a Google Ultra HDR JPEG per mode: the tonemapped SDR image plus a gain map that
        /// lets an HDR display reconstruct what the curve had to discard.
        /// </summary>
        public bool UltraHdr { get; set; }

        /// <summary>
        /// Resolution divisor for the stored gain map. 1 keeps full resolution, which is the default
        /// because where the SDR base clipped the map is the only carrier of detail - downsampling
        /// throws away precisely the highlights this is meant to preserve.
        /// </summary>
        public int GainMapDivisor { get; set; } = 1;

        /// <summary>Rewrite the scenario on matched frames instead of evaluating. Null means off.</summary>
        public HdrCorpusScenario? RetagScenarioOrNull { get; set; }

        public HdrCorpusScenario RetagScenario => RetagScenarioOrNull ?? HdrCorpusScenario.Unknown;

        /// <summary>Optional new label to write alongside the scenario.</summary>
        public string RetagLabel { get; set; }

        /// <summary>Retag is a dry run until this is set, so a bad filter cannot quietly rewrite a corpus.</summary>
        public bool RetagConfirmed { get; set; }

        public string CsvPath => Path.Combine(OutputDirectory, "metrics.csv");
        public string HtmlPath => Path.Combine(OutputDirectory, "report.html");
        /// <summary>Write the untonemapped frame as an HDR PNG so the report can show the original.</summary>
        public bool WriteReference { get; set; } = true;

        public string PngDirectory => Path.Combine(OutputDirectory, "png");
        public string ReferenceDirectory => Path.Combine(OutputDirectory, "reference");
        public string ThumbDirectory => Path.Combine(OutputDirectory, "thumb");
        public string UltraHdrDirectory => Path.Combine(OutputDirectory, "ultrahdr");
        public string LogPath => Path.Combine(OutputDirectory, "debug.log");

        public static string Usage => """
ShareX.HdrEval - offline HDR tonemap evaluation over a captured frame corpus

  ShareX.HdrEval --corpus <dir> [--out <dir>] [options]

Required
  --corpus <dir>          Directory of .hdrframe files (see HdrFrameDump).

Output
  --out <dir>             Output directory. Default: <corpus>\eval.
  --no-png                Skip full-resolution PNGs. Much faster.
  --no-thumbs             Skip contact-sheet thumbnails.
  --png-divisor <n>       Downscale full-resolution PNGs and HDR references by n in each
                          direction, so n=5 is 25x fewer pixels. Thumbnails are unaffected.
                          Use it to make a session small enough to hand to someone.
                          NOT for pixel analysis: bicubic downscaling pushes near-white
                          pixels to 255, so any clipping measured off divided output is
                          inflated. Metrics in the CSV are computed before scaling and are
                          unaffected; it is external analysis of the PNGs that breaks.
  --incremental           Reuse rows already in the output metrics.csv and measure only
                          frames that are new, then rewrite the merged CSV and report.
                          Refuses to reuse anything built by a different ShareX build, so
                          a rebuilt tonemap always re-measures. For accumulating captures
                          into one report; not a substitute for a clean run.
  --no-reference          Skip the HDR reference images. These are the untonemapped
                          original, written as cICP PQ PNG, which Chrome shows as real
                          HDR on an HDR display - the thing to compare results against.
  --thumb-width <px>      Thumbnail width. Default 320.
  --thumb-exposure <v>    Which exposure appears in the contact sheet. Default 1.00.

What to evaluate
  --modes <list>          Comma-separated: Auto,Desktop,AutoHDR,Filmic,WindowsWIC.
                          Default: all five.
  --exposures <list>      Comma-separated, each clamped to [0.70, 1.30].
                          Default: 1.00. Try 0.7,0.85,1.0,1.15,1.3 for a sweep.
  --scenario <list>       Only these scenarios: Desktop,AutoHdr,NativeHdr,Unknown.
  --filter <text>         Only frames whose filename or label contains this.
  --max-frames <n>        Stop after n frames.

Speed and comparison
  --stride <n>            Sample every nth pixel for cheap metrics. Default 1.
                          Local contrast always runs full-res regardless.
  --baseline <csv>        A previous metrics.csv to diff against.
  --fail-on-flags         Exit 1 if any measurement trips a threshold or fails. Without it,
                          exit 0 means "the run completed" - per-measurement failures are
                          normal (WindowsWIC cannot tonemap HDR10) and are reported instead.
                          Exit 2 means it could not run at all.

Fixing up tags
  --retag <scenario>      Rewrite the scenario on frames matched by --filter / --scenario,
                          then exit. Scenario selects which thresholds apply, and Desktop
                          is the only one with an exact ground truth - an untagged desktop
                          capture silently loses its round-trip check. Dry run until --yes.
  --retag-label <text>    New label to write alongside the scenario.
  --yes                   Actually write. Without it, --retag only lists what it would do.

Bootstrapping
  --ultrahdr              Also write a Google Ultra HDR JPEG per mode into ultrahdr/: the SDR
                          image with a gain map appended, so an HDR display can reconstruct the
                          highlight detail sRGB has no code values for. Degrades to a plain SDR
                          JPEG anywhere the map is ignored or stripped.
  --gainmap-divisor <n>   Resolution divisor for the stored gain map (default 1 = full). Unlike
                          --png-divisor this is not a free size win: where the base clipped, the
                          map is the only carrier of detail.

  --synthesize <dir>      Write synthetic HDR test patterns into <dir> and exit.
                          Known-value targets (paper white, sRGB round-trip, ramps,
                          saturated highlights) - useful for calibrating thresholds
                          and smoke-testing this tool before collecting real frames.

  -h, --help              This text.

Examples
  Sweep every mode at one exposure, with a contact sheet:
    ShareX.HdrEval --corpus D:\...\HdrCorpus

  Exposure sweep, metrics only, no images:
    ShareX.HdrEval --corpus D:\...\HdrCorpus --exposures 0.7,0.85,1.0,1.15,1.3 --no-png --no-thumbs

  Check a curve change against the last run:
    ShareX.HdrEval --corpus D:\...\HdrCorpus --baseline D:\...\HdrCorpus\eval\metrics.csv

  Make a synthetic corpus, then evaluate it:
    ShareX.HdrEval --synthesize D:\...\HdrCorpusSynth
    ShareX.HdrEval --corpus D:\...\HdrCorpusSynth

  Tag the debug captures you took of the desktop (dry run, then apply):
    ShareX.HdrEval --corpus "%USERPROFILE%\Documents\ShareX\HDRDebug\2026-09-02" --filter 14-32 --retag Desktop
    ShareX.HdrEval --corpus "%USERPROFILE%\Documents\ShareX\HDRDebug\2026-09-02" --filter 14-32 --retag Desktop --retag-label explorer-dark --yes
""";

        /// <summary>Parses argv. Returns null and prints why when the arguments do not make sense.</summary>
        public static EvalOptions Parse(string[] args, TextWriter error)
        {
            EvalOptions options = new EvalOptions();
            bool modesGiven = false;
            bool exposuresGiven = false;
            bool thumbExposureGiven = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                switch (arg)
                {
                    case "-h":
                    case "--help":
                        return null;

                    case "--corpus":
                        if (!TryNext(args, ref i, error, arg, out string corpusDir)) return null;
                        options.CorpusDirectory = corpusDir;
                        break;

                    case "--out":
                        if (!TryNext(args, ref i, error, arg, out string outDir)) return null;
                        options.OutputDirectory = outDir;
                        break;

                    case "--no-png":
                        options.WritePng = false;
                        break;

                    case "--no-thumbs":
                        options.WriteThumbnails = false;
                        break;

                    case "--no-reference":
                        options.WriteReference = false;
                        break;

                    case "--thumb-width":
                        if (!TryNextInt(args, ref i, error, arg, out int thumbWidth)) return null;
                        options.ThumbnailWidth = Math.Clamp(thumbWidth, 64, 3840);
                        break;

                    case "--thumb-exposure":
                        if (!TryNextFloat(args, ref i, error, arg, out float thumbExposure)) return null;
                        options.ThumbnailExposure = HdrTonemap.ClampExposure(thumbExposure);
                        thumbExposureGiven = true;
                        break;

                    case "--modes":
                        if (!TryNext(args, ref i, error, arg, out string modeList)) return null;
                        options.Modes = new List<HdrTonemapMode>();
                        foreach (string token in Split(modeList))
                        {
                            if (!Enum.TryParse(token, true, out HdrTonemapMode mode))
                            {
                                error.WriteLine($"Unknown tonemap mode '{token}'. Valid: " +
                                    string.Join(", ", Enum.GetNames<HdrTonemapMode>()));
                                return null;
                            }

                            if (!options.Modes.Contains(mode))
                            {
                                options.Modes.Add(mode);
                            }
                        }

                        modesGiven = true;
                        break;

                    case "--exposures":
                        if (!TryNext(args, ref i, error, arg, out string exposureList)) return null;
                        options.Exposures = new List<float>();
                        foreach (string token in Split(exposureList))
                        {
                            if (!float.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out float value))
                            {
                                error.WriteLine($"Could not parse exposure '{token}'.");
                                return null;
                            }

                            float clamped = HdrTonemap.ClampExposure(value);
                            if (Math.Abs(clamped - value) > 1e-4f)
                            {
                                error.WriteLine($"Note: exposure {value.ToString(CultureInfo.InvariantCulture)} " +
                                    $"clamped to {clamped.ToString(CultureInfo.InvariantCulture)} " +
                                    $"(valid range {HdrTonemap.ExposureMin}-{HdrTonemap.ExposureMax}).");
                            }

                            if (!options.Exposures.Contains(clamped))
                            {
                                options.Exposures.Add(clamped);
                            }
                        }

                        exposuresGiven = true;
                        break;

                    case "--scenario":
                        if (!TryNext(args, ref i, error, arg, out string scenarioList)) return null;
                        foreach (string token in Split(scenarioList))
                        {
                            if (!Enum.TryParse(token, true, out HdrCorpusScenario scenario))
                            {
                                error.WriteLine($"Unknown scenario '{token}'. Valid: " +
                                    string.Join(", ", Enum.GetNames<HdrCorpusScenario>()));
                                return null;
                            }

                            options.Scenarios.Add(scenario);
                        }

                        break;

                    case "--filter":
                        if (!TryNext(args, ref i, error, arg, out string filter)) return null;
                        options.Filter = filter;
                        break;

                    case "--max-frames":
                        if (!TryNextInt(args, ref i, error, arg, out int maxFrames)) return null;
                        options.MaxFrames = Math.Max(0, maxFrames);
                        break;

                    case "--stride":
                        if (!TryNextInt(args, ref i, error, arg, out int stride)) return null;
                        options.SampleStride = Math.Clamp(stride, 1, 64);
                        break;

                    case "--baseline":
                        if (!TryNext(args, ref i, error, arg, out string baseline)) return null;
                        options.BaselineCsv = baseline;
                        break;

                    case "--fail-on-flags":
                        options.FailOnFlags = true;
                        break;

                    case "--incremental":
                        options.Incremental = true;
                        break;

                    case "--png-divisor":
                        if (!TryNextInt(args, ref i, error, arg, out int divisor)) return null;
                        if (divisor < 1)
                        {
                            error.WriteLine("--png-divisor must be 1 or more.");
                            return null;
                        }

                        options.PngDivisor = divisor;
                        break;

                    case "--ultrahdr":
                        options.UltraHdr = true;
                        break;

                    case "--gainmap-divisor":
                        if (!TryNextInt(args, ref i, error, arg, out int gainDivisor)) return null;
                        if (gainDivisor < 1)
                        {
                            error.WriteLine("--gainmap-divisor must be 1 or more.");
                            return null;
                        }

                        options.GainMapDivisor = gainDivisor;
                        break;

                    case "--synthesize":
                        if (!TryNext(args, ref i, error, arg, out string synthDir)) return null;
                        options.SynthesizeDirectory = synthDir;
                        break;

                    case "--retag":
                        if (!TryNext(args, ref i, error, arg, out string retagValue)) return null;
                        if (!Enum.TryParse(retagValue, true, out HdrCorpusScenario retagScenario))
                        {
                            error.WriteLine($"Unknown scenario '{retagValue}'. Valid: " +
                                string.Join(", ", Enum.GetNames<HdrCorpusScenario>()));
                            return null;
                        }

                        options.RetagScenarioOrNull = retagScenario;
                        break;

                    case "--retag-label":
                        if (!TryNext(args, ref i, error, arg, out string retagLabel)) return null;
                        options.RetagLabel = retagLabel;
                        break;

                    case "--yes":
                        options.RetagConfirmed = true;
                        break;

                    default:
                        error.WriteLine($"Unknown argument '{arg}'.");
                        return null;
                }
            }

            // --synthesize only writes patterns; it needs no corpus to read.
            if (!string.IsNullOrWhiteSpace(options.SynthesizeDirectory))
            {
                return options;
            }

            if (options.RetagScenarioOrNull.HasValue)
            {
                if (string.IsNullOrWhiteSpace(options.CorpusDirectory))
                {
                    error.WriteLine("--retag needs --corpus.");
                    return null;
                }

                if (!Directory.Exists(options.CorpusDirectory))
                {
                    error.WriteLine($"Corpus directory not found: {options.CorpusDirectory}");
                    return null;
                }

                return options;
            }

            if (string.IsNullOrWhiteSpace(options.CorpusDirectory))
            {
                error.WriteLine("--corpus is required.");
                return null;
            }

            if (!Directory.Exists(options.CorpusDirectory))
            {
                error.WriteLine($"Corpus directory not found: {options.CorpusDirectory}");
                return null;
            }

            if (modesGiven && options.Modes.Count == 0)
            {
                error.WriteLine("--modes listed no valid modes.");
                return null;
            }

            if (exposuresGiven && options.Exposures.Count == 0)
            {
                error.WriteLine("--exposures listed no valid exposures.");
                return null;
            }

            if (string.IsNullOrWhiteSpace(options.OutputDirectory))
            {
                options.OutputDirectory = Path.Combine(options.CorpusDirectory, "eval");
            }

            if (!string.IsNullOrEmpty(options.BaselineCsv) && !File.Exists(options.BaselineCsv))
            {
                error.WriteLine($"Baseline CSV not found: {options.BaselineCsv}");
                return null;
            }

            // A contact sheet needs the thumbnail exposure to actually be measured. If the user
            // narrowed the sweep and did not name one, use the closest exposure they asked for.
            if (options.Exposures.Count > 0 &&
                !options.Exposures.Any(e => Math.Abs(e - options.ThumbnailExposure) < 0.001f))
            {
                float closest = options.Exposures.OrderBy(e => Math.Abs(e - options.ThumbnailExposure)).First();
                if (thumbExposureGiven)
                {
                    error.WriteLine($"Note: --thumb-exposure " +
                        $"{options.ThumbnailExposure.ToString("0.00", CultureInfo.InvariantCulture)} is not in " +
                        $"--exposures; the contact sheet will use " +
                        $"{closest.ToString("0.00", CultureInfo.InvariantCulture)}.");
                }

                options.ThumbnailExposure = closest;
            }

            return options;
        }

        private static IEnumerable<string> Split(string value) =>
            value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        private static bool TryNext(string[] args, ref int i, TextWriter error, string arg, out string value)
        {
            if (i + 1 >= args.Length)
            {
                error.WriteLine($"{arg} needs a value.");
                value = null;
                return false;
            }

            value = args[++i];
            return true;
        }

        private static bool TryNextInt(string[] args, ref int i, TextWriter error, string arg, out int value)
        {
            value = 0;
            if (!TryNext(args, ref i, error, arg, out string text))
            {
                return false;
            }

            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                error.WriteLine($"{arg} expects a whole number, got '{text}'.");
                return false;
            }

            return true;
        }

        private static bool TryNextFloat(string[] args, ref int i, TextWriter error, string arg, out float value)
        {
            value = 0f;
            if (!TryNext(args, ref i, error, arg, out string text))
            {
                return false;
            }

            if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                error.WriteLine($"{arg} expects a number, got '{text}'.");
                return false;
            }

            return true;
        }
    }
}
