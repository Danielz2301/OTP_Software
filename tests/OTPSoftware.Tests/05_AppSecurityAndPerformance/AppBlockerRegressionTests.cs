namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.App.Views.Charts;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;
using Xunit;

public sealed class AppBlockerRegressionTests : IDisposable
{
    private readonly string _tempDir;

    public AppBlockerRegressionTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"otp_blocker_tests_{Guid.NewGuid():N}");
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

    private static T RunOnStaThread<T>(Func<T> action)
    {
        T? result = default;
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
            finally
            {
                Dispatcher.FromThread(Thread.CurrentThread)?.InvokeShutdown();
            }
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new AggregateException(captured);
        }
        return result!;
    }

    private static byte[] CreateValidPngBytes(int width, int height)
    {
        int stride = (width + 7) / 8;
        byte[] pixels = new byte[stride * height];
        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Indexed1,
            BitmapPalettes.BlackAndWhite,
            pixels,
            stride);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    [Fact]
    public void Blocker01_ChartCanvasRenderer_EmitsZeroUiElementsAndSupportsHitTestingInAllModes()
    {
        var t0 = new DateTime(2026, 11, 15, 8, 0, 0);
        var timelineSnapshot = new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.CumulativeTimeline,
            ShowLines = true,
            ShowLabels = true,
            TimelineData = new ErrorChartData
            {
                TotalErrors = 2,
                UniquePmntCount = 1,
                MaxErrorCount = 2,
                MinTime = t0,
                MaxTime = t0.AddMinutes(10),
                Points =
                [
                    new ErrorChartPoint
                    {
                        PointIndex = 0,
                        UnitIndex = 1,
                        Pmnt = "PMNT1001A",
                        Group = "A",
                        Timestamp = t0.AddMinutes(2),
                        TimeFormatted = "08:02:00",
                        ShortTime = "08:02",
                        ErrorCount = 1,
                        TotalPmntError = 2,
                        IsPeakForPmnt = false,
                        Errors = ["XYLV"]
                    },
                    new ErrorChartPoint
                    {
                        PointIndex = 1,
                        UnitIndex = 2,
                        Pmnt = "PMNT1001A",
                        Group = "A",
                        Timestamp = t0.AddMinutes(8),
                        TimeFormatted = "08:08:00",
                        ShortTime = "08:08",
                        ErrorCount = 2,
                        TotalPmntError = 2,
                        IsPeakForPmnt = true,
                        Errors = ["FLICKER"]
                    }
                ],
                Series =
                [
                    new ErrorChartSeries(
                        "PMNT1001A",
                        "A",
                        2,
                        [
                            new ErrorChartPoint
                            {
                                PointIndex = 0,
                                UnitIndex = 1,
                                Pmnt = "PMNT1001A",
                                Group = "A",
                                Timestamp = t0.AddMinutes(2),
                                TimeFormatted = "08:02:00",
                                ShortTime = "08:02",
                                ErrorCount = 1,
                                TotalPmntError = 2,
                                IsPeakForPmnt = false
                            },
                            new ErrorChartPoint
                            {
                                PointIndex = 1,
                                UnitIndex = 2,
                                Pmnt = "PMNT1001A",
                                Group = "A",
                                Timestamp = t0.AddMinutes(8),
                                TimeFormatted = "08:08:00",
                                ShortTime = "08:08",
                                ErrorCount = 2,
                                TotalPmntError = 2,
                                IsPeakForPmnt = true
                            }
                        ])
                ]
            },
            TimelineLabels = new ChartTimelineLabels("Count", "Time", "HH:mm")
        };

        var paretoSnapshot = new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.DefectPareto,
            ParetoData = new ParetoChartData
            {
                TotalDefects = 10,
                MaxCount = 7,
                TopLabel = "PMNT1001A",
                VitalFewCount = 1,
                Items =
                [
                    new ParetoChartItem { Label = "PMNT1001A", Group = "A", Count = 7, Percentage = 70.0, CumulativePercentage = 70.0 },
                    new ParetoChartItem { Label = "PMNT2002B", Group = "B", Count = 3, Percentage = 30.0, CumulativePercentage = 100.0 }
                ]
            },
            ParetoLabels = new ChartParetoLabels("80% Cutoff", "Count", "Cum %", "PMNT")
        };

        var hourlySnapshot = new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.HourlyTrend,
            HourlyData = new HourlyTrendData
            {
                TotalUnits = 50,
                TotalOk = 45,
                TotalNg = 5,
                MaxHourlyTotal = 30,
                MaxNgRatePercent = 12.5,
                ShiftLabel = "Day",
                Buckets =
                [
                    new HourlyThroughputBucket { HourStart = t0, HourLabel = "08:00", TotalCount = 30, OkCount = 27, NgCount = 3, NgRatePercent = 10.0, FpyPercent = 90.0 },
                    new HourlyThroughputBucket { HourStart = t0.AddHours(1), HourLabel = "09:00", TotalCount = 20, OkCount = 18, NgCount = 2, NgRatePercent = 10.0, FpyPercent = 90.0 }
                ]
            },
            HourlyLabels = new ChartHourlyLabels("Units", "NG %", "Hour")
        };

        var (timelineCount, timelineHit, paretoCount, paretoHit, hourlyCount) = RunOnStaThread(() =>
        {
            var renderer = new ChartCanvasRenderer();
            var canvas = new Canvas { Width = 1000, Height = 520 };

            renderer.RenderSnapshot(canvas, timelineSnapshot, attachInteractiveHandlers: true);
            int tlChildren = canvas.Children.Count;
            Assert.Single(canvas.Children);
            Assert.IsType<ChartVisualHost>(canvas.Children[0]);
            Assert.NotEmpty(renderer.PlottedHitRects);
            var tlRect = renderer.PlottedHitRects[0].Bounds;
            string tlHit = renderer.FindHitRectPmnt(tlRect.X + (tlRect.Width / 2.0), tlRect.Y + (tlRect.Height / 2.0));

            renderer.RenderSnapshot(canvas, paretoSnapshot, attachInteractiveHandlers: true);
            int pChildren = canvas.Children.Count;
            Assert.Single(canvas.Children);
            Assert.IsType<ChartVisualHost>(canvas.Children[0]);
            Assert.True(renderer.PlottedHitRects.Count >= 4);
            var pRect = renderer.PlottedHitRects[0].Bounds;
            string pHit = renderer.FindHitRectPmnt(pRect.X + (pRect.Width / 2.0), pRect.Y + (pRect.Height / 2.0));

            renderer.RenderSnapshot(canvas, hourlySnapshot, attachInteractiveHandlers: true);
            int hChildren = canvas.Children.Count;
            Assert.Single(canvas.Children);
            Assert.IsType<ChartVisualHost>(canvas.Children[0]);

            renderer.Detach();
            return (tlChildren, tlHit, pChildren, pHit, hChildren);
        });

        Assert.Equal(1, timelineCount);
        Assert.Equal("PMNT1001A", timelineHit);
        Assert.Equal(1, paretoCount);
        Assert.Equal("PMNT1001A", paretoHit);
        Assert.Equal(1, hourlyCount);
    }

    [Fact]
    public async Task Blocker02_ImageHelper_BlocksOversizedFramesAndPreservesTrueOneToOneResolution()
    {
        byte[] validSmallBytes = RunOnStaThread(() => CreateValidPngBytes(16, 16));
        using (var smallStream = new MemoryStream(validSmallBytes))
        {
            smallStream.Position = 7;
            Assert.True(ImageHelper.ValidateImageDimensionsSafely(smallStream));
            Assert.Equal(7, smallStream.Position);
            Assert.False(ImageHelper.ValidateImageDimensionsSafely(smallStream, maxAllowedDimension: 8));
            Assert.Equal(7, smallStream.Position);
        }

        // Oversized frame exceeding MaxAllowedImageDimension (24_576px) must be rejected at header stage.
        byte[] bombBytes = RunOnStaThread(() => CreateValidPngBytes(ImageHelper.MaxAllowedImageDimension + 256, 1));
        string bombPath = Path.Combine(_tempDir, "bomb_25000px.png");
        await File.WriteAllBytesAsync(bombPath, bombBytes);

        using (var bombStream = new MemoryStream(bombBytes))
        {
            Assert.False(ImageHelper.ValidateImageDimensionsSafely(bombStream));
        }

        Assert.Null(ImageHelper.DecodeFrozenBitmapFromFile(bombPath, decodePixelWidth: ImageHelper.ThumbnailDecodeWidth));
        Assert.Null(await ImageHelper.LoadOneToOneBitmapAsync(bombPath));

        // Non-seekable stream and corrupt payload must safely return false.
        using (var corruptStream = new MemoryStream([1, 2, 3, 4]))
        {
            Assert.False(ImageHelper.ValidateImageDimensionsSafely(corruptStream));
        }

        // True 1:1 resolution above legacy 4096px clamp must preserve full PixelWidth.
        byte[] hiResBytes = RunOnStaThread(() => CreateValidPngBytes(4200, 8));
        string hiResPath = Path.Combine(_tempDir, "hires_4200px.png");
        await File.WriteAllBytesAsync(hiResPath, hiResBytes);

        var loaded = await ImageHelper.LoadOneToOneBitmapAsync(hiResPath);
        Assert.NotNull(loaded);
        Assert.True(loaded!.IsFrozen);
        Assert.Equal(4200, loaded.PixelWidth);
        Assert.Equal(8, loaded.PixelHeight);
    }

    [Fact]
    public void Blocker03_TrustedFactoryPathValidation_BlocksExternalUncAndInjectionVectors()
    {
        Assert.True(BackgroundMonitorService.IsTrustedFactoryPath(@"C:\AOI\Images\Panel01"));
        Assert.True(BackgroundMonitorService.IsTrustedFactoryPath(@"\\192.168.10.25\photos\day1"));
        Assert.True(BackgroundMonitorService.IsTrustedFactoryPath(@"\\10.1.2.3\aoi"));
        Assert.True(BackgroundMonitorService.IsTrustedFactoryPath(@"\\172.16.5.4\aoi"));
        Assert.True(BackgroundMonitorService.IsTrustedFactoryPath(@"\\127.0.0.1\share"));

        // External domains / public IPs / device namespaces / quote injection / relative paths must be rejected.
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath(@"\\8.8.8.8\share"));
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath(@"\\attacker.example.com\c$"));
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath(@"\\.\pipe\evil"));
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath(@"\\?\C:\Windows"));
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath("C:\\AOI\" --cmd"));
        Assert.False(BackgroundMonitorService.IsTrustedFactoryPath(@"relative\folder"));

        // Root containment must prevent traversal outside configured camera root.
        Assert.True(BackgroundMonitorService.IsPathWithinOrEqualRoot(@"D:\CameraRoot", @"D:\CameraRoot\20261115\Panel1"));
        Assert.True(BackgroundMonitorService.IsPathWithinOrEqualRoot(@"D:\CameraRoot", @"D:\CameraRoot"));
        Assert.False(BackgroundMonitorService.IsPathWithinOrEqualRoot(@"D:\CameraRoot", @"D:\CameraRoot\..\Windows\System32"));
    }

    [Fact]
    public async Task Blocker04_CameraImageLoader_SupportsAsyncDecodeAndCancellation()
    {
        byte[] pngBytes = RunOnStaThread(() => CreateValidPngBytes(120, 40));
        string imgPath = Path.Combine(_tempDir, "cam_sample.png");
        await File.WriteAllBytesAsync(imgPath, pngBytes);

        var bmp = await CameraImageLoader.LoadDecodedBitmapAsync(imgPath, decodePixelWidth: 60);
        Assert.NotNull(bmp);
        Assert.True(bmp!.IsFrozen);
        Assert.Equal(60, bmp.PixelWidth);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CameraImageLoader.LoadDecodedBitmapAsync(imgPath, 60, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            CameraImageLoader.LoadDecodedBitmap(imgPath, 60, cts.Token));
    }

    [Fact]
    public async Task Blocker05_AsyncRelayCommandAndBrowseLog_HandleExceptionsAndCanExecuteSafely()
    {
        var previousLogger = ErrorLoggerService.Default;
        var isolatedLogger = new ErrorLoggerService(_tempDir);
        ErrorLoggerService.Default = isolatedLogger;
        try
        {
            Exception? observed = null;
            var cmdWithCallback = new AsyncRelayCommand(
                () => Task.FromException(new InvalidOperationException("Simulated command failure")),
                onError: ex => observed = ex);

            await cmdWithCallback.ExecuteAsync(null);
            Assert.NotNull(observed);
            Assert.False(cmdWithCallback.IsExecuting);
            Assert.True(cmdWithCallback.CanExecute(null));
            Assert.True(File.Exists(isolatedLogger.LogFilePath));

            // ExecuteAsync without onError must still log & never throw out to crash dispatcher.
            var cmdWithoutCallback = new AsyncRelayCommand(
                () => Task.FromException(new InvalidOperationException("Unhandled command failure")));
            await cmdWithoutCallback.ExecuteAsync(null);
            Assert.False(cmdWithoutCallback.IsExecuting);

            // OperationCanceledException must not trigger onError callback.
            bool canceledTriggeredError = false;
            var canceledCmd = new AsyncRelayCommand(
                () => Task.FromException(new OperationCanceledException()),
                onError: _ => canceledTriggeredError = true);
            await canceledCmd.ExecuteAsync(null);
            Assert.False(canceledTriggeredError);
        }
        finally
        {
            ErrorLoggerService.Default = previousLogger;
        }

        var row = new MachineRowViewModel("OTP-1", dialogService: new MockDialogService());
        Assert.True(row.BrowseLogCommand.CanExecute("OTP"));
        Assert.True(row.BrowseLogCommand.CanExecute("MES"));
        Assert.False(row.BrowseLogCommand.CanExecute("UNKNOWN"));
    }

    [Fact]
    public void Blocker06_DisposeAndWeakEventManager_PreventSubscriberRetention()
    {
        var state = new GuiDisplayState();
        state.AllUnits.Add(new UnitRecord
        {
            Index = 1,
            Source = "MMAPI120",
            Pmnt = "PMNT1001A",
            PanelName = "PANEL01",
            BluName = "BLU01",
            Errors = ["XYLV"],
            Timestamp = DateTime.Now
        });

        WeakReference weakDisplayVm = CreateAndDisposeDisplayStateVm(state);
        state.Update(new UnitFilterSpec { SourceName = "0" });

        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakDisplayVm.IsAlive)
            {
                break;
            }
        }
        Assert.False(weakDisplayVm.IsAlive);

        // Verify LanguageChangedEventManager allows subscribers to be GC'd without explicit RemoveHandler.
        var loc = new LocalizationService("en");
        WeakReference weakSub = CreateAndSubscribeWeakListener(loc);
        loc.SetLanguage("vi");

        for (int i = 0; i < 4; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            if (!weakSub.IsAlive)
            {
                break;
            }
        }

        Assert.False(weakSub.IsAlive);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndDisposeDisplayStateVm(GuiDisplayState state)
    {
        var vm = new DisplayStateViewModel(state);
        vm.Dispose();
        return new WeakReference(vm);
    }

    [Fact]
    public async Task Blocker07_ExportOffscreenShiftChartAsync_ExportsValidPngFromBackgroundStaThread()
    {
        string outPngPath = Path.Combine(_tempDir, "offscreen_export.png");
        var emptySnapshot = new ChartRenderSnapshot
        {
            Mode = ChartDisplayMode.CumulativeTimeline,
            IsEmpty = true
        };

        string savedPath = await WpfChartExportService.Instance.ExportOffscreenShiftChartAsync(
            outPngPath,
            emptySnapshot,
            "SHIFT COMPLETE",
            "ZERO DEFECTS");

        Assert.Equal(outPngPath, savedPath);
        Assert.True(File.Exists(savedPath));
        byte[] bytes = await File.ReadAllBytesAsync(savedPath);
        Assert.True(bytes.Length > 8);
        // Standard PNG magic header: 89 50 4E 47
        Assert.Equal(0x89, bytes[0]);
        Assert.Equal(0x50, bytes[1]);
        Assert.Equal(0x4E, bytes[2]);
        Assert.Equal(0x47, bytes[3]);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateAndSubscribeWeakListener(ILocalizationService loc)
    {
        var subscriber = new WeakLanguageSubscriber(loc);
        return new WeakReference(subscriber);
    }

    private sealed class WeakLanguageSubscriber
    {
        public int CallCount { get; private set; }

        public WeakLanguageSubscriber(ILocalizationService loc)
        {
            LanguageChangedEventManager.AddHandler(loc, OnLangChanged);
        }

        private void OnLangChanged(object? sender, LanguageChangedEventArgs e) => CallCount++;
    }

    private sealed class MockDialogService : IDialogService
    {
        public string? ShowOpenFileDialog(string title, string filter, string? initialDirectory = null) => null;
        public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string initialFileName, string? initialDirectory = null) => null;
        public void ShowMessage(string message, string title, DialogMessageSeverity severity = DialogMessageSeverity.Information) { }
        public void ShowMesDetailDialog(DisplayRowViewModel item, IReadOnlyList<MesPanelDetailRecord> details, string mesLogPath, BackgroundMonitorService? backgroundMonitor, IReadOnlyList<CameraImageConfig>? cameraConfigs) { }
    }
}
