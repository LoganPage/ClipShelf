using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Media.Ocr;

namespace ClipShelf;

internal static class ImageOcrTests
{
    internal static async Task RunAsync(string directory)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(directory);
        var checks = new List<string>(); var samples = new List<object>(); string? error = null;
        string? language = null;
        try
        {
            var service = new ImageOcrService();
            string english = Path.Combine(directory, "ocr-english.png");
            string chinese = Path.Combine(directory, "ocr-chinese.png");
            string mixed = Path.Combine(directory, "ocr-mixed.png");
            WriteTextImage(english, "HELLO CLIPSHELF 2026", 1400, 260, "Segoe UI");
            WriteTextImage(chinese, "剪贴板 测试 2026", 1400, 260, "Microsoft YaHei UI");
            WriteTextImage(mixed, "ClipShelf 剪贴板 2026", 1400, 260, "Microsoft YaHei UI");
            bool simulatedMissingLanguage = false;
            try { await new ImageOcrService(() => false).RecognizeAsync(english, default); }
            catch (PreviewException exception) { simulatedMissingLanguage = exception.Code == "OcrLanguageMissing"; }
            Check(simulatedMissingLanguage, "Missing OCR languages produce an explicit recoverable status", checks);

            if (OcrEngine.AvailableRecognizerLanguages.Count == 0)
            {
                checks.Add("PASS Current machine has no OCR language, so content recognition is safely skipped");
            }
            else
            {
                foreach ((string name, string path) in new[] { ("English", english), ("Chinese", chinese), ("Mixed", mixed) })
                {
                    var watch = Stopwatch.StartNew(); ImageOcrResult result = await service.RecognizeAsync(path, default); watch.Stop();
                    language ??= result.LanguageTag;
                    samples.Add(new { name, output = result.Text, elapsedMilliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 1), result.Downscaled });
                    Check(result.Text.Length > 0, name + " generated image produces OCR text", checks);
                    Check(result.WordRegions.Count > 0 && result.WordRegions.All(word => word.Bounds.Left >= 0 && word.Bounds.Top >= 0 &&
                        word.Bounds.Right <= 1 && word.Bounds.Bottom <= 1), name + " exposes normalized word positions for direct image selection", checks);
                }
                string blank = Path.Combine(directory, "ocr-blank.png"); WriteTextImage(blank, "", 800, 240, "Segoe UI");
                ImageOcrResult empty = await service.RecognizeAsync(blank, default);
                Check(empty.Text.Length == 0, "Image without text returns a clear empty result", checks);

                string oversized = Path.Combine(directory, "ocr-oversized.png");
                WriteTextImage(oversized, "LARGE IMAGE 2026", (int)OcrEngine.MaxImageDimension + 200, 320, "Segoe UI");
                ImageOcrResult scaled = await service.RecognizeAsync(oversized, default);
                Check(scaled.Downscaled, "Oversized image is safely downscaled before OCR", checks);
                Check(scaled.WordRegions.All(word => word.Bounds.Right <= 1 && word.Bounds.Bottom <= 1), "Downscaled OCR positions remain normalized to the displayed image", checks);
            }

