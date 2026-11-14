namespace OTPSoftware.App.ViewModels;

using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

/// <summary>
/// Primary MVVM coordinator for MainWindow.
/// Coordinates DisplayStateViewModel, ChartViewModel, GridActionsViewModel, SpotlightViewModel, BackgroundMonitorService,
/// and core domain services. Uses C# 14 <c>field</c> contextual keyword in all property setters.
/// Split into partial classes by functional domain (Filtering, Polling, Wallboard).
/// </summary>
public sealed partial class MainViewModel : ViewModelBase, IDisposable
{
    private readonly ISettingsService _settingsService;
    private readonly IStatisticsService _statisticsService;
    private readonly IExportService _exportService;
    private readonly IMesDetailService _mesDetailService;
    private readonly ICameraImageService _cameraImageService;
    private readonly ILogTailReaderService _logTailReaderService;
    private readonly ErrorLoggerService _errorLogger;
    private readonly TuningOptions _tuning;
    private readonly BackgroundMonitorService _backgroundMonitor;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;
    private readonly bool _hasCustomLocalizationService;
    private readonly bool _hasCustomDialogService;
    private readonly string _sessionArchivePath;
    private OtpSettings _settings;
    private bool _disposed;
    private int _isOpeningMesDetail;
    private string? _lastStatusKey = "Status_ReadyShort";
    private string _lastStatusColor = "#38BDF8";
    private object?[]? _lastStatusArgs;

