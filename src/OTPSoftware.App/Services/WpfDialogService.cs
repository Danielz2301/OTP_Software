namespace OTPSoftware.App.Services;

using System.IO;
using System.Windows;
using Microsoft.Win32;
using OTPSoftware.App.ViewModels;
using OTPSoftware.App.Views;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;

/// <summary>
/// Default WPF implementation of <see cref="IDialogService"/> for desktop UI dialogs and notifications.
/// </summary>
public sealed class WpfDialogService : IDialogService
{
    public string? ShowOpenFileDialog(string title, string filter, string? initialDirectory = null)
    {
        var dlg = new OpenFileDialog
        {
            Title = title,
            Filter = filter
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dlg.InitialDirectory = initialDirectory;
        }

        bool? result = Application.Current?.MainWindow is { IsVisible: true } owner
            ? dlg.ShowDialog(owner)
            : dlg.ShowDialog();
        return result == true ? dlg.FileName : null;
    }

    public string? ShowSaveFileDialog(string title, string filter, string defaultExt, string initialFileName, string? initialDirectory = null)
    {
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            DefaultExt = defaultExt,
            FileName = initialFileName
        };
        if (!string.IsNullOrWhiteSpace(initialDirectory) && Directory.Exists(initialDirectory))
        {
            dlg.InitialDirectory = initialDirectory;
        }

        bool? result = Application.Current?.MainWindow is { IsVisible: true } owner
            ? dlg.ShowDialog(owner)
            : dlg.ShowDialog();
        return result == true ? dlg.FileName : null;
    }

    public void ShowMessage(string message, string title, DialogMessageSeverity severity = DialogMessageSeverity.Information)
    {
        if (Application.Current is null) return;

        MessageBoxImage icon = severity switch
        {
            DialogMessageSeverity.Error => MessageBoxImage.Error,
            DialogMessageSeverity.Warning => MessageBoxImage.Warning,
            _ => MessageBoxImage.Information
        };

        MessageBox.Show(message, title, MessageBoxButton.OK, icon);
    }

    public void ShowMesDetailDialog(
        DisplayRowViewModel item,
        IReadOnlyList<MesPanelDetailRecord> details,
        string mesLogPath,
        BackgroundMonitorService? backgroundMonitor = null,
        IReadOnlyList<CameraImageConfig>? cameraConfigs = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(details);
        if (details.Count == 0 || Application.Current is null) return;

        var vm = new MesDetailViewModel(item, details, mesLogPath, backgroundMonitor, cameraConfigs);
        var win = new MesDetailWindow(vm);
        if (Application.Current.MainWindow is { IsVisible: true } owner)
        {
            win.Owner = owner;
        }

        win.ShowDialog();
    }
}
