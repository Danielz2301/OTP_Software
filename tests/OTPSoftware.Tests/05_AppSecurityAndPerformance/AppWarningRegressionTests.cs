namespace OTPSoftware.Tests;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml.Linq;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.App.Views.Charts;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;
using Xunit;

public sealed class AppWarningRegressionTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlXNs = "http://schemas.microsoft.com/winfx/2006/xaml";

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

    private static string ResolveAppFilePath(params string[] relativeParts)
    {
        string baseDir = AppContext.BaseDirectory;
        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(new[] { dir.FullName, "src", "OTPSoftware.App" }.Concat(relativeParts).ToArray());
            if (File.Exists(candidate))
                return candidate;
        }

        throw new FileNotFoundException($"Could not locate app file: {Path.Combine(relativeParts)}");
    }

    [Fact]
    public void Warning03And04_XamlTouchTargetsLocalizationAndTextTrimming_MeetIndustrialStandards()
    {
        var appDoc = XDocument.Load(ResolveAppFilePath("App.xaml"));
        var mainDoc = XDocument.Load(ResolveAppFilePath("MainWindow.xaml"));
        var mesDoc = XDocument.Load(ResolveAppFilePath("Views", "MesDetailWindow.xaml"));

        // App.xaml: DatePicker calendar tooltip uses DynamicResource (no hardcoded "Open calendar")
        var calendarButtons = appDoc.Descendants(XamlNs + "Button")
            .Where(b => (string?)b.Attribute("ToolTip") is not null)
            .ToList();
        Assert.DoesNotContain(calendarButtons, b => string.Equals((string?)b.Attribute("ToolTip"), "Open calendar", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(calendarButtons, b => string.Equals((string?)b.Attribute("ToolTip"), "{DynamicResource DatePicker_OpenCalendarTooltip}", StringComparison.Ordinal));

        // App.xaml: CheckBox style uses MinHeight >= 32 and 18x18 CheckBoxBorder
        var checkBoxStyle = appDoc.Descendants(XamlNs + "Style")
            .FirstOrDefault(s => string.Equals((string?)s.Attribute("TargetType"), "CheckBox", StringComparison.Ordinal) &&
                                 s.Attribute(XamlXNs + "Key") is null);
        Assert.NotNull(checkBoxStyle);
        Assert.Contains(checkBoxStyle!.Elements(XamlNs + "Setter"), s =>
            string.Equals((string?)s.Attribute("Property"), "MinHeight", StringComparison.Ordinal) &&
            double.TryParse((string?)s.Attribute("Value"), out double mh) && mh >= 32.0);
        var checkBoxBorder = checkBoxStyle.Descendants(XamlNs + "Border")
            .FirstOrDefault(b => string.Equals((string?)b.Attribute(XamlXNs + "Name"), "CheckBoxBorder", StringComparison.Ordinal));
        Assert.NotNull(checkBoxBorder);
        Assert.Equal("18", (string?)checkBoxBorder!.Attribute("Width"));
        Assert.Equal("18", (string?)checkBoxBorder.Attribute("Height"));

        // MainWindow.xaml: Industrial touch targets >= 36px (carousel dots + chart/camera controls) and TextTrimming
        foreach (string btnName in new[] { "btnChartModeTimeline", "btnChartModePareto", "btnChartModeHourly", "btnChartModeSpc", "btnRefreshCameraHealth" })
        {
            var btn = mainDoc.Descendants(XamlNs + "Button")
                .FirstOrDefault(b => string.Equals((string?)b.Attribute(XamlXNs + "Name"), btnName, StringComparison.Ordinal));
            Assert.NotNull(btn);
            Assert.True(double.TryParse((string?)btn!.Attribute("MinHeight"), out double mh) && mh >= 36.0, $"{btnName} MinHeight must be >= 36");
        }

        var carouselDotButtons = mainDoc.Descendants(XamlNs + "Button")
            .Where(b => string.Equals((string?)b.Attribute("Command"), "{Binding SelectCarouselSlideCommand}", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(10, carouselDotButtons.Count);
        Assert.All(carouselDotButtons, b =>
        {
            Assert.True(double.TryParse((string?)b.Attribute("MinWidth"), out double mw) && mw >= 36.0);
            Assert.True(double.TryParse((string?)b.Attribute("MinHeight"), out double mh) && mh >= 36.0);
        });

        foreach (string tbName in new[] { "lblAlertTitle", "txtAlertMessage", "txtStatusIndicator", "txtRealtimeStatus" })
        {
            var tb = mainDoc.Descendants(XamlNs + "TextBlock")
                .FirstOrDefault(t => string.Equals((string?)t.Attribute(XamlXNs + "Name"), tbName, StringComparison.Ordinal));
            Assert.NotNull(tb);
            Assert.Equal("CharacterEllipsis", (string?)tb!.Attribute("TextTrimming"));
        }

        // MesDetailWindow.xaml: Interactive header/viewer toolbar buttons have MinHeight >= 36
        string[] mesToolbarCommands =
        [
            "{Binding CopyPanelCommand}",
            "{Binding ZoomOutImageCommand}",
            "{Binding ZoomInImageCommand}",
            "{Binding ResetImageZoomCommand}",
            "{Binding OneToOneZoomCommand}",
            "{Binding ToggleDefectCropCommand}",
            "{Binding ToggleSideBySideCommand}"
        ];
        foreach (string cmd in mesToolbarCommands)
        {
            var btn = mesDoc.Descendants(XamlNs + "Button")
                .FirstOrDefault(b => string.Equals((string?)b.Attribute("Command"), cmd, StringComparison.Ordinal));
            Assert.NotNull(btn);
            bool hasMinH = double.TryParse((string?)btn!.Attribute("MinHeight"), out double mh) && mh >= 36.0;
            bool hasH = double.TryParse((string?)btn.Attribute("Height"), out double h) && h >= 36.0;
            Assert.True(hasMinH || hasH, $"Button with Command='{cmd}' should be >= 36px tall");
        }

        // MesDetailWindow.xaml: Camera toggle header buttons also have MinHeight >= 36 (excluding stretch thumbnail tiles)
        foreach (string camCmd in new[] { "{Binding SelectLeftCameraCommand}", "{Binding SelectRightCameraCommand}" })
        {
            var headerToggleBtn = mesDoc.Descendants(XamlNs + "Button")
                .FirstOrDefault(b => string.Equals((string?)b.Attribute("Command"), camCmd, StringComparison.Ordinal) &&
                                     !string.Equals((string?)b.Attribute("HorizontalContentAlignment"), "Stretch", StringComparison.Ordinal));
            Assert.NotNull(headerToggleBtn);
            Assert.True(double.TryParse((string?)headerToggleBtn!.Attribute("MinHeight"), out double mh) && mh >= 36.0,
                $"Camera toggle button '{camCmd}' should be >= 36px tall");
        }
    }

    [Fact]
    public void Warning04_Localization_FormatsGridHeadersAndViewModelStringsAcrossLanguages()
    {
        var row = new DisplayRowViewModel
        {
            No = 1,
            Source = "M1",
            Timestamp = new DateTime(2026, 11, 15, 8, 30, 0),
            TimeFormatted = "2026-11-15 08:30:00",
            PMNT = "PMNT1001A",
            RunDisplay = "2",
            PanelName = "PANEL_ABC",
            BluName = "BLU_1",
            StatusText = "NG"
        };

        var enLoc = new LocalizationService("en");
        var viLoc = new LocalizationService("vi");
        var zhLoc = new LocalizationService("zh");

        string enTsv = GridActionsViewModel.FormatSelectedRows([row], includeHeader: true, enLoc);
        string viTsv = GridActionsViewModel.FormatSelectedRows([row], includeHeader: true, viLoc);
        string zhTsv = GridActionsViewModel.FormatSelectedRows([row], includeHeader: true, zhLoc);

        string enHeader = enTsv.Split(Environment.NewLine)[0];
        string viHeader = viTsv.Split(Environment.NewLine)[0];
        string zhHeader = zhTsv.Split(Environment.NewLine)[0];

        Assert.Equal("#\tSource\tTimestamp\tPMNT Code\tRun\tPanel Name\tBlu Name\tStatus", enHeader);
        Assert.NotEqual(enHeader, viHeader);
        Assert.NotEqual(enHeader, zhHeader);
        Assert.Contains(viLoc.Get("Col_PanelName"), viHeader, StringComparison.Ordinal);
        Assert.Contains(zhLoc.Get("Col_PanelName"), zhHeader, StringComparison.Ordinal);

        foreach (var loc in new[] { enLoc, viLoc, zhLoc })
        {
            Assert.DoesNotContain("[", loc.Get("DatePicker_OpenCalendarTooltip"), StringComparison.Ordinal);
            Assert.StartsWith("[", loc.Format("Spot_CropStatusFormat", "img.jpg"), StringComparison.Ordinal);
            Assert.Contains("img.jpg", loc.Format("Spot_CropStatusFormat", "img.jpg"), StringComparison.Ordinal);
            Assert.Contains("A1 / B1", loc.Format("Spot_CameraBadgeFormat", "A1 / B1"), StringComparison.Ordinal);
            string mesHeader = loc.Format("MesDetail_CopyHeaderFormat", "P1", "PMNT1", "M1", "2026-11-15");
            Assert.Contains("P1", mesHeader, StringComparison.Ordinal);
            Assert.Contains("PMNT1", mesHeader, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Warning05_ChartCanvasRenderer_EnablesPixelSnappingAndRendersGridlinesCrisply()
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
                MaxTime = t0.AddMinutes(5),
                Points =
                [
                    new ErrorChartPoint
                    {
                        PointIndex = 0,
                        UnitIndex = 1,
                        Pmnt = "PMNT_X",
                        Group = "A",
                        Timestamp = t0.AddMinutes(1),
                        TimeFormatted = "08:01:00",
                        ShortTime = "08:01",
                        ErrorCount = 1,
                        TotalPmntError = 2
                    },
                    new ErrorChartPoint
                    {
                        PointIndex = 1,
                        UnitIndex = 2,
                        Pmnt = "PMNT_X",
                        Group = "A",
                        Timestamp = t0.AddMinutes(4),
                        TimeFormatted = "08:04:00",
                        ShortTime = "08:04",
                        ErrorCount = 2,
                        TotalPmntError = 2,
                        IsPeakForPmnt = true
                    }
                ],
                Series =
                [
                    new ErrorChartSeries(
                        "PMNT_X",
                        "A",
                        2,
                        [
                            new ErrorChartPoint
                            {
                                PointIndex = 0,
                                UnitIndex = 1,
                                Pmnt = "PMNT_X",
                                Group = "A",
                                Timestamp = t0.AddMinutes(1),
                                TimeFormatted = "08:01:00",
                                ShortTime = "08:01",
                                ErrorCount = 1,
                                TotalPmntError = 2
                            },
                            new ErrorChartPoint
                            {
                                PointIndex = 1,
                                UnitIndex = 2,
                                Pmnt = "PMNT_X",
                                Group = "A",
                                Timestamp = t0.AddMinutes(4),
                                TimeFormatted = "08:04:00",
                                ShortTime = "08:04",
                                ErrorCount = 2,
                                TotalPmntError = 2,
                                IsPeakForPmnt = true
                            }
                        ])
                ]
            },
            TimelineLabels = new ChartTimelineLabels("Count", "Time", "HH:mm")
        };

        var (useLayoutRounding, snapsToDevicePixels, childCount) = RunOnStaThread(() =>
        {
            var host = new ChartVisualHost();
            var renderer = new ChartCanvasRenderer();
            var canvas = new Canvas { Width = 800, Height = 450 };
            renderer.RenderSnapshot(canvas, timelineSnapshot, attachInteractiveHandlers: false);
            var renderedHost = Assert.IsType<ChartVisualHost>(canvas.Children[0]);
            return (host.UseLayoutRounding && renderedHost.UseLayoutRounding,
                    host.SnapsToDevicePixels && renderedHost.SnapsToDevicePixels,
                    canvas.Children.Count);
        });

        Assert.True(useLayoutRounding);
        Assert.True(snapsToDevicePixels);
        Assert.Equal(1, childCount);
    }

    [Fact]
    public void Warning06_PendingRealtimeChannel_DropsOldestWhenFullAndIsLocalFastDirectoryRejectsUnc()
    {
        var channel = MainViewModel.CreatePendingRealtimeChannel(capacity: 3);
        for (int i = 0; i < 5; i++)
        {
            var batch = new IReadOnlyList<MachineTailReadResult>[]
            {
                [new MachineTailReadResult(new LogTailState { Path = $"path_{i}" }, [], DroppedNew: i, Unreachable: false, ErrorMessage: $"M{i}")]
            }[0];
            Assert.True(channel.Writer.TryWrite(batch));
        }

        var drained = new List<string?>();
        while (channel.Reader.TryRead(out var item))
        {
            drained.Add(item[0].ErrorMessage);
        }

        Assert.Equal(["M2", "M3", "M4"], drained);

        // BackgroundMonitorService UNC checks cover both backslash and forward-slash UNC forms
        Assert.False(BackgroundMonitorService.IsLocalFastDirectory(@"\\192.0.2.1\share\log"));
        Assert.False(BackgroundMonitorService.IsLocalFastDirectory("//192.0.2.1/share/log"));
        Assert.True(BackgroundMonitorService.IsLocalFastDirectory(Path.GetTempPath()));
    }

    [Fact]
    public async Task Warning07_RelayCommands_RaiseCanExecuteChangedDirectlyOncePerTransition()
    {
        int syncCalls = 0;
        var relay = new RelayCommand(_ => { });
        relay.CanExecuteChanged += (_, _) => syncCalls++;
        relay.RaiseCanExecuteChanged();
        Assert.Equal(1, syncCalls);

        int asyncCalls = 0;
        bool observedExecuting = false;
        var asyncCmd = new AsyncRelayCommand(async _ => await Task.Yield());
        asyncCmd.CanExecuteChanged += (_, _) =>
        {
            asyncCalls++;
            if (asyncCmd.IsExecuting)
            {
                observedExecuting = true;
                Assert.False(asyncCmd.CanExecute(null));
            }
        };

        await asyncCmd.ExecuteAsync();
        Assert.True(observedExecuting);
        Assert.Equal(2, asyncCalls); // once at start, once in finally
        Assert.True(asyncCmd.CanExecute(null));
    }

    [Fact]
    public async Task Warning01_ImageHelper_RetriesSharingViolationAndBoundsStalledOpenTimeout()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"otp_warn_img_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            byte[] pngBytes = RunOnStaThread(() =>
            {
                var wb = new WriteableBitmap(64, 64, 96, 96, PixelFormats.Bgr32, null);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(wb));
                using var ms = new MemoryStream();
                encoder.Save(ms);
                return ms.ToArray();
            });

            string imgPath = Path.Combine(tempDir, "camera_flush.png");
            await File.WriteAllBytesAsync(imgPath, pngBytes);

            // 1. Transient sharing lock (0x80070020) held briefly by simulated camera station recovers via FileIoRetryHelper
            using (var lockStream = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(40);
                    lockStream.Dispose();
                });

                var recovered = await ImageHelper.LoadBitmapAsync(
                    imgPath,
                    decodePixelWidth: 32,
                    maxRetries: 2,
                    retryDelayMs: 30);

                Assert.NotNull(recovered);
                Assert.True(recovered!.IsFrozen);
                Assert.Equal(32, recovered.PixelWidth);
            }

            // 2. Synchronous DecodeFrozenBitmapFromFile also retries transient sharing violation
            using (var syncLock = new FileStream(imgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(35);
                    syncLock.Dispose();
                });

                var syncLoaded = ImageHelper.DecodeFrozenBitmapFromFile(imgPath, decodePixelWidth: 0);
                Assert.NotNull(syncLoaded);
                Assert.Equal(64, syncLoaded!.PixelWidth);
            }

            // 3. Missing file returns null promptly without sleeping through full retry schedule
            string missingPath = Path.Combine(tempDir, "does_not_exist.png");
            var missingSw = Stopwatch.StartNew();
            var missingResult = await ImageHelper.LoadBitmapAsync(missingPath, maxRetries: 3, retryDelayMs: 500);
            missingSw.Stop();
            Assert.Null(missingResult);
            Assert.True(missingSw.Elapsed < TimeSpan.FromMilliseconds(450), $"Missing file should return promptly, took {missingSw.ElapsedMilliseconds}ms.");

            // 4. Stalled open respects bounded smbOpenTimeoutMs instead of hanging worker thread
            using var gate = new ManualResetEventSlim(false);
            var timeoutSw = Stopwatch.StartNew();
            var stalledResult = await ImageHelper.LoadBitmapCoreAsync(
                @"\\192.0.2.10\camera\stalled.png",
                decodePixelWidth: ImageHelper.ThumbnailDecodeWidth,
                maxRetries: 2,
                retryDelayMs: 25,
                smbOpenTimeoutMs: 90,
                streamOpener: (_, _) =>
                {
                    gate.Wait(1500, CancellationToken.None);
                    throw new IOException("Delayed SMB fault");
                });
            timeoutSw.Stop();
            gate.Set();

            Assert.Null(stalledResult);
            Assert.True(timeoutSw.Elapsed < TimeSpan.FromMilliseconds(750), $"Stalled SMB open exceeded bounded timeout: {timeoutSw.ElapsedMilliseconds}ms.");
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }
}
