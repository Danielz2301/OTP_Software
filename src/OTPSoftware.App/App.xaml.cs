using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Services;

namespace OTPSoftware.App;

/// <summary>
/// Application entry point, CLI parameter resolution, and global unhandled exception bootstrapper.
/// </summary>
public partial class App : Application, IDisposable
{
    private ErrorLoggerService _errorLogger = ErrorLoggerService.Default;
    private LocalizationService _localizationService = LocalizationService.Default;
    private MainViewModel? _mainViewModel;

    public App()
    {
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        try
        {
            base.OnStartup(e);

            var (initialLogFile, initialLogFile2, cliRootDir) = ParseCliArguments(e.Args);
            string appRootDir = ResolveAppRootDir(cliRootDir);

            _errorLogger = new ErrorLoggerService(appRootDir);
            ErrorLoggerService.Default = _errorLogger;

            var settingsService = new SettingsService();
            var userState = settingsService.LoadUserState(appRootDir);
            var localizationService = new LocalizationService(userState.Language);
            LocalizationService.Default = localizationService;
            _localizationService = localizationService;

            _mainViewModel = new MainViewModel(
                appRootDir: appRootDir,
                initialLogFile: initialLogFile,
                initialLogFile2: initialLogFile2,
                settingsService: settingsService,
                errorLogger: _errorLogger,
                localizationService: _localizationService);

            var window = new MainWindow
            {
                DataContext = _mainViewModel,
                Language = XmlLanguage.GetLanguage(LocalizationService.GetIetfLanguageTag(_localizationService.CurrentLanguage))
            };

            MainWindow = window;

            if (e.Args.Any(a => string.Equals(a, "--validate-startup", StringComparison.OrdinalIgnoreCase)))
            {
                window.Show();
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
                timer.Tick += (s, ev) =>
                {
                    timer.Stop();
                    frame.Continue = false;
                };
                timer.Start();
                Dispatcher.PushFrame(frame);

                Console.WriteLine("STARTUP_VALIDATION_SUCCESS");
                Shutdown(0);
                return;
            }

            window.Show();
        }
        catch (Exception ex)
        {
            bool isValidation = e.Args?.Any(a => string.Equals(a, "--validate-startup", StringComparison.OrdinalIgnoreCase)) == true;
            _errorLogger.HandleStartupError(
                $"Failed to start application: {ex.Message}",
                ex,
                showMessageBox: !isValidation);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try
        {
            Dispose(true);
        }
        catch (Exception ex)
        {
            _errorLogger.LogException(ex, "App.OnExit");
        }
        finally
        {
            base.OnExit(e);
        }
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Interlocked.Exchange(ref _mainViewModel, null)?.Dispose();
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        _errorLogger.LogException(e.Exception, "DispatcherUnhandledException");
        try
        {
            if (_mainViewModel?.IsKioskMode == true)
            {
                _mainViewModel.SetStatusLoc("Status_UiErrorFormat", "#EF4444", e.Exception.Message);
            }
            else
            {
                MessageBox.Show(
                    _localizationService.Format("Dlg_UnexpectedUiErrorFormat", e.Exception.Message),
                    _localizationService.Get("Dlg_AppErrorTitle"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        catch
        {
            // Ignore secondary dialog error
        }

        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            _errorLogger.LogException(ex, "AppDomain.UnhandledException");
        }
        else
        {
            _errorLogger.LogError("AppDomain.UnhandledException", e.ExceptionObject?.ToString());
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        _errorLogger.LogException(e.Exception, "TaskScheduler.UnobservedTaskException");
        e.SetObserved();
    }

    public static string ResolveAppRootDir(string? explicitRootDir = null)
    {
        if (!string.IsNullOrWhiteSpace(explicitRootDir) && Directory.Exists(explicitRootDir))
        {
            return Path.GetFullPath(explicitRootDir);
        }

        string baseDir = AppContext.BaseDirectory;
        string? foundFromBase = ProbeUpwardForRoot(baseDir);
        if (foundFromBase is not null)
        {
            return foundFromBase;
        }

        string currentDir = Directory.GetCurrentDirectory();
        string? foundFromCurrent = ProbeUpwardForRoot(currentDir);
        if (foundFromCurrent is not null)
        {
            return foundFromCurrent;
        }

        return !string.IsNullOrWhiteSpace(baseDir) && Directory.Exists(baseDir) ? baseDir : currentDir;
    }

    private static string? ProbeUpwardForRoot(string? startDirectory)
    {
        if (string.IsNullOrWhiteSpace(startDirectory))
            return null;

        // 1. Prefer repository/solution root when launching from bin\... where config/settings.json is copied alongside the binary.
        return ProbeUpwardMatching(startDirectory, dir =>
                   File.Exists(Path.Combine(dir, "OTPSoftware.sln")) &&
                   File.Exists(Path.Combine(dir, "config", "settings.json")))
               ?? ProbeUpwardMatching(startDirectory, dir => File.Exists(Path.Combine(dir, "config", "settings.json")))
               ?? ProbeUpwardMatching(startDirectory, dir => File.Exists(Path.Combine(dir, "OTPSoftware.sln")));
    }

    private static string? ProbeUpwardMatching(string startDirectory, Func<string, bool> predicate)
    {
        var dir = new DirectoryInfo(startDirectory);
        for (int depth = 0; depth < 8 && dir is not null; depth++, dir = dir.Parent)
        {
            if (predicate(dir.FullName))
            {
                return dir.FullName;
            }
        }
        return null;
    }

    public static (string InitialLogFile, string InitialLogFile2, string AppRootDir) ParseCliArguments(string[]? args)
    {
        string logFile1 = string.Empty;
        string logFile2 = string.Empty;
        string appRootDir = string.Empty;

        if (args is null || args.Length == 0)
            return (logFile1, logFile2, appRootDir);

        var positional = new List<string>();

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;

            string normalized = arg.TrimStart('-', '/');
            if (string.Equals(normalized, "InitialLogFile", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(normalized, "LogFile", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) logFile1 = args[++i];
            }
            else if (string.Equals(normalized, "InitialLogFile2", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "LogFile2", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) logFile2 = args[++i];
            }
            else if (string.Equals(normalized, "AppRootDir", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(normalized, "RootDir", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length) appRootDir = args[++i];
            }
            else if (!arg.StartsWith('-'))
            {
                positional.Add(arg);
            }
        }

        if (string.IsNullOrEmpty(logFile1) && positional.Count >= 1)
            logFile1 = positional[0];
        if (string.IsNullOrEmpty(logFile2) && positional.Count >= 2)
            logFile2 = positional[1];
        if (string.IsNullOrEmpty(appRootDir) && positional.Count >= 3)
            appRootDir = positional[2];

        return (logFile1, logFile2, appRootDir);
    }
}
