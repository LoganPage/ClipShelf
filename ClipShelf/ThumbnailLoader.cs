using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace ClipShelf;

public static class ThumbnailLoader
{
    public static readonly DependencyProperty PathProperty = DependencyProperty.RegisterAttached("Path", typeof(string), typeof(ThumbnailLoader), new PropertyMetadata(null, PathChanged));
    private static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached("Request", typeof(CancellationTokenSource), typeof(ThumbnailLoader));
    private static readonly SemaphoreSlim decodeSlots = new(2);
    private static readonly object gate = new();
    private static readonly Dictionary<string, LinkedListNode<(string Path, BitmapImage Image)>> cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Path, BitmapImage Image)> recent = new();
    public static string? GetPath(DependencyObject target) => (string?)target.GetValue(PathProperty);
    internal static void ClearCache() { lock (gate) { cache.Clear(); recent.Clear(); } }
    public static void SetPath(DependencyObject target, string? value) => target.SetValue(PathProperty, value);

    private static void PathChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not Image image) return;
        image.Loaded -= Loaded; image.Unloaded -= Unloaded;
        image.Loaded += Loaded; image.Unloaded += Unloaded;
        Cancel(image); image.Source = null;
        if (image.IsLoaded) Start(image);
    }
    private static void Loaded(object sender, RoutedEventArgs e) => Start((Image)sender);
    private static void Unloaded(object sender, RoutedEventArgs e) => Cancel((Image)sender);
    private static void Cancel(Image image)
    {
        if (image.GetValue(RequestProperty) is CancellationTokenSource request) { request.Cancel(); request.Dispose(); image.ClearValue(RequestProperty); }
    }
    private static async void Start(Image image)
    {
        Cancel(image);
        if (GetPath(image) is not string path || string.IsNullOrWhiteSpace(path)) return;
        var request = new CancellationTokenSource(); image.SetValue(RequestProperty, request);
        var cancellation = request.Token;
        try
        {
            var bitmap = await LoadAsync(path, cancellation);
            if (!cancellation.IsCancellationRequested && GetPath(image) == path) image.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        finally { if (ReferenceEquals(image.GetValue(RequestProperty), request)) { image.ClearValue(RequestProperty); request.Dispose(); } }
    }
    internal static async Task<BitmapImage?> LoadAsync(string path, CancellationToken cancellation = default)
    {
        lock (gate) if (cache.TryGetValue(path, out var node)) { recent.Remove(node); recent.AddLast(node); return node.Value.Image; }
        await decodeSlots.WaitAsync(cancellation);
        try
        {
            return await Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                lock (gate) if (cache.TryGetValue(path, out var cached)) return cached.Value.Image;
                try
                {
                    using var stream = new FileStream(System.IO.Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
                    int width = frame.PixelWidth, height = frame.PixelHeight;
                    double ratio = Math.Min(1, Math.Min(156.0 / Math.Max(1, width), 120.0 / Math.Max(1, height)));
                    stream.Position = 0;
                    var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
                    image.DecodePixelWidth = Math.Max(1, (int)(width * ratio));
                    image.DecodePixelHeight = Math.Max(1, (int)(height * ratio)); image.StreamSource = stream;
                    image.EndInit(); image.Freeze();
                    lock (gate)
                    {
                        if (cache.TryGetValue(path, out var old)) recent.Remove(old);
                        cache[path] = recent.AddLast((path, image));
                        while (cache.Count > 192 && recent.First is { } oldest) { recent.RemoveFirst(); cache.Remove(oldest.Value.Path); }
                    }
                    // Keep completed work when a recycled row has moved away; returning
                    // to it must not decode the same long screenshot again.
                    cancellation.ThrowIfCancellationRequested();
                    return image;
                }
                catch (Exception ex) when (ex is IOException or FileFormatException or UnauthorizedAccessException or NotSupportedException or ArgumentException or InvalidOperationException) { return null; }
            }, cancellation);
        }
        finally { decodeSlots.Release(); }
    }
}
