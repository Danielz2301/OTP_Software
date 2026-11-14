using System.Globalization;
using System.Windows.Input;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// Pure MVVM ViewModel for the multi-mode error analytics chart (Cumulative Timeline,
/// Defect Pareto 80/20, and Hourly Throughput &amp; NG Rate Trend).
/// Decoupled from WPF UI controls: builds immutable <see cref="ChartRenderSnapshot"/>
/// payloads and localized tooltip/axis metadata consumed by View-layer renderers.
/// Uses C# 14 <c>field</c> keyword in all property setters.
/// </summary>
public sealed class ChartViewModel : ViewModelBase
{
    private readonly IStatisticsService _statisticsService;
    private readonly ILocalizationService _localization;
    private readonly IChartExportService? _chartExportService;
    private Action? _refreshSummaryAction;

    public ChartViewModel(
        IStatisticsService? statisticsService = null,
        ILocalizationService? localizationService = null,
        IChartExportService? chartExportService = null)
    {
        _statisticsService = statisticsService ?? new StatisticsService();
        _localization = localizationService ?? LocalizationService.Default;
        _chartExportService = chartExportService ?? ChartExportServiceRegistry.Default;
        _refreshSummaryAction = () => SummaryText = _localization.Get("Chart_SummaryZero");
        _refreshSummaryAction();

        ZoomInCommand = new RelayCommand(() => StepZoom(1));
        ZoomOutCommand = new RelayCommand(() => StepZoom(-1));
        ZoomResetCommand = new RelayCommand(ResetZoom);

        SetTimelineModeCommand = new RelayCommand(() => DisplayMode = ChartDisplayMode.CumulativeTimeline);
        SetParetoModeCommand = new RelayCommand(() => DisplayMode = ChartDisplayMode.DefectPareto);
        SetHourlyModeCommand = new RelayCommand(() => DisplayMode = ChartDisplayMode.HourlyTrend);
    }

    public event EventHandler? FilterChanged;
    public event EventHandler? ZoomChanged;
    public event EventHandler? LocalizationRefreshed;

