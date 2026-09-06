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
using System.Net;
using System.Text;

namespace ShareX.HdrEval
{
    public static class ReportWriter
    {
        public static void WriteCsv(string path, List<MetricRow> rows)
        {
            using StreamWriter writer = new StreamWriter(path, false, new UTF8Encoding(false));
            writer.WriteLine(MetricRow.CsvHeader);
            foreach (MetricRow row in rows)
            {
                writer.WriteLine(row.ToCsv());
            }
        }

        public static List<MetricRow> ReadCsv(string path)
        {
            List<MetricRow> rows = new List<MetricRow>();
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 2)
            {
                return rows;
            }

            string[] header = SplitCsv(lines[0]);
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < header.Length; i++)
            {
                index[header[i]] = i;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (string.IsNullOrWhiteSpace(lines[i]))
                {
                    continue;
                }

                string[] fields = SplitCsv(lines[i]);
                MetricRow row = new MetricRow
                {
                    FrameFile = Get(fields, index, "FrameFile"),
                    Label = Get(fields, index, "Label"),
                    Scenario = ParseEnum<HdrCorpusScenario>(Get(fields, index, "Scenario")),
                    Width = ParseInt(Get(fields, index, "Width")),
                    Height = ParseInt(Get(fields, index, "Height")),
                    DxgiFormat = ParseInt(Get(fields, index, "DxgiFormat")),
                    SdrWhiteNits = ParseFloat(Get(fields, index, "SdrWhiteNits")),
                    RequestedMode = ParseEnum<HdrTonemapMode>(Get(fields, index, "RequestedMode")),
                    ResolvedMode = ParseEnum<HdrTonemapMode>(Get(fields, index, "ResolvedMode")),
                    Exposure = ParseFloat(Get(fields, index, "Exposure")),
                    StatsMaxLuminance = ParseFloat(Get(fields, index, "StatsMaxLuminance")),
                    StatsP99 = ParseFloat(Get(fields, index, "StatsP99")),
                    StatsFractionAboveOne = ParseFloat(Get(fields, index, "StatsFractionAboveOne")),
                    StatsFractionHotUpperSdr = ParseFloat(Get(fields, index, "StatsFractionHotUpperSdr")),
                    SampleCount = ParseLong(Get(fields, index, "SampleCount")),
                    RefPeakNits = ParseFloat(Get(fields, index, "RefPeakNits")),
                    RefMeanNits = ParseFloat(Get(fields, index, "RefMeanNits")),
                    OutMeanLuminance = ParseFloat(Get(fields, index, "OutMeanLuminance")),
                    HighlightFraction = ParseFloat(Get(fields, index, "HighlightFraction")),
                    InRangeFraction = ParseFloat(Get(fields, index, "InRangeFraction")),
                    ClipHighFraction = ParseFloat(Get(fields, index, "ClipHighFraction")),
                    HighlightClipFraction = ParseFloat(Get(fields, index, "HighlightClipFraction")),
                    HighlightSpanCodes = ParseInt(Get(fields, index, "HighlightSpanCodes"), -1),
                    ReferenceHighlightSpanCodes = ParseInt(Get(fields, index, "ReferenceHighlightSpanCodes"), -1),
                    ClipBelowCeilingFraction = ParseFloat(Get(fields, index, "ClipBelowCeilingFraction")),
                    ClipLowFraction = ParseFloat(Get(fields, index, "ClipLowFraction")),
                    CrushedShadowFraction = ParseFloat(Get(fields, index, "CrushedShadowFraction")),
                    PassthroughMaxCodeDelta = ParseInt(Get(fields, index, "PassthroughMaxCodeDelta"), -1),
                    PassthroughP99CodeDelta = ParseInt(Get(fields, index, "PassthroughP99CodeDelta"), -1),
                    PassthroughSampleCount = ParseLong(Get(fields, index, "PassthroughSampleCount")),
                    PaperWhiteMeanCode = ParseFloat(Get(fields, index, "PaperWhiteMeanCode")),
                    PaperWhiteSampleCount = ParseInt(Get(fields, index, "PaperWhiteSampleCount")),
                    HueDriftDegrees = ParseFloat(Get(fields, index, "HueDriftDegrees")),
                    HueDriftHighlightDegrees = ParseFloat(Get(fields, index, "HueDriftHighlightDegrees")),
                    ChromaticityErrorP50 = ParseFloat(Get(fields, index, "ChromaticityErrorP50")),
                    ChromaticityErrorP95 = ParseFloat(Get(fields, index, "ChromaticityErrorP95")),
                    ChromaticityErrorP99 = ParseFloat(Get(fields, index, "ChromaticityErrorP99")),
                    ChromaticityAboveJndFraction = ParseFloat(Get(fields, index, "ChromaticityAboveJndFraction")),
                    ChromaRatio = ParseFloat(Get(fields, index, "ChromaRatio")),
                    ChromaRatioHighlight = ParseFloat(Get(fields, index, "ChromaRatioHighlight")),
                    MidToneShiftCode = ParseFloat(Get(fields, index, "MidToneShiftCode")),
                    MidToneSampleCount = ParseInt(Get(fields, index, "MidToneSampleCount")),
                    LocalContrastRatio = ParseFloat(Get(fields, index, "LocalContrastRatio")),
                    LocalContrastRatioP10 = ParseFloat(Get(fields, index, "LocalContrastRatioP10")),
                    ContrastTileCount = ParseInt(Get(fields, index, "ContrastTileCount")),
                    OccupiedLuminanceCodes = ParseInt(Get(fields, index, "OccupiedLuminanceCodes")),
                    MaxLuminanceCodeGap = ParseInt(Get(fields, index, "MaxLuminanceCodeGap")),
                    ReferenceLuminanceCodeGap = ParseInt(Get(fields, index, "ReferenceLuminanceCodeGap")),
                    MonotonicityViolations = ParseInt(Get(fields, index, "MonotonicityViolations")),
                    PngPath = NullIfEmpty(Get(fields, index, "PngPath")),
                    ThumbPath = NullIfEmpty(Get(fields, index, "ThumbPath")),
                    ReferencePath = NullIfEmpty(Get(fields, index, "ReferencePath")),
                    ReferenceThumbPath = NullIfEmpty(Get(fields, index, "ReferenceThumbPath")),
                    ReferenceInRangePath = NullIfEmpty(Get(fields, index, "ReferenceInRangePath")),
                    ReferenceFullRangePath = NullIfEmpty(Get(fields, index, "ReferenceFullRangePath")),
                    ToolBuildId = NullIfEmpty(Get(fields, index, "ToolBuildId")),
                    ElapsedMs = ParseLong(Get(fields, index, "ElapsedMs")),
                    Error = NullIfEmpty(Get(fields, index, "Error"))
                };

                string flags = Get(fields, index, "Flags");
                if (!string.IsNullOrWhiteSpace(flags))
                {
                    row.Flags.AddRange(flags.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
                }

                rows.Add(row);
            }

            return rows;
        }

