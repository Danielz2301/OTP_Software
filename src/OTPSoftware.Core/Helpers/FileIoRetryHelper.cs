namespace OTPSoftware.Core.Helpers;

/// <summary>
/// Bounded retry helpers with decorrelated jitter for transient file sharing/lock violations
/// (<c>0x80070020</c> / <c>0x80070021</c>) encountered when reading or writing live industrial files.
/// </summary>
internal static class FileIoRetryHelper
{
    private const int HResultSharingViolation = unchecked((int)0x80070020);
    private const int HResultLockViolation = unchecked((int)0x80070021);

    internal const int DefaultMaxAttempts = 3;
    internal const int DefaultBaseDelayMs = 25;

    internal static bool IsTransientSharingViolation(IOException ex) =>
        ex.HResult is HResultSharingViolation or HResultLockViolation;

    internal static int ComputeBackoffDelayMs(int attemptIndex, int baseDelayMs = DefaultBaseDelayMs)
    {
        int ceiling = baseDelayMs * (1 << Math.Min(Math.Max(0, attemptIndex), 4));
        return Random.Shared.Next(baseDelayMs, ceiling + 1);
    }

    internal static FileStream OpenSharedReadStreamWithRetry(
        string path,
        FileShare fileShare = FileShare.ReadWrite | FileShare.Delete,
        int bufferSize = 4096,
        FileOptions options = FileOptions.SequentialScan,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        int attempts = Math.Max(1, maxAttempts);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(path, FileMode.Open, FileAccess.Read, fileShare, bufferSize, options);
            }
            catch (IOException ex) when (attempt + 1 < attempts && IsTransientSharingViolation(ex))
            {
                WaitBeforeRetry(attempt, cancellationToken);
            }
        }
    }

    internal static T ExecuteWithSharingRetry<T>(
        Func<T> action,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        int attempts = Math.Max(1, maxAttempts);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return action();
            }
            catch (IOException ex) when (attempt + 1 < attempts && IsTransientSharingViolation(ex))
            {
                WaitBeforeRetry(attempt, cancellationToken);
            }
        }
    }

    internal static async Task ExecuteWithSharingRetryAsync(
        Func<Task> action,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(action);
        int attempts = Math.Max(1, maxAttempts);

        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await action().ConfigureAwait(false);
                return;
            }
            catch (IOException ex) when (attempt + 1 < attempts && IsTransientSharingViolation(ex))
            {
                int delayMs = ComputeBackoffDelayMs(attempt);
                await Task.Delay(delayMs, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static void WaitBeforeRetry(int attemptIndex, CancellationToken cancellationToken)
    {
        int delayMs = ComputeBackoffDelayMs(attemptIndex);
        if (cancellationToken.CanBeCanceled)
        {
            if (cancellationToken.WaitHandle.WaitOne(delayMs))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        else
        {
            Thread.Sleep(delayMs);
        }
    }
}
