using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Forms = System.Windows.Forms;

namespace LightTranslate
{
    // Same hover and pointer-away behavior as the original controller, with a configurable deadline.
    internal sealed class BubbleLifetime
    {
        private DateTime shownAt, awayAt;
        private int seconds;
        internal static int DisplaySeconds(int configuredSeconds, int automaticSeconds, bool translated)
        { return translated && configuredSeconds > 0 ? configuredSeconds : automaticSeconds; }
        internal void Start(DateTime now, int displaySeconds)
        { shownAt = now; awayAt = DateTime.MinValue; seconds = displaySeconds; }
        internal void Reset() { awayAt = DateTime.MinValue; }
        internal bool ShouldDismiss(DateTime now, bool near, bool over)
        {
            if (over) { shownAt = now; awayAt = DateTime.MinValue; return false; }
            if (!near)
            {
                if (awayAt == DateTime.MinValue) awayAt = now;
                if ((now - awayAt).TotalMilliseconds > 550 && (now - shownAt).TotalMilliseconds > 900) return true;
            }
            else awayAt = DateTime.MinValue;
            return (now - shownAt).TotalSeconds > seconds;
        }
    }

    internal static class BubbleAppearance
    {
        internal static FontFamily ResolveFont(string name)
        {
            string requested = (name ?? "").Trim();
            return Fonts.SystemFontFamilies.FirstOrDefault(f => string.Equals(f.Source, requested, StringComparison.OrdinalIgnoreCase))
                ?? new FontFamily("Microsoft YaHei UI");
        }
        internal static bool TryParseBackground(string text, out Color color)
        {
            color = Colors.Transparent;
            if (string.IsNullOrWhiteSpace(text)) return false;
            string value = text.Trim();
            int rgb;
            if (value.Length != 7 || value[0] != '#' || !value.Substring(1).All(Uri.IsHexDigit) || !int.TryParse(value.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out rgb)) return false;
            color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
            return true;
        }
        internal static Color Background(string text)
        {
            Color color;
            if (TryParseBackground(text, out color)) return color;
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                    if (key != null && Convert.ToInt32(key.GetValue("AppsUseLightTheme", 1)) == 0) return Color.FromRgb(37, 40, 47);
            }
            catch { }
            return Color.FromRgb(252, 252, 253);
        }
        private static double Channel(byte value)
        {
            double channel = value / 255.0;
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }
        internal static double Luminance(Color color)
        { return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B); }
        internal static double Contrast(Color first, Color second)
        {
            double a = Luminance(first), b = Luminance(second);
            return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
        }
        internal static Color Foreground(Color background)
        {
            var dark = Color.FromRgb(36, 40, 48);
            var light = Color.FromRgb(237, 239, 243);
            Color preferred = Luminance(background) < 0.35 ? light : dark;
            if (Contrast(background, preferred) >= 4.5) return preferred;
            // Opaque RGB backgrounds guarantee that black or white reaches at least 4.5:1.
            return Contrast(background, Colors.Black) >= Contrast(background, Colors.White) ? Colors.Black : Colors.White;
        }
        internal static Color Border(Color background)
        { return Luminance(background) < 0.35 ? Color.FromArgb(38, 255, 255, 255) : Color.FromArgb(35, 40, 48, 64); }
    }

    internal sealed class ReadingSettingsWindow : Window
    {
        private const string Automatic = "自动（按译文长度）";
        private readonly Settings settings;
        private readonly Action saved;
        private readonly ComboBox fontFamily, fontSize, autoHide;
        private readonly TextBox background, englishPercent;
        private readonly TextBlock error, previewText;
        private readonly Border preview;
        private readonly Grid layout;

        internal ReadingSettingsWindow(Settings settings, Action saved)
        {
            this.settings = settings;
            this.saved = saved;
            Title = "轻译 · 阅读设置";
            Width = 560; Height = 760; MinWidth = 460; MinHeight = 450;
            MaxHeight = Math.Max(450, SystemParameters.WorkArea.Height - 40);
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            FontFamily = new FontFamily("Microsoft YaHei UI"); FontSize = 14;
            Background = Brushes.White;
            layout = new Grid { Background = Brushes.White };
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = layout;
            var header = new StackPanel { Margin = new Thickness(24, 20, 24, 16) };
            header.Children.Add(new TextBlock { Text = "阅读设置", FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Ink() });
            header.Children.Add(Help("保存后立即生效。预览仅使用示例文字。"));
            layout.Children.Add(header);

            var form = new StackPanel { Margin = new Thickness(24, 0, 24, 16) };
            var scroll = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            Grid.SetRow(scroll, 1); layout.Children.Add(scroll);
            form.Children.Add(Heading("浮层外观"));
            fontFamily = new ComboBox { ItemsSource = Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(x => x).ToArray(), MinHeight = 32 };
            AddField(form, "字体", fontFamily);
            fontSize = new ComboBox { IsEditable = true, ItemsSource = new[] { "10", "12", "14", "16", "18", "20", "24", "32", "48" }, MinHeight = 32 };
            AddField(form, "字号", fontSize);
            background = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
            var colorRow = new Grid();
            colorRow.ColumnDefinitions.Add(new ColumnDefinition());
            colorRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            colorRow.Children.Add(background);
            var pick = Button("选色…"); pick.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(pick, 1);
            AutomationProperties.SetName(pick, "选择浮层背景色");
            pick.Click += delegate { PickColor(); }; colorRow.Children.Add(pick);
            AddField(form, "背景色", colorRow);
            AutomationProperties.SetName(background, "浮层背景色");
            form.Children.Add(Help("留空跟随系统明暗主题，或填写 #RRGGBB。文字颜色自动保持对比度。"));
            previewText = new TextBlock { Text = "译文会使用这个字体和背景。\nSelect text to translate.", TextWrapping = TextWrapping.Wrap };
            preview = new Border
            {
                Padding = new Thickness(15, 10, 15, 11), Margin = new Thickness(0, 12, 0, 20), CornerRadius = new CornerRadius(11),
                BorderThickness = new Thickness(1), MinHeight = 82, MaxHeight = 180,
                Child = new ScrollViewer { Content = previewText, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled }
            };
            AutomationProperties.SetName(preview, "浮层外观示例预览"); form.Children.Add(preview);

            form.Children.Add(Heading("显示与触发"));
            autoHide = new ComboBox { IsEditable = true, ItemsSource = new[] { Automatic, "3", "6", "10", "15", "30", "60", "120" }, MinHeight = 32 };
            AddField(form, "自动消失（秒）", autoHide);
            form.Children.Add(Help("译文自动停留 6–22 秒，也可填 1–120 秒。悬停时续时，移远或继续操作仍会提前收起；等待和错误提示保留原时长。"));
            englishPercent = new TextBox { MinHeight = 32, VerticalContentAlignment = VerticalAlignment.Center };
            AddField(form, "最低英文占比（%）", englishPercent);
            form.Children.Add(Help("0–100%。A–Z/a–z 字母数 ÷ 所有文字字母数；中文及其他语言计入分母，标点、空白、数字不计。Hello中文约为 71%。"));
            form.Children.Add(Help("0% 关闭比例限制，保留原有语言过滤；纯中文和没有字母的选区仍不触发。大于 0% 时必须含 A–Z/a–z。"));
            AutomationProperties.SetHelpText(fontSize, "范围 10 到 48，默认 14，随系统缩放");
            AutomationProperties.SetHelpText(background, "留空跟随系统主题，或输入不透明六位十六进制颜色 #RRGGBB");
            AutomationProperties.SetHelpText(autoHide, "自动，或 1 到 120 秒；鼠标悬停保留浮层");
            AutomationProperties.SetHelpText(englishPercent, "范围 0 到 100，分母为全部 Unicode 字母，不含空白、数字和标点");

            var footer = new StackPanel { Margin = new Thickness(24, 12, 24, 18) };
            error = new TextBlock { Foreground = new SolidColorBrush(Color.FromRgb(180, 35, 24)), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Visibility = Visibility.Collapsed };
            AutomationProperties.SetName(error, "设置错误"); footer.Children.Add(error);
            var actions = new Grid(); actions.ColumnDefinitions.Add(new ColumnDefinition()); actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var reset = Button("恢复阅读默认值"); reset.HorizontalAlignment = HorizontalAlignment.Left;
            reset.Click += delegate { Fill(new Settings()); error.Visibility = Visibility.Collapsed; }; actions.Children.Add(reset);
            var right = new StackPanel { Orientation = Orientation.Horizontal };
            var cancel = Button("取消"); cancel.Click += delegate { Close(); }; right.Children.Add(cancel);
            var save = Button("保存"); save.IsDefault = true; save.Margin = new Thickness(8, 0, 0, 0);
            save.Background = new SolidColorBrush(Color.FromRgb(56, 112, 210)); save.Foreground = Brushes.White;
            save.Click += delegate { Apply(Path.Combine(Program.Folder, "settings.json")); }; right.Children.Add(save);
            Grid.SetColumn(right, 1); actions.Children.Add(right); footer.Children.Add(actions);
            Grid.SetRow(footer, 2); layout.Children.Add(footer);
            fontFamily.SelectionChanged += delegate { UpdatePreview(); };
            fontSize.AddHandler(System.Windows.Controls.Primitives.TextBoxBase.TextChangedEvent, new TextChangedEventHandler(delegate { UpdatePreview(); }));
            background.TextChanged += delegate { UpdatePreview(); };
            PreviewKeyDown += delegate(object sender, KeyEventArgs e) { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
            Fill(settings);
        }
        private static Brush Ink() { return new SolidColorBrush(Color.FromRgb(36, 40, 48)); }
        private static TextBlock Help(string text)
        { return new TextBlock { Text = text, Foreground = new SolidColorBrush(Color.FromRgb(83, 91, 105)), FontSize = 12, LineHeight = 19, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 5, 0, 0) }; }
        private static TextBlock Heading(string text)
        { return new TextBlock { Text = text, FontWeight = FontWeights.SemiBold, Foreground = Ink(), Margin = new Thickness(0, 0, 0, 10) }; }
        private static Button Button(string text)
        { return new Button { Content = text, Padding = new Thickness(14, 7, 14, 7), MinHeight = 32, MinWidth = 64 }; }
        private static void AddField(Panel form, string label, FrameworkElement control)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 10) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) }); row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new Label { Content = label, Target = control, Padding = new Thickness(0, 5, 8, 0), Foreground = Ink() });
            AutomationProperties.SetName(control, label); Grid.SetColumn(control, 1); row.Children.Add(control); form.Children.Add(row);
        }
        internal void Fill(Settings value)
        {
            if (error != null) error.Visibility = Visibility.Collapsed;
            fontFamily.SelectedItem = BubbleAppearance.ResolveFont(value.BubbleFontFamily).Source;
            fontSize.Text = value.BubbleFontSize.ToString(CultureInfo.CurrentCulture);
            background.Text = value.BubbleBackground;
            autoHide.Text = value.AutoHideSeconds == 0 ? Automatic : value.AutoHideSeconds.ToString(CultureInfo.CurrentCulture);
            englishPercent.Text = value.MinEnglishPercent.ToString(CultureInfo.CurrentCulture);
            UpdatePreview();
        }
        private void UpdatePreview()
        {
            if (preview == null) return;
            double size;
            if (!double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) || size < 10 || size > 48 || double.IsNaN(size)) size = 14;
            var color = BubbleAppearance.Background(background.Text);
            previewText.FontFamily = BubbleAppearance.ResolveFont(fontFamily.SelectedItem as string);
            previewText.FontSize = size; previewText.LineHeight = size * 23 / 14;
            previewText.Foreground = new SolidColorBrush(BubbleAppearance.Foreground(color));
            preview.Background = new SolidColorBrush(color); preview.BorderBrush = new SolidColorBrush(BubbleAppearance.Border(color));
        }
        private void PickColor()
        {
            using (var dialog = new Forms.ColorDialog { FullOpen = true })
            {
                Color color;
                if (BubbleAppearance.TryParseBackground(background.Text, out color)) dialog.Color = System.Drawing.Color.FromArgb(color.R, color.G, color.B);
                var handle = new System.Windows.Interop.WindowInteropHelper(this).Handle;
                var owner = new Forms.NativeWindow();
                owner.AssignHandle(handle);
                try
                {
                    if (dialog.ShowDialog(owner) == Forms.DialogResult.OK)
                        background.Text = string.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B);
                }
                finally { owner.ReleaseHandle(); }
            }
        }
        internal bool Apply(string path)
        {
            double size; int seconds, percent; Color color;
            if (!double.TryParse(fontSize.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out size) || double.IsNaN(size) || double.IsInfinity(size) || size < 10 || size > 48)
                return Invalid("字号请输入 10–48 之间的数字。", fontSize);
            if (!string.IsNullOrWhiteSpace(background.Text) && !BubbleAppearance.TryParseBackground(background.Text, out color))
                return Invalid("背景色请留空，或填写 #RRGGBB，例如 #25282F。", background);
            if (autoHide.Text.Trim() == Automatic || autoHide.Text.Trim() == "0") seconds = 0;
            else if (!int.TryParse(autoHide.Text.Trim(), out seconds) || seconds < 1 || seconds > 120)
                return Invalid("自动消失时间请选择“自动”，或填写 1–120 秒。", autoHide);
            if (!int.TryParse(englishPercent.Text.Trim(), out percent) || percent < 0 || percent > 100)
                return Invalid("最低英文占比请输入 0–100 的整数百分比。", englishPercent);
            var next = settings.Clone();
            next.BubbleFontFamily = BubbleAppearance.ResolveFont(fontFamily.SelectedItem as string).Source;
            next.BubbleFontSize = size; next.BubbleBackground = background.Text.Trim().ToUpperInvariant();
            next.AutoHideSeconds = seconds; next.MinEnglishPercent = percent;
            if (!next.TrySave(path)) return Invalid("无法保存设置。请检查应用目录是否可写；当前设置未改变。", null);
            settings.CopyReadingFrom(next);
            if (saved != null) saved();
            Close(); return true;
        }
        private bool Invalid(string message, Control control)
        {
            error.Text = message; error.Visibility = Visibility.Visible;
            AutomationProperties.SetHelpText(this, message);
            if (IsVisible && control != null) control.Focus();
            return false;
        }
        internal void RenderPreview(string file)
        {
            layout.Measure(new Size(540, 725)); layout.Arrange(new Rect(0, 0, 540, 725)); layout.UpdateLayout();
            var bitmap = new RenderTargetBitmap(540, 725, 96, 96, PixelFormats.Pbgra32); bitmap.Render(layout);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using (var stream = File.Create(file)) encoder.Save(stream);
        }
        internal void SetInputForTest(string size, string color, string seconds, string percent)
        { fontSize.Text = size; background.Text = color; autoHide.Text = seconds; englishPercent.Text = percent; }
    }
}
