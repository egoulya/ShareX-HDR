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

using System;
using System.Collections.Generic;
using System.Windows.Forms;

namespace ShareX.HelpersLib
{
    public class RecordingFPSPicker
    {
        public const string CustomPresetLabel = "Custom...";

        private static readonly int[] BasePresets = { 30, 60, 120, 240 };

        private readonly ComboBox presetComboBox;
        private readonly NumericUpDown customNumericUpDown;
        private readonly bool gifMode;
        private bool loading;

        public RecordingFPSPicker(ComboBox presetComboBox, NumericUpDown customNumericUpDown, bool gifMode = false)
        {
            this.presetComboBox = presetComboBox;
            this.customNumericUpDown = customNumericUpDown;
            this.gifMode = gifMode;

            presetComboBox.DropDownStyle = ComboBoxStyle.DropDownList;

            presetComboBox.SelectedIndexChanged += PresetComboBox_SelectedIndexChanged;
            customNumericUpDown.ValueChanged += CustomNumericUpDown_ValueChanged;

            RefreshPresets();
        }

        public event EventHandler FPSChanged;

        public int MaxFps => gifMode ? CaptureHelpers.GetMaximumGIFFPS() : CaptureHelpers.GetMaximumMonitorRefreshRate();

        public static int[] GetPresetValues(int maxFps)
        {
            List<int> presets = new List<int>();

            foreach (int preset in BasePresets)
            {
                if (preset <= maxFps)
                {
                    presets.Add(preset);
                }
            }

            if (maxFps >= 1 && !presets.Contains(maxFps))
            {
                presets.Add(maxFps);
            }

            presets.Sort();
            return presets.ToArray();
        }

        public void RefreshPresets()
        {
            loading = true;

            int currentFps = GetSelectedFPS();
            int maxFps = MaxFps;

            customNumericUpDown.Minimum = 1;
            customNumericUpDown.Maximum = maxFps;

            presetComboBox.Items.Clear();

            foreach (int preset in GetPresetValues(maxFps))
            {
                presetComboBox.Items.Add(preset.ToString());
            }

            presetComboBox.Items.Add(CustomPresetLabel);

            ApplyFps(currentFps);

            loading = false;
        }

        public void Load(int fps)
        {
            loading = true;
            ApplyFps(CaptureHelpers.ClampRecordingFPSForMode(fps, gifMode));
            loading = false;
        }

        public int GetSelectedFPS()
        {
            if (IsCustomSelected())
            {
                return CaptureHelpers.ClampRecordingFPSForMode((int)customNumericUpDown.Value, gifMode);
            }

            if (presetComboBox.SelectedItem is string presetText && int.TryParse(presetText, out int fps))
            {
                return fps;
            }

            return CaptureHelpers.ClampRecordingFPSForMode((int)customNumericUpDown.Value, gifMode);
        }

        private void ApplyFps(int fps)
        {
            int maxFps = MaxFps;
            fps = Math.Clamp(fps, 1, maxFps);
            string presetText = fps.ToString();

            if (presetComboBox.Items.Contains(presetText))
            {
                presetComboBox.SelectedItem = presetText;
                customNumericUpDown.Visible = false;
            }
            else
            {
                presetComboBox.SelectedItem = CustomPresetLabel;
                customNumericUpDown.SetValue(fps);
                customNumericUpDown.Visible = true;
            }
        }

        private bool IsCustomSelected()
        {
            return (presetComboBox.SelectedItem as string) == CustomPresetLabel;
        }

        private void PresetComboBox_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (loading)
            {
                return;
            }

            customNumericUpDown.Visible = IsCustomSelected();
            OnFPSChanged();
        }

        private void CustomNumericUpDown_ValueChanged(object sender, EventArgs e)
        {
            if (loading || !IsCustomSelected())
            {
                return;
            }

            int fps = CaptureHelpers.ClampRecordingFPSForMode((int)customNumericUpDown.Value, gifMode);

            if (customNumericUpDown.Value != fps)
            {
                loading = true;
                customNumericUpDown.SetValue(fps);
                loading = false;
            }

            OnFPSChanged();
        }

        private void OnFPSChanged()
        {
            FPSChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
