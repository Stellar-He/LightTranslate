using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

// A short-lived process owns every UIA/COM object. No clipboard API, file output or network.
internal static class SelectionHost
{
    private static Timer deadline;
    [MTAThread]
    private static int Main(string[] args)
    {
        // Keep the deadline alive through COM finalization; it may only end this helper.
        deadline = new Timer(delegate { TerminateProcess(new IntPtr(-1), 3); }, null, 1500, Timeout.Infinite);
        try
        {
            long rootValue, focusValue; int maximum;
            if (args.Length != 3 || !long.TryParse(args[0], out rootValue) || !long.TryParse(args[1], out focusValue)
                || !int.TryParse(args[2], out maximum) || maximum < 1 || maximum > 65537) return 2;
            IntPtr root = new IntPtr(rootValue), focus = new IntPtr(focusValue);
            if (root == IntPtr.Zero || focus == IntPtr.Zero || GetAncestor(focus, 2) != root) return 2;
            if (GetForegroundWindow() == root)
            {
                var current = AutomationElement.FocusedElement;
                if (current != null && current.Current.IsPassword) return 0;
            }
            var element = AutomationElement.FromHandle(focus);
            if (element == null || element.Current.IsPassword) return 0;
            string text = Read(element, maximum);
            // Browsers can expose their actual focused document below the native HWND.
            if (string.IsNullOrEmpty(text) && GetForegroundWindow() == root)
            {
                var focused = AutomationElement.FocusedElement;
                if (focused != null && !focused.Current.IsPassword && BelongsTo(focused, root))
                {
                    for (int i = 0; i < 8 && focused != null; i++)
                    {
                        if (focused.Current.IsPassword || !BelongsTo(focused, root)) break;
                        text = Read(focused, maximum);
                        if (!string.IsNullOrEmpty(text)) break;
                        focused = TreeWalker.ControlViewWalker.GetParent(focused);
                    }
                }
            }
            if (!string.IsNullOrEmpty(text))
            {
                Console.OutputEncoding = new UTF8Encoding(false);
                Console.Write(text);
            }
            return 0;
        }
        catch { return 2; }
    }
    private static bool BelongsTo(AutomationElement element, IntPtr root)
    {
        for (int i = 0; i < 32 && element != null; i++)
        {
            int handle = element.Current.NativeWindowHandle;
            if (handle != 0) return GetAncestor(new IntPtr(handle), 2) == root;
            element = TreeWalker.ControlViewWalker.GetParent(element);
        }
        return false;
    }
    private static string Read(AutomationElement element, int maximum)
    {
        if (element.Current.IsPassword) return null;
        object value;
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out value)) return null;
        var ranges = ((TextPattern)value).GetSelection();
        if (ranges == null || ranges.Length == 0 || ranges.Length > 8) return null;
        var text = new StringBuilder();
        foreach (var range in ranges)
        {
            if (text.Length != 0) text.Append('\n');
            if (text.Length >= maximum) break;
            text.Append(range.GetText(maximum - text.Length));
        }
        return text.ToString();
    }
    [DllImport("kernel32.dll")] private static extern bool TerminateProcess(IntPtr process, uint code);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
