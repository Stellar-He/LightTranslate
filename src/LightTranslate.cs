// LightTranslate: an automatic selection translation bubble for Windows.
// Compile with the Windows .NET Framework compiler; no extra runtime is required.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    internal static class Program
    {
        internal static readonly string Folder = AppDomain.CurrentDomain.BaseDirectory;
        [STAThread]
        private static int Main(string[] args)
        {
            try
            {
                ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
                ServicePointManager.DefaultConnectionLimit = 8;
                if (args.Length > 0 && args[0] == "--build-dictionary")
                {
                    Lexicon.Build(args[1], Folder);
                    return 0;
                }
                if (args.Length > 0 && args[0] == "--self-test") return SelfTest.Run(false);
                if (args.Length > 0 && args[0] == "--self-test-offline") return SelfTest.Run(true);
                bool created;
                using (var single = new Mutex(true, "Local\\LightTranslate.Instance", out created))
                {
                    if (!created) return 0;
                    var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                    {
                        Log("ui", e.Exception.GetType().Name);
                        e.Handled = true;
                    };
                    using (var context = new Controller(app)) app.Run();
                }
                return 0;
            }
            catch (Exception ex)
            {
                Log("startup", ex.ToString());
                return 1;
            }
        }
        internal static void Log(string area, string message)
        {
            // Never log selected text or clipboard contents.
            try
            {
                var path = Path.Combine(Folder, "status.log");
                if (File.Exists(path) && new FileInfo(path).Length > 128000) File.WriteAllText(path, "");
                File.AppendAllText(path, DateTime.Now.ToString("s") + " " + area + " " + message + Environment.NewLine);
            }
            catch { }
        }
    }

    internal sealed class Settings
    {
        public bool Enabled = true;
        public bool ClipboardFallback = true;
        public string Target = "zh-Hans";
        public int MaxSelectionLength = 2000;
        public string[] ExcludedApps = new string[0];
        public bool Diagnostics = false;
        public static Settings Load()
        {
            try
            {
                var path = Path.Combine(Program.Folder, "settings.json");
                if (File.Exists(path)) return new JavaScriptSerializer().Deserialize<Settings>(File.ReadAllText(path));
            }
            catch { }
            return new Settings();
        }
        public void Save()
        {
            try { File.WriteAllText(Path.Combine(Program.Folder, "settings.json"), new JavaScriptSerializer().Serialize(this), Encoding.UTF8); }
            catch { }
        }
    }

    internal sealed class Controller : IDisposable
    {
        private readonly Application app;
        private readonly Settings settings;
        private readonly Bubble bubble;
        private readonly Translator translator;
        private readonly Lexicon lexicon;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripMenuItem enabledItem;
        private readonly DispatcherTimer timer;
        private readonly Native.Hook mouseDelegate, keyboardDelegate;
        private IntPtr mouseHook, keyboardHook;
        private CancellationTokenSource pending;
        private int generation;
        private bool down, dragged, disposed;
        private Native.POINT downPoint, previousUp, anchor;
        private IntPtr downWindow, sourceWindow;
        private uint lastUpTime;
        private DateTime shownAt, awayAt;
        private int displaySeconds;
        private readonly Queue<string> diagnosticQueue = new Queue<string>();
        private long mouseEvents, mouseDowns, mouseUps, selections;
        private DateTime nextHeartbeat;

        public Controller(Application app)
        {
            this.app = app;
            settings = Settings.Load();
            lexicon = new Lexicon(Program.Folder);
            translator = new Translator();
            var warming = translator.WarmConnection();
            bubble = new Bubble();
            tray = new Forms.NotifyIcon { Text = "轻译 · 选中即译", Icon = MakeIcon(), Visible = true };
            var menu = new Forms.ContextMenuStrip();
            enabledItem = new Forms.ToolStripMenuItem("自动划词翻译") { Checked = settings.Enabled, CheckOnClick = true };
            enabledItem.Click += delegate { settings.Enabled = enabledItem.Checked; settings.Save(); Dismiss(true); };
            menu.Items.Add(enabledItem);
            menu.Items.Add("打开设置文件", null, delegate { Process.Start("notepad.exe", Path.Combine(Program.Folder, "settings.json")); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出轻译", null, delegate { app.Shutdown(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { enabledItem.PerformClick(); };
            settings.Save();
            mouseDelegate = MouseHook;
            keyboardDelegate = KeyboardHook;
            var module = Native.GetModuleHandle(null);
            mouseHook = Native.SetWindowsHookEx(14, mouseDelegate, module, 0);
            keyboardHook = Native.SetWindowsHookEx(13, keyboardDelegate, module, 0);
            if (mouseHook == IntPtr.Zero || keyboardHook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            timer.Tick += delegate { CheckDismissal(); FlushDiagnostics(); };
            timer.Start();
            Program.Log("started", "hooks=ok capture=clipboard dictionary=" + lexicon.Count);
        }

        internal static System.Drawing.Icon MakeIcon()
        {
            using (var bmp = new System.Drawing.Bitmap(32, 32))
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            using (var brush = new System.Drawing.SolidBrush(System.Drawing.Color.FromArgb(56, 112, 210)))
            using (var font = new System.Drawing.Font("Microsoft YaHei UI", 16, System.Drawing.FontStyle.Bold))
            {
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                g.Clear(System.Drawing.Color.Transparent);
                g.FillEllipse(brush, 1, 1, 30, 30);
                g.DrawString("译", font, System.Drawing.Brushes.White, 1, 1);
                IntPtr h = bmp.GetHicon();
                try { return (System.Drawing.Icon)System.Drawing.Icon.FromHandle(h).Clone(); }
                finally { Native.DestroyIcon(h); }
            }
        }

        private IntPtr MouseHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !disposed)
            {
                var data = (Native.MOUSE)Marshal.PtrToStructure(lParam, typeof(Native.MOUSE));
                mouseEvents++;
                // Remote input, accessibility tools, and mouse utilities may inject human actions.
                // Only ignore input generated by this application, rather than all injected input.
                if (data.extra != Native.OwnInputMarker)
                {
                    int message = wParam.ToInt32();
                    if (message == 0x201)
                    {
                        mouseDowns++;
                        bool inBubble = bubble.Contains(data.pt);
                        if (!inBubble)
                        {
                            Dismiss(false);
                            down = settings.Enabled;
                            dragged = false;
                            downPoint = data.pt;
                            downWindow = Native.GetAncestor(Native.WindowFromPoint(data.pt), 2);
                        }
                    }
                    else if (message == 0x200 && down)
                    {
                        if (Math.Abs(data.pt.x - downPoint.x) >= 5 || Math.Abs(data.pt.y - downPoint.y) >= 5) dragged = true;
                    }
                    else if (message == 0x202 && down)
                    {
                        mouseUps++;
                        down = false;
                        bool twice = unchecked(data.time - lastUpTime) <= Native.GetDoubleClickTime()
                            && Math.Abs(data.pt.x - previousUp.x) <= Native.GetSystemMetrics(36)
                            && Math.Abs(data.pt.y - previousUp.y) <= Native.GetSystemMetrics(37);
                        previousUp = data.pt;
                        lastUpTime = data.time;
                        IntPtr window = Native.GetForegroundWindow();
                        Trace("mouse-up drag=" + dragged + " double=" + twice + " same-window=" + (window == downWindow) + " same-app=" + Native.SameApp(window, downWindow) + " injected=" + ((data.flags & 1) != 0));
                        if (settings.Enabled && (dragged || twice) && (window == downWindow || Native.SameApp(window, downWindow)) && !bubble.Contains(data.pt))
                        {
                            var point = data.pt;
                            int queuedVersion = generation;
                            app.Dispatcher.BeginInvoke(new Action(delegate { if (queuedVersion == generation) BeginSelection(window, point); }));
                        }
                    }
                    else if (message == 0x20A || message == 0x20E || message == 0x204 || message == 0x207)
                    {
                        if (!bubble.Contains(data.pt)) Dismiss(false);
                    }
                }
            }
            return Native.CallNextHookEx(mouseHook, code, wParam, lParam);
        }

        private IntPtr KeyboardHook(int code, IntPtr wParam, IntPtr lParam)
        {
            if (code >= 0 && !disposed)
            {
                var data = (Native.KEYBOARD)Marshal.PtrToStructure(lParam, typeof(Native.KEYBOARD));
                if (data.extra != Native.OwnInputMarker)
                {
                    int message = wParam.ToInt32();
                    if (message == 0x100 || message == 0x104) Dismiss(false);
                    if ((message == 0x101 || message == 0x105) && (data.vkCode == 0xA0 || data.vkCode == 0xA1) && settings.Enabled)
                    {
                        Native.POINT point;
                        Native.GetCursorPos(out point);
                        var window = Native.GetForegroundWindow();
                        int queuedVersion = generation;
                        app.Dispatcher.BeginInvoke(new Action(delegate { if (queuedVersion == generation) BeginSelection(window, point); }));
                    }
                }
            }
            return Native.CallNextHookEx(keyboardHook, code, wParam, lParam);
        }

        private void Dismiss(bool immediate)
        {
            generation++;
            if (pending != null) { pending.Cancel(); pending = null; }
            if (bubble.IsVisible) bubble.Dismiss(immediate);
            awayAt = DateTime.MinValue;
        }

        private async void BeginSelection(IntPtr window, Native.POINT point)
        {
            selections++;
            Dismiss(true);
            if (!settings.Enabled || window == IntPtr.Zero) return;
            uint pid;
            Native.GetWindowThreadProcessId(window, out pid);
            if (pid == Process.GetCurrentProcess().Id) return;
            string processName;
            try { using (var process = Process.GetProcessById((int)pid)) processName = process.ProcessName; }
            catch { return; }
            Trace("selection app=" + processName + " pid=" + pid);
            if (settings.ExcludedApps != null && settings.ExcludedApps.Any(x => string.Equals(Path.GetFileNameWithoutExtension(x), processName, StringComparison.OrdinalIgnoreCase))) return;
            int version = generation;
            var cancellation = new CancellationTokenSource();
            pending = cancellation;
            CancellationToken ct = cancellation.Token;
            try
            {
                await Task.Delay(25, ct);
                if (!Valid(version, window)) return;
                // Capture through the clipboard without UIAutomationClient handles.
                // Native UIA pattern finalizers caused an access violation during testing.
                if (!settings.ClipboardFallback || SelectionReader.IsTerminal(processName)) return;
                string text = await SelectionReader.CopySelection(window, ct, Trace);
                Trace("clipboard chars=" + (text == null ? 0 : text.Length));
                if (!Valid(version, window) || string.IsNullOrWhiteSpace(text)) { Trace("selection discarded valid=" + Valid(version, window)); return; }
                text = text.Trim();
                if (!SelectionReader.HasForeignText(text)) { Trace("Chinese-only selection skipped"); return; }
                anchor = point;
                sourceWindow = window;
                if (text.Length > settings.MaxSelectionLength)
                {
                    ShowText("选中文字较长，请分段选择", point, 5);
                    return;
                }
                string translated = lexicon.Lookup(text);
                if (translated != null || translator.TryCached(text, settings.Target, out translated))
                {
                    ShowText(translated, point, ReadingSeconds(translated));
                    return;
                }
                var request = translator.Translate(text, settings.Target, ct);
                if (await Task.WhenAny(request, Task.Delay(220, ct)) != request && Valid(version, window))
                    ShowText("…", point, 8);
                translated = await request;
                if (!Valid(version, window)) return;
                ShowText(translated, point, ReadingSeconds(translated));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                Program.Log("translate", ex.GetType().Name);
                if (Valid(version, window)) ShowText("暂时无法连接翻译服务", point, 3);
            }
            finally
            {
                if (ReferenceEquals(pending, cancellation)) pending = null;
                cancellation.Dispose();
            }
        }
        private bool Valid(int version, IntPtr window)
        {
            return !disposed && version == generation && settings.Enabled && Native.GetForegroundWindow() == window;
        }
        private static int ReadingSeconds(string text) { return Math.Max(6, Math.Min(22, 4 + text.Length / 12)); }
        private void ShowText(string text, Native.POINT point, int seconds)
        {
            displaySeconds = seconds;
            shownAt = DateTime.UtcNow;
            awayAt = DateTime.MinValue;
            bubble.Present(text, point);
            Trace("bubble visible=" + bubble.IsVisible + " chars=" + text.Length);
        }
        private void Trace(string value)
        {
            if (settings.Diagnostics && diagnosticQueue.Count < 64) diagnosticQueue.Enqueue(value);
        }
        private void FlushDiagnostics()
        {
            if (!settings.Diagnostics) return;
            if (DateTime.UtcNow >= nextHeartbeat)
            {
                nextHeartbeat = DateTime.UtcNow.AddSeconds(5);
                Trace("activity mouse=" + mouseEvents + " down=" + mouseDowns + " up=" + mouseUps + " selection=" + selections);
            }
            while (diagnosticQueue.Count > 0) Program.Log("debug", diagnosticQueue.Dequeue());
        }
        private void CheckDismissal()
        {
            if (!bubble.IsVisible) return;
            if (Native.GetForegroundWindow() != sourceWindow) { Dismiss(false); return; }
            Native.POINT point;
            Native.GetCursorPos(out point);
            bool near = Math.Abs(point.x - anchor.x) < 120 && Math.Abs(point.y - anchor.y) < 90;
            bool over = bubble.Contains(point);
            var now = DateTime.UtcNow;
            if (over) { shownAt = now; awayAt = DateTime.MinValue; return; }
            if (!near)
            {
                if (awayAt == DateTime.MinValue) awayAt = now;
                if ((now - awayAt).TotalMilliseconds > 550 && (now - shownAt).TotalMilliseconds > 900) { Dismiss(false); return; }
            }
            else awayAt = DateTime.MinValue;
            if ((now - shownAt).TotalSeconds > displaySeconds) Dismiss(false);
        }
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Dismiss(true);
            timer.Stop();
            if (mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) Native.UnhookWindowsHookEx(keyboardHook);
            tray.Visible = false;
            tray.Icon.Dispose();
            tray.Dispose();
            bubble.Close();
            translator.Dispose();
            lexicon.Dispose();
        }
    }

    internal static class SelectionReader
    {
        public static bool IsTerminal(string name)
        {
            return new[] { "WindowsTerminal", "OpenConsole", "conhost", "cmd", "powershell", "pwsh", "mintty", "wezterm", "alacritty", "putty" }
                .Any(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
        }
        public static bool HasForeignText(string text)
        {
            return text.Any(c => char.IsLetter(c) && !(c >= '\u3400' && c <= '\u9fff'));
        }
        public static async Task<string> CopySelection(IntPtr window, CancellationToken ct, Action<string> trace)
        {
            if (Native.GetForegroundWindow() != window || Native.ModifierDown()) { trace("copy skipped focus/modifier"); return null; }
            if (Native.IsPasswordFocus(window)) { trace("password field skipped"); return null; }
            uint before = Native.GetClipboardSequenceNumber(), copied = before;
            Forms.DataObject saved;
            try { saved = Snapshot(Forms.Clipboard.GetDataObject()); }
            catch (Exception ex) { trace("clipboard snapshot " + ex.GetType().Name); return null; }
            if (Native.GetClipboardSequenceNumber() != before) return null;
            string result = null;
            try
            {
                if (Native.GetForegroundWindow() != window || ct.IsCancellationRequested) return null;
                Native.Copy();
                for (int i = 0; i < 12; i++)
                {
                    await Task.Delay(15);
                    uint sequence = Native.GetClipboardSequenceNumber();
                    if (sequence != before)
                    {
                        bool owned = Native.SameApp(Native.GetClipboardOwner(), window);
                        trace("copy changed owner-same-app=" + owned + " canceled=" + ct.IsCancellationRequested);
                        if (!ct.IsCancellationRequested && Native.GetForegroundWindow() == window && owned)
                        {
                            copied = sequence;
                            if (Forms.Clipboard.ContainsText()) result = Forms.Clipboard.GetText();
                        }
                        break;
                    }
                    if (ct.IsCancellationRequested) break;
                }
                return ct.IsCancellationRequested ? null : result;
            }
            catch { return null; }
            finally
            {
                // Do not overwrite a newer clipboard created by the user or another app.
                if (copied != before)
                {
                    for (int i = 0; i < 3; i++)
                    {
                        if (Native.GetClipboardSequenceNumber() != copied) break;
                        try { Forms.Clipboard.SetDataObject(saved, true); break; }
                        catch { Thread.Sleep(10); }
                    }
                }
            }
        }
        internal static Forms.DataObject Snapshot(Forms.IDataObject source)
        {
            var copy = new Forms.DataObject();
            if (source == null) return copy;
            foreach (string format in source.GetFormats(false))
            {
                var value = source.GetData(format, false);
                var stream = value as MemoryStream;
                var image = value as System.Drawing.Image;
                if (stream != null) value = new MemoryStream(stream.ToArray());
                else if (image != null) value = image.Clone();
                else if (value is string[]) value = ((string[])value).Clone();
                if (value != null) copy.SetData(format, false, value);
            }
            return copy;
        }
    }

    internal sealed class Bubble : Window
    {
        private readonly Border card;
        private readonly TextBlock content;
        private int hideVersion;
        public Bubble()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            content = new TextBlock
            {
                FontFamily = new FontFamily("Microsoft YaHei UI"),
                FontSize = 14,
                LineHeight = 23,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 340,
                Foreground = new SolidColorBrush(Color.FromRgb(36, 40, 48))
            };
            var scroller = new ScrollViewer
            {
                Content = content, MaxHeight = 322,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Focusable = false
            };
            card = new Border
            {
                Child = scroller, Padding = new Thickness(15, 10, 15, 11),
                Background = new SolidColorBrush(Color.FromRgb(252, 252, 253)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(35, 40, 48, 64)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(11),
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 18, Opacity = 0.15, ShadowDepth = 3 }
            };
            Content = new Border { Padding = new Thickness(12), Child = card };
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0)
                    {
                        card.Background = new SolidColorBrush(Color.FromRgb(37, 40, 47));
                        card.BorderBrush = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255));
                        content.Foreground = new SolidColorBrush(Color.FromRgb(237, 239, 243));
                    }
                }
            }
            catch { }
            SourceInitialized += delegate
            {
                var handle = new WindowInteropHelper(this).Handle;
                Native.SetWindowLong(handle, -20, Native.GetWindowLong(handle, -20) | 0x08000000 | 0x80);
                HwndSource.FromHwnd(handle).AddHook(delegate(IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled)
                {
                    if (message == 0x21) { handled = true; return new IntPtr(3); }
                    return IntPtr.Zero;
                });
            };
        }
        public bool Contains(Native.POINT p)
        {
            if (!IsVisible) return false;
            Native.RECT bounds;
            if (!Native.GetWindowRect(new WindowInteropHelper(this).Handle, out bounds)) return false;
            return p.x >= bounds.left && p.x < bounds.right && p.y >= bounds.top && p.y < bounds.bottom;
        }
        public void Present(string text, Native.POINT point)
        {
            hideVersion++;
            bool wasVisible = IsVisible;
            IntPtr handle = new WindowInteropHelper(this).EnsureHandle();
            // Move the hidden HWND first so WPF adopts the destination monitor's DPI.
            if (!wasVisible) Native.SetWindowPos(handle, IntPtr.Zero, point.x, point.y, 0, 0, 0x15);
            content.Text = text;
            ((FrameworkElement)Content).Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var desired = ((FrameworkElement)Content).DesiredSize;
            double scaleX = 1, scaleY = 1;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
            {
                scaleX = source.CompositionTarget.TransformToDevice.M11;
                scaleY = source.CompositionTarget.TransformToDevice.M22;
            }
            var screen = Forms.Screen.FromPoint(new System.Drawing.Point(point.x, point.y)).WorkingArea;
            double width = desired.Width * scaleX, height = desired.Height * scaleY;
            double x = point.x + 12, y = point.y + 18;
            if (x + width > screen.Right) x = point.x - width - 8;
            if (y + height > screen.Bottom) y = point.y - height - 8;
            x = Math.Max(screen.Left + 2, Math.Min(x, screen.Right - width - 2));
            y = Math.Max(screen.Top + 2, Math.Min(y, screen.Bottom - height - 2));
            Left = x / scaleX; Top = y / scaleY;
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            if (!wasVisible)
            {
                Show();
                BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(85)));
            }
            Native.SetWindowPos(handle, IntPtr.Zero, (int)x, (int)y, 0, 0, 0x15);
        }
        public void Dismiss(bool immediate)
        {
            if (!IsVisible) return;
            int version = ++hideVersion;
            if (immediate) { BeginAnimation(OpacityProperty, null); Hide(); return; }
            var fade = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(90));
            fade.Completed += delegate { if (hideVersion == version) { Hide(); BeginAnimation(OpacityProperty, null); Opacity = 1; } };
            BeginAnimation(OpacityProperty, fade);
        }
        internal void RenderPreview(string file)
        {
            content.Text = "选中后直接显示译文。\n继续操作，浮层自动消失。";
            var view = (FrameworkElement)Content;
            view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            view.Arrange(new Rect(view.DesiredSize));
            view.UpdateLayout();
            var bitmap = new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth), (int)Math.Ceiling(view.ActualHeight), 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(view);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) png.Save(stream);
        }
    }

    internal sealed class Lexicon : IDisposable
    {
        private BinaryReader data;
        private long[] offsets;
        public int Count { get { return offsets == null ? 0 : offsets.Length; } }
        private readonly Dictionary<string, string> technical = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "cache", "缓存；藏匿处" }, { "token", "词元；令牌；标记" }, { "prompt", "提示；提示词；促使" },
            { "context", "上下文；语境" }, { "repository", "代码仓库；存储库" }, { "commit", "提交；承诺" },
            { "branch", "分支；树枝" }, { "staging", "暂存；预发布" }, { "deployment", "部署" },
            { "rollback", "回滚；撤回" }, { "dependency", "依赖；依赖项" }, { "dependencies", "依赖项" },
            { "worktree", "工作树；独立的代码工作目录" }, { "refactor", "重构" }, { "refactoring", "重构" },
            { "clipboard", "剪贴板" }, { "hover", "悬停；盘旋" }, { "latency", "延迟" },
            { "streaming", "流式传输" }, { "fallback", "备用方式；回退" }
        };
        public Lexicon(string folder)
        {
            try
            {
                var index = Path.Combine(folder, "lexicon.idx");
                if (!File.Exists(index)) return;
                using (var reader = new BinaryReader(File.OpenRead(index)))
                {
                    int count = reader.ReadInt32();
                    offsets = new long[count];
                    for (int i = 0; i < count; i++) offsets[i] = reader.ReadInt64();
                }
                data = new BinaryReader(File.OpenRead(Path.Combine(folder, "lexicon.dat")), Encoding.UTF8);
            }
            catch { offsets = null; if (data != null) data.Dispose(); data = null; }
        }
        public string Lookup(string text)
        {
            string key = text.Trim().Trim('.', ',', '!', '?', ':', ';', '"', '“', '”', '‘', '’').ToLowerInvariant();
            if (key.Length > 80 || !Regex.IsMatch(key, "^[a-z][a-z '\\-]*$")) return null;
            string known;
            if (technical.TryGetValue(key, out known)) return known;
            if (data == null) return null;
            int low = 0, high = offsets.Length - 1;
            while (low <= high)
            {
                int middle = low + (high - low) / 2;
                data.BaseStream.Position = offsets[middle];
                var word = data.ReadString();
                int order = string.CompareOrdinal(word, key);
                if (order == 0) return data.ReadString();
                if (order < 0) low = middle + 1;
                else high = middle - 1;
            }
            return null;
        }
        public void Dispose() { if (data != null) data.Dispose(); }
        public static void Build(string csv, string folder)
        {
            var words = new SortedDictionary<string, string>(StringComparer.Ordinal);
            using (var parser = new Microsoft.VisualBasic.FileIO.TextFieldParser(csv, Encoding.UTF8))
            {
                parser.TextFieldType = Microsoft.VisualBasic.FileIO.FieldType.Delimited;
                parser.SetDelimiters(",");
                parser.HasFieldsEnclosedInQuotes = true;
                var headers = parser.ReadFields();
                int wordIndex = Array.IndexOf(headers, "word"), translationIndex = Array.IndexOf(headers, "translation");
                while (!parser.EndOfData)
                {
                    var row = parser.ReadFields();
                    if (row.Length <= translationIndex) continue;
                    string word = row[wordIndex].ToLowerInvariant().Trim(), translated = row[translationIndex].Replace("\\n", "\n").Trim();
                    if (translated.Length > 0 && word.Length <= 80 && Regex.IsMatch(word, "^[a-z][a-z '\\-]*$")) words[word] = translated;
                }
            }
            using (var values = new BinaryWriter(File.Create(Path.Combine(folder, "lexicon.dat")), Encoding.UTF8))
            using (var index = new BinaryWriter(File.Create(Path.Combine(folder, "lexicon.idx"))))
            {
                index.Write(words.Count);
                foreach (var entry in words)
                {
                    index.Write(values.BaseStream.Position);
                    values.Write(entry.Key);
                    values.Write(entry.Value);
                }
            }
            File.WriteAllText(Path.Combine(folder, "dictionary-build.txt"), "ECDICT English–Chinese entries: " + words.Count);
        }
    }

    internal sealed class Translator : IDisposable
    {
        private readonly HttpClient http;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private readonly Dictionary<string, string> cache = new Dictionary<string, string>();
        private readonly Queue<string> eviction = new Queue<string>();
        public Translator()
        {
            http = new HttpClient { Timeout = TimeSpan.FromSeconds(7) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
            http.DefaultRequestHeaders.ExpectContinue = false;
        }
        internal async Task WarmConnection()
        {
            try
            {
                using (var request = new HttpRequestMessage(HttpMethod.Head, "https://api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=zh-Hans"))
                using (var response = await http.SendAsync(request)) { }
            }
            catch { }
        }
        public bool TryCached(string text, string target, out string result) { return cache.TryGetValue(target + "\n" + text, out result); }
        public async Task<string> Translate(string text, string target, CancellationToken ct)
        {
            string result;
            if (TryCached(text, target, out result)) return result;
            bool fallback = false;
            result = null;
            try { result = await Microsoft(text, target, ct); }
            catch (OperationCanceledException) { if (ct.IsCancellationRequested) throw; fallback = true; }
            catch { ct.ThrowIfCancellationRequested(); fallback = true; }
            if (fallback) result = await Tencent(text, ct);
            if (string.IsNullOrWhiteSpace(result)) throw new InvalidDataException("Empty translation");
            ct.ThrowIfCancellationRequested();
            string key = target + "\n" + text;
            if (!cache.ContainsKey(key))
            {
                if (cache.Count >= 256) cache.Remove(eviction.Dequeue());
                cache[key] = result;
                eviction.Enqueue(key);
            }
            return result;
        }
        private async Task<string> Microsoft(string text, string target, CancellationToken ct)
        {
            string path = "api.cognitive.microsofttranslator.com/translate?api-version=3.0&to=" + Uri.EscapeDataString(target);
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://" + path))
            {
                request.Headers.TryAddWithoutValidation("X-MT-Signature", Signature(path));
                request.Content = new StringContent(json.Serialize(new[] { new { Text = text } }), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var array = json.DeserializeObject(await response.Content.ReadAsStringAsync()) as object[];
                    var item = (Dictionary<string, object>)array[0];
                    var translations = (object[])item["translations"];
                    return (string)((Dictionary<string, object>)translations[0])["text"];
                }
            }
        }
        private async Task<string> Tencent(string text, CancellationToken ct)
        {
            var payload = new
            {
                header = new { fn = "auto_translation_block", client_key = "browser-chrome-110.0.0-Windows-" + Guid.NewGuid().ToString() },
                type = "plain", model_category = "normal", source = new { lang = "auto", text_block = text }, target = new { lang = "zh" }
            };
            using (var request = new HttpRequestMessage(HttpMethod.Post, "https://transmart.qq.com/api/imt"))
            {
                request.Headers.Referrer = new Uri("https://yi.qq.com/zh-CN/index");
                request.Content = new StringContent(json.Serialize(payload), Encoding.UTF8, "application/json");
                using (var response = await http.SendAsync(request, ct))
                {
                    response.EnsureSuccessStatusCode();
                    var item = json.DeserializeObject(await response.Content.ReadAsStringAsync()) as Dictionary<string, object>;
                    return (string)item["auto_translation"];
                }
            }
        }
        private static string Signature(string path)
        {
            // Adapted from STranslate's MIT-licensed built-in Microsoft translator.
            // See licenses/STranslate-LICENSE.txt and THIRD_PARTY_NOTICES.md.
            byte[] key = { 0xa2,0x29,0x3a,0x3d,0xd0,0xdd,0x32,0x73,0x97,0x7a,0x64,0xdb,0xc2,0xf3,0x27,0xf5,0xd7,0xbf,0x87,0xd9,0x45,0x9d,0xf0,0x5a,0x09,0x66,0xc6,0x30,0xc6,0x6a,0xaa,0x84,0x9a,0x41,0xaa,0x94,0x3a,0xa8,0xd5,0x1a,0x6e,0x4d,0xaa,0xc9,0xa3,0x70,0x12,0x35,0xc7,0xeb,0x12,0xf6,0xe8,0x23,0x07,0x9e,0x47,0x10,0x95,0x91,0x88,0x55,0xd8,0x17 };
            string guid = Guid.NewGuid().ToString("N");
            string date = DateTimeOffset.UtcNow.ToString("ddd, dd MMM yyyy HH:mm:ssG\\MT", System.Globalization.CultureInfo.InvariantCulture);
            string value = ("MSTranslatorAndroidApp" + Uri.EscapeDataString(path) + date + guid).ToLowerInvariant();
            using (var hmac = new HMACSHA256(key))
                return "MSTranslatorAndroidApp::" + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))) + "::" + date + "::" + guid;
        }
        public void Dispose() { http.Dispose(); }
    }

    internal static class Native
    {
        public delegate IntPtr Hook(int code, IntPtr wParam, IntPtr lParam);
        public static readonly UIntPtr OwnInputMarker = new UIntPtr(0x4C54524Eu);
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int left, top, right, bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSE { public POINT pt; public uint mouseData, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYBOARD { public uint vkCode, scanCode, flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct GUITHREADINFO
        {
            public uint size, flags;
            public IntPtr active, focus, capture, menuOwner, moveSize, caret;
            public RECT caretRect;
        }
        [StructLayout(LayoutKind.Sequential)] public struct INPUT { public uint type; public INPUTUNION union; }
        [StructLayout(LayoutKind.Explicit)] public struct INPUTUNION { [FieldOffset(0)] public KEYINPUT keyboard; [FieldOffset(0)] public MOUSEINPUT mouse; }
        [StructLayout(LayoutKind.Sequential)] public struct KEYINPUT { public ushort vk, scan; public uint flags, time; public UIntPtr extra; }
        [StructLayout(LayoutKind.Sequential)] public struct MOUSEINPUT { public int x, y; public uint data, flags, time; public UIntPtr extra; }
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, Hook callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("kernel32.dll", CharSet = CharSet.Auto)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr window, uint flags);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
        [DllImport("user32.dll")] public static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
        [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
        [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT point);
        [DllImport("user32.dll")] public static extern uint GetClipboardSequenceNumber();
        [DllImport("user32.dll")] public static extern IntPtr GetClipboardOwner();
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] public static extern uint SendInput(uint count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr window, int index, int value);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
        public static bool SameProcess(IntPtr first, IntPtr second)
        {
            if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
            uint p1, p2;
            GetWindowThreadProcessId(first, out p1);
            GetWindowThreadProcessId(second, out p2);
            return p1 != 0 && p1 == p2;
        }
        public static bool SameApp(IntPtr first, IntPtr second)
        {
            if (first == IntPtr.Zero || second == IntPtr.Zero) return false;
            uint p1, p2;
            GetWindowThreadProcessId(first, out p1);
            GetWindowThreadProcessId(second, out p2);
            if (p1 == p2 && p1 != 0) return true;
            try
            {
                using (var a = Process.GetProcessById((int)p1))
                using (var b = Process.GetProcessById((int)p2))
                    return string.Equals(a.MainModule.FileName, b.MainModule.FileName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
        public static bool IsPasswordFocus(IntPtr window)
        {
            uint pid;
            uint thread = GetWindowThreadProcessId(window, out pid);
            var info = new GUITHREADINFO { size = (uint)Marshal.SizeOf(typeof(GUITHREADINFO)) };
            return GetGUIThreadInfo(thread, ref info) && IsPasswordEdit(info.focus);
        }
        internal static bool IsPasswordEdit(IntPtr window)
        {
            if (window == IntPtr.Zero) return false;
            var name = new StringBuilder(256);
            GetClassName(window, name, name.Capacity);
            return name.ToString().IndexOf("EDIT", StringComparison.OrdinalIgnoreCase) >= 0
                && (GetWindowLong(window, -16) & 0x20) != 0;
        }
        public static bool ModifierDown() { return (GetAsyncKeyState(0x11) & 0x8000) != 0 || (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x10) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0; }
        public static void Copy()
        {
            INPUT[] keys = new INPUT[4];
            for (int i = 0; i < 4; i++)
            {
                keys[i].type = 1;
                keys[i].union.keyboard.vk = (ushort)((i == 0 || i == 3) ? 0x11 : 0x43);
                keys[i].union.keyboard.flags = (uint)(i >= 2 ? 2 : 0);
                keys[i].union.keyboard.extra = OwnInputMarker;
            }
            if (SendInput(4, keys, Marshal.SizeOf(typeof(INPUT))) != 4) throw new System.ComponentModel.Win32Exception();
        }
    }

    internal static class SelfTest
    {
        public static int Run(bool offline)
        {
            Program.Log("self-test", "started");
            var results = new Dictionary<string, object>();
            using (var lexicon = new Lexicon(Program.Folder))
            {
                var sw = Stopwatch.StartNew();
                string word = lexicon.Lookup("reversible");
                sw.Stop();
                if (word == null || !word.Contains("逆")) throw new Exception("Lexicon lookup failed");
                if (lexicon.Lookup("a completely unknown sentence goes here") != null) throw new Exception("Lexicon false positive");
                results["dictionaryEntries"] = lexicon.Count;
                results["dictionaryLookupMs"] = sw.Elapsed.TotalMilliseconds;
                results["dictionaryText"] = word;
            }
            var original = new Forms.DataObject();
            original.SetData(Forms.DataFormats.UnicodeText, false, "original clipboard");
            original.SetData(Forms.DataFormats.Html, false, "<b>original</b>");
            var snapshot = SelectionReader.Snapshot(original);
            if ((string)snapshot.GetData(Forms.DataFormats.UnicodeText) != "original clipboard" || (string)snapshot.GetData(Forms.DataFormats.Html) != "<b>original</b>") throw new Exception("Clipboard snapshot failed");
            if (!SelectionReader.IsTerminal("pwsh") || SelectionReader.IsTerminal("Codex")) throw new Exception("Terminal guard failed");
            if (SelectionReader.HasForeignText("这是中文") || !SelectionReader.HasForeignText("Read this text")) throw new Exception("Language guard failed");
            results["clipboardSnapshot"] = "text + HTML preserved";
            results["guards"] = "terminal + language passed";
            Program.Log("self-test", "dictionary and clipboard snapshot passed");
            if (System.Reflection.Assembly.GetExecutingAssembly().GetReferencedAssemblies().Any(a => a.Name == "UIAutomationClient"))
                throw new Exception("Unsafe UIA client dependency remains");
            using (var password = new Forms.TextBox { UseSystemPasswordChar = true })
            using (var plain = new Forms.TextBox())
            {
                if (!Native.IsPasswordEdit(password.Handle) || Native.IsPasswordEdit(plain.Handle)) throw new Exception("Password guard failed");
            }
            results["capture"] = "clipboard only; no UIAutomationClient dependency";
            results["passwordGuard"] = "native password/plain controls passed";
            for (int i = 0; i < 100; i++) SelectionReader.Snapshot(original);
            Program.Log("self-test", "password guard passed; collecting objects");
            // OLE finalizers can marshal back to the STA: pump while awaiting them.
            var finalizers = Task.Run(delegate { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!finalizers.IsCompleted && DateTime.UtcNow < deadline)
            {
                Forms.Application.DoEvents();
                Thread.Sleep(5);
            }
            if (!finalizers.IsCompleted) throw new TimeoutException("Finalizer test timed out");
            finalizers.GetAwaiter().GetResult();
            results["finalizers"] = "passed";
            Program.Log("self-test", "finalizers passed; testing translation");
            using (var engine = new Translator())
            {
                using (var canceled = new CancellationTokenSource())
                {
                    canceled.Cancel();
                    bool stopped = false;
                    try { engine.Translate("Canceled request", "zh-Hans", canceled.Token).GetAwaiter().GetResult(); }
                    catch (OperationCanceledException) { stopped = true; }
                    if (!stopped) throw new Exception("Request cancellation failed");
                    results["cancellation"] = "passed";
                }
                if (!offline)
                {
                engine.WarmConnection().GetAwaiter().GetResult();
                var timings = new List<object>();
                foreach (string text in new[] { "This change improves performance.", "Select the text to see the translation.", "The window closes automatically." })
                {
                    var sw = Stopwatch.StartNew();
                    string translated = engine.Translate(text, "zh-Hans", CancellationToken.None).GetAwaiter().GetResult();
                    sw.Stop();
                    if (!translated.Any(c => c >= '\u4e00' && c <= '\u9fff')) throw new Exception("Network translation failed");
                    timings.Add(new { milliseconds = sw.ElapsedMilliseconds, text = translated });
                }
                string cached;
                if (!engine.TryCached("This change improves performance.", "zh-Hans", out cached)) throw new Exception("Cache missed");
                results["network"] = timings;
                results["cache"] = "passed";
                }
                else results["network"] = "skipped (offline self-test)";
            }
            var bubble = new Bubble();
            var handle = new WindowInteropHelper(bubble).EnsureHandle();
            if ((Native.GetWindowLong(handle, -20) & 0x08000000) == 0) throw new Exception("No-activation style missing");
            results["noActivation"] = "window style verified";
            bubble.RenderPreview(Path.Combine(Program.Folder, "preview.png"));
            using (var icon = Controller.MakeIcon())
            using (var file = File.Create(Path.Combine(Program.Folder, "LightTranslate.ico"))) icon.Save(file);
            results["bubblePreview"] = "rendered";
            File.WriteAllText(Path.Combine(Program.Folder, "self-test.json"), new JavaScriptSerializer().Serialize(results), Encoding.UTF8);
            Program.Log("self-test", "passed");
            return 0;
        }
    }
}
