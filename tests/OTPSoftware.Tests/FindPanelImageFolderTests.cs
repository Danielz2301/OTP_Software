using System.Diagnostics;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class FindPanelImageFolderTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _logRoot;
    private readonly string _dayDir;
    private readonly string _panelDirA1;
    private readonly string _panelDirB1;
    private readonly CameraImageService _service;

    public FindPanelImageFolderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "FindPanelImageFolderTests_" + Guid.NewGuid().ToString("N"));
        _logRoot = Path.Combine(_tempDir, "LOG");
        _dayDir = Path.Combine(_logRoot, "202611", "06");
        _panelDirA1 = Path.Combine(_dayDir, "OK_TL699044W0CF_1106182351_A1");
        _panelDirB1 = Path.Combine(_dayDir, "NG_TL698881F0BC_1106180208_B1");

        Directory.CreateDirectory(_panelDirA1);
        Directory.CreateDirectory(_panelDirB1);

        _service = new CameraImageService();
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public async Task TestHostReachableAsync_ValidatesPathsCorrectly()
    {
        Assert.False(await _service.TestHostReachableAsync(string.Empty));
        Assert.True(await _service.TestHostReachableAsync(_tempDir));
        Assert.False(await _service.TestHostReachableAsync(Path.Combine(_tempDir, "khong-co")));

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        bool res = await _service.TestHostReachableAsync(@"\\192.0.2.1\share\LOG", timeoutMs: 100, cts.Token);

        Assert.False(res);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_LocatesImageDirectoryUnderStandardStructure()
    {
        var ts = new DateTime(2026, 11, 6, 18, 23, 51);
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL699044W0CF", "A1", ts);
        Assert.Equal(_panelDirA1, found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_LocatesDifferentPanelInSameDateDirectory()
    {
        var ts = new DateTime(2026, 11, 6, 18, 2, 8);
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL698881F0BC", "B1", ts);
        Assert.Equal(_panelDirB1, found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_LocatesDirectoryWithoutTimestampByScanningContainer()
    {
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL699044W0CF", "A1", null);
        Assert.Equal(_panelDirA1, found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_FastPath_MatchingTimestampPrefixFindsExactDirectory()
    {
        var ts = new DateTime(2026, 11, 6, 18, 2, 8);
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL698881F0BC", "B1", ts);
        Assert.Equal(_panelDirB1, found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_TimestampOffsetBySeconds_IsLocatedViaFallback()
    {
        var ts = new DateTime(2026, 11, 6, 18, 23, 56); // +5 seconds offset
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL699044W0CF", "A1", ts);
        Assert.Equal(_panelDirA1, found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_NonExistentPanel_ReturnsNull()
    {
        var ts = new DateTime(2026, 11, 6, 18, 23, 51);
        string? found = await _service.FindPanelImageFolderAsync(_logRoot, "TL000000X0XX", "A1", ts);
        Assert.Null(found);
    }

    [Fact]
    public async Task FindPanelImageFolderAsync_EmptyOrNonExistentRoot_ReturnsNull()
    {
        Assert.Null(await _service.FindPanelImageFolderAsync(string.Empty, "TL699044W0CF", "A1", null));
        Assert.Null(await _service.FindPanelImageFolderAsync(Path.Combine(_tempDir, "khong-co"), "TL699044W0CF", "A1", null));
    }

    [Fact]
    public async Task GetCameraEndpointsAndCheckHealth_ExtractsAndProbesCorrectly()
    {
        var settings = new SettingsService().GetDefaultSettings();
        var eps = _service.GetCameraEndpoints(settings);

        Assert.Equal(4, eps.Count);
        Assert.Equal("A1", eps[0].Code);
        Assert.Equal("A1 (.201)", eps[0].Label);
        Assert.Equal("B1", eps[2].Code);
        Assert.Equal("B1 (.203)", eps[2].Label);

        var testEps = new List<CameraEndpointDescriptor>
        {
            new("A1", "MMAPI110", _tempDir, "Local", ".Local", "A1 (Local)"),
            new("A2", "MMAPI110", Path.Combine(_tempDir, "missing_cam"), "Local", ".Local", "A2 (Local)")
        };

        var health = await _service.CheckCameraEndpointsHealthAsync(testEps, timeoutMs: 100);
        Assert.Equal(2, health.Count);
        Assert.True(health[0].Online);
        Assert.False(health[1].Online);
    }
}
