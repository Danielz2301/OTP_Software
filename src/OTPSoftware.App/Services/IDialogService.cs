namespace OTPSoftware.App.Services;

using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;

/// <summary>
/// Severity hint for interactive UI notifications without coupling ViewModels to WPF message box enums.
/// </summary>
public enum DialogMessageSeverity
{
    Information = 0,
    Warning = 1,
    Error = 2
}

/// <summary>
/// Abstraction for file pickers, interactive alerts, and the MES detail modal dialog.
/// Enables unit testing ViewModels without launching real WPF windows.
/// </summary>
public interface IDialogService
{
    string? ShowOpenFileDialog(string title, string filter, string? initialDirectory = null);

    string? ShowSaveFileDialog(string title, string filter, string defaultExt, string initialFileName, string? initialDirectory = null);

    void ShowMessage(string message, string title, DialogMessageSeverity severity = DialogMessageSeverity.Information);

    void ShowMesDetailDialog(
        DisplayRowViewModel item,
        IReadOnlyList<MesPanelDetailRecord> details,
        string mesLogPath,
        BackgroundMonitorService? backgroundMonitor = null,
        IReadOnlyList<CameraImageConfig>? cameraConfigs = null);
}
