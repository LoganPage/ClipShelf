using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace ClipShelf;

/// <summary>Draws and hit-tests OCR word regions without replacing the previewed image.</summary>
internal sealed class OcrSelectionOverlay : FrameworkElement
{
    private static readonly Brush RecognizedFill = FrozenBrush(Color.FromArgb(16, 40, 112, 219));
    private static readonly Brush SelectedFill = FrozenBrush(Color.FromArgb(78, 40, 112, 219));
    private static readonly Pen RecognizedPen = FrozenPen(Color.FromArgb(66, 40, 112, 219), .8);
    private static readonly Pen SelectedPen = FrozenPen(Color.FromArgb(225, 40, 112, 219), 1.4);
    private static readonly Pen DragPen = FrozenPen(Color.FromArgb(240, 40, 112, 219), 1.2, new DoubleCollection { 4, 3 });

    private IReadOnlyList<OcrWordRegion> words = Array.Empty<OcrWordRegion>();
    private readonly HashSet<int> selected = new();
    private Func<Rect>? imageBoundsProvider;
    private Point dragStart;
    private Point dragCurrent;
    private bool dragging;

    internal event Action<string>? SelectionChanged;
    internal string SelectedText { get; private set; } = "";
    internal int SelectedCount => selected.Count;
    internal int WordCount => words.Count;

    internal OcrSelectionOverlay()
    {
        Cursor = Cursors.Cross;
        Focusable = true;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    internal void SetWords(IReadOnlyList<OcrWordRegion> regions, Func<Rect>? boundsProvider = null)
    {
        words = regions ?? Array.Empty<OcrWordRegion>();
        imageBoundsProvider = boundsProvider;
        selected.Clear();
        SelectedText = "";
        dragging = false;
        IsHitTestVisible = words.Count > 0;
        InvalidateVisual();
        SelectionChanged?.Invoke(SelectedText);
    }

    internal void Clear()
    {
        words = Array.Empty<OcrWordRegion>();
        selected.Clear();
        SelectedText = "";
        dragging = false;
        IsHitTestVisible = false;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
        SelectionChanged?.Invoke(SelectedText);
    }

    internal void SelectAll()
    {
        selected.Clear();
        for (int i = 0; i < words.Count; i++) selected.Add(i);
        FinishSelection();
    }

    /// <summary>Used by the pointer path and deterministic regression tests.</summary>
    internal void SelectNormalized(Rect normalizedSelection)
    {
        selected.Clear();
        Rect selection = ClampNormalized(normalizedSelection);
        for (int i = 0; i < words.Count; i++)
            if (selection.IntersectsWith(words[i].Bounds)) selected.Add(i);
        FinishSelection();
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (!PointerPressed(e.GetPosition(this))) return;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!dragging || e.LeftButton != MouseButtonState.Pressed) return;
        PointerMoved(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!dragging) return;
        PointerReleased(e.GetPosition(this));
        if (IsMouseCaptured) ReleaseMouseCapture();
        e.Handled = true;
    }

    // These three methods are the coordinate-stable core of the real WPF pointer route above.
    // Keeping them separate from MouseDevice.GetPosition lets the headless suite drive the same path.
    internal bool PointerPressed(Point point)
    {
        Rect imageBounds = ImageBounds();
        if (!imageBounds.Contains(point)) return false;
        Focus();
        dragStart = dragCurrent = point;
        dragging = true;
        selected.Clear();
        SelectedText = "";
        InvalidateVisual();
        SelectionChanged?.Invoke(SelectedText);
        return true;
    }

    internal void PointerMoved(Point point)
    {
        if (!dragging) return;
        dragCurrent = ClampPoint(point, ImageBounds());
        SelectDisplayRect(new Rect(dragStart, dragCurrent), live: true);
    }

