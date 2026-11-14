using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

namespace OTPSoftware.App.Services;

/// <summary>
/// Result of reading or tail-polling a single machine's OTP_1.log in the background.
/// </summary>
public sealed record MachineTailReadResult(
    LogTailState State,
    IReadOnlyList<UnitRecord> Units,
    int DroppedNew,
    bool Unreachable,
    string? ErrorMessage = null,
    bool TimedOut = false
);

/// <summary>
/// Target specification for searching optical camera inspection folders.
/// </summary>
public sealed record PanelImageSearchTarget(
    string Path,
    string Side,
    string Code
);

/// <summary>
/// Result of searching a single camera share for a panel's inspection image directory.
/// </summary>
public sealed record PanelImageSearchResult(
    string Side,
    string Root,
    bool Offline,
    string? DirectoryPath,
    bool IsFallback,
    string? Error = null
);

/// <summary>
/// Result of searching MES_1.log candidate files for a panel's detailed optical parameters.
/// </summary>
public sealed record MesDetailSearchResult(
    string MesPath,
    bool FoundFile,
    bool Unreachable,
    IReadOnlyList<MesPanelDetailRecord> Details,
    string? Error = null
);

/// <summary>
/// Manages asynchronous SMB file I/O, PeriodicTimer real-time log tail polling, camera health probing,
/// and image/MES lookups without blocking the WPF STA UI Dispatcher thread.
/// </summary>
public sealed partial class BackgroundMonitorService : IDisposable
{
    private readonly ILogTailReaderService _tailReader;
    private readonly ICameraImageService _cameraImageService;
    private readonly IMesDetailService _mesDetailService;
    private readonly ErrorLoggerService _errorLogger;
    private readonly TuningOptions _tuning;

    private const int WatcherRetryCooldownSec = 30;
    private const int WatcherErrorRetryCooldownSec = 5;

    private readonly Dictionary<string, FileSystemWatcher> _watchersByKey = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _watcherRetryAfterUtc = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _watcherDebounceCts;
    private readonly Lock _watcherLock = new();
    private readonly Lock _realtimeLifecycleLock = new();
    private long _realtimeSessionId;
    private Func<Task>? _triggerPollAsync;

    private CancellationTokenSource? _tailJobCts;
    private CancellationTokenSource? _realtimeLoopCts;
    private PeriodicTimer? _realtimeTimer;
    private Task? _realtimeLoopTask;

    private CancellationTokenSource? _cameraLoopCts;
    private PeriodicTimer? _cameraTimer;
    private Task? _cameraLoopTask;

    private bool _disposed;

    public int ActiveLogWatchersCount
    {
        get
        {
            lock (_watcherLock)
            {
                return _watchersByKey.Count;
            }
        }
    }

    [GeneratedRegex(@"(?<=\.log|\.txt|\.json)\s+(?=[A-Za-z]:|\\\\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex JoinedPathSplitRegex();

    public BackgroundMonitorService(
        ILogTailReaderService? tailReader = null,
        ICameraImageService? cameraImageService = null,
        IMesDetailService? mesDetailService = null,
        ErrorLoggerService? errorLogger = null,
        TuningOptions? tuning = null)
    {
        _tailReader = tailReader ?? new LogTailReaderService();
        _cameraImageService = cameraImageService ?? new CameraImageService();
        _mesDetailService = mesDetailService ?? new MesDetailService();
        _errorLogger = errorLogger ?? ErrorLoggerService.Default;
        _tuning = tuning ?? new TuningOptions();
    }

    public int RealtimePollMs => _tuning.RealtimePollMs;

    public int CameraHealthPollMs => _tuning.CameraHealthPollMs;

    public bool IsCameraHealthPolling => _cameraLoopCts is { IsCancellationRequested: false };

    /// <summary>
    /// Marshals an action onto the WPF UI Dispatcher thread using Application.Current.Dispatcher.InvokeAsync,
    /// or executes inline when running in a headless test environment without an active WPF Application.
    /// </summary>
    public static async Task InvokeOnUiThreadAsync(Action action, DispatcherPriority priority = DispatcherPriority.Background)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.HasShutdownFinished)
        {
            if (dispatcher.CheckAccess())
            {
                action();
            }
            else
            {
                try
                {
                    await dispatcher.InvokeAsync(action, priority).Task.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Dispatcher shutting down or shutting down before queued callback ran
                }
            }
        }
        else
        {
            action();
        }
    }

