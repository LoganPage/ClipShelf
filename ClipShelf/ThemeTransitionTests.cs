using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ClipShelf;

internal static class ThemeTransitionTests
{
    internal static async Task Run(string report)
    {
        var checks = new List<string>(); var timings = new List<double>();
        MainWindow? window = null; string? error = null;
        void Check(bool value, string label) { if (!value) throw new Exception(label); checks.Add(label); }
        try {
            var fixture = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ClipShelf-theme-" + Guid.NewGuid().ToString("N"));
            var store = new HistoryStore(fixture, deferredPersistence: true); store.Settings.Theme = "Light";
            store.Add(new ClipItem { Text = "Theme transition synthetic record" });
            window = new MainWindow(store, demo: true) { ShowInTaskbar = false, Width = 1000, Height = 760 };
            window.Show(); window.OpenSettings();
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var settings = ((ContentControl)window.FindName("SettingsContent")).Content;
            Check(((Ellipse)window.FindName("WindowFocusLight")).Width == 10, "Focus light is 10 DIP (1.25 times original)");
            foreach (var theme in new[] { "Dark", "Light", "Dark", "Light" }) {
                var watch = Stopwatch.StartNew(); store.Settings.Theme = theme; window.ApplyPreferences(); watch.Stop(); timings.Add(watch.Elapsed.TotalMilliseconds);
                Check(ThemeTransition.ActiveCount <= 8, theme + ": only flat surface colors animate");
                Check(new[] { "TextBrush", "MutedBrush", "AccentBrush", "TextTileBrush", "SelectedBrush" }.All(key => Application.Current.Resources[key] is SolidColorBrush b && b.IsFrozen && !b.HasAnimatedProperties), theme + ": text, icons and cached row brushes do not animate");
                Check(ReferenceEquals(settings, ((ContentControl)window.FindName("SettingsContent")).Content), theme + ": settings tree is retained");
                await Task.Delay(70);
            }
            await Task.Delay(350);
            Check(ThemeTransition.ActiveCount == 0 && Application.Current.Resources.Values.OfType<SolidColorBrush>().All(b => b.IsFrozen), "Finished transition releases animation clocks and freezes colors");
            window.ApplyPreferences(); Check(ThemeTransition.ActiveCount == 0, "Unchanged theme does not capture or animate");
            Check(((SolidColorBrush)Application.Current.Resources["BackgroundBrush"]).Color.ToString() == "#FFF6F7F9", "Final palette is exact light theme");
            store.Settings.CloseToTray = false; store.SaveSettings(); await store.FlushAsync();
            Check(!new HistoryStore(fixture).Settings.CloseToTray, "Exit behavior survives deferred persistence and reload");
        } catch (Exception ex) { error = ex.ToString(); }
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = error is null, checks, timings, error, scope = "Synthetic WPF regression; timings are synchronous switch work, not displayed frame rate." }, new JsonSerializerOptions { WriteIndented = true }));
        window?.Quit(); Application.Current.Shutdown(error is null ? 0 : 1);
    }
}
