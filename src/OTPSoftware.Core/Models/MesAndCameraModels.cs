namespace OTPSoftware.Core.Models;

/// <summary>
/// Evaluation result for a single optical measurement parameter against factory specifications.
/// </summary>
public readonly record struct MesParameterEvaluation(
    string Spec,
    string StatusText,
    MesEvaluationStatus EvaluationStatus
);

/// <summary>
/// Decomposed measurement row from <OTPDEFECTCODE> and <OTPDEFECTVALUE>.
/// </summary>
public sealed record MesParameterRow(
    int Index,
    string DefectCodeGroup,
    string ParamName,
    string ParamValue,
    string Spec,
    string StatusText,
    MesEvaluationStatus EvaluationStatus
);

/// <summary>
/// Parsed MES transaction record for a specific panel from MES_1.log (or rotated backup logs).
/// </summary>
public sealed record MesPanelDetailRecord
{
    public required string PanelName { get; init; }
    public string LogTime { get; init; } = string.Empty;
    public string TestResult { get; init; } = string.Empty;
    public string MesResult { get; init; } = "Successful";
    public string RoiLocation { get; init; } = string.Empty;
    public string DefectCodes { get; init; } = string.Empty;
    public string OtpDefectCode { get; init; } = string.Empty;
    public string OtpDefectValue { get; init; } = string.Empty;
    public string SourceFile { get; init; } = string.Empty;
    public IReadOnlyList<MesParameterRow> Parameters { get; init; } = [];
}

/// <summary>
/// Resolved camera endpoint metadata extracted from machine settings.
/// </summary>
public sealed record CameraEndpointDescriptor(
    string Code,
    string Machine,
    string Root,
    string Host,
    string ShortIp,
    string Label
);

/// <summary>
/// Live reachability status for a camera SMB share endpoint.
/// </summary>
public sealed record CameraHealthStatus
{
    public required string Code { get; init; }
    public required string Machine { get; init; }
    public required string Root { get; init; }
    public required string Host { get; init; }
    public required string Label { get; init; }
    public bool Online { get; init; }
    public string ToolTip => $"Camera {Code} ({Machine}): {(Online ? "Online" : "Offline")}\nPath: {Root}";
}

