using System.Text.Json.Serialization;

namespace OTPSoftware.Core.Models;

/// <summary>
/// Camera SMB network share configuration for a single side (e.g. A1/A2 or B1/B2).
/// </summary>
public sealed record CameraImageConfig(
    [property: JsonPropertyName("root")] string Root,
    [property: JsonPropertyName("code")] string Code
);

/// <summary>
/// Configuration entry for a single test machine (e.g. MMAPI120, MMAPI110, or dynamic Nth machine).
/// </summary>
public sealed record MachineConfig
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("color")]
    public string Color { get; init; } = "#74B9FF";

    [JsonPropertyName("bgColor")]
    public string BgColor { get; init; } = "#1B3B52";

    [JsonPropertyName("logPath")]
    public string LogPath { get; init; } = string.Empty;

    [JsonPropertyName("mesLogPath")]
    public string MesLogPath { get; init; } = string.Empty;

    [JsonPropertyName("images")]
    public IReadOnlyList<CameraImageConfig> Images { get; init; } = [];
}

/// <summary>
/// Resolved foreground and background hex colors for a machine badge.
/// </summary>
public readonly record struct MachineVisualStyle(string Foreground, string Background);

/// <summary>
/// Configurable thresholds and timing options for the Industrial Wallboard display mode.
/// </summary>
public sealed record WallboardThresholds
{
    [JsonPropertyName("warningNgRatePercent")]
    public double WarningNgRatePercent { get; init; } = 2.0;

    [JsonPropertyName("criticalNgRatePercent")]
    public double CriticalNgRatePercent { get; init; } = 5.0;

    [JsonPropertyName("shiftTargetUnits")]
    public int ShiftTargetUnits { get; init; } = 15000;

    [JsonPropertyName("carouselIntervalSec")]
    public int CarouselIntervalSec { get; init; } = 15;
}

/// <summary>
/// Configuration options for automatic shift handover reporting and chart snapshotting.
/// </summary>
public sealed record AutoShiftExportConfig
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;

    [JsonPropertyName("dayShiftStart")]
    public string? DayShiftStart { get; init; }

    [JsonPropertyName("nightShiftStart")]
    public string? NightShiftStart { get; init; }

    [JsonPropertyName("dayShiftTime")]
    public string DayShiftTime { get; init; } = "20:30";

    [JsonPropertyName("nightShiftTime")]
    public string NightShiftTime { get; init; } = "08:30";
}

/// <summary>
/// Aggregated system configuration loaded from defaults and config/settings.json.
/// Exposes both ordered machine iteration and O(1) lookup by machine name.
/// </summary>
public sealed record OtpSettings
{
    public IReadOnlyList<MachineConfig> Machines { get; init; } = [];

    public IReadOnlyDictionary<string, MachineConfig> MachinesByName { get; init; } =
        new Dictionary<string, MachineConfig>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("wallboard")]
    public WallboardThresholds Wallboard { get; init; } = new();

    [JsonPropertyName("autoShiftExport")]
    public AutoShiftExportConfig AutoShiftExport { get; init; } = new();

    [JsonPropertyName("mesSpecs")]
    public IReadOnlyDictionary<string, MesParameterSpec>? MesSpecs { get; init; }

    public string? LoadError { get; init; }
}

/// <summary>
/// Configurable min/max specification bounds for evaluating a MES optical parameter.
/// Any omitted bound falls back to built-in optical specification defaults.
/// </summary>
public sealed record MesParameterSpec(
    [property: JsonPropertyName("min")] double? Min = null,
    [property: JsonPropertyName("max")] double? Max = null,
    [property: JsonPropertyName("spec")] string? SpecDisplay = null);

/// <summary>
/// Persisted per-machine state in config/user-state.json.
/// </summary>
public sealed record UserMachineState(
    string Name,
    string LogPath,
    string MesLogPath,
    bool Enabled
);

/// <summary>
/// Persisted UI session state in config/user-state.json.
/// </summary>
public sealed record UserState
{
    public int SelectedTab { get; init; }
    public IReadOnlyList<UserMachineState> Machines { get; init; } = [];
    public bool AutoScroll { get; init; } = true;
    public bool SoundAlert { get; init; } = true;
    public string Language { get; init; } = "en";
}

/// <summary>
/// System-wide polling, timeout, and UI threshold tuning constants.
/// Mapped 1:1 from $script:Config.Tuning in OTPSoftware.psm1.
/// </summary>
public sealed record TuningOptions
{
    public int RealtimePollMs { get; init; } = 2000;
    public int FilterDebounceMs { get; init; } = 150;
    public int ImageSearchTimeoutSec { get; init; } = 30;
    public int LogHostTimeoutMs { get; init; } = 1500;
    public int ImageHostTimeoutMs { get; init; } = 1200;
    public int SlowServerWarnSec { get; init; } = 5;
    public int MinPlausibleYear { get; init; } = 2000;
    public int ClipboardRetries { get; init; } = 5;
    public int ClipboardRetryDelayMs { get; init; } = 40;
    public int ConsecutiveNgThreshold { get; init; } = 3;
    public int DayShiftStartHour { get; init; } = 8;
    public int NightShiftStartHour { get; init; } = 20;
    public int CameraHealthPollMs { get; init; } = 15000;
    public int CameraHealthTimeoutMs { get; init; } = 600;
    public int ChartVisualThreshold { get; init; } = 150;
    public int MaxRetainedUnits { get; init; } = 30000;
    public int SlidingWindowTrimBatch { get; init; } = 2000;
    public WallboardThresholds Wallboard { get; init; } = new();
}

