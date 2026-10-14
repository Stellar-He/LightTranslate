using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LightTranslate
{
    internal sealed class FakeStartupRegistration : IStartupRegistration
    {
        internal StartupKind Kind = StartupKind.Off;
        internal bool FailWrites, FailRollback;
        internal int Writes;
        public StartupStatus Read()
        {
            string message = Kind == StartupKind.On ? "当前目录已配置登录后启动（隔离测试示例）。" : Kind == StartupKind.Off ? "当前目录未设置开机自启（隔离测试示例）。" : "启动状态受限制或无法确认（模拟测试示例）。";
            return new StartupStatus(Kind, message);
        }
        public bool TrySet(bool enabled, out Action rollback, out string error)
        {
            rollback = null; error = null;
            if (!Read().CanChoose || FailWrites) { error = "模拟启动项保存失败"; return false; }
            if (Read().Enabled == enabled) return true;
            var before = Kind; Kind = enabled ? StartupKind.On : StartupKind.Off; Writes++;
            rollback = delegate { if (FailRollback) throw new IOException("模拟恢复失败"); Kind = before; };
            return true;
        }
    }
    internal static class UnifiedSettingsTests
    {
        private static void Check(bool value, string message) { if (!value) throw new Exception("Unified settings: " + message); }
        internal static void Run(Dictionary<string, object> results)
        {
            string root = Path.Combine(Program.Folder, "self-test-unified-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                string path = Path.Combine(root, "settings.json");
                var target = new Settings { Enabled = false, ClipboardFallback = false, Target = "fr", MaxSelectionLength = 9000,
                    ExcludedApps = new[] { "Keep.exe", "KEEP.exe" }, Diagnostics = true, BubbleFontSize = 18, MinEnglishPercent = 60 };
                Check(target.TrySave(path), "fixture saved");
                var startup = new FakeStartupRegistration();
                var window = new SettingsWindow(target, null, startup);
                window.RenderPreview(Path.Combine(Program.Folder, "settings-translation-preview.png"), 0);
                window.RenderPreview(Path.Combine(Program.Folder, "settings-startup-preview.png"), 2);
                Check(startup.Writes == 0 && window.StartupForTest == false, "open and rendering never change startup");
                window.SetBehaviorForTest(false, false, "invalid code !", "9000", "Keep.exe\nKEEP.exe");
                Check(!window.Apply(path), "invalid changed language rejected");
                window.SetBehaviorForTest(false, false, "fr", "0", "Keep.exe\nKEEP.exe");
                Check(!window.Apply(path), "invalid changed maximum rejected");
                window.SetBehaviorForTest(false, false, "fr", "9000", @"C:\new\app.exe");
                Check(!window.Apply(path), "changed excluded path rejected");
                Check(!target.Enabled && Settings.Load(path).MaxSelectionLength == 9000, "validation changes neither memory nor disk");
                window.SetBehaviorForTest(true, true, "ja", "500", "One.exe;Two，One.exe");
                window.SetStartupForTest(true);
                Check(startup.Writes == 0, "checking startup does not write before save");
                Check(window.Apply(path) && startup.Kind == StartupKind.On && startup.Writes == 1, "save applies startup exactly once");
                var saved = Settings.Load(path);
                Check(saved.Enabled && saved.ClipboardFallback && saved.Target == "ja" && saved.MaxSelectionLength == 500, "original user options round trip");
                Check(saved.ExcludedApps.SequenceEqual(new[] { "One.exe", "Two" }), "app list separators and deduplication");
                Check(saved.Diagnostics && saved.BubbleFontSize == 18 && saved.MinEnglishPercent == 60, "hidden diagnostics and prior reading settings preserved");

                var sync = new SettingsWindow(target, null, startup);
                sync.SetInputForTest("24", "#FFFFFF", "10", "70");
                Check(target.TrySetEnabled(false, path) && !sync.EnabledForTest, "tray off immediately synchronizes open panel");
                Check(target.TrySetEnabled(true, path) && sync.EnabledForTest, "tray on immediately synchronizes open panel");
                Check(!target.TrySetEnabled(false, Path.Combine(root, "missing", "settings.json")) && target.Enabled && sync.EnabledForTest, "failed tray save reverts without notifying stale state");
                Check(sync.Apply(path), "panel saves latest tray state");
                Check(Settings.Load(path).Enabled && Settings.Load(path).BubbleFontSize == 24, "tray sync does not erase unrelated unsaved edits");

                var cancelBackend = new FakeStartupRegistration { Kind = StartupKind.On };
                var cancel = new SettingsWindow(target, null, cancelBackend);
                cancel.SetStartupForTest(false); cancel.Fill(new Settings());
                Check(cancel.StartupForTest == false && cancelBackend.Writes == 0, "reset changes configuration inputs but not startup choice or registration");
                cancel.Close();
                Check(cancelBackend.Kind == StartupKind.On && Settings.Load(path).BubbleFontSize == 24, "cancel preserves configuration and startup");

                var unchanged = new Settings { Enabled = false, ClipboardFallback = false, Target = "x-legacy-custom", MaxSelectionLength = 0,
                    ExcludedApps = new[] { @"C:\legacy\Keep.exe", "Keep.exe", "Keep.exe" }, Diagnostics = true };
                var legacy = new SettingsWindow(unchanged, null, new FakeStartupRegistration());
                Check(legacy.Apply(path), "unchanged legacy custom values remain savable");
                var legacySaved = Settings.Load(path);
                Check(legacySaved.Target == unchanged.Target && legacySaved.MaxSelectionLength == 0 && legacySaved.ExcludedApps.SequenceEqual(unchanged.ExcludedApps), "unchanged legacy values preserved verbatim");
                Check(legacySaved.Diagnostics, "diagnostics never reset by normal panel");

                var failureTarget = new Settings();
                var failedStartup = new FakeStartupRegistration { FailWrites = true };
                var failure = new SettingsWindow(failureTarget, null, failedStartup);
                failure.SetInputForTest("22", "", "10", "50"); failure.SetStartupForTest(true);
                byte[] previous = File.ReadAllBytes(path);
                Check(!failure.Apply(path) && failedStartup.Kind == StartupKind.Off && failureTarget.BubbleFontSize == 14, "startup failure does not apply configuration");
                Check(File.ReadAllBytes(path).SequenceEqual(previous) && failure.ErrorForTest.Contains("失败"), "startup failure visibly reported, existing file unchanged");
                failure.RenderPreview(Path.Combine(Program.Folder, "settings-startup-error-preview.png"), 2); failure.Close();
                var rollbackBackend = new FakeStartupRegistration();
                var rollbackWindow = new SettingsWindow(failureTarget, null, rollbackBackend);
                rollbackWindow.SetStartupForTest(true);
                Check(!rollbackWindow.Apply(Path.Combine(root, "missing", "settings.json")) && rollbackBackend.Kind == StartupKind.Off && rollbackWindow.StartupForTest == false,
                    "configuration failure restores startup and refreshes actual state");
                rollbackWindow.Close();
                var partialBackend = new FakeStartupRegistration { FailRollback = true };
                var partial = new SettingsWindow(failureTarget, null, partialBackend); partial.SetStartupForTest(true);
                Check(!partial.Apply(Path.Combine(root, "missing", "settings.json")) && partial.StartupForTest == true && partial.ErrorForTest.Contains("恢复失败"),
                    "rollback failure reported without pretending startup unchanged"); partial.Close();
                foreach (var kind in new[] { StartupKind.Unknown, StartupKind.OtherTarget, StartupKind.WindowsDisabled })
                {
                    var restricted = new FakeStartupRegistration { Kind = kind };
                    var restrictedWindow = new SettingsWindow(failureTarget, null, restricted);
                    if (kind == StartupKind.Unknown) Check(restrictedWindow.StartupForTest == null, "unreadable startup is indeterminate, not falsely disabled");
                    restrictedWindow.SetInputForTest("18", "", "10", "50");
                    Check(restrictedWindow.Apply(path) && restricted.Writes == 0, "unavailable startup does not prevent independent configuration save");
                }
                var changedBackend = new FakeStartupRegistration();
                var changedWindow = new SettingsWindow(failureTarget, null, changedBackend);
                changedWindow.SetStartupForTest(true); changedBackend.Kind = StartupKind.OtherTarget;
                previous = File.ReadAllBytes(path);
                Check(!changedWindow.Apply(path) && changedBackend.Writes == 0 && File.ReadAllBytes(path).SequenceEqual(previous),
                    "external startup target change during editing blocks save without overwriting it"); changedWindow.Close();
                results["unifiedSettings"] = "original option round trip, legacy values, hidden diagnostics, validation, tray sync, cancel and save/rollback failures passed with fake startup";

                TestShortcutBackend(root);
                results["startupRegistration"] = "real .lnk create/read/remove/byte-exact rollback, foreign shortcut protection, concurrent changes, OS approval states and I/O failure passed in isolated test directories only";
            }
            finally { Directory.Delete(root, true); }
        }
        private static void TestShortcutBackend(string root)
        {
            Check(StartupRegistration.DecodeApproval(null) == StartupApproval.Allowed, "missing approval record");
            foreach (byte state in new byte[] { 2, 6 }) { var record = new byte[12]; record[0] = state; Check(StartupRegistration.DecodeApproval(record) == StartupApproval.Allowed, "known enabled approval"); }
            foreach (byte state in new byte[] { 3, 7 }) { var record = new byte[12]; record[0] = state; Check(StartupRegistration.DecodeApproval(record) == StartupApproval.Disabled, "known disabled approval"); }
            Check(StartupRegistration.DecodeApproval(new byte[] { 2, 0, 0, 0 }) == StartupApproval.Unknown && StartupRegistration.DecodeApproval("bad") == StartupApproval.Unknown, "truncated approval never enabled");
            var unknown = new byte[12]; unknown[0] = 99;
            Check(StartupRegistration.DecodeApproval(unknown) == StartupApproval.Unknown, "unknown approval never enabled");
            string directory = Path.Combine(root, "isolated-startup");
            string executable = Path.Combine(Program.Folder, "LightTranslate.exe"), link = Path.Combine(directory, "LightTranslate.lnk");
            var approval = StartupApproval.Allowed;
            var service = new StartupRegistration(directory, executable, delegate { return approval; });
            Action undo; string error;
            Check(service.Read().Kind == StartupKind.Off && !File.Exists(link), "isolated initial state");
            Check(service.TrySet(true, out undo, out error) && service.Read().Kind == StartupKind.On, "real shortcut enable and read-back");
            undo(); Check(!File.Exists(link), "created shortcut rollback");
            Check(service.TrySet(true, out undo, out error), "fixture shortcut recreate");
            byte[] original = File.ReadAllBytes(link);
            Check(service.TrySet(false, out undo, out error) && !File.Exists(link), "real shortcut disable");
            undo(); Check(File.ReadAllBytes(link).SequenceEqual(original), "removed shortcut byte-exact restore");
            approval = StartupApproval.Disabled;
            Check(service.Read().Kind == StartupKind.WindowsDisabled && !service.TrySet(true, out undo, out error), "OS disabled record respected without writes");
            Check(File.ReadAllBytes(link).SequenceEqual(original), "disabled record preserves shortcut");
            approval = StartupApproval.Unknown; Check(service.Read().Enabled == null, "unknown OS state visible");
            approval = StartupApproval.Allowed;
            StartupRegistration.WriteLink(link, Path.Combine(root, "other.exe"));
            byte[] foreign = File.ReadAllBytes(link);
            Check(service.Read().Kind == StartupKind.OtherTarget && !service.TrySet(false, out undo, out error), "foreign target never removed");
            Check(File.ReadAllBytes(link).SequenceEqual(foreign), "foreign target contents preserved");
            File.Delete(link);
            Check(service.TrySet(true, out undo, out error), "concurrency fixture");
            StartupRegistration.WriteLink(link, Path.Combine(root, "other.exe")); foreign = File.ReadAllBytes(link);
            bool refused = false; try { undo(); } catch (IOException) { refused = true; }
            Check(refused && File.ReadAllBytes(link).SequenceEqual(foreign), "rollback does not overwrite a concurrent foreign change");
            string occupied = Path.Combine(root, "occupied"); File.WriteAllText(occupied, "fixture");
            var blocked = new StartupRegistration(occupied, executable, delegate { return StartupApproval.Allowed; });
            Check(!blocked.TrySet(true, out undo, out error) && !string.IsNullOrEmpty(error), "real startup directory write failure reported");
        }
    }
}
