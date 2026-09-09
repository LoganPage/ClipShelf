using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace ClipShelf;

internal sealed record PreviewTable(IReadOnlyList<string> Headers, IReadOnlyList<string[]> Rows);

internal static class CommonFilePreview
{
    private const int MaxRows = 500, MaxColumns = 50, MaxXml = 4 * 1024 * 1024;
    internal static bool IsMedia(string path) => Path.GetExtension(path).ToLowerInvariant() is ".mp3" or ".wav" or ".m4a" or ".aac" or ".flac" or ".wma" or ".mp4" or ".mov" or ".mkv" or ".avi" or ".wmv" or ".webm";
    internal static bool IsPackage(string ext) => ext is ".docx" or ".docm" or ".xlsx" or ".xlsm" or ".pptx" or ".pptm" or ".odt" or ".zip";
    internal static FilePreviewResult Folder(string path, CancellationToken token)
    {
        var rows = new List<string[]>();
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        foreach (var entry in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options)) {
            token.ThrowIfCancellationRequested(); if (rows.Count == MaxRows) break;
            rows.Add(new[] { entry.Name, (entry.Attributes & FileAttributes.Directory) != 0 ? "文件夹" : entry.Extension, entry.LastWriteTime.ToString("g") });
        }
        return new("文件夹", "仅列出当前层，最多 500 项；不展开子文件夹", Table: new(new[] { "名称", "类型", "修改时间" }, rows.OrderBy(r => r[0], StringComparer.CurrentCultureIgnoreCase).ToArray()));
    }
    internal static FilePreviewResult Package(string path, string details, int page, CancellationToken token)
    {
        if (new FileInfo(path).Length > 100 * 1024 * 1024) return new("信息", details, "文件超过 100 MB 的内容预览上限，请用默认应用打开。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Count > 10000) return new("信息", details, "文件内部项目过多，已停止预览。");
        token.ThrowIfCancellationRequested();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".zip") {
            var rows = new List<string[]>();
            foreach (var entry in zip.Entries.Take(MaxRows)) {
                token.ThrowIfCancellationRequested();
                rows.Add(new[] { entry.FullName, entry.Name.Length == 0 ? "文件夹" : entry.Length.ToString("N0") + " 字节", entry.CompressedLength.ToString("N0") + " 字节" });
            }
            return new("ZIP", details + $" · 共 {zip.Entries.Count} 项，仅列前 500 项，不解压", Table: new(new[] { "路径", "原始大小", "压缩大小" }, rows));
        }
        if (ext is ".docx" or ".docm" or ".odt") {
            var xml = ReadXml(zip, ext == ".odt" ? "content.xml" : "word/document.xml", token);
            string text = Paragraphs(xml, token, ext == ".odt");
            return new("Word", details + " · 正文预览，不含原排版和图片，不执行宏", text.Length == 0 ? "没有可预览的正文。" : text);
        }
        if (ext is ".pptx" or ".pptm") {
            var presentation = ReadXml(zip, "ppt/presentation.xml", token);
            var ids = presentation.Descendants().Where(e => e.Name.LocalName == "sldId").Take(1001).ToArray();
            if (ids.Length is 0 or > 1000) return new("信息", details, "没有幻灯片，或幻灯片数量超过预览上限。");
            var id = ids[Math.Clamp(page, 0, ids.Length - 1)].Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value;
            string part = RelatedPart(zip, "ppt/presentation.xml", id, token);
            var slide = ReadXml(zip, part, token);
            return new("PPT", details + " · 逐页文字预览，不含原排版、图片和动画", Paragraphs(slide, token), PageCount: ids.Length, Section: "幻灯片");
        }
        return Workbook(zip, details, page, token);
    }
    private static FilePreviewResult Workbook(ZipArchive zip, string details, int page, CancellationToken token)
    {
        var workbook = ReadXml(zip, "xl/workbook.xml", token);
        var sheets = workbook.Descendants().Where(e => e.Name.LocalName == "sheet").Take(1001).ToArray();
        if (sheets.Length is 0 or > 1000) return new("信息", details, "没有工作表，或工作表数量超过预览上限。");
        var selected = sheets[Math.Clamp(page, 0, sheets.Length - 1)];
        string? id = selected.Attributes().FirstOrDefault(a => a.Name.LocalName == "id")?.Value;
        var sheet = ReadSheetPrefix(zip, RelatedPart(zip, "xl/workbook.xml", id, token), token);
        var neededStrings = sheet.Descendants().Where(e => e.Name.LocalName == "c" && (string?)e.Attribute("t") == "s")
            .Select(e => e.Elements().FirstOrDefault(v => v.Name.LocalName == "v")?.Value)
            .Select(v => int.TryParse(v, out int n) ? n : -1).Where(n => n >= 0).ToHashSet();
        var strings = ReadNeededStrings(zip, neededStrings, token);
        var dateStyles = new HashSet<int>();
        if (zip.GetEntry("xl/styles.xml") is not null) {
            var styles = ReadXml(zip, "xl/styles.xml", token);
            var formats = styles.Descendants().Where(e => e.Name.LocalName == "numFmt").Where(e => int.TryParse((string?)e.Attribute("numFmtId"), out _))
                .GroupBy(e => (int)e.Attribute("numFmtId")!).ToDictionary(g => g.Key, g => (string?)g.First().Attribute("formatCode") ?? "");
            var xfs = styles.Descendants().FirstOrDefault(e => e.Name.LocalName == "cellXfs")?.Elements().ToArray() ?? Array.Empty<XElement>();
            for (int i = 0; i < xfs.Length; i++) {
                int.TryParse((string?)xfs[i].Attribute("numFmtId"), out int format);
                if (format is >= 14 and <= 22 or >= 45 and <= 47 || formats.TryGetValue(format, out var code) && (code.Contains('y') || code.Contains('d'))) dateStyles.Add(i);
            }
        }
        bool date1904 = workbook.Descendants().Any(e => e.Name.LocalName == "workbookPr" && (string?)e.Attribute("date1904") is "1" or "true");
        var rows = new List<string[]>(); int maxColumn = 0;
        foreach (var row in sheet.Descendants().Where(e => e.Name.LocalName == "row").Take(MaxRows)) {
            token.ThrowIfCancellationRequested(); var values = new string[MaxColumns + 1]; Array.Fill(values, "");
            values[0] = (string?)row.Attribute("r") ?? (rows.Count + 1).ToString();
            int nextColumn = 0;
            foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "c").Take(MaxColumns * 2)) {
                int column = Column((string?)cell.Attribute("r"), nextColumn); nextColumn = column + 1;
                if (column < 0 || column >= MaxColumns) continue;
                string value = cell.Elements().FirstOrDefault(e => e.Name.LocalName == "v")?.Value ?? "";
                string? type = (string?)cell.Attribute("t");
                if (type == "s") value = int.TryParse(value, out int si) && strings.TryGetValue(si, out var shared) ? shared : "[未载入的字符串]";
                else if (type == "inlineStr") value = string.Concat(cell.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                else if (type == "b") value = value == "1" ? "TRUE" : "FALSE";
                else if (dateStyles.Contains(int.TryParse((string?)cell.Attribute("s"), out int style) ? style : -1) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double days)) {
                    try { value = DateTime.FromOADate(days + (date1904 ? 1462 : days is > 0 and < 60 ? 1 : 0)).ToString("yyyy-MM-dd HH:mm:ss"); } catch (ArgumentException) { }
                }
                if (value.Length == 0 && cell.Elements().FirstOrDefault(e => e.Name.LocalName == "f") is XElement formula) value = "=" + formula.Value;
                values[column + 1] = Trim(value); maxColumn = Math.Max(maxColumn, column + 1);
            }
            rows.Add(values);
        }
        maxColumn = Math.Max(1, maxColumn);
        return new("Excel", details + " · " + (string?)selected.Attribute("name") + " · 前 500 行/50 列；公式使用已有结果，不重新计算；不含图表和原格式",
            PageCount: sheets.Length, Section: "工作表", Table: new(new[] { "行" }.Concat(Enumerable.Range(0, maxColumn).Select(ColumnName)).ToArray(), rows.Select(r => r.Take(maxColumn + 1).ToArray()).ToArray()));
    }
    private static int Column(string? address, int fallback)
    {
        if (string.IsNullOrEmpty(address)) return fallback;
        int value = 0; foreach (char c in address) { if (c is < 'A' or > 'Z') break; value = value * 26 + c - 'A' + 1; if (value > MaxColumns) return MaxColumns; }
        return value == 0 ? fallback : value - 1;
    }
    // Consume only the rows that can be displayed, not the complete worksheet DOM.
    private static XDocument ReadSheetPrefix(ZipArchive zip, string path, CancellationToken token)
    {
        var entry = zip.GetEntry(path) ?? throw new InvalidDataException("Missing worksheet");
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml });
        var root = new XElement("sheetData"); int rows = 0;
        while (reader.Read()) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "row") continue;
            using (var subtree = reader.ReadSubtree()) root.Add(XElement.Load(subtree));
            if (++rows >= MaxRows) break;
        }
        return new XDocument(root);
    }
    private static Dictionary<int, string> ReadNeededStrings(ZipArchive zip, HashSet<int> needed, CancellationToken token)
    {
        var result = new Dictionary<int, string>();
        var entry = zip.GetEntry("xl/sharedStrings.xml");
        if (entry is null || needed.Count == 0) return result;
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml });
        int index = 0;
        while (reader.Read()) {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "si") continue;
            if (needed.Contains(index)) {
                using var subtree = reader.ReadSubtree(); var value = XElement.Load(subtree);
                result[index] = Trim(string.Concat(value.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value)));
            }
            index++;
            if (result.Count == needed.Count || index >= 100000) break;
        }
        return result;
    }
    internal static string ColumnName(int column) => column < 26 ? ((char)('A' + column)).ToString() : "A" + (char)('A' + column - 26);
    private static string RelatedPart(ZipArchive zip, string part, string? id, CancellationToken token)
    {
        int slash = part.LastIndexOf('/'); string directory = part[..(slash + 1)];
        var relationships = ReadXml(zip, directory + "_rels/" + part[(slash + 1)..] + ".rels", token);
        var relation = relationships.Descendants().FirstOrDefault(e => e.Name.LocalName == "Relationship" && (string?)e.Attribute("Id") == id);
        string? target = (string?)relation?.Attribute("Target");
        if (target is null || (string?)relation?.Attribute("TargetMode") == "External" || target.Contains('\\')) throw new InvalidDataException("External or invalid relationship");
        var root = new Uri("https://package.invalid/" + part);
        var resolved = new Uri(root, target);
        if (resolved.Host != root.Host || resolved.Scheme != "https" || resolved.Query.Length != 0) throw new InvalidDataException("External relationship");
        return Uri.UnescapeDataString(resolved.AbsolutePath.TrimStart('/'));
    }
    private static XDocument ReadXml(ZipArchive zip, string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var entry = zip.GetEntry(path) ?? throw new InvalidDataException("Missing document part");
        if (entry.Length > MaxXml) throw new InvalidDataException("Document part exceeds preview limit");
        using var input = entry.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXml, MaxCharactersFromEntities = 0 });
        var xml = XDocument.Load(reader); token.ThrowIfCancellationRequested(); return xml;
    }
    private static string Paragraphs(XDocument xml, CancellationToken token, bool odt = false)
    {
        var text = new StringBuilder();
        foreach (var paragraph in xml.Descendants().Where(e => e.Name.LocalName == "p" || odt && e.Name.LocalName == "h")) {
            token.ThrowIfCancellationRequested();
            if (odt) text.Append(paragraph.Value);
            else foreach (var e in paragraph.Descendants()) { if (e.Name.LocalName == "t") text.Append(e.Value); else if (e.Name.LocalName == "tab") text.Append('\t'); else if (e.Name.LocalName == "br") text.AppendLine(); }
            text.AppendLine(); if (text.Length >= FilePreviewLoader.TextChunk) { text.Length = FilePreviewLoader.TextChunk; text.Append("\n[正文预览已达上限]"); break; }
        }
        return text.ToString();
    }
    private static string Trim(string value) => value.Length > 2048 ? value[..2048] + "…" : value;
    internal static PreviewTable Delimited(string text, char separator)
    {
        var rows = new List<string[]>(); var row = new List<string>(); var cell = new StringBuilder(); bool quoted = false;
        void Cell() { if (row.Count < MaxColumns) row.Add(Trim(cell.ToString())); cell.Clear(); }
        void Row() { Cell(); rows.Add(row.ToArray()); row.Clear(); }
        for (int i = 0; i < text.Length && rows.Count < MaxRows; i++) {
            char c = text[i];
            if (c == '"') { if (quoted && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; } else quoted = !quoted; }
            else if (c == separator && !quoted) Cell();
            else if (c is '\r' or '\n' && !quoted) { Row(); if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++; }
            else if (cell.Length < 2049) cell.Append(c);
        }
        if (rows.Count < MaxRows && (cell.Length > 0 || row.Count > 0)) Row();
        int columns = Math.Max(1, rows.Count == 0 ? 1 : rows.Max(r => r.Length));
        return new(Enumerable.Range(0, columns).Select(ColumnName).ToArray(), rows.Select(r => Enumerable.Range(0, columns).Select(i => i < r.Length ? r[i] : "").ToArray()).ToArray());
    }
}