        // ====================================================================
        // HTML contact sheet
        // ====================================================================

        private sealed record MetricColumn(
            string Name,
            Func<MetricRow, string> Format,
            Func<MetricRow, MetricThresholds, bool> IsBad,
            string Tooltip);

        private static readonly MetricColumn[] Columns =
        {
            new("resolved", r => r.ResolvedMode.ToString(), (r, t) => false,
                "What Auto actually decided, or the mode you asked for."),
            new("round trip", r => r.PassthroughSampleCount > 0
                    ? r.PassthroughP99CodeDelta + " / " + r.PassthroughMaxCodeDelta
                    : "&ndash;",
                (r, t) => t.MaxPassthroughCodeDelta >= 0 && r.PassthroughSampleCount > 0 &&
                    r.PassthroughP99CodeDelta > t.MaxPassthroughCodeDelta,
                "P99 / max 8-bit deviation from a pure passthrough, over pixels that fit inside SDR. " +
                "This is the scenario-1 ground truth: in-range content must survive unchanged."),
            new("clip hi", r => Pct(r.ClipHighFraction), (r, t) => false,
                "Fraction of pixels with any channel at 255. COMPARATIVE ONLY: how much clips depends " +
                "on how much of the frame is above SDR range, and saturated primaries cannot be held " +
                "without clipping a channel."),
            new("hl clip", r => Pct(r.HighlightClipFraction), (r, t) => false,
                "Of the pixels above SDR range, the fraction that hard-clipped to 255 instead of being " +
                "compressed to a distinct value. Whether the shoulder rolls off or saturates early. " +
                "Comparative: watch it against a baseline."),
            new("hl span", r => r.HighlightSpanCodes < 0 ? "&ndash;"
                    : r.HighlightSpanCodes + " / " + r.ReferenceHighlightSpanCodes, (r, t) => false,
                "Output code range the above-range content is spread across, against the same range " +
                "in the peak-normalized reference. This is what \"detail in the bright parts\" is: " +
                "highlights crowded into a narrow band read as flat even when every value is " +
                "technically distinct. Comparative - the reference span is a property of the frame."),
            new("blew out", r => Pct(r.ClipBelowCeilingFraction),
                (r, t) => r.ClipBelowCeilingFraction > t.MaxClipBelowCeilingFraction,
                "Content whose every channel was at or below 0.90 that got clipped to 255 anyway. " +
                "The damning one."),
            new("crushed", r => Pct(r.CrushedShadowFraction),
                (r, t) => r.CrushedShadowFraction > t.MaxCrushedShadowFraction,
                "Reference had signal, output is pure black."),
            new("hue", r => Num(r.HueDriftDegrees, "0.00") + "&deg;",
                (r, t) => !float.IsNaN(r.HueDriftDegrees) && r.HueDriftDegrees > t.MaxHueDriftDegrees,
                "Chroma-weighted mean hue rotation over in-range pixels, measured at unit luminance."),
            new("chroma", r => Num(r.ChromaRatio, "0.000"),
                (r, t) => !float.IsNaN(r.ChromaRatio) && r.ChromaRatio < t.MinChromaRatio,
                "In-range output chroma over reference chroma. Below 1.0 is desaturation."),
            new("hue hi", r => Num(r.HueDriftHighlightDegrees, "0.00") + "&deg;",
                (r, t) => t.MaxHueDriftHighlightDegrees > 0f && !float.IsNaN(r.HueDriftHighlightDegrees) &&
                    r.HueDriftHighlightDegrees > t.MaxHueDriftHighlightDegrees,
                "Hue rotation of out-of-gamut highlights. Gated: a gamut fit that scales the colour " +
                "rather than desaturating it holds this near zero, so drift here is a defect."),
            new("chroma hi", r => Num(r.ChromaRatioHighlight, "0.000"),
                (r, t) => t.MinChromaRatioHighlight > 0f && !float.IsNaN(r.ChromaRatioHighlight) &&
                    r.ChromaRatioHighlight < t.MinChromaRatioHighlight,
                "Chroma retained in out-of-gamut highlights. Gated: this reached 0.33 unreported " +
                "while being obvious to the eye, which is what prompted gating it."),
            new("mid-tone", r => Num(r.MidToneShiftCode, "+0.0;-0.0;0"),
                (r, t) => t.MaxAbsMidToneShiftCode >= 0f && !float.IsNaN(r.MidToneShiftCode) &&
                    Math.Abs(r.MidToneShiftCode) > t.MaxAbsMidToneShiftCode,
                "Median mid-tone output minus a pure passthrough, in 8-bit codes."),
            new("paper white", r => r.PaperWhiteSampleCount > 0 ? Num(r.PaperWhiteMeanCode, "0.0") : "&ndash;",
                (r, t) => t.MinPaperWhiteCode > 0f && r.PaperWhiteSampleCount >= 64 &&
                    r.PaperWhiteMeanCode < t.MinPaperWhiteCode,
                "Where known SDR white landed. Should be 253-255 for Desktop."),
            new("contrast P10", r => Num(r.LocalContrastRatioP10, "0.000"), (r, t) => false,
                "10th-percentile per-tile log-contrast retention. COMPARATIVE ONLY: compressing " +
                "headroom into SDR reduces log-contrast by definition, so a low value on an HDR " +
                "frame is the tonemapper working. Compare across modes and against a baseline."),
            new("code gap", r => r.MaxLuminanceCodeGap + " / " + r.ReferenceLuminanceCodeGap,
                (r, t) => r.MaxLuminanceCodeGap > Math.Max(r.ReferenceLuminanceCodeGap, 1) + t.BandingGapMargin,
                "Output / reference widest gap between used luminance codes. Only a gap well beyond " +
                "the reference own gap is banding: discrete source content has gaps of its own."),
            new("mono", r => r.MonotonicityViolations.ToString(CultureInfo.InvariantCulture),
                (r, t) => r.MonotonicityViolations > 0,
                "Bins where brighter input produced darker output. Any non-zero is a real artifact.")
        };

