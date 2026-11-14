using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using OTPSoftware.App.Services;
using OTPSoftware.Core.Models;

namespace OTPSoftware.App.ViewModels;

/// <summary>
/// Live NG Unit Spotlight ViewModel for Wallboard and real-time operator inspection.
/// Automatically pulls optical parameters from MES_1.log and decodes thumbnail images
/// from Left/Right cameras asynchronously without blocking the UI thread.
/// Uses C# 14 <c>field</c> keyword in all property setters.
/// </summary>
public sealed class SpotlightViewModel : ViewModelBase, IDisposable
{
    private readonly BackgroundMonitorService _backgroundMonitor;
    private readonly OtpSettings _settings;
    private readonly ErrorLoggerService? _errorLogger;
    private readonly ILocalizationService _localization;

    private CancellationTokenSource? _cts;
    private bool _disposed;
    private string? _mesStatusKey;
    private object[] _mesStatusArgs = [];
    private string? _leftImageStatusKey;
    private string? _rightImageStatusKey;

    public SpotlightViewModel(
        BackgroundMonitorService backgroundMonitor,
        OtpSettings settings,
        ErrorLoggerService? errorLogger = null,
        ILocalizationService? localizationService = null)
    {
        _backgroundMonitor = backgroundMonitor ?? throw new ArgumentNullException(nameof(backgroundMonitor));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _errorLogger = errorLogger;
        _localization = localizationService ?? LocalizationService.Default;
        ApplyDefaultWaitingTexts();
    }

    private void ApplyDefaultWaitingTexts()
    {
        PanelName = _localization.Get("Spot_WaitingNg");
        DefectSummary = _localization.Get("Spot_NoActiveDefects");
        LeftCameraLabel = _localization.Get("Spot_LeftCamDefault");
        RightCameraLabel = _localization.Get("Spot_RightCamDefault");
        SetMesStatus("Spot_Idle");
        SetImageStatus(isLeft: true, "Spot_NoImage");
        SetImageStatus(isLeft: false, "Spot_NoImage");
    }

    private void SetMesStatus(string key, params object[] args)
    {
        _mesStatusKey = key;
        _mesStatusArgs = args;
        MesStatusText = ResolveStatusText(key, args);
    }

    private void SetImageStatus(bool isLeft, string key)
    {
        string text = ResolveStatusText(key);
        if (isLeft)
        {
            _leftImageStatusKey = key;
            LeftImageStatus = text;
        }
        else
        {
            _rightImageStatusKey = key;
            RightImageStatus = text;
        }
    }

    private string ResolveStatusText(string key, params object[] args) => key switch
    {
        "Spot_Idle" => _localization.Get("Spot_Idle"),
        "Spot_NoImage" => _localization.Get("Spot_NoImage"),
        "Spot_LoadingMesCam" => _localization.Get("Spot_LoadingMesCam"),
        "Spot_Searching" => _localization.Get("Spot_Searching"),
        "Spot_NoPanelForLookup" => _localization.Get("Spot_NoPanelForLookup"),
        "Spot_NoPanelName" => _localization.Get("Spot_NoPanelName"),
        "Spot_MesParamsNgFormat" => _localization.Format("Spot_MesParamsNgFormat", args),
        "Spot_MesParamsAllOkFormat" => _localization.Format("Spot_MesParamsAllOkFormat", args),
        "Spot_MesUnreachable" => _localization.Get("Spot_MesUnreachable"),
        "Spot_MesLogNotFound" => _localization.Get("Spot_MesLogNotFound"),
        "Spot_MesPanelNotFound" => _localization.Get("Spot_MesPanelNotFound"),
        "Spot_MesLookupError" => _localization.Get("Spot_MesLookupError"),
        "Spot_NotConfigured" => _localization.Get("Spot_NotConfigured"),
        "Spot_CameraOffline" => _localization.Get("Spot_CameraOffline"),
        "Spot_FolderNotFound" => _localization.Get("Spot_FolderNotFound"),
        "Spot_NoImagesInFolder" => _localization.Get("Spot_NoImagesInFolder"),
        "Spot_SerialImageNotFoundFormat" => _localization.Format("Spot_SerialImageNotFoundFormat", PanelName),
        "Spot_DecodeError" => _localization.Get("Spot_DecodeError"),
        "Spot_SearchError" => _localization.Get("Spot_SearchError"),
        _ => _localization.Get(key)
    };

