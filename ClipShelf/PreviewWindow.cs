using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace ClipShelf;

public sealed class PreviewWindow : Window
{
    private readonly PreviewSession session;
    private readonly PreviewCacheService cache;
    private readonly bool ownsCache;
    private readonly Border card = new() { CornerRadius = new(12), Margin = new(12), RenderTransformOrigin = new(.5, .35), RenderTransform = new ScaleTransform(1, 1) };
    private readonly TextBlock title = new() { TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock pages = new() { Width = 128, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly RecordTypeIcon icon = new() { Width = 32, Height = 28, Margin = new(0, 0, 10, 0) };
    private readonly Image image = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image previous = new() { Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false };
    private readonly TextBox textContent = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 16,
        BorderThickness = new(0), Background = Brushes.Transparent, Padding = new(14), VerticalAlignment = VerticalAlignment.Top,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Visibility = Visibility.Collapsed };
    private readonly TextBlock lineNumbers = new() { FontSize = 16, TextAlignment = TextAlignment.Right, Padding = new(10, 14, 8, 14), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private readonly TextBlock textNotice = new() { Margin = new(14, 8, 14, 12), TextWrapping = TextWrapping.Wrap };
    private readonly Grid textPanel = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel segmentControls = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 4, 0, 12) };
    private readonly Border searchPanel = new() { Padding = new(8), CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new(18), Visibility = Visibility.Collapsed };
    private readonly TextBox previewSearch = new() { Width = 280, Height = 34, Padding = new(10, 5, 10, 5), VerticalContentAlignment = VerticalAlignment.Center };
    private readonly TextBlock searchStatus = new() { Width = 74, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly SmoothScrollViewer scroll = new();
    private readonly DataGrid spreadsheet = new() { Visibility = Visibility.Collapsed, IsReadOnly = true, AutoGenerateColumns = false,
        EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.All,
        CanUserAddRows = false, CanUserDeleteRows = false, GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        RowHeaderWidth = 52, FrozenColumnCount = 0, Margin = new(12, 4, 12, 12) };
    private readonly Border loading = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16), Padding = new(14, 8, 14, 8), CornerRadius = new(8), IsHitTestVisible = false };
    private readonly TextBlock loadingText = new() { Text = "正在准备文档…" };
    private readonly Border skeleton = new() { Width = 300, Height = 400, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border errorLayer = new() { Padding = new(32), CornerRadius = new(8), Visibility = Visibility.Collapsed };
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, TextAlignment = TextAlignment.Center };
    private readonly TextBox fileLocation = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        MaxWidth = 480, MaxHeight = 120, Margin = new(0, 14, 0, 0), BorderThickness = new(0), Background = Brushes.Transparent,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly TextBlock warnings = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16), MaxWidth = 400, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
    private readonly TextBlock zoomLabel = new() { Width = 52, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel zoomControls = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16) };
    private readonly Button back, next, retry, searchToggle, wrapToggle, linesToggle, previousSegment, nextSegment;
    private RenderedPage? displayed;
    private TextPreviewResult? displayedText;
    private SpreadsheetPreview? displayedSpreadsheet;
    private TextPreviewViewModel? displayedTextViewModel;
    private bool updatingSearch;
    private int lastRecordIndex;
    private bool closing, finishedClose;
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private readonly DispatcherTimer zoomTimer = new() { Interval = TimeSpan.FromMilliseconds(220) };
    private double visualZoom = 1;
    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    internal object? PresentedContent => session.Error is not null ? errorText : displayedSpreadsheet is not null ? spreadsheet : displayedText is not null ? textContent : image;
    internal Task PendingRender => session.Pending;
    internal int FileIndex => 0;
    internal int PageIndex => session.Page;
    internal int RecordIndex => session.Index;
    internal PreviewSession Session => session;
    internal string DisplayedLocation => fileLocation.Text;
    internal TextBox TextContent => textContent;
    internal TextBox PreviewSearch => previewSearch;
    internal void ZoomTo(double factor) => SetVisualZoom(factor);
    internal event Action<ClipItem>? RecordChanged;
    private string? ActionPath => session.Path ?? session.Current.SourcePath ?? (session.Current.FilePaths.Count > 0 ? session.Current.FilePaths[0] : null);
    internal void SetAnimationOrigin(double rowFraction) => card.RenderTransformOrigin = new Point(.5, Math.Clamp(rowFraction, .1, .9));
    public PreviewWindow(IReadOnlyList<ClipItem> items, int index) : this(items, index, null) { }
    internal PreviewWindow(IReadOnlyList<ClipItem> items, int index, PreviewCacheService? sharedCache, bool prewarmAdjacent = false)
    {
        if (items.Count == 0) throw new ArgumentException("No preview items", nameof(items));
        cache = sharedCache ?? new(); ownsCache = sharedCache is null; session = new(items, index, cache, prewarmAdjacent: prewarmAdjacent); lastRecordIndex = session.Index;
        Width = 900; Height = 720; MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Title = "快速预览 · ClipShelf";
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        FocusCuePolicy.SetIsEnabled(this, true);
        var appearance = new WindowAppearance(this);
        card.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
        var layout = new Grid(); layout.RowDefinitions.Add(new() { Height = new GridLength(56) }); layout.RowDefinitions.Add(new());
        var toolbar = new DockPanel { Margin = new(14, 8, 14, 8) };
        var controls = new StackPanel { Orientation = Orientation.Horizontal };
        controls.Children.Add(pages);
        searchToggle = Button("\uE721", "在预览文本中查找（Ctrl+F）", ShowTextSearch);
        wrapToggle = Button("\uE8E9", "切换自动换行", ToggleTextWrap);
        linesToggle = Button("\uE8FD", "切换行号", ToggleLineNumbers);
        controls.Children.Add(searchToggle); controls.Children.Add(wrapToggle); controls.Children.Add(linesToggle);
        back = Button("\uE76B", "上一页（←）", () => NavigatePage(-1)); next = Button("\uE76C", "下一页（→）", () => NavigatePage(1));
        controls.Children.Add(back); controls.Children.Add(next); controls.Children.Add(Button("\uE838", "在资源管理器中显示", Reveal)); controls.Children.Add(Button("\uE8BB", "关闭（Space / Esc）", Close));
        DockPanel.SetDock(controls, Dock.Right); toolbar.Children.Add(controls); toolbar.Children.Add(icon); toolbar.Children.Add(title); layout.Children.Add(toolbar);
        var content = new Grid { ClipToBounds = true, Margin = new(10, 0, 10, 10) }; Grid.SetRow(content, 1); layout.Children.Add(content);
        var pageLayers = new Grid { Margin = new(12) };
        var retained = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        retained.Children.Add(previous); pageLayers.Children.Add(retained); pageLayers.Children.Add(image);
        textContent.SetResourceReference(ForegroundProperty, "TextBrush"); textNotice.SetResourceReference(ForegroundProperty, "MutedBrush");
        lineNumbers.SetResourceReference(ForegroundProperty, "MutedBrush");
        textPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); textPanel.RowDefinitions.Add(new() { Height = GridLength.Auto }); textPanel.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var textGrid = new Grid(); textGrid.ColumnDefinitions.Add(new() { Width = GridLength.Auto }); textGrid.ColumnDefinitions.Add(new());
        textGrid.Children.Add(lineNumbers); Grid.SetColumn(textContent, 1); textGrid.Children.Add(textContent); textPanel.Children.Add(textGrid);
        Grid.SetRow(textNotice, 1); textPanel.Children.Add(textNotice);
        previousSegment = TextButton("上一段", () => NavigateTextSegment(-1)); nextSegment = TextButton("下一段", () => NavigateTextSegment(1));
        segmentControls.Children.Add(previousSegment); segmentControls.Children.Add(nextSegment); Grid.SetRow(segmentControls, 2); textPanel.Children.Add(segmentControls);
        pageLayers.Children.Add(textPanel);
        scroll.Content = pageLayers; content.Children.Add(scroll);
        spreadsheet.SetResourceReference(DataGrid.BackgroundProperty, "SurfaceBrush");
        spreadsheet.LoadingRow += (_, e) => { if (e.Row.Item is SpreadsheetRow row) e.Row.Header = row.Number.ToString(); };
        for (int i = 0; i < 50; i++) spreadsheet.Columns.Add(new DataGridTextColumn { Header = ColumnName(i), Width = new DataGridLength(136),
            Binding = new Binding($"Cells[{i}]") { Mode = BindingMode.OneWay } });
        content.Children.Add(spreadsheet);
        zoomControls.Children.Add(Button("\uE738", "缩小预览", () => SetVisualZoom(visualZoom / 1.2)));
        zoomControls.Children.Add(zoomLabel);
        zoomControls.Children.Add(Button("\uE710", "放大预览", () => SetVisualZoom(visualZoom * 1.2)));
        zoomControls.Children.Add(Button("\uE777", "重置缩放", () => SetVisualZoom(1)));
        zoomControls.SetResourceReference(Panel.BackgroundProperty, "SurfaceBrush");
        Panel.SetZIndex(zoomControls, 10); content.Children.Add(zoomControls);
        skeleton.SetResourceReference(Border.BackgroundProperty, "ActionBrush"); content.Children.Add(skeleton);
        loading.SetResourceReference(Border.BackgroundProperty, "ActionBrush"); loading.Child = loadingText; content.Children.Add(loading);
        var errorStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        warnings.SetResourceReference(ForegroundProperty, "MutedBrush"); content.Children.Add(warnings);
        errorStack.Children.Add(errorText);
        fileLocation.SetResourceReference(ForegroundProperty, "MutedBrush");
        System.Windows.Automation.AutomationProperties.SetName(fileLocation, "文件位置"); errorStack.Children.Add(fileLocation);
        var errorButtons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new(0, 18, 0, 0) };
        retry = TextButton("重试", () => session.Retry()); errorButtons.Children.Add(retry); errorButtons.Children.Add(TextButton("默认应用打开", OpenDefault)); errorButtons.Children.Add(TextButton("在资源管理器中显示", Reveal)); errorStack.Children.Add(errorButtons);
        errorLayer.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); errorLayer.Child = errorStack; content.Children.Add(errorLayer);
        var searchLayout = new StackPanel { Orientation = Orientation.Horizontal };
        searchLayout.Children.Add(previewSearch); searchLayout.Children.Add(searchStatus);
        searchLayout.Children.Add(Button("\uE72C", "上一处（Shift+F3）", () => FindText(true)));
        searchLayout.Children.Add(Button("\uE72D", "下一处（F3）", () => FindText(false)));
        searchLayout.Children.Add(Button("\uE8BB", "关闭查找（Esc）", HideTextSearch));
        searchPanel.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); searchPanel.Child = searchLayout; Panel.SetZIndex(searchPanel, 20); content.Children.Add(searchPanel);
        card.Child = layout; Content = card;
        session.Changed += Update;
        PreviewKeyDown += OnPreviewKey;
        PreviewKeyUp += (_, e) => e.Handled = true;
        Loaded += (_, _) => { session.PixelDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX; session.PixelWidth = Math.Clamp((int)((ActualWidth - 70) * VisualTreeHelper.GetDpi(this).DpiScaleX), 480, 1800); AnimateCard(true); session.Start(); Focus(); };
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); if (!closing) { session.PixelDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX; session.Resize((int)((ActualWidth - 70) * VisualTreeHelper.GetDpi(this).DpiScaleX), deferRender: zoomTimer.IsEnabled && Math.Abs(visualZoom - session.ZoomFactor) >= .001); } };
        zoomTimer.Tick += (_, _) => { zoomTimer.Stop(); if (resizeTimer.IsEnabled) { zoomTimer.Start(); return; } if (!closing) session.SetZoomFactor(visualZoom); };
        SizeChanged += (_, _) => { if (IsLoaded && !closing) { resizeTimer.Stop(); resizeTimer.Start(); } };
        DpiChanged += (_, _) => { if (!closing) { resizeTimer.Stop(); resizeTimer.Start(); } };
        PreviewMouseWheel += OnPreviewWheel;
        previewSearch.TextChanged += (_, _) => { if (!updatingSearch && displayedTextViewModel is { } view) { view.SearchText = previewSearch.Text; FindText(false, true); } };
        previewSearch.KeyDown += (_, e) => { if (e.Key == Key.Enter) { FindText(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; } };
        scroll.ScrollChanged += (_, _) => { if (displayedTextViewModel is { } view && textPanel.IsVisible) view.ScrollOffset = scroll.VerticalOffset; };
        Closing += OnClosing;
        Closed += (_, _) => { resizeTimer.Stop(); zoomTimer.Stop(); scroll.CancelWheelMotion(); session.Changed -= Update; PreviewKeyDown -= OnPreviewKey; PreviewMouseWheel -= OnPreviewWheel; appearance.Dispose(); image.Source = previous.Source = null; textContent.Clear(); spreadsheet.ItemsSource = null; Cleanup = ReleaseAsync(); };
    }
    private static string ColumnName(int index) { string name = ""; for (int n = index + 1; n > 0; n = (n - 1) / 26) name = (char)('A' + (n - 1) % 26) + name; return name; }
    private static Button Button(string glyph, string tooltip, Action action)
    {
        var b = new Button { Content = glyph, ToolTip = tooltip, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), Width = 34, Height = 34, Margin = new(2, 0, 2, 0), Padding = new(0), Style = (Style)Application.Current.FindResource("SoftButton") };
        System.Windows.Automation.AutomationProperties.SetName(b, tooltip); b.Click += (_, _) => action(); return b;
    }
    private static Button TextButton(string text, Action action)
    {
        var b = new Button { Content = text, Margin = new(4), Padding = new(14, 8, 14, 8), Style = (Style)Application.Current.FindResource("SoftButton") }; b.Click += (_, _) => action(); return b;
    }
    private void OnPreviewKey(object sender, KeyEventArgs e)
    {
        scroll.CancelWheelMotion();
        if (PreviewFormatRegistry.FormatOf(session.Current) == PreviewFormat.Spreadsheet && session.PresentedSpreadsheet is not null && Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.PageDown or Key.PageUp or Key.Home or Key.End) {
            var viewer = FindScrollViewer(spreadsheet);
            if (viewer is not null) { if (e.Key == Key.PageDown) viewer.PageDown(); else if (e.Key == Key.PageUp) viewer.PageUp(); else if (e.Key == Key.Home) viewer.ScrollToTop(); else viewer.ScrollToBottom(); }
            e.Handled = true; return;
        }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 && session.PresentedSpreadsheet is null) {
            SetVisualZoom(e.Key is Key.OemPlus or Key.Add ? visualZoom * 1.2 : e.Key is Key.OemMinus or Key.Subtract ? visualZoom / 1.2 : 1);
            e.Handled = true; return;
        }
        if (e.Key == Key.Escape && searchPanel.IsVisible) { HideTextSearch(); e.Handled = true; return; }
        if (e.Key is Key.Space or Key.Escape) { if (!e.IsRepeat) Close(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.F && session.PresentedTextViewModel is not null) { ShowTextSearch(); e.Handled = true; return; }
        if (e.Key == Key.F3 && session.PresentedTextViewModel is not null) { FindText(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.None && e.Key is Key.Up or Key.Down) { RememberTextViewState(); session.HandleKey(e.Key); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.None && session.HandleKey(e.Key)) { e.Handled = true; return; }
        // Selection/copy stay local to the read-only text control; never dispatch the shelf's copy command.
        if ((textContent.IsKeyboardFocusWithin || fileLocation.IsKeyboardFocusWithin || previewSearch.IsKeyboardFocusWithin) && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.C or Key.A) return;
        // A separate top-level window plus this boundary prevents owner shortcuts firing.
        if (e.Key is not (Key.Tab or Key.Enter or Key.LeftAlt or Key.RightAlt or Key.System)) e.Handled = true;
    }
    private static ScrollViewer? FindScrollViewer(DependencyObject root) {
        if (root is ScrollViewer viewer) return viewer;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) if (FindScrollViewer(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }
    private void OnPreviewWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0 || zoomControls.Visibility != Visibility.Visible) return;
        SetVisualZoom(visualZoom * Math.Pow(1.1, e.Delta / 120d)); e.Handled = true;
    }
    private void SetVisualZoom(double factor)
    {
        if (zoomControls.Visibility != Visibility.Visible) return;
        visualZoom = Math.Clamp(Math.Round(factor, 3), .5, 4);
        zoomLabel.Text = $"{visualZoom:P0}";
        double width = Math.Max(240, ActualWidth - 70) * visualZoom;
        image.Width = width; previous.Width = width;
        zoomTimer.Stop(); zoomTimer.Start();
    }
    internal void NavigatePage(int delta) => session.NavigatePage(delta);
    internal void NavigateFile(int delta) => session.NavigateRecord(delta);
    internal void NavigateTextSegment(int delta) { RememberTextViewState(); session.NavigateTextSegment(delta); }
    internal void ToggleTextWrap()
    {
        if (session.PresentedTextViewModel is not { } view) return;
        view.WordWrap = !view.WordWrap; ApplyTextOptions(view); UpdateTextButtonState(view);
    }
    internal void ToggleLineNumbers()
    {
        if (session.PresentedTextViewModel is not { } view) return;
        view.ShowLineNumbers = !view.ShowLineNumbers; ApplyTextOptions(view); UpdateTextButtonState(view);
    }
    internal void ShowTextSearch()
    {
        if (session.PresentedTextViewModel is not { } view) return;
        searchPanel.Visibility = Visibility.Visible; updatingSearch = true; previewSearch.Text = view.SearchText; updatingSearch = false;
        previewSearch.Focus(); previewSearch.SelectAll();
    }
    internal void HideTextSearch() { searchPanel.Visibility = Visibility.Collapsed; textContent.Focus(); }
    internal bool FindText(bool backwards, bool restart = false)
    {
        if (session.PresentedTextViewModel is not { } view) return false;
        if (!ReferenceEquals(view, displayedTextViewModel)) displayedTextViewModel = view;
        bool found = view.Find(backwards, restart); searchStatus.Text = string.IsNullOrEmpty(view.SearchText) ? "" : found ? "已找到" : "无结果";
        if (found) { textContent.Focus(); textContent.Select(view.MatchStart, view.MatchLength); textContent.ScrollToLine(Math.Max(0, view.Lines.Starts.TakeWhile(start => start <= view.MatchStart).Count() - 1)); }
        return found;
    }
    private void RememberTextViewState() { if (displayedTextViewModel is { } view && textPanel.IsVisible) view.ScrollOffset = scroll.VerticalOffset; }
    private void Update()
    {
        using var timing = PreviewMetrics.Measure("bitmap-submit");
        if (closing) return;
        if (lastRecordIndex != session.Index) { lastRecordIndex = session.Index; visualZoom = 1; zoomTimer.Stop(); RecordChanged?.Invoke(session.Current); }
        title.Text = session.Current.Kind == ClipKind.File && session.Path is { } path ? Path.GetFileName(path) : session.Current.DisplayTitle;
        icon.Item = session.Current; icon.SetResourceReference(RecordTypeIcon.PaletteProperty, "TextBrush");
        back.IsEnabled = session.Count > 0 && session.Page > 0; next.IsEnabled = session.Count > 0 && (!session.CountFinal || session.Page < session.Count - 1);
        bool textMode = session.Error is null && PreviewFormatRegistry.FormatOf(session.Current) == PreviewFormat.Text;
        bool sheetMode = session.Error is null && PreviewFormatRegistry.FormatOf(session.Current) == PreviewFormat.Spreadsheet;
        zoomControls.Visibility = session.Error is null && !textMode && !sheetMode && PreviewFormatRegistry.Supports(session.Current) ? Visibility.Visible : Visibility.Collapsed;
        zoomLabel.Text = $"{visualZoom:P0}";
        searchToggle.Visibility = wrapToggle.Visibility = linesToggle.Visibility = textMode ? Visibility.Visible : Visibility.Hidden;
        back.Visibility = next.Visibility = textMode ? Visibility.Hidden : Visibility.Visible;
        if (!textMode) searchPanel.Visibility = Visibility.Collapsed;
        if (!session.Loading) pages.Text = session.Error is not null ? "— / —" : session.PresentedSpreadsheet is { } table ? $"{table.SheetIndex + 1} / {table.SheetCount} 工作表" : session.PresentedText is { } textResult ? textResult.IsFile && textResult.IsLarge ? $"第 {textResult.SegmentIndex + 1} 段" : textResult.IsFile ? "文本" : "文字" :
            session.Presented is { } shown ? PreviewFormatRegistry.FormatOf(session.Current) == PreviewFormat.Image ? "图片" : session.CountFinal ? $"第 {shown.Page + 1} / {session.Count} 页" : $"第 {shown.Page + 1} 页" : "— / —";
        loading.Visibility = session.Loading ? Visibility.Visible : Visibility.Collapsed;
        loadingText.Text = session.LoadingStatus;
        if (session.Loading) { previousSegment.IsEnabled = false; nextSegment.IsEnabled = false; }
        skeleton.Visibility = displayed is null && displayedText is null && displayedSpreadsheet is null && session.Loading ? Visibility.Visible : Visibility.Collapsed;
        errorLayer.Visibility = session.Error is null ? Visibility.Collapsed : Visibility.Visible;
        errorText.Text = session.Error?.Message ?? ""; retry.Visibility = session.Error?.Code == "Unsupported" ? Visibility.Collapsed : Visibility.Visible;
        fileLocation.Text = ActionPath is { } location ? "文件位置：" + location : "";
        fileLocation.Visibility = session.Error is not null && fileLocation.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        warnings.Text = session.PresentedSpreadsheet is { } sh ? "近似预览" + (sh.Truncated ? " · 仅显示前 500 行 / 50 列" : "") + (sh.MissingFormulaCache ? " · 部分公式结果未保存" : "") + (sh.HasUnsupportedLayout ? " · 部分排版未还原" : "") : session.Presented?.IsApproximate == true ? "近似预览" + (session.Presented.Warnings?.Length > 0 ? " · " + string.Join("、", session.Presented.Warnings) : "") : "";
        warnings.Visibility = session.Error is null && !session.Loading && warnings.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (session.Error is not null || (session.Loading && session.Presented?.Quality != PreviewQuality.Thumbnail)) return;
        if (session.PresentedSpreadsheet is { } sheet) {
            if (ReferenceEquals(displayedSpreadsheet, sheet)) return;
            displayedSpreadsheet = sheet; displayedText = null; displayedTextViewModel = null; displayed = null;
            image.Source = previous.Source = null; image.Visibility = Visibility.Collapsed; textPanel.Visibility = Visibility.Collapsed;
            scroll.Visibility = Visibility.Collapsed; spreadsheet.ItemsSource = sheet.Rows; spreadsheet.Visibility = Visibility.Visible;
            title.Text = Path.GetFileName(session.Path) + " · " + sheet.SheetName;
            spreadsheet.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 1)));
            return;
        }
        spreadsheet.Visibility = Visibility.Collapsed; scroll.Visibility = Visibility.Visible;
        if (session.PresentedText is { } text) {
            if (ReferenceEquals(displayedText, text) && ReferenceEquals(displayedTextViewModel, session.PresentedTextViewModel)) return;
            scroll.CancelWheelMotion(); image.Visibility = Visibility.Collapsed; image.Source = previous.Source = null; displayed = null; displayedSpreadsheet = null;
            displayedTextViewModel = session.PresentedTextViewModel; textContent.Text = text.Text; textContent.Visibility = textPanel.Visibility = Visibility.Visible; displayedText = text;
            textNotice.Text = text.Notice.Length > 0 ? text.Notice : text.Truncated ? "文字较长，快速预览仅显示当前安全范围；原记录内容完整保留。" : text.Text.Length == 0 ? (text.IsFile ? "文件为空。" : "这条文字记录没有内容。") : "";
            textNotice.Visibility = textNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            previousSegment.IsEnabled = text.HasPrevious; nextSegment.IsEnabled = text.HasNext;
            segmentControls.Visibility = text.IsLarge ? Visibility.Visible : Visibility.Collapsed;
            if (displayedTextViewModel is { } view) { ApplyTextOptions(view); UpdateTextButtonState(view); updatingSearch = true; previewSearch.Text = view.SearchText; updatingSearch = false; }
            Dispatcher.BeginInvoke(new Action(() => scroll.ScrollToVerticalOffset(displayedTextViewModel?.ScrollOffset ?? 0)), DispatcherPriority.Loaded);
            textPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 1)));
            return;
        }
        if (session.Presented is not { } page || ReferenceEquals(displayed, page)) return;
        // Only the content changes. Hold the last decoded frame until a valid new page arrives.
        scroll.CancelWheelMotion(); bool changedPage = displayed is null || displayed.DocumentId != page.DocumentId || displayed.Page != page.Page;
        previous.Source = image.Source; image.Source = page.Image; displayed = page;
        image.Width = previous.Width = Math.Max(240, ActualWidth - 70) * visualZoom;
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        if (changedPage) scroll.ScrollToTop();
        displayedText = null; displayedTextViewModel = null; displayedSpreadsheet = null; textPanel.Visibility = Visibility.Collapsed; textContent.Clear(); image.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 1));
        fade.Completed += (_, _) => { if (ReferenceEquals(displayed, page)) previous.Source = null; };
        image.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
    }
    private void ApplyTextOptions(TextPreviewViewModel view)
    {
        textContent.FontFamily = view.Result.Monospace ? new FontFamily("Cascadia Mono, Consolas") : new FontFamily("Segoe UI Variable Text, Segoe UI");
        lineNumbers.FontFamily = textContent.FontFamily;
        textContent.TextWrapping = view.WordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        scroll.HorizontalScrollBarVisibility = view.WordWrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        lineNumbers.Text = view.Lines.Numbers(view.Result.FirstLineNumber); lineNumbers.Visibility = view.ShowLineNumbers ? Visibility.Visible : Visibility.Collapsed;
    }
    private void UpdateTextButtonState(TextPreviewViewModel view)
    {
        wrapToggle.Opacity = view.WordWrap ? 1 : .55; linesToggle.Opacity = view.ShowLineNumbers ? 1 : .55;
        wrapToggle.ToolTip = view.WordWrap ? "自动换行：已开启" : "自动换行：已关闭";
        linesToggle.ToolTip = view.ShowLineNumbers ? "行号：已显示" : "行号：已隐藏";
    }
    private void Reveal()
    {
        string? path = ActionPath;
        if (path is null) return;
        try { var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true }; start.Arguments = "/select,\"" + Path.GetFullPath(path).Replace("\"", "") + "\""; Process.Start(start); }
        catch { errorText.Text = "无法在资源管理器中定位此路径。"; errorLayer.Visibility = Visibility.Visible; }
    }
    private void OpenDefault() {
        if (ActionPath is not { } path || (!File.Exists(path) && !Directory.Exists(path))) { errorText.Text = "文件已移动或删除，无法打开。"; errorLayer.Visibility = Visibility.Visible; return; }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch { errorText.Text = "默认应用无法打开此文件。"; errorLayer.Visibility = Visibility.Visible; }
    }
    private void AnimateCard(bool opening)
    {
        int duration = SystemParameters.ClientAreaAnimation ? opening ? 200 : 160 : 1;
        var easing = new CubicEase { EasingMode = opening ? EasingMode.EaseOut : EasingMode.EaseIn };
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(opening ? 0 : card.Opacity, opening ? 1 : 0, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
        if (SystemParameters.ClientAreaAnimation) {
            var scale = (ScaleTransform)card.RenderTransform;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(opening ? .97 : scale.ScaleX, opening ? 1 : .97, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(opening ? .97 : scale.ScaleY, opening ? 1 : .97, TimeSpan.FromMilliseconds(duration)) { EasingFunction = easing });
        }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (finishedClose) return;
        e.Cancel = true; if (closing) return; closing = true; session.Cancel(); AnimateCard(false);
        await Task.Delay(SystemParameters.ClientAreaAnimation ? 160 : 1); finishedClose = true; Close();
    }
    private async Task ReleaseAsync() { await session.DisposeAsync(); displayed = null; displayedText = null; if (ownsCache) cache.Dispose(); }
    internal async Task CloseAndReleaseAsync()
    {
        session.Cancel(); closing = finishedClose = true; Close(); await Cleanup;
    }
}