    public MainViewModel(
        string? appRootDir = null,
        string? initialLogFile = null,
        string? initialLogFile2 = null,
        ISettingsService? settingsService = null,
        IStatisticsService? statisticsService = null,
        IExportService? exportService = null,
        IMesDetailService? mesDetailService = null,
        ICameraImageService? cameraImageService = null,
        ILogTailReaderService? logTailReaderService = null,
        BackgroundMonitorService? backgroundMonitor = null,
        ErrorLoggerService? errorLogger = null,
        TuningOptions? tuning = null,
        IDialogService? dialogService = null,
        ILocalizationService? localizationService = null)
    {
        AppRootDir = string.IsNullOrWhiteSpace(appRootDir)
            ? App.ResolveAppRootDir()
            : appRootDir;

        _tuning = tuning ?? new TuningOptions();
        _errorLogger = errorLogger ?? new ErrorLoggerService(AppRootDir);
        _hasCustomDialogService = dialogService is not null;
        _dialogService = dialogService ?? new WpfDialogService();
        _hasCustomLocalizationService = localizationService is not null;
        _localization = localizationService ?? new LocalizationService();
        _sessionArchivePath = Path.Combine(
            Path.GetTempPath(),
            $"OTPSoftware_evicted_{Environment.ProcessId}_{Guid.NewGuid():N}.csv");
        _settingsService = settingsService ?? new SettingsService();
        _settings = _settingsService.LoadSettings(AppRootDir);
        _statisticsService = statisticsService ?? new StatisticsService();
        _exportService = exportService ?? new ExportService(_statisticsService);
        _mesDetailService = mesDetailService ?? new MesDetailService(_settings.MesSpecs);
        _cameraImageService = cameraImageService ?? new CameraImageService();
        _logTailReaderService = logTailReaderService ?? new LogTailReaderService();
        _backgroundMonitor = backgroundMonitor ?? new BackgroundMonitorService(
            _logTailReaderService,
            _cameraImageService,
            _mesDetailService,
            _errorLogger,
            _tuning);

        var wbThresholds = _settings.Wallboard ?? _tuning.Wallboard;
        AutoRotateIntervalSec = wbThresholds.CarouselIntervalSec > 0 ? wbThresholds.CarouselIntervalSec : 15;
        CurrentShiftBadgeText = FormatShiftBadge(DateTime.Now);
        WallboardClockText = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

        var guiState = new GuiDisplayState
        {
            MaxRetainedUnits = _tuning.MaxRetainedUnits,
            TrimBatch = _tuning.SlidingWindowTrimBatch
        };
        DisplayState = new DisplayStateViewModel(guiState);
        DisplayList = DisplayState.DisplayList;
        guiState.DisplayListReplaced += OnDisplayListReplaced;

        Chart = new ChartViewModel(_statisticsService, _localization)
        {
            ChartVisualThreshold = _tuning.ChartVisualThreshold
        };
        Chart.FilterChanged += OnChartFilterChanged;

        Spotlight = new SpotlightViewModel(_backgroundMonitor, _settings, _errorLogger, _localization);
        GridActions = new GridActionsViewModel(_localization);

        // Populate 00-23 hours and 00-59 minutes
        var hoursList = new ObservableCollection<string>();
        for (int h = 0; h < 24; h++) hoursList.Add($"{h:D2}");
        Hours = hoursList;

        var minutesList = new ObservableCollection<string>();
        for (int m = 0; m < 60; m++) minutesList.Add($"{m:D2}");
        Minutes = minutesList;

        // Commands
        SetLanguageCommand = new RelayCommand(ExecuteSetLanguage);
        LoadLogCommand = new AsyncRelayCommand(LoadLogsAsync, onError: ex => _errorLogger.LogException(ex, "LoadLogCommand"));
        DismissAlertCommand = new RelayCommand(() => IsAlertVisible = false);
        ClearFilterCommand = new RelayCommand(ClearFilter);
        ToggleRealtimeCommand = new RelayCommand(ToggleRealtime);
        ExportCsvCommand = new AsyncRelayCommand(ExportCsvAsync, onError: ex => _errorLogger.LogException(ex, "ExportCsvCommand"));
        ExportShiftCommand = new AsyncRelayCommand(ExportShiftReportAsync, onError: ex => _errorLogger.LogException(ex, "ExportShiftCommand"));
        ToggleKioskCommand = new RelayCommand(() => IsKioskMode = !IsKioskMode);

        ToggleAutoRotateCommand = new RelayCommand(ExecuteToggleAutoRotate);
        CycleAutoRotateIntervalCommand = new RelayCommand(ExecuteCycleAutoRotateInterval);
        NextCarouselSlideCommand = new RelayCommand(ExecuteNextCarouselSlide);
        SelectCarouselSlideCommand = new RelayCommand(ExecuteSelectCarouselSlide);

        FilterByPmntCommand = new RelayCommand(ExecuteFilterByPmnt);
        CopyPmntNameCommand = new AsyncRelayCommand(ExecuteCopyPmntNameAsync, onError: ex => _errorLogger.LogException(ex, "CopyPmntNameCommand"));

        CopyCellCommand = new AsyncRelayCommand(ExecuteCopyCellAsync, onError: ex => _errorLogger.LogException(ex, "CopyCellCommand"));
        CopyRowCommand = new AsyncRelayCommand(param => CopySelectedRowsAsync(param as IEnumerable<DisplayRowViewModel>, includeHeader: false), onError: ex => _errorLogger.LogException(ex, "CopyRowCommand"));
        CopyRowWithHeaderCommand = new AsyncRelayCommand(param => CopySelectedRowsAsync(param as IEnumerable<DisplayRowViewModel>, includeHeader: true), onError: ex => _errorLogger.LogException(ex, "CopyRowWithHeaderCommand"));
        CopyPanelCommand = new AsyncRelayCommand(ExecuteCopyPanelAsync, onError: ex => _errorLogger.LogException(ex, "CopyPanelCommand"));
        CopyBluCommand = new AsyncRelayCommand(ExecuteCopyBluAsync, onError: ex => _errorLogger.LogException(ex, "CopyBluCommand"));
        CopyPmntCommand = new AsyncRelayCommand(ExecuteCopyCurrentPmntAsync, onError: ex => _errorLogger.LogException(ex, "CopyPmntCommand"));
        FilterCurrentPmntCommand = new RelayCommand(ExecuteFilterCurrentPmnt);
        FilterCurrentPanelCommand = new RelayCommand(ExecuteFilterCurrentPanel);
        OpenImagesBothCommand = new AsyncRelayCommand(() => OpenPanelImagesAsync(CameraTargetSide.Both), onError: ex => _errorLogger.LogException(ex, "OpenImagesBothCommand"));
        OpenImagesLeftCommand = new AsyncRelayCommand(() => OpenPanelImagesAsync(CameraTargetSide.Left), onError: ex => _errorLogger.LogException(ex, "OpenImagesLeftCommand"));
        OpenImagesRightCommand = new AsyncRelayCommand(() => OpenPanelImagesAsync(CameraTargetSide.Right), onError: ex => _errorLogger.LogException(ex, "OpenImagesRightCommand"));
        ShowMesDetailCommand = new AsyncRelayCommand(
            param => ShowMesDetailsForItemAsync(param as DisplayRowViewModel ?? SelectedResultItem),
            canExecute: param => Volatile.Read(ref _isOpeningMesDetail) == 0 && (param is DisplayRowViewModel || SelectedResultItem is not null),
            onError: ex => _errorLogger.LogException(ex, "ShowMesDetailCommand"));
        RefreshCameraHealthCommand = new AsyncRelayCommand(() => UpdateCameraHealthAsync(force: true), onError: ex => _errorLogger.LogException(ex, "RefreshCameraHealthCommand"));

        _localization.LanguageChanged += OnLanguageChanged;
        InitializeMachinesAndUserState(initialLogFile, initialLogFile2);
        RefreshLocalizedPropertiesOnLanguageChange();
    }

