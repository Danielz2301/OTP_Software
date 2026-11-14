using System.Text.Json;
using OTPSoftware.Core.Display;
using OTPSoftware.Core.Interfaces;
using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Services;

/// <summary>
/// Manages system machine/path configuration (config/settings.json) and persisted UI session state (config/user-state.json).
/// Supports Tier 1 defaults, Tier 2 JSON Array and legacy JSON Object overrides, path fallback heuristics, and corporate visual palettes.
/// </summary>
public sealed class SettingsService : ISettingsService
{
    public OtpSettings GetDefaultSettings()
    {
        var m120 = new MachineConfig
        {
            Name = "MMAPI120",
            Enabled = true,
            Color = "#74B9FF",
            BgColor = "#1B3B52",
            LogPath = @"\\192.168.3.206\c\GoArms\logs\OTP_1.log",
            MesLogPath = @"\\192.168.3.206\c\GoArms\logs\MES_1.log",
            Images =
            [
                new CameraImageConfig(@"\\192.168.3.203\e\LOG", "B1"),
                new CameraImageConfig(@"\\192.168.3.204\e\LOG", "B2")
            ]
        };

        var m110 = new MachineConfig
        {
            Name = "MMAPI110",
            Enabled = true,
            Color = "#A29BFE",
            BgColor = "#3A2A54",
            LogPath = @"\\192.168.3.205\c\GoArms\logs\OTP_1.log",
            MesLogPath = @"\\192.168.3.205\c\GoArms\logs\MES_1.log",
            Images =
            [
                new CameraImageConfig(@"\\192.168.3.201\e\LOG", "A1"),
                new CameraImageConfig(@"\\192.168.3.202\e\LOG", "A2")
            ]
        };

        var machines = new List<MachineConfig> { m120, m110 };
        var dict = new Dictionary<string, MachineConfig>(StringComparer.OrdinalIgnoreCase)
        {
            [m120.Name] = m120,
            [m110.Name] = m110
        };

        return new OtpSettings
        {
            Machines = machines,
            MachinesByName = dict
        };
    }

