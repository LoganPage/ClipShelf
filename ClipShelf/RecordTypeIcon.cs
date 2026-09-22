using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Cached, resolution-independent Fluent file symbols; never asks the shell or reads a file while rendering.</summary>
public sealed class RecordTypeIcon : FrameworkElement
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(ClipItem), typeof(RecordTypeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(nameof(Palette), typeof(Brush), typeof(RecordTypeIcon), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    private static readonly Dictionary<(string Type, bool Dark), DrawingGroup> cache = new();
    public ClipItem? Item { get => (ClipItem?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    public Brush? Palette { get => (Brush?)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }
    internal static string Category(ClipItem item)
    {
        if (item.Kind == ClipKind.Text) return "Text";
        if (item.Kind == ClipKind.Image) return "Image";
        if (item.IsDirectory) return "Folder";
        if (item.FilePaths.Count != 1) return "File";
        return Path.GetExtension(item.FilePaths[0]).ToLowerInvariant() switch {
            ".pdf" => "PDF", ".xls" or ".xlsx" or ".xlsm" or ".xlsb" or ".csv" or ".ods" => "Excel",
            ".doc" or ".docx" or ".docm" or ".rtf" or ".odt" => "Word",
            ".ppt" or ".pptx" or ".pptm" or ".odp" => "PowerPoint",
            ".zip" or ".7z" or ".rar" or ".gz" or ".tar" => "Archive",
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".tif" or ".tiff" or ".webp" or ".heic" => "Image",
            ".txt" or ".md" or ".log" or ".json" or ".xml" or ".csv" or ".ini" or ".yaml" or ".yml" or
            ".cs" or ".cpp" or ".h" or ".py" or ".js" or ".ts" or ".html" or ".css" or ".sql" or ".ps1" or ".bat" or ".cmd" => "Document",
            _ => "File"
        };
    }
    internal static string Label(ClipItem item) => Category(item) switch {
        "PDF" => "PDF 文档", "Excel" => "电子表格", "Word" => "文字文档", "PowerPoint" => "演示文稿",
        "Archive" => "压缩文件", "Image" => "图片文件", "Document" => "文本文件", "Folder" => "文件夹", _ => "文件"
    };
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc); if (Item is null) return;
        dc.PushTransform(new ScaleTransform(ActualWidth / 52, ActualHeight / 40));
        dc.DrawDrawing(GetDrawing(Category(Item), ThemeManager.IsDark)); dc.Pop();
    }
    private static DrawingGroup GetDrawing(string type, bool dark)
    {
        if (cache.TryGetValue((type, dark), out var saved)) return saved;
        var (geometry, light, bright, badge) = type switch {
            "PDF" => ("document_pdf", "#B93434", "#FF9797", ""),
            "Excel" => ("document_table", "#187447", "#7FDAA5", "X"),
            "Word" => ("document_text", "#2463B4", "#8FBDF8", "W"),
            "PowerPoint" => ("slide_text", "#B54B28", "#FFB192", "P"),
            "Archive" => ("folder_zip", "#7955A5", "#C8A9ED", ""),
            "Folder" => ("folder", "#996400", "#F4CB71", ""),
            "Image" => ("image", "#326C8E", "#8DCBEA", ""),
            "Text" => ("text_description", "#496B87", "#ADCBE3", ""),
            "Document" => ("document_text", "#526C83", "#B0C6DB", ""),
            _ => ("document", "#637083", "#BAC4D2", "")
        };
        var color = (Color)ColorConverter.ConvertFromString(dark ? bright : light);
        var fill = new SolidColorBrush(color);
        var background = new SolidColorBrush(Color.FromArgb(dark ? (byte)28 : (byte)18, color.R, color.G, color.B));
        var group = new DrawingGroup(); using (var dc = group.Open()) {
            dc.DrawRoundedRectangle(background, null, new Rect(0, 0, 52, 40), 4, 4);
            dc.PushTransform(new TranslateTransform(11, 5)); dc.PushTransform(new ScaleTransform(1.25, 1.25));
            dc.DrawGeometry(fill, null, (Geometry)Application.Current.FindResource("Record_" + geometry)); dc.Pop(); dc.Pop();
            if (badge.Length > 0) {
                dc.DrawRoundedRectangle(new SolidColorBrush((Color)ColorConverter.ConvertFromString(light)), null, new Rect(4, 20, 17, 15), 2, 2);
                var text = new FormattedText(badge, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal), 10, Brushes.White, 1);
                dc.DrawText(text, new Point(12.5 - text.Width / 2, 20));
            }
        }
        group.Freeze(); cache[(type, dark)] = group; return group;
    }
}