    public event EventHandler? ChartRefreshRequested;
    public event EventHandler<DisplayRowViewModel>? AutoScrollRequested;
    public event EventHandler<bool>? KioskModeChanged;

    public string AppRootDir { get; }

    public OtpSettings Settings => _settings;

    public TuningOptions Tuning => _tuning;

    public BackgroundMonitorService BackgroundMonitor => _backgroundMonitor;

    public DisplayStateViewModel DisplayState { get; }

    public GuiDisplayState State => DisplayState.State;

    public IReadOnlyList<UnitRecord> ScopeUnits => DisplayState.ScopeUnits;

    public ChartViewModel Chart { get; }

    public SpotlightViewModel Spotlight { get; }

    public GridActionsViewModel GridActions { get; }

    public ILocalizationService Localization => _localization;

    public string CurrentLanguage => _localization.CurrentLanguage;

    public bool IsLanguageEn => string.Equals(_localization.CurrentLanguage, "en", StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageZh => string.Equals(_localization.CurrentLanguage, "zh", StringComparison.OrdinalIgnoreCase);

    public bool IsLanguageVi => string.Equals(_localization.CurrentLanguage, "vi", StringComparison.OrdinalIgnoreCase);

    public ICommand SetLanguageCommand { get; }

    internal string SessionArchivePath => _sessionArchivePath;

    private void ExecuteSetLanguage(object? parameter)
    {
        if (parameter is string code && _localization.SetLanguage(code))
        {
            ScheduleUserStateSave();
        }
    }

    private void OnLanguageChanged(object? sender, string newLanguage)
    {
        OnPropertyChanged(nameof(CurrentLanguage));
        OnPropertyChanged(nameof(IsLanguageEn));
        OnPropertyChanged(nameof(IsLanguageZh));
        OnPropertyChanged(nameof(IsLanguageVi));
        OnPropertyChanged(nameof(DefectRateText));
        OnPropertyChanged(nameof(AutoRotateButtonText));

        RefreshLocalizedPropertiesOnLanguageChange();
    }

    private void RefreshLocalizedPropertiesOnLanguageChange()
    {
        // 1. Subtitle & SourceFilterItems
        if (MachineRows.Count > 0)
        {
            string namesStr = string.Join(" + ", MachineRows.Select(m => m.Name));
            AppSubtitle = _localization.Format("Header_SubtitleMachines", MachineRows.Count, namesStr);
        }
        else
        {
            AppSubtitle = _localization.Get("Header_SubtitleDefault");
        }

        int prevSourceIdx = SelectedSourceIndex;
        _suppressFilterEvents = true;
        try
        {
            var newSourceItems = new ObservableCollection<string> { _localization.Get("Filter_AllMachines") };
            foreach (var row in MachineRows)
            {
                row.RefreshLocalization();
                newSourceItems.Add(_localization.Format("Filter_OnlyMachineFormat", row.Name));
            }
            SourceFilterItems = newSourceItems;
            SelectedSourceIndex = Math.Clamp(prevSourceIdx, 0, Math.Max(0, newSourceItems.Count - 1));
        }
        finally
        {
            _suppressFilterEvents = false;
        }

        // 2. Realtime button & status
        RealtimeButtonText = _localization.Get(IsRealtimeActive ? "Filter_StopRealtime" : "Filter_StartRealtime");
        RealtimeStatusText = _localization.Get(IsRealtimeActive ? "Footer_RtRunning" : "Footer_RtStopped");

        // 3. Shift badge & Wallboard carousel title
        CurrentShiftBadgeText = FormatShiftBadge(DateTime.Now);
        WallboardCarouselTitle = GetCarouselSlideTitle(WallboardCarouselIndex);

        // 4. Footer filtered count & unloaded file info
        RefreshFilteredCountText();
        if (DisplayState.AllUnits.Count == 0 && _tailStates.Count == 0)
        {
            FileInfoText = _localization.Get("Footer_FileNotLoaded");
        }

        // 5. Default alert strings when not actively showing a custom unit alert
        if (!IsAlertVisible)
        {
            AlertTitle = _localization.Get("Alert_NewDefectTitle");
            AlertMessage = _localization.Get("Alert_NewDefectDefault");
        }

        // 6. Sub-ViewModels
        GridActions.RefreshLocalization(_settings.MachinesByName);
        Spotlight.RefreshLocalization();
        Chart.RefreshLocalization();
        RequestChartRefresh();

        // 7. Re-evaluate metrics strings (ShiftTargetText, KpiAlertLevel, PmntACountText, etc.)
        _lastBaseStatsSig = null;
        UpdateMetrics(DisplayState.ScopeUnits);

        // 8. Re-apply last localized status indicator
        if (!string.IsNullOrEmpty(_lastStatusKey))
        {
            StatusIndicatorText = _lastStatusArgs is { Length: > 0 }
                ? _localization.Format(_lastStatusKey, _lastStatusArgs)
                : _localization.Get(_lastStatusKey);
            StatusIndicatorColor = _lastStatusColor;
        }
    }

    // --- Header & Machines ---

    public string AppSubtitle
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Multi-Machine Parallel Monitoring";

    public ObservableCollection<MachineRowViewModel> MachineRows
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public string StatusIndicatorText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "● Ready";

    public string StatusIndicatorColor
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#38BDF8";

    // --- Context Menu / Cell Actions ---

    public ICommand CopyCellCommand { get; }
    public ICommand CopyRowCommand { get; }
    public ICommand CopyRowWithHeaderCommand { get; }
    public ICommand CopyPanelCommand { get; }
    public ICommand CopyBluCommand { get; }
    public ICommand CopyPmntCommand { get; }
    public ICommand FilterCurrentPmntCommand { get; }
    public ICommand FilterCurrentPanelCommand { get; }
    public ICommand OpenImagesBothCommand { get; }
    public ICommand OpenImagesLeftCommand { get; }
    public ICommand OpenImagesRightCommand { get; }
    public ICommand ShowMesDetailCommand { get; }

    private async Task CopyTextToClipboardAsync(string? text, string prefixKey, string? customColumnLabel = null)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (await GridActionsViewModel.SetSafeClipboardAsync(text, _tuning.ClipboardRetries, _tuning.ClipboardRetryDelayMs).ConfigureAwait(true))
        {
            string prefix = !string.IsNullOrEmpty(customColumnLabel)
                ? _localization.Format(prefixKey, customColumnLabel)
                : _localization.Get(prefixKey);
            SetStatus($"{prefix}: {text}", "#10B981");
        }
    }

