using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;

namespace ClipShelf;
internal static class NativePreviewTests
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    internal static void Package(string path, Dictionary<string, byte[]> entries) {
        using var file = File.Create(path); using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        foreach (var (name, bytes) in entries) { using var stream = zip.CreateEntry(name).Open(); stream.Write(bytes); }
    }
    private static byte[] Xml(string xml) => Encoding.UTF8.GetBytes(xml);
    private static string Rels(params (string Id, string Type, string Path)[] rows) => "<Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'>" + string.Concat(rows.Select(r => $"<Relationship Id='{r.Id}' Type='{R}/{r.Type}' Target='{r.Path}'/>")) + "</Relationships>";
    internal static byte[] ImageFixture() {
        var bytes = new byte[200 * 100 * 4]; for (int i = 0; i < bytes.Length; i += 4) { bytes[i] = 100; bytes[i + 1] = 160; bytes[i + 2] = 40; bytes[i + 3] = 255; }
        var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(BitmapSource.Create(200, 100, 96, 96, PixelFormats.Bgra32, null, bytes, 800))); using var stream = new MemoryStream(); png.Save(stream); return stream.ToArray();
    }
    internal static void Docx(string path, int paragraphs, bool rich = false) {
        string text = string.Concat(Enumerable.Range(0, paragraphs).Select(i => $"<w:p><w:pPr><w:pStyle w:val='{(i == 0 ? "Heading" : "Normal")}'/></w:pPr><w:r><w:t>第 {i + 1} 段：ClipShelf 本地文档预览。中文 English 混排，自动换行；不启动 Office。 This is a readable paragraph with multiple lines and fallback fonts.</w:t></w:r></w:p>"));
        if (rich) text += "<w:p><w:r><w:br w:type='page'/><w:t>显式分页后的内容</w:t></w:r></w:p><w:tbl><w:tr><w:tc><w:p><w:r><w:t>表格 A</w:t></w:r></w:p></w:tc><w:tc><w:p><w:r><w:t>表格 B</w:t></w:r></w:p></w:tc></w:tr></w:tbl><w:p><w:r><w:drawing><wp:inline xmlns:wp='http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing'><wp:extent cx='1905000' cy='952500'/><a:graphic xmlns:a='" + A + "'><a:blip r:embed='image'/></a:graphic></wp:inline></w:drawing></w:r></w:p><w:p><w:r><w:object/></w:r></w:p>";
        var entries = new Dictionary<string, byte[]> {
            ["word/document.xml"] = Xml($"<w:document xmlns:w='{W}' xmlns:r='{R}'><w:body>{text}<w:sectPr><w:pgSz w:w='11910' w:h='16845'/><w:pgMar w:top='1440' w:bottom='1440' w:left='1440' w:right='1440'/><w:headerReference r:id='header'/></w:sectPr></w:body></w:document>"),
            ["word/styles.xml"] = Xml($"<w:styles xmlns:w='{W}'><w:style w:type='paragraph' w:styleId='Normal'><w:pPr><w:spacing w:after='120'/></w:pPr><w:rPr><w:sz w:val='22'/></w:rPr></w:style><w:style w:type='paragraph' w:styleId='Heading'><w:basedOn w:val='Normal'/><w:pPr><w:keepNext/><w:ind w:left='300'/></w:pPr><w:rPr><w:b/><w:sz w:val='32'/><w:color w:val='2255AA'/></w:rPr></w:style></w:styles>"),
            ["word/_rels/document.xml.rels"] = Xml(Rels(("image", "image", "media/image.png"), ("header", "header", "header1.xml"))),
            ["word/header1.xml"] = Xml($"<w:hdr xmlns:w='{W}'><w:p><w:r><w:t>ClipShelf · 合成测试文档</w:t></w:r></w:p></w:hdr>"), ["word/media/image.png"] = ImageFixture()
        }; Package(path, entries);
    }
    internal static void Pptx(string path, int count) {
        var entries = new Dictionary<string, byte[]>();
        string ids = string.Concat(Enumerable.Range(1, count).Select(i => $"<p:sldId id='{255 + i}' r:id='r{i}'/>"));
        entries["ppt/presentation.xml"] = Xml($"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldIdLst>{ids}</p:sldIdLst><p:sldSz cx='9144000' cy='5143500'/></p:presentation>");
        entries["ppt/_rels/presentation.xml.rels"] = Xml(Rels(Enumerable.Range(1, count).Select(i => ($"r{i}", "slide", $"slides/slide{count - i + 1}.xml")).ToArray()));
        for (int i = 1; i <= count; i++) {
            entries[$"ppt/slides/slide{i}.xml"] = Xml($"<p:sld xmlns:p='{P}' xmlns:a='{A}' xmlns:r='{R}'><p:cSld><p:bg><p:bgPr><a:solidFill><a:schemeClr val='lt1'/></a:solidFill></p:bgPr></p:bg><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type='title' idx='1'/></p:nvPr></p:nvSpPr><p:spPr/><p:txBody><a:bodyPr/><a:p><a:r><a:rPr sz='3000' b='1'><a:solidFill><a:schemeClr val='accent1'/></a:solidFill></a:rPr><a:t>第 {i} 张 · ClipShelf</a:t></a:r></a:p></p:txBody></p:sp><p:sp><p:spPr><a:xfrm rot='600000'><a:off x='600000' y='1800000'/><a:ext cx='3000000' cy='1500000'/></a:xfrm><a:prstGeom prst='roundRect'/><a:solidFill><a:schemeClr val='accent1'><a:alpha val='70000'/></a:schemeClr></a:solidFill><a:ln w='19050'><a:solidFill><a:srgbClr val='112233'/></a:solidFill></a:ln></p:spPr></p:sp><p:pic><p:blipFill><a:blip r:embed='img'/></p:blipFill><p:spPr><a:xfrm><a:off x='4500000' y='1800000'/><a:ext cx='3000000' cy='1500000'/></a:xfrm></p:spPr></p:pic><p:graphicFrame><p:xfrm><a:off x='600000' y='3600000'/><a:ext cx='3000000' cy='700000'/></p:xfrm><a:graphic><a:graphicData><a:tbl><a:tblGrid><a:gridCol w='1500000'/><a:gridCol w='1500000'/></a:tblGrid><a:tr h='700000'><a:tc><a:txBody><a:p><a:r><a:t>表格 A</a:t></a:r></a:p></a:txBody></a:tc><a:tc><a:txBody><a:p><a:r><a:t>表格 B</a:t></a:r></a:p></a:txBody></a:tc></a:tr></a:tbl></a:graphicData></a:graphic></p:graphicFrame></p:spTree></p:cSld></p:sld>");
            entries[$"ppt/slides/_rels/slide{i}.xml.rels"] = Xml(Rels(("layout", "slideLayout", "../slideLayouts/slideLayout1.xml"), ("img", "image", "../media/image.png")));
        }
        entries["ppt/slideLayouts/slideLayout1.xml"] = Xml($"<p:sldLayout xmlns:p='{P}' xmlns:a='{A}'><p:cSld><p:spTree><p:sp><p:nvSpPr><p:nvPr><p:ph type='title' idx='1'/></p:nvPr></p:nvSpPr><p:spPr><a:xfrm><a:off x='600000' y='400000'/><a:ext cx='7500000' cy='1000000'/></a:xfrm></p:spPr></p:sp></p:spTree></p:cSld></p:sldLayout>");
        entries["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = Xml(Rels(("master", "slideMaster", "../slideMasters/slideMaster1.xml")));
        entries["ppt/slideMasters/slideMaster1.xml"] = Xml($"<p:sldMaster xmlns:p='{P}' xmlns:a='{A}'><p:cSld><p:spTree/></p:cSld><p:clrMap bg1='lt1' tx1='dk1'/><p:txStyles><p:titleStyle><a:lvl1pPr><a:defRPr sz='4400'/></a:lvl1pPr></p:titleStyle><p:otherStyle><a:lvl1pPr><a:defRPr sz='1800'/></a:lvl1pPr></p:otherStyle></p:txStyles></p:sldMaster>");
        var autoSlide = XDocument.Parse(Encoding.UTF8.GetString(entries[$"ppt/slides/slide{count}.xml"]));
        autoSlide.D("spTree").First().Add(XElement.Parse($"<p:sp xmlns:p='{P}' xmlns:a='{A}'><p:spPr><a:xfrm><a:off x='600000' y='4400000'/><a:ext cx='7000000' cy='95250'/></a:xfrm></p:spPr><p:txBody><a:bodyPr><a:spAutoFit/></a:bodyPr><a:p><a:r><a:t>AutoFit body fixture</a:t></a:r></a:p></p:txBody></p:sp>"));
        entries[$"ppt/slides/slide{count}.xml"] = Xml(autoSlide.ToString());
        entries["ppt/slideMasters/_rels/slideMaster1.xml.rels"] = Xml(Rels(("theme", "theme", "../theme/theme1.xml")));
        entries["ppt/theme/theme1.xml"] = Xml($"<a:theme xmlns:a='{A}'><a:themeElements><a:clrScheme><a:accent1><a:srgbClr val='1266AA'/></a:accent1><a:lt1><a:srgbClr val='FFFFFF'/></a:lt1></a:clrScheme><a:fontScheme><a:majorFont><a:latin typeface='MissingFontFixture'/></a:majorFont><a:minorFont><a:latin typeface='Segoe UI'/></a:minorFont></a:fontScheme></a:themeElements></a:theme>");
        entries["ppt/media/image.png"] = ImageFixture(); entries["docProps/thumbnail.jpeg"] = ImageFixture(); Package(path, entries);
    }
    internal static async Task RunAsync(string root) {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>(); var timing = new Dictionary<string, double>(); string? error = null;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        using var cache = new PreviewCacheService(Path.Combine(root, "cache"));
        try {
            var scrollFixture = new SmoothScrollViewer { Width = 300, Height = 120, Content = new System.Windows.Controls.Border { Height = 1000 } };
            scrollFixture.ApplyTemplate(); scrollFixture.Measure(new Size(300, 120)); scrollFixture.Arrange(new Rect(0, 0, 300, 120)); scrollFixture.UpdateLayout();
            var fixtureBar = (System.Windows.Controls.Primitives.ScrollBar)scrollFixture.Template.FindName("PART_VerticalScrollBar", scrollFixture);
            Check(fixtureBar.Width == 13 && fixtureBar.Margin.Left == 2 && fixtureBar.Margin.Right == 0, "Shared scrollbar moves right without reducing its drag target");
            foreach (string ext in new[] { "pdf", "docx", "pptx", "txt", "md", "json", "cs" }) Check(PreviewFormatRegistry.Get("x." + ext) != PreviewFormat.Unsupported, "Supported " + ext);
            foreach (string ext in new[] { "doc", "ppt", "xlsx", "zip", "mp3", "mp4" }) Check(PreviewFormatRegistry.Get("x." + ext) == PreviewFormat.Unsupported, "Excluded " + ext);
            Check(PreviewFormatRegistry.Supports(new ClipItem { Kind = ClipKind.Text }) && PreviewFormatRegistry.Supports(new ClipItem { Kind = ClipKind.Image }), "Latest user override retains text and images");
            foreach (int count in new[] { 1, 20, 200 }) {
                string path = Path.Combine(root, $"pdf-{count}.pdf"); FilePreviewTests.MakePdf(path, count); var id = DocumentIdentity.Read(path); var watch = Stopwatch.StartNew();
                await using var provider = new PdfPreviewProvider(); await provider.OpenAsync(id, default); var first = await provider.RenderPageAsync(0, 1000, 96, PreviewQuality.Normal, 7, default);
                timing[$"pdf-{count}-cold-ms"] = watch.Elapsed.TotalMilliseconds; Check((await provider.GetPageCountAsync(false, default)) == count && first.RequestVersion == 7, $"PDF {count} metadata/page/version");
                var last = await provider.RenderPageAsync(9999, 1000, 96, PreviewQuality.Normal, 8, default); Check(last.PageIndex == count - 1, $"PDF {count} last-page clamp");
            }
            string doc = Path.Combine(root, "text.docx"), rich = Path.Combine(root, "rich.docx"); Docx(doc, 150); Docx(rich, 5, true);
            foreach (string path in new[] { doc, rich }) {
                var id = DocumentIdentity.Read(path); var watch = Stopwatch.StartNew(); await using var provider = new DocxPreviewProvider(cache); await provider.OpenAsync(id, default);
                Check(await provider.GetPageCountAsync(false, default) is null, "DOCX first page does not wait for total count");
                var first = await provider.RenderPageAsync(0, 1000, 96, PreviewQuality.Normal, 1, default); timing[Path.GetFileNameWithoutExtension(path) + "-cold-ms"] = watch.Elapsed.TotalMilliseconds; Save(first.RenderedBitmap, Path.Combine(root, Path.GetFileNameWithoutExtension(path) + ".png"));
                Check(first.IsApproximate && first.Width <= 1001, "DOCX direct scene bitmap is explicitly approximate");
                int count = (await provider.GetPageCountAsync(true, default))!.Value; Check(count > 1, "DOCX incremental pagination finishes multiple pages");
                var state = cache.Model<DocxPaginationState>(id.Id + ":docx-checkpoints")!; int checkpoint = state.BodyIndex;
                await provider.RenderPageAsync(count - 1, 1000, 96, PreviewQuality.Normal, 2, default);
                Check(state.BodyIndex == checkpoint && state.Complete, "DOCX cached pagination does not restart from beginning");
                Check(state.Pages.SelectMany(p => p.Nodes).OfType<SceneText>().Any(t => t.Runs.Any(r => r.Bold && r.Size > 20)), "DOCX style inheritance preserves heading bold/size");
                if (path == rich) { Check(state.Pages.SelectMany(p => p.Nodes).OfType<SceneImage>().Any(), "DOCX inline image scene"); Check(state.Pages.SelectMany(p => p.Nodes).OfType<ScenePlaceholder>().Any(), "DOCX unsupported object retains placeholder"); }
            }
            foreach (int count in new[] { 10, 100 }) {
                string path = Path.Combine(root, $"slides-{count}.pptx"); Pptx(path, count); var id = DocumentIdentity.Read(path); var watch = Stopwatch.StartNew();
                await using var provider = new PptxPreviewProvider(cache); await provider.OpenAsync(id, default); var thumbnail = await provider.GetThumbnailAsync(default); timing[$"pptx-{count}-thumb-ms"] = watch.Elapsed.TotalMilliseconds;
                Check(thumbnail?.Quality == PreviewQuality.Thumbnail, $"PPTX {count} embedded thumbnail");
                var first = await provider.RenderPageAsync(0, 1000, 96, PreviewQuality.Normal, 10, default); timing[$"pptx-{count}-cold-ms"] = watch.Elapsed.TotalMilliseconds; Save(first.RenderedBitmap, Path.Combine(root, $"pptx-{count}.png"));
                var scene = cache.Model<PreviewScene>(id.Id + ":ppt-scene:0")!;
                Check(scene.Nodes.OfType<SceneText>().Any(t => t.Runs.Any(r => r.Text.Contains($"第 {count} 张"))), "PPTX order follows relationships, not slide filenames");
                Check(scene.Nodes.OfType<SceneText>().Any(t => t.Box.X > 60 && t.Runs.Any(r => r.Color == "#FF1266AA")), "PPTX layout position and theme color inheritance");
                Check(scene.Nodes.OfType<SceneImage>().Any() && scene.Nodes.OfType<SceneShape>().Any(s => s.Rotation != 0), "PPTX images, shapes and rotation");
                Check(cache.Model<PreviewScene>(id.Id + ":ppt-scene:1") is null, "PPTX opening does not parse every slide");
                var autoText = scene.Nodes.OfType<SceneText>().Single(t => t.Runs.Any(r => r.Text == "AutoFit body fixture"));
                Check(autoText.Runs[0].Size == 24 && autoText.Box.Height > 20, "PPTX ordinary auto-fit box uses body size and avoids clipping");
            }
            var identity = DocumentIdentity.Read(doc); Check(identity.Id == DocumentIdentity.Read(doc).Id, "Stable document identity");
            Check(PreviewCacheService.RenderKey(identity, 0, 1000, 96) != PreviewCacheService.RenderKey(identity, 0, 1000, 144), "DPI participates in render cache identity");
            await using (var renderer = new PdfPageRenderService(cache)) {
                var watch = Stopwatch.StartNew(); var first = await renderer.RenderAsync(identity, 0, 1000, default); timing["docx-facade-cold-ms"] = watch.Elapsed.TotalMilliseconds;
                watch.Restart(); var warm = await renderer.RenderAsync(identity, 0, 1000, default); timing["docx-memory-warm-ms"] = watch.Elapsed.TotalMilliseconds; Check(ReferenceEquals(first.Image, warm.Image), "Warm page cache reuses bitmap");
            }
            await Task.Delay(300);
            using (var secondCache = new PreviewCacheService(cache.Root)) { var disk = secondCache.ReadPage(PreviewCacheService.RenderKey(identity, 0, 1000), default); Check(disk is not null, "Disk page cache survives preview session closure"); }
            using (var quota = new PreviewCacheService(Path.Combine(root, "quota-cache"))) {
                Directory.CreateDirectory(quota.Root); string oversized = Path.Combine(quota.Root, new string('A', 64) + ".png");
                using (var file = File.Create(oversized)) file.SetLength(PreviewCacheService.DiskLimit + 1);
                string keep = Path.Combine(quota.Root, "user-note.txt"); File.WriteAllText(keep, "keep"); quota.CleanDisk();
                Check(!File.Exists(oversized) && File.Exists(keep), "Disk capacity cleanup removes oversized generated cache only");
            }
            string mutable = Path.Combine(root, "mutable.docx"); File.Copy(doc, mutable, true); var original = DocumentIdentity.Read(mutable); Docx(mutable, 3); Check(original.Id != DocumentIdentity.Read(mutable).Id, "Source modification invalidates all cache layers");
            var records = new[] { FilePreviewTests.Item(doc), FilePreviewTests.Item(Path.Combine(root, "unsupported.xlsx")), FilePreviewTests.Item(Path.Combine(root, "slides-10.pptx")), FilePreviewTests.Item(Path.Combine(root, "pdf-20.pdf")) };
            await using (var session = new PreviewSession(records, 0, cache)) {
                session.Start(); await session.Pending; Check(session.Error is null, "Unified session opens DOCX directly"); var stale = session.CurrentRequest;
                session.NavigateRecord(1); await session.Pending; Check(session.Index == 1 && session.Error?.Code == "Unsupported", "Unified session exposes unsupported adjacent records");
                session.NavigateRecord(-1); await session.Pending; Check(session.Index == 0 && session.Error is null, "Unified session returns from unsupported to DOCX");
                for (int i = 0; i < 20; i++) { session.NavigateRecord(1); session.NavigateRecord(1); session.NavigateRecord(-1); session.NavigateRecord(-1); }
                session.NavigateRecord(1); session.NavigateRecord(1); await session.Pending; Check(session.Index == 2 && session.Presented?.DocumentId == DocumentIdentity.Read(records[2].FilePaths[0]).Id && !session.Accepts(stale), "Rapid mixed navigation rejects stale file results");
                for (int i = 0; i < 15; i++) session.NavigatePage(1); await session.Pending; Check(session.Page == 9, "Rapid PPTX arrows clamp at final slide");
                session.NavigateRecord(1); session.Cancel(); await session.Pending; Check(!session.Accepts(session.CurrentRequest), "Closing rejects all late results");
            }
            Check(!new AppSettings().PrewarmAdjacentPreview, "Adjacent preview warming defaults off for existing settings");
            var warmRecords = new[] { FilePreviewTests.Item(Path.Combine(root, "slides-10.pptx")), FilePreviewTests.Item(doc), FilePreviewTests.Item(Path.Combine(root, "pdf-20.pdf")) };
            using (var warmCache = new PreviewCacheService(Path.Combine(root, "warmup-cache"))) {
                await using var warmSession = new PreviewSession(warmRecords, 1, warmCache, prewarmAdjacent: true);
                warmSession.Start(); await warmSession.Pending; await warmSession.PrefetchPending; await warmSession.WarmupPending;
                Check(warmSession.WarmupCompleted == 2, "Low-priority warmup prepares both adjacent records");
                foreach (var neighbor in new[] { warmRecords[0], warmRecords[2] }) {
                    var id = DocumentIdentity.Read(neighbor.FilePaths[0]);
                    Check(warmCache.ReadPage(PreviewCacheService.RenderKey(id, 0, warmSession.PixelWidth, warmSession.PixelDpi), default) is not null,
                        "Warmup uses the ordinary foreground page-cache key");
                }
                var oldRequest = warmSession.CurrentRequest;
                warmSession.SetZoomFactor(1.37); await warmSession.Pending;
                Check(warmSession.ZoomFactor == 1.37 && warmSession.Presented?.Image.PixelWidth > 1000 && !warmSession.Accepts(oldRequest),
                    "Arbitrary zoom rerenders a sharper layer and rejects the old request");
                var zoomId = DocumentIdentity.Read(doc);
                Check(PreviewCacheService.RenderKey(zoomId, 0, 1370, 96, zoom: 1.37) != PreviewCacheService.RenderKey(zoomId, 0, 1370, 96),
                    "Zoom factor separates otherwise identical resolution cache entries");
                warmSession.NavigateRecord(1); await warmSession.Pending;
                Check(warmSession.ZoomFactor == 1 && warmSession.Index == 2 && !warmSession.Accepts(oldRequest), "Record navigation cancels obsolete zoom and resets the next record");
                warmSession.Cancel(); await warmSession.Pending; await warmSession.WarmupPending;
                Check(warmCache.Scheduler.QueuedCount == 0, "Closing removes stale speculative jobs from the renderer queue");
            }
            using (var bounded = new PreviewCacheService(Path.Combine(root, "bounded-layer-cache"))) {
                var bytes = new byte[800 * 800 * 4]; var bitmap = BitmapSource.Create(800, 800, 96, 96, PixelFormats.Bgra32, null, bytes, 800 * 4); bitmap.Freeze();
                bounded.SetActiveDocument("active"); bounded.Put("native-2:active:0:800:96:Normal:z1000", bitmap);
                for (int i = 0; i < 32; i++) bounded.Put($"native-2:other-{i}:0:800:96:Normal:z1000", bitmap);
                Check(bounded.MemoryBytes <= PreviewCacheService.MemoryLimit && bounded.PageCount <= 24 && bounded.Get("native-2:active:0:800:96:Normal:z1000") is not null,
                    "Zoom layers respect 64 MiB / 24 pages and preserve the current document over older neighbors");
            }
            using (var cancelQueue = new CancellationTokenSource()) {
                var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                using var release = new ManualResetEventSlim();
                Task blocker = cache.Scheduler.Run(() => { started.TrySetResult(); release.Wait(); return true; }, default);
                await started.Task;
                var queued = Enumerable.Range(0, 40).Select(_ => cache.Scheduler.Run(() => true, cancelQueue.Token, 2)).ToArray();
                cancelQueue.Cancel(); await Task.WhenAll(queued.Select(async job => { try { await job; } catch (OperationCanceledException) { } }));
                Check(cache.Scheduler.QueuedCount == 0, "Cancelled warmup jobs leave the priority queue without piling up");
                release.Set(); await blocker;
            }
            using (var cancel = new CancellationTokenSource()) { cancel.Cancel(); bool cancelled = false; try { await cache.Scheduler.Run(() => 1, cancel.Token); } catch (OperationCanceledException) { cancelled = true; } Check(cancelled, "Scheduler observes cancellation before execution"); }
            string bad = Path.Combine(root, "bad.docx"); Package(bad, new() { ["word/document.xml"] = Xml("<broken>") });
            await Error(bad, "InvalidPackage");
            string deep = Path.Combine(root, "deep.docx"); Package(deep, new() { ["word/document.xml"] = Xml(string.Concat(Enumerable.Repeat("<x>", 70)) + string.Concat(Enumerable.Repeat("</x>", 70))) }); await Error(deep, "SafetyLimit");
            string bomb = Path.Combine(root, "bomb.docx"); Package(bomb, new() { ["word/document.xml"] = new byte[33 * 1024 * 1024] }); await Error(bomb, "SafetyLimit");
            string badZip = Path.Combine(root, "bad-zip.docx"); File.WriteAllText(badZip, "not a zip archive"); await Error(badZip, "InvalidPackage");
            string missingRel = Path.Combine(root, "missing-rel.pptx"); Package(missingRel, new() { ["ppt/presentation.xml"] = Xml($"<p:presentation xmlns:p='{P}' xmlns:r='{R}'><p:sldId r:id='absent'/></p:presentation>") }); await Error(missingRel, "MissingRelationship");
            string entity = Path.Combine(root, "entity.docx"); Package(entity, new() { ["word/document.xml"] = Xml("<!DOCTYPE x [<!ENTITY external SYSTEM 'https://example.invalid/private'>]><x>&external;</x>") }); await Error(entity, "InvalidPackage");
            bool imageLimited = false; try { PreviewSceneRenderer.ValidateImageSize(50000, 50000); } catch (PreviewException e) { imageLimited = e.Code == "SafetyLimit"; } Check(imageLimited, "Oversized image dimensions rejected before decoding pixels");
            string external = Path.Combine(root, "external.docx"); Package(external, new() { ["word/_rels/document.xml.rels"] = Xml($"<Relationships><Relationship Id='remote' TargetMode='External' Target='https://example.invalid/private' Type='{R}/image'/></Relationships>") });
            using (var package = new SafeOoxmlPackage(external)) Check(package.Relations("word/document.xml", default).Count == 0, "External relationships are ignored without fetching");
            string encrypted = Path.Combine(root, "encrypted.docx"); File.WriteAllBytes(encrypted, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }); await Error(encrypted, "Encrypted");
            await Error(Path.Combine(root, "deleted.docx"), "MissingFile");
            string changedWhileOpen = Path.Combine(root, "changed-open.docx"); Docx(changedWhileOpen, 3);
            await using (var session = new PreviewSession(new[] { FilePreviewTests.Item(changedWhileOpen) }, 0, cache)) { session.Start(); await session.Pending; File.SetLastWriteTimeUtc(changedWhileOpen, DateTime.UtcNow.AddSeconds(10)); await Task.Delay(150); await FilePreviewTests.Idle(); Check(session.Error?.Code == "Changed", "Changing open document stops its session and requests reload"); }
            async Task Error(string path, string code) { await using var session = new PreviewSession(new[] { FilePreviewTests.Item(path) }, 0, cache); session.Start(); await session.Pending; Check(session.Error?.Code == code, "Safe failure " + code); }
            Check(!typeof(PreviewProviderRegistry).Assembly.GetTypes().Any(t => t.Name is "WindowsPreviewHandler" or "OfficeConversionWorker" or "DocumentConversionService"), "No Preview Handler or Office conversion implementation remains");
            var store = new HistoryStore(Path.Combine(root, "unsupported-main-fixture")); store.Settings.HistoryEnabled = store.Settings.WatchScreenshots = false;
            string[] unsupportedPaths = [Path.Combine(root, "excluded.xlsx"), Path.Combine(root, "excluded.zip"), Path.Combine(root, "excluded.mp3")];
            DateTimeOffset unsupportedTime = DateTimeOffset.UtcNow;
            for (int i = 0; i < unsupportedPaths.Length; i++) {
                var item = FilePreviewTests.Item(unsupportedPaths[i]); item.CreatedAt = unsupportedTime.AddSeconds(i); store.Add(item);
            }
            var main = new MainWindow(store, demo: true) { ShowActivated = false, ShowInTaskbar = false, Left = -12000, Top = -12000, WindowStartupLocation = WindowStartupLocation.Manual };
            try { main.Show(); await main.PendingSearch; await FilePreviewTests.Idle(); main.ApplyRowSelection(1, ModifierKeys.None);
                var list = (HistoryListBox)main.FindName("HistoryList"); await main.HandleHistoryKeyAsync(Key.Space, ModifierKeys.None, list); await FilePreviewTests.Idle();
                var unsupportedWindow = main.OwnedWindows.OfType<PreviewWindow>().Single(); await unsupportedWindow.PendingRender; await Task.Delay(220);
                Check(unsupportedWindow.Session.Error?.Code == "Unsupported" && unsupportedWindow.IsVisible, "Unsupported Space opens a fixed preview window without loading the file");
                Check(unsupportedWindow.DisplayedLocation.Contains(((ClipItem)list.Items[1]).FilePaths[0]), "Unsupported preview displays the full file location");
                Check(list.SelectedItems.Count == 1 && main.IsVisible, "Unsupported preview leaves main selection/window intact");
                Send(unsupportedWindow, Key.Down); await unsupportedWindow.PendingRender; await FilePreviewTests.Idle();
                Check(unsupportedWindow.RecordIndex == 2 && list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItems[0], list.Items[2]), "Preview Down synchronizes the main-list selection without skipping unsupported records");
                Send(unsupportedWindow, Key.Up); await unsupportedWindow.PendingRender; await FilePreviewTests.Idle();
                Check(unsupportedWindow.RecordIndex == 1 && list.SelectedItems.Count == 1 && ReferenceEquals(list.SelectedItems[0], list.Items[1]) && unsupportedWindow.IsVisible, "Preview Up restores the matching main-list selection while preview stays open");
                SaveWindow(unsupportedWindow, Path.Combine(root, "unsupported-preview.png"));
                await unsupportedWindow.CloseAndReleaseAsync();
            } finally { main.Close(); }
            var window = new PreviewWindow(records, 2, cache) { ShowActivated = false, ShowInTaskbar = false };
            try { var showTimer = Stopwatch.StartNew(); window.Show(); timing["container-show-call-ms"] = showTimer.Elapsed.TotalMilliseconds; await FilePreviewTests.Idle(); await window.PendingRender; await Task.Delay(400); window.UpdateLayout();
                SaveWindow(window, Path.Combine(root, "preview-window.png")); double width = window.ActualWidth, height = window.ActualHeight;
                window.ZoomTo(1.2); window.ZoomTo(1.43); await Task.Delay(280); await window.PendingRender; window.UpdateLayout();
                Check(window.Session.ZoomFactor == 1.43 && window.ActualWidth == width && window.ActualHeight == height,
                    "Debounced zoom applies only its latest target without resizing the outer preview window");
                SaveWindow(window, Path.Combine(root, "preview-zoom.png"));
                window.ZoomTo(1); await Task.Delay(280); await window.PendingRender;
                for (int i = 0; i < 20; i++) { Send(window, Key.Right); Send(window, Key.Left); } await window.PendingRender;
                Check(window.ActualWidth == width && window.ActualHeight == height && window.PageIndex == 0, "Real preview routed keys keep fixed window and final page");
                foreach (bool dark in new[] { false, true }) { ThemeManager.Apply(new AppSettings { Theme = dark ? "Dark" : "Light" }); await Task.Delay(220); window.UpdateLayout();
                    foreach (double dpi in new[] { 1.25, 1.5, 2.0 }) { var capture = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); capture.Render(window); Save(capture, Path.Combine(root, $"native-preview-{dark}-{dpi}.png")); Check(window.ActualWidth == width && window.ActualHeight == height, $"Native preview fixed layout simulated {dpi * 100}% / dark={dark}"); }
                }
                Send(window, Key.Escape); await Task.Delay(210); await window.Cleanup; Check(!window.IsVisible, "Esc closes and releases native preview");
            } finally { if (window.IsVisible) await window.CloseAndReleaseAsync(); }
            timing["peak-working-set-mb"] = Process.GetCurrentProcess().PeakWorkingSet64 / 1048576d;
            timing["cache-hit-ratio"] = cache.Hits / (double)Math.Max(1, cache.Hits + cache.Misses);
        } catch (Exception e) { error = e.ToString(); }
        finally { File.WriteAllText(Path.Combine(root, "native-preview-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, timing, phases = PreviewMetrics.Snapshot().Select(x => new { stage = x.Stage, ms = x.Milliseconds }), error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
    private static void Send(PreviewWindow window, Key key) => window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
    private static void Save(BitmapSource image, string path) { var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(image)); using var stream = File.Create(path); png.Save(stream); }
    private static void SaveWindow(PreviewWindow window, string path) { var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); Save(bitmap, path); }
}
