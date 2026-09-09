using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace ClipShelf;

internal sealed record FilePreviewResult(string Kind, string Details, string? Text = null,
    BitmapSource? Image = null, int PageCount = 0, bool HasMore = false, bool Exists = true,
    PreviewTable? Table = null, string? Section = null, string? MediaPath = null);

internal static class FilePreviewLoader
{
    internal const int TextChunk = 128 * 1024, TextLimit = 1024 * 1024;
    private static readonly System.Collections.Generic.HashSet<string> TextTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".log", ".md", ".json", ".xml", ".csv", ".yaml", ".yml", ".ini", ".toml", ".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".html", ".css", ".sql", ".c", ".cpp", ".h", ".swift", ".rs", ".go", ".sh", ".ps1", ".bat" };
    internal static bool IsImage(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" or ".webp" or ".heic" or ".heif";
    internal static bool CanOpen(string path) => IsImage(path) || CommonFilePreview.IsMedia(path) || Path.GetExtension(path).ToLowerInvariant() is
        ".pdf" or ".txt" or ".log" or ".md" or ".csv" or ".tsv" or ".doc" or ".docx" or ".docm" or ".xls" or ".xlsx" or ".xlsm" or ".ppt" or ".pptx" or ".pptm" or ".odt" or ".zip" or ".rar" or ".7z";
    internal static bool IsLocal(string path) => Path.IsPathFullyQualified(path) && !path.StartsWith(@"\\", StringComparison.Ordinal);
    internal static async Task<FilePreviewResult> LoadAsync(string path, int page, int textLength, CancellationToken token)
    {
        if (!IsLocal(path)) return new("信息", "仅自动预览本地文件", path, Exists: false);
        return await Task.Run(async () => {
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(path)) return CommonFilePreview.Folder(path, token);
            if (!File.Exists(path)) return new FilePreviewResult("不可用", "原文件已移动、删除或无法访问", path, Exists: false);
            var info = new FileInfo(path);
            string details = $"{info.Extension.TrimStart('.').ToUpperInvariant()} · {info.Length:N0} 字节 · 修改于 {info.LastWriteTime:g}";
            if (IsImage(path)) return new FilePreviewResult("图片", details, Image: LoadImage(path, token));
            if (CommonFilePreview.IsMedia(path)) return new FilePreviewResult("音视频", details + " · 点击播放才会载入媒体；编码支持取决于系统", MediaPath: path);
            if (CommonFilePreview.IsPackage(info.Extension.ToLowerInvariant())) return CommonFilePreview.Package(path, details, page, token);
            if (TextTypes.Contains(info.Extension) || info.Extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
            {
                int limit = Math.Clamp(textLength, TextChunk, TextLimit);
                string text;
                try { text = await ReadText(path, new UTF8Encoding(false, true), limit + 1, token); }
                catch (DecoderFallbackException) {
                    Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                    text = await ReadText(path, Encoding.GetEncoding(54936), limit + 1, token);
                }
                if (text.Contains('\0')) return new FilePreviewResult("信息", details, "内容包含二进制数据，未作为文本展示。");
                bool more = text.Length > limit;
                if (info.Extension.Equals(".csv", StringComparison.OrdinalIgnoreCase) || info.Extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase))
                    return new FilePreviewResult("表格", details + " · 前 500 行/50 列，最多读取 128K 字符", Table: CommonFilePreview.Delimited(text[..Math.Min(text.Length, TextChunk)], info.Extension.Equals(".tsv", StringComparison.OrdinalIgnoreCase) ? '\t' : ','));
                return new FilePreviewResult("文本", details + (more ? " · 已显示部分内容" : ""), more ? text[..limit] : text, HasMore: more);
            }
            if (info.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            {
                if (info.Length > 100 * 1024 * 1024) return new FilePreviewResult("信息", details, "PDF 超过 100 MB，请用默认应用打开。");
                using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var random = input.AsRandomAccessStream();
                var document = await PdfDocument.LoadFromStreamAsync(random).AsTask(token);
                if (document.PageCount == 0) return new FilePreviewResult("信息", details, "PDF 没有可显示的页面。");
                using var pdfPage = document.GetPage((uint)Math.Clamp(page, 0, (int)document.PageCount - 1));
                using var rendered = new InMemoryRandomAccessStream();
                var options = new PdfPageRenderOptions();
                // Windows takes DIPs here, so the encoded bitmap grows with system DPI.
                if (pdfPage.Size.Width >= pdfPage.Size.Height) options.DestinationWidth = 900; else options.DestinationHeight = 900;
                await pdfPage.RenderToStreamAsync(rendered, options).AsTask(token);
                rendered.Seek(0);
                using var stream = rendered.AsStreamForRead();
                var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
                if (pdfPage.Size.Width >= pdfPage.Size.Height) bitmap.DecodePixelWidth = 1800; else bitmap.DecodePixelHeight = 1800;
                bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze();
                return new FilePreviewResult("PDF", details, Image: bitmap, PageCount: (int)document.PageCount);
            }
            return new FilePreviewResult("信息", details, (info.Extension.ToLowerInvariant() is ".doc" or ".xls" or ".ppt" ? "旧版 Office 二进制格式暂不支持内容预览，可另存为 DOCX/XLSX/PPTX 后查看。" : "此格式暂不支持内容预览。") + "\n\n" + path);
        }, token);
    }
    private static async Task<string> ReadText(string path, Encoding encoding, int count, CancellationToken token)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, true);
        using var reader = new StreamReader(stream, encoding, true);
        var buffer = new char[count]; int total = 0;
        while (total < count) { int read = await reader.ReadAsync(buffer.AsMemory(total, count - total), token); if (read == 0) break; total += read; }
        return new string(buffer, 0, total);
    }
    internal static BitmapSource LoadImage(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var frame = BitmapFrame.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None);
        int width = frame.PixelWidth, height = frame.PixelHeight;
        stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width >= height) image.DecodePixelWidth = Math.Min(width, 1800); else image.DecodePixelHeight = Math.Min(height, 1800);
        image.StreamSource = stream; image.EndInit(); image.Freeze(); token.ThrowIfCancellationRequested(); return image;
    }
}
