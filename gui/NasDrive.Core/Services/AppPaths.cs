using System;
using System.IO;

namespace NasDrive.Services;

/// <summary>
/// Locations of settings, logs, cache and the engine executable.
/// Windows: %APPDATA%\NasDrive (settings) and %LOCALAPPDATA%\NasDrive (logs, cache).
/// macOS: ~/Library/Application Support/NasDrive, ~/Library/Logs/NasDrive, ~/Library/Caches/NasDrive.
/// </summary>
public static class AppPaths
{
    private static readonly string Home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    /// <summary>Optional NASDRIVE_HOME: keeps every file (and the macOS mount root) under one folder, for tests.</summary>
    private static readonly string? Override = Environment.GetEnvironmentVariable("NASDRIVE_HOME") is { Length: > 0 } h ? h : null;

    public static string RoamingDir { get; } = Ensure(Override != null ? Path.Combine(Override, "config") : OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", "NasDrive")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NasDrive"));
    public static string LocalDir { get; } = Ensure(Override != null ? Path.Combine(Override, "local") : OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", "NasDrive")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NasDrive"));
    public static string LogDir { get; } = Ensure(Override != null ? Path.Combine(Override, "logs") : OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Logs", "NasDrive")
        : Path.Combine(LocalDir, "Logs"));
    public static string DefaultCacheDir { get; } = Override != null ? Path.Combine(Override, "cache") : OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Caches", "NasDrive")
        : Path.Combine(LocalDir, "Cache");
    /// <summary>macOS: default parent folder of the drive mount points (~/NasDrive).</summary>
    public static string DefaultMountRoot => Path.Combine(Override ?? Home, "NasDrive");
    /// <summary>macOS: parent folder of the drive mount points (settings value, else the default).</summary>
    public static string MountRoot => AppServices.Settings?.Current.MountRoot is { Length: > 0 } root ? root : DefaultMountRoot;
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
            var dir = Environment.GetEnvironmentVariable("NASDRIVE_ENGINE_DIR") is { Length: > 0 } d ? d : AppContext.BaseDirectory;
            return Path.Combine(dir, NasDrive.Platform.Platforms.Current.EngineFileName);
        }
    }

    private static string Ensure(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
