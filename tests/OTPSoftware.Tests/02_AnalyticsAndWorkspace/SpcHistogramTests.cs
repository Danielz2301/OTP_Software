namespace OTPSoftware.Tests;

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Windows.Controls;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.App.Views.Charts;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public sealed class SpcHistogramTests
{
    private readonly StatisticsService _service = new();

    [Fact]
    public void ComputeSpcMetrics_CalculatesExactMeanAndStdDevForKnownDataset()
    {
        // Known dataset: [2, 4, 4, 4, 5, 5, 7, 9]
        // N = 8, Mean = 5.0, sum((x - mean)^2) = 32.0
        // Sample variance = 32 / 7, Sample StdDev = sqrt(32 / 7) ≈ 2.138089935299395
        // Population variance = 32 / 8 = 4.0, Population StdDev = 2.0
        ReadOnlySpan<double> samples = [2.0, 4.0, 4.0, 4.0, 5.0, 5.0, 7.0, 9.0];

        var sampleMetrics = _service.ComputeSpcMetrics(samples, lsl: 3.0, usl: 8.0, useSampleStdDev: true);
        Assert.Equal(8, sampleMetrics.TotalCount);
        Assert.Equal(5.0, sampleMetrics.Mean, precision: 9);
        Assert.Equal(Math.Sqrt(32.0 / 7.0), sampleMetrics.StdDev, precision: 9);
        Assert.Equal(2.0, sampleMetrics.Min, precision: 9);
        Assert.Equal(9.0, sampleMetrics.Max, precision: 9);
        Assert.Equal(6, sampleMetrics.InSpecCount);
        Assert.Equal(2, sampleMetrics.OutOfSpecCount);

        var popMetrics = _service.ComputeSpcMetrics(samples, lsl: 3.0, usl: 8.0, useSampleStdDev: false);
        Assert.Equal(2.0, popMetrics.StdDev, precision: 9);
    }

    [Fact]
    public void ComputeSpcMetrics_CalculatesTwoSidedCpAndCpkAccurately()
    {
        // Construct a 4-sample dataset with exact Mean = 6000.0 and Sample StdDev = 250.0:
        // Let deviations from 6000 be [-d, -d, +d, +d].
        // Sample variance = 4 * d^2 / 3 = 250^2 => d = 250 * sqrt(3) / 2
        double d = 250.0 * Math.Sqrt(3.0) / 2.0;
        ReadOnlySpan<double> cctSamples = [6000.0 - d, 6000.0 - d, 6000.0 + d, 6000.0 + d];

        // CCT two-sided spec: LSL = 5500, USL = 8000
        // Cp = (8000 - 5500) / (6 * 250) = 2500 / 1500 = 5/3 ≈ 1.6666667
        // Cpl = (6000 - 5500) / (3 * 250) = 500 / 750 = 2/3 ≈ 0.6666667
        // Cpu = (8000 - 6000) / (3 * 250) = 2000 / 750 = 8/3 ≈ 2.6666667
        // Cpk = min(Cpl, Cpu) = 2/3 ≈ 0.6666667
        var metrics = _service.ComputeSpcMetrics(cctSamples, lsl: 5500.0, usl: 8000.0);

        Assert.Equal(6000.0, metrics.Mean, precision: 8);
        Assert.Equal(250.0, metrics.StdDev, precision: 8);
        Assert.NotNull(metrics.Cp);
        Assert.NotNull(metrics.Cpk);
        Assert.Equal(2500.0 / 1500.0, metrics.Cp!.Value, precision: 6);
        Assert.Equal(500.0 / 750.0, metrics.Cpk!.Value, precision: 6);
    }

    [Fact]
    public void ComputeSpcMetrics_CalculatesOneSidedCpkForLowerSpecLimitOnly()
    {
        // White_lv >= 300 (One-sided LSL = 300, USL = null)
        // Construct dataset with exact Mean = 330.0 and Sample StdDev = 5.0
        double d = 5.0 * Math.Sqrt(3.0) / 2.0;
        ReadOnlySpan<double> whiteLvSamples = [330.0 - d, 330.0 - d, 330.0 + d, 330.0 + d];

        var data = _service.GetSpcHistogramData(whiteLvSamples, "White_lv");

        Assert.Equal(300.0, data.Metrics.Lsl);
        Assert.Null(data.Metrics.Usl);
        Assert.Null(data.Metrics.Cp);
        Assert.NotNull(data.Metrics.Cpk);
        // Cpk = (330 - 300) / (3 * 5) = 30 / 15 = 2.0
        Assert.Equal(2.0, data.Metrics.Cpk!.Value, precision: 6);
    }

    [Fact]
    public void GetSpcHistogramData_PartitionsInto20To30BinsAndComputesGaussianCurve()
    {
        var samples = new double[120];
        for (int i = 0; i < samples.Length; i++)
        {
            // Range from 292.0 to 351.5 (some below LSL=300, most >= 300)
            samples[i] = 292.0 + (i * 0.5);
        }

        var hist = _service.GetSpcHistogramData(samples.AsSpan(), "White_lv");

        Assert.Equal("White_lv", hist.ParameterName);
        Assert.InRange(hist.Bins.Count, 20, 30);
        Assert.Equal(samples.Length, hist.Bins.Sum(b => b.Count));
        Assert.True(hist.MaxBinCount > 0);

        // Bins strictly below 300.0 must be marked out-of-spec (IsWithinSpec == false)
        var outOfSpecBins = hist.Bins.Where(b => b.EndValue < 300.0 && b.Count > 0).ToList();
        Assert.NotEmpty(outOfSpecBins);
        Assert.All(outOfSpecBins, b => Assert.False(b.IsWithinSpec));

        // Bins strictly above 300.0 must be marked in-spec (IsWithinSpec == true)
        var inSpecBins = hist.Bins.Where(b => b.StartValue > 300.0 && b.Count > 0).ToList();
        Assert.NotEmpty(inSpecBins);
        Assert.All(inSpecBins, b => Assert.True(b.IsWithinSpec));

        // Verify Gaussian Bell Curve points and peak density formula f(mu) = 1 / (sigma * sqrt(2*pi))
        Assert.NotEmpty(hist.GaussianPoints);
        double expectedPeakDensity = 1.0 / (hist.Metrics.StdDev * Math.Sqrt(2.0 * Math.PI));
        double actualMaxDensity = hist.GaussianPoints.Max(p => p.Density);
        Assert.Equal(expectedPeakDensity, actualMaxDensity, precision: 6);

        double expectedPeakScaled = expectedPeakDensity * hist.Metrics.TotalCount * hist.BinWidth;
        Assert.Equal(expectedPeakScaled, hist.MaxGaussianScaledCount, precision: 6);
    }

    [Fact]
    public void ExtractMesParameterValues_ExtractsOpticalParametersAndAliasesFromMesRecords()
    {
        var records = new List<MesPanelDetailRecord>
        {
            new()
            {
                PanelName = "PNL001",
                OtpDefectValue = "[White_lv=315.500,whitex=0.312,whitey=0.328,cct=6450.0,contrast=1280,ntsc=74.2%|flk=2.400,result=OK]"
            },
            new()
            {
                PanelName = "PNL002",
                OtpDefectValue = "[whitelv=295.000,whitex=0.340,whitey=0.330,colortemp=5400.0,contrast=1020,ntsc=39.5%|flicker=31.500,result=NG]"
            }
        };

        double[] whiteLv = _service.ExtractMesParameterValues(records, "White_lv");
        Assert.Equal([315.5, 295.0], whiteLv);

        double[] contrast = _service.ExtractMesParameterValues(records, "contrast");
        Assert.Equal([1280.0, 1020.0], contrast);

        double[] ntsc = _service.ExtractMesParameterValues(records, "ntsc");
        Assert.Equal([74.2, 39.5], ntsc);

        double[] cct = _service.ExtractMesParameterValues(records, "cct");
        Assert.Equal([6450.0, 5400.0], cct);

        double[] flk = _service.ExtractMesParameterValues(records, "flk");
        Assert.Equal([2.4, 31.5], flk);

        double[] whitex = _service.ExtractMesParameterValues(records, "whitex");
        Assert.Equal([0.312, 0.340], whitex);

        double[] whitey = _service.ExtractMesParameterValues(records, "whitey");
        Assert.Equal([0.328, 0.330], whitey);
    }

    [Fact]
    public void MesDetailService_ExtractRecentPanelRecords_ParsesBulkMesLogFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "SpcMesBulkTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string mesPath = Path.Combine(tempDir, "MES_1.log");
            string content = """
            [2026-11-15 10:00:01.000] [info] SEND MSG:
            <MESSAGE><BODY><PANELNAME>PNL101</PANELNAME><OTPDEFECTVALUE>[White_lv=322.400,whitex=0.315|flk=1.900]</OTPDEFECTVALUE></BODY></MESSAGE>
            [2026-11-15 10:00:02.000] [info] SEND MSG:
            <MESSAGE><BODY><PANELNAME>PNL102</PANELNAME><OTPDEFECTVALUE>[White_lv=334.800,whitex=0.318|flk=2.100]</OTPDEFECTVALUE></BODY></MESSAGE>
            """;
            File.WriteAllText(mesPath, content, new UTF8Encoding(false));

            var mesService = new MesDetailService();
            var records = mesService.ExtractRecentPanelRecords(mesPath);
            Assert.Equal(2, records.Count);

            var hist = _service.GetSpcHistogramData(records, "White_lv");
            Assert.Equal(2, hist.Metrics.TotalCount);
            Assert.Equal(328.6, hist.Metrics.Mean, precision: 5);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ChartCanvasRenderer_RendersSpcHistogramVisualsOnSingleVisualHostAndSupportsHitTest()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new ChartViewModel
                {
                    DisplayMode = ChartDisplayMode.SpcHistogram,
                    SelectedSpcParameter = "White_lv",
                    SpcSampleValues = [295.0, 305.0, 312.0, 318.0, 320.0, 325.0, 330.0, 335.0, 342.0]
                };

                var canvas = new Canvas { Width = 960, Height = 520 };
                var renderer = new ChartCanvasRenderer();

                renderer.Render(canvas, vm, []);

                Assert.False(vm.IsChartEmpty);
                Assert.Single(canvas.Children);
                Assert.IsType<ChartVisualHost>(canvas.Children[0]);
                Assert.InRange(renderer.PlottedHistogramBins.Count, 20, 30);

                var nonEmptyEntry = renderer.PlottedHistogramBins.First(b => b.Bin.Count > 0);
                double centerX = nonEmptyEntry.Bounds.Left + (nonEmptyEntry.Bounds.Width * 0.5);
                double centerY = nonEmptyEntry.Bounds.Top + (nonEmptyEntry.Bounds.Height * 0.5);

                var hitBin = renderer.FindHistogramBinAt(centerX, centerY);
                Assert.NotNull(hitBin);
                Assert.Equal(nonEmptyEntry.Bin.BinIndex, hitBin!.BinIndex);

                var tooltip = vm.BuildSpcTooltipContent(hitBin, vm.SelectedSpcParameter);
                Assert.Contains("White_lv", tooltip.Header);
                Assert.Contains(hitBin.RangeLabel, tooltip.Body);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx is not null)
        {
            throw new AggregateException("STA test failed", threadEx);
        }
    }

    [Fact]
    public void GetSpcHistogramData_HandlesEmptySingleAndZeroVarianceEdgeCasesGracefully()
    {
        // 1. Empty input
        var emptyHist = _service.GetSpcHistogramData(ReadOnlySpan<double>.Empty, "White_lv");
        Assert.Equal(0, emptyHist.Metrics.TotalCount);
        Assert.Null(emptyHist.Metrics.Cp);
        Assert.Null(emptyHist.Metrics.Cpk);
        Assert.Empty(emptyHist.Bins);
        Assert.Empty(emptyHist.GaussianPoints);

        // 2. Single sample
        var singleHist = _service.GetSpcHistogramData([315.0], "White_lv");
        Assert.Equal(1, singleHist.Metrics.TotalCount);
        Assert.Equal(315.0, singleHist.Metrics.Mean, precision: 6);
        Assert.Equal(0.0, singleHist.Metrics.StdDev, precision: 6);
        Assert.Null(singleHist.Metrics.Cp);
        Assert.Null(singleHist.Metrics.Cpk);
        Assert.InRange(singleHist.Bins.Count, 20, 30);
        Assert.Equal(1, singleHist.Bins.Sum(b => b.Count));
        Assert.Empty(singleHist.GaussianPoints);

        // 3. Constant values (zero variance) + non-finite filtering
        var constHist = _service.GetSpcHistogramData(
            [320.0, double.NaN, 320.0, double.PositiveInfinity, 320.0, double.NegativeInfinity, 320.0],
            "White_lv");
        Assert.Equal(4, constHist.Metrics.TotalCount);
        Assert.Equal(320.0, constHist.Metrics.Mean, precision: 6);
        Assert.Equal(0.0, constHist.Metrics.StdDev, precision: 6);
        Assert.Null(constHist.Metrics.Cp);
        Assert.Null(constHist.Metrics.Cpk);
        Assert.Equal(4, constHist.Bins.Sum(b => b.Count));
    }

    [Fact]
    public void GetSpcHistogramData_AutoDetectsNtscRatioAndPercentScales()
    {
        var ratioHist = _service.GetSpcHistogramData([0.45, 0.50, 0.55, 0.60], "ntsc");
        Assert.Equal(0.42, ratioHist.Metrics.Lsl);
        Assert.Null(ratioHist.Metrics.Usl);
        Assert.Equal(">= 0.42", ratioHist.SpecDisplay);

        var pctHist = _service.GetSpcHistogramData([45.0, 50.0, 55.0, 60.0], "ntsc");
        Assert.Equal(42.0, pctHist.Metrics.Lsl);
        Assert.Null(pctHist.Metrics.Usl);
        Assert.Equal(">= 42%", pctHist.SpecDisplay);
    }

    [Fact]
    public void ChartViewModel_SynchronizesSpcModeAndParameterSelection()
    {
        var vm = new ChartViewModel();
        Assert.Equal(ChartDisplayMode.CumulativeTimeline, vm.DisplayMode);
        Assert.False(vm.IsSpcMode);

        vm.SetSpcModeCommand.Execute(null);
        Assert.Equal(ChartDisplayMode.SpcHistogram, vm.DisplayMode);
        Assert.Equal(3, vm.SelectedDisplayModeIndex);
        Assert.True(vm.IsSpcMode);

        vm.SelectedSpcParameter = "cct";
        Assert.Equal("cct", vm.SelectedSpcParameter);
        Assert.Equal(3, vm.SelectedSpcParameterIndex);

        vm.SelectedSpcParameterIndex = 5;
        Assert.Equal("whitex", vm.SelectedSpcParameter);

        var snapshot = vm.BuildSpcHistogramSnapshot([0.295, 0.305, 0.312, 0.318, 0.325]);
        Assert.Equal(ChartDisplayMode.SpcHistogram, snapshot.Mode);
        Assert.False(snapshot.IsEmpty);
        Assert.NotNull(snapshot.SpcData);
        Assert.NotNull(snapshot.SpcLabels);
        Assert.Contains("whitex", vm.SummaryText);
    }

    [Fact]
    public void GetSpcHistogramData_WhenUnitsDoNotMatchMesRecords_ReturnsEmptyData()
    {
        var units = new List<UnitRecord>
        {
            new() { PanelName = "PNL_FILTERED_OUT_01" },
            new() { PanelName = "PNL_FILTERED_OUT_02" }
        };

        var mesRecords = new List<MesPanelDetailRecord>
        {
            new() { PanelName = "PNL_OTHER_01", OtpDefectValue = "[White_lv=325.5]" },
            new() { PanelName = "PNL_OTHER_02", OtpDefectValue = "[White_lv=330.0]" }
        };

        var spcData = _service.GetSpcHistogramData(units, "White_lv", mesRecords);
        Assert.Equal(0, spcData.Metrics.TotalCount);
        Assert.Empty(spcData.Bins);
    }

    [Fact]
    public void HistogramBin_WhenSamplesAreOutOfSpec_MarksBinOutOfSpec()
    {
        // Dataset where White_lv < 300 (e.g. 290.0, 295.0, 298.0)
        // With LSL = 300, all bins strictly containing these samples must have IsWithinSpec == false
        ReadOnlySpan<double> samples = [290.0, 295.0, 298.0, 310.0, 315.0, 320.0];
        var hist = _service.GetSpcHistogramData(samples, "White_lv", lsl: 300.0, usl: null);

        var violatingBins = hist.Bins.Where(b => b.Count > 0 && b.StartValue < 300.0).ToList();
        Assert.NotEmpty(violatingBins);
        foreach (var bin in violatingBins)
        {
            if (bin.EndValue <= 300.0)
            {
                Assert.False(bin.IsWithinSpec);
            }
        }
    }

    [Fact]
    public void ComputeSpcMetrics_WhenLslGreaterThanOrEqualToUsl_ReturnsNullCpAndCpk()
    {
        ReadOnlySpan<double> samples = [10.0, 12.0, 14.0, 16.0, 18.0];
        // Inverted specification: LSL (20) > USL (5)
        var metrics = _service.ComputeSpcMetrics(samples, lsl: 20.0, usl: 5.0);
        Assert.Null(metrics.Cp);
        Assert.Null(metrics.Cpk);

        // Equal specification: LSL == USL
        var metricsEqual = _service.ComputeSpcMetrics(samples, lsl: 10.0, usl: 10.0);
        Assert.Null(metricsEqual.Cp);
        Assert.Null(metricsEqual.Cpk);
    }

    [Fact]
    public void ChartViewModel_BuildRenderSnapshot_FiltersByPmntGroup()
    {
        var vm = new ChartViewModel
        {
            DisplayMode = ChartDisplayMode.SpcHistogram,
            SelectedSpcParameter = "White_lv",
            GroupFilter = PmntGroupFilter.A
        };

        var units = new List<UnitRecord>
        {
            new() { Pmnt = "PMNT-01A", PanelName = "PNL_A1" },
            new() { Pmnt = "PMNT-02B", PanelName = "PNL_B1" }
        };

        var mesRecords = new List<MesPanelDetailRecord>
        {
            new() { PanelName = "PNL_A1", OtpDefectValue = "[White_lv=320.0]" },
            new() { PanelName = "PNL_B1", OtpDefectValue = "[White_lv=340.0]" }
        };

        vm.MesRecords = mesRecords;
        var snapshot = vm.BuildRenderSnapshot(units);

        Assert.NotNull(snapshot.SpcData);
        Assert.Equal(1, snapshot.SpcData.Metrics.TotalCount);
        Assert.Equal(320.0, snapshot.SpcData.Metrics.Mean, precision: 4);
    }

    [Fact]
    public void ExtractMesParameterValues_SupportsSemicolonAndExtendedAliases()
    {
        var records = new List<MesPanelDetailRecord>
        {
            new()
            {
                PanelName = "PNL_EXT_01",
                OtpDefectValue = "color_temp=6200.0; white_x=0.310; white_y=0.325; flk=2.1"
            }
        };

        double[] cct = _service.ExtractMesParameterValues(records, "cct");
        Assert.Equal([6200.0], cct);

        double[] whitex = _service.ExtractMesParameterValues(records, "whitex");
        Assert.Equal([0.310], whitex);

        double[] whitey = _service.ExtractMesParameterValues(records, "whitey");
        Assert.Equal([0.325], whitey);

        double[] flk = _service.ExtractMesParameterValues(records, "flk");
        Assert.Equal([2.1], flk);
    }

    [Fact]
    public void ExtractMesParameterValues_StripsOpticalEngineeringUnitsAndSupportsColonSeparators()
    {
        var records = new List<MesPanelDetailRecord>
        {
            new()
            {
                PanelName = "PNL_UNIT_01",
                OtpDefectValue = "[White_lv: 320.5nit, contrast: 1280:1, cct: 6500K, flk: 2.4dB]"
            },
            new()
            {
                PanelName = "PNL_UNIT_02",
                OtpDefectValue = "[lv=315.0cd/m2, cr=1100:1, tc=6400K]"
            }
        };

        double[] whiteLv = _service.ExtractMesParameterValues(records, "White_lv");
        Assert.Equal([320.5, 315.0], whiteLv);

        double[] contrast = _service.ExtractMesParameterValues(records, "contrast");
        Assert.Equal([1280.0, 1100.0], contrast);

        double[] cct = _service.ExtractMesParameterValues(records, "cct");
        Assert.Equal([6500.0, 6400.0], cct);

        double[] flk = _service.ExtractMesParameterValues(records, "flk");
        Assert.Equal([2.4], flk);
    }

    [Fact]
    public void GetSpcHistogramData_FallsBackToUnitRecordMesResultWhenMesLogLacksMatchingPanels()
    {
        var units = new List<UnitRecord>
        {
            new() { PanelName = "PNL_LOCAL_01", MesResult = "[White_lv=322.0nit]" },
            new() { PanelName = "PNL_LOCAL_02", MesResult = "[White_lv=328.0nit]" }
        };

        var unmatchedMesRecords = new List<MesPanelDetailRecord>
        {
            new() { PanelName = "PNL_REMOTE_99", OtpDefectValue = "[White_lv=290.0]" }
        };

        var hist = _service.GetSpcHistogramData(units, "White_lv", unmatchedMesRecords);
        Assert.Equal(2, hist.Metrics.TotalCount);
        Assert.Equal(325.0, hist.Metrics.Mean, precision: 6);
    }

    [Fact]
    public void ComputeSpcMetrics_SanitizesNonFiniteLslAndUslGracefully()
    {
        ReadOnlySpan<double> samples = [10.0, 20.0, 30.0];
        var metrics = _service.ComputeSpcMetrics(samples, lsl: double.NaN, usl: double.PositiveInfinity);

        Assert.Equal(3, metrics.TotalCount);
        Assert.Equal(20.0, metrics.Mean, precision: 6);
        Assert.Null(metrics.Lsl);
        Assert.Null(metrics.Usl);
        Assert.Null(metrics.Cp);
        Assert.Null(metrics.Cpk);

        var hist = _service.GetSpcHistogramData(samples, "White_lv", lsl: double.NaN, usl: double.NegativeInfinity);
        Assert.True(double.IsFinite(hist.PlotMinX));
        Assert.True(double.IsFinite(hist.PlotMaxX));
    }

    [Fact]
    public void ChartCanvasRenderer_SupportsFullColumnHitTestingAcrossEntirePlotHeight()
    {
        Exception? threadEx = null;
        var thread = new Thread(() =>
        {
            try
            {
                var vm = new ChartViewModel
                {
                    DisplayMode = ChartDisplayMode.SpcHistogram,
                    SelectedSpcParameter = "White_lv",
                    SpcSampleValues = [310.0, 320.0, 330.0]
                };

                var canvas = new Canvas { Width = 960, Height = 520 };
                var renderer = new ChartCanvasRenderer();
                renderer.Render(canvas, vm, []);

                var entry = renderer.PlottedHistogramBins.First(b => b.Bin.Count > 0);
                double centerX = entry.Bounds.Left + (entry.Bounds.Width * 0.5);

                // Top of plot (padTop + 10px) - high above the bar
                var hitTop = renderer.FindHistogramBinAt(centerX, 48.0);
                Assert.NotNull(hitTop);
                Assert.Equal(entry.Bin.BinIndex, hitTop!.BinIndex);

                // Bottom of plot (baselineY - 5px) - near baseline
                var hitBottom = renderer.FindHistogramBinAt(centerX, 460.0);
                Assert.NotNull(hitBottom);
                Assert.Equal(entry.Bin.BinIndex, hitBottom!.BinIndex);
            }
            catch (Exception ex)
            {
                threadEx = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (threadEx is not null)
        {
            throw new AggregateException("STA test failed", threadEx);
        }
    }

    [Fact]
    public void FormatSpcMetricValue_HandlesNonFiniteAndExtremeValuesGracefully()
    {
        Assert.Equal("N/A", ChartViewModel.FormatSpcMetricValue("White_lv", double.NaN));
        Assert.Equal("N/A", ChartViewModel.FormatSpcMetricValue("White_lv", double.PositiveInfinity));
        Assert.Equal("N/A", ChartViewModel.FormatSpcMetricValue("White_lv", double.NegativeInfinity));
        Assert.Equal("0.3120", ChartViewModel.FormatSpcMetricValue("whitex", 0.312));
    }

    [Fact]
    public void ComputeSpcMetrics_CalculatesOneSidedCpkForUpperSpecLimitOnly()
    {
        // Construct 4 samples with exact Mean = 4.0 and Sample StdDev = 1.0
        double d = Math.Sqrt(3.0) / 2.0;
        ReadOnlySpan<double> samples = [4.0 - d, 4.0 - d, 4.0 + d, 4.0 + d];

        var metrics = _service.ComputeSpcMetrics(samples, lsl: null, usl: 10.0);
        Assert.Null(metrics.Lsl);
        Assert.Equal(10.0, metrics.Usl);
        Assert.Null(metrics.Cp);
        Assert.NotNull(metrics.Cpk);
        // Cpk = (USL - Mean) / (3 * StdDev) = (10 - 4) / (3 * 1) = 2.0
        Assert.Equal(2.0, metrics.Cpk!.Value, precision: 6);
        Assert.Equal(4, metrics.InSpecCount);
        Assert.Equal(0, metrics.OutOfSpecCount);

        ReadOnlySpan<double> withOutlier = [2.0, 3.0, 4.0, 12.0];
        var hist = _service.GetSpcHistogramData(withOutlier, "flk", lsl: null, usl: 10.0);
        Assert.Equal(3, hist.Metrics.InSpecCount);
        Assert.Equal(1, hist.Metrics.OutOfSpecCount);
        var outlierBin = hist.Bins.First(b => b.Count > 0 && b.StartValue >= 10.0);
        Assert.False(outlierBin.IsWithinSpec);
    }

    [Fact]
    public void SelectedSpcParameter_CanonicalizesAliases()
    {
        var vm = new ChartViewModel();

        vm.SelectedSpcParameter = "whitelv";
        Assert.Equal("White_lv", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "lv";
        Assert.Equal("White_lv", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "white-lv";
        Assert.Equal("White_lv", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "color_temp";
        Assert.Equal("cct", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "color-temp";
        Assert.Equal("cct", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "tc";
        Assert.Equal("cct", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "flicker";
        Assert.Equal("flk", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "cr";
        Assert.Equal("contrast", vm.SelectedSpcParameter);

        vm.SelectedSpcParameter = "unknown_metric_xyz";
        Assert.Contains(vm.SelectedSpcParameter, vm.SpcParameters);
        Assert.Equal("White_lv", vm.SelectedSpcParameter);
        Assert.Equal(0, vm.SelectedSpcParameterIndex);
    }

    [Fact]
    public void GetSpcHistogramData_IncludesSpecLimitsInPlotBoundsWhenAllSamplesOutOfSpec()
    {
        // Case 1: One-sided LSL = 300, 100% of samples strictly below LSL
        double[] allBelowLsl = [240.0, 245.0, 250.0, 255.0, 260.0];
        SpcHistogramData belowData = _service.GetSpcHistogramData(allBelowLsl, "White_lv", lsl: 300.0, usl: null);
        Assert.Equal(5, belowData.Metrics.OutOfSpecCount);
        Assert.True(belowData.PlotMinX < 300.0 && 300.0 < belowData.PlotMaxX,
            $"Expected LSL 300 inside [{belowData.PlotMinX}, {belowData.PlotMaxX}]");
        Assert.All(belowData.Bins.Where(b => b.Count > 0), b => Assert.False(b.IsWithinSpec));

        // Case 2: One-sided USL = 100, 100% of samples strictly above USL
        double[] allAboveUsl = [130.0, 135.0, 140.0, 145.0, 150.0];
        SpcHistogramData aboveData = _service.GetSpcHistogramData(allAboveUsl, "custom_usl", lsl: null, usl: 100.0);
        Assert.Equal(5, aboveData.Metrics.OutOfSpecCount);
        Assert.True(aboveData.PlotMinX < 100.0 && 100.0 < aboveData.PlotMaxX,
            $"Expected USL 100 inside [{aboveData.PlotMinX}, {aboveData.PlotMaxX}]");
        Assert.All(aboveData.Bins.Where(b => b.Count > 0), b => Assert.False(b.IsWithinSpec));

        // Verify STA rendering completes cleanly when all bars are NG and spec line is far from cluster
        var vm = new ChartViewModel
        {
            DisplayMode = ChartDisplayMode.SpcHistogram,
            ShowLines = true,
            ShowLabels = true
        };

        foreach (SpcHistogramData spc in new[] { belowData, aboveData })
        {
            var snapshot = vm.BuildSpcHistogramSnapshot(spc, updateState: false, zoomOverride: 1.0);
            Exception? staEx = null;
            var staThread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Width = 960, Height = 520 };
                    var renderer = new ChartCanvasRenderer();
                    try
                    {
                        renderer.RenderSnapshot(canvas, snapshot, attachInteractiveHandlers: false);
                        Assert.NotEmpty(renderer.PlottedHistogramBins);
                    }
                    finally
                    {
                        renderer.Detach();
                    }
                }
                catch (Exception ex)
                {
                    staEx = ex;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            if (staEx is not null)
            {
                throw new AggregateException("STA out-of-spec bounds render failed", staEx);
            }
        }
    }

    [Fact]
    public void ExtractMesParameterValues_HandlesOversizedMalformedTokensWithoutStackOverflow()
    {
        string hugeKey = new('X', 65_536);
        Assert.False(StatisticsService.IsParamNameMatch(hugeKey.AsSpan(), "White_lv", "whitelv"));

        string hugeAlias = new('Z', 65_536);
        Assert.False(StatisticsService.IsParamNameMatch("White_lv".AsSpan(), hugeKey.AsSpan(), hugeAlias.AsSpan()));

        var mesRecords = new List<MesPanelDetailRecord>
        {
            new()
            {
                PanelName = "PANEL_HUGE_01",
                OtpDefectValue = $"{hugeKey}=999; White-lv=325.5nit; {new string('Y', 32_768)}=123"
            },
            new()
            {
                PanelName = "PANEL_HUGE_02",
                OtpDefectValue = $"lv=340.25; {hugeAlias}=888"
            }
        };

        double[] extracted = _service.ExtractMesParameterValues(mesRecords, "white-lv");
        Assert.Equal(2, extracted.Length);
        Assert.Equal(325.5, extracted[0], 4);
        Assert.Equal(340.25, extracted[1], 4);
    }

    [Fact]
    public void BuildSpcHistogramSnapshot_TracksEnViZhLocalizationAndRendersWithoutError()
    {
        double[] samples = Enumerable.Range(0, 30).Select(i => 320.0 + ((i % 7) - 3) * 4.0).ToArray();
        var loc = new LocalizationService("en");
        var vm = new ChartViewModel(_service, loc)
        {
            DisplayMode = ChartDisplayMode.SpcHistogram,
            SelectedSpcParameter = "White_lv",
            ShowLines = true,
            ShowLabels = true
        };

        SpcHistogramData spc = _service.GetSpcHistogramData(samples, "White_lv");

        foreach (string lang in new[] { "en", "vi", "zh" })
        {
            Assert.True(loc.SetLanguage(lang));
            vm.RefreshLocalization();

            var snapshot = vm.BuildSpcHistogramSnapshot(spc, updateState: true, zoomOverride: 1.0);
            Assert.NotNull(snapshot.SpcLabels);
            Assert.DoesNotContain("Chart_Spc", snapshot.SpcLabels!.CardTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("Chart_Spc", snapshot.SpcLabels.YAxisTitle, StringComparison.Ordinal);
            Assert.DoesNotContain("Chart_Spc", snapshot.SpcLabels.XAxisTitle, StringComparison.Ordinal);
            Assert.Contains("White_lv", snapshot.SpcLabels.XAxisTitle, StringComparison.Ordinal);

            Exception? staEx = null;
            var staThread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Width = 1024, Height = 560 };
                    var renderer = new ChartCanvasRenderer();
                    try
                    {
                        renderer.RenderSnapshot(canvas, snapshot, attachInteractiveHandlers: false);
                        Assert.NotEmpty(renderer.PlottedHistogramBins);
                    }
                    finally
                    {
                        renderer.Detach();
                    }
                }
                catch (Exception ex)
                {
                    staEx = ex;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            if (staEx is not null)
            {
                throw new AggregateException($"STA render failed for language '{lang}'", staEx);
            }
        }
    }

    [Fact]
    public async Task WpfChartExportService_ExportsValidPngAcrossAllCpkTiers()
    {
        // Tier 1: Cpk >= 1.33, Tier 2: 1.0 <= Cpk < 1.33, Tier 3: Cpk < 1.0
        double[] cpkHighSamples = Enumerable.Range(0, 24).Select(i => 330.0 + ((i % 4) - 1.5) * 2.0).ToArray();
        double[] cpkMidSamples = [312.0, 314.0, 316.0, 318.0, 312.0, 314.0, 316.0, 318.0];
        double[] cpkLowSamples = [295.0, 305.0, 315.0, 325.0, 300.0, 310.0, 320.0, 330.0];

        var vm = new ChartViewModel
        {
            DisplayMode = ChartDisplayMode.SpcHistogram,
            SelectedSpcParameter = "White_lv",
            ShowLines = true,
            ShowLabels = true
        };

        foreach (double[] tierSamples in new[] { cpkHighSamples, cpkMidSamples, cpkLowSamples })
        {
            var data = _service.GetSpcHistogramData(tierSamples, "White_lv", lsl: 300.0, usl: null);
            var snapshot = vm.BuildSpcHistogramSnapshot(data, updateState: true, zoomOverride: 1.0);
            Assert.False(snapshot.IsEmpty);

            Exception? staEx = null;
            var staThread = new Thread(() =>
            {
                try
                {
                    var canvas = new Canvas { Width = 960, Height = 520 };
                    var renderer = new ChartCanvasRenderer();
                    try
                    {
                        renderer.RenderSnapshot(canvas, snapshot, attachInteractiveHandlers: false);
                        Assert.NotEmpty(renderer.PlottedHistogramBins);
                    }
                    finally
                    {
                        renderer.Detach();
                    }
                }
                catch (Exception ex)
                {
                    staEx = ex;
                }
            });
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.Start();
            staThread.Join();
            if (staEx is not null)
            {
                throw new AggregateException("STA tier render failed", staEx);
            }
        }

        string tempPng = Path.Combine(Path.GetTempPath(), $"otp_spc_test_{Guid.NewGuid():N}.png");
        try
        {
            var exportData = _service.GetSpcHistogramData(cpkHighSamples, "White_lv", lsl: 300.0, usl: null);
            var exportSnapshot = vm.BuildSpcHistogramSnapshot(exportData, updateState: false, zoomOverride: 1.0);
            string savedPath = await WpfChartExportService.Instance.ExportOffscreenShiftChartAsync(
                tempPng,
                exportSnapshot,
                "Empty",
                "No SPC Samples");

            Assert.Equal(tempPng, savedPath);
            Assert.True(File.Exists(tempPng));
            byte[] bytes = await File.ReadAllBytesAsync(tempPng);
            Assert.True(bytes.Length > 1024, $"Expected non-trivial PNG output, got {bytes.Length} bytes");
            Assert.Equal(0x89, bytes[0]);
            Assert.Equal(0x50, bytes[1]);
            Assert.Equal(0x4E, bytes[2]);
            Assert.Equal(0x47, bytes[3]);
        }
        finally
        {
            if (File.Exists(tempPng))
            {
                File.Delete(tempPng);
            }
        }
    }

    [Fact]
    public async Task BuildRenderSnapshot_WithUnreachableUncMesPath_ReturnsImmediatelyFromInMemoryCacheWithoutBlockingIo()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_ui_cache_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            const string unreachableUncMes = @"\\192.0.2.254\unreachable_share\MES_1.log";
            string localMesPath = Path.Combine(tempRoot, "Machine1", "MES_1.log");
            Directory.CreateDirectory(Path.GetDirectoryName(localMesPath)!);
            WriteSampleMesLog(localMesPath, new[]
            {
                ("TL700001", 312.5, "OK"),
                ("TL700002", 315.0, "OK"),
                ("TL700003", 317.5, "OK"),
                ("TL700004", 320.0, "OK"),
                ("TL700005", 322.5, "OK"),
                ("TL700006", 325.0, "OK")
            });

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(unreachableUncMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();
            var tuning = new TuningOptions
            {
                LogHostTimeoutMs = 150,
                ImageSearchTimeoutSec = 5
            };

            using var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: tuning);

            vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
            vm.Chart.SelectedSpcParameter = "White_lv";

            var inlineUnits = Enumerable.Range(1, 6)
                .Select(i => new UnitRecord
                {
                    Source = "Machine 1",
                    PanelName = $"TL70000{i}",
                    Timestamp = new DateTime(2026, 11, 15, 10, i, 0),
                    MesResult = $"White_lv={310.0 + i * 2.0:F1}"
                })
                .ToList();

            // 1. Cold cache + unreachable UNC path: BuildRenderSnapshot must be strictly in-memory O(1), zero disk/UNC I/O.
            ChartRenderSnapshot? coldSnapshot = null;
            long coldElapsedMs = RunOnStaThread(() =>
            {
                var sw = Stopwatch.StartNew();
                coldSnapshot = vm.Chart.BuildRenderSnapshot(inlineUnits);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            });

            Assert.True(coldElapsedMs < 500, $"Expected non-blocking UI snapshot (<500ms), took {coldElapsedMs}ms.");
            Assert.Empty(mesSpy.ExtractCalls);
            Assert.Empty(camSpy.HostReachabilityChecks);
            Assert.NotNull(coldSnapshot);
            Assert.False(coldSnapshot!.IsEmpty);
            Assert.Equal(6, coldSnapshot.SpcData!.Metrics.TotalCount);

            // 2. Populate in-memory cache via background loader using reachable local + unreachable UNC candidate paths,
            // then confirm BuildRenderSnapshot uses only the cached MES records with zero subsequent disk/UNC calls.
            var unitsWithoutInlineMes = Enumerable.Range(1, 6)
                .Select(i => new UnitRecord
                {
                    Source = "Machine 1",
                    PanelName = $"TL70000{i}",
                    Timestamp = new DateTime(2026, 11, 15, 10, i, 0),
                    MesResult = string.Empty
                })
                .ToList();

            vm.MachineRows[0].LogPath = Path.Combine(tempRoot, "Machine1", "OTP_1.log");
            await vm.RefreshSpcMesRecordsAsync(unitsWithoutInlineMes).WaitAsync(TimeSpan.FromSeconds(10));

            mesSpy.Reset();
            camSpy.Reset();

            ChartRenderSnapshot? warmSnapshot = null;
            long warmElapsedMs = RunOnStaThread(() =>
            {
                var sw = Stopwatch.StartNew();
                warmSnapshot = vm.Chart.BuildRenderSnapshot(unitsWithoutInlineMes);
                sw.Stop();
                return sw.ElapsedMilliseconds;
            });

            Assert.True(warmElapsedMs < 500, $"Expected fast cached UI snapshot (<500ms), took {warmElapsedMs}ms.");
            Assert.Empty(mesSpy.ExtractCalls);
            Assert.Empty(camSpy.HostReachabilityChecks);
            Assert.NotNull(warmSnapshot);
            Assert.False(warmSnapshot!.IsEmpty);
            Assert.Equal(6, warmSnapshot.SpcData!.Metrics.TotalCount);
            Assert.Equal(318.75, warmSnapshot.SpcData.Metrics.Mean, precision: 2);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesBackgroundLoader_SkipsUnreachableUncHosts_LoadsLocalTempMesLogOffThreadAndRefreshesCache()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_bg_load_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            const string unreachableUncMes = @"\\192.0.2.254\unreachable_share\MES_1.log";
            string localMesDir = Path.Combine(tempRoot, "MachineB");
            Directory.CreateDirectory(localMesDir);
            string localMesPath = Path.Combine(localMesDir, "MES_1.log");

            WriteSampleMesLog(localMesPath, new[]
            {
                ("TL800001", 306.0, "OK"),
                ("TL800002", 308.0, "OK"),
                ("TL800003", 310.0, "OK"),
                ("TL800004", 312.0, "OK"),
                ("TL800005", 314.0, "OK"),
                ("TL800006", 316.0, "OK"),
                ("TL800007", 318.0, "OK"),
                ("TL800008", 320.0, "OK")
            });

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine A",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(unreachableUncMes)}}
                },
                {
                  "name": "Machine B",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(localMesPath)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();
            var tuning = new TuningOptions
            {
                LogHostTimeoutMs = 150,
                ImageSearchTimeoutSec = 5
            };

            using var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: tuning);

            int refreshRequestedCount = 0;
            vm.ChartRefreshRequested += (_, _) => Interlocked.Increment(ref refreshRequestedCount);

            // Populate scope units covering both enabled machines without inline MesResult values.
            for (int i = 1; i <= 8; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord
                {
                    Index = i - 1,
                    Source = i <= 2 ? "Machine A" : "Machine B",
                    PanelName = $"TL80000{i}",
                    Timestamp = new DateTime(2026, 11, 15, 11, i, 0),
                    MesResult = string.Empty
                });
            }

            // Trigger entering SPC Histogram mode on Tab 3 from a dedicated STA UI thread.
            int staUiThreadId = RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.SelectedTabIndex = 2;
                vm.Chart.SelectedSpcParameter = "White_lv";
                vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                return Environment.CurrentManagedThreadId;
            });

            // Confirm zero synchronous disk/UNC extraction happened on the STA UI thread itself.
            Assert.DoesNotContain(mesSpy.ExtractCalls, call => call.ThreadId == staUiThreadId);
            Assert.DoesNotContain(camSpy.HostReachabilityChecks, call => call.ThreadId == staUiThreadId);

            // Wait deterministically for the automatically triggered background load task.
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains(
                camSpy.HostReachabilityChecks,
                c => string.Equals(c.Path, unreachableUncMes, StringComparison.OrdinalIgnoreCase) && c.ThreadId != staUiThreadId);
            Assert.DoesNotContain(
                mesSpy.ExtractCalls,
                c => string.Equals(c.Path, unreachableUncMes, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                mesSpy.ExtractCalls,
                c => string.Equals(c.Path, localMesPath, StringComparison.OrdinalIgnoreCase) && c.ThreadId != staUiThreadId);
            Assert.True(Volatile.Read(ref refreshRequestedCount) >= 1, "Expected ChartRefreshRequested after background MES cache update.");

            // Verify rendering uses updated cache with zero further I/O.
            mesSpy.Reset();
            camSpy.Reset();

            var snapshot = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.Empty(mesSpy.ExtractCalls);
            Assert.Empty(camSpy.HostReachabilityChecks);
            Assert.NotNull(snapshot.SpcData);
            Assert.Equal(8, snapshot.SpcData!.Metrics.TotalCount);
            Assert.Equal(313.0, snapshot.SpcData.Metrics.Mean, precision: 2);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesCache_IgnoresDisabledMachines_LoadsOnTabEntry_AndRefreshesOnSourceFilterChange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_lifecycle_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            string m1Mes = Path.Combine(tempRoot, "M1", "MES_1.log");
            string m2Mes = Path.Combine(tempRoot, "M2", "MES_1.log");
            Directory.CreateDirectory(Path.GetDirectoryName(m1Mes)!);
            Directory.CreateDirectory(Path.GetDirectoryName(m2Mes)!);
            const string disabledUnc = @"\\192.0.2.254\disabled_share\MES_1.log";

            WriteSampleMesLog(m1Mes, new[]
            {
                ("M1_0", 300.0, "OK"),
                ("M1_1", 302.0, "OK"),
                ("M1_2", 304.0, "OK"),
                ("M1_3", 306.0, "OK")
            });
            WriteSampleMesLog(m2Mes, new[]
            {
                ("M2_0", 340.0, "OK"),
                ("M2_1", 342.0, "OK"),
                ("M2_2", 344.0, "OK"),
                ("M2_3", 346.0, "OK")
            });

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(m1Mes)}}
                },
                {
                  "name": "Machine 2",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(m2Mes)}}
                },
                {
                  "name": "Machine 3",
                  "enabled": false,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(disabledUnc)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();
            using var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 150, ImageSearchTimeoutSec = 5 });

            // Entering SPC Histogram mode while still on Tab 0 must not trigger background MES loading prematurely.
            vm.Chart.SelectedSpcParameter = "White_lv";
            vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(mesSpy.ExtractCalls);
            Assert.Empty(camSpy.HostReachabilityChecks);

            // Switching to Tab 3 (SelectedTabIndex == 2) BEFORE loading OTP logs keeps all 4 charts empty and avoids MES I/O.
            RunOnStaThread(() =>
            {
                vm.SelectedTabIndex = 2;
                return 0;
            });
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(mesSpy.ExtractCalls);
            var preLoadSnapshot = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.True(preLoadSnapshot.IsEmpty);
            Assert.Equal(0, preLoadSnapshot.SpcData?.Metrics.TotalCount ?? 0);

            // Simulate loading OTP logs ("Read Log"), then SPC loads MES_1.log for enabled machines only.
            for (int i = 0; i < 4; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord { Index = i, Source = "Machine 1", PanelName = $"M1_{i}" });
                vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 4 + i, Source = "Machine 2", PanelName = $"M2_{i}" });
            }
            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.TriggerSpcMesLoadIfNeeded(vm.DisplayState.ScopeUnits);
                return 0;
            });
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Empty(camSpy.HostReachabilityChecks);
            Assert.DoesNotContain(mesSpy.ExtractCalls, c => string.Equals(c.Path, disabledUnc, StringComparison.OrdinalIgnoreCase));

            var allSnapshot = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(allSnapshot.SpcData);
            Assert.Equal(8, allSnapshot.SpcData!.Metrics.TotalCount);
            Assert.Equal(323.0, allSnapshot.SpcData.Metrics.Mean, precision: 2);

            // Switching SelectedSourceIndex to Machine 1 updates cache and scope to Machine 1 only.
            mesSpy.Reset();
            RunOnStaThread(() =>
            {
                vm.SelectedSourceIndex = 1;
                return 0;
            });
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Single(mesSpy.ExtractCalls);
            Assert.Contains(mesSpy.ExtractCalls, c => string.Equals(c.Path, m1Mes, StringComparison.OrdinalIgnoreCase));

            var m1Snapshot = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(m1Snapshot.SpcData);
            Assert.Equal(4, m1Snapshot.SpcData!.Metrics.TotalCount);
            Assert.Equal(303.0, m1Snapshot.SpcData.Metrics.Mean, precision: 2);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesCache_CoalescesIdenticalInflightTriggers_AndCancelsSupersededRequestOnSourceSwitch()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_coalesce_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            const string m1SlowUnc = @"\\192.0.2.50\m1\MES_1.log";
            string m1LocalFallback = Path.Combine(tempRoot, "M1", "MES_1.log");
            string m2Local = Path.Combine(tempRoot, "M2", "MES_1.log");
            Directory.CreateDirectory(Path.GetDirectoryName(m1LocalFallback)!);
            Directory.CreateDirectory(Path.GetDirectoryName(m2Local)!);

            WriteSampleMesLog(m1LocalFallback, new[]
            {
                ("P1_1", 300.0, "OK"),
                ("P1_2", 302.0, "OK"),
                ("P1_3", 304.0, "OK"),
                ("P1_4", 306.0, "OK")
            });
            WriteSampleMesLog(m2Local, new[]
            {
                ("P2_1", 350.0, "OK"),
                ("P2_2", 352.0, "OK"),
                ("P2_3", 354.0, "OK"),
                ("P2_4", 356.0, "OK")
            });

            string m1Otp = Path.Combine(tempRoot, "M1", "OTP_1.log");
            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": {{System.Text.Json.JsonSerializer.Serialize(m1Otp)}},
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(m1SlowUnc)}}
                },
                {
                  "name": "Machine 2",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(m2Local)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            int m1ProbeCanceled = 0;

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService
            {
                OnTestHostReachableAsync = async (path, _, token) =>
                {
                    if (!string.IsNullOrEmpty(path) && path.Contains("192.0.2.50", StringComparison.OrdinalIgnoreCase))
                    {
                        gateEntered.TrySetResult();
                        try
                        {
                            return await releaseGate.Task.WaitAsync(token).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (token.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref m1ProbeCanceled);
                            return false;
                        }
                    }

                    return true;
                }
            };

            using var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 });

            for (int i = 1; i <= 4; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord { Index = i - 1, Source = "Machine 1", PanelName = $"P1_{i}" });
                vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 3 + i, Source = "Machine 2", PanelName = $"P2_{i}" });
            }
            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.SelectedTabIndex = 2;
                vm.Chart.SelectedSpcParameter = "White_lv";
                vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                return 0;
            });

            //Wait for initial All-Machines probe to enter M1's slow UNC check, then supersede with M1 only,
            // Wait — verify coalescing first: clear initial load, then test SelectedSourceIndex = 1 coalescing:
            releaseGate.TrySetResult(false);
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            gateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            releaseGate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            m1ProbeCanceled = 0;
            camSpy.Reset();
            mesSpy.Reset();
            vm.InvalidateSpcMesCache(clearRecords: true);

            // Part A: SelectedSourceIndex = 1 fires TriggerSpcMesLoadIfNeeded both during ApplyFilter()
            // and immediately afterward while _spcMesCacheStale == true. Both must coalesce into ONE task.
            Task t1 = null!;
            Task t2 = null!;
            RunOnStaThread(() =>
            {
                vm.SelectedSourceIndex = 1;
                t1 = vm.WaitForActiveSpcMesLoadAsync();
                t2 = vm.RefreshSpcMesRecordsAsync(vm.DisplayState.ScopeUnits);
                return 0;
            });

            await gateEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Same(t1, t2);
            Assert.Equal(0, Volatile.Read(ref m1ProbeCanceled));
            Assert.Single(camSpy.HostReachabilityChecks);

            // Part B: Switching to Machine 2 while Machine 1's slow probe is still active cancels M1 cleanly and loads M2.
            RunOnStaThread(() =>
            {
                vm.SelectedSourceIndex = 2;
                return 0;
            });

            await t1.WaitAsync(TimeSpan.FromSeconds(5));
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(Volatile.Read(ref m1ProbeCanceled) >= 1, "Superseded in-flight M1 SPC load should have been canceled.");
            Assert.DoesNotContain(mesSpy.ExtractCalls, c => string.Equals(c.Path, m1LocalFallback, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(mesSpy.ExtractCalls, c => string.Equals(c.Path, m2Local, StringComparison.OrdinalIgnoreCase));

            var m2Snapshot = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(m2Snapshot.SpcData);
            Assert.Equal(4, m2Snapshot.SpcData!.Metrics.TotalCount);
            Assert.Equal(353.0, m2Snapshot.SpcData.Metrics.Mean, precision: 2);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesCache_TimeoutAndTransientException_DoNotPoisonWarmCache_AndKeepStaleForRetry()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_timeout_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            string m1Local = Path.Combine(tempRoot, "M1", "MES_1.log");
            const string m1SlowUnc = @"\\192.0.2.51\slow\MES_1.log";
            Directory.CreateDirectory(Path.GetDirectoryName(m1Local)!);

            WriteSampleMesLog(m1Local, new[]
            {
                ("P1_1", 310.0, "OK"),
                ("P1_2", 312.0, "OK"),
                ("P1_3", 314.0, "OK"),
                ("P1_4", 316.0, "OK")
            });

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(m1Local)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            bool delayUnc = false;
            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService
            {
                OnTestHostReachableAsync = async (path, _, token) =>
                {
                    if (delayUnc && !string.IsNullOrEmpty(path) && path.Contains("192.0.2.51", StringComparison.OrdinalIgnoreCase))
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    }
                    return false;
                }
            };

            using var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 150, ImageSearchTimeoutSec = 1 });

            for (int i = 1; i <= 6; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord { Index = i - 1, Source = "Machine 1", PanelName = $"P1_{i}" });
            }
            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.SelectedTabIndex = 2;
                vm.Chart.SelectedSpcParameter = "White_lv";
                vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                return 0;
            });
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            var warm1 = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(warm1.SpcData);
            Assert.Equal(4, warm1.SpcData!.Metrics.TotalCount);
            Assert.Equal(313.0, warm1.SpcData.Metrics.Mean, precision: 2);
            Assert.False(vm.IsSpcMesCacheStale());

            // 1. Transient extraction failure after soft invalidation must NOT wipe warm cache and MUST leave cache stale.
            WriteSampleMesLog(m1Local, new[]
            {
                ("P1_1", 310.0, "OK"),
                ("P1_2", 312.0, "OK"),
                ("P1_3", 314.0, "OK"),
                ("P1_4", 316.0, "OK"),
                ("P1_5", 322.0, "OK"),
                ("P1_6", 326.0, "OK")
            });
            mesSpy.ThrowOnce = new IOException("Transient MES read failure");
            vm.InvalidateSpcMesCache(clearRecords: false);
            await vm.RefreshSpcMesRecordsAsync(vm.DisplayState.ScopeUnits).WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(vm.IsSpcMesCacheStale());
            var preservedAfterErr = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(preservedAfterErr.SpcData);
            Assert.Equal(4, preservedAfterErr.SpcData!.Metrics.TotalCount);
            Assert.Equal(313.0, preservedAfterErr.SpcData.Metrics.Mean, precision: 2);

            // 2. Actual timeout inside BackgroundMonitorService bulk extraction must also leave cache stale without wiping prior data.
            delayUnc = true;
            var timedOutResult = await new BackgroundMonitorService(
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 150, ImageSearchTimeoutSec = 1 })
                .ExtractRecentMesRecordsDetailedAsync([[m1SlowUnc]], timeoutSec: 1)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(timedOutResult.Completed);

            // 3. Subsequent retry once transient failure clears updates cache to all 6 records.
            delayUnc = false;
            await vm.RefreshSpcMesRecordsAsync(vm.DisplayState.ScopeUnits).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(vm.IsSpcMesCacheStale());
            var recovered = RunOnStaThread(() => vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits));
            Assert.NotNull(recovered.SpcData);
            Assert.Equal(6, recovered.SpcData!.Metrics.TotalCount);
            Assert.Equal(316.67, recovered.SpcData.Metrics.Mean, precision: 2);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void BuildSpcHistogramSnapshotForScope_WhenGroupFilterMatchesZeroUnits_ReturnsEmptyWithoutLeakingCachedMesRecords()
    {
        var cachedRecords = new[]
        {
            new MesPanelDetailRecord { PanelName = "PB_1", OtpDefectValue = "[White_lv=315.0,opticsresult=OK]" },
            new MesPanelDetailRecord { PanelName = "PB_2", OtpDefectValue = "[White_lv=317.0,opticsresult=OK]" },
            new MesPanelDetailRecord { PanelName = "PB_3", OtpDefectValue = "[White_lv=319.0,opticsresult=OK]" },
            new MesPanelDetailRecord { PanelName = "PB_4", OtpDefectValue = "[White_lv=321.0,opticsresult=OK]" }
        };

        var chart = new ChartViewModel
        {
            DisplayMode = ChartDisplayMode.SpcHistogram,
            SelectedSpcParameter = "White_lv",
            MesRecordsProvider = _ => cachedRecords
        };

        var scopeUnits = new[]
        {
            new UnitRecord { Source = "M1", Pmnt = "PMNT-01B", PanelName = "PB_1" },
            new UnitRecord { Source = "M1", Pmnt = "PMNT-02B", PanelName = "PB_2" },
            new UnitRecord { Source = "M1", Pmnt = "PMNT-03B", PanelName = "PB_3" },
            new UnitRecord { Source = "M1", Pmnt = "PMNT-04B", PanelName = "PB_4" }
        };

        chart.GroupFilter = PmntGroupFilter.A;
        var emptyGroupSnapshot = chart.BuildRenderSnapshot(scopeUnits);
        Assert.NotNull(emptyGroupSnapshot.SpcData);
        Assert.Equal(0, emptyGroupSnapshot.SpcData!.Metrics.TotalCount);
        Assert.True(emptyGroupSnapshot.IsEmpty);

        chart.GroupFilter = PmntGroupFilter.B;
        var matchingGroupSnapshot = chart.BuildRenderSnapshot(scopeUnits);
        Assert.NotNull(matchingGroupSnapshot.SpcData);
        Assert.Equal(4, matchingGroupSnapshot.SpcData!.Metrics.TotalCount);
        Assert.Equal(318.0, matchingGroupSnapshot.SpcData.Metrics.Mean, precision: 2);
    }

    [Fact]
    public async Task SpcMesCache_RepeatedRendersAreInMemoryO1_AndDisposeCancelsInflightLoad()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_o1_dispose_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));

        try
        {
            const string hangingUnc = @"\\192.0.2.52\hang\MES_1.log";
            string localMes = Path.Combine(tempRoot, "M1", "MES_1.log");
            Directory.CreateDirectory(Path.GetDirectoryName(localMes)!);
            WriteSampleMesLog(localMes, new[]
            {
                ("P9_1", 320.0, "OK"),
                ("P9_2", 322.0, "OK"),
                ("P9_3", 324.0, "OK"),
                ("P9_4", 326.0, "OK")
            });

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(localMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();
            var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            int loadCanceledOnDispose = 0;

            using (var vm = new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 }))
            {
                for (int i = 0; i < 1000; i++)
                {
                    vm.DisplayState.AllUnits.Add(new UnitRecord
                    {
                        Index = i,
                        Source = "Machine 1",
                        PanelName = i < 4 ? $"P9_{i + 1}" : $"P9_OTHER_{i}",
                        Pmnt = i % 2 == 0 ? "PMNT-A1" : "PMNT-A2",
                        Timestamp = new DateTime(2026, 11, 15, 12, 0, 0).AddSeconds(i)
                    });
                }

                RunOnStaThread(() =>
                {
                    vm.ApplyFilter();
                    vm.SelectedTabIndex = 2;
                    vm.Chart.SelectedSpcParameter = "White_lv";
                    vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                    return 0;
                });

                await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
                mesSpy.Reset();
                camSpy.Reset();

                // Repeated BuildRenderSnapshot on the UI thread (zoom/pan/parameter switches) must run strictly in-memory O(1) with zero I/O.
                RunOnStaThread(() =>
                {
                    for (int iter = 0; iter < 50; iter++)
                    {
                        var snap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                        Assert.NotNull(snap.SpcData);
                        Assert.Equal(4, snap.SpcData!.Metrics.TotalCount);
                    }
                    return 0;
                });
                Assert.Empty(mesSpy.ExtractCalls);
                Assert.Empty(camSpy.HostReachabilityChecks);

                // Now start an in-flight slow UNC probe and verify Dispose() cancels it immediately instead of leaking/hanging.
                camSpy.OnTestHostReachableAsync = async (_, _, token) =>
                {
                    probeEntered.TrySetResult();
                    try
                    {
                        await Task.Delay(TimeSpan.FromSeconds(10), token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        Interlocked.Increment(ref loadCanceledOnDispose);
                    }
                    return false;
                };

                var inflightTask = RunOnStaThread(() =>
                {
                    vm.MachineRows[0].MesLogPath = hangingUnc;
                    return vm.WaitForActiveSpcMesLoadAsync();
                });
                await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

                vm.Dispose();
                await inflightTask.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(Volatile.Read(ref loadCanceledOnDispose) >= 1, "Dispose() must cancel any in-flight SPC MES load.");
            }
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public void ExtractPanelRecordsInWindow_ReadsRotatedAndOldOffsetsWithinSelectedTimeRange()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_window_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);
        try
        {
            string activeMes = Path.Combine(tempRoot, "MES_1.log");
            string rotatedMes = Path.Combine(tempRoot, "MES_1.1.log");

            // Archive file with morning + afternoon blocks on 2026-11-10 plus a bare <TRANSACTIONSTARTTIME> block
            var archiveSb = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                AppendSampleMesBlock(archiveSb, new DateTime(2026, 11, 10, 9, 10, i), $"OLD_AM_{i}", 310.0 + i);
            }
            AppendSampleMesBlock(archiveSb, new DateTime(2026, 11, 10, 15, 30, 0), "OLD_PM_0", 350.0);
            archiveSb.AppendLine("<MESSAGE>");
            archiveSb.AppendLine("\t<HEADER><TRANSACTIONSTARTTIME>20261110101500</TRANSACTIONSTARTTIME></HEADER>");
            archiveSb.AppendLine("\t<BODY>");
            archiveSb.AppendLine("\t\t<PANELNAME>OLD_TX_AM</PANELNAME>");
            archiveSb.AppendLine("\t\t<OTPDEFECTVALUE>[White_lv=325.000,opticsresult=OK]</OTPDEFECTVALUE>");
            archiveSb.AppendLine("\t</BODY>");
            archiveSb.AppendLine("</MESSAGE>");
            File.WriteAllText(rotatedMes, archiveSb.ToString(), new UTF8Encoding(false));
            File.SetLastWriteTime(rotatedMes, new DateTime(2026, 11, 10, 16, 0, 0));

            // Active file > 12 MB where early 2026-11-12 blocks live beyond the 8 MB tail AND cross the 4 MB chunk boundary
            var activeSb = new StringBuilder(14 * 1024 * 1024);
            AppendSampleMesBlock(activeSb, new DateTime(2026, 11, 12, 8, 5, 0), "DEEP_NOV12_A", 330.0);
            string fillerLine = new string('X', 200);
            int padIterations = 0;
            while (activeSb.Length < (4 * 1024 * 1024) - 180)
            {
                activeSb.Append("[2026-11-12 08:06:00.000] [info] pad ").Append(padIterations++).Append(' ').AppendLine(fillerLine);
            }
            AppendSampleMesBlock(activeSb, new DateTime(2026, 11, 12, 8, 7, 0), "CHUNK_SPLIT_NOV12", 332.0);
            while (activeSb.Length < 10 * 1024 * 1024)
            {
                activeSb.Append("[2026-11-15 09:00:00.000] [info] heartbeat ").Append(padIterations++).Append(' ').AppendLine(fillerLine);
            }
            AppendSampleMesBlock(activeSb, new DateTime(2026, 11, 15, 11, 0, 0), "TODAY_NOV15", 340.0);
            File.WriteAllText(activeMes, activeSb.ToString(), new UTF8Encoding(false));

            var service = new MesDetailService();

            // Repro baseline: recent-only 8 MB tail extractor misses both 2026-11-10 archive and >8 MB deep 2026-11-12 offset
            var recentOnly = service.ExtractRecentPanelRecords(activeMes, maxBytes: 8 * 1024 * 1024);
            Assert.DoesNotContain(recentOnly, r => r.PanelName.StartsWith("OLD_", StringComparison.Ordinal));
            Assert.DoesNotContain(recentOnly, r => r.PanelName == "DEEP_NOV12_A");
            Assert.Contains(recentOnly, r => r.PanelName == "TODAY_NOV15");

            // Morning window on 2026-11-10 hits rotated archive + TX-time block, excluding afternoon & later days
            var morningNov10 = service.ExtractPanelRecordsInWindow(
                activeMes,
                new DateTime(2026, 11, 10, 8, 0, 0),
                new DateTime(2026, 11, 10, 12, 0, 0));
            var morningPanels = morningNov10.Select(r => r.PanelName).ToList();
            Assert.Equal(5, morningPanels.Count);
            Assert.Contains("OLD_TX_AM", morningPanels);
            Assert.DoesNotContain("OLD_PM_0", morningPanels);
            Assert.DoesNotContain("TODAY_NOV15", morningPanels);

            // 2026-11-12 window hits deep offset AND 4 MB chunk-boundary split record
            var nov12 = service.ExtractPanelRecordsInWindow(
                activeMes,
                new DateTime(2026, 11, 12, 0, 0, 0),
                new DateTime(2026, 11, 12, 23, 59, 59));
            Assert.Equal(["DEEP_NOV12_A", "CHUNK_SPLIT_NOV12"], nov12.Select(r => r.PanelName).ToArray());

            // Inverted date window returns empty cleanly
            var inverted = service.ExtractPanelRecordsInWindow(
                activeMes,
                new DateTime(2026, 11, 15, 0, 0, 0),
                new DateTime(2026, 11, 10, 0, 0, 0));
            Assert.Empty(inverted);
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesCache_DateAndHourFilterChanges_ReloadRotatedWindowAndRenderHistogram()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_vmdate_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));
        try
        {
            string logDir = Path.Combine(tempRoot, "M1");
            Directory.CreateDirectory(logDir);
            string activeMes = Path.Combine(logDir, "MES_1.log");
            string rotatedMes = Path.Combine(logDir, "MES_1.1.log");

            var oldSb = new StringBuilder();
            for (int i = 0; i < 4; i++)
            {
                AppendSampleMesBlock(oldSb, new DateTime(2026, 11, 10, 9, i, 0), $"NOV10_AM_{i}", 315.0 + i * 2);
            }
            for (int i = 0; i < 3; i++)
            {
                AppendSampleMesBlock(oldSb, new DateTime(2026, 11, 10, 15, i, 0), $"NOV10_PM_{i}", 340.0 + i * 2);
            }
            await File.WriteAllTextAsync(rotatedMes, oldSb.ToString(), new UTF8Encoding(false));
            File.SetLastWriteTime(rotatedMes, new DateTime(2026, 11, 10, 18, 0, 0));

            var todaySb = new StringBuilder();
            for (int i = 0; i < 3; i++)
            {
                AppendSampleMesBlock(todaySb, new DateTime(2026, 11, 15, 11, i, 0), $"NOV15_{i}", 360.0 + i);
            }
            await File.WriteAllTextAsync(activeMes, todaySb.ToString(), new UTF8Encoding(false));

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(activeMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();

            using var vm = RunOnStaThread(() => new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 }));
            vm.IsKioskMode = false;

            int idx = 0;
            for (int i = 0; i < 4; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord
                {
                    Index = idx++,
                    Source = "Machine 1",
                    PanelName = $"NOV10_AM_{i}",
                    Timestamp = new DateTime(2026, 11, 10, 9, i, 0)
                });
            }
            for (int i = 0; i < 3; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord
                {
                    Index = idx++,
                    Source = "Machine 1",
                    PanelName = $"NOV10_PM_{i}",
                    Timestamp = new DateTime(2026, 11, 10, 15, i, 0)
                });
            }
            for (int i = 0; i < 3; i++)
            {
                vm.DisplayState.AllUnits.Add(new UnitRecord
                {
                    Index = idx++,
                    Source = "Machine 1",
                    PanelName = $"NOV15_{i}",
                    Timestamp = new DateTime(2026, 11, 15, 11, i, 0)
                });
            }

            // Select an older date window (2026-11-10) on SPC tab -> must pull from rotated MES_1.1.log
            RunOnStaThread(() =>
            {
                vm.SelectedTabIndex = 2;
                vm.Chart.SelectedSpcParameter = "White_lv";
                vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                vm.StartDate = new DateTime(2026, 11, 10);
                vm.EndDate = new DateTime(2026, 11, 10);
                return 0;
            });

            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            RunOnStaThread(() =>
            {
                var snap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.NotNull(snap.SpcData);
                Assert.Equal(7, snap.SpcData!.Metrics.TotalCount);
                Assert.False(snap.IsEmpty);
                return 0;
            });

            int callsBeforeHourFilter = mesSpy.WindowExtractCalls.Count;

            // Narrow hour range (08:00..11:59) without changing date -> must trigger window reload and show only AM panels
            RunOnStaThread(() =>
            {
                vm.SelectedStartHourIndex = 8;
                vm.SelectedEndHourIndex = 11;
                return 0;
            });

            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(mesSpy.WindowExtractCalls.Count > callsBeforeHourFilter, "Hour combo changes must trigger a windowed MES reload.");

            RunOnStaThread(() =>
            {
                var snap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.NotNull(snap.SpcData);
                Assert.Equal(4, snap.SpcData!.Metrics.TotalCount);
                Assert.False(snap.IsEmpty);
                return 0;
            });
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task SpcMesCache_EmptyFilteredScopeWithLoadedUnits_ReturnsEmptyInsteadOfLeakingActiveMesTail()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_spc_empty_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));
        try
        {
            string logDir = Path.Combine(tempRoot, "M1");
            Directory.CreateDirectory(logDir);
            string activeMes = Path.Combine(logDir, "MES_1.log");

            WriteSampleMesLog(activeMes,
            [
                ("LIVE_P1", 315.0, "OK"),
                ("LIVE_P2", 320.0, "OK"),
                ("LIVE_P3", 325.0, "OK")
            ]);

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(activeMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();

            using var vm = RunOnStaThread(() => new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 }));
            vm.IsKioskMode = false;

            vm.DisplayState.AllUnits.Add(new UnitRecord
            {
                Index = 0,
                Source = "Machine 1",
                PanelName = "LIVE_P1",
                Timestamp = new DateTime(2026, 11, 15, 10, 0, 1)
            });
            vm.DisplayState.AllUnits.Add(new UnitRecord
            {
                Index = 1,
                Source = "Machine 1",
                PanelName = "LIVE_P2",
                Timestamp = new DateTime(2026, 11, 15, 10, 0, 2)
            });

            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.SelectedTabIndex = 2;
                vm.Chart.SelectedSpcParameter = "White_lv";
                vm.Chart.DisplayMode = ChartDisplayMode.SpcHistogram;
                return 0;
            });

            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // Apply panel filter matching 0 loaded units -> must return empty chart instead of leaking active MES records
            RunOnStaThread(() =>
            {
                vm.SearchPanel = "DOES_NOT_EXIST";
                vm.ApplyFilter();
                Assert.Empty(vm.DisplayState.ScopeUnits);

                var emptySnap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.True(emptySnap.IsEmpty);
                Assert.Equal(0, emptySnap.SpcData?.Metrics.TotalCount ?? 0);

                // Clearing the panel filter restores cached SPC data without needing re-extraction
                vm.SearchPanel = string.Empty;
                vm.ApplyFilter();
                var restoredSnap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.False(restoredSnap.IsEmpty);
                Assert.Equal(2, restoredSnap.SpcData!.Metrics.TotalCount);
                return 0;
            });
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task AllFourChartModes_BeforeLoadingLogs_StayEmptyAndAvoidBackgroundMesIoUntilOtpLoaded()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), $"otp_all_empty_startup_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
        Directory.CreateDirectory(Path.Combine(tempRoot, "logs"));
        try
        {
            string logDir = Path.Combine(tempRoot, "M1");
            Directory.CreateDirectory(logDir);
            string activeMes = Path.Combine(logDir, "MES_1.log");

            WriteSampleMesLog(activeMes,
            [
                ("STARTUP_P1", 315.0, "OK"),
                ("STARTUP_P2", 320.0, "OK"),
                ("STARTUP_P3", 325.0, "OK")
            ]);

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(activeMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();

            using var vm = RunOnStaThread(() => new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 }));
            vm.IsKioskMode = false;

            await vm.OnLoadedAsync();
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            // Before pressing Read Log (AllUnits.Count == 0), all 4 chart modes must be empty with zero MES I/O
            RunOnStaThread(() =>
            {
                vm.SelectedTabIndex = 2;
                foreach (var mode in new[]
                         {
                             ChartDisplayMode.CumulativeTimeline,
                             ChartDisplayMode.DefectPareto,
                             ChartDisplayMode.HourlyTrend,
                             ChartDisplayMode.SpcHistogram
                         })
                {
                    vm.Chart.DisplayMode = mode;
                    var snap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                    Assert.True(snap.IsEmpty, $"Expected chart mode {mode} to be empty before loading OTP logs.");
                }
                return 0;
            });

            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Empty(mesSpy.ExtractCalls);
            Assert.Empty(mesSpy.WindowExtractCalls);

            // Even if MES records were loaded directly earlier, ResolveMesRecordsForChartScope returns empty while AllUnits is empty
            await vm.RefreshSpcMesRecordsAsync(vm.DisplayState.ScopeUnits).WaitAsync(TimeSpan.FromSeconds(5));
            RunOnStaThread(() =>
            {
                var snap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.True(snap.IsEmpty);
                Assert.Equal(0, snap.SpcData?.Metrics.TotalCount ?? 0);
                return 0;
            });

            // Once OTP logs are loaded into AllUnits and filtered, SPC renders normally
            vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 0, Source = "Machine 1", PanelName = "STARTUP_P1" });
            vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 1, Source = "Machine 1", PanelName = "STARTUP_P2" });
            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.TriggerSpcMesLoadIfNeeded(vm.DisplayState.ScopeUnits);
                return 0;
            });
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            RunOnStaThread(() =>
            {
                var loadedSnap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.False(loadedSnap.IsEmpty);
                Assert.Equal(2, loadedSnap.SpcData!.Metrics.TotalCount);
                return 0;
            });
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
        }
    }

    [Fact]
    public async Task WallboardCarousel_SupportsSlide5SpcHistogram_CyclesAllFiveSlidesAndLoadsMesData()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "OTP_WallboardSpc_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(tempRoot, "config"));
            string logDir = Path.Combine(tempRoot, "logs");
            Directory.CreateDirectory(logDir);
            string activeMes = Path.Combine(logDir, "MES_1.log");

            WriteSampleMesLog(activeMes,
            [
                ("WB_P1", 312.5, "OK"),
                ("WB_P2", 318.0, "OK"),
                ("WB_P3", 295.0, "NG")
            ]);

            string settingsJson = $$"""
            {
              "machines": [
                {
                  "name": "Machine 1",
                  "enabled": true,
                  "logPath": "",
                  "mesLogPath": {{System.Text.Json.JsonSerializer.Serialize(activeMes)}}
                }
              ]
            }
            """;
            await File.WriteAllTextAsync(Path.Combine(tempRoot, "config", "settings.json"), settingsJson, new UTF8Encoding(false));

            var mesSpy = new SpyMesDetailService();
            var camSpy = new SpyCameraImageService();

            using var vm = RunOnStaThread(() => new MainViewModel(
                appRootDir: tempRoot,
                mesDetailService: mesSpy,
                cameraImageService: camSpy,
                tuning: new TuningOptions { LogHostTimeoutMs = 500, ImageSearchTimeoutSec = 10 }));
            await vm.OnLoadedAsync();
            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 0, Source = "Machine 1", PanelName = "WB_P1", Timestamp = new DateTime(2026, 11, 15, 10, 0, 1) });
            vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 1, Source = "Machine 1", PanelName = "WB_P2", Timestamp = new DateTime(2026, 11, 15, 10, 0, 2) });
            vm.DisplayState.AllUnits.Add(new UnitRecord { Index = 2, Source = "Machine 1", PanelName = "WB_P3", Timestamp = new DateTime(2026, 11, 15, 10, 0, 3), Errors = ["XYLV"] });

            RunOnStaThread(() =>
            {
                vm.ApplyFilter();
                vm.SelectedTabIndex = 0; // Desktop is not on Tab 2; Wallboard/Kiosk owns chart mode
                vm.IsKioskMode = true;

                Assert.Equal(0, vm.WallboardCarouselIndex);
                Assert.True(vm.IsWallboardChartSlide);
                Assert.Equal(ChartDisplayMode.DefectPareto, vm.Chart.DisplayMode);

                vm.NextCarouselSlideCommand.Execute(null);
                Assert.Equal(1, vm.WallboardCarouselIndex);
                Assert.True(vm.IsWallboardChartSlide);
                Assert.Equal(ChartDisplayMode.HourlyTrend, vm.Chart.DisplayMode);

                vm.NextCarouselSlideCommand.Execute(null);
                Assert.Equal(2, vm.WallboardCarouselIndex);
                Assert.True(vm.IsWallboardChartSlide);
                Assert.Equal(ChartDisplayMode.CumulativeTimeline, vm.Chart.DisplayMode);

                vm.NextCarouselSlideCommand.Execute(null);
                Assert.Equal(3, vm.WallboardCarouselIndex);
                Assert.False(vm.IsWallboardChartSlide);
                Assert.True(vm.IsWallboardPmntSlide);
                Assert.False(vm.IsWallboardSpcSlide);

                vm.NextCarouselSlideCommand.Execute(null);
                Assert.Equal(4, vm.WallboardCarouselIndex);
                Assert.True(vm.IsWallboardChartSlide);
                Assert.False(vm.IsWallboardPmntSlide);
                Assert.True(vm.IsWallboardSpcSlide);
                Assert.Equal(ChartDisplayMode.SpcHistogram, vm.Chart.DisplayMode);
                Assert.Contains("[5/5]", vm.WallboardCarouselTitle, StringComparison.Ordinal);

                // Wrap back to slide 0, then jump directly to Slide 5 via SelectCarouselSlideCommand
                vm.NextCarouselSlideCommand.Execute(null);
                Assert.Equal(0, vm.WallboardCarouselIndex);
                vm.SelectCarouselSlideCommand.Execute("4");
                Assert.Equal(4, vm.WallboardCarouselIndex);
                Assert.Equal(ChartDisplayMode.SpcHistogram, vm.Chart.DisplayMode);
                return 0;
            });

            await vm.WaitForActiveSpcMesLoadAsync().WaitAsync(TimeSpan.FromSeconds(5));

            RunOnStaThread(() =>
            {
                var spcSnap = vm.Chart.BuildRenderSnapshot(vm.DisplayState.ScopeUnits);
                Assert.False(spcSnap.IsEmpty);
                Assert.NotNull(spcSnap.SpcData);
                Assert.Equal(3, spcSnap.SpcData!.Metrics.TotalCount);
                vm.IsKioskMode = false;
                return 0;
            });
        }
        finally
        {
            TryDeleteDirectory(tempRoot);
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
        })
        {
            IsBackground = true
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw new AggregateException("STA test action failed", captured);
        }

        return result!;
    }

    private static void WriteSampleMesLog(string path, IEnumerable<(string PanelName, double WhiteLv, string Result)> entries)
    {
        var sb = new StringBuilder();
        int sec = 0;
        foreach (var (panelName, whiteLv, result) in entries)
        {
            sec++;
            AppendSampleMesBlock(sb, new DateTime(2026, 11, 15, 10, 0, sec), panelName, whiteLv, result);
        }
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
    }

    private static void AppendSampleMesBlock(
        StringBuilder sb,
        DateTime timestamp,
        string panelName,
        double whiteLv,
        string result = "OK")
    {
        sb.AppendLine(FormattableString.Invariant($"[{timestamp:yyyy-MM-dd HH:mm:ss.fff}] [info] [GovionLogger.cpp:110] SEND MSG:"));
        sb.AppendLine("<MESSAGE>");
        sb.AppendLine("\t<BODY>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t<PANELNAME>{panelName}</PANELNAME>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t<TESTRESULT>{result}</TESTRESULT>");
        sb.AppendLine(CultureInfo.InvariantCulture, $"\t\t<OTPDEFECTCODE>[OTP-XYLV-{result}]</OTPDEFECTCODE>");
        sb.AppendLine(FormattableString.Invariant($"\t\t<OTPDEFECTVALUE>[White_lv={whiteLv:F3},whitex=0.311,whitey=0.329,opticsresult={result}]</OTPDEFECTVALUE>"));
        sb.AppendLine("\t</BODY>");
        sb.AppendLine("</MESSAGE>");
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private sealed class SpyMesDetailService : IMesDetailService
    {
        private readonly MesDetailService _inner = new();
        private Exception? _throwOnce;

        public ConcurrentBag<(string Path, int ThreadId)> ExtractCalls { get; } = new();
        public ConcurrentBag<(string Path, DateTime? Start, DateTime? End)> WindowExtractCalls { get; } = new();

        public Exception? ThrowOnce
        {
            get => Volatile.Read(ref _throwOnce);
            set => Interlocked.Exchange(ref _throwOnce, value);
        }

        public void Reset()
        {
            ExtractCalls.Clear();
            WindowExtractCalls.Clear();
        }

        public MesParameterEvaluation EvaluateParameter(string? paramName, string? paramValue, bool groupIsNg = false) =>
            _inner.EvaluateParameter(paramName, paramValue, groupIsNg);

        public IReadOnlyList<MesParameterRow> ConvertFromOtpDefectDetail(string? otpDefectCode, string? otpDefectValue) =>
            _inner.ConvertFromOtpDefectDetail(otpDefectCode, otpDefectValue);

        public IReadOnlyList<MesPanelDetailRecord> FindMesPanelDetails(
            string mesLogPath,
            string panelName,
            int maxRotatedFiles = 5,
            long chunkBytes = 4 * 1024 * 1024,
            CancellationToken cancellationToken = default) =>
            _inner.FindMesPanelDetails(mesLogPath, panelName, maxRotatedFiles, chunkBytes, cancellationToken);

        public IReadOnlyList<MesPanelDetailRecord> ExtractRecentPanelRecords(
            string mesLogPath,
            int maxBytes = 8 * 1024 * 1024)
        {
            ExtractCalls.Add((mesLogPath, Environment.CurrentManagedThreadId));
            var pendingThrow = Interlocked.Exchange(ref _throwOnce, null);
            if (pendingThrow is not null)
            {
                throw pendingThrow;
            }
            return _inner.ExtractRecentPanelRecords(mesLogPath, maxBytes);
        }

        public IReadOnlyList<MesPanelDetailRecord> ExtractPanelRecordsInWindow(
            string mesLogPath,
            DateTime? startTime,
            DateTime? endTime,
            int maxRotatedFiles = 15,
            int maxBytes = 8 * 1024 * 1024,
            CancellationToken cancellationToken = default)
        {
            WindowExtractCalls.Add((mesLogPath, startTime, endTime));
            var pendingThrow = Interlocked.Exchange(ref _throwOnce, null);
            if (pendingThrow is not null)
            {
                throw pendingThrow;
            }
            return _inner.ExtractPanelRecordsInWindow(mesLogPath, startTime, endTime, maxRotatedFiles, maxBytes, cancellationToken);
        }
    }

    private sealed class SpyCameraImageService : ICameraImageService
    {
        public ConcurrentBag<(string? Path, int ThreadId)> HostReachabilityChecks { get; } = new();

        public Func<string?, int, CancellationToken, Task<bool>>? OnTestHostReachableAsync { get; set; }

        public void Reset() => HostReachabilityChecks.Clear();

        public Task<bool> TestHostReachableAsync(
            string? uncPath,
            int timeoutMs = 1200,
            CancellationToken cancellationToken = default)
        {
            HostReachabilityChecks.Add((uncPath, Environment.CurrentManagedThreadId));
            if (OnTestHostReachableAsync is not null)
            {
                return OnTestHostReachableAsync(uncPath, timeoutMs, cancellationToken);
            }

            if (!string.IsNullOrWhiteSpace(uncPath) &&
                uncPath.Contains("192.0.2.", StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(false);
            }

            return Task.FromResult(true);
        }

        public Task<string?> FindPanelImageFolderAsync(
            string rootPath,
            string panelName,
            string sideCode,
            DateTime? timestamp,
            bool skipHostCheck = false,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<string?>(null);

        public IReadOnlyList<CameraEndpointDescriptor> GetCameraEndpoints(OtpSettings settings) => [];

        public Task<IReadOnlyList<CameraHealthStatus>> CheckCameraEndpointsHealthAsync(
            IReadOnlyList<CameraEndpointDescriptor> endpoints,
            int timeoutMs = 600,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CameraHealthStatus>>([]);
    }
}
