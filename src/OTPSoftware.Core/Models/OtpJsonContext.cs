namespace OTPSoftware.Core.Models;

using System.Collections.Generic;
using System.Text.Json.Serialization;

/// <summary>
/// AOT/Trimming-safe JSON serialization context for configuration and user-state persistence.
/// Eliminates reflection overhead and supports System.Text.Json Source Generation.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(UserState))]
[JsonSerializable(typeof(UserMachineState))]
[JsonSerializable(typeof(WallboardThresholds))]
[JsonSerializable(typeof(AutoShiftExportConfig))]
[JsonSerializable(typeof(MesParameterSpec))]
[JsonSerializable(typeof(Dictionary<string, MesParameterSpec>))]
[JsonSerializable(typeof(OtpSettings))]
[JsonSerializable(typeof(MachineConfig))]
[JsonSerializable(typeof(MachineVisualStyle))]
[JsonSerializable(typeof(CameraImageConfig))]
[JsonSerializable(typeof(TuningOptions))]
[JsonSerializable(typeof(MesParameterEvaluation))]
[JsonSerializable(typeof(MesParameterRow))]
[JsonSerializable(typeof(MesPanelDetailRecord))]
[JsonSerializable(typeof(CameraEndpointDescriptor))]
[JsonSerializable(typeof(CameraHealthStatus))]
[JsonSerializable(typeof(PmntDefectRankItem))]
[JsonSerializable(typeof(UnitStatisticsResult))]
[JsonSerializable(typeof(ShiftWindow))]
[JsonSerializable(typeof(ShiftReportResult))]
[JsonSerializable(typeof(UnitRecord))]
[JsonSerializable(typeof(UnitFilterSpec))]
[JsonSerializable(typeof(LogTailState))]
internal sealed partial class OtpJsonContext : JsonSerializerContext
{
}