    public ChartDisplayMode DisplayMode
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedDisplayModeIndex));
                ResetZoom();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = ChartDisplayMode.CumulativeTimeline;

    public int SelectedDisplayModeIndex
    {
        get => (int)DisplayMode;
        set => DisplayMode = value switch
        {
            1 => ChartDisplayMode.DefectPareto,
            2 => ChartDisplayMode.HourlyTrend,
            _ => ChartDisplayMode.CumulativeTimeline
        };
    }

    public PmntGroupFilter GroupFilter
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedGroupIndex));
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = PmntGroupFilter.All;

    public int SelectedGroupIndex
    {
        get => (int)GroupFilter;
        set => GroupFilter = value switch
        {
            1 => PmntGroupFilter.A,
            2 => PmntGroupFilter.B,
            3 => PmntGroupFilter.Other,
            _ => PmntGroupFilter.All
        };
    }

    public DefectFilterType DefectFilter
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedDefectIndex));
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = DefectFilterType.All;

    public int SelectedDefectIndex
    {
        get => (int)DefectFilter;
        set => DefectFilter = value switch
        {
            1 => DefectFilterType.Xylv,
            2 => DefectFilterType.PowerOn,
            3 => DefectFilterType.PowerOff,
            4 => DefectFilterType.Flicker,
            _ => DefectFilterType.All
        };
    }

    public bool ShowLines
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = true;

    public bool ShowLabels
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                FilterChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = true;

    public double ZoomFactor
    {
        get => field;
        set
        {
            double clamped = Math.Clamp(Math.Round(value, 2), 1.0, 8.0);
            if (Math.Abs(field - clamped) > 0.0001)
            {
                field = clamped;
                OnPropertyChanged();
                ZoomText = $"{(int)Math.Round(clamped * 100.0)}%";
                ZoomChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = 1.0;

    public string ZoomText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "100%";

    public string SummaryText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public bool IsChartEmpty
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public int ChartVisualThreshold
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = 150;

    public bool IsPanning
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public ICommand ZoomInCommand { get; }
    public ICommand ZoomOutCommand { get; }
    public ICommand ZoomResetCommand { get; }

    public ICommand SetTimelineModeCommand { get; }
    public ICommand SetParetoModeCommand { get; }
    public ICommand SetHourlyModeCommand { get; }

    public void RefreshLocalization()
    {
        _refreshSummaryAction?.Invoke();
        LocalizationRefreshed?.Invoke(this, EventArgs.Empty);
    }

    public void StepZoom(int direction)
    {
        double cur = ZoomFactor;
        if (direction > 0)
        {
            double next = Math.Min(8.0, Math.Round(cur * 1.18, 2));
            if (next <= cur) next = Math.Min(8.0, cur + 0.15);
            ZoomFactor = next;
        }
        else if (direction < 0)
        {
            double next = Math.Max(1.0, Math.Round(cur / 1.18, 2));
            if (next < 1.08) next = 1.0;
            ZoomFactor = next;
        }
    }

    public void ResetZoom() => ZoomFactor = 1.0;

    public static string GetPmntSeriesHex(string? group, int seriesIndex = 0) =>
        ChartPalette.GetPmntSeriesHex(group, seriesIndex);

    /// <summary>
    /// Computes the View-ready dataset and localized labels for the active <see cref="DisplayMode"/>,
    /// updating <see cref="SummaryText"/> and <see cref="IsChartEmpty"/> atomically.
    /// </summary>
    public ChartRenderSnapshot BuildRenderSnapshot(IReadOnlyList<UnitRecord> scopeUnits)
    {
        ArgumentNullException.ThrowIfNull(scopeUnits);

        return DisplayMode switch
        {
            ChartDisplayMode.DefectPareto => BuildParetoSnapshot(
                _statisticsService.GetParetoChartData(scopeUnits, GroupFilter, DefectFilter, topN: 10)),
            ChartDisplayMode.HourlyTrend => BuildHourlySnapshot(
                _statisticsService.GetHourlyTrendData(scopeUnits)),
            _ => BuildTimelineSnapshot(
                _statisticsService.GetErrorChartData(scopeUnits, GroupFilter, DefectFilter))
        };
    }

    /// <summary>
    /// Builds a timeline render snapshot from precomputed <see cref="ErrorChartData"/>.
    /// Set <paramref name="updateState"/> to <c>false</c> for isolated offscreen PNG exports.
    /// </summary>
    public ChartRenderSnapshot BuildTimelineSnapshot(
        ErrorChartData chartData,
        bool updateState = true,
        double? zoomOverride = null)
    {
        ArgumentNullException.ThrowIfNull(chartData);

        double zoom = Math.Clamp(zoomOverride ?? ZoomFactor, 1.0, 8.0);
        bool isEmpty = chartData.TotalErrors <= 0;

        if (updateState)
        {
            ZoomText = $"{(int)Math.Round(zoom * 100.0)}%";

            if (!isEmpty)
            {
                int totalErrs = chartData.TotalErrors;
                int uniquePmnt = chartData.UniquePmntCount;
                string topPmnt = chartData.TopPmnt;
                int topCount = chartData.TopPmntCount;
                _refreshSummaryAction = () =>
                {
                    string topInfo = !string.IsNullOrEmpty(topPmnt)
                        ? _localization.Format(
                            "Chart_SummaryTopFormat",
                            topPmnt,
                            topCount.ToString("N0", CultureInfo.InvariantCulture))
                        : string.Empty;
                    SummaryText = _localization.Format(
                        "Chart_SummaryFormat",
                        totalErrs.ToString("N0", CultureInfo.InvariantCulture),
                        uniquePmnt.ToString("N0", CultureInfo.InvariantCulture),
                        topInfo);
                };
            }
            else
            {
                _refreshSummaryAction = () => SummaryText = _localization.Get("Chart_SummaryZero");
            }

            _refreshSummaryAction();
            IsChartEmpty = isEmpty;
        }

        return new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.CumulativeTimeline,
            ShowLines = ShowLines,
            ShowLabels = ShowLabels,
            ZoomFactor = zoom,
            VisualThreshold = ChartVisualThreshold,
            IsEmpty = isEmpty,
            TimelineData = chartData,
            TimelineLabels = isEmpty ? null : BuildTimelineLabels(chartData)
        };
    }

    /// <summary>
    /// Builds a Pareto 80/20 render snapshot from precomputed <see cref="ParetoChartData"/>.
    /// </summary>
    public ChartRenderSnapshot BuildParetoSnapshot(
        ParetoChartData paretoData,
        bool updateState = true,
        double? zoomOverride = null)
    {
        ArgumentNullException.ThrowIfNull(paretoData);

        double zoom = Math.Clamp(zoomOverride ?? ZoomFactor, 1.0, 8.0);
        bool isEmpty = paretoData.Items.Count == 0 || paretoData.TotalDefects == 0;

        if (updateState)
        {
            if (!isEmpty)
            {
                int totalParetoDefects = paretoData.TotalDefects;
                int paretoItemCount = paretoData.Items.Count;
                int vitalFewCount = paretoData.VitalFewCount;
                _refreshSummaryAction = () =>
                {
                    string cutoffInfo = vitalFewCount > 0
                        ? _localization.Format(
                            "Chart_ParetoCutoffInfoFormat",
                            vitalFewCount.ToString(CultureInfo.InvariantCulture))
                        : string.Empty;
                    SummaryText = _localization.Format(
                        "Chart_ParetoSummaryFormat",
                        totalParetoDefects.ToString("N0", CultureInfo.InvariantCulture),
                        paretoItemCount.ToString("N0", CultureInfo.InvariantCulture),
                        cutoffInfo);
                };
            }
            else
            {
                _refreshSummaryAction = () => SummaryText = _localization.Get("Chart_ParetoSummaryZero");
            }

            _refreshSummaryAction();
            IsChartEmpty = isEmpty;
        }

        return new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.DefectPareto,
            ShowLines = ShowLines,
            ShowLabels = ShowLabels,
            ZoomFactor = zoom,
            VisualThreshold = ChartVisualThreshold,
            IsEmpty = isEmpty,
            ParetoData = paretoData,
            ParetoLabels = isEmpty ? null : BuildParetoLabels()
        };
    }

    /// <summary>
    /// Builds an Hourly Throughput &amp; Yield render snapshot from precomputed <see cref="HourlyTrendData"/>.
    /// </summary>
    public ChartRenderSnapshot BuildHourlySnapshot(
        HourlyTrendData hourlyData,
        bool updateState = true,
        double? zoomOverride = null)
    {
        ArgumentNullException.ThrowIfNull(hourlyData);

        double zoom = Math.Clamp(zoomOverride ?? ZoomFactor, 1.0, 8.0);
        bool isEmpty = hourlyData.Buckets.Count == 0 || hourlyData.TotalUnits == 0;

        if (updateState)
        {
            if (!isEmpty)
            {
                double overallNgRate = hourlyData.TotalUnits > 0
                    ? (hourlyData.TotalNg / (double)hourlyData.TotalUnits) * 100.0
                    : 0.0;
                int totalUnits = hourlyData.TotalUnits;
                int totalNg = hourlyData.TotalNg;
                int maxHourlyTotal = hourlyData.MaxHourlyTotal;
                _refreshSummaryAction = () =>
                {
                    SummaryText = _localization.Format(
                        "Chart_HourlySummaryFormat",
                        totalUnits.ToString("N0", CultureInfo.InvariantCulture),
                        totalNg.ToString("N0", CultureInfo.InvariantCulture),
                        overallNgRate.ToString("F2", CultureInfo.InvariantCulture),
                        maxHourlyTotal.ToString("N0", CultureInfo.InvariantCulture));
                };
            }
            else
            {
                _refreshSummaryAction = () => SummaryText = _localization.Get("Chart_HourlySummaryZero");
            }

            _refreshSummaryAction();
            IsChartEmpty = isEmpty;
        }

        return new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.HourlyTrend,
            ShowLines = ShowLines,
            ShowLabels = ShowLabels,
            ZoomFactor = zoom,
            VisualThreshold = ChartVisualThreshold,
            IsEmpty = isEmpty,
            HourlyData = hourlyData,
            HourlyLabels = isEmpty ? null : BuildHourlyLabels(hourlyData)
        };
    }

    /// <summary>
    /// Formats localized hover tooltip content for a single timeline defect node without creating UI controls.
    /// </summary>
    public ChartTooltipContent BuildTooltipContent(ErrorChartPoint point)
    {
        ArgumentNullException.ThrowIfNull(point);

        string header = _localization.Format(
            "Chart_TooltipHeaderFormat",
            point.Pmnt,
            point.ErrorCount.ToString("N0", CultureInfo.InvariantCulture),
            point.TotalPmntError.ToString("N0", CultureInfo.InvariantCulture));

        string panelText = string.IsNullOrEmpty(point.PanelName) ? "N/A" : point.PanelName;
        string body =
            $"{_localization.Get("Chart_TooltipTimestampLabel")}: {point.TimeFormatted}\n" +
            $"{_localization.Get("Chart_TooltipDefectLabel")}: {point.ErrorText}\n" +
            $"{_localization.Get("Chart_TooltipMachineLabel")}: {point.Source}\n" +
            $"{_localization.Get("Chart_TooltipPanelLabel")}: {panelText}";

        if (!string.IsNullOrWhiteSpace(point.BluName))
        {
            body += $"\n{_localization.Get("Chart_TooltipBluLabel")}: {point.BluName}";
        }

        string footer = _localization.Get("Chart_TooltipFooter");
        string group = point.Pmnt.EndsWith("B", StringComparison.OrdinalIgnoreCase) ? "B" : "A";

        return new ChartTooltipContent(header, body, footer, group);
    }

    /// <summary>
    /// Exports a visual chart element supplied by the View layer to a high-resolution PNG file.
    /// Returns <see cref="string.Empty"/> when <paramref name="chartElement"/> is <c>null</c>.
    /// </summary>
    public Task<string> ExportChartImageAsync(string filePath, object? chartElement = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        return _chartExportService is not null
            ? _chartExportService.ExportChartElementAsync(filePath, chartElement)
            : Task.FromResult(string.Empty);
    }

    /// <summary>
    /// Prepares shift chart data on the ViewModel and delegates offscreen STA rendering &amp; PNG saving
    /// to <see cref="IChartExportService"/>.
    /// </summary>
    public Task<string> ExportOffscreenShiftChartAsync(string filePath, IReadOnlyList<UnitRecord> scopeUnits)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(scopeUnits);

        if (scopeUnits.Count == 0 || _chartExportService is null)
            return Task.FromResult(string.Empty);

        var chartData = _statisticsService.GetErrorChartData(
            scopeUnits,
            PmntGroupFilter.All,
            DefectFilterType.All);

        var snapshot = BuildTimelineSnapshot(chartData, updateState: false, zoomOverride: 1.0);
        string zeroTitle = _localization.Get("Chart_ZeroShiftTitle");
        string zeroSubtitle = _localization.Format(
            "Chart_ZeroShiftSubtitleFormat",
            scopeUnits.Count.ToString("N0", CultureInfo.InvariantCulture));

        return _chartExportService.ExportOffscreenShiftChartAsync(filePath, snapshot, zeroTitle, zeroSubtitle);
    }

    private ChartTimelineLabels BuildTimelineLabels(ErrorChartData chartData)
    {
        DateTime fallbackMin = DateTime.Now.AddMinutes(-5);
        DateTime fallbackMax = fallbackMin.AddMinutes(10);
        DateTime rawMin = chartData.MinTime ?? fallbackMin;
        DateTime rawMax = chartData.MaxTime ?? fallbackMax;

        bool sameDay = rawMin.Date == rawMax.Date;
        string timeFormat = sameDay ? "HH:mm:ss" : "dd/MM HH:mm";
        string xTitleText = sameDay
            ? _localization.Format(
                "Chart_AxisTimelineSingleDayFormat",
                rawMin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : _localization.Format(
                "Chart_AxisTimelineRangeFormat",
                rawMin.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                rawMax.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

        return new ChartTimelineLabels(
            _localization.Get("Chart_AxisCumulativeY"),
            xTitleText,
            timeFormat);
    }

    private ChartParetoLabels BuildParetoLabels() => new(
        _localization.Get("Chart_ParetoCutoffTag"),
        _localization.Get("Chart_ParetoYLeft"),
        _localization.Get("Chart_ParetoYRight"),
        _localization.Get("Chart_ParetoXAxis"));

    private ChartHourlyLabels BuildHourlyLabels(HourlyTrendData hourlyData)
    {
        string shiftSuffix = !string.IsNullOrEmpty(hourlyData.ShiftLabel)
            ? $" ({hourlyData.ShiftLabel})"
            : string.Empty;

        return new ChartHourlyLabels(
            _localization.Get("Chart_HourlyYLeft"),
            _localization.Get("Chart_HourlyYRight"),
            $"{_localization.Get("Chart_HourlyXAxis")}{shiftSuffix}");
    }
}
