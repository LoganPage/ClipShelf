using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;

namespace ClipShelf;

internal sealed record SpreadsheetSheet(string Name, string Part);
internal sealed record SpreadsheetRow(int Number, string[] Cells);
internal sealed record SpreadsheetPreview(string DocumentId, int SheetIndex, string SheetName, int SheetCount,
    IReadOnlyList<SpreadsheetRow> Rows, int ColumnCount, bool Truncated, bool MissingFormulaCache, bool HasUnsupportedLayout);

internal sealed class SpreadsheetPreviewService
{
    private const int MaxRows = 500, MaxColumns = 50;
    private const int XmlCharacterLimit = 16 * 1024 * 1024, XmlNodeLimit = 300000;
    internal Task<SpreadsheetPreview> ReadAsync(DocumentIdentity document, int sheetIndex, CancellationToken token) =>
        Task.Run(() => Read(document, sheetIndex, token), token);

    private static SpreadsheetPreview Read(DocumentIdentity document, int sheetIndex, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var package = new SafeOoxmlPackage(document.Path);
        if (!package.Has("xl/workbook.xml")) throw new PreviewException("InvalidPackage", "缺少 Excel 工作簿定义。");
        var workbook = package.Xml("xl/workbook.xml", token);
        var relationships = package.Relations("xl/workbook.xml", token);
        var sheets = workbook.Descendants().Where(x => x.Name.LocalName == "sheet").Select(x => {
            string name = (string?)x.Attribute("name") ?? "工作表";
            string id = (string?)x.Attributes().FirstOrDefault(a => a.Name.LocalName == "id") ?? "";
            return relationships.TryGetValue(id, out var rel) && rel.Type.EndsWith("/worksheet", StringComparison.Ordinal)
                ? new SpreadsheetSheet(name, rel.Path) : null;
        }).Where(x => x is not null).Cast<SpreadsheetSheet>().Take(1024).ToArray();
        if (sheets.Length == 0) throw new PreviewException("InvalidPackage", "工作簿中没有可读取的工作表。");
        int index = Math.Clamp(sheetIndex, 0, sheets.Length - 1);
        bool date1904 = workbook.Descendants().Any(x => x.Name.LocalName == "workbookPr" && (string?)x.Attribute("date1904") is "1" or "true");
        var shared = ReadSharedStrings(package, token);
        var formats = ReadFormats(package, token);
        var selected = sheets[index];
        if (!package.Has(selected.Part)) throw new PreviewException("InvalidPackage", "工作表内容缺失。");
        if (package.Length(selected.Part) > XmlCharacterLimit) throw SafeOoxmlPackage.Limit();
        var rows = new List<SpreadsheetRow>();
        bool truncated = false, missingCache = false, unsupportedLayout = false;
        using var part = package.OpenPart(selected.Part);
        using var xml = XmlReader.Create(part, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = XmlCharacterLimit, MaxCharactersFromEntities = 0, IgnoreWhitespace = true });
        int nodes = 0, rowNumber = 0;
        while (xml.Read()) {
            token.ThrowIfCancellationRequested();
            if (xml.Depth > 64 || ++nodes > XmlNodeLimit || xml.Value.Length > 200000) throw SafeOoxmlPackage.Limit();
            if (xml.NodeType != XmlNodeType.Element) continue;
            if (xml.LocalName is "mergeCells" or "cols" or "conditionalFormatting") unsupportedLayout = true;
            if (xml.LocalName != "row") continue;
            rowNumber = int.TryParse(xml.GetAttribute("r"), out int explicitRow) ? explicitRow : rowNumber + 1;
            if (rowNumber > MaxRows || rows.Count >= MaxRows) { truncated = true; break; }
            if (rowNumber < 1) continue;
            var row = Enumerable.Repeat("", MaxColumns).ToArray(); int sequentialColumn = 0;
            using var subtree = xml.ReadSubtree();
            while (subtree.Read()) {
                token.ThrowIfCancellationRequested();
                if (subtree.Depth > 64 || ++nodes > XmlNodeLimit || subtree.Value.Length > 200000) throw SafeOoxmlPackage.Limit();
                if (subtree.NodeType != XmlNodeType.Element || subtree.LocalName != "c") continue;
                string address = subtree.GetAttribute("r") ?? "";
                int column = Column(address); if (column < 0) column = sequentialColumn;
                sequentialColumn = column + 1;
                if (column >= MaxColumns) { truncated = true; subtree.Skip(); continue; }
                string type = subtree.GetAttribute("t") ?? "";
                int style = int.TryParse(subtree.GetAttribute("s"), out int s) ? s : 0;
                using var cell = subtree.ReadSubtree(); string? value = null; bool formula = false;
                while (cell.Read()) {
                    token.ThrowIfCancellationRequested();
                    if (cell.Depth > 64 || ++nodes > XmlNodeLimit || cell.Value.Length > 200000) throw SafeOoxmlPackage.Limit();
                    if (cell.NodeType != XmlNodeType.Element) continue;
                    if (cell.LocalName == "f") formula = true;
                    if (cell.LocalName is "v" or "t" && (cell.LocalName == "v" || type == "inlineStr")) {
                        string fragment = cell.ReadElementContentAsString();
                        if (fragment.Length > 200000) throw SafeOoxmlPackage.Limit();
                        value = (value ?? "") + fragment;
                    }
                }
                if (formula && string.IsNullOrEmpty(value)) { row[column] = "公式结果未保存"; missingCache = true; }
                else row[column] = Format(value, type, style, formats, shared, date1904);
            }
            rows.Add(new SpreadsheetRow(rowNumber, row));
        }
        return new(document.Id, index, selected.Name, sheets.Length, rows, MaxColumns, truncated, missingCache, unsupportedLayout);
    }

    private static int Column(string address)
    {
        int col = 0, i = 0; while (i < address.Length && char.IsLetter(address[i])) { col = checked(col * 26 + char.ToUpperInvariant(address[i]) - 'A' + 1); i++; }
        return i == 0 ? -1 : col - 1;
    }
    private static string[] ReadSharedStrings(SafeOoxmlPackage package, CancellationToken token)
    {
        if (!package.Has("xl/sharedStrings.xml")) return Array.Empty<string>();
        if (package.Length("xl/sharedStrings.xml") > XmlCharacterLimit) throw SafeOoxmlPackage.Limit();
        var result = new List<string>();
        using var part = package.OpenPart("xl/sharedStrings.xml");
        using var xml = XmlReader.Create(part, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = XmlCharacterLimit, MaxCharactersFromEntities = 0 });
        int nodes = 0;
        while (xml.Read()) {
            token.ThrowIfCancellationRequested();
            if (xml.Depth > 64 || ++nodes > XmlNodeLimit || xml.Value.Length > 200000) throw SafeOoxmlPackage.Limit();
            if (xml.NodeType != XmlNodeType.Element || xml.LocalName != "si") continue;
            if (result.Count >= 100000) throw SafeOoxmlPackage.Limit();
            using var sub = xml.ReadSubtree(); var text = new System.Text.StringBuilder();
            while (sub.Read()) { token.ThrowIfCancellationRequested(); if (sub.Depth > 64 || ++nodes > XmlNodeLimit || sub.Value.Length > 200000) throw SafeOoxmlPackage.Limit(); if (sub.NodeType == XmlNodeType.Element && sub.LocalName == "t") text.Append(sub.ReadElementContentAsString()); if (text.Length > 200000) throw SafeOoxmlPackage.Limit(); }
            result.Add(text.ToString());
        }
        return result.ToArray();
    }
    private static (int Id, string Pattern)[] ReadFormats(SafeOoxmlPackage package, CancellationToken token)
    {
        if (!package.Has("xl/styles.xml")) return Array.Empty<(int, string)>();
        var xml = package.Xml("xl/styles.xml", token);
        var custom = xml.Descendants().Where(x => x.Name.LocalName == "numFmt").ToDictionary(x => (int?)x.Attribute("numFmtId") ?? 0, x => (string?)x.Attribute("formatCode") ?? "");
        return xml.Descendants().FirstOrDefault(x => x.Name.LocalName == "cellXfs")?.Elements().Where(x => x.Name.LocalName == "xf")
            .Select(x => { int id = (int?)x.Attribute("numFmtId") ?? 0; return (id, custom.GetValueOrDefault(id, "")); }).ToArray() ?? Array.Empty<(int, string)>();
    }
    private static string Format(string? value, string type, int style, (int Id, string Pattern)[] formats, string[] shared, bool date1904)
    {
        if (value is null) return "";
        if (type == "s") return int.TryParse(value, out int key) && key >= 0 && key < shared.Length ? shared[key] : "[共享字符串缺失]";
        if (type == "b") return value == "1" ? "TRUE" : "FALSE";
        if (type is "inlineStr" or "str" or "e") return value;
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)) return value;
        var (id, pattern) = style >= 0 && style < formats.Length ? formats[style] : (0, "");
        bool date = id is >= 14 and <= 22 or >= 45 and <= 47 || LooksLikeDate(pattern);
        if (date) {
            try { var epoch = date1904 ? new DateTime(1904, 1, 1) : new DateTime(1899, 12, 30); var d = epoch.AddDays(number);
                if (id is 18 or 19 or 20 or 21 or 45 or 46 or 47 || (id == 0 && !pattern.Contains('y', StringComparison.OrdinalIgnoreCase) && !pattern.Contains('d', StringComparison.OrdinalIgnoreCase))) return d.ToString("HH:mm:ss", CultureInfo.CurrentCulture);
                return d.ToString(id == 22 || pattern.Contains('h', StringComparison.OrdinalIgnoreCase) ? "yyyy-MM-dd HH:mm" : "yyyy-MM-dd", CultureInfo.CurrentCulture);
            } catch (ArgumentOutOfRangeException) { return value; }
        }
        if (id is 9 or 10 || pattern.Contains('%')) return (number * 100).ToString(pattern.Contains("0.00") ? "0.00" : "0.##", CultureInfo.CurrentCulture) + "%";
        if (id is 5 or 6 or 7 or 8 or 44 || pattern.IndexOfAny(new[] { '$', '¥', '€', '£' }) >= 0) {
            string symbol = pattern.Contains('¥') ? "¥" : pattern.Contains('€') ? "€" : pattern.Contains('£') ? "£" : "$";
            return symbol + number.ToString("N2", CultureInfo.CurrentCulture);
        }
        string section = pattern.Split(';')[0];
        if (section.Length is > 0 and <= 32 && section.All(c => c == '0') && section.Length > 1 && number == Math.Truncate(number)) return number.ToString(new string('0', section.Length), CultureInfo.InvariantCulture);
        return number.ToString("G", CultureInfo.CurrentCulture);
    }
    private static bool LooksLikeDate(string pattern)
    {
        bool quoted = false; var clean = new System.Text.StringBuilder();
        foreach (char c in pattern) { if (c == '"') quoted = !quoted; else if (!quoted) clean.Append(char.ToLowerInvariant(c)); }
        string s = clean.ToString(); return s.Contains('y') || s.Contains('d') || (s.Contains('h') && s.Contains('m'));
    }
}
