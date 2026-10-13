using System;

namespace ClipShelf;

/// <summary>Pure, elapsed-time scrolling state. No timer, WPF dependency, or fixed frame-rate assumption.</summary>
internal sealed class WheelScrollMotion
{
    internal const double DefaultResponseFrequency = 28;
    // Fraction of the critically damped "pure ease-out" speed given to a packet that starts
    // or outpaces the current motion. 0 is the classic spring from rest (7.5% of a notch in
    // the first 16.7 ms); 0.6 moves about 26% in that frame and never brakes a gesture.
    internal const double DefaultLaunchRatio = .6;
    // Settle once the remainder is below a fifth of a device pixel at 125% scale; the old
    // 0.04 DIP / 0.5 DIP/s tail kept re-arranging the list for ~100 ms of invisible motion.
    private const double SettleDistance = .15, SettleVelocity = 6;
    private readonly MotionValue state = new(0, new MotionSpec(DefaultResponseFrequency, 1, SettleDistance, SettleVelocity));
    // A short critically damped response for wheel notches; units are radians/second, not frames.
    internal double ResponseFrequency { get; set; } = DefaultResponseFrequency;
    internal double LaunchRatio { get; set; } = DefaultLaunchRatio;
    internal double Position => state.Current;
    internal double Target => state.Target;
    internal double Velocity => state.Velocity;
    internal bool IsActive => state.IsActive;
    private MotionSpec Spec => new(ResponseFrequency, 1, SettleDistance, SettleVelocity);

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
        state.Snap(Clamp(position, maximum));
    }

    internal void AddDistance(double distance, double maximum)
    {
        if (!double.IsFinite(distance)) return;
        double remaining = Target - Position;
        // A reversal cancels old-direction backlog immediately. Retarget from the
        // presented position while carrying velocity into the new damped response.
        bool reverses = distance != 0 && Math.Abs(remaining) > 0.01 && Math.Sign(distance) != Math.Sign(remaining);
        double target = Clamp((reverses ? Position : Target) + distance, maximum);
        state.Retarget(target, Spec);
        if (!state.IsActive || LaunchRatio <= 0) return;
        // With velocity = ResponseFrequency * remaining the critically damped solution is a
        // pure exponential ease-out; a partial launch keeps a soft finish. Only raise speed
        // toward the target, so a packet inside a running gesture never stalls the motion.
        double launch = Math.Min(1, LaunchRatio) * ResponseFrequency * (state.Target - state.Current);
        if (Math.Sign(state.Velocity) != Math.Sign(launch) || Math.Abs(state.Velocity) < Math.Abs(launch))
            state.Launch(launch);
    }

    internal double Advance(double elapsedSeconds, double maximum)
    {
        maximum = SafeMaximum(maximum);
        double target = Clamp(Target, maximum), current = Clamp(Position, maximum);
        if (current != Position) state.Snap(current);
        if (target != state.Target) state.Retarget(target, Spec);
        double next = state.Advance(elapsedSeconds);
        double bounded = Clamp(next, maximum);
        if (bounded != next) state.Snap(bounded);
        return state.Current;
    }

    private static double SafeMaximum(double value) => double.IsFinite(value) ? Math.Max(0, value) : 0;
    private static double Clamp(double value, double maximum) => double.IsFinite(value) ? Math.Clamp(value, 0, SafeMaximum(maximum)) : 0;
}
