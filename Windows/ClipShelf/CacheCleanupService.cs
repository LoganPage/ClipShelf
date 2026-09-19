using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace ClipShelf;

internal sealed record CleanupResult(long Bytes, int Files, int Skipped);

// Deliberately non-recursive: history, source images, backups and extracted installers
// are not cleanup inputs. Never traverse junctions or symbolic links.
internal static class CacheCleanupService
{
    internal static bool GeneratedPage(string name)
    {
        var parts = name.Split('.');
        bool Hex(string value, int length) => value.Length == length && value.All(Uri.IsHexDigit);
        return parts.Length == 2 && Hex(parts[0], 64) && parts[1] is "png" or "json" or "pdf"
            || parts.Length == 3 && Hex(parts[0], 64) && Hex(parts[1], 32) && parts[2] == "pending";
    }

    internal static bool SafeDirectory(string path)
    {
        for (var directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0) return false;
        return true;
    }

    internal static CleanupResult Clean(string previewRoot, string updateRoot, CancellationToken token)
    {
        long bytes = 0; int files = 0, skipped = 0;
        void Delete(FileInfo file, string parent)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                file.Refresh();
                if (!file.Exists) return;
                if (!SafeDirectory(parent) || !string.Equals(file.DirectoryName, Path.GetFullPath(parent), StringComparison.OrdinalIgnoreCase)
                    || (file.Attributes & FileAttributes.ReparsePoint) != 0) { skipped++; return; }
                long size = file.Length; file.Delete(); bytes += size; files++;
            }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        try
        {
            if (Directory.Exists(previewRoot))
            {
                if (!SafeDirectory(previewRoot)) skipped++;
                else foreach (var file in new DirectoryInfo(previewRoot).EnumerateFiles())
                    if (GeneratedPage(file.Name)) Delete(file, previewRoot);
            }
        }
        catch (IOException) { skipped++; }
        catch (UnauthorizedAccessException) { skipped++; }
        try
        {
            if (Directory.Exists(updateRoot))
            {
                if (!SafeDirectory(updateRoot)) skipped++;
                else foreach (var stage in new DirectoryInfo(updateRoot).EnumerateDirectories("stage-*"))
                {
                    token.ThrowIfCancellationRequested();
                    if (!Guid.TryParseExact(stage.Name[6..], "N", out _) || !SafeDirectory(stage.FullName)) continue;
                    var package = new FileInfo(Path.Combine(stage.FullName, "package.zip"));
                    if (package.Exists && package.LastWriteTimeUtc < DateTime.UtcNow.AddDays(-1)) Delete(package, stage.FullName);
                }
            }
        }
        catch (IOException) { skipped++; }
        catch (UnauthorizedAccessException) { skipped++; }
        return new(bytes, files, skipped);
    }
}
