namespace OTPSoftware.App.ViewModels;

using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;

/// <summary>
/// ViewModel for the dark-mode MES Parameter Details &amp; Optical Inspection dialog window.
/// Implements asynchronous multi-tier AOI camera image resolution (<c>piresult/AlgIntegrate.jpg</c>,
/// <c>piresult/defectPicture/*</c>, and on-demand 1:1 <c>photo/Main*.png</c>), bounded-RAM decoding,
/// Side-by-Side comparison, and 3x 500ms incomplete-write retry.
/// Uses C# 14 <c>field</c> contextual keyword in all property setters.
/// </summary>
public sealed class MesDetailViewModel : ViewModelBase, IDisposable
{
    public const double MinImageZoom = 1.0;
    public const double MaxImageZoom = 5.0;

    private readonly DisplayRowViewModel _item;
    private readonly IReadOnlyList<MesPanelDetailRecord> _details;
    private readonly ILocalizationService _localization;
    private readonly IReadOnlyList<CameraImageConfig>? _cameraConfigs;
    private List<MesParameterRow> _sortedParameters = [];
    private DispatcherTimer? _searchDebounceTimer;
    private CancellationTokenSource? _imageCts;
    private CancellationTokenSource? _oneToOneCts;
    private CameraChannelImageSet? _leftImageSet;
    private CameraChannelImageSet? _rightImageSet;
    private ImageSource? _leftStandardPreview;
    private ImageSource? _rightStandardPreview;
    private ImageSource? _leftCropPreview;
    private ImageSource? _rightCropPreview;
    private ImageSource? _leftRawHighResPreview;
    private ImageSource? _rightRawHighResPreview;
    private string _rawMesResult = string.Empty;
    private bool _hasCopiedPanel;
    private bool _hasCopiedTable;
    private bool _disposed;

    public MesDetailViewModel(
        DisplayRowViewModel item,
        IReadOnlyList<MesPanelDetailRecord> details,
        string mesLogPath,
        BackgroundMonitorService? backgroundMonitor = null,
        IReadOnlyList<CameraImageConfig>? cameraConfigs = null,
        ILocalizationService? localizationService = null)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(details);

        _item = item;
        _details = details;
        _localization = localizationService ?? LocalizationService.Default;
        _cameraConfigs = cameraConfigs;
        MesLogPath = mesLogPath ?? string.Empty;

        PanelName = item.PanelName ?? string.Empty;
        Source = item.Source ?? string.Empty;
        Pmnt = item.PMNT ?? string.Empty;
        TimeFormatted = item.TimeFormatted ?? string.Empty;
        BluName = !string.IsNullOrWhiteSpace(item.BluName) ? item.BluName : "-";
        DefectCodes = !string.IsNullOrWhiteSpace(item.Errors) ? item.Errors : "-";

        CopyButtonText = _localization.Get("MesDetail_CopyTableBtn");
        CopyPanelButtonText = _localization.Get("MesDetail_CopyPanel");
        LeftImageStatus = _localization.Get("Spot_NotConfigured");
        RightImageStatus = _localization.Get("Spot_NotConfigured");