        public static void WriteHtml(string path, List<MetricRow> rows, EvalOptions options,
            List<MetricRow> baseline, TimeSpan elapsed)
        {
            StringBuilder html = new StringBuilder();
            AppendHead(html);

            html.AppendLine("<h1>HDR tonemap evaluation</h1>");
            html.Append("<p class=\"meta\">");
            html.Append($"corpus <code>{Esc(options.CorpusDirectory)}</code> &middot; ");
            html.Append($"{rows.Select(r => r.FrameFile).Distinct().Count()} frame(s) &middot; ");
            html.Append($"{rows.Count} measurement(s) &middot; ");
            html.Append($"{elapsed.TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s &middot; ");
            html.Append($"{DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}");
            html.AppendLine("</p>");

            AppendCallout(html);
            AppendSummary(html, rows);

            if (baseline != null && baseline.Count > 0)
            {
                AppendBaselineDiff(html, rows, baseline);
            }

            int setIndex = 0;
            foreach (var group in rows.GroupBy(r => r.FrameFile).OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                AppendFrameSection(html, group.Key, group.ToList(), options, setIndex++);
            }

            AppendThresholdAppendix(html);
            ReportViewer.AppendMarkup(html, ReportViewer.BuildData(rows, options));
            html.AppendLine("</body></html>");

            File.WriteAllText(path, html.ToString(), new UTF8Encoding(false));
        }

