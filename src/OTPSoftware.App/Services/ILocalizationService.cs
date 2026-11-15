namespace OTPSoftware.App.Services;

using System.Windows;

/// <summary>
/// Runtime localization service supporting instant XAML DynamicResource dictionary swapping
/// and C# string lookup/formatting across English (en), Simplified Chinese (zh), and Vietnamese (vi).
/// </summary>
public interface ILocalizationService
{
    /// <summary>
    /// Currently active two-letter language code ("en", "zh", or "vi").
    /// </summary>
    string CurrentLanguage { get; }

    /// <summary>
    /// Supported two-letter language codes ("en", "zh", "vi").
    /// </summary>
    IReadOnlyList<string> SupportedLanguages { get; }

    /// <summary>
    /// Fired whenever <see cref="CurrentLanguage"/> changes. Argument is the new language code ("en", "zh", or "vi").
    /// </summary>
    event EventHandler<string>? LanguageChanged;

    /// <summary>
    /// Switches the active language at runtime, updating WPF MergedDictionaries if Application.Current is running.
    /// Returns true if the language code is valid and applied; false if null, empty, or unsupported.
    /// </summary>
    bool SetLanguage(string? languageCode);

    /// <summary>
    /// Resolves a localized string by key from the active language dictionary,
    /// falling back to English ("en") and then to the raw key if not found.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords", Justification = "Concise localization lookup method in C# codebase")]
    string Get(string key);

    /// <summary>
    /// Resolves a localized format string by key and formats it using InvariantCulture.
    /// </summary>
    string Format(string key, params object?[] args);
}

/// <summary>
/// Event arguments carrying the updated active language code for weak localization subscriptions.
/// </summary>
public sealed class LanguageChangedEventArgs(string languageCode) : EventArgs
{
    public string LanguageCode { get; } = languageCode;
}

/// <summary>
/// Weak-event manager for <see cref="ILocalizationService.LanguageChanged"/> so view models
/// bound to long-lived localization services can be garbage-collected even without explicit disposal.
/// </summary>
public sealed class LanguageChangedEventManager : WeakEventManager
{
    private LanguageChangedEventManager()
    {
    }

    public static void AddHandler(ILocalizationService source, EventHandler<LanguageChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        CurrentManager.ProtectedAddHandler(source, handler);
    }

    public static void RemoveHandler(ILocalizationService source, EventHandler<LanguageChangedEventArgs> handler)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(handler);
        CurrentManager.ProtectedRemoveHandler(source, handler);
    }

    protected override ListenerList NewListenerList() => new ListenerList<LanguageChangedEventArgs>();

    protected override void StartListening(object source)
    {
        if (source is ILocalizationService loc)
        {
            loc.LanguageChanged += OnSourceLanguageChanged;
        }
    }

    protected override void StopListening(object source)
    {
        if (source is ILocalizationService loc)
        {
            loc.LanguageChanged -= OnSourceLanguageChanged;
        }
    }

    private void OnSourceLanguageChanged(object? sender, string languageCode) =>
        DeliverEvent(sender, new LanguageChangedEventArgs(languageCode));

    private static LanguageChangedEventManager CurrentManager
    {
        get
        {
            Type managerType = typeof(LanguageChangedEventManager);
            if (GetCurrentManager(managerType) is not LanguageChangedEventManager manager)
            {
                manager = new LanguageChangedEventManager();
                SetCurrentManager(managerType, manager);
            }
            return manager;
        }
    }
}
