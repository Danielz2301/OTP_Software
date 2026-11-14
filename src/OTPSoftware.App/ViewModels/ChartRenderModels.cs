using OTPSoftware.Core.Models;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// Associates an <see cref="ErrorChartPoint"/> with its rendered canvas coordinates for O(N) hit-testing.
/// </summary>
public sealed record PlottedChartPoint(ErrorChartPoint Point, double PlotX, double PlotY);

/// <summary>
/// Framework-agnostic localized tooltip text payload for a hovered defect node.
/// </summary>
public sealed record ChartTooltipContent(
    string Header,
    string Body,
    string Footer,
    string Group);

/// <summary>
/// Pre-localized axis and formatting labels for the Cumulative Timeline chart mode.
/// </summary>
public sealed record ChartTimelineLabels(
    string YAxisTitle,
    string XAxisTitle,
    string XTickTimeFormat);

/// <summary>
/// Pre-localized axis and cutoff labels for the Pareto 80/20 chart mode.
/// </summary>
public sealed record ChartParetoLabels(
    string CutoffTag,
    string YLeftTitle,
    string YRightTitle,
    string XAxisTitle);

/// <summary>
/// Pre-localized axis labels for the Hourly Throughput &amp; NG Rate chart mode.
/// </summary>
public sealed record ChartHourlyLabels(
    string YLeftTitle,
    string YRightTitle,
    string XAxisTitle);

/// <summary>
/// Immutable ViewModel-prepared snapshot consumed by View-layer chart renderers and offscreen PNG exporters.
/// Contains zero WPF UI elements or dispatcher dependencies.
/// </summary>
public sealed record ChartRenderSnapshot
{
    public required ChartDisplayMode Mode { get; init; }
    public bool ShowLines { get; init; }
    public bool ShowLabels { get; init; }
    public double ZoomFactor { get; init; } = 1.0;
    public int VisualThreshold { get; init; } = 150;
    public bool IsEmpty { get; init; }

    public ErrorChartData? TimelineData { get; init; }
    public ChartTimelineLabels? TimelineLabels { get; init; }

    public ParetoChartData? ParetoData { get; init; }
    public ChartParetoLabels? ParetoLabels { get; init; }

    public HourlyTrendData? HourlyData { get; init; }
    public ChartHourlyLabels? HourlyLabels { get; init; }
}

/// <summary>
/// Pure hex-color palette mapping for PMNT series so ViewModels stay independent of WPF Brushes.
/// </summary>
public static class ChartPalette
{
    public static readonly string[] GroupAHex =
        ["#38BDF8", "#3B82F6", "#10B981", "#22D3EE", "#60A5FA", "#818CF8"];

    public static readonly string[] GroupBHex =
        ["#EF4444", "#F59E0B", "#F87171", "#FB923C", "#F43F5E", "#FBBF24"];

    public static string GetPmntSeriesHex(string? group, int seriesIndex = 0)
    {
        var palette = string.Equals(group, "B", StringComparison.OrdinalIgnoreCase)
            ? GroupBHex
            : GroupAHex;
        return palette[Math.Abs(seriesIndex) % palette.Length];
    }
}

/// <summary>
/// Abstraction allowing <see cref="ChartViewModel"/> to export live visuals or offscreen shift charts
/// without holding any WPF UI or imaging references.
/// </summary>
public interface IChartExportService
{
    Task<string> ExportChartElementAsync(string filePath, object? chartElement);

    Task<string> ExportOffscreenShiftChartAsync(
        string filePath,
        ChartRenderSnapshot snapshot,
        string zeroShiftTitle,
        string zeroShiftSubtitle);
}

/// <summary>
/// Assembly-internal registry populated by the View layer's module initializer so parameterless
/// <see cref="ChartViewModel"/> construction keeps full offscreen PNG export support even in tests.
/// </summary>
internal static class ChartExportServiceRegistry
{
    public static IChartExportService? Default { get; set; }
}

