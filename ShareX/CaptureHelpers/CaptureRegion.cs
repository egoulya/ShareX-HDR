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
using ShareX.ScreenCaptureLib;
using ShareX.ScreenCaptureLib.Presentation.RegionCapture;
using SkiaSharp;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ShareX
{
    public class CaptureRegion : CaptureBase
    {
        protected static RegionCaptureType lastRegionCaptureType = RegionCaptureType.Default;

        public RegionCaptureType RegionCaptureType { get; protected set; }

        public CaptureRegion()
        {
        }

        public CaptureRegion(RegionCaptureType regionCaptureType)
        {
            RegionCaptureType = regionCaptureType;
        }

        protected override TaskMetadata Execute(TaskSettings taskSettings)
        {
            switch (RegionCaptureType)
            {
                default:
                case RegionCaptureType.Default:
                    return ExecuteRegionCapture(taskSettings);
                case RegionCaptureType.Light:
                    return ExecuteRegionCaptureLight(taskSettings);
                case RegionCaptureType.Transparent:
                    return ExecuteRegionCaptureTransparent(taskSettings);
            }
        }

        protected override async Task<TaskMetadata> ExecuteAsync(TaskSettings taskSettings)
        {
            if (GetType() == typeof(CaptureRegion) && RegionCaptureType == RegionCaptureType.Default)
            {
                return await ExecuteRegionCaptureAvaloniaAsync(taskSettings);
            }

            return Execute(taskSettings);
        }

        protected async Task<TaskMetadata> ExecuteRegionCaptureAvaloniaAsync(TaskSettings taskSettings)
        {
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
            screenshot.CaptureCursor = false;

            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;
            Rectangle screenBounds = activeMonitorMode
                ? CaptureHelpers.GetActiveScreenBounds()
                : CaptureHelpers.GetScreenBounds();

            SKBitmap frozenScreenshot;
            HdrMasterImage canvasMaster = null;
            using (Bitmap canvas = activeMonitorMode
                ? screenshot.CaptureActiveMonitor()
                : screenshot.CaptureFullscreen())
            {
                canvasMaster = screenshot.TakeLastHdrMaster();
                frozenScreenshot = GdiSkiaBitmapConverter.ToSKBitmap(canvas);
            }

            SKBitmap cursorBitmap = null;
            Point cursorPosition = Point.Empty;

            if (taskSettings.CaptureSettings.ShowCursor)
            {
                CursorData cursorData = new CursorData();
                if (cursorData.IsVisible)
                {
                    using Bitmap cursor = cursorData.ToBitmap();
                    cursorBitmap = GdiSkiaBitmapConverter.ToSKBitmap(cursor);
                    cursorPosition = new Point(
                        cursorData.DrawPosition.X - screenBounds.X,
                        cursorData.DrawPosition.Y - screenBounds.Y);
                }
            }

            AvaloniaRegionCaptureRequest request = new AvaloniaRegionCaptureRequest
            {
                Screenshot = frozenScreenshot,
                ScreenBounds = screenBounds,
                CaptureOptions = taskSettings.CaptureSettingsReference.SurfaceOptions,
                EditorOptions = taskSettings.ToolsSettingsReference.ImageEditorOptions,
                EnableAnnotations = !taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation,
                CursorBitmap = cursorBitmap,
                CursorPosition = cursorPosition
            };

            AvaloniaRegionCaptureResult result = await RegionCaptureIntegration.CaptureAsync(request);
            if (result == null)
            {
                return null;
            }

            using (result.Image)
            {
                Bitmap output = GdiSkiaBitmapConverter.ToGdiBitmap(result.Image);
                TaskMetadata metadata = new TaskMetadata(output);

                metadata.HdrMaster = CropMaster(canvasMaster, result.ScreenRectangle, screenBounds, output);

                if (result.ImageModified)
                {
                    AllowAnnotation = false;
                }

                if (result.WindowInfo != null)
                {
                    metadata.UpdateInfo(result.WindowInfo);
                }

                lastRegionCaptureType = RegionCaptureType.Default;
                return metadata;
            }
        }

        protected TaskMetadata ExecuteRegionCapture(TaskSettings taskSettings)
        {
            RegionCaptureMode mode;

            if (taskSettings.AdvancedSettings.RegionCaptureDisableAnnotation)
            {
                mode = RegionCaptureMode.Default;
            }
            else
            {
                mode = RegionCaptureMode.Annotation;
            }

            Bitmap canvas;
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
            screenshot.CaptureCursor = false;

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = screenshot.CaptureActiveMonitor();
            }
            else
            {
                canvas = screenshot.CaptureFullscreen();
            }

            HdrMasterImage canvasMaster = screenshot.TakeLastHdrMaster();

            CursorData cursorData = null;

            if (taskSettings.CaptureSettings.ShowCursor)
            {
                cursorData = new CursorData();
            }

            using (RegionCaptureForm form = new RegionCaptureForm(mode, taskSettings.CaptureSettingsReference.SurfaceOptions, canvas))
            {
                if (cursorData != null && cursorData.IsVisible)
                {
                    form.AddCursor(cursorData.ToBitmap(), form.PointToClient(cursorData.DrawPosition));
                }

                form.ShowDialog();

                Bitmap result = form.GetResultImage();

                if (result != null)
                {
                    TaskMetadata metadata = new TaskMetadata(result);

                    Rectangle region = form.GetSelectedRectangle();
                    if (region.Width <= 0 || region.Height <= 0)
                    {
                        region = RegionCaptureIntegration.LastRegionRectangle;
                    }

                    Rectangle canvasBounds = form.ScreenBounds.IsEmpty
                        ? (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode
                            ? CaptureHelpers.GetActiveScreenBounds()
                            : CaptureHelpers.GetScreenBounds())
                        : form.ScreenBounds;
                    metadata.HdrMaster = CropMaster(canvasMaster, region, canvasBounds, result);

                    if (form.IsImageModified)
                    {
                        AllowAnnotation = false;
                    }

                    if (form.Result == RegionResult.Region)
                    {
                        WindowInfo windowInfo = form.GetWindowInfo();
                        metadata.UpdateInfo(windowInfo);
                    }

                    lastRegionCaptureType = RegionCaptureType.Default;

                    return metadata;
                }
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureLight(TaskSettings taskSettings)
        {
            Bitmap canvas;
            Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);

            if (taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode)
            {
                canvas = screenshot.CaptureActiveMonitor();
            }
            else
            {
                canvas = screenshot.CaptureFullscreen();
            }

            HdrMasterImage canvasMaster = screenshot.TakeLastHdrMaster();

            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            using (RegionCaptureLightForm rectangleLight = new RegionCaptureLightForm(canvas, activeMonitorMode))
            {
                if (rectangleLight.ShowDialog() == DialogResult.OK)
                {
                    Bitmap result = rectangleLight.GetAreaImage();

                    if (result != null)
                    {
                        lastRegionCaptureType = RegionCaptureType.Light;

                        TaskMetadata metadata = new TaskMetadata(result);
                        Rectangle canvasBounds = activeMonitorMode
                            ? CaptureHelpers.GetActiveScreenBounds()
                            : CaptureHelpers.GetScreenBounds();
                        metadata.HdrMaster = CropMaster(canvasMaster, rectangleLight.ScreenSelectionRectangle, canvasBounds, result);

                        return metadata;
                    }
                }
            }

            return null;
        }

        protected TaskMetadata ExecuteRegionCaptureTransparent(TaskSettings taskSettings)
        {
            bool activeMonitorMode = taskSettings.CaptureSettings.SurfaceOptions.ActiveMonitorMode;

            using (RegionCaptureLightForm rectangleTransparent = new RegionCaptureLightForm(null, activeMonitorMode))
            {
                if (rectangleTransparent.ShowDialog() == DialogResult.OK)
                {
                    Screenshot screenshot = TaskHelpers.GetScreenshot(taskSettings);
                    Bitmap result = rectangleTransparent.GetAreaImage(screenshot);

                    if (result != null)
                    {
                        lastRegionCaptureType = RegionCaptureType.Transparent;

                        TaskMetadata metadata = new TaskMetadata(result)
                        {
                            HdrMaster = screenshot.TakeLastHdrMaster()
                        };
                        return metadata;
                    }
                }
            }

            return null;
        }

        private static HdrMasterImage CropMaster(HdrMasterImage canvasMaster, Rectangle region, Rectangle canvasBounds, Bitmap output)
        {
            if (canvasMaster == null)
            {
                return null;
            }

            if (region.Width > 0 && region.Height > 0)
            {
                HdrMasterImage cropped = canvasMaster.Crop(region, canvasBounds);
                if (cropped != null)
                {
                    return cropped;
                }
            }

            if (output != null && canvasMaster.Width == output.Width && canvasMaster.Height == output.Height)
            {
                DebugHelper.WriteLine("HDR master: using uncropped companion because it already matches the result size.");
                return canvasMaster;
            }

            DebugHelper.WriteLine("HDR master: region crop produced no companion.");
            return null;
        }
    }
}
