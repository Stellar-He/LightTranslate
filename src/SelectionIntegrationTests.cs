using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class SelectionIntegrationTests
    {
        // Real native edit controls and the real external UIA process; no user clipboard payload.
        private sealed class ProtectedClipboard : ISelectionClipboard
        {
            internal int Copies, Reads, Restores;
            public uint Sequence { get { return 1; } }
            public bool TrySnapshot(out ClipboardSnapshot snapshot) { snapshot = null; return false; }
            public bool TryReadText(IntPtr source, uint expected, out uint sequence, out string text)
            { Reads++; sequence = 0; text = null; throw new Exception("Protected clipboard must not be read"); }
            public RestoreResult Restore(ClipboardSnapshot snapshot, uint expected, Func<bool> valid)
            { Restores++; throw new Exception("Protected clipboard must not be restored"); }
        }
        internal static T Pump<T>(Task<T> task)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (!task.IsCompleted && DateTime.UtcNow < until) { Forms.Application.DoEvents(); Thread.Sleep(5); }
            if (!task.IsCompleted) throw new Exception("Selection integration timed out");
            return task.GetAwaiter().GetResult();
        }
        internal static int Run()
        {
            uint before = Native.GetClipboardSequenceNumber();
            var results = new Dictionary<string, object>();
            using (var form = new Forms.Form())
            using (var text = new Forms.TextBox { Multiline = true, Text = "prefix reversible suffix" })
            {
                form.Controls.Add(text);
                IntPtr root = form.Handle, focus = text.Handle;
                text.Select(7, 10);
                var clipboard = new ProtectedClipboard();
                string selected = Pump(ClipboardCapture.Read(clipboard, root, delegate { return true; },
                    delegate { clipboard.Copies++; }, delegate { }, delegate { return Task.Delay(15); },
                    delegate { return IsolatedSelectionReader.Read(root, CancellationToken.None, 2001, focus); }));
                ClipboardCaptureTests.Check(selected == "reversible", "real edit selection through protected clipboard fallback");
                ClipboardCaptureTests.Check(clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0, "protected clipboard untouched");
                ClipboardCaptureTests.Check(SelectionReader.ShouldTranslate(selected, 25), "selected English passes real language gate");
                using (var lexicon = new Lexicon(Program.Folder))
                    ClipboardCaptureTests.Check((lexicon.Lookup(selected) ?? "").Contains("逆"), "selected text reaches local dictionary without network");
                text.Select(0, 0);
                ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 2001, focus))), "empty selection stays empty");
                text.Select(0, text.TextLength);
                ClipboardCaptureTests.Check(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 8, focus)).Length == 8, "bounded selection output");
                text.UseSystemPasswordChar = true;
                ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(root, CancellationToken.None, 2001, text.Handle))), "password selection rejected");
                using (var other = new Forms.Form())
                    ClipboardCaptureTests.Check(string.IsNullOrEmpty(Pump(IsolatedSelectionReader.Read(other.Handle, CancellationToken.None, 2001, text.Handle))), "wrong root rejected");
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel(); bool stopped = false;
                    try { Pump(IsolatedSelectionReader.Read(root, canceled.Token, 2001, text.Handle)); }
                    catch (OperationCanceledException) { stopped = true; }
                    ClipboardCaptureTests.Check(stopped, "canceled selection rejected");
                }
            }
            results["selectionIntegration"] = "real Windows edit selection -> external UIA helper -> protected clipboard fallback -> language gate -> local dictionary; empty/password/wrong-root/canceled/length cases passed; no network or clipboard contents accessed";
            results["clipboardSequenceUnchanged"] = before == Native.GetClipboardSequenceNumber();
            File.WriteAllText(Path.Combine(Program.Folder, "selection-integration-test.json"), new JavaScriptSerializer().Serialize(results));
            return 0;
        }
    }
}
