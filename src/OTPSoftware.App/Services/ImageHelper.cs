namespace OTPSoftware.App.Services;

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using OTPSoftware.Core.Helpers;

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
    /// Bounded SMB open/decode timeout (ms) applied to UNC paths so stalled camera shares cannot hang worker threads.
    /// </summary>
    public const int DefaultSmbImageTimeoutMs = 4000;

    /// <summary>
    /// Asynchronously reads and decodes an image file on a background thread with automatic retry
    /// (default 3 retries with jitter) and sharing-violation protection to handle files actively flushed by AOI camera stations.
    /// </summary>
    public static Task<BitmapImage?> LoadBitmapAsync(
        string? filePath,
        int decodePixelWidth = PreviewDecodeWidth,
        int maxRetries = 3,
        int retryDelayMs = 500,
        CancellationToken cancellationToken = default) =>
        LoadBitmapCoreAsync(
            filePath,
            decodePixelWidth,
            maxRetries,
            retryDelayMs,
            DefaultSmbImageTimeoutMs,
            OpenSharedImageStream,
            cancellationToken);

    /// <summary>
    /// Overload allowing callers to override the bounded SMB/UNC open timeout while preserving
    /// <see cref="CancellationToken"/> as the trailing parameter.
    /// </summary>
    public static Task<BitmapImage?> LoadBitmapAsync(
        string? filePath,
        int decodePixelWidth,
        int maxRetries,
        int retryDelayMs,
        int smbOpenTimeoutMs,
        CancellationToken cancellationToken = default) =>
        LoadBitmapCoreAsync(
            filePath,
            decodePixelWidth,
            maxRetries,
            retryDelayMs,
            smbOpenTimeoutMs,
            OpenSharedImageStream,
            cancellationToken);

    internal static async Task<BitmapImage?> LoadBitmapCoreAsync(
        string? filePath,
        int decodePixelWidth,
        int maxRetries,
        int retryDelayMs,
        int smbOpenTimeoutMs,
        Func<string, CancellationToken, Stream> streamOpener,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        int totalAttempts = Math.Max(1, maxRetries);
        bool isUncPath = SecurityPathValidator.IsUncLikePath(filePath);
        bool enforceTimeout = smbOpenTimeoutMs > 0 && (isUncPath || smbOpenTimeoutMs != DefaultSmbImageTimeoutMs);

        for (int attempt = 1; attempt <= totalAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            if (enforceTimeout)
            {
                attemptCts.CancelAfter(smbOpenTimeoutMs);
            }

            try
            {
                CancellationToken attemptToken = attemptCts.Token;

                // Open via FileIoRetryHelper and decode directly on a worker thread to eliminate LOH MemoryStream copies,
                // while bounding stalled SMB/UNC opens so offline camera shares cannot block worker threads indefinitely.
                Task<BitmapImage?> decodeTask = Task.Run(
                    () => DecodeFrozenBitmapUsingOpener(filePath, decodePixelWidth, streamOpener, attemptToken),
                    attemptToken);

                BitmapImage? decoded;
                if (enforceTimeout)
                {
                    try
                    {
                        decoded = await decodeTask
                            .WaitAsync(TimeSpan.FromMilliseconds(smbOpenTimeoutMs), cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch (TimeoutException timeoutEx)
                    {
                        ObserveFaultedTask(decodeTask);
                        ErrorLoggerService.Default.LogException(
                            timeoutEx,
                            $"ImageHelper.LoadBitmapAsync timed out after {smbOpenTimeoutMs}ms ({filePath})");
                        return null;
                    }
                }
                else
                {
                    decoded = await decodeTask.ConfigureAwait(false);
                }

                if (decoded is not null)
                {
                    return decoded;
                }

                // Zero-byte file still flushing from camera station -> retry with jitter
                if (attempt < totalAttempts)
                {
                    int delayMs = ComputeRetryDelayWithJitterMs(retryDelayMs);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
                    continue;
                }

                return null;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException timeoutCancelEx) when (enforceTimeout && attemptCts.IsCancellationRequested)
            {
                ErrorLoggerService.Default.LogException(
                    timeoutCancelEx,
                    $"ImageHelper.LoadBitmapAsync timed out after {smbOpenTimeoutMs}ms ({filePath})");
                return null;
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (InvalidDataException invalidEx)
            {
                ErrorLoggerService.Default.LogException(invalidEx, $"ImageHelper.LoadBitmapAsync rejected invalid/oversized image ({filePath})");
                return null;
            }
            catch (OutOfMemoryException oomEx)
            {
                ErrorLoggerService.Default.LogException(oomEx, $"ImageHelper.LoadBitmapAsync OOM ({filePath})");
                if (decodePixelWidth == 0 || decodePixelWidth > HighResFallbackDecodeWidth)
                {
                    // Fallback to safe 4K decode width if 1:1 raw PNG exhausted unmanaged WIC memory
                    return await LoadBitmapCoreAsync(
                        filePath,
                        decodePixelWidth: HighResFallbackDecodeWidth,
                        maxRetries: 1,
                        retryDelayMs: retryDelayMs,
                        smbOpenTimeoutMs: smbOpenTimeoutMs,
                        streamOpener: streamOpener,
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                return null;
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or FileFormatException or NotSupportedException or InvalidOperationException)
            {
                // File is still locked or partially written by the camera station -> retry with jitter
                if (attempt < totalAttempts)
                {
                    int delayMs = ComputeRetryDelayWithJitterMs(retryDelayMs);
                    if (delayMs > 0)
                    {
                        await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
                    }
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

    private static Stream OpenSharedImageStream(string filePath, CancellationToken cancellationToken) =>
        FileIoRetryHelper.OpenSharedReadStreamWithRetry(
            filePath,
            fileShare: FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 65536,
            options: FileOptions.SequentialScan,
            maxAttempts: FileIoRetryHelper.DefaultMaxAttempts,
            cancellationToken: cancellationToken);

    private static BitmapImage? DecodeFrozenBitmapUsingOpener(
        string filePath,
        int decodePixelWidth,
        Func<string, CancellationToken, Stream> streamOpener,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using Stream fs = streamOpener(filePath, cancellationToken);
        if (fs.CanSeek && fs.Length == 0)
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return DecodeFrozenBitmapFromStream(fs, decodePixelWidth);
    }

    private static int ComputeRetryDelayWithJitterMs(int baseDelayMs)
    {
        if (baseDelayMs <= 0)
        {
            return 0;
        }

        int jitterCap = Math.Max(1, baseDelayMs / 4);
        return baseDelayMs + Random.Shared.Next(0, jitterCap + 1);
    }

    private static void ObserveFaultedTask(Task task)
    {
        if (task.IsCompleted)
        {
            _ = task.Exception;
            return;
        }

        _ = task.ContinueWith(
            static t => _ = t.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
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
    /// Upper pixel bound per axis before rejecting malformed headers / WIC decompression bombs.
    /// Accommodates full-resolution AOI raw captures (e.g., 17701x14208) while blocking extreme frames.
    /// </summary>
    public const int MaxAllowedImageDimension = 24_576;

    /// <summary>
    /// Loads the 1:1 high-resolution image (<c>photo/Main*.png</c> or unscaled overview) on-demand when requested by the technician.
    /// Automatically falls back to 4096px if the 17701x14208 (~1GB uncompressed) bitmap exceeds WIC/GPU texture limits.
    /// </summary>
    public static Task<BitmapImage?> LoadOneToOneBitmapAsync(
        string? filePath,
        CancellationToken cancellationToken = default) =>
        LoadBitmapAsync(filePath, decodePixelWidth: 0, maxRetries: 3, retryDelayMs: 500, cancellationToken);

    /// <summary>
    /// Synchronous decode helper (for legacy callers already running inside <see cref="Task.Run"/>).
    /// </summary>
    public static BitmapImage? DecodeFrozenBitmapFromFile(
        string? filePath,
        int decodePixelWidth = ThumbnailDecodeWidth,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            return null;
        }

        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return DecodeFrozenBitmapUsingOpener(filePath, decodePixelWidth, OpenSharedImageStream, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OutOfMemoryException oomEx)
        {
            ErrorLoggerService.Default.LogException(oomEx, $"ImageHelper.DecodeFrozenBitmapFromFile OOM ({filePath})");
            if (decodePixelWidth == 0 || decodePixelWidth > HighResFallbackDecodeWidth)
            {
                return DecodeFrozenBitmapFromFile(filePath, HighResFallbackDecodeWidth, cancellationToken);
            }
            return null;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ErrorLoggerService.Default.LogException(ex, $"ImageHelper.DecodeFrozenBitmapFromFile({filePath})");
            return null;
        }
    }

    /// <summary>
    /// Inspects image header metadata using delayed creation to block WIC decompression bombs
    /// before allocating pixel buffers.
    /// </summary>
    public static bool ValidateImageDimensionsSafely(
        Stream imageStream,
        int maxAllowedDimension = MaxAllowedImageDimension)
    {
        if (imageStream is null || !imageStream.CanRead || !imageStream.CanSeek || maxAllowedDimension <= 0)
        {
            return false;
        }

        long originalPosition = imageStream.Position;
        try
        {
            imageStream.Position = 0;
            var decoder = BitmapDecoder.Create(
                imageStream,
                BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);

            if (decoder.Frames.Count == 0)
            {
                return false;
            }

            BitmapFrame frame = decoder.Frames[0];
            return frame.PixelWidth > 0 &&
                   frame.PixelHeight > 0 &&
                   frame.PixelWidth <= maxAllowedDimension &&
                   frame.PixelHeight <= maxAllowedDimension;
        }
        catch (Exception ex) when (ex is NotSupportedException or FileFormatException or IOException or InvalidOperationException or ArgumentException)
        {
            return false;
        }
        finally
        {
            if (imageStream.CanSeek)
            {
                imageStream.Position = originalPosition;
            }
        }
    }

    private static BitmapImage DecodeFrozenBitmapFromStream(Stream stream, int decodePixelWidth)
    {
        if (!ValidateImageDimensionsSafely(stream))
        {
            throw new InvalidDataException($"Image failed dimension safety check (max {MaxAllowedImageDimension}px per axis).");
        }

        if (stream.CanSeek)
        {
            stream.Position = 0;
        }

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

