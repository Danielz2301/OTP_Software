using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Models;

namespace OTPSoftware.App.Views.Charts;

/// <summary>
/// Lightweight host element wrapping a single <see cref="DrawingVisual"/> for GPU-composited chart rendering.
/// </summary>
public sealed class ChartVisualHost : FrameworkElement
{
    private readonly DrawingVisual _visual = new();

    public ChartVisualHost()
    {
        AddVisualChild(_visual);
        AddLogicalChild(_visual);
        IsHitTestVisible = false;
    }

    public DrawingContext Open() => _visual.RenderOpen();

    public void Clear()
    {
        using var dc = _visual.RenderOpen();
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) =>
        index == 0 ? _visual : throw new ArgumentOutOfRangeException(nameof(index));
}

/// <summary>
/// Dedicated View-layer WPF renderer for a single chart <see cref="Canvas"/> instance.
/// Owns GPU <see cref="DrawingVisual"/> hosts, frozen brushes/pens, reusable hover <see cref="ToolTip"/>,
/// O(N) point hit-testing, and mouse interaction routing back to ViewModel callbacks.
/// </summary>
public sealed class ChartCanvasRenderer
{
    private readonly List<PlottedChartPoint> _plottedPoints = [];
    private Canvas? _boundCanvas;
    private ErrorChartPoint? _lastHoverPt;
    private Action<string>? _onPmntClick;
    private Action<string>? _onPmntRightClick;
    private Func<ErrorChartPoint, ChartTooltipContent>? _tooltipBuilder;
    private Func<bool>? _isPanningAccessor;
    private Func<double>? _zoomFactorAccessor;

    // Single reusable ToolTip per Canvas renderer to eliminate per-hover allocations
    private ToolTip? _sharedToolTip;
    private TextBlock? _ttHeader;
    private TextBlock? _ttBody;
    private TextBlock? _ttFooter;

    private static readonly SolidColorBrush[] PaletteA = CreatePalette(ChartPalette.GroupAHex);
    private static readonly SolidColorBrush[] PaletteB = CreatePalette(ChartPalette.GroupBHex);

    internal static readonly SolidColorBrush GridBrush = CreateFrozenBrush("#1E293B");
    internal static readonly SolidColorBrush AxisBrush = CreateFrozenBrush("#334155");
    internal static readonly SolidColorBrush TextMutedBrush = CreateFrozenBrush("#94A3B8");
    internal static readonly SolidColorBrush TextBrightBrush = CreateFrozenBrush("#F8FAFC");
    internal static readonly SolidColorBrush NodeStrokeBrush = CreateFrozenBrush("#0B0F19");
    internal static readonly SolidColorBrush BadgeBgBrush = CreateFrozenBrush("#E6111827");
    internal static readonly SolidColorBrush TooltipBgBrush = CreateFrozenBrush("#111827");
    internal static readonly SolidColorBrush ExportBgBrush = CreateFrozenBrush("#0B0F19");
    internal static readonly SolidColorBrush Cutoff80Brush = CreateFrozenBrush("#F59E0B");
    internal static readonly SolidColorBrush CutoffTagBgBrush = CreateFrozenBrush("#271A0C");
    internal static readonly SolidColorBrush GoldBrush = CreateFrozenBrush("#FBBF24");
    internal static readonly SolidColorBrush OkBrush = CreateFrozenBrush("#10B981");
    internal static readonly SolidColorBrush NgBrush = CreateFrozenBrush("#EF4444");
    internal static readonly SolidColorBrush ParetoBarABrush = CreateFrozenBrush("#38BDF8");
    internal static readonly SolidColorBrush ParetoBarBBrush = CreateFrozenBrush("#EF4444");

    private static readonly Pen AxisPen = CreateFrozenPen(AxisBrush, 1.5);
    private static readonly Pen GridHorizPen = CreateFrozenPen(GridBrush, 1.0, [4.0, 4.0]);
    private static readonly Pen GridVertPen = CreateFrozenPen(GridBrush, 1.0, [3.0, 5.0]);
    private static readonly Pen NodeStrokePen = CreateFrozenPen(NodeStrokeBrush, 1.5);
    private static readonly Pen Cutoff80Pen = CreateFrozenPen(Cutoff80Brush, 1.5, [4.0, 4.0]);
    private static readonly Pen GoldPen = CreateFrozenPen(GoldBrush, 2.0);
    private static readonly Dictionary<SolidColorBrush, Pen> SeriesLinePens = CreatePalettePenLookup(2.0, PenLineJoin.Round);
    private static readonly Dictionary<SolidColorBrush, Pen> SeriesHaloPens = CreatePalettePenLookup(1.2, PenLineJoin.Miter);

    /// <summary>
    /// Prepares a chart snapshot through <paramref name="viewModel"/> and renders it onto <paramref name="canvas"/>.
    /// </summary>
    public void Render(
        Canvas canvas,
        ChartViewModel viewModel,
        IReadOnlyList<UnitRecord> scopeUnits,
        Action<string>? onPmntClick = null,
        Action<string>? onPmntRightClick = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(scopeUnits);

        var snapshot = viewModel.BuildRenderSnapshot(scopeUnits);
        RenderSnapshot(
            canvas,
            snapshot,
            viewModel.BuildTooltipContent,
            () => viewModel.IsPanning,
            () => viewModel.ZoomFactor,
            onPmntClick,
            onPmntRightClick);
    }

