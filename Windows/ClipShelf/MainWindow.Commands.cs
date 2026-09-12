using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

public partial class MainWindow
{
    private Guid? pendingDeselectId;
    private bool activationClickPending, activationPointer;
    private ContextMenu? historyContextMenu;
    private sealed record HistoryMenuCommand(MenuItem Item, string Gesture, Action Execute);
    private readonly ConditionalWeakTable<ContextMenu, List<HistoryMenuCommand>> historyMenuCommands = new();

    private void InitializeHistoryInteraction()
    {
        PreviewMouseDown += (_, _) => { activationPointer = activationClickPending; activationClickPending = false; interactionVersion++; HistoryList.CancelWheelMotion(); };
        PreviewMouseWheel += (_, _) => interactionVersion++;
        GotKeyboardFocus += (_, e) => { interactionVersion++; KeepFocusInsideSettings(e); };
        Activated += (_, _) => { interactionVersion++; EnsureSettingsFocus(); };
        Deactivated += (_, _) => { interactionVersion++; HistoryList.CancelWheelMotion(); };
        IsVisibleChanged += (_, _) => interactionVersion++;
        HistoryList.GotKeyboardFocus += (_, e) => {
            // The actual container is authoritative, including Tab and accessibility focus.
            if (Ancestor<ListBoxItem>(e.NewFocus as DependencyObject)?.DataContext is ClipItem item)
                focusedId = item.Id;
        };
        HistoryList.PreviewMouseRightButtonDown += (_, e) => {
            if (Ancestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
            var row = Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject);
            if (row?.DataContext is ClipItem item)
            {
                int index = visible.IndexOf(item);
                if (!HistoryList.SelectedItems.Contains(item)) CollapseSelection(index);
                FocusHistoryItem(index, scrollIntoView: false);
            }
            e.Handled = true;
        };
        HistoryList.PreviewMouseRightButtonUp += (_, e) => {
            if (Ancestor<ScrollBar>(e.OriginalSource as DependencyObject) is not null) return;
            OpenHistoryContextMenu(Ancestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as ClipItem, keyboard: false);
            e.Handled = true;
        };
        PreviewKeyUp += (_, _) => {
            if ((Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != (ModifierKeys.Control | ModifierKeys.Shift))
                keyboardRangeBase = null;
        };
    }

    internal void HandleApplicationDeactivated() { FocusCuePolicy.Reset(this); interactionVersion++; HistoryList.CancelWheelMotion(); EndDrag(); }

    private void ResetRangeCache() { rangeStart = rangeEnd = -1; cachedRangeBase = null; }

    internal void ApplyRowSelection(int index, ModifierKeys modifiers)
    {
        interactionVersion++;
        if (index < 0 || index >= visible.Count) return;
        keyboardRangeBase = null;
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
        int start = anchorId is Guid id ? visible.FindIndex(item => item.Id == id) : -1;
        if (shift)
        {
            if (start < 0) start = focusedId is Guid focused ? visible.FindIndex(item => item.Id == focused) : index;
            if (start < 0) start = index;
            SelectRangeWithBase(start, index, ctrl ? Selected().Select(item => item.Id).ToHashSet() : null);
        }
        else if (ctrl)
        {
            var item = visible[index];
            if (HistoryList.SelectedItems.Contains(item)) HistoryList.SelectedItems.Remove(item);
            else HistoryList.SelectedItems.Add(item);
            anchor = index; anchorId = focusedId = item.Id;
        }
        else if (CanDeselectOnClick(index, modifiers))
        {
            HistoryList.ReplaceSelection(Array.Empty<ClipItem>());
            // A cleared selection still has a useful keyboard position and Shift anchor.
            anchor = index; anchorId = focusedId = visible[index].Id;
        }
        else CollapseSelection(index);
    }

    private bool CanDeselectOnClick(int index, ModifierKeys modifiers) =>
        modifiers == ModifierKeys.None && Store.Settings.DeselectOnRepeatedClick
        && index >= 0 && index < visible.Count && HistoryList.SelectedItems.Count == 1
        && HistoryList.SelectedItems.Contains(visible[index]);

    internal void BeginRowPointer(int index, Point point, ModifierKeys modifiers, bool activationOnly = false)
    {
        HistoryList.CancelWheelMotion();
        if (index < 0 || index >= visible.Count) return;
        // Take the snapshot before Ctrl+click toggles its starting row.
        dragBaseSelection = modifiers.HasFlag(ModifierKeys.Control) ? Selected().Select(item => item.Id).ToHashSet() : null;
        pointerDown = true; dragging = false; suppressDragRelease = false;
        downIndex = index; downPoint = point;
        if (activationOnly) { pendingDeselectId = null; pointerDown = false; interactionVersion++; return; }
        // Resolve a repeat click on release so starting a drag never clears the
        // selected row for one frame. Capture loss/cancel must never commit it.
        pendingDeselectId = CanDeselectOnClick(index, modifiers) ? visible[index].Id : null;
        if (pendingDeselectId is null) ApplyRowSelection(index, modifiers);
        else { interactionVersion++; keyboardRangeBase = null; }
    }

    internal bool CompleteRowPointer(Point point)
    {
        bool wasDragging = dragging || suppressDragRelease;
        Guid? candidate = pointerDown && !wasDragging ? pendingDeselectId : null;
        bool withinList = point.X >= 0 && point.X < HistoryList.ActualWidth
            && point.Y >= 0 && point.Y < HistoryList.ActualHeight;
        int releasedIndex = withinList ? (int)Math.Floor((point.Y + (HistoryScroll?.VerticalOffset ?? 0)) / RowHeight) : -1;
        bool releasedOnCandidate = candidate is not null && releasedIndex >= 0 && releasedIndex < visible.Count
            && visible[releasedIndex].Id == candidate;
        EndDrag();
        // EndDrag may apply a queued history update: resolve the stable identity
        // again and cancel if selection or preferences changed in the meantime.
        int index = releasedOnCandidate ? visible.FindIndex(item => item.Id == candidate) : -1;
        bool deselected = CanDeselectOnClick(index, ModifierKeys.None);
        if (deselected) ApplyRowSelection(index, ModifierKeys.None);
        return wasDragging || deselected;
    }

    private int FocusedHistoryIndex()
    {
        int index = focusedId is Guid id ? visible.FindIndex(item => item.Id == id) : -1;
        return index >= 0 ? index : visible.FindIndex(item => HistoryList.SelectedItems.Contains(item));
    }

    private void RestoreHistoryFocus(bool scrollIntoView = true)
    {
        if (SettingsOverlay.Visibility == Visibility.Visible) { EnsureSettingsFocus(); return; }
        int index = FocusedHistoryIndex();
        if (index >= 0) FocusHistoryItem(index, scrollIntoView);
        else HistoryList.Focus();
    }

    private void FocusHistoryItem(int index, bool scrollIntoView = true)
    {
        if (scrollIntoView) HistoryList.CancelWheelMotion();
        if (index < 0 || index >= visible.Count) return;
        var item = visible[index]; focusedId = item.Id;
        if (scrollIntoView) { HistoryList.ScrollIntoView(item); HistoryList.UpdateLayout(); }
        var container = HistoryList.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem;
        if (container is null) return;
        if (ReferenceEquals(Keyboard.FocusedElement, container)) return;
        // Focusing a WPF ListBoxItem can trigger native selection behavior; preserve our
        // explicit selection, especially Ctrl+arrows and the Ctrl+Space focus-only case.
        var selection = HistoryList.SelectedItems.Cast<ClipItem>().ToArray();
        container.Focus();
        if (!selection.ToHashSet().SetEquals(HistoryList.SelectedItems.Cast<ClipItem>())) HistoryList.ReplaceSelection(selection);
    }

    private void NavigateHistory(Key key, ModifierKeys modifiers)
    {
        if (visible.Count == 0) return;
        int current = FocusedHistoryIndex();
        int page = Math.Max(1, (int)((HistoryScroll?.ViewportHeight ?? RowHeight) / RowHeight));
        if (current < 0)
        {
            int first = Math.Clamp((int)((HistoryScroll?.VerticalOffset ?? 0) / RowHeight), 0, visible.Count - 1);
            int last = Math.Clamp((int)Math.Ceiling(((HistoryScroll?.VerticalOffset ?? 0) + (HistoryScroll?.ViewportHeight ?? RowHeight)) / RowHeight) - 1, first, visible.Count - 1);
            current = key is Key.Up or Key.PageUp ? last + 1 : first - 1;
        }
        int index = key switch {
            Key.Home => 0, Key.End => visible.Count - 1,
            Key.PageUp => current - page, Key.PageDown => current + page,
            Key.Up => current - 1, _ => current + 1
        };
        index = Math.Clamp(index, 0, visible.Count - 1);
        bool ctrl = modifiers.HasFlag(ModifierKeys.Control), shift = modifiers.HasFlag(ModifierKeys.Shift);
        if (shift)
        {
            int start = anchorId is Guid id ? visible.FindIndex(item => item.Id == id) : -1;
            if (start < 0) start = Math.Clamp(current, 0, visible.Count - 1);
            if (ctrl) keyboardRangeBase ??= Selected().Select(item => item.Id).ToHashSet();
            else keyboardRangeBase = null;
            SelectRangeWithBase(start, index, keyboardRangeBase);
        }
        else
        {
            keyboardRangeBase = null;
            if (!ctrl) CollapseSelection(index);
        }
        FocusHistoryItem(index);
    }

    private static bool IsWithin(DependencyObject? item, DependencyObject parent)
    {
        while (item is not null)
        {
            if (ReferenceEquals(item, parent)) return true;
            item = item is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(item) : LogicalTreeHelper.GetParent(item);
        }
        return false;
    }

    private bool IsHistoryContentOrigin(DependencyObject? origin) => IsWithin(origin, HistoryList)
        && Ancestor<ButtonBase>(origin) is null && Ancestor<TextBoxBase>(origin) is null
        && Ancestor<ScrollBar>(origin) is null;

    internal static bool IsContextMenuGesture(Key key, ModifierKeys modifiers) =>
        (key == Key.Apps && modifiers == ModifierKeys.None) || (key == Key.F10 && modifiers == ModifierKeys.Shift);

    private static bool MatchesShortcut(Key key, ModifierKeys modifiers, string shortcut)
    {
        if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) == 0) return false;
        string value = (modifiers.HasFlag(ModifierKeys.Control) ? "Ctrl+" : "")
            + (modifiers.HasFlag(ModifierKeys.Alt) ? "Alt+" : "")
            + (modifiers.HasFlag(ModifierKeys.Shift) ? "Shift+" : "")
            + (modifiers.HasFlag(ModifierKeys.Windows) ? "Win+" : "")
            + new KeyConverter().ConvertToInvariantString(key);
        return string.Equals(value, shortcut, StringComparison.OrdinalIgnoreCase);
    }

    internal async Task<bool> HandleHistoryKeyAsync(Key key, ModifierKeys modifiers, DependencyObject? origin)
    {
        interactionVersion++;
        if (quitting) return false;
        if (preview?.IsVisible == true) return true;
        if (SettingsOverlay.Visibility == Visibility.Visible)
        {
            if (key == Key.Escape && modifiers == ModifierKeys.None && Ancestor<TextBox>(origin)?.Tag as string != "ShortcutRecorder") { CloseSettings(); return true; }
            return false;
        }
        if (key == Key.F && modifiers == ModifierKeys.Control) { SearchBox.Focus(); SearchBox.SelectAll(); return true; }
        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Clear();
            else if (HistoryList.SelectedItems.Count > 0) ClearSelectionState();
            else Hide();
            return true;
        }
        bool searchOrigin = IsWithin(origin, SearchBox);
        if (searchOrigin)
        {
            // Let TextBox own every editing shortcut, including Ctrl+Z and Shift+Home/End.
            if (modifiers != ModifierKeys.None || key is not (Key.Up or Key.Down)) return false;
            if (!await WaitForSearchForKey()) return true;
            NavigateHistory(key, modifiers);
            return true;
        }
        if (Ancestor<TextBoxBase>(origin) is not null) return false;
        if (key == Key.Z && modifiers == ModifierKeys.Control) { UndoHistoryDelete(); return true; }
        if (IsContextMenuGesture(key, modifiers) && IsWithin(origin, HistoryList))
        {
            var item = Ancestor<ListBoxItem>(origin)?.DataContext as ClipItem;
            item ??= visible.FirstOrDefault(clip => clip.Id == focusedId) ?? Selected().FirstOrDefault();
            OpenHistoryContextMenu(item, keyboard: true); return true;
        }
        // In particular, Enter/Space must reach an actual focused Button unchanged.
        if (!IsHistoryContentOrigin(origin)) return false;
        if (Ancestor<ListBoxItem>(origin)?.DataContext is ClipItem focusedItem) focusedId = focusedItem.Id;
        if (!HistoryShortcutPolicy.IsReserved(Store.Settings.ClearSelectionHotKey) && MatchesShortcut(key, modifiers, Store.Settings.ClearSelectionHotKey)) { ClearSelectionState(); return true; }
        if (!HistoryShortcutPolicy.IsReserved(Store.Settings.PinHotKey) && MatchesShortcut(key, modifiers, Store.Settings.PinHotKey)) { Store.TogglePinned(Selected().Select(item => item.Id)); return true; }
        if ((modifiers & (ModifierKeys.Alt | ModifierKeys.Windows)) != 0) return false;
        if (key is Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown)
        {
            if (await WaitForSearchForKey()) NavigateHistory(key, modifiers);
            return true;
        }
        if (modifiers == ModifierKeys.Control)
        {
            if (key == Key.A)
            {
                HistoryList.SelectAll();
                int index = Math.Max(0, FocusedHistoryIndex());
                if (visible.Count > 0) { anchor = index; anchorId = visible[index].Id; FocusHistoryItem(index); }
                return true;
            }
            if (key == Key.C) { await CopyItems(Selected()); return true; }
            if (key == Key.Space)
            {
                int index = FocusedHistoryIndex();
                if (index >= 0) { ApplyRowSelection(index, ModifierKeys.Control); FocusHistoryItem(index); }
                return true;
            }
        }
        if (modifiers != ModifierKeys.None) return false;
        if (key == Key.Space) { TogglePreview(); return true; }
        if (key == Key.Delete) { DeleteSelection(); return true; }
        return false;
    }

    private async Task<bool> WaitForSearchForKey()
    {
        var pending = PendingSearch;
        var focus = Keyboard.FocusedElement;
        long version = interactionVersion;
        bool visibleAtStart = IsVisible, activeAtStart = IsActive, enabledAtStart = IsEnabled;
        var settingsAtStart = SettingsOverlay.Visibility;
        var stateAtStart = WindowState;
        await pending;
        // Compare before/after, not against "active=true": isolated/off-screen fixtures
        // are valid too, but a delayed key must never follow the user into a new context.
        return pending == PendingSearch && !quitting && interactionVersion == version
            && ReferenceEquals(Keyboard.FocusedElement, focus)
            && IsVisible == visibleAtStart && IsActive == activeAtStart && IsEnabled == enabledAtStart
            && SettingsOverlay.Visibility == settingsAtStart && WindowState == stateAtStart;
    }

    private void DeleteHistoryItems(IReadOnlyList<ClipItem> items)
    {
        var ids = items.Select(item => item.Id).ToHashSet();
        int count = Store.Items.Count(item => ids.Contains(item.Id));
        if (count == 0) return;
        Store.Remove(ids); UpdateActions();
        ShowStatus($"已删除 {count} 条记录 · Ctrl+Z 撤销");
    }

    private void UndoDelete_Click(object sender, RoutedEventArgs e) => UndoHistoryDelete();
    private void UndoHistoryDelete()
    {
        if (!Store.CanUndoDelete) return;
        int restored = Store.UndoDelete(); UpdateActions();
        ShowStatus(restored == 0 ? "该批记录已存在或历史已满，未恢复记录" : $"已恢复 {restored} 条记录；已有内容和新记录优先保留");
    }

    internal ContextMenu BuildHistoryContextMenu(ClipItem? item)
    {
        HistoryList.CancelWheelMotion();
        interactionVersion++;
        if (item is not null && visible.Contains(item))
        {
            if (!HistoryList.SelectedItems.Contains(item)) CollapseSelection(visible.IndexOf(item));
            focusedId = item.Id;
        }
        var menu = new ContextMenu { Style = (Style)FindResource("FluentContextMenu") };
        var commands = new List<HistoryMenuCommand>(); historyMenuCommands.Add(menu, commands);
        menu.PreviewKeyDown += (_, e) => {
            if (HandleHistoryMenuKey(menu, e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers)) e.Handled = true;
        };
        AutomationProperties.SetName(menu, item is null ? "历史记录空白区域菜单" : $"所选 {HistoryList.SelectedItems.Count} 条记录的操作");
        MenuItem Add(string text, string gesture, string glyph, Action action, bool enabled = true, bool shortcutPriority = false)
        {
            var icon = new TextBlock { Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var entry = new MenuItem { Header = text, InputGestureText = gesture, IsEnabled = enabled,
                Icon = icon, Style = (Style)FindResource("FluentMenuItem") };
            AutomationProperties.SetName(entry, text);
            entry.Click += (_, _) => { menu.IsOpen = false; action(); };
            var command = new HistoryMenuCommand(entry, gesture, action);
            if (shortcutPriority) commands.Insert(0, command); else commands.Add(command);
            menu.Items.Add(entry); return entry;
        }
        if (item is not null)
        {
            var items = Selected().ToArray(); string count = items.Length > 1 ? $" {items.Length} 条记录" : "";
            Add("复制" + count, "Ctrl+C", "\uE8C8", async () => await CopyItems(items), items.Length > 0);
            Add("预览", "Space", "\uE890", () => PreviewHistoryItem(items[0]), items.Length == 1);
            string pinGesture = HistoryShortcutPolicy.IsReserved(Store.Settings.PinHotKey) ? "" : Store.Settings.PinHotKey;
            Add((items.All(clip => clip.IsPinned) ? "取消置顶" : "置顶") + count, pinGesture, "\uE718",
                () => Store.TogglePinned(items.Select(clip => clip.Id)), items.Length > 0, shortcutPriority: true);
            Add("删除" + count, "Delete", "\uE74D", () => DeleteHistoryItems(items), items.Length > 0);
            menu.Items.Add(new Separator { Style = (Style)FindResource("FluentMenuSeparator") });
        }
        Add("撤销删除", "Ctrl+Z", "\uE7A7", UndoHistoryDelete, Store.CanUndoDelete);
        if (item is null) Add("清空全部历史记录…（不可撤销）", "", "\uE74D", ConfirmClearHistory, Store.Items.Count > 0);
        return menu;
    }

    internal bool HandleHistoryMenuKey(ContextMenu menu, Key key, ModifierKeys modifiers)
    {
        // ContextMenu is a separate popup route; use exactly the menu's captured targets.
        // Enter/Space activate the highlighted menu item; navigation remains native.
        if (key is Key.Enter or Key.Space or Key.Up or Key.Down or Key.Left or Key.Right or Key.Escape) return false;
        if (!historyMenuCommands.TryGetValue(menu, out var commands)) return false;
        var command = commands.FirstOrDefault(candidate => candidate.Gesture switch {
            "Space" => false,
            "Delete" => key == Key.Delete && modifiers == ModifierKeys.None,
            "" => false,
            _ => MatchesShortcut(key, modifiers, candidate.Gesture)
        });
        if (command is null) return false;
        if (!command.Item.IsEnabled) return true;
        menu.IsOpen = false; command.Execute(); return true;
    }

    private void OpenHistoryContextMenu(ClipItem? item, bool keyboard)
    {
        EndDrag();
        if (historyContextMenu is not null) historyContextMenu.IsOpen = false;
        var menu = BuildHistoryContextMenu(item);
        menu.PlacementTarget = item is null ? HistoryList : HistoryList.ItemContainerGenerator.ContainerFromItem(item) as UIElement ?? HistoryList;
        menu.Placement = keyboard ? PlacementMode.Bottom : PlacementMode.MousePoint;
        historyContextMenu = menu; menu.IsOpen = true;
    }

    private void PreviewHistoryItem(ClipItem item)
    {
        int index = visible.IndexOf(item);
        if (index < 0) return;
        OpenDocumentPreview(index);
    }

    private void OpenDocumentPreview(int index)
    {
        if (index < 0 || index >= visible.Count) return;
        if (preview?.IsVisible == true) { preview.Activate(); return; }
        var window = new PreviewWindow(visible.ToArray(), index, previewCache) { Owner = this };
        if (HistoryList.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement row)
            window.SetAnimationOrigin(row.TranslatePoint(new Point(0, row.ActualHeight / 2), this).Y / Math.Max(1, ActualHeight));
        preview = window;
        window.Closed += (_, _) => {
            if (ReferenceEquals(preview, window)) preview = null;
            Dispatcher.BeginInvoke(new Action(() => { if (IsActive && !quitting) RestoreHistoryFocus(false); }));
        };
        window.Show();
    }
}
