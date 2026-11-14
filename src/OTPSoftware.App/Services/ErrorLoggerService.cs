using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;

namespace OTPSoftware.App.Services;

/// <summary>
/// Thread-safe error and diagnostic logger writing to logs/gui-error.log under the application root directory.
/// </summary>
public sealed class ErrorLoggerService
{
    private static readonly object SyncRoot = new();

    public static ErrorLoggerService Default { get; set; } = new(App.ResolveAppRootDir());

    public ErrorLoggerService(string? appRootDir = null)
    {
        AppRootDir = string.IsNullOrWhiteSpace(appRootDir)
            ? App.ResolveAppRootDir()
            : appRootDir;
    }

    public string AppRootDir
    {
        get => field;
        set => field = string.IsNullOrWhiteSpace(value) ? App.ResolveAppRootDir() : value;
    } = string.Empty;

    public string LogDirectory => Path.Combine(AppRootDir, "logs");

    public string LogFilePath => Path.Combine(LogDirectory, "gui-error.log");

    public void LogError(string message, Exception? exception = null)
    {
        string detail = exception?.ToString() ?? string.Empty;
        LogError(message, detail);
    }

    public void LogError(string message, string? detail)
    {
        try
        {
            lock (SyncRoot)
            {
                string logDir = LogDirectory;
                if (!Directory.Exists(logDir))
                {
                    Directory.CreateDirectory(logDir);
                }

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                string entry = string.IsNullOrWhiteSpace(detail)
                    ? $"[{timestamp}] {message}\r\n\r\n"
                    : $"[{timestamp}] {message}\r\n{detail}\r\n\r\n";

                if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > 10 * 1024 * 1024)
                {
                    string archivePath = Path.Combine(logDir, "gui-error.1.log");
                    File.Move(LogFilePath, archivePath, overwrite: true);
                }

                File.AppendAllText(LogFilePath, entry, Encoding.UTF8);
            }
        }
        catch
        {
            // Swallow secondary logging failures (e.g. read-only directory) so UI never crashes on logging.
        }
    }

    public void LogException(Exception exception, string? context = null)
    {
        if (exception is null) return;
        string header = string.IsNullOrWhiteSpace(context)
            ? $"Unhandled exception: {exception.Message}"
            : $"{context}: {exception.Message}";
        LogError(header, exception.ToString());
    }

    public void LogDiagnostic(string message)
    {
        LogError($"[DIAG] {message}", string.Empty);
    }

    public void HandleStartupError(string message, Exception? exception = null, bool showMessageBox = true)
    {
        LogError(message, exception);

        try
        {
            Console.Error.WriteLine($"[STARTUP ERROR] {message}");
            if (exception is not null)
            {
                Console.Error.WriteLine(exception.ToString());
            }
        }
        catch
        {
            // Ignore console redirect issues
        }

        if (!showMessageBox) return;

        try
        {
            string detail = exception is not null ? $"\n\nException Details:\n{exception}" : string.Empty;
            MessageBox.Show(
                $"{message}{detail}\n\nDetails have been logged to: logs\\gui-error.log",
                "OTP Log Monitor - Startup Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        catch
        {
            // Ignore UI dialog errors if windowing subsystem is unavailable.
        }
    }
}
