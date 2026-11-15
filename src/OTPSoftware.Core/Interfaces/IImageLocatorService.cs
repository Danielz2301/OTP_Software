using OTPSoftware.Core.Services;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Resolves AOI inspection directories and locates overview JPG, defect crop, and on-demand 1:1 raw PNG paths.
/// </summary>
public interface IImageLocatorService
{
    Task<CameraChannelImageSet> LocateChannelImagesAsync(
        string rootPath,
        string serialNumber,
        string cameraChannel,
        string side = "Left",
        DateTime? timestamp = null,
        string? defectCode = null,
        bool skipHostCheck = false,
        CancellationToken cancellationToken = default);

    Task<DualCameraImagePair> LocateDualCameraPairAsync(
        string leftRootPath,
        string leftCameraChannel,
        string? rightRootPath,
        string? rightCameraChannel,
        string serialNumber,
        DateTime? timestamp = null,
        string? defectCode = null,
        CancellationToken cancellationToken = default);
}
