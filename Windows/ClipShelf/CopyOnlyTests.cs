using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>Own-app regression checks. No input injection or clipboard writes.</summary>
public static class CopyOnlyTests
{
    private static MainWindow Fixture(string directory)
    {
        var store = new HistoryStore(Path.Combine(directory, "fixture-" + Guid.NewGuid().ToString("N")));
        store.Settings.HistoryEnabled = store.Settings.WatchScreenshots = store.Settings.LaunchAtLogin = false;
        for (int i = 0; i < 2; i++)
        {
            var item = new ClipItem { Kind = ClipKind.Image, Title = $"独立图片验证 {i + 1}", CreatedAt = DateTimeOffset.Now.AddMinutes(-i) };
            item.ImagePath = store.ImagePathFor(item.Id);
            var pixels = Enumerable.Range(0, 320 * 180).SelectMany(_ => new byte[] { (byte)(80 + i * 80), 150, 40, 255 }).ToArray();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(320, 180, 96, 96, PixelFormats.Bgra32, null, pixels, 320 * 4)));
            using (var stream = File.Create(item.ImagePath)) encoder.Save(stream);
            store.Add(item);
        }
        store.Add(new ClipItem { Title = "独立文字验证", Text = "Synthetic clipboard-free record", CreatedAt = DateTimeOffset.Now.AddMinutes(-3) });
        return new MainWindow(store, demo: true) { Width = 900, Height = 700, Title = "ClipShelf · 复制与预览验证" };
    }

    public static void RunDemo(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var window = Fixture(directory); Application.Current.MainWindow = window;
        window.PreviewKeyDown += (_, e) => { if (e.Key == Key.F12) { e.Handled = true; window.Quit(); } };
        window.Show();
    }

    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>(); string? error = null; MainWindow? window = null;
        void Check(bool condition, string description) { if (!condition) throw new InvalidOperationException(description); checks.Add(description); }
        async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
        try
        {
            var assembly = typeof(MainWindow).Assembly;
            Check(!assembly.GetTypes().Any(type => type.Name.Contains("Paste", StringComparison.Ordinal)), "No automatic-paste implementation or legacy paste fixture remains in the assembly");
            Check(!typeof(NativeMethods).GetMethods(BindingFlags.Static | BindingFlags.NonPublic).Any(method => method.Name is "SendInput" or "SetWinEventHook" or "GetAsyncKeyState"), "No native input injection, target tracking hook, or modifier polling API remains");
            Check(!typeof(WindowsIntegration).GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Any(member => member.Name.Contains("Foreground", StringComparison.Ordinal) || member.Name.Contains("Paste", StringComparison.Ordinal)), "Clipboard integration has no external foreground state or paste entry point");
            window = Fixture(directory); window.Left = window.Top = -12000;
            window.WindowStartupLocation = WindowStartupLocation.Manual; window.ShowInTaskbar = false;
            window.Show(); await window.PendingSearch; await Idle();
            var list = (HistoryListBox)window.FindName("HistoryList");
            var search = (TextBox)window.FindName("SearchBox");
            var copy = (Button)window.FindName("CopyButton");
            Check(window.Integration is null && window.FindName("PasteButton") is null, "The isolated main window has no paste toolbar button");
            Check(Descendants<Button>(window).All(button => !AutomationProperties.GetName(button).Contains("粘贴") && button.Tag as string != "Paste"), "No toolbar or realized row exposes an automatic-paste action");
            window.ApplyRowSelection(0, ModifierKeys.None); await Idle();
            var row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(0);
            Check(Descendants<Button>(row).Select(button => button.Tag as string).OrderBy(tag => tag).SequenceEqual(new[] { "Copy", "Delete", "Pin" }), "Rows contain only pin, copy, and delete actions");
            var selected = list.SelectedItems.Cast<ClipItem>().Select(item => item.Id).ToArray();
            uint sequence = NativeMethods.GetClipboardSequenceNumber();
            var pending = window.PendingSearch;
            var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(MainWindow).GetProperty("PendingSearch", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(window, gate.Task);
            try
            {
                foreach (var origin in new DependencyObject[] { list, row, search })
                    foreach (var (key, modifiers) in new[] { (Key.Enter, ModifierKeys.None), (Key.V, ModifierKeys.Control) })
                    {
                        var command = window.HandleHistoryKeyAsync(key, modifiers, origin);
                        Check(command.IsCompleted && !await command, $"{origin.GetType().Name}: {modifiers}+{key} is not an app action, even during a pending search");
                    }
            }
            finally
            {
                gate.SetResult();
                typeof(MainWindow).GetProperty("PendingSearch", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.SetValue(window, pending);
            }
            Check(sequence == NativeMethods.GetClipboardSequenceNumber() && selected.SequenceEqual(list.SelectedItems.Cast<ClipItem>().Select(item => item.Id)) && window.IsVisible && window.WindowState == WindowState.Normal, "Legacy shortcuts do not alter clipboard, selection, or window visibility");
            for (int i = 0; i < 3; i++)
            {
                // Reproduce changing selection after a toolbar button owned focus.
                copy.Focus(); window.ApplyRowSelection(i, ModifierKeys.None);
                row = (ListBoxItem)list.ItemContainerGenerator.ContainerFromIndex(i); row.Focus();
                sequence = NativeMethods.GetClipboardSequenceNumber();
                Check(await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, row), $"Record {i + 1}: Space is handled as preview");
                var preview = window.OwnedWindows.OfType<PreviewWindow>().Single();
                await Idle(); await preview.PendingRender;
                Check(preview.IsVisible && preview.Session.Error is null && (i < 2 ? preview.Session.Presented?.Image.PixelWidth == 320 : preview.Session.PresentedText?.Text == "Synthetic clipboard-free record"), $"Record {i + 1}: Space displays image/text without pasting");
                Check(sequence == NativeMethods.GetClipboardSequenceNumber() && window.IsVisible && window.WindowState == WindowState.Normal, $"Record {i + 1}: preview never writes the clipboard or hides/minimizes the shelf");
                preview.Close(); await Task.Delay(210); await preview.Cleanup; await Idle();
            }
            var menu = window.BuildHistoryContextMenu((ClipItem)list.Items[1]);
            Check(menu.Items.OfType<MenuItem>().Count() == 5 && menu.Items.OfType<MenuItem>().All(item => item.InputGestureText != "Ctrl+V" && !(item.Header?.ToString() ?? "").Contains("粘贴")), "Record menu has five labelled actions without paste");
            Check(!window.HandleHistoryMenuKey(menu, Key.V, ModifierKeys.Control), "Ctrl+V in the record menu cannot dispatch a stale paste command");
            Check(await window.HandleHistoryKeyAsync(Key.C, ModifierKeys.Control, row), "Ctrl+C still dispatches the selected record's copy command");
            Check(((TextBlock)window.FindName("ToastText")).Text == "界面预览模式" && window.IsVisible && window.WindowState == WindowState.Normal, "Isolated copy reaches the expected demo guard and keeps the window open");
            Check(!await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, search), "Search owns literal spaces instead of opening previews");
            Check(!await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, copy), "An actual focused button retains standard Space activation without list-command fallthrough");
        }
        catch (Exception ex) { error = ex.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "copy-only-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error,
                scope = "Own-process WPF handlers and visual tree; generated images and isolated history; clipboard sequence read only. No synthetic system input, native clipboard writes, or claims about another app accepting content." }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit(); if (window is null) Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { var child = VisualTreeHelper.GetChild(root, i); if (child is T match) yield return match; foreach (var nested in Descendants<T>(child)) yield return nested; }
    }
}
