using System.Diagnostics;
using System.Text.Json;
using ClipShelf;

var root = Path.Combine(Path.GetTempPath(), "ClipShelf-storage-benchmark-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
if (args.Contains("--tests"))
{
    var results = StorageTests.Run(root).ToList();
    foreach (string testMethod in new[] { "RunDeferredAsync", "RunUndoAsync" })
    {
        var asynchronousTests = typeof(StorageTests).GetMethod(testMethod);
        if (asynchronousTests is null) continue;
        var task = (Task<IReadOnlyList<string>>)asynchronousTests.Invoke(null, [root])!;
        results.AddRange(await task);
    }
    EmitReport(JsonSerializer.Serialize(new { passed = true, checks = results }, new JsonSerializerOptions { WriteIndented = true }), args);
    return;
}

bool deferred = args.Contains("--deferred");
var trials = new List<object>();
for (int trial = 0; trial < 3; trial++)
{
    string directory = Path.Combine(root, "trial-" + trial);
    var constructor = typeof(HistoryStore).GetConstructor([typeof(string), typeof(bool)]);
    var store = constructor is not null
        ? (HistoryStore)constructor.Invoke([directory, deferred])
        : new HistoryStore(directory);
    for (int i = 0; i < 100; i++) store.Add(Item(i));
    Flush(store);
    var add = new List<double>();
    var pin = new List<double>();
    var total = Stopwatch.StartNew();
    for (int i = 100; i < 220; i++)
    {
        var clock = Stopwatch.StartNew();
        store.Add(Item(i));
        add.Add(clock.Elapsed.TotalMilliseconds);
    }
    Guid id = store.Items[0].Id;
    for (int i = 0; i < 120; i++)
    {
        var clock = Stopwatch.StartNew();
        store.TogglePinned([id]);
        pin.Add(clock.Elapsed.TotalMilliseconds);
    }
    double callerMilliseconds = total.Elapsed.TotalMilliseconds;
    var flushClock = Stopwatch.StartNew();
    Flush(store);
    double flushMilliseconds = flushClock.Elapsed.TotalMilliseconds;
    var reopened = new HistoryStore(directory);
    if (!store.Items.Select(item => (item.Id, item.IsPinned)).SequenceEqual(reopened.Items.Select(item => (item.Id, item.IsPinned))))
        throw new InvalidOperationException("Durable final state does not match in-memory history.");
    trials.Add(new
    {
        trial,
        add = Summarize(add),
        togglePinned = Summarize(pin),
        callerMilliseconds,
        flushMilliseconds,
        historyBytes = new FileInfo(store.HistoryPath).Length,
        itemCount = reopened.Items.Count
    });
}
string report = JsonSerializer.Serialize(new
{
    mode = deferred ? "deferred" : "synchronous",
    scenario = "100 records, 2048-character text repeated in title; 120 unique adds then 120 pin toggles; 3 trials; includes full disk flush before final-state validation",
    startedAt = DateTimeOffset.Now,
    trials
}, new JsonSerializerOptions { WriteIndented = true });
EmitReport(report, args);

static void EmitReport(string report, string[] arguments)
{
    int outputIndex = Array.IndexOf(arguments, "--output");
    if (outputIndex >= 0 && outputIndex + 1 < arguments.Length)
    {
        string output = Path.GetFullPath(arguments[outputIndex + 1]);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, report);
    }
    Console.WriteLine(report);
}

static ClipItem Item(int index)
{
    string content = $"Clipboard record {index:D4}: " + new string('x', 2048);
    return new ClipItem { Text = content, Title = content, CreatedAt = DateTimeOffset.UtcNow.AddTicks(index) };
}

static void Flush(HistoryStore store) => typeof(HistoryStore).GetMethod("Flush")?.Invoke(store, null);

static object Summarize(List<double> milliseconds)
{
    milliseconds.Sort();
    return new
    {
        operations = milliseconds.Count,
        totalMilliseconds = milliseconds.Sum(),
        meanMilliseconds = milliseconds.Average(),
        p50Milliseconds = milliseconds[milliseconds.Count / 2],
        p95Milliseconds = milliseconds[(int)Math.Floor(milliseconds.Count * 0.95)],
        maxMilliseconds = milliseconds[^1]
    };
}
