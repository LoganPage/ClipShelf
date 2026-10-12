using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace ClipShelf;

public static class ThemeManager
{
    private static readonly BitmapSource?[] icons = new BitmapSource?[4];
    public const string ClassicStyle = "Classic", ModernStyle = "Modern";
    public static bool IsDark { get; private set; }
    // Independent from light/dark: always "Classic" or "Modern".
    public static string VisualStyle { get; private set; } = ClassicStyle;
    private static bool initialized;
    private static bool animate;
    public static string NormalizeVisualStyle(string? value) =>
        string.Equals(value, ModernStyle, StringComparison.OrdinalIgnoreCase) ? ModernStyle : ClassicStyle;
    public static void Apply(AppSettings settings)
    {
        settings.AppIcon = 2;
        using var reg = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool nextDark = settings.Theme == "Dark" || (settings.Theme == "System" && reg?.GetValue("AppsUseLightTheme") is int value && value == 0);
        animate = initialized && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        IsDark = nextDark;
        VisualStyle = NormalizeVisualStyle(settings.VisualStyle);
        bool modern = VisualStyle == ModernStyle;
        var colors = new Dictionary<string, (string light, string dark)> {
            ["ScrollThumbBrush"]=("#C4C7CC","#626872"),["ScrollThumbHoverBrush"]=("#858B94","#959DA9"),["ScrollThumbDragBrush"]=("#555D68","#CBD1D9"),
            ["BackgroundBrush"]=("#F6F7F9","#131519"),["SurfaceBrush"]=("#FFFFFF","#202329"),["SearchBrush"]=("#FFFFFF","#1B1D21"),["SettingsBrush"]=("#FFFFFF","#1E2127"),
            ["TextBrush"]=("#242830","#EEF0F5"),["MutedBrush"]=("#737985","#A4ABB7"),["BorderBrush"]=("#D6D9E0","#383D47"),["ActionBrush"]=("#E9EBEF","#404552"),["HoverBrush"]=("#DCE2EA","#515A69"),["AccentBrush"]=("#2870DB","#82B9FF"),
            ["MenuSurfaceBrush"]=("#FAFAFA","#282828"),["MenuBorderBrush"]=("#D6D9E0","#383D47"),["MenuHoverBrush"]=("#EDEDED","#383838"),["MenuDividerBrush"]=("#D6D9E0","#383D47"),
            ["SettingsCanvasBrush"]=("#F3F3F3","#202020"),["SettingsCardBrush"]=("#FFFFFF","#2B2B2B"),["SettingsStrokeBrush"]=("#D6D9E0","#383D47"),["SettingsControlBrush"]=("#FFFFFF","#353535"),["SettingsHoverBrush"]=("#F4F4F4","#404040"),["SettingsSecondaryBrush"]=("#606060","#BFBFBF"),["ToggleOffBrush"]=("#EFEFEF","#303030"),["ToggleKnobBrush"]=("#606060","#D5D5D5"),["OnAccentBrush"]=("#FFFFFF","#18283A"),
            ["TextTileBrush"]=("#E6F2FA","#1F3842"),["TextTileForeground"]=("#1A526B","#7AD6E6"),["FileTileBrush"]=("#E8F5EB","#213D2E"),["FileTileForeground"]=("#1F6133","#73DBAB"),["ImageTileBrush"]=("#FAF0E0","#423324"),["ImageTileForeground"]=("#8F4D0A","#F5C26B"),
            // Style tokens: Classic values reproduce the pre-style appearance exactly.
            ["TitleBarBrush"]=("#00FFFFFF","#00FFFFFF"),["BrandStripBrush"]=("#00FFFFFF","#00FFFFFF"),["BrandTextBrush"]=("#242830","#EEF0F5"),["SectionTitleBrush"]=("#242830","#EEF0F5"),
            ["FocusLightActiveBrush"]=("#32B96B","#32B96B"),["FocusLightInactiveBrush"]=("#92989F","#92989F"),["ScrimBrush"]=("#40000000","#40000000"),
            ["ToastBrush"]=("#242830","#EEF0F5"),["ToastTextBrush"]=("#FFFFFF","#202329"),["ToastBorderBrush"]=("#00FFFFFF","#00FFFFFF"),
            ["SearchFocusBorderBrush"]=("#D6D9E0","#383D47"),["FilterBarBrush"]=("#E9EBEF","#404552"),["FilterSelectionBrush"]=("#FFFFFF","#1B1D21"),["FilterSelectionBorderBrush"]=("#D6D9E0","#383D47"),["FilterSelectedTextBrush"]=("#2870DB","#82B9FF"),
            ["ToolbarPillBrush"]=("#00FFFFFF","#00FFFFFF"),["RowHoverBrush"]=("#00FFFFFF","#00FFFFFF"),["RowSelectedBrush"]=("#E8EFF7","#283544"),["RowAccentBrush"]=("#2870DB","#82B9FF"),["RowFocusMarkerBrush"]=("#737985","#A4ABB7"),
            ["SelectionBadgeBrush"]=("#E8EFF7","#283544"),["SelectionBadgeTextBrush"]=("#242830","#EEF0F5"),["EmptyIconBrush"]=("#737985","#A4ABB7"),["EmptyIconTileBrush"]=("#00FFFFFF","#00FFFFFF"),
            ["PreviewCardBorderBrush"]=("#00FFFFFF","#00FFFFFF")
        };
        if (modern) foreach (var pair in ModernPalette) colors[pair.Key] = pair.Value;
        foreach (var pair in colors) Set(pair.Key, IsDark ? pair.Value.dark : pair.Value.light);
        // Classic keeps the focus-only row marker bound to the shared muted brush instance.
        if (!modern) Application.Current.Resources["RowFocusMarkerBrush"] = Application.Current.Resources["MutedBrush"];
        // Selection colour is now a stable design token. Legacy settings remain readable,
        // but no longer alter list, badge or settings-choice selection surfaces.
        Set("SelectedBrush", modern ? (IsDark ? "#1D3A35" : "#DDF1EC") : (IsDark ? "#283544" : "#E8EFF7"));
        ApplyGeometry(modern);
        initialized = true;
    }
    // Modern: ClipShelf teal on cool neutral surfaces. Every accent text pair stays above 4.5:1.
    private static readonly Dictionary<string, (string light, string dark)> ModernPalette = new() {
        ["ScrollThumbBrush"]=("#B9C6C3","#4B5B57"),["ScrollThumbHoverBrush"]=("#7C8D89","#7F928D"),["ScrollThumbDragBrush"]=("#4C5C58","#B3C4BF"),
        ["BackgroundBrush"]=("#EEF2F1","#0E1413"),["SurfaceBrush"]=("#FFFFFF","#18201F"),["SearchBrush"]=("#FFFFFF","#131A19"),["SettingsBrush"]=("#FFFFFF","#172120"),
        ["TextBrush"]=("#17211F","#E8F1EF"),["MutedBrush"]=("#5F6E6B","#9DB0AC"),["BorderBrush"]=("#D3DCDA","#2C3A38"),["ActionBrush"]=("#E3EAE8","#26312F"),["HoverBrush"]=("#D4E2DE","#34443F"),["AccentBrush"]=("#0E7C6B","#4FD1B5"),
        ["MenuSurfaceBrush"]=("#FBFDFC","#1D2625"),["MenuBorderBrush"]=("#CBD6D3","#34423F"),["MenuHoverBrush"]=("#E3F1EE","#2B3A37"),["MenuDividerBrush"]=("#DDE5E3","#2F3D3A"),
        ["SettingsCanvasBrush"]=("#EEF2F1","#121918"),["SettingsCardBrush"]=("#FFFFFF","#1C2625"),["SettingsStrokeBrush"]=("#D3DCDA","#2F3E3B"),["SettingsControlBrush"]=("#FFFFFF","#25302E"),["SettingsHoverBrush"]=("#EAF3F1","#2F3D3A"),["SettingsSecondaryBrush"]=("#55635F","#A9BAB6"),["ToggleOffBrush"]=("#E1E8E6","#2B3633"),["ToggleKnobBrush"]=("#55635F","#C5D3CF"),["OnAccentBrush"]=("#FFFFFF","#06241D"),
        ["TextTileBrush"]=("#E1F1ED","#1B3B36"),["TextTileForeground"]=("#14594D","#7FE0CB"),["FileTileBrush"]=("#E6F0F7","#1B3040"),["FileTileForeground"]=("#1F4F73","#8CC4F0"),["ImageTileBrush"]=("#FBEFD9","#40321F"),["ImageTileForeground"]=("#8F4D0A","#F5C26B"),
        ["TitleBarBrush"]=("#E6EDEB","#121A19"),["BrandStripBrush"]=("#0E7C6B","#4FD1B5"),["BrandTextBrush"]=("#0B6557","#6FE0C8"),["SectionTitleBrush"]=("#0B6557","#6FE0C8"),
        ["ScrimBrush"]=("#4D000000","#8C000000"),
        ["ToastBrush"]=("#17211F","#E8F1EF"),["ToastTextBrush"]=("#F4FAF8","#0E1413"),["ToastBorderBrush"]=("#2B3A37","#C5D3CF"),
        ["SearchFocusBorderBrush"]=("#0E7C6B","#4FD1B5"),["FilterBarBrush"]=("#E3EAE8","#212B29"),["FilterSelectionBrush"]=("#DDF1EC","#1D3A35"),["FilterSelectionBorderBrush"]=("#0E7C6B","#4FD1B5"),["FilterSelectedTextBrush"]=("#0B6557","#6FE0C8"),
        ["ToolbarPillBrush"]=("#E3EAE8","#212B29"),["RowHoverBrush"]=("#EDF5F3","#1E2928"),["RowSelectedBrush"]=("#D3EEE7","#1F433C"),["RowAccentBrush"]=("#0E7C6B","#4FD1B5"),["RowFocusMarkerBrush"]=("#0E7C6B","#4FD1B5"),
        ["SelectionBadgeBrush"]=("#0E7C6B","#4FD1B5"),["SelectionBadgeTextBrush"]=("#FFFFFF","#06241D"),["EmptyIconBrush"]=("#0E7C6B","#4FD1B5"),["EmptyIconTileBrush"]=("#DDF1EC","#1D3A35"),
        ["PreviewCardBorderBrush"]=("#D3DCDA","#2C3A38")
    };
    private static void ApplyGeometry(bool modern)
    {
        // Classic values are the original shared geometry; Modern is rounder with a visible row selection edge.
        SetResource("ControlCorners", new CornerRadius(modern ? 8 : 4));
        SetResource("OverlayCorners", new CornerRadius(modern ? 12 : 8));
        SetResource("CardCorners", new CornerRadius(modern ? 12 : 8));
        SetResource("SearchCorners", new CornerRadius(modern ? 12 : 4));
        SetResource("ToastCorners", new CornerRadius(modern ? 12 : 8));
        SetResource("PreviewCardCorners", new CornerRadius(modern ? 16 : 12));
        SetResource("RowCorners", new CornerRadius(modern ? 10 : 0));
        SetResource("RowSurfaceMargin", modern ? new Thickness(6, 2, 6, 2) : new Thickness(0));
        SetResource("RowAccentMargin", modern ? new Thickness(8, 20, 0, 20) : new Thickness(0));
        SetResource("RowAccentWidth", modern ? 3d : 0d);
        // Rest-state row actions recede in Modern, but never under high contrast.
        SetResource("RowActionRestOpacity", modern && !SystemParameters.HighContrast ? 0.55 : 1d);
        SetResource("BrandStripHeight", modern ? 2d : 0d);
        SetResource("EmptyIconPadding", modern ? new Thickness(20) : new Thickness(0));
        SetResource("EmptyIconOpacity", modern ? 1d : 0.5);
        SetResource("ToastBorderThickness", modern ? new Thickness(1) : new Thickness(0));
        SetResource("PreviewCardBorderThickness", modern ? new Thickness(1) : new Thickness(0));
    }
    private static void SetResource(string name, object value)
    {
        var resources = Application.Current.Resources;
        if (!Equals(resources[name], value)) resources[name] = value;
    }
    private static void Set(string name, string color)
    {
        try {
            var parsed = (Color)ColorConverter.ConvertFromString(color);
            ThemeTransition.Set(name, parsed, animate);
        } catch (FormatException) { }
    }
    public static BitmapSource Icon(int choice)
    {
        int index = 1; // One identity across the window, shell and tray.
        if (icons[index] is BitmapSource icon) return icon;
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad;
        source.UriSource = new Uri($"pack://application:,,,/Assets/AppIcon{index + 1}.png");
        source.EndInit(); source.Freeze();
        // The shared Mac artwork has a 38 px transparent safe area on its 512 px
        // canvas. Windows already supplies the taskbar spacing: retaining both
        // makes the tile about 15% smaller than neighboring app icons. Keep a
        // 2 px antialiasing guard while giving every choice the same optical size.
        // generate-icons.ps1 uses this same 36/440 crop for the installed ICO.
        var cropped = new CroppedBitmap(source, new Int32Rect(36, 36, 440, 440));
        cropped.Freeze();
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(cropped, new Rect(0, 0, 256, 256));
        var rendered = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32);
        rendered.Render(visual); rendered.Freeze();
        icons[index] = rendered; return rendered;
    }
}

public sealed class ClipImageConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage> cache = new(StringComparer.OrdinalIgnoreCase);
    public object? Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path)) return null;
        if (cache.TryGetValue(path, out var cached)) return cached;
        try {
            var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.DecodePixelWidth = 156; image.UriSource = new Uri(Path.GetFullPath(path)); image.EndInit(); image.Freeze();
            if (cache.Count > 150) cache.Clear();
            cache[path] = image; return image;
        } catch (Exception ex) when (ex is IOException or NotSupportedException or System.IO.FileFormatException) { return null; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
}