    public void RefreshLocalization()
    {
        if (!HasSpotlightUnit)
        {
            ApplyDefaultWaitingTexts();
            return;
        }

        if (_settings.MachinesByName.TryGetValue(Source, out var machineCfg))
        {
            LeftCameraLabel = machineCfg.Images.Count >= 1
                ? _localization.Format("Spot_LeftCamFormat", machineCfg.Images[0].Code)
                : _localization.Get("Spot_LeftCamDefault");
            RightCameraLabel = machineCfg.Images.Count >= 2
                ? _localization.Format("Spot_RightCamFormat", machineCfg.Images[1].Code)
                : _localization.Get("Spot_RightCamDefault");
        }
        else
        {
            LeftCameraLabel = _localization.Get("Spot_LeftCamDefault");
            RightCameraLabel = _localization.Get("Spot_RightCamDefault");
        }

        if (_mesStatusKey is { } mesKey)
        {
            MesStatusText = ResolveStatusText(mesKey, _mesStatusArgs);
        }

        if (_leftImageStatusKey is { } leftKey)
        {
            LeftImageStatus = ResolveStatusText(leftKey);
        }

        if (_rightImageStatusKey is { } rightKey)
        {
            RightImageStatus = ResolveStatusText(rightKey);
        }
    }

    public bool HasSpotlightUnit
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string PanelName
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Waiting for NG unit...";

    public string BluName
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "-";

    public string Source
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "-";

    public string Pmnt
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "-";

