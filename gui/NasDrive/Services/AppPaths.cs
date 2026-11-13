using System;
using System.IO;

namespace NasDrive.Services;

/// <summary>Locations of settings, logs, cache and the engine executable.</summary>
public static class AppPaths
{
    public static string RoamingDir { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NasDrive"));
    public static string LocalDir { get; } = Ensure(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasDrive"));
    public static string LogDir { get; } = Ensure(Path.Combine(LocalDir, "Logs"));
    public static string DefaultCacheDir { get; } = Path.Combine(LocalDir, "Cache");
    public static string ProfilesFile => Path.Combine(RoamingDir, "profiles.json");
    public static string SettingsFile => Path.Combine(RoamingDir, "settings.json");
    /// <summary>Empty rclone config so the engine never touches a user's own rclone.conf.</summary>
    public static string EngineConfigFile => Path.Combine(RoamingDir, "engine.conf");
    public static string EngineReadyFile => Path.Combine(LocalDir, "engine.ready.json");
    public static string EngineLogFile => Path.Combine(LogDir, "engine.log");

    public static string EngineExe
    {
        get
        {
            var dir = AppContext.BaseDirectory;
            var p = Path.Combine(dir, "nasdrive-engine.exe");
            return p;
        }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
