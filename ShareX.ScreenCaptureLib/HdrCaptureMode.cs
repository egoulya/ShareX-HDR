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

using Newtonsoft.Json;
using ShareX.HelpersLib;
using System;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using Vortice.DXGI;

namespace ShareX.ScreenCaptureLib
{
    public enum HdrCaptureMode
    {
        [Description("Off")]
        Off = 0,
        [Description("Dynamic")]
        Dynamic = 1,
        [Description("On")]
        On = 2
    }

    public static class HdrCaptureModeExtensions
    {
        public static bool MayUseHdrPipeline(this HdrCaptureMode mode) =>
            mode == HdrCaptureMode.On || mode == HdrCaptureMode.Dynamic;
    }

    public sealed class HdrCaptureModeConverter : JsonConverter
    {
        public override bool CanConvert(Type objectType) => objectType == typeof(HdrCaptureMode);

        public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Boolean)
            {
                return (bool)reader.Value ? HdrCaptureMode.Dynamic : HdrCaptureMode.Off;
            }

            if (reader.TokenType == JsonToken.Integer)
            {
                long n = Convert.ToInt64(reader.Value, CultureInfo.InvariantCulture);
                return n switch
                {
                    2 => HdrCaptureMode.On,
                    1 => HdrCaptureMode.Dynamic,
                    _ => HdrCaptureMode.Off
                };
            }

            if (reader.TokenType == JsonToken.String)
            {
                string s = (string)reader.Value;
                if (Enum.TryParse(s, true, out HdrCaptureMode parsed))
                {
                    return parsed;
                }

                if (bool.TryParse(s, out bool b))
                {
                    return b ? HdrCaptureMode.Dynamic : HdrCaptureMode.Off;
                }
            }

            if (reader.TokenType == JsonToken.Null)
            {
                return HdrCaptureMode.Off;
            }

            throw new JsonSerializationException($"Cannot convert {reader.TokenType} to HdrCaptureMode.");
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
        {
            writer.WriteValue(((HdrCaptureMode)value).ToString());
        }
    }

    public static class HdrDisplayProbe
    {
        public const uint ColorSpaceSdr = 0;
        public const uint ColorSpaceHdr10 = 12;
        public const uint ColorSpaceScRgb = 16;

        public static bool IsHdrColorSpace(uint colorSpace) =>
            colorSpace == ColorSpaceHdr10 || colorSpace == ColorSpaceScRgb;

        public static bool HasHdrOutput(Rectangle captureRect)
        {
            if (captureRect.Width <= 0 || captureRect.Height <= 0)
            {
                captureRect = CaptureHelpers.GetScreenBounds();
            }

            try
            {
                using IDXGIFactory1 factory = Vortice.DXGI.DXGI.CreateDXGIFactory1<IDXGIFactory1>();

                for (uint adapterIndex = 0; ; adapterIndex++)
                {
                    if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1 adapter).Failure)
                    {
                        break;
                    }

                    using (adapter)
                    {
                        for (uint outputIndex = 0; ; outputIndex++)
                        {
                            if (adapter.EnumOutputs(outputIndex, out IDXGIOutput output).Failure)
                            {
                                break;
                            }

                            using (output)
                            {
                                try
                                {
                                    using IDXGIOutput6 output6 = output.QueryInterface<IDXGIOutput6>();
                                    OutputDescription1 description = output6.Description1;
                                    Rectangle outputBounds = Rectangle.FromLTRB(
                                        description.DesktopCoordinates.Left,
                                        description.DesktopCoordinates.Top,
                                        description.DesktopCoordinates.Right,
                                        description.DesktopCoordinates.Bottom);
                                    uint colorSpace = (uint)description.ColorSpace;
                                    bool hdr = description.AttachedToDesktop &&
                                        captureRect.IntersectsWith(outputBounds) &&
                                        IsHdrColorSpace(colorSpace);

                                    DebugHelper.WriteLine($"HDR: probe output {description.DeviceName} colorSpace={colorSpace} hdr={hdr}");

                                    if (hdr)
                                    {
                                        return true;
                                    }
                                }
                                catch (Exception)
                                {
                                    // Unsupported or unavailable output metadata is treated as SDR.
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception)
            {
                // DXGI is unavailable; no output can be identified as HDR.
            }

            return false;
        }

        public static bool ResolveHdrPipeline(HdrCaptureMode mode, Rectangle captureRect)
        {
            return mode switch
            {
                HdrCaptureMode.On => true,
                HdrCaptureMode.Dynamic => HasHdrOutput(captureRect),
                _ => false
            };
        }
    }
}
