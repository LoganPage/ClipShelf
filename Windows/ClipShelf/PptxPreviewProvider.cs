using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace ClipShelf;
internal sealed class PptxPreviewProvider(PreviewCacheService cache) : OoxmlPreviewProvider(cache)
{
    private sealed record Metadata(string[] Slides, double Width, double Height);
    private Metadata metadata = null!;
    public override bool CanPreview(string file) => PreviewFormatRegistry.Get(file) == PreviewFormat.PowerPoint;
    private XDocument Xml(string part, CancellationToken token, bool optional = false) {
        string key = Identity.Id + ":xml:" + part; if (Cache.Model<XDocument>(key) is { } hit) return hit;
        var xml = Package.Xml(part, token, optional); Cache.Model(key, xml, Package.Length(part) * 4 + 256); return xml;
    }
    protected override void ReadInfo(CancellationToken token) {
        string key = Identity.Id + ":ppt-meta";
        metadata = Cache.Model<Metadata>(key)!;
        if (metadata is null) {
            var xml = Xml("ppt/presentation.xml", token); var rels = Package.Relations("ppt/presentation.xml", token);
            var ids = xml.D("sldId").ToArray(); if (ids.Length is 0 or > 1000) throw SafeOoxmlPackage.Limit();
            var slides = ids.Select(e => e.Attributes().FirstOrDefault(a => a.Name.LocalName == "id" && a.Name.NamespaceName.Length > 0)?.Value)
                .Select(id => id is not null && rels.TryGetValue(id, out var rel) && rel.Type.EndsWith("/slide") ? rel.Path : throw new PreviewException("MissingRelationship", "演示文稿缺少幻灯片关系。")).ToArray();
            var size = xml.D("sldSz").FirstOrDefault(); metadata = new(slides, Math.Clamp(size.N("cx", 9144000) / 9525, 100, 5000), Math.Clamp(size.N("cy", 5143500) / 9525, 100, 5000));
            Cache.Model(key, metadata, slides.Length * 200 + 256);
        }
        Info = new(Identity.Id, "PPTX", System.IO.Path.GetFileName(Identity.Path), metadata.Slides.Length, metadata.Width, metadata.Height, Package.Has("docProps/thumbnail.jpeg"), PreviewProviderRegistry.Version);
    }
    protected override Task<PreviewScene> GetSceneAsync(int page, CancellationToken token) => Cache.Scheduler.Run(() => {
        page = Math.Clamp(page, 0, metadata.Slides.Length - 1); string key = Identity.Id + ":ppt-scene:" + page;
        if (Cache.Model<PreviewScene>(key) is { } cached) return cached;
        using var timer = PreviewMetrics.Measure("ppt-scene");
        string part = metadata.Slides[page]; var slide = Xml(part, token);
        string? Related(string owner, string type) => Package.Relations(owner, token).Values.FirstOrDefault(r => r.Type.EndsWith("/" + type)).Path;
        string? layoutPart = Related(part, "slideLayout"), masterPart = layoutPart is null ? null : Related(layoutPart, "slideMaster");
        var layout = layoutPart is null ? null : Xml(layoutPart, token); var master = masterPart is null ? null : Xml(masterPart, token);
        string? themePart = masterPart is null ? null : Related(masterPart, "theme");
        var theme = new OoxmlTheme(themePart is null ? null : Xml(themePart, token), master.D("clrMap").FirstOrDefault());
        var nodes = new List<SceneNode>(); var warnings = new HashSet<string>();
        XElement? Background(XDocument? doc) => doc.D("bgPr").FirstOrDefault();
        var bg = Background(slide) ?? Background(layout) ?? Background(master);
        string background = theme.Resolve(bg.E("solidFill"), "#FFFFFF"); var gradient = bg.E("gradFill").D("gs").ToArray();
        if (gradient.Length > 0) background = theme.Resolve(gradient[0], "#FFFFFF");
        string? gradientEnd = gradient.Length > 1 ? theme.Resolve(gradient[^1], "#FFFFFF") : null;
        XElement? Placeholder(XElement shape) => shape.D("ph").FirstOrDefault();
        XElement? Inherit(XElement shape, XDocument? document) {
            var ph = Placeholder(shape); if (ph is null) return null;
            return document.D("sp").FirstOrDefault(s => { var p = Placeholder(s); return p is not null && (p.A("idx") ?? "0") == (ph.A("idx") ?? "0"); })
                ?? document.D("sp").FirstOrDefault(s => Placeholder(s).A("type") == ph.A("type"));
        }
        void Tree(XDocument? document, string? owner, bool skipPlaceholders) {
            if (document is null || owner is null) return; var relations = Package.Relations(owner, token);
            foreach (var shape in document.D("spTree").FirstOrDefault()?.Elements() ?? Enumerable.Empty<XElement>()) {
                token.ThrowIfCancellationRequested(); if (shape.Name.LocalName is "nvGrpSpPr" or "grpSpPr" or "extLst") continue;
                if (skipPlaceholders && Placeholder(shape) is not null) continue;
                if (nodes.Count > 5000) throw SafeOoxmlPackage.Limit();
                var inherited = Inherit(shape, layout) ?? Inherit(shape, master); var masterShape = Inherit(shape, master);
                var xfrm = shape.E("spPr").E("xfrm") ?? shape.E("xfrm") ?? inherited?.E("spPr").E("xfrm") ?? masterShape?.E("spPr").E("xfrm");
                var off = xfrm.E("off"); var ext = xfrm.E("ext");
                var b = new SceneBox(Math.Clamp(off.N("x") / 9525, -10000, 10000), Math.Clamp(off.N("y") / 9525, -10000, 10000), Math.Clamp(ext.N("cx", 1905000) / 9525, 1, 10000), Math.Clamp(ext.N("cy", 952500) / 9525, 1, 10000));
                double angle = xfrm.N("rot") / 60000;
                switch (shape.Name.LocalName) {
                    case "sp": case "cxnSp":
                        var props = shape.E("spPr"); var fill = props.E("solidFill") ?? inherited?.E("spPr").E("solidFill"); var stops = props.E("gradFill").D("gs").ToArray();
                        string color = props.E("noFill") is not null ? "#00000000" : theme.Resolve(fill, "#00000000");
                        if (stops.Length > 0) color = theme.Resolve(stops[0]);
                        string kind = props.E("prstGeom").A("prst") ?? (shape.Name.LocalName == "cxnSp" ? "line" : "rect");
                        if (kind is not ("rect" or "roundRect" or "ellipse" or "triangle" or "line")) { warnings.Add("高级形状简化为矩形"); kind = "rect"; }
                        nodes.Add(new SceneShape(b, kind, color, theme.Resolve(props.E("ln").E("solidFill"), "#00000000"), props.E("ln").N("w", 9525) / 9525, angle, stops.Length > 1 ? theme.Resolve(stops[^1]) : null));
                        var body = shape.E("txBody");
                        if (body is not null) {
                            var inheritedBody = inherited?.E("txBody") ?? masterShape?.E("txBody"); double y = b.Y + body.E("bodyPr").N("tIns", 45720) / 9525;
                            double left = body.E("bodyPr").N("lIns", 91440) / 9525, right = body.E("bodyPr").N("rIns", 91440) / 9525;
                            int number = 0;
                            foreach (var paragraph in body.Elements().Where(e => e.Name.LocalName == "p")) {
                                var ppr = paragraph.E("pPr"); string level = "lvl" + ((int)ppr.N("lvl") + 1) + "pPr";
                                string styleKind = Placeholder(shape).A("type") switch { "title" or "ctrTitle" => "titleStyle", "body" or "subTitle" => "bodyStyle", _ => "otherStyle" };
                                var defaults = body.E("lstStyle").E(level) ?? inheritedBody.E("lstStyle").E(level)
                                    ?? master.D("txStyles").FirstOrDefault().E(styleKind).E(level)
                                    ?? Xml("ppt/presentation.xml", token).D("defaultTextStyle").FirstOrDefault().E(level);
                                var runs = ReadRuns(paragraph, theme, defaults.E("defRPr"));
                                if (ppr.E("buChar") is not null || defaults.E("buChar") is not null) runs.Insert(0, new SceneRun((ppr.E("buChar").A("char") ?? defaults.E("buChar").A("char") ?? "•") + " ", Size: runs.FirstOrDefault()?.Size ?? 20));
                                if (ppr.E("buAutoNum") is not null) runs.Insert(0, new SceneRun(++number + ". ", Size: runs.FirstOrDefault()?.Size ?? 20));
                                double indent = ppr.N("marL", defaults.N("marL")) / 9525; double textWidth = Math.Max(1, b.Width - left - right - indent);
                                string align = ppr.A("algn") ?? defaults.A("algn") ?? "left";
                                var formatted = PreviewSceneRenderer.Text(runs.ToArray(), textWidth, align);
                                // Auto-sized text boxes may have a saved height smaller than our fallback font metrics.
                                // Let them grow within the slide, while keeping explicitly fixed boxes clipped.
                                double bottom = body.E("bodyPr").E("spAutoFit") is not null ? metadata.Height : b.Y + b.Height;
                                double h = Math.Min(formatted.Height + 4, Math.Max(1, bottom - y));
                                nodes.Add(new SceneText(new(b.X + left + indent, y, textWidth, h), runs.ToArray(), align, RotationAngle: angle)); y += h;
                            }
                        } break;
                    case "pic":
                        var blip = shape.D("blip").FirstOrDefault(); string? rid = blip.A("embed");
                        if (rid is not null && relations.TryGetValue(rid, out var imageRel) && Package.Has(imageRel.Path)) {
                            var crop = shape.D("srcRect").FirstOrDefault();
                            nodes.Add(new SceneImage(b, Package.Bytes(imageRel.Path, token), angle, crop.N("l") / 100000, crop.N("t") / 100000, crop.N("r") / 100000, crop.N("b") / 100000));
                        } else { warnings.Add("图片关系缺失或外部图片"); nodes.Add(new ScenePlaceholder(b, "图片不可用")); } break;
                    case "graphicFrame":
                        var table = shape.D("tbl").FirstOrDefault();
                        if (table is null) { warnings.Add("图表或 SmartArt"); nodes.Add(new ScenePlaceholder(b, "图表 / SmartArt")); break; }
                        var rows = table.Elements().Where(e => e.Name.LocalName == "tr").ToArray(); var cols = table.D("gridCol").Select(e => e.N("w", 952500)).ToArray();
                        if (rows.Length > 200 || cols.Length > 50) throw SafeOoxmlPackage.Limit(); double ty = b.Y;
                        foreach (var row in rows) { double tx = b.X; double rh = Math.Max(1, row.N("h", b.Height * 9525 / Math.Max(1, rows.Length)) / 9525); int column = 0;
                            foreach (var cell in row.Elements().Where(e => e.Name.LocalName == "tc")) { double cw = column < cols.Length ? b.Width * cols[column] / Math.Max(1, cols.Sum()) : b.Width / Math.Max(1, cols.Length); column++;
                                var cb = new SceneBox(tx, ty, cw, rh); nodes.Add(new SceneShape(cb, "rect", theme.Resolve(cell.E("tcPr").E("solidFill"), "#FFFFFF"), "#ADB5C0"));
                                var runs = cell.D("p").SelectMany(p => ReadRuns(p, theme, null).Append(new SceneRun("\n"))).ToArray();
                                nodes.Add(new SceneText(new(tx + 5, ty + 3, Math.Max(1, cw - 10), Math.Max(1, rh - 6)), runs)); tx += cw;
                            } ty += rh;
                        } break;
                    default: warnings.Add("组合形状或嵌入对象"); nodes.Add(new ScenePlaceholder(b, "组合 / 嵌入对象")); break;
                }
            }
        }
        if (slide.Root.A("showMasterSp") != "0") Tree(master, masterPart, true); Tree(layout, layoutPart, true); Tree(slide, part, false);
        if (slide.D("timing").Any() || slide.D("transition").Any()) warnings.Add("动画与过渡未播放");
        var scene = new PreviewScene(metadata.Width, metadata.Height, background, nodes.ToArray(), warnings.ToArray(), gradientEnd);
        long bytes = nodes.OfType<SceneImage>().Sum(i => (long)i.Bytes.Length) + nodes.OfType<SceneText>().Sum(t => t.Runs.Sum(r => r.Text.Length * 2L)) + nodes.Count * 256L;
        Cache.Model(key, scene, bytes); return scene;
    }, token);
    private static List<SceneRun> ReadRuns(XElement paragraph, OoxmlTheme theme, XElement? inherited) {
        var result = new List<SceneRun>(); var defaults = paragraph.E("pPr").E("defRPr") ?? inherited;
        foreach (var run in paragraph.Elements()) {
            if (run.Name.LocalName == "br") { result.Add(new("\n")); continue; }
            if (run.Name.LocalName is not ("r" or "fld")) continue;
            var pr = run.E("rPr"); var text = run.E("t")?.Value ?? "";
            result.Add(new(text, theme.Font(pr.E("latin").A("typeface") ?? defaults.E("latin").A("typeface")), pr.N("sz", defaults.N("sz", 1800)) / 75,
                (pr.A("b") ?? defaults.A("b")) == "1", (pr.A("i") ?? defaults.A("i")) == "1", (pr.A("u") ?? defaults.A("u")) is not (null or "none"), theme.Resolve(pr.E("solidFill") ?? defaults.E("solidFill"))));
        }
        return result;
    }
}
