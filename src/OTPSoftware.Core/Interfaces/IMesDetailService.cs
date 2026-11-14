using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Reverse chunked stream parser for MES_1.log XML messages and optical measurement specification evaluator.
/// </summary>
public interface IMesDetailService
{
    MesParameterEvaluation EvaluateParameter(string? paramName, string? paramValue, bool groupIsNg = false);
    IReadOnlyList<MesParameterRow> ConvertFromOtpDefectDetail(string? otpDefectCode, string? otpDefectValue);
    IReadOnlyList<MesPanelDetailRecord> FindMesPanelDetails(
        string mesLogPath,
        string panelName,
        int maxRotatedFiles = 5,
        long chunkBytes = 4 * 1024 * 1024,
        CancellationToken cancellationToken = default);
}

