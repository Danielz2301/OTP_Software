using System;
using System.Collections.Generic;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Pure calculation engine for KPI statistics, PMNT A/B fixture defect rankings, consecutive NG streaks,
/// cumulative error chart trajectories, and SPC optical parameter capability distributions.
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

    SpcMetrics ComputeSpcMetrics(
        ReadOnlySpan<double> values,
        double? lsl = null,
        double? usl = null,
        bool useSampleStdDev = true);

    double[] ExtractMesParameterValues(
        IReadOnlyList<MesPanelDetailRecord> mesRecords,
        string parameterName);

    double[] ExtractMesParameterValues(
        IReadOnlyList<string> otpDefectValues,
        string parameterName);

    SpcHistogramData GetSpcHistogramData(
        ReadOnlySpan<double> values,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0);

    SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<double> values,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0);

    SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<MesPanelDetailRecord> mesRecords,
        string parameterName = "White_lv",
        double? lsl = null,
        double? usl = null,
        int binCount = 0);

    SpcHistogramData GetSpcHistogramData(
        IReadOnlyList<UnitRecord> units,
        string parameterName = "White_lv",
        IReadOnlyList<MesPanelDetailRecord>? mesRecords = null,
        double? lsl = null,
        double? usl = null,
        int binCount = 0);
}

