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
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;

namespace ShareX.ScreenCaptureLib
{
    public class ScreenRecorder : IDisposable
    {
        public bool IsRecording { get; private set; }

        public int FPS
        {
            get
            {
                return fps;
            }
            set
            {
                if (!IsRecording)
                {
                    fps = value;
                    UpdateInfo();
                }
            }
        }

        public float DurationSeconds
        {
            get
            {
                return durationSeconds;
            }
            set
            {
                if (!IsRecording)
                {
                    durationSeconds = value;
                    UpdateInfo();
                }
            }
        }

        public Rectangle CaptureRectangle
        {
            get
            {
                return captureRectangle;
            }
            private set
            {
                if (!IsRecording)
                {
                    captureRectangle = value;
                }
            }
        }

        public string CachePath { get; private set; }

        public ScreenRecordOutput OutputType { get; private set; }

        public ScreenRecordingOptions Options { get; set; }

        public event Action RecordingStarted;

        public delegate void ProgressEventHandler(int progress);
        public event ProgressEventHandler EncodingProgressChanged;

        private int fps, delay, frameCount, previousProgress;
        private float durationSeconds;
        private Screenshot screenshot;
        private Rectangle captureRectangle;
        private ImageCache imgCache;
        private FFmpegCLIManager ffmpeg;
        private bool stopRequested;
        private bool hdrDxgiPipeRecording;

        public ScreenRecorder(ScreenRecordOutput outputType, ScreenRecordingOptions options, Screenshot screenshot, Rectangle captureRectangle)
        {
            if (string.IsNullOrEmpty(options.OutputPath))
            {
                throw new Exception("Screen recorder cache path is empty.");
            }

            FPS = options.FPS;
            DurationSeconds = options.Duration;
            CaptureRectangle = captureRectangle;
            CachePath = options.OutputPath;
            OutputType = outputType;

            Options = options;

            switch (OutputType)
            {
                default:
                case ScreenRecordOutput.FFmpeg:
                    FileHelpers.CreateDirectoryFromFilePath(Options.OutputPath);
                    ffmpeg = new FFmpegCLIManager(Options.FFmpeg.FFmpegPath);
                    ffmpeg.ShowError = true;
                    ffmpeg.EncodeStarted += OnRecordingStarted;
                    ffmpeg.EncodeProgressChanged += OnEncodingProgressChanged;
                    break;
                case ScreenRecordOutput.GIF:
                    imgCache = new HardDiskCache(Options);
                    break;
            }

            this.screenshot = screenshot;
        }

        private void UpdateInfo()
        {
            delay = 1000 / fps;
            frameCount = (int)(fps * durationSeconds);
        }

        public void StartRecording()
        {
            if (!IsRecording)
            {
                IsRecording = true;
                stopRequested = false;

                if (OutputType == ScreenRecordOutput.FFmpeg)
                {
                    if (Options.UseHdrDxgiPipe)
                    {
                        RecordUsingHdrDxgiPipe();
                    }
                    else
                    {
                        ffmpeg.Run(Options.GetFFmpegCommands());
                    }
                }
                else
                {
                    OnRecordingStarted();
                    RecordUsingCache();
                }
            }

            IsRecording = false;
        }

        private void RecordUsingHdrDxgiPipe()
        {
            Rectangle captureRect = CaptureHelpers.EvenRectangleSize(CaptureRectangle);
            int width = captureRect.Width;
            int height = captureRect.Height;
            hdrDxgiPipeRecording = true;
            Options.HdrDxgiPipeRecording = true;

            try
            {
                string args = Options.GetFFmpegHdrDxgiPipeArgs(width, height);
                DebugHelper.WriteLine("HDR recording: piping DXGI tonemapped frames to FFmpeg.");

                if (!ffmpeg.Start(args))
                {
                    DebugHelper.WriteLine("HDR recording: failed to start FFmpeg pipe encoder.");
                    return;
                }

                OnRecordingStarted();

                using (Screenshot.HdrRecordingCapture capture = screenshot.BeginHdrRecordingCapture(captureRect))
                {
                    Stream stdin = ffmpeg.GetStandardInputStream();
                    byte[] frameBuffer = new byte[capture.FrameBytes];
                    bool hasFrame = false;

                    while (!hasFrame && !stopRequested)
                    {
                        if (capture.TryCaptureFrame(frameBuffer))
                        {
                            hasFrame = true;
                        }
                        else
                        {
                            Thread.Sleep(1);
                        }
                    }

                    RecordUsingTimedFrameCapture(() =>
                    {
                        if (stdin == null || !ffmpeg.IsProcessRunning)
                        {
                            stopRequested = true;
                            return;
                        }

                        if (!capture.TryCaptureFrame(frameBuffer))
                        {
                            stopRequested = true;
                            return;
                        }

                        stdin.Write(frameBuffer, 0, frameBuffer.Length);
                    });
                }
            }
            catch (Exception ex)
            {
                DebugHelper.WriteException(ex, "HDR recording failed.");
            }
            finally
            {
                hdrDxgiPipeRecording = false;
                Options.HdrDxgiPipeRecording = false;
                Screenshot.WarmHdrCapture();
                ffmpeg.Finish();
            }
        }

