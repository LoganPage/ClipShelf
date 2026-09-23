using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;

namespace ClipShelf;

internal readonly record struct PreviewRequest(string DocumentId, int Page, long Version);
internal sealed class PreviewSession : IAsyncDisposable
{
    private readonly IReadOnlyList<ClipItem> items;
    private readonly IPreviewPageRenderer renderer;
    private readonly TextImagePreviewService textImages;
    private readonly TextFilePreviewProvider textFiles;
    private readonly PreviewCacheService cache;
    private readonly bool customRenderer;
    private readonly bool prewarmAdjacent;
    private readonly Dictionary<string, int> rememberedPages = new();
    private readonly Dictionary<string, TextPreviewViewModel> rememberedText = new();
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? request;
    private DocumentIdentity? identity;
    private bool closed;
    private FileSystemWatcher? watcher;
    private long version;
    private Task prefetch = Task.CompletedTask;
    internal Task WarmupPending { get; private set; } = Task.CompletedTask;
    internal Task PrefetchPending => prefetch;
    internal int WarmupCompleted { get; private set; }
    private readonly ConcurrentDictionary<Task, byte> work = new();
    internal event Action? Changed;
    internal int Index { get; private set; }
    internal int Page { get; private set; }
    internal int Count { get; private set; }
    internal bool CountFinal { get; private set; } = true;
    internal int PixelWidth { get; set; } = 1000;
    internal double PixelDpi { get; set; } = 96;
    internal double ZoomFactor { get; private set; } = 1;
    private int RenderWidth => Math.Clamp((int)Math.Round(PixelWidth * ZoomFactor), 240, 4096);
    internal bool Loading { get; private set; }
    internal PreviewException? Error { get; private set; }
    internal RenderedPage? Presented { get; private set; }
    internal TextPreviewResult? PresentedText { get; private set; }
    internal TextPreviewViewModel? PresentedTextViewModel { get; private set; }
    internal string LoadingStatus { get; private set; } = "正在准备预览…";
    internal Task Pending { get; private set; } = Task.CompletedTask;
    internal ClipItem Current => items[Index];
    internal string? Path => PreviewFormatRegistry.PathOf(Current);
    internal PreviewRequest CurrentRequest => new(identity?.Id ?? Path ?? Current.Id.ToString(), Page, version);
    internal PreviewSession(IReadOnlyList<ClipItem> items, int index, PreviewCacheService cache, IPreviewPageRenderer? renderer = null, bool prewarmAdjacent = false)
    {
        this.items = items.ToArray(); Index = Math.Clamp(index, 0, items.Count - 1); this.renderer = renderer ?? new PdfPageRenderService(cache);
        textImages = new(cache); textFiles = new(cache);
        this.cache = cache;
        customRenderer = renderer is not null;
        this.prewarmAdjacent = prewarmAdjacent;
    }
    internal static int BoundPage(int page, int count) => Math.Clamp(page, 0, Math.Max(0, count - 1));
    internal bool Accepts(PreviewRequest stamp) => !closed && stamp == CurrentRequest;
    internal void Start() => Load(reidentify: true);
    internal void Retry() { StopWatching(); identity = null; Load(reidentify: true); }
    internal void SetZoomFactor(double factor)
    {
        double zoom = Math.Clamp(Math.Round(factor, 3), .5, 4);
        if (Math.Abs(ZoomFactor - zoom) < .001 || closed) return;
        ZoomFactor = zoom;
        if (Count > 0 && PreviewFormatRegistry.FormatOf(Current) != PreviewFormat.Text) Load(reidentify: false);
    }
    internal void Resize(int pixelWidth, bool deferRender = false)
    {
        int width = Math.Clamp(pixelWidth, 480, 1800);
        if (Math.Abs(PixelWidth - width) < 64) return;
        PixelWidth = width;
        if (!deferRender && Count > 0 && !closed && PreviewFormatRegistry.FormatOf(Current) != PreviewFormat.Text) Load(reidentify: false);
    }
    internal void NavigateRecord(int direction)
    {
        if (closed || direction == 0) return;
        int next = Index + Math.Sign(direction);
        if (next < 0 || next >= items.Count || next == Index) return;
        if (identity is not null) rememberedPages[identity.Id] = Page;
        StopWatching(); Index = next; identity = null; Count = Page = 0; CountFinal = true; ZoomFactor = 1; cache.SetActiveDocument(null); Load(reidentify: true);
    }
    internal void NavigatePage(int delta) => SetPage(CountFinal ? BoundPage(Page + delta, Count) : Math.Clamp(Page + delta, 0, 999));
    internal void SetPage(int page)
    {
        if (closed || Count == 0) return;
        int target = CountFinal ? BoundPage(page, Count) : Math.Clamp(page, 0, 999); if (target == Page) return;
        Page = target; Load(reidentify: false);
    }
    internal void NavigateTextSegment(int direction)
    {
        if (closed || direction == 0 || PresentedTextViewModel?.Document is not { } document) return;
        if ((direction < 0 && document.SegmentIndex == 0) || (direction > 0 && document.Current.EndOfFile)) return;
        request?.Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long revision = ++version; Loading = true; LoadingStatus = "文件过大，正在分段加载…"; Error = null; Changed?.Invoke();
        Pending = Track(LoadTextSegmentAsync(PresentedTextViewModel, direction, revision, request.Token));
    }
    internal bool HandleKey(Key key)
    {
        switch (key) {
            case Key.Up: NavigateRecord(-1); return true; case Key.Down: NavigateRecord(1); return true;
            case Key.Left: NavigatePage(-1); return true; case Key.Right: NavigatePage(1); return true;
            case Key.Home: SetPage(0); return true; case Key.End: SetPage(CountFinal ? Count - 1 : 999); return true;
            default: return false;
        }
    }
    private void Load(bool reidentify)
    {
        if (closed) return;
        request?.Cancel(); request?.Dispose(); request = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        long revision = ++version; Loading = true; Error = null;
        LoadingStatus = PreviewFormatRegistry.FormatOf(Current) == PreviewFormat.Text && Current.Kind == ClipKind.File ? "正在检测文本编码…" : "正在准备预览…";
        Changed?.Invoke();
        Pending = Track(LoadAsync(reidentify, revision, request.Token));
    }
    private Task Track(Task task)
    {
        work.TryAdd(task, 0);
        _ = task.ContinueWith(completed => work.TryRemove(completed, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }
    private async Task LoadAsync(bool reidentify, long revision, CancellationToken token)
    {
        try {
            var format = PreviewFormatRegistry.FormatOf(Current);
            if (format == PreviewFormat.Text && Current.Kind == ClipKind.Text) {
                var textStamp = CurrentRequest;
                var text = await TextImagePreviewService.ReadTextAsync(Current, token);
                if (!Accepts(textStamp)) return;
                string key = "record:" + Current.Id;
                if (!rememberedText.TryGetValue(key, out var textView)) rememberedText[key] = textView = new TextPreviewViewModel(text);
                else textView.Apply(text);
                PresentText(textView); ScheduleWarmup(token); return;
            }
            string? path = Path;
            if (!PreviewFormatRegistry.Supports(Current)) throw new PreviewException("Unsupported", "当前格式暂不支持快速预览，可使用默认应用打开。");
            if (path is null) throw new PreviewException("MissingFile", "图片或文档文件已移动或删除。");
            if (reidentify) {
                var found = await Task.Run(() => DocumentIdentity.Read(path), token);
                if (closed || revision != version) return;
                identity = found; cache.SetActiveDocument(found.Id); Page = rememberedPages.GetValueOrDefault(found.Id); Watch(found);
            }
            if (format == PreviewFormat.Text) {
                var textStamp = CurrentRequest;
                if (!rememberedText.TryGetValue(identity!.Id, out var textView)) {
                    LoadingStatus = "正在读取文本…"; Changed?.Invoke();
                    var document = await textFiles.OpenAsync(identity, token);
                    if (!Accepts(textStamp)) return;
                    textView = new TextPreviewViewModel(Result(document), document); rememberedText[identity.Id] = textView;
                }
                if (!Accepts(textStamp)) return;
                PresentText(textView); ScheduleWarmup(token); return;
            }
            var stamp = CurrentRequest;
            int renderWidth = RenderWidth; double zoom = ZoomFactor;
            var cached = !customRenderer && format != PreviewFormat.Image ? await Task.Run(() => cache.ReadPage(PreviewCacheService.RenderKey(identity!, stamp.Page, renderWidth, PixelDpi, zoom: zoom), token), token) : null;
            if (cached is not null && (cached.DocumentId != stamp.DocumentId || cached.Page != stamp.Page)) cached = null;
            if (cached is null && reidentify && stamp.Page == 0 && format == PreviewFormat.PowerPoint) {
                var thumbnail = await renderer.ThumbnailAsync(identity!, token);
                if (!Accepts(stamp)) return;
                if (thumbnail is not null && thumbnail.DocumentId == stamp.DocumentId) { Presented = thumbnail with { RequestVersion = stamp.Version }; PresentedText = null; PresentedTextViewModel = null; Count = thumbnail.Count; CountFinal = thumbnail.CountFinal; Changed?.Invoke(); }
            }
            var result = cached ?? (format == PreviewFormat.Image
                ? await textImages.RenderImageAsync(identity!, renderWidth, token, zoom)
                : await renderer.RenderViewportAsync(identity!, stamp.Page, renderWidth, PixelDpi, stamp.Version, token, zoom));
            if (!Accepts(stamp)) return;
            if (result.DocumentId != stamp.DocumentId) throw new PreviewException("InvalidDocument", "预览结果与当前文档不匹配，请重试。");
            Page = result.Page; Count = result.Count; CountFinal = result.CountFinal; Presented = result with { RequestVersion = stamp.Version }; PresentedText = null; PresentedTextViewModel = null; rememberedPages[result.DocumentId] = Page; Loading = false;
            Changed?.Invoke();
            if (format != PreviewFormat.Image) prefetch = Track(PrefetchAsync(identity!, Page, Count, renderWidth, zoom, token));
            else ScheduleWarmup(token);
        } catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && revision == version) { Error = PreviewException.From(error); Loading = false; Changed?.Invoke(); } }
    }
    private async Task LoadTextSegmentAsync(TextPreviewViewModel view, int direction, long revision, CancellationToken token)
    {
        try {
            var stamp = CurrentRequest;
            await textFiles.MoveAsync(view.Document!, direction, token);
            if (!Accepts(stamp) || revision != version) return;
            view.Apply(Result(view.Document!)); PresentText(view);
        } catch (OperationCanceledException) { }
        catch (Exception error) { if (!closed && revision == version) { Error = PreviewException.From(error); Loading = false; Changed?.Invoke(); } }
    }
    private static TextPreviewResult Result(TextPreviewDocument document)
    {
        var chunk = document.Current;
        string notice = document.IsLarge ? "文件较大，快速预览采用分段读取，原文件内容未被修改。" : chunk.Text.Length == 0 ? "文件为空。" : "";
        return new(document.Identity.Id, chunk.Text, false, true, document.IsLarge, document.SegmentIndex > 0,
            !chunk.EndOfFile, document.SegmentIndex, chunk.FirstLineNumber, document.Encoding.DisplayName, true, notice);
    }
    private void PresentText(TextPreviewViewModel view)
    {
        PresentedTextViewModel = view; PresentedText = view.Result; Presented = null; Page = 0; Count = 1; CountFinal = true; Loading = false; Error = null; Changed?.Invoke();
    }
    private async Task PrefetchAsync(DocumentIdentity document, int page, int count, int width, double zoom, CancellationToken token)
    {
        try {
            PreviewRenderScheduler.Priority.Value = 1;
            await Task.Delay(80, token);
            foreach (int neighbor in new[] { page + 1, page - 1 }) if (neighbor >= 0 && (neighbor < count || !CountFinal)) await renderer.RenderViewportAsync(document, neighbor, width, PixelDpi, version, token, zoom);
            ScheduleWarmup(token);
            var stamp = CurrentRequest;
            int? completeCount = !CountFinal ? await renderer.CompletePaginationAsync(document, token) : null;
            if (completeCount is int total && identity?.Id == document.Id && Accepts(stamp)) { Count = total; CountFinal = true; Changed?.Invoke(); }
        } catch (OperationCanceledException) { } catch (Exception) { /* Speculative failures never replace the visible page. */ }
        finally { PreviewRenderScheduler.Priority.Value = 0; }
    }
    private void ScheduleWarmup(CancellationToken token)
    {
        if (prewarmAdjacent && !customRenderer && !closed) WarmupPending = Track(PrewarmAdjacentAsync(Index, version, token));
    }
    private async Task PrewarmAdjacentAsync(int sourceIndex, long revision, CancellationToken token)
    {
        try {
            await Task.Delay(180, token);
            foreach (int neighbor in new[] { sourceIndex - 1, sourceIndex + 1 }) {
                token.ThrowIfCancellationRequested();
                if (revision != version || neighbor < 0 || neighbor >= items.Count) return;
                var item = items[neighbor]; var format = PreviewFormatRegistry.FormatOf(item);
                if (!PreviewFormatRegistry.Supports(item) || item.Kind == ClipKind.Text) continue;
                string? path = PreviewFormatRegistry.PathOf(item); if (path is null) continue;
                // Enter the lowest-priority queue before any speculative I/O or rendering.
                await cache.Scheduler.Run(() => true, token, 2);
                token.ThrowIfCancellationRequested(); if (revision != version) return;
                try {
                    var id = await Task.Run(() => DocumentIdentity.Read(path), token);
                    PreviewRenderScheduler.Priority.Value = 2;
                    if (format == PreviewFormat.Text) await textFiles.PrewarmAsync(id, token);
                    else if (format == PreviewFormat.Image) await textImages.RenderImageAsync(id, PixelWidth, token);
                    else await renderer.RenderViewportAsync(id, 0, PixelWidth, PixelDpi, revision, token);
                    if (revision == version) WarmupCompleted++;
                } catch (OperationCanceledException) { throw; }
                catch (Exception) { /* A broken neighbor must not disturb the visible document. */ }
                finally { PreviewRenderScheduler.Priority.Value = 0; }
            }
        } catch (OperationCanceledException) { }
    }
    internal void Cancel()
    {
        if (closed) return; closed = true; version++; request?.Cancel(); lifetime.Cancel(); StopWatching(); cache.SetActiveDocument(null); Changed = null;
    }
    private void StopWatching() { watcher?.Dispose(); watcher = null; }
    private void Watch(DocumentIdentity document) {
        StopWatching();
        try {
            watcher = new FileSystemWatcher(System.IO.Path.GetDirectoryName(document.Path)!, System.IO.Path.GetFileName(document.Path)) { NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName };
            void ChangedFile(object sender, FileSystemEventArgs e) {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher is null || dispatcher.HasShutdownStarted) return;
                dispatcher.BeginInvoke(new Action(() => {
                    if (closed || identity?.Id != document.Id) return;
                    version++; request?.Cancel(); Loading = false; Error = new PreviewException("Changed", "文件已发生修改、移动或删除，请重试以重新加载。"); StopWatching(); Changed?.Invoke();
                }));
            }
            watcher.Changed += ChangedFile; watcher.Deleted += ChangedFile; watcher.Renamed += ChangedFile; watcher.EnableRaisingEvents = true;
        } catch (IOException) { StopWatching(); } catch (UnauthorizedAccessException) { StopWatching(); }
    }
    public async ValueTask DisposeAsync()
    {
        Cancel(); await Task.WhenAll(work.Keys); await Pending; await prefetch; await WarmupPending; await renderer.DisposeAsync(); request?.Dispose(); lifetime.Dispose(); Presented = null; PresentedText = null; PresentedTextViewModel = null; rememberedText.Clear();
    }
}
