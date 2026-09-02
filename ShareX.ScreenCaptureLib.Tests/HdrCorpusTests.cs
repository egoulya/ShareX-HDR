#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Xunit;

namespace ShareX.ScreenCaptureLib.Tests
{
    /// <summary>
    /// Covers the offline evaluation seam: the <see cref="HdrRawFrame"/> corpus format and
    /// <see cref="HdrFrameTonemapper.TonemapFrame"/>, which replays a captured frame through the
    /// same primitives the live capture path uses.
    /// </summary>
    [Collection(HdrTestCollection.Name)]
    public class HdrCorpusTests : IDisposable
    {
        private readonly string tempDirectory;

        public HdrCorpusTests()
        {
            HdrTonemap.ResetAutoModeMemory();
            tempDirectory = Path.Combine(Path.GetTempPath(), "sharex-hdr-corpus-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
        }

        public void Dispose()
        {
            HdrFrameDump.Directory = null;
            HdrFrameDump.Reset();
            HdrTonemap.ResetAutoModeMemory();

            try
            {
                Directory.Delete(tempDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Leaving a temp directory behind must not fail a test run.
            }
        }

        private static readonly (byte r, byte g, byte b)[] SrgbPalette =
        {
            (0, 0, 0),
            (255, 255, 255),
            (128, 128, 128),
            (32, 32, 32),
            (192, 192, 192),
            (220, 40, 40),
            (40, 180, 70),
            (50, 90, 210),
            (240, 200, 80),
            (18, 18, 18),
            (7, 90, 90),
            (250, 250, 200),
            (1, 1, 1),
            (254, 128, 3),
            (77, 77, 77),
            (200, 12, 190)
        };

        // ================================================================
        // Corpus format
        // ================================================================

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Raw_frame_round_trips_through_file(bool compress)
        {
            HdrRawFrame written = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.NativeHdr, "round-trip");
            written.Metadata.Notes = "unit test frame";
            written.Metadata.ColorSpace = HdrDisplayProbe.ColorSpaceScRgb;
            written.Metadata.DeviceName = @"\\.\DISPLAY3";

            string path = Path.Combine(tempDirectory, "frame" + HdrRawFrame.FileExtension);
            HdrRawFrame.Write(path, written, compress);
            HdrRawFrame read = HdrRawFrame.Read(path);

            Assert.Equal(written.Width, read.Width);
            Assert.Equal(written.Height, read.Height);
            Assert.Equal(written.Stride, read.Stride);
            Assert.Equal(written.DxgiFormat, read.DxgiFormat);
            Assert.Equal(written.SdrWhiteNits, read.SdrWhiteNits);
            Assert.Equal(compress, read.Metadata.Compressed);
            Assert.Equal(HdrCorpusScenario.NativeHdr, read.Metadata.Scenario);
            Assert.Equal("round-trip", read.Metadata.Label);
            Assert.Equal("unit test frame", read.Metadata.Notes);
            Assert.Equal(@"\\.\DISPLAY3", read.Metadata.DeviceName);
            Assert.Equal(HdrDisplayProbe.ColorSpaceScRgb, read.Metadata.ColorSpace);
            Assert.Equal(written.Pixels, read.Pixels);
        }

        [Fact]
        public void Raw_frame_metadata_reads_without_payload()
        {
            HdrRawFrame frame = BuildScRgbPaletteFrame(400f, HdrCorpusScenario.AutoHdr, "metadata-only");
            frame.Metadata.MonitorRect = new HdrFrameRect(0, 0, 3840, 2160);
            frame.Metadata.CaptureRect = new HdrFrameRect(100, 50, 800, 600);

            string path = Path.Combine(tempDirectory, "meta" + HdrRawFrame.FileExtension);
            HdrRawFrame.Write(path, frame);

            HdrFrameMetadata metadata = HdrRawFrame.ReadMetadata(path);

            Assert.Equal(HdrCorpusScenario.AutoHdr, metadata.Scenario);
            Assert.Equal(400f, metadata.SdrWhiteNits);
            Assert.Equal(3840, metadata.MonitorRect.Width);
            Assert.Equal(800, metadata.CaptureRect.Width);
            Assert.Equal(50, metadata.CaptureRect.Y);
            Assert.Equal(frame.Pixels.Length, metadata.PayloadBytes);
            Assert.False(string.IsNullOrEmpty(metadata.PayloadSha256));
        }

        [Fact]
        public void Raw_frame_rejects_payload_that_does_not_match_its_hash()
        {
            HdrRawFrame frame = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "tamper");
            string path = Path.Combine(tempDirectory, "tampered" + HdrRawFrame.FileExtension);
            HdrRawFrame.Write(path, frame, compress: false);

            byte[] bytes = File.ReadAllBytes(path);
            bytes[bytes.Length - 3] ^= 0xFF;
            File.WriteAllBytes(path, bytes);

            InvalidDataException error = Assert.Throws<InvalidDataException>(() => HdrRawFrame.Read(path));
            Assert.Contains("hash mismatch", error.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void Raw_frame_rejects_foreign_files_and_truncation()
        {
            string bogus = Path.Combine(tempDirectory, "bogus" + HdrRawFrame.FileExtension);
            File.WriteAllBytes(bogus, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 });
            Assert.Throws<InvalidDataException>(() => HdrRawFrame.ReadMetadata(bogus));

            HdrRawFrame frame = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "truncated");
            string path = Path.Combine(tempDirectory, "truncated" + HdrRawFrame.FileExtension);
            HdrRawFrame.Write(path, frame, compress: false);

            byte[] bytes = File.ReadAllBytes(path);
            File.WriteAllBytes(path, bytes[..(bytes.Length - 64)]);

            Assert.Throws<EndOfStreamException>(() => HdrRawFrame.Read(path));
        }

