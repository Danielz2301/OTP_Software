namespace OTPSoftware.Tests;

using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

public sealed class CacheIntegrationTests : IDisposable
{
    private readonly string _tempDir;

    public CacheIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "CacheTests_" + Guid.NewGuid().ToString("N"));
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
    public async Task CameraImageService_CachesValidFolder_AndSkipsEmptyResults()
    {
        var service = new CameraImageService();
        string rootPath = Path.Combine(_tempDir, "CameraRoot");
        string targetPanel = "TL699001";
        string targetSide = "A1";

        // Create the expected image folder
        string targetDir = Path.Combine(rootPath, $"OK_{targetPanel}_1112100000_{targetSide}");
        Directory.CreateDirectory(targetDir);

        Assert.Equal(0, service.ImageFolderCache.Count);

        // 1. First search -> should find and cache
        var found1 = await service.FindPanelImageFolderAsync(
            rootPath,
            targetPanel,
            targetSide,
            timestamp: new DateTime(2026, 11, 12, 10, 0, 0),
            skipHostCheck: true);

        Assert.NotNull(found1);
        Assert.Equal(targetDir, found1);
        Assert.Equal(1, service.ImageFolderCache.Count);

        // 2. Second search -> should return from cache
        var found2 = await service.FindPanelImageFolderAsync(
            rootPath,
            targetPanel,
            targetSide,
            timestamp: new DateTime(2026, 11, 12, 10, 0, 0),
            skipHostCheck: true);

        Assert.Equal(found1, found2);
        Assert.Equal(1, service.ImageFolderCache.Count);

        // 3. Search for a non-existent panel -> returns null and does NOT cache empty result
        var nonExistent = await service.FindPanelImageFolderAsync(
            rootPath,
            "NON_EXISTENT_PANEL",
            targetSide,
            timestamp: null,
            skipHostCheck: true);

        Assert.Null(nonExistent);
        // Cache count should still be 1 (empty result not cached)
        Assert.Equal(1, service.ImageFolderCache.Count);
    }

    [Fact]
    public void MesDetailService_CachesParsedResults_AndLimitsCapacity()
    {
        var service = new MesDetailService();
        string mesLogPath = Path.Combine(_tempDir, "MES_1.log");

        string sample = """
        [2026-11-12 10:00:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
            <BODY>
                <PANELNAME>TL699001</PANELNAME>
                <TESTRESULT>OK</TESTRESULT>
                <OTPDEFECTCODE>[OTP-XYLV-OK]</OTPDEFECTCODE>
                <OTPDEFECTVALUE>[White_lv=350.500,opticsresult=OK]</OTPDEFECTVALUE>
            </BODY>
        </MESSAGE>
        [2026-11-12 10:01:00.000] [info] [GovionLogger.cpp:110] SEND MSG:
        <MESSAGE>
            <BODY>
                <PANELNAME>TL699002</PANELNAME>
                <TESTRESULT>NG</TESTRESULT>
                <OTPDEFECTCODE>[OTP-XYLV-NG]</OTPDEFECTCODE>
                <OTPDEFECTVALUE>[White_lv=280.000,opticsresult=NG]</OTPDEFECTVALUE>
            </BODY>
        </MESSAGE>
        """;
        File.WriteAllText(mesLogPath, sample, new UTF8Encoding(false));

        Assert.Equal(0, service.Cache.Count);

        // 1. Search TL699001 -> found and cached
        var res1 = service.FindMesPanelDetails(mesLogPath, "TL699001");
        Assert.NotEmpty(res1);
        Assert.Equal(1, service.Cache.Count);

        // 2. Search TL699001 again -> returns cached
        var res2 = service.FindMesPanelDetails(mesLogPath, "TL699001");
        Assert.Same(res1, res2);

        // 3. Search non-existent panel -> empty results, should NOT cache
        var resEmpty = service.FindMesPanelDetails(mesLogPath, "TL_DOES_NOT_EXIST");
        Assert.Empty(resEmpty);
        Assert.Equal(1, service.Cache.Count);

        // 4. Verify capacity limit (100)
        Assert.Equal(100, service.Cache.Capacity);
    }
}
