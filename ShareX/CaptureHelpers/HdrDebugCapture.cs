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
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;

namespace ShareX
{
    /// <summary>
    /// Development aid: captures the full screen through the normal HDR path, records the raw DXGI
    /// frame to a one-shot corpus, then runs ShareX.HdrEval over it and opens the report.
    ///
    /// Deliberately does not go through <see cref="CaptureBase"/>. That would run the whole
    /// after-capture pipeline - saving to the user's output folder, uploading to their configured
    /// destination, history. A diagnostic action must not upload anything, so this drives
    /// <see cref="Screenshot"/> directly and keeps everything inside its own session folder.
    /// </summary>
    public static class HdrDebugCapture
    {
        private const string SessionFolderName = "HDRDebug";

        /// <summary>Root of all debug sessions: &lt;personal folder&gt;\HDRDebug.</summary>
        public static string RootFolder => Path.Combine(Program.PersonalFolder, SessionFolderName);

        private static readonly Lazy<string> LazySessionFolder = new(BuildSessionFolder);

        /// <summary>
        /// One folder per app launch. Every keypress adds a frame to it and re-runs the evaluation,
        /// so the session's single report grows to cover every capture taken since launch.
        ///
        /// Re-running is cheap because the evaluation is incremental: frames already measured by the
        /// same build are reused from metrics.csv and only the new frame is processed, so the cost of
        /// a press is one frame no matter how many are already in the session. A rebuilt
        /// ScreenCaptureLib invalidates that reuse, so changing the tonemap always re-measures
        /// everything rather than mixing old and new numbers in one report.
        /// </summary>
        public static string SessionFolder => LazySessionFolder.Value;

        private static string BuildSessionFolder()
        {
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
            string label = SanitizeForFolder(ReadLabelFromEnvironment());
            HdrCorpusScenario scenario = ReadScenarioFromEnvironment();

            string name = timestamp;
            if (scenario != HdrCorpusScenario.Unknown)
            {
                name = scenario + "_" + name;
            }

            if (!string.IsNullOrEmpty(label))
            {
                name = label + "_" + name;
            }

            return Path.Combine(RootFolder, name);
        }

