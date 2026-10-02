using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>
/// Process-local diagnostic switches. Every value is deliberately false by default so a
/// normal launch remains identical to the 1.4.5 interaction path.
/// </summary>
public static class RuntimeFeatureSwitches
{
    public static bool NativeWheel { get; private set; }
    public static bool InstantWheel { get; private set; }
    public static double? WheelResponse { get; private set; }
    public static bool WheelPixelSnap { get; private set; }
    public static bool NoRowCache { get; private set; }
    public static bool LowThumbnailQuality { get; private set; }
    public static bool NoRowMotion { get; private set; }
    public static bool NoDragRender { get; private set; }
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
        foreach (string argument in args)
            if (argument.StartsWith("--wheel-response=", StringComparison.OrdinalIgnoreCase)
                && double.TryParse(argument[17..], NumberStyles.Float, CultureInfo.InvariantCulture, out double response)
                && response is >= 1 and <= 240)
                WheelResponse = response;
        int diagnostic = Array.FindIndex(args, argument => argument.Equals("--scroll-diag", StringComparison.OrdinalIgnoreCase));
        if (diagnostic >= 0 && diagnostic + 1 < args.Length)
            ScrollDiagnosticReport = System.IO.Path.GetFullPath(args[diagnostic + 1]);
    }

    internal static void Reset()
    {
        NativeWheel = InstantWheel = WheelPixelSnap = NoRowCache = LowThumbnailQuality = NoRowMotion = NoDragRender = false;
        WheelResponse = null;
        ScrollDiagnosticReport = null;
    }

    internal static object Snapshot() => new
    {
        nativeWheel = NativeWheel, instantWheel = InstantWheel, wheelResponse = WheelResponse,
        wheelPixelSnap = WheelPixelSnap, noRowCache = NoRowCache,
        lowThumbnailQuality = LowThumbnailQuality, noRowMotion = NoRowMotion, noDragRender = NoDragRender
    };
}
