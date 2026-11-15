using System.Text.RegularExpressions;
using System.Xml.Linq;
using OTPSoftware.App.Services;
using OTPSoftware.App.ViewModels;
using OTPSoftware.Core.Models;
using OTPSoftware.Core.Services;
using Xunit;

namespace OTPSoftware.Tests;

/// <summary>
/// Automated verification suite for the Multi-Language (i18n) resource dictionaries,
/// MainWindow.xaml DynamicResource bindings, and headless LocalizationService behavior.
/// </summary>
public sealed partial class LocalizationDictionaryTests
{
    private static readonly XNamespace XamlXNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [GeneratedRegex(@"\{DynamicResource\s+([A-Za-z0-9_]+)\}", RegexOptions.CultureInvariant)]
    private static partial Regex DynamicResourceKeyRegex();

    [GeneratedRegex(@"(?:_localization\.(?:Get|Format)|SetStatusLoc)\(\s*""([A-Za-z0-9_]+)""", RegexOptions.CultureInvariant)]
    private static partial Regex CSharpLocalizationKeyRegex();

    [GeneratedRegex(@"\{(\d+)(?:[:,][^}]*)?\}", RegexOptions.CultureInvariant)]
    private static partial Regex FormatPlaceholderRegex();

    [Fact]
    public void LanguageDictionaries_HaveIdenticalKeysNoDuplicatesAndMatchingPlaceholders()
    {
        // Arrange
        var (enMap, enDuplicates) = LoadDictionaryEntries("Strings.en.xaml");
        var (zhMap, zhDuplicates) = LoadDictionaryEntries("Strings.zh.xaml");
        var (viMap, viDuplicates) = LoadDictionaryEntries("Strings.vi.xaml");

        // Assert: No duplicate keys
        Assert.Empty(enDuplicates);
        Assert.Empty(zhDuplicates);
        Assert.Empty(viDuplicates);

        // Assert: Expected minimum dictionary size and identical key sets
        Assert.True(enMap.Count >= 180, $"Expected at least 180 keys in Strings.en.xaml, found {enMap.Count}.");
        Assert.Equal(enMap.Count, zhMap.Count);
        Assert.Equal(enMap.Count, viMap.Count);

        var missingInZh = enMap.Keys.Except(zhMap.Keys, StringComparer.Ordinal).ToList();
        var extraInZh = zhMap.Keys.Except(enMap.Keys, StringComparer.Ordinal).ToList();
        var missingInVi = enMap.Keys.Except(viMap.Keys, StringComparer.Ordinal).ToList();
        var extraInVi = viMap.Keys.Except(enMap.Keys, StringComparer.Ordinal).ToList();

        Assert.True(missingInZh.Count == 0, $"Keys missing in Strings.zh.xaml: {string.Join(", ", missingInZh)}");
        Assert.True(extraInZh.Count == 0, $"Unexpected keys in Strings.zh.xaml: {string.Join(", ", extraInZh)}");
        Assert.True(missingInVi.Count == 0, $"Keys missing in Strings.vi.xaml: {string.Join(", ", missingInVi)}");
        Assert.True(extraInVi.Count == 0, $"Unexpected keys in Strings.vi.xaml: {string.Join(", ", extraInVi)}");

        // Assert: Non-empty values and identical format placeholder indices ({0}, {1}, ...)
        foreach (var (key, enValue) in enMap)
        {
            string zhValue = zhMap[key];
            string viValue = viMap[key];

            Assert.False(string.IsNullOrWhiteSpace(enValue), $"Empty English value for key '{key}'.");
            Assert.False(string.IsNullOrWhiteSpace(zhValue), $"Empty Chinese value for key '{key}'.");
            Assert.False(string.IsNullOrWhiteSpace(viValue), $"Empty Vietnamese value for key '{key}'.");

            var enPlaceholders = ExtractPlaceholderIndices(enValue);
            var zhPlaceholders = ExtractPlaceholderIndices(zhValue);
            var viPlaceholders = ExtractPlaceholderIndices(viValue);

            Assert.True(
                enPlaceholders.SetEquals(zhPlaceholders),
                $"Placeholder mismatch in Strings.zh.xaml for key '{key}': EN=[{string.Join(",", enPlaceholders)}], ZH=[{string.Join(",", zhPlaceholders)}]");
            Assert.True(
                enPlaceholders.SetEquals(viPlaceholders),
                $"Placeholder mismatch in Strings.vi.xaml for key '{key}': EN=[{string.Join(",", enPlaceholders)}], VI=[{string.Join(",", viPlaceholders)}]");
        }
    }

