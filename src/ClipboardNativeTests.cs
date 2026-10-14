using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    // Opt-in native tests run in a new window station, which owns a separate clipboard.
    // Isolation failure aborts before opening any clipboard. Never save/restore the user's data.
    internal static class ClipboardNativeTests
    {
        private static void Check(bool ok, string message) { ClipboardCaptureTests.Check(ok, message); }
        private static void Put(IntPtr owner, params KeyValuePair<uint, byte[]>[] formats)
        {
            Check(ClipboardNative.OpenClipboard(owner), "fixture open");
            try
            {
                Check(ClipboardNative.EmptyClipboard(), "fixture empty");
                foreach (var item in formats)
                {
                    var handle = NativeSelectionClipboard.Allocate(item.Value);
                    Check(handle != IntPtr.Zero, "fixture allocation");
                    if (ClipboardNative.SetClipboardData(item.Key, handle) == IntPtr.Zero)
                    { ClipboardNative.GlobalFree(handle); throw new Exception("Fixture set failed"); }
                }
            }
            finally { ClipboardNative.CloseClipboard(); }
        }
        private static KeyValuePair<uint, byte[]> Text(string value)
        { return new KeyValuePair<uint, byte[]>(13, Encoding.Unicode.GetBytes(value + "\0")); }

        private sealed class DelayedWindow : Forms.NativeWindow
        {
            internal int Renders;
            internal DelayedWindow() { CreateHandle(new Forms.CreateParams { Parent = new IntPtr(-3) }); }
            protected override void WndProc(ref Forms.Message message)
            {
                if (message.Msg == 0x305)
                {
                    Renders++;
                    var handle = NativeSelectionClipboard.Allocate(Text("delayed").Value);
                    if (ClipboardNative.SetClipboardData(13, handle) == IntPtr.Zero) ClipboardNative.GlobalFree(handle);
                    return;
                }
                base.WndProc(ref message);
            }
        }

        internal static int Run()
        {
            IntPtr previousStation = GetProcessWindowStation();
            IntPtr previousDesktop = GetThreadDesktop(Native.GetCurrentThreadId());
            // A named station requires administrator membership. A standard user can
            // request an automatically named station only if it does not already exist.
            IntPtr station = CreateWindowStation("LightTranslate-test-" + Guid.NewGuid().ToString("N"), 1, 0x37f, IntPtr.Zero);
            if (station == IntPtr.Zero && Marshal.GetLastWin32Error() == 5)
                station = CreateWindowStation(null, 1, 0x37f, IntPtr.Zero);
            Check(station != IntPtr.Zero, "create isolated window station: " + Marshal.GetLastWin32Error());
            IntPtr desktop = IntPtr.Zero;
            try
            {
                Check(SetProcessWindowStation(station), "select isolated window station");
                desktop = CreateDesktop("test", IntPtr.Zero, IntPtr.Zero, 0, 0x1ff, IntPtr.Zero);
                Check(desktop != IntPtr.Zero && SetThreadDesktop(desktop), "select isolated desktop");
                using (var clipboard = new NativeSelectionClipboard())
                {
                    uint html = ClipboardNative.RegisterClipboardFormat("HTML Format");
                    uint rtf = ClipboardNative.RegisterClipboardFormat("Rich Text Format");
                    uint unknown = ClipboardNative.RegisterClipboardFormat("LightTranslate unsupported fixture");
                    Put(clipboard.Handle, Text("original"), new KeyValuePair<uint, byte[]>(html, Encoding.UTF8.GetBytes("<b>original</b>\0")),
                        new KeyValuePair<uint, byte[]>(rtf, Encoding.ASCII.GetBytes("{\\rtf1 original}\0")));
                    ClipboardSnapshot original;
                    Check(clipboard.TrySnapshot(out original), "text/HTML/RTF snapshot");
                    Put(clipboard.Handle, Text("selected"));
                    uint version; string selected;
                    Check(clipboard.TryReadText(clipboard.Handle, clipboard.Sequence, out version, out selected) && selected == "selected", "native Unicode read");
                    Check(clipboard.Restore(original, version, delegate { return true; }) == RestoreResult.Restored, "native restore");
                    ClipboardSnapshot restored;
                    Check(clipboard.TrySnapshot(out restored), "restored snapshot");
                    Check(original.Formats.Count == restored.Formats.Count && original.Formats.All(x => restored.Formats.Any(y => x.Key == y.Key && x.Value.SequenceEqual(y.Value))), "all format bytes preserved");
                    Put(clipboard.Handle, Text("new user copy"));
                    Check(clipboard.Restore(original, version, delegate { return true; }) == RestoreResult.Changed, "stale restore rejected");
                    Check(clipboard.Restore(original, clipboard.Sequence, delegate { return false; }) == RestoreResult.Changed, "canceled restore rejected");
                    Put(clipboard.Handle, Text("keep"), new KeyValuePair<uint, byte[]>(unknown, new byte[] { 1, 2, 3 }));
                    uint before = clipboard.Sequence;
                    Check(!clipboard.TrySnapshot(out restored) && clipboard.Sequence == before, "unknown mixed format skips without mutation");
                    Put(clipboard.Handle);
                    Check(clipboard.TrySnapshot(out original) && original.Formats.Count == 0, "empty clipboard snapshot");
                    Put(clipboard.Handle, Text("temporary"));
                    Check(clipboard.Restore(original, clipboard.Sequence, delegate { return true; }) == RestoreResult.Restored, "empty restore");
                    Check(clipboard.TrySnapshot(out restored) && restored.Formats.Count == 0, "empty preserved");
                    Put(clipboard.Handle, new KeyValuePair<uint, byte[]>(13, new byte[NativeSelectionClipboard.MaxFormatBytes + 2]));
                    Check(!clipboard.TrySnapshot(out restored), "oversize snapshot rejected");
                    Put(clipboard.Handle, Text("busy"));
                    using (var opened = new ManualResetEvent(false))
                    using (var release = new ManualResetEvent(false))
                    {
                        bool locked = false;
                        var thread = new Thread(delegate()
                        {
                            SetThreadDesktop(desktop);
                            locked = ClipboardNative.OpenClipboard(IntPtr.Zero); opened.Set();
                            release.WaitOne(); if (locked) ClipboardNative.CloseClipboard();
                        });
                        thread.Start(); opened.WaitOne();
                        try { Check(locked && !clipboard.TrySnapshot(out restored), "busy clipboard skips"); }
                        finally { release.Set(); thread.Join(); }
                    }
                    var delayed = new DelayedWindow();
                    try
                    {
                        Check(ClipboardNative.OpenClipboard(delayed.Handle), "mixed delayed open");
                        try
                        {
                            Check(ClipboardNative.EmptyClipboard(), "mixed delayed empty");
                            ClipboardNative.SetClipboardData(13, IntPtr.Zero);
                            var privateData = NativeSelectionClipboard.Allocate(new byte[] { 1, 2, 3 });
                            if (ClipboardNative.SetClipboardData(unknown, privateData) == IntPtr.Zero)
                            { ClipboardNative.GlobalFree(privateData); throw new Exception("Private fixture set failed"); }
                        }
                        finally { ClipboardNative.CloseClipboard(); }
                        Check(!clipboard.TrySnapshot(out restored) && delayed.Renders == 0, "unsupported snapshot never requests delayed data");
                        Check(ClipboardNative.OpenClipboard(delayed.Handle), "delayed open");
                        try { Check(ClipboardNative.EmptyClipboard(), "delayed empty"); ClipboardNative.SetClipboardData(13, IntPtr.Zero); }
                        finally { ClipboardNative.CloseClipboard(); }
                        bool captured = clipboard.TrySnapshot(out original);
                        Check(delayed.Renders == 1, "native delayed rendering invoked");
                        // A changed sequence rejects the first snapshot; the next attempt can succeed.
                        Check(captured || clipboard.TrySnapshot(out original), "delayed rendering safely resolves or skips");
                        Check(original.Formats.Any(x => x.Key == 13 && NativeSelectionClipboard.DecodeUnicode(x.Value) == "delayed"), "delayed bytes preserved");
                    }
                    finally { delayed.DestroyHandle(); }
                }
                File.WriteAllText(Path.Combine(Program.Folder, "clipboard-native-test.json"),
                    new JavaScriptSerializer().Serialize(new { isolation = "private window station", result = "passed", cases = "text, HTML/RTF, exact bytes, empty, unknown, oversized, busy, stale/canceled restore, delayed rendering" }));
                return 0;
            }
            finally
            {
                SetThreadDesktop(previousDesktop); SetProcessWindowStation(previousStation);
                if (desktop != IntPtr.Zero) CloseDesktop(desktop);
                CloseWindowStation(station);
            }
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowStation(string name, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern IntPtr GetProcessWindowStation();
        [DllImport("user32.dll")] private static extern bool SetProcessWindowStation(IntPtr station);
        [DllImport("user32.dll")] private static extern bool CloseWindowStation(IntPtr station);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateDesktop(string name, IntPtr device, IntPtr mode, uint flags, uint access, IntPtr security);
        [DllImport("user32.dll")] private static extern IntPtr GetThreadDesktop(uint thread);
        [DllImport("user32.dll")] private static extern bool SetThreadDesktop(IntPtr desktop);
        [DllImport("user32.dll")] private static extern bool CloseDesktop(IntPtr desktop);
    }
}