    internal void PointerReleased(Point point)
    {
        if (!dragging) return;
        dragCurrent = ClampPoint(point, ImageBounds());
        Rect selection = new(dragStart, dragCurrent);
        dragging = false;
        if (selection.Width < 5 && selection.Height < 5) SelectPoint(dragCurrent);
        else SelectDisplayRect(selection, live: false);
    }

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters) =>
        IsHitTestVisible && ImageBounds().Contains(hitTestParameters.HitPoint)
            ? new PointHitTestResult(this, hitTestParameters.HitPoint)
            : null;

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        Rect imageBounds = ImageBounds();
        if (imageBounds.IsEmpty) return;
        for (int i = 0; i < words.Count; i++)
        {
            Rect rect = ToDisplay(words[i].Bounds, imageBounds);
            bool isSelected = selected.Contains(i);
            drawingContext.DrawRoundedRectangle(isSelected ? SelectedFill : RecognizedFill,
                isSelected ? SelectedPen : RecognizedPen, rect, 2, 2);
        }
        if (dragging)
        {
            Rect drag = new(dragStart, dragCurrent);
            drag.Intersect(imageBounds);
            if (!drag.IsEmpty) drawingContext.DrawRoundedRectangle(Brushes.Transparent, DragPen, drag, 3, 3);
        }
    }

    private void SelectDisplayRect(Rect displaySelection, bool live)
    {
        Rect imageBounds = ImageBounds();
        displaySelection.Intersect(imageBounds);
        selected.Clear();
        if (!displaySelection.IsEmpty)
        {
            Rect normalized = new((displaySelection.Left - imageBounds.Left) / imageBounds.Width,
                (displaySelection.Top - imageBounds.Top) / imageBounds.Height,
                displaySelection.Width / imageBounds.Width, displaySelection.Height / imageBounds.Height);
            for (int i = 0; i < words.Count; i++)
                if (normalized.IntersectsWith(words[i].Bounds)) selected.Add(i);
        }
        UpdateSelectedText();
        InvalidateVisual();
        SelectionChanged?.Invoke(SelectedText);
        if (!live) dragging = false;
    }

    private void SelectPoint(Point point)
    {
        Rect bounds = ImageBounds();
        if (bounds.IsEmpty) { FinishSelection(); return; }
        Point normalized = new((point.X - bounds.Left) / bounds.Width, (point.Y - bounds.Top) / bounds.Height);
        selected.Clear();
        for (int i = 0; i < words.Count; i++)
            if (words[i].Bounds.Contains(normalized)) { selected.Add(i); break; }
        FinishSelection();
    }

    private void FinishSelection()
    {
        dragging = false;
        UpdateSelectedText();
        InvalidateVisual();
        SelectionChanged?.Invoke(SelectedText);
    }

    private void UpdateSelectedText()
    {
        var ordered = selected.OrderBy(index => words[index].LineIndex).ThenBy(index => words[index].WordIndex).ToArray();
        var text = new StringBuilder();
        int previousLine = -1;
        foreach (int index in ordered)
        {
            OcrWordRegion word = words[index];
            if (text.Length > 0) text.Append(word.LineIndex == previousLine ? ' ' : '\n');
            text.Append(word.Text);
            previousLine = word.LineIndex;
        }
        SelectedText = text.ToString();
    }

    private Rect ImageBounds()
    {
        Rect bounds = imageBoundsProvider?.Invoke() ?? new Rect(0, 0, ActualWidth, ActualHeight);
        return bounds.Width > 0 && bounds.Height > 0 ? bounds : Rect.Empty;
    }

    private static Rect ToDisplay(Rect normalized, Rect bounds) => new(bounds.Left + normalized.Left * bounds.Width,
        bounds.Top + normalized.Top * bounds.Height, normalized.Width * bounds.Width, normalized.Height * bounds.Height);

    private static Point ClampPoint(Point point, Rect bounds) => bounds.IsEmpty ? point :
        new Point(Math.Clamp(point.X, bounds.Left, bounds.Right), Math.Clamp(point.Y, bounds.Top, bounds.Bottom));

    private static Rect ClampNormalized(Rect rect)
    {
        double left = Math.Clamp(rect.Left, 0, 1), top = Math.Clamp(rect.Top, 0, 1);
        double right = Math.Clamp(rect.Right, left, 1), bottom = Math.Clamp(rect.Bottom, top, 1);
        return new Rect(left, top, right - left, bottom - top);
    }

    private static Brush FrozenBrush(Color color) { var brush = new SolidColorBrush(color); brush.Freeze(); return brush; }
    private static Pen FrozenPen(Color color, double thickness, DoubleCollection? dash = null)
    {
        var pen = new Pen(FrozenBrush(color), thickness) { DashStyle = dash is null ? DashStyles.Solid : new DashStyle(dash, 0) };
        pen.Freeze();
        return pen;
    }
}
