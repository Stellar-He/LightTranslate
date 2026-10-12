using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace LightTranslate
{
    internal enum StartupKind { Off, On, WindowsDisabled, OtherTarget, Unknown }
    internal enum StartupApproval { Allowed, Disabled, Unknown }
    internal sealed class StartupStatus
    {
        internal readonly StartupKind Kind;
        internal readonly string Message;
        internal StartupStatus(StartupKind kind, string message) { Kind = kind; Message = message; }
        internal bool? Enabled { get { return Kind == StartupKind.Unknown ? (bool?)null : Kind == StartupKind.On; } }
        internal bool CanChoose { get { return Kind == StartupKind.On || Kind == StartupKind.Off; } }
    }
    internal interface IStartupRegistration
    {
        StartupStatus Read();
        bool TrySet(bool enabled, out Action rollback, out string error);
    }

    // The same current-user .lnk as startup.ps1. Never writes StartupApproved or security settings.
    // Directory and approval reader are explicit dependencies so tests cannot touch real startup settings.
    internal sealed class StartupRegistration : IStartupRegistration
    {
        private readonly string directory, executable, shortcut;
        private readonly Func<StartupApproval> approval;
        internal StartupRegistration(string directory, string executable, Func<StartupApproval> approval)
        {
            if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(executable) || approval == null) throw new ArgumentException("Startup dependencies required");
            this.directory = Path.GetFullPath(directory); this.executable = Path.GetFullPath(executable);
            shortcut = Path.Combine(this.directory, "LightTranslate.lnk"); this.approval = approval;
        }
        internal static IStartupRegistration CurrentUser()
        {
            try { return new StartupRegistration(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Path.Combine(Program.Folder, "LightTranslate.exe"), ReadCurrentApproval); }
            catch { return new UnavailableStartupRegistration(); }
        }
        private static StartupApproval ReadCurrentApproval()
        {
            using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\StartupFolder"))
                return DecodeApproval(key == null ? null : key.GetValue("LightTranslate.lnk"));
        }
        internal static StartupApproval DecodeApproval(object value)
        {
            if (value == null) return StartupApproval.Allowed;
            var bytes = value as byte[];
            if (bytes == null || bytes.Length != 12) return StartupApproval.Unknown;
            // Recognize common shell states conservatively; an unknown record is never assumed enabled.
            uint state = BitConverter.ToUInt32(bytes, 0);
            if (state == 2 || state == 6) return StartupApproval.Allowed;
            if (state == 3 || state == 7) return StartupApproval.Disabled;
            return StartupApproval.Unknown;
        }
        public StartupStatus Read()
        {
            try
            {
                if (File.Exists(shortcut))
                {
                    string target, arguments;
                    ReadLink(shortcut, out target, out arguments);
                    if (string.IsNullOrWhiteSpace(target) || !string.Equals(Path.GetFullPath(target), executable, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(arguments))
                        return new StartupStatus(StartupKind.OtherTarget, "已有同名启动项指向其他目录或带有参数，本面板不会覆盖它。请先检查旧启动项，再刷新状态。");
                }
                var permission = approval();
                if (permission == StartupApproval.Disabled)
                    return new StartupStatus(StartupKind.WindowsDisabled, "Windows 已禁用此启动项。请在 Windows 的启动应用中处理后刷新；本面板不会更改系统审批记录。");
                if (permission == StartupApproval.Unknown)
                    return new StartupStatus(StartupKind.Unknown, "无法确认 Windows 保存的启动状态，本次不修改开机自启。其他设置仍可保存。");
                return File.Exists(shortcut)
                    ? new StartupStatus(StartupKind.On, "当前目录已配置为登录 Windows 后启动。")
                    : new StartupStatus(StartupKind.Off, "当前目录未设置开机自启。");
            }
            catch { return new StartupStatus(StartupKind.Unknown, "无法读取开机自启状态，请检查启动目录访问权限。其他设置仍可保存。"); }
        }
        public bool TrySet(bool enabled, out Action rollback, out string error)
        {
            rollback = null; error = null;
            var current = Read();
            if (!current.CanChoose) { error = current.Message; return false; }
            if (current.Enabled == enabled) return true;
            byte[] before = null, after = null;
            bool changed = false;
            try
            {
                before = File.Exists(shortcut) ? File.ReadAllBytes(shortcut) : null;
                if (enabled)
                {
                    if (before != null) throw new IOException("启动项已变化，请刷新后重试。");
                    Directory.CreateDirectory(directory);
                    // A failed COM Save may leave a partial file; mark it for guarded recovery first.
                    changed = true;
                    WriteLink(shortcut, executable);
                    after = File.ReadAllBytes(shortcut);
                }
                else
                {
                    if (before == null) throw new IOException("启动项已变化，请刷新后重试。");
                    var refreshed = Read();
                    if (refreshed.Kind != StartupKind.On || !SameBytes(before, File.ReadAllBytes(shortcut))) throw new IOException("启动项已变化，请刷新后重试。");
                    File.Delete(shortcut); changed = true;
                }
                var verified = Read();
                if (verified.Enabled != enabled || !verified.CanChoose) throw new IOException("保存后未能确认启动状态。" + verified.Message);
                byte[] original = before, written = after;
                rollback = delegate { Restore(original, written); };
                return true;
            }
            catch (Exception ex)
            {
                error = "无法修改开机自启：" + ex.Message;
                if (changed)
                {
                    try
                    {
                        // On a partial create, only remove a link still owned by this executable.
                        if (enabled && after == null && File.Exists(shortcut))
                        {
                            string target, args; ReadLink(shortcut, out target, out args);
                            if (!string.Equals(target, executable, StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(args)) throw new IOException("启动项内容无法确认，未删除。");
                            after = File.ReadAllBytes(shortcut);
                        }
                        Restore(before, after);
                    }
                    catch { error += " 无法恢复启动项，请检查当前实际状态。"; }
                }
                return false;
            }
        }
        private void Restore(byte[] original, byte[] written)
        {
            byte[] current = File.Exists(shortcut) ? File.ReadAllBytes(shortcut) : null;
            if (!SameBytes(current, written)) throw new IOException("启动项已被其他程序修改，未覆盖其新内容。");
            if (original == null) { if (current != null) File.Delete(shortcut); }
            else File.WriteAllBytes(shortcut, original);
            byte[] restored = File.Exists(shortcut) ? File.ReadAllBytes(shortcut) : null;
            if (!SameBytes(restored, original)) throw new IOException("启动项恢复验证失败。");
        }
        private static bool SameBytes(byte[] a, byte[] b)
        { return a == null || b == null ? a == b : a.SequenceEqual(b); }
        private static object Invoke(object value, string member, BindingFlags flags, params object[] arguments)
        { return value.GetType().InvokeMember(member, flags, null, value, arguments); }
        private static void Release(object value) { if (value != null && Marshal.IsComObject(value)) Marshal.FinalReleaseComObject(value); }
        private static void ReadLink(string path, out string target, out string arguments)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path);
                target = Convert.ToString(Invoke(link, "TargetPath", BindingFlags.GetProperty));
                arguments = Convert.ToString(Invoke(link, "Arguments", BindingFlags.GetProperty));
            }
            finally { Release(link); Release(shell); }
        }
        internal static void WriteLink(string path, string executable)
        {
            object shell = null, link = null;
            try
            {
                shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true));
                link = Invoke(shell, "CreateShortcut", BindingFlags.InvokeMethod, path);
                Invoke(link, "TargetPath", BindingFlags.SetProperty, executable);
                Invoke(link, "WorkingDirectory", BindingFlags.SetProperty, Path.GetDirectoryName(executable));
                Invoke(link, "Arguments", BindingFlags.SetProperty, "");
                Invoke(link, "Description", BindingFlags.SetProperty, "LightTranslate automatic selection translation");
                Invoke(link, "IconLocation", BindingFlags.SetProperty, executable + ",0");
                Invoke(link, "Save", BindingFlags.InvokeMethod);
            }
            finally { Release(link); Release(shell); }
        }
    }
    internal sealed class UnavailableStartupRegistration : IStartupRegistration
    {
        public StartupStatus Read() { return new StartupStatus(StartupKind.Unknown, "无法确定当前用户启动目录。本次不修改开机自启，其他设置仍可保存。"); }
        public bool TrySet(bool enabled, out Action rollback, out string error) { rollback = null; error = Read().Message; return false; }
    }
}
