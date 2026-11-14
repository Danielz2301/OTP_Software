namespace OTPSoftware.Core.Extensions;

using System;
using System.Collections.Generic;
using System.Linq;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

/// <summary>
/// Extension operations for unit records and unit collections.
/// </summary>
public static class UnitCollectionExtensions
{
    private static readonly IUnitFilterService DefaultFilterService = new UnitFilterService();
    private static readonly IStatisticsService DefaultStatisticsService = new StatisticsService();

    public static bool IsInScope(this UnitRecord unit, UnitFilterSpec spec, IUnitFilterService? filterService = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        filterService ??= DefaultFilterService;
        return filterService.IsUnitInScope(unit, spec);
    }

    public static bool IsDisplayed(this UnitRecord unit, UnitFilterSpec spec, IUnitFilterService? filterService = null)
    {
        ArgumentNullException.ThrowIfNull(unit);
        filterService ??= DefaultFilterService;
        return filterService.IsUnitDisplayed(unit, spec);
    }

    public static IEnumerable<UnitRecord> WhereInScope(
        this IEnumerable<UnitRecord> source,
        UnitFilterSpec spec,
        IUnitFilterService? filterService = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);

        filterService ??= DefaultFilterService;
        if (filterService is UnitFilterService concrete)
        {
            string expectedSource = UnitFilterService.ResolveExpectedSource(spec.SourceName, concrete.Machines);
            foreach (var unit in source)
            {
                if (concrete.IsUnitInScope(unit, spec, expectedSource))
                {
                    yield return unit;
                }
            }
            yield break;
        }

        foreach (var unit in source)
        {
            if (filterService.IsUnitInScope(unit, spec))
            {
                yield return unit;
            }
        }
    }

    public static IEnumerable<UnitRecord> WhereDisplayed(
        this IEnumerable<UnitRecord> source,
        UnitFilterSpec spec,
        IUnitFilterService? filterService = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);

        filterService ??= DefaultFilterService;
        if (filterService is UnitFilterService concrete)
        {
            string expectedSource = UnitFilterService.ResolveExpectedSource(spec.SourceName, concrete.Machines);
            foreach (var unit in source)
            {
                if (concrete.IsUnitInScope(unit, spec, expectedSource) && concrete.IsUnitDisplayed(unit, spec))
                {
                    yield return unit;
                }
            }
            yield break;
        }

        foreach (var unit in source)
        {
            if (filterService.IsUnitInScope(unit, spec) && filterService.IsUnitDisplayed(unit, spec))
            {
                yield return unit;
            }
        }
    }

    public static UnitStatisticsResult ComputeStatistics(
        this IEnumerable<UnitRecord> source,
        UnitStatusFilter filterStatus = UnitStatusFilter.All,
        int consecutiveNgThreshold = 3,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var list = source as IReadOnlyList<UnitRecord> ?? source.ToList();
        statsService ??= DefaultStatisticsService;
        return statsService.GetUnitStatistics(list, filterStatus, consecutiveNgThreshold);
    }

    public static ErrorChartData BuildErrorChartData(
        this IEnumerable<UnitRecord> source,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var list = source as IReadOnlyList<UnitRecord> ?? source.ToList();
        statsService ??= DefaultStatisticsService;
        return statsService.GetErrorChartData(list, groupFilter, defectFilter);
    }

    public static ParetoChartData BuildParetoChartData(
        this IEnumerable<UnitRecord> source,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        int topN = 10,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var list = source as IReadOnlyList<UnitRecord> ?? source.ToList();
        statsService ??= DefaultStatisticsService;
        return statsService.GetParetoChartData(list, groupFilter, defectFilter, topN);
    }

    public static HourlyTrendData BuildHourlyTrendData(
        this IEnumerable<UnitRecord> source,
        int dayShiftStartHour = 8,
        int nightShiftStartHour = 20,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(source);

        var list = source as IReadOnlyList<UnitRecord> ?? source.ToList();
        statsService ??= DefaultStatisticsService;
        return statsService.GetHourlyTrendData(list, dayShiftStartHour, nightShiftStartHour);
    }

    public static UnitStatisticsResult ComputeStatistics(
        this IReadOnlyList<UnitRecord> units,
        UnitStatusFilter filterStatus = UnitStatusFilter.All,
        int consecutiveNgThreshold = 3,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        statsService ??= DefaultStatisticsService;
        return statsService.GetUnitStatistics(units, filterStatus, consecutiveNgThreshold);
    }

    public static ErrorChartData BuildErrorChartData(
        this IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        statsService ??= DefaultStatisticsService;
        return statsService.GetErrorChartData(units, groupFilter, defectFilter);
    }

    public static ParetoChartData BuildParetoChartData(
        this IReadOnlyList<UnitRecord> units,
        PmntGroupFilter groupFilter = PmntGroupFilter.All,
        DefectFilterType defectFilter = DefectFilterType.All,
        int topN = 10,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        statsService ??= DefaultStatisticsService;
        return statsService.GetParetoChartData(units, groupFilter, defectFilter, topN);
    }

    public static HourlyTrendData BuildHourlyTrendData(
        this IReadOnlyList<UnitRecord> units,
        int dayShiftStartHour = 8,
        int nightShiftStartHour = 20,
        IStatisticsService? statsService = null)
    {
        ArgumentNullException.ThrowIfNull(units);

        statsService ??= DefaultStatisticsService;
        return statsService.GetHourlyTrendData(units, dayShiftStartHour, nightShiftStartHour);
    }
}