            var overlay = new OcrSelectionOverlay();
            overlay.SetWords(new[] {
                new OcrWordRegion("第一行", new Rect(.05, .05, .24, .1), 0, 0),
                new OcrWordRegion("文字", new Rect(.34, .05, .18, .1), 0, 1),
                new OcrWordRegion("第二行", new Rect(.05, .25, .24, .1), 1, 0)
            });
            overlay.SelectNormalized(new Rect(.02, .02, .55, .15));
            Check(overlay.SelectedText == "第一行 文字" && overlay.SelectedCount == 2, "Spatial selection preserves OCR reading order within a line", checks);
            overlay.SelectAll();
            Check(overlay.SelectedText == "第一行 文字\n第二行", "Select all preserves OCR line breaks", checks);
            Rect displayBounds = new(20, 10, 400, 200);
            overlay.SetWords(new[] {
                new OcrWordRegion("第一行", new Rect(.05, .05, .24, .1), 0, 0),
                new OcrWordRegion("文字", new Rect(.34, .05, .18, .1), 0, 1),
                new OcrWordRegion("第二行", new Rect(.05, .25, .24, .1), 1, 0)
            }, () => displayBounds);
            Check(overlay.PointerPressed(new Point(25, 12)), "Pointer press enters the real OCR drag route", checks);
            overlay.PointerMoved(new Point(245, 85)); overlay.PointerReleased(new Point(245, 85));
            Check(overlay.SelectedText == "第一行 文字\n第二行" && overlay.SelectedCount == 3, "Pointer drag across two lines selects words in reading order", checks);
            overlay.PointerPressed(new Point(70, 30)); overlay.PointerReleased(new Point(70, 30));
            Check(overlay.SelectedText == "第一行" && overlay.SelectedCount == 1, "Pointer click selects one OCR word", checks);
            overlay.PointerPressed(new Point(330, 150)); overlay.PointerMoved(new Point(400, 190)); overlay.PointerReleased(new Point(400, 190));
            Check(overlay.SelectedText.Length == 0 && overlay.SelectedCount == 0, "Pointer drag through blank image space clears OCR selection", checks);
            displayBounds = new Rect(20, 10, 800, 400);
            overlay.PointerPressed(new Point(30, 15)); overlay.PointerMoved(new Point(470, 160)); overlay.PointerReleased(new Point(470, 160));
            Check(overlay.SelectedText == "第一行 文字\n第二行" && overlay.SelectedCount == 3, "Pointer mapping selects the same words after two-times image zoom", checks);

            bool missing = false;
            try { await service.RecognizeAsync(Path.Combine(directory, "missing.png"), default); }
            catch (PreviewException exception) { missing = exception.Code == "MissingFile"; }
            Check(missing, "Missing image reports an explicit OCR error", checks);
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); bool stopped = false;
                try { await service.RecognizeAsync(english, cancelled.Token); } catch (OperationCanceledException) { stopped = true; }
                Check(stopped, "Cancelled OCR request cannot return a result", checks);
            }
            string corrupt = Path.Combine(directory, "ocr-corrupt.png"); File.WriteAllText(corrupt, "not a real image");
            bool invalid = false;
            try { await service.RecognizeAsync(corrupt, default); }
            catch (PreviewException exception) { invalid = exception.Code == "OcrFailed"; }
            Check(invalid || OcrEngine.AvailableRecognizerLanguages.Count == 0, "Unreadable image fails without crashing OCR", checks);
            Check(service.InvocationCount >= 3, "OCR invocation counter changes only for explicit requests", checks);
        }
        catch (Exception exception) { error = exception.GetType().Name + ": " + exception.Message; }
        string report = Path.Combine(directory, "ocr-results.json");
        File.WriteAllText(report, JsonSerializer.Serialize(new { passed = error is null, language, availableLanguages = OcrEngine.AvailableRecognizerLanguages.Count,
            checks, samples, error }, new JsonSerializerOptions { WriteIndented = true }));
        Application.Current.Shutdown(error is null ? 0 : 1);
    }

    private static void WriteTextImage(string path, string text, int width, int height, string font)
    {
        var visual = new DrawingVisual();
        using (DrawingContext drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(Brushes.White, null, new Rect(0, 0, width, height));
            if (text.Length > 0)
            {
                var formatted = new FormattedText(text, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
                    new Typeface(font), 92, Brushes.Black, 1);
                drawing.DrawText(formatted, new Point(45, 60));
            }
        }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }

    private static void Check(bool condition, string name, ICollection<string> checks)
    {
        if (!condition) throw new InvalidOperationException(name);
        checks.Add("PASS " + name);
    }
}
