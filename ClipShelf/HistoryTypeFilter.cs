using System;
using System.Collections.Generic;
using System.Threading;

namespace ClipShelf;

internal static class HistoryTypeFilter
{
    internal const string All = "All";
    internal const string Text = "Text";
    internal const string File = "File";
    internal const string Image = "Image";

    internal static string Normalize(string? value) => value is Text or File or Image ? value : All;

    internal static bool Matches(ClipItem item, string? value) => Normalize(value) switch
    {
        Text => item.Kind == ClipKind.Text,
        File => item.Kind == ClipKind.File,
        Image => item.Kind == ClipKind.Image,
        _ => true
    };

    internal static List<ClipItem> Filter(IReadOnlyList<ClipItem> items, string? value, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        string normalized = Normalize(value);
        var result = new List<ClipItem>(normalized == All ? items.Count : Math.Min(items.Count, 128));
        for (int index = 0; index < items.Count; index++)
        {
            token.ThrowIfCancellationRequested();
            if (Matches(items[index], normalized)) result.Add(items[index]);
        }
        return result;
    }
}
