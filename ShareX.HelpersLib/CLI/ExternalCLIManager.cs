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
using System.Diagnostics;
using System.IO;
using System.Text;

namespace ShareX.HelpersLib
{
    public abstract class ExternalCLIManager : IDisposable
    {
        public event DataReceivedEventHandler OutputDataReceived;
        public event DataReceivedEventHandler ErrorDataReceived;

        public bool IsProcessRunning { get; private set; }

        protected Process process;

        public virtual int Open(string path, string args = null)
        {
            if (!StartProcess(path, args))
            {
                return -1;
            }

            try
            {
                IsProcessRunning = true;
                process.WaitForExit();
                return process.ExitCode;
            }
            finally
            {
                IsProcessRunning = false;
            }
        }

        public bool StartProcess(string path, string args = null)
        {
            if (!File.Exists(path))
            {
                return false;
            }

            process?.Dispose();
            process = new Process();

            ProcessStartInfo psi = new ProcessStartInfo()
            {
                FileName = path,
                WorkingDirectory = Path.GetDirectoryName(path),
                Arguments = args,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            process.EnableRaisingEvents = true;
            if (psi.RedirectStandardOutput) process.OutputDataReceived += cli_OutputDataReceived;
            if (psi.RedirectStandardError) process.ErrorDataReceived += cli_ErrorDataReceived;
            process.StartInfo = psi;

            DebugHelper.WriteLine($"CLI: \"{psi.FileName}\" {psi.Arguments}");
            process.Start();

            if (psi.RedirectStandardOutput) process.BeginOutputReadLine();
            if (psi.RedirectStandardError) process.BeginErrorReadLine();

            IsProcessRunning = true;
            return true;
        }

        public Stream GetStandardInputStream()
        {
            return process?.StandardInput?.BaseStream;
        }

        public int FinishProcess()
        {
            if (process == null || !IsProcessRunning)
            {
                return -1;
            }

            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }

            process.WaitForExit();
            IsProcessRunning = false;
            return process.ExitCode;
        }

        private void cli_OutputDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                OutputDataReceived?.Invoke(sender, e);
            }
        }

        private void cli_ErrorDataReceived(object sender, DataReceivedEventArgs e)
        {
            if (e.Data != null)
            {
                ErrorDataReceived?.Invoke(sender, e);
            }
        }

        public void WriteInput(string input)
        {
            if (IsProcessRunning && process != null && process.StartInfo != null && process.StartInfo.RedirectStandardInput)
            {
                process.StandardInput.WriteLine(input);
            }
        }

        public virtual void Close()
        {
            if (IsProcessRunning && process != null)
            {
                try
                {
                    process.StandardInput.Close();
                }
                catch
                {
                }
            }
        }

        public void Dispose()
        {
            if (process != null)
            {
                process.Dispose();
            }
        }
    }
}