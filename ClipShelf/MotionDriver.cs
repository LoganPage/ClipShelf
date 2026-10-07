using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>A single render-loop owner for all interruptible WPF motion on the UI dispatcher.</summary>
internal sealed class MotionDriver
{
    private sealed class Channel
    {
        internal required WeakReference<DependencyObject> Owner;
        internal required string Name;
        internal required MotionValue Value;
        internal required Action<double> Apply;
        internal Action? Completed;
        internal int Version;
    }

    private static readonly ConditionalWeakTable<System.Windows.Threading.Dispatcher, MotionDriver> drivers = new();
    internal static MotionDriver Current => drivers.GetValue(Application.Current.Dispatcher, _ => new MotionDriver());

    private readonly List<Channel> channels = new();
    private bool rendering;
    private long lastTimestamp;
    private TimeSpan lastRenderingTime = TimeSpan.MinValue;
    internal int ActiveCount => channels.Count;
    internal bool IsRendering => rendering;

    internal void Animate(DependencyObject owner, string name, double presentedValue, double target,
        MotionSpec spec, Action<double> apply, Action? completed = null)
    {
        Channel? channel = channels.FirstOrDefault(candidate => candidate.Name == name
            && candidate.Owner.TryGetTarget(out var existing) && ReferenceEquals(existing, owner));
        if (channel is null)
        {
            channel = new Channel
            {
                Owner = new(owner), Name = name, Value = new MotionValue(presentedValue, spec), Apply = apply
            };
            channels.Add(channel);
        }
        else
        {
            channel.Apply = apply;
        }
        channel.Completed = completed;
        channel.Version++;
        channel.Value.Retarget(target, spec);
        if (!channel.Value.IsActive)
        {
            apply(target);
            channels.Remove(channel);
            completed?.Invoke();
            StopIfIdle();
            return;
        }
        Start();
    }

    internal void Snap(DependencyObject owner, string name, double value, Action<double> apply)
    {
        Cancel(owner, name);
        apply(value);
    }

    internal void Cancel(DependencyObject owner, string? name = null)
    {
        channels.RemoveAll(channel => (!channel.Owner.TryGetTarget(out var existing) || ReferenceEquals(existing, owner))
            && (name is null || channel.Name == name));
        StopIfIdle();
    }

    internal void CancelAll()
    {
        channels.Clear();
        StopIfIdle();
    }

    private void Start()
    {
        if (rendering) return;
        lastTimestamp = Stopwatch.GetTimestamp();
        lastRenderingTime = TimeSpan.MinValue;
        CompositionTarget.Rendering += OnRendering;
        rendering = true;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        if (args is RenderingEventArgs frame)
        {
            if (frame.RenderingTime == lastRenderingTime) return;
            lastRenderingTime = frame.RenderingTime;
        }
        long now = Stopwatch.GetTimestamp();
        double elapsed = (now - lastTimestamp) / (double)Stopwatch.Frequency;
        lastTimestamp = now;
        // Apply callbacks can synchronously change visual state and cancel/retarget other
        // channels. Iterate a stable snapshot and verify ownership before removing.
        foreach (Channel channel in channels.ToArray())
        {
            if (!channels.Contains(channel)) continue;
            if (!channel.Owner.TryGetTarget(out var owner)
                || owner is FrameworkElement element && (!element.IsLoaded || !element.IsVisible))
            {
                channels.Remove(channel);
                continue;
            }
            int version = channel.Version;
            double value = channel.Value.Advance(elapsed);
            channel.Apply(value);
            if (!channels.Contains(channel) || channel.Value.IsActive || version != channel.Version) continue;
            channels.Remove(channel);
            channel.Completed?.Invoke();
        }
        StopIfIdle();
    }

    private void StopIfIdle()
    {
        if (!rendering || channels.Count != 0) return;
        CompositionTarget.Rendering -= OnRendering;
        rendering = false;
        lastRenderingTime = TimeSpan.MinValue;
    }
}
