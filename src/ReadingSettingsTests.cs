using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows.Media;

namespace LightTranslate
{
    internal static class ReadingSettingsTests
    {
        private static void Check(bool condition, string message)
        { if (!condition) throw new Exception("Reading settings: " + message); }
        internal static void Run(Dictionary<string, object> results)
        {
            // The 0% default deliberately retains the old non-Han language guard.
            Check(SelectionReader.ShouldTranslate("Hello中文", 0), "mixed default");
            Check(SelectionReader.ShouldTranslate("Привет", 0), "legacy non-English default");
            Check(!SelectionReader.ShouldTranslate("纯中文", 0), "Chinese-only default");
            Check(!SelectionReader.ShouldTranslate(null, 0) && !SelectionReader.ShouldTranslate(" ", 0), "empty selection");
            Check(!SelectionReader.ShouldTranslate("123 !? 😀", 0) && !SelectionReader.ShouldTranslate("123 !? 😀", 50), "no letters");
            Check(SelectionReader.ShouldTranslate("abc中文", 60), "exact threshold equality");
            Check(!SelectionReader.ShouldTranslate("abc中文", 61), "below threshold");
            Check(SelectionReader.ShouldTranslate("abc中文！？ 12345 😀", 60), "ignore punctuation, spaces, numbers and emoji");
            Check(SelectionReader.ShouldTranslate("a\U00020000", 50) && !SelectionReader.ShouldTranslate("a\U00020000", 51), "supplementary Han counted once");
            Check(SelectionReader.ShouldTranslate("e\u0301", 100), "combining marks are not letters");
            Check(!SelectionReader.ShouldTranslate("éПривет中文", 1), "requires ASCII English letters");
            Check(!SelectionReader.ShouldTranslate("ＡＢＣ", 1), "full-width letters are denominator only");
            Check(SelectionReader.ShouldTranslate("English 123!?", 100), "English-only 100 percent");
            Check(!SelectionReader.ShouldTranslate("English中文", 100), "100 percent excludes mixed letters");
            results["englishThreshold"] = "default compatibility, exact boundaries, mixed scripts, supplementary Unicode and non-letter exclusions passed";

            var start = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
            var lifetime = new BubbleLifetime();
            Check(BubbleLifetime.DisplaySeconds(0, 22, true) == 22, "automatic reading duration preserved");
            Check(BubbleLifetime.DisplaySeconds(1, 22, true) == 1, "fixed translated-text duration");
            Check(BubbleLifetime.DisplaySeconds(1, 8, false) == 8 && BubbleLifetime.DisplaySeconds(120, 3, false) == 3, "short reading duration does not cut off pending translation or alter error hints");
            lifetime.Start(start, 1);
            Check(!lifetime.ShouldDismiss(start.AddSeconds(1), true, false), "deadline equality retains original > comparison");
            Check(lifetime.ShouldDismiss(start.AddMilliseconds(1001), true, false), "one second timeout");
            lifetime.Start(start, 3);
            Check(!lifetime.ShouldDismiss(start.AddSeconds(50), false, true), "hover always retains bubble");
            Check(!lifetime.ShouldDismiss(start.AddSeconds(53), true, false), "hover renews full lifetime");
            Check(lifetime.ShouldDismiss(start.AddMilliseconds(53001), true, false), "timeout after hover");
            lifetime.Start(start, 120);
            Check(!lifetime.ShouldDismiss(start.AddMilliseconds(700), false, false), "away grace begins");
            Check(lifetime.ShouldDismiss(start.AddMilliseconds(1251), false, false), "pointer-away still closes early with long timeout");
            lifetime.Start(start, 120);
            Check(!lifetime.ShouldDismiss(start.AddSeconds(1), false, false), "first away event");
            Check(!lifetime.ShouldDismiss(start.AddMilliseconds(1400), true, false), "returning near resets away grace");
            Check(!lifetime.ShouldDismiss(start.AddMilliseconds(1500), false, false), "new away grace");
            Check(lifetime.ShouldDismiss(start.AddMilliseconds(2051), false, false), "new away grace expires");
            results["bubbleLifetime"] = "automatic/fixed reading deadline, unchanged waiting/error durations, hover renewal, pointer-away grace and near-pointer reset passed";

            Color parsed;
            Check(BubbleAppearance.TryParseBackground("#aabbcc", out parsed) && parsed == Color.FromRgb(170, 187, 204), "RGB color parsing");
            foreach (string invalid in new[] { "#ABC", "#80112233", "red", "# 12345", "#XYZABC", "", "https://example.com" })
                Check(!BubbleAppearance.TryParseBackground(invalid, out parsed), "reject invalid color " + invalid);
            double minimumContrast = double.MaxValue;
            for (int r = 0; r <= 255; r += 17)
                for (int g = 0; g <= 255; g += 17)
                    for (int b = 0; b <= 255; b += 17)
                    {
                        var background = Color.FromRgb((byte)r, (byte)g, (byte)b);
                        double contrast = BubbleAppearance.Contrast(background, BubbleAppearance.Foreground(background));
                        minimumContrast = Math.Min(minimumContrast, contrast);
                        Check(contrast >= 4.5, "foreground contrast below 4.5:1");
                    }
            results["backgroundContrast"] = new { samples = 4096, minimumRatio = minimumContrast };

            string directory = Path.Combine(Program.Folder, "self-test-settings-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                string path = Path.Combine(directory, "settings.json");
                File.WriteAllText(path, "{\"Enabled\":false,\"Target\":\"fr\",\"ExcludedApps\":[\"KeepMe\"]}");
                var old = Settings.Load(path);
                Check(!old.Enabled && old.Target == "fr" && old.ExcludedApps[0] == "KeepMe", "legacy fields preserved");
                Check(old.BubbleFontSize == 14 && old.BubbleBackground == "" && old.AutoHideSeconds == 0 && old.MinEnglishPercent == 0, "old file gets compatible defaults");
                old.BubbleFontFamily = "not an installed font"; old.BubbleFontSize = double.NaN;
                old.BubbleBackground = "# 12345"; old.AutoHideSeconds = 121; old.MinEnglishPercent = -1;
                old.NormalizeReading();
                Check(old.BubbleFontFamily == "Microsoft YaHei UI" && old.BubbleFontSize == 14 && old.BubbleBackground == "" && old.AutoHideSeconds == 0 && old.MinEnglishPercent == 0, "invalid values use defaults");
                File.WriteAllText(path, "null"); Check(Settings.Load(path) != null, "JSON null safe");
                File.WriteAllText(path, "{invalid"); Check(Settings.Load(path).BubbleFontSize == 14, "malformed JSON safe");
                File.WriteAllText(path, "{\"BubbleFontSize\":48,\"AutoHideSeconds\":120,\"MinEnglishPercent\":100,\"BubbleBackground\":\"#abcdef\"}");
                var bounds = Settings.Load(path);
                Check(bounds.BubbleFontSize == 48 && bounds.AutoHideSeconds == 120 && bounds.MinEnglishPercent == 100 && bounds.BubbleBackground == "#ABCDEF", "upper bounds and normalization");

                var target = new Settings { Enabled = false, ClipboardFallback = false, Target = "fr", ExcludedApps = new[] { "KeepMe" } };
                int saved = 0;
                var window = new ReadingSettingsWindow(target, delegate { saved++; });
                window.RenderPreview(Path.Combine(Program.Folder, "reading-settings-preview.png"));
                window.SetInputForTest("9", "", "10", "50"); Check(!window.Apply(path), "invalid font blocked");
                window.SetInputForTest("14", "#GGGGGG", "10", "50"); Check(!window.Apply(path), "invalid background blocked");
                window.SetInputForTest("14", "", "121", "50"); Check(!window.Apply(path), "invalid duration blocked");
                window.SetInputForTest("14", "", "10", "101"); Check(!window.Apply(path), "invalid threshold blocked");
                Check(saved == 0 && target.BubbleFontSize == 14 && Settings.Load(path).BubbleFontSize == 48, "validation failure changes neither memory nor file");
                window.SetInputForTest("18", "#25282F", "10", "60");
                Check(!window.Apply(Path.Combine(directory, "missing", "settings.json")), "unwritable destination reported");
                Check(saved == 0 && target.BubbleBackground == "", "write failure does not apply settings");
                window.RenderPreview(Path.Combine(Program.Folder, "reading-settings-error-preview.png"));
                window.Fill(new Settings { BubbleFontFamily = "Segoe UI", BubbleFontSize = 18, BubbleBackground = "#25282F", AutoHideSeconds = 10, MinEnglishPercent = 60 });
                window.RenderPreview(Path.Combine(Program.Folder, "reading-settings-custom-preview.png"));
                Check(window.Apply(path) && saved == 1, "valid GUI save succeeds");
                var roundTrip = Settings.Load(path);
                Check(roundTrip.BubbleFontFamily == "Segoe UI" && roundTrip.BubbleFontSize == 18 && roundTrip.BubbleBackground == "#25282F" && roundTrip.AutoHideSeconds == 10 && roundTrip.MinEnglishPercent == 60, "font family and reading settings persisted");
                Check(!roundTrip.Enabled && !roundTrip.ClipboardFallback && roundTrip.Target == "fr" && roundTrip.ExcludedApps[0] == "KeepMe", "GUI preserves unrelated settings");
                Check(target.BubbleFontSize == 18 && target.MinEnglishPercent == 60, "live settings updated");
                var cancel = new ReadingSettingsWindow(target, null);
                cancel.Fill(new Settings()); cancel.Close();
                Check(target.BubbleFontSize == 18 && Settings.Load(path).MinEnglishPercent == 60, "reset preview and cancel do not save");
                results["readingSettings"] = "legacy load, defaults, invalid values, validation, atomic save, live apply and cancel passed; window previews rendered without showing windows";
            }
            finally { Directory.Delete(directory, true); }
        }
    }
}
