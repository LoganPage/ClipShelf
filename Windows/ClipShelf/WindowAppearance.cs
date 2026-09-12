using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace ClipShelf;

/// <summary>
/// Lets DWM own the top-level contour. The client must remain opaque and rectangular:
/// do not add a second rounded clip/stroke, a window region, or per-pixel transparency.
/// WindowChrome must retain its nonzero glass margin to stay on WPF's DWM path.
/// </summary>
// Native ownership/policy: https://learn.microsoft.com/windows/apps/desktop/modernize/ui/apply-rounded-corners
// Attributes and physical border units: https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
public sealed class WindowAppearance : IDisposable
{
    private const int ImmersiveDarkMode = 20, CornerPreference = 33, BorderColor = 34,
        CaptionColor = 35, TextColor = 36, VisibleFrameBorderThickness = 37;
    private const uint ColorDefault = 0xffffffff;
    private const int WmDpiChanged = 0x02e0, WmThemeChanged = 0x031a, WmDwmCompositionChanged = 0x031e;
    private readonly Window window;
    private readonly DependencyPropertyDescriptor? stateDescriptor;
    private HwndSource? source;
    private nint handle;
    private bool disposed, refreshQueued, forceQueued;
    private AppearanceValues? applied;
    private sealed record AppearanceValues(uint Corners, uint Dark, uint Border, uint Caption, uint Text);

    public WindowAppearanceStatus LastStatus { get; private set; } = new();

    public WindowAppearance(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.Dispatcher.VerifyAccess();
        this.window = window;
        // WPF defers the native state change (and StateChanged) while a window is hidden.
        // Observe the requested state as well, so the next show already has the right contour.
        stateDescriptor = DependencyPropertyDescriptor.FromProperty(Window.WindowStateProperty, typeof(Window));
        stateDescriptor?.AddValueChanged(window, StateChanged);
        window.SourceInitialized += SourceInitialized;
        window.StateChanged += StateChanged;
        window.Activated += StateChanged;
        window.Deactivated += StateChanged;
        window.Closed += WindowClosed;
        if (new WindowInteropHelper(window).Handle != 0) Initialize();
    }

    /// <summary>Call after the application updates its theme resources. Does not alter system settings.</summary>
    public void Refresh()
    {
        window.Dispatcher.VerifyAccess();
        Apply(force: false);
    }

    private void SourceInitialized(object? sender, EventArgs args) => Initialize();

    private void Initialize()
    {
        if (disposed || source is not null) return;
        handle = new WindowInteropHelper(window).Handle;
        if (handle == 0) return;
        source = HwndSource.FromHwnd(handle);
        source?.AddHook(MessageHook);
        Apply(force: true);
    }

    private void StateChanged(object? sender, EventArgs args) => QueueRefresh(force: false);

