using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

// Plain data: parsers do not create WPF controls or retain dispatcher-bound objects.
internal record SceneRun(string Text, string Font = "Segoe UI", double Size = 16, bool Bold = false, bool Italic = false, bool Underline = false, string Color = "#202020");
internal record SceneBox(double X, double Y, double Width, double Height);
internal abstract record SceneNode(SceneBox Box, double Rotation = 0);
internal record SceneText(SceneBox Bounds, SceneRun[] Runs, string Align = "left", double LineHeight = 0, double RotationAngle = 0) : SceneNode(Bounds, RotationAngle);
internal record SceneShape(SceneBox Bounds, string Kind, string Fill, string Stroke = "#00000000", double Thickness = 1, double Angle = 0, string? GradientEnd = null) : SceneNode(Bounds, Angle);
internal record SceneImage(SceneBox Bounds, byte[] Bytes, double Angle = 0, double CropLeft = 0, double CropTop = 0, double CropRight = 0, double CropBottom = 0) : SceneNode(Bounds, Angle);
internal record ScenePlaceholder(SceneBox Bounds, string Kind) : SceneNode(Bounds);
internal sealed record PreviewScene(double Width, double Height, string Background, SceneNode[] Nodes, string[] Warnings, string? GradientEnd = null);

internal static class PreviewSceneRenderer
{
    internal static void ValidateImageSize(int width, int height) { if (width < 1 || height < 1 || (long)width * height > 40_000_000) throw SafeOoxmlPackage.Limit(); }
    internal static Brush Brush(string value) { try { var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)); brush.Freeze(); return brush; } catch { return Brushes.Transparent; } }
    internal static FormattedText Text(SceneRun[] runs, double width, string align = "left", double lineHeight = 0) {
        using var timing = PreviewMetrics.Measure("text-measure");
        string content = string.Concat(runs.Select(r => r.Text));
        var text = new FormattedText(content.Length == 0 ? " " : content, CultureInfo.GetCultureInfo("zh-CN"), FlowDirection.LeftToRight,
            new Typeface("Segoe UI, Microsoft YaHei UI"), 16, Brushes.Black, 1) { MaxTextWidth = Math.Max(1, width) };
        text.TextAlignment = align switch { "center" or "ctr" => TextAlignment.Center, "right" or "r" => TextAlignment.Right, "both" or "just" => TextAlignment.Justify, _ => TextAlignment.Left };
        if (lineHeight > 0) text.LineHeight = Math.Clamp(lineHeight, 4, 300);
        int position = 0;
        foreach (var run in runs) {
            int length = run.Text.Length; if (length == 0) continue;
            string font = run.Font.Length <= 100 && run.Font.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') ? run.Font : "Segoe UI";
            text.SetFontFamily(new FontFamily(font + ", Microsoft YaHei UI, Segoe UI"), position, length);
            text.SetFontSize(Math.Clamp(run.Size, 4, 200), position, length); text.SetForegroundBrush(Brush(run.Color), position, length);
            if (run.Bold) text.SetFontWeight(FontWeights.Bold, position, length);
            if (run.Italic) text.SetFontStyle(FontStyles.Italic, position, length);
            if (run.Underline) text.SetTextDecorations(TextDecorations.Underline, position, length);
            position += length;
        }
        return text;
    }
    internal static BitmapSource Decode(byte[] bytes, int width, CancellationToken token) {
        using var timing = PreviewMetrics.Measure("image-decode"); token.ThrowIfCancellationRequested();
        if (bytes.Length > SafeOoxmlPackage.EntryLimit) throw SafeOoxmlPackage.Limit();
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None); var frame = decoder.Frames[0];
        ValidateImageSize(frame.PixelWidth, frame.PixelHeight);
        double scale = Math.Min(1, Math.Min(Math.Clamp(width, 1, 4096) / (double)Math.Max(1, frame.PixelWidth), Math.Min(5600d / Math.Max(1, frame.PixelHeight), Math.Sqrt(14_000_000d / ((double)frame.PixelWidth * frame.PixelHeight)))));
        stream.Position = 0; var bitmap = new BitmapImage(); bitmap.BeginInit(); bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.DecodePixelWidth = Math.Max(1, (int)(frame.PixelWidth * scale)); bitmap.StreamSource = stream; bitmap.EndInit(); bitmap.Freeze(); token.ThrowIfCancellationRequested(); return bitmap;
    }
    internal static BitmapSource Render(PreviewScene scene, int width, CancellationToken token, HashSet<string>? warnings = null) {
        using var timing = PreviewMetrics.Measure("scene-draw");
        double scale = Math.Min(Math.Clamp(width, 240, 4096) / scene.Width, Math.Min(5600 / scene.Height, Math.Sqrt(14_000_000 / (scene.Width * scene.Height))));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) {
            dc.PushTransform(new ScaleTransform(scale, scale)); dc.PushClip(new RectangleGeometry(new Rect(0, 0, scene.Width, scene.Height)));
            Brush bg = scene.GradientEnd is null ? Brush(scene.Background) : new LinearGradientBrush(((SolidColorBrush)Brush(scene.Background)).Color, ((SolidColorBrush)Brush(scene.GradientEnd)).Color, 90);
            dc.DrawRectangle(bg, null, new Rect(0, 0, scene.Width, scene.Height));
            foreach (var node in scene.Nodes) {
                token.ThrowIfCancellationRequested(); var b = node.Box; var rect = new Rect(b.X, b.Y, Math.Max(1, b.Width), Math.Max(1, b.Height));
                dc.PushTransform(new RotateTransform(node.Rotation, b.X + b.Width / 2, b.Y + b.Height / 2));
                dc.PushClip(new RectangleGeometry(rect));
                try {
                    switch (node) {
                        case SceneText text: dc.DrawText(Text(text.Runs, b.Width, text.Align, text.LineHeight), new Point(b.X, b.Y)); break;
                        case SceneShape shape:
                            Brush fill = shape.GradientEnd is null ? Brush(shape.Fill) : new LinearGradientBrush(((SolidColorBrush)Brush(shape.Fill)).Color, ((SolidColorBrush)Brush(shape.GradientEnd)).Color, 90);
                            var pen = new Pen(Brush(shape.Stroke), Math.Clamp(shape.Thickness, 0, 30));
                            if (shape.Kind == "ellipse") dc.DrawEllipse(fill, pen, new Point(b.X + b.Width / 2, b.Y + b.Height / 2), b.Width / 2, b.Height / 2);
                            else if (shape.Kind == "line") dc.DrawLine(pen, rect.TopLeft, rect.BottomRight);
                            else if (shape.Kind == "triangle") { var geometry = new StreamGeometry(); using (var g = geometry.Open()) { g.BeginFigure(new Point(b.X + b.Width / 2, b.Y), true, true); g.LineTo(rect.BottomRight, true, false); g.LineTo(rect.BottomLeft, true, false); } dc.DrawGeometry(fill, pen, geometry); }
                            else dc.DrawRoundedRectangle(fill, pen, rect, shape.Kind == "roundRect" ? 8 : 0, shape.Kind == "roundRect" ? 8 : 0); break;
                        case SceneImage image:
                            var bitmap = Decode(image.Bytes, Math.Max(1, (int)(b.Width * scale)), token);
                            double cw = Math.Max(.01, 1 - image.CropLeft - image.CropRight), ch = Math.Max(.01, 1 - image.CropTop - image.CropBottom);
                            dc.DrawImage(bitmap, new Rect(b.X - b.Width * image.CropLeft / cw, b.Y - b.Height * image.CropTop / ch, b.Width / cw, b.Height / ch)); break;
                        case ScenePlaceholder placeholder: DrawPlaceholder(dc, rect, placeholder.Kind); break;
                    }
                } catch (OperationCanceledException) { throw; }
                catch (Exception e) { warnings?.Add(e is PreviewException failure ? failure.Code : "对象绘制降级"); DrawPlaceholder(dc, rect, "对象无法显示"); }
                dc.Pop(); dc.Pop();
            }
            dc.Pop(); dc.Pop();
        }
        var result = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(scene.Width * scale)), Math.Max(1, (int)Math.Ceiling(scene.Height * scale)), 96, 96, PixelFormats.Pbgra32);
        result.Render(visual); result.Freeze(); return result;
    }
    private static void DrawPlaceholder(DrawingContext dc, Rect rect, string kind) {
        dc.DrawRectangle(Brush("#EEF0F3"), new Pen(Brush("#C2C8D0"), 1), rect);
        dc.DrawText(Text(new[] { new SceneRun("[" + kind + "]", Size: 12, Color: "#647080") }, Math.Max(1, rect.Width - 12)), new Point(rect.X + 6, rect.Y + 6));
    }
}
