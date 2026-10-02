using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Bounded, passive diagnostics for real user wheel input.</summary>
internal sealed class ScrollDiagnosticsSession : IDisposable
{
    private static readonly long WindowTicks = Stopwatch.Frequency * 20;
    private readonly MainWindow window;
    private readonly HistoryListBox list;
    private readonly string reportPath;
    private readonly List<CompositionSample> composition = new();
    private readonly List<long> callbacks = new();
    private readonly List<PacketSample> packets = new();
    private TimeSpan previousRendering = TimeSpan.MinValue;
    private bool disposed;

    public ScrollDiagnosticsSession(MainWindow window, string reportPath)
    {
        this.window = window;
        list = (HistoryListBox)window.FindName("HistoryList")!;
        this.reportPath = reportPath;
        list.DiagnosticWheelPacket += OnPacket;
        list.DiagnosticWheelMove += OnMove;
        list.DiagnosticWheelSettled += OnSettled;
        window.PreviewKeyDown += OnKeyDown;
        window.Closed += OnClosed;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (e is not RenderingEventArgs frame) return;
        long now = Stopwatch.GetTimestamp();
        callbacks.Add(now);
        if (frame.RenderingTime == previousRendering) return;
        double interval = previousRendering == TimeSpan.MinValue ? 0 : (frame.RenderingTime - previousRendering).TotalMilliseconds;
        previousRendering = frame.RenderingTime;
        composition.Add(new CompositionSample(now, interval));
        Trim(now);
    }

    private void OnPacket(WheelDiagnosticPacket packet)
    {
        packets.Add(new PacketSample(packet));
        Trim(packet.Timestamp);
    }

    private void OnMove(WheelDiagnosticMove move)
    {
        foreach (PacketSample packet in packets.Where(value => !value.Settled && value.Timestamp <= move.Timestamp))
            packet.Observe(move);
        if (!move.IsActive) Complete(move.Timestamp, move.Offset);
    }

    private void OnSettled(long timestamp, double offset) => Complete(timestamp, offset);

    private void Complete(long timestamp, double offset)
    {
        foreach (PacketSample packet in packets.Where(value => !value.Settled && value.Timestamp <= timestamp))
            packet.Complete(timestamp, offset);
    }