    public static string GetUncServerName(string? uncPath)
    {
        if (string.IsNullOrWhiteSpace(uncPath) || !uncPath.StartsWith(@"\\", StringComparison.Ordinal))
            return string.Empty;

        string trimmed;
        if (uncPath.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            trimmed = uncPath[@"\\?\UNC\".Length..];
        else if (uncPath.StartsWith(@"\\?\", StringComparison.Ordinal))
            return string.Empty;
        else
            trimmed = uncPath.TrimStart('\\');

        int slashIdx = trimmed.IndexOf('\\');
        string host = slashIdx >= 0 ? trimmed[..slashIdx] : trimmed;
        return string.IsNullOrWhiteSpace(host) ? string.Empty : host;
    }

    public MachineVisualStyle GetMachineVisualStyle(string? sourceName, OtpSettings? settings = null) =>
        GuiDisplayState.GetMachineVisualStyle(sourceName, settings?.MachinesByName);

    public OtpSettings LoadSettings(string rootDir)
    {
        var defaults = GetDefaultSettings();
        if (string.IsNullOrWhiteSpace(rootDir))
            return defaults;

        string settingsFile = Path.Combine(rootDir, "config", "settings.json");
        if (!File.Exists(settingsFile))
            return defaults;

        try
        {
            string jsonText = File.ReadAllText(settingsFile);
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return defaults with
                {
                    LoadError = $"Failed to read '{settingsFile}' (Root JSON value must be an object) - using default configuration."
                };
            }

            var warnings = new List<string>();

            WallboardThresholds wallboard = defaults.Wallboard;
            if (root.TryGetProperty("wallboard", out var wbProp))
            {
                if (wbProp.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var parsedWb = JsonSerializer.Deserialize(wbProp, OtpJsonContext.Default.WallboardThresholds);
                        if (parsedWb is not null)
                        {
                            wallboard = parsedWb;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"wallboard section invalid ({ex.Message})");
                    }
                }
                else
                {
                    warnings.Add("wallboard section is not a JSON object");
                }
            }

            AutoShiftExportConfig autoShiftExport = defaults.AutoShiftExport;
            if (root.TryGetProperty("autoShiftExport", out var expProp))
            {
                if (expProp.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var parsedExp = JsonSerializer.Deserialize(expProp, OtpJsonContext.Default.AutoShiftExportConfig);
                        if (parsedExp is not null)
                        {
                            autoShiftExport = parsedExp;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"autoShiftExport section invalid ({ex.Message})");
                    }
                }
                else
                {
                    warnings.Add("autoShiftExport section is not a JSON object");
                }
            }

            IReadOnlyDictionary<string, MesParameterSpec>? mesSpecs = defaults.MesSpecs;
            if (root.TryGetProperty("mesSpecs", out var mesProp))
            {
                if (mesProp.ValueKind == JsonValueKind.Object)
                {
                    try
                    {
                        var parsedMes = JsonSerializer.Deserialize(mesProp, OtpJsonContext.Default.DictionaryStringMesParameterSpec);
                        if (parsedMes is not null)
                        {
                            mesSpecs = new Dictionary<string, MesParameterSpec>(parsedMes, StringComparer.OrdinalIgnoreCase);
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"mesSpecs section invalid ({ex.Message})");
                    }
                }
                else
                {
                    warnings.Add("mesSpecs section is not a JSON object");
                }
            }

            string? sectionError = warnings.Count > 0 ? string.Join("; ", warnings) : null;

            if (!root.TryGetProperty("machines", out var machinesElement))
                return defaults with { Wallboard = wallboard, AutoShiftExport = autoShiftExport, MesSpecs = mesSpecs, LoadError = sectionError };

            var existingByName = new Dictionary<string, MachineConfig>(defaults.MachinesByName, StringComparer.OrdinalIgnoreCase);

            if (machinesElement.ValueKind == JsonValueKind.Array)
            {
                var ordered = new List<MachineConfig>();
                var orderedIndexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var customByName = new Dictionary<string, MachineConfig>(StringComparer.OrdinalIgnoreCase);
                int idx = 0;
                int itemIndex = -1;
                foreach (var item in machinesElement.EnumerateArray())
                {
                    itemIndex++;
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add($"machines[{itemIndex}] is not a JSON object");
                        continue;
                    }

                    if (!item.TryGetProperty("name", out var nameProp) || nameProp.ValueKind != JsonValueKind.String)
                    {
                        warnings.Add($"machines[{itemIndex}] missing valid string 'name'");
                        continue;
                    }

                    string name = nameProp.GetString()?.Trim() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        warnings.Add($"machines[{itemIndex}] has empty 'name'");
                        continue;
                    }

                    try
                    {
                        existingByName.TryGetValue(name, out var existing);
                        var merged = ParseMachineConfig(name, item, idx++, existing);
                        customByName[name] = merged;
                        if (orderedIndexByName.TryGetValue(name, out int replaceAt))
                        {
                            ordered[replaceAt] = merged;
                        }
                        else
                        {
                            orderedIndexByName[name] = ordered.Count;
                            ordered.Add(merged);
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"machine '{name}' invalid ({ex.Message})");
                    }
                }

                return new OtpSettings
                {
                    Machines = ordered.Count > 0 ? ordered : defaults.Machines,
                    MachinesByName = ordered.Count > 0 ? customByName : defaults.MachinesByName,
                    Wallboard = wallboard,
                    AutoShiftExport = autoShiftExport,
                    MesSpecs = mesSpecs,
                    LoadError = warnings.Count > 0 ? $"Partial settings warning in '{settingsFile}': {string.Join("; ", warnings)}" : null
                };
            }
            else if (machinesElement.ValueKind == JsonValueKind.Object)
            {
                var machineList = new List<MachineConfig>(defaults.Machines);
                int idx = machineList.Count;

                foreach (var prop in machinesElement.EnumerateObject())
                {
                    string name = prop.Name.Trim();
                    if (string.IsNullOrWhiteSpace(name))
                        continue;

                    if (prop.Value.ValueKind != JsonValueKind.Object)
                    {
                        warnings.Add($"machine '{name}' is not a JSON object");
                        continue;
                    }

                    try
                    {
                        existingByName.TryGetValue(name, out var existing);
                        var merged = ParseMachineConfig(name, prop.Value, idx, existing);
                        existingByName[name] = merged;

                        int existingIdx = machineList.FindIndex(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
                        if (existingIdx >= 0)
                        {
                            machineList[existingIdx] = merged;
                        }
                        else
                        {
                            machineList.Add(merged);
                            idx++;
                        }
                    }
                    catch (Exception ex)
                    {
                        warnings.Add($"machine '{name}' invalid ({ex.Message})");
                    }
                }

                return new OtpSettings
                {
                    Machines = machineList,
                    MachinesByName = existingByName,
                    Wallboard = wallboard,
                    AutoShiftExport = autoShiftExport,
                    MesSpecs = mesSpecs,
                    LoadError = warnings.Count > 0 ? $"Partial settings warning in '{settingsFile}': {string.Join("; ", warnings)}" : null
                };
            }

            warnings.Add("'machines' must be a JSON array or object");
            return defaults with
            {
                Wallboard = wallboard,
                AutoShiftExport = autoShiftExport,
                MesSpecs = mesSpecs,
                LoadError = $"Partial settings warning in '{settingsFile}': {string.Join("; ", warnings)}"
            };
        }
        catch (Exception ex)
        {
            return defaults with
            {
                LoadError = $"Failed to read '{settingsFile}' ({ex.Message}) - using default configuration."
            };
        }
    }

