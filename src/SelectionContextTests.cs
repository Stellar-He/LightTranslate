using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class SelectionContextTests
    {
        private sealed class Clipboard : ISelectionClipboard
        {
            internal int Snapshots, Copies, Reads, Restores;
            internal bool CanSnapshot = true;
            private uint sequence = 10;
            public uint Sequence { get { return sequence; } }
            public bool TrySnapshot(out ClipboardSnapshot saved)
            { Snapshots++; saved = new ClipboardSnapshot { Sequence = sequence }; return CanSnapshot; }
            internal void Copy() { Copies++; sequence++; }
            public bool TryReadText(IntPtr source, uint expected, out uint version, out string text)
            { Reads++; version = sequence; text = "reversible"; return true; }
            public RestoreResult Restore(ClipboardSnapshot saved, uint expected, Func<bool> valid)
            { Restores++; return RestoreResult.Restored; }
        }

        private static string Acquire(SelectionReadResult result, Clipboard clipboard)
        {
            return SelectionIntegrationTests.Pump(SelectionAcquisition.Read(delegate { return Task.FromResult(result); },
                delegate(SelectionReadResult inspected) { return ClipboardCapture.Read(clipboard, IntPtr.Zero, delegate { return true; },
                    clipboard.Copy, delegate { }, delegate { return Task.FromResult(0); }, delegate { return Task.FromResult(inspected.ForCapture()); }); },
                delegate { return true; }));
        }

        internal static int Run()
        {
            var passed = new List<string>();
            Action<string, bool> check = delegate(string name, bool value) { ClipboardCaptureTests.Check(value, name); passed.Add(name); };
            uint before = Native.GetClipboardSequenceNumber();
            using (var form = new Forms.Form())
            using (var list = new Forms.ListView { View = Forms.View.LargeIcon, MultiSelect = true, LabelEdit = true })
            using (var tree = new Forms.TreeView())
            using (var stale = new Forms.TextBox { Text = "stale fixture" })
            {
                form.Controls.Add(list); form.Controls.Add(tree); form.Controls.Add(stale);
                IntPtr root = form.Handle, listHandle = list.Handle, treeHandle = tree.Handle, staleHandle = stale.Handle;
                stale.SelectAll();
                Func<Forms.Control, SelectionReadResult> probe = delegate(Forms.Control control)
                { return SelectionIntegrationTests.Pump(IsolatedSelectionReader.ReadResult(root, CancellationToken.None, 2001, control.Handle)); };
                Action<string, Forms.Control> nonText = delegate(string label, Forms.Control control)
                {
                    SelectionReadResult result = probe(control);
                    check(label + " classified as non-text", result.State == SelectionReadState.NonText);
                    foreach (bool snapshotAllowed in new[] { true, false })
                    {
                        var clipboard = new Clipboard { CanSnapshot = snapshotAllowed };
                        check(label + " returns no stale text", Acquire(result, clipboard) == string.Empty);
                        check(label + " never accesses clipboard", clipboard.Snapshots == 0 && clipboard.Copies == 0 && clipboard.Reads == 0 && clipboard.Restores == 0);
                    }
                };
                nonText("desktop-style empty icon canvas", list);
                list.Items.Add("synthetic icon").Selected = true;
                nonText("selected icon drag context", list);
                list.View = Forms.View.Details; list.Columns.Add("synthetic column", 120);
                nonText("explorer-style item list", list);
                tree.Nodes.Add("synthetic tree item"); tree.SelectedNode = tree.Nodes[0];
                nonText("tree selection context", tree);
                using (var rename = new Forms.TextBox { Text = "reversible.txt" })
                {
                    list.Controls.Add(rename); IntPtr renameHandle = rename.Handle; rename.Select(0, 10);
                    var clipboard = new Clipboard();
                    check("inline edit inside item list remains selectable text", Acquire(probe(rename), clipboard) == "reversible");
                    check("inline edit selection avoids clipboard", clipboard.Snapshots == 0 && clipboard.Copies == 0);
                }
            }
            foreach (SelectionReadState state in new[] { SelectionReadState.Unsupported, SelectionReadState.Timeout, SelectionReadState.Failed })
            {
                var clipboard = new Clipboard(); bool error = false;
                try { Acquire(new SelectionReadResult(state), clipboard); } catch (SelectionUnavailableException) { error = true; }
                check(state + " remains an error before clipboard access", error && clipboard.Snapshots == 0 && clipboard.Copies == 0);
            }
            var supportedFallback = new Clipboard();
            check("confirmed text context can use safe clipboard fallback", Acquire(new SelectionReadResult(SelectionReadState.TextUnsupported), supportedFallback) == "reversible"
                && supportedFallback.Snapshots == 1 && supportedFallback.Copies == 1 && supportedFallback.Restores == 1);
            var protectedFallback = new Clipboard { CanSnapshot = false }; bool protectedError = false;
            try { Acquire(new SelectionReadResult(SelectionReadState.TextUnsupported), protectedFallback); } catch (SelectionUnavailableException) { protectedError = true; }
            check("unsupported text preserves unsafe clipboard and reports error", protectedError && protectedFallback.Copies == 0 && protectedFallback.Reads == 0);
            bool valid = true; int fallbackCalls = 0;
            string canceled = SelectionIntegrationTests.Pump(SelectionAcquisition.Read(
                delegate { valid = false; return Task.FromResult(new SelectionReadResult(SelectionReadState.Selected, "stale fixture")); },
                delegate { fallbackCalls++; return Task.FromResult("stale fallback"); }, delegate { return valid; }));
            check("changed source discards selection before fallback or translation", canceled == null && fallbackCalls == 0);
            var waiting = new TaskCompletionSource<SelectionReadResult>(); int probes = 0;
            Func<SelectionReadResult, Task<string>> noFallback = delegate { throw new Exception("Unexpected clipboard fallback"); };
            Task<string> first = SelectionAcquisition.Read(delegate { probes++; return waiting.Task; }, noFallback, delegate { return true; });
            string second = SelectionIntegrationTests.Pump(SelectionAcquisition.Read(delegate { probes++; return waiting.Task; }, noFallback, delegate { return true; }));
            waiting.SetResult(new SelectionReadResult(SelectionReadState.NonText, string.Empty));
            check("overlapping gestures do not launch another helper", second == null && SelectionIntegrationTests.Pump(first) == string.Empty && probes == 1);
            File.WriteAllText(Path.Combine(Program.Folder, "selection-context-test.json"), new JavaScriptSerializer().Serialize(new {
                passed, clipboardSequenceUnchanged = before == Native.GetClipboardSequenceNumber(),
                scope = "Owned hidden ListView, TreeView and inline Edit; real external UIA; synthetic gesture contexts, no user icons or files moved"
            }));
            return 0;
        }
    }
}