    public Task CopyDoubleClickedCellAsync(DisplayRowViewModel? item, string? colProp, string? colHeader = null)
    {
        if (item is null || string.IsNullOrEmpty(colProp)) return Task.CompletedTask;
        string val = GridActionsViewModel.GetCellValueByProperty(item, colProp);
        return CopyTextToClipboardAsync(val, "Status_CopiedColFormat", colHeader ?? colProp);
    }

    private Task ExecuteCopyCellAsync()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        string prop = GridActions.LastRightClickedColProp;
        if (item is null || string.IsNullOrEmpty(prop)) return Task.CompletedTask;

        string val = GridActionsViewModel.GetCellValueByProperty(item, prop);
        return CopyTextToClipboardAsync(val, "Status_CopiedPrefix");
    }

    public async Task CopySelectedRowsAsync(IEnumerable<DisplayRowViewModel>? rows, bool includeHeader = false)
    {
        var list = (rows ?? GridActions.SelectedRowsProvider?.Invoke())?.Where(r => r is not null).ToList() ?? [];
        if (list.Count == 0)
        {
            var fallback = GridActions.LastRightClickedItem ?? SelectedResultItem;
            if (fallback is not null) list.Add(fallback);
        }
        if (list.Count == 0) return;

        string clipText = GridActionsViewModel.FormatSelectedRows(list, includeHeader);
        if (await GridActionsViewModel.SetSafeClipboardAsync(clipText, _tuning.ClipboardRetries, _tuning.ClipboardRetryDelayMs).ConfigureAwait(true))
        {
            if (list.Count == 1)
            {
                SetStatusLoc("Status_CopiedOneRow", "#10B981");
            }
            else
            {
                SetStatusLoc("Status_CopiedRowsFormat", "#10B981", list.Count);
            }
        }
    }

    private Task ExecuteCopyPanelAsync()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        return CopyTextToClipboardAsync(item?.PanelName, "Status_CopiedPanel");
    }

    private Task ExecuteCopyBluAsync()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        return CopyTextToClipboardAsync(item?.BluName, "Status_CopiedBlu");
    }

    private Task ExecuteCopyCurrentPmntAsync()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        return CopyTextToClipboardAsync(item?.PMNT, "Status_CopiedPmnt");
    }

    private void ExecuteFilterCurrentPmnt()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        if (item is not null && !string.IsNullOrEmpty(item.PMNT))
        {
            _suppressFilterEvents = true;
            try { SearchPmnt = item.PMNT; }
            finally { _suppressFilterEvents = false; }
            ApplyFilter();
        }
    }

    private void ExecuteFilterCurrentPanel()
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        if (item is not null && !string.IsNullOrEmpty(item.PanelName))
        {
            _suppressFilterEvents = true;
            try { SearchPanel = item.PanelName; }
            finally { _suppressFilterEvents = false; }
            ApplyFilter();
        }
    }

    public async Task OpenPanelImagesAsync(CameraTargetSide targetSide)
    {
        var item = GridActions.LastRightClickedItem ?? SelectedResultItem;
        if (item is null) return;

        string panel = item.PanelName;
        if (string.IsNullOrWhiteSpace(panel))
        {
            SetStatusLoc("Status_NoPanelForImages", "#EF4444");
            return;
        }

        string source = item.Source;
        IReadOnlyList<CameraImageConfig> images = [];
        if (_settings.MachinesByName.TryGetValue(source, out var mCfg))
        {
            images = mCfg.Images;
        }

        var targets = new List<PanelImageSearchTarget>(2);
        if ((targetSide == CameraTargetSide.Both || targetSide == CameraTargetSide.Left) && images.Count >= 1)
        {
            targets.Add(new PanelImageSearchTarget(images[0].Root, "Left", images[0].Code));
        }
        if ((targetSide == CameraTargetSide.Both || targetSide == CameraTargetSide.Right) && images.Count >= 2)
        {
            targets.Add(new PanelImageSearchTarget(images[1].Root, "Right", images[1].Code));
        }

        if (targets.Count == 0)
        {
            SetStatusLoc("Status_ImagesNotConfiguredFormat", "#EF4444", source);
            return;
        }

        SetStatusLoc("Status_SearchingImagesFormat", "#F59E0B", panel, source);

        var searchResults = await _backgroundMonitor.FindPanelImagesAsync(
            targets,
            panel,
            item.Timestamp,
            _tuning.ImageSearchTimeoutSec);

        int openedCount = 0;
        var offlineList = new List<string>();
        string? errText = null;

        foreach (var r in searchResults)
        {
            if (r.Offline)
            {
                offlineList.Add($"{r.Side} ({r.Root})");
                continue;
            }
            if (!string.IsNullOrEmpty(r.Error) && string.IsNullOrEmpty(errText))
            {
                errText = r.Error;
            }
            if (!string.IsNullOrWhiteSpace(r.DirectoryPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{r.DirectoryPath}\"")
                    {
                        UseShellExecute = true
                    });
                    openedCount++;
                }
                catch (Exception ex)
                {
                    errText = ex.Message;
                }
            }
        }

        if (openedCount > 0)
        {
            SetStatusLoc("Status_OpenedImagesFormat", "#38BDF8", panel, source, openedCount);
        }
        else if (offlineList.Count > 0)
        {
            SetStatusLoc("Status_ImageHostOfflineFormat", "#EF4444", string.Join(", ", offlineList));
        }
        else if (!string.IsNullOrEmpty(errText))
        {
            SetStatusLoc("Status_ImageSearchErrorFormat", "#EF4444", errText);
        }
        else
        {
            SetStatusLoc("Status_ImageFolderNotFoundFormat", "#EF4444", panel, source);
        }
    }

    public IReadOnlyList<string> GetMachineMesLogCandidates(string source)
    {
        var candidates = new List<string>();
        var mRow = MachineRows.FirstOrDefault(r => string.Equals(r.Name, source, StringComparison.OrdinalIgnoreCase));

        void AddIfUnique(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            string trimmed = candidate.Trim();
            if (!candidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
            {
                candidates.Add(trimmed);
            }
        }

        if (mRow is not null)
        {
            AddIfUnique(mRow.MesLogPath);
            if (!string.IsNullOrWhiteSpace(mRow.LogPath))
            {
                string? otpDir = Path.GetDirectoryName(mRow.LogPath.Trim());
                if (!string.IsNullOrWhiteSpace(otpDir))
                {
                    AddIfUnique(Path.Combine(otpDir, "MES_1.log"));
                }
            }
        }

        if (_settings.MachinesByName.TryGetValue(source, out var cfg))
        {
            AddIfUnique(cfg.MesLogPath);
            if (!string.IsNullOrWhiteSpace(cfg.LogPath))
            {
                string? otpDir = Path.GetDirectoryName(cfg.LogPath.Trim());
                if (!string.IsNullOrWhiteSpace(otpDir))
                {
                    AddIfUnique(Path.Combine(otpDir, "MES_1.log"));
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(AppRootDir))
        {
            AddIfUnique(Path.Combine(AppRootDir, "logs", "MES_1.log"));
        }

        return candidates;
    }

    internal void ReportUnexpectedUiError(Exception ex, string context) =>
        _errorLogger.LogException(ex, context);

    public async Task ShowMesDetailsForItemAsync(DisplayRowViewModel? item)
    {
        if (item is null || Interlocked.CompareExchange(ref _isOpeningMesDetail, 1, 0) != 0) return;

        (ShowMesDetailCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        try
        {
            string panelName = item.PanelName;
            if (string.IsNullOrWhiteSpace(panelName) || string.Equals(panelName, "N/A", StringComparison.OrdinalIgnoreCase))
            {
                SetStatusLoc("Status_NoValidPanelForMes", "#F59E0B");
                ShowNotificationIfInteractive(
                    _localization.Get("Dlg_NoValidPanelForMesMsg"),
                    _localization.Get("Dlg_NotificationTitle"),
                    DialogMessageSeverity.Warning);
                return;
            }

            string source = item.Source;
            var candidates = GetMachineMesLogCandidates(source);
            if (candidates.Count == 0)
            {
                SetStatusLoc("Status_MesNotConfiguredFormat", "#F59E0B", source);
                ShowNotificationIfInteractive(
                    _localization.Format("Dlg_MesNotConfiguredMsgFormat", source),
                    _localization.Get("Dlg_MesNotFoundTitle"),
                    DialogMessageSeverity.Warning);
                return;
            }

            IsBusy = true;
            SetStatusLoc("Status_LookingUpMesFormat", "#F59E0B", panelName, source);

            try
            {
                var res = await _backgroundMonitor.FindMesDetailsAsync(candidates, panelName, _tuning.ImageSearchTimeoutSec);
                IsBusy = false;

                if (!string.IsNullOrEmpty(res.Error))
                {
                    SetStatusLoc("Status_MesReadErrorFormat", "#EF4444", res.Error);
                    ShowNotificationIfInteractive(
                        _localization.Format("Dlg_MesReadErrorMsgFormat", res.Error),
                        _localization.Get("Dlg_ErrorTitle"),
                        DialogMessageSeverity.Error);
                    return;
                }

                if (!res.FoundFile)
                {
                    SetStatusLoc("Status_MesNotFoundOrLostFormat", "#EF4444", source);
                    ShowNotificationIfInteractive(
                        _localization.Format("Dlg_MesNotFoundMsgFormat", source, res.MesPath),
                        _localization.Get("Dlg_MesNotFoundTitle"),
                        DialogMessageSeverity.Warning);
                    return;
                }

                if (res.Details.Count == 0)
                {
                    SetStatusLoc("Status_NoMesDataFormat", "#F59E0B", panelName);
                    ShowNotificationIfInteractive(
                        _localization.Format("Dlg_NoMesBodyMsgFormat", panelName, res.MesPath),
                        _localization.Get("Dlg_NoMesDataTitle"),
                        DialogMessageSeverity.Information);
                    return;
                }

                SetStatusLoc("Status_LoadedMesDetailsFormat", "#38BDF8", panelName, res.Details.Count);
                IReadOnlyList<CameraImageConfig>? cameras = _settings.MachinesByName.TryGetValue(source, out var mCfg)
                    ? mCfg.Images
                    : null;
                _dialogService.ShowMesDetailDialog(item, res.Details, res.MesPath, _backgroundMonitor, cameras);
            }
            finally
            {
                IsBusy = false;
            }
        }
        finally
        {
            Volatile.Write(ref _isOpeningMesDetail, 0);
            (ShowMesDetailCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        }
    }

    private void TryDeleteSessionArchiveFile()
    {
        try
        {
            if (File.Exists(_sessionArchivePath))
            {
                File.Delete(_sessionArchivePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _errorLogger.LogException(ex, "TryDeleteSessionArchiveFile");
        }
    }

    public void SetStatus(string text, string color = "#38BDF8")
    {
        _lastStatusKey = null;
        _lastStatusArgs = null;
        _lastStatusColor = color;
        StatusIndicatorText = text;
        StatusIndicatorColor = color;
    }

    public void SetStatusLoc(string key, string color = "#38BDF8", params object?[] args)
    {
        _lastStatusKey = key;
        _lastStatusColor = color;
        _lastStatusArgs = args;
        StatusIndicatorText = args is { Length: > 0 }
            ? _localization.Format(key, args)
            : _localization.Get(key);
        StatusIndicatorColor = color;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _localization.LanguageChanged -= OnLanguageChanged;
        State.DisplayListReplaced -= OnDisplayListReplaced;
        Chart.FilterChanged -= OnChartFilterChanged;

        if (_filterDebounceTimer is not null)
        {
            _filterDebounceTimer.Stop();
            _filterDebounceTimer.Tick -= OnFilterDebounceTimerTick;
            _filterDebounceTimer = null;
        }

        if (_hasPendingStateSave && !_suppressStateSave)
        {
            SaveCurrentUserState();
        }

        if (_stateSaveDebounceTimer is not null)
        {
            _stateSaveDebounceTimer.Stop();
            _stateSaveDebounceTimer.Tick -= OnStateSaveDebounceTimerTick;
            _stateSaveDebounceTimer = null;
        }

        foreach (var row in MachineRows)
        {
            row.PropertyChanged -= OnMachineRowPropertyChanged;
        }

        if (_wallboardTimer is not null)
        {
            _wallboardTimer.Stop();
            _wallboardTimer.Tick -= OnWallboardTimerTick;
            _wallboardTimer = null;
        }

        StopBackgroundActivities();
        Spotlight.Dispose();
        _backgroundMonitor.Dispose();
        DisplayState.State.Dispose();
        TryDeleteSessionArchiveFile();
    }
}