        public bool FFmpegHdrTonemapVideo(string input, string output)
        {
            FileHelpers.CreateDirectoryFromFilePath(output);

            Options.IsRecording = false;
            Options.IsLossless = false;
            Options.InputPath = input;
            Options.OutputPath = output;

            try
            {
                ffmpeg.TrackEncodeProgress = false;

                return HdrRecordingTonemapper.Process(Options.FFmpeg.FFmpegPath, Options, OnEncodingProgressChanged);
            }
            finally
            {
                ffmpeg.TrackEncodeProgress = false;
            }
        }

        private void RecordUsingTimedFrameCapture(Action writeFrame)
        {
            Stopwatch clock = Stopwatch.StartNew();
            long framesSent = 0;

            while (!stopRequested && (frameCount == 0 || framesSent < frameCount))
            {
                double frameTime = framesSent / (double)fps;

                while (clock.Elapsed.TotalSeconds < frameTime && !stopRequested)
                {
                    Thread.Sleep(1);
                }

                if (stopRequested)
                {
                    break;
                }

                writeFrame();
                framesSent++;
            }
        }

        private void RecordUsingTimedFrameCapture(Action<Bitmap> writeFrame)
        {
            RecordUsingTimedFrameCapture(() =>
            {
                using (Bitmap bmp = screenshot.CaptureRectangle(CaptureRectangle))
                {
                    writeFrame(bmp);
                }
            });
        }

        private void RecordUsingCache()
        {
            try
            {
                RecordUsingTimedFrameCapture(bmp => imgCache.AddImageAsync((Image)bmp.Clone()));
            }
            finally
            {
                imgCache.Finish();
            }
        }

        public void StopRecording()
        {
            stopRequested = true;

            if (ffmpeg != null && !hdrDxgiPipeRecording)
            {
                ffmpeg.Close();
            }
        }

        public void SaveAsGIF(string path, GIFQuality quality)
        {
            if (imgCache != null && imgCache is HardDiskCache && !IsRecording)
            {
                FileHelpers.CreateDirectoryFromFilePath(path);

                HardDiskCache hdCache = imgCache as HardDiskCache;

                using (AnimatedGifCreator gifEncoder = new AnimatedGifCreator(path, delay))
                {
                    int i = 0;
                    int count = hdCache.Count;

                    foreach (Image img in hdCache.GetImageEnumerator())
                    {
                        i++;
                        OnEncodingProgressChanged((int)((float)i / count * 100));

                        using (img)
                        {
                            gifEncoder.AddFrame(img, quality);
                        }
                    }
                }
            }
        }

        public bool FFmpegEncodeVideo(string input, string output)
        {
            FileHelpers.CreateDirectoryFromFilePath(output);

            Options.IsRecording = false;
            Options.IsLossless = false;
            Options.InputPath = input;
            Options.OutputPath = output;

            try
            {
                ffmpeg.TrackEncodeProgress = true;

                return ffmpeg.Run(Options.GetFFmpegCommands());
            }
            finally
            {
                ffmpeg.TrackEncodeProgress = false;
            }
        }

        public bool FFmpegEncodeAsGIF(string input, string output)
        {
            FileHelpers.CreateDirectoryFromFilePath(output);

            try
            {
                ffmpeg.TrackEncodeProgress = true;

                StringBuilder args = new StringBuilder();

                args.Append("-threads 0 ");
                args.Append($"-i \"{input}\" ");

                // FFmpeg 8 no longer auto-wires an unlabeled palettegen pad. Split the
                // video stream explicitly: https://ffmpeg.org/ffmpeg-filters.html#palettegen-1
                args.Append($"-filter_complex \"[0:v]split[v0][v1];[v0]palettegen=stats_mode={Options.FFmpeg.GIFStatsMode}[p];");
                args.Append($"[v1][p]paletteuse=dither={Options.FFmpeg.GIFDither}");

                if (Options.FFmpeg.GIFDither == FFmpegPaletteUseDither.bayer)
                {
                    args.Append($":bayer_scale={Options.FFmpeg.GIFBayerScale}");
                }

                if (Options.FFmpeg.GIFStatsMode == FFmpegPaletteGenStatsMode.single)
                {
                    args.Append(":new=1");
                }

                args.Append("\" ");
                args.Append("-y ");
                args.Append($"\"{output}\"");

                return ffmpeg.Run(args.ToString());
            }
            finally
            {
                ffmpeg.TrackEncodeProgress = false;
            }
        }

        protected void OnRecordingStarted()
        {
            RecordingStarted?.Invoke();
        }

        protected void OnEncodingProgressChanged(float progress)
        {
            int currentProgress = (int)progress;

            if (EncodingProgressChanged != null && currentProgress != previousProgress)
            {
                EncodingProgressChanged(currentProgress);
                previousProgress = currentProgress;
            }
        }

        public void Dispose()
        {
            if (ffmpeg != null)
            {
                ffmpeg.Dispose();
            }

            if (imgCache != null)
            {
                imgCache.Dispose();
            }
        }
    }
}
