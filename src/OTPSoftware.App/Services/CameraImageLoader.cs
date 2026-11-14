namespace OTPSoftware.App.Services;

using System.Windows.Media.Imaging;
using OTPSoftware.Core.Services;

/// <summary>
/// Shared facade over <see cref="ImageLocatorService"/> and <see cref="ImageHelper"/>
/// preserving backward compatibility while supporting nested AOI <c>piresult/</c> and <c>defectPicture/</c> directories.
/// </summary>
public static class CameraImageLoader
{
    /// <summary>
    /// Resolves the best representative image in a panel directory (<c>piresult/defectPicture/*</c>,
    /// <c>piresult/AlgIntegrate.jpg</c>, <c>piresult/Main*_L255.jpg</c>, or top-level fallback).
    /// </summary>
    public static string? FindFirstImageFile(string? directoryPath, string? defectCode = null, bool preferDefectCrop = false)
    {
        if (string.IsNullOrWhiteSpace(directoryPath)) return null;

        var set = ImageLocatorService.ResolveChannelImagesFromDirectory(
            directoryPath,
            serialNumber: string.Empty,
            cameraChannel: string.Empty,
            defectCode: defectCode);

        return preferDefectCrop
            ? set.PreferredSpotlightPath
            : set.PreferredPreviewPath;
    }

    /// <summary>
    /// Decodes an image file without locking it on disk and returns a frozen <see cref="BitmapImage"/>.
    /// </summary>
    public static BitmapImage? LoadDecodedBitmap(string? filePath, int decodePixelWidth = ImageHelper.ThumbnailDecodeWidth) =>
        ImageHelper.DecodeFrozenBitmapFromFile(filePath, decodePixelWidth);
}