        [Fact]
        public unsafe void From_mapped_region_tightly_packs_a_padded_source()
        {
            const int width = 5;
            const int height = 3;
            const int bpp = 8;
            int rowPitch = width * bpp + 48; // DXGI staging textures are padded.

            byte[] source = new byte[rowPitch * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    // Mark each pixel so a stride bug shows up as shifted values, not zeros.
                    source[y * rowPitch + x * bpp] = (byte)(y * 16 + x + 1);
                }

                for (int pad = width * bpp; pad < rowPitch; pad++)
                {
                    source[y * rowPitch + pad] = 0xEE;
                }
            }

            HdrRawFrame frame;
            fixed (byte* sourcePtr = source)
            {
                frame = HdrRawFrame.FromMappedRegion((IntPtr)sourcePtr, rowPitch,
                    HdrPixelConvert.FormatR16G16B16A16Float, 0, 0, width, height,
                    new HdrFrameMetadata { SdrWhiteNits = 203f });
            }

            Assert.Equal(width * bpp, frame.Stride);
            Assert.Equal(width * bpp * height, frame.Pixels.Length);

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal((byte)(y * 16 + x + 1), frame.Pixels[y * frame.Stride + x * bpp]);
                }
            }

            Assert.DoesNotContain((byte)0xEE, frame.Pixels);
        }

        [Fact]
        public unsafe void From_mapped_region_honours_a_sub_region_offset()
        {
            const int width = 8;
            const int height = 8;
            const int bpp = 4;
            int rowPitch = width * bpp;

            byte[] source = new byte[rowPitch * height];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    source[y * rowPitch + x * bpp] = (byte)(y * 8 + x);
                }
            }

            HdrRawFrame frame;
            fixed (byte* sourcePtr = source)
            {
                frame = HdrRawFrame.FromMappedRegion((IntPtr)sourcePtr, rowPitch,
                    HdrPixelConvert.FormatR10G10B10A2Unorm, 2, 3, 4, 2,
                    new HdrFrameMetadata { SdrWhiteNits = 203f });
            }

            Assert.Equal(4, frame.Width);
            Assert.Equal(2, frame.Height);
            Assert.Equal(2, frame.Metadata.SourceRegion.X);
            Assert.Equal(3, frame.Metadata.SourceRegion.Y);
            Assert.Equal((byte)(3 * 8 + 2), frame.Pixels[0]);
            Assert.Equal((byte)(4 * 8 + 2), frame.Pixels[frame.Stride]);
        }

        [Fact]
        public void Corpus_enumerates_and_writes_a_manifest()
        {
            for (int i = 0; i < 3; i++)
            {
                HdrRawFrame frame = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "scene-" + i);
                HdrRawFrame.Write(Path.Combine(tempDirectory, "frame-" + i + HdrRawFrame.FileExtension), frame);
            }

            var entries = HdrFrameCorpus.Enumerate(tempDirectory);
            Assert.Equal(3, entries.Count);
            Assert.All(entries, e => Assert.True(e.FileBytes > 0));
            Assert.Contains(entries, e => e.Metadata.Label == "scene-1");

            string manifest = HdrFrameCorpus.WriteManifest(tempDirectory);
            Assert.True(File.Exists(manifest));
            string json = File.ReadAllText(manifest);
            Assert.Contains("scene-2", json);
            Assert.Contains("PayloadSha256", json);
        }

        [Fact]
        public void Corpus_enumeration_skips_unreadable_files()
        {
            HdrRawFrame frame = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "good");
            HdrRawFrame.Write(Path.Combine(tempDirectory, "good" + HdrRawFrame.FileExtension), frame);
            File.WriteAllText(Path.Combine(tempDirectory, "junk" + HdrRawFrame.FileExtension), "not a frame");

            var entries = HdrFrameCorpus.Enumerate(tempDirectory);
            Assert.Single(entries);
            Assert.Equal("good", entries[0].Metadata.Label);
        }

        // ================================================================
        // Dump switch
        // ================================================================

        [Fact]
        public unsafe void Dump_is_off_until_a_directory_is_set()
        {
            HdrFrameDump.Directory = null;
            Assert.False(HdrFrameDump.Enabled);

            byte[] source = new byte[8 * 4 * 4];
            fixed (byte* sourcePtr = source)
            {
                Assert.Null(HdrFrameDump.TryDump((IntPtr)sourcePtr, 8 * 4, HdrPixelConvert.FormatR10G10B10A2Unorm,
                    0, 0, 8, 4, new HdrFrameMetadata { SdrWhiteNits = 203f }));
            }

            Assert.Equal(0, HdrFrameDump.DumpedCount);
        }

        [Fact]
        public unsafe void Dump_writes_a_readable_frame_and_respects_its_quota()
        {
            HdrFrameDump.Reset();
            HdrFrameDump.Directory = tempDirectory;
            HdrFrameDump.Scenario = HdrCorpusScenario.AutoHdr;
            HdrFrameDump.Label = "dump test";
            HdrFrameDump.Notes = "quota";
            HdrFrameDump.MaxFrames = 2;

            HdrRawFrame template = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.AutoHdr, "ignored");

            for (int i = 0; i < 5; i++)
            {
                fixed (byte* pixels = template.Pixels)
                {
                    HdrFrameDump.TryDump((IntPtr)pixels, template.Stride, template.DxgiFormat,
                        0, 0, template.Width, template.Height,
                        HdrFrameDump.CreateTemplate(@"\\.\DISPLAY1", 203f, HdrDisplayProbe.ColorSpaceScRgb,
                            new Rectangle(0, 0, 3840, 2160), new Rectangle(0, 0, 1920, 1080), default));
                }
            }

            Assert.Equal(2, HdrFrameDump.DumpedCount);
            Assert.Equal(2, HdrFrameCorpus.Enumerate(tempDirectory).Count);

            HdrRawFrame written = HdrRawFrame.Read(HdrFrameDump.LastPath);
            Assert.Equal(HdrCorpusScenario.AutoHdr, written.Metadata.Scenario);
            Assert.Equal("dump test", written.Metadata.Label);
            Assert.Equal("quota", written.Metadata.Notes);
            Assert.Equal(template.Pixels, written.Pixels);

            // The device name must survive into the filename without illegal characters.
            string fileName = Path.GetFileName(HdrFrameDump.LastPath);
            Assert.Contains("DISPLAY1", fileName);
            Assert.Contains("AutoHdr", fileName);
            Assert.DoesNotContain('\\', fileName);
        }

        // ================================================================
        // The seam: offline replay
        // ================================================================

        /// <summary>
        /// Scenario 1 has a real ground truth: known sRGB content composited at SDR white on an
        /// HDR desktop must come back out of the pipeline as the same bytes. This is the
        /// whole-frame version of that check, through the same code the live capture path runs.
        /// </summary>
        [Theory]
        [InlineData(80f)]
        [InlineData(203f)]
        [InlineData(400f)]
        public void Desktop_frame_round_trips_to_original_srgb(float sdrWhiteNits)
        {
            HdrRawFrame frame = BuildScRgbPaletteFrame(sdrWhiteNits, HdrCorpusScenario.Desktop, "srgb-roundtrip");

            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop);

            Assert.Equal(HdrTonemapMode.Desktop, result.ResolvedMode);
            Assert.Equal(frame.Width, result.Sdr.Width);

            byte[] actual = ReadBgra(result.Sdr);
            for (int i = 0; i < SrgbPalette.Length; i++)
            {
                (byte r, byte g, byte b) = SrgbPalette[i];
                // +/-2: Bayer dither plus encode-LUT quantization, same budget as the pixel-level test.
                AssertNear(b, actual[i * 4 + 0], 2, $"blue at palette index {i}, {sdrWhiteNits} nits");
                AssertNear(g, actual[i * 4 + 1], 2, $"green at palette index {i}, {sdrWhiteNits} nits");
                AssertNear(r, actual[i * 4 + 2], 2, $"red at palette index {i}, {sdrWhiteNits} nits");
                Assert.Equal(255, actual[i * 4 + 3]);
            }
        }

        [Fact]
        public void Hdr10_frame_round_trips_to_original_srgb()
        {
            HdrRawFrame frame = BuildHdr10PaletteFrame(203f);

            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop);

            byte[] actual = ReadBgra(result.Sdr);
            for (int i = 0; i < SrgbPalette.Length; i++)
            {
                (byte r, byte g, byte b) = SrgbPalette[i];
                // +/-4: adds the BT.709 -> BT.2020 -> BT.709 round trip and 10-bit PQ quantization.
                AssertNear(b, actual[i * 4 + 0], 4, $"blue at palette index {i}");
                AssertNear(g, actual[i * 4 + 1], 4, $"green at palette index {i}");
                AssertNear(r, actual[i * 4 + 2], 4, $"red at palette index {i}");
            }
        }

        [Fact]
        public void Replay_from_disk_matches_replay_from_memory()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 6f);
            string path = Path.Combine(tempDirectory, "ramp" + HdrRawFrame.FileExtension);
            HdrRawFrame.Write(path, frame);

            using HdrTonemapResult fromMemory = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop);
            using HdrTonemapResult fromDisk = HdrFrameTonemapper.TonemapFrame(HdrRawFrame.Read(path), HdrTonemapMode.Desktop);

            Assert.Equal(fromMemory.ResolvedMode, fromDisk.ResolvedMode);
            Assert.Equal(fromMemory.Stats.MaxLuminance, fromDisk.Stats.MaxLuminance);
            Assert.Equal(fromMemory.Stats.P99Estimate, fromDisk.Stats.P99Estimate);
            Assert.Equal(ReadBgra(fromMemory.Sdr), ReadBgra(fromDisk.Sdr));
        }

        [Fact]
        public void Replay_is_deterministic_and_order_independent_without_a_hysteresis_key()
        {
            HdrRawFrame desktop = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "desktop");
            HdrRawFrame highlights = BuildHighlightRampFrame(203f, peakNorm: 4f);

            using HdrTonemapResult first = HdrFrameTonemapper.TonemapFrame(desktop, HdrTonemapMode.Auto);
            using HdrTonemapResult afterOther = RunAfter(highlights, desktop);

            Assert.Equal(first.ResolvedMode, afterOther.ResolvedMode);
            Assert.Equal(ReadBgra(first.Sdr), ReadBgra(afterOther.Sdr));
        }

        [Fact]
        public void Auto_mode_memory_reset_clears_remembered_decisions()
        {
            const string key = "corpus-reset-test";
            HdrTonemap.ResetAutoModeMemory();
            Assert.False(HdrTonemap.TryGetRememberedAutoMode(key, out _));

            HdrLuminanceStats hotSdr = new HdrLuminanceStats(10_000, 10, 0, 900, 1.0f, 0.95f);
            HdrTonemapMode resolved = HdrTonemap.ResolveMode(HdrTonemapMode.Auto, hotSdr, key, hdrDxgiCapture: false);

            Assert.True(HdrTonemap.TryGetRememberedAutoMode(key, out HdrTonemapMode remembered));
            Assert.Equal(resolved, remembered);

            HdrTonemap.ResetAutoModeMemory(key);
            Assert.False(HdrTonemap.TryGetRememberedAutoMode(key, out _));
        }

        [Fact]
        public void Every_tonemap_mode_produces_a_usable_frame()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 8f);

            foreach (HdrTonemapMode mode in new[]
            {
                HdrTonemapMode.Auto,
                HdrTonemapMode.Desktop,
                HdrTonemapMode.AutoHDR,
                HdrTonemapMode.Filmic,
                HdrTonemapMode.WindowsWIC
            })
            {
                using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, mode);

                Assert.Equal(frame.Width, result.Sdr.Width);
                Assert.Equal(frame.Height, result.Sdr.Height);

                byte[] pixels = ReadBgra(result.Sdr);
                bool anyNonZero = false;
                foreach (byte value in pixels)
                {
                    if (value != 0 && value != 255)
                    {
                        anyNonZero = true;
                        break;
                    }
                }

                Assert.True(anyNonZero, $"{mode} produced a fully clipped frame.");
            }
        }

        [Fact]
        public void Master_is_only_built_when_asked_for()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 4f);

            using HdrTonemapResult without = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop);
            Assert.Null(without.Master);

            using HdrTonemapResult with = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop,
                buildMaster: true);
            Assert.NotNull(with.Master);
            Assert.Equal(frame.Width, with.Master.Width);
            Assert.True(with.Master.MaxCLL > 203f,
                $"A 4x paper-white ramp should exceed SDR white in the master, got {with.Master.MaxCLL:0.#} nits.");
            Assert.Equal(ReadBgra(without.Sdr), ReadBgra(with.Sdr));
        }

        [Fact]
        public void Build_stats_matches_what_tonemap_frame_used()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 5f);

            HdrLuminanceStats standalone = HdrFrameTonemapper.BuildStats(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop);

            Assert.Equal(standalone.SampleCount, result.Stats.SampleCount);
            Assert.Equal(standalone.MaxLuminance, result.Stats.MaxLuminance);
            Assert.Equal(standalone.P99Estimate, result.Stats.P99Estimate);
            Assert.Equal(standalone.AboveOneCount, result.Stats.AboveOneCount);
        }


        // ================================================================
        // Analysis helpers used by the offline evaluation runner
        // ================================================================

        [Fact]
        public void Decode_to_linear_matches_the_curve_input()
        {
            HdrRawFrame frame = BuildScRgbPaletteFrame(203f, HdrCorpusScenario.Desktop, "decode");

            HdrLinearFrame linear = HdrFrameTonemapper.DecodeToLinear(frame);

            Assert.Equal(frame.Width, linear.Width);
            Assert.Equal(frame.Height, linear.Height);
            Assert.Equal(frame.Width * frame.Height * 3, linear.Rgb.Length);
            Assert.Equal(203f, linear.SdrWhiteNits);

            for (int i = 0; i < SrgbPalette.Length; i++)
            {
                (byte r, byte g, byte b) = SrgbPalette[i];
                AssertLinearNear(SrgbToLinear(r), linear.Rgb[i * 3], $"red at palette index {i}");
                AssertLinearNear(SrgbToLinear(g), linear.Rgb[i * 3 + 1], $"green at palette index {i}");
                AssertLinearNear(SrgbToLinear(b), linear.Rgb[i * 3 + 2], $"blue at palette index {i}");
            }

            // White is the palette entry at index 1; its luminance must be exactly paper white.
            Assert.Equal(1f, linear.LuminanceAt(1), 2);
        }

        [Fact]
        public void Decode_to_linear_scales_with_the_display_white_level()
        {
            // The same scRGB bits mean different scene luminance on displays with different SDR
            // white levels. Decode must normalize both to 1.0 = paper white.
            foreach (float nits in new[] { 80f, 203f, 400f })
            {
                HdrRawFrame frame = BuildScRgbPaletteFrame(nits, HdrCorpusScenario.Desktop, "white-level");
                HdrLinearFrame linear = HdrFrameTonemapper.DecodeToLinear(frame);
                Assert.Equal(1f, linear.LuminanceAt(1), 2);
            }
        }

        [Fact]
        public void Build_master_does_not_depend_on_mode_or_exposure()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 4f);

            HdrMasterImage standalone = HdrFrameTonemapper.BuildMaster(frame);
            using HdrTonemapResult desktop = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop,
                exposure: 1.0f, buildMaster: true);
            using HdrTonemapResult filmic = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Filmic,
                exposure: 1.3f, buildMaster: true);

            // The master is the pre-tonemap reference, so a sweep can build it once per frame.
            Assert.Equal(standalone.Rgb, desktop.Master.Rgb);
            Assert.Equal(standalone.Rgb, filmic.Master.Rgb);
            Assert.Equal(standalone.MaxCLL, desktop.Master.MaxCLL);
            Assert.Equal(standalone.MaxCLL, filmic.Master.MaxCLL);
        }

        [Fact]
        public void Build_master_carries_the_mastering_display_through()
        {
            HdrRawFrame frame = BuildHighlightRampFrame(203f, peakNorm: 2f);
            frame.Metadata.SetMasteringDisplay(new HdrMasteringDisplay
            {
                RedX = 0.68f,
                RedY = 0.32f,
                MinLuminanceNits = 0.005f,
                MaxLuminanceNits = 1000f,
                HasValue = true
            });

            HdrMasterImage master = HdrFrameTonemapper.BuildMaster(frame);

            Assert.True(master.MasteringDisplay.HasValue);
            Assert.Equal(1000f, master.MasteringDisplay.MaxLuminanceNits);
            Assert.Equal(0.68f, master.MasteringDisplay.RedX, 3);
        }

        // ================================================================
        // Helpers
        // ================================================================

        private static HdrTonemapResult RunAfter(HdrRawFrame other, HdrRawFrame frame)
        {
            using (HdrFrameTonemapper.TonemapFrame(other, HdrTonemapMode.Auto))
            {
            }

            return HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Auto);
        }

        /// <summary>
        /// scRGB payloads are fp16, so a decoded value carries half-precision error: about one part
        /// in 2048 near 1.0. An absolute tolerance would be too tight at the top of the range and
        /// far too loose near black, so this is relative with a small absolute floor.
        /// </summary>
        private static void AssertLinearNear(float expected, float actual, string because)
        {
            float tolerance = Math.Max(1e-4f, Math.Abs(expected) * 0.001f);
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{because}: expected {expected:0.000000} +/-{tolerance:0.000000}, got {actual:0.000000}.");
        }

        private static void AssertNear(byte expected, byte actual, int tolerance, string because)
        {
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                $"{because}: expected {expected} +/-{tolerance}, got {actual}.");
        }

        private static byte[] ReadBgra(Bitmap bitmap)
        {
            byte[] pixels = new byte[bitmap.Width * bitmap.Height * 4];
            BitmapData bd = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bitmap.Height; y++)
                {
                    Marshal.Copy(bd.Scan0 + y * bd.Stride, pixels, y * bitmap.Width * 4, bitmap.Width * 4);
                }
            }
            finally
            {
                bitmap.UnlockBits(bd);
            }

            return pixels;
        }

        /// <summary>One row of <see cref="SrgbPalette"/> encoded as scRGB at SDR white.</summary>
        private static HdrRawFrame BuildScRgbPaletteFrame(float sdrWhiteNits, HdrCorpusScenario scenario, string label)
        {
            int width = SrgbPalette.Length;
            const int bpp = 8;
            byte[] pixels = new byte[width * bpp];
            float scale = sdrWhiteNits / HdrPixelConvert.SceneReferredWhiteNits;

            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = SrgbPalette[x];
                WriteScRgb(pixels, x * bpp,
                    SrgbToLinear(r) * scale,
                    SrgbToLinear(g) * scale,
                    SrgbToLinear(b) * scale);
            }

            return new HdrRawFrame(BuildMetadata(width, 1, bpp, HdrPixelConvert.FormatR16G16B16A16Float,
                sdrWhiteNits, HdrDisplayProbe.ColorSpaceScRgb, scenario, label), pixels);
        }

        /// <summary>The same palette encoded as HDR10 PQ, to exercise the other decode path.</summary>
        private static HdrRawFrame BuildHdr10PaletteFrame(float sdrWhiteNits)
        {
            int width = SrgbPalette.Length;
            const int bpp = 4;
            byte[] pixels = new byte[width * bpp];

            for (int x = 0; x < width; x++)
            {
                (byte r, byte g, byte b) = SrgbPalette[x];
                HdrPixelConvert.EncodeSrgb8ToPqBt2020(r, g, b, sdrWhiteNits,
                    out ushort rPq, out ushort gPq, out ushort bPq, out _);

                uint packed = (uint)(rPq >> 6) | ((uint)(gPq >> 6) << 10) | ((uint)(bPq >> 6) << 20) | (3u << 30);
                BitConverter.GetBytes(packed).CopyTo(pixels, x * bpp);
            }

            return new HdrRawFrame(BuildMetadata(width, 1, bpp, HdrPixelConvert.FormatR10G10B10A2Unorm,
                sdrWhiteNits, HdrDisplayProbe.ColorSpaceHdr10, HdrCorpusScenario.NativeHdr, "hdr10-palette"), pixels);
        }

        /// <summary>
        /// A 32x8 neutral ramp running from black to <paramref name="peakNorm"/> times paper white,
        /// so highlight roll-off and Auto-mode classification have something real to chew on.
        /// </summary>
        private static HdrRawFrame BuildHighlightRampFrame(float sdrWhiteNits, float peakNorm)
        {
            const int width = 32;
            const int height = 8;
            const int bpp = 8;
            byte[] pixels = new byte[width * height * bpp];
            float scale = sdrWhiteNits / HdrPixelConvert.SceneReferredWhiteNits;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = x / (float)(width - 1);
                    float value = t * t * peakNorm * scale;
                    // Tint the rows so chroma handling is exercised, not just neutrals.
                    float rowR = 1f + 0.15f * y / (height - 1);
                    float rowB = 1f - 0.15f * y / (height - 1);
                    WriteScRgb(pixels, (y * width + x) * bpp, value * rowR, value, value * rowB);
                }
            }

            return new HdrRawFrame(BuildMetadata(width, height, bpp, HdrPixelConvert.FormatR16G16B16A16Float,
                sdrWhiteNits, HdrDisplayProbe.ColorSpaceScRgb, HdrCorpusScenario.NativeHdr, "highlight-ramp"), pixels);
        }

        private static HdrFrameMetadata BuildMetadata(int width, int height, int bpp, int dxgiFormat,
            float sdrWhiteNits, uint colorSpace, HdrCorpusScenario scenario, string label)
        {
            return new HdrFrameMetadata
            {
                Width = width,
                Height = height,
                Stride = width * bpp,
                DxgiFormat = dxgiFormat,
                SdrWhiteNits = sdrWhiteNits,
                ColorSpace = colorSpace,
                Scenario = scenario,
                Label = label,
                CapturedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                PayloadBytes = width * height * bpp,
                MonitorRect = new HdrFrameRect(0, 0, width, height),
                CaptureRect = new HdrFrameRect(0, 0, width, height),
                SourceRegion = new HdrFrameRect(0, 0, width, height)
            };
        }

        private static void WriteScRgb(byte[] destination, int offset, float r, float g, float b)
        {
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)r)).CopyTo(destination, offset);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)g)).CopyTo(destination, offset + 2);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)b)).CopyTo(destination, offset + 4);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)1f)).CopyTo(destination, offset + 6);
        }

        private static float SrgbToLinear(byte value)
        {
            float c = value / 255f;
            return c <= 0.04045f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
        }
    }
}
