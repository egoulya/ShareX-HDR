#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using ShareX.HelpersLib;
using ShareX.MediaLib;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ShareX.ScreenCaptureLib
{
    internal static class HdrRecordingTonemapper
    {
        public static bool Process(string ffmpegPath, ScreenRecordingOptions options, Action<float> progressChanged)
        {
            string inputPath = options.InputPath;
            string outputPath = Path.ChangeExtension(options.OutputPath, options.FFmpeg.Extension);

            using (FFmpegCLIManager probe = new FFmpegCLIManager(ffmpegPath))
            {
                probe.ShowError = false;
                VideoInfo videoInfo = probe.GetVideoInfo(inputPath);

                if (videoInfo == null)
                {
                    DebugHelper.WriteLine("HDR recording pass 2: failed to probe intermediate video.");
                    return false;
                }

                int width = videoInfo.VideoResolution.Width;
                int height = videoInfo.VideoResolution.Height;
                double fps = videoInfo.VideoFPS > 0 ? videoInfo.VideoFPS : options.FPS;
                long totalFrames = fps > 0 ? (long)Math.Ceiling(videoInfo.Duration.TotalSeconds * fps) : 0;

                bool normalizationAppliedInPass1 = videoInfo.VideoCodec != null &&
                    videoInfo.VideoCodec.Contains("ffv1", StringComparison.OrdinalIgnoreCase);

                DebugHelper.WriteLine($"HDR recording pass 2: C# tonemap {width}x{height} @ {fps} fps, pass1Normalized={normalizationAppliedInPass1}");

                string decodeArgs = BuildDecodeArgs(inputPath, videoInfo);
                string encodeArgs = BuildEncodeArgs(options, inputPath, outputPath, width, height, fps, !string.IsNullOrEmpty(videoInfo.AudioCodec));

                return RunPipeline(ffmpegPath, decodeArgs, encodeArgs, width, height, totalFrames, normalizationAppliedInPass1, progressChanged);
            }
        }

        private static string BuildDecodeArgs(string inputPath, VideoInfo videoInfo)
        {
            StringBuilder args = new StringBuilder();
            args.Append("-hide_banner -loglevel error -nostdin ");
            args.Append($"-i \"{inputPath}\" -an -sn ");

            bool isFfv1Intermediate = videoInfo.VideoCodec != null &&
                videoInfo.VideoCodec.Contains("ffv1", StringComparison.OrdinalIgnoreCase);

            if (isFfv1Intermediate)
            {
                args.Append("-vf format=gbrpf32le ");
            }
            else
            {
                args.Append("-vf \"format=yuv444p10le,zscale=tin=linear:pin=709:min=709:rangein=full:t=linear:p=709:m=709:range=full,format=gbrpf32le\" ");
            }

            args.Append("-f rawvideo -pix_fmt gbrpf32le pipe:1");
            return args.ToString();
        }

        private static string BuildEncodeArgs(ScreenRecordingOptions options, string inputPath, string outputPath, int width, int height, double fps, bool hasAudio)
        {
            StringBuilder args = new StringBuilder();
            string fpsText = fps.ToString("0.####", CultureInfo.InvariantCulture);

            args.Append("-hide_banner -loglevel error -nostdin ");
            args.Append($"-f rawvideo -pix_fmt bgr24 -video_size {width}x{height} -framerate {fpsText} -i pipe:0 ");
            args.Append($"-i \"{inputPath}\" -map 0:v:0 ");

            if (hasAudio)
            {
                args.Append("-map 1:a? -c:a copy ");
            }

            FFmpegOptions ffmpeg = options.FFmpeg;

            switch (ffmpeg.VideoCodec)
            {
                case FFmpegVideoCodec.libx265:
                    args.Append("-c:v libx265 ");
                    break;
                case FFmpegVideoCodec.h264_nvenc:
                    args.Append("-c:v h264_nvenc ");
                    break;
                case FFmpegVideoCodec.hevc_nvenc:
                    args.Append("-c:v hevc_nvenc ");
                    break;
                default:
                    args.Append("-c:v libx264 ");
                    break;
            }

            switch (ffmpeg.VideoCodec)
            {
                case FFmpegVideoCodec.libx264:
                case FFmpegVideoCodec.libx265:
                    args.Append($"-preset {ffmpeg.x264_Preset} ");
                    if (ffmpeg.x264_Use_Bitrate)
                    {
                        args.Append($"-b:v {ffmpeg.x264_Bitrate}k ");
                    }
                    else
                    {
                        args.Append($"-crf {ffmpeg.x264_CRF} ");
                    }
                    break;
                case FFmpegVideoCodec.h264_nvenc:
                case FFmpegVideoCodec.hevc_nvenc:
                    args.Append($"-preset {ffmpeg.NVENC_Preset} ");
                    args.Append($"-tune {ffmpeg.NVENC_Tune} ");
                    args.Append($"-b:v {ffmpeg.NVENC_Bitrate}k ");
                    break;
            }

            args.Append("-pix_fmt yuv420p ");
            args.Append("-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range pc ");
            args.Append("-movflags +faststart ");
            args.Append($"-r {fpsText} ");
            args.Append("-y ");
            args.Append($"\"{outputPath}\"");

            return args.ToString();
        }

        private static bool RunPipeline(string ffmpegPath, string decodeArgs, string encodeArgs, int width, int height, long totalFrames, bool normalizationAppliedInPass1, Action<float> progressChanged)
        {
            int frameBytesIn = width * height * 12;
            int frameBytesOut = width * height * 3;
            byte[] inFrame = new byte[frameBytesIn];
            byte[] outFrame = new byte[frameBytesOut];

            Process decoder = CreatePipeProcess(ffmpegPath, decodeArgs, redirectStdout: true);
            Process encoder = CreatePipeProcess(ffmpegPath, encodeArgs, redirectStdout: false);

            if (decoder == null || encoder == null)
            {
                decoder?.Dispose();
                encoder?.Dispose();
                return false;
            }

            DebugHelper.WriteLine($"HDR recording pass 2 decode: {decodeArgs}");
            DebugHelper.WriteLine($"HDR recording pass 2 encode: {encodeArgs}");

            StringBuilder encodeLog = new StringBuilder();
            StringBuilder decodeLog = new StringBuilder();

            bool success = false;
            long framesDone = 0;

            try
            {
                decoder.Start();
                encoder.Start();

                DrainProcessStderr(decoder, decodeLog);
                DrainProcessStderr(encoder, encodeLog);

                Stream stdout = decoder.StandardOutput.BaseStream;
                Stream stdin = encoder.StandardInput.BaseStream;

                while (ReadExact(stdout, inFrame))
                {
                    if (encoder.HasExited)
                    {
                        throw new InvalidOperationException($"HDR pass 2: encoder exited early with code {encoder.ExitCode}.");
                    }

                    Screenshot.TonemapLinearScRgbFrameToBgr24(inFrame, outFrame, width, height, applyNormalization: !normalizationAppliedInPass1);
                    stdin.Write(outFrame, 0, frameBytesOut);
                    framesDone++;

                    if (totalFrames > 0 && progressChanged != null)
                    {
                        // Reserve the last percent for x264/mux finishing after the pipe closes.
                        float pumpProgress = framesDone * 99f / totalFrames;
                        progressChanged(Math.Min(99f, pumpProgress));
                    }
                }

                stdin.Flush();
                stdin.Close();

                if (!WaitForProcessExit(decoder, 60000, "decoder"))
                {
                    return false;
                }

                if (progressChanged != null)
                {
                    progressChanged(99f);
                }

                if (!WaitForProcessExit(encoder, 600000, "encoder"))
                {
                    return false;
                }

                success = decoder.ExitCode == 0 && encoder.ExitCode == 0;

                if (!success)
                {
                    DebugHelper.WriteLine($"HDR recording pass 2 failed. Decoder exit={decoder.ExitCode}, encoder exit={encoder.ExitCode}");
                    WriteTailLog("HDR pass 2 decoder log", decodeLog);
                    WriteTailLog("HDR pass 2 encoder log", encodeLog);
                }
                else if (progressChanged != null)
                {
                    progressChanged(100f);
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "HDR recording pass 2 pipeline failed.");
                WriteTailLog("HDR pass 2 encoder log", encodeLog);
            }
            finally
            {
                try
                {
                    if (!decoder.HasExited) decoder.Kill();
                }
                catch
                {
                }

                try
                {
                    if (!encoder.HasExited) encoder.Kill();
                }
                catch
                {
                }

                decoder.Dispose();
                encoder.Dispose();
            }

            return success;
        }

        private static void DrainProcessStderr(Process process, StringBuilder log)
        {
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    if (log.Length < 8192)
                    {
                        log.AppendLine(e.Data);
                    }
                }
            };

            process.BeginErrorReadLine();
        }

        private static bool WaitForProcessExit(Process process, int timeoutMs, string name)
        {
            if (process.WaitForExit(timeoutMs))
            {
                return true;
            }

            DebugHelper.WriteLine($"HDR recording pass 2: {name} timed out after {timeoutMs} ms, killing process.");
            try
            {
                process.Kill();
                process.WaitForExit(5000);
            }
            catch
            {
            }

            return false;
        }

        private static void WriteTailLog(string title, StringBuilder log)
        {
            if (log == null || log.Length == 0)
            {
                return;
            }

            const int maxChars = 2048;
            string text = log.ToString();

            if (text.Length > maxChars)
            {
                text = text.Substring(text.Length - maxChars);
            }

            DebugHelper.WriteLine($"{title}:{Environment.NewLine}{text}");
        }

        private static Process CreatePipeProcess(string ffmpegPath, string args, bool redirectStdout)
        {
            if (!File.Exists(ffmpegPath))
            {
                return null;
            }

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = ffmpegPath,
                WorkingDirectory = Path.GetDirectoryName(ffmpegPath),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = redirectStdout,
                RedirectStandardError = true
            };

            return new Process() { StartInfo = psi };
        }

        private static bool ReadExact(Stream stream, byte[] buffer)
        {
            int offset = 0;

            while (offset < buffer.Length)
            {
                int read = stream.Read(buffer, offset, buffer.Length - offset);
                if (read == 0)
                {
                    return offset > 0 ? throw new EndOfStreamException("HDR pass 2: truncated frame from decoder.") : false;
                }

                offset += read;
            }

            return true;
        }
    }
}
