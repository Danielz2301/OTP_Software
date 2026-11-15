using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Locates panel optical inspection image folders across dual-camera SMB shares with fast TCP 445 reachability probing.
/// </summary>
public interface ICameraImageService
{
    Task<bool> TestHostReachableAsync(
        string? uncPath,
        int timeoutMs = 1200,
        CancellationToken cancellationToken = default);

    Task<string?> FindPanelImageFolderAsync(
        string rootPath,
        string panelName,
        string sideCode,
        DateTime? timestamp,
        bool skipHostCheck = false,
        CancellationToken cancellationToken = default);

    IReadOnlyList<CameraEndpointDescriptor> GetCameraEndpoints(OtpSettings settings);

    Task<IReadOnlyList<CameraHealthStatus>> CheckCameraEndpointsHealthAsync(
        IReadOnlyList<CameraEndpointDescriptor> endpoints,
        int timeoutMs = 600,
        CancellationToken cancellationToken = default);
}

