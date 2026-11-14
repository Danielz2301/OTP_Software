using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Pure calculation engine for KPI statistics, PMNT A/B fixture defect rankings, consecutive NG streaks, and cumulative error chart trajectories.
/// </summary>
public interface IStatisticsService
{
    UnitStatisticsResult GetUnitStatistics(
        IReadOnlyList<UnitRecord> units,
        UnitStatusFilter filterStatus = UnitStatusFilter.All,
        int consecutiveNgThreshold = 3);

    ErrorChartData GetErrorChartData(
        IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All);

    ParetoChartData GetParetoChartData(
        IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        int topN = 10);

    HourlyTrendData GetHourlyTrendData(
        IReadOnlyList<UnitRecord> units,
        int dayShiftStartHour = 8,
        int nightShiftStartHour = 20);
}

