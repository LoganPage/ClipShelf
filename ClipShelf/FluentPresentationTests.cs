using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>Own-app presentation checks with synthetic records; no clipboard or injected system input.</summary>
public static class FluentPresentationTests
{
    public static async Task RunAsync(string directory)
    {
        directory = Path.GetFullPath(directory); Directory.CreateDirectory(directory);
        var checks = new List<object>(); string? error = null; MainWindow? window = null;
        void Check(bool passed, string name)
        {
            checks.Add(new { name, passed });
            if (!passed) throw new InvalidOperationException(name);
        }
        try
        {
            var store = new HistoryStore(Path.Combine(Path.GetTempPath(), "ClipShelf-fluent-" + Guid.NewGuid().ToString("N")));
            SeedFixture(store);
            window = new MainWindow(store, demo: true) { Left = -12000, Top = -12000, Width = 720, Height = 572,
                WindowStartupLocation = WindowStartupLocation.Manual, ShowActivated = false, ShowInTaskbar = false };
            window.Show();
            async Task Idle() { await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); window.UpdateLayout(); }
            await Idle();
            var list = (HistoryListBox)window.FindName("HistoryList");
            var search = (TextBox)window.FindName("SearchBox");
            var searchBorder = (Border)window.FindName("SearchBorder");
            var historyBorder = (Border)window.FindName("HistoryBorder");
            var badge = (Border)window.FindName("SelectionBadge");
            var count = (TextBlock)window.FindName("SelectionCountText");
            Check(window.Integration is null, "Presentation fixture never connects to the user's clipboard");
            Check(store.Items.Count == 6 && store.Items.Select(item => item.Kind).Distinct().Count() == 3,
                "Presentation fixture contains only six independently generated text/file/image records");
            foreach (string theme in new[] { "Light", "Dark" })
            {
                store.Settings.Theme = theme; window.ApplyPreferences();
                Check(window.FindResource("ControlCorners") is CornerRadius controlCorners && controlCorners == new CornerRadius(4),
                    $"{theme}: global control corner token is 4 DIP");
                Check(window.FindResource("OverlayCorners") is CornerRadius overlayCorners && overlayCorners == new CornerRadius(8),
                    $"{theme}: global overlay corner token is 8 DIP");
                Check(window.FindResource("SquareCorners") is CornerRadius squareCorners && squareCorners == new CornerRadius(0),
                    $"{theme}: global seamless corner token is 0 DIP");
                CheckFocusTemplate("KeyboardFocusVisual", 4, theme, Check);
                CheckRowFocusTemplate(theme, Check);
                foreach (double width in new[] { 680.0, 1200.0 })
                {
                    window.Width = width; list.UnselectAll(); await Idle();
                    double unselectedSearchWidth = search.ActualWidth;
                    Check(badge.Visibility == Visibility.Hidden, $"{theme}/{width}: no empty selection badge is displayed");
                    list.ReplaceSelection(new[] { list.Items[0], list.Items[1] }); await Idle();
                    var badgeBounds = badge.TransformToAncestor(searchBorder).TransformBounds(new Rect(badge.RenderSize));
                    Check(badge.Visibility == Visibility.Visible && count.Text == "已选 2 条", $"{theme}/{width}: concise selection count appears");
                    Check(Math.Abs(search.ActualWidth - unselectedSearchWidth) < 0.1, $"{theme}/{width}: selecting never shifts the search input");
                    Check(badgeBounds.Left > 0 && badgeBounds.Right < searchBorder.ActualWidth && badgeBounds.Top > 0 && badgeBounds.Bottom < searchBorder.ActualHeight,
                        $"{theme}/{width}: selection badge belongs inside the search surface");
                    Check(search.ActualWidth > 250, $"{theme}/{width}: the search input retains useful width");
                    Check(searchBorder.CornerRadius == new CornerRadius(4) && historyBorder.CornerRadius == new CornerRadius(4),
                        $"{theme}/{width}: search and history use 4 DIP resident-surface corners");
                    Check(badge.CornerRadius == new CornerRadius(4), $"{theme}/{width}: selection badge uses 4 DIP control corners");
                    CheckTextInput(search, $"{theme}/{width}: search", Check);
                    Check(search.Template.FindName("InputBorder", search) is Border inputBorder
                        && inputBorder.BorderThickness == new Thickness(0)
                        && inputBorder.Background is SolidColorBrush { Color.A: 0 },
                        $"{theme}/{width}: search editor adds no second stroke or opaque input surface");
                    CheckButtonCorners((FrameworkElement)window.Content, $"{theme}/{width}: main window", Check);
                    CheckHistoryCorners(list, $"{theme}/{width}", Check);
                    Save((FrameworkElement)window.Content, Path.Combine(directory, $"window-{theme.ToLowerInvariant()}-{width:0}.png"), 1.5);
                }
                foreach (bool multiple in new[] { false, true })
                {
                    list.ReplaceSelection(multiple ? new[] { list.Items[0], list.Items[1] } : new[] { list.Items[0] });
                    var menu = window.BuildHistoryContextMenu((ClipItem)list.Items[0]);
                    Check(ReferenceEquals(menu.Style, window.FindResource("FluentContextMenu")), $"{theme}/{multiple}: menu uses the Fluent surface");
                    Check(!menu.HasDropShadow && !menu.IsOpen, $"{theme}/{multiple}: no legacy shadow or native popup is used by this fixture");
                    var actions = menu.Items.OfType<MenuItem>().ToArray();
                    Check(actions.Length == 5 && actions.All(item => ReferenceEquals(item.Style, window.FindResource("FluentMenuItem")) && item.Icon is TextBlock),
                        $"{theme}/{multiple}: every action has the consistent icon and row template");
                    Check(actions.All(item => item.MinHeight >= 34), $"{theme}/{multiple}: menu rows have comfortable pointer targets");
                    Check(actions.First().Header.ToString() == (multiple ? "复制 2 条记录" : "复制"), $"{theme}/{multiple}: labels omit redundant wording but retain batch scope");
                    menu.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    menu.Arrange(new Rect(menu.DesiredSize)); menu.UpdateLayout();
                    var surface = (Border)menu.Template.FindName("MenuSurface", menu);
                    Check(surface.CornerRadius == new CornerRadius(8) && surface.BorderThickness == new Thickness(1),
                        $"{theme}/{multiple}: menu has one subtle border and 8 DIP corners");
                    Check(actions.All(item => item.Template.FindName("MenuRow", item) is Border row && row.CornerRadius == new CornerRadius(4)),
                        $"{theme}/{multiple}: all menu action rows use 4 DIP control corners");
                    Check(menu.Items.OfType<Separator>().All(separator => TemplateBorder(separator)?.CornerRadius == new CornerRadius(0)),
                        $"{theme}/{multiple}: menu separator edges remain square");
                    Check(menu.ActualWidth >= 240 && menu.ActualHeight >= 180, $"{theme}/{multiple}: menu layout is realized before raster capture");
                    Save(menu, Path.Combine(directory, $"menu-{theme.ToLowerInvariant()}-{(multiple ? "batch" : "single")}.png"), 1.5);
                }

                // Mount the real settings content in this isolated window without calling
                // OpenSettings, which also schedules keyboard focus and native hotkey work.
                window.Width = 720; window.Height = 780;
                var settingsOverlay = (Grid)window.FindName("SettingsOverlay");
                var settingsCard = (Border)window.FindName("SettingsCard");
                var settingsContent = (ContentControl)window.FindName("SettingsContent");
                var settings = new SettingsPanel(window);
                settingsContent.Content = settings; settingsOverlay.Visibility = Visibility.Visible;
                await Idle();
                Check(settingsCard.CornerRadius == new CornerRadius(8), $"{theme}: settings overlay uses 8 DIP corners");
                CheckButtonCorners(settings, $"{theme}: settings", Check);
                var inputs = FindAll<TextBox>(settings).ToArray();
                Check(inputs.Length >= 4, $"{theme}: settings color and shortcut inputs are realized");
                foreach (var input in inputs) CheckTextInput(input, $"{theme}: settings", Check);
                var combos = FindAll<ComboBox>(settings).ToArray();
                Check(combos.Length > 0, $"{theme}: settings ComboBox is realized for corner checks");
                for (int index = 0; index < combos.Length; index++)
                    CheckComboCorners(combos[index], $"{theme}/{index}", directory, Check);
                // Capture the origin-aligned full client while the overlay is visible.
                // A nested settings card carries parent layout offsets into raster capture.
                Save((FrameworkElement)window.Content, Path.Combine(directory, $"settings-{theme.ToLowerInvariant()}.png"), 1.5);
                settingsOverlay.Visibility = Visibility.Collapsed; settingsContent.Content = null;
                await Idle();

                var toast = (Border)window.FindName("Toast");
                Check(toast.CornerRadius == new CornerRadius(8), $"{theme}: toast uses 8 DIP overlay corners");
                Check(window.Integration is null, $"{theme}: settings and dropdown fixtures never start native services");
                window.Height = 572;
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "presentation-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error,
                scope = "Own-app off-screen layout and raster checks using independently generated text, file and image fixtures; no real user data, clipboard access, system input, or display frame-rate claim.",
                cornerScope = "Checks the implemented 4/8/0 DIP templates in light and dark themes. Focus templates and dropdown child trees are laid out without acquiring keyboard focus or opening native popups. Settings captures show only the isolated fixture. DWM outer-window corners are not raster-validated here." }, new JsonSerializerOptions { WriteIndented = true }));
            window?.Quit();
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static void CheckHistoryCorners(HistoryListBox list, string label, Action<bool, string> check)
    {
        var rows = Enumerable.Range(0, list.Items.Count)
            .Select(index => list.ItemContainerGenerator.ContainerFromIndex(index)).OfType<ListBoxItem>().ToArray();
        check(rows.Length >= 3 && rows.Select(row => ((ClipItem)row.DataContext).Kind).Distinct().Count() == 3,
            $"{label}: text/file/image row templates are realized");
        foreach (var row in rows)
        {
            string kind = ((ClipItem)row.DataContext).Kind.ToString();
            check(row.Template.FindName("RowBg", row) is Border background && background.CornerRadius == new CornerRadius(0),
                $"{label}/{kind}: row selection background stays square and seamless");
            check(ReferenceEquals(row.FocusVisualStyle, Application.Current.FindResource("RowKeyboardFocusVisual")),
                $"{label}/{kind}: row uses the independent minimal focus-marker template");
            var tile = FindAll<Border>(row).SingleOrDefault(border => border.Name == "Tile");
            check(tile is not null && tile.CornerRadius == new CornerRadius(4),
                $"{label}/{kind}: thumbnail/type tile uses 4 DIP control corners");
            check(tile is RoundedClipBorder { Clip: RectangleGeometry { RadiusX: 4, RadiusY: 4 } } roundedTile
                && roundedTile.Child?.Clip is RectangleGeometry { RadiusX: 4, RadiusY: 4 },
                $"{label}/{kind}: thumbnail content and tile share the actual 4 DIP clip");
            var separator = FindAll<Border>(row).SingleOrDefault(border => border.Name == "RowSeparator");
            check(separator is not null && separator.CornerRadius == new CornerRadius(0),
                $"{label}/{kind}: history row separator stays square");
        }
    }

    private static void CheckButtonCorners(DependencyObject root, string label, Action<bool, string> check)
    {
        var buttons = FindAll<Button>(root).ToArray();
        check(buttons.Length > 0, label + ": button templates are present");
        var captionStyle = (Style)Application.Current.FindResource("CaptionButton");
        foreach (var button in buttons)
        {
            bool caption = false;
            for (Style? style = button.Style; style is not null; style = style.BasedOn)
                if (ReferenceEquals(style, captionStyle)) { caption = true; break; }
            var expectedRadius = caption ? new CornerRadius(0) : new CornerRadius(4);
            check(TemplateBorder(button)?.CornerRadius == expectedRadius,
                label + (caption ? ": native-style caption button stays square" : ": content button uses 4 DIP corners"));
            check(ReferenceEquals(button.FocusVisualStyle, Application.Current.FindResource("KeyboardFocusVisual")),
                label + ": button retains the 4 DIP keyboard-focus style");
        }
    }

    private static void CheckFocusTemplate(string resource, double radius, string label, Action<bool, string> check)
    {
        // Render the actual focus style on a detached control. No Focus(), keyboard event,
        // adorner activation or HWND is required to check the template's geometry.
        var focus = new Control { Style = (Style)Application.Current.FindResource(resource), Width = 100, Height = 40 };
        LayoutDetached(focus, new Size(100, 40));
        var border = TemplateBorder(focus);
        check(border is not null && border.CornerRadius == new CornerRadius(radius),
            $"{label}: {resource} template has {radius:0} DIP corners");
        check(border is not null && !border.IsHitTestVisible && PresentationSource.FromVisual(focus) is null,
            $"{label}: {resource} remains a non-interactive detached focus visual");
        check(border is not null && border.BorderThickness == new Thickness(2)
            && ReferenceEquals(border.BorderBrush, Application.Current.FindResource("AccentBrush")),
            $"{label}: buttons retain their four-sided accent keyboard focus outline");
    }

    private static void CheckRowFocusTemplate(string label, Action<bool, string> check)
    {
        var focus = new Control { Style = (Style)Application.Current.FindResource("RowKeyboardFocusVisual"), Width = 100, Height = 74 };
        LayoutDetached(focus, new Size(100, 74));
        var borders = FindAll<Border>(focus).ToArray();
        var marker = borders.SingleOrDefault();
        check(marker is not null && marker.Width == 2 && marker.HorizontalAlignment == HorizontalAlignment.Left
            && marker.Margin.Top > 0 && marker.Margin.Bottom > 0 && marker.Margin.Left > 0,
            $"{label}: row keyboard focus uses only an inset 2 DIP left-side marker");
        check(marker is not null && marker.BorderThickness == new Thickness(0) && marker.BorderBrush is null
            && !FindAll<System.Windows.Shapes.Shape>(focus).Any(shape => shape.Stroke is not null),
            $"{label}: the row focus template has no enclosing border or stroked rectangle");
        check(marker is not null && ReferenceEquals(marker.Background, Application.Current.FindResource("MutedBrush")),
            $"{label}: the row focus marker remains neutral in this theme");
        check(marker is not null && !marker.IsHitTestVisible && marker.Visibility == Visibility.Collapsed
            && PresentationSource.FromVisual(focus) is null,
            $"{label}: the detached row marker is non-interactive and hidden without a focused unselected target");
    }

    private static void CheckTextInput(TextBox input, string label, Action<bool, string> check)
    {
        input.ApplyTemplate();
        check(input.Template.FindName("InputBorder", input) is Border border
            && border.CornerRadius == new CornerRadius(4) && border.BorderThickness == input.BorderThickness,
            label + ": TextBox uses 4 DIP corners and preserves its requested border thickness");
        check(input.Template.FindName("PART_ContentHost", input) is ScrollViewer { Focusable: false },
            label + ": TextBox keeps its non-focusable PART_ContentHost editing surface");
    }

    private static void CheckComboCorners(ComboBox combo, string label, string directory, Action<bool, string> check)
    {
        combo.ApplyTemplate();
        var toggle = FindAll<ToggleButton>(combo).FirstOrDefault(button => ReferenceEquals(button.TemplatedParent, combo));
        check(toggle is not null && TemplateBorder(toggle)?.CornerRadius == new CornerRadius(4),
            $"{label}: ComboBox closed surface uses 4 DIP control corners");
        check(ReferenceEquals(combo.FocusVisualStyle, Application.Current.FindResource("KeyboardFocusVisual")),
            $"{label}: ComboBox keeps the 4 DIP keyboard-focus style");
        var popup = combo.Template.FindName("PART_Popup", combo) as Popup;
        check(popup is not null && !popup.IsOpen && !combo.IsDropDownOpen,
            $"{label}: dropdown checks do not open a native popup");
        var surface = popup?.Child as Border;
        check(surface is not null && surface.CornerRadius == new CornerRadius(8),
            $"{label}: dropdown surface uses 8 DIP overlay corners");
        if (surface is null) return;
        LayoutDetached(surface, new Size(Math.Max(220, combo.ActualWidth), double.PositiveInfinity));
        var options = FindAll<ComboBoxItem>(surface).ToArray();
        check(options.Length > 0, $"{label}: closed dropdown child layout realizes option templates");
        check(options.All(option => TemplateBorder(option)?.CornerRadius == new CornerRadius(4)),
            $"{label}: all dropdown option rows use 4 DIP control corners");
        check(popup is not null && !popup.IsOpen && PresentationSource.FromVisual(surface) is null,
            $"{label}: dropdown raster remains detached from any native popup window");
        Save(surface, Path.Combine(directory, "dropdown-" + label.Replace('/', '-').ToLowerInvariant() + ".png"), 1.5);
    }

    private static Border? TemplateBorder(Control control)
    {
        control.ApplyTemplate();
        return FindAll<Border>(control).FirstOrDefault(border => ReferenceEquals(border.TemplatedParent, control));
    }

    private static void LayoutDetached(FrameworkElement element, Size available)
    {
        element.Measure(available); element.Arrange(new Rect(element.DesiredSize)); element.UpdateLayout();
    }

    private static IEnumerable<T> FindAll<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T found) yield return found;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in FindAll<T>(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void SeedFixture(HistoryStore store)
    {
        store.Settings.WatchScreenshots = false;
        store.Settings.LaunchAtLogin = false;
        store.Settings.ScreenshotFolder = Path.Combine(store.DirectoryPath, "synthetic-screenshots");
        DateTimeOffset time = new(2026, 9, 8, 10, 0, 0, TimeSpan.Zero);
        store.Add(new ClipItem { Kind = ClipKind.Text, Title = "ClipShelf · 圆角一致性测试", Text = "独立生成的置顶文字。", IsPinned = true, CreatedAt = time });
        string file = Path.Combine(store.DirectoryPath, "synthetic-document.txt");
        File.WriteAllText(file, "This file belongs only to the isolated presentation fixture.");
        store.Add(new ClipItem { Kind = ClipKind.File, Title = "合成文件.txt", FilePaths = new() { file }, CreatedAt = time.AddSeconds(-1) });
        var image = new ClipItem { Kind = ClipKind.Image, Title = "合成截图.png", CreatedAt = time.AddSeconds(-2) };
        image.ImagePath = store.ImagePathFor(image.Id); image.SourcePath = image.ImagePath;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 234, 244)), null, new Rect(0, 0, 320, 180));
            drawing.DrawRectangle(Brushes.White, null, new Rect(20, 20, 280, 140));
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 112, 219)), null, new Rect(36, 36, 48, 48));
        }
        var bitmap = new RenderTargetBitmap(320, 180, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(image.ImagePath)) encoder.Save(stream);
        store.Add(image);
        for (int index = 0; index < 3; index++)
            store.Add(new ClipItem { Kind = ClipKind.Text, Title = $"独立合成记录 {index + 1}", Text = $"文字、文件和截图的圆角应保持一致。 {index + 1}", CreatedAt = time.AddSeconds(-3 - index) });
    }

    private static void Save(FrameworkElement element, string path, double scale)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(element.ActualWidth * scale), (int)Math.Ceiling(element.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
