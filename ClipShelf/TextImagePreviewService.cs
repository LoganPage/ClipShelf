using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal sealed record TextPreviewResult(string RecordId, string Text, bool Truncated);

// Text and raster images share the session's cancellation/version boundary, not the Office pipeline.
internal sealed class TextImagePreviewService(PreviewCacheService cache)
{
    internal const int TextLimit = 100_000;
    internal static Task<TextPreviewResult> ReadTextAsync(ClipItem item, CancellationToken token) => Task.Run(() => {
        token.ThrowIfCancellationRequested();
        string text = item.Text ?? item.Title;
        int length = Math.Min(text.Length, TextLimit);
        if (length < text.Length && length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return new TextPreviewResult(item.Id.ToString(), text[..length], length < text.Length);
    }, token);

    internal Task<RenderedPage> RenderImageAsync(DocumentIdentity identity, int width, CancellationToken token) => Task.Run(() => {
        token.ThrowIfCancellationRequested();
        width = Math.Clamp(width, 480, 1800);
        string key = $"image-v1:{identity.Id}:{width}";
        if (cache.Get(key) is { } cached) return new RenderedPage(identity.Id, 0, 1, cached);
        try {
            if (identity.Size > 256L * 1024 * 1024) throw new PreviewException("ImageTooLarge", "图片文件超过 256 MB，无法安全生成快速预览。");
            using var stream = new FileStream(identity.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if ((long)frame.PixelWidth * frame.PixelHeight > 200_000_000) throw new PreviewException("ImageTooLarge", "图片像素尺寸过大，无法安全生成快速预览。");
            double scale = Math.Min(1, Math.Min((double)width / frame.PixelWidth, 2400d / frame.PixelHeight));
            int targetWidth = Math.Max(1, (int)(frame.PixelWidth * scale));
            token.ThrowIfCancellationRequested();
            stream.Position = 0;
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = targetWidth; image.StreamSource = stream; image.EndInit(); image.Freeze();
            token.ThrowIfCancellationRequested();
            cache.Put(key, image);
            return new RenderedPage(identity.Id, 0, 1, image);
        } catch (OperationCanceledException) { throw; }
        catch (FileNotFoundException) { throw; } catch (DirectoryNotFoundException) { throw; }
        catch (UnauthorizedAccessException) { throw; } catch (IOException) { throw; }
        catch (PreviewException) { throw; }
        catch (Exception) { throw new PreviewException("InvalidImage", "图片损坏或系统不支持此图片编码，无法生成预览。"); }
    }, token);
}
