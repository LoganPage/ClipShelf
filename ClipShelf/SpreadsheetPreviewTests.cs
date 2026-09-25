using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ClipShelf;

internal static class SpreadsheetPreviewTests
{
    internal static async Task RunAsync(string root, Action<bool, string> check)
    {
        string path = Path.Combine(root, "spreadsheet.xlsx");
        using (var file = File.Create(path)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
            Add(zip, "xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='一月' r:id='s1'/><sheet name='二月' r:id='s2'/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='s1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='s2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/></Relationships>");
            Add(zip, "xl/sharedStrings.xml", "<sst><si><t>共享中文</t></si></sst>");
            Add(zip, "xl/styles.xml", "<styleSheet><numFmts><numFmt numFmtId='164' formatCode='00000'/></numFmts><cellXfs><xf numFmtId='0'/><xf numFmtId='14'/><xf numFmtId='9'/><xf numFmtId='5'/><xf numFmtId='164'/></cellXfs></styleSheet>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r='1'><c r='A1' t='s'><v>0</v></c><c r='C1' t='inlineStr'><is><t>内联</t></is></c><c r='D1' t='b'><v>1</v></c><c r='E1' t='e'><v>#N/A</v></c><c r='F1'><f>1+2</f><v>3</v></c><c r='G1'><f>1+2</f></c><c r='X1'><f>1+2</f><v /></c></row><row r='2'><c r='A2' s='1'><v>45292</v></c><c r='B2' s='2'><v>0.25</v></c><c r='C2' s='3'><v>12.5</v></c><c r='D2' s='4'><v>42</v></c></row></sheetData></worksheet>");
            Add(zip, "xl/worksheets/sheet2.xml", "<worksheet><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>第二张</t></is></c><c r='B1'><f>1+2</f><v>3</v></c></row></sheetData></worksheet>");
        }
        var service = new SpreadsheetPreviewService(); var id = DocumentIdentity.Read(path);
        var first = await service.ReadAsync(id, 0, default);
        check(first.SheetCount == 2 && first.SheetName == "一月" && first.Rows[0].Cells[0] == "共享中文" && first.Rows[0].Cells[1] == "" && first.Rows[0].Cells[2] == "内联", "XLSX shared/inline/sparse cells: " + string.Join("|", first.Rows[0].Cells.Take(4)));
        check(first.Rows[0].Cells[3] == "TRUE" && first.Rows[0].Cells[4] == "#N/A" && first.Rows[0].Cells[5] == "3" && first.MissingFormulaCache && first.Rows[0].Cells[6] == "公式结果未保存", "XLSX booleans/errors/formula cache");
        check(first.Rows[0].Cells[23] == "公式结果未保存" && first.MissingFormulaCache, "XLSX empty self-closing formula cache");
        check(first.Rows[1].Cells[0].Contains("2024") && first.Rows[1].Cells[1] == "25%" && first.Rows[1].Cells[2].Contains('$') && first.Rows[1].Cells[3] == "00042", "XLSX date/percent/currency/leading zeros: " + string.Join("|", first.Rows[1].Cells.Take(4)));
        var second = await service.ReadAsync(id, 1, default); check(second.SheetIndex == 1 && second.Rows[0].Cells[0] == "第二张", "XLSX sheet switching");
        check(second.Rows[0].Cells[1] == "3" && !second.MissingFormulaCache, "XLSX saved formula cache stays intact");
        using (var cancelled = new System.Threading.CancellationTokenSource()) { cancelled.Cancel();
            try { await service.ReadAsync(id, 0, cancelled.Token); check(false, "Cancelled XLSX read"); }
            catch (OperationCanceledException) { check(true, "Cancelled XLSX read"); }
        }
        string wide = Path.Combine(root, "truncated.xlsx");
        using (var file = File.Create(wide)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
            Add(zip, "xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Large' r:id='s1'/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='s1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet><sheetData>" + string.Concat(Enumerable.Range(1, 501).Select(i => $"<row r='{i}'><c r='AX{i}'><v>1</v></c></row>")) + "</sheetData></worksheet>");
        }
        var limited = await service.ReadAsync(DocumentIdentity.Read(wide), 0, default);
        check(limited.Rows.Count == 500 && limited.Truncated, "XLSX stops at row/column limits");
        using var cache = new PreviewCacheService(Path.Combine(root, "spreadsheet-cache"));
        await using (var session = new PreviewSession(new[] { FilePreviewTests.Item(path) }, 0, cache)) {
            session.Start(); await session.Pending; check(session.PresentedSpreadsheet?.SheetName == "一月", "XLSX preview session opens");
            session.NavigatePage(1); await session.Pending; check(session.PresentedSpreadsheet?.SheetName == "二月", "XLSX preview session changes sheet");
        }
        string broken = Path.Combine(root, "broken.xlsx"); File.WriteAllText(broken, "not a zip");
        try { await service.ReadAsync(DocumentIdentity.Read(broken), 0, default); check(false, "Reject malformed workbook"); }
        catch (InvalidDataException) { check(true, "Reject malformed workbook"); }
        string encrypted = Path.Combine(root, "encrypted.xlsx"); File.WriteAllBytes(encrypted, new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0, 0, 0, 0 });
        try { await service.ReadAsync(DocumentIdentity.Read(encrypted), 0, default); check(false, "Reject encrypted workbook"); }
        catch (PreviewException error) when (error.Code == "Encrypted") { check(true, "Reject encrypted workbook"); }
        string oversized = Path.Combine(root, "oversized.xlsx");
        using (var file = File.Create(oversized)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
            Add(zip, "xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Large' r:id='s1'/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='s1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet><sheetData><row r='1'><c r='A1' t='inlineStr'><is><t>" + new string('x', 16 * 1024 * 1024) + "</t></is></c></row></sheetData></worksheet>");
        }
        try { await service.ReadAsync(DocumentIdentity.Read(oversized), 0, default); check(false, "XLSX oversized sheet uses SafetyLimit"); }
        catch (PreviewException error) when (error.Code == "SafetyLimit") { check(true, "XLSX oversized sheet uses SafetyLimit"); }
        string excessiveNodes = Path.Combine(root, "excessive-nodes.xlsx");
        using (var file = File.Create(excessiveNodes)) using (var zip = new ZipArchive(file, ZipArchiveMode.Create)) {
            Add(zip, "xl/workbook.xml", "<workbook xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><sheets><sheet name='Large' r:id='s1'/></sheets></workbook>");
            Add(zip, "xl/_rels/workbook.xml.rels", "<Relationships><Relationship Id='s1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/></Relationships>");
            Add(zip, "xl/worksheets/sheet1.xml", "<worksheet>" + string.Concat(Enumerable.Repeat("<ignored/>", 300100)) + "<sheetData/></worksheet>");
        }
        try { await service.ReadAsync(DocumentIdentity.Read(excessiveNodes), 0, default); check(false, "XLSX node limit uses SafetyLimit"); }
        catch (PreviewException error) when (error.Code == "SafetyLimit") { check(true, "XLSX node limit uses SafetyLimit"); }
        string writerEmpty = Path.Combine(root, "writer-empty-cache.xlsx");
        string writerCached = Path.Combine(root, "writer-saved-cache.xlsx");
        WriteWriterShapeWorkbook(writerEmpty, cached: false);
        WriteWriterShapeWorkbook(writerCached, cached: true);
        var missing = await service.ReadAsync(DocumentIdentity.Read(writerEmpty), 0, default);
        var saved = await service.ReadAsync(DocumentIdentity.Read(writerCached), 0, default);
        check(missing.Rows.Single(r => r.Number == 6).Cells[2] == "公式结果未保存" && missing.MissingFormulaCache &&
            missing.SheetCount == 2 && !missing.Truncated && missing.Rows.Single(r => r.Number == 2).Cells[0] == "中文共享字符串" &&
            missing.Rows.Single(r => r.Number == 4).Cells[5] == "00042",
            "Writer-shaped XLSX self-closing formula cache is missing");
        check(saved.Rows.Single(r => r.Number == 6).Cells[2] == "1322.5" && !saved.MissingFormulaCache &&
            saved.SheetCount == 2 && !saved.Truncated && saved.Rows.Single(r => r.Number == 4).Cells[0].Contains("2024"),
            "Writer-shaped XLSX saved formula cache is preserved");
        var nextSheet = await service.ReadAsync(DocumentIdentity.Read(writerCached), 1, default);
        check(nextSheet.SheetName == "第二工作表" && nextSheet.Rows[0].Cells[0] == "第二页中文" && !nextSheet.MissingFormulaCache,
            "Writer-shaped XLSX second worksheet remains readable");
    }
    private static void WriteWriterShapeWorkbook(string path, bool cached)
    {
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", "<?xml version='1.0' encoding='utf-8'?><Types xmlns='http://schemas.openxmlformats.org/package/2006/content-types'><Default Extension='rels' ContentType='application/vnd.openxmlformats-package.relationships+xml'/><Default Extension='xml' ContentType='application/xml'/><Override PartName='/xl/workbook.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml'/><Override PartName='/xl/worksheets/sheet1.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/worksheets/sheet2.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml'/><Override PartName='/xl/styles.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml'/><Override PartName='/xl/sharedStrings.xml' ContentType='application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml'/></Types>");
        Add(zip, "_rels/.rels", "<?xml version='1.0' encoding='utf-8'?><Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument' Target='xl/workbook.xml'/></Relationships>");
        Add(zip, "xl/workbook.xml", "<?xml version='1.0' encoding='utf-8'?><workbook xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' xmlns:r='http://schemas.openxmlformats.org/officeDocument/2006/relationships'><workbookPr/><sheets><sheet name='数据' sheetId='1' r:id='rId1'/><sheet name='第二工作表' sheetId='2' r:id='rId2'/></sheets></workbook>");
        Add(zip, "xl/_rels/workbook.xml.rels", "<?xml version='1.0' encoding='utf-8'?><Relationships xmlns='http://schemas.openxmlformats.org/package/2006/relationships'><Relationship Id='rId1' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet1.xml'/><Relationship Id='rId2' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet' Target='worksheets/sheet2.xml'/><Relationship Id='rId3' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles' Target='styles.xml'/><Relationship Id='rId4' Type='http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings' Target='sharedStrings.xml'/></Relationships>");
        Add(zip, "xl/sharedStrings.xml", "<?xml version='1.0' encoding='utf-8'?><sst xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main' count='2' uniqueCount='2'><si><t>中文共享字符串</t></si><si><t>第二页中文</t></si></sst>");
        Add(zip, "xl/styles.xml", "<?xml version='1.0' encoding='utf-8'?><styleSheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><numFmts count='2'><numFmt numFmtId='164' formatCode='00000'/><numFmt numFmtId='165' formatCode='¥#,##0.00'/></numFmts><fonts count='1'><font><name val='Calibri'/></font></fonts><fills count='1'><fill><patternFill patternType='none'/></fill></fills><borders count='1'><border/></borders><cellStyleXfs count='1'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/></cellStyleXfs><cellXfs count='4'><xf numFmtId='0' fontId='0' fillId='0' borderId='0'/><xf numFmtId='14' fontId='0' fillId='0' borderId='0'/><xf numFmtId='164' fontId='0' fillId='0' borderId='0'/><xf numFmtId='165' fontId='0' fillId='0' borderId='0'/></cellXfs></styleSheet>");
        string formulaCache = cached ? "<v>1322.5</v>" : "<v/>";
        Add(zip, "xl/worksheets/sheet1.xml", "<?xml version='1.0' encoding='utf-8'?><worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><dimension ref='A2:F8'/><sheetViews><sheetView workbookViewId='0'/></sheetViews><sheetFormatPr defaultRowHeight='15'/><sheetData><row r='2'><c r='A2' t='s'><v>0</v></c><c r='D2' t='inlineStr'><is><t>稀疏行</t></is></c></row><row r='4'><c r='A4' s='1' t='n'><v>45292</v></c><c r='C4' s='3' t='n'><v>1322.5</v></c><c r='F4' s='2' t='n'><v>42</v></c></row><row r='6'><c r='C6'><f>SUM(C4)</f>" + formulaCache + "</c></row><row r='8'><c r='A8' t='inlineStr'><is><t>结尾</t></is></c></row></sheetData><mergeCells count='1'><mergeCell ref='A8:B8'/></mergeCells></worksheet>");
        Add(zip, "xl/worksheets/sheet2.xml", "<?xml version='1.0' encoding='utf-8'?><worksheet xmlns='http://schemas.openxmlformats.org/spreadsheetml/2006/main'><dimension ref='A1:C3'/><sheetData><row r='1'><c r='A1' t='s'><v>1</v></c></row><row r='3'><c r='C3' t='inlineStr'><is><t>末行</t></is></c></row></sheetData></worksheet>");
    }
    private static void Add(ZipArchive zip, string name, string xml) { using var stream = zip.CreateEntry(name).Open(); byte[] bytes = Encoding.UTF8.GetBytes(xml); stream.Write(bytes); }
}
