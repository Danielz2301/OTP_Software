namespace OTPSoftware.Tests;

using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Helpers;
using OTPSoftware.Core.Services;
using Xunit;

public sealed class AppOptimizationRegressionTests
{
    private static readonly XNamespace XamlNs = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XamlXNs = "http://schemas.microsoft.com/winfx/2006/xaml";

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
    public void Optimization01_InfiniteStoryboards_CapDesiredFrameRateToTwentyFps()
    {
        var docs = new[]
        {
            XDocument.Load(ResolveAppFilePath("App.xaml")),
            XDocument.Load(ResolveAppFilePath("MainWindow.xaml")),
            XDocument.Load(ResolveAppFilePath("Views", "MesDetailWindow.xaml"))
        };

        var infiniteStoryboards = docs
            .SelectMany(d => d.Descendants(XamlNs + "Storyboard"))
            .Where(s => string.Equals((string?)s.Attribute("RepeatBehavior"), "Forever", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.True(infiniteStoryboards.Count >= 6, $"Expected at least 6 RepeatBehavior='Forever' Storyboards, found {infiniteStoryboards.Count}.");
        Assert.All(infiniteStoryboards, sb =>
        {
            string? fps = (string?)sb.Attribute("Timeline.DesiredFrameRate");
            Assert.Equal("20", fps);
        });
    }

    [Fact]
    public void Optimization02_KpiCardsAndMesDetailsActionColumn_HaveFlattenedVisualTree()
    {
        var mainDoc = XDocument.Load(ResolveAppFilePath("MainWindow.xaml"));

        // All 5 KPI cards (identified by their label x:Name or DefectDetails grid) must avoid nested DockPanel wrappers
        string[] kpiTextNames = ["lblTotalItems", "lblOkItems", "lblNgItems", "lblNgRate"];
        foreach (string tbName in kpiTextNames)
        {
            var tb = mainDoc.Descendants(XamlNs + "TextBlock")
                .FirstOrDefault(t => string.Equals((string?)t.Attribute(XamlXNs + "Name"), tbName, StringComparison.Ordinal));
            Assert.NotNull(tb);

            var cardGrid = tb!.Ancestors(XamlNs + "Grid").FirstOrDefault();
            Assert.NotNull(cardGrid);
            Assert.Empty(cardGrid!.Descendants(XamlNs + "DockPanel"));
            Assert.True(cardGrid.Elements(XamlNs + "Grid.ColumnDefinitions").Any(), $"{tbName} card should use flat 2-column Grid");
        }

        // Card 5 (OTP DEFECT DETAILS) also uses flat Grid without DockPanel
        var defectDetailsHeader = mainDoc.Descendants(XamlNs + "TextBlock")
            .FirstOrDefault(t => string.Equals((string?)t.Attribute("Text"), "{DynamicResource Kpi_DefectDetails}", StringComparison.Ordinal));
        Assert.NotNull(defectDetailsHeader);
        var card5Grid = defectDetailsHeader!.Ancestors(XamlNs + "Grid").FirstOrDefault();
        Assert.NotNull(card5Grid);
        Assert.Empty(card5Grid!.Descendants(XamlNs + "DockPanel"));
        Assert.True(card5Grid.Elements(XamlNs + "Grid.ColumnDefinitions").Any());

        // DataGrid MES Details column button uses flat Content without inner StackPanel or ancestor-binding Path
        var viewDetailsButton = mainDoc.Descendants(XamlNs + "Button")
            .FirstOrDefault(b => string.Equals((string?)b.Attribute("ToolTip"), "{DynamicResource Col_ViewDetailsTooltip}", StringComparison.Ordinal));
        Assert.NotNull(viewDetailsButton);
        Assert.Equal("{DynamicResource Col_ViewDetailsBtn}", (string?)viewDetailsButton!.Attribute("Content"));
        Assert.Empty(viewDetailsButton.Descendants(XamlNs + "StackPanel"));
        Assert.Empty(viewDetailsButton.Descendants(XamlNs + "Path"));
    }

    [Fact]
    public void Optimization03_SecurityPathValidator_IsPublicSingleSourceAndMatchesServiceWrappers()
    {
        Assert.Equal("192.168.3.203", SecurityPathValidator.ExtractUncServerName("//192.168.3.203/share/LOG"));
        Assert.True(SecurityPathValidator.IsSafeIdentifierToken("TL699044W0CF"));
        Assert.False(SecurityPathValidator.IsSafeIdentifierToken(@"..\secret"));

        string[] samplePaths =
        [
            @"C:\AOI\Images\Panel01",
            @"\\192.168.10.25\photos\day1",
            "//10.1.2.3/aoi/share",
            @"\\fileserver01\cam_a1\img",
            @"\\8.8.8.8\share",
            @"\\attacker.example.com\c$",
            @"\\.\pipe\otp_test",
            @"\\?\C:\Windows",
            "//./pipe/otp_test",
            "//?/C:/Windows",
            @"relative\folder"
        ];

        string[] allowedRoots = [@"\\fileserver01\cam_a1"];

        foreach (string path in samplePaths)
        {
            Assert.Equal(
                SecurityPathValidator.IsUncLikePath(path),
                BackgroundMonitorService.IsUncLikePath(path));

            Assert.Equal(
                SecurityPathValidator.IsTrustedFactoryPath(path, allowedRoots),
                BackgroundMonitorService.IsTrustedFactoryPath(path, allowedRoots));
        }

        Assert.True(SecurityPathValidator.IsTrustedFactoryPath(@"\\fileserver01\cam_a1\img", allowedRoots));
        Assert.False(SecurityPathValidator.IsTrustedFactoryPath(@"\\other\cam_a1\img", allowedRoots));

        Assert.True(SecurityPathValidator.IsPathWithinOrEqualRoot(@"D:\Cam", @"D:\Cam\Sub"));
        Assert.Equal(
            SecurityPathValidator.IsPathWithinOrEqualRoot(@"D:\Cam", @"D:\Cam\..\Secret"),
            BackgroundMonitorService.IsPathWithinOrEqualRoot(@"D:\Cam", @"D:\Cam\..\Secret"));
    }

    [Fact]
    public void Optimization01_SearchValuesDefectDelimitersAndUnsafePathChars_ParseAccurately()
    {
        const string payload = "<OTPDEFECTVALUE>[ , | [White_lv = 318.75 nits] | ; contrast:1420.5 ; flk=1.85% ; whitex : 0.3125 ]</OTPDEFECTVALUE>";

        Assert.True(StatisticsService.TryExtractParameterFromDefectValue(payload, "White_lv", default, requirePositive: true, out double whiteLv));
        Assert.Equal(318.75, whiteLv, precision: 4);

        Assert.True(StatisticsService.TryExtractParameterFromDefectValue(payload, "contrast", default, requirePositive: true, out double contrast));
        Assert.Equal(1420.5, contrast, precision: 4);

        Assert.True(StatisticsService.TryExtractParameterFromDefectValue(payload, "flk", default, requirePositive: false, out double flk));
        Assert.Equal(1.85, flk, precision: 4);

        Assert.True(StatisticsService.TryExtractParameterFromDefectValue(payload, "whitex", default, requirePositive: true, out double wx));
        Assert.Equal(0.3125, wx, precision: 4);

        Assert.False(StatisticsService.TryExtractParameterFromDefectValue(payload, "missing_param", default, requirePositive: false, out _));

        // SearchValues<char> UnsafePathChars blocks embedded quotes, null bytes, and newlines
        Assert.False(SecurityPathValidator.IsTrustedFactoryPath("C:\\AOI\\log\0evil"));
        Assert.False(SecurityPathValidator.IsTrustedFactoryPath("C:\\AOI\\log\r\nevil"));
        Assert.False(SecurityPathValidator.IsTrustedFactoryPath("C:\\AOI\\log\"quote"));
    }

    [Fact]
    public void Optimization02_HexToFrozenBrushConverter_CachesFrozenBrushesAndEnforcesCap()
    {
        var converter = new OTPSoftware.App.Converters.HexToFrozenBrushConverter();

        var b1 = OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush("#10B981");
        var b2 = OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush("  #10b981  ");
        var alphaBrush = OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush("#26EF4444");

        Assert.True(b1.IsFrozen);
        Assert.Same(b1, b2);
        Assert.True(alphaBrush.IsFrozen);

        var scb = Assert.IsType<System.Windows.Media.SolidColorBrush>(alphaBrush);
        Assert.Equal(0x26, scb.Color.A);

        Assert.Same(System.Windows.Media.Brushes.Transparent, OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush(null));
        Assert.Same(System.Windows.Media.Brushes.Transparent, OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush("#ZZZ"));

        var customFallback = System.Windows.Media.Brushes.Crimson;
        Assert.Same(customFallback, OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush("not-hex", customFallback));

        // IValueConverter Convert / ConvertBack contract
        var converted = converter.Convert("#3B82F6", typeof(System.Windows.Media.Brush), null, System.Globalization.CultureInfo.InvariantCulture);
        var convertedBrush = Assert.IsType<System.Windows.Media.SolidColorBrush>(converted);
        Assert.True(convertedBrush.IsFrozen);
        Assert.Same(
            System.Windows.Data.Binding.DoNothing,
            converter.ConvertBack(convertedBrush, typeof(string), null, System.Globalization.CultureInfo.InvariantCulture));

        // Thread-safe eviction beyond MaxCachedBrushes (512)
        var ex = Record.Exception(() =>
            System.Threading.Tasks.Parallel.For(0, 640, i =>
            {
                string hex = $"#{(i & 0xFFFFFF):X6}";
                var brush = OTPSoftware.App.Converters.HexToFrozenBrushConverter.ConvertHexToBrush(hex);
                Assert.True(brush.IsFrozen);
            }));

        Assert.Null(ex);
    }
}