    /// <summary>
    /// Renders a precomputed <see cref="ChartRenderSnapshot"/> onto <paramref name="canvas"/>.
    /// Set <paramref name="attachInteractiveHandlers"/> to <c>false</c> for offscreen PNG snapshots.
    /// </summary>
    public void RenderSnapshot(
        Canvas canvas,
        ChartRenderSnapshot snapshot,
        Func<ErrorChartPoint, ChartTooltipContent>? tooltipBuilder = null,
        Func<bool>? isPanningAccessor = null,
        Func<double>? zoomFactorAccessor = null,
        Action<string>? onPmntClick = null,
        Action<string>? onPmntRightClick = null,
        bool attachInteractiveHandlers = true)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);

        _onPmntClick = onPmntClick;
        _onPmntRightClick = onPmntRightClick;
        _tooltipBuilder = tooltipBuilder;
        _isPanningAccessor = isPanningAccessor;
        _zoomFactorAccessor = zoomFactorAccessor;

        if (attachInteractiveHandlers)
        {
            EnsureCanvasHandlers(canvas);
        }

        ClearCanvasVisuals(canvas);
        _plottedPoints.Clear();
        _lastHoverPt = null;

        if (snapshot.IsEmpty)
        {
            HideSharedToolTip(canvas);
            return;
        }

        switch (snapshot.Mode)
        {
            case ChartDisplayMode.DefectPareto when snapshot.ParetoData is not null && snapshot.ParetoLabels is not null:
                RenderParetoVisuals(canvas, snapshot.ParetoData, snapshot.ParetoLabels);
                break;

            case ChartDisplayMode.HourlyTrend when snapshot.HourlyData is not null && snapshot.HourlyLabels is not null:
                RenderHourlyVisuals(canvas, snapshot.HourlyData, snapshot.HourlyLabels);
                break;

            case ChartDisplayMode.CumulativeTimeline when snapshot.TimelineData is not null && snapshot.TimelineLabels is not null:
                RenderTimelineVisuals(canvas, snapshot, snapshot.TimelineData, snapshot.TimelineLabels);
                break;
        }
    }

    /// <summary>
    /// Resets any cached hover tooltip state (for example, right after switching UI language).
    /// </summary>
    public void InvalidateTooltip()
    {
        _lastHoverPt = null;
        if (_boundCanvas is not null)
        {
            HideSharedToolTip(_boundCanvas);
        }
    }

    /// <summary>
    /// Unsubscribes all Canvas mouse handlers and clears retained visual/hover state to prevent window leaks.
    /// </summary>
    public void Detach()
    {
        if (_boundCanvas is not null)
        {
            _boundCanvas.MouseLeftButtonDown -= OnCanvasMouseLeftButtonDown;
            _boundCanvas.MouseRightButtonDown -= OnCanvasMouseRightButtonDown;
            _boundCanvas.MouseMove -= OnCanvasMouseMove;
            _boundCanvas.MouseLeave -= OnCanvasMouseLeave;
            HideSharedToolTip(_boundCanvas);
            ClearCanvasVisuals(_boundCanvas);
            _boundCanvas = null;
        }

        _plottedPoints.Clear();
        _lastHoverPt = null;
        _onPmntClick = null;
        _onPmntRightClick = null;
        _tooltipBuilder = null;
        _isPanningAccessor = null;
        _zoomFactorAccessor = null;
    }

    public static SolidColorBrush GetPmntSeriesBrush(string? group, int seriesIndex = 0)
    {
        var palette = string.Equals(group, "B", StringComparison.OrdinalIgnoreCase) ? PaletteB : PaletteA;
        return palette[Math.Abs(seriesIndex) % palette.Length];
    }

    public PlottedChartPoint? FindNearestPoint(double x, double y, double maxDistance = 10.0)
    {
        if (_plottedPoints.Count == 0) return null;
        PlottedChartPoint? best = null;
        double bestDistSq = maxDistance * maxDistance;

        for (int i = _plottedPoints.Count - 1; i >= 0; i--)
        {
            var pt = _plottedPoints[i];
            double dx = pt.PlotX - x;
            double dy = pt.PlotY - y;
            double dSq = (dx * dx) + (dy * dy);
            if (dSq <= bestDistSq)
            {
                bestDistSq = dSq;
                best = pt;
            }
        }
        return best;
    }

    public static string ResolveClickedPmnt(DependencyObject? sourceElement, DependencyObject? stopElement)
    {
        var cur = sourceElement;
        while (cur is not null && !ReferenceEquals(cur, stopElement))
        {
            if (cur is FrameworkElement { Tag: string tag } && !string.IsNullOrWhiteSpace(tag))
            {
                return tag;
            }
            var parent = VisualTreeHelper.GetParent(cur);
            if (parent is null && cur is FrameworkElement fe)
            {
                parent = fe.Parent;
            }
            cur = parent;
        }
        return string.Empty;
    }

    private static SolidColorBrush[] CreatePalette(string[] hexColors) =>
        hexColors.Select(CreateFrozenBrush).ToArray();

    private static SolidColorBrush CreateFrozenBrush(string hex)
    {
        var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(hex)!;
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }

    private static Pen CreateFrozenPen(Brush brush, double thickness, double[]? dashes = null)
    {
        var pen = new Pen(brush, thickness);
        if (dashes is not null)
        {
            var dc = new DoubleCollection(dashes);
            if (dc.CanFreeze) dc.Freeze();
            pen.DashStyle = new DashStyle(dc, 0.0);
        }
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static Dictionary<SolidColorBrush, Pen> CreatePalettePenLookup(double thickness, PenLineJoin lineJoin)
    {
        var map = new Dictionary<SolidColorBrush, Pen>();
        foreach (var brush in PaletteA.Concat(PaletteB))
        {
            if (map.ContainsKey(brush)) continue;
            var pen = new Pen(brush, thickness) { LineJoin = lineJoin };
            if (pen.CanFreeze) pen.Freeze();
            map[brush] = pen;
        }
        return map;
    }

    private static TextBlock ApplyTabularNumerals(TextBlock tb)
    {
        Typography.SetNumeralAlignment(tb, FontNumeralAlignment.Tabular);
        Typography.SetNumeralStyle(tb, FontNumeralStyle.Lining);
        return tb;
    }

    private ToolTip EnsureSharedToolTip()
    {
        if (_sharedToolTip is not null) return _sharedToolTip;

        _ttHeader = new TextBlock
        {
            FontWeight = FontWeights.Bold,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 4)
        };
        _ttBody = new TextBlock
        {
            Foreground = TextBrightBrush,
            FontSize = 12,
            Margin = new Thickness(0, 1, 0, 1),
            LineHeight = 18.0
        };
        _ttFooter = new TextBlock
        {
            Foreground = TextMutedBrush,
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0)
        };

        var sp = new StackPanel();
        sp.Children.Add(_ttHeader);
        sp.Children.Add(_ttBody);
        sp.Children.Add(_ttFooter);

        _sharedToolTip = new ToolTip
        {
            Background = TooltipBgBrush,
            BorderThickness = new Thickness(1.5),
            Padding = new Thickness(10, 8, 10, 8),
            Content = sp
        };
        return _sharedToolTip;
    }

    private void UpdateOrShowSharedToolTip(Canvas c, ChartTooltipContent content, Brush accent)
    {
        var tt = EnsureSharedToolTip();
        tt.BorderBrush = accent;
        _ttHeader!.Foreground = accent;
        _ttHeader.Text = content.Header;
        _ttBody!.Text = content.Body;
        _ttFooter!.Text = content.Footer;

        tt.PlacementTarget = c;
        tt.Placement = PlacementMode.Mouse;
        if (c.ToolTip is ToolTip oldTt && !ReferenceEquals(oldTt, tt)) oldTt.IsOpen = false;
        c.ToolTip = tt;
        tt.IsOpen = true;
    }

    private void HideSharedToolTip(Canvas c)
    {
        if (_sharedToolTip is not null)
        {
            _sharedToolTip.IsOpen = false;
            _sharedToolTip.PlacementTarget = null;
        }
        if (c.ToolTip is ToolTip oldTt)
        {
            oldTt.IsOpen = false;
            c.ToolTip = null;
        }
    }

    private void EnsureCanvasHandlers(Canvas canvas)
    {
        if (ReferenceEquals(_boundCanvas, canvas)) return;

        Detach();

        canvas.MouseLeftButtonDown += OnCanvasMouseLeftButtonDown;
        canvas.MouseRightButtonDown += OnCanvasMouseRightButtonDown;
        canvas.MouseMove += OnCanvasMouseMove;
        canvas.MouseLeave += OnCanvasMouseLeave;

        _boundCanvas = canvas;
    }

    private void OnCanvasMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_onPmntClick is null || sender is not Canvas c) return;
        string pmnt = ResolveClickedPmnt(e.OriginalSource as DependencyObject, c);
        if (string.IsNullOrWhiteSpace(pmnt) && _plottedPoints.Count > 0)
        {
            var pos = e.GetPosition(c);
            var hit = FindNearestPoint(pos.X, pos.Y, 10.0);
            if (hit is not null) pmnt = hit.Point.Pmnt;
        }
        if (!string.IsNullOrWhiteSpace(pmnt))
        {
            e.Handled = true;
            _onPmntClick(pmnt);
        }
    }

    private void OnCanvasMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_onPmntRightClick is null || sender is not Canvas c) return;
        string pmnt = ResolveClickedPmnt(e.OriginalSource as DependencyObject, c);
        if (string.IsNullOrWhiteSpace(pmnt) && _plottedPoints.Count > 0)
        {
            var pos = e.GetPosition(c);
            var hit = FindNearestPoint(pos.X, pos.Y, 10.0);
            if (hit is not null) pmnt = hit.Point.Pmnt;
        }
        if (!string.IsNullOrWhiteSpace(pmnt))
        {
            e.Handled = true;
            _onPmntRightClick(pmnt);
        }
    }

    private void OnCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_isPanningAccessor?.Invoke() == true || _plottedPoints.Count == 0 || sender is not Canvas c) return;
        var pos = e.GetPosition(c);
        var hit = FindNearestPoint(pos.X, pos.Y, 10.0);
        if (hit is not null && _tooltipBuilder is not null)
        {
            c.Cursor = Cursors.Hand;
            if (!ReferenceEquals(_lastHoverPt, hit.Point))
            {
                _lastHoverPt = hit.Point;
                var tooltipContent = _tooltipBuilder(hit.Point);
                var accent = GetPmntSeriesBrush(tooltipContent.Group, 0);
                UpdateOrShowSharedToolTip(c, tooltipContent, accent);
            }
        }
        else
        {
            if (_lastHoverPt is not null)
            {
                HideSharedToolTip(c);
                _lastHoverPt = null;
            }
            double zoom = _zoomFactorAccessor?.Invoke() ?? 1.0;
            c.Cursor = zoom > 1.01 ? Cursors.SizeAll : Cursors.Arrow;
        }
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Canvas c)
        {
            HideSharedToolTip(c);
        }
        _lastHoverPt = null;
    }

    private static (double Width, double Height) ResolveCanvasDimensions(Canvas canvas)
    {
        double w;
        double h;
        if (canvas.Parent is FrameworkElement container && container.ActualWidth > 80 && container.ActualHeight > 80)
        {
            w = container.ActualWidth - 2.0;
            h = container.ActualHeight - 2.0;
        }
        else
        {
            w = canvas.ActualWidth > 0 ? canvas.ActualWidth : canvas.Width;
            h = canvas.ActualHeight > 0 ? canvas.ActualHeight : canvas.Height;
        }
        return (w, h);
    }

    private static void ClearCanvasVisuals(Canvas canvas)
    {
        for (int i = 0; i < canvas.Children.Count; i++)
        {
            if (canvas.Children[i] is ChartVisualHost host)
            {
                host.Clear();
            }
        }
        canvas.Children.Clear();
    }

    private void RenderTimelineVisuals(
        Canvas canvas,
        ChartRenderSnapshot snapshot,
        ErrorChartData chartData,
        ChartTimelineLabels labels)
    {
        var (w, h) = ResolveCanvasDimensions(canvas);
        if (w < 120 || h < 100 || double.IsNaN(w) || double.IsNaN(h)) return;

        double zoom = Math.Clamp(snapshot.ZoomFactor, 1.0, 8.0);
        canvas.Cursor = zoom > 1.01 ? Cursors.SizeAll : Cursors.Arrow;

        const double padLeft = 64.0;
        const double padRight = 92.0;
        const double padTop = 34.0;
        const double padBottom = 52.0;

        double plotW = Math.Max(50.0, w - padLeft - padRight);
        double plotH = Math.Max(50.0, h - padTop - padBottom);

        int maxY = Math.Max(1, chartData.MaxErrorCount);
        int stepY = maxY <= 10 ? 1 : (int)Math.Ceiling(maxY / 8.0);
        int axisMaxY = (int)(Math.Ceiling(maxY / (double)stepY) * stepY);
        if (axisMaxY < maxY) axisMaxY = maxY;

        DateTime minDt = chartData.MinTime ?? DateTime.Now;
        DateTime maxDt = chartData.MaxTime ?? minDt;
        long spanTicks = maxDt.Ticks - minDt.Ticks;
        if (spanTicks <= 0L)
        {
            minDt = minDt.AddMinutes(-5);
            maxDt = maxDt.AddMinutes(5);
            spanTicks = maxDt.Ticks - minDt.Ticks;
        }
        else
        {
            long padTicks = (long)(spanTicks * 0.03);
            if (padTicks < TimeSpan.FromSeconds(5).Ticks)
            {
                padTicks = TimeSpan.FromSeconds(5).Ticks;
            }
            minDt = minDt.AddTicks(-padTicks);
            maxDt = maxDt.AddTicks(padTicks);
            spanTicks = maxDt.Ticks - minDt.Ticks;
        }

        var visualHost = new ChartVisualHost();
        canvas.Children.Add(visualHost);
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines & integer labels
        for (int yVal = 0; yVal <= axisMaxY; yVal += stepY)
        {
            double ratioY = yVal / (double)axisMaxY;
            double py = padTop + plotH - (ratioY * plotH);

            var hPen = yVal == 0 ? AxisPen : GridHorizPen;
            dc.DrawLine(hPen, new Point(padLeft, py), new Point(padLeft + plotW, py));

            var yLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = yVal.ToString(CultureInfo.InvariantCulture),
                Foreground = TextMutedBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Width = padLeft - 10,
                TextAlignment = TextAlignment.Right
            });
            Canvas.SetLeft(yLbl, 0);
            Canvas.SetTop(yLbl, py - 8);
            canvas.Children.Add(yLbl);
        }

        // 2. Vertical X-Axis gridlines & time tick labels
        int xTicks = Math.Min(24, Math.Max(4, (int)Math.Floor(plotW / 130.0)));

        for (int i = 0; i <= xTicks; i++)
        {
            double ratioX = i / (double)xTicks;
            double px = padLeft + (ratioX * plotW);
            DateTime tickDt = minDt.AddTicks((long)(spanTicks * ratioX));

            var vPen = i == 0 ? AxisPen : GridVertPen;
            dc.DrawLine(vPen, new Point(px, padTop), new Point(px, padTop + plotH));

            var xLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = tickDt.ToString(labels.XTickTimeFormat, CultureInfo.InvariantCulture),
                Foreground = TextMutedBrush,
                FontSize = 11,
                Width = 90,
                TextAlignment = TextAlignment.Center
            });
            Canvas.SetLeft(xLbl, px - 45);
            Canvas.SetTop(xLbl, padTop + plotH + 8);
            canvas.Children.Add(xLbl);
        }

        // 3. Axis titles
        var yTitle = new TextBlock
        {
            Text = labels.YAxisTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(yTitle, 8);
        Canvas.SetTop(yTitle, 6);
        canvas.Children.Add(yTitle);

        var xTitle = new TextBlock
        {
            Text = labels.XAxisTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Width = plotW,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(xTitle, padLeft);
        Canvas.SetTop(xTitle, padTop + plotH + 28);
        canvas.Children.Add(xTitle);

        // Map Series -> Brush and compute pixel coordinates for all points
        var seriesBrushes = new Dictionary<string, SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        var coordsByPoint = new Dictionary<ErrorChartPoint, Point>(chartData.Points.Count);
        int idxA = 0;
        int idxB = 0;

        foreach (var s in chartData.Series)
        {
            var b = string.Equals(s.Group, "A", StringComparison.OrdinalIgnoreCase)
                ? GetPmntSeriesBrush("A", idxA++)
                : GetPmntSeriesBrush(s.Group, idxB++);
            seriesBrushes[s.Pmnt] = b;

            foreach (var pt in s.Points)
            {
                double rx = (pt.Timestamp.Ticks - minDt.Ticks) / (double)spanTicks;
                double ry = pt.ErrorCount / (double)axisMaxY;
                double px = padLeft + (rx * plotW);
                double py = padTop + plotH - (ry * plotH);
                coordsByPoint[pt] = new Point(px, py);
            }
        }

        // 4. Draw connecting lines per PMNT via StreamGeometry on DrawingVisual
        if (snapshot.ShowLines)
        {
            foreach (var s in chartData.Series)
            {
                if (s.Points.Count < 2) continue;
                var sBrush = seriesBrushes[s.Pmnt];
                if (!SeriesLinePens.TryGetValue(sBrush, out var linePen))
                {
                    linePen = CreateFrozenPen(sBrush, 2.0);
                }

                var geom = new StreamGeometry();
                using (var gCtx = geom.Open())
                {
                    var firstCoord = coordsByPoint[s.Points[0]];
                    gCtx.BeginFigure(firstCoord, isFilled: false, isClosed: false);
                    for (int pIdx = 1; pIdx < s.Points.Count; pIdx++)
                    {
                        gCtx.LineTo(coordsByPoint[s.Points[pIdx]], isStroked: true, isSmoothJoin: true);
                    }
                }
                if (geom.CanFreeze) geom.Freeze();

                dc.PushOpacity(0.68);
                dc.DrawGeometry(null, linePen, geom);
                dc.Pop();
            }
        }

        // 5. Draw intersection points (nodes) onto DrawingVisual & collision-aware PMNT badges
        var placedBoxes = new List<Rect>();
        int badgeThreshold = snapshot.VisualThreshold > 0 ? snapshot.VisualThreshold : 150;
        bool isHighDensity = chartData.TotalErrors > badgeThreshold;
        bool showAllPointLabels = !isHighDensity &&
            (zoom >= 1.5 || chartData.TotalErrors <= 65 || chartData.UniquePmntCount <= 3);

        foreach (var pt in chartData.Points)
        {
            if (!coordsByPoint.TryGetValue(pt, out var centerPt))
            {
                double rx = (pt.Timestamp.Ticks - minDt.Ticks) / (double)spanTicks;
                double ry = pt.ErrorCount / (double)axisMaxY;
                centerPt = new Point(padLeft + (rx * plotW), padTop + plotH - (ry * plotH));
            }

            _plottedPoints.Add(new PlottedChartPoint(pt, centerPt.X, centerPt.Y));

            if (!seriesBrushes.TryGetValue(pt.Pmnt, out var brush))
            {
                brush = GetPmntSeriesBrush(pt.Group, 0);
            }

            // Outer halo for the peak point of each PMNT
            if (pt.IsPeakForPmnt && pt.TotalPmntError >= 2)
            {
                if (!SeriesHaloPens.TryGetValue(brush, out var haloPen))
                {
                    haloPen = CreateFrozenPen(brush, 1.2);
                }
                dc.PushOpacity(0.45);
                dc.DrawEllipse(null, haloPen, centerPt, 8.0, 8.0);
                dc.Pop();
            }

            double radius = pt.IsPeakForPmnt ? 5.0 : 4.25;
            dc.DrawEllipse(brush, NodeStrokePen, centerPt, radius, radius);

            bool shouldLabel = snapshot.ShowLabels && (
                (isHighDensity && pt.IsPeakForPmnt) ||
                (!isHighDensity && (showAllPointLabels || pt.IsPeakForPmnt || pt.ErrorCount == 1)));

            if (shouldLabel)
            {
                string labelStr = pt.IsPeakForPmnt && pt.TotalPmntError > 1
                    ? $"{pt.Pmnt} ({pt.ErrorCount})"
                    : pt.Pmnt;

                double estW = (labelStr.Length * 6.5) + 10.0;
                const double estH = 18.0;
                double px = centerPt.X;
                double py = centerPt.Y;

                Span<Point> candidates =
                [
                    new Point(px + 7.0, py - 19.0),
                    new Point(px - estW - 7.0, py - 19.0),
                    new Point(px + 7.0, py + 4.0),
                    new Point(px - estW - 7.0, py + 4.0),
                    new Point(px - (estW / 2.0), py - 23.0),
                    new Point(px - (estW / 2.0), py + 7.0)
                ];

                double minLabelX = padLeft + 2.0;
                double maxLabelX = Math.Max(minLabelX, w - estW - 4.0);
                double minLabelY = 4.0;
                double maxLabelY = Math.Max(minLabelY, padTop + plotH - estH);

                var bestPos = new Point(
                    Math.Clamp(candidates[0].X, minLabelX, maxLabelX),
                    Math.Clamp(candidates[0].Y, minLabelY, maxLabelY));
                foreach (var cand in candidates)
                {
                    double cx = Math.Clamp(cand.X, minLabelX, maxLabelX);
                    double cy = Math.Clamp(cand.Y, minLabelY, maxLabelY);
                    var rect = new Rect(cx, cy, estW, estH);

                    bool overlaps = false;
                    foreach (var box in placedBoxes)
                    {
                        if (rect.IntersectsWith(box))
                        {
                            overlaps = true;
                            break;
                        }
                    }
                    if (!overlaps)
                    {
                        bestPos = new Point(cx, cy);
                        break;
                    }
                }

                placedBoxes.Add(new Rect(bestPos.X, bestPos.Y, estW, estH));

                var bdr = new Border
                {
                    Background = BadgeBgBrush,
                    BorderBrush = brush,
                    BorderThickness = new Thickness(1.0),
                    CornerRadius = new CornerRadius(4.0),
                    Padding = new Thickness(4, 1, 4, 1),
                    Cursor = Cursors.Hand,
                    Tag = pt.Pmnt,
                    Child = ApplyTabularNumerals(new TextBlock
                    {
                        Text = labelStr,
                        Foreground = brush,
                        FontSize = 10.5,
                        FontWeight = pt.IsPeakForPmnt ? FontWeights.Bold : FontWeights.SemiBold
                    })
                };

                Canvas.SetLeft(bdr, bestPos.X);
                Canvas.SetTop(bdr, bestPos.Y);
                canvas.Children.Add(bdr);
            }
        }
    }

    private static void RenderParetoVisuals(
        Canvas canvas,
        ParetoChartData paretoData,
        ChartParetoLabels labels)
    {
        var (w, h) = ResolveCanvasDimensions(canvas);
        if (w < 120 || h < 100 || double.IsNaN(w) || double.IsNaN(h)) return;

        canvas.Cursor = Cursors.Arrow;

        const double padLeft = 60.0;
        const double padRight = 60.0;
        const double padTop = 38.0;
        const double padBottom = 54.0;

        double plotW = Math.Max(50.0, w - padLeft - padRight);
        double plotH = Math.Max(50.0, h - padTop - padBottom);

        int maxY = Math.Max(1, paretoData.MaxCount);
        int stepY = maxY <= 10 ? 1 : (int)Math.Ceiling(maxY / 8.0);
        int axisMaxY = (int)(Math.Ceiling(maxY / (double)stepY) * stepY);
        if (axisMaxY < maxY) axisMaxY = maxY;

        var visualHost = new ChartVisualHost();
        canvas.Children.Add(visualHost);
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines (Dual Y-axis: Left Count, Right %)
        for (int pct = 0; pct <= 100; pct += 20)
        {
            double ratio = pct / 100.0;
            double py = padTop + plotH - (ratio * plotH);

            var pen = pct == 0 ? AxisPen : GridHorizPen;
            dc.DrawLine(pen, new Point(padLeft, py), new Point(padLeft + plotW, py));

            int countVal = (int)Math.Round(ratio * axisMaxY);
            var leftLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = countVal.ToString(CultureInfo.InvariantCulture),
                Foreground = TextMutedBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Width = padLeft - 10,
                TextAlignment = TextAlignment.Right
            });
            Canvas.SetLeft(leftLbl, 0);
            Canvas.SetTop(leftLbl, py - 8);
            canvas.Children.Add(leftLbl);

            var rightLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = $"{pct}%",
                Foreground = TextMutedBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Width = padRight - 8,
                TextAlignment = TextAlignment.Left
            });
            Canvas.SetLeft(rightLbl, padLeft + plotW + 8);
            Canvas.SetTop(rightLbl, py - 8);
            canvas.Children.Add(rightLbl);
        }

        // 2. Horizontal 80% Cutoff Reference Line
        double py80 = padTop + plotH - (0.80 * plotH);
        dc.DrawLine(Cutoff80Pen, new Point(padLeft, py80), new Point(padLeft + plotW, py80));

        var cutoffTag = new Border
        {
            Background = CutoffTagBgBrush,
            BorderBrush = Cutoff80Brush,
            BorderThickness = new Thickness(1.0),
            CornerRadius = new CornerRadius(4.0),
            Padding = new Thickness(4, 1, 4, 1),
            Child = ApplyTabularNumerals(new TextBlock
            {
                Text = labels.CutoffTag,
                Foreground = Cutoff80Brush,
                FontSize = 10,
                FontWeight = FontWeights.Bold
            })
        };
        cutoffTag.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(cutoffTag, padLeft + plotW - cutoffTag.DesiredSize.Width - 6);
        Canvas.SetTop(cutoffTag, py80 - (cutoffTag.DesiredSize.Height / 2.0));
        canvas.Children.Add(cutoffTag);

        // 3. Axis titles
        var yLeftTitle = new TextBlock
        {
            Text = labels.YLeftTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(yLeftTitle, 8);
        Canvas.SetTop(yLeftTitle, 6);
        canvas.Children.Add(yLeftTitle);

        var yRightTitle = new TextBlock
        {
            Text = labels.YRightTitle,
            Foreground = Cutoff80Brush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            Width = 220
        };
        Canvas.SetLeft(yRightTitle, padLeft + plotW + padRight - 220 - 8);
        Canvas.SetTop(yRightTitle, 6);
        canvas.Children.Add(yRightTitle);

        var xTitle = new TextBlock
        {
            Text = labels.XAxisTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Width = plotW,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(xTitle, padLeft);
        Canvas.SetTop(xTitle, padTop + plotH + 30);
        canvas.Children.Add(xTitle);

        // 4. Draw Bars and collect points for Cumulative line
        int n = paretoData.Items.Count;
        double slotW = plotW / n;
        double barW = Math.Max(14.0, Math.Min(48.0, slotW * 0.65));
        var cumPoints = new Point[n];

        for (int i = 0; i < n; i++)
        {
            var item = paretoData.Items[i];
            double slotCenter = padLeft + (i * slotW) + (slotW / 2.0);
            double barLeft = slotCenter - (barW / 2.0);
            double barH = (item.Count / (double)axisMaxY) * plotH;
            double barTop = padTop + plotH - barH;

            var barBrush = string.Equals(item.Group, "B", StringComparison.OrdinalIgnoreCase)
                ? ParetoBarBBrush
                : ParetoBarABrush;

            dc.DrawRoundedRectangle(barBrush, null, new Rect(barLeft, barTop, barW, barH), 4.0, 4.0);

            var countLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = $"{item.Count} ({item.Percentage:F1}%)",
                Foreground = TextBrightBrush,
                FontSize = 10.5,
                FontWeight = FontWeights.SemiBold,
                Width = slotW + 12,
                TextAlignment = TextAlignment.Center
            });
            Canvas.SetLeft(countLbl, slotCenter - ((slotW + 12) / 2.0));
            Canvas.SetTop(countLbl, Math.Max(padTop - 18, barTop - 18));
            canvas.Children.Add(countLbl);

            var badge = new Border
            {
                Background = BadgeBgBrush,
                BorderBrush = barBrush,
                BorderThickness = new Thickness(1.0),
                CornerRadius = new CornerRadius(4.0),
                Padding = new Thickness(6, 1, 6, 1),
                Cursor = Cursors.Hand,
                Tag = item.Label,
                Child = new TextBlock
                {
                    Text = item.Label,
                    Foreground = barBrush,
                    FontSize = 11,
                    FontWeight = FontWeights.Bold
                }
            };
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double badgeWidth = badge.DesiredSize.Width > 0 ? badge.DesiredSize.Width : (item.Label.Length * 7.2) + 14.0;
            Canvas.SetLeft(badge, slotCenter - (badgeWidth / 2.0));
            Canvas.SetTop(badge, padTop + plotH + 8);
            canvas.Children.Add(badge);

            double ratioCum = Math.Clamp(item.CumulativePercentage / 100.0, 0.0, 1.0);
            double pyCum = padTop + plotH - (ratioCum * plotH);
            cumPoints[i] = new Point(slotCenter, pyCum);
        }

        // 5. Draw Cumulative % Polyline via StreamGeometry
        if (n > 1)
        {
            var geom = new StreamGeometry();
            using (var gCtx = geom.Open())
            {
                gCtx.BeginFigure(cumPoints[0], isFilled: false, isClosed: false);
                for (int pIdx = 1; pIdx < n; pIdx++)
                {
                    gCtx.LineTo(cumPoints[pIdx], isStroked: true, isSmoothJoin: true);
                }
            }
            if (geom.CanFreeze) geom.Freeze();
            dc.DrawGeometry(null, GoldPen, geom);
        }

        // 6. Draw Cumulative Node Circles & percentage badges
        for (int i = 0; i < n; i++)
        {
            dc.DrawEllipse(GoldBrush, NodeStrokePen, cumPoints[i], 4.5, 4.5);

            var pctLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = $"{paretoData.Items[i].CumulativePercentage:F0}%",
                Foreground = GoldBrush,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                Width = 42,
                TextAlignment = TextAlignment.Center
            });
            Canvas.SetLeft(pctLbl, cumPoints[i].X - 21);
            Canvas.SetTop(pctLbl, cumPoints[i].Y - 16);
            canvas.Children.Add(pctLbl);
        }
    }

    private static void RenderHourlyVisuals(
        Canvas canvas,
        HourlyTrendData hourlyData,
        ChartHourlyLabels labels)
    {
        var (w, h) = ResolveCanvasDimensions(canvas);
        if (w < 120 || h < 100 || double.IsNaN(w) || double.IsNaN(h)) return;

        canvas.Cursor = Cursors.Arrow;

        const double padLeft = 60.0;
        const double padRight = 60.0;
        const double padTop = 38.0;
        const double padBottom = 54.0;

        double plotW = Math.Max(50.0, w - padLeft - padRight);
        double plotH = Math.Max(50.0, h - padTop - padBottom);

        int maxY = Math.Max(10, hourlyData.MaxHourlyTotal);
        int stepY = maxY <= 10 ? 2 : (int)Math.Ceiling(maxY / 6.0);
        int axisMaxY = (int)(Math.Ceiling(maxY / (double)stepY) * stepY);
        if (axisMaxY < maxY) axisMaxY = maxY;

        double maxRate = Math.Max(5.0, Math.Ceiling(hourlyData.MaxNgRatePercent * 1.2));
        if (maxRate > 100.0) maxRate = 100.0;

        var visualHost = new ChartVisualHost();
        canvas.Children.Add(visualHost);
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines (Dual Y-axis: Left Throughput, Right NG Rate %)
        for (int step = 0; step <= 5; step++)
        {
            double ratio = step / 5.0;
            double py = padTop + plotH - (ratio * plotH);

            var pen = step == 0 ? AxisPen : GridHorizPen;
            dc.DrawLine(pen, new Point(padLeft, py), new Point(padLeft + plotW, py));

            int yVal = (int)Math.Round(ratio * axisMaxY);
            var leftLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = yVal.ToString(CultureInfo.InvariantCulture),
                Foreground = TextMutedBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Width = padLeft - 10,
                TextAlignment = TextAlignment.Right
            });
            Canvas.SetLeft(leftLbl, 0);
            Canvas.SetTop(leftLbl, py - 8);
            canvas.Children.Add(leftLbl);

            double rateVal = ratio * maxRate;
            var rightLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = $"{rateVal:F1}%",
                Foreground = TextMutedBrush,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Width = padRight - 8,
                TextAlignment = TextAlignment.Left
            });
            Canvas.SetLeft(rightLbl, padLeft + plotW + 8);
            Canvas.SetTop(rightLbl, py - 8);
            canvas.Children.Add(rightLbl);
        }

        // 2. Axis titles
        var yLeftTitle = new TextBlock
        {
            Text = labels.YLeftTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold
        };
        Canvas.SetLeft(yLeftTitle, 8);
        Canvas.SetTop(yLeftTitle, 6);
        canvas.Children.Add(yLeftTitle);

        var yRightTitle = new TextBlock
        {
            Text = labels.YRightTitle,
            Foreground = NgBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Right,
            Width = 220
        };
        Canvas.SetLeft(yRightTitle, padLeft + plotW + padRight - 220 - 8);
        Canvas.SetTop(yRightTitle, 6);
        canvas.Children.Add(yRightTitle);

        var xTitle = new TextBlock
        {
            Text = labels.XAxisTitle,
            Foreground = TextMutedBrush,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Width = plotW,
            TextAlignment = TextAlignment.Center
        };
        Canvas.SetLeft(xTitle, padLeft);
        Canvas.SetTop(xTitle, padTop + plotH + 30);
        canvas.Children.Add(xTitle);

        // 3. Stacked Bars per hour bucket
        int n = hourlyData.Buckets.Count;
        double slotW = plotW / n;
        double barW = Math.Max(12.0, Math.Min(44.0, slotW * 0.65));
        var ratePoints = new List<Point>(n);

        for (int i = 0; i < n; i++)
        {
            var b = hourlyData.Buckets[i];
            double slotCenter = padLeft + (i * slotW) + (slotW / 2.0);
            double barLeft = slotCenter - (barW / 2.0);

            double okH = (b.OkCount / (double)axisMaxY) * plotH;
            double okTop = padTop + plotH - okH;
            if (okH > 0)
            {
                dc.DrawRoundedRectangle(OkBrush, null, new Rect(barLeft, okTop, barW, okH), 4.0, 4.0);
            }

            double ngH = (b.NgCount / (double)axisMaxY) * plotH;
            double ngTop = okTop - ngH;
            if (ngH > 0)
            {
                dc.DrawRoundedRectangle(NgBrush, null, new Rect(barLeft, ngTop, barW, ngH), 4.0, 4.0);
            }

            if (b.TotalCount > 0)
            {
                var totalLbl = ApplyTabularNumerals(new TextBlock
                {
                    Text = b.TotalCount.ToString(CultureInfo.InvariantCulture),
                    Foreground = TextBrightBrush,
                    FontSize = 10,
                    FontWeight = FontWeights.SemiBold,
                    Width = slotW + 8,
                    TextAlignment = TextAlignment.Center
                });
                Canvas.SetLeft(totalLbl, slotCenter - ((slotW + 8) / 2.0));
                Canvas.SetTop(totalLbl, Math.Max(padTop - 16, ngTop - 16));
                canvas.Children.Add(totalLbl);
            }

            var xLbl = ApplyTabularNumerals(new TextBlock
            {
                Text = b.HourLabel,
                Foreground = TextMutedBrush,
                FontSize = 10.5,
                Width = slotW + 8,
                TextAlignment = TextAlignment.Center
            });
            Canvas.SetLeft(xLbl, slotCenter - ((slotW + 8) / 2.0));
            Canvas.SetTop(xLbl, padTop + plotH + 8);
            canvas.Children.Add(xLbl);

            if (b.TotalCount > 0)
            {
                double ratioRate = Math.Clamp(b.NgRatePercent / maxRate, 0.0, 1.0);
                double pyRate = padTop + plotH - (ratioRate * plotH);
                ratePoints.Add(new Point(slotCenter, pyRate));
            }
        }

        // 4. Draw NG Rate Line
        if (ratePoints.Count > 1)
        {
            var geom = new StreamGeometry();
            using (var gCtx = geom.Open())
            {
                gCtx.BeginFigure(ratePoints[0], isFilled: false, isClosed: false);
                for (int pIdx = 1; pIdx < ratePoints.Count; pIdx++)
                {
                    gCtx.LineTo(ratePoints[pIdx], isStroked: true, isSmoothJoin: true);
                }
            }
            if (geom.CanFreeze) geom.Freeze();
            dc.DrawGeometry(null, GoldPen, geom);
        }

        // 5. Draw NG Rate Nodes and % badges
        int activeIdx = 0;
        for (int i = 0; i < n; i++)
        {
            var b = hourlyData.Buckets[i];
            if (b.TotalCount <= 0) continue;

            var pt = ratePoints[activeIdx++];
            var nodeBrush = b.NgRatePercent > 2.0 ? NgBrush : GoldBrush;
            dc.DrawEllipse(nodeBrush, NodeStrokePen, pt, 4.0, 4.0);

            if (b.NgRatePercent > 0.0)
            {
                var rateLbl = ApplyTabularNumerals(new TextBlock
                {
                    Text = $"{b.NgRatePercent:F1}%",
                    Foreground = nodeBrush,
                    FontSize = 9.5,
                    FontWeight = FontWeights.Bold,
                    Width = 38,
                    TextAlignment = TextAlignment.Center
                });
                Canvas.SetLeft(rateLbl, pt.X - 19);
                Canvas.SetTop(rateLbl, pt.Y - 14);
                canvas.Children.Add(rateLbl);
            }
        }
    }
}

