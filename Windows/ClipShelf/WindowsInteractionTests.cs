using System;
using System.Collections.Generic;
using System.Diagnostics;
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
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>
/// Exercises the application's own interaction handlers using explicit modifiers and synthetic data.
/// No SendInput, clipboard service, global shortcut, real mouse event, or foreground activation is used.
/// </summary>
public static class WindowsInteractionTests
{
    public static async Task RunAsync(string reportPath)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        var checks = new List<string>();
        var watch = Stopwatch.StartNew();
        string fixtureDirectory = Path.Combine(Path.GetTempPath(), "ClipShelf-interactions-" + Guid.NewGuid().ToString("N"));
        MainWindow? window = null;
        HistoryStore? store = null;
        string? error = null;

        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException("Interaction regression: " + name);
            checks.Add("PASS " + name);
        }

        try
        {
            store = new HistoryStore(fixtureDirectory, deferredPersistence: true);
            store.Settings.WatchScreenshots = false;
            store.Settings.HistoryEnabled = false;
            store.Settings.LaunchAtLogin = false;
            var created = DateTimeOffset.Now;
            var seed = Enumerable.Range(0, 8).Select(index => new ClipItem
            {
                Title = $"Interaction fixture {index}: 合成记录",
                Text = $"Interaction fixture text {index}",
                CreatedAt = created.AddSeconds(-index)
            }).ToArray();
            foreach (var item in seed) store.Add(item);
            Check(await store.FlushAsync(), "Synthetic interaction fixture persists under its own temporary directory");
            window = new MainWindow(store, demo: true)
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -12000, Top = -12000, Width = 960, Height = 900,
                ShowActivated = false, ShowInTaskbar = false
            };
            window.Show();
            await IdleAsync();
            var list = Named<HistoryListBox>(window, "HistoryList");
            var search = Named<TextBox>(window, "SearchBox");
            var toolbarPin = Named<Button>(window, "PinButton");
            var toolbarDelete = Named<Button>(window, "DeleteButton");
            var toolbarUndo = Named<Button>(window, "UndoDeleteButton");
            var selectionCount = Named<TextBlock>(window, "SelectionCountText");
            Check(window.Integration is null && list.Items.Count == seed.Length,
                "Demo fixture has no native clipboard integration and contains only synthetic records");
            var keyboardFocusVisual = window.FindResource("KeyboardFocusVisual");
            var firstContainer = list.ItemContainerGenerator.ContainerFromItem(seed[0]) as ListBoxItem
                ?? throw new InvalidOperationException("Initial interaction row was not realized.");
            Check(ReferenceEquals(firstContainer.FocusVisualStyle, window.FindResource("RowKeyboardFocusVisual")) &&
                AutomationProperties.GetName(firstContainer) == seed[0].DisplayTitle,
                "History rows expose their title and use the keyboard-only focus visual");
            Check(new[] { toolbarPin, toolbarDelete, toolbarUndo }.All(button =>
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(button)) && ReferenceEquals(button.FocusVisualStyle, keyboardFocusVisual)),
                "Pin, Delete, and Undo toolbar controls have accessible names and keyboard focus visuals");
            var settingsOverlay = Named<Grid>(window, "SettingsOverlay");
            Check(KeyboardNavigation.GetTabNavigation(settingsOverlay) == KeyboardNavigationMode.Cycle &&
                KeyboardNavigation.GetControlTabNavigation(settingsOverlay) == KeyboardNavigationMode.Cycle &&
                FocusManager.GetIsFocusScope(settingsOverlay),
                "Settings establishes a focus scope with Tab and Ctrl+Tab cycling");
            Check(AutomationProperties.GetLiveSetting(selectionCount) == AutomationLiveSetting.Polite,
                "Selection count exposes a polite accessibility live region");

            void SelectExactly(params int[] indices)
            {
                if (indices.Length == 0) { Invoke(window, "ClearSelectionState"); return; }
                window.ApplyRowSelection(indices[0], ModifierKeys.None);
                foreach (int index in indices.Skip(1)) window.ApplyRowSelection(index, ModifierKeys.Control);
            }

            bool SelectedIndices(params int[] indices) => SelectedIds(list).SetEquals(indices.Select(index => ((ClipItem)list.Items[index]).Id));

            window.ApplyRowSelection(0, ModifierKeys.None);
            window.ApplyRowSelection(3, ModifierKeys.None);
            Check(SelectedIndices(3), "Clicking B while A is selected immediately selects B");
            window.ApplyRowSelection(3, ModifierKeys.None);
            Check(SelectedIndices(3), "Clicking the sole selected row preserves its selection");
            window.ApplyRowSelection(1, ModifierKeys.Control);
            Check(SelectedIndices(1, 3), "Ctrl-click adds an unselected row without clearing the existing row");
            Check(selectionCount.Visibility == Visibility.Visible && selectionCount.Text.Contains("2", StringComparison.Ordinal) &&
                AutomationProperties.GetName(selectionCount) == selectionCount.Text,
                "Multi-selection updates the visible and accessible selection count");
            window.ApplyRowSelection(3, ModifierKeys.Control);
            Check(SelectedIndices(1), "Ctrl-click removes exactly the clicked selected row");

            SelectExactly(1);
            window.ApplyRowSelection(4, ModifierKeys.Shift);
            Check(SelectedIndices(1, 2, 3, 4), "Shift-click selects the inclusive anchor range");
            SelectExactly(0, 5);
            window.ApplyRowSelection(3, ModifierKeys.Control | ModifierKeys.Shift);
            Check(SelectedIndices(0, 3, 4, 5), "Ctrl+Shift-click adds a range while preserving a disjoint selected row");

            SelectExactly(0, 6);
            var scroll = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("The synthetic list has no scroll viewer.");
            scroll.ScrollToTop();
            await IdleAsync();
            window.BeginRowPointer(2, new Point(20, 2.5 * 74), ModifierKeys.Control);
            Invoke(window, "UpdateDragSelection", new Point(20, 4.5 * 74));
            Check(SelectedIndices(0, 2, 3, 4, 6), "Ctrl-drag adds the dragged range and retains prior disjoint selection");
            Invoke(window, "EndDrag", true);
            SelectExactly(0, 2);
            var retainedOnDeactivate = SelectedIds(list);
            window.HandleApplicationDeactivated();
            Check(SelectedIds(list).SetEquals(retainedOnDeactivate), "Application deactivation preserves the selected rows");

            var first = (ClipItem)list.Items[0];
            var third = (ClipItem)list.Items[2];
            Check(window.RowItems(first).Select(item => item.Id).SequenceEqual([first.Id]),
                "An inline copy target resolves only its own row during multi-selection");
            var rowPin = await RowButtonAsync(list, first, "Pin");
            rowPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, rowPin));
            await IdleAsync();
            Check(store.Items.Single(item => item.Id == first.Id).IsPinned && !store.Items.Single(item => item.Id == third.Id).IsPinned,
                "The inline pin button changes its row only");
            toolbarPin.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toolbarPin));
            await IdleAsync();
            Check(store.Items.Single(item => item.Id == first.Id).IsPinned && store.Items.Single(item => item.Id == third.Id).IsPinned,
                "The toolbar pin button applies to the selected group");
            store.TogglePinned([first.Id, third.Id]);
            await IdleAsync();

            SelectExactly(0, 2);
            var rowDelete = await RowButtonAsync(list, first, "Delete");
            rowDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, rowDelete));
            await IdleAsync();
            Check(store.Items.Count == seed.Length - 1 && store.Items.All(item => item.Id != first.Id) && store.Items.Any(item => item.Id == third.Id),
                "The inline delete button removes one row even while several rows are selected");
            Check(store.CanUndoDelete, "An inline delete exposes undo availability");
            Check(toolbarUndo.IsEnabled, "Undo toolbar action becomes enabled after a deletion");
            Check(await window.HandleHistoryKeyAsync(Key.Z, ModifierKeys.Control, list), "List Ctrl+Z is handled as deletion undo");
            await IdleAsync();
            Check(store.Items.Count == seed.Length && store.Items.Any(item => item.Id == first.Id), "Ctrl+Z restores the deleted inline row");

            SelectExactly(0, 2);
            var batch = SelectedIds(list);
            Check(await window.HandleHistoryKeyAsync(Key.Delete, ModifierKeys.None, list), "List Delete is handled by the selected-record action");
            await IdleAsync();
            Check(store.Items.Count == seed.Length - batch.Count && store.Items.All(item => !batch.Contains(item.Id)),
                "List Delete removes the selected group only");
            Check(await window.HandleHistoryKeyAsync(Key.Z, ModifierKeys.Control, list), "Ctrl+Z handles a multi-record deletion");
            await IdleAsync();
            Check(store.Items.Count == seed.Length && batch.All(id => store.Items.Any(item => item.Id == id)),
                "A single Ctrl+Z restores the whole deleted group");

            SelectExactly(1, 4);
            var toolbarBatch = SelectedIds(list);
            toolbarDelete.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toolbarDelete));
            await IdleAsync();
            Check(store.Items.Count == seed.Length - toolbarBatch.Count && store.Items.All(item => !toolbarBatch.Contains(item.Id)),
                "The toolbar Delete button deletes the selected group");
            toolbarUndo.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toolbarUndo));
            await IdleAsync();
            Check(store.Items.Count == seed.Length && toolbarBatch.All(id => store.Items.Any(item => item.Id == id)),
                "The toolbar Undo button restores the group removed by toolbar Delete");

            SelectExactly();
            Check(!toolbarDelete.IsEnabled && selectionCount.Visibility == Visibility.Collapsed,
                "An empty selection disables toolbar Delete and hides the selection count");
            var beforeEmptyDelete = store.Items.Select(item => item.Id).ToHashSet();
            await window.HandleHistoryKeyAsync(Key.Delete, ModifierKeys.None, list);
            await IdleAsync();
            Check(beforeEmptyDelete.SetEquals(store.Items.Select(item => item.Id)),
                "Delete with no selected rows returns without deleting records or requesting confirmation");

            var searchUndoVictim = store.Items[1];
            store.Remove([searchUndoVictim.Id]);
            await IdleAsync();
            Check(store.CanUndoDelete, "Search-undo isolation fixture contains a restorable deletion");
            Check(!await window.HandleHistoryKeyAsync(Key.Z, ModifierKeys.Control, search),
                "Search-box Ctrl+Z is not intercepted by the window's history shortcut handler");
            Check(store.Items.All(item => item.Id != searchUndoVictim.Id) && store.CanUndoDelete,
                "Search-box undo does not consume the history deletion undo");
            store.UndoDelete();
            await IdleAsync();

            SelectExactly(0);
            var beforeButtonKeys = SelectedIds(list);
            Check(!await window.HandleHistoryKeyAsync(Key.Enter, ModifierKeys.None, toolbarPin),
                "Enter originating from a button is left for the button's own action");
            Check(!await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, toolbarPin),
                "Space originating from a button is left for the button's own action");
            Check(SelectedIds(list).SetEquals(beforeButtonKeys) && store.Items.Count == seed.Length,
                "Button keyboard routing does not trigger list copying, preview, or deletion");

            search.Clear();
            search.Text = "Interaction fixture";
            await window.PendingSearch;
            await IdleAsync();
            Check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.None, search),
                "Down from the search box is routed into the history list");
            Check(SelectedIndices(0), "Search-to-list Down selects the first visible result");
            Check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.None, list) && SelectedIndices(1),
                "Down in the list advances to the next result");
            Check(await window.HandleHistoryKeyAsync(Key.Up, ModifierKeys.None, list) && SelectedIndices(0),
                "Up in the list returns to the previous result");

            SelectExactly(4);
            Check(await window.HandleHistoryKeyAsync(Key.Home, ModifierKeys.None, list) && SelectedIndices(0) && FocusedIndex(window, list) == 0,
                "Home selects and logically focuses the first result");
            Check(await window.HandleHistoryKeyAsync(Key.End, ModifierKeys.None, list) && SelectedIndices(list.Items.Count - 1) && FocusedIndex(window, list) == list.Items.Count - 1,
                "End selects and logically focuses the last result");
            Check(scroll.ViewportHeight >= (list.Items.Count - 1) * 74,
                "Page-navigation fixture fits all eight synthetic rows within a page");
            Check(await window.HandleHistoryKeyAsync(Key.PageUp, ModifierKeys.None, list) && SelectedIndices(0),
                "PageUp reaches the first result when fewer than one page of rows precedes focus");
            Check(await window.HandleHistoryKeyAsync(Key.PageDown, ModifierKeys.None, list) && SelectedIndices(list.Items.Count - 1),
                "PageDown reaches the last result when fewer than one page of rows follows focus");

            SelectExactly(0, 4);
            var beforeFocusOnlyMove = SelectedIds(list);
            Check(await window.HandleHistoryKeyAsync(Key.Up, ModifierKeys.Control, list) && FocusedIndex(window, list) == 3 &&
                SelectedIds(list).SetEquals(beforeFocusOnlyMove),
                "Ctrl+Up moves logical focus without changing selected rows");
            Check(await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.Control, list) && FocusedIndex(window, list) == 3 && SelectedIndices(0, 3, 4),
                "Ctrl+Space adds only the logically focused row");
            Check(await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.Control, list) && FocusedIndex(window, list) == 3 && SelectedIndices(0, 4),
                "A second Ctrl+Space removes only the focused row and preserves focus");
            Check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.Control, list) && FocusedIndex(window, list) == 4 && SelectedIndices(0, 4),
                "Ctrl+Down changes logical focus without collapsing a disjoint selection");
            SelectExactly(0, 2);
            Check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.Control | ModifierKeys.Shift, list) && SelectedIndices(0, 2, 3),
                "Ctrl+Shift+Down adds the anchor range while preserving disjoint selection");
            Check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.Control | ModifierKeys.Shift, list) && SelectedIndices(0, 2, 3, 4),
                "A subsequent Ctrl+Shift+Down extends the same additive range");

            SelectExactly(0, 2);
            var selectedMenu = window.BuildHistoryContextMenu((ClipItem)list.Items[0]);
            Check(SelectedIndices(0, 2), "Building a context menu on an already selected row preserves the selected group");
            CheckMenuAccessibility(selectedMenu, Check);
            var outside = (ClipItem)list.Items[4];
            var outsideMenu = window.BuildHistoryContextMenu(outside);
            Check(SelectedIds(list).SetEquals([outside.Id]), "A context menu requested on an unselected row first selects that row");
            CheckMenuAccessibility(outsideMenu, Check);
            Check(ContextMenuGestureExists(window, Key.F10, ModifierKeys.Shift), "Shift+F10 is exposed as a history context-menu gesture");
            var blankMenu = window.BuildHistoryContextMenu(null);
            var blankActions = blankMenu.Items.OfType<MenuItem>().ToArray();
            var clearAction = blankActions.SingleOrDefault(item => (item.Header?.ToString() ?? "").Contains("清空", StringComparison.Ordinal));
            var undoAction = blankActions.SingleOrDefault(item => NormalizeGesture(item.InputGestureText) == "CTRL+Z");
            Check(clearAction is not null && undoAction is not null && !ReferenceEquals(clearAction, undoAction) &&
                !string.IsNullOrWhiteSpace(AutomationProperties.GetName(clearAction)) && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(undoAction)),
                "The blank-area context menu exposes separately labelled Clear History and Undo Delete actions");
            Check(NormalizeGesture(clearAction!.InputGestureText) != "DELETE",
                "Blank-area Clear History is not advertised as the Delete selection shortcut");
            Check(!selectedMenu.IsOpen && !outsideMenu.IsOpen && !blankMenu.IsOpen,
                "Menu-contract checks build actions without opening a native popup");
            var beforeNativeMenuKeys = SelectedIds(list);
            var beforeNativeMenuItems = store.Items.Select(item => item.Id).ToHashSet();
            foreach (var key in new[] { Key.Enter, Key.Space, Key.Escape, Key.Up, Key.Down, Key.Left, Key.Right })
                Check(!window.HandleHistoryMenuKey(outsideMenu, key, ModifierKeys.None),
                    "Context-menu " + key + " is left to native highlighted-item activation or navigation");
            Check(SelectedIds(list).SetEquals(beforeNativeMenuKeys) && beforeNativeMenuItems.SetEquals(store.Items.Select(item => item.Id)) &&
                !outsideMenu.IsOpen,
                "Passing through native menu keys does not change records or open a popup");
            Check(!window.HandleHistoryMenuKey(blankMenu, Key.Delete, ModifierKeys.None),
                "Delete in a blank-area menu cannot invoke Clear History");

            SelectExactly(0, 2);
            var capturedMenuIds = SelectedIds(list);
            var capturedMenu = window.BuildHistoryContextMenu((ClipItem)list.Items[0]);
            SelectExactly(5);
            var newlySelectedId = ((ClipItem)list.Items[5]).Id;
            Check(window.HandleHistoryMenuKey(capturedMenu, Key.Delete, ModifierKeys.None),
                "Context-menu Delete dispatches its captured selected-record command");
            await IdleAsync();
            await window.PendingSearch;
            await IdleAsync();
            Check(store.Items.Count == seed.Length - capturedMenuIds.Count && store.Items.All(item => !capturedMenuIds.Contains(item.Id)) &&
                store.Items.Any(item => item.Id == newlySelectedId),
                "Context-menu Delete uses the menu's original target snapshot, not a later selection");
            var menuUndo = window.BuildHistoryContextMenu(null);
            Check(window.HandleHistoryMenuKey(menuUndo, Key.Z, ModifierKeys.Control),
                "Blank-area context-menu Ctrl+Z dispatches deletion undo");
            await IdleAsync();
            await window.PendingSearch;
            await IdleAsync();
            Check(store.Items.Count == seed.Length && capturedMenuIds.All(id => store.Items.Any(item => item.Id == id)) &&
                !capturedMenu.IsOpen && !menuUndo.IsOpen,
                "Context-menu Ctrl+Z restores its deleted batch without opening a popup");

            var toast = Named<Border>(window, "Toast");
            var toastText = Named<TextBlock>(window, "ToastText");
            async Task CheckDelayedKeyCancellation(Key key, DependencyObject origin, string context, Action intervene, Action restore)
            {
                await window.PendingSearch;
                await IdleAsync();
                SelectExactly(1);
                var previousPending = window.PendingSearch;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                string marker = "Synthetic cancellation sentinel: " + key + "/" + context;
                toastText.Text = marker; toast.Visibility = Visibility.Collapsed;
                SetPendingSearch(window, gate.Task);
                try
                {
                    var command = window.HandleHistoryKeyAsync(key, ModifierKeys.None, origin);
                    Check(!command.IsCompleted, key + " from " + (ReferenceEquals(origin, search) ? "search" : "list") +
                        " waits for the controlled search task before " + context);
                    intervene();
                    await IdleAsync();
                    var selectionAfterIntervention = SelectedIds(list);
                    int focusAfterIntervention = FocusedIndex(window, list);
                    gate.SetResult();
                    Check(await command.WaitAsync(TimeSpan.FromSeconds(5)), "An invalidated delayed " + key + " is consumed after " + context);
                    await IdleAsync();
                    Check(toastText.Text == marker && toast.Visibility == Visibility.Collapsed &&
                        SelectedIds(list).SetEquals(selectionAfterIntervention) && FocusedIndex(window, list) == focusAfterIntervention,
                        "Delayed " + key + " performs no clipboard action and does not reclaim logical focus after " + context);
                }
                finally
                {
                    gate.TrySetResult();
                    SetPendingSearch(window, previousPending);
                    restore();
                    await IdleAsync();
                }
            }

            foreach (var key in new[] { Key.Up, Key.Down })
            {
                await CheckDelayedKeyCancellation(key, search, "opening settings", window.OpenSettings, window.CloseSettings);
                await CheckDelayedKeyCancellation(key, search, "a newer row selection", () => window.ApplyRowSelection(5, ModifierKeys.None), () => { });
                await CheckDelayedKeyCancellation(key, search, "hiding the window", window.Hide, window.Show);
            }
            await CheckDelayedKeyCancellation(Key.Down, list, "a newer list selection", () => window.ApplyRowSelection(5, ModifierKeys.None), () => { });
            foreach (var key in new[] { Key.Up, Key.Down })
            {
                SelectExactly(1);
                var previousPending = window.PendingSearch;
                var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                toastText.Text = "Synthetic successful-delay sentinel"; toast.Visibility = Visibility.Collapsed;
                SetPendingSearch(window, gate.Task);
                try
                {
                    var command = window.HandleHistoryKeyAsync(key, ModifierKeys.None, search);
                    Check(!command.IsCompleted, "An unchanged-context " + key + " also waits for the controlled search task");
                    gate.SetResult();
                    Check(await command.WaitAsync(TimeSpan.FromSeconds(5)), "An unchanged-context delayed " + key + " remains handled");
                    Check(SelectedIndices(key == Key.Up ? 0 : 2) && FocusedIndex(window, list) == (key == Key.Up ? 0 : 2),
                        "An unchanged-context delayed " + key + " still completes its expected demo action");
                }
                finally
                {
                    gate.TrySetResult(); SetPendingSearch(window, previousPending);
                }
            }

            await CheckRepeatedClickPreferenceAsync(window, store, list, Check);

            string originalTheme = store.Settings.Theme;
            int originalIcon = store.Settings.AppIcon;
            var settingsContent = Named<ContentControl>(window, "SettingsContent");
            var settingsPanel = new SettingsPanel(window);
            settingsContent.Content = settingsPanel;
            settingsOverlay.Visibility = Visibility.Visible;
            await IdleAsync();
            var originalSettingsButtons = Descendants<Button>(settingsPanel).ToArray();
            string nextTheme = originalTheme == "Dark" ? "Light" : "Dark";
            string themeLabel = nextTheme == "Dark" ? "深色" : "浅色";
            var themeButton = originalSettingsButtons.Single(button => button.Content as string == themeLabel);
            themeButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, themeButton));
            await IdleAsync();
            Check(store.Settings.Theme == nextTheme && ReferenceEquals(settingsContent.Content, settingsPanel) &&
                Descendants<Button>(settingsPanel).SequenceEqual(originalSettingsButtons) && IsDescendantOf(themeButton, settingsPanel),
                "Changing theme updates synthetic preferences without replacing settings buttons or their visual tree");
            int nextIcon = originalIcon == 2 ? 1 : 2;
            var iconButton = originalSettingsButtons.Single(button => AutomationProperties.GetName(button) == $"图标方案 {nextIcon}");
            iconButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, iconButton));
            await IdleAsync();
            Check(store.Settings.AppIcon == nextIcon && ReferenceEquals(settingsContent.Content, settingsPanel) &&
                Descendants<Button>(settingsPanel).SequenceEqual(originalSettingsButtons) && IsDescendantOf(iconButton, settingsPanel),
                "Changing the app icon keeps the original settings controls attached");

            var recorders = Descendants<TextBox>(settingsPanel).Where(input => input.Tag as string == "ShortcutRecorder").ToArray();
            Check(recorders.Length == 3, "The settings fixture exposes all three shortcut-recording inputs");
            var presentationSource = PresentationSource.FromVisual(window)
                ?? throw new InvalidOperationException("The off-screen interaction window has no presentation source.");
            foreach (var recorder in recorders)
            {
                string shortcutBefore = recorder.Text;
                var tab = new KeyEventArgs(Keyboard.PrimaryDevice, presentationSource, Environment.TickCount, Key.Tab)
                {
                    RoutedEvent = Keyboard.PreviewKeyDownEvent,
                    Source = recorder
                };
                recorder.RaiseEvent(tab);
                Check(!tab.Handled && recorder.Text == shortcutBefore,
                    "Shortcut recorder " + shortcutBefore + " lets Tab continue without recording or swallowing it");
            }
            Check(window.Integration is null, "Settings tests do not create native clipboard or startup integration");
            settingsOverlay.Visibility = Visibility.Collapsed;
            settingsContent.Content = null;
            store.Settings.Theme = originalTheme;
            store.Settings.AppIcon = originalIcon;
            window.ApplyPreferences();
            Check(await store.FlushAsync() && store.LastError is null, "Synthetic interaction changes finish without persistence errors");
        }
        catch (Exception exception)
        {
            error = exception is TargetInvocationException { InnerException: { } inner } ? inner.ToString() : exception.ToString();
            Environment.ExitCode = 1;
        }

        try
        {
            string absoluteReportPath = Path.GetFullPath(reportPath);
            Directory.CreateDirectory(Path.GetDirectoryName(absoluteReportPath)!);
            File.WriteAllText(absoluteReportPath, JsonSerializer.Serialize(new
            {
                passed = error is null,
                checks,
                error,
                fixtureDirectory,
                durationSeconds = Math.Round(watch.Elapsed.TotalSeconds, 3),
                scope = "Off-screen synthetic WPF window; explicit modifier and origin routing; no system input, clipboard access, global shortcut, or context-menu popup. OS focus transfer is not asserted."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            if (window is not null) window.Quit();
            else Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static async Task CheckRepeatedClickPreferenceAsync(MainWindow window, HistoryStore store,
        HistoryListBox list, Action<bool, string> check)
    {
        // Keep every preference file and pointer event inside this existing synthetic fixture.
        var legacyDirectory = Path.Combine(store.DirectoryPath, "legacy-repeated-click");
        Directory.CreateDirectory(legacyDirectory);
        File.WriteAllText(Path.Combine(legacyDirectory, "settings.json"), "{\"Theme\":\"Dark\"}");
        var legacyStore = new HistoryStore(legacyDirectory, deferredPersistence: true);
        check(!new AppSettings().DeselectOnRepeatedClick && !legacyStore.Settings.DeselectOnRepeatedClick
            && legacyStore.Settings.Theme == "Dark", "New and legacy settings without the repeated-click field default to disabled");
        legacyStore.Settings.DeselectOnRepeatedClick = true; legacyStore.SaveSettings();
        check(await legacyStore.FlushAsync()
            && new HistoryStore(legacyDirectory, deferredPersistence: true).Settings.DeselectOnRepeatedClick,
            "The repeated-click enabled preference survives save and reload");
        legacyStore.Settings.DeselectOnRepeatedClick = false; legacyStore.SaveSettings();
        check(await legacyStore.FlushAsync()
            && !new HistoryStore(legacyDirectory, deferredPersistence: true).Settings.DeselectOnRepeatedClick,
            "The repeated-click disabled preference survives save and reload");

        var settingsContent = Named<ContentControl>(window, "SettingsContent");
        var settingsOverlay = Named<Grid>(window, "SettingsOverlay");
        var count = Named<TextBlock>(window, "SelectionCountText");
        var badge = Named<Border>(window, "SelectionBadge");
        var delete = Named<Button>(window, "DeleteButton");
        var scroll = Find<ScrollViewer>(list) ?? throw new InvalidOperationException("Repeated-click fixture has no scroller.");
        var previousContent = settingsContent.Content;
        var previousOverlay = settingsOverlay.Visibility;
        bool previousPreference = store.Settings.DeselectOnRepeatedClick;
        int itemCount = store.Items.Count;
        bool Selected(params int[] indices) => SelectedIds(list).SetEquals(indices.Select(index => ((ClipItem)list.Items[index]).Id));
        void Select(params int[] indices)
        {
            Invoke(window, "EndDrag", true); Invoke(window, "ClearSelectionState");
            if (indices.Length == 0) return;
            window.ApplyRowSelection(indices[0], ModifierKeys.None);
            foreach (int index in indices.Skip(1)) window.ApplyRowSelection(index, ModifierKeys.Control);
        }
        int AnchorIndex()
        {
            var anchor = typeof(MainWindow).GetField("anchorId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
            return anchor is Guid id ? list.Items.Cast<ClipItem>().ToList().FindIndex(item => item.Id == id) : -1;
        }
        Point At(int index) => new(20, (index + 0.5) * 74 - scroll.VerticalOffset);
        void End() => Invoke(window, "EndDrag", true);
        int selectionEvents = 0;
        SelectionChangedEventHandler changed = (_, _) => selectionEvents++;
        list.SelectionChanged += changed;
        try
        {
            store.Settings.DeselectOnRepeatedClick = false;
            Select(1); window.ApplyRowSelection(1, ModifierKeys.None);
            check(Selected(1), "With the option disabled, repeating a sole-row click retains selection");

            var panel = new SettingsPanel(window);
            settingsContent.Content = panel; settingsOverlay.Visibility = Visibility.Visible; await IdleAsync();
            var toggles = Descendants<CheckBox>(panel).ToArray();
            var toggle = toggles.Single(box => box.Name == "DeselectOnRepeatedClickToggle");
            check(toggle.IsChecked == false && toggle.Content as string == "再次点击已选记录时取消选中",
                "History settings expose the optional repeated-click control with the requested label and disabled default");
            var selectedBeforeSetting = SelectedIds(list);
            toggle.IsChecked = true; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
            check(store.Settings.DeselectOnRepeatedClick && ReferenceEquals(settingsContent.Content, panel)
                && Descendants<CheckBox>(panel).SequenceEqual(toggles) && SelectedIds(list).SetEquals(selectedBeforeSetting),
                "Enabling repeated-click behavior updates the preference without rebuilding settings or changing selection");
            check(await store.FlushAsync()
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.SettingsPath))!.DeselectOnRepeatedClick,
                "The settings toggle saves its enabled value immediately through the production preference handler");
            toggle.IsChecked = false; toggle.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, toggle));
            check(!store.Settings.DeselectOnRepeatedClick && ReferenceEquals(settingsContent.Content, panel)
                && Descendants<CheckBox>(panel).SequenceEqual(toggles) && await store.FlushAsync()
                && !JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.SettingsPath))!.DeselectOnRepeatedClick,
                "Disabling the same settings control saves false and preserves its attached UI");
            settingsOverlay.Visibility = Visibility.Collapsed; settingsContent.Content = null;

            store.Settings.DeselectOnRepeatedClick = true;
            Select(1); window.ApplyRowSelection(1, ModifierKeys.None);
            check(Selected() && FocusedIndex(window, list) == 1 && AnchorIndex() == 1,
                "Enabled repeated click clears only a sole selection and preserves its logical focus and range anchor");
            check(count.Visibility == Visibility.Collapsed && badge.Visibility == Visibility.Hidden && !delete.IsEnabled,
                "Repeated-click deselection clears the selection badge and disables selected-record actions");
            window.ApplyRowSelection(4, ModifierKeys.Shift);
            check(Selected(1, 2, 3, 4), "Shift selection continues from the anchor retained after repeated-click deselection");
            Select(1, 3); window.ApplyRowSelection(3, ModifierKeys.None);
            check(Selected(3), "Clicking within a multiple selection collapses to that row instead of clearing every row");
            window.ApplyRowSelection(3, ModifierKeys.None);
            check(Selected(), "Only the next repeated sole-row click clears the collapsed selection");
            Select(1); window.ApplyRowSelection(4, ModifierKeys.None);
            check(Selected(4), "The option does not change ordinary selection of a different row");
            window.ApplyRowSelection(4, ModifierKeys.Shift);
            check(Selected(4), "Shift-clicking the sole selected row does not invoke optional repeated-click deselection");
            window.ApplyRowSelection(4, ModifierKeys.Control);
            check(Selected(), "Ctrl-click still removes the clicked selected row using its original modifier semantics");
            window.ApplyRowSelection(4, ModifierKeys.Control); window.ApplyRowSelection(1, ModifierKeys.Control);
            check(Selected(1, 4), "Ctrl-click still builds a disjoint multi-selection with the option enabled");

            Select(1);
            var menu = window.BuildHistoryContextMenu((ClipItem)list.Items[1]);
            check(Selected(1) && !menu.IsOpen, "Right-click menu preparation on a sole selected row does not deselect it");
            var inline = await RowButtonAsync(list, (ClipItem)list.Items[1], "Pin");
            var surface = Named<Border>(window, "HistoryBorder");
            var buttonDown = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = inline };
            Invoke(window, "History_MouseDown", surface, buttonDown);
            check(!buttonDown.Handled && Selected(1)
                && typeof(MainWindow).GetField("pointerDown", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window) is false,
                "Inline action-button presses bypass repeated-row clicking and do not start a deselection gesture");
            check(!await window.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, inline) && Selected(1),
                "Button keyboard activation remains owned by its button rather than the repeated-click option");

            Select(0);
            foreach (var key in new[] { Key.Home, Key.Up, Key.Home, Key.PageUp })
                check(await window.HandleHistoryKeyAsync(key, ModifierKeys.None, list) && Selected(0),
                    key + " at the first-row boundary keeps keyboard selection even when repeated-click deselection is enabled");
            check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.None, list) && Selected(1)
                && await window.HandleHistoryKeyAsync(Key.Up, ModifierKeys.None, list) && Selected(0),
                "Normal Down and Up retain their selection-navigation behavior with the option enabled");
            foreach (var key in new[] { Key.End, Key.Down, Key.End, Key.PageDown })
                check(await window.HandleHistoryKeyAsync(key, ModifierKeys.None, list) && Selected(list.Items.Count - 1),
                    key + " at the last-row boundary cannot act as a repeated mouse click");
            Select(1);
            check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.Control, list) && Selected(1) && FocusedIndex(window, list) == 2,
                "Ctrl+Down remains focus-only with the repeated-click option enabled");
            check(await window.HandleHistoryKeyAsync(Key.Down, ModifierKeys.Shift, list) && Selected(1, 2, 3),
                "Shift+Down keeps range-selection semantics with the option enabled");

            scroll.ScrollToTop(); await IdleAsync();
            Select(1); selectionEvents = 0;
            window.BeginRowPointer(1, At(1), ModifierKeys.None);
            check(Selected(1) && selectionEvents == 0 && count.Visibility == Visibility.Visible && delete.IsEnabled,
                "Pressing the sole selected row defers deselection without an empty frame or action-state flicker");
            check(window.CompleteRowPointer(At(1)) && Selected() && FocusedIndex(window, list) == 1 && AnchorIndex() == 1,
                "A normal same-row release commits pending deselection and retains focus and anchor");
            End();
            check(selectionEvents == 1 && count.Visibility == Visibility.Collapsed && badge.Visibility == Visibility.Hidden && !delete.IsEnabled,
                "Deferred click commits one selection transaction and updates count, badge, and actions together");
            check(!window.CompleteRowPointer(At(1)) && Selected(), "A duplicate release cannot select or deselect anything again");

            Select(1); window.BeginRowPointer(1, At(1), ModifierKeys.None, activationOnly: true);
            window.CompleteRowPointer(At(1));
            check(list.SelectedItems.Count == 1, "Activation click on selected record preserves selection");
            window.BeginRowPointer(1, At(1), ModifierKeys.None); window.CompleteRowPointer(At(1));
            check(list.SelectedItems.Count == 0, "Second active click can deselect normally");
            Select(1); window.BeginRowPointer(1, At(1), ModifierKeys.None); End();
            check(!window.CompleteRowPointer(At(1)) && Selected(1), "Cancelled pointer ownership discards pending deselection");
            window.BeginRowPointer(1, At(1), ModifierKeys.None); window.HandleApplicationDeactivated();
            check(!window.CompleteRowPointer(At(1)) && Selected(1), "Application deactivation cancels pending click without clearing selection");
            window.BeginRowPointer(1, At(1), ModifierKeys.None);
            check(!window.CompleteRowPointer(At(2)) && Selected(1), "Releasing over another row cannot commit pending sole-row deselection");
            End(); window.BeginRowPointer(1, At(1), ModifierKeys.None);
            check(!window.CompleteRowPointer(new Point(-10, At(1).Y)) && Selected(1),
                "Releasing outside the list's horizontal bounds cancels the pending click");
            End(); window.BeginRowPointer(1, At(1), ModifierKeys.None);
            check(!window.CompleteRowPointer(new Point(20, -10)) && Selected(1),
                "Releasing outside the list's vertical bounds cancels the pending click");
            End();

            window.BeginRowPointer(1, At(1), ModifierKeys.None);
            typeof(MainWindow).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "UpdateDragSelection", At(4));
            check(Selected(1, 2, 3, 4) && window.CompleteRowPointer(At(4)) && Selected(1, 2, 3, 4),
                "Dragging from the sole selected row extends a range rather than committing the pending click");
            Select(1); window.BeginRowPointer(1, At(1), ModifierKeys.None);
            typeof(MainWindow).GetField("dragging", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
            Invoke(window, "UpdateDragSelection", At(4));
            Invoke(window, "UpdateDragSelection", At(1));
            check(Selected(1) && window.CompleteRowPointer(At(1)) && Selected(1),
                "Dragging out and back to the original row never turns the release into deselection");
            End();
            store.Settings.DeselectOnRepeatedClick = false;
            window.BeginRowPointer(1, At(1), ModifierKeys.None);
            check(!window.CompleteRowPointer(At(1)) && Selected(1),
                "Disabling the option restores keep-selection behavior on the same press-and-release path");
            End();
            check(store.Items.Count == itemCount, "Repeated-click preference checks never delete or rewrite clipboard records");
        }
        finally
        {
            list.SelectionChanged -= changed; End();
            settingsOverlay.Visibility = previousOverlay; settingsContent.Content = previousContent;
            store.Settings.DeselectOnRepeatedClick = previousPreference; window.ApplyPreferences();
        }
    }

    private static void CheckMenuAccessibility(ContextMenu menu, Action<bool, string> check)
    {
        var items = menu.Items.OfType<MenuItem>().ToArray();
        check(items.Length >= 3 && !string.IsNullOrWhiteSpace(AutomationProperties.GetName(menu)), "History context menu has an accessible name and actions");
        check(items.All(item => !string.IsNullOrWhiteSpace(AutomationProperties.GetName(item))), "Each history context-menu action has an accessible label");
        check(items.Any(item => NormalizeGesture(item.InputGestureText) == "CTRL+C") &&
            items.Any(item => NormalizeGesture(item.InputGestureText) == "SPACE") &&
            items.All(item => NormalizeGesture(item.InputGestureText) != "CTRL+V") &&
            items.Any(item => NormalizeGesture(item.InputGestureText) == "DELETE"),
            "Context menu exposes copy, preview, and delete gestures without automatic paste");
    }

    private static bool ContextMenuGestureExists(MainWindow window, Key key, ModifierKeys modifiers)
    {
        if (window.InputBindings.OfType<KeyBinding>().Any(binding => binding.Key == key && binding.Modifiers == modifiers)) return true;
        var gesture = typeof(MainWindow).GetMethod("IsContextMenuGesture", BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            null, [typeof(Key), typeof(ModifierKeys)], null);
        if (gesture is null) return false;
        return gesture.Invoke(gesture.IsStatic ? null : window, [key, modifiers]) is true;
    }

    private static string NormalizeGesture(string? gesture) => (gesture ?? "").Replace(" ", "", StringComparison.Ordinal).ToUpperInvariant();
    private static void SetPendingSearch(MainWindow window, Task task)
    {
        var property = typeof(MainWindow).GetProperty(nameof(MainWindow.PendingSearch), BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The interaction fixture cannot control the pending search task.");
        property.SetValue(window, task);
    }

    private static HashSet<Guid> SelectedIds(HistoryListBox list) => list.SelectedItems.Cast<ClipItem>().Select(item => item.Id).ToHashSet();
    private static int FocusedIndex(MainWindow window, HistoryListBox list)
    {
        var focused = typeof(MainWindow).GetField("focusedId", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(window);
        return focused is Guid id ? list.Items.Cast<ClipItem>().ToList().FindIndex(item => item.Id == id) : -1;
    }
    private static async Task IdleAsync() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);

    private static T Named<T>(MainWindow window, string name) where T : FrameworkElement => window.FindName(name) as T
        ?? throw new InvalidOperationException("Interaction fixture could not find " + name + ".");

    private static async Task<Button> RowButtonAsync(HistoryListBox list, ClipItem item, string action)
    {
        list.ScrollIntoView(item);
        await IdleAsync();
        var container = list.ItemContainerGenerator.ContainerFromItem(item) as ListBoxItem
            ?? throw new InvalidOperationException("The interaction fixture row was not realized.");
        return Descendants<Button>(container).Single(button => string.Equals(button.Tag as string, action, StringComparison.Ordinal));
    }

    private static object? Invoke(MainWindow window, string method, params object[] arguments)
    {
        var target = typeof(MainWindow).GetMethods(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Single(candidate => candidate.Name == method && candidate.GetParameters().Length == arguments.Length);
        return target.Invoke(window, arguments);
    }

    private static T? Find<T>(DependencyObject parent) where T : DependencyObject => Descendants<T>(parent).FirstOrDefault();

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = child; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, ancestor)) return true;
        return false;
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is T found) yield return found;
            foreach (var descendant in Descendants<T>(child)) yield return descendant;
        }
    }
}
