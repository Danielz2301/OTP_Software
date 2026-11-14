namespace OTPSoftware.App.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

/// <summary>
/// High-performance asynchronous WPF image loader tailored for Machine Vision &amp; Industrial AOI inspection.
/// Ensures zero UI-thread freeze, bounded RAM consumption via <see cref="BitmapImage.DecodePixelWidth"/>,
/// automatic retry on incomplete camera writes, and cross-thread safety via <see cref="Freezable.Freeze()"/>.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Decode width for bottom camera strip and Wallboard Spotlight thumbnails (~360–480px).
    /// </summary>
    public const int ThumbnailDecodeWidth = 400;

    /// <summary>
    /// Decode width for main viewport overview (<c>piresult/AlgIntegrate.jpg</c> or <c>Main*_L255.jpg</c>).
    /// </summary>
    public const int PreviewDecodeWidth = 1920;

    /// <summary>
    /// Safe fallback decode width when a 50MB 17701x14208 raw PNG exceeds available contiguous WIC/GPU memory.
    /// </summary>
    public const int HighResFallbackDecodeWidth = 4096;

    /// <summary>
    /// Asynchronously reads and decodes an image file on a background thread with automatic retry
    /// (default 3 retries x 500ms) to handle files actively being flushed by AOI camera stations.
    /// </summary>
    public static async Task<BitmapImage?> LoadBitmapAsync(
        string? filePath,
        int decodePixelWidth = PreviewDecodeWidth,
        int maxRetries = 3,
        int retryDelayMs = 500,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        int totalAttempts = Math.Max(1, maxRetries);

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // Decode directly from SequentialScan FileStream on thread pool to eliminate LOH MemoryStream copies (10–50MB),
                // using shared ReadWrite access so AOI camera station writers are never blocked.
                BitmapImage? decoded = await Task.Run(() =>
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var info = new FileInfo(filePath);
                    if (!info.Exists || info.Length == 0)
                    {
                        return null;
                    }

                    using var fs = new FileStream(
                        filePath,
                        new FileStreamOptions
                        {
                            Mode = FileMode.Open,
                            Access = FileAccess.Read,
                            Share = FileShare.ReadWrite | FileShare.Delete,
                            Options = FileOptions.SequentialScan,
                            BufferSize = 65536
                        });

                    if (fs.Length == 0)
                    {
                        return null;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    return DecodeFrozenBitmapFromStream(fs, decodePixelWidth);
                }, cancellationToken).ConfigureAwait(false);

                if (decoded is not null)
                {
                    return decoded;
                }

                if (attempt < totalAttempts)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (OutOfMemoryException oomEx)
            {
                ErrorLoggerService.Default.LogException(oomEx, $"ImageHelper.LoadBitmapAsync OOM ({filePath})");
                if (decodePixelWidth == 0 || decodePixelWidth > HighResFallbackDecodeWidth)
                {
                    // Fallback to safe 4K decode width if 1:1 raw PNG exhausted unmanaged WIC memory
                    return await LoadBitmapAsync(
                        filePath,
                        decodePixelWidth: HighResFallbackDecodeWidth,
                        maxRetries: 1,
                        retryDelayMs: retryDelayMs,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                return null;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or FileFormatException or NotSupportedException or InvalidOperationException)
            {
                // File is still locked or partially written by the camera station -> retry after 500ms
                if (attempt < totalAttempts)
                {
                    await Task.Delay(retryDelayMs, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                ErrorLoggerService.Default.LogException(ex, $"ImageHelper.LoadBitmapAsync({filePath}) after {attempt} attempts");
                return null;
            }
            catch (Exception ex)
            {
                ErrorLoggerService.Default.LogException(ex, $"ImageHelper.LoadBitmapAsync({filePath})");
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// Loads a lightweight thumbnail (<see cref="ThumbnailDecodeWidth"/> = 400px) for camera strips and Wallboard Spotlight.
    /// </summary>
    public static Task<BitmapImage?> LoadThumbnailAsync(
        string? filePath,
        int decodePixelWidth = ThumbnailDecodeWidth,
        CancellationToken cancellationToken = default) =>
        LoadBitmapAsync(filePath, decodePixelWidth, maxRetries: 3, retryDelayMs: 500, cancellationToken);

    /// <summary>
    /// Loads the 1:1 high-resolution image (<c>photo/Main*.png</c> or unscaled overview) on-demand when requested by the technician.
    /// Automatically falls back to 4096px if the 17701x14208 (~1GB uncompressed) bitmap exceeds WIC/GPU texture limits.
    /// </summary>
    public static Task<BitmapImage?> LoadOneToOneBitmapAsync(
        string? filePath,
        CancellationToken cancellationToken = default) =>
        LoadBitmapAsync(filePath, decodePixelWidth: HighResFallbackDecodeWidth, maxRetries: 3, retryDelayMs: 500, cancellationToken);

    /// <summary>
    /// Synchronous decode helper (for legacy callers already running inside <see cref="Task.Run"/>).
    /// </summary>
    public static BitmapImage? DecodeFrozenBitmapFromFile(string? filePath, int decodePixelWidth = ThumbnailDecodeWidth)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        try
        {
            using var fs = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 65536,
                FileOptions.SequentialScan);
            return DecodeFrozenBitmapFromStream(fs, decodePixelWidth);
        }
        catch (Exception ex)
        {
            ErrorLoggerService.Default.LogException(ex, $"ImageHelper.DecodeFrozenBitmapFromFile({filePath})");
            return null;
        }
    }

    private static BitmapImage DecodeFrozenBitmapFromStream(Stream stream, int decodePixelWidth)
    {
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        if (decodePixelWidth > 0)
        {
            bmp.DecodePixelWidth = decodePixelWidth;
        }
        bmp.StreamSource = stream;
        bmp.EndInit();
        if (bmp.CanFreeze)
        {
            bmp.Freeze();
        }
        return bmp;
    }
}

