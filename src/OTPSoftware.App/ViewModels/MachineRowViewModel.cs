using System.IO;
using System.Windows.Input;
using OTPSoftware.App.Services;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// Bindable machine configuration row for icMachines in MainWindow.xaml.
/// Uses C# 14 <c>field</c> keyword in all property setters and notifies optional callbacks on changes.
/// </summary>
public sealed class MachineRowViewModel : ViewModelBase
{
    private readonly Action<MachineRowViewModel>? _onChanged;
    private readonly Func<MachineRowViewModel, string, Task>? _onBrowseRequested;
    private readonly IDialogService _dialogService;
    private readonly ILocalizationService _localization;

    public MachineRowViewModel(
        string name,
        bool enabled = true,
        string color = "#74B9FF",
        string bgColor = "#1B3B52",
        string logPath = "",
        string mesLogPath = "",
        Action<MachineRowViewModel>? onChanged = null,
        Func<MachineRowViewModel, string, Task>? onBrowseRequested = null,
        IDialogService? dialogService = null,
        ILocalizationService? localizationService = null)
    {
        _localization = localizationService ?? LocalizationService.Default;
        Name = name;
        Enabled = enabled;
        Color = color;
        BgColor = bgColor;
        LogPath = logPath;
        MesLogPath = mesLogPath;

        _onChanged = onChanged;
        _onBrowseRequested = onBrowseRequested;
        _dialogService = dialogService ?? new WpfDialogService();
        RefreshLocalization();

        BrowseLogCommand = new AsyncRelayCommand(ExecuteBrowseLogAsync);
    }

    public void RefreshLocalization()
    {
        EnableToolTip = _localization.Format("Tip_EnableMachineFormat", Name);
        PathToolTip = _localization.Format("Tip_OtpPathFormat", Name);
        MesPathToolTip = _localization.Format("Tip_MesPathFormat", Name);
    }

    public string Name
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (_localization is not null)
                {
                    RefreshLocalization();
                }
            }
        }
    } = string.Empty;

    public bool Enabled
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                _onChanged?.Invoke(this);
            }
        }
    } = true;

    public string Color
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = "#74B9FF";

    public string BgColor
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = "#1B3B52";

    public string LogPath
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value ?? string.Empty;
                OnPropertyChanged();
                _onChanged?.Invoke(this);
            }
        }
    } = string.Empty;

    public string MesLogPath
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value ?? string.Empty;
                OnPropertyChanged();
                _onChanged?.Invoke(this);
            }
        }
    } = string.Empty;

    public string EnableToolTip
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = string.Empty;

    public string PathToolTip
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = string.Empty;

    public string MesPathToolTip
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
            }
        }
    } = string.Empty;

    public ICommand BrowseLogCommand { get; }

    private Task ExecuteBrowseLogAsync(object? parameter)
    {
        string kind = parameter as string ?? "OTP";
        if (_onBrowseRequested is not null)
        {
            return _onBrowseRequested(this, kind);
        }

        bool isMes = string.Equals(kind, "MES", StringComparison.OrdinalIgnoreCase);
        string title = _localization.Format(isMes ? "Dlg_SelectMesTitleFormat" : "Dlg_SelectOtpTitleFormat", Name);
        string? selectedPath = _dialogService.ShowOpenFileDialog(
            title,
            "Log Files (*.log)|*.log|Text Files (*.txt)|*.*");

        if (!string.IsNullOrWhiteSpace(selectedPath))
        {
            if (isMes)
            {
                MesLogPath = selectedPath;
            }
            else
            {
                LogPath = selectedPath;
                Enabled = true;
                string? parentDir = Path.GetDirectoryName(selectedPath);
                if (!string.IsNullOrWhiteSpace(parentDir) && string.IsNullOrWhiteSpace(MesLogPath))
                {
                    MesLogPath = Path.Combine(parentDir, "MES_1.log");
                }
            }
        }

        return Task.CompletedTask;
    }
}
