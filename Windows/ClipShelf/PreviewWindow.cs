using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
    private readonly TextBlock textNotice = new() { Margin = new(14, 8, 14, 12), TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel textPanel = new() { Visibility = Visibility.Collapsed };
    private readonly SmoothScrollViewer scroll = new();
    private readonly Border loading = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16), Padding = new(14, 8, 14, 8), CornerRadius = new(8), IsHitTestVisible = false };
    private readonly TextBlock loadingText = new() { Text = "正在准备文档…" };
    private readonly Border skeleton = new() { Width = 300, Height = 400, CornerRadius = new(8), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border errorLayer = new() { Padding = new(32), CornerRadius = new(8), Visibility = Visibility.Collapsed };
    private readonly TextBlock errorText = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 480, TextAlignment = TextAlignment.Center };
    private readonly TextBox fileLocation = new() { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center,
        MaxWidth = 480, MaxHeight = 120, Margin = new(0, 14, 0, 0), BorderThickness = new(0), Background = Brushes.Transparent,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Visibility = Visibility.Collapsed };
    private readonly TextBlock warnings = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(16), MaxWidth = 400, TextTrimming = TextTrimming.CharacterEllipsis, IsHitTestVisible = false };
    private readonly Button back, next, retry;
    private RenderedPage? displayed;
    private TextPreviewResult? displayedText;
    private bool closing, finishedClose;
    private readonly DispatcherTimer resizeTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    internal Task Cleanup { get; private set; } = Task.CompletedTask;
    internal object? PresentedContent => session.Error is not null ? errorText : displayedText is not null ? textContent : image;
    internal Task PendingRender => session.Pending;
    internal int FileIndex => 0;
    internal int PageIndex => session.Page;
    internal int RecordIndex => session.Index;
    internal PreviewSession Session => session;
    internal string DisplayedLocation => fileLocation.Text;
    private string? ActionPath => session.Path ?? session.Current.SourcePath ?? (session.Current.FilePaths.Count > 0 ? session.Current.FilePaths[0] : null);
    internal void SetAnimationOrigin(double rowFraction) => card.RenderTransformOrigin = new Point(.5, Math.Clamp(rowFraction, .1, .9));
    public PreviewWindow(IReadOnlyList<ClipItem> items, int index) : this(items, index, null) { }
    internal PreviewWindow(IReadOnlyList<ClipItem> items, int index, PreviewCacheService? sharedCache)
    {
        if (items.Count == 0) throw new ArgumentException("No preview items", nameof(items));
        cache = sharedCache ?? new(); ownsCache = sharedCache is null; session = new(items, index, cache);
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
        back = Button("\uE76B", "上一页（←）", () => NavigatePage(-1)); next = Button("\uE76C", "下一页（→）", () => NavigatePage(1));
        controls.Children.Add(back); controls.Children.Add(next); controls.Children.Add(Button("\uE838", "在资源管理器中显示", Reveal)); controls.Children.Add(Button("\uE8BB", "关闭（Space / Esc）", Close));
        DockPanel.SetDock(controls, Dock.Right); toolbar.Children.Add(controls); toolbar.Children.Add(icon); toolbar.Children.Add(title); layout.Children.Add(toolbar);
        var content = new Grid { ClipToBounds = true, Margin = new(10, 0, 10, 10) }; Grid.SetRow(content, 1); layout.Children.Add(content);
        var pageLayers = new Grid { Margin = new(12) };
        var retained = new Canvas { IsHitTestVisible = false, ClipToBounds = true };
        previous.SetBinding(WidthProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = pageLayers });
        retained.Children.Add(previous); pageLayers.Children.Add(retained); pageLayers.Children.Add(image);
        textContent.SetResourceReference(ForegroundProperty, "TextBrush"); textNotice.SetResourceReference(ForegroundProperty, "MutedBrush");
        textPanel.Children.Add(textContent); textPanel.Children.Add(textNotice); pageLayers.Children.Add(textPanel);
        scroll.Content = pageLayers; content.Children.Add(scroll);
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
        card.Child = layout; Content = card;
        session.Changed += Update;
        PreviewKeyDown += OnPreviewKey;
        PreviewKeyUp += (_, e) => e.Handled = true;
        Loaded += (_, _) => { session.PixelDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX; session.PixelWidth = Math.Clamp((int)((ActualWidth - 70) * VisualTreeHelper.GetDpi(this).DpiScaleX), 480, 1800); AnimateCard(true); session.Start(); Focus(); };
        resizeTimer.Tick += (_, _) => { resizeTimer.Stop(); if (!closing) { session.PixelDpi = VisualTreeHelper.GetDpi(this).PixelsPerInchX; session.Resize((int)((ActualWidth - 70) * VisualTreeHelper.GetDpi(this).DpiScaleX)); } };
        SizeChanged += (_, _) => { if (IsLoaded && !closing) { resizeTimer.Stop(); resizeTimer.Start(); } };
        DpiChanged += (_, _) => { if (!closing) { resizeTimer.Stop(); resizeTimer.Start(); } };
        Closing += OnClosing;
        Closed += (_, _) => { resizeTimer.Stop(); scroll.CancelWheelMotion(); session.Changed -= Update; PreviewKeyDown -= OnPreviewKey; appearance.Dispose(); image.Source = previous.Source = null; textContent.Clear(); Cleanup = ReleaseAsync(); };
    }
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
        if (e.Key is Key.Space or Key.Escape) { if (!e.IsRepeat) Close(); e.Handled = true; return; }
        if (Keyboard.Modifiers == ModifierKeys.None && session.HandleKey(e.Key)) { e.Handled = true; return; }
        // Selection/copy stay local to the read-only text control; never dispatch the shelf's copy command.
        if ((textContent.IsKeyboardFocusWithin || fileLocation.IsKeyboardFocusWithin) && Keyboard.Modifiers == ModifierKeys.Control && e.Key is Key.C or Key.A) return;
        // A separate top-level window plus this boundary prevents owner shortcuts firing.
        if (e.Key is not (Key.Tab or Key.Enter or Key.LeftAlt or Key.RightAlt or Key.System)) e.Handled = true;
    }
    internal void NavigatePage(int delta) => session.NavigatePage(delta);
    internal void NavigateFile(int delta) => session.NavigateRecord(delta);
    private void Update()
    {
        using var timing = PreviewMetrics.Measure("bitmap-submit");
        if (closing) return;
        title.Text = session.Current.Kind == ClipKind.File && session.Path is { } path ? Path.GetFileName(path) : session.Current.DisplayTitle;
        icon.Item = session.Current; icon.SetResourceReference(RecordTypeIcon.PaletteProperty, "TextBrush");
        back.IsEnabled = session.Count > 0 && session.Page > 0; next.IsEnabled = session.Count > 0 && (!session.CountFinal || session.Page < session.Count - 1);
        if (!session.Loading) pages.Text = session.Error is not null ? "— / —" : session.PresentedText is not null ? "文字" :
            session.Presented is { } shown ? PreviewFormatRegistry.FormatOf(session.Current) == PreviewFormat.Image ? "图片" : session.CountFinal ? $"第 {shown.Page + 1} / {session.Count} 页" : $"第 {shown.Page + 1} 页" : "— / —";
        loading.Visibility = session.Loading ? Visibility.Visible : Visibility.Collapsed;
        loadingText.Text = displayed is null && displayedText is null ? "正在准备预览…" : "正在载入…";
        skeleton.Visibility = displayed is null && displayedText is null && session.Loading ? Visibility.Visible : Visibility.Collapsed;
        errorLayer.Visibility = session.Error is null ? Visibility.Collapsed : Visibility.Visible;
        errorText.Text = session.Error?.Message ?? ""; retry.Visibility = session.Error?.Code == "Unsupported" ? Visibility.Collapsed : Visibility.Visible;
        fileLocation.Text = ActionPath is { } location ? "文件位置：" + location : "";
        fileLocation.Visibility = session.Error is not null && fileLocation.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        warnings.Text = session.Presented?.IsApproximate == true ? "近似预览" + (session.Presented.Warnings?.Length > 0 ? " · " + string.Join("、", session.Presented.Warnings) : "") : "";
        warnings.Visibility = session.Error is null && !session.Loading && warnings.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (session.Error is not null || (session.Loading && session.Presented?.Quality != PreviewQuality.Thumbnail)) return;
        if (session.PresentedText is { } text) {
            if (ReferenceEquals(displayedText, text)) return;
            scroll.CancelWheelMotion(); image.Visibility = Visibility.Collapsed; image.Source = previous.Source = null; displayed = null;
            textContent.Text = text.Text; textContent.Visibility = textPanel.Visibility = Visibility.Visible; displayedText = text;
            textNotice.Text = text.Truncated ? "文字较长，快速预览仅显示前 100,000 个字符；原记录内容完整保留。" : text.Text.Length == 0 ? "这条文字记录没有内容。" : "";
            textNotice.Visibility = textNotice.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            scroll.ScrollToTop(); textPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 1)));
            return;
        }
        if (session.Presented is not { } page || ReferenceEquals(displayed, page)) return;
        // Only the content changes. Hold the last decoded frame until a valid new page arrives.
        scroll.CancelWheelMotion(); previous.Source = image.Source; image.Source = page.Image; displayed = page; scroll.ScrollToTop();
        displayedText = null; textPanel.Visibility = Visibility.Collapsed; textContent.Clear(); image.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 130 : 1));
        fade.Completed += (_, _) => { if (ReferenceEquals(displayed, page)) previous.Source = null; };
        image.BeginAnimation(OpacityProperty, fade, HandoffBehavior.SnapshotAndReplace);
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
