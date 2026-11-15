namespace OTPSoftware.Core.Models;

/// <summary>
/// Presentation filter mode for test outcomes (0 = All, 1 = NG only, 2 = OK only).
/// </summary>
public enum UnitStatusFilter
{
    All = 0,
    NgOnly = 1,
    OkOnly = 2
}

/// <summary>
/// Dual-track fixture group filter (ALL, Track A, Track B, Other).
/// </summary>
public enum PmntGroupFilter
{
    All,
    A,
    B,
    Other
}

/// <summary>
/// Semantic evaluation outcome for MES parameter checks.
/// </summary>
public enum MesEvaluationStatus
{
    Neutral = 0,
    Ok = 1,
    Ng = 2
}

/// <summary>
/// Optical test failure categories.
/// </summary>
public enum DefectFilterType
{
    All,
    Xylv,
    PowerOn,
    PowerOff,
    Flicker
}

/// <summary>
/// Target camera side when opening inspection image folders.
/// </summary>
public enum CameraTargetSide
{
    Both,
    Left,
    Right
}

