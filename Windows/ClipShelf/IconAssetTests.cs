using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class IconAssetTests
{
    internal static void Verify(Action<bool, string> check)
    {
        var designs = new List<byte[]>();
        for (int choice = 1; choice <= 4; choice++)
        {
            var icon = ThemeManager.Icon(choice);
            check(icon.IsFrozen && icon.PixelWidth == 256 && icon.PixelHeight == 256 &&
                ReferenceEquals(icon, ThemeManager.Icon(choice)), $"icon {choice} is a cached frozen 256 px Windows asset");
            var pixels = new byte[256 * 256 * 4];
            icon.CopyPixels(pixels, 256 * 4, 0);
            int minX = 256, minY = 256, maxX = -1, maxY = -1;
            for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                    if (pixels[(y * 256 + x) * 4 + 3] >= 16)
                    { minX = Math.Min(minX, x); minY = Math.Min(minY, y); maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y); }
            check(maxX - minX + 1 >= 252 && maxY - minY + 1 >= 252,
                $"icon {choice} rounded-square tile fills at least 98 percent of the Windows canvas");
            check(pixels[3] == 0 && pixels[(255 * 256 + 255) * 4 + 3] == 0 &&
                pixels[(128 * 256 + 128) * 4 + 3] == 255, $"icon {choice} retains transparent rounded corners and opaque artwork");
            check(designs.All(previous => !previous.SequenceEqual(pixels)), $"icon {choice} retains its distinct original design");
            designs.Add(pixels);
        }
        check(ReferenceEquals(ThemeManager.Icon(0), ThemeManager.Icon(1)) &&
            ReferenceEquals(ThemeManager.Icon(5), ThemeManager.Icon(4)), "invalid icon choices retain existing safe clamping");

        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "ClipShelf.ico");
        using var stream = File.OpenRead(path);
        using var reader = new BinaryReader(stream);
        check(reader.ReadUInt16() == 0 && reader.ReadUInt16() == 1, "installed shell asset has a valid ICO header");
        int[] sizes = { 16, 20, 24, 32, 40, 48, 64, 96, 128, 256 };
        check(reader.ReadUInt16() == sizes.Length, "shell icon provides all ten native Windows DPI sizes");
        for (int i = 0; i < sizes.Length; i++)
        {
            stream.Position = 6 + i * 16;
            int width = reader.ReadByte(); if (width == 0) width = 256;
            int height = reader.ReadByte(); if (height == 0) height = 256;
            reader.ReadUInt16();
            int planes = reader.ReadUInt16(), depth = reader.ReadUInt16();
            int length = checked((int)reader.ReadUInt32()), offset = checked((int)reader.ReadUInt32());
            stream.Position = offset;
            using var frameStream = new MemoryStream(reader.ReadBytes(length));
            var frame = BitmapDecoder.Create(frameStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad).Frames[0];
            check(width == sizes[i] && height == sizes[i] && planes == 1 && depth == 32 &&
                frame.PixelWidth == sizes[i] && frame.PixelHeight == sizes[i], $"ICO {sizes[i]} px directory and payload dimensions agree");
        }
        using var native = new System.Drawing.Icon(path, 32, 32);
        check(native.Width == 32 && native.Height == 32, "Windows native icon loader selects the true 32 px frame");
    }
}
