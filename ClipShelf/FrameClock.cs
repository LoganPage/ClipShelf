using System;
using System.Diagnostics;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>
/// Elapsed time for one CompositionTarget.Rendering loop. WPF reports the frame's estimated
/// presentation time; stepping by that value keeps each presented frame an even distance
/// apart even when dispatcher work delays the callback itself. Callback time is only the
/// fallback for the first frame and for non-WPF callers such as isolated fixtures.
/// </summary>
internal sealed class FrameClock
{
    // A presentation gap beyond this is a stall or a clock discontinuity, not a frame.
    private const double MaximumPresentationStep = .25;
    private long lastTimestamp;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;

    internal void Start()
    {
        lastTimestamp = Stopwatch.GetTimestamp();
        lastRenderingTime = TimeSpan.MinValue;
    }

    /// <summary>Returns false for a repeated callback inside the same frame.</summary>
    internal bool TryAdvance(EventArgs args, out double elapsedSeconds)
    {
        TimeSpan? renderingTime = args is RenderingEventArgs frame ? frame.RenderingTime : null;
        if (renderingTime == lastRenderingTime) { elapsedSeconds = 0; return false; }
        long now = Stopwatch.GetTimestamp();
        elapsedSeconds = (now - lastTimestamp) / (double)Stopwatch.Frequency;
        if (renderingTime is TimeSpan presented && lastRenderingTime != TimeSpan.MinValue)
        {
            double step = (presented - lastRenderingTime).TotalSeconds;
            if (step > 0 && step <= MaximumPresentationStep) elapsedSeconds = step;
        }
        lastTimestamp = now;
        if (renderingTime is TimeSpan current) lastRenderingTime = current;
        return true;
    }
}
