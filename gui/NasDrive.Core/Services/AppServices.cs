namespace NasDrive.Services;

/// <summary>Process wide service instances, created once in App.OnStartup.</summary>
public static class AppServices
{
    public static SettingsStore Settings { get; set; } = null!;
    public static ProfileStore Profiles { get; set; } = null!;
    public static EngineSupervisor Engine { get; set; } = null!;
    public static DriveManager Drives { get; set; } = null!;
}
