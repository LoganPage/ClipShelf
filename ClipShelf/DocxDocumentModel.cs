using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Xml.Linq;

namespace ClipShelf;

internal abstract record DocBlock;
internal record DocParagraph(SceneRun[] Runs, string Align, double Left, double Right, double First, double Before, double After, double LineHeight, bool KeepNext = false) : DocBlock;
internal record DocImage(byte[] Bytes, double Width, double Height) : DocBlock;
internal record DocPageBreak : DocBlock;
internal record DocTable(DocParagraph[][] Rows, int Columns) : DocBlock;
internal record DocPlaceholder(string Kind, double Height = 64) : DocBlock;
internal sealed class DocxDocumentModel
{
    private readonly SafeOoxmlPackage package;
    private readonly Dictionary<string, XElement> styles;
    private readonly XDocument numbering;
    private readonly XElement? defaults;
    private readonly Dictionary<string, (string Path, string Type)> relations;
    private readonly Dictionary<string, int> counters;
    internal XElement[] Body { get; }
    internal double Width = 794, Height = 1123, Left = 96, Right = 96, Top = 96, Bottom = 96;
    internal SceneRun[] Header = Array.Empty<SceneRun>(), Footer = Array.Empty<SceneRun>();
    internal readonly HashSet<string> Warnings = new();
    internal OoxmlTheme Theme { get; }
    internal DocxDocumentModel(SafeOoxmlPackage package, PreviewCacheService cache, string id, Dictionary<string, int> counters, CancellationToken token)
    {
        this.package = package; this.counters = counters; using var timing = PreviewMetrics.Measure("docx-styles");
        XDocument Xml(string part, bool optional = false) { string key = id + ":xml:" + part; if (cache.Model<XDocument>(key) is { } hit) return hit; var result = package.Xml(part, token, optional); cache.Model(key, result, package.Length(part) * 4 + 256); return result; }
        var document = Xml("word/document.xml");
        var body = document.Root.E("body") ?? throw new PreviewException("InvalidPackage", "Word 文档缺少正文结构。");
        Body = body.Elements().Where(e => e.Name.LocalName != "sectPr").ToArray(); if (Body.Length > 20000) throw SafeOoxmlPackage.Limit();
        var styleXml = Xml("word/styles.xml", true); defaults = styleXml.D("docDefaults").FirstOrDefault();
        styles = styleXml.D("style").Where(e => e.A("styleId") is not null).GroupBy(e => e.A("styleId")!).ToDictionary(g => g.Key, g => g.Last());
        numbering = Xml("word/numbering.xml", true); _ = Xml("word/settings.xml", true);
        relations = package.Relations("word/document.xml", token);
        string? themePath = relations.Values.FirstOrDefault(r => r.Type.EndsWith("/theme")).Path;
        Theme = new(themePath is null ? null : package.Xml(themePath, token));
        var section = body.E("sectPr") ?? body.D("sectPr").LastOrDefault();
        var size = section.E("pgSz"); var margin = section.E("pgMar");
        Width = Math.Clamp(size.N("w", 11910) / 15, 200, 3000); Height = Math.Clamp(size.N("h", 16845) / 15, 200, 4000);
        Left = Math.Clamp(margin.N("left", 1440) / 15, 0, Width / 3); Right = Math.Clamp(margin.N("right", 1440) / 15, 0, Width / 3);
        Top = Math.Clamp(margin.N("top", 1440) / 15, 0, Height / 3); Bottom = Math.Clamp(margin.N("bottom", 1440) / 15, 0, Height / 3);
        SceneRun[] Edge(string kind) {
            string? id = section.E(kind + "Reference").A("id");
            if (id is null || !relations.TryGetValue(id, out var related)) return Array.Empty<SceneRun>();
            try { var xml = package.Xml(related.Path, token); return xml.D("p").SelectMany(p => Runs(p, new XElement("pPr")).Append(new SceneRun("\n", Size: 12))).ToArray(); }
            catch (OperationCanceledException) { throw; } catch { Warnings.Add("页眉页脚关系不可用"); return Array.Empty<SceneRun>(); }
        }
        Header = Edge("header"); Footer = Edge("footer");
        if (body.D("sectPr").Count() > 1) Warnings.Add("多节页面设置按末节近似");
    }
    private IEnumerable<XElement> Chain(string? id, string property) {
        var chain = new List<XElement>(); var seen = new HashSet<string>();
        while (id is not null && styles.TryGetValue(id, out var style)) { if (!seen.Add(id) || seen.Count > 16) { Warnings.Add("样式继承循环"); break; } if (style.E(property) is { } pr) chain.Add(pr); id = style.E("basedOn").Val(); }
        chain.Reverse(); return chain;
    }
    private static XElement Merge(string name, IEnumerable<XElement?> elements) {
        var result = new XElement(name);
        foreach (var element in elements) if (element is not null) foreach (var child in element.Elements()) {
            var old = result.Elements().FirstOrDefault(c => c.Name.LocalName == child.Name.LocalName);
            if (old is null) result.Add(new XElement(child));
            else { foreach (var attr in child.Attributes()) old.SetAttributeValue(attr.Name, attr.Value); foreach (var nested in child.Elements()) { old.Elements().Where(e => e.Name.LocalName == nested.Name.LocalName).Remove(); old.Add(new XElement(nested)); } }
        }
        return result;
    }
    internal XElement ParagraphProperties(XElement paragraph) {
        string? style = paragraph.E("pPr").E("pStyle").Val() ?? styles.Values.FirstOrDefault(e => e.A("type") == "paragraph" && e.A("default") == "1")?.A("styleId");
        return Merge("pPr", new[] { defaults.E("pPrDefault").E("pPr") }.Concat(Chain(style, "pPr")).Append(paragraph.E("pPr")));
    }
    private SceneRun[] Runs(XElement paragraph, XElement ppr) {
        string? paragraphStyle = paragraph.E("pPr").E("pStyle").Val();
        var result = new List<SceneRun>();
        foreach (var run in paragraph.D("r")) {
            if (run.Ancestors().Any(e => e.Name.LocalName == "del")) { Warnings.Add("修订删除内容未显示"); continue; }
            var pr = Merge("rPr", new[] { defaults.E("rPrDefault").E("rPr") }.Concat(Chain(paragraphStyle, "rPr")).Concat(Chain(run.E("rPr").E("rStyle").Val(), "rPr")).Append(run.E("rPr")));
            string text = string.Concat(run.Elements().Select(e => e.Name.LocalName switch { "t" => e.Value, "tab" => "    ", "br" when e.A("type") != "page" => "\n", "cr" => "\n", _ => "" }));
            string font = pr.E("rFonts").A("eastAsia") ?? pr.E("rFonts").A("ascii") ?? Theme.Minor;
            string? themeColor = pr.E("color").A("themeColor");
            string color = themeColor is not null ? Theme.Colors.GetValueOrDefault(themeColor, "#202020") : OoxmlTheme.Hex(pr.E("color").Val());
            result.Add(new(text, font, Math.Clamp(pr.E("sz").N("val", 22) * 2 / 3, 4, 200), pr.E("b").On(), pr.E("i").On(), pr.E("u").On(), color));
        }
        if (paragraph.D("fldChar").Any() || paragraph.D("instrText").Any()) Warnings.Add("复杂域显示保存时文字");
        return result.ToArray();
    }
    private DocParagraph Paragraph(XElement element) {
        var p = ParagraphProperties(element); var runs = Runs(element, p).ToList(); var num = p.E("numPr");
        if (num is not null) {
            string numId = num.E("numId").Val() ?? "0", level = num.E("ilvl").Val() ?? "0";
            string? abstractId = numbering.D("num").FirstOrDefault(n => n.A("numId") == numId).E("abstractNumId").Val();
            var lvl = numbering.D("abstractNum").FirstOrDefault(n => n.A("abstractNumId") == abstractId)?.Elements().FirstOrDefault(e => e.Name.LocalName == "lvl" && e.A("ilvl") == level);
            string key = numId + ":" + level; int value = counters.GetValueOrDefault(key, (int)lvl.E("start").N("val", 1) - 1) + 1; counters[key] = value;
            string format = lvl.E("numFmt").Val() ?? "bullet";
            string prefix = format == "bullet" ? "•" : System.Text.RegularExpressions.Regex.Replace(lvl.E("lvlText").Val() ?? "%1.", "%([1-9])", m => counters.GetValueOrDefault(numId + ":" + (int.Parse(m.Groups[1].Value) - 1), 1).ToString());
            if (format is not ("bullet" or "decimal")) Warnings.Add("特殊编号格式按数字近似");
            runs.Insert(0, new(prefix + "  ", Size: runs.FirstOrDefault()?.Size ?? 16));
        }
        var indent = p.E("ind"); var spacing = p.E("spacing"); double lh = spacing.N("line");
        lh = lh == 0 ? 0 : spacing.A("lineRule") is "exact" or "atLeast" ? lh / 15 : (runs.Select(r => r.Size).DefaultIfEmpty(16).Max() * 1.2 * lh / 240);
        return new(runs.ToArray(), p.E("jc").Val() ?? "left", indent.N("left") / 15, indent.N("right") / 15, (indent.N("firstLine") - indent.N("hanging")) / 15,
            spacing.N("before") / 15, spacing.N("after", 120) / 15, lh, p.E("keepNext").On());
    }
    internal DocBlock[] Parse(int index, CancellationToken token) {
        using var timing = PreviewMetrics.Measure("docx-block-parse"); token.ThrowIfCancellationRequested(); var element = Body[index]; var blocks = new List<DocBlock>();
        if (element.Name.LocalName == "tbl") {
            var rows = element.Elements().Where(e => e.Name.LocalName == "tr").ToArray(); if (rows.Length > 2000) throw SafeOoxmlPackage.Limit();
            var parsed = rows.Select(r => r.Elements().Where(e => e.Name.LocalName == "tc").Select(c => {
                var ps = c.Elements().Where(e => e.Name.LocalName == "p").Select(Paragraph).ToArray();
                return new DocParagraph(ps.SelectMany(p => p.Runs.Append(new SceneRun("\n"))).ToArray(), "left", 0, 0, 0, 0, 0, 0);
            }).ToArray()).ToArray(); int cols = parsed.Select(r => r.Length).DefaultIfEmpty(1).Max(); if (cols > 50) throw SafeOoxmlPackage.Limit();
            if (element.D("gridSpan").Any() || element.D("vMerge").Any()) Warnings.Add("合并表格单元格近似"); blocks.Add(new DocTable(parsed, cols));
        } else if (element.Name.LocalName == "p") {
            if (ParagraphProperties(element).E("pageBreakBefore").On()) blocks.Add(new DocPageBreak());
            // Split explicitly at page breaks before parsing text so neither side disappears.
            var pieces = new List<XElement>(); var current = new XElement(element.Name, element.E("pPr") is { } ppr ? new XElement(ppr) : null);
            foreach (var child in element.Elements().Where(e => e.Name.LocalName != "pPr")) {
                if (child.Name.LocalName == "r" && child.Elements().Any(e => e.Name.LocalName == "br" && e.A("type") == "page")) {
                    var run = new XElement(child.Name, child.E("rPr") is { } pr ? new XElement(pr) : null);
                    foreach (var content in child.Elements().Where(e => e.Name.LocalName != "rPr")) {
                        if (content.Name.LocalName == "br" && content.A("type") == "page") { current.Add(run); pieces.Add(current); pieces.Add(new XElement("pageBreak")); current = new XElement(element.Name, element.E("pPr") is { } pp ? new XElement(pp) : null); run = new XElement(child.Name, child.E("rPr") is { } rp ? new XElement(rp) : null); }
                        else run.Add(new XElement(content));
                    } current.Add(run);
                } else current.Add(new XElement(child));
            }
            pieces.Add(current);
            foreach (var piece in pieces) {
                if (piece.Name.LocalName == "pageBreak") { blocks.Add(new DocPageBreak()); continue; }
                var paragraph = Paragraph(piece); if (paragraph.Runs.Any(r => r.Text.Length > 0) || !piece.D("drawing").Any()) blocks.Add(paragraph);
                foreach (var drawing in piece.D("drawing")) {
                    var blip = drawing.D("blip").FirstOrDefault(); string? id = blip.A("embed"); var extent = drawing.D("extent").FirstOrDefault();
                    if (id is not null && relations.TryGetValue(id, out var rel) && package.Has(rel.Path)) {
                        blocks.Add(new DocImage(package.Bytes(rel.Path, token), Math.Clamp(extent.N("cx", 2857500) / 9525, 1, 3000), Math.Clamp(extent.N("cy", 1905000) / 9525, 1, 4000)));
                        if (drawing.D("anchor").Any()) Warnings.Add("浮动图片按行内块显示");
                    } else { Warnings.Add("图片关系不可用"); blocks.Add(new DocPlaceholder("图片不可用")); }
                }
                foreach (string kind in new[] { "object", "pict", "oMath", "txbxContent", "AlternateContent" }) if (piece.D(kind).Any()) { Warnings.Add(kind); blocks.Add(new DocPlaceholder(kind)); }
            }
        } else { Warnings.Add("复杂文档对象"); blocks.Add(new DocPlaceholder("复杂文档对象")); }
        return blocks.ToArray();
    }
}
