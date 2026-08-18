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
using System;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace ShareX.ScreenCaptureLib
{
    public class ScreenRecordingOptions
    {
        public bool IsRecording { get; set; }
        public bool IsLossless { get; set; }
        public string InputPath { get; set; }
        public string OutputPath { get; set; }
        public int FPS { get; set; }
        public int GIFFPS { get; set; }
        public Rectangle CaptureArea { get; set; }
        public float Duration { get; set; }
        public bool DrawCursor { get; set; }
        public bool CaptureHDREnabled { get; set; }
        public HdrTonemapMode HdrTonemapMode { get; set; } = HdrTonemapMode.Auto;
        public float HdrExposure { get; set; } = HdrTonemap.ExposureDefault;
        public bool HdrDxgiPipeRecording { get; set; }
        public FFmpegOptions FFmpeg { get; set; } = new FFmpegOptions();

        public string GetFFmpegCommands()
        {
            string commands;

            if (IsRecording && !string.IsNullOrEmpty(FFmpeg.VideoSource) &&
                FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.ScreenCaptureRecorder.Value, StringComparison.OrdinalIgnoreCase))
            {
                // https://github.com/rdp/screen-capture-recorder-to-video-windows-free
                string registryPath = "Software\\screen-capture-recorder";
                RegistryHelpers.CreateRegistry(registryPath, "start_x", CaptureArea.X);
                RegistryHelpers.CreateRegistry(registryPath, "start_y", CaptureArea.Y);
                RegistryHelpers.CreateRegistry(registryPath, "capture_width", CaptureArea.Width);
                RegistryHelpers.CreateRegistry(registryPath, "capture_height", CaptureArea.Height);
                RegistryHelpers.CreateRegistry(registryPath, "default_max_fps", 60);
                RegistryHelpers.CreateRegistry(registryPath, "capture_mouse_default_1", DrawCursor ? 1 : 0);
            }

            if (!IsLossless && FFmpeg.UseCustomCommands && !string.IsNullOrEmpty(FFmpeg.CustomCommands))
            {
                commands = FFmpeg.CustomCommands.
                    Replace("$fps$", FPS.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_x$", CaptureArea.X.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_y$", CaptureArea.Y.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_width$", CaptureArea.Width.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$area_height$", CaptureArea.Height.ToString(), StringComparison.OrdinalIgnoreCase).
                    Replace("$cursor$", DrawCursor ? "1" : "0", StringComparison.OrdinalIgnoreCase).
                    Replace("$duration$", Duration.ToString("0.0", CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase).
                    Replace("$output$", Path.ChangeExtension(OutputPath, FFmpeg.Extension), StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                commands = GetFFmpegArgs();
            }

            return commands.Trim();
        }

        public string GetFFmpegHdrDxgiPipeArgs(int width, int height)
        {
            StringBuilder args = new StringBuilder();
            string framerate = FPS.ToString(CultureInfo.InvariantCulture);

            // Do not pass -nostdin: on Windows FFmpeg 8 treats that as "replace stdin
            // with NUL", so -i pipe:0 gets immediate EOF and the MP4 is empty/corrupt.
            args.Append("-hide_banner -loglevel error ");

            if (FFmpeg.IsAudioSourceSelected)
            {
                AppendInputDevice(args, "dshow", true);
                args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
            }

            args.Append($"-f rawvideo -pix_fmt bgr24 -video_size {width}x{height} -framerate {framerate} -i pipe:0 ");

            if (FFmpeg.IsAudioSourceSelected)
            {
                args.Append("-map 1:v:0 -map 0:a? ");
            }
            else
            {
                args.Append("-map 0:v:0 ");
            }

            if (!string.IsNullOrEmpty(FFmpeg.UserArgs))
            {
                args.Append(FFmpeg.UserArgs + " ");
            }

            AppendVideoEncodingArgs(args, framerate, isHdrTonemapPass: false);
            args.Append("-pix_fmt yuv420p -color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range pc ");

            if (Duration > 0)
            {
                args.Append($"-t {Duration.ToString("0.0", CultureInfo.InvariantCulture)} ");
            }

            args.Append("-y ");
            string outputExtension = IsLossless ? "mp4" : FFmpeg.Extension;
            args.Append($"\"{Path.ChangeExtension(OutputPath, outputExtension)}\"");

            return args.ToString();
        }

        public string GetFFmpegArgs(bool isCustom = false)
        {
            if (IsRecording && !FFmpeg.IsVideoSourceSelected && !FFmpeg.IsAudioSourceSelected)
            {
                return null;
            }

            StringBuilder args = new StringBuilder();

            string framerate = isCustom ? "$fps$" : FPS.ToString();

            if (IsRecording)
            {
                if (FFmpeg.IsVideoSourceSelected)
                {
                    if (ShouldUseDDAGrab())
                    {
                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            AppendInputDevice(args, "dshow", true);
                            args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }

                        if (CaptureHDREnabled && IsLossless)
                        {
                            AppendDDAGrabFilterComplex(args, framerate);
                        }
                        else
                        {
                            AppendDDAGrabVideoInput(args, framerate);
                        }
                    }
                    else if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.GDIGrab.Value, StringComparison.OrdinalIgnoreCase))
                    {
                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            AppendInputDevice(args, "dshow", true);
                            args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }

                        string x = isCustom ? "$area_x$" : CaptureArea.X.ToString();
                        string y = isCustom ? "$area_y$" : CaptureArea.Y.ToString();
                        string width = isCustom ? "$area_width$" : CaptureArea.Width.ToString();
                        string height = isCustom ? "$area_height$" : CaptureArea.Height.ToString();
                        string cursor = isCustom ? "$cursor$" : DrawCursor ? "1" : "0";

                        // https://ffmpeg.org/ffmpeg-devices.html#gdigrab
                        AppendInputDevice(args, "gdigrab", false);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-offset_x {x} ");
                        args.Append($"-offset_y {y} ");
                        args.Append($"-video_size {width}x{height} ");
                        args.Append($"-draw_mouse {cursor} ");
                        args.Append("-i desktop ");
                    }
                    else
                    {
                        // https://ffmpeg.org/ffmpeg-devices.html#dshow
                        AppendInputDevice(args, "dshow", FFmpeg.IsAudioSourceSelected);
                        args.Append($"-framerate {framerate} ");
                        args.Append($"-i video={Helpers.EscapeCLIText(FFmpeg.VideoSource)}");

                        if (FFmpeg.IsAudioSourceSelected)
                        {
                            args.Append($":audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                        }
                        else
                        {
                            args.Append(" ");
                        }
                    }
                }
                else if (FFmpeg.IsAudioSourceSelected)
                {
                    AppendInputDevice(args, "dshow", true);
                    args.Append($"-i audio={Helpers.EscapeCLIText(FFmpeg.AudioSource)} ");
                }
            }
            else
            {
                args.Append($"-i \"{InputPath}\" ");
            }

            if (!string.IsNullOrEmpty(FFmpeg.UserArgs))
            {
                args.Append(FFmpeg.UserArgs + " ");
            }

            if (FFmpeg.IsVideoSourceSelected)
            {
                AppendVideoEncodingArgs(args, framerate, isHdrTonemapPass: false);
            }

            if (FFmpeg.IsAudioSourceSelected)
            {
                switch (FFmpeg.AudioCodec)
                {
                    case FFmpegAudioCodec.libvoaacenc: // http://trac.ffmpeg.org/wiki/Encode/AAC
                        args.Append($"-c:a aac -ac 2 -b:a {FFmpeg.AAC_Bitrate}k "); // -ac 2 required otherwise failing with 7.1
                        break;
                    case FFmpegAudioCodec.libopus: // https://www.ffmpeg.org/ffmpeg-codecs.html#libopus-1
                        args.Append($"-c:a libopus -b:a {FFmpeg.Opus_Bitrate}k ");
                        break;
                    case FFmpegAudioCodec.libvorbis: // http://trac.ffmpeg.org/wiki/TheoraVorbisEncodingGuide
                        args.Append($"-c:a libvorbis -qscale:a {FFmpeg.Vorbis_QScale} ");
                        break;
                    case FFmpegAudioCodec.libmp3lame: // http://trac.ffmpeg.org/wiki/Encode/MP3
                        args.Append($"-c:a libmp3lame -qscale:a {FFmpeg.MP3_QScale} ");
                        break;
                }
            }

            if (Duration > 0)
            {
                string duration = isCustom ? "$duration$" : Duration.ToString("0.0", CultureInfo.InvariantCulture);
                args.Append($"-t {duration} "); // duration limit
            }

            args.Append("-y "); // overwrite file

            string output = isCustom ? "$output$" : Path.ChangeExtension(OutputPath, IsLossless ? "mp4" : FFmpeg.Extension);
            args.Append($"\"{output}\"");

            return args.ToString();
        }

        private void AppendVideoEncodingArgs(StringBuilder args, string framerate, bool isHdrTonemapPass)
        {
            if (IsLossless || FFmpeg.VideoCodec != FFmpegVideoCodec.apng)
            {
                if (!(IsLossless && CaptureHDREnabled && IsRecording && !HdrDxgiPipeRecording))
                {
                    string videoCodec;

                    if (IsLossless)
                    {
                        videoCodec = FFmpegVideoCodec.libx264.ToString();
                    }
                    else if (FFmpeg.VideoCodec == FFmpegVideoCodec.libvpx_vp9)
                    {
                        videoCodec = "libvpx-vp9";
                    }
                    else
                    {
                        videoCodec = FFmpeg.VideoCodec.ToString();
                    }

                    args.Append($"-c:v {videoCodec} ");
                }

                if (!isHdrTonemapPass)
                {
                    args.Append($"-r {framerate} "); // output FPS
                }
            }

            if (IsLossless)
            {
                if (CaptureHDREnabled && IsRecording && !HdrDxgiPipeRecording)
                {
                    // gbrpf32le needs ffv1 v4 (experimental/disabled in bundled FFmpeg). Use gbrp16le v3 instead.
                    args.Append("-c:v ffv1 -level 3 -pix_fmt gbrp16le -color_primaries bt709 -color_trc linear -colorspace bt709 -color_range pc ");
                }
                else
                {
                    args.Append($"-preset {FFmpegPreset.ultrafast} ");
                    args.Append($"-tune {FFmpegTune.zerolatency} ");
                    args.Append("-qp 0 ");
                }
            }
            else
            {
                switch (FFmpeg.VideoCodec)
                {
                    case FFmpegVideoCodec.libx264: // https://trac.ffmpeg.org/wiki/Encode/H.264
                    case FFmpegVideoCodec.libx265: // https://trac.ffmpeg.org/wiki/Encode/H.265
                        args.Append($"-preset {FFmpeg.x264_Preset} ");
                        if (IsRecording && !isHdrTonemapPass) args.Append($"-tune {FFmpegTune.zerolatency} ");
                        if (FFmpeg.x264_Use_Bitrate)
                        {
                            args.Append($"-b:v {FFmpeg.x264_Bitrate}k ");
                        }
                        else
                        {
                            args.Append($"-crf {FFmpeg.x264_CRF} ");
                        }
                        args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise can't stream in Chrome
                        if (isHdrTonemapPass)
                        {
                            args.Append("-color_primaries bt709 -color_trc bt709 -colorspace bt709 -color_range pc ");
                        }

                        args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                        break;
                    case FFmpegVideoCodec.libvpx: // https://trac.ffmpeg.org/wiki/Encode/VP8
                    case FFmpegVideoCodec.libvpx_vp9: // https://trac.ffmpeg.org/wiki/Encode/VP9
                        if (IsRecording && !isHdrTonemapPass) args.Append("-deadline realtime ");
                        args.Append($"-b:v {FFmpeg.VPx_Bitrate}k ");
                        args.Append("-pix_fmt yuv420p "); // -pix_fmt yuv420p required otherwise causing issues in Chrome related to WebM transparency support
                        break;
                    case FFmpegVideoCodec.libxvid: // https://trac.ffmpeg.org/wiki/Encode/MPEG-4
                        args.Append($"-qscale:v {FFmpeg.XviD_QScale} ");
                        break;
                    case FFmpegVideoCodec.h264_nvenc: // https://trac.ffmpeg.org/wiki/HWAccelIntro#NVENC
                    case FFmpegVideoCodec.hevc_nvenc:
                        args.Append($"-preset {FFmpeg.NVENC_Preset} ");
                        args.Append($"-tune {FFmpeg.NVENC_Tune} ");
                        args.Append($"-b:v {FFmpeg.NVENC_Bitrate}k ");
                        args.Append("-movflags +faststart "); // This will move some information to the beginning of your file and allow the video to begin playing before it is completely downloaded by the viewer
                        break;
                    case FFmpegVideoCodec.h264_amf:
                    case FFmpegVideoCodec.hevc_amf:
                        args.Append($"-usage {FFmpeg.AMF_Usage} ");
                        args.Append($"-quality {FFmpeg.AMF_Quality} ");
                        args.Append($"-b:v {FFmpeg.AMF_Bitrate}k ");
                        args.Append("-pix_fmt yuv420p ");
                        break;
                    case FFmpegVideoCodec.h264_qsv: // https://trac.ffmpeg.org/wiki/Hardware/QuickSync
                    case FFmpegVideoCodec.hevc_qsv:
                        args.Append($"-preset {FFmpeg.QSV_Preset} ");
                        args.Append($"-b:v {FFmpeg.QSV_Bitrate}k ");
                        break;
                    case FFmpegVideoCodec.libwebp: // https://www.ffmpeg.org/ffmpeg-codecs.html#libwebp
                        args.Append("-lossless 0 ");
                        args.Append("-preset default ");
                        args.Append("-loop 0 ");
                        break;
                    case FFmpegVideoCodec.apng:
                        args.Append("-f apng ");
                        args.Append("-plays 0 ");
                        break;
                }

                switch (FFmpeg.VideoCodec)
                {
                    case FFmpegVideoCodec.libx265:
                    case FFmpegVideoCodec.hevc_nvenc:
                    case FFmpegVideoCodec.hevc_amf:
                    case FFmpegVideoCodec.hevc_qsv:
                        args.Append("-tag:v hvc1 "); // https://trac.ffmpeg.org/wiki/Encode/H.265#FinalCutandApplestuffcompatibility
                        break;
                }
            }
        }

        private void AppendDDAGrabFilterComplex(StringBuilder args, string framerate)
        {
            args.Append("-filter_complex \"");
            args.Append(BuildDDAGrabGraph(framerate, forFilterComplex: true));
            args.Append("\" ");

            if (FFmpeg.IsAudioSourceSelected)
            {
                args.Append("-map \"[v]\" -map 0:a ");
            }
            else
            {
                args.Append("-map \"[v]\" ");
            }
        }

        private string BuildDDAGrabGraph(string framerate, bool forFilterComplex)
        {
            Rectangle captureArea = GetDDAGrabCaptureArea(out int monitorIndex, out string deviceName);

            StringBuilder graph = new StringBuilder();

            if (forFilterComplex)
            {
                graph.Append("ddagrab=");
            }

            graph.Append($"output_idx={monitorIndex}:");
            graph.Append($"draw_mouse={DrawCursor.ToString().ToLowerInvariant()}:");
            graph.Append($"framerate={framerate}:");
            graph.Append($"offset_x={captureArea.X}:");
            graph.Append($"offset_y={captureArea.Y}:");
            graph.Append($"video_size={captureArea.Width}x{captureArea.Height}:");

            if (CaptureHDREnabled)
            {
                float normScale = Screenshot.GetSdrWhiteNormalizationScale(deviceName);
                string scale = normScale.ToString("0.######", CultureInfo.InvariantCulture);

                graph.Append("output_fmt=rgbaf16,hwdownload,format=rgbaf16,format=gbrpf32le,");
                graph.Append($"colorchannelmixer=rr={scale}:gg={scale}:bb={scale},");
                graph.Append("format=gbrp16le");
            }
            else
            {
                graph.Append("output_fmt=bgra");

                if (FFmpeg.VideoCodec != FFmpegVideoCodec.h264_nvenc && FFmpeg.VideoCodec != FFmpegVideoCodec.hevc_nvenc)
                {
                    graph.Append(",hwdownload,format=bgra");
                }
            }

            if (forFilterComplex)
            {
                graph.Append("[v]");
            }

            return graph.ToString();
        }

        private Rectangle GetDDAGrabCaptureArea(out int monitorIndex, out string deviceName)
        {
            Screen[] screens = Screen.AllScreens.OrderBy(x => !x.Primary).ToArray();
            monitorIndex = 0;
            Rectangle captureArea = screens[0].Bounds;
            deviceName = screens[0].DeviceName;
            int maxIntersectionArea = 0;

            for (int i = 0; i < screens.Length; i++)
            {
                Screen screen = screens[i];
                Rectangle intersection = Rectangle.Intersect(screen.Bounds, CaptureArea);
                int intersectionArea = intersection.Width * intersection.Height;

                if (intersectionArea > maxIntersectionArea)
                {
                    maxIntersectionArea = intersectionArea;

                    monitorIndex = i;
                    captureArea = new Rectangle(intersection.X - screen.Bounds.X, intersection.Y - screen.Bounds.Y, intersection.Width, intersection.Height);
                    deviceName = screen.DeviceName;
                }
            }

            captureArea = CaptureHelpers.EvenRectangleSize(captureArea);

            return captureArea;
        }

        private bool ShouldUseDDAGrab()
        {
            if (CaptureHDREnabled)
            {
                return true;
            }

            if (FFmpeg.VideoSource.Equals(FFmpegCaptureDevice.DDAGrab.Value, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return false;
        }

        private void AppendDDAGrabVideoInput(StringBuilder args, string framerate)
        {
            Rectangle captureArea = GetDDAGrabCaptureArea(out int monitorIndex, out string deviceName);

            // https://ffmpeg.org/ffmpeg-filters.html#ddagrab
            AppendInputDevice(args, "lavfi", false);
            args.Append("-i ddagrab=");
            args.Append($"output_idx={monitorIndex}:");
            args.Append($"draw_mouse={DrawCursor.ToString().ToLowerInvariant()}:");
            args.Append($"framerate={framerate}:");
            args.Append($"offset_x={captureArea.X}:");
            args.Append($"offset_y={captureArea.Y}:");
            args.Append($"video_size={captureArea.Width}x{captureArea.Height}:");

            if (CaptureHDREnabled)
            {
                args.Append("output_fmt=rgbaf16");
                args.Append(",hwdownload");
                args.Append(",format=rgbaf16");
            }
            else
            {
                args.Append("output_fmt=bgra");

                if (FFmpeg.VideoCodec != FFmpegVideoCodec.h264_nvenc && FFmpeg.VideoCodec != FFmpegVideoCodec.hevc_nvenc)
                {
                    args.Append(",hwdownload");
                    args.Append(",format=bgra");
                }
            }

            args.Append(" ");
        }

        private void AppendInputDevice(StringBuilder args, string inputDevice, bool audioSource)
        {
            args.Append($"-f {inputDevice} ");
            args.Append("-thread_queue_size 1024 "); // This option sets the maximum number of queued packets when reading from the file or device.
            args.Append("-rtbufsize 256M "); // Default real time buffer size is 3041280 (3M)

            if (audioSource)
            {
                args.Append("-audio_buffer_size 80 "); // Set audio device buffer size in milliseconds (which can directly impact latency, depending on the device).
            }
        }
    }
}
