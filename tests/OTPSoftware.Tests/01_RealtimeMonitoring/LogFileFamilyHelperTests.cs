namespace OTPSoftware.Tests;

using System.Globalization;
using OTPSoftware.Core.Helpers;
using Xunit;

public sealed class LogFileFamilyHelperTests : IDisposable
{
    private readonly string _tempDir;

    public LogFileFamilyHelperTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"otp_family_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup of temp directory
        }
    }

    [Theory]
    [InlineData("MES_1.log", "MES_1", ".log", 0)]
    [InlineData("MES_1.1.log", "MES_1", ".log", 1)]
    [InlineData("MES_1.999.log", "MES_1", ".log", 999)]
    [InlineData("MES_1.0.log", "MES_1.0", ".log", 0)]
    [InlineData("MES_1.1000.log", "MES_1.1000", ".log", 0)]
    public void ParseFileFamilyIdentity_GivenActiveAndRotatedPaths_ExtractsIdentityCorrectly(
        string fileName,
        string expectedBaseName,
        string expectedExt,
        int expectedRotationIndex)
    {
        string inputPath = Path.Combine(_tempDir, fileName);

        var identity = LogFileFamilyHelper.ParseFileFamilyIdentity(inputPath);

        Assert.NotNull(identity);
        Assert.Equal(_tempDir, identity.Directory);
        Assert.Equal(expectedBaseName, identity.BaseName);
        Assert.Equal(expectedExt, identity.Extension);
        Assert.Equal(Path.Combine(_tempDir, $"{expectedBaseName}{expectedExt}"), identity.ActiveFilePath);
        Assert.Equal($"{expectedBaseName}*{expectedExt}", identity.WatcherFilter);
        Assert.Equal(expectedRotationIndex, identity.RequestedRotationIndex);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParseFileFamilyIdentity_GivenNullOrWhitespace_ReturnsNull(string? path)
    {
        Assert.Null(LogFileFamilyHelper.ParseFileFamilyIdentity(path));
    }

    [Theory]
    [InlineData("MES_1.log", true, 0)]
    [InlineData("MES_1.1.log", true, 1)]
    [InlineData("mes_1.42.log", true, 42)]
    [InlineData("MES_1.999.log", true, 999)]
    [InlineData("MES_1.0.log", false, -1)]
    [InlineData("MES_1.1000.log", false, -1)]
    [InlineData("MES_1_copy.log", false, -1)]
    [InlineData("MES_10.log", false, -1)]
    [InlineData("MES_1.bak.log", false, -1)]
    [InlineData("OTHER.1.log", false, -1)]
    public void TryParseFamilyMemberRotationIndex_GivenCandidateFileName_MatchesOnlyFamilyMembers(
        string candidateFileName,
        bool expectedMatch,
        int expectedRotationIndex)
    {
        bool matched = LogFileFamilyHelper.TryParseFamilyMemberRotationIndex(
            candidateFileName,
            baseName: "MES_1",
            extension: ".log",
            out int rotationIndex);

        Assert.Equal(expectedMatch, matched);
        if (expectedMatch)
        {
            Assert.Equal(expectedRotationIndex, rotationIndex);
        }
    }

    [Fact]
    public void ResolveActiveLogPath_GivenRotatedPathWhenActiveExists_ReturnsActiveLogPath()
    {
        string activePath = Path.Combine(_tempDir, "OTP.log");
        string rotatedPath = Path.Combine(_tempDir, "OTP.2.log");
        File.WriteAllText(activePath, "active");
        File.WriteAllText(rotatedPath, "rotated");

        string resolved = LogFileFamilyHelper.ResolveActiveLogPath(rotatedPath);

        Assert.Equal(activePath, resolved);
    }

    [Fact]
    public void ResolveLogFileFamily_GivenRotatedArchivesAndDistractors_OrdersChronologicallyAndRespectsCap()
    {
        string active = Path.Combine(_tempDir, "MES_1.log");
        string rot1 = Path.Combine(_tempDir, "MES_1.1.log");
        string rot2 = Path.Combine(_tempDir, "MES_1.2.log");
        string emptyRot3 = Path.Combine(_tempDir, "MES_1.3.log");
        string zeroSuffix = Path.Combine(_tempDir, "MES_1.0.log");
        string thousandSuffix = Path.Combine(_tempDir, "MES_1.1000.log");
        string unrelated = Path.Combine(_tempDir, "MES_1_backup.log");

        File.WriteAllText(active, "[2026-11-15 10:00:00.000] [INF] active\n");
        File.WriteAllText(rot1, "[2026-11-15 09:00:00.000] [INF] rot1\n");
        File.WriteAllText(rot2, "[2026-11-15 08:00:00.000] [INF] rot2\n");
        File.WriteAllBytes(emptyRot3, []);
        File.WriteAllText(zeroSuffix, "[2026-11-15 07:00:00.000] [INF] invalid0\n");
        File.WriteAllText(thousandSuffix, "[2026-11-15 06:00:00.000] [INF] invalid1000\n");
        File.WriteAllText(unrelated, "[2026-11-15 05:00:00.000] [INF] unrelated\n");

        // Place OS mtimes in descending order so archives without 0-byte tie keep valid ordering while 0-byte falls back to earliest mtime,
        // and deliberately swap rot1 vs rot2 mtimes to prove embedded FirstLogTimestamp drives non-empty archive order.
        var baseUtc = new DateTime(2026, 11, 14, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(emptyRot3, baseUtc);
        File.SetLastWriteTimeUtc(rot1, baseUtc.AddMinutes(10));
        File.SetLastWriteTimeUtc(rot2, baseUtc.AddMinutes(20)); // Newer OS mtime than rot1, but older embedded timestamp
        File.SetLastWriteTimeUtc(active, baseUtc.AddMinutes(30));

        var family = LogFileFamilyHelper.ResolveLogFileFamily(
            rot1,
            maxRotatedFiles: null,
            includeFirstTimestamps: true);

        Assert.NotNull(family);
        Assert.Equal([active, rot1, rot2, emptyRot3], family.NewestToOldest.Select(e => e.Path).ToArray());
        Assert.Equal([emptyRot3, rot2, rot1, active], family.OldestToNewest.Select(e => e.Path).ToArray());
        Assert.Equal(DateTime.ParseExact("2026-11-15 09:00:00.000", "yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture), family.NewestToOldest[1].FirstLogTimestamp);
        Assert.Null(family.NewestToOldest[3].FirstLogTimestamp);

        var capped = LogFileFamilyHelper.ResolveLogFileFamily(active, maxRotatedFiles: 1);
        Assert.NotNull(capped);
        Assert.Equal([active, rot2], capped.NewestToOldest.Select(e => e.Path).ToArray());
    }
}
