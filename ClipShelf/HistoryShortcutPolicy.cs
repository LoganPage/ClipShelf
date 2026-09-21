using System;
using System.Collections.Generic;

namespace ClipShelf;

internal static class HistoryShortcutPolicy
{
    private static readonly HashSet<string> Reserved = BuildReserved();

    internal static bool IsReserved(string shortcut) => Reserved.Contains(Normalize(shortcut));

    internal static string Normalize(string shortcut) => shortcut.Replace(" ", "", StringComparison.Ordinal)
        .ToUpperInvariant().Replace("CONTROL+", "CTRL+", StringComparison.Ordinal)
        .Replace("PRIOR", "PAGEUP", StringComparison.Ordinal).Replace("NEXT", "PAGEDOWN", StringComparison.Ordinal)
        .Replace("RETURN", "ENTER", StringComparison.Ordinal).Replace("ESCAPE", "ESC", StringComparison.Ordinal);

    private static HashSet<string> BuildReserved()
    {
        var keys = new HashSet<string>(StringComparer.Ordinal)
        {
            "CTRL+A", "CTRL+C", "CTRL+V", "CTRL+X", "CTRL+Z", "CTRL+Y", "CTRL+F",
            "CTRL+SPACE", "SHIFT+F10", "APPS", "ESC", "CTRL+ESC", "ALT+F4", "ALT+SPACE",
            "ENTER", "SPACE", "DELETE", "SHIFT+DELETE", "CTRL+INSERT", "SHIFT+INSERT"
        };
        foreach (var key in new[] { "UP", "DOWN", "LEFT", "RIGHT", "HOME", "END", "PAGEUP", "PAGEDOWN", "TAB" })
            foreach (var prefix in new[] { "", "SHIFT+", "CTRL+", "CTRL+SHIFT+" }) keys.Add(prefix + key);
        return keys;
    }
}
