using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class CommonPreviewTests
{
    public static async Task RunAsync(string root)
    {
        root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>(); string? error = null; PreviewWindow? window = null;
        void Check(bool value, string name) { if (!value) throw new Exception(name); checks.Add(name); }
        string Package(string name, params (string, string)[] entries) {
            string path = Path.Combine(root, name); using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (part, text) in entries) { using var writer = new StreamWriter(zip.CreateEntry(part).Open()); writer.Write(text); } return path;
        }
        Task<FilePreviewResult> Load(string path, int page = 0) => FilePreviewLoader.LoadAsync(path, page, FilePreviewLoader.TextChunk, default);
        try {
            string doc = Package("正文.docx", ("word/document.xml", "<document><body><p><r><t>正文 中文</t></r></p><p><r><t>第二段</t></r></p></body></document>"));
            Check((await Load(doc)).Text!.Contains("第二段"), "DOCX paragraphs");
            string odt = Package("正文.odt", ("content.xml", "<document><h>标题</h><p>开放文档</p></document>"));
            Check((await Load(odt)).Text!.Contains("开放文档"), "ODT text");
            string ppt = Package("幻灯片.pptx", ("ppt/presentation.xml", "<presentation xmlns:r='urn:rel'><sldIdLst><sldId id='1' r:id='b'/><sldId id='2' r:id='a'/></sldIdLst></presentation>"), ("ppt/_rels/presentation.xml.rels", "<Relationships><Relationship Id='a' Target='slides/slide1.xml'/><Relationship Id='b' Target='slides/slide9.xml'/></Relationships>"), ("ppt/slides/slide9.xml", "<slide><p><t>First</t></p></slide>"), ("ppt/slides/slide1.xml", "<slide><p><t>Second</t></p></slide>"));
            Check((await Load(ppt)).Text!.Trim() == "First" && (await Load(ppt, 1)).Text!.Trim() == "Second" && (await Load(ppt)).PageCount == 2, "PPT relationship order and paging");
            string xlsx = Package("表格.xlsx", ("xl/workbook.xml", "<workbook xmlns:r='urn:rel'><sheets><sheet name='销售' r:id='a'/><sheet name='汇总' r:id='b'/></sheets></workbook>"), ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='a' Target='worksheets/custom.xml'/><Relationship Id='b' Target='worksheets/other.xml'/></Relationships>"), ("xl/sharedStrings.xml", "<sst><si><t>产品</t></si></sst>"), ("xl/worksheets/custom.xml", "<worksheet><sheetData><row r='3'><c r='A3' t='s'><v>0</v></c><c r='C3'><f>1+2</f><v>3</v></c></row><row r='4'><c r='A4' t='inlineStr'><is><t>项目</t></is></c><c r='B4' t='b'><v>1</v></c></row></sheetData></worksheet>"), ("xl/worksheets/other.xml", "<worksheet><sheetData><row r='1'><c r='A1'><v>42</v></c></row></sheetData></worksheet>"));
            var sheet = await Load(xlsx); Check(sheet.PageCount == 2 && sheet.Table!.Rows[0].SequenceEqual(new[] { "3", "产品", "", "3" }), "Excel sparse cells, shared strings, cached formula");
            Check(sheet.Table!.Rows[1][1] == "项目" && sheet.Table.Rows[1][2] == "TRUE" && (await Load(xlsx, 1)).Table!.Rows[0][1] == "42", "Excel inline strings, boolean and sheet switch");
            string largeSheet = Package("large-sheet.xlsx", ("xl/workbook.xml", "<workbook xmlns:r='urn:rel'><sheets><sheet name='Large' r:id='a'/></sheets></workbook>"), ("xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='a' Target='worksheets/large.xml'/></Relationships>"), ("xl/worksheets/large.xml", "<worksheet><sheetData>" + string.Concat(Enumerable.Range(1, 100000).Select(n => $"<row r='{n}'><c r='A{n}'><v>{n}</v></c></row>")) + "</sheetData></worksheet>"));
            var timer = System.Diagnostics.Stopwatch.StartNew(); var largePreview = await Load(largeSheet); timer.Stop();
            Check(largePreview.Table!.Rows.Count == 500 && largePreview.Table.Rows[499][1] == "500", "100000-row worksheet reads only preview prefix");
            File.WriteAllText(Path.Combine(root, "large-sheet-timing.json"), JsonSerializer.Serialize(new { rows = 100000, shown = 500, milliseconds = timer.Elapsed.TotalMilliseconds, scope = "Synthetic local workbook; not a guarantee for user files" }));
            var csv = CommonFilePreview.Delimited("a,\"b,c\"\r\n1,\"two\nlines\"", ',');
            Check(csv.Rows.Count == 2 && csv.Rows[0][1] == "b,c" && csv.Rows[1][1].Contains('\n'), "CSV quoted separators and newlines");
            Check(CommonFilePreview.Delimited(string.Join('\n', Enumerable.Repeat("x\ty", 600)), '\t').Rows.Count == 500, "Delimited row bound");
            string archive = Package("清单.zip", ("../never-extracted.txt", "private fixture"), ("folder/file.txt", "test"));
            Check((await Load(archive)).Table!.Rows.Count == 2 && !File.Exists(Path.Combine(Path.GetDirectoryName(root)!, "never-extracted.txt")), "ZIP lists without extraction");
            Check((await Load(root)).Table!.Rows.Any(r => r[0] == "清单.zip"), "Folder immediate entries");
            string dtd = Package("blocked.docx", ("word/document.xml", "<!DOCTYPE doc [<!ENTITY x SYSTEM 'file:///never-read'>]><doc><p><t>&x;</t></p></doc>"));
            bool blocked = false; try { await Load(dtd); } catch (System.Xml.XmlException) { blocked = true; } Check(blocked, "DTD and external entities rejected");
            string oversized = Package("large.docx", ("word/document.xml", "<doc>" + new string('x', 4 * 1024 * 1024) + "</doc>"));
            blocked = false; try { await Load(oversized); } catch (InvalidDataException) { blocked = true; } Check(blocked, "Uncompressed XML limit enforced");
            string legacy = Path.Combine(root, "legacy.xls"); File.WriteAllText(legacy, "fixture"); Check((await Load(legacy)).Text!.Contains("旧版"), "Legacy Office fallback");
            string media = Path.Combine(root, "silent.wav");
            using (var writer = new BinaryWriter(File.Create(media))) { writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(16036); writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(8000); writer.Write(16000); writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(16000); writer.Write(new byte[16000]); }
            Check((await Load(media)).MediaPath == media, "Media offered without decoding or playback");
            using (var player = new PreviewMediaControl(media)) { Check(!player.HasSource && !player.IsPlaying, "Player never autoplays"); player.Pause(); player.Dispose(); player.Play(); Check(!player.HasSource && !player.IsPlaying, "Disposed player cannot restart"); }
            foreach (string file in new[] { doc, xlsx, ppt, archive, media }) {
                window = new PreviewWindow(new[] { new ClipItem { Kind = ClipKind.File, FilePaths = new() { file } } }, 0); window.Show(); await window.PendingRender; window.UpdateLayout();
                Check(!All<TextBlock>(window).Any(t => t.Text == "正在读取…"), "UI loaded " + Path.GetExtension(file));
                if (file == xlsx) {
                    Check(All<DataGrid>(window).Any(), "Excel uses read-only virtualized grid"); window.NavigatePage(1); await window.PendingRender; window.UpdateLayout(); Check(window.PageIndex == 1, "UI worksheet paging");
                    var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(root, "table-preview.png")); encoder.Save(output);
                    ThemeManager.Apply(new AppSettings { Theme = "Dark" }); window.UpdateLayout();
                    var dark = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); dark.Render(window); var darkEncoder = new PngBitmapEncoder(); darkEncoder.Frames.Add(BitmapFrame.Create(dark)); using var darkOutput = File.Create(Path.Combine(root, "table-preview-dark.png")); darkEncoder.Save(darkOutput); ThemeManager.Apply(new AppSettings { Theme = "Light" });
                }
                if (file == media) { var player = All<PreviewMediaControl>(window).Single(); player.Play(); Check(player.HasSource && player.IsPlaying, "Explicit playback starts"); player.Pause(); Check(!player.IsPlaying, "Playback pauses"); window.Close(); Check(!player.HasSource && !player.IsPlaying, "Window close releases media"); window = null; continue; }
                window.Close(); window = null;
            }
            window = new PreviewWindow(new[] { doc, xlsx, ppt }.Select(p => new ClipItem { Kind = ClipKind.File, FilePaths = new() { p } }).ToArray(), 1);
            window.Show(); await window.PendingRender; window.UpdateLayout();
            void KeyPress(Key key, UIElement? source = null) { source ??= window; source.Focus(); source.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window), Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent }); }
            var beforePage = window.PresentedContent;
            KeyPress(Key.Right, All<DataGrid>(window).Single()); Check(ReferenceEquals(beforePage, window.PresentedContent), "Paging retains visible content while loading"); await window.PendingRender; window.UpdateLayout(); Check(window.PageIndex == 1 && window.RecordIndex == 1, "Right changes worksheet with grid focus");
            KeyPress(Key.Right); await window.PendingRender; Check(window.PageIndex == 1 && window.RecordIndex == 1, "Last page never jumps to another record");
            KeyPress(Key.Left); await window.PendingRender; window.UpdateLayout(); Check(window.PageIndex == 0, "Left changes worksheet backwards");
            KeyPress(Key.Down, All<DataGrid>(window).Single()); await window.PendingRender; window.UpdateLayout(); Check(window.RecordIndex == 2, "Down leaves Excel for next record");
            KeyPress(Key.Right, All<TextBox>(window).Single()); await window.PendingRender; window.UpdateLayout(); Check(window.PageIndex == 1 && window.RecordIndex == 2, "PPT right paging with text focus");
            KeyPress(Key.Up, All<TextBox>(window).Single()); await window.PendingRender; window.UpdateLayout(); Check(window.RecordIndex == 1 && window.PageIndex == 0, "Up returns to Excel and resets page");
            KeyPress(Key.Up, All<DataGrid>(window).Single()); await window.PendingRender; window.UpdateLayout(); Check(window.RecordIndex == 0, "Up leaves Excel for previous record");
            All<Button>(window).Single(b => b.Content?.ToString() == "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.PendingRender; window.UpdateLayout();
            All<Button>(window).Single(b => b.Content?.ToString() == "下一条").RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); await window.PendingRender; Check(window.RecordIndex == 2, "Next-record button leaves Excel");
            window.Close(); window = null;
        } catch (Exception ex) { error = ex.ToString(); }
        finally { window?.Close(); File.WriteAllText(Path.Combine(root, "common-preview-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }
    }
    private static IEnumerable<T> All<T>(DependencyObject root) where T : DependencyObject {
        if (root is T value) yield return value;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++) foreach (var child in All<T>(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
