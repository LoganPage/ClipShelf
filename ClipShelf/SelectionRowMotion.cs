using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace ClipShelf;

// Only the two flat row surfaces animate. The cached row's text and icons stay static.
internal static class SelectionRowMotion
{
    private static readonly Duration duration = new(TimeSpan.FromMilliseconds(110));
    private static readonly CubicEase easing = new() { EasingMode = EasingMode.EaseOut };

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SelectionRowMotion),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value) => element.SetValue(IsEnabledProperty, value);
    public static bool GetIsEnabled(DependencyObject element) => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not ListBoxItem row) return;
        if ((bool)args.NewValue)
        {
            row.Loaded += RowLoaded;
            row.Unloaded += RowUnloaded;
            row.Selected += SelectionChanged;
            row.Unselected += SelectionChanged;
            if (row.IsLoaded) Update(row, animate: false);
        }
        else
        {
            row.Loaded -= RowLoaded;
            row.Unloaded -= RowUnloaded;
            row.Selected -= SelectionChanged;
            row.Unselected -= SelectionChanged;
            StopAnimations(row);
        }
    }

    private static void RowLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem row) Update(row, animate: false);
    }

    private static void RowUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem row) StopAnimations(row);
    }

    private static void SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (sender is ListBoxItem row && ReferenceEquals(e.OriginalSource, row))
            Update(row, animate: row.IsLoaded && row.IsVisible && SystemParameters.ClientAreaAnimation);
    }

    private static void Update(ListBoxItem row, bool animate)
    {
        if (row.Template.FindName("RowBg", row) is not Border layer ||
            row.Template.FindName("RowSeparator", row) is not Border separator) return;
        SetOpacity(layer, row.IsSelected ? 1 : 0, animate);
        SetOpacity(separator, row.IsSelected ? 0 : 0.45, animate);
    }

    private static void StopAnimations(ListBoxItem row)
    {
        if (row.Template.FindName("RowBg", row) is Border layer)
            layer.BeginAnimation(UIElement.OpacityProperty, null);
        if (row.Template.FindName("RowSeparator", row) is Border separator)
            separator.BeginAnimation(UIElement.OpacityProperty, null);
    }

    private static void SetOpacity(Border surface, double target, bool animate)
    {
        if (!animate)
        {
            surface.BeginAnimation(UIElement.OpacityProperty, null);
            surface.Opacity = target;
            return;
        }
        // SnapshotAndReplace makes a rapid reverse selection start from the current
        // displayed value. Each row owns at most one active transition per surface.
        surface.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(surface.Opacity, target, duration) { EasingFunction = easing, FillBehavior = FillBehavior.Stop },
            HandoffBehavior.SnapshotAndReplace);
        surface.Opacity = target;
    }
}
