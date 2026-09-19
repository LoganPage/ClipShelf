using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

internal static class ToolTipPresentationTests
{
    internal static async Task RunAsync(string root)
    {
        Directory.CreateDirectory(root); Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>(); string? error = null;
        var samples = new StackPanel { Margin = new Thickness(20) };
        var canvas = new Grid { Width = 420, Height = 260 }; canvas.SetResourceReference(Panel.BackgroundProperty, "BackgroundBrush"); canvas.Children.Add(samples);
        var window = new Window { Content = canvas, Width = 460, Height = 300, Left = -12000, Top = -12000, ShowActivated = false, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        void Check(bool value, string name) { if (!value) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            window.Show();
            foreach (string theme in new[] { "Light", "Dark" })
            {
                ThemeManager.Apply(new AppSettings { Theme = theme }); samples.Children.Clear();
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); await Task.Delay(300);
                var bitmap = new RenderTargetBitmap(840, 600, 192, 192, PixelFormats.Pbgra32);
                var background = new DrawingVisual(); using (var dc = background.RenderOpen()) dc.DrawRectangle((Brush)Application.Current.FindResource("BackgroundBrush"), null, new Rect(0, 0, 420, 300)); bitmap.Render(background);
                double y = 10;
                foreach (string content in new[] { "置顶此记录", "选择截图文件夹", "复制选中的 12 条记录 · Ctrl+C", new string('长', 70) })
                {
                    var tip = new ToolTip { Content = content, PlacementTarget = window, IsOpen = true };
                    try {
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
                    Check(tip.Template.FindName("TipSurface", tip) is Border { CornerRadius.TopLeft: 8 }, theme + ": implicit tooltip uses rounded Fluent template");
                    Check(tip.VerticalOffset + 8 == 4 && tip.HorizontalOffset + 8 == 0, theme + ": visible surface sits 4 DIP below the target with aligned left edge, excluding shadow padding");
                    Check(tip.ActualWidth <= 360 && !tip.Focusable && !tip.HasDropShadow, theme + ": bounded width, no focus stealing or duplicate native shadow");
                    Check(ReferenceEquals(tip.Background, Application.Current.FindResource("MenuSurfaceBrush")), theme + ": tooltip follows active theme resources");
                    var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) dc.DrawRectangle(new VisualBrush(tip), null, new Rect(12, y, tip.ActualWidth, tip.ActualHeight)); bitmap.Render(visual); y += tip.ActualHeight + 4;
                    } finally { tip.IsOpen = false; }
                }
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(root, theme + ".png")); encoder.Save(output);
            }
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally { window.Close(); File.WriteAllText(Path.Combine(root, "results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
}
