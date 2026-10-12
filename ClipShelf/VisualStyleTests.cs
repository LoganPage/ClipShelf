using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

// Verifies the independent Classic/Modern visual-style system: persistence, legacy settings,
// token swaps, light/dark independence, row emphasis, readable contrast and the settings control.
internal static class VisualStyleTests
{
    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in FindAll<T>(child)) yield return nested;
        }
    }

    private static double Luminance(Color color)
    {
        static double Channel(byte value) { double c = value / 255d; return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(color.R) + 0.7152 * Channel(color.G) + 0.0722 * Channel(color.B);
    }

    private static double Contrast(string foreground, string background)
    {
        double a = Luminance(Brush(foreground)), b = Luminance(Brush(background));
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }

    private static Color Brush(string key)
    {
        var color = ((SolidColorBrush)Application.Current.Resources[key]).Color;
        return color.A == 255 ? color : Color.FromRgb(color.R, color.G, color.B);
    }

    internal static async Task RunAsync(string report)
    {
        var checks = new List<string>();
        MainWindow? window = null; string? error = null;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        try
        {
            string root = Path.Combine(Path.GetTempPath(), "ClipShelf-visual-style-" + Guid.NewGuid().ToString("N"));

            // Settings compatibility: absent / unknown values are Classic and nothing else is lost.
            var legacy = new HistoryStore(Path.Combine(root, "legacy"));
            File.WriteAllText(legacy.SettingsPath, "{\"Theme\":\"Dark\",\"MaxItems\":42,\"CloseToTray\":false}");
            var legacyReopened = new HistoryStore(legacy.DirectoryPath).Settings;
            Check(legacyReopened.VisualStyle == "Classic" && legacyReopened.Theme == "Dark" && legacyReopened.MaxItems == 42 && !legacyReopened.CloseToTray,
                "Legacy settings.json without VisualStyle loads as Classic with no data loss");
            File.WriteAllText(legacy.SettingsPath, "{\"VisualStyle\":\"Modern\",\"Theme\":\"Light\"}");
            Check(new HistoryStore(legacy.DirectoryPath).Settings.VisualStyle == "Modern", "Modern settings.json loads");
            Check(ThemeManager.NormalizeVisualStyle("Neon") == "Classic" && ThemeManager.NormalizeVisualStyle(null) == "Classic"
                && ThemeManager.NormalizeVisualStyle("modern") == "Modern", "Unknown or missing style values normalise predictably");

            var fixture = Path.Combine(root, "live");
            var store = new HistoryStore(fixture, deferredPersistence: true); store.Settings.Theme = "Light";
            for (int i = 0; i < 4; i++) store.Add(new ClipItem { Text = "Visual style synthetic record " + i });
            window = new MainWindow(store, demo: true) { ShowInTaskbar = false, Width = 1000, Height = 760 };
            window.Show(); window.OpenSettings();
            async Task Idle() => await window.Dispatcher.InvokeAsync(() => { window.UpdateLayout(); }, DispatcherPriority.ApplicationIdle);
            await Idle();
            var settingsPanel = (SettingsPanel)((ContentControl)window.FindName("SettingsContent")).Content;
            var classicButton = FindAll<Button>(settingsPanel).First(b => b.Name == "VisualStyleClassicButton");
            var modernButton = FindAll<Button>(settingsPanel).First(b => b.Name == "VisualStyleModernButton");
            Check(classicButton.Content?.ToString() == "经典" && modernButton.Content?.ToString() == "现代", "Settings → Appearance offers 经典 / 现代");

            // Classic must keep the historical geometry exactly.
            Check(store.Settings.VisualStyle == "Classic" && ThemeManager.VisualStyle == "Classic", "Default style is Classic");
            foreach (string theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme; window.ApplyPreferences(appearanceOnly: true); await Task.Delay(260); await Idle();
                var res = Application.Current.Resources;
                Check((CornerRadius)res["ControlCorners"] == new CornerRadius(4) && (CornerRadius)res["OverlayCorners"] == new CornerRadius(8)
                    && (double)res["RowAccentWidth"] == 0 && (double)res["RowActionRestOpacity"] == 1 && (double)res["BrandStripHeight"] == 0,
                    $"Classic/{theme}: geometry and emphasis tokens are neutral");
                Check(ReferenceEquals(res["RowFocusMarkerBrush"], res["MutedBrush"]), $"Classic/{theme}: focus marker remains the shared muted brush");
            }

            // Click the setting: style switches immediately and persists, theme is untouched.
            store.Settings.Theme = "Light"; window.ApplyPreferences(appearanceOnly: true); await Task.Delay(260);
            modernButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Idle();
            Check(store.Settings.VisualStyle == "Modern" && store.Settings.Theme == "Light" && ThemeManager.VisualStyle == "Modern",
                "Choosing 现代 applies immediately and leaves light/dark unchanged");
            await store.FlushAsync();
            var persisted = new HistoryStore(fixture).Settings;
            Check(persisted.VisualStyle == "Modern" && persisted.Theme == "Light", "Modern style persists across restart");
            Check(((SolidColorBrush)window.FindResource("AccentBrush")).Color != Color.FromRgb(0x28, 0x70, 0xDB), "Modern changes the brand accent");

            window.CloseSettings(); await Idle();
            var list = (HistoryListBox)window.FindName("HistoryList");
            foreach (string theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme; window.ApplyPreferences(appearanceOnly: true); await Task.Delay(260); await Idle();
                var res = Application.Current.Resources;
                Check((CornerRadius)res["ControlCorners"] == new CornerRadius(8) && (CornerRadius)res["RowCorners"] == new CornerRadius(10)
                    && (double)res["RowAccentWidth"] == 3 && (double)res["RowActionRestOpacity"] < 1 && (double)res["BrandStripHeight"] > 0,
                    $"Modern/{theme}: geometry and emphasis tokens apply");
                var strip = (Border)window.FindName("BrandStrip");
                Check(strip.ActualHeight > 0 && ((SolidColorBrush)strip.Background).Color == ((SolidColorBrush)res["AccentBrush"]).Color, $"Modern/{theme}: title bar carries the brand strip");
                Check(((Border)window.FindName("SearchBorder")).CornerRadius == new CornerRadius(12), $"Modern/{theme}: search uses the modern radius");

                list.UnselectAll(); await Idle();
                var first = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
                var second = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(1);
                Button Action(ListBoxItem row, string name) => FindAll<Button>(row).First(b => b.Name == name);
                Check(Action(second, "RowCopyButton").Opacity < 1 && Action(second, "RowDeleteButton").Opacity < 1 && Action(second, "RowPinButton").Opacity < 1,
                    $"Modern/{theme}: inactive row actions recede");
                list.SelectedIndex = 0; await Idle(); await Task.Delay(120);
                Check(Action(first, "RowCopyButton").Opacity == 1 && Action(first, "RowDeleteButton").Opacity == 1,
                    $"Modern/{theme}: selected row actions are fully emphasised");
                var accent = FindAll<Border>(first).First(b => b.Name == "RowAccent");
                Check(accent.Opacity == 1 && accent.ActualWidth == 3, $"Modern/{theme}: selected row shows the accent edge");
                var unselectedAccent = FindAll<Border>(second).First(b => b.Name == "RowAccent");
                Check(unselectedAccent.Opacity == 0, $"Modern/{theme}: unselected rows hide the accent edge");

                // Readability of the token pairs used by the new surfaces.
                foreach (var (fg, bg, minimum) in new[] {
                    ("TextBrush", "SurfaceBrush", 7.0), ("TextBrush", "BackgroundBrush", 7.0), ("MutedBrush", "SurfaceBrush", 4.5), ("MutedBrush", "BackgroundBrush", 4.5),
                    ("AccentBrush", "SurfaceBrush", 4.5), ("BrandTextBrush", "BackgroundBrush", 4.5), ("FilterSelectedTextBrush", "FilterSelectionBrush", 4.5),
                    ("SelectionBadgeTextBrush", "SelectionBadgeBrush", 4.5), ("ToastTextBrush", "ToastBrush", 7.0), ("TextBrush", "RowSelectedBrush", 7.0),
                    ("TextBrush", "MenuSurfaceBrush", 7.0), ("TextBrush", "MenuHoverBrush", 7.0), ("SectionTitleBrush", "SettingsCardBrush", 4.5),
                    ("SettingsSecondaryBrush", "SettingsCardBrush", 4.5), ("TextTileForeground", "TextTileBrush", 4.5), ("EmptyIconBrush", "EmptyIconTileBrush", 3.0) })
                    Check(Contrast(fg, bg) >= minimum, $"Modern/{theme}: {fg} on {bg} contrast is {Contrast(fg, bg):0.0}:1 (minimum {minimum})");
                Check(Contrast("TextBrush", "SurfaceBrush") > 7, $"Modern/{theme}: body text is high contrast");
            }

            // Back to Classic through the same control, with the other theme: no residue.
            store.Settings.Theme = "Dark"; window.OpenSettings(); await Idle();
            settingsPanel = (SettingsPanel)((ContentControl)window.FindName("SettingsContent")).Content;
            FindAll<Button>(settingsPanel).First(b => b.Name == "VisualStyleClassicButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await Task.Delay(260); await Idle();
            Check(store.Settings.VisualStyle == "Classic" && (CornerRadius)Application.Current.Resources["ControlCorners"] == new CornerRadius(4)
                && (double)Application.Current.Resources["BrandStripHeight"] == 0 && ((Border)window.FindName("SearchBorder")).CornerRadius == new CornerRadius(4),
                "Returning to 经典 restores the original geometry immediately");
        }
        catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        window?.Quit(); Application.Current.Shutdown(error is null ? 0 : 1);
    }
}
