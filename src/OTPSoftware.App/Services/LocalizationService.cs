using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using System.Xml.Linq;

namespace OTPSoftware.App.Services;

/// <summary>
/// Manages runtime XAML ResourceDictionary swapping and C# localized string lookups for en / zh / vi.
/// Operates both inside the WPF UI process and in headless unit test environments.
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private static readonly string[] SupportedCodes = ["en", "zh", "vi"];
    private readonly Dictionary<string, ResourceDictionary> _cache = new(StringComparer.OrdinalIgnoreCase);
    private ResourceDictionary _enDict;
    private ResourceDictionary _activeDict;
    private ResourceDictionary? _attachedOverlayDict;
    private bool _enAttachedToApp;
    private string _currentLanguage = "en";

    public static LocalizationService Default { get; set; } = new();

    public string CurrentLanguage => _currentLanguage;

    public IReadOnlyList<string> SupportedLanguages => SupportedCodes;

    public event EventHandler<string>? LanguageChanged;

    public LocalizationService(string initialLanguage = "en")
    {
        _enDict = GetOrLoadDictionary("en");
        _activeDict = _enDict;
        if (!SetLanguage(initialLanguage))
        {
            SetLanguage("en");
        }
    }

    public static string NormalizeLanguageCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
            return string.Empty;

        string trimmed = code.Trim();
        if (trimmed.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(trimmed, "en", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("en-", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("en_", StringComparison.OrdinalIgnoreCase)
                ? "en"
                : string.Empty;
        }

        if (trimmed.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(trimmed, "zh", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("zh-", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("zh_", StringComparison.OrdinalIgnoreCase)
                ? "zh"
                : string.Empty;
        }

        if (trimmed.StartsWith("vi", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(trimmed, "vi", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("vi-", StringComparison.OrdinalIgnoreCase) ||
                   trimmed.StartsWith("vi_", StringComparison.OrdinalIgnoreCase)
                ? "vi"
                : string.Empty;
        }

        return string.Empty;
    }

    public static string GetIetfLanguageTag(string? code) => NormalizeLanguageCode(code) switch
    {
        "zh" => "zh-CN",
        "vi" => "vi-VN",
        _ => "en-US"
    };

    public bool SetLanguage(string? languageCode)
    {
        string normalized = NormalizeLanguageCode(languageCode);
        if (string.IsNullOrEmpty(normalized))
            return false;

        bool changed = !string.Equals(_currentLanguage, normalized, StringComparison.OrdinalIgnoreCase);
        _currentLanguage = normalized;
        _activeDict = string.Equals(normalized, "en", StringComparison.OrdinalIgnoreCase)
            ? _enDict
            : GetOrLoadDictionary(normalized);

        SyncWithApplicationResources(normalized);

        if (changed)
        {
            LanguageChanged?.Invoke(this, normalized);
        }

        return true;
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key))
            return string.Empty;

        if (_activeDict.Contains(key) && _activeDict[key] is string activeVal)
            return activeVal;

        if (!ReferenceEquals(_activeDict, _enDict) && _enDict.Contains(key) && _enDict[key] is string enVal)
            return enVal;

        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        if (args is null || args.Length == 0)
            return template;

        try
        {
            return string.Format(CultureInfo.InvariantCulture, template, args);
        }
        catch (FormatException)
        {
            return template;
        }
    }

    private void SyncWithApplicationResources(string normalizedCode)
    {
        var app = Application.Current;
        if (app is null)
            return;

        if (app.Dispatcher is not null && !app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.Invoke(() => ApplyToApplicationOnUiThread(app, normalizedCode));
            return;
        }

        ApplyToApplicationOnUiThread(app, normalizedCode);
    }

    private void ApplyToApplicationOnUiThread(Application app, string normalizedCode)
    {
        var merged = app.Resources.MergedDictionaries;

        if (!_enAttachedToApp || !merged.Contains(_enDict))
        {
            merged.Add(_enDict);
            _enAttachedToApp = true;
        }

        if (string.Equals(normalizedCode, "en", StringComparison.OrdinalIgnoreCase))
        {
            if (_attachedOverlayDict is not null)
            {
                merged.Remove(_attachedOverlayDict);
                _attachedOverlayDict = null;
            }
        }
        else
        {
            if (!ReferenceEquals(_attachedOverlayDict, _activeDict))
            {
                if (_attachedOverlayDict is not null)
                {
                    merged.Remove(_attachedOverlayDict);
                }
                merged.Add(_activeDict);
                _attachedOverlayDict = _activeDict;
            }
        }

        if (app.MainWindow is not null)
        {
            try
            {
                app.MainWindow.Language = XmlLanguage.GetLanguage(GetIetfLanguageTag(normalizedCode));
            }
            catch
            {
                // Ignore if XmlLanguage cannot be set in non-standard window states
            }
        }
    }

    private ResourceDictionary GetOrLoadDictionary(string code)
    {
        if (_cache.TryGetValue(code, out var existing))
            return existing;

        var dict = LoadDictionaryCore(code);
        _cache[code] = dict;
        return dict;
    }

    private static ResourceDictionary LoadDictionaryCore(string code)
    {
        try
        {
            var uri = new Uri($"/OTPSoftware.App;component/Resources/Languages/Strings.{code}.xaml", UriKind.Relative);
            if (Application.LoadComponent(uri) is ResourceDictionary componentDict)
            {
                return componentDict;
            }
        }
        catch
        {
            // Fallback to XML file parsing when running in environments without pack URI registration
        }

        string fileName = $"Strings.{code}.xaml";
        string[] candidatePaths =
        [
            Path.Combine(AppContext.BaseDirectory, "Resources", "Languages", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName)
        ];

        foreach (string path in candidatePaths)
        {
            if (File.Exists(path))
            {
                return ParseXamlFileToDictionary(path);
            }
        }

        return new ResourceDictionary();
    }

    private static ResourceDictionary ParseXamlFileToDictionary(string filePath)
    {
        var dict = new ResourceDictionary();
        var doc = XDocument.Load(filePath, LoadOptions.PreserveWhitespace);
        if (doc.Root is null)
            return dict;

        XNamespace xNs = "http://schemas.microsoft.com/winfx/2006/xaml";
        foreach (var el in doc.Root.Elements())
        {
            string? key = el.Attribute(xNs + "Key")?.Value;
            if (!string.IsNullOrEmpty(key))
            {
                dict[key] = el.Value;
            }
        }

        return dict;
    }
}
