using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LightTranslate
{
    internal sealed class SelectionUnavailableException : Exception
    {
        internal SelectionUnavailableException(string message = "剪贴板无法安全备份，已保留原内容；当前应用暂时无法读取选区。") : base(message) { }
    }
    internal static class IsolatedSelectionReader
    {
        internal static async Task<string> Read(IntPtr window, CancellationToken ct, int maximum = 65537, IntPtr focus = default(IntPtr))
        {
            ct.ThrowIfCancellationRequested();
            if (focus == IntPtr.Zero)
            {
                uint pid;
                uint thread = Native.GetWindowThreadProcessId(window, out pid);
                var info = new Native.GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                if (!Native.GetGUIThreadInfo(thread, ref info)) return null;
                focus = info.focus;
            }
            string path = Path.Combine(Program.Folder, "LightTranslate.SelectionHost.exe");
            if (focus == IntPtr.Zero || !File.Exists(path)) return null;
            var start = new ProcessStartInfo(path, window.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + focus.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + maximum.ToString(CultureInfo.InvariantCulture))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8 };
            try
            {
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    // The helper self-exits on timeout; never terminate the user's application.
                    bool exited = await Task.Run(delegate { return process.WaitForExit(2200); });
                    ct.ThrowIfCancellationRequested();
                    if (!exited || process.ExitCode != 0) return null;
                    string text = await output;
                    return text.Length <= maximum ? text : null;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }
    }
}
