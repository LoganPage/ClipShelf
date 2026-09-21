using System;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ClipShelf;

public partial class MainWindow
{
    private ContextMenu? trayContextMenu;
    private TrayMenuHost? trayMenuHost;
    private long trayMenuRequest;
    private System.Drawing.Icon? trayIcon;
    internal event Action<string>? TrayMenuDiagnostic;

    internal void InitializeTray(string? tooltip = null)
    {
        if (tray is not null) return;
        trayIcon = new System.Drawing.Icon(Path.Combine(AppContext.BaseDirectory, "Assets", "ClipShelf.ico"));
        tray = new Forms.NotifyIcon { Text = tooltip ?? "ClipShelf · 剪贴板历史", Icon = trayIcon, Visible = true };
        // Single-click is immediate. Never bind both Click and DoubleClick, which would
        // activate the shelf twice. No legacy ContextMenuStrip is attached to this icon.
        tray.MouseClick += (_, e) => Dispatcher.Invoke(() => HandleTrayMouseClick(e.Button));
    }

    internal void HandleTrayMouseClick(Forms.MouseButtons button)
    {
        if (quitting) return;
        if (button == Forms.MouseButtons.Left) ShowShelf();
        else if (button == Forms.MouseButtons.Right)
        {
            long request = ++trayMenuRequest;
            Dispatcher.BeginInvoke(() => {
                if (!quitting && request == trayMenuRequest) OpenTrayContextMenu();
            }, DispatcherPriority.Background);
        }
    }

    internal ContextMenu BuildTrayContextMenu()
    {
        var menu = new ContextMenu { Style = (Style)FindResource("FluentContextMenu") };
        AutomationProperties.SetName(menu, "ClipShelf 托盘菜单");
        void Add(string text, string glyph, Action action, string gesture = "")
        {
            var entry = new MenuItem { Header = text, InputGestureText = gesture,
                Style = (Style)FindResource("FluentMenuItem"),
                Icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = 16, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            AutomationProperties.SetName(entry, text);
            entry.Click += (_, _) => { menu.IsOpen = false; if (!quitting) action(); };
            menu.Items.Add(entry);
        }
        void Divider() => menu.Items.Add(new Separator { Style = (Style)FindResource("FluentMenuSeparator") });

        Add("打开 ClipShelf", "\uE8A7", ShowShelf);
        Divider();
        Add(Store.Settings.HistoryEnabled ? "暂停历史记录" : "继续历史记录",
            Store.Settings.HistoryEnabled ? "\uE769" : "\uE768",
            () => { Store.Settings.HistoryEnabled = !Store.Settings.HistoryEnabled; ApplyPreferences(); });
        Add("选择截图文件夹…", "\uE8B7", () => { ShowShelf(); ChooseFolder(); });
        Add("设置", "\uE713", () => { ShowShelf(); OpenSettings(); });
        Divider();
        Add("退出", "\uE8BB", Quit);
        // History belongs in the main shelf: multiline clipboard contents must never
        // turn the taskbar menu into a screen-height document or expose private snippets.
        return menu;
    }

    private void OpenTrayContextMenu()
    {
        CloseTrayContextMenu();
        HistoryList.CancelWheelMotion();
        EndDrag();
        var menu = BuildTrayContextMenu();
        // A ContextMenu popup is a non-activating HWND. Activating that HWND after
        // WPF enters menu mode can clear its keyboard focus and immediately dismiss
        // it. Give WPF a normal, already-active owner before opening the popup.
        // The host is independent from the hidden shelf and lives for this menu only.
        var host = new TrayMenuHost();
        Action? detachDiagnostic = null;
        if (TrayMenuDiagnostic is not null) {
            var descriptor = DependencyPropertyDescriptor.FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu));
            EventHandler onChanged = (_, _) => { if (!menu.IsOpen) TrayMenuDiagnostic?.Invoke("is-open-false: " + Environment.StackTrace); };
            descriptor.AddValueChanged(menu, onChanged);
            bool attached = true;
            detachDiagnostic = () => { if (attached) { attached = false; descriptor.RemoveValueChanged(menu, onChanged); } };
        }
        // MousePoint still anchors at the cursor, not at the 1 DIP focus host.
        menu.PlacementTarget = host.FocusAnchor;
        menu.Placement = PlacementMode.MousePoint;
        trayContextMenu = menu;
        trayMenuHost = host;
        menu.Opened += (_, _) => {
            TrayMenuDiagnostic?.Invoke("opened: " + menu.IsOpen);
            if (menu.IsOpen && !quitting && ReferenceEquals(trayContextMenu, menu))
            {
                bool focused = menu.Focus();
                TrayMenuDiagnostic?.Invoke("menu-focus: " + focused);
            }
        };
        menu.Closed += (_, _) => {
            detachDiagnostic?.Invoke();
            TrayMenuDiagnostic?.Invoke("closed");
            if (ReferenceEquals(trayContextMenu, menu)) trayContextMenu = null;
            if (ReferenceEquals(trayMenuHost, host)) trayMenuHost = null;
            // Closed can follow a 150ms fade after another menu or ShowShelf has
            // already run. Release only this host, once; never reactivate anything.
            host.Release();
        };
        try
        {
            host.Show();
            host.Activate();
            var handle = new WindowInteropHelper(host).Handle;
            if (NativeMethods.GetForegroundWindow() != handle)
                NativeMethods.SetForegroundWindow(handle);
            bool activated = NativeMethods.GetForegroundWindow() == handle;
            TrayMenuDiagnostic?.Invoke("host-foreground: " + activated);
            if (!activated)
            {
                // Do not open an unfocused menu, reveal the shelf, or steal focus
                // repeatedly if Windows denies this user-requested activation.
                TrayMenuDiagnostic?.Invoke("host-activation-blocked");
                detachDiagnostic?.Invoke();
                CloseTrayContextMenu();
                return;
            }
            host.FocusAnchor.Focus();
            menu.IsOpen = true;
            TrayMenuDiagnostic?.Invoke("requested: " + menu.IsOpen);
        }
        catch
        {
            detachDiagnostic?.Invoke();
            if (ReferenceEquals(trayContextMenu, menu)) CloseTrayContextMenu();
            else host.Release();
            throw;
        }
    }

    private void CloseTrayContextMenu()
    {
        ++trayMenuRequest;
        var menu = trayContextMenu;
        var host = trayMenuHost;
        trayContextMenu = null;
        trayMenuHost = null;
        try { if (menu is not null) menu.IsOpen = false; }
        finally { host?.Release(); }
    }

    private void DisposeTray()
    {
        CloseTrayContextMenu();
        if (tray is not null) { tray.Visible = false; tray.Dispose(); tray = null; }
        trayIcon?.Dispose(); trayIcon = null;
    }

    private sealed class TrayMenuHost : Window
    {
        private bool released;
        internal Border FocusAnchor { get; } = new() { Focusable = true, Background = Brushes.Transparent };

        internal TrayMenuHost()
        {
            Title = "ClipShelf · 托盘菜单";
            Width = Height = 1;
            Left = SystemParameters.WorkArea.Left;
            Top = SystemParameters.WorkArea.Top;
            WindowStartupLocation = WindowStartupLocation.Manual;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            Opacity = 0;
            ShowInTaskbar = false;
            ShowActivated = true;
            Content = FocusAnchor;
            // Deliberately no Owner: hiding/minimizing the main shelf must not
            // suppress this temporary tray-menu owner or its popup.
        }

        internal void Release()
        {
            if (released) return;
            released = true;
            Close();
        }
    }
}
