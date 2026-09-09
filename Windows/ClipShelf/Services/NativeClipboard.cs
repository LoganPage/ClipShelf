using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ClipShelf;

internal static class NativeClipboard
{
    internal static readonly uint PngFormat = NativeMethods.RegisterClipboardFormat("PNG");
    private static readonly uint ExcludedFormat = NativeMethods.RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private static readonly uint HistoryFormat = NativeMethods.RegisterClipboardFormat("CanIncludeInClipboardHistory");

    internal static bool IsExcluded(nint owner)
    {
        if (NativeMethods.IsClipboardFormatAvailable(ExcludedFormat)) return true;
        var historyFlag = ReadBytes(owner, HistoryFormat);
        return historyFlag is { Length: >= 4 } && BitConverter.ToInt32(historyFlag, 0) == 0;
    }

    internal static byte[]? ReadBytes(nint owner, uint format)
    {
        if (!NativeMethods.IsClipboardFormatAvailable(format)) return null;
        if (!NativeMethods.OpenClipboard(owner)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var handle = NativeMethods.GetClipboardData(format);
            if (handle == 0) return null;
            var length = checked((int)NativeMethods.GlobalSize(handle));
            if (length == 0) return null;
            var pointer = NativeMethods.GlobalLock(handle);
            if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var bytes = new byte[length];
                Marshal.Copy(pointer, bytes, 0, length);
                return bytes;
            }
            finally { NativeMethods.GlobalUnlock(handle); }
        }
        finally { NativeMethods.CloseClipboard(); }
    }

    internal static BitmapSource? ReadDibImage(nint owner)
    {
        var dib = ReadBytes(owner, NativeMethods.CfDibV5) ?? ReadBytes(owner, NativeMethods.CfDib);
        if (dib is null || dib.Length < 40) return null;
        var headerSize = BitConverter.ToInt32(dib, 0);
        if (headerSize < 40 || headerSize > dib.Length) return null;
        var width = BitConverter.ToInt32(dib, 4);
        var signedHeight = BitConverter.ToInt32(dib, 8);
        if (width <= 0 || signedHeight == 0 || signedHeight == int.MinValue) return null;
        var height = Math.Abs(signedHeight);
        var bits = BitConverter.ToUInt16(dib, 14);
        var compression = BitConverter.ToUInt32(dib, 16);
        var colors = BitConverter.ToUInt32(dib, 32);
        var paletteCount = colors != 0 ? colors : bits <= 8 ? 1u << bits : 0;
        var extraMasks = headerSize == 40 ? compression == 3 ? 12 : compression == 6 ? 16 : 0 : 0;
        var offset64 = (long)headerSize + extraMasks + paletteCount * 4L;
        if (offset64 >= dib.Length) return null;
        var offset = (int)offset64;

        // WPF's generic Clipboard.GetImage can discard alpha. Honor explicit BGRA masks ourselves.
        if (headerSize >= 56 && bits == 32 && compression is 3 or 6 &&
            BitConverter.ToUInt32(dib, 40) == 0x00ff0000 && BitConverter.ToUInt32(dib, 44) == 0x0000ff00 &&
            BitConverter.ToUInt32(dib, 48) == 0x000000ff && BitConverter.ToUInt32(dib, 52) == 0xff000000)
        {
            var required = (long)width * 4 * height;
            if (required > dib.Length - offset || required > int.MaxValue) return null;
            var stride = width * 4;
            var pixels = new byte[(int)required];
            for (var row = 0; row < height; row++)
            {
                var sourceRow = signedHeight < 0 ? row : height - row - 1;
                Buffer.BlockCopy(dib, offset + sourceRow * stride, pixels, row * stride, stride);
            }
            var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            image.Freeze();
            return image;
        }

        // DIB is a BMP without its 14-byte file header; WIC handles palette and legacy formats.
        using var stream = new MemoryStream(checked(dib.Length + 14));
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
        {
            writer.Write((ushort)0x4d42); writer.Write(dib.Length + 14);
            writer.Write(0); writer.Write(offset + 14); writer.Write(dib);
        }
        return DecodeImage(stream.ToArray());
    }

    internal static Dictionary<uint, byte[]> Text(string text) => new()
    {
        [NativeMethods.CfUnicodeText] = Encoding.Unicode.GetBytes(text + '\0')
    };

    internal static Dictionary<uint, byte[]> Files(IEnumerable<string> paths)
    {
        var names = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.Unicode, true);
        writer.Write(20); // DROPFILES header size / start of double-null-terminated names.
        writer.Write(0); writer.Write(0); writer.Write(0); writer.Write(1);
        writer.Write(Encoding.Unicode.GetBytes(string.Join('\0', names) + "\0\0"));
        return new Dictionary<uint, byte[]> { [NativeMethods.CfHDrop] = stream.ToArray() };
    }

    internal static Dictionary<uint, byte[]> Image(byte[] png, BitmapSource image) => new()
    {
        [PngFormat] = png,
        [NativeMethods.CfDibV5] = CreateDib(image, true),
        [NativeMethods.CfDib] = CreateDib(image, false)
    };

    // DIBV5 carries explicit alpha masks; the 24-bit DIB is a fallback for older apps.
    private static byte[] CreateDib(BitmapSource source, bool alpha)
    {
        var format = alpha ? PixelFormats.Bgra32 : PixelFormats.Bgr24;
        var converted = source.Format == format ? source : new FormatConvertedBitmap(source, format, null, 0);
        var width = converted.PixelWidth;
        var height = converted.PixelHeight;
        var bits = alpha ? 32 : 24;
        var stride = checked(((width * bits + 31) / 32) * 4);
        var pixelLength = checked(stride * height);
        var headerSize = alpha ? 124 : 40;
        // Fill the final clipboard payload directly, avoiding two additional full-size pixel copies.
        var bytes = new byte[checked(headerSize + pixelLength)];
        using var stream = new MemoryStream(bytes, true);
        using var writer = new BinaryWriter(stream);
        writer.Write(headerSize); writer.Write(width); writer.Write(-height);
        writer.Write((ushort)1); writer.Write((ushort)bits);
        writer.Write(alpha ? 3u : 0u); // BI_BITFIELDS / BI_RGB
        writer.Write(pixelLength); writer.Write(3780); writer.Write(3780);
        writer.Write(0); writer.Write(0);
        if (alpha)
        {
            writer.Write(0x00ff0000u); writer.Write(0x0000ff00u); writer.Write(0x000000ffu); writer.Write(0xff000000u);
            writer.Write(0x73524742u); // LCS_sRGB
            writer.Write(new byte[48]); // CIEXYZTRIPLE endpoints and gamma values.
            writer.Write(4u); writer.Write(0); writer.Write(0); writer.Write(0);
        }
        converted.CopyPixels(bytes, stride, headerSize);
        return bytes;
    }

    internal static void Write(nint owner, IReadOnlyDictionary<uint, byte[]> formats)
    {
        // Allocate all payloads before touching the clipboard so allocation errors keep its old contents.
        var allocated = new Dictionary<uint, nint>();
        try
        {
            foreach (var (format, bytes) in formats)
            {
                var handle = NativeMethods.GlobalAlloc(0x0002, (nuint)bytes.Length);
                if (handle == 0) throw new OutOfMemoryException();
                allocated.Add(format, handle);
                var pointer = NativeMethods.GlobalLock(handle);
                if (pointer == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
                try { Marshal.Copy(bytes, 0, pointer, bytes.Length); }
                finally { NativeMethods.GlobalUnlock(handle); }
            }
            if (!NativeMethods.OpenClipboard(owner)) throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                if (!NativeMethods.EmptyClipboard()) throw new Win32Exception(Marshal.GetLastWin32Error());
                foreach (var format in formats.Keys)
                {
                    if (NativeMethods.SetClipboardData(format, allocated[format]) == 0)
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    allocated[format] = 0; // Ownership has transferred to Windows.
                }
            }
            finally { NativeMethods.CloseClipboard(); }
        }
        finally
        {
            foreach (var handle in allocated.Values)
                if (handle != 0) NativeMethods.GlobalFree(handle);
        }
    }

    internal static BitmapSource DecodeImage(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var image = decoder.Frames[0];
        image.Freeze();
        return image;
    }

    internal static byte[] EncodePng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        // Frozen decoded frames can still retain a dispatcher-bound decoder for their metadata.
        // Supplying metadata explicitly avoids BitmapFrame.Create(source)'s implicit cross-thread lookup.
        encoder.Frames.Add(BitmapFrame.Create(image, null, null, null));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }
}
