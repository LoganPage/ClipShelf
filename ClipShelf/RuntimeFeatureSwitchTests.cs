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
                && !RuntimeFeatureSwitches.NoRowMotion && !RuntimeFeatureSwitches.NoDragRender, "all diagnostic switches default off");

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
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally { RuntimeFeatureSwitches.Reset(); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new { passed = error is null, checks, error }, new JsonSerializerOptions { WriteIndented = true }));
        System.Windows.Application.Current.Shutdown(error is null ? 0 : 1);
        return Task.CompletedTask;
    }
}
