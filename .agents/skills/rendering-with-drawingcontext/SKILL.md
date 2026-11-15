---
name: rendering-with-drawingcontext
description: Use this skill when implementing, optimizing, or reviewing high-performance 2D rendering in WPF using DrawingVisual, DrawingContext, and Direct Visual Layer rendering for real-time charts, AOI optical ROI bounding boxes, and camera image defect overlays.
---

# High-Performance WPF Rendering with DrawingContext & DrawingVisual

This skill provides architecture patterns, zero-allocation conventions, and performance guidelines for high-frequency 2D rendering in `OTPSoftware.App` (e.g., `ChartCanvasRenderer`, defect ROI overlays, coordinate mapping on high-res camera captures).

---

## 1. Visual Layer Rendering vs. UIElements

In industrial inspection and 60 FPS wallboards, avoid instantiating heavy WPF `UIElement` / `Shape` objects (`System.Windows.Shapes.Path`, `Rectangle`, `Line`) inside dynamically updating canvases.

- **Why avoid `Shape` objects?**
  Each `Shape` carries the full overhead of WPF dependency properties, layout measurement passes (`MeasureOverride`/`ArrangeOverride`), input event routing, and visual tree tracking (~1–2 KB per object). Adding thousands of ROI boxes or chart points causes severe GC spikes and layout stutter.
- **Why use `DrawingVisual` & `DrawingContext`?**
  `DrawingVisual` is an extremely lightweight visual container designed for direct retain-mode graphics commands. Graphics drawn with `DrawingContext` are compiled directly into native MilCore rendering primitives with hardware acceleration and near-zero memory footprint.

---

## 2. Implementing a Custom Visual Canvas Host

Create a lightweight host element deriving from `FrameworkElement` that manages a `VisualCollection`:

```csharp
public sealed class DirectVisualCanvas : FrameworkElement
{
    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _drawingVisual;

    public DirectVisualCanvas()
    {
        _visuals = new VisualCollection(this);
        _drawingVisual = new DrawingVisual();
        _visuals.Add(_drawingVisual);
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    public void RenderContent(Action<DrawingContext> drawAction)
    {
        using DrawingContext dc = _drawingVisual.RenderOpen();
        drawAction(dc);
    }
}
```

---

## 3. High-Performance Drawing Guidelines

### A. Pre-create and Freeze Graphic Assets
Never allocate `Brush` or `Pen` instances inside tight rendering loops. Pre-allocate static brushes and pens in a cache or static class, and **always freeze them**:

```csharp
public static class RenderResources
{
    public static readonly Brush DefectNgBrush = CreateFrozenSolidBrush(Color.FromArgb(160, 235, 87, 87));
    public static readonly Pen DefectNgPen = CreateFrozenPen(Brushes.Crimson, 2.0);
    public static readonly Pen GridLinePen = CreateFrozenPen(new SolidColorBrush(Color.FromRgb(45, 55, 72)), 1.0);

    private static Pen CreateFrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static Brush CreateFrozenSolidBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
```
*Benefits:* Frozen objects become thread-safe, bypass WPF dependency-tracking overhead, and communicate directly with the compositor.

### B. Snap to Pixels & Crisp Edges
When drawing 1px gridlines, axes, or ROI bounding boxes, sub-pixel anti-aliasing can cause blurred lines. Use `GuidelineSet` or set layout rounding:

```csharp
// Set on FrameworkElement:
UseLayoutRounding = true;
SnapsToDevicePixels = true;

// Or explicitly in DrawingContext for crisp 1px strokes:
var guidelines = new GuidelineSet();
guidelines.GuidelinesX.Add(xCoord + 0.5);
guidelines.GuidelinesY.Add(yCoord + 0.5);
dc.PushGuidelineSet(guidelines);
try
{
    dc.DrawLine(pen, new Point(xCoord, y1), new Point(xCoord, y2));
}
finally
{
    dc.Pop();
}
```

### C. Drawing Formatted Text Efficiently
When rendering axis labels, KPI badges, or defect identifiers on overlays, use `FormattedText` with pixels-per-dip awareness (`VisualTreeHelper.GetDpi`):

```csharp
var dpi = VisualTreeHelper.GetDpi(this);
var formattedText = new FormattedText(
    text,
    CultureInfo.InvariantCulture,
    FlowDirection.LeftToRight,
    new Typeface("Segoe UI"),
    11.0,
    Brushes.White,
    dpi.PixelsPerDip);

dc.DrawText(formattedText, new Point(x, y));
```

---

## 4. Optical Inspection (AOI) Defect ROI Overlay Pattern

For rendering bounding boxes from `roi.dat` / `defect.dat` over camera images:

1. **Normalized Coordinate Transformation**:
   Convert machine coordinate space `(X, Y, Width, Height)` to Viewport display coordinates using scale factor `scale = viewportWidth / imageWidth`.
2. **Clipping Boundary**:
   Use `dc.PushClip(new RectangleGeometry(new Rect(0, 0, RenderSize.Width, RenderSize.Height)))` to guarantee overlays never bleed outside the image viewport.
3. **Batching Direct Render Passes**:
   Draw defect rectangles in a single pass before releasing the `DrawingContext`.

```csharp
public void DrawDefectOverlays(DrawingContext dc, ReadOnlySpan<DefectRoi> rois, double scaleX, double scaleY)
{
    foreach (ref readonly var roi in rois)
    {
        var rect = new Rect(roi.X * scaleX, roi.Y * scaleY, roi.Width * scaleX, roi.Height * scaleY);
        
        // Draw filled semi-transparent highlight
        dc.DrawRectangle(RenderResources.DefectNgBrush, null, rect);
        
        // Draw crisp border stroke
        dc.DrawRectangle(null, RenderResources.DefectNgPen, rect);
    }
}
```

---

## 5. Checklist for DrawingContext Code Reviews

- [ ] Are all `Brush`, `Pen`, and `Geometry` instances created outside the draw loop and **frozen** via `.Freeze()`?
- [ ] Is layout rounding or `GuidelineSet` applied to prevent blurred 1px lines on charts?
- [ ] Does the rendering avoid allocating collections or closures inside `RenderOpen()` / `OnRender()`?
- [ ] Are coordinate transforms computed with fast value types (`Point`, `Rect`, `Matrix`) without boxing?
- [ ] Is `RenderOpen()` enclosed in a `using` statement or `try...finally` block?
