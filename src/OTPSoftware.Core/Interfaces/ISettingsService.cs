using OTPSoftware.Core.Models;

namespace OTPSoftware.Core.Interfaces;

/// <summary>
/// Manages system machine/path configuration (config/settings.json) and persisted UI session state (config/user-state.json).
/// </summary>
public interface ISettingsService
{
    OtpSettings GetDefaultSettings();
    OtpSettings LoadSettings(string rootDir);
    MachineVisualStyle GetMachineVisualStyle(string? sourceName, OtpSettings? settings = null);
    UserState LoadUserState(string rootDir);
    void SaveUserState(string rootDir, UserState state);
}