/// <summary>
/// WPF View-layer implementation of <see cref="IChartExportService"/> handling STA rendering,
/// <see cref="RenderTargetBitmap"/> encoding, offscreen zero-defects banner composition, and atomic PNG disk writes.
/// </summary>
public sealed class WpfChartExportService : IChartExportService
{
    public static WpfChartExportService Instance { get; } = new();

    [ModuleInitializer]
    internal static void RegisterDefault()
    {
        ChartExportServiceRegistry.Default ??= Instance;
    }

    public async Task<string> ExportChartElementAsync(string filePath, object? chartElement)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        if (chartElement is not FrameworkElement element)
            return string.Empty;

        byte[] pngBytes;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            pngBytes = await dispatcher.InvokeAsync(() => EncodeElementToPngBytes(element)).Task.ConfigureAwait(false);
        }
        else
        {
            pngBytes = EncodeElementToPngBytes(element);
        }

        if (pngBytes.Length == 0)
            return string.Empty;

        return await WriteFileAtomicAsync(filePath, pngBytes).ConfigureAwait(false);
    }

    public async Task<string> ExportOffscreenShiftChartAsync(
        string filePath,
        ChartRenderSnapshot snapshot,
        string zeroShiftTitle,
        string zeroShiftSubtitle)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(snapshot);

        byte[] pngBytes;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null)
        {
            pngBytes = dispatcher.CheckAccess()
                ? EncodeOffscreenPngBytes(snapshot, zeroShiftTitle, zeroShiftSubtitle)
                : await dispatcher.InvokeAsync(() =>
                    EncodeOffscreenPngBytes(snapshot, zeroShiftTitle, zeroShiftSubtitle)).Task.ConfigureAwait(false);
        }
        else if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA)
        {
            pngBytes = EncodeOffscreenPngBytes(snapshot, zeroShiftTitle, zeroShiftSubtitle);
        }
        else
        {
            var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var staThread = new Thread(() =>
            {
                try
                {
                    tcs.SetResult(EncodeOffscreenPngBytes(snapshot, zeroShiftTitle, zeroShiftSubtitle));
                }
                catch (Exception ex)
                {
                    tcs.SetException(ex);
                }
            })
            {
                IsBackground = true
            };
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            pngBytes = await tcs.Task.ConfigureAwait(false);
        }

        if (pngBytes.Length == 0)
            return string.Empty;

        return await WriteFileAtomicAsync(filePath, pngBytes).ConfigureAwait(false);
    }

    private static byte[] EncodeOffscreenPngBytes(
        ChartRenderSnapshot snapshot,
        string zeroShiftTitle,
        string zeroShiftSubtitle)
    {
        const double exportWidth = 1280.0;
        const double exportHeight = 680.0;

        var offscreenCanvas = new Canvas
        {
            Width = exportWidth,
            Height = exportHeight,
            SnapsToDevicePixels = true
        };

        var renderer = new ChartCanvasRenderer();
        try
        {
            renderer.RenderSnapshot(offscreenCanvas, snapshot, attachInteractiveHandlers: false);
            if (snapshot.IsEmpty)
            {
                AddOffscreenZeroDefectsBanner(
                    offscreenCanvas,
                    exportWidth,
                    exportHeight,
                    zeroShiftTitle,
                    zeroShiftSubtitle);
            }
            return EncodeElementToPngBytes(offscreenCanvas);
        }
        finally
        {
            renderer.Detach();
        }
    }

    private static void AddOffscreenZeroDefectsBanner(
        Canvas canvas,
        double width,
        double height,
        string title,
        string subtitle)
    {
        var panel = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center
        };

        panel.Children.Add(new TextBlock
        {
            Text = title,
            Foreground = ChartCanvasRenderer.TextMutedBrush,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 0, 0, 8)
        });

        panel.Children.Add(new TextBlock
        {
            Text = subtitle,
            Foreground = ChartCanvasRenderer.OkBrush,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            TextAlignment = TextAlignment.Center
        });

        var card = new Border
        {
            Background = ChartCanvasRenderer.BadgeBgBrush,
            BorderBrush = ChartCanvasRenderer.AxisBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(28, 18, 28, 18),
            Child = panel
        };

        card.Measure(new Size(width, height));
        double cardW = card.DesiredSize.Width;
        double cardH = card.DesiredSize.Height;
        Canvas.SetLeft(card, Math.Max(0, (width - cardW) / 2.0));
        Canvas.SetTop(card, Math.Max(0, (height - cardH) / 2.0));
        canvas.Children.Add(card);
    }

    private static byte[] EncodeElementToPngBytes(FrameworkElement element)
    {
        int width = (int)Math.Max(1, Math.Ceiling(element.ActualWidth > 0 ? element.ActualWidth : (element.Width > 0 ? element.Width : 1200)));
        int height = (int)Math.Max(1, Math.Ceiling(element.ActualHeight > 0 ? element.ActualHeight : (element.Height > 0 ? element.Height : 600)));

        if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            element.Measure(new Size(width, height));
            element.Arrange(new Rect(0, 0, width, height));
            element.UpdateLayout();
        }

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(ChartCanvasRenderer.ExportBgBrush, null, new Rect(0, 0, width, height));
        }

        var rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Render(element);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));

        using var ms = new MemoryStream();
        encoder.Save(ms);
        rtb.Clear();
        return ms.ToArray();
    }

    private static async Task<string> WriteFileAtomicAsync(string filePath, byte[] bytes)
    {
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string fileName = Path.GetFileName(filePath);
        string tempName = $".{fileName}.{Guid.NewGuid():N}.tmp";
        string tempPath = string.IsNullOrEmpty(dir) ? tempName : Path.Combine(dir, tempName);

        try
        {
            await File.WriteAllBytesAsync(tempPath, bytes).ConfigureAwait(false);
            File.Move(tempPath, filePath, overwrite: true);
        }
        catch
        {
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch
            {
            }
            throw;
        }

        return filePath;
    }
}

