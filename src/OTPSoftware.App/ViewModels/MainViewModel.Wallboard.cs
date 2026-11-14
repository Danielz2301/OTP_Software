namespace OTPSoftware.App.ViewModels;

using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;

/// <summary>
/// Partial class of <see cref="MainViewModel"/> implementing Industrial Wallboard (F11 fullscreen) mode,
/// high-contrast KPI Hero Cards, Auto-Carousel chart rotation, alert banners, and Live NG Spotlight integration.
/// </summary>
public sealed partial class MainViewModel
{
    private DispatcherTimer? _wallboardTimer;
    private int _carouselTickCounter;
    private string? _lastSpotlightPanelKey;

    // --- Kiosk / Wallboard Fullscreen Mode Commands & State ---

    public ICommand ToggleKioskCommand { get; }

    public bool IsKioskMode
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (value)
                {
                    _carouselTickCounter = 0;
                    WallboardCarouselProgress = 0.0;
                    WallboardClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    CurrentShiftBadgeText = FormatShiftBadge(DateTime.Now);
                    StartWallboardTimerIfAvailable();
                    if (WallboardCarouselIndex < 3)
                    {
                        SyncChartModeToCarouselIndex(WallboardCarouselIndex);
                    }
                    RequestChartRefresh();
                    EnsureSpotlightPopulatedFromScope();
                }
                else
                {
                    StopWallboardTimer();
                    if (SelectedTabIndex == 2 && _chartDirty)
                    {
                        RequestChartRefresh();
                    }
                }
                KioskModeChanged?.Invoke(this, value);
            }
        }
    }

    // --- Wallboard & KPI Hero Card Properties ---

    public double FpyRate
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    } = 100.0;

    public string FpyRateText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "100.00%";

    public double ShiftProgressPercent
    {
        get => field;
        set { if (Math.Abs(field - value) > 0.0001) { field = value; OnPropertyChanged(); } }
    }

    public string ShiftTargetText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Target: 0 / 15,000 (0.0%)";

    public string KpiAlertLevel
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Normal";

    public string KpiHeroRateBg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#0B221C";

    public string KpiHeroRateBorder
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#10B981";

    public string KpiHeroRateFg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#10B981";

    public bool IsCriticalAlarmFlashing
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string WallboardClockText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    public string CurrentShiftBadgeText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "DAY SHIFT (08:30 - 20:30)";

    public bool IsAutoRotateActive
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutoRotateButtonText));
            }
        }
    } = true;

    public int AutoRotateIntervalSec
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AutoRotateButtonText));
            }
        }
    } = 15;

    public string AutoRotateButtonText => IsAutoRotateActive
        ? _localization.Format("Wall_AutoRotateOnFormat", AutoRotateIntervalSec)
        : _localization.Get("Wall_AutoRotatePaused");

    public double WallboardCarouselProgress
    {
        get => field;
        set
        {
            double clamped = Math.Clamp(value, 0.0, 1.0);
            if (Math.Abs(field - clamped) > 0.0001)
            {
                field = clamped;
                OnPropertyChanged();
            }
        }
    }

    internal string GetCarouselSlideTitle(int slideIndex) => slideIndex switch
    {
        0 => _localization.Get("Wall_Slide1Header"),
        1 => _localization.Get("Wall_Slide2Header"),
        2 => _localization.Get("Wall_Slide3Header"),
        _ => _localization.Get("Wall_Slide4Header")
    };

    public int WallboardCarouselIndex
    {
        get => field;
        set
        {
            int clamped = ((value % 4) + 4) % 4;
            if (field != clamped)
            {
                field = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsWallboardChartSlide));
                OnPropertyChanged(nameof(IsWallboardPmntSlide));
                WallboardCarouselTitle = GetCarouselSlideTitle(clamped);

                if (clamped < 3)
                {
                    SyncChartModeToCarouselIndex(clamped);
                    if (IsKioskMode)
                    {
                        RequestChartRefresh();
                    }
                }
            }
        }
    }

    public bool IsWallboardChartSlide => WallboardCarouselIndex < 3;

    public bool IsWallboardPmntSlide => WallboardCarouselIndex == 3;

    public string WallboardCarouselTitle
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "[1/4] Top Defect Pareto (80/20 Analysis)";

    public ICommand ToggleAutoRotateCommand { get; }

    public ICommand CycleAutoRotateIntervalCommand { get; }

    public ICommand NextCarouselSlideCommand { get; }

    public ICommand SelectCarouselSlideCommand { get; }

    // --- Alert Banner Properties & Commands ---

    public bool IsAlertVisible
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string AlertTitle
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "NEW DEFECT ALERT:";

    public string AlertMessage
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Defect NG detected...";

    public string AlertTitleColor
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#F87171";

    public string AlertBackground
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#241218";

    public string AlertBorderBrush
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#EF4444";

    public ICommand DismissAlertCommand { get; }

    // --- Wallboard & Carousel Coordination ---

    internal bool IsWallboardTimerRunning => _wallboardTimer?.IsEnabled == true;

    private void StartWallboardTimerIfAvailable()
    {
        var dispatcher = Application.Current?.Dispatcher ?? Dispatcher.FromThread(Thread.CurrentThread);
        if (dispatcher is null) return;

        if (_wallboardTimer is null)
        {
            _wallboardTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _wallboardTimer.Tick += OnWallboardTimerTick;
        }

        if (!_wallboardTimer.IsEnabled)
        {
            _wallboardTimer.Start();
        }
    }

    private void StopWallboardTimer() => _wallboardTimer?.Stop();

    private void OnWallboardTimerTick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;
        WallboardClockText = now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        CurrentShiftBadgeText = FormatShiftBadge(now);

        if (IsKioskMode && IsAutoRotateActive)
        {
            _carouselTickCounter++;
            int interval = Math.Max(3, AutoRotateIntervalSec);
            if (_carouselTickCounter >= interval)
            {
                _carouselTickCounter = 0;
                WallboardCarouselProgress = 0.0;
                WallboardCarouselIndex = (WallboardCarouselIndex + 1) % 4;
            }
            else
            {
                WallboardCarouselProgress = (double)_carouselTickCounter / interval;
            }
        }
    }

    internal string FormatShiftBadge(DateTime now)
    {
        var (dayStart, nightStart) = GetConfiguredShiftBoundaries();
        if (dayStart >= nightStart)
        {
            dayStart = new TimeSpan(8, 0, 0);
            nightStart = new TimeSpan(20, 0, 0);
        }
        var tod = now.TimeOfDay;
        bool isDay = tod >= dayStart && tod < nightStart;
        return isDay
            ? _localization.Format("Wall_DayShiftFormat", dayStart, nightStart)
            : _localization.Format("Wall_NightShiftFormat", nightStart, dayStart);
    }

    private void SyncChartModeToCarouselIndex(int slideIndex)
    {
        var targetMode = slideIndex switch
        {
            0 => ChartDisplayMode.DefectPareto,
            1 => ChartDisplayMode.HourlyTrend,
            _ => ChartDisplayMode.CumulativeTimeline
        };
        Chart.DisplayMode = targetMode;
    }

    private void ExecuteToggleAutoRotate()
    {
        _carouselTickCounter = 0;
        WallboardCarouselProgress = 0.0;
        IsAutoRotateActive = !IsAutoRotateActive;
    }

    private void ExecuteCycleAutoRotateInterval()
    {
        _carouselTickCounter = 0;
        WallboardCarouselProgress = 0.0;
        AutoRotateIntervalSec = AutoRotateIntervalSec == 15 ? 30 : 15;
    }

    private void ExecuteNextCarouselSlide()
    {
        _carouselTickCounter = 0;
        WallboardCarouselProgress = 0.0;
        WallboardCarouselIndex = (WallboardCarouselIndex + 1) % 4;
    }

    private void ExecuteSelectCarouselSlide(object? parameter)
    {
        int idx = parameter switch
        {
            int i => i,
            string s when int.TryParse(s, out int parsed) => parsed,
            _ => 0
        };
        _carouselTickCounter = 0;
        WallboardCarouselProgress = 0.0;
        WallboardCarouselIndex = idx;
    }

    // --- Spotlight Coordination ---

    public Task TriggerSpotlightForUnitAsync(UnitRecord? unit, int? consecutiveNg = null)
    {
        if (unit is null || !unit.IsNg) return Task.CompletedTask;
        string key = $"{unit.Source}|{unit.Pmnt}|{unit.PanelName}|{unit.Timestamp:O}|{unit.Index}";
        if (string.Equals(_lastSpotlightPanelKey, key, StringComparison.Ordinal) && Spotlight.HasSpotlightUnit)
        {
            return Task.CompletedTask;
        }
        _lastSpotlightPanelKey = key;
        var candidates = GetMachineMesLogCandidates(unit.Source);
        int streak = consecutiveNg ?? DisplayState.GetUnitConsecutiveNg(unit);
        if (streak <= 0 && unit.IsNg) streak = 1;
        return Spotlight.InspectUnitAsync(unit, candidates, _tuning.ConsecutiveNgThreshold, streak);
    }

    private void EnsureSpotlightPopulatedFromScope()
    {
        if (Spotlight.HasSpotlightUnit) return;
        var scope = DisplayState.ScopeUnits;
        for (int i = scope.Count - 1; i >= 0; i--)
        {
            if (scope[i].IsNg)
            {
                _ = TriggerSpotlightForUnitAsync(scope[i]);
                break;
            }
        }
    }

    private UnitRecord? ResolveUnitForDisplayRow(DisplayRowViewModel? row)
    {
        if (row is null) return null;
        var shown = DisplayState.ShownUnits;
        int idx = row.No - 1;
        if ((uint)idx < (uint)shown.Count)
        {
            var candidate = shown[idx];
            if (string.Equals(candidate.PanelName, row.PanelName, StringComparison.Ordinal) &&
                string.Equals(candidate.Pmnt, row.PMNT, StringComparison.Ordinal))
            {
                return candidate;
            }
        }

        for (int i = shown.Count - 1; i >= 0; i--)
        {
            var u = shown[i];
            if (string.Equals(u.PanelName, row.PanelName, StringComparison.Ordinal) &&
                string.Equals(u.Pmnt, row.PMNT, StringComparison.Ordinal) &&
                u.Timestamp == row.Timestamp)
            {
                return u;
            }
        }

        return null;
    }
}
