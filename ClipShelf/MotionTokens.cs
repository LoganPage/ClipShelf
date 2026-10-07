using System;
using System.Windows;

namespace ClipShelf;

internal enum MotionDomain
{
    MicroInteraction,
    SharedIndicator,
    Overlay,
    ListLayout,
    DirectManipulation
}

internal readonly record struct MotionSpec(
    double Response,
    double Damping,
    double ValueTolerance = 0.001,
    double VelocityTolerance = 0.02);

/// <summary>Semantic motion values. UI code chooses intent, never an unrelated duration.</summary>
internal static class MotionTokens
{
    internal static readonly MotionSpec Hover = new(30, 1);
    internal static readonly MotionSpec Press = new(38, 1);
    internal static readonly MotionSpec Selection = new(30, 1);
    internal static readonly MotionSpec Theme = new(24, 1, 0.75, 12);
    internal static readonly MotionSpec SharedIndicator = new(20, 1, 0.02, 0.1);
    internal static readonly MotionSpec Overlay = new(25, 1, 0.01, 0.5);
    // Sheet/preview completion follows the visually settled state instead of
    // waiting for sub-pixel velocity to become numerically zero. This keeps
    // close latency in the intended 260-340 ms range while still allowing
    // the same spring to reverse from its live value and velocity.
    internal static readonly MotionSpec Sheet = new(18, 1, 0.015, 0.6);
    internal static readonly MotionSpec Preview = new(20, 1, 0.015, 0.6);
    internal static readonly MotionSpec ListLayout = new(21, 1, 0.05, 0.5);
    internal static readonly MotionSpec Insert = new(25, 1, 0.002, 0.05);
    internal static readonly MotionSpec Remove = new(28, 1, 0.002, 0.05);
    internal static readonly MotionSpec DragRelease = new(18, 0.88, 0.02, 0.1);
    internal static readonly MotionSpec Reduced = new(45, 1);

    internal const double PressScale = 0.98;
    internal const double PressOffset = 0.8;
    internal const double SheetOffset = 12;
    internal const double SheetScale = 0.985;
    internal const double ListEnterOffset = 8;
}

internal static class MotionPolicy
{
    internal static bool ReducedMotion => !SystemParameters.ClientAreaAnimation;
    internal static bool HighContrast => SystemParameters.HighContrast;

    internal static bool Allows(MotionDomain domain) => AllowsForPreferences(domain, ReducedMotion, HighContrast);

    internal static bool AllowsForPreferences(MotionDomain domain, bool reducedMotion, bool highContrast)
    {
        if (highContrast || reducedMotion) return false;
        return domain switch
        {
            MotionDomain.MicroInteraction => !RuntimeFeatureSwitches.NoMicroMotion,
            MotionDomain.SharedIndicator => !RuntimeFeatureSwitches.NoIndicatorMotion,
            MotionDomain.Overlay => !RuntimeFeatureSwitches.NoOverlayMotion,
            MotionDomain.ListLayout => !RuntimeFeatureSwitches.NoListLayoutMotion,
            MotionDomain.DirectManipulation => !RuntimeFeatureSwitches.NoDirectMotion,
            _ => true
        };
    }
}

/// <summary>Elapsed-time spring state used by the WPF driver and deterministic tests.</summary>
internal sealed class MotionValue
{
    internal double Current { get; private set; }
    internal double Velocity { get; private set; }
    internal double Target { get; private set; }
    internal MotionSpec Spec { get; private set; }
    internal bool IsActive { get; private set; }

    internal MotionValue(double value, MotionSpec spec)
    {
        Current = Target = value;
        Spec = spec;
    }

    internal void Retarget(double target, MotionSpec spec)
    {
        if (!double.IsFinite(target)) return;
        Target = target;
        Spec = spec;
        IsActive = Math.Abs(Target - Current) > spec.ValueTolerance || Math.Abs(Velocity) > spec.VelocityTolerance;
    }

    internal void Snap(double value)
    {
        Current = Target = value;
        Velocity = 0;
        IsActive = false;
    }

    internal double Advance(double elapsedSeconds)
    {
        if (!IsActive || !double.IsFinite(elapsedSeconds) || elapsedSeconds <= 0) return Current;
        // Very long dispatcher stalls should settle rather than replaying invisible intermediate frames.
        elapsedSeconds = Math.Min(elapsedSeconds, 1);
        double response = Math.Max(0.01, Spec.Response);
        double damping = Math.Clamp(Spec.Damping, 0.05, 1);
        double x = Current - Target;
        if (damping >= 0.9995)
        {
            double coefficient = Velocity + response * x;
            double decay = Math.Exp(-response * elapsedSeconds);
            double nextX = (x + coefficient * elapsedSeconds) * decay;
            Velocity = (Velocity - response * coefficient * elapsedSeconds) * decay;
            Current = Target + nextX;
        }
        else
        {
            double wd = response * Math.Sqrt(1 - damping * damping);
            double a = x;
            double b = (Velocity + damping * response * x) / wd;
            double angle = wd * elapsedSeconds;
            double cosine = Math.Cos(angle), sine = Math.Sin(angle);
            double wave = a * cosine + b * sine;
            double derivative = -a * wd * sine + b * wd * cosine;
            double decay = Math.Exp(-damping * response * elapsedSeconds);
            Current = Target + decay * wave;
            Velocity = decay * (derivative - damping * response * wave);
        }
        if (!double.IsFinite(Current) || !double.IsFinite(Velocity))
        {
            Snap(Target);
            return Current;
        }
        if (Math.Abs(Target - Current) <= Spec.ValueTolerance && Math.Abs(Velocity) <= Spec.VelocityTolerance)
            Snap(Target);
        return Current;
    }
}