    [Fact]
    public void MainWindowXaml_AllDynamicResourceKeysExistInEnglishDictionary()
    {
        // Arrange
        var (enMap, _) = LoadDictionaryEntries("Strings.en.xaml");
        string mainWindowPath = ResolveMainWindowXamlPath();
        string xamlContent = File.ReadAllText(mainWindowPath);

        // Act
        var referencedKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DynamicResourceKeyRegex().Matches(xamlContent))
        {
            referencedKeys.Add(match.Groups[1].Value);
        }

        // Assert
        Assert.NotEmpty(referencedKeys);
        var missingKeys = referencedKeys.Where(k => !enMap.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missingKeys.Count == 0,
            $"MainWindow.xaml references DynamicResource keys missing from Strings.en.xaml: {string.Join(", ", missingKeys)}");

        // Also verify all C# localization keys in src/OTPSoftware.App exist in Strings.en.xaml
        string appProjectDir = Path.GetDirectoryName(mainWindowPath)!;
        var csharpKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (string csFile in Directory.EnumerateFiles(appProjectDir, "*.cs", SearchOption.AllDirectories))
        {
            if (csFile.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) ||
                csFile.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string csText = File.ReadAllText(csFile);
            foreach (Match match in CSharpLocalizationKeyRegex().Matches(csText))
            {
                csharpKeys.Add(match.Groups[1].Value);
            }
        }

        var missingCsKeys = csharpKeys.Where(k => !enMap.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        Assert.True(
            missingCsKeys.Count == 0,
            $"C# files reference localization keys missing from Strings.en.xaml: {string.Join(", ", missingCsKeys)}");
    }

    [Theory]
    [InlineData("en-US", "en")]
    [InlineData("EN", "en")]
    [InlineData("zh-CN", "zh")]
    [InlineData("zh-Hans", "zh")]
    [InlineData("vi-VN", "vi")]
    [InlineData("VI", "vi")]
    public void LocalizationService_NormalizesSupportedCultureCodes(string rawCode, string expectedNormalized)
    {
        var service = new LocalizationService(rawCode);
        Assert.Equal(expectedNormalized, service.CurrentLanguage);
    }

