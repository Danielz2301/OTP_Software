namespace OTPSoftware.Core.Models;

/// <summary>
/// A single defect occurrence node on the Time vs. Cumulative Error Count chart.
/// </summary>
public sealed record ErrorChartPoint
{
    public int PointIndex { get; init; }
    public int UnitIndex { get; init; }
    public required string Pmnt { get; init; }
    public required string Group { get; init; }
    public DateTime Timestamp { get; init; }
    public required string TimeFormatted { get; init; }
    public required string ShortTime { get; init; }
    public int ErrorCount { get; init; }
    public int TotalPmntError { get; init; }
    public bool IsPeakForPmnt { get; init; }
    public IReadOnlyList<string> Errors { get; init; } = [];
    public string ErrorText { get; init; } = string.Empty;
    public string PanelName { get; init; } = string.Empty;
    public string BluName { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
}

/// <summary>
/// Chronological trajectory of defects for a single PMNT fixture.
/// </summary>
public sealed record ErrorChartSeries(
    string Pmnt,
    string Group,
    int TotalErrors,
    IReadOnlyList<ErrorChartPoint> Points
);

/// <summary>
/// Complete dataset for rendering the Time vs. Cumulative Error Count chart.
/// </summary>
public sealed record ErrorChartData
{
    public IReadOnlyList<ErrorChartPoint> Points { get; init; } = [];
    public IReadOnlyList<ErrorChartSeries> Series { get; init; } = [];
    public int TotalErrors { get; init; }
    public int UniquePmntCount { get; init; }
    public int MaxErrorCount { get; init; }
    public DateTime? MinTime { get; init; }
    public DateTime? MaxTime { get; init; }
    public string TopPmnt { get; init; } = string.Empty;
    public int TopPmntCount { get; init; }
}

/// <summary>
/// Visualization mode for the error analytics chart viewport.
/// </summary>
public enum ChartDisplayMode
{
    CumulativeTimeline = 0,
    DefectPareto = 1,
    HourlyTrend = 2
}

/// <summary>
/// A single category bar item on the Pareto chart (e.g. a PMNT fixture or defect category).
/// </summary>
public sealed record ParetoChartItem
{
    public string Label { get; init; } = string.Empty;
    public string Group { get; init; } = "Other";
    public int Count { get; init; }
    public double Percentage { get; init; }
    public double CumulativePercentage { get; init; }
}

/// <summary>
/// Aggregated Pareto 80/20 dataset.
/// </summary>
public sealed record ParetoChartData
{
    public IReadOnlyList<ParetoChartItem> Items { get; init; } = [];
    public int TotalDefects { get; init; }
    public int MaxCount { get; init; }
    public string TopLabel { get; init; } = string.Empty;
    public int VitalFewCount { get; init; }
}

/// <summary>
/// Hourly production throughput and defect yield bucket.
/// </summary>
public sealed record HourlyThroughputBucket
{
    public DateTime HourStart { get; init; }
    public string HourLabel { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public int OkCount { get; init; }
    public int NgCount { get; init; }
    public double NgRatePercent { get; init; }
    public double FpyPercent { get; init; }
}

/// <summary>
/// Shift-wide hourly throughput and yield trend dataset.
/// </summary>
public sealed record HourlyTrendData
{
    public IReadOnlyList<HourlyThroughputBucket> Buckets { get; init; } = [];
    public int TotalUnits { get; init; }
    public int TotalOk { get; init; }
    public int TotalNg { get; init; }
    public int MaxHourlyTotal { get; init; }
    public double MaxNgRatePercent { get; init; }
    public string ShiftLabel { get; init; } = string.Empty;
}

