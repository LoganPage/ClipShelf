using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace ClipShelf;

public partial class MainWindow : Window
{
    public HistoryStore Store { get; }
    public WindowsIntegration? Integration { get; private set; }
    private readonly bool demo;
    private readonly WindowAppearance windowAppearance;
    internal WindowAppearanceStatus NativeAppearanceStatus => windowAppearance.LastStatus;
    private Forms.NotifyIcon? tray;
    private List<ClipItem> visible = new();
    private readonly ObservableCollection<ClipItem> displayed = new();
    private ScrollViewer? historyScroll;
    private ScrollViewer? HistoryScroll => historyScroll ??= Descendant<ScrollViewer>(HistoryList);
    private CancellationTokenSource? searchCancellation;
    internal Task PendingSearch { get; private set; } = Task.CompletedTask;
    private bool refreshPending, refreshQueued, resetScroll, showingSearchStatus;
    private int rangeStart = -1, rangeEnd = -1;
    private const double RowHeight = 74;
    private readonly DispatcherTimer toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool dragRendering;
    private long dragFrameTick;
    private TimeSpan dragRenderingTime = TimeSpan.MinValue;
    private bool quitting, refreshing, dragging, pointerDown, suppressDragRelease;
    private Point downPoint;
    private int anchor = -1, downIndex = -1;
    private int dragReleaseVersion;
    private long interactionVersion;
    private HashSet<Guid>? dragBaseSelection, keyboardRangeBase, cachedRangeBase;
    private Guid? focusedId, anchorId;
    private IInputElement? settingsReturnFocus;
    private PreviewWindow? preview;
    private readonly PreviewCacheService previewCache = new();
    private static readonly int showMessage = RegisterWindowMessage("ClipShelf.Windows.Show");
    private static readonly int quitMessage = RegisterWindowMessage("ClipShelf.Windows.Quit");
    public MainWindow(HistoryStore store, bool demo = false)
    {
        Store = store; this.demo = demo;
        ThemeManager.Apply(store.Settings);
        InitializeComponent();
        FocusCuePolicy.SetIsEnabled(this, true);
        windowAppearance = new WindowAppearance(this);
        HistoryList.ItemsSource = displayed;
        InitializeHistoryInteraction();
        // Start compact; resizing remains available for the current run. Existing
        // persisted dimensions are retained for compatibility, not startup sizing.
        Width = Math.Min(AppSettings.DefaultWindowWidth, Math.Max(MinWidth, SystemParameters.WorkArea.Width));
        Height = Math.Min(AppSettings.DefaultWindowHeight, Math.Max(MinHeight, SystemParameters.WorkArea.Height));
        UpdateIcon();
        store.Changed += StoreChanged;
        toastTimer.Tick += (_, _) => { Toast.Visibility = Visibility.Collapsed; toastTimer.Stop(); };
        SourceInitialized += (_, _) => InitializeNative();
        Closing += OnClosing;
        SystemEvents.UserPreferenceChanged += SystemAppearanceChanged;
        ContentRendered += (_, _) => Dispatcher.BeginInvoke(() => {
            if (!quitting && SettingsContent.Content is null) SettingsContent.Content = new SettingsPanel(this);
        }, DispatcherPriority.ContextIdle);
        Refresh();
    }
    private void InitializeNative()
    {
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(MessageHook);
        if (!demo) {
            ConnectIntegration();
        } else { StatusText.Text = "界面预览 · 示例记录"; return; }
        InitializeTray();
    }
    internal void ConnectIntegration(bool manageStartup = true)
    {
        Integration = new WindowsIntegration(this, Store, manageStartup);
        Integration.ShowRequested += ShowShelf; Integration.Status += ShowStatus;
        StatusText.Text = Integration.ScreenshotStatus;
        if (Integration.LastHotKeyError is string error) ShowStatus(error);
    }
    private IntPtr MessageHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == showMessage) { ShowShelf(); handled = true; }
        else if (msg == quitMessage) { Quit(); handled = true; }
        else if (msg == 0x0021) activationClickPending = !IsActive && (lParam.ToInt64() & 0xffff) == 1 && ((lParam.ToInt64() >> 16) & 0xffff) == 0x0201; // Only client-area left-button activation.
        else if (msg == 0x001C && wParam == IntPtr.Zero) HandleApplicationDeactivated();
        return IntPtr.Zero;
    }
    public static void SignalExistingInstance() => PostMessage(new IntPtr(0xffff), showMessage, IntPtr.Zero, IntPtr.Zero);
    public static void QuitExistingInstance() => PostMessage(new IntPtr(0xffff), quitMessage, IntPtr.Zero, IntPtr.Zero);
    public void ShowShelf()
    {
        CloseTrayContextMenu();
        interactionVersion++;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate(); Topmost = true; Topmost = false;
        RestoreHistoryFocus();
    }
    public void ApplyPreferences(bool appearanceOnly = false)
    {
        Store.SaveSettings(); ThemeManager.Apply(Store.Settings); windowAppearance.Refresh(); UpdateIcon();
        if (!appearanceOnly) Integration?.ApplySettings();
        if (!demo && Integration is not null) StatusText.Text = Integration.ScreenshotStatus;
        UpdateActions();
    }
    private void SystemAppearanceChanged(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.BeginInvoke(() => { ThemeManager.Apply(Store.Settings); windowAppearance.Refresh(); });
    public void UpdateIcon() { var icon = ThemeManager.Icon(Store.Settings.AppIcon); if (!ReferenceEquals(AppImage.Source, icon)) { AppImage.Source = icon; Icon = icon; } }
    private void StoreChanged()
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(StoreChanged); return; }
        refreshPending = true;
        if (pointerDown || dragging || refreshQueued || quitting) return;
        refreshQueued = true;
        Dispatcher.BeginInvoke(() => { refreshQueued = false; if (!pointerDown && !dragging && refreshPending && !quitting) Refresh(); }, DispatcherPriority.Background);
    }
    public void Refresh()
    {
        if (HistoryList is null) return;
        if (pointerDown || dragging) { refreshPending = true; return; }
        refreshPending = false;
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = null;
        RestoreSearchStatus();
        string query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query)) { ApplyVisible(Store.Items.ToList(), query); PendingSearch = Task.CompletedTask; return; }
        searchCancellation = new CancellationTokenSource();
        PendingSearch = SearchAsync(Store.Items.ToArray(), query, searchCancellation.Token);
    }
    private async Task SearchAsync(ClipItem[] snapshot, string query, CancellationToken cancellation)
    {
        try
        {
            // A short, cancellable coalescing window avoids indexing each intermediate keystroke.
            await Task.Delay(60, cancellation);
            var work = Task.Run(() => SearchMatcher.Filter(snapshot, query, cancellation), cancellation);
            _ = ShowSearchProgressAsync(work, cancellation);
            var matches = await work;
            if (cancellation.IsCancellationRequested || quitting) return;
            RestoreSearchStatus();
            if (pointerDown || dragging) { refreshPending = true; return; }
            ApplyVisible(matches, query);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Trace.TraceError("Search failed: {0}", ex.GetType().Name); if (!cancellation.IsCancellationRequested) { RestoreSearchStatus(); ShowStatus("搜索暂时没有完成，请重试。"); } }
    }
    private async Task ShowSearchProgressAsync(Task work, CancellationToken cancellation)
    {
        await Task.WhenAny(work, Task.Delay(180, cancellation));
        if (!work.IsCompleted && !cancellation.IsCancellationRequested && !quitting) { showingSearchStatus = true; StatusText.Text = "正在搜索…"; }
    }
    private void RestoreSearchStatus()
    {
        if (!showingSearchStatus) return;
        showingSearchStatus = false; StatusText.Text = demo ? "界面预览 · 示例记录" : Integration?.ScreenshotStatus ?? "准备就绪";
    }
    private void ApplyVisible(List<ClipItem> next, string query)
    {
        HistoryList.CancelWheelMotion();
        var selected = HistoryList.SelectedItems.Cast<ClipItem>().Select(x => x.Id).ToHashSet();
        int previousFocus = focusedId is Guid focusId ? visible.FindIndex(item => item.Id == focusId) : -1;
        bool hadRowFocus = IsHistoryContentOrigin(Keyboard.FocusedElement as DependencyObject);
        var scroll = HistoryScroll;
        double offset = scroll?.VerticalOffset ?? 0;
        int topIndex = Math.Clamp((int)(offset / RowHeight), 0, Math.Max(0, visible.Count - 1));
        Guid? topId = !resetScroll && offset > 0 && visible.Count > 0 ? visible[topIndex].Id : null;
        refreshing = true;
        try
        {
            var wanted = next.Select(x => x.Id).ToHashSet();
            for (int i = displayed.Count - 1; i >= 0; i--) if (!wanted.Contains(displayed[i].Id)) displayed.RemoveAt(i);
            for (int i = 0; i < next.Count; i++)
            {
                if (i < displayed.Count && displayed[i].Id == next[i].Id) { if (!ReferenceEquals(displayed[i], next[i])) displayed[i] = next[i]; continue; }
                int oldIndex = -1;
                for (int j = i + 1; j < displayed.Count; j++) if (displayed[j].Id == next[i].Id) { oldIndex = j; break; }
                if (oldIndex >= 0) { displayed.Move(oldIndex, i); if (!ReferenceEquals(displayed[i], next[i])) displayed[i] = next[i]; }
                else displayed.Insert(i, next[i]);
            }
            visible = next;
            HistoryList.ReplaceSelection(visible.Where(x => selected.Contains(x.Id)).ToArray());
            ResetRangeCache();
            if (focusedId is Guid oldFocus && !wanted.Contains(oldFocus))
                focusedId = next.Count == 0 ? null : next[Math.Clamp(previousFocus, 0, next.Count - 1)].Id;
            if (anchorId is Guid oldAnchor && !wanted.Contains(oldAnchor)) anchorId = focusedId;
            anchor = anchorId is Guid currentAnchor ? next.FindIndex(item => item.Id == currentAnchor) : -1;
            if (topId is Guid id && next.FindIndex(x => x.Id == id) is int newTop && newTop >= 0)
                scroll?.ScrollToVerticalOffset(newTop * RowHeight + offset % RowHeight);
            else if (resetScroll) scroll?.ScrollToTop();
            resetScroll = false;
        }
        finally { refreshing = false; }
        bool empty = visible.Count == 0;
        EmptyPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        HistoryBorder.Visibility = empty ? Visibility.Hidden : Visibility.Visible;
        EmptyTitle.Text = string.IsNullOrWhiteSpace(query) ? "还没有历史记录" : "没有匹配的记录";
        EmptyDetail.Text = string.IsNullOrWhiteSpace(query) ? "复制文字、文件或图片后，会显示在这里。" : "换个关键词，试试文字、文件名或拼音。";
        UpdateActions();
        if (hadRowFocus && IsActive && !pointerDown && !dragging
            && SettingsOverlay.Visibility != Visibility.Visible) RestoreHistoryFocus(scrollIntoView: false);
    }
    private List<ClipItem> Selected() { var selected = HistoryList.SelectedItems.Cast<ClipItem>().ToHashSet(); return visible.Where(selected.Contains).ToList(); }
    internal IReadOnlyList<ClipItem> RowItems(ClipItem item) => new[] { item };
    private void UpdateActions()
    {
        int count = HistoryList.SelectedItems.Count;
        CopyButton.IsEnabled = PinButton.IsEnabled = DeleteButton.IsEnabled = count > 0;
        SelectionCountText.Text = $"已选 {count} 条";
        SelectionCountText.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectionBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Hidden;
        AutomationProperties.SetName(SelectionCountText, SelectionCountText.Text);
        string pinGesture = HistoryShortcutPolicy.IsReserved(Store.Settings.PinHotKey) ? "" : Store.Settings.PinHotKey;
        bool allPinned = count > 0 && HistoryList.SelectedItems.Cast<ClipItem>().All(item => item.IsPinned);
        PinButton.Tag = allPinned ? "Pinned" : null;
        PinButton.SetResourceReference(ForegroundProperty, allPinned ? "AccentBrush" : "TextBrush");
        foreach (var (button, verb, shortcut) in new[] { (CopyButton, "复制", "Ctrl+C"), (PinButton, "置顶 / 取消置顶", pinGesture), (DeleteButton, "删除", "Delete") })
        {
            string action = count > 0 ? $"{verb}选中的 {count} 条记录" : $"{verb}选中记录";
            button.ToolTip = action + (string.IsNullOrEmpty(shortcut) ? "" : " · " + shortcut);
            AutomationProperties.SetName(button, action);
        }
        if (allPinned)
        {
            string action = $"取消置顶选中的 {count} 条记录";
            PinButton.ToolTip = action + (string.IsNullOrEmpty(pinGesture) ? "" : " · " + pinGesture);
            AutomationProperties.SetName(PinButton, action);
        }
        UndoDeleteButton.IsEnabled = Store.CanUndoDelete;
        UndoDeleteButton.ToolTip = Store.CanUndoDelete ? $"撤销最近一次删除 · Ctrl+Z（可撤销 {Store.UndoDeleteCount} 次）" : "没有可撤销的删除";
        AutomationProperties.SetName(UndoDeleteButton, "撤销最近一次删除");
    }
    private void History_SelectionChanged(object sender, SelectionChangedEventArgs e) { ResetRangeCache(); if (!refreshing) UpdateActions(); }
    private void Search_Changed(object sender, TextChangedEventArgs e) { if (HistoryList is null) return; ClearSelectionState(); resetScroll = true; SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed; Refresh(); }
    private void ClearSearch_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); RestoreHistoryFocus(); }
    private async void Copy_Click(object sender, RoutedEventArgs e) => await CopyItems(Selected());
    private void Pin_Click(object sender, RoutedEventArgs e) => Store.TogglePinned(Selected().Select(x => x.Id));
    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();
    private void DeleteSelection()
    {
        DeleteHistoryItems(Selected());
    }
    public void ClearHistory() { Integration?.CancelPendingCaptures(); Store.Clear(); UpdateActions(); }
    public void ConfirmClearHistory()
    {
        if (Store.Items.Count == 0) return;
        if (MessageBox.Show(this, $"清空全部 {Store.Items.Count} 条历史记录？\n此操作不能撤销；原始文件和截图将继续保留。", "清空全部历史", MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK)
        { ClearHistory(); ShowStatus("已清空全部历史记录"); }
    }
    private async void RowAction_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is not Button { DataContext: ClipItem item } button) return;
        switch (button.Tag as string) {
            case "Pin": Store.TogglePinned(new[] { item.Id }); break;
            case "Delete": DeleteHistoryItems(new[] { item }); break;
            case "Copy": await CopyItems(RowItems(item)); break;
        }
    }
    private async Task CopyItems(IReadOnlyList<ClipItem> items)
    {
        if (items.Count == 0) return;
        if (Integration is null) { ShowStatus("界面预览模式"); return; }
        if (await Integration.CopyAsync(items)) ShowStatus(items.Count > 1 ? $"已复制 {items.Count} 条记录" : "已复制");
    }
    public void ShowStatus(string text)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => ShowStatus(text)); return; }
        ToastText.Text = text; Toast.Visibility = Visibility.Visible; toastTimer.Stop(); toastTimer.Start();
    }
    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        var work = HandleHistoryKeyAsync(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers,
            e.OriginalSource as DependencyObject ?? Keyboard.FocusedElement as DependencyObject);
        // Recognized asynchronous commands must stop routing before their first await.
        if (!work.IsCompleted) e.Handled = true;
        e.Handled |= await work;
    }
    private void MoveSelection(int delta, bool extend)
    {
        NavigateHistory(delta < 0 ? Key.Up : Key.Down, extend ? ModifierKeys.Shift : ModifierKeys.None);
    }
    private void ClearSelectionState()
    {
        HistoryList.UnselectAll(); ResetRangeCache(); keyboardRangeBase = null;
        anchor = -1; anchorId = focusedId = null;
    }
    private void CollapseSelection(int index)
    {
        if (index < 0 || index >= visible.Count) { ClearSelectionState(); return; }
        HistoryList.ReplaceSelection(new[] { visible[index] });
        anchor = index; anchorId = focusedId = visible[index].Id;
    }
    private static T? Ancestor<T>(DependencyObject? child) where T : DependencyObject
    { while (child is not null) { if (child is T result) return result; child = child is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(child) : LogicalTreeHelper.GetParent(child); } return null; }
    private void History_MouseDown(object sender, MouseButtonEventArgs e)
    {
        suppressDragRelease = false;
        if (activationPointer) { pendingDeselectId = null; e.Handled = true; return; }
        if (Ancestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
        var container = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not ClipItem item) { ClearSelectionState(); HistoryList.Focus(); return; }
        int index = visible.IndexOf(item);
        if (index < 0) return;
        if (Ancestor<ButtonBase>(e.OriginalSource as DependencyObject) is not null) return;
        BeginRowPointer(index, e.GetPosition(HistoryList), Keyboard.Modifiers, activationPointer);
        FocusHistoryItem(index, scrollIntoView: false);
        // The outer surface exclusively owns this gesture. Capturing the ListBox
        // would also start WPF's native auto-selection/scroll timer, which can
        // collapse our range to one row between custom drag updates.
        if (!HistoryBorder.CaptureMouse()) EndDrag();
        e.Handled = true;
    }
    private void ApplyRowClick(int index, bool useModifiers)
    {
        ApplyRowSelection(index, useModifiers ? Keyboard.Modifiers : ModifierKeys.None);
    }
    private void History_MouseMove(object sender, MouseEventArgs e)
    {
        if (!pointerDown) return;
        if (e.LeftButton != MouseButtonState.Pressed || !HistoryBorder.IsMouseCaptured) { EndDrag(); return; }
        var point = e.GetPosition(HistoryList);
        if (!dragging && (point - downPoint).Length < 6) return;
        if (!dragging) { pendingDeselectId = null; dragging = true; StartDragFrames(); UpdateDragSelection(point); }
        // Coalesce high-polling-rate mouse packets to the next display frame.
        e.Handled = true;
    }
    private void UpdateDragSelection(Point point)
    {
        var scroll = HistoryScroll;
        double offset = scroll?.VerticalOffset ?? 0;
        int index = Math.Clamp((int)Math.Floor((point.Y + offset) / RowHeight), 0, Math.Max(0, visible.Count - 1));
        SelectRangeWithBase(downIndex, index, dragBaseSelection);
    }
    private void History_DragMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Captured input routes to the surface, not the child list. Preserve
        // immediate wheel scrolling during selection without starting inertia.
        if (e.Handled || !pointerDown || !HistoryBorder.IsMouseCaptured) return;
        pendingDeselectId = null;
        if (!HistoryList.HandleWheelDelta(e.Delta, directInput: true)) return;
        if (dragging) UpdateDragSelection(e.GetPosition(HistoryList));
        e.Handled = true;
    }
    private void StartDragFrames()
    {
        if (dragRendering) return;
        dragRendering = true; dragFrameTick = Stopwatch.GetTimestamp(); dragRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += RenderDragFrame;
    }
    private void StopDragFrames()
    {
        if (!dragRendering) return;
        CompositionTarget.Rendering -= RenderDragFrame; dragRendering = false;
    }
    private void RenderDragFrame(object? sender, EventArgs e)
    {
        if (e is RenderingEventArgs frame) { if (frame.RenderingTime == dragRenderingTime) return; dragRenderingTime = frame.RenderingTime; }
        if (!dragging || !IsVisible || !HistoryBorder.IsMouseCaptured || Mouse.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        long now = Stopwatch.GetTimestamp(); double seconds = Math.Clamp((now - dragFrameTick) / (double)Stopwatch.Frequency, 0, .05); dragFrameTick = now;
        AdvanceDragFrame(Mouse.GetPosition(HistoryList), seconds);
    }
    private void AutoScrollDragAt(Point point)
        => AdvanceDragFrame(point, .03); // Deterministic input for isolated regression fixtures.
    internal void AdvanceDragFrame(Point point, double seconds)
    {
        if (!dragging) return;
        int direction = point.Y < 30 ? -1 : point.Y > HistoryList.ActualHeight - 30 ? 1 : 0;
        double offset = direction == 0 ? HistoryScroll?.VerticalOffset ?? 0 : HistoryList.ScrollDragBy(direction * (22 / .03) * Math.Clamp(seconds, 0, .05));
        int index = Math.Clamp((int)Math.Floor((point.Y + offset) / RowHeight), 0, Math.Max(0, visible.Count - 1));
        SelectRangeWithBase(downIndex, index, dragBaseSelection);
    }
    private void SelectRange(int start, int end)
    {
        SelectRangeWithBase(start, end, null);
    }
    private void SelectRangeWithBase(int start, int end, HashSet<Guid>? originalSelection)
    {
        if (visible.Count == 0) return;
        start = Math.Clamp(start, 0, visible.Count - 1); end = Math.Clamp(end, 0, visible.Count - 1);
        if (rangeStart == start && rangeEnd == end && ReferenceEquals(cachedRangeBase, originalSelection)) return;
        int first = Math.Min(start, end), last = Math.Max(start, end);
        // A moving endpoint normally changes one row. Do not rebuild/hash the
        // entire selected range for that row, especially during edge scrolling.
        if (rangeStart == start && ReferenceEquals(cachedRangeBase, originalSelection) && Math.Abs(rangeEnd - end) == 1)
        {
            int oldFirst = Math.Min(start, rangeEnd), oldLast = Math.Max(start, rangeEnd);
            int changedFirst = Math.Min(rangeEnd, end), changedLast = Math.Max(rangeEnd, end);
            for (int i = changedFirst; i <= changedLast; i++)
            {
                bool before = i >= oldFirst && i <= oldLast, after = i >= first && i <= last;
                if (before == after || originalSelection?.Contains(visible[i].Id) == true) continue;
                if (after) HistoryList.SelectedItems.Add(visible[i]); else HistoryList.SelectedItems.Remove(visible[i]);
            }
        }
        else HistoryList.ReplaceSelection(originalSelection is null ? visible.GetRange(first, last - first + 1)
            : visible.Where((item, index) => (index >= first && index <= last) || originalSelection.Contains(item.Id)).ToArray());
        rangeStart = start; rangeEnd = end; cachedRangeBase = originalSelection;
        anchor = start; anchorId = visible[start].Id; focusedId = visible[end].Id;
    }
    private static T? Descendant<T>(DependencyObject root) where T : DependencyObject
    { for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) { var child = VisualTreeHelper.GetChild(root, i); if (child is T found) return found; if (Descendant<T>(child) is T nested) return nested; } return null; }
    private void History_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (CompleteRowPointer(e.GetPosition(HistoryList)))
        { e.Handled = true; if (IsActive) RestoreHistoryFocus(scrollIntoView: false); }
    }
    private void History_LostCapture(object sender, MouseEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, HistoryBorder) && (pointerDown || dragging)) EndDrag();
    }
    private void EndDrag(bool applyPending = true)
    {
        bool wasDragging = dragging; pointerDown = dragging = false; dragBaseSelection = null; pendingDeselectId = null;
        StopDragFrames(); if (HistoryBorder.IsMouseCaptured) HistoryBorder.ReleaseMouseCapture();
        if (wasDragging)
        {
            // Only the release event of this gesture is guarded. The next intentional click is immediate.
            suppressDragRelease = true; int version = ++dragReleaseVersion;
            Dispatcher.BeginInvoke(() => { if (version == dragReleaseVersion) suppressDragRelease = false; }, DispatcherPriority.Input);
        }
        if (applyPending && refreshPending && !quitting) Refresh();
    }
    private void TogglePreview()
    {
        if (preview?.IsVisible == true) { preview.Close(); preview = null; return; }
        var selected = Selected(); if (selected.Count != 1) return;
        OpenDocumentPreview(visible.IndexOf(selected[0]));
    }
    private void Folder_Click(object sender, RoutedEventArgs e) => ChooseFolder();
    public void ChooseFolder()
    {
        var dialog = new OpenFolderDialog { Title = "选择截图文件夹", Multiselect = false };
        if (Directory.Exists(Store.Settings.ScreenshotFolder)) dialog.InitialDirectory = Store.Settings.ScreenshotFolder;
        if (dialog.ShowDialog(this) == true) { Store.Settings.ScreenshotFolder = dialog.FolderName; Store.Settings.WatchScreenshots = true; ApplyPreferences(); if (SettingsContent.Content is SettingsPanel panel) panel.RefreshFromSettings(); }
    }
    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();
    public void OpenSettings()
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) { EnsureSettingsFocus(); return; }
        HistoryList.CancelWheelMotion();
        interactionVersion++;
        settingsReturnFocus = Keyboard.FocusedElement;
        if (SettingsContent.Content is not SettingsPanel) SettingsContent.Content = new SettingsPanel(this);
        ((SettingsPanel)SettingsContent.Content).RefreshFromSettings();
        SettingsOverlay.Visibility = Visibility.Visible;
        SettingsCard.BeginAnimation(OpacityProperty, null);
        SettingsOverlay.BeginAnimation(OpacityProperty, null);
        if (SystemParameters.ClientAreaAnimation)
            SettingsOverlay.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140)) {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }, FillBehavior = FillBehavior.Stop });
        // Let the normal layout pass finish; never force a full settings layout on the click path.
        int request = ++settingsFocusRequest;
        Dispatcher.BeginInvoke(() => { if (request == settingsFocusRequest) EnsureSettingsFocus(); }, DispatcherPriority.Loaded);
    }
    public void CloseSettings()
    {
        interactionVersion++;
        settingsFocusRequest++;
        SettingsOverlay.BeginAnimation(OpacityProperty, null);
        SettingsOverlay.Visibility = Visibility.Collapsed; Integration?.RegisterHotKey(Store.Settings.GlobalHotKey);
        if (settingsReturnFocus is DependencyObject previous && IsWithin(previous, HistoryList)) RestoreHistoryFocus();
        else if (settingsReturnFocus is UIElement { IsVisible: true, IsEnabled: true } control) control.Focus();
        else RestoreHistoryFocus();
        settingsReturnFocus = null;
    }
    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) { if (e.OriginalSource == SettingsOverlay) CloseSettings(); }
    private void Card_MouseDown(object sender, MouseButtonEventArgs e) { e.Handled = true; }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void Hide_Click(object sender, RoutedEventArgs e) { if (Store.Settings.CloseToTray) Hide(); else Quit(); }
    private void OnClosing(object? sender, CancelEventArgs e) { if (!quitting) { e.Cancel = true; if (Store.Settings.CloseToTray) Hide(); else Quit(); } }
    public async void Quit()
    {
        if (quitting) return;
        if (!UpdateInstalling) updateCancellation?.Cancel();
        quitting = true; IsEnabled = false;
        CloseTrayContextMenu();
        searchCancellation?.Cancel(); searchCancellation?.Dispose(); searchCancellation = null;
        Integration?.Dispose(); Integration = null;
        Store.Settings.WindowWidth = RestoreBounds.Width; Store.Settings.WindowHeight = RestoreBounds.Height - 30; Store.SaveSettings();
        bool saved = await Store.FlushAsync();
        if (saved) saved = Store.Flush();
        if (!saved)
        {
            UpdateExitFailed("历史尚未保存，已暂缓安装。请检查磁盘空间后重试。");
            // Keep unsaved snapshots and the app available for retry instead of silently losing data.
            quitting = false; IsEnabled = true; if (!demo) ConnectIntegration(); Refresh();
            ShowShelf(); ShowStatus("历史记录尚未保存，已暂缓退出。请检查磁盘空间后重试。"); return;
        }
        if (pendingUpdateStage is { } stage)
        {
            try { await WindowsUpdateService.LaunchInstallerAsync(stage); }
            catch { quitting = false; IsEnabled = true; if (!demo) ConnectIntegration(); UpdateExitFailed("安装助手未能启动，已保留当前版本。请重试。"); return; }
        }
        updateCancellation?.Cancel(); updates.Dispose();
        Store.Changed -= StoreChanged; SystemEvents.UserPreferenceChanged -= SystemAppearanceChanged;
        toastTimer.Stop(); StopDragFrames(); DisposeTray();
        if (preview is { } activePreview) await activePreview.CloseAndReleaseAsync();
        previewCache.Dispose(); Close(); Application.Current.Shutdown();
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int RegisterWindowMessage(string name);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