    [Fact]
    public void LocalizationService_SupportsRuntimeSwitchingFallbackAndUserStatePersistence()
    {
        // Arrange
        var service = new LocalizationService();
        var observedLanguages = new List<string>();
        service.LanguageChanged += (_, lang) => observedLanguages.Add(lang);

        // Assert initial default is English
        Assert.Equal("en", service.CurrentLanguage);
        Assert.Equal("Read Log", service.Get("Header_ReadLogBtn"));

        // Act & Assert: Switch to Chinese Simplified
        Assert.True(service.SetLanguage("zh"));
        Assert.Equal("zh", service.CurrentLanguage);
        Assert.Equal("读取日志", service.Get("Header_ReadLogBtn"));
        Assert.Contains("5", service.Format("Status_LoadedRecordsFormat", 5));

        // Act & Assert: Switch to Vietnamese
        Assert.True(service.SetLanguage("vi"));
        Assert.Equal("vi", service.CurrentLanguage);
        Assert.Equal("Đọc Log", service.Get("Header_ReadLogBtn"));
        Assert.Contains("5", service.Format("Status_LoadedRecordsFormat", 5));

        // Invalid language codes are rejected without changing CurrentLanguage
        Assert.False(service.SetLanguage("fr"));
        Assert.False(service.SetLanguage(""));
        Assert.False(service.SetLanguage(null!));
        Assert.Equal("vi", service.CurrentLanguage);
        Assert.Equal(["zh", "vi"], observedLanguages);

        // Unknown key falls back to key name safely
        Assert.Equal("NonExistent_Key_123", service.Get("NonExistent_Key_123"));

        // Verify UserState round-trip persistence via SettingsService and MainViewModel
        string tempDir = Path.Combine(Path.GetTempPath(), $"OTPSoftware_i18n_test_{Guid.NewGuid():N}");
        try
        {
            var settingsService = new SettingsService();
            settingsService.SaveUserState(tempDir, new UserState { Language = "zh", SelectedTab = 1 });

            var loadedState = settingsService.LoadUserState(tempDir);
            Assert.Equal("zh", loadedState.Language);

            using var vm = new MainViewModel(appRootDir: tempDir, settingsService: settingsService);
            Assert.Equal("zh", vm.CurrentLanguage);
            Assert.False(vm.IsLanguageEn);
            Assert.True(vm.IsLanguageZh);
            Assert.False(vm.IsLanguageVi);

            vm.SetLanguageCommand.Execute("vi");
            Assert.Equal("vi", vm.CurrentLanguage);
            Assert.True(vm.IsLanguageVi);
            Assert.Equal("Bật Thời gian thực", vm.RealtimeButtonText);
            Assert.Equal("Tất cả máy", vm.SourceFilterItems[0]);

            var persistedAfterCommand = settingsService.LoadUserState(tempDir);
            Assert.Equal("vi", persistedAfterCommand.Language);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }
    }

    private static (Dictionary<string, string> Entries, List<string> Duplicates) LoadDictionaryEntries(string fileName)
    {
        string path = ResolveLanguageDictionaryPath(fileName);
        var doc = XDocument.Load(path);
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        var duplicates = new List<string>();

        if (doc.Root is null)
            throw new InvalidOperationException($"Empty XML root in {path}");

        foreach (var element in doc.Root.Elements())
        {
            string? key = element.Attribute(XamlXNamespace + "Key")?.Value;
            if (string.IsNullOrWhiteSpace(key))
                continue;

            if (!entries.TryAdd(key, element.Value))
            {
                duplicates.Add(key);
            }
        }

        return (entries, duplicates);
    }

    private static HashSet<int> ExtractPlaceholderIndices(string formatString)
    {
        var indices = new HashSet<int>();
        foreach (Match match in FormatPlaceholderRegex().Matches(formatString))
        {
            if (int.TryParse(match.Groups[1].Value, out int idx))
            {
                indices.Add(idx);
            }
        }
        return indices;
    }

    private static string ResolveLanguageDictionaryPath(string fileName)
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "Resources", "Languages", fileName);
        if (File.Exists(candidate1))
            return candidate1;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate2 = Path.Combine(dir.FullName, "src", "OTPSoftware.App", "Resources", "Languages", fileName);
            if (File.Exists(candidate2))
                return candidate2;
        }

        throw new FileNotFoundException($"Could not locate language dictionary '{fileName}'.");
    }

    private static string ResolveMainWindowXamlPath()
    {
        string baseDir = AppContext.BaseDirectory;
        string candidate1 = Path.Combine(baseDir, "MainWindow.xaml");
        if (File.Exists(candidate1))
            return candidate1;

        var dir = new DirectoryInfo(baseDir);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate2 = Path.Combine(dir.FullName, "src", "OTPSoftware.App", "MainWindow.xaml");
            if (File.Exists(candidate2))
                return candidate2;
        }

        throw new FileNotFoundException("Could not locate MainWindow.xaml.");
    }
}
