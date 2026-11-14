namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public sealed class BackgroundMonitorServiceHybridTests : IDisposable
{
    private readonly string _tempDir;

    public BackgroundMonitorServiceHybridTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "HybridTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task HybridMonitoring_InitializesWatcher_AndTriggersOnLogChange()
    {
        string logPath = Path.Combine(_tempDir, "OTP_1.log");
        File.WriteAllText(logPath, "initial line\r\n", new UTF8Encoding(false));

        var tailReader = new LogTailReaderService();
        var tuning = new TuningOptions
        {
            RealtimePollMs = 5000 // Slow periodic timer so only FileSystemWatcher triggers immediately
        };
        using var service = new BackgroundMonitorService(tailReader: tailReader, tuning: tuning);

        var st = tailReader.CreateTailState(logPath, "TEST_MACHINE");
        var states = new List<LogTailState> { st };

        var initialPollTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pollTriggerTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int pollCount = 0;

        service.StartRealtimePolling(
            statesProvider: () => states,
            onPollCompleted: results =>
            {
                int count = Interlocked.Increment(ref pollCount);
                if (count == 1)
                {
                    initialPollTcs.TrySetResult(true);
                }
                else if (count >= 2) // 1st is initial run, 2nd is from FileSystemWatcher
                {
                    pollTriggerTcs.TrySetResult(true);
                }
            });

        // Verify active watcher was created for the directory
        Assert.Equal(1, service.ActiveLogWatchersCount);

        // Wait deterministically for the initial poll before mutating the file to avoid racing the watcher setup
        await initialPollTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await File.AppendAllTextAsync(logPath, "second line\r\n", new UTF8Encoding(false));

        // FileSystemWatcher debounce is ~200ms, should complete quickly without waiting 5000ms periodic timer
        await pollTriggerTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        // Stop polling and verify cleanup
        service.StopRealtimePolling();
        Assert.Equal(0, service.ActiveLogWatchersCount);
    }

    [Fact]
    public void HybridMonitoring_HandlesInvalidOrMissingDirectories_WithoutCrashing()
    {
        using var service = new BackgroundMonitorService();
        string nonExistentPath = @"Z:\NonExistentShare\Sub\OTP_1.log";
        var tailReader = new LogTailReaderService();
        var st = tailReader.CreateTailState(nonExistentPath, "OFFLINE_MACHINE");

        // Should not throw any exception when directory does not exist
        service.StartRealtimePolling(
            statesProvider: () => [st],
            onPollCompleted: _ => { });

        Assert.Equal(0, service.ActiveLogWatchersCount);
        service.StopRealtimePolling();
    }

    [Fact]
    public async Task HybridMonitoring_TriggersPollAndDrainsUnitsOnFileRenameRollover()
    {
        string logPath = Path.Combine(_tempDir, "OTP_1.log");
        string rotatedPath = Path.Combine(_tempDir, "OTP_1.1.log");
        var utf8NoBom = new UTF8Encoding(false);

        static string MakeUnit(string ts, string pmnt, string panel) =>
            $"[{ts}] [info] [GovionLogger.cpp:110] *****************START*****************\r\n" +
            $"[{ts}] [info] [GovionLogger.cpp:110] Register address:2010.0, read value:{pmnt}\r\n" +
            $"[{ts}] [info] [GovionLogger.cpp:110] PANELNAME:{panel}\r\n" +
            $"[{ts}] [info] [GovionLogger.cpp:110] *****************END*****************\r\n";

        File.WriteAllText(logPath, MakeUnit("2026-11-13 14:00:00.000", "PMNT1001A", "TL699_INIT"), utf8NoBom);

        var tailReader = new LogTailReaderService();
        var tuning = new TuningOptions
        {
            RealtimePollMs = 6000
        };
        using var service = new BackgroundMonitorService(tailReader: tailReader, tuning: tuning);

        var st = tailReader.CreateTailState(logPath, "MMAPI120");
        st.SkipFirst = true;
        var states = new List<LogTailState> { st };

        var collectedPanels = new List<string>();
        var firstPollDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var rolloverDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        service.StartRealtimePolling(
            statesProvider: () => states,
            onPollCompleted: results =>
            {
                firstPollDone.TrySetResult(true);
                foreach (var r in results)
                {
                    foreach (var u in r.Units)
                    {
                        lock (collectedPanels)
                        {
                            collectedPanels.Add(u.PanelName);
                            if (collectedPanels.Count >= 3)
                            {
                                rolloverDone.TrySetResult(true);
                            }
                        }
                    }
                }
            });

        await firstPollDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, service.ActiveLogWatchersCount);

        // Append 2 units, rename OTP_1.log -> OTP_1.1.log, and create fresh OTP_1.log with 1 unit
        await Task.Delay(80);
        await File.AppendAllTextAsync(
            logPath,
            MakeUnit("2026-11-13 14:01:00.000", "PMNT1002A", "TL699_ROLL_1") +
            MakeUnit("2026-11-13 14:02:00.000", "PMNT1003B", "TL699_ROLL_2"),
            utf8NoBom);
        File.Move(logPath, rotatedPath);
        await File.WriteAllTextAsync(
            logPath,
            MakeUnit("2026-11-13 14:03:00.000", "PMNT1004A", "TL699_AFTER_ROLL"),
            utf8NoBom);

        await rolloverDone.Task.WaitAsync(TimeSpan.FromSeconds(10));

        service.StopRealtimePolling();

        lock (collectedPanels)
        {
            Assert.Equal(["TL699_ROLL_1", "TL699_ROLL_2", "TL699_AFTER_ROLL"], collectedPanels);
        }
    }

    [Fact]
    public async Task StartAndStopRealtimePolling_RapidRestartAndDispose_DoesNotThrow()
    {
        string logPath = Path.Combine(_tempDir, "OTP_1.log");
        File.WriteAllText(logPath, "header\r\n", new UTF8Encoding(false));

        var tailReader = new LogTailReaderService();
        using var service = new BackgroundMonitorService(tailReader: tailReader, tuning: new TuningOptions { RealtimePollMs = 50 });
        var st = tailReader.CreateTailState(logPath, "MMAPI120");

        for (int i = 0; i < 6; i++)
        {
            service.StartRealtimePolling(() => [st], _ => { });
            await Task.Delay(15);
            service.StopRealtimePolling();
        }

        service.StartRealtimePolling(() => [st], _ => { });
        service.Dispose();
        Assert.Equal(0, service.ActiveLogWatchersCount);
    }

    [Fact]
    public async Task LoadMachinesLogsAsync_StalledReadTimesOut_WhileActiveProgressExtendsTimeout()
    {
        string logPath = Path.Combine(_tempDir, "OTP_1.log");
        File.WriteAllText(logPath, "dummy\r\n", new UTF8Encoding(false));

        // 1. Stalled reader never calls onProgress -> times out cleanly with TimedOut=true, Unreachable=false
        var stalledReader = new StubProgressTailReader(reportProgressTicks: 0, delayPerTickMs: 1800);
        using var stalledSvc = new BackgroundMonitorService(tailReader: stalledReader);
        var stalledResults = await stalledSvc.LoadMachinesLogsAsync(
            [new LogTailState { Source = "MMAPI120", Path = logPath, SkipFirst = false }],
            timeoutSec: 1);

        Assert.Single(stalledResults);
        Assert.True(stalledResults[0].TimedOut);
        Assert.False(stalledResults[0].Unreachable);
        Assert.Contains("timed out", stalledResults[0].ErrorMessage, StringComparison.OrdinalIgnoreCase);

        // 2. Progress-reporting reader runs longer than 1s total while reporting progress every 250ms -> succeeds!
        var progressingReader = new StubProgressTailReader(reportProgressTicks: 6, delayPerTickMs: 250);
        using var activeSvc = new BackgroundMonitorService(tailReader: progressingReader);
        var okResults = await activeSvc.LoadMachinesLogsAsync(
            [new LogTailState { Source = "MMAPI120", Path = logPath, SkipFirst = false }],
            timeoutSec: 1);

        Assert.Single(okResults);
        Assert.False(okResults[0].TimedOut);
        Assert.True(okResults[0].State.Initialized);
        Assert.Single(okResults[0].Units);
    }

    private sealed class StubProgressTailReader(int reportProgressTicks, int delayPerTickMs)
        : OTPSoftware.Core.Interfaces.ILogTailReaderService
    {
        public LogTailState CreateTailState(string path, string source = "") =>
            new() { Path = path, Source = source };

        public void ReadLogTailLines(LogTailState state, List<string> lines, bool skipExisting = false, long maxBytes = 0, bool enableRolloverDrain = true) { }
        public void ConvertLogLinesToUnits(LogTailState state, IReadOnlyList<string> lines, List<UnitRecord> units) { }
        public int ReadLogTailUnits(LogTailState state, List<UnitRecord> units, bool skipExisting = false, long maxBytes = 0, CancellationToken cancellationToken = default) => 0;

        public void ReadLogUnitsFromFile(
            LogTailState state,
            List<UnitRecord> units,
            long chunkBytes = 16 * 1024 * 1024,
            DateTime? startTime = null,
            DateTime? endTime = null,
            Action? onProgress = null,
            CancellationToken cancellationToken = default)
        {
            if (reportProgressTicks == 0)
            {
                cancellationToken.WaitHandle.WaitOne(delayPerTickMs);
                cancellationToken.ThrowIfCancellationRequested();
                return;
            }

            for (int i = 0; i < reportProgressTicks; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(delayPerTickMs);
                onProgress?.Invoke();
            }

            units.Add(new UnitRecord
            {
                Index = 1,
                Source = state.Source,
                PanelName = "TL_PROGRESS_OK",
                Timestamp = new DateTime(2026, 11, 13, 10, 0, 0)
            });
            state.Initialized = true;
        }
    }

    [Fact]
    public async Task StartRealtimePolling_StopThenRestartQuickly_DoesNotOverlapLoopsAndLogsSetupDiagnostic()
    {
        string missingDirLog = Path.Combine(_tempDir, "missing_folder", "OTP_1.log");
        var logger = new ErrorLoggerService(_tempDir);
        var overlapDetected = 0;
        var activeCount = 0;
        var session2Completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowReader = new OverlappingDetectTailReader(
            onReadEntered: () =>
            {
                if (Interlocked.Increment(ref activeCount) > 1)
                {
                    Interlocked.Exchange(ref overlapDetected, 1);
                }
            },
            onReadExited: () => Interlocked.Decrement(ref activeCount));

        using var monitor = new BackgroundMonitorService(
            tailReader: slowReader,
            errorLogger: logger,
            tuning: new TuningOptions { RealtimePollMs = 5000 });

        int session1Callbacks = 0;
        monitor.StartRealtimePolling(
            statesProvider: () => throw new InvalidOperationException("Initial watcher probe simulated failure"),
            onPollCompleted: _ => Interlocked.Increment(ref session1Callbacks));

        // Rapid stop -> restart with valid slow tail read + second restart -> must never run two poll cycles simultaneously
        int activeRestartCallbacks = 0;
        monitor.StartRealtimePolling(
            statesProvider: () => [new LogTailState { Source = "MMAPI120", Path = missingDirLog }],
            onPollCompleted: _ => { });

        await Task.Delay(30);
        monitor.StopRealtimePolling();

        monitor.StartRealtimePolling(
            statesProvider: () => [new LogTailState { Source = "MMAPI120", Path = missingDirLog }],
            onPollCompleted: _ =>
            {
                if (Interlocked.Increment(ref activeRestartCallbacks) >= 1)
                {
                    session2Completed.TrySetResult(true);
                }
            });

        await session2Completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        monitor.StopRealtimePolling();

        Assert.Equal(0, overlapDetected);
        Assert.Equal(0, session1Callbacks);
        Assert.True(File.Exists(logger.LogFilePath));
        string diagLog = await File.ReadAllTextAsync(logger.LogFilePath);
        Assert.Contains("Initial watcher probe simulated failure", diagLog, StringComparison.Ordinal);
    }

    private sealed class OverlappingDetectTailReader(Action onReadEntered, Action onReadExited)
        : OTPSoftware.Core.Interfaces.ILogTailReaderService
    {
        public LogTailState CreateTailState(string path, string source = "") =>
            new() { Path = path, Source = source };

        public void ReadLogTailLines(LogTailState state, List<string> lines, bool skipExisting = false, long maxBytes = 0, bool enableRolloverDrain = true) { }
        public void ConvertLogLinesToUnits(LogTailState state, IReadOnlyList<string> lines, List<UnitRecord> units) { }
        public void ReadLogUnitsFromFile(LogTailState state, List<UnitRecord> units, long chunkBytes = 16 * 1024 * 1024, DateTime? startTime = null, DateTime? endTime = null, Action? onProgress = null, CancellationToken cancellationToken = default) { }

        public int ReadLogTailUnits(LogTailState state, List<UnitRecord> units, bool skipExisting = false, long maxBytes = 0, CancellationToken cancellationToken = default)
        {
            onReadEntered();
            try
            {
                cancellationToken.WaitHandle.WaitOne(120);
                cancellationToken.ThrowIfCancellationRequested();
                return 0;
            }
            finally
            {
                onReadExited();
            }
        }
    }

    [Fact]
    public async Task UncPath_WhenPort445Unreachable_MarksMachineUnreachableFast()
    {
        // Arrange: Configure 1 workstation with unreachable RFC 5737 TEST-NET-1 UNC share
        var settings = new OtpSettings
        {
            Machines =
            [
                new MachineConfig
                {
                    Name = "RFC5737_TESTNET1_MACHINE",
                    LogPath = @"\\192.0.2.1\share\OTP_1.log",
                    Enabled = true
                }
            ]
        };

        var tuning = new TuningOptions
        {
            LogHostTimeoutMs = 250
        };
        using var service = new BackgroundMonitorService(tuning: tuning);

        var st = new LogTailState
        {
            Source = settings.Machines[0].Name,
            Path = settings.Machines[0].LogPath,
            SkipFirst = false
        };

        // Act: Execute full load with 2s outer timeout (TCP 445 fast-fail happens in ~250ms)
        var startTime = DateTime.UtcNow;
        var results = await service.LoadMachinesLogsAsync([st], timeoutSec: 2);
        var elapsed = DateTime.UtcNow - startTime;

        // Assert: Marks machine Unreachable = true quickly without hanging
        Assert.Single(results);
        Assert.True(results[0].Unreachable);
        Assert.False(results[0].State.Initialized);
        Assert.Equal("Cannot connect to server", results[0].ErrorMessage);
        Assert.True(elapsed < TimeSpan.FromSeconds(5), $"Expected fast reachability timeout, but took {elapsed.TotalSeconds}s");
    }
}