        CopyCommand = new AsyncRelayCommand(
            CopyParameterTableAsync,
            onError: ex => CopyStatusMessage = _localization.Format("MesDetail_CopyErrorFormat", ex.Message));
        CopyPanelCommand = new AsyncRelayCommand(
            CopyPanelAsync,
            onError: ex => CopyStatusMessage = _localization.Format("MesDetail_CopyErrorFormat", ex.Message));
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke());
        ClearParameterSearchCommand = new RelayCommand(() => ParameterSearchText = string.Empty);
        SelectLeftCameraCommand = new RelayCommand(() =>
        {
            IsSideBySideMode = false;
            SelectedCameraIndex = 0;
        });
        SelectRightCameraCommand = new RelayCommand(() =>
        {
            IsSideBySideMode = false;
            SelectedCameraIndex = 1;
        });
        ZoomInImageCommand = new RelayCommand(() => ZoomImageBy(1.25));
        ZoomOutImageCommand = new RelayCommand(() => ZoomImageBy(1.0 / 1.25));
        ResetImageZoomCommand = new RelayCommand(ResetImageZoom);
        OneToOneZoomCommand = new AsyncRelayCommand(
            ActivateOneToOneInspectionAsync,
            onError: ex => ErrorLoggerService.Default.LogException(ex, "MesDetailViewModel.OneToOneZoomCommand"));
        ToggleDefectCropCommand = new RelayCommand(ToggleDefectCropMode);
        ToggleSideBySideCommand = new RelayCommand(() => IsSideBySideMode = !IsSideBySideMode);

        UpdateCameraLabels();

        if (details.Count > 1)
        {
            IsMultiRun = true;
            BuildRunItems();
            SelectedRunIndex = details.Count - 1;
        }
        else if (details.Count == 1)
        {
            IsMultiRun = false;
            ApplyEntry(details[0]);
        }

        _localization.LanguageChanged += OnLanguageChanged;
        InitializeCameraInspection(backgroundMonitor, cameraConfigs);
    }

    public event Action? RequestClose;

    public event EventHandler? ImageZoomChanged;

    public string PanelName { get; }

    public string Source { get; }

    public string Pmnt { get; }

    public string MesLogPath { get; }

    public string MesFileLabel => _localization.Format("MesDetail_MesFileFormat", MesLogPath);

    public string TimeFormatted { get; }

    public string BluName { get; }

    public string RoiLocation
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "-";

    public string DefectCodes
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "-";

    public bool IsMultiRun
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public ObservableCollection<string> RunItems
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public int SelectedRunIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (value >= 0 && value < _details.Count)
                {
                    ApplyEntry(_details[value]);
                }
            }
        }
    } = -1;

    public string LogTime
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string TestResultText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string TestResultForeground
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "#10B981";

    public ObservableCollection<MesParameterRow> Parameters
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    public string ParameterSearchText
    {
        get => field;
        set
        {
            string normalized = value ?? string.Empty;
            if (!string.Equals(field, normalized, StringComparison.Ordinal))
            {
                field = normalized;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasParameterSearchText));
                if (normalized.Length == 0 || Application.Current?.Dispatcher is null)
                {
                    ApplyParameterFilter();
                }
                else
                {
                    ScheduleParameterFilter();
                }
            }
        }
    } = string.Empty;

    public bool ShowNgOnly
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                ApplyParameterFilter();
            }
        }
    }

    public int TotalParameterCount
    {
        get => field;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParameterCountSummary));
            }
        }
    }

    public int NgParameterCount
    {
        get => field;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasNgParameters));
                OnPropertyChanged(nameof(NgFilterLabel));
            }
        }
    }

    public int FilteredParameterCount
    {
        get => field;
        private set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ParameterCountSummary));
            }
        }
    }

    public string CopyStatusMessage
    {
        get => field;
        private set
        {
            if (!string.Equals(field, value, StringComparison.Ordinal))
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCopyStatusMessage));
            }
        }
    } = string.Empty;

    public bool HasNgParameters => NgParameterCount > 0;

    public bool HasParameterSearchText => !string.IsNullOrEmpty(ParameterSearchText);

    public bool HasCopyStatusMessage => !string.IsNullOrEmpty(CopyStatusMessage);

    public string NgFilterLabel => _localization.Format("MesDetail_NgOnlyFormat", NgParameterCount);

    public string ParameterCountSummary =>
        FilteredParameterCount == TotalParameterCount
            ? _localization.Format("MesDetail_ParamCountFormat", TotalParameterCount)
            : _localization.Format("MesDetail_ParamFilteredCountFormat", FilteredParameterCount, TotalParameterCount);

    public string CopyButtonText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string CopyPanelButtonText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public bool IsImageLoading
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsActiveImageMissing));
            }
        }
    }

    public bool IsShowingDefectCrop
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                UpdateActivePreviewForSelection();
            }
        }
    }

    public bool IsOneToOneModeActive
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
    }

    public int SelectedCameraIndex
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ActivePreviewImage));
                OnPropertyChanged(nameof(ActiveImageStatus));
                OnPropertyChanged(nameof(IsLeftCameraSelected));
                OnPropertyChanged(nameof(IsRightCameraSelected));
                OnPropertyChanged(nameof(ActiveHasDefectCrop));
                OnPropertyChanged(nameof(IsActiveImageMissing));
                OnPropertyChanged(nameof(ActiveMissingImageMessage));
                ResetImageZoom();
            }
        }
    }

    public bool IsSideBySideMode
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsSingleCameraMode));
                OnPropertyChanged(nameof(IsLeftCameraSelected));
                OnPropertyChanged(nameof(IsRightCameraSelected));
                OnPropertyChanged(nameof(IsActiveImageMissing));
                ResetImageZoom();
            }
        }
    }

    public bool IsSingleCameraMode => !IsSideBySideMode;

    public bool IsLeftCameraSelected => !IsSideBySideMode && SelectedCameraIndex == 0;

    public bool IsRightCameraSelected => !IsSideBySideMode && SelectedCameraIndex == 1;

    public double ImageZoomFactor
    {
        get => field;
        set
        {
            double clamped = Math.Clamp(value, MinImageZoom, MaxImageZoom);
            if (Math.Abs(field - clamped) > 0.0001)
            {
                field = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ImageZoomText));
                ImageZoomChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    } = MinImageZoom;

    public string ImageZoomText => $"{(int)Math.Round(ImageZoomFactor * 100)}%";

    public void ZoomImageBy(double multiplier)
    {
        ImageZoomFactor = Math.Clamp(ImageZoomFactor * multiplier, MinImageZoom, MaxImageZoom);
    }

    public void ResetImageZoom()
    {
        if (IsOneToOneModeActive)
        {
            IsOneToOneModeActive = false;
            UpdateActivePreviewForSelection();
        }
        ImageZoomFactor = MinImageZoom;
        ImageZoomChanged?.Invoke(this, EventArgs.Empty);
    }

    public string LeftCameraLabel
    {
        get => field;
        private set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    public string RightCameraLabel
    {
        get => field;
        private set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = string.Empty;

    /// <summary>
    /// Lightweight thumbnail (400px decode width) for the bottom Left Camera strip.
    /// </summary>
    public ImageSource? LeftThumbnail
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// Lightweight thumbnail (400px decode width) for the bottom Right Camera strip.
    /// </summary>
    public ImageSource? RightThumbnail
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
            }
        }
    }

    /// <summary>
    /// High-clarity preview image for Left Camera in Main Viewport and Side-by-Side mode.
    /// </summary>
    public ImageSource? LeftPreviewImage
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (SelectedCameraIndex == 0)
                {
                    OnPropertyChanged(nameof(ActivePreviewImage));
                    OnPropertyChanged(nameof(IsActiveImageMissing));
                }
            }
        }
    }

    /// <summary>
    /// High-clarity preview image for Right Camera in Main Viewport and Side-by-Side mode.
    /// </summary>
    public ImageSource? RightPreviewImage
    {
        get => field;
        set
        {
            if (!ReferenceEquals(field, value))
            {
                field = value;
                OnPropertyChanged();
                if (SelectedCameraIndex == 1)
                {
                    OnPropertyChanged(nameof(ActivePreviewImage));
                    OnPropertyChanged(nameof(IsActiveImageMissing));
                }
            }
        }
    }

    public string LeftImageStatus
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (SelectedCameraIndex == 0)
                {
                    OnPropertyChanged(nameof(ActiveImageStatus));
                    OnPropertyChanged(nameof(ActiveMissingImageMessage));
                }
            }
        }
    } = string.Empty;

    public string RightImageStatus
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                if (SelectedCameraIndex == 1)
                {
                    OnPropertyChanged(nameof(ActiveImageStatus));
                    OnPropertyChanged(nameof(ActiveMissingImageMessage));
                }
            }
        }
    } = string.Empty;

    public ImageSource? ActivePreviewImage => SelectedCameraIndex == 1 ? RightPreviewImage : LeftPreviewImage;

    public string ActiveImageStatus => SelectedCameraIndex == 1 ? RightImageStatus : LeftImageStatus;

    public bool ActiveHasDefectCrop =>
        SelectedCameraIndex == 1
            ? _rightCropPreview is not null
            : _leftCropPreview is not null;

    public bool IsActiveImageMissing =>
        !IsImageLoading &&
        !IsSideBySideMode &&
        ActivePreviewImage is null;

    public string ActiveMissingImageMessage =>
        !string.IsNullOrWhiteSpace(PanelName) && !PanelName.Equals("N/A", StringComparison.OrdinalIgnoreCase)
            ? _localization.Format("Spot_SerialImageNotFoundFormat", PanelName)
            : ActiveImageStatus;

    public ICommand CopyCommand { get; }

    public ICommand CopyPanelCommand { get; }

    public ICommand CloseCommand { get; }

    public ICommand ClearParameterSearchCommand { get; }

    public ICommand SelectLeftCameraCommand { get; }

    public ICommand SelectRightCameraCommand { get; }

    public ICommand ZoomInImageCommand { get; }

    public ICommand ZoomOutImageCommand { get; }

    public ICommand ResetImageZoomCommand { get; }

    public ICommand OneToOneZoomCommand { get; }

    public ICommand ToggleDefectCropCommand { get; }

    public ICommand ToggleSideBySideCommand { get; }

    private void ToggleDefectCropMode()
    {
        if (!ActiveHasDefectCrop) return;
        IsOneToOneModeActive = false;
        IsShowingDefectCrop = !IsShowingDefectCrop;
    }

    private void UpdateActivePreviewForSelection()
    {
        if (IsShowingDefectCrop)
        {
            if (_leftCropPreview is not null) LeftPreviewImage = _leftCropPreview;
            if (_rightCropPreview is not null) RightPreviewImage = _rightCropPreview;
        }
        else
        {
            if (_leftStandardPreview is not null) LeftPreviewImage = _leftStandardPreview;
            if (_rightStandardPreview is not null) RightPreviewImage = _rightStandardPreview;
        }
    }

    private async Task ActivateOneToOneInspectionAsync()
    {
        var activeSet = SelectedCameraIndex == 1 ? _rightImageSet : _leftImageSet;
        bool isRight = SelectedCameraIndex == 1;

        var cachedRaw = isRight ? _rightRawHighResPreview : _leftRawHighResPreview;
        if (cachedRaw is not null)
        {
            IsShowingDefectCrop = false;
            IsOneToOneModeActive = true;
            if (isRight) RightPreviewImage = cachedRaw;
            else LeftPreviewImage = cachedRaw;
            ImageZoomFactor = 2.5;
            return;
        }

        string? rawFile = activeSet?.RawHighResPngPath ?? activeSet?.OverviewImagePath;
        if (string.IsNullOrWhiteSpace(rawFile))
        {
            ImageZoomFactor = 2.5;
            return;
        }

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _oneToOneCts, cts);
        CancelCtsOnly(previous);
        if (_disposed)
        {
            Interlocked.CompareExchange(ref _oneToOneCts, null, cts);
            cts.Dispose();
            return;
        }

        IsImageLoading = true;
        try
        {
            var rawBmp = await ImageHelper.LoadOneToOneBitmapAsync(rawFile, cts.Token).ConfigureAwait(false);
            if (_disposed || cts.IsCancellationRequested || rawBmp is null) return;

            await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
            {
                if (_disposed || cts.IsCancellationRequested) return;
                IsShowingDefectCrop = false;
                IsOneToOneModeActive = true;
                string fileName = Path.GetFileName(rawFile);
                string rawLabel = _localization.Format("MesDetail_RawPrefixFormat", fileName);
                if (isRight)
                {
                    _rightRawHighResPreview = rawBmp;
                    RightPreviewImage = rawBmp;
                    RightImageStatus = rawLabel;
                }
                else
                {
                    _leftRawHighResPreview = rawBmp;
                    LeftPreviewImage = rawBmp;
                    LeftImageStatus = rawLabel;
                }
                ImageZoomFactor = 2.5;
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _oneToOneCts, null, cts);
            cts.Dispose();
            if (!_disposed)
            {
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (!_disposed) IsImageLoading = false;
                }).ConfigureAwait(false);
            }
        }
    }

    private void ApplyEntry(MesPanelDetailRecord? entry)
    {
        if (entry is null) return;
        LogTime = !string.IsNullOrEmpty(entry.LogTime) ? entry.LogTime : (_item.TimeFormatted ?? string.Empty);
        RoiLocation = !string.IsNullOrWhiteSpace(entry.RoiLocation) ? entry.RoiLocation : "-";
        DefectCodes = !string.IsNullOrWhiteSpace(entry.DefectCodes)
            ? entry.DefectCodes
            : !string.IsNullOrWhiteSpace(_item.Errors) ? _item.Errors : "-";

        string entryMes = entry.MesResult ?? string.Empty;
        string itemMes = _item.MesResult ?? string.Empty;
        _rawMesResult = entryMes.Contains("Fail", StringComparison.OrdinalIgnoreCase) ? entryMes
            : !string.IsNullOrWhiteSpace(entryMes) ? entryMes
            : !string.IsNullOrWhiteSpace(itemMes) ? itemMes
            : "Successful";

        TestResultText = LocalizeMesResultText(_rawMesResult);
        TestResultForeground = (_rawMesResult.Contains("Fail", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(_rawMesResult.Trim(), "NG", StringComparison.OrdinalIgnoreCase))
            ? "#EF4444"
            : "#10B981";

        _sortedParameters = entry.Parameters
            .OrderByDescending(IsNgParameter)
            .ThenBy(static p => p.Index)
            .ToList();

        TotalParameterCount = _sortedParameters.Count;
        NgParameterCount = _sortedParameters.Count(IsNgParameter);
        ApplyParameterFilter();
    }

    private string LocalizeMesResultText(string? rawResult)
    {
        string trimmed = rawResult?.Trim() ?? string.Empty;
        if (trimmed.Length == 0 || string.Equals(trimmed, "Successful", StringComparison.OrdinalIgnoreCase))
        {
            return _localization.Get("MesDetail_Successful");
        }
        return trimmed;
    }

    private void UpdateCameraLabels()
    {
        LeftCameraLabel = _cameraConfigs is { Count: >= 1 }
            ? _localization.Format("Spot_LeftCamFormat", _cameraConfigs[0].Code)
            : _localization.Get("Spot_LeftCamDefault");
        RightCameraLabel = _cameraConfigs is { Count: >= 2 }
            ? _localization.Format("Spot_RightCamFormat", _cameraConfigs[1].Code)
            : _localization.Get("Spot_RightCamDefault");
    }

    private void BuildRunItems()
    {
        int prevIndex = SelectedRunIndex;
        var runs = new ObservableCollection<string>();
        for (int i = 0; i < _details.Count; i++)
        {
            var d = _details[i];
            runs.Add(_localization.Format("MesDetail_RunItemFormat", i + 1, d.LogTime, LocalizeMesResultText(d.MesResult)));
        }
        RunItems = runs;
        if (prevIndex >= 0 && prevIndex < runs.Count)
        {
            SelectedRunIndex = prevIndex;
        }
    }

    private void OnLanguageChanged(object? sender, string languageCode) => RefreshLocalization();

    public void RefreshLocalization()
    {
        UpdateCameraLabels();
        if (IsMultiRun)
        {
            BuildRunItems();
        }
        TestResultText = LocalizeMesResultText(_rawMesResult);
        CopyButtonText = _hasCopiedTable
            ? _localization.Get("MesDetail_CopiedBtn")
            : _localization.Get("MesDetail_CopyTableBtn");
        CopyPanelButtonText = _hasCopiedPanel
            ? _localization.Get("MesDetail_CopiedBtn")
            : _localization.Get("MesDetail_CopyPanel");
        OnPropertyChanged(nameof(MesFileLabel));
        OnPropertyChanged(nameof(NgFilterLabel));
        OnPropertyChanged(nameof(ParameterCountSummary));
        OnPropertyChanged(nameof(ActiveMissingImageMessage));
    }

    private static bool IsNgParameter(MesParameterRow row) =>
        string.Equals(row.StatusText, "NG", StringComparison.OrdinalIgnoreCase);

    private void ScheduleParameterFilter()
    {
        if (_disposed) return;

        if (_searchDebounceTimer is null)
        {
            _searchDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _searchDebounceTimer.Tick += OnSearchDebounceTimerTick;
        }

        _searchDebounceTimer.Stop();
        _searchDebounceTimer.Start();
    }

    private void OnSearchDebounceTimerTick(object? sender, EventArgs e)
    {
        _searchDebounceTimer?.Stop();
        ApplyParameterFilter();
    }

    private void ApplyParameterFilter()
    {
        _searchDebounceTimer?.Stop();
        if (_disposed) return;

        string query = ParameterSearchText?.Trim() ?? string.Empty;
        bool hasQuery = query.Length > 0;

        IEnumerable<MesParameterRow> filtered = _sortedParameters;
        if (ShowNgOnly)
        {
            filtered = filtered.Where(IsNgParameter);
        }

        if (hasQuery)
        {
            filtered = filtered.Where(p =>
                (p.DefectCodeGroup?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.ParamName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.ParamValue?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.Spec?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                (p.StatusText?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        var list = filtered as IList<MesParameterRow> ?? filtered.ToList();
        Parameters = new ObservableCollection<MesParameterRow>(list);
        FilteredParameterCount = list.Count;
        _hasCopiedTable = false;
        CopyButtonText = _localization.Get("MesDetail_CopyTableBtn");
        CopyStatusMessage = string.Empty;
    }

    private void InitializeCameraInspection(
        BackgroundMonitorService? backgroundMonitor,
        IReadOnlyList<CameraImageConfig>? cameraConfigs)
    {
        if (cameraConfigs is null || cameraConfigs.Count == 0)
        {
            LeftImageStatus = _localization.Get("Spot_NotConfigured");
            RightImageStatus = _localization.Get("Spot_NotConfigured");
            return;
        }

        if (backgroundMonitor is null)
        {
            string unavailable = _localization.Get("MesDetail_CameraServiceUnavailable");
            LeftImageStatus = unavailable;
            RightImageStatus = cameraConfigs.Count >= 2 ? unavailable : _localization.Get("Spot_NotConfigured");
            return;
        }

        if (string.IsNullOrWhiteSpace(PanelName) || PanelName.Equals("N/A", StringComparison.OrdinalIgnoreCase))
        {
            LeftImageStatus = _localization.Get("Spot_NoPanelName");
            RightImageStatus = _localization.Get("Spot_NoPanelName");
            return;
        }

        var targets = new List<PanelImageSearchTarget>(2)
        {
            new(cameraConfigs[0].Root, "Left", cameraConfigs[0].Code)
        };
        if (cameraConfigs.Count >= 2)
        {
            targets.Add(new(cameraConfigs[1].Root, "Right", cameraConfigs[1].Code));
        }

        IsImageLoading = true;
        string searchingText = _localization.Get("Spot_Searching");
        LeftImageStatus = searchingText;
        RightImageStatus = targets.Count >= 2 ? searchingText : _localization.Get("Spot_NotConfigured");

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _imageCts, cts);
        CancelCtsOnly(previous);
        if (_disposed)
        {
            Interlocked.CompareExchange(ref _imageCts, null, cts);
            cts.Dispose();
            return;
        }
        _ = LoadCameraImagesAsync(backgroundMonitor, targets, _item.Timestamp, cts);
    }

    private async Task LoadCameraImagesAsync(
        BackgroundMonitorService backgroundMonitor,
        List<PanelImageSearchTarget> targets,
        DateTime? timestamp,
        CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            var results = await backgroundMonitor.FindPanelImagesAsync(
                targets,
                PanelName,
                timestamp,
                timeoutSec: 3,
                cancellationToken: ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            foreach (var r in results)
            {
                if (ct.IsCancellationRequested) return;
                bool isLeft = string.Equals(r.Side, "Left", StringComparison.OrdinalIgnoreCase);
                string camCode = targets.FirstOrDefault(t => string.Equals(t.Side, r.Side, StringComparison.OrdinalIgnoreCase))?.Code ?? r.Side;

                if (r.Offline)
                {
                    await SetCameraSideOnUiAsync(
                        isLeft,
                        _localization.Get("Spot_CameraOffline"),
                        thumbnail: null,
                        preview: null,
                        cropPreview: null,
                        imageSet: null,
                        ct).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(r.DirectoryPath))
                {
                    await SetCameraSideOnUiAsync(
                        isLeft,
                        _localization.Format("Spot_SerialImageNotFoundFormat", PanelName),
                        thumbnail: null,
                        preview: null,
                        cropPreview: null,
                        imageSet: null,
                        ct).ConfigureAwait(false);
                    continue;
                }

                string dir = r.DirectoryPath;
                var imageSet = await Task.Run(
                    () => ImageLocatorService.ResolveChannelImagesFromDirectory(dir, PanelName, camCode, r.Side, r.Root, DefectCodes),
                    ct).ConfigureAwait(false);

                string? previewPath = imageSet.PreferredPreviewPath;
                string? thumbPath = imageSet.PreferredSpotlightPath ?? previewPath;

                if (string.IsNullOrWhiteSpace(previewPath) || string.IsNullOrWhiteSpace(thumbPath))
                {
                    await SetCameraSideOnUiAsync(
                        isLeft,
                        _localization.Format("Spot_SerialImageNotFoundFormat", PanelName),
                        thumbnail: null,
                        preview: null,
                        cropPreview: null,
                        imageSet: imageSet,
                        ct).ConfigureAwait(false);
                    continue;
                }

                // Decode lightweight thumbnail (400px) and main viewport preview (1920px) in parallel with 3x 500ms retry
                var thumbTask = ImageHelper.LoadThumbnailAsync(thumbPath, ImageHelper.ThumbnailDecodeWidth, ct);
                var previewTask = ImageHelper.LoadBitmapAsync(previewPath, ImageHelper.PreviewDecodeWidth, maxRetries: 3, retryDelayMs: 500, ct);
                Task<System.Windows.Media.Imaging.BitmapImage?> cropTask =
                    imageSet.PrimaryDefectCropPath is { } cropFile && !string.Equals(cropFile, previewPath, StringComparison.OrdinalIgnoreCase)
                        ? ImageHelper.LoadBitmapAsync(cropFile, decodePixelWidth: 1280, maxRetries: 3, retryDelayMs: 500, ct)
                        : Task.FromResult<System.Windows.Media.Imaging.BitmapImage?>(null);

                await Task.WhenAll(thumbTask, previewTask, cropTask).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                var thumbBmp = await thumbTask.ConfigureAwait(false);
                var previewBmp = await previewTask.ConfigureAwait(false) ?? thumbBmp;
                var cropBmp = await cropTask.ConfigureAwait(false);

                string status = previewBmp is not null
                    ? Path.GetFileName(previewPath)
                    : _localization.Get("Spot_DecodeError");

                await SetCameraSideOnUiAsync(isLeft, status, thumbBmp, previewBmp, cropBmp, imageSet, ct).ConfigureAwait(false);
            }

            // Auto-select Right Camera tab if Left has no image/defect but Right has an image or defect crop
            await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
            {
                if (_disposed || ct.IsCancellationRequested) return;
                if (LeftPreviewImage is null && RightPreviewImage is not null)
                {
                    SelectedCameraIndex = 1;
                }
                else if (_leftImageSet is { HasDefectCrop: false } && _rightImageSet is { HasDefectCrop: true })
                {
                    SelectedCameraIndex = 1;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (!ct.IsCancellationRequested)
            {
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (_disposed || ct.IsCancellationRequested) return;
                    string errText = _localization.Get("Spot_SearchError");
                    LeftImageStatus = errText;
                    if (RightImageStatus == _localization.Get("Spot_Searching")) RightImageStatus = errText;
                }).ConfigureAwait(false);
            }
        }
        finally
        {
            try
            {
                if (!ct.IsCancellationRequested)
                {
                    await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                    {
                        if (!_disposed && !ct.IsCancellationRequested)
                        {
                            IsImageLoading = false;
                        }
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                Interlocked.CompareExchange(ref _imageCts, null, cts);
                cts.Dispose();
            }
        }
    }

    private Task SetCameraSideOnUiAsync(
        bool isLeft,
        string status,
        ImageSource? thumbnail,
        ImageSource? preview,
        ImageSource? cropPreview,
        CameraChannelImageSet? imageSet,
        CancellationToken ct) =>
        BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
        {
            if (_disposed || ct.IsCancellationRequested) return;
            if (isLeft)
            {
                _leftImageSet = imageSet;
                _leftStandardPreview = preview;
                _leftCropPreview = cropPreview;
                LeftThumbnail = thumbnail;
                LeftPreviewImage = IsShowingDefectCrop && cropPreview is not null ? cropPreview : preview;
                LeftImageStatus = status;
            }
            else
            {
                _rightImageSet = imageSet;
                _rightStandardPreview = preview;
                _rightCropPreview = cropPreview;
                RightThumbnail = thumbnail;
                RightPreviewImage = IsShowingDefectCrop && cropPreview is not null ? cropPreview : preview;
                RightImageStatus = status;
            }
            OnPropertyChanged(nameof(ActiveHasDefectCrop));
            OnPropertyChanged(nameof(IsActiveImageMissing));
            OnPropertyChanged(nameof(ActiveMissingImageMessage));
        });

    private async Task CopyPanelAsync()
    {
        if (string.IsNullOrWhiteSpace(PanelName)) return;
        if (await GridActionsViewModel.SetSafeClipboardAsync(PanelName))
        {
            _hasCopiedPanel = true;
            CopyPanelButtonText = _localization.Get("MesDetail_CopiedBtn");
        }
    }

    private async Task CopyParameterTableAsync()
    {
        int idx = _details.Count > 1 && SelectedRunIndex >= 0 ? SelectedRunIndex : 0;
        string logTime = (idx >= 0 && idx < _details.Count) ? _details[idx].LogTime : LogTime;
        var visibleRows = Parameters.ToList();

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"PANELNAME: {PanelName} | PMNT: {Pmnt} | Source: {Source} | MES Time: {logTime}");
        sb.Append("#\t")
          .Append(_localization.Get("MesDetail_Col_TestCode")).Append('\t')
          .Append(_localization.Get("MesDetail_Col_ParamName")).Append('\t')
          .Append(_localization.Get("MesDetail_Col_MeasuredValue")).Append('\t')
          .Append(_localization.Get("MesDetail_Col_StandardSpec")).Append('\t')
          .Append(_localization.Get("MesDetail_Col_Evaluation")).AppendLine();
        foreach (var p in visibleRows)
        {
            sb.Append(p.Index).Append('\t')
              .Append(p.DefectCodeGroup).Append('\t')
              .Append(p.ParamName).Append('\t')
              .Append(p.ParamValue).Append('\t')
              .Append(p.Spec).Append('\t')
              .Append(p.StatusText).AppendLine();
        }

        string tableText = sb.ToString().TrimEnd('\r', '\n');
        if (await GridActionsViewModel.SetSafeClipboardAsync(tableText))
        {
            _hasCopiedTable = true;
            CopyButtonText = _localization.Get("MesDetail_CopiedBtn");
            CopyStatusMessage = visibleRows.Count == 1
                ? _localization.Get("MesDetail_CopiedOneRow")
                : _localization.Format("MesDetail_CopiedRowsFormat", visibleRows.Count);
        }
    }

    private static void CancelCtsOnly(CancellationTokenSource? cts)
    {
        if (cts is null) return;
        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _localization.LanguageChanged -= OnLanguageChanged;

        if (_searchDebounceTimer is not null)
        {
            _searchDebounceTimer.Stop();
            _searchDebounceTimer.Tick -= OnSearchDebounceTimerTick;
            _searchDebounceTimer = null;
        }

        CancelCtsOnly(Interlocked.Exchange(ref _imageCts, null));
        CancelCtsOnly(Interlocked.Exchange(ref _oneToOneCts, null));

        // Release all decoded BitmapImage references (especially 4096px 1:1 previews) immediately upon close
        _leftImageSet = null;
        _rightImageSet = null;
        _leftStandardPreview = null;
        _rightStandardPreview = null;
        _leftCropPreview = null;
        _rightCropPreview = null;
        _leftRawHighResPreview = null;
        _rightRawHighResPreview = null;
        LeftThumbnail = null;
        RightThumbnail = null;
        LeftPreviewImage = null;
        RightPreviewImage = null;
    }
}
