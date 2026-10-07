using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>
/// Process-local diagnostic switches. Every value is deliberately false by default so a
/// normal launch remains identical to the 1.4.6 interaction path.
/// </summary>
public static class RuntimeFeatureSwitches
{
    public static bool NativeWheel { get; private set; }
    public static bool InstantWheel { get; private set; }
    public static double? WheelResponse { get; private set; }
    public static double WheelPrewarmMs { get; private set; }
    public static bool SnappyWheel { get; private set; }
    public static bool BalancedWheel { get; private set; }
    public static bool SoftWheel { get; private set; }
    public static bool WheelPixelSnap { get; private set; }
    public static bool NoRowCache { get; private set; }
    public static bool LowThumbnailQuality { get; private set; }
    public static bool NoRowMotion { get; private set; }
    public static bool NoDragRender { get; private set; }
    public static bool NoMicroMotion { get; private set; }
    public static bool NoIndicatorMotion { get; private set; }
    public static bool NoOverlayMotion { get; private set; }
    public static bool NoListLayoutMotion { get; private set; }
    public static bool NoDirectMotion { get; private set; }
    public static string? ScrollDiagnosticReport { get; private set; }

    public static bool RowMotionEnabled => !NoRowMotion;
    public static BitmapScalingMode ThumbnailBitmapScalingMode => LowThumbnailQuality
        ? BitmapScalingMode.Unspecified : BitmapScalingMode.HighQuality;

    public static void Configure(IEnumerable<string> arguments)
    {
        Reset();
        string[] args = arguments.ToArray();
        NativeWheel = args.Contains("--wheel=native", StringComparer.OrdinalIgnoreCase);
        InstantWheel = args.Contains("--wheel=instant", StringComparer.OrdinalIgnoreCase);
        WheelPixelSnap = args.Contains("--wheel-pixel-snap", StringComparer.OrdinalIgnoreCase);
        NoRowCache = args.Contains("--no-row-cache", StringComparer.OrdinalIgnoreCase);
        LowThumbnailQuality = args.Contains("--thumbnail-quality=low", StringComparer.OrdinalIgnoreCase);
        NoRowMotion = args.Contains("--no-row-motion", StringComparer.OrdinalIgnoreCase);
        NoDragRender = args.Contains("--no-drag-render", StringComparer.OrdinalIgnoreCase);
        bool noMotion = args.Contains("--motion=off", StringComparer.OrdinalIgnoreCase);
        NoMicroMotion = noMotion || args.Contains("--motion-micro=off", StringComparer.OrdinalIgnoreCase);
        NoIndicatorMotion = noMotion || args.Contains("--motion-indicator=off", StringComparer.OrdinalIgnoreCase);
        NoOverlayMotion = noMotion || args.Contains("--motion-overlay=off", StringComparer.OrdinalIgnoreCase);
        NoListLayoutMotion = noMotion || args.Contains("--motion-list=off", StringComparer.OrdinalIgnoreCase);
        NoDirectMotion = noMotion || args.Contains("--motion-direct=off", StringComparer.OrdinalIgnoreCase);
        foreach (string argument in args)
        {
            if (argument.Equals("--wheel=snappy", StringComparison.OrdinalIgnoreCase))
                ApplyPreset(response: 90, prewarmMs: 90, snappy: true);
            else if (argument.Equals("--wheel=balanced", StringComparison.OrdinalIgnoreCase))
                ApplyPreset(response: 65, prewarmMs: 60, balanced: true);
            else if (argument.Equals("--wheel=soft", StringComparison.OrdinalIgnoreCase))
                ApplyPreset(response: 45, prewarmMs: 0, soft: true);
        }
        foreach (string argument in args)
        {
            if (argument.StartsWith("--wheel-response=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(argument[17..], NumberStyles.Float, CultureInfo.InvariantCulture, out double response)
                && response is >= 1 and <= 240)
                WheelResponse = response;
            if (argument.StartsWith("--wheel-prewarm=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(argument[16..], NumberStyles.Float, CultureInfo.InvariantCulture, out double prewarm)
                && prewarm is >= 0 and <= 500)
                WheelPrewarmMs = prewarm;
        }
        int diagnostic = Array.FindIndex(args, argument => argument.Equals("--scroll-diag", StringComparison.OrdinalIgnoreCase));
        if (diagnostic >= 0 && diagnostic + 1 < args.Length)
            ScrollDiagnosticReport = System.IO.Path.GetFullPath(args[diagnostic + 1]);
    }

    internal static void Reset()
    {
        NativeWheel = InstantWheel = WheelPixelSnap = NoRowCache = LowThumbnailQuality = NoRowMotion = NoDragRender = false;
        NoMicroMotion = NoIndicatorMotion = NoOverlayMotion = NoListLayoutMotion = NoDirectMotion = false;
        WheelResponse = null; WheelPrewarmMs = 0;
        SnappyWheel = BalancedWheel = SoftWheel = false;
        ScrollDiagnosticReport = null;
    }

    private static void ApplyPreset(double response, double prewarmMs, bool snappy = false, bool balanced = false, bool soft = false)
    {
        WheelResponse = response; WheelPrewarmMs = prewarmMs;
        SnappyWheel = snappy; BalancedWheel = balanced; SoftWheel = soft;
    }

    internal static object Snapshot() => new
    {
        nativeWheel = NativeWheel, instantWheel = InstantWheel, wheelResponse = WheelResponse,
        wheelPrewarmMs = WheelPrewarmMs,
        wheelPreset = SnappyWheel ? "snappy" : BalancedWheel ? "balanced" : SoftWheel ? "soft" : null,
        wheelPixelSnap = WheelPixelSnap, noRowCache = NoRowCache,
        lowThumbnailQuality = LowThumbnailQuality, noRowMotion = NoRowMotion, noDragRender = NoDragRender,
        noMicroMotion = NoMicroMotion, noIndicatorMotion = NoIndicatorMotion, noOverlayMotion = NoOverlayMotion,
        noListLayoutMotion = NoListLayoutMotion, noDirectMotion = NoDirectMotion
    };
}
