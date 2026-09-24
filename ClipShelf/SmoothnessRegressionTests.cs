using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;

namespace ClipShelf;

/// <summary>
/// Regression guard for main-list scroll smoothness.
///
/// Runs <see cref="SmoothnessProbe"/> once per child process (the probe is designed as
/// "one process run == one pass"), then asserts that a fractional wheel packet
/// (|delta| &lt; 120) does not spend a whole extra frame budget compared with a whole
/// detent. Pre-fix baseline, measured in <c>artifacts/ab/REPORT-smoothness.md</c>:
/// whole detent 16.2-24.0 ms with 0 stalls, fractional 50.3-59.4 ms with 1-3 stalls.
/// The cause was the first realize of the viewport being paid inside a single
/// synchronous frame on the fractional path.
///
/// This guard is deliberately absolute rather than relative to a previous build: it
/// encodes the post-fix target, so it fails on the pre-fix build and passes once the
/// first-realize cost leaves the interaction path.
/// </summary>
public static class SmoothnessRegressionTests
{
    /// <summary>
    /// Allowance for UI-thread blocking beyond 33 ms. This is the primary criterion: it measures the
    /// UI thread itself rather than the compositor tick, so frame-rate variation does not disturb it.
    /// Measured: whole detent 0 in every batch; fractional 0 in the warm state and 2 in the cold state.
    /// </summary>
    private const int UiBlockBudget = 1;

    /// <summary>
    /// The fractional path must stay within this multiple of the whole-detent callback maximum.
    /// Measured ratio: 2.5-3.1 in the cold state, 0.67-1.31 in the warm state.
    /// </summary>
    private const double RatioBudget = 2.0;

    public static async Task RunAsync(string reportPath)
    {
        var checks = new List<string>();
        var warnings = new List<string>();
        var metrics = new Dictionary<string, double>();
        string? error = null;
        string directory = Path.Combine(Path.GetTempPath(), "ClipShelf-smoothness-regression-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); checks.Add(name); }

            string wholeReport = Path.Combine(directory, "whole.json");
            string fineReport = Path.Combine(directory, "fine.json");
            await RunProbeAsync("--smoothness-test", wholeReport);
            await RunProbeAsync("--smoothness-fine-test", fineReport);

            var whole = ReadSample(wholeReport);
            var fine = ReadSample(fineReport);

            metrics["wholeCallbackMaxMs"] = whole.IntervalMax;
            metrics["wholeUiLatencyMaxMs"] = whole.UiLatencyMax;
            metrics["wholeStalls"] = whole.Stalls;
            metrics["wholeOver33"] = whole.Over33;
            metrics["fineCallbackMaxMs"] = fine.IntervalMax;
            metrics["fineUiLatencyMaxMs"] = fine.UiLatencyMax;
            metrics["fineStalls"] = fine.Stalls;
            metrics["fineOver33"] = fine.Over33;
            metrics["fineToWholeRatio"] = whole.IntervalMax <= 0 ? 0 : fine.IntervalMax / whole.IntervalMax;

            Check(whole.Over33 <= UiBlockBudget, $"whole-detent keeps UI-thread blocking within {UiBlockBudget} (measured {whole.Over33})");
            Check(fine.Over33 <= UiBlockBudget, $"fractional keeps UI-thread blocking within {UiBlockBudget} (measured {fine.Over33})");
            Check(whole.IntervalMax <= 0 || fine.IntervalMax <= whole.IntervalMax * RatioBudget,
                $"fractional callback max stays within {RatioBudget:F1}x the whole-detent max (measured {fine.IntervalMax:F1} ms vs {whole.IntervalMax:F1} ms)");

            if (fine.Stalls > 0)
                warnings.Add($"fractional callback interval exceeded 33 ms {fine.Stalls} time(s) without blocking the UI thread; the compositor tick, not the UI thread, was late");
        }
        catch (Exception exception) { error = exception.ToString(); }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(new
            {
                passed = error is null,
                checks,
                warnings,
                metrics,
                error,
                scope = "Two isolated child-process probe passes over synthetic WPF fixtures (1000 rows, one third image rows); " +
                        "no desktop input, no clipboard access. Thresholds: UI-thread blocking beyond 33 ms <= " + UiBlockBudget +
                        " on both wheel paths, and the fractional callback max <= " + RatioBudget.ToString("F1") +
                        "x the whole-detent max. Callback intervals are not displayed frames and not a refresh-rate guarantee."
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally { Application.Current?.Shutdown(error is null ? 0 : 1); }
    }

    /// <summary>
    /// Starts one probe pass in its own process. A fresh process per pass is required by the
    /// probe's design (its counters and warm-up state are process-scoped) and keeps this guard
    /// from perturbing the measurement it is checking.
    /// </summary>
    private static async Task RunProbeAsync(string probeArgument, string reportPath)
    {
        string processPath = Environment.ProcessPath
            ?? throw new InvalidOperationException("Cannot resolve the host executable path.");
        string entry = Environment.GetCommandLineArgs()[0];
        var start = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(processPath), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            start.FileName = processPath;
            start.ArgumentList.Add(entry);
        }
        else start.FileName = processPath;
        start.ArgumentList.Add(probeArgument);
        start.ArgumentList.Add(reportPath);
        using var process = Process.Start(start)
            ?? throw new InvalidOperationException($"Cannot start the probe process for {probeArgument}.");
        await process.WaitForExitAsync();
        if (!File.Exists(reportPath))
            throw new InvalidOperationException($"{probeArgument} produced no report (exit code {process.ExitCode}).");
    }

    private static (double IntervalMax, double UiLatencyMax, int Stalls, int Over33) ReadSample(string reportPath)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(reportPath));
        var root = json.RootElement;
        if (!root.TryGetProperty("passed", out var passed) || passed.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException($"Probe pass did not succeed: {Path.GetFileName(reportPath)} -> " +
                (root.TryGetProperty("error", out var failure) ? failure.ToString() : "no error recorded"));
        var result = root.GetProperty("result");
        return (
            result.GetProperty("callbackIntervalMs").GetProperty("max").GetDouble(),
            result.GetProperty("uiLatencyMs").GetProperty("max").GetDouble(),
            result.GetProperty("stallCount").GetInt32(),
            result.GetProperty("uiBlocked").GetProperty("over33_3").GetInt32());
    }
}
