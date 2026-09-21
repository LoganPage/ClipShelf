using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

public static class DemoContent
{
    public static void Add(HistoryStore store)
    {
        store.Settings.WatchScreenshots = false;
        store.Add(new ClipItem { Kind = ClipKind.Text, Title = "https://github.com/LoganPage/ClipShelf", Text = "https://github.com/LoganPage/ClipShelf", CreatedAt = DateTimeOffset.Now.AddMinutes(-10) });
        store.Add(new ClipItem { Kind = ClipKind.File, Title = "项目资料", FilePaths = new() { Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) }, CreatedAt = DateTimeOffset.Now.AddMinutes(-8) });
        var image = new ClipItem { Kind = ClipKind.Image, Title = "截图 2026-09-08 18.42.16.png", CreatedAt = DateTimeOffset.Now.AddMinutes(-6) };
        image.ImagePath = store.ImagePathFor(image.Id); image.SourcePath = image.ImagePath;
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen()) {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 234, 244)), null, new Rect(0, 0, 640, 360));
            dc.DrawRoundedRectangle(Brushes.White, null, new Rect(56, 40, 528, 280), 20, 20);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(40, 112, 219)), null, new Rect(82, 70, 42, 42), 10, 10);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(225, 231, 239)), null, new Rect(146, 80, 205, 12));
            for (int i = 0; i < 4; i++) dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb((byte)(238 - i * 2), 242, 247)), null, new Rect(82, 140 + i * 35, 470 - i * 45, 18), 5, 5);
        }
        var bitmap = new RenderTargetBitmap(640, 360, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using (var stream = File.Create(image.ImagePath)) encoder.Save(stream); store.Add(image);
        store.Add(new ClipItem { Kind = ClipKind.Text, Title = "让每一次复制，都有迹可循。\n文字、文件和截图，随时取用。", Text = "让每一次复制，都有迹可循。\n文字、文件和截图，随时取用。", CreatedAt = DateTimeOffset.Now.AddMinutes(-4) });
        store.Add(new ClipItem { Kind = ClipKind.Text, Title = "今天的待办：完善 Windows 版 ClipShelf，保留熟悉的 Mac 体验。", Text = "今天的待办：完善 Windows 版 ClipShelf，保留熟悉的 Mac 体验。", CreatedAt = DateTimeOffset.Now.AddMinutes(-2) });
        var pinned = new ClipItem { Kind = ClipKind.Text, Title = "ClipShelf · 你的剪贴板，井井有条。", Text = "ClipShelf · 你的剪贴板，井井有条。", IsPinned = true }; store.Add(pinned);
    }
}
