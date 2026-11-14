namespace OTPSoftware.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;

/// <summary>
/// Partial class of <see cref="MainViewModel"/> implementing filtering logic, debounce timer,
/// search patterns, statistics calculation, and DataGrid PMNT/Record synchronization.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _filterDebounceTimer;
    private bool _suppressFilterEvents;
    private bool _isUpdatingMetrics;
    private int _metricsGeneration;
    private PmntDefectRankItem? _lastRightClickedPmnt;
    private string? _lastBaseStatsSig;
    private int _lastRenderedChartNgCount = -1;
    private int _lastRenderedChartTotalCount = -1;
    private ChartDisplayMode _lastRenderedChartMode = ChartDisplayMode.CumulativeTimeline;
    private string? _lastRenderedChartScopeSig;
    private bool _chartDirty = true;
    private bool _hasAppliedFilterOnce;

    internal bool SuppressPmntAutoNavigate { get; set; }

    public void SetRightClickedPmnt(PmntDefectRankItem? item) => _lastRightClickedPmnt = item;

    // --- Source Filter ---

    public ObservableCollection<string> SourceFilterItems
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = ["All Machines"];

    public int SelectedSourceIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ApplyFilter();
                }
            }
        }
    }

    // --- KPI Cards Properties ---

    public string TotalItemsText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalTested));
            }
        }
    } = "0";

    public string TotalTested => TotalItemsText;

    public string OkItemsText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalOk));
            }
        }
    } = "0";

    public string TotalOk => OkItemsText;

    public string NgItemsText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(TotalNg));
            }
        }
    } = "0";

    public string TotalNg => NgItemsText;

    public string NgRateText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DefectRateText));
            }
        }
    } = "0.00%";

    public string DefectRateText => _localization.Format("Kpi_NgRateFormat", NgRateText);

    public string XylvCountText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountXylv));
            }
        }
    } = "0";

    public string CountXylv => XylvCountText;

    public string PowerOnCountText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountPowerOn));
            }
        }
    } = "0";

    public string CountPowerOn => PowerOnCountText;

    public string PowerOffCountText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountPowerOff));
            }
        }
    } = "0";

    public string CountPowerOff => PowerOffCountText;

    public string FlickerCountText
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CountFlicker));
            }
        }
    } = "0";

    public string CountFlicker => FlickerCountText;

    public double XylvShareRatio
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    }

    public string XylvShareText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "0.0%";

    public double PowerOnShareRatio
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    }

    public string PowerOnShareText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "0.0%";

    public double PowerOffShareRatio
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    }

    public string PowerOffShareText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "0.0%";

    public double FlickerShareRatio
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    }

    public string FlickerShareText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "0.0%";

    // --- Filter Bar Properties ---

    public int SelectedStatusIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ApplyFilter();
                }
            }
        }
    }

    public string SearchPmnt
    {
        get => field;
        set
        {
            string normalized = value ?? string.Empty;
            if (field != normalized)
            {
                field = normalized;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ScheduleDebouncedFilter();
                }
            }
        }
    } = string.Empty;

    public string SearchPanel
    {
        get => field;
        set
        {
            string normalized = value ?? string.Empty;
            if (field != normalized)
            {
                field = normalized;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ScheduleDebouncedFilter();
                }
            }
        }
    } = string.Empty;

    public DateTime? StartDate
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ApplyFilter();
                }
            }
        }
    }

    public DateTime DisplayStartDate
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = DateTime.Today;

    public ObservableCollection<string> Hours
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public ObservableCollection<string> Minutes
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public int SelectedStartHourIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    EnsureStartDateWhenComboChanged();
                    ApplyFilter();
                }
            }
        }
    }

    public int SelectedStartMinuteIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    EnsureStartDateWhenComboChanged();
                    ApplyFilter();
                }
            }
        }
    }

    public DateTime? EndDate
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    ApplyFilter();
                }
            }
        }
    }

    public DateTime DisplayEndDate
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = DateTime.Today;

    public int SelectedEndHourIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    EnsureEndDateWhenComboChanged();
                    ApplyFilter();
                }
            }
        }
    } = 23;

    public int SelectedEndMinuteIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!_suppressFilterEvents && !_isUpdatingMetrics)
                {
                    EnsureEndDateWhenComboChanged();
                    ApplyFilter();
                }
            }
        }
    } = 59;

    public ICommand ClearFilterCommand { get; }

    // --- Tabs, DataGrids, & Selection ---

    public int SelectedTabIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (value == 2 && _chartDirty)
                {
                    RequestChartRefresh();
                }
            }
        }
    }

    public bool AutoScroll
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public bool SoundAlert
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = true;

    public string FilteredCountText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Showing: 0 records";

    public ObservableCollection<DisplayRowViewModel> DisplayList
    {
        get => field;
        private set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public DisplayRowViewModel? SelectedResultItem
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (value is { IsNG: true })
                {
                    var rawUnit = ResolveUnitForDisplayRow(value);
                    if (rawUnit is not null)
                    {
                        _ = TriggerSpotlightForUnitAsync(rawUnit);
                    }
                }
            }
        }
    }

    public ObservableCollection<PmntDefectRankItem> PmntAList
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public ObservableCollection<PmntDefectRankItem> PmntBList
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public ObservableCollection<PmntDefectRankItem> PmntOtherList
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public bool HasPmntOther
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string PmntACountText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "(0 PMNT)";

    public string PmntBCountText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "(0 PMNT)";

    public string PmntOtherCountText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "(0 PMNT)";

    public PmntDefectRankItem? SelectedPmntA
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (!_isUpdatingMetrics && !SuppressPmntAutoNavigate && value is not null)
                {
                    string selectedName = value.Name;
                    field = null;
                    OnPropertyChanged();
                    FilterToPmntAndSwitchTab(selectedName);
                }
            }
        }
    }

    public PmntDefectRankItem? SelectedPmntB
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (!_isUpdatingMetrics && !SuppressPmntAutoNavigate && value is not null)
                {
                    string selectedName = value.Name;
                    field = null;
                    OnPropertyChanged();
                    FilterToPmntAndSwitchTab(selectedName);
                }
            }
        }
    }

    public PmntDefectRankItem? SelectedPmntOther
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (!_isUpdatingMetrics && !SuppressPmntAutoNavigate && value is not null)
                {
                    string selectedName = value.Name;
                    field = null;
                    OnPropertyChanged();
                    FilterToPmntAndSwitchTab(selectedName);
                }
            }
        }
    }

    public ICommand FilterByPmntCommand { get; }

    public ICommand CopyPmntNameCommand { get; }

    // --- Filter Execution & Metrics Update ---

    private void EnsureStartDateWhenComboChanged()
    {
        if (StartDate is null)
        {
            _suppressFilterEvents = true;
            try
            {
                StartDate = DisplayStartDate.Date;
            }
            finally
            {
                _suppressFilterEvents = false;
            }
        }
    }

    private void EnsureEndDateWhenComboChanged()
    {
        if (EndDate is null)
        {
            _suppressFilterEvents = true;
            try
            {
                EndDate = DisplayEndDate.Date;
            }
            finally
            {
                _suppressFilterEvents = false;
            }
        }
    }

    private void ScheduleDebouncedFilter()
    {
        if (Application.Current?.Dispatcher is null)
        {
            ApplyFilter();
            return;
        }

        if (_filterDebounceTimer is null)
        {
            _filterDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(_tuning.FilterDebounceMs)
            };
            _filterDebounceTimer.Tick += OnFilterDebounceTimerTick;
        }

        _filterDebounceTimer.Stop();
        _filterDebounceTimer.Start();
    }

    public (UnitFilterSpec Spec, bool HasTimeWarning) BuildFilterSpec()
    {
        string sourceName = string.Empty;
        if (SelectedSourceIndex > 0 && SelectedSourceIndex - 1 < MachineRows.Count)
        {
            sourceName = MachineRows[SelectedSourceIndex - 1].Name;
        }

        DateTime? startDt = null;
        if (StartDate.HasValue)
        {
            var d = StartDate.Value.Date;
            int h = Math.Clamp(SelectedStartHourIndex, 0, 23);
            int m = Math.Clamp(SelectedStartMinuteIndex, 0, 59);
            startDt = d.AddHours(h).AddMinutes(m);
        }

        DateTime? endDt = GetSelectedEndDateTime();

        int minYear = _tuning.MinPlausibleYear;
        bool hasTimeWarning =
            (startDt.HasValue && startDt.Value.Year < minYear) ||
            (endDt.HasValue && endDt.Value.Year < minYear) ||
            (startDt.HasValue && endDt.HasValue && startDt.Value > endDt.Value);

        var statusFilter = SelectedStatusIndex switch
        {
            1 => UnitStatusFilter.NgOnly,
            2 => UnitStatusFilter.OkOnly,
            _ => UnitStatusFilter.All
        };

        var spec = new UnitFilterSpec
        {
            SourceName = sourceName,
            PmntPattern = SearchPmnt.Trim(),
            PanelPattern = SearchPanel.Trim(),
            StartTime = startDt,
            EndTime = endDt,
            Status = statusFilter
        };

        return (spec, hasTimeWarning);
    }

    private DateTime? GetSelectedEndDateTime()
    {
        if (!EndDate.HasValue) return null;
        var d = EndDate.Value.Date;
        int h = Math.Clamp(SelectedEndHourIndex, 0, 23);
        int m = Math.Clamp(SelectedEndMinuteIndex, 0, 59);
        return d.AddHours(h).AddMinutes(m).AddSeconds(59).AddMilliseconds(999);
    }

    private void RefreshFilteredCountText()
    {
        if (!_hasAppliedFilterOnce && DisplayState.AllUnits.Count == 0 && DisplayState.ShownUnits.Count == 0 && DisplayState.EvictedCount == 0)
        {
            FilteredCountText = _localization.Get("Filter_ShowingZero");
            return;
        }

        int shownCount = DisplayState.ShownUnits.Count;
        int totalInRam = DisplayState.AllUnits.Count;
        int evictedCount = DisplayState.EvictedCount;

        FilteredCountText = evictedCount > 0
            ? _localization.Format("Filter_ShowingTrimmedFormat", shownCount, totalInRam, evictedCount)
            : _localization.Format("Filter_ShowingFormat", shownCount, totalInRam);
    }

    public void ApplyFilter()
    {
        _filterDebounceTimer?.Stop();
        _hasAppliedFilterOnce = true;

        var (spec, hasTimeWarning) = BuildFilterSpec();
        bool changed = DisplayState.Update(
            spec,
            _settings.MachinesByName,
            maxUnits: _tuning.MaxRetainedUnits,
            trimBatch: _tuning.SlidingWindowTrimBatch,
            archivePath: _sessionArchivePath,
            exportService: _exportService);

        DisplayList = DisplayState.DisplayList;

        int shownCount = DisplayState.ShownUnits.Count;
        RefreshFilteredCountText();

        if (changed)
        {
            if (AutoScroll && DisplayList.Count > 0)
            {
                AutoScrollRequested?.Invoke(this, DisplayList[^1]);
            }

            UpdateMetrics(DisplayState.ScopeUnits);
            EnsureSpotlightPopulatedFromScope();
        }

        if (!hasTimeWarning && !IsRealtimeActive)
        {
            if (TryBuildOutsideRamWindowHint(spec, shownCount, out string hint))
            {
                SetStatus(hint, "#F59E0B");
            }
            else
            {
                SetStatusLoc("Status_FilterAppliedFormat", "#38BDF8", shownCount);
            }
        }
    }

    private bool TryBuildOutsideRamWindowHint(UnitFilterSpec spec, int shownCount, out string message)
    {
        if ((_loadedWindowStart.HasValue || _loadedWindowEnd.HasValue) &&
            ((_loadedWindowStart.HasValue && (!spec.StartTime.HasValue || spec.StartTime.Value < _loadedWindowStart.Value)) ||
             (_loadedWindowEnd.HasValue && (!spec.EndTime.HasValue || spec.EndTime.Value > _loadedWindowEnd.Value))))
        {
            string startLabel = _loadedWindowStart?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "start";
            string endLabel = _loadedWindowEnd?.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) ?? "now";
            message = _localization.Format("Status_OutsideRamWindowFormat", shownCount, startLabel, endLabel);
            return true;
        }

        DateTime? earliestInRam = DisplayState.AllUnits.Count > 0 ? DisplayState.AllUnits[0].Timestamp : null;
        if (DisplayState.EvictedCount > 0 &&
            earliestInRam.HasValue &&
            ((spec.StartTime.HasValue && spec.StartTime.Value < earliestInRam.Value) ||
             (spec.EndTime.HasValue && spec.EndTime.Value < earliestInRam.Value)))
        {
            message = _localization.Format("Status_TrimmedRamWindowFormat", shownCount, earliestInRam.Value);
            return true;
        }

        message = string.Empty;
        return false;
    }

    public void UpdateMetrics(IReadOnlyList<UnitRecord> scopeUnits)
    {
        var filterStatus = SelectedStatusIndex switch
        {
            1 => UnitStatusFilter.NgOnly,
            2 => UnitStatusFilter.OkOnly,
            _ => UnitStatusFilter.All
        };

        int consecThreshold = _tuning.ConsecutiveNgThreshold;
        var baseUnits = DisplayState.BaseScopeUnits;
        string baseCheckSig = $"{DisplayState.BaseScopeSig}|{baseUnits.Count}";
        bool needBaseStats = !string.Equals(baseCheckSig, _lastBaseStatsSig, StringComparison.Ordinal);
        bool reuseScopeAsBase = (ReferenceEquals(scopeUnits, baseUnits) || baseUnits.Count == scopeUnits.Count)
            && filterStatus != UnitStatusFilter.OkOnly;

        int generation = ++_metricsGeneration;
        var dispatcher = Application.Current?.Dispatcher;
        bool offloadToThreadPool = dispatcher is not null &&
            (scopeUnits.Count > 2000 || (needBaseStats && !reuseScopeAsBase && baseUnits.Count > 2000));

        if (!offloadToThreadPool)
        {
            var stats = _statisticsService.GetUnitStatistics(scopeUnits, filterStatus, consecThreshold);
            UnitStatisticsResult? pmntStats = null;
            if (needBaseStats)
            {
                pmntStats = reuseScopeAsBase
                    ? stats
                    : _statisticsService.GetUnitStatistics(baseUnits, UnitStatusFilter.All, consecThreshold);
            }

            ApplyComputedMetrics(stats, pmntStats, needBaseStats, baseCheckSig);
            return;
        }

        var scopeSnapshot = scopeUnits.ToArray();
        UnitRecord[]? baseSnapshot = (needBaseStats && !reuseScopeAsBase) ? baseUnits.ToArray() : null;
        _ = ComputeAndApplyMetricsAsync(
            generation,
            scopeSnapshot,
            filterStatus,
            consecThreshold,
            needBaseStats,
            reuseScopeAsBase,
            baseSnapshot,
            baseCheckSig,
            dispatcher!);
    }

    private async Task ComputeAndApplyMetricsAsync(
        int generation,
        UnitRecord[] scopeSnapshot,
        UnitStatusFilter filterStatus,
        int consecThreshold,
        bool needBaseStats,
        bool reuseScopeAsBase,
        UnitRecord[]? baseSnapshot,
        string baseCheckSig,
        Dispatcher dispatcher)
    {
        try
        {
            var (stats, pmntStats) = await Task.Run(() =>
            {
                var s = _statisticsService.GetUnitStatistics(scopeSnapshot, filterStatus, consecThreshold);
                UnitStatisticsResult? p = null;
                if (needBaseStats)
                {
                    p = reuseScopeAsBase
                        ? s
                        : _statisticsService.GetUnitStatistics(baseSnapshot ?? scopeSnapshot, UnitStatusFilter.All, consecThreshold);
                }
                return (s, p);
            }).ConfigureAwait(false);

            if (_disposed || generation != _metricsGeneration) return;

            await dispatcher.InvokeAsync(() =>
            {
                if (_disposed || generation != _metricsGeneration) return;
                ApplyComputedMetrics(stats, pmntStats, needBaseStats, baseCheckSig);
            }, DispatcherPriority.DataBind).Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "UpdateMetrics");
        }
    }

    private void ApplyComputedMetrics(
        UnitStatisticsResult stats,
        UnitStatisticsResult? pmntStats,
        bool updateBasePmnts,
        string baseCheckSig)
    {
        if (_isUpdatingMetrics) return;
        _isUpdatingMetrics = true;

        try
        {
            TotalItemsText = stats.DisplayTotal.ToString("N0", CultureInfo.InvariantCulture);
            OkItemsText = stats.OkCount.ToString("N0", CultureInfo.InvariantCulture);
            NgItemsText = stats.NgCount.ToString("N0", CultureInfo.InvariantCulture);
            NgRateText = $"{stats.NgRate.ToString("F2", CultureInfo.InvariantCulture)}%";

            XylvCountText = stats.Xylv.ToString(CultureInfo.InvariantCulture);
            PowerOnCountText = stats.PowerOn.ToString(CultureInfo.InvariantCulture);
            PowerOffCountText = stats.PowerOff.ToString(CultureInfo.InvariantCulture);
            FlickerCountText = stats.Flicker.ToString(CultureInfo.InvariantCulture);

            int totalOtpDefects = stats.Xylv + stats.PowerOn + stats.PowerOff + stats.Flicker;
            if (totalOtpDefects > 0)
            {
                double invTotal = 1.0 / totalOtpDefects;
                XylvShareRatio = Math.Clamp(stats.Xylv * invTotal, 0.0, 1.0);
                PowerOnShareRatio = Math.Clamp(stats.PowerOn * invTotal, 0.0, 1.0);
                PowerOffShareRatio = Math.Clamp(stats.PowerOff * invTotal, 0.0, 1.0);
                FlickerShareRatio = Math.Clamp(stats.Flicker * invTotal, 0.0, 1.0);

                XylvShareText = $"{(XylvShareRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%";
                PowerOnShareText = $"{(PowerOnShareRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%";
                PowerOffShareText = $"{(PowerOffShareRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%";
                FlickerShareText = $"{(FlickerShareRatio * 100.0).ToString("F1", CultureInfo.InvariantCulture)}%";
            }
            else
            {
                XylvShareRatio = 0.0;
                PowerOnShareRatio = 0.0;
                PowerOffShareRatio = 0.0;
                FlickerShareRatio = 0.0;

                XylvShareText = "0.0%";
                PowerOnShareText = "0.0%";
                PowerOffShareText = "0.0%";
                FlickerShareText = "0.0%";
            }

            // Update Wallboard Hero KPI metrics & threshold alert state
            double fpy = stats.DisplayTotal > 0 ? Math.Clamp(100.0 - stats.NgRate, 0.0, 100.0) : 100.0;
            FpyRate = Math.Round(fpy, 2);
            FpyRateText = $"{FpyRate.ToString("F2", CultureInfo.InvariantCulture)}%";

            var wbThresholds = _settings.Wallboard ?? _tuning.Wallboard;
            int targetUnits = Math.Max(1, wbThresholds.ShiftTargetUnits);
            double shiftPct = Math.Clamp((stats.DisplayTotal * 100.0) / targetUnits, 0.0, 100.0);
            ShiftProgressPercent = Math.Round(shiftPct, 1);
            ShiftTargetText = _localization.Format("Kpi_ShiftTargetFormat", stats.DisplayTotal, targetUnits, ShiftProgressPercent);

            if (stats.NgRate >= wbThresholds.CriticalNgRatePercent || stats.AlarmPmnts.Count > 0)
            {
                KpiAlertLevel = _localization.Get("Kpi_AlertCritical");
                KpiHeroRateBg = "#2B1117";
                KpiHeroRateBorder = "#EF4444";
                KpiHeroRateFg = "#EF4444";
                IsCriticalAlarmFlashing = true;
            }
            else if (stats.NgRate >= wbThresholds.WarningNgRatePercent)
            {
                KpiAlertLevel = _localization.Get("Kpi_AlertWarning");
                KpiHeroRateBg = "#271A0C";
                KpiHeroRateBorder = "#F59E0B";
                KpiHeroRateFg = "#FBBF24";
                IsCriticalAlarmFlashing = false;
            }
            else
            {
                KpiAlertLevel = _localization.Get("Kpi_AlertNormal");
                KpiHeroRateBg = "#0B221C";
                KpiHeroRateBorder = "#10B981";
                KpiHeroRateFg = "#10B981";
                IsCriticalAlarmFlashing = false;
            }

            // Tab 2 (Faulty PMNT Stats) computes over BaseScopeUnits so all PMNTs stay visible when searching a PMNT
            if (updateBasePmnts && pmntStats is not null)
            {
                SyncRankCollection(PmntAList, pmntStats.PmntA);
                SyncRankCollection(PmntBList, pmntStats.PmntB);
                SyncRankCollection(PmntOtherList, pmntStats.PmntOther);

                PmntACountText = _localization.Format("Pmnt_CountSummaryFormat", pmntStats.PmntA.Count, pmntStats.PmntAErrorSum);
                PmntBCountText = _localization.Format("Pmnt_CountSummaryFormat", pmntStats.PmntB.Count, pmntStats.PmntBErrorSum);
                int otherErrorSum = pmntStats.PmntOther.Sum(p => p.Count);
                HasPmntOther = pmntStats.PmntOther.Count > 0;
                PmntOtherCountText = _localization.Format("Pmnt_CountSummaryFormat", pmntStats.PmntOther.Count, otherErrorSum);
                _lastBaseStatsSig = baseCheckSig;
            }

            // Tab 3 / Wallboard Chart: only re-render when NG count, Hourly total count, display mode, or scope filter changes
            bool chartNeedsUpdate =
                stats.NgCount != _lastRenderedChartNgCount ||
                (Chart.DisplayMode == ChartDisplayMode.HourlyTrend && stats.DisplayTotal != _lastRenderedChartTotalCount) ||
                Chart.DisplayMode != _lastRenderedChartMode ||
                !string.Equals(DisplayState.ScopeSig, _lastRenderedChartScopeSig, StringComparison.Ordinal);

            if (chartNeedsUpdate)
            {
                _lastRenderedChartNgCount = stats.NgCount;
                _lastRenderedChartTotalCount = stats.DisplayTotal;
                _lastRenderedChartMode = Chart.DisplayMode;
                _lastRenderedChartScopeSig = DisplayState.ScopeSig;

                if (SelectedTabIndex == 2 || IsKioskMode)
                {
                    RequestChartRefresh();
                }
                else
                {
                    _chartDirty = true;
                }
            }
        }
        finally
        {
            _isUpdatingMetrics = false;
        }
    }

    public void ClearFilter()
    {
        _suppressFilterEvents = true;
        try
        {
            SelectedSourceIndex = 0;
            SelectedStatusIndex = 0;
            SearchPmnt = string.Empty;
            SearchPanel = string.Empty;
            StartDate = null;
            EndDate = null;
            DisplayStartDate = DateTime.Today;
            DisplayEndDate = DateTime.Today;
            SelectedStartHourIndex = 0;
            SelectedStartMinuteIndex = 0;
            SelectedEndHourIndex = 23;
            SelectedEndMinuteIndex = 59;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        SetStatusLoc("Status_ClearedFilter", "#38BDF8");
        ApplyFilter();
    }

    // --- Chart Interactions ---

    public void RequestChartRefresh()
    {
        _chartDirty = false;
        ChartRefreshRequested?.Invoke(this, EventArgs.Empty);
    }

    public void OnChartPmntClick(string? clickedPmnt)
    {
        if (string.IsNullOrWhiteSpace(clickedPmnt)) return;

        string currentPmnt = SearchPmnt.Trim();
        _suppressFilterEvents = true;
        try
        {
            SearchPmnt = string.Equals(currentPmnt, clickedPmnt, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : clickedPmnt;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        ApplyFilter();

        if (string.IsNullOrEmpty(SearchPmnt))
        {
            SetStatusLoc("Status_ChartClearedPmnt", "#38BDF8");
        }
        else
        {
            SetStatusLoc("Status_ChartFilteringPmntFormat", "#3B82F6", clickedPmnt);
        }
    }

    public void OnChartPmntRightClick(string? clickedPmnt)
    {
        if (string.IsNullOrWhiteSpace(clickedPmnt)) return;

        _suppressFilterEvents = true;
        try
        {
            SearchPmnt = clickedPmnt;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        SelectedTabIndex = 0;
        ApplyFilter();
        SetStatusLoc("Status_ChartSwitchedLogFormat", "#38BDF8", clickedPmnt);
    }

    private void OnDisplayListReplaced(object? sender, EventArgs e)
    {
        DisplayList = DisplayState.DisplayList;
    }

    private void OnChartFilterChanged(object? sender, EventArgs e)
    {
        if (_suppressFilterEvents || _isUpdatingMetrics) return;
        _lastRenderedChartMode = Chart.DisplayMode;
        if (SelectedTabIndex == 2 || IsKioskMode)
        {
            RequestChartRefresh();
        }
        else
        {
            _chartDirty = true;
        }
    }

    private void OnFilterDebounceTimerTick(object? sender, EventArgs e)
    {
        _filterDebounceTimer?.Stop();
        ApplyFilter();
    }

    private static void SyncRankCollection(
        ObservableCollection<PmntDefectRankItem> target,
        IReadOnlyList<PmntDefectRankItem> source)
    {
        int commonCount = Math.Min(target.Count, source.Count);
        for (int i = 0; i < commonCount; i++)
        {
            if (!Equals(target[i], source[i]))
            {
                target[i] = source[i];
            }
        }

        for (int i = commonCount; i < source.Count; i++)
        {
            target.Add(source[i]);
        }

        for (int i = target.Count - 1; i >= source.Count; i--)
        {
            target.RemoveAt(i);
        }
    }

    private Task ExecuteCopyPmntNameAsync(object? parameter)
    {
        string? pmnt = parameter switch
        {
            PmntDefectRankItem rank => rank.Name,
            string s => s,
            _ => (_lastRightClickedPmnt ?? SelectedPmntA ?? SelectedPmntB ?? SelectedPmntOther)?.Name
        };

        return string.IsNullOrWhiteSpace(pmnt)
            ? Task.CompletedTask
            : CopyTextToClipboardAsync(pmnt, "Status_CopiedPmnt");
    }

    private void ExecuteFilterByPmnt(object? parameter)
    {
        string? pmnt = parameter switch
        {
            PmntDefectRankItem rank => rank.Name,
            string s => s,
            _ => (_lastRightClickedPmnt ?? SelectedPmntA ?? SelectedPmntB ?? SelectedPmntOther)?.Name
        };

        if (!string.IsNullOrWhiteSpace(pmnt))
        {
            FilterToPmntAndSwitchTab(pmnt);
        }
    }

    private void FilterToPmntAndSwitchTab(string pmnt)
    {
        _suppressFilterEvents = true;
        try
        {
            SearchPmnt = pmnt;
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        SelectedTabIndex = 0;
        ApplyFilter();
    }
}

