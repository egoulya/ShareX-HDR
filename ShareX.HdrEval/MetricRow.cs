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
using System.Text;

namespace ShareX.HdrEval
{
    /// <summary>One measured (frame, mode, exposure) combination.</summary>
    public sealed class MetricRow
    {
        // --- identity -------------------------------------------------------
        public string FrameFile { get; set; }
        public string Label { get; set; }
        public HdrCorpusScenario Scenario { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public int DxgiFormat { get; set; }
        public float SdrWhiteNits { get; set; }

        public HdrTonemapMode RequestedMode { get; set; }
        public HdrTonemapMode ResolvedMode { get; set; }
        public float Exposure { get; set; }

        // --- what Auto saw --------------------------------------------------
        public float StatsMaxLuminance { get; set; }
        public float StatsP99 { get; set; }
        public float StatsFractionAboveOne { get; set; }
        public float StatsFractionHotUpperSdr { get; set; }

        // --- frame character ------------------------------------------------
        public long SampleCount { get; set; }
        public float RefPeakNits { get; set; }
        public float RefMeanNits { get; set; }
        public float OutMeanLuminance { get; set; }

        /// <summary>Fraction of pixels with any channel above SDR white.</summary>
        public float HighlightFraction { get; set; }

        /// <summary>Fraction of pixels that fit entirely inside SDR, so passthrough is defined.</summary>
        public float InRangeFraction { get; set; }

        // --- clipping -------------------------------------------------------
        /// <summary>
        /// Fraction of pixels with any channel at 255. COMPARATIVE ONLY: how much of a frame clips
        /// depends entirely on how much of it sits above SDR range, so there is no content-independent
        /// bar. Saturated primaries cannot be held without clipping a channel at all.
        /// </summary>
        public float ClipHighFraction { get; set; }

        /// <summary>
        /// Of the pixels that were above SDR range, the fraction that hard-clipped to 255 rather
        /// than being compressed to a distinct value. This is the readout on whether the shoulder is
        /// rolling off or saturating early. Comparative: diff it against a baseline.
        /// </summary>
        public float HighlightClipFraction { get; set; }

        /// <summary>
        /// Output code range (1st..99th percentile) that above-range content is spread across, and
        /// the same range in the peak-normalized reference. The ratio is how much highlight detail
        /// survived. Comparative: how much range highlights occupy in the source is a property of
        /// the frame. -1 when the frame has no out-of-range content.
        /// </summary>
        public int HighlightSpanCodes { get; set; } = -1;

        public int ReferenceHighlightSpanCodes { get; set; } = -1;

        /// <summary>
        /// The damning one: content whose every channel was at or below 0.90 - clear of the top of
        /// the range - that got blown out to 255 anyway.
        /// </summary>
        public float ClipBelowCeilingFraction { get; set; }

        public float ClipLowFraction { get; set; }
        public float CrushedShadowFraction { get; set; }

        // --- round trip (absolute meaning; this is the scenario-1 ground truth)
        /// <summary>Worst 8-bit deviation from a pure passthrough, over pixels that fit inside SDR.</summary>
        public int PassthroughMaxCodeDelta { get; set; } = -1;

        public int PassthroughP99CodeDelta { get; set; } = -1;
        public long PassthroughSampleCount { get; set; }

        // --- paper white (absolute meaning) ---------------------------------
        public float PaperWhiteMeanCode { get; set; }
        public int PaperWhiteSampleCount { get; set; }

        // --- colour ---------------------------------------------------------
        /// <summary>Chroma-weighted mean hue rotation over in-SDR-range pixels.</summary>
        public float HueDriftDegrees { get; set; }

        /// <summary>Same, over pixels above SDR white. Largely a design choice, so reported only.</summary>
        public float HueDriftHighlightDegrees { get; set; }

        public float HueDriftMaxDegrees { get; set; }

        /// <summary>Output chroma over reference chroma, in-range pixels. Below 1.0 is desaturation.</summary>
        public float ChromaRatio { get; set; }

        /// <summary>Same, in the highlights, where some desaturation is the intended trade.</summary>
        public float ChromaRatioHighlight { get; set; }

        // --- tone -----------------------------------------------------------
        public float MidToneShiftCode { get; set; }
        public int MidToneSampleCount { get; set; }

        // --- detail (COMPARATIVE ONLY - never thresholded) ------------------
        public float LocalContrastRatio { get; set; }
        public float LocalContrastRatioP10 { get; set; }
        public int ContrastTileCount { get; set; }

        // --- banding --------------------------------------------------------
        public int OccupiedLuminanceCodes { get; set; }
        public int MaxLuminanceCodeGap { get; set; }

        /// <summary>The reference's own code gap. Discrete source content has gaps that are not banding.</summary>
        public int ReferenceLuminanceCodeGap { get; set; }

        public int MonotonicityViolations { get; set; }

        // --- bookkeeping ----------------------------------------------------
        public string PngPath { get; set; }
        public string ThumbPath { get; set; }

        /// <summary>
        /// Per-frame HDR reference (cICP PQ PNG). Same value on every row of a frame; presentation
        /// only, so it is not serialized to CSV.
        /// </summary>
        public string ReferencePath { get; set; }

        public string ReferenceThumbPath { get; set; }

        /// <summary>SDR view of the original, hard-clipped at paper white. Viewable anywhere.</summary>
        public string ReferenceInRangePath { get; set; }

        /// <summary>SDR view of the original, linearly scaled so the peak lands at 1.0.</summary>
        public string ReferenceFullRangePath { get; set; }
        public long ElapsedMs { get; set; }
        public string Error { get; set; }

        /// <summary>
        /// Identity of the build that produced this row. Incremental runs refuse to reuse a row
        /// whose build differs, because a rebuilt ScreenCaptureLib is exactly when tonemap output
        /// can change - reusing across that would silently report stale results as current.
        /// </summary>
        public string ToolBuildId { get; set; }

        /// <summary>Threshold breaches, in check order. Empty means nothing flagged.</summary>
        public List<string> Flags { get; } = new();

        /// <summary>MVID of ShareX.ScreenCaptureLib: changes on every rebuild of the tonemap code.</summary>
        public static string CurrentBuildId { get; } =
            typeof(HdrTonemap).Assembly.ManifestModule.ModuleVersionId.ToString("N");

        public string Key => $"{FrameFile}|{RequestedMode}|{Exposure.ToString("0.00", CultureInfo.InvariantCulture)}";

        public static string CsvHeader =>
            "FrameFile,Label,Scenario,Width,Height,DxgiFormat,SdrWhiteNits," +
            "RequestedMode,ResolvedMode,Exposure," +
            "StatsMaxLuminance,StatsP99,StatsFractionAboveOne,StatsFractionHotUpperSdr," +
            "SampleCount,RefPeakNits,RefMeanNits,OutMeanLuminance,HighlightFraction,InRangeFraction," +
            "ClipHighFraction,HighlightClipFraction,HighlightSpanCodes,ReferenceHighlightSpanCodes," +
            "ClipBelowCeilingFraction,ClipLowFraction," +
            "CrushedShadowFraction," +
            "PassthroughMaxCodeDelta,PassthroughP99CodeDelta,PassthroughSampleCount," +
            "PaperWhiteMeanCode,PaperWhiteSampleCount," +
            "HueDriftDegrees,HueDriftHighlightDegrees,HueDriftMaxDegrees,ChromaRatio,ChromaRatioHighlight," +
            "MidToneShiftCode,MidToneSampleCount," +
            "LocalContrastRatio,LocalContrastRatioP10,ContrastTileCount," +
            "OccupiedLuminanceCodes,MaxLuminanceCodeGap,ReferenceLuminanceCodeGap,MonotonicityViolations," +
            "PngPath,ThumbPath,ReferencePath,ReferenceThumbPath,ReferenceInRangePath,ReferenceFullRangePath," +
            "ToolBuildId,Flags,ElapsedMs,Error";

        public string ToCsv()
        {
            StringBuilder sb = new StringBuilder();
            Append(sb, FrameFile);
            Append(sb, Label);
            Append(sb, Scenario.ToString());
            Append(sb, Width);
            Append(sb, Height);
            Append(sb, DxgiFormat);
            Append(sb, SdrWhiteNits, "0.##");
            Append(sb, RequestedMode.ToString());
            Append(sb, ResolvedMode.ToString());
            Append(sb, Exposure, "0.00");
            Append(sb, StatsMaxLuminance, "0.0000");
            Append(sb, StatsP99, "0.0000");
            Append(sb, StatsFractionAboveOne, "0.000000");
            Append(sb, StatsFractionHotUpperSdr, "0.000000");
            Append(sb, SampleCount);
            Append(sb, RefPeakNits, "0.##");
            Append(sb, RefMeanNits, "0.##");
            Append(sb, OutMeanLuminance, "0.0000");
            Append(sb, HighlightFraction, "0.000000");
            Append(sb, InRangeFraction, "0.000000");
            Append(sb, ClipHighFraction, "0.000000");
            Append(sb, HighlightClipFraction, "0.000000");
            Append(sb, HighlightSpanCodes);
            Append(sb, ReferenceHighlightSpanCodes);
            Append(sb, ClipBelowCeilingFraction, "0.000000");
            Append(sb, ClipLowFraction, "0.000000");
            Append(sb, CrushedShadowFraction, "0.000000");
            Append(sb, PassthroughMaxCodeDelta);
            Append(sb, PassthroughP99CodeDelta);
            Append(sb, PassthroughSampleCount);
            Append(sb, PaperWhiteMeanCode, "0.00");
            Append(sb, PaperWhiteSampleCount);
            Append(sb, HueDriftDegrees, "0.000");
            Append(sb, HueDriftHighlightDegrees, "0.000");
            Append(sb, HueDriftMaxDegrees, "0.000");
            Append(sb, ChromaRatio, "0.0000");
            Append(sb, ChromaRatioHighlight, "0.0000");
            Append(sb, MidToneShiftCode, "0.00");
            Append(sb, MidToneSampleCount);
            Append(sb, LocalContrastRatio, "0.0000");
            Append(sb, LocalContrastRatioP10, "0.0000");
            Append(sb, ContrastTileCount);
            Append(sb, OccupiedLuminanceCodes);
            Append(sb, MaxLuminanceCodeGap);
            Append(sb, ReferenceLuminanceCodeGap);
            Append(sb, MonotonicityViolations);
            Append(sb, PngPath);
            Append(sb, ThumbPath);
            Append(sb, ReferencePath);
            Append(sb, ReferenceThumbPath);
            Append(sb, ReferenceInRangePath);
            Append(sb, ReferenceFullRangePath);
            Append(sb, ToolBuildId);
            Append(sb, string.Join("; ", Flags));
            Append(sb, ElapsedMs);
            Append(sb, Error, last: true);
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string value, bool last = false)
        {
            if (!string.IsNullOrEmpty(value) && (value.Contains(',') || value.Contains('"') || value.Contains('\n')))
            {
                sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
            }
            else
            {
                sb.Append(value);
            }

            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void Append(StringBuilder sb, long value, bool last = false)
        {
            sb.Append(value.ToString(CultureInfo.InvariantCulture));
            if (!last)
            {
                sb.Append(',');
            }
        }

        private static void Append(StringBuilder sb, float value, string format, bool last = false)
        {
            sb.Append(float.IsNaN(value) ? string.Empty : value.ToString(format, CultureInfo.InvariantCulture));
            if (!last)
            {
                sb.Append(',');
            }
        }
    }

    /// <summary>
    /// Per-scenario pass bands, applied only to metrics that have absolute meaning.
    ///
    /// These numbers are STARTING POINTS, not authority. Calibrate them once against outputs you
    /// have already judged by eye. A flag means "this differs from the reference more than the band
    /// allows", never "this looks bad".
    ///
    /// Deliberately NOT thresholded, because they depend on content or measure the tonemapper
    /// doing its job rather than failing at it: total clip-high and highlight-clip fraction (how much
    /// of a frame is above range is a property of the frame), local contrast retention (compressing
    /// headroom reduces log-contrast by definition), and mid-tone placement outside the Desktop
    /// scenario. Those are reported and diffed against a baseline instead.
    ///
    /// Highlight hue and chroma used to be on that list, described as the roll-off's trade to make.
    /// That was a mistake: a gamut fit exists that holds both, so a loss there is a defect and is now
    /// gated. The lesson generalises - before calling a metric comparative, check whether some
    /// approach achieves the ideal. If one does, the metric is absolute.
    ///
    /// What remains is a set where every flag means the output is wrong, not merely different.
    ///
    /// Desktop is tight because it has a real ground truth: known sRGB composited at SDR white must
    /// survive the round trip. Native HDR is loose because it has no ground truth at all.
    /// </summary>
    public sealed class MetricThresholds
    {
        public float MaxClipBelowCeilingFraction { get; init; }
        public float MaxCrushedShadowFraction { get; init; }
        public float MaxHueDriftDegrees { get; init; }
        public float MinChromaRatio { get; init; }

        /// <summary>
        /// Chroma floor for out-of-gamut highlights. This was left unthresholded on the reasoning
        /// that highlight colour is "the roll-off's trade to make", and that reasoning was wrong: a
        /// gamut fit that scales the colour instead of desaturating it holds chroma at 1.000, so
        /// losing two thirds of it was never a trade, it was a defect. It went unreported on frames
        /// where it was obvious to the eye. Negative disables the check.
        /// </summary>
        public float MinChromaRatioHighlight { get; init; } = -1f;

        /// <summary>Hue drift ceiling for out-of-gamut highlights. Negative disables the check.</summary>
        public float MaxHueDriftHighlightDegrees { get; init; } = -1f;
        public float MinPaperWhiteCode { get; init; }

        /// <summary>Round-trip budget in 8-bit codes. Negative disables the check.</summary>
        public int MaxPassthroughCodeDelta { get; init; } = -1;

        /// <summary>Mid-tone placement budget. Negative disables the check.</summary>
        public float MaxAbsMidToneShiftCode { get; init; } = -1f;

        /// <summary>How much worse than the reference's own code gap counts as banding.</summary>
        public int BandingGapMargin { get; init; } = 2;

        public static MetricThresholds For(HdrCorpusScenario scenario) => scenario switch
        {
            // Exact ground truth: in-range content must come back unchanged.
            HdrCorpusScenario.Desktop => new MetricThresholds
            {
                MinChromaRatioHighlight = 0.90f,
                MaxHueDriftHighlightDegrees = 3.0f,
                MaxClipBelowCeilingFraction = 0.001f,
                MaxCrushedShadowFraction = 0.002f,
                MaxHueDriftDegrees = 1.5f,
                MinChromaRatio = 0.97f,
                MinPaperWhiteCode = 253f,
                MaxPassthroughCodeDelta = 3,
                MaxAbsMidToneShiftCode = 3f,
                BandingGapMargin = 1
            },

            // Derivable ground truth (the same scene with Auto-HDR off), but tone placement is
            // deliberately different, so only structural defects are gated.
            HdrCorpusScenario.AutoHdr => new MetricThresholds
            {
                MinChromaRatioHighlight = 0.88f,
                MaxHueDriftHighlightDegrees = 4.0f,
                MaxClipBelowCeilingFraction = 0.01f,
                MaxCrushedShadowFraction = 0.005f,
                MaxHueDriftDegrees = 3.0f,
                MinChromaRatio = 0.90f,
                MinPaperWhiteCode = 0f, // Auto-HDR maps paper white below 255 on purpose.
                MaxPassthroughCodeDelta = -1,
                MaxAbsMidToneShiftCode = -1f,
                BandingGapMargin = 2
            },

            // No ground truth. Only defects that are wrong regardless of artistic intent.
            _ => new MetricThresholds
            {
                MinChromaRatioHighlight = 0.85f,
                MaxHueDriftHighlightDegrees = 5.0f,
                MaxClipBelowCeilingFraction = 0.02f,
                MaxCrushedShadowFraction = 0.01f,
                MaxHueDriftDegrees = 5.0f,
                MinChromaRatio = 0.85f,
                MinPaperWhiteCode = 0f,
                MaxPassthroughCodeDelta = -1,
                MaxAbsMidToneShiftCode = -1f,
                BandingGapMargin = 2
            }
        };

        public void Apply(MetricRow row)
        {
            row.Flags.Clear();

            Check(row, row.ClipBelowCeilingFraction > MaxClipBelowCeilingFraction,
                $"blew out in-range content: {row.ClipBelowCeilingFraction:P3} of pixels at or below 0.90 hit 255");

            Check(row, row.CrushedShadowFraction > MaxCrushedShadowFraction,
                $"crushed shadows {row.CrushedShadowFraction:P3} > {MaxCrushedShadowFraction:P3}");

            Check(row, MaxPassthroughCodeDelta >= 0 && row.PassthroughSampleCount > 0 &&
                row.PassthroughP99CodeDelta > MaxPassthroughCodeDelta,
                $"round trip off by {row.PassthroughP99CodeDelta} codes at P99 " +
                $"(max {row.PassthroughMaxCodeDelta}), budget {MaxPassthroughCodeDelta}");

            Check(row, !float.IsNaN(row.HueDriftDegrees) && row.HueDriftDegrees > MaxHueDriftDegrees,
                $"hue drift {row.HueDriftDegrees:0.00}deg > {MaxHueDriftDegrees:0.00}deg (in-range pixels)");

            Check(row, !float.IsNaN(row.ChromaRatio) && row.ChromaRatio < MinChromaRatio,
                $"desaturated in-range colour: chroma ratio {row.ChromaRatio:0.000} < {MinChromaRatio:0.000}");

            Check(row, MaxAbsMidToneShiftCode >= 0f && !float.IsNaN(row.MidToneShiftCode) &&
                Math.Abs(row.MidToneShiftCode) > MaxAbsMidToneShiftCode,
                $"mid-tone shift {row.MidToneShiftCode:+0.0;-0.0} codes exceeds +/-{MaxAbsMidToneShiftCode:0}");

            // Only banding beyond what the source itself has. Discrete content (UI, colour bars)
            // carries large gaps of its own.
            int bandingBudget = Math.Max(row.ReferenceLuminanceCodeGap, 1) + BandingGapMargin;
            Check(row, row.MaxLuminanceCodeGap > bandingBudget,
                $"banding: code gap {row.MaxLuminanceCodeGap} vs reference gap " +
                $"{row.ReferenceLuminanceCodeGap} (budget {bandingBudget})");

            Check(row, MinChromaRatioHighlight > 0f && !float.IsNaN(row.ChromaRatioHighlight) &&
                row.ChromaRatioHighlight < MinChromaRatioHighlight,
                $"desaturated highlights: chroma ratio {row.ChromaRatioHighlight:0.000} < {MinChromaRatioHighlight:0.000}");

            Check(row, MaxHueDriftHighlightDegrees > 0f && !float.IsNaN(row.HueDriftHighlightDegrees) &&
                row.HueDriftHighlightDegrees > MaxHueDriftHighlightDegrees,
                $"highlight hue drift {row.HueDriftHighlightDegrees:0.00}deg > {MaxHueDriftHighlightDegrees:0.00}deg");

            Check(row, row.MonotonicityViolations > 0,
                $"transfer curve reverses in {row.MonotonicityViolations} bin(s)");

            Check(row, MinPaperWhiteCode > 0f && row.PaperWhiteSampleCount >= 64 &&
                row.PaperWhiteMeanCode < MinPaperWhiteCode,
                $"paper white lands at {row.PaperWhiteMeanCode:0.0}, below {MinPaperWhiteCode:0}");
        }

        private static void Check(MetricRow row, bool failed, string message)
        {
            if (failed)
            {
                row.Flags.Add(message);
            }
        }
    }
}
