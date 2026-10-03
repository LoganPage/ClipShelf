using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace ClipShelf;

public static class RuntimeFeatureSwitchTests
{
    public static Task RunAsync(string reportPath)
    {
        reportPath = Path.Combine(reportPath, "runtime-switch-results.json");
        var checks = new List<string>();
        string? error = null;
        try
        {
            void Check(bool result, string name) { if (!result) throw new InvalidOperationException(name); checks.Add(name); }
            RuntimeFeatureSwitches.Configure(Array.Empty<string>());
            Check(!RuntimeFeatureSwitches.NativeWheel && !RuntimeFeatureSwitches.InstantWheel && RuntimeFeatureSwitches.WheelResponse is null
                && !RuntimeFeatureSwitches.WheelPixelSnap && !RuntimeFeatureSwitches.NoRowCache && !RuntimeFeatureSwitches.LowThumbnailQuality
                && !RuntimeFeatureSwitches.NoRowMotion && !RuntimeFeatureSwitches.NoDragRender
                && RuntimeFeatureSwitches.WheelPrewarmMs == 0 && !RuntimeFeatureSwitches.SnappyWheel
                && !RuntimeFeatureSwitches.BalancedWheel && !RuntimeFeatureSwitches.SoftWheel,
                "all diagnostic switches and wheel presets default off");

            var cases = new (string Argument, Func<bool> Enabled)[]
            {
                ("--wheel=native", () => RuntimeFeatureSwitches.NativeWheel),
                ("--wheel=instant", () => RuntimeFeatureSwitches.InstantWheel),
                ("--wheel-response=60", () => RuntimeFeatureSwitches.WheelResponse == 60),
                ("--wheel-pixel-snap", () => RuntimeFeatureSwitches.WheelPixelSnap),
                ("--no-row-cache", () => RuntimeFeatureSwitches.NoRowCache),
                ("--thumbnail-quality=low", () => RuntimeFeatureSwitches.LowThumbnailQuality),
                ("--no-row-motion", () => RuntimeFeatureSwitches.NoRowMotion),
                ("--no-drag-render", () => RuntimeFeatureSwitches.NoDragRender)
            };
            foreach (var item in cases)
            {
                RuntimeFeatureSwitches.Configure([item.Argument]);
                Check(item.Enabled(), item.Argument + " enables its isolated diagnostic path");
            }
            string diagnostic = Path.Combine(Path.GetTempPath(), "clipshelf-scroll-diag.json");
            RuntimeFeatureSwitches.Configure(["--scroll-diag", diagnostic]);
            Check(RuntimeFeatureSwitches.ScrollDiagnosticReport == Path.GetFullPath(diagnostic), "scroll diagnostic report path is normalized");

            RuntimeFeatureSwitches.Configure(["--wheel=snappy"]);
            Check(RuntimeFeatureSwitches.SnappyWheel && RuntimeFeatureSwitches.WheelResponse == 90 && RuntimeFeatureSwitches.WheelPrewarmMs == 90,
                "snappy preset expands to response 90 and prewarm 90 ms");
            RuntimeFeatureSwitches.Configure(["--wheel=balanced"]);
            Check(RuntimeFeatureSwitches.BalancedWheel && RuntimeFeatureSwitches.WheelResponse == 65 && RuntimeFeatureSwitches.WheelPrewarmMs == 60,
                "balanced preset expands to response 65 and prewarm 60 ms");
            RuntimeFeatureSwitches.Configure(["--wheel=soft"]);
            Check(RuntimeFeatureSwitches.SoftWheel && RuntimeFeatureSwitches.WheelResponse == 45 && RuntimeFeatureSwitches.WheelPrewarmMs == 0,
                "soft preset expands to response 45 without prewarm");
            RuntimeFeatureSwitches.Configure(["--wheel=snappy", "--wheel-response=45", "--wheel-prewarm=30"]);
            Check(RuntimeFeatureSwitches.WheelResponse == 45 && RuntimeFeatureSwitches.WheelPrewarmMs == 30,
                "explicit response and prewarm override a preset");

            var baseline = new WheelScrollMotion { ResponseFrequency = 28 };
            baseline.Reset(100, 1000); baseline.AddDistance(48, 1000);
            var prewarmed = new WheelScrollMotion { ResponseFrequency = 28 };
            prewarmed.Reset(100, 1000); prewarmed.AddDistance(48, 1000);
            double prewarmPosition = prewarmed.Advance(.060, 1000);
            Check(Math.Abs(baseline.Target - prewarmed.Target) < .0001,
                "prewarm does not change the final target");
            double progress = (prewarmPosition - 100) / 48;
            Check(progress is >= .45 and <= .55,
                $"60 ms prewarm advances 45-55 percent before the first frame (measured {progress:P1})");
            var bounded = new WheelScrollMotion { ResponseFrequency = 28 };
            bounded.Reset(995, 1000); bounded.AddDistance(48, 1000);
            double boundedPosition = bounded.Advance(.090, 1000);
            Check(boundedPosition is >= 0 and <= 1000 && bounded.Target == 1000,
                "prewarm remains inside the scroll extent");

            RuntimeFeatureSwitches.Configure(["--wheel=balanced"]);
            Check(HistoryListBox.ShouldApplyWheelPrewarm(clientAreaAnimation: true, directInput: false),
                "animated custom wheel path applies configured prewarm");
            RuntimeFeatureSwitches.Configure(["--wheel=instant", "--wheel-prewarm=60"]);
            Check(!HistoryListBox.ShouldApplyWheelPrewarm(clientAreaAnimation: true, directInput: false),
                "instant wheel and prewarm are mutually exclusive");
            RuntimeFeatureSwitches.Configure(["--wheel=native", "--wheel-prewarm=60"]);
            Check(!HistoryListBox.ShouldApplyWheelPrewarm(clientAreaAnimation: true, directInput: false),
                "native wheel and prewarm are mutually exclusive");
            RuntimeFeatureSwitches.Configure(["--wheel-prewarm=60"]);
            Check(!HistoryListBox.ShouldApplyWheelPrewarm(clientAreaAnimation: false, directInput: false)
                && !HistoryListBox.ShouldApplyWheelPrewarm(clientAreaAnimation: true, directInput: true),
                "reduced motion and direct drag input never prewarm");
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { RuntimeFeatureSwitches.Reset(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        System.Windows.Application.Current.Shutdown(error is null ? 0 : 1);
        return Task.CompletedTask;
    }
}