    private void Trim(long now)
    {
        long cutoff = now - WindowTicks;
        composition.RemoveAll(sample => sample.Timestamp < cutoff);
        callbacks.RemoveAll(timestamp => timestamp < cutoff);
        packets.RemoveAll(packet => packet.Timestamp < cutoff);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.F12 || (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) !=
            (ModifierKeys.Control | ModifierKeys.Shift)) return;
        e.Handled = true;
        try { WriteReport(); window.ShowStatus("滚动诊断报告已保存"); }
        catch (Exception error) { window.ShowStatus("诊断报告保存失败：" + error.Message); }
    }

    internal void WriteReport()
    {
        long now = Stopwatch.GetTimestamp();
        Trim(now);
        double[] compositionIntervals = composition.Where(sample => sample.IntervalMs > 0).Select(sample => sample.IntervalMs).ToArray();
        PacketSample[] packetSnapshot = packets.ToArray();
        long frames = composition.Count;
        double callbacksPerFrame = frames == 0 ? 0 : callbacks.Count / (double)frames;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        File.WriteAllText(reportPath, JsonSerializer.Serialize(new
        {
            generatedAt = DateTimeOffset.Now,
            version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(),
            buildSha256 = HashAssembly(),
            sourceData = "Read-only copy of %LOCALAPPDATA%\\ClipShelf; clipboard changes during diagnostics are written only to the temporary copy.",
            rollingWindowSeconds = 20,
            switches = RuntimeFeatureSwitches.Snapshot(),
            compositionFrameCount = frames,
            renderingCallbackCount = callbacks.Count,
            callbacksPerCompositionFrame = callbacksPerFrame,
            callbackMetricWarning = callbacksPerFrame > 1.5
                ? "callbacksPerCompositionFrame > 1.5: callback intervals cannot be treated as displayed frame intervals; earlier callback-only conclusions are unreliable."
                : null,
            compositionIntervalMs = Summary(compositionIntervals),
            inputToFirstMoveMs = Summary(packetSnapshot.Where(packet => packet.FirstMoveMs.HasValue).Select(packet => packet.FirstMoveMs!.Value).ToArray()),
            timeToTarget95Ms = Summary(packetSnapshot.Where(packet => packet.Target95Ms.HasValue).Select(packet => packet.Target95Ms!.Value).ToArray()),
            settleMs = Summary(packetSnapshot.Where(packet => packet.SettleMs.HasValue).Select(packet => packet.SettleMs!.Value).ToArray()),
            overshootDip = Summary(packetSnapshot.Select(packet => packet.OvershootDip).ToArray()),
            packets = packetSnapshot.Select(packet => packet.Report()).ToArray()
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static object Summary(double[] values)
    {
        Array.Sort(values);
        double Percentile(double p) => values.Length == 0 ? 0 : values[(int)Math.Clamp(Math.Ceiling(p * values.Length) - 1, 0, values.Length - 1)];
        return new { count = values.Length, p50 = Percentile(.5), p90 = Percentile(.9), p95 = Percentile(.95), p99 = Percentile(.99), max = Percentile(1) };
    }

    private static string HashAssembly()
    {
        using var input = File.OpenRead(Assembly.GetExecutingAssembly().Location);
        return Convert.ToHexString(SHA256.HashData(input)).ToLowerInvariant();
    }

    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        CompositionTarget.Rendering -= OnRendering;
        list.DiagnosticWheelPacket -= OnPacket;
        list.DiagnosticWheelMove -= OnMove;
        list.DiagnosticWheelSettled -= OnSettled;
        window.PreviewKeyDown -= OnKeyDown;
        window.Closed -= OnClosed;
    }

    private readonly record struct CompositionSample(long Timestamp, double IntervalMs);

    private sealed class PacketSample
    {
        private readonly double direction;
        public long Timestamp { get; }
        public int Delta { get; }
        public double StartOffset { get; }
        public double TargetOffset { get; }
        public double? FirstMoveMs { get; private set; }
        public double? Target95Ms { get; private set; }
        public double? SettleMs { get; private set; }
        public double OvershootDip { get; private set; }
        public bool Settled => SettleMs.HasValue;

        public PacketSample(WheelDiagnosticPacket packet)
        {
            Timestamp = packet.Timestamp; Delta = packet.Delta; StartOffset = packet.StartOffset; TargetOffset = packet.TargetOffset;
            direction = Math.Sign(TargetOffset - StartOffset);
        }

        public void Observe(WheelDiagnosticMove move)
        {
            double elapsed = Milliseconds(move.Timestamp - Timestamp);
            double moved = Math.Abs(move.Offset - StartOffset);
            double total = Math.Abs(TargetOffset - StartOffset);
            if (!FirstMoveMs.HasValue && moved > .5) FirstMoveMs = elapsed;
            if (!Target95Ms.HasValue && (total <= .5 || moved >= total * .95)) Target95Ms = elapsed;
            double beyond = direction == 0 ? 0 : (move.Offset - TargetOffset) * direction;
            OvershootDip = Math.Max(OvershootDip, Math.Max(0, beyond));
        }

        public void Complete(long timestamp, double offset)
        {
            Observe(new WheelDiagnosticMove(timestamp, offset, false));
            SettleMs = Milliseconds(timestamp - Timestamp);
        }

        public object Report() => new
        {
            delta = Delta, startOffset = StartOffset, targetOffset = TargetOffset,
            inputToFirstMoveMs = FirstMoveMs, timeToTarget95Ms = Target95Ms,
            settleMs = SettleMs, overshootDip = OvershootDip
        };

        private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
