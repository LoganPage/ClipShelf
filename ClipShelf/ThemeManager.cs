using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Animation;
using Microsoft.Win32;

namespace ClipShelf;

public static class ThemeManager
{
    private static readonly BitmapSource?[] icons = new BitmapSource?[4];
    public static bool IsDark { get; private set; }
    private static bool initialized;
    private static bool animate;
    public static readonly string[] Presets = { "coolGrayBlue", "appleBlue", "neutralGray", "lavenderGray", "tealGray", "Custom" };
    public static readonly string[] PresetNames = { "冷灰蓝 · 稳重耐看", "Apple 蓝 · 交互明显", "中性灰 · 极简克制", "淡紫灰 · 柔和有感", "青灰 · 清爽工具感", "自定义颜色" };
    public static void Apply(AppSettings settings)
    {
        settings.AppIcon = 2;
        using var reg = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        bool nextDark = settings.Theme == "Dark" || (settings.Theme == "System" && reg?.GetValue("AppsUseLightTheme") is int value && value == 0);
        animate = initialized && SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        IsDark = nextDark;
        var colors = new Dictionary<string, (string light, string dark)> {
            ["ScrollThumbBrush"]=("#C4C7CC","#626872"),["ScrollThumbHoverBrush"]=("#858B94","#959DA9"),["ScrollThumbDragBrush"]=("#555D68","#CBD1D9"),
            ["BackgroundBrush"]=("#F6F7F9","#131519"),["SurfaceBrush"]=("#FFFFFF","#202329"),["SearchBrush"]=("#FFFFFF","#1B1D21"),["SettingsBrush"]=("#FFFFFF","#1E2127"),
            ["TextBrush"]=("#242830","#EEF0F5"),["MutedBrush"]=("#737985","#A4ABB7"),["BorderBrush"]=("#D6D9E0","#383D47"),["ActionBrush"]=("#E9EBEF","#404552"),["HoverBrush"]=("#DCE2EA","#515A69"),["AccentBrush"]=("#2870DB","#82B9FF"),
            ["MenuSurfaceBrush"]=("#FAFAFA","#282828"),["MenuBorderBrush"]=("#DCDCDC","#454545"),["MenuHoverBrush"]=("#EDEDED","#383838"),["MenuDividerBrush"]=("#E5E5E5","#404040"),
            ["SettingsCanvasBrush"]=("#F3F3F3","#202020"),["SettingsCardBrush"]=("#FFFFFF","#2B2B2B"),["SettingsStrokeBrush"]=("#E3E3E3","#3B3B3B"),["SettingsControlBrush"]=("#FFFFFF","#353535"),["SettingsHoverBrush"]=("#F4F4F4","#404040"),["SettingsSecondaryBrush"]=("#606060","#BFBFBF"),["ToggleOffBrush"]=("#EFEFEF","#303030"),["ToggleKnobBrush"]=("#606060","#D5D5D5"),["OnAccentBrush"]=("#FFFFFF","#18283A"),
            ["TextTileBrush"]=("#E6F2FA","#1F3842"),["TextTileForeground"]=("#1A526B","#7AD6E6"),["FileTileBrush"]=("#E8F5EB","#213D2E"),["FileTileForeground"]=("#1F6133","#73DBAB"),["ImageTileBrush"]=("#FAF0E0","#423324"),["ImageTileForeground"]=("#8F4D0A","#F5C26B")
        };
        foreach (var pair in colors) Set(pair.Key, IsDark ? pair.Value.dark : pair.Value.light);
        string[] light = { "#E8EFF7", "#E5F1FF", "#ECEDEF", "#EEEAF7", "#E6F1F1" };
        string[] dark = { "#283544", "#173A5E", "#343538", "#373146", "#243B3D" };
        int preset = Array.IndexOf(Presets, settings.SelectionPreset);
        Set("SelectedBrush", preset >= 0 && preset < 5 ? (IsDark ? dark[preset] : light[preset]) : settings.SelectionPreset == "Custom" ? settings.SelectionColor : (IsDark ? dark[0] : light[0]));
        initialized = true;
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
