using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using ClipShelf;

internal static class SearchRegressionTests
{
    private static int checks;
    internal static async Task RunAsync()
    {
        var random = new Random(468179);
        const string alphabet = "abcdefxyz";
        for (int trial = 0; trial < 16000; trial++)
        {
            int length = trial < 8000 ? random.Next(1, 20) : random.Next(60, 129);
            string query = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            string value = new(Enumerable.Range(0, random.Next(Math.Max(1, length - 4), length + 100)).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            if (trial % 3 == 0)
            {
                char[] near = query.ToCharArray();
                near[random.Next(near.Length)] = alphabet[random.Next(alphabet.Length)];
                value = "zzzz" + new string(near) + "xxxx";
            }
            bool actual = new SearchMatcher.QueryToken(query).Matches(value, CancellationToken.None);
            Assert(actual == ReferenceToken(query, value), "Bit-vector search agrees with substring edit-distance at length " + length);
        }
        Console.WriteLine("PASS 16000 randomized substring/subsequence/fuzzy equivalence cases (1–128 characters)");
        foreach (var (item, query) in new[]
        {
            (new ClipItem { Title = "Résumé ＣＡＦÉ", Text = "Project notes" }, "resume cafe"),
            (new ClipItem { Title = "剪贴板历史", Text = "保持原始文字" }, "jiantieban"),
            (new ClipItem { Title = "剪贴板历史", Text = "保持原始文字" }, "jtbls"),
            (new ClipItem { Title = "中文", Text = "窗口交互保持流畅，中文搜索和编辑器 accessibility clipboard workbench." }, "bianjiqi"),
            (new ClipItem { Title = "title", FilePaths = ["C:\\工作文件\\项目复盘.pdf"] }, "xmfp"),
            (new ClipItem { Title = "截图", SourcePath = "C:\\Screenshots\\tailmarker.png" }, "tailmarker"),
            (new ClipItem { Text = "accessibility" }, "accesibility"),
            (new ClipItem { Text = "abcd efgh" }, "ad gh"),
            (new ClipItem { Text = "ordinary text" }, "qqqqvvvv"),
            (new ClipItem { Text = "重庆银行行长，重新打开。ＡＢＣ中文 test-punctuation" }, "cq"),
            (new ClipItem { Text = "école notes\n下一行" }, "en"),
            (new ClipItem { Text = "anything" }, " + / ")
        })
        {
            Assert(SearchMatcher.Matches(item, query) == Reference(item, query), "Full pinyin/normalization parity: " + query);
            Assert(SearchMatcher.Filter([item], query).Count == (Reference(item, query) ? 1 : 0), "Filter and Matches agree: " + query);
        }
        Console.WriteLine("PASS Pinyin, initials, accent/width/case folding, paths, multiple tokens and punctuation parity");
        var longItem = new ClipItem { Title = "长文字", Text = new string('a', 300000) + "完整末尾标记 tailrarexyz" };
        Assert(SearchMatcher.Matches(longItem, "完整末尾标记"), "Entire long text, including its tail, remains searchable");
        var mutable = new ClipItem { Text = "oldtext", FilePaths = ["oldpath"] };
        Assert(SearchMatcher.Matches(mutable, "oldtext"), "Initial mutable item index");
        mutable.Text = "newtext"; mutable.FilePaths[0] = "freshlocation";
        Assert(SearchMatcher.Matches(mutable, "freshlocation") && !SearchMatcher.Matches(mutable, "qqqoldzzz"), "Changed text and file paths rebuild the index");
        string longQuery = string.Concat(Enumerable.Repeat("abcdefghijklmnopqrstuvw", 7));
        string nearLong = longQuery[..78] + "0" + longQuery[79..];
        Assert(new SearchMatcher.QueryToken(longQuery).Matches(nearLong, CancellationToken.None), "Queries longer than 128 retain fuzzy matching via pooled DP");
        var records = Enumerable.Range(0, 1000).Select(i => new ClipItem { Title = "测试 " + i, Text = "editor clipboard alpha beta " + i }).ToList();
        var expected = SearchMatcher.Filter(records, "clipboard").Select(item => item.Id).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int repeat = 0; repeat < 5; repeat++)
                if (!SearchMatcher.Filter(records, "clipboard").Select(item => item.Id).SequenceEqual(expected))
                    throw new InvalidOperationException("Concurrent cache access changed result order.");
        })));
        Assert(SearchMatcher.Filter(records, "editor").Count == 1000, "Over 400 records retain stable cache behavior");
        var chineseRecords = Enumerable.Range(0, 80).Select(i => new ClipItem { Title = "剪贴板历史 " + i, Text = "编辑器记录 " + i }).ToList();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            for (int repeat = 0; repeat < 3; repeat++)
                if (SearchMatcher.Filter(chineseRecords, "jiantieban").Count != chineseRecords.Count)
                    throw new InvalidOperationException("Concurrent first-time pinyin indexing lost a result.");
        })));
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            try { SearchMatcher.Filter(records, "editor", cancelled.Token); throw new InvalidOperationException("Pre-cancelled search ran."); }
            catch (OperationCanceledException) { checks++; }
        }
        var cancellationRecords = Enumerable.Range(0, 30).Select(i => new ClipItem { Text = new string('a', 800000) + i }).ToList();
        using (var cancellation = new CancellationTokenSource())
        {
            var pending = Task.Run(() => SearchMatcher.Filter(cancellationRecords, "vvvvqqqq", cancellation.Token));
            await Task.Delay(10);
            var watch = Stopwatch.StartNew(); cancellation.Cancel();
            try { await pending; throw new InvalidOperationException("Running search ignored cancellation."); }
            catch (OperationCanceledException) { }
            Assert(watch.ElapsedMilliseconds < 1000, "Cancellation returns within a second");
            Console.WriteLine("PASS Concurrent readers, result order and active cancellation in " + watch.Elapsed.TotalMilliseconds.ToString("F2") + " ms");
        }
        Assert(SearchMatcher.Matches(cancellationRecords[0], "aaaa"), "Cancellation does not poison cached fields");
        var weak = IndexTemporaryRecord();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert(!weak.TryGetTarget(out _), "Cache does not retain deleted records");
        Console.WriteLine("PASS Full-text tail, index invalidation, long queries, cancelled-cache retry and weak-cache lifetime");
        Console.WriteLine("Search checks passed: " + checks);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference<ClipItem> IndexTemporaryRecord()
    {
        var item = new ClipItem { Text = "temporary uncached record " + Guid.NewGuid() };
        SearchMatcher.Matches(item, "temporary");
        return new WeakReference<ClipItem>(item);
    }

    private static bool Reference(ClipItem item, string query)
    {
        var tokens = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(Normalize).Where(value => value.Length > 0).ToArray();
        var fields = new[] { item.Title, item.Text, item.SourcePath }.Concat(item.FilePaths).Where(value => !string.IsNullOrEmpty(value)).ToArray();
        return tokens.All(token => fields.Any(field =>
        {
            string latin = PinyinTransliterator.ToLatin(field!);
            string initials = string.Concat(latin.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(Normalize).Where(value => value.Length > 0).Select(value => value[0]));
            return new[] { Normalize(field!), Normalize(latin), initials }.Any(form => ReferenceToken(token, form));
        }));
    }

    private static string Normalize(string value) => new(value.Normalize(NormalizationForm.FormKD)
        .Where(c => char.IsLetterOrDigit(c) && CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Select(char.ToLowerInvariant).ToArray());

    private static bool ReferenceToken(string query, string value)
    {
        if (value.Contains(query, StringComparison.Ordinal)) return true;
        int index = 0;
        foreach (char c in value) if (c == query[index] && ++index == query.Length) return true;
        if (query.Length < 3 || value.Length < query.Length) return false;
        int limit = query.Length <= 5 ? 1 : 2;
        int[] previous = Enumerable.Range(0, query.Length + 1).ToArray(), current = new int[query.Length + 1];
        for (int offset = 0; offset < value.Length; offset++)
        {
            current[0] = 0;
            for (int i = 1; i <= query.Length; i++) current[i] = Math.Min(Math.Min(previous[i] + 1, current[i - 1] + 1), previous[i - 1] + (query[i - 1] == value[offset] ? 0 : 1));
            if (current[query.Length] <= limit) return true;
            (previous, current) = (current, previous);
        }
        return false;
    }
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Search regression: " + message);
        checks++;
    }
}
