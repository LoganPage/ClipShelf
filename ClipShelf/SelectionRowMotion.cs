using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace ClipShelf;

// Only the two flat row surfaces animate. The cached row's text and icons stay static.
internal static class SelectionRowMotion
{
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
            Update(row, animate: row.IsLoaded && row.IsVisible && RuntimeFeatureSwitches.RowMotionEnabled
                && MotionPolicy.Allows(MotionDomain.MicroInteraction));
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
            MotionDriver.Current.Cancel(layer, "selection-opacity");
        if (row.Template.FindName("RowSeparator", row) is Border separator)
            MotionDriver.Current.Cancel(separator, "selection-opacity");
    }

    private static void SetOpacity(Border surface, double target, bool animate)
    {
        if (!animate)
        {
            surface.Opacity = target;
            return;
        }
        MotionDriver.Current.Animate(surface, "selection-opacity", surface.Opacity, target,
            MotionTokens.Selection, value => surface.Opacity = value);
    }
}
