using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipShelf;

internal static class WindowPositionTests
{
    internal static async Task RunAsync(string directory)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(directory); var checks = new List<string>(); string? error = null; Window? window = null;
        try
        {
            var area = Forms.Screen.PrimaryScreen?.WorkingArea ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
            int targetX = area.Left + 96, targetY = area.Top + 84;
            window = new Window { Width = 680, Height = 552, WindowStartupLocation = WindowStartupLocation.CenterScreen,
                WindowStyle = WindowStyle.None, ShowActivated = false, ShowInTaskbar = false, Opacity = .01 };
            window.SourceInitialized += (_, _) => SetWindowPos(new WindowInteropHelper(window).Handle, IntPtr.Zero,
                targetX, targetY, 0, 0, 0x0001 | 0x0004 | 0x0010);
            window.Show(); await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            Check(GetWindowRect(new WindowInteropHelper(window).Handle, out NativeRect moved) && moved.Left == targetX && moved.Top == targetY,
                "A SourceInitialized native move overrides CenterScreen", checks);
            Check(Math.Abs(window.ActualWidth - 680) < 1 && Math.Abs(window.ActualHeight - 552) < 1,
                "Position restoration leaves the startup size unchanged", checks);
            var synthetic = new[] { new Rect(0, 0, 1920, 1040), new Rect(1920, -200, 2560, 1440) };
            Check(WindowPositionPolicy.IsReachable(2300, -100, 680, synthetic), "Injected secondary monitor accepts a reachable title bar", checks);
            Check(!WindowPositionPolicy.IsReachable(7000, 0, 680, synthetic), "Removed-monitor coordinates fall back instead of restoring off-screen", checks);
            window.Close(); window = null;
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        finally { window?.Close(); }
        File.WriteAllText(Path.Combine(directory, "window-position-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }
    private static void Check(bool condition, string name, ICollection<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name); checks.Add("PASS " + name);
    }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rectangle);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
}
