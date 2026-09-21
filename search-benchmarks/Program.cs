using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using ClipShelf;

var mode = args.FirstOrDefault() ?? "current";
if (mode == "tests") { await SearchRegressionTests.RunAsync(); return; }
var reports = new List<object>();
var filterMethod = typeof(SearchMatcher).GetMethod("Filter", BindingFlags.Public | BindingFlags.Static);
var filter = filterMethod?.CreateDelegate<Func<IReadOnlyList<ClipItem>, string?, CancellationToken, List<ClipItem>>>();
if (mode == "baseline" && filter is not null) throw new InvalidOperationException("baseline.json was captured before optimization; choose another report name to preserve that evidence.");
Console.WriteLine("Search benchmark " + mode + " / " + (filter is null ? "legacy per-item query" : "prepared cancellable Filter"));
foreach (int count in new[] { 100, 1000 })
{
    var records = Enumerable.Range(0, count).Select(i => new ClipItem
    {
        Title = $"项目记录 {i:D4} · 剪贴板历史",
        Text = $"item {i:D4} " + string.Concat(Enumerable.Repeat("窗口交互保持流畅，中文搜索和编辑器 accessibility clipboard workbench. ", 36)) +
            (i % 11 == 0 ? " tailneedle 完整末尾标记" : " ordinary ending"),
        SourcePath = i % 5 == 0 ? $"C:\\截图\\项目 {i:D4}.png" : null
    }).ToList();
    foreach (string query in new[] { "tailneedle", "bianjiqi", "jtbls", "accesibility", "zzzzzzz" })
    {
        for (int pass = 0; pass < 2; pass++)
        {
            long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            var found = filter is null ? records.Where(x => x.Search(query)).ToList() : filter(records, query, CancellationToken.None);
            watch.Stop();
            long allocated = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
            var report = new { Count = count, Query = query, Pass = pass, Milliseconds = Math.Round(watch.Elapsed.TotalMilliseconds, 2), AllocatedBytes = allocated, Matches = found.Count };
            reports.Add(report);
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
    }
}
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", mode + ".json"));
File.WriteAllText(output, JsonSerializer.Serialize(reports, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Report: " + output);
