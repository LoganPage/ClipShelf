using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace ClipShelf;
internal static class Ox
{
    internal static XElement? E(this XContainer? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    internal static IEnumerable<XElement> D(this XContainer? e, string name) => e?.Descendants().Where(x => x.Name.LocalName == name) ?? Enumerable.Empty<XElement>();
    internal static string? A(this XElement? e, string name) => e?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
    internal static double N(this XElement? e, string name, double fallback = 0) => double.TryParse(e.A(name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n) ? Math.Clamp(n, -1e10, 1e10) : fallback;
    internal static string? Val(this XElement? e) => e.A("val");
    internal static bool On(this XElement? e) => e is not null && e.Val() is not ("false" or "0" or "off" or "none");
}
internal sealed class OoxmlTheme
{
    internal readonly Dictionary<string, string> Colors = new() { ["dk1"] = "#000000", ["lt1"] = "#FFFFFF", ["dk2"] = "#44546A", ["lt2"] = "#E7E6E6", ["accent1"] = "#4472C4", ["accent2"] = "#ED7D31", ["accent3"] = "#A5A5A5", ["accent4"] = "#FFC000", ["accent5"] = "#5B9BD5", ["accent6"] = "#70AD47", ["hlink"] = "#0563C1", ["folHlink"] = "#954F72" };
    internal readonly Dictionary<string, string> Map = new() { ["bg1"] = "lt1", ["tx1"] = "dk1", ["bg2"] = "lt2", ["tx2"] = "dk2" };
    internal string Major = "Segoe UI", Minor = "Segoe UI";
    internal OoxmlTheme(XDocument? xml = null, XElement? colorMap = null) {
        var scheme = xml.D("clrScheme").FirstOrDefault();
        if (scheme is not null) foreach (var entry in scheme.Elements()) { var color = entry.Elements().FirstOrDefault(); Colors[entry.Name.LocalName] = Hex(color.A("lastClr") ?? color.A("val"), "#202020"); }
        Major = xml.D("majorFont").FirstOrDefault().E("latin").A("typeface") ?? Major; Minor = xml.D("minorFont").FirstOrDefault().E("latin").A("typeface") ?? Minor;
        if (colorMap is not null) foreach (var attr in colorMap.Attributes()) Map[attr.Name.LocalName] = attr.Value;
    }
    internal string Font(string? value) => value is null ? Minor : value.StartsWith("+mj") ? Major : value.StartsWith("+mn") ? Minor : value;
    internal static string Hex(string? value, string fallback = "#202020") => value?.Length == 6 && value.All(Uri.IsHexDigit) ? "#" + value : fallback;
    internal string Resolve(XElement? element, string fallback = "#202020") {
        if (element is null) return fallback;
        var color = element.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr" ? element : element.Elements().FirstOrDefault(e => e.Name.LocalName is "srgbClr" or "schemeClr" or "sysClr");
        if (color is null) return fallback;
        string key = color.Val() ?? "tx1"; if (Map.TryGetValue(key, out var mapped)) key = mapped;
        string raw = color.Name.LocalName == "schemeClr" ? Colors.GetValueOrDefault(key, fallback) : Hex(color.A("lastClr") ?? color.Val(), fallback);
        if (raw.Length != 7) return fallback;
        double mod = color.E("lumMod").N("val", 100000) / 100000, off = color.E("lumOff").N("val") / 100000;
        double tint = color.E("tint").N("val") / 100000, shade = color.E("shade").N("val", 100000) / 100000;
        byte Channel(int start) { double c = Convert.ToInt32(raw.Substring(start, 2), 16) / 255d; return (byte)Math.Clamp(((c * mod + off) * (1 - tint) + tint) * shade * 255, 0, 255); }
        byte alpha = (byte)Math.Clamp(color.E("alpha").N("val", 100000) * 255 / 100000, 0, 255);
        return $"#{alpha:X2}{Channel(1):X2}{Channel(3):X2}{Channel(5):X2}";
    }
}
