using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage;
using System.Windows;
using System.Collections.Generic;

namespace ClipShelf;

internal sealed record OcrWordRegion(string Text, Rect Bounds, int LineIndex, int WordIndex);

internal sealed record ImageOcrResult(string Text, string LanguageTag, bool Downscaled, TimeSpan Duration,
    IReadOnlyList<OcrWordRegion>? Words = null)
{
    internal IReadOnlyList<OcrWordRegion> WordRegions => Words ?? Array.Empty<OcrWordRegion>();
}

internal interface IImageOcrService
{
    int InvocationCount { get; }
    Task<ImageOcrResult> RecognizeAsync(string path, CancellationToken token);
}

internal interface IOcrClipboard
{
    bool TrySetText(string text);
}

internal sealed class OcrClipboard : IOcrClipboard
{
    public bool TrySetText(string text)
    {
        try { Clipboard.SetText(text, TextDataFormat.UnicodeText); return true; }
        catch { return false; }
    }
}

internal sealed class ImageOcrService : IImageOcrService
{
    private int invocationCount;
    private readonly Func<bool> languagesAvailable;
    internal ImageOcrService(Func<bool>? languagesAvailable = null) =>
        this.languagesAvailable = languagesAvailable ?? (() => OcrEngine.AvailableRecognizerLanguages.Count > 0);
    public int InvocationCount => Volatile.Read(ref invocationCount);

    public async Task<ImageOcrResult> RecognizeAsync(string path, CancellationToken token)
    {
        Interlocked.Increment(ref invocationCount);
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            throw new PreviewException("MissingFile", "图片文件已移动或删除，无法提取文字。");
        if (!languagesAvailable())
            throw new PreviewException("OcrLanguageMissing", "Windows 尚未安装可用的 OCR 语言。请在系统语言设置中添加语言包后重试。");

        OcrEngine? engine = OcrEngine.TryCreateFromUserProfileLanguages()
            ?? OcrEngine.TryCreateFromLanguage(OcrEngine.AvailableRecognizerLanguages.First());
        if (engine is null)
            throw new PreviewException("OcrLanguageMissing", "Windows 无法创建本地文字识别器。请检查系统语言包。");

        var watch = Stopwatch.StartNew();
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            token.ThrowIfCancellationRequested();
            using var stream = await file.OpenReadAsync();
            BitmapDecoder decoder = await BitmapDecoder.CreateAsync(stream);
            token.ThrowIfCancellationRequested();
            uint width = decoder.PixelWidth, height = decoder.PixelHeight;
            if (width == 0 || height == 0) throw new PreviewException("InvalidImage", "图片尺寸无效，无法提取文字。");
            bool downscaled = width > OcrEngine.MaxImageDimension || height > OcrEngine.MaxImageDimension;
            double scale = downscaled ? Math.Min((double)OcrEngine.MaxImageDimension / width, (double)OcrEngine.MaxImageDimension / height) : 1;
            var transform = new BitmapTransform {
                ScaledWidth = Math.Max(1u, (uint)Math.Round(width * scale)),
                ScaledHeight = Math.Max(1u, (uint)Math.Round(height * scale))
            };
            using SoftwareBitmap bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8,
                BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.RespectExifOrientation,
                ColorManagementMode.DoNotColorManage);
            token.ThrowIfCancellationRequested();
            OcrResult recognized = await engine.RecognizeAsync(bitmap);
            token.ThrowIfCancellationRequested();
            var words = new List<OcrWordRegion>();
            for (int lineIndex = 0; lineIndex < recognized.Lines.Count; lineIndex++)
            {
                OcrLine line = recognized.Lines[lineIndex];
                for (int wordIndex = 0; wordIndex < line.Words.Count; wordIndex++)
                {
                    OcrWord word = line.Words[wordIndex];
                    if (string.IsNullOrWhiteSpace(word.Text)) continue;
                    Windows.Foundation.Rect bounds = word.BoundingRect;
                    double left = Math.Clamp(bounds.X / bitmap.PixelWidth, 0, 1);
                    double top = Math.Clamp(bounds.Y / bitmap.PixelHeight, 0, 1);
                    double right = Math.Clamp((bounds.X + bounds.Width) / bitmap.PixelWidth, left, 1);
                    double bottom = Math.Clamp((bounds.Y + bounds.Height) / bitmap.PixelHeight, top, 1);
                    if (right > left && bottom > top)
                        words.Add(new OcrWordRegion(word.Text, new Rect(left, top, right - left, bottom - top), lineIndex, wordIndex));
                }
            }
            watch.Stop();
            return new ImageOcrResult(recognized.Text?.Trim() ?? "", engine.RecognizerLanguage.LanguageTag, downscaled, watch.Elapsed, words);
        }
        catch (OperationCanceledException) { throw; }
        catch (PreviewException) { throw; }
        catch (UnauthorizedAccessException) { throw new PreviewException("AccessDenied", "没有权限读取这张图片，无法提取文字。"); }
        catch (FileNotFoundException) { throw new PreviewException("MissingFile", "图片文件已移动或删除，无法提取文字。"); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            throw new PreviewException("OcrFailed", "Windows 无法识别这张图片中的文字。图片可能已损坏或使用了不支持的编码。");
        }
    }
}
