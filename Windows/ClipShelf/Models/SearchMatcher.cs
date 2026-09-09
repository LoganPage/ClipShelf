using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ClipShelf;

internal static class SearchMatcher
{
    // Weak record keys prevent retaining deleted history. Cache size follows live records rather
    // than evicting every field when a fixed string-count threshold is reached.
    private static readonly ConditionalWeakTable<ClipItem, ItemCache> SearchCache = new();

    public static bool Matches(ClipItem item, string? query) => Prepare(query).Matches(item);

    /// <summary>Filter a stable snapshot in original order. Safe for worker threads.</summary>
    public static List<ClipItem> Filter(IReadOnlyList<ClipItem> items, string? query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(items);
        var prepared = Prepare(query, cancellationToken);
        var result = new List<ClipItem>(Math.Min(items.Count, 128));
        for (int i = 0; i < items.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (prepared.Matches(items[i], cancellationToken)) result.Add(items[i]);
        }
        return result;
    }

    public static PreparedQuery Prepare(string? query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        QueryToken[] tokens = string.IsNullOrWhiteSpace(query) ? [] : query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => Normalize(value, cancellationToken)).Where(value => value.Length > 0)
            .Distinct(StringComparer.Ordinal).Select(value => new QueryToken(value)).ToArray();
        return new PreparedQuery(tokens);
    }

    internal sealed class PreparedQuery
    {
        private readonly QueryToken[] tokens;
        internal PreparedQuery(QueryToken[] tokens) => this.tokens = tokens;

        public bool Matches(ClipItem item, CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(item);
            cancellationToken.ThrowIfCancellationRequested();
            if (tokens.Length == 0) return true;
            var fields = SearchCache.GetValue(item, static _ => new ItemCache()).Snapshot(item);
            foreach (var token in tokens)
            {
                bool matched = false;
                // Search original fields before transliterating a preceding Chinese title or long text.
                foreach (var field in fields)
                {
                    if (token.Matches(field.Basic(cancellationToken), cancellationToken)) { matched = true; break; }
                    if (field.CachedLatin is { } cached)
                        foreach (var form in cached)
                            if (token.Matches(form, cancellationToken)) { matched = true; break; }
                    if (matched) break;
                }
                if (!matched)
                    foreach (var field in fields)
                    {
                        foreach (var form in field.Latin(cancellationToken))
                            if (token.Matches(form, cancellationToken)) { matched = true; break; }
                        if (matched) break;
                    }
                if (!matched) return false;
            }
            return true;
        }
    }

    private sealed class ItemCache
    {
        private readonly object gate = new();
        private string? title, text, source;
        private string[] paths = [];
        private FieldIndex[] fields = [];
        private bool initialized;

        internal FieldIndex[] Snapshot(ClipItem item)
        {
            lock (gate)
            {
                bool same = initialized && title == item.Title && text == item.Text && source == item.SourcePath
                    && paths.Length == (item.FilePaths?.Count ?? 0);
                if (same)
                    for (int i = 0; i < paths.Length; i++)
                        if (paths[i] != item.FilePaths![i]) { same = false; break; }
                if (same) return fields;
                title = item.Title; text = item.Text; source = item.SourcePath;
                paths = item.FilePaths?.ToArray() ?? [];
                fields = new[] { title, text, source }.Concat(paths).Where(value => !string.IsNullOrEmpty(value))
                    .Distinct(StringComparer.Ordinal).Select(value => new FieldIndex(value!)).ToArray();
                initialized = true;
                return fields;
            }
        }
    }

    private sealed class FieldIndex(string source)
    {
        private readonly object gate = new();
        private string? basic;
        private string[]? latin;
        internal string[]? CachedLatin => Volatile.Read(ref latin);

        internal string Basic(CancellationToken token)
        {
            if (Volatile.Read(ref basic) is { } cached) return cached;
            Enter(gate, token);
            try { return basic ??= Normalize(source, token); }
            finally { Monitor.Exit(gate); }
        }

        internal string[] Latin(CancellationToken token)
        {
            if (Volatile.Read(ref latin) is { } cached) return cached;
            Enter(gate, token);
            try
            {
                if (latin is not null) return latin;
                var translated = PinyinTransliterator.ToLatin(source, token);
                var normalized = Normalize(translated, token);
                var initials = new StringBuilder();
                foreach (var word in translated.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                {
                    token.ThrowIfCancellationRequested();
                    var normalizedWord = Normalize(word, token);
                    if (normalizedWord.Length > 0) initials.Append(normalizedWord[0]);
                }
                var original = basic ??= Normalize(source, token);
                var result = new[] { normalized, initials.ToString() }.Where(form => form.Length > 0 && form != original)
                    .Distinct(StringComparer.Ordinal).ToArray();
                token.ThrowIfCancellationRequested();
                Volatile.Write(ref latin, result);
                return result;
            }
            finally { Monitor.Exit(gate); }
        }
    }

    private static void Enter(object gate, CancellationToken token)
    {
        while (!Monitor.TryEnter(gate, 15)) token.ThrowIfCancellationRequested();
        if (!token.IsCancellationRequested) return;
        Monitor.Exit(gate);
        token.ThrowIfCancellationRequested();
    }

    private static string Normalize(string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        bool ascii = true;
        for (int i = 0; i < value.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            if (value[i] > 127) { ascii = false; break; }
        }
        string decomposed = ascii ? value : value.Normalize(NormalizationForm.FormKD);
        var result = new StringBuilder(decomposed.Length);
        for (int i = 0; i < decomposed.Length; i++)
        {
            if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
            char character = decomposed[i];
            if (character is >= 'A' and <= 'Z') result.Append((char)(character + ('a' - 'A')));
            else if (character is >= 'a' and <= 'z' or >= '0' and <= '9') result.Append(character);
            else if (character > 127 && char.IsLetterOrDigit(character)) result.Append(char.ToLowerInvariant(character));
        }
        return result.ToString();
    }

    internal sealed class QueryToken
    {
        private readonly string query;
        private readonly Dictionary<char, UInt128>? masks;
        private readonly int distanceLimit;
        internal QueryToken(string query)
        {
            this.query = query;
            distanceLimit = query.Length <= 5 ? 1 : 2;
            if (query.Length is >= 3 and <= 128)
            {
                masks = new Dictionary<char, UInt128>();
                for (int i = 0; i < query.Length; i++)
                {
                    masks.TryGetValue(query[i], out var existing);
                    masks[query[i]] = existing | (UInt128.One << i);
                }
            }
        }

        internal bool Matches(string value, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (value.Contains(query, StringComparison.Ordinal)) return true;
            int position = 0;
            for (int i = 0; i < value.Length; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                if (value[i] == query[position] && ++position == query.Length) return true;
            }
            if (query.Length < 3 || value.Length < query.Length) return false;
            return masks is null ? FuzzyLong(value, token) : FuzzyBits(value, token);
        }

        private bool FuzzyBits(string value, CancellationToken token)
        {
            // Myers recurrence with free text prefixes: the same substring edit-distance as
            // the former DP, scanning the complete field without truncating content.
            UInt128 positive = UInt128.MaxValue, negative = UInt128.Zero;
            UInt128 last = UInt128.One << (query.Length - 1);
            int distance = query.Length;
            for (int offset = 0; offset < value.Length; offset++)
            {
                if ((offset & 255) == 0) token.ThrowIfCancellationRequested();
                masks!.TryGetValue(value[offset], out UInt128 equal);
                UInt128 vertical = equal | negative;
                UInt128 horizontal = unchecked((((equal & positive) + positive) ^ positive) | equal);
                UInt128 plus = negative | ~(horizontal | positive);
                UInt128 minus = positive & horizontal;
                if ((plus & last) != 0) distance++;
                else if ((minus & last) != 0) distance--;
                // No injected low 1 bit: DP[0, textOffset] stays zero for substring matching.
                plus <<= 1; minus <<= 1;
                positive = minus | ~(vertical | plus);
                negative = plus & vertical;
                if (distance <= distanceLimit) return true;
            }
            return false;
        }

        private bool FuzzyLong(string value, CancellationToken token)
        {
            int[] previous = ArrayPool<int>.Shared.Rent(query.Length + 1);
            int[] current = ArrayPool<int>.Shared.Rent(query.Length + 1);
            try
            {
                for (int i = 0; i <= query.Length; i++) previous[i] = i;
                for (int offset = 0; offset < value.Length; offset++)
                {
                    if ((offset & 31) == 0) token.ThrowIfCancellationRequested();
                    current[0] = 0;
                    for (int i = 1; i <= query.Length; i++)
                    {
                        if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                        current[i] = Math.Min(Math.Min(previous[i] + 1, current[i - 1] + 1),
                            previous[i - 1] + (query[i - 1] == value[offset] ? 0 : 1));
                    }
                    if (current[query.Length] <= distanceLimit) return true;
                    (previous, current) = (current, previous);
                }
                return false;
            }
            finally { ArrayPool<int>.Shared.Return(previous); ArrayPool<int>.Shared.Return(current); }
        }
    }
}

