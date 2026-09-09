using System;

namespace ClipShelf;

/// <summary>Pure, elapsed-time scrolling state. No timer, WPF dependency, or fixed frame-rate assumption.</summary>
internal sealed class WheelScrollMotion
{
    // A short critically damped response for wheel notches; units are radians/second, not frames.
    private const double AngularFrequency = 28;
    internal double Position { get; private set; }
    internal double Target { get; private set; }
    internal double Velocity { get; private set; }
    internal bool IsActive { get; private set; }

    internal static double WheelDistance(int delta, int lines, double viewportHeight)
    {
        // WHEEL_DELTA=120; WPF's physical line distance is 16 DIP. Preserve both small
        // fractional packets and multi-notch packets, while respecting the OS setting.
        double unit = lines < 0 ? Math.Max(0, viewportHeight) : lines * 16.0;
        return double.IsFinite(unit) ? -(delta / 120.0) * unit : 0;
    }

    internal static bool IsFractionalWheelDelta(int delta) => delta != 0 && Math.Abs((long)delta) % 120 != 0;

    internal void Reset(double position, double maximum)
    {
        Position = Target = Clamp(position, maximum); Velocity = 0; IsActive = false;
    }

    internal void AddDistance(double distance, double maximum)
    {
        if (!double.IsFinite(distance)) return;
        double remaining = Target - Position;
        // A reversal cancels old-direction backlog immediately. Retarget from the
        // presented position while carrying velocity into the new damped response.
        bool reverses = distance != 0 && Math.Abs(remaining) > 0.01 && Math.Sign(distance) != Math.Sign(remaining);
        Target = Clamp((reverses ? Position : Target) + distance, maximum);
        Position = Clamp(Position, maximum);
        IsActive = Math.Abs(Target - Position) > 0.01 || Math.Abs(Velocity) > 0.1;
    }

    internal double Advance(double elapsedSeconds, double maximum)
    {
        maximum = SafeMaximum(maximum);
        Target = Clamp(Target, maximum); Position = Clamp(Position, maximum);
        if (!IsActive || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return Position;
        // Exact critically damped spring solution: identical elapsed time gives identical
        // motion at 60, 120, 144, 240 Hz, or irregular callback intervals.
        double displacement = Position - Target;
        double coefficient = Velocity + AngularFrequency * displacement;
        double decay = Math.Exp(-AngularFrequency * elapsedSeconds);
        if (decay == 0) { Reset(Target, maximum); return Position; }
        double next = Target + (displacement + coefficient * elapsedSeconds) * decay;
        double nextVelocity = (Velocity - AngularFrequency * coefficient * elapsedSeconds) * decay;
        Position = Clamp(next, maximum); Velocity = Position == next ? nextVelocity : 0;
        if (Math.Abs(Target - Position) <= 0.04 && Math.Abs(Velocity) <= 0.5)
        { Position = Target; Velocity = 0; IsActive = false; }
        return Position;
    }

    private static double SafeMaximum(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double Clamp(double value, double maximum) => double.IsFinite(value) ? Math.Clamp(value, 0, SafeMaximum(maximum)) : 0;
}
