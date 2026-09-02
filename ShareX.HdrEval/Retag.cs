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
using System.IO;

namespace ShareX.HdrEval
{
    /// <summary>
    /// Rewrites the scenario and/or label on existing corpus frames.
    ///
    /// Scenario is not cosmetic: it selects which thresholds apply, and Desktop is the only one with
    /// an exact ground truth. A desktop capture left tagged Unknown silently loses the round-trip
    /// check, which is the single strongest measurement in the suite. Since captures happen in the
    /// middle of doing something else, mis-tagging is inevitable - so this exists to fix it after the
    /// fact rather than forcing a re-capture.
    /// </summary>
    public static class Retag
    {
        public static int Run(EvalOptions options)
        {
            List<HdrFrameCorpus.Entry> entries = HdrFrameCorpus.Enumerate(options.CorpusDirectory);
            if (entries.Count == 0)
            {
                Console.Error.WriteLine($"No .hdrframe files found in {options.CorpusDirectory}.");
                return 2;
            }

            List<HdrFrameCorpus.Entry> matched = new List<HdrFrameCorpus.Entry>();

            foreach (HdrFrameCorpus.Entry entry in entries)
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

                matched.Add(entry);
            }

            if (matched.Count == 0)
            {
                Console.Error.WriteLine("Nothing matched. Narrow or widen --filter / --scenario.");
                return 2;
            }

            Console.WriteLine($"{matched.Count} of {entries.Count} frame(s) matched:");
            foreach (HdrFrameCorpus.Entry entry in matched)
            {
                Console.WriteLine($"  {entry.Metadata.Scenario,-9} {entry.Metadata.Label ?? "(no label)",-24} {entry.FileName}");
            }

            Console.WriteLine();
            Console.WriteLine($"  scenario -> {options.RetagScenario}" +
                (options.RetagLabel != null ? $", label -> {options.RetagLabel}" : string.Empty));

            if (!options.RetagConfirmed)
            {
                Console.WriteLine();
                Console.WriteLine("Nothing written. Add --yes to apply.");
                return 0;
            }

            int rewritten = 0;
            foreach (HdrFrameCorpus.Entry entry in matched)
            {
                string path = Path.Combine(options.CorpusDirectory, entry.FileName);
                try
                {
                    // Read and rewrite whole: the payload is the frame's identity (its SHA-256 is in
                    // the header), so only metadata changes and the hash must still verify after.
                    HdrRawFrame frame = HdrRawFrame.Read(path);
                    frame.Metadata.Scenario = options.RetagScenario;
                    if (options.RetagLabel != null)
                    {
                        frame.Metadata.Label = options.RetagLabel;
                    }

                    string temp = path + ".retag.tmp";
                    HdrRawFrame.Write(temp, frame, frame.Metadata.Compressed);

                    // Verify before replacing, so a failed rewrite cannot destroy a captured frame.
                    HdrRawFrame check = HdrRawFrame.Read(temp);
                    if (check.Metadata.Scenario != options.RetagScenario)
                    {
                        throw new InvalidDataException("Rewritten frame did not carry the new scenario.");
                    }

                    File.Move(temp, path, overwrite: true);
                    rewritten++;
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"  {entry.FileName}: {e.Message}");
                }
            }

            Console.WriteLine();
            Console.WriteLine($"{rewritten} frame(s) rewritten.");

            if (rewritten > 0)
            {
                HdrFrameCorpus.WriteManifest(options.CorpusDirectory);
                Console.WriteLine($"manifest refreshed: {Path.Combine(options.CorpusDirectory, HdrFrameCorpus.ManifestFileName)}");
            }

            return rewritten == matched.Count ? 0 : 1;
        }
    }
}