    /// <summary>
    /// Performs initial/full log loading across all enabled machines in parallel on background threads.
    /// </summary>
    public async Task<IReadOnlyList<MachineTailReadResult>> LoadMachinesLogsAsync(
        IEnumerable<LogTailState> states,
        int timeoutSec = 30,
        DateTime? startTime = null,
        DateTime? endTime = null,
        CancellationToken cancellationToken = default)
    {
        using var newCts = CreateLinkedCtsSafe(cancellationToken);
        var previousCts = Interlocked.Exchange(ref _tailJobCts, newCts);
        CancelCtsOnly(ref previousCts);
        var linkedToken = newCts.Token;

        try
        {
            var stateList = states?.Where(s => s is not null).ToList() ?? [];
            if (stateList.Count == 0)
                return [];

            var tasks = stateList.Select(st => ExecuteSingleMachineReadAsync(st, isFullLoad: true, timeoutSec, startTime, endTime, linkedToken));
            return await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.CompareExchange(ref _tailJobCts, null, newCts);
        }
    }

    /// <summary>
    /// Reads historical shift records from disk using isolated temporary <see cref="LogTailState"/> objects
    /// so live real-time tail offsets remain undisturbed.
    /// </summary>
    public async Task<IReadOnlyList<MachineTailReadResult>> ReadShiftBackfillAsync(
        IEnumerable<(string Source, string LogPath)> machines,
        DateTime shiftStart,
        DateTime shiftEnd,
        int timeoutSec = 30,
        CancellationToken cancellationToken = default)
    {
        var tempStates = new List<LogTailState>();
        if (machines is not null)
        {
            foreach (var (source, logPath) in machines)
            {
                string resolved = LogFileFamilyHelper.ResolveActiveLogPath(logPath?.Trim() ?? string.Empty);
                if (string.IsNullOrWhiteSpace(resolved))
                    continue;

                tempStates.Add(_tailReader.CreateTailState(resolved, source));
            }
        }

        if (tempStates.Count == 0)
            return [];

        var tasks = tempStates.Select(st => ExecuteSingleMachineReadAsync(
            st,
            isFullLoad: true,
            timeoutSec: timeoutSec,
            startTime: shiftStart,
            endTime: shiftEnd,
            cancellationToken: cancellationToken));

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Performs incremental tail polling across all active machines in parallel on background threads.
    /// </summary>
    public async Task<IReadOnlyList<MachineTailReadResult>> PollMachinesTailAsync(
        IEnumerable<LogTailState> states,
        int timeoutSec = 30,
        CancellationToken cancellationToken = default)
    {
        var stateList = states?.Where(s => s is not null).ToList() ?? [];
        if (stateList.Count == 0)
            return [];

        using var pollCts = CreateLinkedCtsSafe(cancellationToken);
        var tasks = stateList.Select(st => ExecuteSingleMachineReadAsync(st, isFullLoad: false, timeoutSec, startTime: null, endTime: null, pollCts.Token));
        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task<MachineTailReadResult> ExecuteSingleMachineReadAsync(
        LogTailState state,
        bool isFullLoad,
        int timeoutSec,
        DateTime? startTime,
        DateTime? endTime,
        CancellationToken cancellationToken)
    {
        CancellationTokenSource? timeoutCts = null;
        try
        {
            timeoutCts = CreateLinkedCtsSafe(cancellationToken);
            if (timeoutSec > 0)
            {
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
            }

            var token = timeoutCts.Token;
            Action? onProgress = isFullLoad && timeoutSec > 0
                ? () =>
                {
                    try
                    {
                        if (!cancellationToken.IsCancellationRequested)
                        {
                            timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                    }
                }
                : null;

            return await Task.Run(async () =>
            {
                token.ThrowIfCancellationRequested();

                bool reachable = true;
                if (state.Path.StartsWith(@"\\", StringComparison.Ordinal) && (!state.Initialized || !string.IsNullOrEmpty(state.ErrorText)))
                {
                    reachable = await _cameraImageService
                        .TestHostReachableAsync(state.Path, _tuning.LogHostTimeoutMs, token)
                        .ConfigureAwait(false);
                }

                if (!reachable)
                {
                    state.ErrorText = "Cannot connect to server";
                    return new MachineTailReadResult(state, [], 0, Unreachable: true, state.ErrorText);
                }

                var units = new List<UnitRecord>();
                int droppedBefore = state.DroppedBlocks;

                if ((isFullLoad || !state.Initialized) && !state.SkipFirst)
                {
                    _tailReader.ReadLogUnitsFromFile(
                        state,
                        units,
                        cancellationToken: token,
                        startTime: startTime,
                        endTime: endTime,
                        onProgress: onProgress);
                }
                else
                {
                    bool skipExisting = state.SkipFirst && !state.Initialized;
                    _tailReader.ReadLogTailUnits(state, units, skipExisting: skipExisting, maxBytes: 4 * 1024 * 1024, cancellationToken: token);
                }

                int droppedNew = state.DroppedBlocks - droppedBefore;
                bool unreachable = !string.IsNullOrEmpty(state.ErrorText) && !state.Initialized;
                return new MachineTailReadResult(state, units, droppedNew, unreachable, state.ErrorText);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && timeoutSec > 0)
        {
            string msg = isFullLoad
                ? $"Read timed out after {timeoutSec}s without progress"
                : $"Operation timed out ({timeoutSec}s)";
            state.ErrorText = msg;
            _errorLogger.LogDiagnostic($"Tail read timed out for {state.Source} ({state.Path}) after {timeoutSec}s.");
            return new MachineTailReadResult(state, [], 0, Unreachable: false, ErrorMessage: msg, TimedOut: true);
        }
        catch (OperationCanceledException)
        {
            return new MachineTailReadResult(state, [], 0, Unreachable: false, state.ErrorText);
        }
        catch (Exception ex)
        {
            state.ErrorText = ex.Message;
            _errorLogger.LogException(ex, $"TailRead error for {state.Source} ({state.Path})");
            return new MachineTailReadResult(state, [], 0, Unreachable: true, ex.Message);
        }
        finally
        {
            timeoutCts?.Dispose();
        }
    }

    /// <summary>
    /// Starts a PeriodicTimer loop (default 2000ms) that polls machine log tails in the background
    /// and marshals completion callbacks onto the UI thread via Application.Current.Dispatcher.InvokeAsync.
    /// </summary>
    public void StartRealtimePolling(
        Func<IReadOnlyList<LogTailState>> statesProvider,
        Action<IReadOnlyList<MachineTailReadResult>> onPollCompleted,
        Action? onSlowServerWarning = null,
        Action<Exception>? onPollError = null)
    {
        lock (_realtimeLifecycleLock)
        {
            if (_disposed)
            {
                return;
            }

            Task? priorLoopTask = StopRealtimePollingLocked();
            long sessionId = Interlocked.Increment(ref _realtimeSessionId);

            var cts = new CancellationTokenSource();
            var ct = cts.Token;
            int intervalMs = Math.Max(100, _tuning.RealtimePollMs);
            var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            _realtimeLoopCts = cts;
            _realtimeTimer = timer;

            int pollBusyFlag = 0;
            DateTime pollStartedUtc = DateTime.UtcNow;

            bool IsSessionActive() => !ct.IsCancellationRequested && Volatile.Read(ref _realtimeSessionId) == sessionId;

            async Task ExecutePollCycleAsync()
            {
                if (!IsSessionActive()) return;

                if (Interlocked.CompareExchange(ref pollBusyFlag, 1, 0) != 0)
                {
                    double waitedSec = (DateTime.UtcNow - pollStartedUtc).TotalSeconds;
                    if (waitedSec > _tuning.SlowServerWarnSec && onSlowServerWarning is not null && IsSessionActive())
                    {
                        await InvokeOnUiThreadAsync(() =>
                        {
                            if (IsSessionActive())
                            {
                                onSlowServerWarning();
                            }
                        }).ConfigureAwait(false);
                    }
                    return;
                }

                pollStartedUtc = DateTime.UtcNow;
                try
                {
                    IReadOnlyList<LogTailState> states = [];
                    await InvokeOnUiThreadAsync(() =>
                    {
                        if (IsSessionActive())
                        {
                            states = statesProvider();
                        }
                    }).ConfigureAwait(false);

                    if (states.Count == 0 || !IsSessionActive())
                        return;

                    await SetupHybridWatchersAsync(states, ct).ConfigureAwait(false);
                    if (!IsSessionActive())
                        return;

                    var results = await PollMachinesTailAsync(states, timeoutSec: 30, ct).ConfigureAwait(false);
                    if (!IsSessionActive())
                        return;

                    await InvokeOnUiThreadAsync(() =>
                    {
                        if (IsSessionActive())
                        {
                            onPollCompleted(results);
                        }
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when stopping real-time mode
                }
                catch (Exception ex)
                {
                    _errorLogger.LogException(ex, "Realtime poll cycle failed");
                    if (onPollError is not null && IsSessionActive())
                    {
                        await InvokeOnUiThreadAsync(() =>
                        {
                            if (IsSessionActive())
                            {
                                onPollError(ex);
                            }
                        }).ConfigureAwait(false);
                    }
                }
                finally
                {
                    Interlocked.Exchange(ref pollBusyFlag, 0);
                }
            }

            _triggerPollAsync = ExecutePollCycleAsync;

            try
            {
                var initialStates = statesProvider();
                SetupHybridWatchers(initialStates, allowRemoteOrUnc: false);
            }
            catch (Exception ex)
            {
                _errorLogger.LogDiagnostic($"Initial hybrid watcher setup failed: {ex.Message}");
            }

            _realtimeLoopTask = Task.Run(async () =>
            {
                try
                {
                    if (priorLoopTask is not null && !priorLoopTask.IsCompleted)
                    {
                        try
                        {
                            await priorLoopTask.WaitAsync(ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (priorLoopTask.IsCanceled || ct.IsCancellationRequested)
                        {
                            // Expected when stopping previous loop or restarting rapidly
                        }
                        catch (Exception ex)
                        {
                            _errorLogger.LogDiagnostic($"Previous realtime polling loop terminated with fault: {ex.Message}");
                        }
                    }

                    if (!IsSessionActive()) return;
                    await ExecutePollCycleAsync().ConfigureAwait(false);

                    while (IsSessionActive() && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                    {
                        if (!IsSessionActive()) break;
                        await ExecutePollCycleAsync().ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // Clean shutdown
                }
                catch (Exception ex)
                {
                    _errorLogger.LogException(ex, "Realtime PeriodicTimer loop faulted");
                }
                finally
                {
                    Interlocked.CompareExchange(ref _realtimeTimer, null, timer);
                    Interlocked.CompareExchange(ref _realtimeLoopCts, null, cts);
                    timer.Dispose();
                    cts.Dispose();
                }
            });
        }
    }

    private static (string Directory, string Filter)? ResolveWatcherTarget(string? statePath)
    {
        var identity = LogFileFamilyHelper.ParseFileFamilyIdentity(statePath);
        if (identity is null || string.IsNullOrWhiteSpace(identity.Directory))
            return null;

        return (identity.Directory, identity.WatcherFilter);
    }

    private async Task SetupHybridWatchersAsync(IReadOnlyList<LogTailState> states, CancellationToken cancellationToken)
    {
        if (states == null || states.Count == 0)
            return;

        var pending = new Dictionary<string, (string Directory, string Filter, string ProbePath)>(StringComparer.OrdinalIgnoreCase);
        DateTime nowUtc = DateTime.UtcNow;

        lock (_watcherLock)
        {
            if (_realtimeLoopCts is not { IsCancellationRequested: false })
                return;

            foreach (var state in states)
            {
                var target = ResolveWatcherTarget(state?.Path);
                if (target is not var (dir, filter))
                    continue;

                string key = $"{dir}|{filter}";
                if (_watchersByKey.ContainsKey(key) || pending.ContainsKey(key))
                    continue;

                if (_watcherRetryAfterUtc.TryGetValue(key, out var retryAfter) && nowUtc < retryAfter)
                    continue;

                pending[key] = (dir, filter, state!.Path);
            }
        }

        if (pending.Count == 0)
            return;

        var probeTasks = pending.Select(async kv =>
        {
            string key = kv.Key;
            var (dir, filter, probePath) = kv.Value;
            try
            {
                if (dir.StartsWith(@"\\", StringComparison.Ordinal))
                {
                    bool reachable = await _cameraImageService
                        .TestHostReachableAsync(probePath, _tuning.LogHostTimeoutMs, cancellationToken)
                        .ConfigureAwait(false);
                    if (!reachable)
                    {
                        return (Key: key, Directory: dir, Filter: filter, Ready: false, Canceled: false);
                    }
                }

                bool exists = Directory.Exists(dir);
                return (Key: key, Directory: dir, Filter: filter, Ready: exists, Canceled: false);
            }
            catch (OperationCanceledException)
            {
                return (Key: key, Directory: dir, Filter: filter, Ready: false, Canceled: true);
            }
            catch
            {
                return (Key: key, Directory: dir, Filter: filter, Ready: false, Canceled: false);
            }
        });

        var probeResults = await Task.WhenAll(probeTasks).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var candidates = new List<(string Directory, string Filter)>(probeResults.Length);
        DateTime nextRetryUtc = DateTime.UtcNow.AddSeconds(WatcherRetryCooldownSec);

        lock (_watcherLock)
        {
            foreach (var res in probeResults)
            {
                if (res.Canceled)
                    continue;

                if (res.Ready)
                {
                    _watcherRetryAfterUtc.Remove(res.Key);
                    candidates.Add((res.Directory, res.Filter));
                }
                else
                {
                    _watcherRetryAfterUtc[res.Key] = nextRetryUtc;
                }
            }
        }

        RegisterWatchers(candidates);
    }

    private static bool IsLocalFastDirectory(string dir)
    {
        if (string.IsNullOrWhiteSpace(dir) || dir.StartsWith(@"\\", StringComparison.Ordinal))
            return false;

        string? root = Path.GetPathRoot(dir);
        if (string.IsNullOrWhiteSpace(root))
            return false;

        try
        {
            var driveType = new DriveInfo(root).DriveType;
            return driveType is DriveType.Fixed or DriveType.Ram;
        }
        catch
        {
            return false;
        }
    }

    private void SetupHybridWatchers(IReadOnlyList<LogTailState> states, bool allowRemoteOrUnc)
    {
        if (states == null || states.Count == 0)
            return;

        var candidates = new List<(string Directory, string Filter)>(states.Count);
        foreach (var state in states)
        {
            var target = ResolveWatcherTarget(state?.Path);
            if (target is not var (dir, filter))
                continue;

            try
            {
                if (!allowRemoteOrUnc && !IsLocalFastDirectory(dir))
                    continue;

                string key = $"{dir}|{filter}";
                lock (_watcherLock)
                {
                    if (_watchersByKey.ContainsKey(key))
                        continue;
                }

                if (Directory.Exists(dir))
                {
                    candidates.Add((dir, filter));
                }
            }
            catch
            {
                // Invalid path format
            }
        }

        RegisterWatchers(candidates);
    }

    private void RegisterWatchers(List<(string Directory, string Filter)> candidates)
    {
        if (candidates.Count == 0)
            return;

        lock (_watcherLock)
        {
            if (_realtimeLoopCts is not { IsCancellationRequested: false })
                return;

            foreach (var (dir, filter) in candidates)
            {
                string key = $"{dir}|{filter}";
                if (_watchersByKey.ContainsKey(key))
                    continue;

                try
                {
                    var watcher = new FileSystemWatcher(dir, filter)
                    {
                        NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
                        IncludeSubdirectories = false,
                        EnableRaisingEvents = true
                    };

                    watcher.Changed += OnLogFileChanged;
                    watcher.Created += OnLogFileChanged;
                    watcher.Renamed += OnLogFileChanged;
                    watcher.Error += OnWatcherError;

                    _watchersByKey[key] = watcher;
                }
                catch (Exception ex)
                {
                    _watcherRetryAfterUtc[key] = DateTime.UtcNow.AddSeconds(WatcherRetryCooldownSec);
                    _errorLogger.LogDiagnostic($"FileSystemWatcher initialization skipped for '{dir}' ({filter}): {ex.Message}");
                }
            }
        }
    }

    private void OnLogFileChanged(object sender, FileSystemEventArgs e)
    {
        lock (_watcherLock)
        {
            if (_realtimeLoopCts is not { IsCancellationRequested: false }) return;

            var newDebounce = new CancellationTokenSource();
            var previous = Interlocked.Exchange(ref _watcherDebounceCts, newDebounce);
            CancelCtsOnly(ref previous);
            var token = newDebounce.Token;

            _ = Task.Delay(200, token).ContinueWith(t =>
            {
                try
                {
                    if (!t.IsCanceled && !token.IsCancellationRequested)
                    {
                        _ = _triggerPollAsync?.Invoke();
                    }
                }
                finally
                {
                    Interlocked.CompareExchange(ref _watcherDebounceCts, null, newDebounce);
                    newDebounce.Dispose();
                }
            }, TaskScheduler.Default);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        _errorLogger.LogDiagnostic($"Log FileSystemWatcher encountered network issue: {e.GetException()?.Message}");

        if (sender is not FileSystemWatcher failedWatcher)
            return;

        lock (_watcherLock)
        {
            string? failedKey = null;
            foreach (var kv in _watchersByKey)
            {
                if (ReferenceEquals(kv.Value, failedWatcher))
                {
                    failedKey = kv.Key;
                    break;
                }
            }

            if (failedKey is not null)
            {
                _watchersByKey.Remove(failedKey);
                _watcherRetryAfterUtc[failedKey] = DateTime.UtcNow.AddSeconds(WatcherErrorRetryCooldownSec);
            }
        }

        DisposeWatcherInstance(failedWatcher);
    }

    private void DisposeWatcherInstance(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnLogFileChanged;
            watcher.Created -= OnLogFileChanged;
            watcher.Renamed -= OnLogFileChanged;
            watcher.Error -= OnWatcherError;
            watcher.Dispose();
        }
        catch
        {
        }
    }

    private void DisposeWatchersInternal()
    {
        lock (_watcherLock)
        {
            CancelCtsOnly(ref _watcherDebounceCts);

            foreach (var watcher in _watchersByKey.Values)
            {
                DisposeWatcherInstance(watcher);
            }
            _watchersByKey.Clear();
            _watcherRetryAfterUtc.Clear();
            _triggerPollAsync = null;
        }
    }

    private Task? StopRealtimePollingLocked()
    {
        Interlocked.Increment(ref _realtimeSessionId);
        DisposeWatchersInternal();
        CancelCtsOnly(ref _realtimeLoopCts);
        Interlocked.Exchange(ref _realtimeTimer, null);
        return _realtimeLoopTask;
    }

    public void StopRealtimePolling()
    {
        lock (_realtimeLifecycleLock)
        {
            try
            {
                _ = StopRealtimePollingLocked();
            }
            catch (Exception ex)
            {
                _errorLogger.LogException(ex, "StopRealtimePolling");
            }
        }
    }

    /// <summary>
    /// Starts a PeriodicTimer loop (default 15000ms) that probes camera SMB endpoints in the background
    /// and marshals health statuses onto the UI thread via Application.Current.Dispatcher.InvokeAsync.
    /// </summary>
    public void StartCameraHealthPolling(
        Func<IReadOnlyList<CameraEndpointDescriptor>> endpointsProvider,
        Action<IReadOnlyList<CameraHealthStatus>> onHealthUpdated)
    {
        StopCameraHealthPolling();

        var cts = new CancellationTokenSource();
        var ct = cts.Token;
        int intervalMs = Math.Max(1000, _tuning.CameraHealthPollMs);
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
        _cameraLoopCts = cts;
        _cameraTimer = timer;

        _cameraLoopTask = Task.Run(async () =>
        {
            try
            {
                while (!ct.IsCancellationRequested && await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;

                    try
                    {
                        var endpoints = endpointsProvider();
                        if (endpoints.Count == 0) continue;

                        var statuses = await CheckCameraHealthAsync(endpoints, _tuning.CameraHealthTimeoutMs, ct).ConfigureAwait(false);
                        if (ct.IsCancellationRequested) break;

                        await InvokeOnUiThreadAsync(() => onHealthUpdated(statuses)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        _errorLogger.LogException(ex, "Periodic camera health probe failed");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Clean shutdown
            }
            catch (Exception ex)
            {
                _errorLogger.LogException(ex, "CameraHealth PeriodicTimer loop faulted");
            }
            finally
            {
                Interlocked.CompareExchange(ref _cameraTimer, null, timer);
                Interlocked.CompareExchange(ref _cameraLoopCts, null, cts);
                timer.Dispose();
                cts.Dispose();
            }
        });
    }

    public void StopCameraHealthPolling()
    {
        try
        {
            CancelCtsOnly(ref _cameraLoopCts);
            Interlocked.Exchange(ref _cameraTimer, null);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "StopCameraHealthPolling");
        }
        finally
        {
            _cameraLoopTask = null;
        }
    }

    /// <summary>
    /// Checks camera SMB endpoint reachability asynchronously without blocking the UI thread.
    /// </summary>
    public async Task<IReadOnlyList<CameraHealthStatus>> CheckCameraHealthAsync(
        IReadOnlyList<CameraEndpointDescriptor> endpoints,
        int timeoutMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (endpoints is null || endpoints.Count == 0)
            return [];

        int effectiveTimeout = timeoutMs > 0 ? timeoutMs : _tuning.CameraHealthTimeoutMs;
        try
        {
            return await _cameraImageService
                .CheckCameraEndpointsHealthAsync(endpoints, effectiveTimeout, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "CheckCameraHealthAsync");
            return [];
        }
    }

    /// <summary>
    /// Searches configured camera SMB roots for a panel's inspection image folder with a timeout (default 30s)
    /// and date-folder fallback.
    /// </summary>
    public async Task<IReadOnlyList<PanelImageSearchResult>> FindPanelImagesAsync(
        IReadOnlyList<PanelImageSearchTarget> targets,
        string panelName,
        DateTime? timestamp,
        int timeoutSec = 0,
        CancellationToken cancellationToken = default)
    {
        if (targets is null || targets.Count == 0)
            return [];

        int effectiveTimeoutSec = timeoutSec > 0 ? timeoutSec : _tuning.ImageSearchTimeoutSec;
        using var cts = CreateLinkedCtsSafe(cancellationToken);
        if (effectiveTimeoutSec > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSec));
        }
        var token = cts.Token;

        var tasks = targets.Select(async tgt =>
        {
            string root = tgt.Path;
            try
            {
                token.ThrowIfCancellationRequested();

                bool reachable = await _cameraImageService
                    .TestHostReachableAsync(root, _tuning.ImageHostTimeoutMs, token)
                    .ConfigureAwait(false);

                if (!reachable)
                {
                    return new PanelImageSearchResult(tgt.Side, root, Offline: true, DirectoryPath: null, IsFallback: false);
                }

                string? found = await _cameraImageService
                    .FindPanelImageFolderAsync(root, panelName, tgt.Code, timestamp, skipHostCheck: true, token)
                    .ConfigureAwait(false);

                if (!string.IsNullOrWhiteSpace(found))
                {
                    return new PanelImageSearchResult(tgt.Side, root, Offline: false, DirectoryPath: found, IsFallback: false);
                }

                string? fallbackDir = null;
                if (timestamp.HasValue && timestamp.Value != DateTime.MinValue)
                {
                    var ts = timestamp.Value;
                    string[] candidates =
                    [
                        Path.Combine(root, ts.ToString("yyyyMM", CultureInfo.InvariantCulture), ts.ToString("dd", CultureInfo.InvariantCulture)),
                        Path.Combine(root, ts.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                        Path.Combine(root, ts.ToString("yyyyMM", CultureInfo.InvariantCulture)),
                        Path.Combine(root, ts.ToString("yyyy-MM", CultureInfo.InvariantCulture))
                    ];

                    foreach (var cand in candidates)
                    {
                        if (Directory.Exists(cand))
                        {
                            fallbackDir = cand;
                            break;
                        }
                    }
                }

                return new PanelImageSearchResult(
                    tgt.Side,
                    root,
                    Offline: false,
                    DirectoryPath: fallbackDir,
                    IsFallback: fallbackDir is not null);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                string timeoutMsg = $"Operation timed out ({effectiveTimeoutSec}s)";
                _errorLogger.LogDiagnostic($"Image search timed out for panel '{panelName}' side '{tgt.Side}' after {effectiveTimeoutSec}s.");
                return new PanelImageSearchResult(tgt.Side, root, Offline: false, DirectoryPath: null, IsFallback: false, Error: timeoutMsg);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _errorLogger.LogException(ex, $"FindPanelImagesAsync[{tgt.Side}:{root}]");
                return new PanelImageSearchResult(tgt.Side, root, Offline: false, DirectoryPath: null, IsFallback: false, Error: ex.Message);
            }
        });

        return await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// Searches candidate MES_1.log paths in the background with reachability checks and timeout.
    /// </summary>
    public async Task<MesDetailSearchResult> FindMesDetailsAsync(
        IEnumerable<string> candidatePaths,
        string panelName,
        int timeoutSec = 0,
        CancellationToken cancellationToken = default)
    {
        int effectiveTimeoutSec = timeoutSec > 0 ? timeoutSec : _tuning.ImageSearchTimeoutSec;
        using var cts = CreateLinkedCtsSafe(cancellationToken);
        if (effectiveTimeoutSec > 0)
        {
            cts.CancelAfter(TimeSpan.FromSeconds(effectiveTimeoutSec));
        }
        var token = cts.Token;

        var flatCandidates = new List<string>();
        if (candidatePaths is not null)
        {
            foreach (var cand in candidatePaths)
            {
                if (string.IsNullOrWhiteSpace(cand)) continue;
                var parts = JoinedPathSplitRegex().Split(cand);
                foreach (var p in parts)
                {
                    string trimmed = p.Trim();
                    if (!string.IsNullOrWhiteSpace(trimmed) &&
                        !flatCandidates.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                    {
                        flatCandidates.Add(trimmed);
                    }
                }
            }
        }

        try
        {
            return await Task.Run(async () =>
            {
                string resolvedPath = string.Empty;
                bool unreachable = false;

                foreach (var c in flatCandidates)
                {
                    token.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(c)) continue;

                    if (c.StartsWith(@"\\", StringComparison.Ordinal))
                    {
                        bool reachable = await _cameraImageService
                            .TestHostReachableAsync(c, _tuning.LogHostTimeoutMs, token)
                            .ConfigureAwait(false);
                        if (!reachable)
                        {
                            unreachable = true;
                            continue;
                        }
                    }

                    var resolvedFamily = LogFileFamilyHelper.ResolveLogFileFamily(
                        c,
                        maxRotatedFiles: 5,
                        includeFirstTimestamps: false);
                    if (File.Exists(c) || resolvedFamily is { NewestToOldest.Count: > 0 })
                    {
                        resolvedPath = c;
                        unreachable = false;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(resolvedPath))
                {
                    return new MesDetailSearchResult(
                        string.Join("\n", flatCandidates),
                        FoundFile: false,
                        Unreachable: unreachable,
                        Details: []);
                }

                var details = _mesDetailService.FindMesPanelDetails(
                    resolvedPath,
                    panelName,
                    cancellationToken: token);
                string actualPath = details.Count > 0 && !string.IsNullOrWhiteSpace(details[0].SourceFile)
                    ? details[0].SourceFile
                    : resolvedPath;

                return new MesDetailSearchResult(
                    actualPath,
                    FoundFile: true,
                    Unreachable: false,
                    Details: details);
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            string msg = $"Operation timed out ({effectiveTimeoutSec}s)";
            _errorLogger.LogDiagnostic($"MES search timed out for panel '{panelName}' after {effectiveTimeoutSec}s.");
            return new MesDetailSearchResult(
                string.Join("\n", flatCandidates),
                FoundFile: false,
                Unreachable: true,
                Details: [],
                Error: msg);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, $"FindMesDetailsAsync[{panelName}]");
            return new MesDetailSearchResult(
                string.Join("\n", flatCandidates),
                FoundFile: false,
                Unreachable: false,
                Details: [],
                Error: ex.Message);
        }
    }

    public void StopPendingTailJobs()
    {
        try
        {
            CancelCtsOnly(ref _tailJobCts);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "StopPendingTailJobs");
        }
    }

    private static CancellationTokenSource CreateLinkedCtsSafe(CancellationToken externalToken)
    {
        try
        {
            return CancellationTokenSource.CreateLinkedTokenSource(externalToken);
        }
        catch (ObjectDisposedException)
        {
            var canceled = new CancellationTokenSource();
            canceled.Cancel();
            return canceled;
        }
    }

    private static void CancelCtsOnly(ref CancellationTokenSource? cts)
    {
        var local = Interlocked.Exchange(ref cts, null);
        if (local is null)
            return;

        try
        {
            local.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void StopAll()
    {
        StopPendingTailJobs();
        StopRealtimePolling();
        StopCameraHealthPolling();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAll();
    }
}