    public int ConsecutiveNg
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public bool IsConsecutiveAlarm
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    }

    public string TimeFormatted
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "--:--:--";

    public string DefectSummary
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "No active defects";

    public string MesStatusText
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Idle";

    public ImageSource? LeftThumbnail
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    }

    public ImageSource? RightThumbnail
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    }

    public string LeftCameraLabel
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Left Cam (Cam 1)";

    public string RightCameraLabel
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "Right Cam (Cam 2)";

    public string LeftImageStatus
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "No image";

    public string RightImageStatus
    {
        get => field;
        set { if (field != value) { field = value; OnPropertyChanged(); } }
    } = "No image";

    /// <summary>
    /// Displays the camera station(s) where the NG defect or panel image was detected (e.g., "CAM: B1" or "CAM: A1 / A2").
    /// </summary>
    public string DetectedCameraBadge
    {
        get => field;
        set
        {
            if (field != value)
            {
                field = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasDetectedCamera));
            }
        }
    } = string.Empty;

    public bool HasDetectedCamera => !string.IsNullOrWhiteSpace(DetectedCameraBadge);

    public ObservableCollection<MesParameterRow> MesParameters
    {
        get => field;
        set { if (!ReferenceEquals(field, value)) { field = value; OnPropertyChanged(); } }
    } = [];

    /// <summary>
    /// Resets all properties to default waiting state when logs or filters are cleared.
    /// </summary>
    public void Clear()
    {
        CancelCurrentInspection();

        HasSpotlightUnit = false;
        BluName = "-";
        Source = "-";
        Pmnt = "-";
        ConsecutiveNg = 0;
        IsConsecutiveAlarm = false;
        TimeFormatted = "--:--:--";
        DetectedCameraBadge = string.Empty;
        LeftThumbnail = null;
        RightThumbnail = null;
        if (MesParameters.Count > 0)
        {
            MesParameters = [];
        }
        ApplyDefaultWaitingTexts();
    }

    /// <summary>
    /// Automatically populates metadata for the latest NG unit and initiates parallel background lookups
    /// for MES_1.log optical parameters and Left/Right camera thumbnails.
    /// </summary>
    public async Task InspectUnitAsync(
        UnitRecord? unit,
        IReadOnlyList<string> mesLogCandidates,
        int consecutiveThreshold = 3,
        int? consecutiveNg = null)
    {
        if (unit is null || _disposed) return;

        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cts, cts);
        CancelCtsOnly(previous);
        if (_disposed)
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
            return;
        }

        var ct = cts.Token;

        try
        {
            // 1. Immediately populate unit metadata on the ViewModel
            int streak = consecutiveNg ?? (unit.IsNg ? 1 : 0);
            HasSpotlightUnit = true;
            PanelName = string.IsNullOrWhiteSpace(unit.PanelName) ? "N/A" : unit.PanelName;
            BluName = string.IsNullOrWhiteSpace(unit.BluName) ? "-" : unit.BluName;
            Source = string.IsNullOrWhiteSpace(unit.Source) ? "-" : unit.Source;
            Pmnt = string.IsNullOrWhiteSpace(unit.Pmnt) ? "-" : unit.Pmnt;
            ConsecutiveNg = streak;
            IsConsecutiveAlarm = streak >= consecutiveThreshold;
            TimeFormatted = unit.Timestamp?.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) ?? "-";
            DefectSummary = unit.IsNg ? string.Join(" | ", unit.Errors) : "OK";
            DetectedCameraBadge = string.Empty;
            SetMesStatus("Spot_LoadingMesCam");

            LeftThumbnail = null;
            RightThumbnail = null;
            SetImageStatus(isLeft: true, "Spot_Searching");
            SetImageStatus(isLeft: false, "Spot_Searching");
            if (MesParameters.Count > 0)
            {
                MesParameters = [];
            }

            // Resolve camera labels from machine configuration
            if (_settings.MachinesByName.TryGetValue(unit.Source, out var machineCfg))
            {
                LeftCameraLabel = machineCfg.Images.Count >= 1
                    ? _localization.Format("Spot_LeftCamFormat", machineCfg.Images[0].Code)
                    : _localization.Get("Spot_LeftCamDefault");
                RightCameraLabel = machineCfg.Images.Count >= 2
                    ? _localization.Format("Spot_RightCamFormat", machineCfg.Images[1].Code)
                    : _localization.Get("Spot_RightCamDefault");
            }
            else
            {
                LeftCameraLabel = _localization.Get("Spot_LeftCamDefault");
                RightCameraLabel = _localization.Get("Spot_RightCamDefault");
            }

            // Validate PanelName
            if (string.IsNullOrWhiteSpace(unit.PanelName) || unit.PanelName.Equals("N/A", StringComparison.OrdinalIgnoreCase))
            {
                SetMesStatus("Spot_NoPanelForLookup");
                SetImageStatus(isLeft: true, "Spot_NoPanelName");
                SetImageStatus(isLeft: false, "Spot_NoPanelName");
                return;
            }

            // 2. Run in parallel: MES Parameter Lookup and Camera Thumbnail Search
            var mesTask = FetchMesDetailsAsync(mesLogCandidates, unit.PanelName, ct);
            var cameraTask = FetchCameraThumbnailsAsync(unit, ct);

            await Task.WhenAll(mesTask, cameraTask).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when a newer unit preempts the current inspection
        }
        catch (Exception ex)
        {
            _errorLogger?.LogException(ex, "SpotlightViewModel.InspectUnitAsync");
        }
        finally
        {
            Interlocked.CompareExchange(ref _cts, null, cts);
            cts.Dispose();
        }
    }

    private async Task FetchMesDetailsAsync(
        IReadOnlyList<string> mesLogCandidates,
        string panelName,
        CancellationToken ct)
    {
        try
        {
            var res = await _backgroundMonitor.FindMesDetailsAsync(
                mesLogCandidates,
                panelName,
                timeoutSec: 5,
                cancellationToken: ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
            {
                if (ct.IsCancellationRequested) return;

                if (res.Details.Count > 0)
                {
                    var latest = res.Details[^1];
                    var sorted = latest.Parameters
                        .OrderByDescending(p => p.EvaluationStatus == MesEvaluationStatus.Ng)
                        .ThenBy(p => p.Index)
                        .ToList();

                    MesParameters = new ObservableCollection<MesParameterRow>(sorted);
                    int ngCount = sorted.Count(p => p.EvaluationStatus == MesEvaluationStatus.Ng);
                    if (ngCount > 0)
                    {
                        SetMesStatus("Spot_MesParamsNgFormat", sorted.Count, ngCount);
                    }
                    else
                    {
                        SetMesStatus("Spot_MesParamsAllOkFormat", sorted.Count);
                    }
                }
                else if (res.Unreachable)
                {
                    SetMesStatus("Spot_MesUnreachable");
                }
                else if (!res.FoundFile)
                {
                    SetMesStatus("Spot_MesLogNotFound");
                }
                else
                {
                    SetMesStatus("Spot_MesPanelNotFound");
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorLogger?.LogException(ex, "SpotlightViewModel.FetchMesDetailsAsync");
            if (!ct.IsCancellationRequested)
            {
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested) SetMesStatus("Spot_MesLookupError");
                }).ConfigureAwait(false);
            }
        }
    }

    private async Task FetchCameraThumbnailsAsync(UnitRecord unit, CancellationToken ct)
    {
        var targets = new List<PanelImageSearchTarget>(2);
        string leftCode = "Cam 1";
        string rightCode = "Cam 2";
        if (_settings.MachinesByName.TryGetValue(unit.Source, out var machineCfg))
        {
            if (machineCfg.Images.Count >= 1)
            {
                leftCode = machineCfg.Images[0].Code;
                targets.Add(new PanelImageSearchTarget(machineCfg.Images[0].Root, "Left", leftCode));
            }
            if (machineCfg.Images.Count >= 2)
            {
                rightCode = machineCfg.Images[1].Code;
                targets.Add(new PanelImageSearchTarget(machineCfg.Images[1].Root, "Right", rightCode));
            }
        }

        if (targets.Count == 0)
        {
            await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
            {
                if (!ct.IsCancellationRequested)
                {
                    SetImageStatus(isLeft: true, "Spot_NotConfigured");
                    SetImageStatus(isLeft: false, "Spot_NotConfigured");
                }
            }).ConfigureAwait(false);
            return;
        }

        try
        {
            var searchResults = await _backgroundMonitor.FindPanelImagesAsync(
                targets,
                unit.PanelName,
                unit.Timestamp,
                timeoutSec: 3,
                cancellationToken: ct).ConfigureAwait(false);

            if (ct.IsCancellationRequested) return;

            var detectedCropChannels = new List<string>(2);
            var detectedAnyChannels = new List<string>(2);
            string defectHint = unit.IsNg ? string.Join(" | ", unit.Errors) : string.Empty;

            // Process each camera search result
            foreach (var r in searchResults)
            {
                if (ct.IsCancellationRequested) return;
                bool isLeft = string.Equals(r.Side, "Left", StringComparison.OrdinalIgnoreCase);
                string channelCode = isLeft ? leftCode : rightCode;

                if (r.Offline)
                {
                    await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                    {
                        if (!ct.IsCancellationRequested) SetImageStatus(isLeft, "Spot_CameraOffline");
                    }).ConfigureAwait(false);
                    continue;
                }

                if (string.IsNullOrWhiteSpace(r.DirectoryPath))
                {
                    await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                    {
                        if (!ct.IsCancellationRequested) SetImageStatus(isLeft, "Spot_SerialImageNotFoundFormat");
                    }).ConfigureAwait(false);
                    continue;
                }

                string dir = r.DirectoryPath;
                var imageSet = await Task.Run(
                    () => OTPSoftware.Core.Services.ImageLocatorService.ResolveChannelImagesFromDirectory(
                        dir,
                        unit.PanelName,
                        channelCode,
                        r.Side,
                        r.Root,
                        defectHint),
                    ct).ConfigureAwait(false);

                string? imageFile = imageSet.PreferredSpotlightPath;
                if (string.IsNullOrWhiteSpace(imageFile))
                {
                    await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                    {
                        if (!ct.IsCancellationRequested) SetImageStatus(isLeft, "Spot_SerialImageNotFoundFormat");
                    }).ConfigureAwait(false);
                    continue;
                }

                detectedAnyChannels.Add(channelCode);
                if (imageSet.HasDefectCrop)
                {
                    detectedCropChannels.Add(channelCode);
                }

                var bmp = await ImageHelper.LoadThumbnailAsync(
                    imageFile,
                    ImageHelper.ThumbnailDecodeWidth,
                    ct).ConfigureAwait(false);
                if (ct.IsCancellationRequested) return;

                string statusLabel = imageSet.HasDefectCrop && string.Equals(imageFile, imageSet.PrimaryDefectCropPath, StringComparison.OrdinalIgnoreCase)
                    ? $"[Crop] {Path.GetFileName(imageFile)}"
                    : Path.GetFileName(imageFile);

                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (ct.IsCancellationRequested) return;
                    if (bmp is not null)
                    {
                        if (isLeft)
                        {
                            LeftThumbnail = bmp;
                            _leftImageStatusKey = null;
                            LeftImageStatus = statusLabel;
                        }
                        else
                        {
                            RightThumbnail = bmp;
                            _rightImageStatusKey = null;
                            RightImageStatus = statusLabel;
                        }
                    }
                    else
                    {
                        SetImageStatus(isLeft, "Spot_DecodeError");
                    }
                }).ConfigureAwait(false);
            }

            if (detectedAnyChannels.Count > 0 && !ct.IsCancellationRequested)
            {
                var badgeChannels = detectedCropChannels.Count > 0 ? detectedCropChannels : detectedAnyChannels;
                string badgeText = $"CAM: {string.Join(" / ", badgeChannels)}";
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        DetectedCameraBadge = badgeText;
                    }
                }).ConfigureAwait(false);
            }

            // If only 1 target was configured, mark the other as Not configured
            if (targets.Count < 2)
            {
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        SetImageStatus(isLeft: false, "Spot_NotConfigured");
                    }
                }).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _errorLogger?.LogException(ex, "SpotlightViewModel.FetchCameraThumbnailsAsync");
            if (!ct.IsCancellationRequested)
            {
                await BackgroundMonitorService.InvokeOnUiThreadAsync(() =>
                {
                    if (!ct.IsCancellationRequested)
                    {
                        SetImageStatus(isLeft: true, "Spot_SearchError");
                        SetImageStatus(isLeft: false, "Spot_SearchError");
                    }
                }).ConfigureAwait(false);
            }
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

    private void CancelCurrentInspection()
    {
        var cts = Interlocked.Exchange(ref _cts, null);
        CancelCtsOnly(cts);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        CancelCurrentInspection();
        LeftThumbnail = null;
        RightThumbnail = null;
        if (MesParameters.Count > 0)
        {
            MesParameters = [];
        }
    }
}
