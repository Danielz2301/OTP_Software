using System.Buffers;
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
        UseLayoutRounding = true;
        SnapsToDevicePixels = true;
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
    private static readonly Typeface RegularUiTypeface = CreateFrozenTypeface(FontWeights.Normal);
    private static readonly Typeface SemiBoldUiTypeface = CreateFrozenTypeface(FontWeights.SemiBold);
    private static readonly Typeface BoldUiTypeface = CreateFrozenTypeface(FontWeights.Bold);

    private readonly List<PlottedChartPoint> _plottedPoints = [];
    private readonly List<(Rect Bounds, string Pmnt)> _plottedHitRects = [];
    private readonly List<(Rect Bounds, HistogramBin Bin, string ParameterName)> _plottedHistogramBins = [];
    private Canvas? _boundCanvas;
    private ErrorChartPoint? _lastHoverPt;
    private HistogramBin? _lastHoverBin;
    private Rect? _spcCardBounds;
    private Action<string>? _onPmntClick;
    private Action<string>? _onPmntRightClick;
    private Func<ErrorChartPoint, ChartTooltipContent>? _tooltipBuilder;
    private Func<HistogramBin, string, ChartTooltipContent>? _spcTooltipBuilder;
    private Func<bool>? _isPanningAccessor;
    private Func<double>? _zoomFactorAccessor;

    private static Typeface CreateFrozenTypeface(FontWeight weight) =>
        new(new FontFamily("Segoe UI"), FontStyles.Normal, weight, FontStretches.Normal);

    private static FormattedText CreateFormattedText(
        string text,
        Brush foreground,
        double fontSize,
        FontWeight weight,
        double pixelsPerDip = 1.0)
    {
        Typeface typeface = weight == FontWeights.Bold
            ? BoldUiTypeface
            : weight == FontWeights.SemiBold
                ? SemiBoldUiTypeface
                : RegularUiTypeface;

        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            fontSize,
            foreground,
            pixelsPerDip);
        return ft;
    }

    internal IReadOnlyList<(Rect Bounds, string Pmnt)> PlottedHitRects => _plottedHitRects;

    internal IReadOnlyList<(Rect Bounds, HistogramBin Bin, string ParameterName)> PlottedHistogramBins => _plottedHistogramBins;

    public HistogramBin? FindHistogramBinAt(double x, double y)
    {
        for (int i = _plottedHistogramBins.Count - 1; i >= 0; i--)
        {
            if (_plottedHistogramBins[i].Bounds.Contains(x, y))
            {
                return _plottedHistogramBins[i].Bin;
            }
        }
        return null;
    }

    private (Rect Bounds, HistogramBin Bin, string ParameterName)? FindHistogramBinEntryAt(double x, double y)
    {
        for (int i = _plottedHistogramBins.Count - 1; i >= 0; i--)
        {
            if (_plottedHistogramBins[i].Bounds.Contains(x, y))
            {
                return _plottedHistogramBins[i];
            }
        }
        return null;
    }

    internal string FindHitRectPmnt(double x, double y)
    {
        for (int i = _plottedHitRects.Count - 1; i >= 0; i--)
        {
            if (_plottedHitRects[i].Bounds.Contains(x, y))
            {
                return _plottedHitRects[i].Pmnt;
            }
        }
        return string.Empty;
    }

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
    internal static readonly SolidColorBrush SpcLimitTagBgBrush = CreateFrozenBrush("#2B1117");
    internal static readonly SolidColorBrush SpcMeanTagBgBrush = CreateFrozenBrush("#0C2538");
    internal static readonly SolidColorBrush SpcMeanBrush = CreateFrozenBrush("#38BDF8");
    internal static readonly SolidColorBrush SpcGaussBrush = CreateFrozenBrush("#F59E0B");
    internal static readonly SolidColorBrush SpcCardBgBrush = CreateFrozenBrush("#EC0F172A");
    internal static readonly SolidColorBrush SpcCpkGreenBgBrush = CreateFrozenBrush("#0B221C");
    internal static readonly SolidColorBrush SpcCpkAmberBgBrush = CreateFrozenBrush("#271A0C");
    internal static readonly SolidColorBrush SpcCpkRedBgBrush = CreateFrozenBrush("#2B1117");
    private static readonly SolidColorBrush SpcBarOkFillBrush = CreateFrozenBrush("#D610B981");
    private static readonly SolidColorBrush SpcBarNgFillBrush = CreateFrozenBrush("#D6EF4444");

    private static readonly Pen AxisPen = CreateFrozenPen(AxisBrush, 1.5);
    private static readonly Pen GridHorizPen = CreateFrozenPen(GridBrush, 1.0, [4.0, 4.0]);
    private static readonly Pen GridVertPen = CreateFrozenPen(GridBrush, 1.0, [3.0, 5.0]);
    private static readonly Pen NodeStrokePen = CreateFrozenPen(NodeStrokeBrush, 1.5);
    private static readonly Pen Cutoff80Pen = CreateFrozenPen(Cutoff80Brush, 1.5, [4.0, 4.0]);
    private static readonly Pen GoldPen = CreateFrozenPen(GoldBrush, 2.0);
    private static readonly Pen Cutoff80BorderPen = CreateFrozenPen(Cutoff80Brush, 1.0);
    private static readonly Pen ParetoBarABorderPen = CreateFrozenPen(ParetoBarABrush, 1.0);
    private static readonly Pen ParetoBarBBorderPen = CreateFrozenPen(ParetoBarBBrush, 1.0);
    private static readonly Pen SpcSpecLimitPen = CreateFrozenPen(NgBrush, 1.8, [5.0, 4.0]);
    private static readonly Pen SpcSpecTagBorderPen = CreateFrozenPen(NgBrush, 1.0);
    private static readonly Pen SpcMeanLinePen = CreateFrozenPen(SpcMeanBrush, 1.3, [3.0, 3.0]);
    private static readonly Pen SpcMeanTagBorderPen = CreateFrozenPen(SpcMeanBrush, 1.0);
    private static readonly Pen SpcGaussCurvePen = CreateFrozenPen(SpcGaussBrush, 2.4, lineJoin: PenLineJoin.Round);
    private static readonly Pen SpcBarOkBorderPen = CreateFrozenPen(OkBrush, 1.0);
    private static readonly Pen SpcBarNgBorderPen = CreateFrozenPen(NgBrush, 1.0);
    private static readonly Pen SpcCardGreenBorderPen = CreateFrozenPen(OkBrush, 1.2);
    private static readonly Pen SpcCardAmberBorderPen = CreateFrozenPen(Cutoff80Brush, 1.2);
    private static readonly Pen SpcCardRedBorderPen = CreateFrozenPen(NgBrush, 1.2);
    private static readonly Pen SpcCardDefaultBorderPen = CreateFrozenPen(AxisBrush, 1.2);
    private static readonly Dictionary<SolidColorBrush, Pen> SeriesLinePens = CreatePalettePenLookup(2.0, PenLineJoin.Round);
    private static readonly Dictionary<SolidColorBrush, Pen> SeriesHaloPens = CreatePalettePenLookup(1.2, PenLineJoin.Miter);
    private static readonly Dictionary<SolidColorBrush, Pen> SeriesBadgeBorderPens = CreatePalettePenLookup(1.0, PenLineJoin.Miter);
    private static readonly Pen FallbackSeriesLinePen = CreateFrozenPen(TextBrightBrush, 2.0, lineJoin: PenLineJoin.Round);
    private static readonly Pen FallbackSeriesHaloPen = CreateFrozenPen(TextBrightBrush, 1.2);

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
            onPmntRightClick,
            attachInteractiveHandlers: true,
            spcTooltipBuilder: viewModel.BuildSpcTooltipContent);
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
        bool attachInteractiveHandlers = true,
        Func<HistogramBin, string, ChartTooltipContent>? spcTooltipBuilder = null)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(snapshot);

        _onPmntClick = onPmntClick;
        _onPmntRightClick = onPmntRightClick;
        _tooltipBuilder = tooltipBuilder;
        _spcTooltipBuilder = spcTooltipBuilder;
        _isPanningAccessor = isPanningAccessor;
        _zoomFactorAccessor = zoomFactorAccessor;

        if (attachInteractiveHandlers)
        {
            EnsureCanvasHandlers(canvas);
        }

        ClearCanvasVisuals(canvas);
        _plottedPoints.Clear();
        _plottedHitRects.Clear();
        _plottedHistogramBins.Clear();
        _lastHoverPt = null;
        _lastHoverBin = null;
        _spcCardBounds = null;

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

            case ChartDisplayMode.SpcHistogram when snapshot.SpcData is not null && snapshot.SpcLabels is not null:
                RenderSpcHistogramVisuals(canvas, snapshot, snapshot.SpcData, snapshot.SpcLabels);
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
        _lastHoverBin = null;
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
        _plottedHitRects.Clear();
        _plottedHistogramBins.Clear();
        _lastHoverPt = null;
        _lastHoverBin = null;
        _spcCardBounds = null;
        _onPmntClick = null;
        _onPmntRightClick = null;
        _tooltipBuilder = null;
        _spcTooltipBuilder = null;
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

    private static Pen CreateFrozenPen(
        Brush brush,
        double thickness,
        double[]? dashes = null,
        PenLineJoin lineJoin = PenLineJoin.Miter)
    {
        var pen = new Pen(brush, thickness) { LineJoin = lineJoin };
        if (dashes is not null)
        {
            var dc = new DoubleCollection(dashes);
            if (dc.CanFreeze) dc.Freeze();
            var dashStyle = new DashStyle(dc, 0.0);
            if (dashStyle.CanFreeze) dashStyle.Freeze();
            pen.DashStyle = dashStyle;
        }
        if (pen.CanFreeze) pen.Freeze();
        return pen;
    }

    private static void DrawCrispHorizontalLine(DrawingContext dc, Pen pen, double x1, double x2, double y)
    {
        double halfPen = pen.Thickness * 0.5;
        double snappedY = Math.Floor(y) + halfPen;

        var guidelines = new GuidelineSet();
        guidelines.GuidelinesY.Add(snappedY - halfPen);
        guidelines.GuidelinesY.Add(snappedY + halfPen);
        if (guidelines.CanFreeze) guidelines.Freeze();

        dc.PushGuidelineSet(guidelines);
        dc.DrawLine(pen, new Point(Math.Round(x1), snappedY), new Point(Math.Round(x2), snappedY));
        dc.Pop();
    }

    private static void DrawCrispVerticalLine(DrawingContext dc, Pen pen, double x, double y1, double y2)
    {
        double halfPen = pen.Thickness * 0.5;
        double snappedX = Math.Floor(x) + halfPen;

        var guidelines = new GuidelineSet();
        guidelines.GuidelinesX.Add(snappedX - halfPen);
        guidelines.GuidelinesX.Add(snappedX + halfPen);
        if (guidelines.CanFreeze) guidelines.Freeze();

        dc.PushGuidelineSet(guidelines);
        dc.DrawLine(pen, new Point(snappedX, Math.Round(y1)), new Point(snappedX, Math.Round(y2)));
        dc.Pop();
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
        var pos = e.GetPosition(c);
        string pmnt = FindHitRectPmnt(pos.X, pos.Y);
        if (string.IsNullOrWhiteSpace(pmnt))
        {
            pmnt = ResolveClickedPmnt(e.OriginalSource as DependencyObject, c);
        }
        if (string.IsNullOrWhiteSpace(pmnt) && _plottedPoints.Count > 0)
        {
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
        var pos = e.GetPosition(c);
        string pmnt = FindHitRectPmnt(pos.X, pos.Y);
        if (string.IsNullOrWhiteSpace(pmnt))
        {
            pmnt = ResolveClickedPmnt(e.OriginalSource as DependencyObject, c);
        }
        if (string.IsNullOrWhiteSpace(pmnt) && _plottedPoints.Count > 0)
        {
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
        if (_isPanningAccessor?.Invoke() == true ||
            (_plottedPoints.Count == 0 && _plottedHitRects.Count == 0 && _plottedHistogramBins.Count == 0) ||
            sender is not Canvas c)
        {
            return;
        }

        var pos = e.GetPosition(c);

        if (_spcCardBounds.HasValue && _spcCardBounds.Value.Contains(pos))
        {
            if (_lastHoverPt is not null || _lastHoverBin is not null)
            {
                HideSharedToolTip(c);
                _lastHoverPt = null;
                _lastHoverBin = null;
            }
            c.Cursor = Cursors.Arrow;
            return;
        }

        var hit = FindNearestPoint(pos.X, pos.Y, 10.0);
        if (hit is not null && _tooltipBuilder is not null)
        {
            c.Cursor = Cursors.Hand;
            _lastHoverBin = null;
            if (!ReferenceEquals(_lastHoverPt, hit.Point))
            {
                _lastHoverPt = hit.Point;
                var tooltipContent = _tooltipBuilder(hit.Point);
                var accent = GetPmntSeriesBrush(tooltipContent.Group, 0);
                UpdateOrShowSharedToolTip(c, tooltipContent, accent);
            }
            return;
        }

        var binHit = FindHistogramBinEntryAt(pos.X, pos.Y);
        if (binHit is { } entry)
        {
            c.Cursor = Cursors.Hand;
            _lastHoverPt = null;
            if (!ReferenceEquals(_lastHoverBin, entry.Bin))
            {
                _lastHoverBin = entry.Bin;
                var tooltipContent = _spcTooltipBuilder is not null
                    ? _spcTooltipBuilder(entry.Bin, entry.ParameterName)
                    : BuildDefaultSpcBinTooltip(entry.Bin, entry.ParameterName);
                var accent = entry.Bin.IsWithinSpec ? OkBrush : NgBrush;
                UpdateOrShowSharedToolTip(c, tooltipContent, accent);
            }
            return;
        }

        if (_lastHoverPt is not null || _lastHoverBin is not null)
        {
            HideSharedToolTip(c);
            _lastHoverPt = null;
            _lastHoverBin = null;
        }

        if (!string.IsNullOrWhiteSpace(FindHitRectPmnt(pos.X, pos.Y)))
        {
            c.Cursor = Cursors.Hand;
            return;
        }

        double zoom = _zoomFactorAccessor?.Invoke() ?? 1.0;
        c.Cursor = zoom > 1.01 ? Cursors.SizeAll : Cursors.Arrow;
    }

    private static ChartTooltipContent BuildDefaultSpcBinTooltip(HistogramBin bin, string parameterName)
    {
        string header = $"{parameterName}  •  Bin #{bin.BinIndex + 1}";
        string status = bin.IsWithinSpec ? "OK (In Spec)" : "NG (Out of Spec)";
        string body =
            $"Range: {bin.RangeLabel}\n" +
            $"Samples: {bin.Count.ToString("N0", CultureInfo.InvariantCulture)} ({bin.Percentage.ToString("F1", CultureInfo.InvariantCulture)}%)\n" +
            $"Status: {status}";
        return new ChartTooltipContent(header, body, "SPC Parameter Distribution", bin.IsWithinSpec ? "A" : "B");
    }

    private void OnCanvasMouseLeave(object sender, MouseEventArgs e)
    {
        if (sender is Canvas c)
        {
            HideSharedToolTip(c);
        }
        _lastHoverPt = null;
        _lastHoverBin = null;
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
        double pixelsPerDip = VisualTreeHelper.GetDpi(visualHost).PixelsPerDip;
        if (pixelsPerDip <= 0) pixelsPerDip = 1.0;
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines & integer labels
        for (int yVal = 0; yVal <= axisMaxY; yVal += stepY)
        {
            double ratioY = yVal / (double)axisMaxY;
            double py = padTop + plotH - (ratioY * plotH);

            var hPen = yVal == 0 ? AxisPen : GridHorizPen;
            DrawCrispHorizontalLine(dc, hPen, padLeft, padLeft + plotW, py);

            var yFt = CreateFormattedText(
                yVal.ToString(CultureInfo.InvariantCulture),
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(yFt, new Point(padLeft - 10.0 - yFt.Width, py - (yFt.Height / 2.0)));
        }

        // 2. Vertical X-Axis gridlines & time tick labels
        int xTicks = Math.Min(24, Math.Max(4, (int)Math.Floor(plotW / 130.0)));

        for (int i = 0; i <= xTicks; i++)
        {
            double ratioX = i / (double)xTicks;
            double px = padLeft + (ratioX * plotW);
            DateTime tickDt = minDt.AddTicks((long)(spanTicks * ratioX));

            var vPen = i == 0 ? AxisPen : GridVertPen;
            DrawCrispVerticalLine(dc, vPen, px, padTop, padTop + plotH);

            var xFt = CreateFormattedText(
                tickDt.ToString(labels.XTickTimeFormat, CultureInfo.InvariantCulture),
                TextMutedBrush,
                11.0,
                FontWeights.Normal,
                pixelsPerDip);
            dc.DrawText(xFt, new Point(px - (xFt.Width / 2.0), padTop + plotH + 8.0));
        }

        // 3. Axis titles
        var yTitleFt = CreateFormattedText(labels.YAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yTitleFt, new Point(8.0, 6.0));

        var xTitleFt = CreateFormattedText(labels.XAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(xTitleFt, new Point(padLeft + ((plotW - xTitleFt.Width) / 2.0), padTop + plotH + 28.0));

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
                    linePen = FallbackSeriesLinePen;
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
                    haloPen = FallbackSeriesHaloPen;
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

                FontWeight badgeWeight = pt.IsPeakForPmnt ? FontWeights.Bold : FontWeights.SemiBold;
                var badgeFt = CreateFormattedText(labelStr, brush, 10.5, badgeWeight, pixelsPerDip);
                double badgeW = Math.Ceiling(badgeFt.Width) + 8.0;
                double badgeH = Math.Ceiling(badgeFt.Height) + 2.0;
                double px = centerPt.X;
                double py = centerPt.Y;

                Span<Point> candidates =
                [
                    new Point(px + 7.0, py - badgeH - 1.0),
                    new Point(px - badgeW - 7.0, py - badgeH - 1.0),
                    new Point(px + 7.0, py + 4.0),
                    new Point(px - badgeW - 7.0, py + 4.0),
                    new Point(px - (badgeW / 2.0), py - badgeH - 5.0),
                    new Point(px - (badgeW / 2.0), py + 7.0)
                ];

                double minLabelX = padLeft + 2.0;
                double maxLabelX = Math.Max(minLabelX, w - badgeW - 4.0);
                double minLabelY = 4.0;
                double maxLabelY = Math.Max(minLabelY, padTop + plotH - badgeH);

                var bestPos = new Point(
                    Math.Clamp(candidates[0].X, minLabelX, maxLabelX),
                    Math.Clamp(candidates[0].Y, minLabelY, maxLabelY));
                foreach (var cand in candidates)
                {
                    double cx = Math.Clamp(cand.X, minLabelX, maxLabelX);
                    double cy = Math.Clamp(cand.Y, minLabelY, maxLabelY);
                    var rect = new Rect(cx, cy, badgeW, badgeH);

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

                var badgeRect = new Rect(bestPos.X, bestPos.Y, badgeW, badgeH);
                placedBoxes.Add(badgeRect);

                if (!SeriesBadgeBorderPens.TryGetValue(brush, out var borderPen))
                {
                    borderPen = AxisPen;
                }
                dc.DrawRoundedRectangle(BadgeBgBrush, borderPen, badgeRect, 4.0, 4.0);
                dc.DrawText(badgeFt, new Point(badgeRect.X + 4.0, badgeRect.Y + 1.0));
                _plottedHitRects.Add((badgeRect, pt.Pmnt));
            }
        }
    }

    private void RenderParetoVisuals(
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
        double pixelsPerDip = VisualTreeHelper.GetDpi(visualHost).PixelsPerDip;
        if (pixelsPerDip <= 0) pixelsPerDip = 1.0;
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines (Dual Y-axis: Left Count, Right %)
        for (int pct = 0; pct <= 100; pct += 20)
        {
            double ratio = pct / 100.0;
            double py = padTop + plotH - (ratio * plotH);

            var pen = pct == 0 ? AxisPen : GridHorizPen;
            DrawCrispHorizontalLine(dc, pen, padLeft, padLeft + plotW, py);

            int countVal = (int)Math.Round(ratio * axisMaxY);
            var leftFt = CreateFormattedText(
                countVal.ToString(CultureInfo.InvariantCulture),
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(leftFt, new Point(padLeft - 10.0 - leftFt.Width, py - (leftFt.Height / 2.0)));

            var rightFt = CreateFormattedText(
                $"{pct}%",
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(rightFt, new Point(padLeft + plotW + 8.0, py - (rightFt.Height / 2.0)));
        }

        // 2. Horizontal 80% Cutoff Reference Line
        double py80 = padTop + plotH - (0.80 * plotH);
        DrawCrispHorizontalLine(dc, Cutoff80Pen, padLeft, padLeft + plotW, py80);

        var cutoffFt = CreateFormattedText(labels.CutoffTag, Cutoff80Brush, 10.0, FontWeights.Bold, pixelsPerDip);
        double cutoffW = Math.Ceiling(cutoffFt.Width) + 8.0;
        double cutoffH = Math.Ceiling(cutoffFt.Height) + 2.0;
        var cutoffRect = new Rect(padLeft + plotW - cutoffW - 6.0, py80 - (cutoffH / 2.0), cutoffW, cutoffH);
        dc.DrawRoundedRectangle(CutoffTagBgBrush, Cutoff80BorderPen, cutoffRect, 4.0, 4.0);
        dc.DrawText(cutoffFt, new Point(cutoffRect.X + 4.0, cutoffRect.Y + 1.0));

        // 3. Axis titles
        var yLeftTitleFt = CreateFormattedText(labels.YLeftTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yLeftTitleFt, new Point(8.0, 6.0));

        var yRightTitleFt = CreateFormattedText(labels.YRightTitle, Cutoff80Brush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yRightTitleFt, new Point(padLeft + plotW + padRight - 8.0 - yRightTitleFt.Width, 6.0));

        var xTitleFt = CreateFormattedText(labels.XAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(xTitleFt, new Point(padLeft + ((plotW - xTitleFt.Width) / 2.0), padTop + plotH + 30.0));

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

            bool isGroupB = string.Equals(item.Group, "B", StringComparison.OrdinalIgnoreCase);
            var barBrush = isGroupB ? ParetoBarBBrush : ParetoBarABrush;
            var barBorderPen = isGroupB ? ParetoBarBBorderPen : ParetoBarABorderPen;

            var barRect = new Rect(barLeft, barTop, barW, barH);
            dc.DrawRoundedRectangle(barBrush, null, barRect, 4.0, 4.0);
            _plottedHitRects.Add((barRect, item.Label));

            var countFt = CreateFormattedText(
                $"{item.Count} ({item.Percentage:F1}%)",
                TextBrightBrush,
                10.5,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(
                countFt,
                new Point(
                    slotCenter - (countFt.Width / 2.0),
                    Math.Max(padTop - countFt.Height - 2.0, barTop - countFt.Height - 2.0)));

            var badgeFt = CreateFormattedText(item.Label, barBrush, 11.0, FontWeights.Bold, pixelsPerDip);
            double badgeW = Math.Ceiling(badgeFt.Width) + 12.0;
            double badgeH = Math.Ceiling(badgeFt.Height) + 2.0;
            var badgeRect = new Rect(slotCenter - (badgeW / 2.0), padTop + plotH + 8.0, badgeW, badgeH);
            dc.DrawRoundedRectangle(BadgeBgBrush, barBorderPen, badgeRect, 4.0, 4.0);
            dc.DrawText(badgeFt, new Point(badgeRect.X + 6.0, badgeRect.Y + 1.0));
            _plottedHitRects.Add((badgeRect, item.Label));

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

            var pctFt = CreateFormattedText(
                $"{paretoData.Items[i].CumulativePercentage:F0}%",
                GoldBrush,
                10.0,
                FontWeights.Bold,
                pixelsPerDip);
            dc.DrawText(pctFt, new Point(cumPoints[i].X - (pctFt.Width / 2.0), cumPoints[i].Y - 16.0));
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
        double pixelsPerDip = VisualTreeHelper.GetDpi(visualHost).PixelsPerDip;
        if (pixelsPerDip <= 0) pixelsPerDip = 1.0;
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines (Dual Y-axis: Left Throughput, Right NG Rate %)
        for (int step = 0; step <= 5; step++)
        {
            double ratio = step / 5.0;
            double py = padTop + plotH - (ratio * plotH);

            var pen = step == 0 ? AxisPen : GridHorizPen;
            DrawCrispHorizontalLine(dc, pen, padLeft, padLeft + plotW, py);

            int yVal = (int)Math.Round(ratio * axisMaxY);
            var leftFt = CreateFormattedText(
                yVal.ToString(CultureInfo.InvariantCulture),
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(leftFt, new Point(padLeft - 10.0 - leftFt.Width, py - (leftFt.Height / 2.0)));

            double rateVal = ratio * maxRate;
            var rightFt = CreateFormattedText(
                $"{rateVal:F1}%",
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(rightFt, new Point(padLeft + plotW + 8.0, py - (rightFt.Height / 2.0)));
        }

        // 2. Axis titles
        var yLeftTitleFt = CreateFormattedText(labels.YLeftTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yLeftTitleFt, new Point(8.0, 6.0));

        var yRightTitleFt = CreateFormattedText(labels.YRightTitle, NgBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yRightTitleFt, new Point(padLeft + plotW + padRight - 8.0 - yRightTitleFt.Width, 6.0));

        var xTitleFt = CreateFormattedText(labels.XAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(xTitleFt, new Point(padLeft + ((plotW - xTitleFt.Width) / 2.0), padTop + plotH + 30.0));

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
                var totalFt = CreateFormattedText(
                    b.TotalCount.ToString(CultureInfo.InvariantCulture),
                    TextBrightBrush,
                    10.0,
                    FontWeights.SemiBold,
                    pixelsPerDip);
                dc.DrawText(
                    totalFt,
                    new Point(
                        slotCenter - (totalFt.Width / 2.0),
                        Math.Max(padTop - totalFt.Height - 2.0, ngTop - totalFt.Height - 2.0)));
            }

            var xFt = CreateFormattedText(b.HourLabel, TextMutedBrush, 10.5, FontWeights.Normal, pixelsPerDip);
            dc.DrawText(xFt, new Point(slotCenter - (xFt.Width / 2.0), padTop + plotH + 8.0));

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
                var rateFt = CreateFormattedText(
                    $"{b.NgRatePercent:F1}%",
                    nodeBrush,
                    9.5,
                    FontWeights.Bold,
                    pixelsPerDip);
                dc.DrawText(rateFt, new Point(pt.X - (rateFt.Width / 2.0), pt.Y - 14.0));
            }
        }
    }

    private void RenderSpcHistogramVisuals(
        Canvas canvas,
        ChartRenderSnapshot snapshot,
        SpcHistogramData spcData,
        ChartSpcLabels labels)
    {
        var (w, h) = ResolveCanvasDimensions(canvas);
        if (w < 120 || h < 100 || double.IsNaN(w) || double.IsNaN(h)) return;

        double zoom = Math.Clamp(snapshot.ZoomFactor, 1.0, 8.0);
        canvas.Cursor = zoom > 1.01 ? Cursors.SizeAll : Cursors.Arrow;

        const double padLeft = 62.0;
        const double padRight = 48.0;
        const double padTop = 38.0;
        const double padBottom = 54.0;

        double plotW = Math.Max(50.0, w - padLeft - padRight);
        double plotH = Math.Max(50.0, h - padTop - padBottom);

        int rawPeakY = Math.Max(
            spcData.MaxBinCount,
            (int)Math.Ceiling(spcData.MaxGaussianScaledCount * 1.08));
        int maxY = Math.Max(4, rawPeakY);
        int stepY = maxY <= 10 ? 2 : (int)Math.Ceiling(maxY / 6.0);
        int axisMaxY = (int)(Math.Ceiling(maxY / (double)stepY) * stepY);
        if (axisMaxY < maxY) axisMaxY = maxY;

        double plotMinX = spcData.PlotMinX;
        double plotMaxX = spcData.PlotMaxX;
        double spanX = plotMaxX - plotMinX;
        if (spanX <= 1e-12)
        {
            plotMinX -= 1.0;
            plotMaxX += 1.0;
            spanX = 2.0;
        }

        double ValueToPlotX(double val) =>
            padLeft + (Math.Clamp((val - plotMinX) / spanX, 0.0, 1.0) * plotW);

        double CountToPlotY(double count) =>
            padTop + plotH - (Math.Clamp(count / axisMaxY, 0.0, 1.0) * plotH);

        var visualHost = new ChartVisualHost();
        canvas.Children.Add(visualHost);
        double pixelsPerDip = VisualTreeHelper.GetDpi(visualHost).PixelsPerDip;
        if (pixelsPerDip <= 0) pixelsPerDip = 1.0;
        using var dc = visualHost.Open();

        // 1. Horizontal Y-Axis gridlines & integer frequency labels
        for (int yVal = 0; yVal <= axisMaxY; yVal += stepY)
        {
            double ratioY = yVal / (double)axisMaxY;
            double py = padTop + plotH - (ratioY * plotH);

            var hPen = yVal == 0 ? AxisPen : GridHorizPen;
            DrawCrispHorizontalLine(dc, hPen, padLeft, padLeft + plotW, py);

            var yFt = CreateFormattedText(
                yVal.ToString(CultureInfo.InvariantCulture),
                TextMutedBrush,
                11.0,
                FontWeights.SemiBold,
                pixelsPerDip);
            dc.DrawText(yFt, new Point(padLeft - 10.0 - yFt.Width, py - (yFt.Height / 2.0)));
        }

        // 2. Vertical X-Axis gridlines & optical parameter value ticks
        string valFmt = DetermineAxisValueFormat(spcData.ParameterName, spanX);
        int xTicks = Math.Min(12, Math.Max(5, (int)Math.Floor(plotW / 95.0)));

        for (int i = 0; i <= xTicks; i++)
        {
            double ratioX = i / (double)xTicks;
            double px = padLeft + (ratioX * plotW);
            double tickVal = plotMinX + (spanX * ratioX);

            var vPen = i == 0 ? AxisPen : GridVertPen;
            DrawCrispVerticalLine(dc, vPen, px, padTop, padTop + plotH);

            var xFt = CreateFormattedText(
                tickVal.ToString(valFmt, CultureInfo.InvariantCulture),
                TextMutedBrush,
                10.5,
                FontWeights.Normal,
                pixelsPerDip);
            dc.DrawText(xFt, new Point(px - (xFt.Width / 2.0), padTop + plotH + 8.0));
        }

        // 3. Axis titles
        var yTitleFt = CreateFormattedText(labels.YAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(yTitleFt, new Point(8.0, 6.0));

        var xTitleFt = CreateFormattedText(labels.XAxisTitle, TextMutedBrush, 11.0, FontWeights.Bold, pixelsPerDip);
        dc.DrawText(xTitleFt, new Point(padLeft + ((plotW - xTitleFt.Width) / 2.0), padTop + plotH + 28.0));

        // 4. Draw Histogram Frequency Bars (#10B981 in-spec, #EF4444 out-of-spec) & register hover rects
        int binCount = spcData.Bins.Count;
        double baselineY = padTop + plotH;

        for (int i = 0; i < binCount; i++)
        {
            var bin = spcData.Bins[i];
            double pxLeft = ValueToPlotX(bin.StartValue);
            double pxRight = ValueToPlotX(bin.EndValue);
            double rawBarW = pxRight - pxLeft;
            double barW = Math.Max(2.5, rawBarW - 1.5);
            double barCenter = (pxLeft + pxRight) * 0.5;
            double barX = barCenter - (barW * 0.5);

            if (bin.Count > 0)
            {
                double barH = Math.Max(2.0, (bin.Count / (double)axisMaxY) * plotH);
                double barTop = baselineY - barH;
                var barRect = new Rect(barX, barTop, barW, barH);

                var fillBrush = bin.IsWithinSpec ? SpcBarOkFillBrush : SpcBarNgFillBrush;
                var borderPen = bin.IsWithinSpec ? SpcBarOkBorderPen : SpcBarNgBorderPen;

                dc.DrawRoundedRectangle(fillBrush, borderPen, barRect, 2.5, 2.5);

                if (snapshot.ShowLabels && barW >= 16.0)
                {
                    var cntFt = CreateFormattedText(
                        bin.Count.ToString(CultureInfo.InvariantCulture),
                        TextBrightBrush,
                        9.5,
                        FontWeights.SemiBold,
                        pixelsPerDip);
                    dc.DrawText(
                        cntFt,
                        new Point(
                            barCenter - (cntFt.Width * 0.5),
                            Math.Max(padTop + 2.0, barTop - cntFt.Height - 2.0)));
                }

                // Register full-column hit rect spanning from padTop to baselineY for seamless hover across the entire column
                double colLeft = Math.Min(pxLeft, barX);
                double colW = Math.Max(rawBarW, barW);
                var hitRect = new Rect(colLeft, padTop, colW, plotH);
                _plottedHistogramBins.Add((hitRect, bin, spcData.ParameterName));
            }
            else
            {
                // Register column hit area even for 0-count bins so users can inspect empty ranges
                double colLeft = Math.Min(pxLeft, barX);
                double colW = Math.Max(rawBarW, barW);
                var hitRect = new Rect(colLeft, padTop, colW, plotH);
                _plottedHistogramBins.Add((hitRect, bin, spcData.ParameterName));
            }
        }

        // 5. Draw smooth Gaussian Bell Curve via StreamGeometry with frozen amber pen (#F59E0B)
        if (snapshot.ShowLines && spcData.GaussianPoints.Count > 1)
        {
            int gCount = spcData.GaussianPoints.Count;
            Point[] screenPts = ArrayPool<Point>.Shared.Rent(gCount);
            try
            {
                for (int i = 0; i < gCount; i++)
                {
                    var gp = spcData.GaussianPoints[i];
                    screenPts[i] = new Point(ValueToPlotX(gp.X), CountToPlotY(gp.ScaledCount));
                }

                var curveGeom = BuildSmoothGaussianGeometry(screenPts.AsSpan(0, gCount));
                double clipTop = Math.Max(0.0, padTop - 2.0);
                double clipHeight = (padTop + plotH) - clipTop;
                if (plotW > 0 && clipHeight > 0)
                {
                    var plotClip = new RectangleGeometry(new Rect(padLeft, clipTop, plotW, clipHeight));
                    plotClip.Freeze();
                    dc.PushClip(plotClip);
                    dc.DrawGeometry(null, SpcGaussCurvePen, curveGeom);
                    dc.Pop();
                }
            }
            finally
            {
                ArrayPool<Point>.Shared.Return(screenPts);
            }
        }

        // 6. Draw Mean (mu) vertical reference line
        if (spcData.Metrics.TotalCount > 0)
        {
            double pxMean = ValueToPlotX(spcData.Metrics.Mean);
            DrawCrispVerticalLine(dc, SpcMeanLinePen, pxMean, padTop, baselineY);

            string meanTagText = $"{labels.MeanTagPrefix}: {spcData.Metrics.Mean.ToString(valFmt, CultureInfo.InvariantCulture)}";
            var meanFt = CreateFormattedText(meanTagText, SpcMeanBrush, 9.5, FontWeights.Bold, pixelsPerDip);
            double meanBadgeW = Math.Ceiling(meanFt.Width) + 8.0;
            double meanBadgeH = Math.Ceiling(meanFt.Height) + 2.0;
            double meanBadgeX = Math.Clamp(pxMean - (meanBadgeW * 0.5), padLeft + 2.0, padLeft + plotW - meanBadgeW - 2.0);
            var meanBadgeRect = new Rect(meanBadgeX, padTop - meanBadgeH - 3.0, meanBadgeW, meanBadgeH);
            dc.DrawRoundedRectangle(SpcMeanTagBgBrush, SpcMeanTagBorderPen, meanBadgeRect, 3.5, 3.5);
            dc.DrawText(meanFt, new Point(meanBadgeRect.X + 4.0, meanBadgeRect.Y + 1.0));
        }

        // 7. Draw Summary KPI Card (sits cleanly on top of bars and curve)
        _spcCardBounds = DrawSpcSummaryCard(dc, spcData, labels, padLeft, padTop, plotW, valFmt, pixelsPerDip);

        // 8. Draw LSL and USL vertical dashed red specification limit lines with Spec badges
        Rect? drawnLslRect = null;
        if (spcData.Metrics.Lsl is { } lslVal)
        {
            double pxLsl = ValueToPlotX(lslVal);
            DrawCrispVerticalLine(dc, SpcSpecLimitPen, pxLsl, padTop, baselineY);

            string lslText = $"{labels.LslTagPrefix}: {lslVal.ToString(valFmt, CultureInfo.InvariantCulture)}";
            var lslFt = CreateFormattedText(lslText, NgBrush, 10.0, FontWeights.Bold, pixelsPerDip);
            double lslW = Math.Ceiling(lslFt.Width) + 8.0;
            double lslH = Math.Ceiling(lslFt.Height) + 2.0;
            double lslX = Math.Clamp(pxLsl - (lslW * 0.5), padLeft + 2.0, padLeft + plotW - lslW - 2.0);
            double lslY = padTop + 4.0;
            var testLslRect = new Rect(lslX, lslY, lslW, lslH);
            if (_spcCardBounds.HasValue && _spcCardBounds.Value.IntersectsWith(testLslRect))
            {
                lslY = _spcCardBounds.Value.Bottom + 4.0;
            }
            var lslRect = new Rect(lslX, lslY, lslW, lslH);
            dc.DrawRoundedRectangle(SpcLimitTagBgBrush, SpcSpecTagBorderPen, lslRect, 4.0, 4.0);
            dc.DrawText(lslFt, new Point(lslRect.X + 4.0, lslRect.Y + 1.0));
            drawnLslRect = lslRect;
        }

        if (spcData.Metrics.Usl is { } uslVal)
        {
            double pxUsl = ValueToPlotX(uslVal);
            DrawCrispVerticalLine(dc, SpcSpecLimitPen, pxUsl, padTop, baselineY);

            string uslText = $"{labels.UslTagPrefix}: {uslVal.ToString(valFmt, CultureInfo.InvariantCulture)}";
            var uslFt = CreateFormattedText(uslText, NgBrush, 10.0, FontWeights.Bold, pixelsPerDip);
            double uslW = Math.Ceiling(uslFt.Width) + 8.0;
            double uslH = Math.Ceiling(uslFt.Height) + 2.0;
            double uslX = Math.Clamp(pxUsl - (uslW * 0.5), padLeft + 2.0, padLeft + plotW - uslW - 2.0);
            double uslY = padTop + 4.0;
            var testUslRect = new Rect(uslX, uslY, uslW, uslH);
            if (_spcCardBounds.HasValue && _spcCardBounds.Value.IntersectsWith(testUslRect))
            {
                uslY = _spcCardBounds.Value.Bottom + 4.0;
            }
            var uslRect = new Rect(uslX, uslY, uslW, uslH);
            if (drawnLslRect.HasValue && drawnLslRect.Value.IntersectsWith(uslRect))
            {
                uslY = drawnLslRect.Value.Bottom + 4.0;
                uslRect = new Rect(uslX, uslY, uslW, uslH);
            }
            dc.DrawRoundedRectangle(SpcLimitTagBgBrush, SpcSpecTagBorderPen, uslRect, 4.0, 4.0);
            dc.DrawText(uslFt, new Point(uslRect.X + 4.0, uslRect.Y + 1.0));
        }
    }

    private static StreamGeometry BuildSmoothGaussianGeometry(ReadOnlySpan<Point> points)
    {
        var geom = new StreamGeometry();
        if (points.Length < 2)
        {
            if (geom.CanFreeze) geom.Freeze();
            return geom;
        }

        using (var ctx = geom.Open())
        {
            ctx.BeginFigure(points[0], isFilled: false, isClosed: false);

            if (points.Length == 2)
            {
                ctx.LineTo(points[1], isStroked: true, isSmoothJoin: true);
            }
            else
            {
                for (int i = 0; i < points.Length - 1; i++)
                {
                    Point p0 = i == 0 ? points[0] : points[i - 1];
                    Point p1 = points[i];
                    Point p2 = points[i + 1];
                    Point p3 = (i + 2 < points.Length) ? points[i + 2] : points[i + 1];

                    Point cp1 = new(
                        p1.X + ((p2.X - p0.X) / 6.0),
                        p1.Y + ((p2.Y - p0.Y) / 6.0));
                    Point cp2 = new(
                        p2.X - ((p3.X - p1.X) / 6.0),
                        p2.Y - ((p3.Y - p1.Y) / 6.0));

                    ctx.BezierTo(cp1, cp2, p2, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        if (geom.CanFreeze) geom.Freeze();
        return geom;
    }

    private static Rect DrawSpcSummaryCard(
        DrawingContext dc,
        SpcHistogramData spcData,
        ChartSpcLabels labels,
        double padLeft,
        double padTop,
        double plotW,
        string valFmt,
        double pixelsPerDip)
    {
        double? cpk = spcData.Metrics.Cpk;
        SolidColorBrush cpkFg;
        SolidColorBrush cpkPillBg;
        Pen cardBorderPen;
        string cpkGrade;

        if (!cpk.HasValue)
        {
            cpkFg = TextMutedBrush;
            cpkPillBg = BadgeBgBrush;
            cardBorderPen = SpcCardDefaultBorderPen;
            cpkGrade = "N/A";
        }
        else if (cpk.Value >= 1.33)
        {
            cpkFg = OkBrush;
            cpkPillBg = SpcCpkGreenBgBrush;
            cardBorderPen = SpcCardGreenBorderPen;
            cpkGrade = $"{cpk.Value.ToString("F2", CultureInfo.InvariantCulture)} (OK)";
        }
        else if (cpk.Value >= 1.0)
        {
            cpkFg = Cutoff80Brush;
            cpkPillBg = SpcCpkAmberBgBrush;
            cardBorderPen = SpcCardAmberBorderPen;
            cpkGrade = $"{cpk.Value.ToString("F2", CultureInfo.InvariantCulture)} (WARN)";
        }
        else
        {
            cpkFg = NgBrush;
            cpkPillBg = SpcCpkRedBgBrush;
            cardBorderPen = SpcCardRedBorderPen;
            cpkGrade = $"{cpk.Value.ToString("F2", CultureInfo.InvariantCulture)} (NG)";
        }

        string meanValStr = spcData.Metrics.Mean.ToString(valFmt, CultureInfo.InvariantCulture);
        string stdValStr = spcData.Metrics.StdDev.ToString(valFmt, CultureInfo.InvariantCulture);
        string samplesValStr = spcData.Metrics.TotalCount.ToString("N0", CultureInfo.InvariantCulture);
        string cpValStr = spcData.Metrics.Cp.HasValue
            ? spcData.Metrics.Cp.Value.ToString("F2", CultureInfo.InvariantCulture)
            : "N/A";

        var titleFt = CreateFormattedText(labels.CardTitle, TextBrightBrush, 10.5, FontWeights.Bold, pixelsPerDip);
        var meanLblFt = CreateFormattedText(labels.CardMeanLabel, TextMutedBrush, 10.0, FontWeights.SemiBold, pixelsPerDip);
        var meanValFt = CreateFormattedText(meanValStr, SpcMeanBrush, 10.5, FontWeights.Bold, pixelsPerDip);

        var stdLblFt = CreateFormattedText(labels.CardStdDevLabel, TextMutedBrush, 10.0, FontWeights.SemiBold, pixelsPerDip);
        var stdValFt = CreateFormattedText(stdValStr, TextBrightBrush, 10.5, FontWeights.Bold, pixelsPerDip);

        var cntLblFt = CreateFormattedText(labels.CardSamplesLabel, TextMutedBrush, 10.0, FontWeights.SemiBold, pixelsPerDip);
        var cntValFt = CreateFormattedText(samplesValStr, TextBrightBrush, 10.5, FontWeights.Bold, pixelsPerDip);

        var cpLblFt = CreateFormattedText(labels.CardCpLabel, TextMutedBrush, 10.0, FontWeights.SemiBold, pixelsPerDip);
        var cpValFt = CreateFormattedText(cpValStr, TextBrightBrush, 10.5, FontWeights.Bold, pixelsPerDip);

        var cpkLblFt = CreateFormattedText(labels.CardCpkLabel, TextMutedBrush, 10.5, FontWeights.Bold, pixelsPerDip);
        var cpkValFt = CreateFormattedText(cpkGrade, cpkFg, 11.0, FontWeights.Bold, pixelsPerDip);

        double maxRowW = Math.Max(
            titleFt.Width,
            Math.Max(
                meanLblFt.Width + meanValFt.Width + 24.0,
                Math.Max(
                    stdLblFt.Width + stdValFt.Width + 24.0,
                    Math.Max(
                        cntLblFt.Width + cntValFt.Width + 24.0,
                        Math.Max(
                            cpLblFt.Width + cpValFt.Width + 24.0,
                            cpkLblFt.Width + cpkValFt.Width + 32.0)))));
        double cardW = Math.Max(176.0, Math.Ceiling(maxRowW) + 24.0);
        const double cardH = 128.0;

        // Adaptive placement: if mean/peak is on right half, anchor card to top-left to avoid covering peak & USL
        double spanX = Math.Max(1e-12, spcData.PlotMaxX - spcData.PlotMinX);
        double pxMean = spcData.Metrics.TotalCount > 0
            ? padLeft + (Math.Clamp((spcData.Metrics.Mean - spcData.PlotMinX) / spanX, 0.0, 1.0) * plotW)
            : padLeft + (plotW * 0.5);
        bool peakOnRight = pxMean > (padLeft + (plotW * 0.52));

        double cardX = peakOnRight
            ? (padLeft + 12.0)
            : Math.Max(padLeft + 8.0, padLeft + plotW - cardW - 10.0);
        double cardY = padTop + 8.0;
        var cardRect = new Rect(cardX, cardY, cardW, cardH);

        dc.DrawRoundedRectangle(SpcCardBgBrush, cardBorderPen, cardRect, 6.0, 6.0);

        double innerLeft = cardX + 10.0;
        double innerRight = cardX + cardW - 10.0;
        double curY = cardY + 7.0;

        dc.DrawText(titleFt, new Point(innerLeft, curY));
        curY += 18.0;
        DrawCrispHorizontalLine(dc, GridHorizPen, innerLeft, innerRight, curY);
        curY += 5.0;

        // Row 1: Mean (mu)
        dc.DrawText(meanLblFt, new Point(innerLeft, curY));
        dc.DrawText(meanValFt, new Point(innerRight - meanValFt.Width, curY));
        curY += 18.0;

        // Row 2: StdDev (sigma)
        dc.DrawText(stdLblFt, new Point(innerLeft, curY));
        dc.DrawText(stdValFt, new Point(innerRight - stdValFt.Width, curY));
        curY += 18.0;

        // Row 3: Sample count (N)
        dc.DrawText(cntLblFt, new Point(innerLeft, curY));
        dc.DrawText(cntValFt, new Point(innerRight - cntValFt.Width, curY));
        curY += 18.0;

        // Row 4: Cp
        dc.DrawText(cpLblFt, new Point(innerLeft, curY));
        dc.DrawText(cpValFt, new Point(innerRight - cpValFt.Width, curY));
        curY += 20.0;

        // Row 5: Cpk with semantic badge
        dc.DrawText(cpkLblFt, new Point(innerLeft, curY));
        double pillW = Math.Ceiling(cpkValFt.Width) + 10.0;
        double pillH = Math.Ceiling(cpkValFt.Height) + 2.0;
        var pillRect = new Rect(innerRight - pillW, curY - 1.0, pillW, pillH);
        dc.DrawRoundedRectangle(cpkPillBg, cardBorderPen, pillRect, 4.0, 4.0);
        dc.DrawText(cpkValFt, new Point(pillRect.X + 5.0, pillRect.Y + 1.0));

        return cardRect;
    }

    private static string DetermineAxisValueFormat(string parameterName, double span)
    {
        if (string.Equals(parameterName, "whitex", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(parameterName, "whitey", StringComparison.OrdinalIgnoreCase))
        {
            return "F4";
        }

        if (span < 0.2) return "F4";
        if (span < 3.0) return "F3";
        if (span < 50.0) return "F2";
        if (span < 500.0) return "F1";
        return "F0";
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
            finally
            {
                global::System.Windows.Threading.Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            Name = "OtpOffscreenChartExport",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal
        };
        staThread.SetApartmentState(ApartmentState.STA);
        staThread.Start();
        byte[] pngBytes = await tcs.Task.ConfigureAwait(false);

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

