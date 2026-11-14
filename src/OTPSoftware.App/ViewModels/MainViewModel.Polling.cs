namespace OTPSoftware.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Models;

/// <summary>
/// Partial class of <see cref="MainViewModel"/> implementing machine log loading, realtime tail polling,
/// camera health checks, shift auto-exporting, and machine file configuration.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, LogTailState?> _tailStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<IReadOnlyList<MachineTailReadResult>> _pendingRealtimeBatches = new();
    private DispatcherTimer? _stateSaveDebounceTimer;
    private bool _hasPendingStateSave;
    private bool _rtHadProblem;
    private bool _cameraHealthBusy;
    private bool _isLoadingLogs;
    private bool _suppressStateSave;
    private DateTime _lastCameraHealthCheckUtc = DateTime.MinValue;
    private string _currentShiftKey = string.Empty;
    private string _lastAutoExportedShiftKey = string.Empty;
    private int _autoExportInFlight;
    private bool _hadPartialTailStart;
    private DateTime? _partialTailStartTime;
    private DateTime? _loadedWindowStart;
    private DateTime? _loadedWindowEnd;

    public string LastAutoExportedShiftKey => _lastAutoExportedShiftKey;

    // --- Machine Log File Loading & Real-time Properties ---

    public bool IsBusy
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (!value && !_isLoadingLogs)
                {
                    FlushPendingRealtimeBatches();
                }
            }
        }
    }

    public ICommand LoadLogCommand { get; }

    public ICommand ToggleRealtimeCommand { get; }

    public string RealtimeButtonText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Start Real-time";

    public bool IsRealtimeActive
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsNotRealtimeActive));
            }
        }
    }

    public bool IsNotRealtimeActive => !IsRealtimeActive;

    public string RealtimeStatusText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Real-time: Stopped";

    public string RealtimeStatusColor
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#64748B";

    public string FileInfoText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "File: Not loaded";

    public ICommand ExportCsvCommand { get; }

    public ICommand ExportShiftCommand { get; }

    public ObservableCollection<CameraHealthStatus> CameraHealthItems
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public ICommand RefreshCameraHealthCommand { get; }

    // --- Initialization & Lifecycle ---

    private void InitializeMachinesAndUserState(string? initialLogFile, string? initialLogFile2)
    {
        var userState = _settingsService.LoadUserState(AppRootDir);
        if (!_hasCustomLocalizationService && !string.IsNullOrWhiteSpace(userState.Language))
        {
            _localization.SetLanguage(userState.Language);
        }

        var userMachinesByName = userState.Machines?
            .Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase)
            ?? new Dictionary<string, UserMachineState>(StringComparer.OrdinalIgnoreCase);

        _suppressFilterEvents = true;
        try
        {
            AutoScroll = userState.AutoScroll;
            SoundAlert = userState.SoundAlert;
            if (userState.SelectedTab is >= 0 and <= 2)
            {
                SelectedTabIndex = userState.SelectedTab;
            }

            foreach (var oldRow in MachineRows)
            {
                oldRow.PropertyChanged -= OnMachineRowPropertyChanged;
            }

            var machines = _settings.Machines.ToList();
            var rows = new ObservableCollection<MachineRowViewModel>();
            var sourceFilters = new ObservableCollection<string> { _localization.Get("Filter_AllMachines") };

            for (int i = 0; i < machines.Count; i++)
            {
                var m = machines[i];
                bool enabled = m.Enabled;
                string logPath = m.LogPath;
                string mesLogPath = m.MesLogPath;

                if (userMachinesByName.TryGetValue(m.Name, out var savedMachine))
                {
                    enabled = savedMachine.Enabled;
                    if (!string.IsNullOrWhiteSpace(savedMachine.LogPath))
                        logPath = savedMachine.LogPath;
                    if (!string.IsNullOrWhiteSpace(savedMachine.MesLogPath))
                        mesLogPath = savedMachine.MesLogPath;
                }

                if (i == 0 && !string.IsNullOrEmpty(initialLogFile))
                {
                    logPath = initialLogFile;
                    string? initDir = Path.GetDirectoryName(initialLogFile);
                    if (!string.IsNullOrWhiteSpace(initDir))
                    {
                        mesLogPath = Path.Combine(initDir, "MES_1.log");
                    }
                    enabled = true;
                }
                else if (i == 1 && !string.IsNullOrEmpty(initialLogFile2))
                {
                    logPath = initialLogFile2;
                    string? initDir2 = Path.GetDirectoryName(initialLogFile2);
                    if (!string.IsNullOrWhiteSpace(initDir2))
                    {
                        mesLogPath = Path.Combine(initDir2, "MES_1.log");
                    }
                    enabled = true;
                }

                var style = GuiDisplayState.GetMachineVisualStyle(m.Name, _settings);
                var row = new MachineRowViewModel(
                    name: m.Name,
                    enabled: enabled,
                    color: style.Foreground,
                    bgColor: style.Background,
                    logPath: logPath,
                    mesLogPath: mesLogPath,
                    onChanged: _ => ScheduleUserStateSave(),
                    onBrowseRequested: HandleMachineBrowseRequestAsync,
                    dialogService: _dialogService,
                    localizationService: _localization);
                row.PropertyChanged += OnMachineRowPropertyChanged;

                rows.Add(row);
                sourceFilters.Add(_localization.Format("Filter_OnlyMachineFormat", m.Name));
            }

            MachineRows = rows;
            SourceFilterItems = sourceFilters;
            SelectedSourceIndex = 0;

            if (machines.Count > 0)
            {
                string namesStr = string.Join(" + ", machines.Select(m => m.Name));
                AppSubtitle = _localization.Format("Header_SubtitleMachines", machines.Count, namesStr);
            }
        }
        finally
        {
            _suppressFilterEvents = false;
        }
    }

    private async Task HandleMachineBrowseRequestAsync(MachineRowViewModel row, string kind)
    {
        if (IsRealtimeActive)
        {
            SetStatusLoc("Status_StopRtBeforeModify", "#F59E0B");
            return;
        }

        if (IsBusy || (LoadLogCommand is AsyncRelayCommand activeLoadCmd && !activeLoadCmd.CanExecute(null)))
        {
            SetStatusLoc("Status_BusyLoadingLogs", "#F59E0B");
            return;
        }

        try
        {
            bool isMes = string.Equals(kind, "MES", StringComparison.OrdinalIgnoreCase);
            string title = _localization.Format(isMes ? "Dlg_SelectMesTitleFormat" : "Dlg_SelectOtpTitleFormat", row.Name);
            string initialDir = Directory.Exists(AppRootDir) ? AppRootDir : Environment.CurrentDirectory;
            string? selectedFile = _dialogService.ShowOpenFileDialog(
                title,
                "Log Files (*.log)|*.log|Text Files (*.txt)|*.txt|All Files (*.*)|*.*",
                initialDir);

            if (string.IsNullOrWhiteSpace(selectedFile))
                return;

            if (isMes)
            {
                _suppressStateSave = true;
                try
                {
                    row.MesLogPath = selectedFile;
                }
                finally
                {
                    _suppressStateSave = false;
                }

                SetStatusLoc("Status_UpdatedMesFileFormat", "#38BDF8", row.Name);
                SaveCurrentUserState();
            }
            else
            {
                string selectedOtpPath = selectedFile;
                bool normalizedFromRotated = false;
                var identity = LogFileFamilyHelper.ParseFileFamilyIdentity(selectedOtpPath);
                if (identity is { RequestedRotationIndex: > 0 } && !string.IsNullOrWhiteSpace(identity.ActiveFilePath))
                {
                    bool activeExists = await Task.Run(() => File.Exists(identity.ActiveFilePath));
                    if (activeExists)
                    {
                        selectedOtpPath = identity.ActiveFilePath;
                        normalizedFromRotated = true;
                    }
                }

                string? parentDir = Path.GetDirectoryName(selectedOtpPath);
                string? autoMesToApply = null;
                if (!string.IsNullOrWhiteSpace(parentDir))
                {
                    string autoMes = Path.Combine(parentDir, "MES_1.log");
                    if (string.IsNullOrWhiteSpace(row.MesLogPath))
                    {
                        autoMesToApply = autoMes;
                    }
                    else
                    {
                        bool reachable = await _cameraImageService.TestHostReachableAsync(autoMes, _tuning.ImageHostTimeoutMs);
                        if (reachable && await Task.Run(() => File.Exists(autoMes)))
                        {
                            autoMesToApply = autoMes;
                        }
                    }
                }

                _suppressStateSave = true;
                try
                {
                    row.LogPath = selectedOtpPath;
                    row.Enabled = true;
                    if (!string.IsNullOrWhiteSpace(autoMesToApply))
                    {
                        row.MesLogPath = autoMesToApply;
                    }
                }
                finally
                {
                    _suppressStateSave = false;
                }

                SaveCurrentUserState();
                if (LoadLogCommand is AsyncRelayCommand asyncLoad)
                {
                    await asyncLoad.ExecuteAsync(null);
                }
                else
                {
                    await LoadLogsAsync();
                }
                if (normalizedFromRotated)
                {
                    SetStatusLoc("Status_NormalizedRotatedFormat", "#38BDF8", Path.GetFileName(selectedOtpPath));
                }
            }
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "HandleMachineBrowseRequest");
            SetStatusLoc("Status_ErrorConfigLogPathFormat", "#EF4444", ex.Message);
        }
    }

    public async Task OnLoadedAsync()
    {
        SetStatusLoc("Status_Ready", "#38BDF8");
        if (!string.IsNullOrWhiteSpace(_settings.LoadError))
        {
            SetStatus($"{_settings.LoadError}", "#F59E0B");
        }

        SyncCameraHealthBadgesToEnabledMachines();

        _backgroundMonitor.StartCameraHealthPolling(
            GetEnabledCameraEndpoints,
            ApplyCameraHealthStatuses);

        await UpdateCameraHealthAsync(force: true);
    }

    public void OnWindowClosing()
    {
        try
        {
            SaveCurrentUserState();
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "OnWindowClosing.SaveUserState");
        }

        StopBackgroundActivities();
    }

    internal IReadOnlyList<CameraEndpointDescriptor> GetEnabledCameraEndpoints()
    {
        var enabledMachines = new HashSet<string>(
            MachineRows.Where(m => m.Enabled).Select(m => m.Name),
            StringComparer.OrdinalIgnoreCase);

        return _cameraImageService.GetCameraEndpoints(_settings)
            .Where(ep => string.IsNullOrWhiteSpace(ep.Machine) || enabledMachines.Contains(ep.Machine))
            .ToList();
    }

    private void SyncCameraHealthBadgesToEnabledMachines()
    {
        var existingByCode = CameraHealthItems.ToDictionary(c => c.Code, StringComparer.OrdinalIgnoreCase);
        var enabledEndpoints = GetEnabledCameraEndpoints();
        var next = enabledEndpoints.Select(ep => existingByCode.TryGetValue(ep.Code, out var prev)
            ? prev
            : new CameraHealthStatus
            {
                Code = ep.Code,
                Machine = ep.Machine,
                Root = ep.Root,
                Host = ep.Host,
                Label = ep.Label,
                Online = false
            }).ToList();

        ApplyCameraHealthStatuses(next);
    }

    private void OnMachineRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MachineRowViewModel.Enabled))
        {
            SyncCameraHealthBadgesToEnabledMachines();
        }
    }

    private void ScheduleUserStateSave()
    {
        if (_suppressStateSave || _disposed) return;

        _hasPendingStateSave = true;
        if (Application.Current?.Dispatcher is null)
        {
            SaveCurrentUserState();
            return;
        }

        if (_stateSaveDebounceTimer is null)
        {
            _stateSaveDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _stateSaveDebounceTimer.Tick += OnStateSaveDebounceTimerTick;
        }

        _stateSaveDebounceTimer.Stop();
        _stateSaveDebounceTimer.Start();
    }

    private void OnStateSaveDebounceTimerTick(object? sender, EventArgs e)
    {
        SaveCurrentUserState();
    }

    public void SaveCurrentUserState()
    {
        _hasPendingStateSave = false;
        _stateSaveDebounceTimer?.Stop();
        if (_suppressStateSave) return;
        try
        {
            var machineStates = MachineRows
                .Select(m => new UserMachineState(m.Name, m.LogPath, m.MesLogPath, m.Enabled))
                .ToList();

            var state = new UserState
            {
                SelectedTab = SelectedTabIndex,
                Machines = machineStates,
                AutoScroll = AutoScroll,
                SoundAlert = SoundAlert,
                Language = _localization.CurrentLanguage
            };

            _settingsService.SaveUserState(AppRootDir, state);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "SaveCurrentUserState");
        }
    }

    public void StopBackgroundActivities()
    {
        try
        {
            _filterDebounceTimer?.Stop();
            _stateSaveDebounceTimer?.Stop();
            _wallboardTimer?.Stop();
            _backgroundMonitor.StopAll();
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "StopBackgroundActivities");
        }
    }

    // --- Log Loading & Real-time Monitoring ---

    private void ShowNotificationIfInteractive(string message, string title, DialogMessageSeverity severity)
    {
        if (IsKioskMode) return;
        if (!_hasCustomDialogService && Application.Current is null) return;

        _dialogService.ShowMessage(message, title, severity);
    }

    public async Task LoadLogsAsync()
    {
        if (IsBusy) return;
        if (_hasPendingStateSave) SaveCurrentUserState();

        var enabledMachines = MachineRows.Where(m => m.Enabled).ToList();
        if (enabledMachines.Count == 0)
        {
            string allNames = string.Join(", ", MachineRows.Select(m => m.Name));
            ShowNotificationIfInteractive(
                _localization.Format("Dlg_EnableOneMachineFormat", allNames),
                _localization.Get("Dlg_NotificationTitle"),
                DialogMessageSeverity.Warning);
            return;
        }

        var (filterSpec, hasTimeWarning) = BuildFilterSpec();
        if (hasTimeWarning)
        {
            SetStatusLoc("Status_InvalidDateRange", "#F59E0B");
            ShowNotificationIfInteractive(
                _localization.Get("Dlg_InvalidDateRangeMsg"),
                _localization.Get("Dlg_InvalidDateRangeTitle"),
                DialogMessageSeverity.Warning);
            return;
        }

        if (IsRealtimeActive)
        {
            ToggleRealtime();
        }

        _backgroundMonitor.StopPendingTailJobs();

        DateTime? loadStartTime = filterSpec.StartTime;
        DateTime? loadEndTime = filterSpec.EndTime;
        _loadedWindowStart = loadStartTime;
        _loadedWindowEnd = loadEndTime;

        _isLoadingLogs = true;
        _pendingRealtimeBatches.Clear();
        IsBusy = true;
        SetStatusLoc(
            loadStartTime.HasValue || loadEndTime.HasValue
                ? "Status_ReadingLogsDateRange"
                : "Status_ReadingLogs",
            "#F59E0B");

        await DisplayState.State.FlushArchiveAsync();
        TryDeleteSessionArchiveFile();
        DisplayState.Clear();
        Spotlight.Clear();
        _lastSpotlightPanelKey = null;
        _lastBaseStatsSig = null;
        _lastRenderedChartNgCount = -1;
        _lastRenderedChartTotalCount = -1;
        _lastRenderedChartScopeSig = null;
        _tailStates.Clear();
        _hadPartialTailStart = false;
        _partialTailStartTime = null;

        var states = new List<LogTailState>(enabledMachines.Count);
        foreach (var m in enabledMachines)
        {
            string p = LogFileFamilyHelper.ResolveActiveLogPath(m.LogPath?.Trim() ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(p))
            {
                var st = _logTailReaderService.CreateTailState(p, m.Name);
                _tailStates[m.Name] = st;
                states.Add(st);
            }
        }

        if (states.Count == 0)
        {
            FileInfoText = string.Empty;
            SetStatusLoc("Status_LoadedZeroRecords", "#10B981");
            ApplyFilter();
            _isLoadingLogs = false;
            IsBusy = false;
            return;
        }

        var warnings = new List<string>();
        string? errMessage = null;

        try
        {
            var results = await _backgroundMonitor.LoadMachinesLogsAsync(
                states,
                startTime: loadStartTime,
                endTime: loadEndTime);

            var validLists = new List<IReadOnlyList<UnitRecord>>(results.Count);
            var fileInfoStrings = new List<string>();

            foreach (var r in results)
            {
                var st = r.State;
                if (r.TimedOut)
                {
                    fileInfoStrings.Add(_localization.Format("Footer_FileTimedOutFormat", st.Source));
                    string detail = !string.IsNullOrWhiteSpace(st.ErrorText) ? $"\n({st.ErrorText})" : string.Empty;
                    warnings.Add(_localization.Format("Dlg_TimedOutReadingLogFormat", st.Source, st.Path, detail));
                    continue;
                }
                if (r.Unreachable || !st.Initialized)
                {
                    fileInfoStrings.Add(_localization.Format("Footer_FileCannotConnectFormat", st.Source));
                    string detail = !string.IsNullOrWhiteSpace(st.ErrorText) ? $"\n({st.ErrorText})" : string.Empty;
                    warnings.Add(_localization.Format("Dlg_CannotConnectLogFormat", st.Source, st.Path, detail));
                    continue;
                }

                if (r.Units.Count > 0)
                {
                    validLists.Add(r.Units);
                }

                string infoText;
                if ((loadStartTime.HasValue || loadEndTime.HasValue) && st.LoadedFilesCount == 0)
                {
                    infoText = _localization.Format("Footer_FileZeroInRangeFormat", st.Source, Path.GetFileName(st.Path));
                }
                else
                {
                    long totalBytes = st.LoadedTotalBytes > 0 ? st.LoadedTotalBytes : st.FileLength;
                    double szMb = Math.Round(totalBytes / (1024.0 * 1024.0), 2);
                    string rotatedSuffix = st.LoadedFilesCount > 1
                        ? _localization.Format("Footer_FileRotatedSuffixFormat", st.LoadedFilesCount - 1)
                        : string.Empty;
                    infoText = $"{st.Source}: {Path.GetFileName(st.Path)}{rotatedSuffix} ({szMb} MB)";
                    if (st.DroppedBlocks > 0)
                    {
                        infoText += _localization.Format("Footer_FileSkippedBlocksSuffixFormat", st.DroppedBlocks);
                    }
                }
                fileInfoStrings.Add(infoText);
            }

            // Bounded K-way chronological merge directly into DisplayState.AllUnits capped at MaxRetainedUnits
            int preEvictedCount = MergeMachineUnitsBounded(validLists, _tuning.MaxRetainedUnits);
            foreach (var r in results)
            {
                if (r.Units is List<UnitRecord> mutable)
                {
                    mutable.Clear();
                }
            }
            if (preEvictedCount > 0)
            {
                DisplayState.State.RecordPreTrimmedEvicted(preEvictedCount);
            }

            FileInfoText = string.Join("  |  ", fileInfoStrings);

            ApplyFilter();

            if (DisplayState.AllUnits.Count > 0)
            {
                var firstTs = DisplayState.AllUnits[0].Timestamp;
                var lastTs = DisplayState.AllUnits[^1].Timestamp;
                if (firstTs.HasValue && StartDate is null)
                {
                    DisplayStartDate = firstTs.Value;
                }
                if (lastTs.HasValue && EndDate is null)
                {
                    DisplayEndDate = lastTs.Value;
                }
            }

            if (loadStartTime.HasValue || loadEndTime.HasValue)
            {
                if (DisplayState.EvictedCount > 0 &&
                    DisplayState.AllUnits.Count > 0 &&
                    DisplayState.AllUnits[0].Timestamp is { } keptStart)
                {
                    SetStatusLoc(
                        "Status_LoadedDateRangeTrimmedFormat",
                        "#F59E0B",
                        DisplayState.ShownUnits.Count,
                        DisplayState.EvictedCount,
                        keptStart);
                }
                else
                {
                    SetStatusLoc("Status_LoadedDateRangeFormat", "#10B981", DisplayState.ShownUnits.Count);
                }
            }
            else if (DisplayState.EvictedCount > 0 &&
                     DisplayState.AllUnits.Count > 0 &&
                     DisplayState.AllUnits[0].Timestamp is { } keptStart)
            {
                SetStatusLoc(
                    "Status_LoadedLatestTrimmedFormat",
                    "#10B981",
                    DisplayState.AllUnits.Count,
                    DisplayState.EvictedCount,
                    keptStart);
            }
            else
            {
                SetStatusLoc("Status_LoadedRecordsFormat", "#10B981", DisplayState.AllUnits.Count);
            }
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "LoadLogsAsync");
            errMessage = _localization.Format("Dlg_ErrorReadingFileFormat", ex.Message);
            SetStatusLoc("Status_FileReadError", "#EF4444");
        }
        finally
        {
            _isLoadingLogs = false;
            IsBusy = false;
        }

        if (!string.IsNullOrEmpty(errMessage))
        {
            ShowNotificationIfInteractive(errMessage, _localization.Get("Dlg_ErrorTitle"), DialogMessageSeverity.Error);
        }
        else if (warnings.Count > 0)
        {
            ShowNotificationIfInteractive(string.Join("\n\n", warnings), _localization.Get("Dlg_ConnWarningTitle"), DialogMessageSeverity.Warning);
        }
    }

    private int MergeMachineUnitsBounded(List<IReadOnlyList<UnitRecord>> rawSources, int maxRetained)
    {
        if (rawSources.Count == 0)
            return 0;

        long totalUnits = 0;
        var sources = new List<IReadOnlyList<UnitRecord>>(rawSources.Count);
        foreach (var raw in rawSources)
        {
            if (raw.Count == 0)
                continue;
            sources.Add(EnsureChronologicalOrder(raw));
            totalUnits += raw.Count;
        }

        if (sources.Count == 0 || totalUnits == 0)
            return 0;

        int limit = maxRetained > 0 ? maxRetained : int.MaxValue;
        int keepCount = (int)Math.Min(totalUnits, (long)limit);
        int evictedCount = (int)Math.Max(0L, totalUnits - keepCount);

        DisplayState.AllUnits.Capacity = Math.Max(DisplayState.AllUnits.Capacity, DisplayState.AllUnits.Count + keepCount);

        if (sources.Count == 1)
        {
            var single = sources[0];
            int startIdx = single.Count - keepCount;
            for (int i = startIdx; i < single.Count; i++)
            {
                var u = single[i];
                u.Index = DisplayState.AllUnits.Count;
                DisplayState.AllUnits.Add(u);
            }
            return evictedCount;
        }

        if (evictedCount == 0)
        {
            int[] cursors = new int[sources.Count];
            for (int emitted = 0; emitted < keepCount; emitted++)
            {
                int bestMachine = -1;
                long bestTicks = long.MaxValue;
                for (int m = 0; m < sources.Count; m++)
                {
                    int idx = cursors[m];
                    if (idx >= sources[m].Count)
                        continue;

                    long ticks = sources[m][idx].Timestamp?.Ticks ?? 0L;
                    if (bestMachine < 0 || ticks < bestTicks)
                    {
                        bestMachine = m;
                        bestTicks = ticks;
                    }
                }

                var chosen = sources[bestMachine][cursors[bestMachine]++];
                chosen.Index = DisplayState.AllUnits.Count;
                DisplayState.AllUnits.Add(chosen);
            }
            return 0;
        }

        int[] tailCursors = new int[sources.Count];
        for (int m = 0; m < sources.Count; m++)
        {
            tailCursors[m] = sources[m].Count - 1;
        }

        var tailBuffer = new UnitRecord[keepCount];
        for (int pos = keepCount - 1; pos >= 0; pos--)
        {
            int bestMachine = -1;
            long bestTicks = long.MinValue;
            for (int m = sources.Count - 1; m >= 0; m--)
            {
                int idx = tailCursors[m];
                if (idx < 0)
                    continue;

                long ticks = sources[m][idx].Timestamp?.Ticks ?? 0L;
                if (bestMachine < 0 || ticks > bestTicks)
                {
                    bestMachine = m;
                    bestTicks = ticks;
                }
            }

            tailBuffer[pos] = sources[bestMachine][tailCursors[bestMachine]--];
        }

        int baseIndex = DisplayState.AllUnits.Count;
        for (int i = 0; i < tailBuffer.Length; i++)
        {
            tailBuffer[i].Index = baseIndex + i;
            DisplayState.AllUnits.Add(tailBuffer[i]);
        }

        return evictedCount;
    }

    private static IReadOnlyList<UnitRecord> EnsureChronologicalOrder(IReadOnlyList<UnitRecord> source)
    {
        if (source.Count <= 1)
            return source;

        long prev = source[0].Timestamp?.Ticks ?? 0L;
        for (int i = 1; i < source.Count; i++)
        {
            long cur = source[i].Timestamp?.Ticks ?? 0L;
            if (cur < prev)
            {
                return source.OrderBy(u => u.Timestamp?.Ticks ?? 0L).ToList();
            }
            prev = cur;
        }

        return source;
    }

    public void ToggleRealtime()
    {
        if (!IsRealtimeActive)
        {
            if (IsBusy)
            {
                SetStatusLoc("Status_WaitFinishBeforeRt", "#F59E0B");
                return;
            }

            if (_hasPendingStateSave) SaveCurrentUserState();

            try
            {
                _backgroundMonitor.StopPendingTailJobs();

                int configured = 0;
                foreach (var m in MachineRows)
                {
                    if (EnsureTailStateForMachine(m) is not null)
                    {
                        configured++;
                    }
                }

                if (configured == 0)
                {
                    ShowNotificationIfInteractive(
                        _localization.Get("Dlg_NoLogsForRealtime"),
                        _localization.Get("Dlg_NotificationTitle"),
                        DialogMessageSeverity.Warning);
                    return;
                }

                _rtHadProblem = false;
                var (dayStart, nightStart) = GetConfiguredShiftBoundaries();
                var curShift = _exportService.GetShiftWindow(
                    DateTime.Now,
                    dayStart.Hours,
                    dayStart.Minutes,
                    nightStart.Hours,
                    nightStart.Minutes);
                _currentShiftKey = curShift.ShiftKey;

                _backgroundMonitor.StartRealtimePolling(
                    statesProvider: GetActiveRealtimeStates,
                    onPollCompleted: CompleteRealtimePoll,
                    onSlowServerWarning: () =>
                    {
                        if (!_rtHadProblem)
                        {
                            SetStatusLoc("Status_WaitingLogServer", "#F59E0B");
                            _rtHadProblem = true;
                        }
                    },
                    onPollError: ex =>
                    {
                        SetStatusLoc("Status_RtErrorFormat", "#EF4444", ex.Message);
                        _rtHadProblem = true;
                    });

                bool expandedPastEndDate = false;
                if (GetSelectedEndDateTime() is { } selectedEnd && selectedEnd < DateTime.Now)
                {
                    _suppressFilterEvents = true;
                    try
                    {
                        EndDate = null;
                        DisplayEndDate = DateTime.Today;
                        SelectedEndHourIndex = 23;
                        SelectedEndMinuteIndex = 59;
                    }
                    finally
                    {
                        _suppressFilterEvents = false;
                    }
                    expandedPastEndDate = true;
                    ApplyFilter();
                }

                if (_loadedWindowEnd.HasValue && _loadedWindowEnd.Value < DateTime.Now)
                {
                    _loadedWindowEnd = DateTime.Now;
                }

                IsRealtimeActive = true;
                RealtimeButtonText = _localization.Get("Filter_StopRealtime");
                RealtimeStatusText = _localization.Get("Footer_RtRunning");
                RealtimeStatusColor = "#10B981";
                SetStatusLoc(
                    expandedPastEndDate
                        ? "Status_MonitoringClearedTo"
                        : "Status_MonitoringParallel",
                    expandedPastEndDate ? "#F59E0B" : "#10B981");
            }
            catch (Exception ex)
            {
                _errorLogger.LogException(ex, "ToggleRealtime.Start");
                ShowNotificationIfInteractive(
                    _localization.Format("Dlg_CannotStartRtFormat", ex.Message),
                    _localization.Get("Dlg_ErrorTitle"),
                    DialogMessageSeverity.Error);
            }
        }
        else
        {
            _backgroundMonitor.StopRealtimePolling();
            IsRealtimeActive = false;
            if (!IsBusy && !_isLoadingLogs)
            {
                FlushPendingRealtimeBatches();
            }
            if (_loadedWindowEnd.HasValue && DisplayState.AllUnits.Count > 0 && DisplayState.AllUnits[^1].Timestamp is { } lastTs && lastTs > _loadedWindowEnd.Value)
            {
                _loadedWindowEnd = lastTs;
            }
            RealtimeButtonText = _localization.Get("Filter_StartRealtime");
            RealtimeStatusText = _localization.Get("Footer_RtStopped");
            RealtimeStatusColor = "#64748B";
            SetStatusLoc("Status_RtStopped", "#F59E0B");
        }
    }

    private LogTailState? EnsureTailStateForMachine(MachineRowViewModel machine)
    {
        string path = LogFileFamilyHelper.ResolveActiveLogPath(machine.LogPath?.Trim() ?? string.Empty);
        if (!machine.Enabled || string.IsNullOrWhiteSpace(path))
        {
            _tailStates[machine.Name] = null;
            return null;
        }

        if (!_tailStates.TryGetValue(machine.Name, out var state) ||
            state is null ||
            !string.Equals(state.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            state = _logTailReaderService.CreateTailState(path, machine.Name);
            _tailStates[machine.Name] = state;
        }

        if (!state.Initialized)
        {
            state.SkipFirst = true;
            _hadPartialTailStart = true;
            _partialTailStartTime ??= DateTime.Now;
        }

        return state;
    }

    private IReadOnlyList<LogTailState> GetActiveRealtimeStates()
    {
        var list = new List<LogTailState>(MachineRows.Count);
        foreach (var m in MachineRows)
        {
            if (EnsureTailStateForMachine(m) is { } st)
            {
                list.Add(st);
            }
        }
        return list;
    }

    private void CompleteRealtimePoll(IReadOnlyList<MachineTailReadResult> results)
    {
        if (_isLoadingLogs || results.Count == 0) return;

        if (IsBusy)
        {
            _pendingRealtimeBatches.Enqueue(results);
            return;
        }

        if (_pendingRealtimeBatches.Count > 0)
        {
            var combined = new List<MachineTailReadResult>();
            while (_pendingRealtimeBatches.Count > 0)
            {
                combined.AddRange(_pendingRealtimeBatches.Dequeue());
            }
            combined.AddRange(results);
            ProcessRealtimeBatch(combined);
            return;
        }

        ProcessRealtimeBatch(results);
    }

    private void FlushPendingRealtimeBatches()
    {
        if (_isLoadingLogs || IsBusy || _pendingRealtimeBatches.Count == 0) return;

        var combined = new List<MachineTailReadResult>();
        while (_pendingRealtimeBatches.Count > 0)
        {
            combined.AddRange(_pendingRealtimeBatches.Dequeue());
        }
        ProcessRealtimeBatch(combined);
    }

    private void ProcessRealtimeBatch(IReadOnlyList<MachineTailReadResult> results)
    {
        bool hasNewUnits = false;
        int machinesWithNewUnits = 0;
        var newUnitsList = new List<UnitRecord>();
        var problems = new List<string>();
        var notes = new List<string>();

        foreach (var r in results)
        {
            var st = r.State;
            if (!_tailStates.TryGetValue(st.Source, out var activeState) || !ReferenceEquals(activeState, st))
                continue;

            if (st.Initialized)
            {
                st.SkipFirst = false;
            }

            if (r.Unreachable || !string.IsNullOrEmpty(st.ErrorText))
            {
                problems.Add($"{st.Source}: {st.ErrorText}");
                continue;
            }

            if (r.DroppedNew > 0)
            {
                notes.Add($"{st.Source}: skipped {r.DroppedNew} blocks missing END");
            }

            if (r.Units.Count > 0)
            {
                machinesWithNewUnits++;
                newUnitsList.AddRange(r.Units);
                hasNewUnits = true;
            }
        }

        if (hasNewUnits)
        {
            if (machinesWithNewUnits > 1 && newUnitsList.Count > 1)
            {
                newUnitsList = newUnitsList.OrderBy(u => u.Timestamp?.Ticks ?? 0L).ToList();
            }

            DateTime? latestLiveTs = null;
            foreach (var unit in newUnitsList)
            {
                unit.Index = DisplayState.AllUnits.Count;
                DisplayState.AllUnits.Add(unit);
                if (unit.Timestamp is { } ts && (!latestLiveTs.HasValue || ts > latestLiveTs.Value))
                {
                    latestLiveTs = ts;
                }
            }

            if (latestLiveTs.HasValue)
            {
                if (_loadedWindowEnd.HasValue && latestLiveTs.Value > _loadedWindowEnd.Value)
                {
                    _loadedWindowEnd = latestLiveTs.Value;
                }
                if (EndDate is null && latestLiveTs.Value > DisplayEndDate)
                {
                    DisplayEndDate = latestLiveTs.Value;
                }
            }

            ApplyFilter();

            int consecThreshold = _tuning.ConsecutiveNgThreshold;
            UnitRecord? latestNgUnit = null;
            foreach (var unit in newUnitsList)
            {
                if (unit.IsNg)
                {
                    latestNgUnit = unit;
                    int streak = DisplayState.GetUnitConsecutiveNg(unit);
                    if (streak <= 0) streak = 1;
                    IsAlertVisible = true;
                    string nowTime = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                    string errStr = string.Join(", ", unit.Errors);

                    if (streak >= consecThreshold)
                    {
                        AlertBackground = "#2F1218";
                        AlertBorderBrush = "#EF4444";
                        AlertTitle = _localization.Format("Alert_ConsecNgTitleFormat", streak);
                        AlertTitleColor = "#F59E0B";
                        AlertMessage = _localization.Format("Alert_ConsecNgMsgFormat", nowTime, unit.Source, unit.Pmnt, streak, unit.PanelName, errStr);
                        if (SoundAlert)
                        {
                            try { SystemSounds.Exclamation.Play(); } catch { }
                        }
                    }
                    else
                    {
                        AlertBackground = "#241218";
                        AlertBorderBrush = "#EF4444";
                        AlertTitle = _localization.Get("Alert_NewDefectTitle");
                        AlertTitleColor = "#F87171";
                        AlertMessage = _localization.Format("Alert_NewDefectMsgFormat", nowTime, unit.Source, unit.Pmnt, unit.PanelName, errStr);
                        if (SoundAlert)
                        {
                            try { SystemSounds.Beep.Play(); } catch { }
                        }
                    }
                }
            }

            if (latestNgUnit is not null)
            {
                int latestStreak = DisplayState.GetUnitConsecutiveNg(latestNgUnit);
                if (latestStreak <= 0 && latestNgUnit.IsNg) latestStreak = 1;
                _ = TriggerSpotlightForUnitAsync(latestNgUnit, latestStreak);
            }
        }

        if (IsRealtimeActive || hasNewUnits)
        {
            _ = InvokeAutoShiftExportCheckAsync();
            _ = UpdateCameraHealthAsync(force: false);
        }

        if (IsRealtimeActive)
        {
            bool pastEndDateFilter = hasNewUnits && GetSelectedEndDateTime() is { } activeEnd && activeEnd < DateTime.Now;
            if (problems.Count > 0)
            {
                SetStatusLoc("Status_ConnLostRetryingFormat", "#EF4444", string.Join(" | ", problems));
                _rtHadProblem = true;
            }
            else if (notes.Count > 0)
            {
                SetStatus($"{string.Join(" | ", notes)}", "#F59E0B");
                _rtHadProblem = true;
            }
            else if (pastEndDateFilter)
            {
                SetStatusLoc("Status_LiveFilteredPastTo", "#F59E0B");
                _rtHadProblem = true;
            }
            else if (_rtHadProblem)
            {
                SetStatusLoc("Status_MonitoringParallel", "#10B981");
                _rtHadProblem = false;
            }
        }
    }

    // --- Shift Auto-Export & Manual Exports ---

    private static bool TryParseShiftTime(string? text, out TimeSpan parsed)
    {
        if (!string.IsNullOrWhiteSpace(text) &&
            TimeSpan.TryParse(text.Trim(), CultureInfo.InvariantCulture, out parsed) &&
            parsed >= TimeSpan.Zero &&
            parsed.Days == 0)
        {
            parsed = new TimeSpan(parsed.Hours, parsed.Minutes, 0);
            return true;
        }

        parsed = default;
        return false;
    }

    internal (TimeSpan DayStart, TimeSpan NightStart) GetConfiguredShiftBoundaries()
    {
        var config = _settings.AutoShiftExport;
        var defaultDayStart = new TimeSpan(_tuning.DayShiftStartHour, 0, 0);
        var defaultNightStart = new TimeSpan(_tuning.NightShiftStartHour, 0, 0);
        if (defaultDayStart >= defaultNightStart)
        {
            defaultDayStart = new TimeSpan(8, 0, 0);
            defaultNightStart = new TimeSpan(20, 0, 0);
        }

        TimeSpan dayStart;
        TimeSpan nightStart;

        bool hasExplicitStart =
            !string.IsNullOrWhiteSpace(config.DayShiftStart) ||
            !string.IsNullOrWhiteSpace(config.NightShiftStart);
        if (hasExplicitStart &&
            TryParseShiftTime(config.DayShiftStart, out var explicitDay) &&
            TryParseShiftTime(config.NightShiftStart, out var explicitNight))
        {
            dayStart = explicitDay;
            nightStart = explicitNight;
        }
        else
        {
            dayStart = TryParseShiftTime(config.NightShiftTime, out var parsedDayStart)
                ? parsedDayStart
                : defaultDayStart;
            nightStart = TryParseShiftTime(config.DayShiftTime, out var parsedNightStart)
                ? parsedNightStart
                : defaultNightStart;
        }

        if (dayStart >= nightStart)
        {
            _errorLogger.LogDiagnostic(
                $"Invalid shift boundaries (dayStart={dayStart:hh\\:mm}, nightStart={nightStart:hh\\:mm}); using fallback {defaultDayStart:hh\\:mm}-{defaultNightStart:hh\\:mm}.");
            return (defaultDayStart, defaultNightStart);
        }

        return (dayStart, nightStart);
    }

    public async Task InvokeAutoShiftExportCheckAsync(DateTime? simulatedNow = null)
    {
        var config = _settings.AutoShiftExport;
        if (!config.Enabled)
            return;

        if (Interlocked.CompareExchange(ref _autoExportInFlight, 1, 0) != 0)
            return;

        try
        {
            var now = simulatedNow ?? DateTime.Now;
            var (dayStart, nightStart) = GetConfiguredShiftBoundaries();

            // 1. Determine if current time falls within handover window for Day or Night shift
            ShiftWindow? targetShift = null;

            var dayCutoff = now.Date + nightStart;
            if (now >= dayCutoff && now < dayCutoff.AddMinutes(30))
            {
                // Completed shift is today's DAY shift
                string shiftDate = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                string shiftKey = $"{shiftDate}_DAY";
                DateTime start = now.Date + dayStart;
                DateTime end = (now.Date + nightStart).AddTicks(-1);
                targetShift = new ShiftWindow("DAY", shiftDate, shiftKey, start, end);
            }
            else
            {
                var nightCutoff = now.Date + dayStart;
                if (now >= nightCutoff && now < nightCutoff.AddMinutes(30))
                {
                    // Completed shift is yesterday's NIGHT shift
                    var yesterday = now.Date.AddDays(-1);
                    string shiftDate = yesterday.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                    string shiftKey = $"{shiftDate}_NIGHT";
                    DateTime start = yesterday + nightStart;
                    DateTime end = (now.Date + dayStart).AddTicks(-1);
                    targetShift = new ShiftWindow("NIGHT", shiftDate, shiftKey, start, end);
                }
            }

            // Fallback: check shift change transition if not triggered by exact cutoff window
            var curShift = _exportService.GetShiftWindow(
                now,
                dayStart.Hours,
                dayStart.Minutes,
                nightStart.Hours,
                nightStart.Minutes);
            if (string.IsNullOrEmpty(_currentShiftKey))
            {
                _currentShiftKey = curShift.ShiftKey;
            }
            else if (curShift.ShiftKey != _currentShiftKey)
            {
                var prevShift = _exportService.GetShiftWindow(
                    now,
                    dayStart.Hours,
                    dayStart.Minutes,
                    nightStart.Hours,
                    nightStart.Minutes,
                    completedShift: true);
                _currentShiftKey = curShift.ShiftKey;
                targetShift ??= prevShift;
            }

            if (targetShift is null)
                return;

            // Anti-duplicate lock: verify shift hasn't already been exported
            if (string.Equals(_lastAutoExportedShiftKey, targetShift.ShiftKey, StringComparison.OrdinalIgnoreCase))
                return;

            var enabledMachines = MachineRows
                .Where(m => m.Enabled && !string.IsNullOrWhiteSpace(m.LogPath))
                .Select(m => (m.Name, LogPath: m.LogPath!))
                .ToArray();

            var ramShiftUnits = DisplayState.AllUnits
                .Where(u => u.Timestamp is { } ts && ts >= targetShift.StartTime && ts <= targetShift.EndTime)
                .ToList();

            bool ramMissingHead =
                DisplayState.EvictedCount > 0 ||
                (_loadedWindowStart.HasValue && _loadedWindowStart.Value > targetShift.StartTime) ||
                (_hadPartialTailStart && _partialTailStartTime is { } pst && pst > targetShift.StartTime);

            UnitRecord[] shiftUnits;
            if (ramMissingHead && enabledMachines.Length > 0)
            {
                var diskResults = await _backgroundMonitor.ReadShiftBackfillAsync(
                    enabledMachines,
                    targetShift.StartTime,
                    targetShift.EndTime);

                var diskBySource = new Dictionary<string, IReadOnlyList<UnitRecord>>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in diskResults)
                {
                    if (!r.Unreachable && r.State.Initialized && string.IsNullOrEmpty(r.State.ErrorText))
                    {
                        diskBySource[r.State.Source] = r.Units;
                    }
                }

                if (diskBySource.Count > 0)
                {
                    var merged = new List<UnitRecord>();
                    foreach (var list in diskBySource.Values)
                    {
                        merged.AddRange(list);
                    }
                    foreach (var u in ramShiftUnits)
                    {
                        if (!diskBySource.ContainsKey(u.Source))
                        {
                            merged.Add(u);
                        }
                    }

                    shiftUnits = merged
                        .Where(u => u.Timestamp is { } ts && ts >= targetShift.StartTime && ts <= targetShift.EndTime)
                        .OrderBy(u => u.Timestamp?.Ticks ?? 0L)
                        .ToArray();
                }
                else
                {
                    shiftUnits = ramShiftUnits.ToArray();
                }
            }
            else
            {
                shiftUnits = ramShiftUnits.ToArray();
            }

            if (shiftUnits.Length == 0)
                return;

            string exportDir = Path.Combine(AppRootDir, "exports");
            Directory.CreateDirectory(exportDir);

            // 1. Export ShiftReport_{yyyyMMdd}_{DAY|NIGHT}.csv
            string csvPath = Path.Combine(exportDir, $"ShiftReport_{targetShift.ShiftKey}.csv");
            await _exportService.ExportShiftSummaryReportCsvAsync(shiftUnits, csvPath, targetShift);

            // 2. Export ShiftChart_{yyyyMMdd}_{DAY|NIGHT}.png (independent offscreen render when UI canvas is hidden/unloaded)
            string pngPath = Path.Combine(exportDir, $"ShiftChart_{targetShift.ShiftKey}.png");
            await Chart.ExportOffscreenShiftChartAsync(pngPath, shiftUnits);

            // 3. Export detailed shift summary & detail reports
            await _exportService.ExportShiftReportAsync(
                shiftUnits,
                exportDir,
                targetShift.StartTime,
                targetShift.EndTime,
                targetShift.ShiftKey,
                _tuning.ConsecutiveNgThreshold);

            _lastAutoExportedShiftKey = targetShift.ShiftKey;
            SetStatusLoc("Status_AutoExportedShiftFormat", "#10B981", targetShift.ShiftKey);
            _errorLogger.LogDiagnostic($"Auto shift export completed for {targetShift.ShiftKey}: CSV='{csvPath}', PNG='{pngPath}'");
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "InvokeAutoShiftExportCheckAsync");
        }
        finally
        {
            Interlocked.Exchange(ref _autoExportInFlight, 0);
        }
    }

    public async Task ExportCsvAsync()
    {
        if (DisplayState.ShownUnits.Count == 0)
        {
            SetStatusLoc("Status_NoDataExportCsv", "#F59E0B");
            ShowNotificationIfInteractive(
                _localization.Get("Status_NoDataExportCsv"),
                _localization.Get("Dlg_NotificationTitle"),
                DialogMessageSeverity.Information);
            return;
        }

        string initialDir = Path.Combine(AppRootDir, "exports");
        if (!Directory.Exists(initialDir)) initialDir = AppRootDir;

        string? savePath = _dialogService.ShowSaveFileDialog(
            _localization.Get("Dlg_ExportCsvDialogTitle"),
            "CSV Files (*.csv)|*.csv",
            ".csv",
            $"REPORT_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
            initialDir);

        if (!string.IsNullOrWhiteSpace(savePath))
        {
            try
            {
                var shownSnapshot = DisplayState.ShownUnits.ToArray();
                await _exportService.ExportUnitDataAsync(
                    shownSnapshot,
                    savePath,
                    append: false,
                    includeMachine: true);

                SetStatusLoc("Status_ExportedCsvFormat", "#10B981", Path.GetFileName(savePath));
                ShowNotificationIfInteractive(
                    _localization.Format("Dlg_CsvExportedMsgFormat", savePath),
                    _localization.Get("Dlg_SuccessTitle"),
                    DialogMessageSeverity.Information);
            }
            catch (Exception ex)
            {
                _errorLogger.LogException(ex, "ExportCsvAsync");
                SetStatusLoc("Status_ExportCsvErrorFormat", "#EF4444", ex.Message);
                ShowNotificationIfInteractive(
                    _localization.Format("Status_ExportCsvErrorFormat", ex.Message),
                    _localization.Get("Dlg_ErrorTitle"),
                    DialogMessageSeverity.Error);
            }
        }
    }

    public async Task ExportShiftReportAsync()
    {
        if (DisplayState.AllUnits.Count == 0)
        {
            SetStatusLoc("Status_NoDataExportShift", "#F59E0B");
            ShowNotificationIfInteractive(
                _localization.Get("Status_NoDataExportShift"),
                _localization.Get("Dlg_NotificationTitle"),
                DialogMessageSeverity.Information);
            return;
        }

        try
        {
            string exportDir = Path.Combine(AppRootDir, "exports");
            var scopeSnapshot = DisplayState.ScopeUnits.ToArray();
            var refUnit = scopeSnapshot.Length > 0
                ? scopeSnapshot[^1]
                : DisplayState.AllUnits[^1];
            var refTime = refUnit.Timestamp ?? DateTime.Now;
            var (dayStart, nightStart) = GetConfiguredShiftBoundaries();
            var shiftWin = _exportService.GetShiftWindow(
                refTime,
                dayStart.Hours,
                dayStart.Minutes,
                nightStart.Hours,
                nightStart.Minutes);

            var rep = await _exportService.ExportShiftReportAsync(
                scopeSnapshot,
                exportDir,
                shiftWin.StartTime,
                shiftWin.EndTime,
                shiftWin.ShiftKey,
                _tuning.ConsecutiveNgThreshold);

            SetStatusLoc("Status_ExportedShiftFormat", "#38BDF8", rep.ShiftKey, rep.UnitCount);

            string detailNote = !string.IsNullOrEmpty(rep.DetailPath)
                ? _localization.Format("Dlg_ShiftDetailLineFormat", rep.DetailPath)
                : string.Empty;
            ShowNotificationIfInteractive(
                _localization.Format("Dlg_ShiftExportedMsgFormat", rep.ShiftKey, rep.UnitCount, rep.Stats.OkCount, rep.Stats.NgCount, rep.Stats.NgRate, rep.SummaryPath, detailNote),
                _localization.Get("Dlg_ShiftExportedTitle"),
                DialogMessageSeverity.Information);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "ExportShiftReportAsync");
            SetStatusLoc("Status_ExportShiftErrorFormat", "#EF4444", ex.Message);
            ShowNotificationIfInteractive(
                _localization.Format("Status_ExportShiftErrorFormat", ex.Message),
                _localization.Get("Dlg_ErrorTitle"),
                DialogMessageSeverity.Error);
        }
    }

    // --- Camera Health ---

    public async Task UpdateCameraHealthAsync(bool force = false)
    {
        if (_cameraHealthBusy) return;
        var nowUtc = DateTime.UtcNow;
        double elapsedMs = (nowUtc - _lastCameraHealthCheckUtc).TotalMilliseconds;
        if (!force && elapsedMs < _tuning.CameraHealthPollMs) return;

        var endpoints = GetEnabledCameraEndpoints();
        if (endpoints.Count == 0)
        {
            ApplyCameraHealthStatuses([]);
            return;
        }

        _cameraHealthBusy = true;
        _lastCameraHealthCheckUtc = nowUtc;

        try
        {
            var statuses = await _backgroundMonitor.CheckCameraHealthAsync(endpoints, _tuning.CameraHealthTimeoutMs);
            if (statuses.Count > 0)
            {
                ApplyCameraHealthStatuses(statuses);
            }
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "UpdateCameraHealthAsync");
        }
        finally
        {
            _cameraHealthBusy = false;
        }
    }

    private void ApplyCameraHealthStatuses(IReadOnlyList<CameraHealthStatus> statuses)
    {
        if (AreCameraHealthListsEqual(CameraHealthItems, statuses))
        {
            return;
        }

        CameraHealthItems = statuses.Count == 0
            ? []
            : new ObservableCollection<CameraHealthStatus>(statuses);
    }

    private static bool AreCameraHealthListsEqual(
        ObservableCollection<CameraHealthStatus> current,
        IReadOnlyList<CameraHealthStatus> next)
    {
        if (current.Count != next.Count) return false;
        for (int i = 0; i < current.Count; i++)
        {
            if (!Equals(current[i], next[i]))
            {
                return false;
            }
        }
        return true;
    }
}

