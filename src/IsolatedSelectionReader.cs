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
        internal static async Task<string> Read(IntPtr window, CancellationToken ct, int maximum = 65537, IntPtr focus = default(IntPtr), Native.POINT? point = null)
        {
            ct.ThrowIfCancellationRequested();
            if (focus == IntPtr.Zero)
            {
                uint pid;
                uint thread = Native.GetWindowThreadProcessId(window, out pid);
                var info = new Native.GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(Native.GUITHREADINFO)) };
                // Missing native focus does not mean the foreground document has no UIA selection.
                focus = Native.GetGUIThreadInfo(thread, ref info) && info.focus != IntPtr.Zero ? info.focus : window;
            }
            string path = Path.Combine(Program.Folder, "LightTranslate.SelectionHost.exe");
            if (focus == IntPtr.Zero || !File.Exists(path)) { Program.Log("selection-helper", "missing-helper-or-window"); return null; }
            string arguments = window.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + focus.ToInt64().ToString(CultureInfo.InvariantCulture) + " " + maximum.ToString(CultureInfo.InvariantCulture);
            if (point.HasValue) arguments += " " + point.Value.x.ToString(CultureInfo.InvariantCulture) + " " + point.Value.y.ToString(CultureInfo.InvariantCulture);
            var start = new ProcessStartInfo(path, arguments)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardOutputEncoding = Encoding.UTF8 };
            try
            {
                using (var process = Process.Start(start))
                {
                    var output = process.StandardOutput.ReadToEndAsync();
                    var diagnostic = process.StandardError.ReadToEndAsync();
                    // The helper self-exits on timeout; never terminate the user's application.
                    bool exited = await Task.Run(delegate { return process.WaitForExit(2200); });
                    ct.ThrowIfCancellationRequested();
                    if (!exited) { Program.Log("selection-helper", "wait-timeout"); return null; }
                    string status = (await diagnostic).Trim();
                    // Only fixed status codes enter logs, never provider text or selected content.
                    if (status != "no-text-pattern" && status != "empty-selection" && status != "password"
                        && status != "wrong-root" && status != "deadline" && status != "provider-error" && status != "depth-limit") status = "unknown";
                    if (process.ExitCode != 0) { Program.Log("selection-helper", "exit=" + process.ExitCode + " status=" + status); return null; }
                    string text = await output;
                    if (string.IsNullOrEmpty(text)) Program.Log("selection-helper", "status=" + status);
                    return text.Length <= maximum ? text : null;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { Program.Log("selection-helper", "launch-or-pipe-error"); return null; }
        }
    }
}