        private static string SanitizeForFolder(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            System.Text.StringBuilder safe = new System.Text.StringBuilder(value.Length);
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c) || c == '-')
                {
                    safe.Append(c);
                }
                else if (safe.Length > 0 && safe[safe.Length - 1] != '-')
                {
                    safe.Append('-');
                }
            }

            return safe.ToString().Trim('-');
        }

        public static void Run(TaskSettings taskSettings)
        {
            // The capture itself must happen promptly; evaluation is slow, so it runs off the
            // caller's thread once the frame is in hand.
            string sessionFolder = SessionFolder;

            if (!TryCapture(taskSettings, sessionFolder, out int frameCount, out string error))
            {
                DebugHelper.WriteLine($"HDR debug: capture failed: {error}");
                TaskHelpers.ShowNotificationTip($"HDR debug capture failed: {error}", "ShareX", 6000);
                return;
            }

            if (frameCount == 0)
            {
                // "No frames" has several distinct causes that look identical to the user but need
                // different fixes, so work out which one actually applied.
                string reason = DiagnoseNoFrames();
                DebugHelper.WriteLine("HDR debug: no HDR frame was recorded. " + reason);
                TaskHelpers.ShowNotificationTip("HDR debug: no frames recorded. " + reason, "ShareX", 12000);
                return;
            }

            int corpusSize = HdrFrameCorpus.Enumerate(sessionFolder).Count;
            TaskHelpers.ShowNotificationTip(
                $"HDR debug: captured {frameCount} frame(s), report now covers {corpusSize}...",
                "ShareX", 4000);

            Task.Run(() => Evaluate(sessionFolder, frameCount));
        }

        private static bool TryCapture(TaskSettings taskSettings, string sessionFolder,
            out int frameCount, out string error)
        {
            frameCount = 0;
            error = null;

            // Remember whatever the environment had armed, so a debug capture does not clobber an
            // ongoing corpus collection.
            string previousDirectory = HdrFrameDump.Directory;
            HdrCorpusScenario previousScenario = HdrFrameDump.Scenario;
            string previousLabel = HdrFrameDump.Label;
            string previousNotes = HdrFrameDump.Notes;
            int previousMax = HdrFrameDump.MaxFrames;

            Bitmap bitmap = null;

            try
            {
                Directory.CreateDirectory(sessionFolder);

                HdrFrameDump.Reset();
                HdrFrameDump.Directory = sessionFolder;
                HdrFrameDump.MaxFrames = 0;

                // Scenario decides which thresholds apply, and Desktop is the only one with an exact
                // ground truth - so an untagged desktop capture silently loses its strongest check.
                // Honour SHAREX_HDR_DUMP_SCENARIO / _LABEL when they are set; ShareX.HdrEval --retag
                // is the fix-up path when they are not.
                HdrCorpusScenario scenario = ReadScenarioFromEnvironment();
                HdrFrameDump.Scenario = scenario;
                HdrFrameDump.Label = ReadLabelFromEnvironment() ?? "debug-capture";
                HdrFrameDump.Notes = "Debug capture from the ShareX HDR debug task." +
                    (scenario == HdrCorpusScenario.Unknown
                        ? " Scenario untagged: set SHAREX_HDR_DUMP_SCENARIO or retag before evaluating."
                        : string.Empty);

                Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);

                // The whole point of a debug capture is to obtain an HDR frame, so the pipeline is
                // forced on rather than following the user's CaptureHDREnabled setting. With that
                // setting Off, a capture takes the SDR path and records nothing, which reads as a
                // broken tool rather than a configuration choice. On an SDR output the per-output
                // check still falls back to GDI, so forcing it is safe.
                HdrCaptureMode requested = screenshot.HdrCaptureMode;
                screenshot.HdrCaptureMode = ReadModeFromEnvironment() ?? HdrCaptureMode.On;
                if (screenshot.HdrCaptureMode != requested)
                {
                    DebugHelper.WriteLine($"HDR debug: overriding capture mode {requested} -> " +
                        $"{screenshot.HdrCaptureMode} for this debug capture.");
                }

                bitmap = screenshot.CaptureFullscreen();

                frameCount = HdrFrameDump.DumpedCount;

                if (bitmap != null && frameCount > 0)
                {
                    // The app's own output beside the frame that produced it, for eyeballing against
                    // what the runner reconstructs. Named after the frame so repeats do not collide.
                    string stem = Path.GetFileNameWithoutExtension(HdrFrameDump.LastPath ?? "capture");
                    string appOutput = Path.Combine(sessionFolder, stem + "__sharex-output.png");
                    bitmap.Save(appOutput, System.Drawing.Imaging.ImageFormat.Png);
                    DebugHelper.WriteLine($"HDR debug: app output saved to {appOutput}");
                }

                if (frameCount > 0)
                {
                    HdrFrameCorpus.WriteManifest(sessionFolder);
                }

                return true;
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR debug: capture failed.");
                error = e.Message;
                return false;
            }
            finally
            {
                bitmap?.Dispose();

                HdrFrameDump.Directory = previousDirectory;
                HdrFrameDump.Scenario = previousScenario;
                HdrFrameDump.Label = previousLabel;
                HdrFrameDump.Notes = previousNotes;
                HdrFrameDump.MaxFrames = previousMax;
            }
        }

        public const string ModeVariable = "SHAREX_HDR_DEBUG_MODE";

        private static HdrCaptureMode? ReadModeFromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(ModeVariable);
            return Enum.TryParse(value, true, out HdrCaptureMode mode) ? mode : null;
        }

        /// <summary>
        /// Works out why the HDR pipeline produced nothing. An SDR output, an HDR output whose
        /// duplication still hands back BGRA8, and a game that hides its swapchain from Desktop
        /// Duplication all look identical from outside but need different fixes.
        /// </summary>
        private static string DiagnoseNoFrames()
        {
            try
            {
                Rectangle bounds = CaptureHelpers.GetScreenBounds();

                if (!HdrDisplayProbe.HasHdrOutput(bounds))
                {
                    return "No output covering the capture area reports an HDR colour space. " +
                        "Turn HDR on for that display in Windows Display settings. If the game is " +
                        "in exclusive fullscreen, try borderless windowed - exclusive fullscreen can " +
                        "hide the swapchain from Desktop Duplication.";
                }

                return "The display is in HDR, so duplication returned an SDR (BGRA8) surface or no " +
                    "frame at all. Exclusive fullscreen is the usual cause and borderless windowed " +
                    "normally fixes it. The ShareX log has the per-output colour space lines.";
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR debug: could not diagnose the empty capture.");
                return "Could not probe the display; see the ShareX log.";
            }
        }

        private static HdrCorpusScenario ReadScenarioFromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(HdrFrameDump.ScenarioVariable);
            return Enum.TryParse(value, true, out HdrCorpusScenario scenario)
                ? scenario
                : HdrCorpusScenario.Unknown;
        }

        private static string ReadLabelFromEnvironment()
        {
            string value = Environment.GetEnvironmentVariable(HdrFrameDump.LabelVariable);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }

        private static readonly object EvaluateLock = new();

        private static void Evaluate(string sessionFolder, int frameCount)
        {
            // Two quick presses would otherwise run two evaluations against the same metrics.csv and
            // the second would overwrite the first's merge. Serialising costs nothing: the work is
            // one frame either way.
            lock (EvaluateLock)
            {
                EvaluateCore(sessionFolder, frameCount);
            }
        }

        private static void EvaluateCore(string sessionFolder, int frameCount)
        {
            try
            {
                string evalPath = FindEvalExecutable();
                if (evalPath == null)
                {
                    // The frames are still on disk and usable, so say where they are rather than
                    // presenting this as a total failure.
                    DebugHelper.WriteLine("HDR debug: ShareX.HdrEval not found; frames kept for manual evaluation.");
                    TaskHelpers.ShowNotificationTip(
                        $"HDR debug: {frameCount} frame(s) saved, but ShareX.HdrEval was not found. " +
                        "Run it manually against the session folder.", "ShareX", 9000);
                    FileHelpers.OpenFolder(sessionFolder);
                    return;
                }

                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = evalPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(evalPath)
                };

                startInfo.ArgumentList.Add("--corpus");
                startInfo.ArgumentList.Add(sessionFolder);

                // Only the frame just captured gets measured; everything already in this session is
                // reused from metrics.csv, so a press costs the same whether it is the first or the
                // twentieth.
                startInfo.ArgumentList.Add("--incremental");

                DebugHelper.WriteLine($"HDR debug: running {evalPath} --corpus \"{sessionFolder}\" --incremental");

                using Process process = Process.Start(startInfo);
                if (process == null)
                {
                    TaskHelpers.ShowNotificationTip("HDR debug: could not start ShareX.HdrEval.", "ShareX", 6000);
                    return;
                }

                string output = process.StandardOutput.ReadToEnd();
                string errors = process.StandardError.ReadToEnd();
                process.WaitForExit();

                if (!string.IsNullOrWhiteSpace(output))
                {
                    DebugHelper.WriteLine("HDR debug: eval output:" + Environment.NewLine + output.Trim());
                }

                if (!string.IsNullOrWhiteSpace(errors))
                {
                    DebugHelper.WriteLine("HDR debug: eval errors:" + Environment.NewLine + errors.Trim());
                }

                string report = Path.Combine(sessionFolder, "eval", "report.html");
                if (File.Exists(report))
                {
                    DebugHelper.WriteLine($"HDR debug: report at {report}");
                    // FileHelpers.OpenFile, not URLHelpers.OpenURL: the latter runs IsValidURL first,
                    // which requires an http/https/ftp protocol and rejects a local path.
                    // The report needs a real browser anyway - the HDR reference is a cICP PQ PNG.
                    FileHelpers.OpenFile(report);
                }
                else
                {
                    TaskHelpers.ShowNotificationTip(
                        $"HDR debug: evaluation finished with exit code {process.ExitCode} but produced no report.",
                        "ShareX", 8000);
                    FileHelpers.OpenFolder(sessionFolder);
                }
            }
            catch (Exception e)
            {
                DebugHelper.WriteException(e, "HDR debug: evaluation failed.");
                TaskHelpers.ShowNotificationTip($"HDR debug evaluation failed: {e.Message}", "ShareX", 6000);
            }
        }

        /// <summary>
        /// Looks for ShareX.HdrEval next to the running app first, then in the sibling build output
        /// that a development checkout produces.
        /// </summary>
        private static string FindEvalExecutable()
        {
            const string exeName = "ShareX.HdrEval.exe";
            string appFolder = Path.GetDirectoryName(Environment.ProcessPath) ?? Directory.GetCurrentDirectory();

            string[] candidates =
            {
                Path.Combine(appFolder, exeName),
                Path.Combine(appFolder, "HdrEval", exeName),

                // Development layouts: ShareX runs from ShareX\bin\<config>, so the eval tool sits a
                // few levels up in its own project output.
                Path.Combine(appFolder, "..", "..", "ShareX.HdrEval", "bin", "Debug", exeName),
                Path.Combine(appFolder, "..", "..", "ShareX.HdrEval", "bin", "Release", exeName),
                Path.Combine(appFolder, "..", "..", "..", "ShareX.HdrEval", "bin", "Debug", exeName),
                Path.Combine(appFolder, "..", "..", "..", "ShareX.HdrEval", "bin", "Release", exeName)
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full))
                    {
                        return full;
                    }
                }
                catch (Exception)
                {
                    // A malformed candidate path is not interesting; try the next one.
                }
            }

            return null;
        }
    }
}