        private static void AppendHead(StringBuilder html)
        {
            html.AppendLine("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\">");
            html.AppendLine("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">");
            html.AppendLine("<title>HDR tonemap evaluation</title><style>");
            html.AppendLine(@"
:root {
  --bg: #fbfaf8; --fg: #1c1b19; --muted: #6b6862; --line: #e2ded7;
  --card: #ffffff; --bad-bg: #fdeceb; --bad-fg: #a3271c; --good-fg: #2f6b3a;
  --warn-bg: #fdf6e3; --accent: #2f5f8f;
}
@media (prefers-color-scheme: dark) {
  :root {
    --bg: #17181a; --fg: #e8e6e3; --muted: #9a978f; --line: #2f3134;
    --card: #1f2124; --bad-bg: #3a1e1b; --bad-fg: #f0a094; --good-fg: #8fc79c;
    --warn-bg: #33291a; --accent: #8ab4de;
  }
}
* { box-sizing: border-box; }
body { margin: 0; padding: 28px 32px 64px; background: var(--bg); color: var(--fg);
  font: 14px/1.55 -apple-system, BlinkMacSystemFont, 'Segoe UI', Roboto, sans-serif; }
h1 { font-size: 22px; margin: 0 0 4px; letter-spacing: -0.01em; }
h2 { font-size: 16px; margin: 36px 0 10px; padding-bottom: 6px; border-bottom: 1px solid var(--line); }
h3 { font-size: 14px; margin: 0 0 2px; font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; }
p.meta { color: var(--muted); margin: 0 0 20px; font-size: 13px; }
code { font-family: ui-monospace, 'Cascadia Code', Consolas, monospace; font-size: 0.92em; }
.callout { background: var(--warn-bg); border-left: 3px solid var(--accent); padding: 10px 14px;
  margin: 0 0 24px; font-size: 13px; border-radius: 0 4px 4px 0; }
.callout p { margin: 0 0 6px; } .callout p:last-child { margin: 0; }
table { border-collapse: collapse; width: 100%; margin: 8px 0 4px; font-size: 12.5px; }
th, td { text-align: right; padding: 5px 8px; border-bottom: 1px solid var(--line); white-space: nowrap; }
th { font-weight: 600; color: var(--muted); text-align: right; font-size: 11.5px;
  text-transform: uppercase; letter-spacing: 0.04em; }
th:first-child, td:first-child { text-align: left; }
td.bad { background: var(--bad-bg); color: var(--bad-fg); font-weight: 600; }
td.mono, th.mono { font-family: ui-monospace, Consolas, monospace; }
.frame { background: var(--card); border: 1px solid var(--line); border-radius: 6px;
  padding: 16px 18px; margin: 0 0 20px; }
.frame-meta { color: var(--muted); font-size: 12.5px; margin: 0 0 14px; }
.sheet { display: flex; flex-wrap: wrap; gap: 14px; margin: 0 0 16px; }
.shot { flex: 0 0 auto; max-width: 340px; }
.shot img { display: block; width: 100%; height: auto; border: 1px solid var(--line);
  border-radius: 4px; background: #000; }
.shot .cap { font-size: 12px; margin-top: 5px; }
.shot .cap b { font-family: ui-monospace, Consolas, monospace; }
.shot.flagged img { outline: 2px solid var(--bad-fg); outline-offset: 1px; }
.shot.reference img { outline: 2px solid var(--accent); outline-offset: 1px; }
.shot.reference { position: relative; }
.shot .note { font-size: 11px; color: var(--muted); margin-top: 2px; line-height: 1.35; }
.flags { color: var(--bad-fg); font-size: 11.5px; margin-top: 3px; }
.ok { color: var(--good-fg); }
.scroll { overflow-x: auto; }
ul.tight { margin: 4px 0 0; padding-left: 20px; } ul.tight li { margin: 2px 0; }
.callout kbd { background: var(--line); border-radius: 3px; padding: 1px 5px; font-size: .9em;
  font-family: ui-monospace, Consolas, monospace; }
");
            ReportViewer.AppendStyles(html);
            html.AppendLine("</style></head><body>");
        }

        private static void AppendCallout(StringBuilder html)
        {
            html.AppendLine("<div class=\"callout\">");
            html.AppendLine("<p><b>What this does and does not tell you.</b> Every number here is a " +
                "comparison against the frame's own pre-tonemap reference. A flag means the output " +
                "differs from that reference by more than the scenario's band allows &mdash; it never " +
                "means the image looks bad.</p>");
            html.AppendLine("<p>Thresholds are starting points, not authority. Calibrate them once against " +
                "outputs you have already judged by eye. <b>Preference is not measurable here</b>: " +
                "ranking modes needs blind, randomized A/B.</p>");
            html.AppendLine("<p><b>Three views of the original</b> lead each contact sheet, outlined in " +
                "blue. <i>HDR original</i> is the untonemapped frame as a cICP PQ PNG: real HDR in Chrome " +
                "on an HDR display, browser-tonemapped anywhere else. <i>in-range</i> hard-clips at paper " +
                "white, showing exactly what was already inside SDR. <i>full range</i> scales the peak to " +
                "1.0, so nothing clips and you can see the detail a curve has to fit. The last two are " +
                "fixed transforms with no free parameters &mdash; they are not candidate curves and cannot " +
                "flatter any mode. Compare candidates <i>down</i> a frame's list; a metric is relative to " +
                "that frame's reference, so it never compares across frames.</p>");
            html.AppendLine("<p><b>Click any image</b> to open the fullscreen viewer. " +
                "<kbd>&larr;</kbd><kbd>&rarr;</kbd> step through the renderings of one frame, " +
                "<kbd>&uarr;</kbd><kbd>&darr;</kbd> move between frames holding your position in the set, " +
                "so you can flip the same patch of pixels between curves. The overlays fade after five " +
                "seconds and only the mouse brings them back &mdash; keys navigate without redrawing " +
                "anything next to what you are judging.</p>");
            html.AppendLine("</div>");
        }

        private static void AppendSummary(StringBuilder html, List<MetricRow> rows)
        {
            html.AppendLine("<h2>Summary by requested mode</h2>");
            html.AppendLine("<div class=\"scroll\"><table>");
            html.AppendLine("<tr><th>mode</th><th>rows</th><th>flagged</th><th>mean clip hi</th>" +
                "<th>mean blew out</th><th>mean hue</th><th>mean chroma</th><th>mean contrast P10</th>" +
                "<th>resolved as</th></tr>");

            // Failed measurements carry no ResolvedMode and would otherwise show up as the enum's
            // default, inventing an "Auto" resolution that never happened. They are counted in the
            // errors list below instead.
            foreach (var group in rows.Where(r => string.IsNullOrEmpty(r.Error))
                .GroupBy(r => r.RequestedMode)
                .OrderBy(g => g.Key.ToString()))
            {
                List<MetricRow> list = group.ToList();
                int flagged = list.Count(r => r.Flags.Count > 0);
                string resolved = string.Join(", ", list.GroupBy(r => r.ResolvedMode)
                    .OrderByDescending(g => g.Count())
                    .Select(g => $"{g.Key} x{g.Count()}"));

                html.Append("<tr>");
                html.Append($"<td>{group.Key}</td>");
                html.Append($"<td>{list.Count}</td>");
                html.Append($"<td class=\"{(flagged > 0 ? "bad" : "ok")}\">{flagged}</td>");
                html.Append($"<td>{Pct(Avg(list, r => r.ClipHighFraction))}</td>");
                html.Append($"<td>{Pct(Avg(list, r => r.ClipBelowCeilingFraction))}</td>");
                html.Append($"<td>{Num(Avg(list, r => r.HueDriftDegrees), "0.00")}&deg;</td>");
                html.Append($"<td>{Num(Avg(list, r => r.ChromaRatio), "0.000")}</td>");
                html.Append($"<td>{Num(Avg(list, r => r.LocalContrastRatioP10), "0.000")}</td>");
                html.Append($"<td style=\"text-align:left\">{Esc(resolved)}</td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</table></div>");

            List<MetricRow> errors = rows.Where(r => !string.IsNullOrEmpty(r.Error)).ToList();
            if (errors.Count > 0)
            {
                html.AppendLine($"<p class=\"flags\">{errors.Count} measurement(s) failed:</p><ul class=\"tight\">");
                foreach (MetricRow row in errors.Take(20))
                {
                    html.AppendLine($"<li><code>{Esc(row.FrameFile)}</code> {row.RequestedMode} " +
                        $"@{row.Exposure.ToString("0.00", CultureInfo.InvariantCulture)}: {Esc(row.Error)}</li>");
                }

                html.AppendLine("</ul>");
            }
        }

        private static void AppendBaselineDiff(StringBuilder html, List<MetricRow> rows, List<MetricRow> baseline)
        {
            Dictionary<string, MetricRow> old = new Dictionary<string, MetricRow>();
            foreach (MetricRow row in baseline)
            {
                old[row.Key] = row;
            }

            List<(MetricRow now, MetricRow was, List<string> changes)> changed = new();

            foreach (MetricRow row in rows)
            {
                if (!old.TryGetValue(row.Key, out MetricRow was))
                {
                    continue;
                }

                List<string> changes = new List<string>();

                if (row.ResolvedMode != was.ResolvedMode)
                {
                    changes.Add($"Auto now resolves to <b>{row.ResolvedMode}</b> (was {was.ResolvedMode})");
                }

                CompareMetric(changes, "clip hi", was.ClipHighFraction, row.ClipHighFraction, 0.002f, true);
                CompareMetric(changes, "highlight clip", was.HighlightClipFraction, row.HighlightClipFraction, 0.005f, true);
                CompareMetric(changes, "highlight span", was.HighlightSpanCodes, row.HighlightSpanCodes, 2f, false);
                CompareMetric(changes, "contrast mean", was.LocalContrastRatio, row.LocalContrastRatio, 0.02f, false);
                CompareMetric(changes, "blew out in-range", was.ClipBelowCeilingFraction, row.ClipBelowCeilingFraction, 0.0005f, true);
                CompareMetric(changes, "round-trip P99", was.PassthroughP99CodeDelta, row.PassthroughP99CodeDelta, 1f, true);
                CompareMetric(changes, "crushed shadows", was.CrushedShadowFraction, row.CrushedShadowFraction, 0.001f, true);
                CompareMetric(changes, "hue drift", was.HueDriftDegrees, row.HueDriftDegrees, 0.25f, true);
                CompareMetric(changes, "chroma ratio", was.ChromaRatio, row.ChromaRatio, 0.01f, false);
                CompareMetric(changes, "contrast P10", was.LocalContrastRatioP10, row.LocalContrastRatioP10, 0.02f, false);
                CompareMetric(changes, "mid-tone", was.MidToneShiftCode, row.MidToneShiftCode, 2f, true);
                CompareMetric(changes, "paper white", was.PaperWhiteMeanCode, row.PaperWhiteMeanCode, 1f, false);

                if (changes.Count > 0)
                {
                    changed.Add((row, was, changes));
                }
            }

            html.AppendLine("<h2>Changes against baseline</h2>");

            int missing = rows.Count(r => !old.ContainsKey(r.Key));
            int dropped = baseline.Count(r => !rows.Any(n => n.Key == r.Key));
            html.Append($"<p class=\"meta\">{changed.Count} of {rows.Count - missing} comparable " +
                $"measurement(s) moved");
            if (missing > 0 || dropped > 0)
            {
                html.Append($" &middot; {missing} new, {dropped} no longer measured");
            }

            html.AppendLine(".</p>");

            if (changed.Count == 0)
            {
                html.AppendLine("<p class=\"ok\">Nothing moved beyond the reporting deltas.</p>");
                return;
            }

            html.AppendLine("<div class=\"scroll\"><table>");
            html.AppendLine("<tr><th>frame</th><th>mode</th><th>exp</th><th>what moved</th></tr>");
            foreach (var (now, _, changes) in changed.OrderByDescending(c => c.changes.Count))
            {
                html.Append("<tr>");
                html.Append($"<td class=\"mono\">{Esc(now.Label ?? now.FrameFile)}</td>");
                html.Append($"<td>{now.RequestedMode}</td>");
                html.Append($"<td>{now.Exposure.ToString("0.00", CultureInfo.InvariantCulture)}</td>");
                html.Append($"<td style=\"text-align:left;white-space:normal\">{string.Join("; ", changes)}</td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</table></div>");
        }

        private static void CompareMetric(List<string> changes, string name, float was, float now,
            float reportDelta, bool higherIsWorse)
        {
            if (float.IsNaN(was) || float.IsNaN(now))
            {
                return;
            }

            float delta = now - was;
            if (Math.Abs(delta) < reportDelta)
            {
                return;
            }

            bool worse = higherIsWorse ? delta > 0 : delta < 0;
            string arrow = delta > 0 ? "&uarr;" : "&darr;";
            string cls = worse ? "style=\"color:var(--bad-fg)\"" : "class=\"ok\"";
            changes.Add($"<span {cls}>{name} {arrow} {Math.Abs(delta).ToString("0.###", CultureInfo.InvariantCulture)}</span>");
        }

        private static void AppendReferenceFigure(StringBuilder html, EvalOptions options,
            string thumbPath, string linkPath, string caption, string note, int setIndex, int itemIndex)
        {
            html.AppendLine($"<figure class=\"shot reference\" style=\"width:{options.ThumbnailWidth}px\">");
            html.AppendLine($"<a class=\"viewerLink\" href=\"{Esc(linkPath)}\" " +
                $"data-set=\"{setIndex}\" data-item=\"{itemIndex}\">" +
                $"<img src=\"{Esc(thumbPath)}\" alt=\"Reference: {Esc(caption)}\" loading=\"lazy\"></a>");
            html.AppendLine($"<div class=\"cap\"><b>{caption}</b></div>");
            html.AppendLine($"<div class=\"note\">{note}</div>");
            html.AppendLine("</figure>");
        }

        private static void AppendFrameSection(StringBuilder html, string frameFile, List<MetricRow> rows,
            EvalOptions options, int setIndex)
        {
            MetricRow first = rows[0];
            MetricThresholds thresholds = MetricThresholds.For(first.Scenario);

            html.AppendLine("<div class=\"frame\">");
            html.AppendLine($"<h3>{Esc(frameFile)}</h3>");
            html.Append("<p class=\"frame-meta\">");
            html.Append($"{first.Scenario}");
            if (!string.IsNullOrWhiteSpace(first.Label))
            {
                html.Append($" &middot; {Esc(first.Label)}");
            }

            html.Append($" &middot; {first.Width}&times;{first.Height}");
            html.Append($" &middot; {(first.DxgiFormat == 10 ? "scRGB fp16" : "HDR10 PQ")}");
            html.Append($" &middot; SDR white {first.SdrWhiteNits.ToString("0.#", CultureInfo.InvariantCulture)} nits");
            html.Append($" &middot; peak {first.RefPeakNits.ToString("0", CultureInfo.InvariantCulture)} nits");
            html.Append($" &middot; {Pct(first.HighlightFraction)} above paper white");
            html.AppendLine("</p>");

            // Contact sheet at the thumbnail exposure only; the table below covers every exposure.
            List<MetricRow> sheet = rows
                .Where(r => Math.Abs(r.Exposure - options.ThumbnailExposure) < 0.001f && r.ThumbPath != null)
                .OrderBy(r => r.RequestedMode.ToString())
                .ToList();

            if (sheet.Count > 0)
            {
                // Item order here must match ReportViewer.BuildData: originals first, then the
                // renderings sorted by mode then exposure.
                int viewerItem = 0;
                html.AppendLine("<div class=\"sheet\">");

                // The originals go first, so every candidate is read against them rather than against
                // whichever mode happens to sort first.
                string referenceThumb = sheet.Select(r => r.ReferenceThumbPath).FirstOrDefault(p => p != null);
                if (referenceThumb != null)
                {
                    string referenceFull = sheet.Select(r => r.ReferencePath).FirstOrDefault(p => p != null);
                    AppendReferenceFigure(html, options, referenceThumb, referenceFull, "HDR original",
                        "PQ / BT.2020. Real HDR in Chrome on an HDR display; browser-tonemapped otherwise.",
                        setIndex, viewerItem++);
                }

                // Always-viewable views of the same original, so the sheet still works on an SDR
                // display and the folder can be shared as-is.
                string inRange = sheet.Select(r => r.ReferenceInRangePath).FirstOrDefault(p => p != null);
                if (inRange != null)
                {
                    AppendReferenceFigure(html, options, inRange, inRange, "original &middot; in-range",
                        "Hard clip at paper white. Exactly what was already inside SDR; " +
                        "everything above it blows white by construction.",
                        setIndex, viewerItem++);
                }

                string fullRange = sheet.Select(r => r.ReferenceFullRangePath).FirstOrDefault(p => p != null);
                if (fullRange != null)
                {
                    AppendReferenceFigure(html, options, fullRange, fullRange, "original &middot; full range",
                        "Linearly scaled so the peak lands at 1.0. Darker than intended, but nothing " +
                        "clips - this is the detail a curve has to fit into SDR.",
                        setIndex, viewerItem++);
                }

                foreach (MetricRow row in sheet)
                {
                    string flaggedClass = row.Flags.Count > 0 ? " flagged" : string.Empty;
                    html.AppendLine($"<figure class=\"shot{flaggedClass}\" style=\"width:{options.ThumbnailWidth}px\">");
                    string link = row.PngPath != null ? Esc(row.PngPath) : Esc(row.ThumbPath);
                    html.AppendLine($"<a class=\"viewerLink\" href=\"{link}\" " +
                        $"data-set=\"{setIndex}\" data-item=\"{viewerItem++}\">" +
                        $"<img src=\"{Esc(row.ThumbPath)}\" alt=\"{row.RequestedMode} at exposure {row.Exposure}\" loading=\"lazy\"></a>");
                    html.Append($"<div class=\"cap\"><b>{row.RequestedMode}</b>");
                    if (row.ResolvedMode != row.RequestedMode)
                    {
                        html.Append($" &rarr; {row.ResolvedMode}");
                    }

                    html.Append("</div>");

                    if (row.Flags.Count > 0)
                    {
                        html.Append($"<div class=\"flags\">{Esc(string.Join("; ", row.Flags))}</div>");
                    }

                    html.AppendLine("</figure>");
                }

                html.AppendLine("</div>");
            }

            html.AppendLine("<div class=\"scroll\"><table>");
            html.Append("<tr><th>mode</th><th>exp</th>");
            foreach (MetricColumn column in Columns)
            {
                html.Append($"<th title=\"{Esc(column.Tooltip)}\">{column.Name}</th>");
            }

            html.AppendLine("</tr>");

            foreach (MetricRow row in rows.OrderBy(r => r.RequestedMode.ToString()).ThenBy(r => r.Exposure))
            {
                html.Append("<tr>");
                html.Append($"<td>{row.RequestedMode}</td>");
                html.Append($"<td class=\"mono\">{row.Exposure.ToString("0.00", CultureInfo.InvariantCulture)}</td>");

                foreach (MetricColumn column in Columns)
                {
                    bool bad = column.IsBad(row, thresholds);
                    html.Append($"<td class=\"mono{(bad ? " bad" : string.Empty)}\">{column.Format(row)}</td>");
                }

                html.AppendLine("</tr>");
            }

            html.AppendLine("</table></div>");
            html.AppendLine("</div>");
        }

        private static void AppendThresholdAppendix(StringBuilder html)
        {
            html.AppendLine("<h2>Thresholds in use</h2>");
            html.AppendLine("<p class=\"meta\">Per scenario, because ground truth differs: Desktop has an " +
                "exact one, Auto-HDR a derivable one, native HDR none at all. " +
                "<b>n/a</b> means the check does not apply to that scenario. Local contrast and " +
                "highlight hue/chroma are never thresholded &mdash; they measure the tonemapper " +
                "doing its job, so they are reported and diffed against a baseline instead.</p>");
            html.AppendLine("<div class=\"scroll\"><table>");
            html.AppendLine("<tr><th>scenario</th><th>round trip</th><th>clip hi</th><th>blew out</th>" +
                "<th>crushed</th><th>hue</th><th>chroma</th><th>mid-tone</th>" +
                "<th>banding margin</th><th>paper white</th></tr>");

            foreach (HdrCorpusScenario scenario in new[]
            {
                HdrCorpusScenario.Desktop, HdrCorpusScenario.AutoHdr,
                HdrCorpusScenario.NativeHdr, HdrCorpusScenario.Unknown
            })
            {
                MetricThresholds t = MetricThresholds.For(scenario);
                html.Append("<tr>");
                html.Append($"<td>{scenario}</td>");
                html.Append($"<td class=\"mono\">{(t.MaxPassthroughCodeDelta >= 0 ? "&le; " + t.MaxPassthroughCodeDelta : "n/a")}</td>");
                html.Append($"<td class=\"mono\">n/a</td>");
                html.Append($"<td class=\"mono\">&le; {Pct(t.MaxClipBelowCeilingFraction)}</td>");
                html.Append($"<td class=\"mono\">&le; {Pct(t.MaxCrushedShadowFraction)}</td>");
                html.Append($"<td class=\"mono\">&le; {Num(t.MaxHueDriftDegrees, "0.0")}&deg;</td>");
                html.Append($"<td class=\"mono\">&ge; {Num(t.MinChromaRatio, "0.00")}</td>");
                html.Append($"<td class=\"mono\">{(t.MaxAbsMidToneShiftCode >= 0f ? "&plusmn;" + Num(t.MaxAbsMidToneShiftCode, "0") : "n/a")}</td>");
                html.Append($"<td class=\"mono\">ref +{t.BandingGapMargin}</td>");
                html.Append($"<td class=\"mono\">{(t.MinPaperWhiteCode > 0 ? "&ge; " + Num(t.MinPaperWhiteCode, "0") : "n/a")}</td>");
                html.AppendLine("</tr>");
            }

            html.AppendLine("</table></div>");
        }

        // ====================================================================
        // Formatting helpers
        // ====================================================================

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

        private static string Pct(float value) =>
            float.IsNaN(value) ? "&ndash;" : (value * 100f).ToString("0.###", CultureInfo.InvariantCulture) + "%";

        private static string Num(float value, string format) =>
            float.IsNaN(value) ? "&ndash;" : value.ToString(format, CultureInfo.InvariantCulture);

        private static string Esc(string value) => WebUtility.HtmlEncode(value ?? string.Empty);

        private static string Get(string[] fields, Dictionary<string, int> index, string name) =>
            index.TryGetValue(name, out int i) && i < fields.Length ? fields[i] : string.Empty;

        private static string NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

        private static int ParseInt(string value, int fallback = 0) =>
            int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int result)
                ? result
                : fallback;

        private static long ParseLong(string value) =>
            long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long result)
                ? result
                : 0L;

        private static float ParseFloat(string value) =>
            float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float result)
                ? result
                : float.NaN;

        private static T ParseEnum<T>(string value) where T : struct, Enum =>
            Enum.TryParse(value, true, out T result) ? result : default;

        private static string[] SplitCsv(string line)
        {
            List<string> fields = new List<string>();
            StringBuilder current = new StringBuilder();
            bool quoted = false;

            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];

                if (quoted)
                {
                    if (c == '"')
                    {
                        if (i + 1 < line.Length && line[i + 1] == '"')
                        {
                            current.Append('"');
                            i++;
                        }
                        else
                        {
                            quoted = false;
                        }
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (c == '"')
                {
                    quoted = true;
                }
                else if (c == ',')
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }

            fields.Add(current.ToString());
            return fields.ToArray();
        }
    }
}
