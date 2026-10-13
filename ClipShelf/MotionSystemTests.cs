using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

internal static class MotionSystemTests
{
    internal static async Task RunAsync(string directory)
    {
        Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        Directory.CreateDirectory(directory);
        var checks = new List<string>(); string? error = null; Window? window = null;
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name); }
        try
        {
            double? reference = null;
            foreach (int rate in new[] { 60, 120, 144, 160, 240 })
            {
                var motion = new MotionValue(0, MotionTokens.SharedIndicator); motion.Retarget(240, MotionTokens.SharedIndicator);
                for (int index = 0; index < rate / 2; index++) motion.Advance(1d / rate);
                reference ??= motion.Current;
                Check(Math.Abs(motion.Current - reference.Value) < .00001, $"Elapsed-time trajectory is stable at {rate} Hz");
                for (int index = 0; index < rate; index++) motion.Advance(1d / rate);
                Check(!motion.IsActive && motion.Current == 240, $"Motion settles and stops at {rate} Hz");
            }

            var regular = new MotionValue(0, MotionTokens.Sheet); regular.Retarget(1, MotionTokens.Sheet);
            for (int index = 0; index < 50; index++) regular.Advance(.01);
            var irregular = new MotionValue(0, MotionTokens.Sheet); irregular.Retarget(1, MotionTokens.Sheet);
            foreach (double step in new[] { .004, .027, .011, .063, .019, .008, .091, .013, .057, .037, .17 }) irregular.Advance(step);
            Check(Math.Abs(regular.Current - irregular.Current) < .00001, "Irregular and regular intervals produce the same half-second state");

            var reverse = new MotionValue(0, MotionTokens.SharedIndicator); reverse.Retarget(300, MotionTokens.SharedIndicator); reverse.Advance(.08);
            double current = reverse.Current, velocity = reverse.Velocity; reverse.Retarget(-80, MotionTokens.SharedIndicator);
            Check(reverse.Current == current && reverse.Velocity == velocity, "Retargeting preserves the presented value and velocity");
            for (int index = 0; index < 12; index++) { reverse.Advance(.018); reverse.Retarget(index % 2 == 0 ? 200 : -40, MotionTokens.SharedIndicator); }
            reverse.Retarget(72, MotionTokens.SharedIndicator); for (int index = 0; index < 240; index++) reverse.Advance(1d / 120);
            Check(!reverse.IsActive && reverse.Current == 72, "Rapid reversals converge only to the newest target");

            var stalled = new MotionValue(0, MotionTokens.ListLayout); stalled.Retarget(500, MotionTokens.ListLayout); stalled.Advance(30);
            Check(double.IsFinite(stalled.Current) && double.IsFinite(stalled.Velocity), "A long dispatcher stall remains finite and stable");
            var release = new MotionValue(0, MotionTokens.DragRelease); release.Retarget(100, MotionTokens.DragRelease);
            for (int index = 0; index < 600; index++) release.Advance(1d / 240);
            Check(!release.IsActive && release.Current == 100, "A momentum profile settles without endless overshoot");

            RunWheelCurveChecks(Check);
            RunFrameClockChecks(Check);

            window = new Window { Width = 240, Height = 120, Left = -12000, Top = -12000, ShowInTaskbar = false, ShowActivated = false };
            var panel = new StackPanel(); var owner = new Border(); var button = new Button { Content = "Motion fixture" };
            panel.Children.Add(owner); panel.Children.Add(button); window.Content = panel;
            PressableMotion.SetIsEnabled(button, true);
            window.Show(); await Idle();
            double presented = 0;
            MotionDriver.Current.Animate(owner, "fixture", 0, 1, MotionTokens.Press, value => presented = value);
            await WaitFor(() => MotionDriver.Current.ActiveCount == 0, 1200);
            Check(Math.Abs(presented - 1) < .001 && !MotionDriver.Current.IsRendering, "The shared driver commits its target and releases the render subscription");
            MotionDriver.Current.Animate(owner, "fixture", presented, 0, MotionTokens.Sheet, value => presented = value);
            MotionDriver.Current.Cancel(owner);
            Check(MotionDriver.Current.ActiveCount == 0 && !MotionDriver.Current.IsRendering, "Cancellation removes the channel and render subscription");

            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = button });
            await Task.Delay(45);
            var transforms = (TransformGroup)button.RenderTransform;
            var buttonScale = (ScaleTransform)transforms.Children[0];
            var buttonOffset = (TranslateTransform)transforms.Children[1];
            Check(buttonScale.ScaleX < 1 && buttonOffset.Y > 0, "Pointer down produces visible scale and displacement feedback by the next available frames");
            button.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, Environment.TickCount)
                { RoutedEvent = Mouse.LostMouseCaptureEvent, Source = button });
            await WaitFor(() => Math.Abs(buttonOffset.Y) < .01, 1200);
            Check(buttonScale.ScaleX >= 1 && Math.Abs(buttonOffset.Y) < .01, "Capture loss clears the pressed state without leaving a stuck transform");

            RuntimeFeatureSwitches.Configure(new[] { "--motion=off" });
            Check(RuntimeFeatureSwitches.NoMicroMotion && RuntimeFeatureSwitches.NoIndicatorMotion && RuntimeFeatureSwitches.NoOverlayMotion
                && RuntimeFeatureSwitches.NoListLayoutMotion && RuntimeFeatureSwitches.NoDirectMotion, "The global motion switch disables every motion domain");
            button.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Left)
                { RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = button });
            Check(buttonScale.ScaleX == MotionTokens.PressScale && buttonOffset.Y == MotionTokens.PressOffset,
                "Reduced/global-off motion snaps to an immediate static pressed state without delaying feedback");
            PressableMotion.SetIsEnabled(button, false);
            Check(button.RenderTransform == Transform.Identity, "Detaching the behavior restores the control's original transform");
            RuntimeFeatureSwitches.Configure(Array.Empty<string>());
            foreach (MotionDomain domain in Enum.GetValues<MotionDomain>())
            {
                Check(!MotionPolicy.AllowsForPreferences(domain, reducedMotion: true, highContrast: false),
                    $"Reduced Motion suppresses {domain} movement without changing business state");
                Check(!MotionPolicy.AllowsForPreferences(domain, reducedMotion: false, highContrast: true),
                    $"High Contrast keeps {domain} on its static visual path");
                Check(MotionPolicy.AllowsForPreferences(domain, reducedMotion: false, highContrast: false),
                    $"Normal accessibility preferences permit {domain} motion");
            }
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            MotionDriver.Current.CancelAll(); window?.Close(); RuntimeFeatureSwitches.Configure(Array.Empty<string>());
            File.WriteAllText(Path.Combine(directory, "motion-system-results.json"), JsonSerializer.Serialize(new
            {
                passed = error is null, checks, error,
                scope = "Synthetic elapsed-time and off-screen WPF checks; no user history, clipboard input, or displayed FPS claim."
            }, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Shutdown(error is null ? 0 : 1);
        }
    }

    private static async Task Idle() => await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
    private static async Task WaitFor(Func<bool> condition, int timeout)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < timeout) await Task.Delay(20);
        if (!condition()) throw new TimeoutException("Motion did not settle");
    }

    private static double FirstFrameProgress(double launchRatio)
    {
        var motion = new WheelScrollMotion { LaunchRatio = launchRatio };
        motion.Reset(100, 10000); motion.AddDistance(48, 10000);
        return (motion.Advance(1d / 60, 10000) - 100) / 48;
    }

    private static void RunWheelCurveChecks(Action<bool, string> check)
    {
        double classic = FirstFrameProgress(0), shipped = FirstFrameProgress(WheelScrollMotion.DefaultLaunchRatio);
        check(classic < .1, $"Classic spring from rest is still available for A/B ({classic:P1} of a notch in the first 60 Hz frame)");
        check(shipped >= .22, $"A wheel notch moves visibly in the first 60 Hz frame ({shipped:P1}, classic {classic:P1})");

        var launched = new WheelScrollMotion(); launched.Reset(100, 10000); launched.AddDistance(48, 10000);
        check(launched.Target == 148, "Launch speed never changes where the notch lands");
        double previous = launched.Position, elapsed = 0;
        bool monotonic = true;
        while (launched.IsActive && elapsed < 1)
        {
            double next = launched.Advance(1d / 60, 10000); elapsed += 1d / 60;
            monotonic &= next >= previous - 1e-9 && next <= 148 + 1e-9; previous = next;
        }
        check(monotonic && !launched.IsActive && launched.Position == 148, "Launched notch eases out monotonically without overshoot");
        check(elapsed <= .28, $"A single notch settles within 280 ms (measured {elapsed * 1000:F0} ms; classic 367 ms)");

        var gesture = new WheelScrollMotion(); gesture.Reset(0, 10000); gesture.AddDistance(48, 10000);
        bool neverBrakes = true;
        for (int packet = 0; packet < 12; packet++)
        {
            gesture.Advance(.035, 10000);
            double before = gesture.Velocity; gesture.AddDistance(48, 10000);
            neverBrakes &= gesture.Velocity >= before;
        }
        check(neverBrakes, "A packet inside a running gesture never lowers the scroll speed");

        var reversal = new WheelScrollMotion(); reversal.Reset(500, 10000); reversal.AddDistance(240, 10000); reversal.Advance(.03, 10000);
        double at = reversal.Position; reversal.AddDistance(-48, 10000);
        check(reversal.Velocity < 0 && Math.Abs(reversal.Target - (at - 48)) < 1e-6, "A reversal heads toward the new target on the next frame");

        double? reference = null;
        foreach (int rate in new[] { 60, 120, 160, 240 })
        {
            var motion = new WheelScrollMotion(); motion.Reset(0, 10000); motion.AddDistance(480, 10000);
            for (int index = 0; index < rate / 5; index++) motion.Advance(1d / rate, 10000);
            reference ??= motion.Position;
            check(Math.Abs(motion.Position - reference.Value) < 1e-6, $"Launched wheel trajectory is identical at {rate} Hz");
        }
    }

    private static void RunFrameClockChecks(Action<bool, string> check)
    {
        var constructor = typeof(RenderingEventArgs).GetConstructor(
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic, null, new[] { typeof(TimeSpan) }, null);
        if (constructor is null) { check(true, "Frame clock presentation-time check skipped: RenderingEventArgs is not constructible"); return; }
        RenderingEventArgs Frame(double milliseconds) => (RenderingEventArgs)constructor.Invoke(new object[] { TimeSpan.FromMilliseconds(milliseconds) });
        var clock = new FrameClock(); clock.Start();
        check(clock.TryAdvance(Frame(1000), out _), "The first frame after start advances");
        check(!clock.TryAdvance(Frame(1000), out _), "A repeated callback inside one frame is ignored");
        var steps = new List<double>();
        foreach (double presented in new[] { 1016.667, 1033.333, 1050, 1066.667 })
        {
            // Simulated dispatcher delay: the callback itself arrives at an irregular time.
            Thread.Sleep(steps.Count % 2 == 0 ? 1 : 9);
            if (clock.TryAdvance(Frame(presented), out double step)) steps.Add(step);
        }
        check(steps.Count == 4 && steps.All(step => Math.Abs(step - 1d / 60) < .0005),
            "Motion steps follow presentation time, not callback jitter");
        check(clock.TryAdvance(Frame(5000), out double stall) && stall < .1,
            "A presentation discontinuity falls back to measured elapsed time");
    }
}