    private static MachineVisualStyle GetDefaultStyle(int index, string name)
    {
        if (string.Equals(name, "MMAPI120", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, "MMAPI110", StringComparison.OrdinalIgnoreCase))
        {
            return GuiDisplayState.GetMachineVisualStyle(name);
        }

        return GuiDisplayState.CorporatePalette[Math.Abs(index) % GuiDisplayState.CorporatePalette.Length];
    }

    private static MachineConfig ParseMachineConfig(string name, JsonElement element, int index, MachineConfig? existing)
    {
        var style = GetDefaultStyle(index, name);
        bool enabled = existing?.Enabled ?? true;
        string color = existing?.Color ?? style.Foreground;
        string bgColor = existing?.BgColor ?? style.Background;
        string logPath = existing?.LogPath ?? string.Empty;
        string mesLogPath = existing?.MesLogPath ?? string.Empty;
        var images = existing?.Images != null ? new List<CameraImageConfig>(existing.Images) : [];

        if (element.TryGetProperty("enabled", out var enabledProp) &&
            (enabledProp.ValueKind == JsonValueKind.True || enabledProp.ValueKind == JsonValueKind.False))
        {
            enabled = enabledProp.GetBoolean();
        }

        if (element.TryGetProperty("color", out var colorProp) && colorProp.ValueKind == JsonValueKind.String)
        {
            string? c = colorProp.GetString();
            if (!string.IsNullOrWhiteSpace(c)) color = c;
        }

        if (element.TryGetProperty("bgColor", out var bgColorProp) && bgColorProp.ValueKind == JsonValueKind.String)
        {
            string? bg = bgColorProp.GetString();
            if (!string.IsNullOrWhiteSpace(bg)) bgColor = bg;
        }

        if (element.TryGetProperty("logPath", out var logPathProp) && logPathProp.ValueKind == JsonValueKind.String)
        {
            string? lp = logPathProp.GetString();
            if (!string.IsNullOrWhiteSpace(lp))
            {
                logPath = lp;
                // Fallback heuristic: if mesLogPath not explicitly provided in json, infer from parent directory of logPath
                bool hasExplicitMes = element.TryGetProperty("mesLogPath", out var checkMes) &&
                                      checkMes.ValueKind == JsonValueKind.String &&
                                      !string.IsNullOrWhiteSpace(checkMes.GetString());
                if (!hasExplicitMes)
                {
                    string? dir = Path.GetDirectoryName(lp);
                    if (!string.IsNullOrWhiteSpace(dir))
                    {
                        mesLogPath = Path.Combine(dir, "MES_1.log");
                    }
                }
            }
        }

        if (element.TryGetProperty("mesLogPath", out var mesLogPathProp) && mesLogPathProp.ValueKind == JsonValueKind.String)
        {
            string? mlp = mesLogPathProp.GetString();
            if (!string.IsNullOrWhiteSpace(mlp))
            {
                mesLogPath = mlp;
            }
        }

        if (element.TryGetProperty("images", out var imagesProp) && imagesProp.ValueKind == JsonValueKind.Array)
        {
            var parsedImages = new List<CameraImageConfig>();
            foreach (var img in imagesProp.EnumerateArray())
            {
                if (img.ValueKind != JsonValueKind.Object)
                    continue;

                string root = img.TryGetProperty("root", out var r) && r.ValueKind == JsonValueKind.String
                    ? r.GetString() ?? ""
                    : "";
                string code = img.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString() ?? ""
                    : "";
                if (!string.IsNullOrWhiteSpace(root) && !string.IsNullOrWhiteSpace(code))
                {
                    parsedImages.Add(new CameraImageConfig(root, code));
                }
            }
            images = parsedImages;
        }

        return new MachineConfig
        {
            Name = name,
            Enabled = enabled,
            Color = color,
            BgColor = bgColor,
            LogPath = logPath,
            MesLogPath = mesLogPath,
            Images = images
        };
    }

    public UserState LoadUserState(string rootDir)
    {
        rootDir ??= string.Empty;
        string path = Path.Combine(rootDir, "config", "user-state.json");
        if (!File.Exists(path))
            return new UserState();

        try
        {
            string json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize(json, OtpJsonContext.Default.UserState);
            if (state is null)
                return new UserState();
            return string.IsNullOrWhiteSpace(state.Language) ? state with { Language = "en" } : state;
        }
        catch
        {
            return new UserState();
        }
    }

    public void SaveUserState(string rootDir, UserState state)
    {
        rootDir ??= string.Empty;
        string path = Path.Combine(rootDir, "config", "user-state.json");
        string? dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string json = JsonSerializer.Serialize(state, OtpJsonContext.Default.UserState);
        string tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(tempPath, json, System.Text.Encoding.UTF8);
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
            }
        }
    }
}
