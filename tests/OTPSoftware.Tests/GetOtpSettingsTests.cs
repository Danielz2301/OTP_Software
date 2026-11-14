using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

public sealed class GetOtpSettingsTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SettingsService _service;

    public GetOtpSettingsTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "GetOtpSettingsTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
        _service = new SettingsService();
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
    public void NoConfigurationFile_UsesDefaultSettingsForBothMachines()
    {
        string emptyDir = Path.Combine(_tempDir, "empty");
        var s = _service.LoadSettings(emptyDir);

        Assert.StartsWith(@"\\", s.MachinesByName["MMAPI120"].LogPath);
        Assert.StartsWith(@"\\", s.MachinesByName["MMAPI110"].LogPath);
        Assert.Equal(2, s.MachinesByName["MMAPI120"].Images.Count);
        Assert.Equal(2, s.MachinesByName["MMAPI110"].Images.Count);
        Assert.Null(s.LoadError);
    }

    [Fact]
    public void SettingsJsonFile_OverridesDefaultValues()
    {
        string overrideDir = Path.Combine(_tempDir, "override");
        string configDir = Path.Combine(overrideDir, "config");
        Directory.CreateDirectory(configDir);

        string json = """
        { "machines": { "MMAPI120": { "logPath": "\\\\10.0.0.1\\x\\OTP_1.log",
                                      "images": [ { "root": "\\\\10.0.0.2\\e\\LOG", "code": "Z1" } ] } } }
        """;
        File.WriteAllText(Path.Combine(configDir, "settings.json"), json);

        var s = _service.LoadSettings(overrideDir);
        Assert.Equal(@"\\10.0.0.1\x\OTP_1.log", s.MachinesByName["MMAPI120"].LogPath);
        Assert.Single(s.MachinesByName["MMAPI120"].Images);
        Assert.Equal(@"\\10.0.0.2\e\LOG", s.MachinesByName["MMAPI120"].Images[0].Root);
        Assert.Equal("Z1", s.MachinesByName["MMAPI120"].Images[0].Code);

        // Machines omitted from configuration file retain default values
        Assert.Equal(_service.GetDefaultSettings().MachinesByName["MMAPI110"].LogPath, s.MachinesByName["MMAPI110"].LogPath);
    }

    [Fact]
    public void MalformedJsonFile_FallsBackToDefaultsAndReportsLoadError()
    {
        string brokenDir = Path.Combine(_tempDir, "broken");
        string configDir = Path.Combine(brokenDir, "config");
        Directory.CreateDirectory(configDir);

        File.WriteAllText(Path.Combine(configDir, "settings.json"), "{ not valid json");

        var s = _service.LoadSettings(brokenDir);
        Assert.False(string.IsNullOrEmpty(s.LoadError));
        Assert.Equal(_service.GetDefaultSettings().MachinesByName["MMAPI120"].LogPath, s.MachinesByName["MMAPI120"].LogPath);
    }

    [Fact]
    public void ShippedSettingsJson_MatchesDefaultSettings()
    {
        string currentDir = AppContext.BaseDirectory;
        string? repoRoot = null;
        var dir = new DirectoryInfo(currentDir);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "settings.json")))
            {
                repoRoot = dir.FullName;
                break;
            }
            dir = dir.Parent;
        }

        Assert.NotNull(repoRoot);

        var s = _service.LoadSettings(repoRoot);
        var d = _service.GetDefaultSettings();

        Assert.Null(s.LoadError);
        foreach (string m in new[] { "MMAPI120", "MMAPI110" })
        {
            Assert.Equal(d.MachinesByName[m].LogPath, s.MachinesByName[m].LogPath);
            Assert.Equal(d.MachinesByName[m].MesLogPath, s.MachinesByName[m].MesLogPath);
            Assert.Equal(d.MachinesByName[m].Images.Count, s.MachinesByName[m].Images.Count);
            for (int i = 0; i < d.MachinesByName[m].Images.Count; i++)
            {
                Assert.Equal(d.MachinesByName[m].Images[i].Root, s.MachinesByName[m].Images[i].Root);
                Assert.Equal(d.MachinesByName[m].Images[i].Code, s.MachinesByName[m].Images[i].Code);
            }
        }
    }

    [Fact]
    public void SupportsNMachineArrayInSettingsJson()
    {
        string multiDir = Path.Combine(_tempDir, "multi");
        string configDir = Path.Combine(multiDir, "config");
        Directory.CreateDirectory(configDir);

        string json = """
        {
          "machines": [
            { "name": "MMAPI120", "logPath": "\\\\10.0.0.1\\c\\OTP_1.log" },
            { "name": "MMAPI110", "logPath": "\\\\10.0.0.2\\c\\OTP_1.log" },
            { "name": "MMAPI130", "enabled": true, "color": "#55EFC4", "bgColor": "#1E463A",
              "logPath": "\\\\10.0.0.3\\c\\OTP_1.log",
              "images": [ { "root": "\\\\10.0.0.31\\e\\LOG", "code": "C1" } ] }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(configDir, "settings.json"), json);

        var s = _service.LoadSettings(multiDir);
        Assert.Equal(3, s.Machines.Count);
        Assert.Equal("MMAPI130", s.Machines[2].Name);
        Assert.Equal(@"\\10.0.0.3\c\OTP_1.log", s.MachinesByName["MMAPI130"].LogPath);
        Assert.Equal("#55EFC4", s.MachinesByName["MMAPI130"].Color);
        Assert.Single(s.MachinesByName["MMAPI130"].Images);
        Assert.Equal("C1", s.MachinesByName["MMAPI130"].Images[0].Code);
    }

    [Fact]
    public void GetUncServerName_ExtractsServerNameFromUncPath()
    {
        Assert.Equal("192.168.3.203", SettingsService.GetUncServerName(@"\\192.168.3.203\e\LOG"));
    }

    [Fact]
    public void GetUncServerName_NonUncPathReturnsEmptyString()
    {
        Assert.Equal(string.Empty, SettingsService.GetUncServerName(@"C:\logs"));
        Assert.Equal(string.Empty, SettingsService.GetUncServerName(""));
        Assert.Equal(string.Empty, SettingsService.GetUncServerName(null));
    }

    [Fact]
    public void LoadSettings_JsonArrayFormat_DoesNotLeakDefaultMachinesAndHonorsEmptyImages()
    {
        string configDir = Path.Combine(_tempDir, "config");
        Directory.CreateDirectory(configDir);
        string settingsJson = """
        {
          "machines": [
            {
              "name": "CUSTOM1",
              "logPath": "C:\\logs\\custom.log",
              "images": []
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(configDir, "settings.json"), settingsJson);

        var settings = _service.LoadSettings(_tempDir);
        Assert.Single(settings.MachinesByName);
        Assert.True(settings.MachinesByName.ContainsKey("CUSTOM1"));
        Assert.False(settings.MachinesByName.ContainsKey("MMAPI120"));
        Assert.Empty(settings.MachinesByName["CUSTOM1"].Images);
    }
}