internal static class PinyinTransliterator
{
    private static int available = OperatingSystem.IsWindows() ? 1 : 0;
    private static readonly object PoolGate = new();
    private static readonly Stack<TransliteratorHandle> Pool = new();
    private const int PoolCapacity = 4;

    public static string ToLatin(string value) => ToLatin(value, CancellationToken.None);

    public static string ToLatin(string value, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (Volatile.Read(ref available) == 0 || !value.Any(c => c >= 0x2e80)) return value;
        TransliteratorHandle? transliterator = null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            lock (PoolGate) if (Pool.Count > 0) transliterator = Pool.Pop();
            int error = 0;
            if (transliterator is null)
            {
                const string transform = "Any-Latin; Latin-ASCII";
                var pointer = utrans_openU(transform, transform.Length, 0, null, 0, IntPtr.Zero, ref error);
                if (error > 0 || pointer == IntPtr.Zero) return value;
                transliterator = new TransliteratorHandle(pointer);
            }
            token.ThrowIfCancellationRequested();
            int capacity = checked(value.Length * 12 + 64);
            buffer = Marshal.AllocHGlobal(checked(capacity * 2));
            Marshal.Copy(value.ToCharArray(), 0, buffer, value.Length);
            int length = value.Length, limit = length;
            utrans_transUChars(transliterator, buffer, ref length, capacity, 0, ref limit, ref error);
            // Preserve full phrase/context handling: cancel before/after the atomic native call.
            token.ThrowIfCancellationRequested();
            return error <= 0 && length >= 0 && length < capacity
                ? Marshal.PtrToStringUni(buffer, length) ?? value : value;
        }
        catch (DllNotFoundException) { Volatile.Write(ref available, 0); return value; }
        catch (EntryPointNotFoundException) { Volatile.Write(ref available, 0); return value; }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
            if (transliterator is not null)
            {
                lock (PoolGate)
                    if (Pool.Count < PoolCapacity) { Pool.Push(transliterator); transliterator = null; }
                transliterator?.Dispose();
            }
        }
    }

    private sealed class TransliteratorHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal TransliteratorHandle(IntPtr pointer) : base(true) => SetHandle(pointer);
        protected override bool ReleaseHandle() { utrans_close(handle); return true; }
    }

    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Unicode)]
    private static extern IntPtr utrans_openU(string id, int length, int direction, string? rules, int rulesLength, IntPtr parseError, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void utrans_transUChars(TransliteratorHandle transliterator, IntPtr text, ref int length, int capacity, int start, ref int limit, ref int error);
    [DllImport("icu.dll", CallingConvention = CallingConvention.Cdecl)]
    private static extern void utrans_close(IntPtr transliterator);
}
