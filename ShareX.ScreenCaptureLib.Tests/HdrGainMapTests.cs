#region License Information (GPL v3)

/*
    ShareX - A program that allows you to take screenshots and share any file type
    Copyright (c) 2007-2026 ShareX Team
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Drawing.Imaging;
using System.Text;
using ShareX.HelpersLib;
using Xunit;

namespace ShareX.ScreenCaptureLib.Tests
{
    /// <summary>
    /// Covers <see cref="HdrGainMap"/> and <see cref="UltraHdrJpegWriter"/>: the side channel that
    /// carries what a global SDR curve structurally cannot, and the container that ships it in a file
    /// naive decoders still read correctly.
    ///
    /// The container assertions matter more than they look. A wrong MPF offset produces a file that
    /// opens fine as SDR everywhere, so the map is silently ignored and the failure is
    /// indistinguishable from a platform that stripped it - exactly the wrong bug to debug by eye.
    /// </summary>
    [Collection(HdrTestCollection.Name)]
    public class HdrGainMapTests
    {
        [Fact]
        public void ReconstructsHdrLuminanceFromTheStoredMap()
        {
            // A ramp from black through paper white and on to 6x, so the curve has real compression to
            // do and the map has something to carry.
            HdrRawFrame frame = BuildRampFrame(256, 8, 6.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            (double median, double max) = MeasureRoundTrip(hdr, sdr, map, abovePaperWhiteOnly: true);

            // Full-resolution reconstruction is near-exact: the ratio is stored against the very bytes
            // a decoder multiplies, so only the log quantisation and the percentile clamp contribute.
            Assert.True(median < 0.02, $"median relative error {median:0.0000} should be under 2%");
            Assert.True(max < 0.60, $"max relative error {max:0.0000} should stay bounded");
        }

        [Fact]
        public void ReconstructionIsAccurateInsideSdrRangeToo()
        {
            HdrRawFrame frame = BuildRampFrame(256, 8, 6.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);
            (double median, _) = MeasureRoundTrip(hdr, sdr, map, abovePaperWhiteOnly: false);

            Assert.True(median < 0.02, $"in-range median relative error {median:0.0000} should be under 2%");
        }

        [Fact]
        public void CapacityDescribesContentPeakNotTheRatioExtreme()
        {
            // The distinction is not academic. A decoder scales how much of the map it applies by
            // capacity, so taking capacity from the largest ratio - which saturated pixels set, not
            // highlights - makes every display under-apply the map across the whole image.
            HdrRawFrame frame = BuildRampFrame(256, 8, 4.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            Assert.Equal(MathF.Log2(map.HdrPeak), map.HdrCapacityMax, 3);
            Assert.Equal(0f, map.HdrCapacityMin);
            Assert.True(map.HdrPeak > 3.5f, $"peak {map.HdrPeak} should reflect the 4x ramp");
        }

        [Fact]
        public void SdrOnlyContentNeedsNoHeadroom()
        {
            // Nothing above paper white means nothing to recover, and a decoder should be told so
            // rather than handed a capacity it will scale against.
            HdrRawFrame frame = BuildRampFrame(256, 8, 1.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            Assert.Equal(0f, map.HdrCapacityMax, 3);
        }

        [Fact]
        public void PercentileClampKeepsRangeBelowTheOutrightMaximum()
        {
            // One very saturated pixel against an otherwise tame frame. Its ratio must not be allowed
            // to set the quantisation range for every other pixel.
            HdrRawFrame frame = BuildRampFrame(256, 8, 2.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            // Poke one extreme value into the HDR layer only, leaving the SDR base as encoded. That is
            // the shape of the real problem: a pixel whose ratio sits far outside the bulk of the
            // distribution, which must not set the quantisation range for everything else.
            hdr.Rgb[0] = 12f;
            hdr.Rgb[1] = 0.02f;
            hdr.Rgb[2] = 0.02f;

            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            Assert.True(map.LogGainStored <= map.LogGainMax + 1e-4f,
                "stored range must not exceed the data");
            Assert.True(map.GainMapMax >= map.GainMapMin, "range must be ordered");
        }

        [Fact]
        public void DownsamplingCostsAccuracyWhichIsWhyItIsNotTheDefault()
        {
            HdrRawFrame frame = BuildRampFrame(256, 64, 5.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData full = HdrGainMap.Compute(hdr, sdr, divisor: 1);
            HdrGainMapData quarter = HdrGainMap.Compute(hdr, sdr, divisor: 4);

            Assert.Equal(hdr.Width, full.Width);
            Assert.Equal(hdr.Width / 4, quarter.Width);

            (double fullMedian, _) = MeasureRoundTrip(hdr, sdr, full, abovePaperWhiteOnly: true);
            (double quarterMedian, _) = MeasureRoundTrip(hdr, sdr, quarter, abovePaperWhiteOnly: true);

            Assert.True(fullMedian <= quarterMedian,
                $"full resolution ({fullMedian:0.0000}) should not be worse than quarter ({quarterMedian:0.0000})");
        }

        [Fact]
        public void MismatchedLayerSizesAreRejected()
        {
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(BuildRampFrame(64, 4, 2.0f));
            using Bitmap wrong = new Bitmap(32, 4, PixelFormat.Format32bppArgb);

            ArgumentException error = Assert.Throws<ArgumentException>(() => HdrGainMap.Compute(hdr, wrong));
            Assert.Contains("same grid", error.Message);
        }

        [Fact]
        public void InvalidDivisorIsRejected()
        {
            HdrRawFrame frame = BuildRampFrame(64, 4, 2.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            Assert.Throws<ArgumentOutOfRangeException>(() => HdrGainMap.Compute(hdr, sdr, divisor: 0));
        }

        [Fact]
        public void UltraHdrFileIsAValidJpegWithBothImagesIndexed()
        {
            HdrRawFrame frame = BuildRampFrame(128, 16, 4.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;
            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            byte[] file = UltraHdrJpegWriter.Build(sdr, map);

            Assert.Equal(0xFF, file[0]);
            Assert.Equal(0xD8, file[1]);

            JpegLayout layout = ParseLayout(file);

            Assert.NotNull(layout.PrimaryXmp);
            Assert.Contains("Container:Directory", layout.PrimaryXmp);
            Assert.Contains("Item:Semantic=\"Primary\"", layout.PrimaryXmp);
            Assert.Contains("Item:Semantic=\"GainMap\"", layout.PrimaryXmp);

            Assert.Equal(2, layout.ImageCount);

            // The offset has to land exactly on the appended image's SOI, and its declared size has to
            // reach exactly the end of the file. Either being off by a byte breaks the map silently.
            Assert.Equal(layout.PrimarySize, layout.GainMapAbsoluteOffset);
            Assert.Equal(0xFF, file[layout.GainMapAbsoluteOffset]);
            Assert.Equal(0xD8, file[layout.GainMapAbsoluteOffset + 1]);
            Assert.Equal(file.Length, layout.GainMapAbsoluteOffset + layout.GainMapSize);

            // XMP and MPF must agree, since decoders pick one or the other.
            Assert.Equal(layout.GainMapSize, layout.DeclaredGainMapLength);
        }

        [Fact]
        public void GainMapCarriesTheMetadataNeededToInvertIt()
        {
            HdrRawFrame frame = BuildRampFrame(128, 16, 4.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;
            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            byte[] file = UltraHdrJpegWriter.Build(sdr, map);
            JpegLayout layout = ParseLayout(file);

            byte[] gainMap = new byte[layout.GainMapSize];
            Buffer.BlockCopy(file, layout.GainMapAbsoluteOffset, gainMap, 0, layout.GainMapSize);

            JpegLayout inner = ParseLayout(gainMap);
            Assert.NotNull(inner.PrimaryXmp);

            foreach (string key in new[]
            {
                "hdrgm:Version", "hdrgm:GainMapMin", "hdrgm:GainMapMax", "hdrgm:Gamma",
                "hdrgm:OffsetSDR", "hdrgm:OffsetHDR", "hdrgm:HDRCapacityMin",
                "hdrgm:HDRCapacityMax", "hdrgm:BaseRenditionIsHDR"
            })
            {
                Assert.Contains(key, inner.PrimaryXmp);
            }

            // Written with invariant formatting: a comma decimal separator would be unparseable to
            // every decoder, and this machine's culture uses one.
            Assert.DoesNotContain(",", inner.PrimaryXmp);
        }

        [Fact]
        public void StrippingTheGainMapLeavesAnIntactSdrJpeg()
        {
            // This is the property the whole approach rests on: a platform that drops the appended
            // image still has a complete, correct picture.
            HdrRawFrame frame = BuildRampFrame(128, 16, 4.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;
            HdrGainMapData map = HdrGainMap.Compute(hdr, sdr);

            byte[] file = UltraHdrJpegWriter.Build(sdr, map);
            JpegLayout layout = ParseLayout(file);

            byte[] baseOnly = new byte[layout.GainMapAbsoluteOffset];
            Buffer.BlockCopy(file, 0, baseOnly, 0, baseOnly.Length);

            Assert.Equal(0xD9, baseOnly[^1]);
            Assert.Equal(0xFF, baseOnly[^2]);

            using System.IO.MemoryStream stream = new System.IO.MemoryStream(baseOnly);
            using Bitmap decoded = new Bitmap(stream);
            Assert.Equal(sdr.Width, decoded.Width);
            Assert.Equal(sdr.Height, decoded.Height);
        }

        [Fact]
        public void PaperWhiteOverrideReplacesTheProbedWhiteLevel()
        {
            float saved = Screenshot.PaperWhiteNitsOverride;
            try
            {
                Screenshot.PaperWhiteNitsOverride = 0f;
                Screenshot.PaperWhiteNitsOverride = 320f;

                // Applied at the probe, so everything downstream - decode normalisation, the curve's
                // nits conversion, recorded metadata - sees one consistent white level.
                Assert.Equal(320f, Screenshot.GetSdrWhiteLevelNits());
                Assert.Equal(320f, Screenshot.PaperWhiteNitsOverride);
            }
            finally
            {
                Screenshot.PaperWhiteNitsOverride = saved;
            }
        }

        [Fact]
        public void PaperWhiteOverrideOfZeroFallsBackToTheProbe()
        {
            float saved = Screenshot.PaperWhiteNitsOverride;
            try
            {
                Screenshot.PaperWhiteNitsOverride = 320f;
                Screenshot.PaperWhiteNitsOverride = 0f;

                Assert.Equal(0f, Screenshot.PaperWhiteNitsOverride);

                // Whatever the probe reports, it must be a usable white level rather than the
                // override leaking through as zero - a zero would invert the normalisation scale.
                Assert.True(Screenshot.GetSdrWhiteLevelNits() > 0f);
            }
            finally
            {
                Screenshot.PaperWhiteNitsOverride = saved;
            }
        }

        [Fact]
        public void PaperWhiteOverrideIsClampedToUsableValues()
        {
            // Not a Theory: the bounds are properties rather than consts, so they cannot appear in an
            // attribute argument.
            (float Requested, float Expected)[] cases =
            {
                (-50f, 0f),                                     // negative is meaningless, treat as auto
                (0f, 0f),                                       // zero means auto
                (10f, Screenshot.MinPaperWhiteNits),            // below scene-referred white inverts normalisation
                (5000f, Screenshot.MaxPaperWhiteNits),          // past any real panel
                (240f, 240f)
            };

            float saved = Screenshot.PaperWhiteNitsOverride;
            try
            {
                foreach ((float requested, float expected) in cases)
                {
                    Screenshot.PaperWhiteNitsOverride = requested;
                    Assert.Equal(expected, Screenshot.PaperWhiteNitsOverride);
                }
            }
            finally
            {
                Screenshot.PaperWhiteNitsOverride = saved;
            }
        }

        [Fact]
        public void OnlyTheThreeWorthwhileModesAreOffered()
        {
            Assert.Equal(
                new[] { HdrTonemapMode.Auto, HdrTonemapMode.Desktop, HdrTonemapMode.Filmic },
                HdrTonemap.SelectableModes);

            // Withdrawn from the UI but still reachable in code: the harness compares against them and
            // ClassifyContent can still return AutoHDR.
            Assert.Equal(HdrTonemapMode.Auto, HdrTonemap.CoerceSelectableMode(HdrTonemapMode.AutoHDR));
            Assert.Equal(HdrTonemapMode.Auto, HdrTonemap.CoerceSelectableMode(HdrTonemapMode.WindowsWIC));

            foreach (HdrTonemapMode mode in HdrTonemap.SelectableModes)
            {
                Assert.Equal(mode, HdrTonemap.CoerceSelectableMode(mode));
            }
        }

        [Fact]
        public void MasterDerivedMapAgreesWithTheLinearFrameMap()
        {
            // The capture path derives the gain map from the PQ master; the harness derives it from a
            // decoded linear frame. They must describe the same picture, or a file written live would
            // differ from everything that was measured offline.
            HdrRawFrame frame = BuildRampFrame(256, 8, 5.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(
                frame, HdrTonemapMode.Desktop, 1.0f, buildMaster: true);

            Assert.NotNull(result.Master);

            HdrGainMapData fromLinear = HdrGainMap.Compute(hdr, result.Sdr);
            HdrGainMapData fromMaster = HdrGainMap.ComputeFromMaster(
                result.Master, result.Sdr, result.SdrWhiteNits);

            Assert.Equal(fromLinear.Width, fromMaster.Width);
            Assert.Equal(fromLinear.Height, fromMaster.Height);

            // 16-bit PQ round-trips luminance far more finely than 8 bits of log gain can express, so
            // the stored codes should be near-identical rather than merely close.
            long diffSum = 0;
            int worst = 0;
            for (int i = 0; i < fromLinear.Gain.Length; i++)
            {
                int d = Math.Abs(fromLinear.Gain[i] - fromMaster.Gain[i]);
                diffSum += d;
                if (d > worst) worst = d;
            }

            double meanDiff = diffSum / (double)fromLinear.Gain.Length;
            Assert.True(meanDiff < 2.0, $"mean stored-code difference {meanDiff:0.000} should be under 2");
            Assert.True(worst <= 12, $"worst stored-code difference {worst} should stay small");

            Assert.Equal(fromLinear.HdrCapacityMax, fromMaster.HdrCapacityMax, 1);
        }

        [Fact]
        public void MasterPathRejectsAMismatchedBase()
        {
            HdrRawFrame frame = BuildRampFrame(128, 8, 3.0f);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(
                frame, HdrTonemapMode.Desktop, 1.0f, buildMaster: true);

            using Bitmap wrong = new Bitmap(64, 8, PixelFormat.Format32bppArgb);
            ArgumentException error = Assert.Throws<ArgumentException>(
                () => HdrGainMap.ComputeFromMaster(result.Master, wrong, result.SdrWhiteNits));
            Assert.Contains("same grid", error.Message);
        }

        [Fact]
        public void ModeDescriptionsResolveFromTheResourceCatalogue()
        {
            // GetLocalizedDescription looks up "<EnumType>_<Member>" and silently falls back to the
            // [Description] attribute when the key is absent - so a typo in a resx key produces
            // English text rather than an error. Asserting the value actually changes with the culture
            // is the only way to catch that.
            CultureInfo saved = CultureInfo.CurrentUICulture;
            try
            {
                foreach (HdrTonemapMode mode in HdrTonemap.SelectableModes)
                {
                    CultureInfo.CurrentUICulture = new CultureInfo("en");
                    string english = mode.GetLocalizedDescription();

                    CultureInfo.CurrentUICulture = new CultureInfo("ru");
                    string russian = mode.GetLocalizedDescription();

                    Assert.False(string.IsNullOrWhiteSpace(english), $"{mode} has no English text");
                    Assert.False(string.IsNullOrWhiteSpace(russian), $"{mode} has no Russian text");
                    Assert.NotEqual(english, russian);
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = saved;
            }
        }

        [Fact]
        public void CaptureModeDescriptionsResolveFromTheResourceCatalogue()
        {
            CultureInfo saved = CultureInfo.CurrentUICulture;
            try
            {
                foreach (HdrCaptureMode mode in Enum.GetValues<HdrCaptureMode>())
                {
                    CultureInfo.CurrentUICulture = new CultureInfo("en");
                    string english = mode.GetLocalizedDescription();

                    CultureInfo.CurrentUICulture = new CultureInfo("ja-JP");
                    string japanese = mode.GetLocalizedDescription();

                    Assert.False(string.IsNullOrWhiteSpace(english), $"{mode} has no English text");
                    Assert.NotEqual(english, japanese);
                }
            }
            finally
            {
                CultureInfo.CurrentUICulture = saved;
            }
        }

        [Fact]
        public unsafe void ContentCompositedOverTheHdrLayerDoesNotStretchTheRange()
        {
            // Reproduces what the mouse cursor does. ShareX draws the cursor into the SDR bitmap after
            // the PQ master has been built, so those pixels are bright over a master that reads black.
            // Their ratio is near zero, and letting it set the bottom of the stored range spends the
            // 8-bit budget on a span the picture never uses - measured at -5.64 stops against a real
            // spread of about 0.09 on an actual capture.
            HdrRawFrame frame = BuildRampFrame(256, 16, 3.0f);
            HdrLinearFrame hdr = HdrFrameTonemapper.DecodeToLinear(frame);
            using HdrTonemapResult result = HdrFrameTonemapper.TonemapFrame(frame, HdrTonemapMode.Desktop, 1.0f);
            Bitmap sdr = result.Sdr;

            HdrGainMapData before = HdrGainMap.Compute(hdr, sdr);

            // Paint a small bright block into the SDR base only, leaving the HDR layer untouched.
            BitmapData bd = sdr.LockBits(new Rectangle(0, 0, sdr.Width, sdr.Height),
                ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bd.Scan0;
                for (int y = 0; y < 4; y++)
                {
                    byte* row = scan0 + ((long)y * bd.Stride);
                    for (int x = 0; x < 4; x++)
                    {
                        row[(x * 4) + 0] = 230;
                        row[(x * 4) + 1] = 230;
                        row[(x * 4) + 2] = 230;
                    }
                }
            }
            finally
            {
                sdr.UnlockBits(bd);
            }

            HdrGainMapData after = HdrGainMap.Compute(hdr, sdr);

            // The outlier is present in the data...
            Assert.True(after.LogGainMin < before.GainMapMin - 1f,
                $"the painted block should be a real outlier (min {after.LogGainMin:0.00})");

            // ...but the stored low end must sit at the floor rather than follow it down.
            Assert.True(after.GainMapMin >= HdrGainMap.LowRangeFloor - 0.01f,
                $"low end {after.GainMapMin:0.00} should not go below the floor {HdrGainMap.LowRangeFloor:0.00}");
            Assert.True(after.GainMapMin > after.LogGainMin + 1f,
                $"low end {after.GainMapMin:0.00} should be clamped well above the outlier {after.LogGainMin:0.00}");

            // Without the floor this reached MaxLogRange, 6 stops.
            float afterSpan = after.GainMapMax - after.GainMapMin;
            Assert.True(afterSpan < 3f, $"stored span {afterSpan:0.00} stops is too wide");
        }

        // ---- helpers ----

        /// <summary>
        /// A horizontal ramp from black up to <paramref name="peak"/> times paper white, repeated down
        /// the frame with the channels offset so some pixels are saturated enough to exercise gamut
        /// fitting. Built as a real scRGB fp16 frame so the tests run the production decode, stats and
        /// curve rather than a reimplementation of them.
        /// </summary>
        private static HdrRawFrame BuildRampFrame(int width, int height, float peak)
        {
            const int bpp = 8;
            const float sdrWhiteNits = 240f;
            float scale = sdrWhiteNits / HdrPixelConvert.SceneReferredWhiteNits;
            byte[] pixels = new byte[width * height * bpp];

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    float t = width == 1 ? 0f : x / (float)(width - 1);
                    float level = t * peak;

                    WriteScRgb(pixels, ((y * width) + x) * bpp,
                        level * scale,
                        level * (0.35f + (0.65f * ((y % 4) / 3f))) * scale,
                        level * (0.15f + (0.85f * ((y % 3) / 2f))) * scale);
                }
            }

            return new HdrRawFrame(new HdrFrameMetadata
            {
                Width = width,
                Height = height,
                Stride = width * bpp,
                DxgiFormat = HdrPixelConvert.FormatR16G16B16A16Float,
                SdrWhiteNits = sdrWhiteNits,
                ColorSpace = HdrDisplayProbe.ColorSpaceScRgb,
                Scenario = HdrCorpusScenario.NativeHdr,
                Label = "gain-map-ramp",
                CapturedUtc = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc),
                PayloadBytes = pixels.Length,
                MonitorRect = new HdrFrameRect(0, 0, width, height),
                CaptureRect = new HdrFrameRect(0, 0, width, height),
                SourceRegion = new HdrFrameRect(0, 0, width, height)
            }, pixels);
        }

        private static void WriteScRgb(byte[] destination, int offset, float r, float g, float b)
        {
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)r)).CopyTo(destination, offset);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)g)).CopyTo(destination, offset + 2);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)b)).CopyTo(destination, offset + 4);
            BitConverter.GetBytes(BitConverter.HalfToUInt16Bits((Half)1f)).CopyTo(destination, offset + 6);
        }

        private static unsafe (double Median, double Max) MeasureRoundTrip(HdrLinearFrame hdr, Bitmap sdr,
            HdrGainMapData map, bool abovePaperWhiteOnly)
        {
            int divisor = hdr.Width / map.Width;
            if (divisor < 1) divisor = 1;

            List<double> errors = new List<double>();

            BitmapData bd = sdr.LockBits(new Rectangle(0, 0, sdr.Width, sdr.Height),
                ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte* scan0 = (byte*)bd.Scan0;
                for (int y = 0; y < hdr.Height; y++)
                {
                    byte* src = scan0 + ((long)y * bd.Stride);
                    for (int x = 0; x < hdr.Width; x++)
                    {
                        int i = ((y * hdr.Width) + x) * 3;
                        float hdrLum = (HdrLinearFrame.LumR * hdr.Rgb[i])
                            + (HdrLinearFrame.LumG * hdr.Rgb[i + 1])
                            + (HdrLinearFrame.LumB * hdr.Rgb[i + 2]);
                        if (hdrLum < 0f) hdrLum = 0f;

                        bool above = hdrLum > 1.0f;
                        if (above != abovePaperWhiteOnly) continue;

                        float sdrLum = (HdrLinearFrame.LumR * SrgbToLinear(src[(x * 4) + 2]))
                            + (HdrLinearFrame.LumG * SrgbToLinear(src[(x * 4) + 1]))
                            + (HdrLinearFrame.LumB * SrgbToLinear(src[(x * 4) + 0]));

                        int mx = Math.Min(x / divisor, map.Width - 1);
                        int my = Math.Min(y / divisor, map.Height - 1);
                        byte stored = map.Gain[(my * map.Width) + mx];

                        float reconstructed = HdrGainMap.Reconstruct(map, stored, sdrLum, float.PositiveInfinity);
                        errors.Add(Math.Abs(reconstructed - hdrLum) / Math.Max(hdrLum, 0.01f));
                    }
                }
            }
            finally
            {
                sdr.UnlockBits(bd);
            }

            if (errors.Count == 0) return (0, 0);

            errors.Sort();
            return (errors[errors.Count / 2], errors[^1]);
        }

        private static float SrgbToLinear(byte value)
        {
            float s = value / 255f;
            return s <= 0.04045f ? s / 12.92f : MathF.Pow((s + 0.055f) / 1.055f, 2.4f);
        }

        private sealed class JpegLayout
        {
            public string PrimaryXmp { get; set; }

            public int ImageCount { get; set; }

            public int PrimarySize { get; set; }

            public int GainMapSize { get; set; }

            public int GainMapAbsoluteOffset { get; set; }

            public int DeclaredGainMapLength { get; set; }
        }

        /// <summary>
        /// Minimal JPEG segment walk that pulls out the XMP packet and the MPF image index, so the
        /// tests assert on the bytes rather than trusting the writer's own arithmetic.
        /// </summary>
        private static JpegLayout ParseLayout(byte[] jpeg)
        {
            JpegLayout layout = new JpegLayout();
            int offset = 2;

            while (offset < jpeg.Length - 1 && jpeg[offset] == 0xFF)
            {
                byte marker = jpeg[offset + 1];
                if (marker == 0xDA) break;

                int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
                int payload = offset + 4;
                int payloadLength = length - 2;

                if (marker == 0xE1 && payloadLength > 29 &&
                    Encoding.ASCII.GetString(jpeg, payload, 28) == "http://ns.adobe.com/xap/1.0/")
                {
                    layout.PrimaryXmp = Encoding.UTF8.GetString(jpeg, payload + 29, payloadLength - 29);
                }
                else if (marker == 0xE2 && payloadLength > 4 &&
                    Encoding.ASCII.GetString(jpeg, payload, 3) == "MPF")
                {
                    ParseMpf(jpeg, payload + 4, layout);
                }

                offset += 2 + length;
            }

            if (layout.PrimaryXmp != null)
            {
                const string marker = "Item:Semantic=\"GainMap\"";
                int at = layout.PrimaryXmp.IndexOf(marker, StringComparison.Ordinal);
                if (at >= 0)
                {
                    const string lengthKey = "Item:Length=\"";
                    int lengthAt = layout.PrimaryXmp.IndexOf(lengthKey, at, StringComparison.Ordinal);
                    if (lengthAt >= 0)
                    {
                        int start = lengthAt + lengthKey.Length;
                        int end = layout.PrimaryXmp.IndexOf('"', start);
                        layout.DeclaredGainMapLength = int.Parse(layout.PrimaryXmp[start..end]);
                    }
                }
            }

            return layout;
        }

        private static void ParseMpf(byte[] jpeg, int tiffStart, JpegLayout layout)
        {
            bool little = jpeg[tiffStart] == (byte)'I';
            int ifdOffset = ReadInt32(jpeg, tiffStart + 4, little);
            int entryCount = ReadInt16(jpeg, tiffStart + ifdOffset, little);

            int entriesOffset = 0;
            for (int i = 0; i < entryCount; i++)
            {
                int entry = tiffStart + ifdOffset + 2 + (i * 12);
                int tag = ReadInt16(jpeg, entry, little);

                if (tag == 0xB001)
                {
                    layout.ImageCount = ReadInt32(jpeg, entry + 8, little);
                }
                else if (tag == 0xB002)
                {
                    entriesOffset = ReadInt32(jpeg, entry + 8, little);
                }
            }

            if (entriesOffset == 0 || layout.ImageCount < 2) return;

            int first = tiffStart + entriesOffset;
            layout.PrimarySize = ReadInt32(jpeg, first + 4, little);

            int second = first + 16;
            layout.GainMapSize = ReadInt32(jpeg, second + 4, little);
            layout.GainMapAbsoluteOffset = tiffStart + ReadInt32(jpeg, second + 8, little);
        }

        private static int ReadInt16(byte[] data, int offset, bool little)
        {
            return little
                ? data[offset] | (data[offset + 1] << 8)
                : (data[offset] << 8) | data[offset + 1];
        }

        private static int ReadInt32(byte[] data, int offset, bool little)
        {
            return little
                ? data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24)
                : (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
        }
    }
}
