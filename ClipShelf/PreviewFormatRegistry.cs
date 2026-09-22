using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace ClipShelf;

internal enum PreviewFormat { Unsupported, Pdf, Word, PowerPoint, Text, Image }
internal static class PreviewFormatRegistry
{
    internal static PreviewFormat Get(string? path) => Path.GetExtension(path ?? "").ToLowerInvariant() switch {
        ".pdf" => PreviewFormat.Pdf, ".docx" => PreviewFormat.Word,
        ".pptx" => PreviewFormat.PowerPoint,
        ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".ini" or ".yaml" or ".yml" or
        ".cs" or ".cpp" or ".h" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".sql" or
        ".ps1" or ".bat" or ".cmd" => PreviewFormat.Text,
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".ico" => PreviewFormat.Image,
        _ => PreviewFormat.Unsupported };
    internal static string? PathOf(ClipItem item) => item.Kind == ClipKind.Image ? item.ImagePath ?? item.SourcePath :
        item.Kind == ClipKind.File && !item.IsDirectory && item.FilePaths.Count == 1 ? item.FilePaths[0] : null;
    internal static PreviewFormat FormatOf(ClipItem item) => item.Kind switch {
        ClipKind.Text => PreviewFormat.Text, ClipKind.Image => PreviewFormat.Image, _ => Get(PathOf(item)) };
    internal static bool Supports(ClipItem item) => FormatOf(item) != PreviewFormat.Unsupported;
}
internal sealed record DocumentIdentity(string Path, long Size, long Modified, string Id)
{
    internal static DocumentIdentity Read(string path)
    {
        using var timing = PreviewMetrics.Measure("file-open");
        string full = System.IO.Path.GetFullPath(path);
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) throw new PreviewException("LocalOnly", "仅支持本地文档，不自动读取网络路径。");
        if (((int)File.GetAttributes(full) & (0x1000 | 0x40000 | 0x400000)) != 0) throw new PreviewException("LocalOnly", "文件尚未完整保存在本机，请先手动下载后再预览。");
        // Opening with shared reads distinguishes permissions, missing files and exclusive locks.
        using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
        var info = new FileInfo(full);
        byte[] sample = new byte[8192];
        int first = stream.Read(sample, 0, 4096);
        stream.Position = Math.Max(first, stream.Length - 4096);
        int last = stream.Read(sample, first, 4096);
        string fingerprint = Convert.ToHexString(SHA256.HashData(sample.AsSpan(0, first + last)));
        string key = $"{PreviewProviderRegistry.Version}|{full.ToUpperInvariant()}|{stream.Length}|{info.LastWriteTimeUtc.Ticks}|{fingerprint}";
        return new(full, stream.Length, info.LastWriteTimeUtc.Ticks, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))));
    }
}
internal sealed class PreviewException : Exception
{
    internal string Code { get; }
    internal PreviewException(string code, string message) : base(message) { Code = code; }
    internal static PreviewException From(Exception e) => e as PreviewException ?? e switch {
        UnauthorizedAccessException => new("AccessDenied", "权限不足，无法读取此文档或写入预览缓存。"),
        FileNotFoundException or DirectoryNotFoundException => new("MissingFile", "文件已移动或删除。"),
        System.Xml.XmlException or InvalidDataException => new("InvalidPackage", "文档包或 XML 结构损坏，无法安全预览。"),
        IOException => new("FileBusy", "文件被占用或暂时无法读取，请关闭占用程序后重试。"),
        _ => new("InvalidDocument", "文档损坏、受密码保护，或系统无法解析此文档。") };
}