    private nint MessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        // In particular, do not handle WM_SIZE/WM_WINDOWPOSCHANGED, call SetWindowRgn,
        // change WindowChrome, or request SWP_FRAMECHANGED during a live resize.
        if (message is WmDpiChanged or WmThemeChanged or WmDwmCompositionChanged) QueueRefresh(force: true);
        return 0;
    }

    private void QueueRefresh(bool force)
    {
        if (disposed) return;
        forceQueued |= force;
        if (refreshQueued) return;
        refreshQueued = true;
        window.Dispatcher.BeginInvoke(() => {
            refreshQueued = false;
            bool applyForce = forceQueued; forceQueued = false;
            if (!disposed) Apply(applyForce);
        }, DispatcherPriority.Background);
    }

    private void Apply(bool force)
    {
        if (disposed || handle == 0) return;
        bool supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);
        int compositionResult;
        bool composition;
        try { compositionResult = DwmIsCompositionEnabled(out composition); }
        catch (DllNotFoundException) { compositionResult = unchecked((int)0x80004001); composition = false; }
        catch (EntryPointNotFoundException) { compositionResult = unchecked((int)0x80004001); composition = false; }
        if (!supported || compositionResult != 0 || !composition)
        {
            // Windows 10 and unsupported sessions retain the normal system frame/shape.
            // There is deliberately no manually rounded fallback to reintroduce double contours.
            applied = null;
            LastStatus = new WindowAppearanceStatus { Supported = supported, CompositionResult = compositionResult,
                CompositionEnabled = composition, ApplyCount = LastStatus.ApplyCount };
            return;
        }

        bool highContrast = SystemParameters.HighContrast;
        var values = new AppearanceValues(
            window.WindowState == WindowState.Maximized ? 1u : 2u,
            !highContrast && ThemeManager.IsDark ? 1u : 0u,
            highContrast ? ColorDefault : ResourceColor("BorderBrush"),
            highContrast ? ColorDefault : ResourceColor("BackgroundBrush"),
            highContrast ? ColorDefault : ResourceColor("TextBrush"));
        if (!force && applied == values) return;

        // The system chooses its own radius and physical-pixel border at this window's DPI.
        // Maximized windows use square corners; restored/snapped policy is otherwise DWM's.
        int cornerResult = Set(CornerPreference, values.Corners);
        int darkResult = Set(ImmersiveDarkMode, values.Dark);
        int borderResult = Set(BorderColor, values.Border);
        int captionResult = Set(CaptionColor, values.Caption);
        int textResult = Set(TextColor, values.Text);
        int thicknessResult = DwmGetWindowAttribute(handle, VisibleFrameBorderThickness, out uint thickness, sizeof(uint));
        applied = values;
        LastStatus = new WindowAppearanceStatus
        {
            Supported = true, CompositionResult = compositionResult, CompositionEnabled = true,
            CornerPreference = values.Corners, DarkMode = values.Dark != 0,
            BorderColor = values.Border, CornerResult = cornerResult, DarkModeResult = darkResult,
            BorderColorResult = borderResult, CaptionColorResult = captionResult, TextColorResult = textResult,
            VisibleBorderThicknessResult = thicknessResult,
            VisibleBorderThicknessPixels = thicknessResult == 0 ? thickness : null,
            ApplyCount = LastStatus.ApplyCount + 1
        };
    }

    private uint ResourceColor(string key)
    {
        if (window.TryFindResource(key) is not SolidColorBrush brush) return ColorDefault;
        var color = (Color)brush.GetAnimationBaseValue(SolidColorBrush.ColorProperty);
        return (uint)(color.R | (color.G << 8) | (color.B << 16));
    }

    private int Set(int attribute, uint value) => DwmSetWindowAttribute(handle, attribute, ref value, sizeof(uint));

    private void WindowClosed(object? sender, EventArgs args) => Dispose();

    public void Dispose()
    {
        if (disposed) return;
        window.Dispatcher.VerifyAccess();
        disposed = true;
        stateDescriptor?.RemoveValueChanged(window, StateChanged);
        window.SourceInitialized -= SourceInitialized;
        window.StateChanged -= StateChanged;
        window.Activated -= StateChanged;
        window.Deactivated -= StateChanged;
        window.Closed -= WindowClosed;
        source?.RemoveHook(MessageHook); source = null; handle = 0;
    }

    [DllImport("dwmapi.dll")] private static extern int DwmIsCompositionEnabled([MarshalAs(UnmanagedType.Bool)] out bool enabled);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint window, int attribute, ref uint value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out uint value, int size);
}

/// <summary>Last native request/results for diagnostics; accepting a corner hint is not proof of visible rounding.</summary>
public sealed record WindowAppearanceStatus
{
    public bool Supported { get; init; }
    public int? CompositionResult { get; init; }
    public bool CompositionEnabled { get; init; }
    public uint? CornerPreference { get; init; }
    public bool? DarkMode { get; init; }
    public uint? BorderColor { get; init; }
    public int? CornerResult { get; init; }
    public int? DarkModeResult { get; init; }
    public int? BorderColorResult { get; init; }
    public int? CaptionColorResult { get; init; }
    public int? TextColorResult { get; init; }
    public int? VisibleBorderThicknessResult { get; init; }
    public uint? VisibleBorderThicknessPixels { get; init; }
    public int ApplyCount { get; init; }
}
