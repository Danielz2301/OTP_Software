namespace OTPSoftware.Core.Models;

/// <summary>
/// Defect ranking entry for a single PMNT fixture (Track A or Track B).
/// </summary>
public sealed record PmntDefectRankItem
{
    public required string Name { get; init; }
    public int Count { get; init; }
    public int Streak { get; init; }
    public int MaxStreak { get; init; }
    public bool IsAlarm { get; init; }
    public int MaxCount { get; init; } = 1;
    public int MaxIndex { get; init; }

    public double CountRatio => MaxCount > 0 ? Math.Clamp((double)Count / MaxCount, 0.0, 1.0) : 0.0;
    public double CountPercent => CountRatio * 100.0;
}

/// <summary>
/// Aggregated KPI metrics, optical defect breakdown, and PMNT A/B rankings for a unit collection.
/// </summary>
public sealed record UnitStatisticsResult
{
    public int TotalScope { get; init; }
    public int DisplayTotal { get; init; }
    public int OkCount { get; init; }
    public int NgCount { get; init; }
    public double NgRate { get; init; }
    public int Xylv { get; init; }
    public int PowerOn { get; init; }
    public int PowerOff { get; init; }
    public int Flicker { get; init; }
    public IReadOnlyList<PmntDefectRankItem> PmntA { get; init; } = [];
    public IReadOnlyList<PmntDefectRankItem> PmntB { get; init; } = [];
    public IReadOnlyList<PmntDefectRankItem> PmntOther { get; init; } = [];
    public int PmntAErrorSum { get; init; }
    public int PmntBErrorSum { get; init; }
    public IReadOnlyList<PmntDefectRankItem> AlarmPmnts { get; init; } = [];
}

/// <summary>
/// Represents a Day (08:30 - 20:30) or Night (20:30 - 08:30) production shift window.
/// </summary>
public sealed record ShiftWindow(
    string ShiftName,
    string ShiftDate,
    string ShiftKey,
    DateTime StartTime,
    DateTime EndTime
);

/// <summary>
/// Summary of a completed shift CSV export operation.
/// </summary>
public sealed record ShiftReportResult(
    string ShiftKey,
    int UnitCount,
    string SummaryPath,
    string? DetailPath,
    UnitStatisticsResult Stats
);

