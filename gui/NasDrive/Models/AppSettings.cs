namespace NasDrive.Models;

/// <summary>User settings stored in %APPDATA%\NasDrive\settings.json.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public bool StartWithWindows { get; set; } = true;
    public bool OpenExplorerAfterConnect { get; set; }
    public bool AllowCustomDriveIcons { get; set; } = true;
    /// <summary>"zh-CN" or "en-US"; empty = follow Windows.</summary>
    public string Language { get; set; } = "";
    /// <summary>Empty = default (%LOCALAPPDATA%\NasDrive\Cache).</summary>
    public string CacheDir { get; set; } = "";
    /// <summary>How long cached file data is kept (vfs_cache_max_age).</summary>
    public string ReadCacheDuration { get; set; } = "1h";
    /// <summary>Delay before a closed file is uploaded (vfs_write_back).</summary>
    public string WriteBackDelay { get; set; } = "5s";
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>none, http</summary>
    public string ProxyType { get; set; } = "none";
    public string ProxyHost { get; set; } = "";
    public int ProxyPort { get; set; }
    public string ProxyUser { get; set; } = "";
    /// <summary>DEBUG, INFO, NOTICE, ERROR</summary>
    public string LogLevel { get; set; } = "INFO";
    public bool TrayHintShown { get; set; }
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
}
