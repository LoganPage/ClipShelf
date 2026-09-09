using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

public sealed class PreviewWindow : Window
{
    private readonly IReadOnlyList<ClipItem> items;
    private int index, fileIndex, page, pageCount, renderVersion, textLength = FilePreviewLoader.TextChunk;
    private bool closed;
    private double zoom = 1;
    private CancellationTokenSource? request;
    private static readonly SemaphoreSlim loadSlots = new(2);
    private readonly ContentControl content = new();
    private readonly TextBlock title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock detail = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) };
    private readonly WrapPanel tools = new();
    private readonly StackPanel actions = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
    private Image? displayedImage;
    private PreviewMediaControl? mediaPreview;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<(string Path, long Size, long Modified, int Page, int Length), Task<FilePreviewResult>> pageCache = new();
    private readonly Queue<(string Path, long Size, long Modified, int Page, int Length)> cacheOrder = new();
    private bool prefetching;
    internal object? PresentedContent => content.Content;
    internal Task PendingRender { get; private set; } = Task.CompletedTask;
    internal int FileIndex => fileIndex;
    internal int PageIndex => page;
    internal int RecordIndex => index;

    public PreviewWindow(IReadOnlyList<ClipItem> items, int index)
    {
        if (items.Count == 0) throw new ArgumentException("No preview items", nameof(items));
        this.items = items.ToArray(); this.index = Math.Clamp(index, 0, items.Count - 1);
        FocusCuePolicy.SetIsEnabled(this, true);
        Width = 820; Height = 620; MinWidth = 620; MinHeight = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BackgroundBrush"); SetResourceReference(ForegroundProperty, "TextBrush");
        var appearance = new WindowAppearance(this); Closed += (_, _) => appearance.Dispose();
        var grid = new DockPanel { Margin = new Thickness(18, 12, 18, 12) };
        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
        var navigation = new StackPanel { Orientation = Orientation.Horizontal };
        navigation.Children.Add(Button("上一条", () => Navigate(-1))); navigation.Children.Add(Button("下一条", () => Navigate(1)));
        DockPanel.SetDock(navigation, Dock.Right); header.Children.Add(navigation); header.Children.Add(title);
        DockPanel.SetDock(header, Dock.Top); grid.Children.Add(header);
        var footer = new StackPanel(); footer.Children.Add(detail); footer.Children.Add(actions);
        DockPanel.SetDock(footer, Dock.Bottom); grid.Children.Add(footer);
        tools.Margin = new Thickness(0, 0, 0, 10); DockPanel.SetDock(tools, Dock.Top); grid.Children.Add(tools);
        var surface = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(8), Child = content };
        surface.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush"); grid.Children.Add(surface); Content = grid;
        content.SizeChanged += (_, _) => SizeImage();
        PreviewKeyDown += (_, e) => {
            if (e.Key == Key.Escape || e.Key == Key.Space && Keyboard.Modifiers == ModifierKeys.None) { if (!e.IsRepeat) Close(); e.Handled = true; }
            else if (Keyboard.Modifiers != ModifierKeys.None) { }
            else if (e.Key is Key.Up or Key.Down) { Navigate(e.Key == Key.Up ? -1 : 1); e.Handled = true; }
            else if (e.Key is Key.Left or Key.Right) {
                int delta = e.Key == Key.Left ? -1 : 1;
                if (pageCount > 0) NavigatePage(delta);
                else if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.RangeBase) return;
                else if (!PendingRender.IsCompleted) { e.Handled = true; return; }
                else if (Current.Kind == ClipKind.File && Current.FilePaths.Count > 1) NavigateFile(delta);
                else Navigate(delta);
                e.Handled = true;
            }
            else if (e.Key is Key.PageUp or Key.PageDown && pageCount > 0) { NavigatePage(e.Key == Key.PageUp ? -1 : 1); e.Handled = true; }
        };
        Closed += (_, _) => { closed = true; renderVersion++; request?.Cancel(); request?.Dispose(); request = null; lifetime.Cancel(); pageCache.Clear(); cacheOrder.Clear(); mediaPreview?.Dispose(); };
        Render();
    }
    private ClipItem Current => items[index];
    private string? CurrentPath => Current.Kind == ClipKind.File && Current.FilePaths.Count > 0 ? Current.FilePaths[fileIndex] : null;
    private static Button Button(string label, Action click, bool enabled = true)
    {
        var button = new Button { Content = label, IsEnabled = enabled, Style = (Style)Application.Current.FindResource("SoftButton"), Margin = new Thickness(4, 0, 0, 0), Padding = new Thickness(10, 6, 10, 6) };
        button.Click += (_, _) => click(); return button;
    }
    private void Navigate(int delta)
    {
        int next = Math.Clamp(index + delta, 0, items.Count - 1); if (next == index) return;
        index = next; fileIndex = page = pageCount = 0; textLength = FilePreviewLoader.TextChunk; zoom = 1; Render();
    }
    internal void NavigateFile(int delta)
    {
        if (Current.Kind != ClipKind.File || Current.FilePaths.Count == 0) return;
        int next = Math.Clamp(fileIndex + delta, 0, Current.FilePaths.Count - 1); if (next == fileIndex) return;
        fileIndex = next; page = pageCount = 0; textLength = FilePreviewLoader.TextChunk; zoom = 1; Render();
    }
    internal void NavigatePage(int delta)
    {
        int next = Math.Clamp(page + delta, 0, Math.Max(0, pageCount - 1)); if (next == page) return;
        page = next; Render(preservePage: true);
    }
    private void Render(bool keepText = false, bool preservePage = false) => PendingRender = RenderAsync(keepText, preservePage);
    private async Task<FilePreviewResult> CachedLoad(string path, int requestedPage, int length)
    {
        var stamp = await Task.Run(() => { var info = new FileInfo(path); return (info.Exists, Size: info.Exists ? info.Length : 0L, Modified: info.Exists ? info.LastWriteTimeUtc.Ticks : 0L); });
        lifetime.Token.ThrowIfCancellationRequested();
        var key = (path, stamp.Size, stamp.Modified, requestedPage, length);
        if (stamp.Exists && pageCache.TryGetValue(key, out var cached)) return await cached;
        async Task<FilePreviewResult> Read() { await loadSlots.WaitAsync(lifetime.Token); try { return await FilePreviewLoader.LoadAsync(path, requestedPage, length, lifetime.Token); } finally { loadSlots.Release(); } }
        var pending = Read();
        if (stamp.Exists) {
            // Four decoded pages bound image memory, and the cache dies with the preview.
            while (pageCache.Count >= 4 && cacheOrder.TryDequeue(out var old)) pageCache.Remove(old);
            pageCache[key] = pending; cacheOrder.Enqueue(key);
        }
        try { return await pending; } catch { pageCache.Remove(key); throw; }
    }
    private async Task Prefetch(string path, int neighbor, int length, CancellationToken token)
    {
        if (prefetching) return;
        prefetching = true;
        try { await Task.Delay(180, token); token.ThrowIfCancellationRequested(); await CachedLoad(path, neighbor, length); }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
        finally { prefetching = false; }
    }
    private async Task RenderAsync(bool keepText, bool preservePage)
    {
        int version = ++renderVersion;
        request?.Cancel(); request?.Dispose(); request = new(); var token = request.Token;
        if (!preservePage) { displayedImage = null; tools.Children.Clear(); actions.Children.Clear(); }
        mediaPreview?.Dispose(); mediaPreview = null;
        string? path = CurrentPath;
        title.Text = path is null ? Current.DisplayTitle : Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));
        Title = "快速预览 · " + title.Text;
        detail.Text = $"记录 {index + 1} / {items.Count} · ↑↓ 切换记录 · ←→ 多页文件翻页 · Space / Esc 关闭";
        detail.SetResourceReference(TextBlock.ForegroundProperty, "MutedBrush");
        if (!preservePage && Current.Kind == ClipKind.File && Current.FilePaths.Count > 1)
        {
            tools.Children.Add(Button("←", () => NavigateFile(-1), fileIndex > 0));
            tools.Children.Add(new TextBlock { Text = $"文件 {fileIndex + 1} / {Current.FilePaths.Count}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
            tools.Children.Add(Button("→", () => NavigateFile(1), fileIndex + 1 < Current.FilePaths.Count));
        }
        if (Current.Kind == ClipKind.Text) { ShowText(Current.PreviewText); return; }
        var previousText = keepText ? content.Content as TextBox : null;
        double previousOffset = previousText?.VerticalOffset ?? 0;
        if (previousText is null && !preservePage) {
            var loading = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            loading.Children.Add(new TextBlock { Text = "正在准备文件预览", HorizontalAlignment = HorizontalAlignment.Center });
            loading.Children.Add(new ProgressBar { IsIndeterminate = true, Width = 180, Height = 3, Margin = new Thickness(0, 14, 0, 14) });
            loading.Children.Add(new TextBlock { Text = "大文件首次打开可能较慢 · Esc 取消 · ↑↓ 切换记录", FontSize = 11 });
            content.Content = loading;
        }
        var item = Current; int requestedPage = page, requestedLength = textLength;
        try
        {
            // Coalesce key-repeat bursts without ever clearing the currently visible page.
            if (preservePage) await Task.Delay(45, token);
            FilePreviewResult result;
                result = item.Kind == ClipKind.Image && item.ImagePath is string imagePath
                    ? new FilePreviewResult("图片", "剪贴板图片", Image: await Task.Run(() => FilePreviewLoader.LoadImage(imagePath, token), token))
                    : path is not null ? await CachedLoad(path, requestedPage, requestedLength)
                    : new FilePreviewResult("信息", "记录不包含文件", Exists: false);
            if (closed || version != renderVersion) return;
            if (preservePage) {
                tools.Children.Clear(); actions.Children.Clear();
                if (Current.Kind == ClipKind.File && Current.FilePaths.Count > 1) {
                    tools.Children.Add(Button("← 文件", () => NavigateFile(-1), fileIndex > 0));
                    tools.Children.Add(Button("文件 →", () => NavigateFile(1), fileIndex + 1 < Current.FilePaths.Count));
                }
            }
            detail.Text = result.Details + "\n" + detail.Text;
            if (path is not null)
            {
                actions.Children.Add(Button("用默认应用打开", () => OpenFile(path), result.Exists && FilePreviewLoader.CanOpen(path)));
                actions.Children.Add(Button("在文件夹中显示", () => Reveal(path), result.Exists));
            }
            pageCount = result.PageCount;
                if (pageCount > 0) {
                    tools.Children.Add(Button("上一页", () => NavigatePage(-1), page > 0));
                    tools.Children.Add(new TextBlock { Text = $"{result.Section ?? "页"} {page + 1} / {pageCount}", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0) });
                    tools.Children.Add(Button("下一页", () => NavigatePage(1), page + 1 < pageCount));
                }
            if (result.Table is not null) ShowTable(result.Table);
            else if (result.MediaPath is not null) content.Content = mediaPreview = new PreviewMediaControl(result.MediaPath);
            else if (result.Image is not null)
            {
                tools.Children.Add(Button("−", () => { zoom = Math.Max(.25, zoom - .25); SizeImage(); }));
                tools.Children.Add(Button("适应窗口", () => { zoom = 1; SizeImage(); }));
                tools.Children.Add(Button("＋", () => { zoom = Math.Min(4, zoom + .25); SizeImage(); }));
                if (preservePage && displayedImage is not null) displayedImage.Source = result.Image;
                else {
                    displayedImage = new Image { Source = result.Image, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
                    content.Content = new ScrollViewer { Content = displayedImage, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
                }
                SizeImage();
            }
            else {
                if (previousText is not null && result.Kind == "文本") {
                    previousText.Text = result.Text ?? "";
                    await Dispatcher.InvokeAsync(() => { if (version == renderVersion && !closed) previousText.ScrollToVerticalOffset(previousOffset); }, System.Windows.Threading.DispatcherPriority.Loaded);
                } else ShowText(result.Text ?? result.Details);
                if (closed || version != renderVersion) return;
                if (result.HasMore && textLength < FilePreviewLoader.TextLimit)
                    tools.Children.Add(Button("再加载一段", () => { textLength += FilePreviewLoader.TextChunk; Render(true); }));
                else if (result.HasMore) detail.Text += " · 已达预览上限，请用默认应用查看全文";
            }
            if (path is not null && result.PageCount > 1) {
                if (page + 1 < result.PageCount) _ = Prefetch(path, page + 1, requestedLength, token);
                else if (page > 0) _ = Prefetch(path, page - 1, requestedLength, token);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException)
        {
            if (closed || version != renderVersion) return;
            ShowText("无法预览此文件。文件可能已移动、被占用、损坏、加密，或缺少对应解码器。\n\n" + path);
            if (path is not null && FilePreviewLoader.IsLocal(path)) {
                actions.Children.Add(Button("用默认应用打开", () => OpenFile(path), FilePreviewLoader.CanOpen(path)));
                actions.Children.Add(Button("在文件夹中显示", () => Reveal(path)));
            }
        }
    }
    private void ShowTable(PreviewTable table)
    {
        if (content.Content is DataGrid previous && previous.Columns.Select(c => c.Header?.ToString()).SequenceEqual(table.Headers)) {
            previous.ItemsSource = table.Rows;
            if (table.Rows.Count > 0) previous.ScrollIntoView(table.Rows[0]);
            return;
        }
        var grid = new DataGrid { IsReadOnly = true, AutoGenerateColumns = false, CanUserAddRows = false, CanUserDeleteRows = false, CanUserReorderColumns = false, CanUserSortColumns = false, EnableRowVirtualization = true, EnableColumnVirtualization = true, HeadersVisibility = DataGridHeadersVisibility.Column, RowHeight = 30, GridLinesVisibility = DataGridGridLinesVisibility.None, BorderThickness = new Thickness(0), ItemsSource = table.Rows };
        grid.SetResourceReference(BackgroundProperty, "SurfaceBrush");
        grid.SetResourceReference(ForegroundProperty, "TextBrush");
        grid.SetResourceReference(DataGrid.RowBackgroundProperty, "SurfaceBrush");
        var header = new Style(typeof(System.Windows.Controls.Primitives.DataGridColumnHeader));
        header.Setters.Add(new Setter(BackgroundProperty, new DynamicResourceExtension("BackgroundBrush")));
        header.Setters.Add(new Setter(ForegroundProperty, new DynamicResourceExtension("TextBrush")));
        header.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8)));
        grid.ColumnHeaderStyle = header;
        for (int i = 0; i < table.Headers.Count; i++) grid.Columns.Add(new DataGridTextColumn { Header = table.Headers[i], Binding = new System.Windows.Data.Binding($"[{i}]"), Width = 160, MinWidth = 60 });
        content.Content = grid;
    }
    private void ShowText(string text) => content.Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, BorderThickness = new Thickness(0), Padding = new Thickness(10), FontFamily = new FontFamily("Cascadia Code, Consolas, Microsoft YaHei UI"), FontSize = 14 };
    private void SizeImage()
    {
        if (displayedImage?.Source is not BitmapSource bitmap) return;
        double fit = Math.Min(Math.Max(1, content.ActualWidth - 24) / bitmap.PixelWidth, Math.Max(1, content.ActualHeight - 24) / bitmap.PixelHeight);
        displayedImage.Width = bitmap.PixelWidth * fit * zoom; displayedImage.Height = bitmap.PixelHeight * fit * zoom;
    }
    private void OpenFile(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(this, "无法打开文件，请检查文件是否存在以及默认应用设置。", "快速预览"); }
    }
    private void Reveal(string path)
    {
        try { SettingsPanel.Reveal(path); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { MessageBox.Show(this, "无法显示文件位置。", "快速预览"); }
    }
}
