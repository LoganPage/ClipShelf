using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ClipShelf;

public sealed class SettingsPanel : UserControl
{
    private readonly MainWindow owner;
    private AppSettings S => owner.Store.Settings;
    private readonly StackPanel body = new() { Margin = new Thickness(20, 4, 14, 20) };
    private readonly List<(Button Button, Func<bool> Selected)> appearanceChoices = new();
    private readonly List<Action> refreshControls = new();
    private bool refreshingControls, restoringFocus;
    public SettingsPanel(MainWindow owner)
    {
        this.owner = owner;
        body.RequestBringIntoView += (_, e) => { if (restoringFocus) e.Handled = true; };
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel();
        var header = new StackPanel { Margin = new Thickness(22, 20, 22, 16) };
        header.Children.Add(new TextBlock { Text = "设置", FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(Note("让 ClipShelf 按你的习惯工作 · 更改即时保存"));
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var footer = new Border { BorderThickness = new Thickness(0, 1, 0, 0), Padding = new Thickness(20, 12, 20, 12) };
        footer.SetResourceReference(Border.BorderBrushProperty, "SettingsStrokeBrush");
        footer.SetResourceReference(Border.BackgroundProperty, "SettingsCanvasBrush");
        var done = Button("完成", owner.CloseSettings); done.HorizontalAlignment = HorizontalAlignment.Right; done.MinWidth = 76;
        footer.Child = done; DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        root.Children.Add(new SmoothScrollViewer { Name = "SettingsScroller", Content = body });
        Content = root;
        Build();
    }
    private void Build()
    {
        var updates = Card(Section("版本与更新"));
        updates.Children.Add(Label($"ClipShelf for Windows {WindowsUpdateService.CurrentVersion}", "检查开发者在 GitHub 发布的 Windows 新版（含体验版）。不会自动下载或安装。"));
        var updateStatus = Note(owner.UpdateStatus);
        updateStatus.MinHeight = 36; updates.Children.Add(updateStatus);
        var updateButton = Button(owner.UpdateActionLabel, () => owner.RunUpdateAction());
        updateButton.Name = "CheckForUpdatesButton";
        updateButton.HorizontalAlignment = HorizontalAlignment.Left;
        updates.Children.Add(updateButton);
        void RefreshUpdate() { updateStatus.Text = owner.UpdateStatus; updateButton.Content = owner.UpdateActionLabel; updateButton.IsEnabled = !owner.UpdateInstalling; }
        Loaded += (_, _) => { owner.UpdateChanged += RefreshUpdate; RefreshUpdate(); };
        Unloaded += (_, _) => owner.UpdateChanged -= RefreshUpdate;
        updates.Children.Add(Note("下载后校验完整性，备份历史和设置，再重启完成覆盖安装。"));
        var appearance = Section("外观");
        var themeCard = Card(appearance);
        themeCard.Children.Add(Label("应用主题", "选择浅色、深色，或与 Windows 保持一致。"));
        var modes = new[] { "System", "Light", "Dark" }; var labels = new[] { "跟随系统", "浅色", "深色" };
        var segments = new UniformGridCompat(3);
        for (int i = 0; i < 3; i++) { string mode = modes[i]; var b = Button(labels[i], () => { S.Theme = mode; owner.ApplyPreferences(appearanceOnly: true); RefreshChoiceHighlights(); }); b.Margin = new Thickness(i == 0 ? 0 : 6, 12, 0, 0); appearanceChoices.Add((b, () => S.Theme == mode)); segments.Children.Add(b); }
        themeCard.Children.Add(segments);


        var shots = Card(Section("截图"));
        shots.Children.Add(Toggle("监听截图文件夹", () => S.WatchScreenshots, value => { S.WatchScreenshots = value; Changed(); }));
        shots.Children.Add(Note("新截图保留原文件，同时收录到历史并复制到剪贴板。"));
        var folderPath = Note(S.ScreenshotFolder); folderPath.Margin = new Thickness(0, 12, 0, 10); shots.Children.Add(folderPath);
        refreshControls.Add(() => folderPath.Text = S.ScreenshotFolder);
        var folders = new StackPanel { Orientation = Orientation.Horizontal };
        folders.Children.Add(Button("选择文件夹", owner.ChooseFolder));
        var reveal = Button("在资源管理器中显示", () => Reveal(S.ScreenshotFolder)); reveal.Margin = new Thickness(8, 0, 0, 0); folders.Children.Add(reveal); shots.Children.Add(folders);

        var historySection = Section("历史与选择");
        var history = Card(historySection);
        history.Children.Add(Toggle("记录文字、文件和图片复制历史", () => S.HistoryEnabled, value => { S.HistoryEnabled = value; Changed(); }));
        history.Children.Add(Note("文字、文件与图片仅保存在本机。"));
        var selectionCard = Card(historySection);
        var repeatClick = Toggle("再次点击已选记录时取消选中", () => S.DeselectOnRepeatedClick,
            value => { S.DeselectOnRepeatedClick = value; owner.Store.SaveSettings(); });
        repeatClick.Name = "DeselectOnRepeatedClickToggle";
        selectionCard.Children.Add(repeatClick);
        selectionCard.Children.Add(Note("仅作用于单条选择；Ctrl / Shift 多选和拖选不受影响。"));
        var colorCard = Card(historySection);
        colorCard.Children.Add(ChoiceRow("选中颜色", ThemeManager.PresetNames, ThemeManager.Presets, () => S.SelectionPreset, value => { S.SelectionPreset = value; Changed(); }));
        var color = new TextBox { Text = S.SelectionColor, MinWidth = 100, MaxWidth = 140 };
        refreshControls.Add(() => { if (!color.IsKeyboardFocusWithin) color.Text = S.SelectionColor; });
        color.LostKeyboardFocus += (_, _) => { if (string.Equals(color.Text, S.SelectionColor, StringComparison.OrdinalIgnoreCase)) return; try { if (color.Text.Length != 7 || !color.Text.StartsWith('#')) throw new FormatException(); ColorConverter.ConvertFromString(color.Text); S.SelectionColor = color.Text; S.SelectionPreset = "Custom"; Changed(); } catch { color.Text = S.SelectionColor; } };
        colorCard.Children.Add(Row("自定义颜色（#RRGGBB）", color));
        colorCard.Children.Add(Note("行内按钮操作当前记录；顶部工具栏操作选中的记录。"));
        var historyButtons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        historyButtons.Children.Add(Button("显示历史文件", () => Reveal(owner.Store.DirectoryPath)));
        var clear = Button("清空全部历史…", owner.ConfirmClearHistory); clear.Margin = new Thickness(8, 0, 0, 0); historyButtons.Children.Add(clear);

        var keys = Card(Section("快捷键"));
        keys.Children.Add(ShortcutRow("全局呼出", S.GlobalHotKey, value => {
            if (!ValidateShortcut(value, S.ClearSelectionHotKey, S.PinHotKey)) return false;
            if (owner.Integration is not null && !owner.Integration.RegisterHotKey(value)) { owner.ShowStatus(owner.Integration.LastHotKeyError ?? "这个快捷键已被占用。"); return false; }
            S.GlobalHotKey = value; owner.Store.SaveSettings(); return true;
        }));
        keys.Children.Add(ShortcutRow("取消选择", S.ClearSelectionHotKey, value => { if (!ValidateShortcut(value, S.GlobalHotKey, S.PinHotKey)) return false; S.ClearSelectionHotKey = value; owner.Store.SaveSettings(); return true; }));
        keys.Children.Add(ShortcutRow("置顶选中记录", S.PinHotKey, value => { if (!ValidateShortcut(value, S.GlobalHotKey, S.ClearSelectionHotKey)) return false; S.PinHotKey = value; owner.Store.SaveSettings(); return true; }));
        keys.Children.Add(Note("点击右侧输入框，按下新的组合键。"));
        keys.Children.Add(Note("↑↓ 选择 · Space 预览 · Ctrl+A 全选 · Ctrl+C 复制\n预览支持文字、常见文本文件、图片及 PDF / DOCX / PPTX；不需要 Office。预览文字时 Ctrl+F 查找、F3 前后跳转，并可切换换行与行号。↑↓ 逐条切换记录，←→ 翻文档页，Home/End 首末页，Space/Esc 关闭，滚轮滚动内容。\nDelete 删除 · Ctrl+Z 撤销 · Shift+F10 菜单 · Ctrl+F 搜索\n复制后，在需要输入的位置按 Ctrl+V 粘贴；ClipShelf 不自动切换应用。"));

        var startup = Card(Section("启动与托盘"));
        startup.Children.Add(Toggle("开机时启动 ClipShelf", () => S.LaunchAtLogin, value => { S.LaunchAtLogin = value; Changed(); }));
        startup.Children.Add(ChoiceRow("点击关闭按钮时", new[] { "最小化到托盘", "退出程序" }, new[] { "Tray", "Exit" }, () => S.CloseToTray ? "Tray" : "Exit", value => { S.CloseToTray = value == "Tray"; Changed(); }));
        startup.Children.Add(Note("托盘模式下继续记录；退出程序会停止记录。左键托盘图标可重新打开窗口。"));
        var data = Card(Section("数据管理"));
        data.Children.Add(Label("本地历史", $"最多保留 {S.MaxItems} 条，优先保留置顶记录。"));
        data.Children.Add(historyButtons);
        data.Children.Add(Note("Ctrl+Z 可撤销本次运行中最近 10 批删除。清空全部或退出后不再可撤销。"));
        var cleanup = Card(Section("缓存清理"));
        cleanup.Children.Add(Label("一键清理", "清理预览缓存、内存缩略图和过期更新下载包。不删除历史、置顶记录、原文件或备份。"));
        var cleanupStatus = Note(owner.CleanupStatus); cleanupStatus.MinHeight = 36; cleanup.Children.Add(cleanupStatus);
        var cleanupButton = Button("一键清理", owner.RunCleanup);
        cleanupButton.Name = "CleanCacheButton"; cleanupButton.HorizontalAlignment = HorizontalAlignment.Left;
        cleanup.Children.Add(cleanupButton);
        cleanup.Children.Add(Note("清理后首次预览可能稍慢；正在使用的缓存会按需重新生成。"));
        void RefreshCleanup() { cleanupStatus.Text = owner.CleanupStatus; cleanupButton.IsEnabled = !owner.CleanupRunning; cleanupButton.Content = owner.CleanupRunning ? "正在清理…" : "一键清理"; }
        Loaded += (_, _) => { owner.CleanupChanged += RefreshCleanup; RefreshCleanup(); };
        Unloaded += (_, _) => owner.CleanupChanged -= RefreshCleanup;
        var historyCleanup = Card(Section("清理旧历史（可选）"));
        historyCleanup.Children.Add(Note("只移除所选天数之前的未置顶记录，不删除原文件。不随缓存清理自动执行。"));
        int retentionDays = 30; ClipItem[]? confirmedItems = null;
        var historyStatus = Note("先查看待清理数量，再确认执行。可在本次运行中回到主列表按 Ctrl+Z 撤销。 ");
        var historyConfirm = new StackPanel { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
        historyCleanup.Children.Add(ChoiceRow("保留最近", new[] { "7 天", "30 天", "90 天" }, new[] { "7", "30", "90" }, () => retentionDays.ToString(), value => { retentionDays = int.Parse(value); confirmedItems = null; historyConfirm.Visibility = Visibility.Collapsed; historyStatus.Text = "先查看待清理数量，再确认执行。"; }));
        historyCleanup.Children.Add(historyStatus);
        var inspectHistory = Button("查看待清理记录数量", () => {
            confirmedItems = owner.OldHistory(retentionDays);
            historyStatus.Text = confirmedItems.Length == 0 ? "没有符合条件的旧记录。" : $"将清理 {retentionDays} 天前的 {confirmedItems.Length} 条未置顶记录。置顶记录和原文件保留。";
            historyConfirm.Visibility = confirmedItems.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        });
        inspectHistory.HorizontalAlignment = HorizontalAlignment.Left; historyCleanup.Children.Add(inspectHistory);
        historyConfirm.Children.Add(Button("确认清理这些记录", () => {
            if (confirmedItems is null) return;
            int count = owner.CleanOldHistory(confirmedItems, retentionDays); confirmedItems = null;
            historyConfirm.Visibility = Visibility.Collapsed; historyStatus.Text = $"已清理 {count} 条旧记录。返回主列表按 Ctrl+Z 可撤销；退出程序后不可撤销。";
        }));
        var cancelHistory = Button("取消", () => { confirmedItems = null; historyConfirm.Visibility = Visibility.Collapsed; historyStatus.Text = "已取消，历史未改动。"; });
        cancelHistory.Margin = new Thickness(8, 0, 0, 0); historyConfirm.Children.Add(cancelHistory); historyCleanup.Children.Add(historyConfirm);
        var about = new TextBlock { Text = $"ClipShelf for Windows {WindowsUpdateService.CurrentVersion}", FontSize = 12, Margin = new Thickness(0, 20, 0, 0), TextWrapping = TextWrapping.Wrap };
        about.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush"); body.Children.Add(about);
        RefreshChoiceHighlights();
    }
    private void RefreshChoiceHighlights()
    {
        // Keep the active buttons and their keyboard focus instead of rebuilding the settings tree.
        foreach (var choice in appearanceChoices)
        {
            choice.Button.SetResourceReference(BackgroundProperty, choice.Selected() ? "SelectedBrush" : "SettingsControlBrush");
            choice.Button.SetResourceReference(BorderBrushProperty, choice.Selected() ? "AccentBrush" : "SettingsStrokeBrush");
            System.Windows.Automation.AutomationProperties.SetItemStatus(choice.Button, choice.Selected() ? "已选择" : "未选择");
        }
    }
    private void Changed() => owner.ApplyPreferences();
    internal void RefreshFromSettings()
    {
        refreshingControls = true;
        try { foreach (var refresh in refreshControls) refresh(); RefreshChoiceHighlights(); }
        finally { refreshingControls = false; }
    }
    internal bool RestoreFocusWithoutScrolling(UIElement control)
    {
        restoringFocus = true;
        try { return control.Focus(); }
        finally { restoringFocus = false; }
    }
    private bool ValidateShortcut(string value, params string[] otherShortcuts)
    {
        if (HistoryShortcutPolicy.IsReserved(value)) { owner.ShowStatus("这个组合用于标准编辑或窗口操作，请选择其它快捷键。"); return false; }
        if (otherShortcuts.Any(other => HistoryShortcutPolicy.Normalize(other) == HistoryShortcutPolicy.Normalize(value)))
        { owner.ShowStatus("这个组合已用于 ClipShelf 的其它操作，请选择不同的快捷键。"); return false; }
        return true;
    }
    private StackPanel Section(string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, body.Children.Count == 0 ? 0 : 20, 0, 0) };
        section.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 13, Margin = new Thickness(2, 0, 0, 8) }); body.Children.Add(section); return section;
    }
    private static StackPanel Card(StackPanel section)
    {
        var content = new StackPanel();
        var card = new Border { Name = "SettingsGroupCard", Child = content, CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(16, 12, 16, 12), Margin = new Thickness(0, 0, 0, 4) };
        card.SetResourceReference(Border.BackgroundProperty, "SettingsCardBrush"); card.SetResourceReference(Border.BorderBrushProperty, "SettingsStrokeBrush");
        section.Children.Add(card); return content;
    }
    private static StackPanel Label(string title, string description)
    {
        var label = new StackPanel(); label.Children.Add(new TextBlock { Text = title, FontSize = 13, TextWrapping = TextWrapping.Wrap }); label.Children.Add(Note(description)); return label;
    }
    private static TextBlock Note(string text)
    { var note = new TextBlock { Text = text, FontSize = 12, TextWrapping = TextWrapping.Wrap, LineHeight = 18, Margin = new Thickness(0, 4, 0, 0) }; note.SetResourceReference(TextBlock.ForegroundProperty, "SettingsSecondaryBrush"); return note; }
    private static Button Button(object content, Action click)
    { var b = new Button { Content = content, Style = (Style)Application.Current.FindResource("SettingsButton") }; b.Click += (_, _) => click(); return b; }
    private CheckBox Toggle(string text, Func<bool> value, Action<bool> change)
    {
        var b = new CheckBox { Content = text, IsChecked = value(), Style = (Style)Application.Current.FindResource("SettingsToggle") };
        refreshControls.Add(() => b.IsChecked = value());
        void MoveKnob(bool animate)
        {
            if (!b.IsLoaded || b.Template is null) return;
            if (b.Template.FindName("knob", b) is not FrameworkElement knob || knob.RenderTransform is not TranslateTransform shift) return;
            if (shift.IsFrozen) { shift = shift.Clone(); knob.RenderTransform = shift; }
            double target = b.IsChecked == true ? 20 : 0;
            double from = shift.X;
            shift.BeginAnimation(TranslateTransform.XProperty, null); shift.X = target;
            if (animate && b.IsVisible && SystemParameters.ClientAreaAnimation)
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, target, TimeSpan.FromMilliseconds(110)) {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        }
        b.Loaded += (_, _) => MoveKnob(false);
        // React to state, not only Click, so keyboard and accessibility toggles save too.
        b.Checked += (_, _) => { MoveKnob(!refreshingControls); if (!refreshingControls) change(true); };
        b.Unchecked += (_, _) => { MoveKnob(!refreshingControls); if (!refreshingControls) change(false); }; return b;
    }
    private static Grid Row(string label, UIElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 5, 0, 5) }; grid.ColumnDefinitions.Add(new ColumnDefinition()); grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) }); Grid.SetColumn(control, 1); grid.Children.Add(control); return grid;
    }
    private Grid ChoiceRow(string label, string[] titles, string[] values, Func<string> current, Action<string> change)
    {
        var choice = new ComboBox { ItemsSource = titles, SelectedIndex = Math.Max(0, Array.IndexOf(values, current())), MaxWidth = 220 };
        refreshControls.Add(() => choice.SelectedIndex = Math.Max(0, Array.IndexOf(values, current())));
        choice.SelectionChanged += (_, _) => { if (!refreshingControls && choice.SelectedIndex >= 0) change(values[choice.SelectedIndex]); }; return Row(label, choice);
    }
    private Grid ShortcutRow(string label, string current, Func<string, bool> change)
    {
        var input = new TextBox { Text = current, IsReadOnly = true, Width = 165, HorizontalContentAlignment = HorizontalAlignment.Center, ToolTip = "点击后按下新的快捷键" };
        input.Tag = "ShortcutRecorder";
        input.GotKeyboardFocus += (_, _) => { owner.Integration?.SuspendHotKey(); input.SelectAll(); };
        input.LostKeyboardFocus += (_, _) => { owner.Integration?.RegisterHotKey(S.GlobalHotKey); };
        input.PreviewKeyDown += (_, e) => {
            // Shortcut recording must not trap keyboard users inside the input.
            if (e.Key == Key.Tab) return;
            e.Handled = true;
            if (e.Key == Key.Escape) { Keyboard.ClearFocus(); return; }
            string? shortcut = Shortcut.FromEvent(e); if (shortcut is null) return;
            if (shortcut == "Ctrl+Escape" || Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) return;
            if (change(shortcut)) { input.Text = shortcut; Keyboard.ClearFocus(); }
        }; return Row(label, input);
    }
    public static void Reveal(string path)
    {
        if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = true });
        else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe") { Arguments = "/select,\"" + path + "\"", UseShellExecute = true });
    }
    private sealed class UniformGridCompat : System.Windows.Controls.Primitives.UniformGrid { public UniformGridCompat(int columns) { Columns = columns; } }
}

public static class Shortcut
{
    public static string? FromEvent(KeyEventArgs e)
    {
        Key key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return null;
        var modifiers = Keyboard.Modifiers;
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return null;
        return (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "") + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "") + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "") + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "") + new KeyConverter().ConvertToInvariantString(key);
    }
    public static bool Matches(KeyEventArgs e, string shortcut) => string.Equals(FromEvent(e), shortcut, StringComparison.OrdinalIgnoreCase);
}
