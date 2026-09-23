using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClipShelf;

internal sealed record TextEncodingInfo(Encoding Encoding, int PreambleLength, string DisplayName);

internal static class TextEncodingDetector
{
    internal const int SampleLimit = 256 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly UnicodeEncoding StrictUtf16Le = new(false, true, true);
    private static readonly UnicodeEncoding StrictUtf16Be = new(true, true, true);

    static TextEncodingDetector() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    internal static TextEncodingInfo Detect(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Validate(StrictUtf8, 3, "UTF-8 BOM", bytes[3..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Validate(StrictUtf16Le, 2, "UTF-16 LE", bytes[2..]);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Validate(StrictUtf16Be, 2, "UTF-16 BE", bytes[2..]);
        if (bytes.Length == 0) return new(StrictUtf8, 0, "UTF-8");

        // A BOM-less UTF-16 text normally has a strong alternating NUL pattern. Treat
        // unrelated NUL bytes as binary before trying permissive legacy encodings.
        if (LooksLikeUtf16(bytes, out bool bigEndian))
            return Validate(bigEndian ? StrictUtf16Be : StrictUtf16Le, 0, bigEndian ? "UTF-16 BE" : "UTF-16 LE", bytes);
        if (bytes.IndexOf((byte)0) >= 0)
            throw new PreviewException("BinaryText", "该文件并非可读取的纯文本文件。");

        try { return Validate(StrictUtf8, 0, "UTF-8", bytes); }
        catch (PreviewException error) when (error.Code == "EncodingError") { }

        try {
            var gb18030 = Encoding.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
            return Validate(gb18030, 0, "GB18030", bytes);
        } catch (PreviewException) { throw; }
        catch (DecoderFallbackException) { throw EncodingError(); }
        catch (ArgumentException) { throw EncodingError(); }
    }

    private static TextEncodingInfo Validate(Encoding encoding, int preamble, string name, ReadOnlySpan<byte> payload)
    {
        try {
            byte[] sample = payload.ToArray(); char[] chars = new char[encoding.GetMaxCharCount(sample.Length)];
            var decoder = encoding.GetDecoder(); decoder.Convert(sample, 0, sample.Length, chars, 0, chars.Length, false, out _, out int used, out _);
            string text = new(chars, 0, used);
            if (LooksBinary(text)) throw new PreviewException("BinaryText", "该文件并非可读取的纯文本文件。");
            return new(encoding, preamble, name);
        } catch (DecoderFallbackException) { throw EncodingError(); }
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        int pairs = Math.Min(bytes.Length / 2, 4096); if (pairs < 2) return false;
        int evenZero = 0, oddZero = 0;
        for (int i = 0; i < pairs * 2; i += 2) { if (bytes[i] == 0) evenZero++; if (bytes[i + 1] == 0) oddZero++; }
        double even = evenZero / (double)pairs, odd = oddZero / (double)pairs;
        if (even > .35 && odd < .08) { bigEndian = true; return true; }
        if (odd > .35 && even < .08) return true;
        return false;
    }

    private static bool LooksBinary(string text)
    {
        if (text.Length == 0) return false;
        int controls = 0, examined = Math.Min(text.Length, 32768);
        for (int i = 0; i < examined; i++) {
            char c = text[i];
            if (c == '\0') return true;
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t' or '\f' or '\b')) controls++;
        }
        return controls > Math.Max(8, examined / 50);
    }

    private static PreviewException EncodingError() => new("EncodingError", "无法可靠识别文本编码，未显示可能产生乱码的内容。");
}

internal sealed record TextChunk(long StartByte, long EndByte, string Text, bool EndOfFile, int FirstLineNumber)
{
    internal int LineCount => TextLineIndex.CountLines(Text);
}

internal sealed class TextChunkReader
{
    internal const int ChunkCharacters = 128 * 1024;

    internal async Task<TextChunk> ReadAsync(DocumentIdentity identity, TextEncodingInfo encoding, long startByte, int firstLineNumber, CancellationToken token)
    {
        return await Task.Run(async () => {
            token.ThrowIfCancellationRequested();
            var before = new FileInfo(identity.Path);
            if (!before.Exists) throw new FileNotFoundException("Text file disappeared.", identity.Path);
            if (before.Length != identity.Size || before.LastWriteTimeUtc.Ticks != identity.Modified)
                throw new PreviewException("Changed", "文件已发生修改，请重新加载。");
            using var stream = new FileStream(identity.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (startByte < encoding.PreambleLength || startByte > stream.Length) throw new PreviewException("Changed", "文件已发生修改，请重新加载。");
            stream.Position = startByte;
            int byteCapacity = Math.Clamp(encoding.Encoding.GetMaxByteCount(ChunkCharacters) + 8, 64 * 1024, 1024 * 1024);
            byte[] bytes = new byte[byteCapacity];
            int read = 0;
            while (read < bytes.Length) {
                int amount = await stream.ReadAsync(bytes.AsMemory(read, bytes.Length - read), token);
                if (amount == 0) break; read += amount;
            }
            char[] chars = new char[ChunkCharacters];
            var decoder = encoding.Encoding.GetDecoder();
            bool atEnd = startByte + read >= stream.Length;
            try { decoder.Convert(bytes, 0, read, chars, 0, chars.Length, atEnd, out int bytesUsed, out int charsUsed, out _);
                token.ThrowIfCancellationRequested();
                if (read > 0 && bytesUsed == 0) throw new PreviewException("EncodingError", "无法可靠识别文本编码，未显示可能产生乱码的内容。");
                // Keep a CR at a segment boundary for the next read so CRLF is never
                // normalized into two line breaks merely because it crossed a chunk.
                if (!atEnd && charsUsed > 0 && chars[charsUsed - 1] == '\r') { charsUsed--; bytesUsed -= encoding.Encoding.GetByteCount("\r"); }
                string text = TextLineIndex.NormalizeNewlines(new string(chars, 0, charsUsed));
                var after = new FileInfo(identity.Path);
                if (!after.Exists || after.Length != identity.Size || after.LastWriteTimeUtc.Ticks != identity.Modified)
                    throw new PreviewException("Changed", "文件读取期间发生修改，请重新加载。");
                long end = startByte + bytesUsed;
                return new TextChunk(startByte, end, text, end >= identity.Size, firstLineNumber);
            } catch (DecoderFallbackException) { throw new PreviewException("EncodingError", "文本内容与检测到的编码不一致，无法可靠显示。"); }
        }, token);
    }
}

internal sealed class TextLineIndex
{
    internal IReadOnlyList<int> Starts { get; }
    internal TextLineIndex(string text)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        Starts = starts;
    }
    internal static int CountLines(string text) => text.Length == 0 ? 0 : 1 + text.Count(c => c == '\n');
    internal static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    internal string Numbers(int firstLine)
    {
        if (Starts.Count == 0) return "";
        var builder = new StringBuilder(Starts.Count * 4);
        for (int i = 0; i < Starts.Count; i++) { if (i > 0) builder.Append('\n'); builder.Append(firstLine + i); }
        return builder.ToString();
    }
}

internal sealed class TextPreviewDocument
{
    internal DocumentIdentity Identity { get; }
    internal TextEncodingInfo Encoding { get; }
    internal TextChunk Current { get; private set; }
    internal int SegmentIndex { get; private set; }
    internal bool IsLarge => Identity.Size > 512 * 1024 || !Current.EndOfFile || SegmentIndex > 0;
    private readonly List<long> starts = new();
    private readonly List<int> firstLines = new();

    internal TextPreviewDocument(DocumentIdentity identity, TextEncodingInfo encoding, TextChunk first)
    { Identity = identity; Encoding = encoding; Current = first; starts.Add(first.StartByte); firstLines.Add(first.FirstLineNumber); }

    internal async Task<TextChunk> MoveAsync(int direction, TextChunkReader reader, CancellationToken token)
    {
        int target = SegmentIndex + Math.Sign(direction);
        if (direction == 0 || target < 0 || (direction > 0 && Current.EndOfFile)) return Current;
        long start; int firstLine;
        if (target < starts.Count) { start = starts[target]; firstLine = firstLines[target]; }
        else {
            start = Current.EndByte;
            firstLine = Current.FirstLineNumber + Current.Text.Count(c => c == '\n');
            starts.Add(start); firstLines.Add(firstLine);
        }
        var chunk = await reader.ReadAsync(Identity, Encoding, start, firstLine, token);
        SegmentIndex = target; Current = chunk; return chunk;
    }
}

internal sealed class TextFilePreviewProvider(PreviewCacheService cache)
{
    private readonly TextChunkReader reader = new();
    private sealed record TextPreviewSeed(TextEncodingInfo Encoding, TextChunk First);
    internal async Task<TextPreviewDocument> OpenAsync(DocumentIdentity identity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string key = "text-encoding-v1:" + identity.Id;
        if (cache.Model<TextPreviewSeed>(key) is { } warm) { token.ThrowIfCancellationRequested(); return new(identity, warm.Encoding, warm.First); }
        var encoding = cache.Model<TextEncodingInfo>(key);
        if (encoding is null) {
            encoding = await Task.Run(async () => {
                using var stream = new FileStream(identity.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
                byte[] sample = new byte[Math.Min(TextEncodingDetector.SampleLimit, checked((int)Math.Min(identity.Size, int.MaxValue)))];
                int read = 0; while (read < sample.Length) { int amount = await stream.ReadAsync(sample.AsMemory(read), token); if (amount == 0) break; read += amount; }
                return TextEncodingDetector.Detect(sample.AsSpan(0, read));
            }, token);
        }
        var first = await reader.ReadAsync(identity, encoding, encoding.PreambleLength, 1, token);
        cache.Model(key, new TextPreviewSeed(encoding, first), 512 + first.Text.Length * 2L);
        return new(identity, encoding, first);
    }
    internal async Task PrewarmAsync(DocumentIdentity identity, CancellationToken token) => _ = await OpenAsync(identity, token);
    internal Task<TextChunk> MoveAsync(TextPreviewDocument document, int direction, CancellationToken token) => document.MoveAsync(direction, reader, token);
}

internal sealed class TextPreviewViewModel
{
    internal TextPreviewDocument? Document { get; }
    internal TextPreviewResult Result { get; private set; }
    internal bool WordWrap { get; set; } = true;
    internal bool ShowLineNumbers { get; set; }
    internal string SearchText { get; set; } = "";
    internal int MatchStart { get; private set; } = -1;
    internal int MatchLength { get; private set; }
    internal double ScrollOffset { get; set; }
    internal TextLineIndex Lines { get; private set; }

    internal TextPreviewViewModel(TextPreviewResult result, TextPreviewDocument? document = null)
    { Result = result; Document = document; Lines = new(result.Text); WordWrap = !result.Monospace; }

    internal void Apply(TextPreviewResult result) { Result = result; Lines = new(result.Text); MatchStart = -1; MatchLength = 0; ScrollOffset = 0; }
    internal bool Find(bool backwards, bool restart = false)
    {
        if (string.IsNullOrEmpty(SearchText) || Result.Text.Length == 0) { MatchStart = -1; MatchLength = 0; return false; }
        var comparison = StringComparison.CurrentCultureIgnoreCase;
        int found;
        if (backwards) {
            int start = restart || MatchStart <= 0 ? Result.Text.Length - 1 : MatchStart - 1;
            found = Result.Text.LastIndexOf(SearchText, start, comparison);
            if (found < 0 && start < Result.Text.Length - 1) found = Result.Text.LastIndexOf(SearchText, comparison);
        } else {
            int start = restart || MatchStart < 0 ? 0 : Math.Min(Result.Text.Length, MatchStart + Math.Max(1, MatchLength));
            found = Result.Text.IndexOf(SearchText, start, comparison);
            if (found < 0 && start > 0) found = Result.Text.IndexOf(SearchText, comparison);
        }
        MatchStart = found; MatchLength = found < 0 ? 0 : SearchText.Length; return found >= 0;
    }
}
