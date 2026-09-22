using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class TextFilePreviewTests
{
    internal static async Task RunAsync(string root)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown; root = Path.GetFullPath(root); Directory.CreateDirectory(root);
        var checks = new List<string>(); string? error = null; PreviewWindow? window = null;
        void Check(bool ok, string label) { if (!ok) throw new Exception(label); checks.Add(label); }
        using var cache = new PreviewCacheService(Path.Combine(root, "cache"));
        try {
            string[] extensions = [".txt", ".md", ".log", ".json", ".xml", ".csv", ".ini", ".yaml", ".yml", ".cs", ".cpp", ".h", ".py", ".js", ".ts", ".html", ".css", ".sql", ".ps1", ".bat", ".cmd"];
            foreach (string extension in extensions) Check(PreviewFormatRegistry.Get("sample" + extension) == PreviewFormat.Text, "Text registry " + extension);
            Check(RecordTypeIcon.Category(FilePreviewTests.Item(Path.Combine(root, "sample.ts"))) == "Document", "Text file keeps the existing document icon category");

            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string utf8 = Path.Combine(root, "utf8.txt"), utf8Bom = Path.Combine(root, "utf8-bom.md"), le = Path.Combine(root, "utf16-le.log"), be = Path.Combine(root, "utf16-be.ini"), gb = Path.Combine(root, "gb18030.txt");
            string mixed = "中文 English emoji 🌿\r\n第二行\n第三行\r第四行";
            File.WriteAllText(utf8, mixed, new UTF8Encoding(false));
            File.WriteAllText(utf8Bom, mixed, new UTF8Encoding(true));
            WriteEncoded(le, mixed, new UnicodeEncoding(false, true, true)); WriteEncoded(be, mixed, new UnicodeEncoding(true, true, true));
            File.WriteAllBytes(gb, Encoding.GetEncoding(54936).GetBytes("广东财经大学，日志预览"));
            var provider = new TextFilePreviewProvider(cache);
            async Task<TextPreviewDocument> Open(string path) => await provider.OpenAsync(DocumentIdentity.Read(path), default);
            foreach (var fixture in new[] { (utf8, "UTF-8"), (utf8Bom, "UTF-8 BOM"), (le, "UTF-16 LE"), (be, "UTF-16 BE"), (gb, "GB18030") }) {
                var document = await Open(fixture.Item1); Check(document.Encoding.DisplayName == fixture.Item2, "Detect " + fixture.Item2);
                Check((fixture.Item1 == gb ? document.Current.Text.Contains("广东财经大学") : document.Current.Text.Contains("中文") && document.Current.Text.Contains("🌿")), "Decode mixed Unicode " + fixture.Item2);
            }
            var normalized = await Open(utf8); Check(!normalized.Current.Text.Contains('\r') && normalized.Current.Text.Count(c => c == '\n') == 3, "CRLF, LF and CR normalize without losing lines");

            string empty = Path.Combine(root, "empty.txt"); File.WriteAllBytes(empty, []); var emptyDocument = await Open(empty);
            Check(emptyDocument.Current.Text.Length == 0 && emptyDocument.Current.EndOfFile, "Empty text file is an explicit empty document");
            string longLine = Path.Combine(root, "long-line.log"); File.WriteAllText(longLine, new string('A', TextChunkReader.ChunkCharacters * 3) + "尾", new UTF8Encoding(false));
            var longDocument = await Open(longLine); Check(longDocument.Current.Text.Length <= TextChunkReader.ChunkCharacters && !longDocument.Current.EndOfFile, "Extreme single line is bounded to one segment");
            await provider.MoveAsync(longDocument, 1, default); await provider.MoveAsync(longDocument, 1, default);
            Check(longDocument.SegmentIndex == 2 && longDocument.Current.Text.Length <= TextChunkReader.ChunkCharacters, "Long single line remains browsable by later segments");
            await provider.MoveAsync(longDocument, -1, default); Check(longDocument.SegmentIndex == 1, "Large text supports previous segment navigation");

            string large = Path.Combine(root, "large.log");
            using (var writer = new StreamWriter(large, false, new UTF8Encoding(false))) for (int i = 0; i < 120000; i++) writer.WriteLine($"{i:D6} 日志内容 event-{i % 97}");
            var pulseWatch = Stopwatch.StartNew(); var loading = Open(large); await Application.Current.Dispatcher.InvokeAsync(() => { });
            Check(pulseWatch.ElapsedMilliseconds < 500, "Large text indexing does not block the UI dispatcher");
            var largeDocument = await loading; Check(largeDocument.IsLarge && !largeDocument.Current.EndOfFile, "Large log opens the first segment before the whole file");
            var nextChunk = await provider.MoveAsync(largeDocument, 1, default); Check(nextChunk.StartByte > 0 && nextChunk.Text.Contains("日志内容"), "Large log later segment remains readable");

            string binary = Path.Combine(root, "binary.txt"); File.WriteAllBytes(binary, Enumerable.Range(0, 4096).Select(i => (byte)(i % 4 == 0 ? 0 : 1)).ToArray());
            await ExpectError(() => Open(binary), "BinaryText", "Binary file disguised as .txt is rejected");
            string invalid = Path.Combine(root, "invalid.txt"); File.WriteAllBytes(invalid, Enumerable.Repeat((byte)0xFF, 4096).ToArray());
            await ExpectError(() => Open(invalid), "EncodingError", "Unreliable encoding is rejected instead of displaying mojibake");
            using (var cancelled = new CancellationTokenSource()) { cancelled.Cancel(); bool stopped = false; try { await provider.OpenAsync(DocumentIdentity.Read(utf8), cancelled.Token); } catch (OperationCanceledException) { stopped = true; } Check(stopped, "Cancelled text detection produces no result"); }

            string changed = Path.Combine(root, "changed.log"); File.Copy(large, changed, true); var changedDocument = await Open(changed); File.AppendAllText(changed, "changed");
            await ExpectError(() => provider.MoveAsync(changedDocument, 1, default), "Changed", "Modification during segmented reading invalidates the document");
            await using (var missing = new PreviewSession(new[] { FilePreviewTests.Item(Path.Combine(root, "missing.txt")) }, 0, cache)) { missing.Start(); await missing.Pending; Check(missing.Error?.Code == "MissingFile", "Missing text file has inline error"); }
            using (var locked = new FileStream(utf8, FileMode.Open, FileAccess.Read, FileShare.None)) { await using var session = new PreviewSession(new[] { FilePreviewTests.Item(utf8) }, 0, cache); session.Start(); await session.Pending; Check(session.Error?.Code == "FileBusy", "Occupied text file has inline error"); }
            Check(PreviewException.From(new UnauthorizedAccessException()).Code == "AccessDenied", "Text permission errors remain distinct");
            string deniedPath = Path.Combine(root, "denied.txt"); File.Copy(utf8, deniedPath, true); var denied = new FileInfo(deniedPath); var originalAcl = denied.GetAccessControl();
            try { var acl = denied.GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadData, AccessControlType.Deny)); denied.SetAccessControl(acl);
                await using var session = new PreviewSession(new[] { FilePreviewTests.Item(deniedPath) }, 0, cache); session.Start(); await session.Pending; Check(session.Error?.Code == "AccessDenied", "Actual denied text file has inline permission error");
            } finally { denied.SetAccessControl(originalAcl); }

            var searchResult = new TextPreviewResult("search", "Alpha beta alpha\nlast", false, Monospace: true);
            var view = new TextPreviewViewModel(searchResult) { SearchText = "alpha" };
            Check(view.Find(false, true) && view.MatchStart == 0 && view.Find(false) && view.MatchStart == 11 && view.Find(true) && view.MatchStart == 0, "Search moves forward, wraps and moves backward");
            view.WordWrap = false; view.ShowLineNumbers = true; Check(!view.WordWrap && view.ShowLineNumbers && view.Lines.Numbers(1).StartsWith("1\n2"), "Wrap and line-number states are independent");

            var records = new[] { FilePreviewTests.Item(large), FilePreviewTests.Item(Path.Combine(root, "unsupported.xlsx")), FilePreviewTests.Item(utf8) };
            await using (var session = new PreviewSession(records, 0, cache)) {
                session.Start(); session.NavigateRecord(1); session.NavigateRecord(1); await session.Pending;
                Check(session.Index == 2 && session.PresentedText?.Text.Contains("中文") == true && session.Error is null, "Rapid Down navigation commits only the latest text file");
                var saved = session.PresentedTextViewModel!; saved.SearchText = "第二行"; saved.WordWrap = false; saved.ShowLineNumbers = true; saved.ScrollOffset = 72;
                session.NavigateRecord(-1); await session.Pending; Check(session.Error?.Code == "Unsupported", "Adjacent unsupported record remains reachable from text preview");
                session.NavigateRecord(1); await session.Pending; Check(ReferenceEquals(saved, session.PresentedTextViewModel) && !saved.WordWrap && saved.ShowLineNumbers && saved.SearchText == "第二行" && saved.ScrollOffset == 72, "Returning to a text file restores search, wrap, line numbers and scroll state");
            }

            uint clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            window = new PreviewWindow(new[] { FilePreviewTests.Item(large), new ClipItem { Kind = ClipKind.Text, Text = "剪贴板文字保持系统字体" } }, 0, cache) { ShowActivated = false, ShowInTaskbar = false };
            window.Show(); await FilePreviewTests.Idle(); await window.PendingRender; window.UpdateLayout(); double width = window.ActualWidth, height = window.ActualHeight;
            Check(window.TextContent.Text.Length <= TextChunkReader.ChunkCharacters && window.Session.PresentedText?.IsLarge == true, "Real preview window displays only the active text segment");
            window.ToggleTextWrap(); window.ToggleLineNumbers(); Check(window.TextContent.TextWrapping == System.Windows.TextWrapping.Wrap && window.Session.PresentedTextViewModel?.ShowLineNumbers == true, "Real preview toggles wrap and line numbers");
            window.ShowTextSearch(); window.PreviewSearch.Text = "event-42"; Check(window.FindText(false, true) && window.TextContent.SelectionLength == 8, "Ctrl+F search layer selects a matching range");
            foreach (bool dark in new[] { false, true }) { ThemeManager.Apply(new AppSettings { Theme = dark ? "Dark" : "Light" }); await Task.Delay(220); window.UpdateLayout();
                foreach (double dpi in new[] { 1.25, 1.5, 2.0 }) { var capture = new RenderTargetBitmap((int)(width * dpi), (int)(height * dpi), 96 * dpi, 96 * dpi, PixelFormats.Pbgra32); capture.Render(window); Check(window.ActualWidth == width && window.ActualHeight == height, $"Text preview fixed layout at {dpi * 100}% / dark={dark}"); }
            }
            void SendKey(Key key) {
                window.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice, PresentationSource.FromVisual(window)!, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent });
            }
            SendKey(Key.Escape); await FilePreviewTests.Idle(); Check(window.IsVisible && !window.PreviewSearch.IsVisible, "First Esc closes text search without closing preview");
            SendKey(Key.Down); await window.PendingRender; Check(window.RecordIndex == 1 && window.Session.PresentedText?.IsFile == false, "Down changes from text file to adjacent clipboard text without window jump");
            Check(window.ActualWidth == width && window.ActualHeight == height && clipboardSequence == NativeMethods.GetClipboardSequenceNumber(), "Text navigation preserves window size and clipboard contents");
            await window.CloseAndReleaseAsync(); window = null;
            using (var exclusive = new FileStream(large, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(exclusive.CanWrite, "Closing preview releases every text file handle");
        } catch (Exception exception) { error = exception.ToString(); }
        finally { if (window is not null && window.IsVisible) await window.CloseAndReleaseAsync(); ThemeManager.Apply(new AppSettings { Theme = "Light" }); File.WriteAllText(Path.Combine(root, "text-preview-results.json"), JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true })); Application.Current.Shutdown(error is null ? 0 : 1); }

        async Task ExpectError(Func<Task> action, string code, string label) { try { await action(); throw new Exception(label + " did not fail"); } catch (PreviewException preview) { Check(preview.Code == code, label); } }
    }

    private static void WriteEncoded(string path, string text, Encoding encoding)
    {
        byte[] preamble = encoding.GetPreamble(), body = encoding.GetBytes(text); using var stream = File.Create(path); stream.Write(preamble); stream.Write(body);
    }
}
